using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using VpNet;
using RWXLoader;

public class VPWorldAreaLoader : MonoBehaviour
{
    [Header("VP Login")]
    public string userName = "Tom";
    public string botName = "Unity";
    public string applicationName = "Unity";
    public string worldName = "VP-Build";

    [Header("Area Settings")]
    public int centerX = 0;
    public int centerY = 0;
    public int radius = 1;

    [Header("Model Loader")]
    [Tooltip("Assign your RWXLoaderAdvanced here, or we'll create one at runtime")]
    public RWXLoaderAdvanced modelLoader;

    [Header("VP Object Server")]
    [Tooltip("Base URL of the VP build object server")]
    public string objectPath = "http://objects.virtualparadise.org/vpbuild/";

    [Header("Performance")]
    [Tooltip("How many RWX downloads to run simultaneously")]
    [Min(1)]
    public int maxConcurrentLoads = 2;

    private VirtualParadiseClient vpClient;
    private GameObject areaRoot;
    private string normalizedObjectPath;

    private struct PendingModelLoad
    {
        public string modelName;
        public UnityEngine.Vector3 position;
        public Quaternion rotation;
        public Transform parent;
    }

    private readonly Queue<PendingModelLoad> pendingModelLoads = new Queue<PendingModelLoad>();
    private Coroutine loadQueueCoroutine;
    private readonly Dictionary<string, GameObject> modelCache = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> modelsBeingLoaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<PendingModelLoad>> pendingSpawnsByModel = new Dictionary<string, List<PendingModelLoad>>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Vector2Int, Transform> cellRoots = new Dictionary<Vector2Int, Transform>();
    private readonly List<RWXLoaderAdvanced> loaderPool = new List<RWXLoaderAdvanced>();
    private readonly Queue<RWXLoaderAdvanced> availableLoaders = new Queue<RWXLoaderAdvanced>();
    private readonly HashSet<RWXLoaderAdvanced> busyLoaders = new HashSet<RWXLoaderAdvanced>();
    private readonly HashSet<RWXLoaderAdvanced> availableLoaderSet = new HashSet<RWXLoaderAdvanced>();

    private void Start()
    {
        // 1) Create a root for this chunk of world
        areaRoot = new GameObject($"VP_Area_{centerX}_{centerY}");

        // Kick off the progressive initialization routine
        StartCoroutine(InitializeAndLoad());
    }

    private IEnumerator InitializeAndLoad()
    {
        // 2) Connect & enter the world
        var loginTask = ConnectAndLogin();
        yield return WaitForTask(loginTask);
        if (loginTask.IsFaulted || loginTask.IsCanceled)
        {
            string message = loginTask.IsFaulted
                ? loginTask.Exception?.GetBaseException().Message
                : "Login task was cancelled";
            Debug.LogError($"[VP] Failed to connect: {message}");
            yield break;
        }

        // 3) Ensure our loader and cache manager are set up
        SetupModelLoader();

        // 4) Query the cells & spawn all models progressively
        yield return QueryAndBuildAreaProgressively();
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
        await vpClient.LoginAndEnterAsync("xxxxxxxx", true);
        Debug.Log($"[VP] Connected & entered '{worldName}' as {userName}");
    }

    private void SetupModelLoader()
    {
        // Guarantee we have an RWXLoaderAdvanced in the scene
        if (modelLoader == null)
        {
            var loaderGO = new GameObject("RWX Remote Loader");
            modelLoader = loaderGO.AddComponent<RWXLoaderAdvanced>();
        }

        normalizedObjectPath = string.IsNullOrWhiteSpace(objectPath)
            ? string.Empty
            : objectPath.TrimEnd('/') + "/";

        ConfigureLoader(modelLoader);
        RegisterLoader(modelLoader);
        EnsureLoaderPool();

        // Also ensure the asset-manager singleton exists
        if (RWXAssetManager.Instance == null)
        {
            var mgrGO = new GameObject("RWX Asset Manager");
            mgrGO.AddComponent<RWXAssetManager>();
        }
    }

    private IEnumerator QueryAndBuildAreaProgressively()
    {
        foreach (var offset in EnumerateCellOffsets())
        {
            int cellX = centerX + offset.x;
            int cellY = centerY + offset.y;

            var cellTask = vpClient.QueryCellAsync(cellX, cellY);
            yield return WaitForTask(cellTask);

            if (cellTask.IsFaulted || cellTask.IsCanceled)
            {
                string message = cellTask.IsFaulted
                    ? cellTask.Exception?.GetBaseException().Message
                    : "Query was cancelled";
                Debug.LogWarning($"[VP] Failed to query cell ({cellX}, {cellY}): {message}");
                continue;
            }

            var cellParent = GetOrCreateCellRoot(cellX, cellY);
            ProcessCell(cellTask.Result, cellParent);
            // Give the queue processor a frame to start loading
            yield return null;
        }

        // Wait for any outstanding model loads to complete before finishing
        while (pendingModelLoads.Count > 0 || loadQueueCoroutine != null)
        {
            yield return null;
        }
    }

