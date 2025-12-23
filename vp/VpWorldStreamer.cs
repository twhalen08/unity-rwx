using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using VpNet;
using RWXLoader;

/// <summary>
/// VPWorldStreamerSmooth
/// ---------------------
/// Streams VP cells around a movable camera and prioritizes model loading by:
///   1) In-frustum first (optional)
///   2) Then closest-to-camera (distance)
///
/// Smoothness improvements:
///   - Time-slices ("budgets") how much work we do per frame when finishing models (actions)
///   - Limits model starts per frame and concurrent in-flight loads
///   - Reprioritizes pending model heap on a cooldown (prevents big spikes while moving)
///
/// NOTE:
/// - QueryCellAsync expects cell coords, so we convert Unity camera position -> VP units -> cell.
/// - Unity API calls remain on main thread; this script reduces hitches by spreading work across frames.
/// </summary>
public class VPWorldStreamerSmooth : MonoBehaviour
{
    [Header("VP Login")]
    public string userName = "Tom";
    public string botName = "Unity";
    public string applicationName = "Unity";
    public string worldName = "VP-Build";

    [Header("Streaming Target")]
    public Camera targetCamera;

    [Header("Streaming Radii")]
    [Tooltip("How many cells out from the camera cell to keep loaded")]
    public int loadRadius = 2;

    [Tooltip("Cells beyond this radius will be unloaded (set to -1 to disable unloading)")]
    public int unloadRadius = 4;

    [Tooltip("Only recompute desired cells when camera crosses into a new cell")]
    public bool updateOnlyOnCellChange = true;

    [Header("Cell Mapping")]
    [Tooltip("VP cell size in VP world units (commonly 2000).")]
    public float vpUnitsPerCell = 2000f;

    [Tooltip("How many VP world units equal 1 Unity unit. If 1 Unity unit == 1 VP unit, set 1. If 1 Unity unit == 1 meter and VP is 100 units/m, set 100.")]
    public float vpUnitsPerUnityUnit = 1f;

    [Header("Model Loader")]
    [Tooltip("Assign your RWXLoaderAdvanced here, or we'll create one at runtime")]
    public RWXLoaderAdvanced modelLoader;

    [Header("VP Object Server")]
    public string objectPath = "http://objects.virtualparadise.org/vpbuild/";
    public string objectPathPassword = "";

    [Header("Throttles")]
    [Tooltip("Max cell queries in-flight at once")]
    public int maxConcurrentCellQueries = 1;

    [Tooltip("Max model loads in-flight at once (RWX downloads + instantiation)")]
    public int maxConcurrentModelLoads = 1;

    [Tooltip("Max models to START per frame (keeps frame stable)")]
    public int maxModelStartsPerFrame = 1;

    [Header("Smoothness Budget")]
    [Tooltip("Max milliseconds per frame spent applying actions to loaded models (lower = smoother, slower load).")]
    public float modelWorkBudgetMs = 2.5f;

    [Tooltip("If true, apply CREATE actions over multiple frames using the budget.")]
    public bool sliceActionApplication = true;

    [Tooltip("Cooldown between heap reprioritizations when camera cell changes. 0 = always.")]
    public float reprioritizeCooldownSeconds = 0.25f;

    [Header("Prioritization")]
    [Tooltip("When camera changes cell, rebuild pending model priorities so nearer/visible objects load first.")]
    public bool reprioritizeModelsOnCellChange = true;

    [Tooltip("If true, skip model loads whose cell root was unloaded before we got to them.")]
    public bool dropModelsFromUnloadedCells = true;

    [Header("Frustum Prioritization")]
    public bool prioritizeFrustum = true;

    [Tooltip("How much to favor objects in the view frustum. Bigger = stronger bias.")]
    public float frustumBonus = 1_000_000f;

    [Tooltip("Ignore frustum test beyond this distance (Unity units). 0 = no limit.")]
    public float frustumMaxDistance = 0f;

    [Header("Debug")]
    public bool logCellLoads = false;
    public bool showDebugOverlay = true;
    public bool logCreateActions = false;
    public bool logActivateActions = false;

    private VirtualParadiseClient vpClient;

    private readonly Dictionary<(int cx, int cy), GameObject> cellRoots = new();
    private readonly HashSet<(int cx, int cy)> loadedCells = new();
    private readonly HashSet<(int cx, int cy)> queuedCells = new();
    private readonly HashSet<(int cx, int cy)> queryingCells = new();
    private readonly List<(int cx, int cy)> desiredCells = new();
    private readonly MinHeap<(int cx, int cy)> queuedCellHeap = new MinHeap<(int cx, int cy)>();

    private struct PendingModelLoad
    {
        public (int cx, int cy) cell;
        public string modelName;
        public UnityEngine.Vector3 position;
        public Quaternion rotation;
        public string action;
    }

    // Model priority heap: smallest priority loads first
    private readonly MinHeap<PendingModelLoad> modelHeap = new MinHeap<PendingModelLoad>();

    private int inFlightCellQueries = 0;
    private int inFlightModelLoads = 0;

    private (int cx, int cy) lastCameraCell = (int.MinValue, int.MinValue);

    private float nextReprioritizeTime = 0f;

    // Debug
    private (int cx, int cy) debugCamCell;
    private UnityEngine.Vector3 debugCamPos;

    private void Start()
    {
        if (targetCamera == null) targetCamera = Camera.main;

        SetupModelLoader();
        StartCoroutine(InitializeAndStream());
    }

    private IEnumerator InitializeAndStream()
    {
        var loginTask = ConnectAndLogin();
        yield return WaitForTask(loginTask);

        if (loginTask.IsFaulted || loginTask.IsCanceled)
        {
            string message = loginTask.IsFaulted
                ? loginTask.Exception?.GetBaseException().Message
                : "Login task cancelled";
            Debug.LogError($"[VP] Failed to connect: {message}");
            yield break;
        }

        StartCoroutine(CellStreamingLoop());
        StartCoroutine(ModelStreamingLoop());
    }

    private async Task ConnectAndLogin()
    {
        vpClient = new VirtualParadiseClient
        {
            Configuration = new VirtualParadiseClientConfiguration
            {
                UserName = userName,
                BotName = botName,
                ApplicationName = applicationName,
                World = new World { Name = worldName }
            }
        };

        await vpClient.LoginAndEnterAsync("", true);
        Debug.Log($"[VP] Connected & entered '{worldName}' as {userName}");
    }

    private void SetupModelLoader()
    {
        if (modelLoader == null)
        {
            var loaderGO = new GameObject("RWX Remote Loader");
            modelLoader = loaderGO.AddComponent<RWXLoaderAdvanced>();
        }

        modelLoader.defaultObjectPath = objectPath.TrimEnd('/') + "/";
        modelLoader.objectPathPassword = objectPathPassword;
        modelLoader.enableDebugLogs = false;
        modelLoader.parentTransform = null;

        if (RWXAssetManager.Instance == null)
        {
            var mgrGO = new GameObject("RWX Asset Manager");
            mgrGO.AddComponent<RWXAssetManager>();
        }
    }