    private IEnumerable<Vector2Int> EnumerateCellOffsets()
    {
        int effectiveRadius = Mathf.Max(0, radius);
        if (effectiveRadius == 0)
        {
            yield return Vector2Int.zero;
            yield break;
        }

        yield return Vector2Int.zero;

        for (int ring = 1; ring <= effectiveRadius; ring++)
        {
            for (int dx = -ring; dx <= ring; dx++)
            {
                for (int dy = -ring; dy <= ring; dy++)
                {
                    if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy)) != ring)
                    {
                        continue;
                    }

                    yield return new Vector2Int(dx, dy);
                }
            }
        }
    }

    private IEnumerator WaitForTask(Task task)
    {
        while (!task.IsCompleted)
        {
            yield return null;
        }
    }

    private void ProcessCell(QueryCellResult cell, Transform cellParent)
    {
        foreach (var obj in cell.Objects)
        {
            if (string.IsNullOrEmpty(obj.Model))
                continue;

            // Strip trailing ".rwx" if present
            string modelName = obj.Model.EndsWith(".rwx", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(obj.Model)
                : obj.Model;

            // Convert VP coords → Unity coords (swap X and Z)
            UnityEngine.Vector3 pos = VPtoUnity(obj.Position);

            // Convert VP axis-angle rotation to Unity Quaternion
            var vpR = obj.Rotation;
            var vpAngle = obj.Angle;
            Quaternion unityRot = Quaternion.identity; // Default to no rotation
            
            // Handle VP rotation - special case for infinity angle (treat as Euler angles)
            if (double.IsInfinity(vpAngle) && !double.IsNaN(vpR.X) && !double.IsNaN(vpR.Y) && !double.IsNaN(vpR.Z))
            {
                // When angle is infinity, VP seems to use the rotation vector as Euler angles
                // Invert both Y and Z rotations to fix all orientation issues
                unityRot = Quaternion.Euler((float)vpR.X, -(float)vpR.Y, -(float)vpR.Z);
            }
            else if (Math.Abs(vpAngle) > 0.001 && !double.IsNaN(vpAngle) && !double.IsInfinity(vpAngle)) // Normal axis-angle rotation
            {
                // Convert rotation axis to match our coordinate system (X-Y ground, Z height)
                UnityEngine.Vector3 rotationAxis = new UnityEngine.Vector3((float)vpR.X, (float)vpR.Y, (float)vpR.Z);
                
                // Check for NaN or infinite values in the rotation axis
                if (float.IsNaN(rotationAxis.x) || float.IsNaN(rotationAxis.y) || float.IsNaN(rotationAxis.z) ||
                    float.IsInfinity(rotationAxis.x) || float.IsInfinity(rotationAxis.y) || float.IsInfinity(rotationAxis.z))
                {
                    Debug.LogWarning($"Invalid rotation axis for {modelName}: {rotationAxis}, using identity");
                }
                // Validate the rotation axis - must not be zero vector
                else if (rotationAxis.magnitude > 0.001f)
                {
                    rotationAxis = rotationAxis.normalized; // Normalize the axis
                    // Convert VP angle from radians to degrees for Unity
                    float angleInDegrees = (float)vpAngle * Mathf.Rad2Deg;
                    
                    // Check for valid angle
                    if (!float.IsNaN(angleInDegrees) && !float.IsInfinity(angleInDegrees))
                    {
                        unityRot = Quaternion.AngleAxis(angleInDegrees, rotationAxis);
                        
                        // Final validation of the quaternion
                        if (float.IsNaN(unityRot.x) || float.IsNaN(unityRot.y) || float.IsNaN(unityRot.z) || float.IsNaN(unityRot.w))
                        {
                            Debug.LogWarning($"Generated NaN quaternion for {modelName}, using identity");
                            unityRot = Quaternion.identity;
                        }
                    }
                    else
                    {
                        Debug.LogWarning($"Invalid angle for {modelName}: {vpAngle} radians, using identity");
                    }
                }
                else
                {
                    Debug.LogWarning($"Zero rotation axis for {modelName}, using identity");
                }
            }

            EnqueueModelLoad(modelName, pos, unityRot, cellParent);
        }
    }

    private void EnqueueModelLoad(string modelName, UnityEngine.Vector3 position, Quaternion rotation, Transform parent)
    {
        pendingModelLoads.Enqueue(new PendingModelLoad
        {
            modelName = modelName,
            position = position,
            rotation = rotation,
            parent = parent
        });

        if (loadQueueCoroutine == null)
        {
            loadQueueCoroutine = StartCoroutine(ProcessLoadQueue());
        }
    }

    private IEnumerator ProcessLoadQueue()
    {
        var deferredLoads = new List<PendingModelLoad>();
        const int maxProcessesPerFrame = 16;

        while (pendingModelLoads.Count > 0 || deferredLoads.Count > 0 || busyLoaders.Count > 0)
        {
            int processedThisFrame = 0;

            while (pendingModelLoads.Count > 0)
            {
                var request = pendingModelLoads.Dequeue();
                processedThisFrame++;

                if (modelCache.TryGetValue(request.modelName, out var cachedPrefab))
                {
                    SpawnAdditionalInstance(cachedPrefab, request);
                    if (processedThisFrame >= maxProcessesPerFrame)
                    {
                        break;
                    }
                    continue;
                }

                if (modelsBeingLoaded.Contains(request.modelName))
                {
                    QueuePendingSpawn(request);
                    if (processedThisFrame >= maxProcessesPerFrame)
                    {
                        break;
                    }
                    continue;
                }

                if (!TryBorrowLoader(out var loader))
                {
                    deferredLoads.Add(request);
                    continue;
                }

                modelsBeingLoaded.Add(request.modelName);
                StartCoroutine(RunRemoteLoad(loader, request));

                if (processedThisFrame >= maxProcessesPerFrame)
                {
                    break;
                }
            }

            if (deferredLoads.Count > 0)
            {
                foreach (var deferred in deferredLoads)
                {
                    pendingModelLoads.Enqueue(deferred);
                }

                deferredLoads.Clear();
            }

            if (pendingModelLoads.Count == 0 && deferredLoads.Count == 0 && busyLoaders.Count == 0)
            {
                break;
            }

            // Allow other systems to breathe before the next batch or while remote loads are running
            yield return null;
        }

        loadQueueCoroutine = null;
    }

    private IEnumerator RunRemoteLoad(RWXLoaderAdvanced loader, PendingModelLoad initialRequest)
    {
        bool completed = false;
        GameObject loadedObject = null;
        string errorMessage = null;

        loader.LoadModelFromRemote(
            initialRequest.modelName,
            string.IsNullOrEmpty(normalizedObjectPath) ? null : normalizedObjectPath,
            (go, errMsg) =>
            {
                loadedObject = go;
                errorMessage = errMsg;
                completed = true;
            });

        while (!completed)
        {
            yield return null;
        }

        modelsBeingLoaded.Remove(initialRequest.modelName);

        if (loadedObject != null)
        {
            modelCache[initialRequest.modelName] = loadedObject;
            PositionAndParentInstance(loadedObject, initialRequest);
            SpawnQueuedInstances(initialRequest.modelName, loadedObject);
        }
        else
        {
            Debug.LogError($"RWX load failed: {initialRequest.modelName} → {errorMessage}");
            ClearFailedQueuedInstances(initialRequest.modelName);
        }

        ReturnLoader(loader);
    }

    private Transform GetSpawnParent()
    {
        if (modelLoader != null && modelLoader.parentTransform != null)
        {
            return modelLoader.parentTransform;
        }

        return areaRoot != null ? areaRoot.transform : transform;
    }

    private void PositionAndParentInstance(GameObject instance, PendingModelLoad request)
    {
        var parent = request.parent != null ? request.parent : GetSpawnParent();
        instance.transform.SetParent(parent, false);
        instance.transform.localPosition = request.position;
        instance.transform.localRotation = request.rotation;
        instance.transform.localScale = UnityEngine.Vector3.one;
    }

    private void SpawnAdditionalInstance(GameObject cachedPrefab, PendingModelLoad request)
    {
        var parent = request.parent != null ? request.parent : GetSpawnParent();
        var clone = UnityEngine.Object.Instantiate(cachedPrefab, parent, false);
        clone.name = cachedPrefab.name;
        clone.transform.localPosition = request.position;
        clone.transform.localRotation = request.rotation;
        clone.transform.localScale = UnityEngine.Vector3.one;
    }

    private void QueuePendingSpawn(PendingModelLoad request)
    {
        if (!pendingSpawnsByModel.TryGetValue(request.modelName, out var list))
        {
            list = new List<PendingModelLoad>();
            pendingSpawnsByModel[request.modelName] = list;
        }

        list.Add(request);
    }

    private void SpawnQueuedInstances(string modelName, GameObject cachedPrefab)
    {
        if (!pendingSpawnsByModel.TryGetValue(modelName, out var queuedRequests))
        {
            return;
        }

        foreach (var pending in queuedRequests)
        {
            SpawnAdditionalInstance(cachedPrefab, pending);
        }

        pendingSpawnsByModel.Remove(modelName);
    }

    private void ClearFailedQueuedInstances(string modelName)
    {
        if (!pendingSpawnsByModel.TryGetValue(modelName, out var queuedRequests))
        {
            return;
        }

        pendingSpawnsByModel.Remove(modelName);

        foreach (var pending in queuedRequests)
        {
            Debug.LogError($"Skipping queued spawn for {modelName} at {pending.position} due to load failure.");
        }
    }

    private Transform GetOrCreateCellRoot(int cellX, int cellY)
    {
        var key = new Vector2Int(cellX, cellY);
        if (cellRoots.TryGetValue(key, out var cached) && cached != null)
        {
            return cached;
        }

        var parent = areaRoot != null ? areaRoot.transform : GetSpawnParent();
        var cellGO = new GameObject($"Cell_{cellX}_{cellY}");
        cellGO.transform.SetParent(parent, false);
        cellRoots[key] = cellGO.transform;
        return cellGO.transform;
    }

    private UnityEngine.Vector3 VPtoUnity(VpNet.Vector3 vpPos)
    {
        // Convert VP coordinates to Unity with X-Y as ground plane and Z as height:
        // Flip only X axis to fix mirroring, keep Y and Z normal
        // VP.X (east/west on ground) → Unity.-X (flip east/west)
        // VP.Y (north/south on ground) → Unity.Y (normal north/south)
        // VP.Z (up/down height) → Unity.Z (up/down height)
        return new UnityEngine.Vector3(
            -(float)vpPos.X,  // VP ground X to Unity ground -X (flipped)
            (float)vpPos.Y,   // VP ground Y to Unity ground Y (normal)
            (float)vpPos.Z    // VP height Z to Unity height Z
        );
    }

    private void ConfigureLoader(RWXLoaderAdvanced loader)
    {
        if (loader == null)
        {
            return;
        }

        if (!string.IsNullOrEmpty(normalizedObjectPath))
        {
            loader.defaultObjectPath = normalizedObjectPath;
        }
        loader.enableDebugLogs = true;
        loader.parentTransform = null;
        loader.EnsureInitialized();
    }

    private void RegisterLoader(RWXLoaderAdvanced loader)
    {
        if (loader == null)
        {
            return;
        }

        if (!loaderPool.Contains(loader))
        {
            loaderPool.Add(loader);
        }
    }

    private void EnsureLoaderPool()
    {
        int desired = Mathf.Max(1, maxConcurrentLoads);

        if (!loaderPool.Contains(modelLoader))
        {
            loaderPool.Add(modelLoader);
        }

        for (int i = loaderPool.Count; i < desired; i++)
        {
            var worker = CreateLoaderWorker(i);
            loaderPool.Add(worker);
        }

        for (int i = 0; i < loaderPool.Count; i++)
        {
            ConfigureLoader(loaderPool[i]);
        }

        RebuildAvailableLoaderQueue();
    }

    private RWXLoaderAdvanced CreateLoaderWorker(int index)
    {
        var loaderGO = new GameObject($"RWX Remote Loader Worker {index}");
        loaderGO.transform.SetParent(transform, false);
        var loader = loaderGO.AddComponent<RWXLoaderAdvanced>();
        ConfigureLoader(loader);
        return loader;
    }

    private void RebuildAvailableLoaderQueue()
    {
        availableLoaders.Clear();
        availableLoaderSet.Clear();

        foreach (var loader in loaderPool)
        {
            if (loader == null)
            {
                continue;
            }

            if (busyLoaders.Contains(loader))
            {
                continue;
            }

            if (availableLoaderSet.Add(loader))
            {
                availableLoaders.Enqueue(loader);
            }
        }
    }

    private bool TryBorrowLoader(out RWXLoaderAdvanced loader)
    {
        while (availableLoaders.Count > 0)
        {
            var candidate = availableLoaders.Dequeue();
            availableLoaderSet.Remove(candidate);

            if (candidate == null)
            {
                continue;
            }

            ConfigureLoader(candidate);
            busyLoaders.Add(candidate);
            loader = candidate;
            return true;
        }

        loader = null;
        return false;
    }

    private void ReturnLoader(RWXLoaderAdvanced loader)
    {
        if (loader == null)
        {
            return;
        }

        busyLoaders.Remove(loader);
        if (availableLoaderSet.Add(loader))
        {
            availableLoaders.Enqueue(loader);
        }
    }
}