    // -------------------------
    // Main loops
    // -------------------------
    private IEnumerator CellStreamingLoop()
    {
        while (true)
        {
            var camCell = GetCameraCell();
            debugCamCell = camCell;

            bool cellChanged = camCell != lastCameraCell;

            if (!updateOnlyOnCellChange || cellChanged)
            {
                lastCameraCell = camCell;

                BuildDesiredCells(camCell.cx, camCell.cy, loadRadius);

                // Enqueue desired cells
                for (int i = 0; i < desiredCells.Count; i++)
                {
                    var c = desiredCells[i];

                    if (loadedCells.Contains(c)) continue;
                    if (queuedCells.Contains(c)) continue;
                    if (queryingCells.Contains(c)) continue;

                    if (queuedCells.Add(c))
                    {
                        float pri = ComputeCellPriority(c, camCell.cx, camCell.cy);
                        queuedCellHeap.Push(c, pri);
                    }
                }

                // Unload far cells if enabled
                if (unloadRadius >= 0)
                    UnloadFarCells(camCell.cx, camCell.cy, unloadRadius);

                // Refresh queued cell priorities when the camera changes cell
                if (cellChanged)
                    RebuildQueuedCellHeap(camCell.cx, camCell.cy);

                // Reprioritize pending models (cooldown-protected)
                if (cellChanged && reprioritizeModelsOnCellChange && modelHeap.Count > 0)
                {
                    if (reprioritizeCooldownSeconds <= 0f || Time.time >= nextReprioritizeTime)
                    {
                        ReprioritizePendingModels();
                        nextReprioritizeTime = Time.time + Mathf.Max(0.01f, reprioritizeCooldownSeconds);
                    }
                }
            }

            // Start cell queries (closest-first)
            while (inFlightCellQueries < maxConcurrentCellQueries)
            {
                var next = DequeueClosestQueuedCell();
                if (next.cx == int.MinValue)
                    break;

                queuedCells.Remove(next);
                queryingCells.Add(next);
                inFlightCellQueries++;

                StartCoroutine(QueryCellAndEnqueueModels(next.cx, next.cy));
            }

            yield return null;
        }
    }

    private IEnumerator ModelStreamingLoop()
    {
        while (true)
        {
            int startedThisFrame = 0;

            while (modelHeap.Count > 0 &&
                   inFlightModelLoads < maxConcurrentModelLoads &&
                   startedThisFrame < maxModelStartsPerFrame)
            {
                var req = modelHeap.PopMin();

                if (dropModelsFromUnloadedCells)
                {
                    if (!cellRoots.TryGetValue(req.cell, out var cellRoot) || cellRoot == null)
                        continue;

                    inFlightModelLoads++;
                    startedThisFrame++;
                    StartCoroutine(LoadOneModelBudgeted(req, cellRoot.transform));
                }
                else
                {
                    Transform parent = null;
                    if (cellRoots.TryGetValue(req.cell, out var cellRoot) && cellRoot != null)
                        parent = cellRoot.transform;

                    inFlightModelLoads++;
                    startedThisFrame++;
                    StartCoroutine(LoadOneModelBudgeted(req, parent));
                }
            }

            yield return null;
        }
    }

    // -------------------------
    // Cell query + model enqueue
    // -------------------------
    private IEnumerator QueryCellAndEnqueueModels(int cellX, int cellY)
    {
        if (logCellLoads) Debug.Log($"[VP] QueryCell ({cellX},{cellY})");

        var cellTask = vpClient.QueryCellAsync(cellX, cellY);
        yield return WaitForTask(cellTask);

        inFlightCellQueries--;
        queryingCells.Remove((cellX, cellY));

        if (cellTask.IsFaulted || cellTask.IsCanceled)
        {
            string message = cellTask.IsFaulted
                ? cellTask.Exception?.GetBaseException().Message
                : "Query cancelled";
            Debug.LogWarning($"[VP] Failed QueryCell ({cellX},{cellY}): {message}");
            yield break;
        }

        var key = (cellX, cellY);

        // Create / reuse cell root
        if (!cellRoots.TryGetValue(key, out var root) || root == null)
        {
            root = new GameObject($"VP_Cell_{cellX}_{cellY}");
            cellRoots[key] = root;
        }

        loadedCells.Add(key);

        UnityEngine.Vector3 camPos = GetCameraWorldPos();

        var cell = cellTask.Result;
        foreach (var obj in cell.Objects)
        {
            if (string.IsNullOrEmpty(obj.Model))
                continue;

            string modelName = obj.Model.EndsWith(".rwx", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(obj.Model)
                : obj.Model;

            UnityEngine.Vector3 pos = VPtoUnity(obj.Position);
            Quaternion rot = ConvertVpRotationToUnity(obj.Rotation, obj.Angle, modelName);

            var req = new PendingModelLoad
            {
                cell = key,
                modelName = modelName,
                position = pos,
                rotation = rot,
                action = obj.Action
            };

            float pri = ComputeModelPriority(pos, camPos);
            modelHeap.Push(req, pri);
        }
    }

    // -------------------------
    // Model load + actions (budgeted)
    // -------------------------
    private IEnumerator LoadOneModelBudgeted(PendingModelLoad req, Transform parent)
    {
        string modelId = Path.GetFileNameWithoutExtension(req.modelName);

        bool completed = false;
        GameObject loadedObject = null;
        string errorMessage = null;

        modelLoader.parentTransform = parent;

        modelLoader.LoadModelFromRemote(
            modelId,
            modelLoader.defaultObjectPath,
            (go, errMsg) =>
            {
                loadedObject = go;
                errorMessage = errMsg;
                completed = true;
            },
            objectPathPassword
        );

        while (!completed)
            yield return null;

        inFlightModelLoads--;

        if (loadedObject == null)
        {
            Debug.LogError($"RWX load failed: {req.modelName} (normalized='{modelId}') → {errorMessage}");
            yield break;
        }

        // Phase 1: cheap transform setup
        loadedObject.transform.localPosition = req.position;
        loadedObject.transform.localRotation = req.rotation;
        loadedObject.transform.localScale = UnityEngine.Vector3.one;

        // Give Unity a frame before heavier work
        yield return null;

        // If the cell vanished while we were loading, discard the object (optional)
        if (dropModelsFromUnloadedCells)
        {
            if (!cellRoots.TryGetValue(req.cell, out var cellRoot) || cellRoot == null)
            {
                Destroy(loadedObject);
                yield break;
            }
        }

        // Phase 2: apply actions (time-sliced)
        if (!string.IsNullOrWhiteSpace(req.action))
        {
            VpActionParser.Parse(req.action, out var createActions, out var activateActions);

            if (logCreateActions && createActions.Count > 0)
                Debug.Log($"[VP Create] {loadedObject.name} will run {createActions.Count} actions");

            if (!sliceActionApplication)
            {
                foreach (var a in createActions)
                    VpActionExecutor.ExecuteCreate(loadedObject, a, modelLoader.defaultObjectPath, objectPathPassword, this);
            }
            else
            {
                float start = Time.realtimeSinceStartup;

                for (int i = 0; i < createActions.Count; i++)
                {
                    VpActionExecutor.ExecuteCreate(loadedObject, createActions[i], modelLoader.defaultObjectPath, objectPathPassword, this);

                    float elapsedMs = (Time.realtimeSinceStartup - start) * 1000f;
                    if (elapsedMs >= modelWorkBudgetMs)
                    {
                        yield return null;
                        start = Time.realtimeSinceStartup;
                    }
                }
            }

            // Activate actions are stored (cheap)
            if (activateActions.Count > 0)
            {
                var act = loadedObject.GetComponent<VpActivateActions>();
                if (act == null) act = loadedObject.AddComponent<VpActivateActions>();
                act.actions.AddRange(activateActions);

                if (logActivateActions)
                    Debug.Log($"[VP Activate] {loadedObject.name} stored {activateActions.Count} actions");
            }
        }
    }

    // -------------------------
    // Priority computation
    // -------------------------
    private float ComputeModelPriority(UnityEngine.Vector3 objPos, UnityEngine.Vector3 camPos)
    {
        float pri = (objPos - camPos).sqrMagnitude;

        if (prioritizeFrustum && IsInViewFrustum(objPos))
            pri -= frustumBonus; // smaller is earlier

        return pri;
    }

    private bool IsInViewFrustum(UnityEngine.Vector3 worldPos)
    {
        if (targetCamera == null) targetCamera = Camera.main;
        if (targetCamera == null) return false;

        if (frustumMaxDistance > 0f)
        {
            float d = (worldPos - targetCamera.transform.position).magnitude;
            if (d > frustumMaxDistance)
                return false;
        }

        UnityEngine.Vector3 vp = targetCamera.WorldToViewportPoint(worldPos);
        return (vp.z > 0f && vp.x >= 0f && vp.x <= 1f && vp.y >= 0f && vp.y <= 1f);
    }

    private void ReprioritizePendingModels()
    {
        UnityEngine.Vector3 camPos = GetCameraWorldPos();

        var items = modelHeap.DumpItems();
        modelHeap.Clear();

        for (int i = 0; i < items.Count; i++)
        {
            var req = items[i];
            float pri = ComputeModelPriority(req.position, camPos);
            modelHeap.Push(req, pri);
        }
    }

    private UnityEngine.Vector3 GetCameraWorldPos()
    {
        if (targetCamera == null) targetCamera = Camera.main;
        if (targetCamera == null) return UnityEngine.Vector3.zero;
        return targetCamera.transform.position;
    }

    // -------------------------
    // Cell math / prioritization
    // -------------------------
    private (int cx, int cy) GetCameraCell()
    {
        if (targetCamera == null)
            targetCamera = Camera.main;

        if (targetCamera == null)
            return (0, 0);

        UnityEngine.Vector3 u = targetCamera.transform.position;
        debugCamPos = u;

        float vpX = (-u.x) * vpUnitsPerUnityUnit;
        float vpZ = (u.z) * vpUnitsPerUnityUnit;

        float cellSize = Mathf.Max(1f, vpUnitsPerCell);

        int cx = Mathf.FloorToInt(vpX / cellSize);
        int cy = Mathf.FloorToInt(vpZ / cellSize);

        return (cx, cy);
    }

    private void BuildDesiredCells(int centerX, int centerY, int radius)
    {
        desiredCells.Clear();

        for (int dx = -radius; dx <= radius; dx++)
            for (int dy = -radius; dy <= radius; dy++)
                desiredCells.Add((centerX + dx, centerY + dy));

        // Sort closest-first (Chebyshev then Manhattan)
        desiredCells.Sort((a, b) =>
        {
            int dax = Mathf.Abs(a.cx - centerX);
            int day = Mathf.Abs(a.cy - centerY);
            int dbx = Mathf.Abs(b.cx - centerX);
            int dby = Mathf.Abs(b.cy - centerY);

            int ca = Mathf.Max(dax, day);
            int cb = Mathf.Max(dbx, dby);
            if (ca != cb) return ca.CompareTo(cb);

            int ma = dax + day;
            int mb = dbx + dby;
            return ma.CompareTo(mb);
        });
    }

    private (int cx, int cy) DequeueClosestQueuedCell()
    {
        while (queuedCellHeap.Count > 0)
        {
            var next = queuedCellHeap.PopMin();

            if (!queuedCells.Contains(next))
                continue; // stale entry

            // Skip items that got loaded/unloaded while waiting
            if (loadedCells.Contains(next) || queryingCells.Contains(next))
                continue;

            return next;
        }

        return (int.MinValue, int.MinValue);
    }

    private float ComputeCellPriority((int cx, int cy) cell, int centerX, int centerY)
    {
        int dx = Mathf.Abs(cell.cx - centerX);
        int dy = Mathf.Abs(cell.cy - centerY);
        return Mathf.Max(dx, dy) * 100 + (dx + dy);
    }

    private void UnloadFarCells(int centerX, int centerY, int radius)
    {
        var toUnload = new List<(int cx, int cy)>();

        foreach (var c in loadedCells)
        {
            int dx = Mathf.Abs(c.cx - centerX);
            int dy = Mathf.Abs(c.cy - centerY);

            if (Mathf.Max(dx, dy) > radius)
                toUnload.Add(c);
        }

        foreach (var c in toUnload)
        {
            loadedCells.Remove(c);
            queuedCells.Remove(c);

            if (cellRoots.TryGetValue(c, out var root) && root != null)
                Destroy(root);

            cellRoots.Remove(c);

            if (logCellLoads) Debug.Log($"[VP] Unloaded cell ({c.cx},{c.cy})");
        }

        if (toUnload.Count > 0)
            RebuildQueuedCellHeap(centerX, centerY);
    }

    // -------------------------
    // Utilities
    // -------------------------
    private IEnumerator WaitForTask(Task task)
    {
        while (!task.IsCompleted)
            yield return null;
    }

    private void RebuildQueuedCellHeap(int centerX, int centerY)
    {
        queuedCellHeap.Clear();

        foreach (var c in queuedCells)
        {
            float pri = ComputeCellPriority(c, centerX, centerY);
            queuedCellHeap.Push(c, pri);
        }
    }

    private UnityEngine.Vector3 VPtoUnity(VpNet.Vector3 vpPos)
    {
        return new UnityEngine.Vector3(
            -(float)vpPos.X,
            (float)vpPos.Y,
            (float)vpPos.Z
        );
    }

    private Quaternion ConvertVpRotationToUnity(VpNet.Vector3 axis, double angle, string modelName)
    {
        if (double.IsInfinity(angle))
        {
            UnityEngine.Vector3 unityEuler = new UnityEngine.Vector3(
                (float)axis.X,
                -(float)axis.Y,
                -(float)axis.Z
            );
            return Quaternion.Euler(unityEuler);
        }

        UnityEngine.Vector3 rotAxis = new UnityEngine.Vector3((float)axis.X, (float)axis.Y, (float)axis.Z);
        if (rotAxis.sqrMagnitude < 1e-8f || double.IsNaN(angle) || double.IsInfinity(angle))
            return Quaternion.identity;

        rotAxis.Normalize();
        float deg = (float)angle * Mathf.Rad2Deg;

        Quaternion vpQ = Quaternion.AngleAxis(deg, rotAxis);
        Quaternion unityQ = new Quaternion(vpQ.x, -vpQ.y, -vpQ.z, vpQ.w);
        return Quaternion.Normalize(unityQ);
    }

    private void OnGUI()
    {
        if (!showDebugOverlay) return;

        GUI.Label(new Rect(10, 10, 1600, 22), $"Cam: {debugCamPos}  Cell: ({debugCamCell.cx},{debugCamCell.cy})");
        GUI.Label(new Rect(10, 32, 1600, 22), $"LoadedCells={loadedCells.Count} QueuedCells={queuedCells.Count} QueryingCells={queryingCells.Count}");
        GUI.Label(new Rect(10, 54, 1600, 22), $"ModelPending={modelHeap.Count} InFlightModels={inFlightModelLoads}");
        GUI.Label(new Rect(10, 76, 1600, 22), $"BudgetMs={modelWorkBudgetMs} SliceActions={sliceActionApplication} ReprioCooldown={reprioritizeCooldownSeconds}s");
        GUI.Label(new Rect(10, 98, 1600, 22), $"vpUnitsPerUnityUnit={vpUnitsPerUnityUnit} vpUnitsPerCell={vpUnitsPerCell} Frustum={prioritizeFrustum}");
    }

    // -------------------------
    // Simple min-heap priority queue
    // -------------------------
    private sealed class MinHeap<T>
    {
        private struct Node
        {
            public float pri;
            public T item;
        }

        private readonly List<Node> heap = new List<Node>(256);

        public int Count => heap.Count;

        public void Clear() => heap.Clear();

        public void Push(T item, float priority)
        {
            heap.Add(new Node { pri = priority, item = item });
            SiftUp(heap.Count - 1);
        }

        public T PopMin()
        {
            if (heap.Count == 0)
                throw new InvalidOperationException("Heap empty");

            T min = heap[0].item;

            int last = heap.Count - 1;
            heap[0] = heap[last];
            heap.RemoveAt(last);

            if (heap.Count > 0)
                SiftDown(0);

            return min;
        }

        public List<T> DumpItems()
        {
            var list = new List<T>(heap.Count);
            for (int i = 0; i < heap.Count; i++)
                list.Add(heap[i].item);
            return list;
        }

        private void SiftUp(int i)
        {
            while (i > 0)
            {
                int p = (i - 1) >> 1;
                if (heap[i].pri >= heap[p].pri) break;
                Swap(i, p);
                i = p;
            }
        }

        private void SiftDown(int i)
        {
            int n = heap.Count;
            while (true)
            {
                int l = (i << 1) + 1;
                int r = l + 1;
                if (l >= n) break;

                int m = (r < n && heap[r].pri < heap[l].pri) ? r : l;
                if (heap[i].pri <= heap[m].pri) break;

                Swap(i, m);
                i = m;
            }
        }

        private void Swap(int a, int b)
        {
            Node tmp = heap[a];
            heap[a] = heap[b];
            heap[b] = tmp;
        }
    }
}
