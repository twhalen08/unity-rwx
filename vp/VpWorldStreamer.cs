using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.Networking;
using VpNet;
using RWXLoader;

/// <summary>
/// VPWorldStreamerSmooth (Improved + Template Clones + Optional Pooling + 5x5 Batch Spawning)
/// ---------------------------------------------------------------------------------------
/// Fixes / features:
/// - FIX: no more "flash at (0,0,0)" by keeping instances INACTIVE until after placement.
/// - Template cloning: load each unique modelId once as an inactive TEMPLATE, clone for repeats.
/// - Optional pooling: recycle clones on cell unload.
/// - Optional action parse cache.
/// - Optional 5x5 region batching: group by (region + modelId + actionString) and spawn many clones at once.
/// - CREATE actions applied via a budgeted loop (time-sliced).
///
/// Assumes these exist in your project:
/// - RWXLoaderAdvanced, RWXAssetManager
/// - VpActionParser, VpActionExecutor
/// - VpActivateActions, VpModelScaleContext
/// - VpPreprocessActionInput, VpPreprocessedAction, VpPreprocessedActionType
/// </summary>
public class VPWorldStreamerSmooth : MonoBehaviour
{
    [Header("VP Login")]
    public string userName = "Tom";
    public string botName = "Unity";
    public string applicationName = "Unity";
    public string worldName = "VP-Build";
    [Tooltip("World password (leave blank if none). Don't hardcode secrets in scripts.")]
    public string worldPassword = "";

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
    private const float VpCellSizeVpUnits = 2000f;

    [Tooltip("How many Unity units equal 1 VP unit. If 1 Unity unit == 1 VP unit, set 1.")]
    public float unityUnitsPerVpUnit = 0.5f;

    [FormerlySerializedAs("vpUnitsPerUnityUnit")]
    [SerializeField, HideInInspector]
    private float legacyVpUnitsPerUnityUnit = 0f;

    [SerializeField, HideInInspector]
    private bool scaleMigrated = false;

    [Header("Model Loader")]
    [Tooltip("Assign your RWXLoaderAdvanced here, or we'll create one at runtime")]
    public RWXLoaderAdvanced modelLoader;

    [Header("Colliders")]
    [Tooltip("Add MeshColliders to loaded models so they are solid.")]
    public bool addModelColliders = true;

    [Header("VP Object Server")]
    public string objectPath = "http://objects.virtualparadise.org/vpbuild/";
    public string objectPathPassword = "";

    [Header("Throttles")]
    [Tooltip("Max cell queries in-flight at once")]
    public int maxConcurrentCellQueries = 2;

    [Tooltip("Max template loads in-flight at once (when batching + templates, this mostly gates TEMPLATE loads).")]
    public int maxConcurrentModelLoads = 2;

    [Tooltip("Max heap items to START per frame (keeps frame stable). For batching, this is batches-per-frame.")]
    public int maxModelStartsPerFrame = 2;

    [Header("Smoothness Budget")]
    [Tooltip("Max milliseconds per frame spent applying actions to loaded models (lower = smoother, slower load).")]
    public float modelWorkBudgetMs = 2.5f;

    [Tooltip("If true, apply CREATE actions over multiple frames using the budget.")]
    public bool sliceActionApplication = true;

    [Tooltip("Cooldown between heap reprioritizations when camera cell changes. 0 = always.")]
    public float reprioritizeCooldownSeconds = 0.25f;

    [Header("Prioritization")]
    [Tooltip("When camera changes cell, rebuild pending priorities so nearer objects load first.")]
    public bool reprioritizeModelsOnCellChange = true;

    [Tooltip("If true, skip spawns whose cell root was unloaded before we got to them.")]
    public bool dropModelsFromUnloadedCells = true;

    [Header("Nearest-First Boost")]
    [Tooltip("Within this radius (Unity units), objects get a hard priority boost and will load first.")]
    public float nearBoostRadius = 30f;

    [Tooltip("How strong the near boost is. More negative = earlier. Keep very negative.")]
    public float nearBoostPriority = -10_000_000f;

    [Header("Frustum Prioritization (Optional)")]
    [Tooltip("Usually not needed for 'nearest first'. Leave off unless you want 'visible first'.")]
    public bool prioritizeFrustum = false;

    [Tooltip("How much to favor objects in the view frustum. Bigger = stronger bias. Use small values if enabled.")]
    public float frustumBonus = 10_000f;

    [Tooltip("Ignore frustum test beyond this distance (Unity units). 0 = no limit.")]
    public float frustumMaxDistance = 0f;

    [Header("Periodic Reprioritize")]
    [Tooltip("If > 0, reprioritize pending items every X seconds while camera moves/rotates.")]
    public float periodicReprioritizeSeconds = 0.35f;

    [Tooltip("Minimum camera movement (Unity units) to consider for periodic reprioritization.")]
    public float reprioritizeMoveThreshold = 0.25f;

    [Tooltip("Minimum camera rotation change (degrees) to consider for periodic reprioritization.")]
    public float reprioritizeRotateThresholdDegrees = 2.5f;

    [Header("Template Clones + Pooling")]
    [Tooltip("Load each unique modelId once into a hidden template, then Instantiate() clones for repeats (pp16 x10).")]
    public bool useTemplateClones = true;

    [Tooltip("If true, return model instances to a pool on cell unload and reuse them on reload.")]
    public bool enablePooling = true;

    [Tooltip("Max inactive instances kept per modelId when pooling.")]
    public int maxPoolPerModel = 256;

    [Tooltip("If true, cache action parsing by the raw action string (good when lots of repeats).")]
    public bool cacheParsedActions = true;

    [Header("5x5 Batching")]
    [Tooltip("Group spawns by (region + modelId + actionString) and spawn many clones in bursts.")]
    public bool enable5x5Batching = true;

    [Tooltip("Region size in CELLS (5 means 5x5 cell regions).")]
    public int batchRegionSizeCells = 5;

    [Tooltip("Max clones to spawn per frame from a batch (keeps frame stable).")]
    public int maxBatchInstancesPerFrame = 50;

    [Header("Terrain")]
    public bool streamTerrain = true;
    public int terrainTileCellSpan = 32;
    public int terrainNodeCellSpan = 8;
    public bool addTerrainColliders = true;
    public Material terrainMaterialTemplate;
    public float terrainHeightOffset = -0.01f;
    public int maxConcurrentTerrainQueries = 1;
    public bool dropTerrainFromUnloadedTiles = true;

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
    private NativeArray<DesiredCellData> desiredCellBuffer;

    private readonly Dictionary<(int tx, int tz), GameObject> terrainTiles = new();
    private readonly HashSet<(int tx, int tz)> loadedTerrainTiles = new();
    private readonly HashSet<(int tx, int tz)> queuedTerrainTiles = new();
    private readonly HashSet<(int tx, int tz)> queryingTerrainTiles = new();
    private readonly MinHeap<(int tx, int tz)> queuedTerrainHeap = new MinHeap<(int tx, int tz)>();
    private readonly Dictionary<ushort, Material> terrainMaterialCache = new();
    private readonly HashSet<ushort> terrainDownloadsInFlight = new();
    private readonly HashSet<(int tx, int tz)> desiredTerrainTiles = new();
    private readonly Dictionary<(int cx, int cz), TerrainCellCacheEntry> terrainCellCache = new();
    private readonly Dictionary<(int tx, int tz), TerrainNode[]> terrainTileNodes = new();
    private readonly int[] terrainFetchAllNodes = Enumerable.Repeat(-1, 16).ToArray();
    private GameObject terrainRoot;

    // Template + pool roots
    private GameObject templateRoot;
    private GameObject poolRoot;

    // Template per modelId
    private readonly Dictionary<string, GameObject> modelTemplateCache = new();
    private readonly HashSet<string> templateLoadsInFlight = new();

    // Pools per modelId
    private readonly Dictionary<string, Stack<GameObject>> modelPools = new();

    // Optional parsed action cache
    private readonly Dictionary<string, (List<VpActionCommand> create, List<VpActionCommand> activate)> actionParseCache = new();

    private struct DesiredCellData
    {
        public int cx;
        public int cy;
        public int chebyshev;
        public int manhattan;
    }

    private struct DesiredCellJob : IJobParallelFor
    {
        [ReadOnly] public int centerX;
        [ReadOnly] public int centerY;
        [ReadOnly] public int radius;
        [WriteOnly] public NativeArray<DesiredCellData> results;

        public void Execute(int index)
        {
            int gridSize = radius * 2 + 1;
            int dx = (index % gridSize) - radius;
            int dy = (index / gridSize) - radius;

            results[index] = new DesiredCellData
            {
                cx = centerX + dx,
                cy = centerY + dy,
                chebyshev = math.max(math.abs(dx), math.abs(dy)),
                manhattan = math.abs(dx) + math.abs(dy)
            };
        }
    }

    private struct TerrainCellData
    {
        public bool hasData;
        public bool isHole;
        public float height;
        public ushort texture;
        public byte rotation;
    }

    private struct TerrainCellCacheEntry
    {
        public bool hasData;
        public bool isHole;
        public float height;
    }

    // ---------- Action preprocessing (Burst) ----------
    [BurstCompile]
    private struct PreprocessActionJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<VpPreprocessActionInput> inputs;
        [WriteOnly] public NativeArray<VpPreprocessedAction> outputs;

        public void Execute(int index)
        {
            var input = inputs[index];
            var result = new VpPreprocessedAction
            {
                type = input.type,
                input0 = input.input0,
                valid = false
            };

            switch (input.type)
            {
                case VpPreprocessedActionType.Ambient:
                    result.value0 = math.clamp(input.input0.x, 0f, 1f);
                    result.valid = true;
                    break;

                case VpPreprocessedActionType.Diffuse:
                    result.value0 = math.max(0f, input.input0.x);
                    result.valid = true;
                    break;

                case VpPreprocessedActionType.Visible:
                    result.value0 = input.flags != 0 ? 1f : 0f;
                    result.valid = true;
                    break;

                case VpPreprocessedActionType.Scale:
                    float3 minScale = new float3(0.1f);
                    float3 s = new float3(input.input0.x, input.input0.y, input.input0.z);
                    result.data0 = math.max(s, minScale);
                    result.valid = true;
                    break;

                case VpPreprocessedActionType.Shear:
                    result.data0 = new float3(input.input0.x, input.input0.y, input.input0.z);
                    result.data1 = new float3(input.input0.w, input.input1.x, input.input1.y);
                    result.value0 = input.input1.z; // xMinus
                    result.valid = true;
                    break;
            }

            outputs[index] = result;
        }
    }

    private struct PendingModelLoad
    {
        public (int cx, int cy) cell;
        public string modelName;
        public UnityEngine.Vector3 position;
        public Quaternion rotation;
        public string action;
    }

    // Per-model priority heap (non-batched mode)
    private readonly MinHeap<PendingModelLoad> modelHeap = new MinHeap<PendingModelLoad>();

    // -------------------------
    // 5x5 batching structures
    // -------------------------
    private struct Placement
    {
        public (int cx, int cy) cell;
        public UnityEngine.Vector3 position;
        public Quaternion rotation;
    }

    private struct BatchKey : IEquatable<BatchKey>
    {
        public int rx, ry;          // region coords
        public string modelId;      // normalized
        public string action;       // raw action string (can be "")

        public bool Equals(BatchKey other)
            => rx == other.rx && ry == other.ry && modelId == other.modelId && action == other.action;

        public override bool Equals(object obj)
            => obj is BatchKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + rx;
                h = h * 31 + ry;
                h = h * 31 + (modelId?.GetHashCode() ?? 0);
                h = h * 31 + (action?.GetHashCode() ?? 0);
                return h;
            }
        }
    }

    private sealed class Batch
    {
        public BatchKey key;
        public List<Placement> placements = new List<Placement>(32);
        public List<VpActionCommand> create;
        public List<VpActionCommand> activate;
        public bool actionsParsed;
    }

    private readonly Dictionary<BatchKey, Batch> batches = new();
    private readonly MinHeap<BatchKey> batchHeap = new MinHeap<BatchKey>();

    private int inFlightCellQueries = 0;
    private int inFlightModelLoads = 0;   // in batched mode, this mainly counts TEMPLATE loads / batch starts
    private int inFlightTerrainQueries = 0;

    private (int cx, int cy) lastCameraCell = (int.MinValue, int.MinValue);
    private (int cx, int cy) lastTerrainCameraCell = (int.MinValue, int.MinValue);

    private float nextReprioritizeTime = 0f;
    private float nextPeriodicReprioritizeTime = 0f;
    private UnityEngine.Vector3 lastReprioCamPos;
    private Quaternion lastReprioCamRot;

    // Debug
    private (int cx, int cy) debugCamCell;
    private UnityEngine.Vector3 debugCamPos;

    // -------------------------
    // Action application queue (budgeted)
    // -------------------------
    private struct ActionApplyStep
    {
        public bool isPreprocessed;
        public int preprocessedIndex;
        public VpActionCommand command;
    }

    private sealed class ActionWorkItem
    {
        public GameObject target;
        public List<ActionApplyStep> steps;
        public List<VpPreprocessedAction> preprocessed;
        public int stepIndex;

        public List<VpActionCommand> activateActions;

        public string objectPath;
        public string objectPathPassword;

        public bool isValid => target != null;
    }

    private readonly Queue<ActionWorkItem> actionQueue = new Queue<ActionWorkItem>(256);

    // Attach this to instances so unload can return them to the right pool
    private sealed class VpModelId : MonoBehaviour
    {
        public string modelId;
    }

    private void Start()
    {
        MigrateLegacyScaleIfNeeded();
        if (targetCamera == null) targetCamera = Camera.main;

        SetupModelLoader();
        EnsureTemplateAndPoolRoots();

        if (streamTerrain && terrainMaterialTemplate == null)
        {
            terrainMaterialTemplate = new Material(Shader.Find("Standard"))
            {
                name = "VP Terrain Material"
            };
        }

        if (streamTerrain && terrainRoot == null)
            terrainRoot = new GameObject("VP Terrain Root");

        StartCoroutine(InitializeAndStream());
    }

    private void OnValidate()
    {
        MigrateLegacyScaleIfNeeded();
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

        StartCoroutine(ModelActionLoop());
        StartCoroutine(CellStreamingLoop());
        if (streamTerrain) StartCoroutine(TerrainStreamingLoop());
        StartCoroutine(ModelStreamingLoop());

        lastReprioCamPos = GetCameraWorldPos();
        lastReprioCamRot = (targetCamera != null) ? targetCamera.transform.rotation : Quaternion.identity;
        nextPeriodicReprioritizeTime = Time.time + Mathf.Max(0.05f, periodicReprioritizeSeconds);
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

    private void EnsureTemplateAndPoolRoots()
    {
        if (templateRoot == null)
        {
            templateRoot = new GameObject("VP_ModelTemplates");
            DontDestroyOnLoad(templateRoot);
        }

        if (poolRoot == null)
        {
            poolRoot = new GameObject("VP_ModelPool");
            DontDestroyOnLoad(poolRoot);
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

                BuildDesiredCellsWithJob(camCell.cx, camCell.cy, loadRadius);

                if (unloadRadius >= 0)
                    UnloadFarCells(camCell.cx, camCell.cy, unloadRadius);

                if (cellChanged && reprioritizeModelsOnCellChange)
                {
                    if (reprioritizeCooldownSeconds <= 0f || Time.time >= nextReprioritizeTime)
                    {
                        if (enable5x5Batching)
                            ReprioritizePendingBatches();
                        else
                            ReprioritizePendingModels();

                        nextReprioritizeTime = Time.time + Mathf.Max(0.01f, reprioritizeCooldownSeconds);
                    }
                }
            }

            while (inFlightCellQueries < maxConcurrentCellQueries)
            {
                var next = DequeueClosestQueuedCell();
                if (next.cx == int.MinValue) break;

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
            if (periodicReprioritizeSeconds > 0f)
            {
                bool hasPending = enable5x5Batching ? batchHeap.Count > 0 : modelHeap.Count > 0;
                if (hasPending && Time.time >= nextPeriodicReprioritizeTime)
                {
                    UnityEngine.Vector3 camPos = GetCameraWorldPos();
                    Quaternion camRot = (targetCamera != null) ? targetCamera.transform.rotation : Quaternion.identity;

                    float moved = UnityEngine.Vector3.Distance(camPos, lastReprioCamPos);
                    float angled = Quaternion.Angle(camRot, lastReprioCamRot);

                    if (moved >= reprioritizeMoveThreshold || angled >= reprioritizeRotateThresholdDegrees)
                    {
                        if (enable5x5Batching)
                            ReprioritizePendingBatches();
                        else
                            ReprioritizePendingModels();

                        lastReprioCamPos = camPos;
                        lastReprioCamRot = camRot;
                    }

                    nextPeriodicReprioritizeTime = Time.time + Mathf.Max(0.05f, periodicReprioritizeSeconds);
                }
            }

            int startedThisFrame = 0;

            if (enable5x5Batching)
            {
                while (batchHeap.Count > 0 &&
                       inFlightModelLoads < maxConcurrentModelLoads &&
                       startedThisFrame < maxModelStartsPerFrame)
                {
                    var key = batchHeap.PopMin();

                    if (!batches.TryGetValue(key, out var batch) || batch == null || batch.placements.Count == 0)
                    {
                        batches.Remove(key);
                        continue;
                    }

                    inFlightModelLoads++;
                    startedThisFrame++;
                    StartCoroutine(SpawnBatch(batch));
                }

                yield return null;
                continue;
            }

            // Non-batched path (your previous behavior)
            while (modelHeap.Count > 0 &&
                   inFlightModelLoads < maxConcurrentModelLoads &&
                   startedThisFrame < maxModelStartsPerFrame)
            {
                var req = modelHeap.PopMin();

                Transform parent = null;
                if (cellRoots.TryGetValue(req.cell, out var cellRoot) && cellRoot != null)
                    parent = cellRoot.transform;

                if (dropModelsFromUnloadedCells && parent == null)
                    continue;

                inFlightModelLoads++;
                startedThisFrame++;
                StartCoroutine(LoadOneModelThenQueueActions(req, parent));
            }

            yield return null;
        }
    }

    private IEnumerator TerrainStreamingLoop()
    {
        while (true)
        {
            var camCell = GetCameraCell();
            bool cellChanged = camCell != lastTerrainCameraCell;

            if (!updateOnlyOnCellChange || cellChanged)
            {
                lastTerrainCameraCell = camCell;

                BuildDesiredTerrainTiles(camCell.cx, camCell.cy, loadRadius);

                if (unloadRadius >= 0)
                    UnloadFarTerrainTiles(camCell.cx, camCell.cy, unloadRadius);
            }

            while (inFlightTerrainQueries < maxConcurrentTerrainQueries)
            {
                var next = DequeueNextTerrainTile();
                if (next.tx == int.MinValue) break;

                queuedTerrainTiles.Remove(next);
                queryingTerrainTiles.Add(next);
                inFlightTerrainQueries++;

                StartCoroutine(QueryTerrainTile(next.tx, next.tz));
            }

            yield return null;
        }
    }

    // -------------------------
    // Cell query + enqueue
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
            string actionStr = obj.Action ?? "";

            if (!enable5x5Batching)
            {
                var req = new PendingModelLoad
                {
                    cell = key,
                    modelName = modelName,
                    position = pos,
                    rotation = rot,
                    action = actionStr
                };

                float pri = ComputeModelPriority(pos, camPos);
                modelHeap.Push(req, pri);
                continue;
            }

            // Batched mode
            string modelId = NormalizeModelId(modelName);
            var region = GetRegionForCell(cellX, cellY);

            var bk = new BatchKey
            {
                rx = region.rx,
                ry = region.ry,
                modelId = modelId,
                action = actionStr
            };

            if (!batches.TryGetValue(bk, out var batch))
            {
                batch = new Batch { key = bk };
                batches[bk] = batch;

                // Priority starts based on first placement
                float pri = ComputeModelPriority(pos, camPos);
                batchHeap.Push(bk, pri);
            }
            else
            {
                // Lazy reprio: push again with possibly better priority (duplicates ok; spawn removes batch)
                float pri = ComputeModelPriority(pos, camPos);
                batchHeap.Push(bk, pri);
            }

            batch.placements.Add(new Placement
            {
                cell = key,
                position = pos,
                rotation = rot
            });
        }
    }

    private (int rx, int ry) GetRegionForCell(int cx, int cy)
    {
        int s = Mathf.Max(1, batchRegionSizeCells);
        int rx = Mathf.FloorToInt(cx / (float)s);
        int ry = Mathf.FloorToInt(cy / (float)s);
        return (rx, ry);
    }

    // -------------------------
    // Batched spawning
    // -------------------------
    private IEnumerator SpawnBatch(Batch batch)
    {
        // Ensure actions parsed once
        if (!batch.actionsParsed)
        {
            GetOrParseActions(batch.key.action, out batch.create, out batch.activate);
            batch.actionsParsed = true;
        }

        // Ensure template (this is the expensive part)
        if (useTemplateClones)
            yield return EnsureTemplateLoaded(batch.key.modelId);

        // Release “in flight” slot as soon as template is ready (so other batches can start)
        inFlightModelLoads--;

        if (useTemplateClones)
        {
            if (!modelTemplateCache.TryGetValue(batch.key.modelId, out var template) || template == null)
            {
                batches.Remove(batch.key);
                yield break;
            }
        }

        int i = 0;
        while (i < batch.placements.Count)
        {
            int spawnedThisFrame = 0;

            while (i < batch.placements.Count && spawnedThisFrame < maxBatchInstancesPerFrame)
            {
                var p = batch.placements[i++];
                if (dropModelsFromUnloadedCells && (!cellRoots.TryGetValue(p.cell, out var cellRoot) || cellRoot == null))
                    continue;

                Transform parent = (cellRoots.TryGetValue(p.cell, out var root) && root != null) ? root.transform : null;
                if (parent == null) continue;

                // Acquire instance (pool -> clone template -> direct load fallback)
                GameObject instance = null;

                if (useTemplateClones && enablePooling)
                    instance = TryAcquireFromPool(batch.key.modelId, parent);

                if (instance == null)
                {
                    if (useTemplateClones)
                    {
                        instance = Instantiate(modelTemplateCache[batch.key.modelId], parent, false);
                        instance.name = batch.key.modelId;
                        instance.SetActive(false); // IMPORTANT: prevent origin flash
                    }
                    else
                    {
                        // Rare: batching enabled but template clones disabled
                        // Do a direct load per instance (slow), but still works.
                        bool done = false;
                        yield return LoadInstanceDirect(batch.key.modelId, parent, activateOnInstantiate: true, (go) =>
                        {
                            instance = go;
                            done = true;
                        });
                        while (!done) yield return null;
                        if (instance == null) continue;

                        instance.SetActive(false); // keep hidden until placed
                    }
                }

                // Place + show
                SetupInstanceForPlacement(batch.key.modelId, instance, p.position, p.rotation, parent);

                // Now show (after placement)
                instance.SetActive(true);

                // Queue actions (budgeted)
                if ((batch.create != null && batch.create.Count > 0) || (batch.activate != null && batch.activate.Count > 0))
                {
                    var work = BuildActionWorkItem(instance, batch.create, batch.activate);
                    if (work != null)
                        actionQueue.Enqueue(work);
                }

                spawnedThisFrame++;
            }

            yield return null;
        }

        batches.Remove(batch.key);
    }

    private void ReprioritizePendingBatches()
    {
        UnityEngine.Vector3 camPos = GetCameraWorldPos();

        var keys = batchHeap.DumpItems();
        batchHeap.Clear();

        for (int i = 0; i < keys.Count; i++)
        {
            var k = keys[i];
            if (!batches.TryGetValue(k, out var batch) || batch == null || batch.placements.Count == 0)
                continue;

            // priority = closest placement in this batch
            float best = float.MaxValue;
            for (int p = 0; p < batch.placements.Count; p++)
            {
                float pri = ComputeModelPriority(batch.placements[p].position, camPos);
                if (pri < best) best = pri;
            }

            batchHeap.Push(k, best);
        }
    }

    // -------------------------
    // Non-batched per-model path (kept for debugging / fallback)
    // -------------------------
    private IEnumerator LoadOneModelThenQueueActions(PendingModelLoad req, Transform parent)
    {
        string modelId = NormalizeModelId(req.modelName);

        if (dropModelsFromUnloadedCells && parent == null)
        {
            inFlightModelLoads--;
            yield break;
        }

        GetOrParseActions(req.action, out var createActions, out var activateActions);
        bool activateOnInstantiate = createActions.Count == 0;

        GameObject instance = null;
        bool instanceReady = false;

        yield return SpawnModelInstanceTemplateFirst(modelId, parent, activateOnInstantiate, (go) =>
        {
            instance = go;
            instanceReady = true;
        });

        inFlightModelLoads--;

        if (!instanceReady || instance == null)
            yield break;

        if (dropModelsFromUnloadedCells)
        {
            if (!cellRoots.TryGetValue(req.cell, out var cellRoot) || cellRoot == null)
            {
                if (enablePooling && useTemplateClones)
                    ReleaseToPool(modelId, instance);
                else
                    Destroy(instance);
                yield break;
            }
        }

        SetupInstanceForPlacement(modelId, instance, req.position, req.rotation, parent);

        // show AFTER placement (prevents 0,0,0 flash)
        if (!instance.activeSelf) instance.SetActive(true);

        if (createActions.Count > 0 || activateActions.Count > 0)
        {
            var work = BuildActionWorkItem(instance, createActions, activateActions);
            if (work != null)
                actionQueue.Enqueue(work);
        }

        yield return null;
    }

    private IEnumerator SpawnModelInstanceTemplateFirst(
        string modelId,
        Transform parent,
        bool activateOnInstantiate,
        Action<GameObject> onReady)
    {
        EnsureTemplateAndPoolRoots();

        // 1) Pool
        if (useTemplateClones && enablePooling)
        {
            var pooled = TryAcquireFromPool(modelId, parent);
            if (pooled != null)
            {
                // IMPORTANT: keep inactive until placed
                pooled.SetActive(false);
                onReady?.Invoke(pooled);
                yield break;
            }
        }

        // 2) Template exists
        if (useTemplateClones && modelTemplateCache.TryGetValue(modelId, out var template) && template != null)
        {
            var clone = Instantiate(template, parent, false);
            clone.name = modelId;

            // IMPORTANT: keep inactive until placed
            clone.SetActive(false);

            onReady?.Invoke(clone);
            yield break;
        }

        // 3) Ensure template loaded
        if (useTemplateClones)
        {
            yield return EnsureTemplateLoaded(modelId);

            if (!modelTemplateCache.TryGetValue(modelId, out template) || template == null)
            {
                onReady?.Invoke(null);
                yield break;
            }

            var clone = Instantiate(template, parent, false);
            clone.name = modelId;

            // IMPORTANT: keep inactive until placed
            clone.SetActive(false);

            onReady?.Invoke(clone);
            yield break;
        }

        // Fallback (no templates): load per-instance
        yield return LoadInstanceDirect(modelId, parent, activateOnInstantiate, (go) =>
        {
            if (go != null) go.SetActive(false); // IMPORTANT: keep inactive until placed
            onReady?.Invoke(go);
        });
    }

    private IEnumerator EnsureTemplateLoaded(string modelId)
    {
        EnsureTemplateAndPoolRoots();

        if (modelTemplateCache.TryGetValue(modelId, out var existing) && existing != null)
            yield break;

        // If already loading, wait
        if (templateLoadsInFlight.Contains(modelId))
        {
            while (!modelTemplateCache.TryGetValue(modelId, out var t) || t == null)
                yield return null;
            yield break;
        }

        templateLoadsInFlight.Add(modelId);

        bool completed = false;
        GameObject loaded = null;
        string error = null;

        modelLoader.parentTransform = templateRoot.transform;

        modelLoader.LoadModelFromRemote(
            modelId,
            modelLoader.defaultObjectPath,
            (go, errMsg) =>
            {
                loaded = go;
                error = errMsg;
                completed = true;
            },
            objectPathPassword,
            activateOnInstantiate: true
        );

        while (!completed)
            yield return null;

        templateLoadsInFlight.Remove(modelId);

        if (loaded == null)
        {
            Debug.LogError($"[VP] Template RWX load failed: {modelId} → {error}");
            yield break;
        }

        loaded.name = $"TEMPLATE_{modelId}";
        loaded.transform.SetParent(templateRoot.transform, false);

        ApplyModelBaseScale(loaded);
        EnsureModelColliders(loaded);

        // Keep template inactive
        loaded.SetActive(false);

        var id = loaded.GetComponent<VpModelId>();
        if (id == null) id = loaded.AddComponent<VpModelId>();
        id.modelId = modelId;

        modelTemplateCache[modelId] = loaded;
    }

    private IEnumerator LoadInstanceDirect(string modelId, Transform parent, bool activateOnInstantiate, Action<GameObject> onReady)
    {
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
            objectPathPassword,
            activateOnInstantiate
        );

        while (!completed)
            yield return null;

        if (loadedObject == null)
        {
            Debug.LogError($"RWX load failed: {modelId} → {errorMessage}");
            onReady?.Invoke(null);
            yield break;
        }

        EnsureModelColliders(loadedObject);

        var id = loadedObject.GetComponent<VpModelId>();
        if (id == null) id = loadedObject.AddComponent<VpModelId>();
        id.modelId = modelId;

        onReady?.Invoke(loadedObject);
    }

    private GameObject TryAcquireFromPool(string modelId, Transform parent)
    {
        if (!enablePooling) return null;

        if (modelPools.TryGetValue(modelId, out var stack) && stack != null)
        {
            while (stack.Count > 0)
            {
                var go = stack.Pop();
                if (go == null) continue;

                go.transform.SetParent(parent, false);

                // IMPORTANT: stay inactive until placement
                go.SetActive(false);

                return go;
            }
        }

        return null;
    }

    private void ReleaseToPool(string modelId, GameObject go)
    {
        if (!enablePooling || go == null)
        {
            if (go != null) Destroy(go);
            return;
        }

        if (templateRoot != null && go.transform.IsChildOf(templateRoot.transform))
        {
            Destroy(go);
            return;
        }

        if (!modelPools.TryGetValue(modelId, out var stack) || stack == null)
        {
            stack = new Stack<GameObject>(32);
            modelPools[modelId] = stack;
        }

        if (stack.Count >= Mathf.Max(0, maxPoolPerModel))
        {
            Destroy(go);
            return;
        }

        ResetInstanceForReuse(go);

        go.SetActive(false);
        go.transform.SetParent(poolRoot.transform, false);
        stack.Push(go);
    }

    private void SetupInstanceForPlacement(string modelId, GameObject go, UnityEngine.Vector3 pos, Quaternion rot, Transform parent)
    {
        if (go == null) return;

        go.transform.SetParent(parent, false);

        ResetInstanceForReuse(go);

        go.transform.localPosition = pos;
        go.transform.localRotation = rot;

        ApplyModelBaseScale(go);

        if (!useTemplateClones)
            EnsureModelColliders(go);

        var id = go.GetComponent<VpModelId>();
        if (id == null) id = go.AddComponent<VpModelId>();
        id.modelId = modelId;
    }

    private void ResetInstanceForReuse(GameObject go)
    {
        if (go == null) return;

        var act = go.GetComponent<VpActivateActions>();
        if (act != null)
            act.actions.Clear();
    }

    private string NormalizeModelId(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            return "";

        string m = modelName.Trim();

        if (m.EndsWith(".rwx", StringComparison.OrdinalIgnoreCase))
            m = Path.GetFileNameWithoutExtension(m);

        m = Path.GetFileNameWithoutExtension(m);
        return m;
    }

    private void GetOrParseActions(string actionString, out List<VpActionCommand> create, out List<VpActionCommand> activate)
    {
        create = null;
        activate = null;

        if (string.IsNullOrWhiteSpace(actionString))
        {
            create = new List<VpActionCommand>();
            activate = new List<VpActionCommand>();
            return;
        }

        if (cacheParsedActions && actionParseCache.TryGetValue(actionString, out var cached))
        {
            create = cached.create ?? new List<VpActionCommand>();
            activate = cached.activate ?? new List<VpActionCommand>();
            return;
        }

        VpActionParser.Parse(actionString, out create, out activate);
        create ??= new List<VpActionCommand>();
        activate ??= new List<VpActionCommand>();

        if (cacheParsedActions)
            actionParseCache[actionString] = (create, activate);
    }

    // -------------------------
    // Action work items + action loop
    // -------------------------
    private ActionWorkItem BuildActionWorkItem(GameObject target, List<VpActionCommand> createActions, List<VpActionCommand> activateActions)
    {
        if (target == null)
            return null;

        var steps = new List<ActionApplyStep>(createActions?.Count ?? 0);
        var preInputs = new List<VpPreprocessActionInput>(createActions?.Count ?? 0);

        for (int i = 0; i < (createActions?.Count ?? 0); i++)
        {
            var cmd = createActions[i];
            if (TryBuildPreprocessInput(cmd, out var input))
            {
                steps.Add(new ActionApplyStep
                {
                    isPreprocessed = true,
                    preprocessedIndex = preInputs.Count,
                    command = null
                });
                preInputs.Add(input);
            }
            else
            {
                steps.Add(new ActionApplyStep
                {
                    isPreprocessed = false,
                    preprocessedIndex = -1,
                    command = cmd
                });
            }
        }

        var preprocessed = new List<VpPreprocessedAction>(preInputs.Count);

        if (preInputs.Count > 0)
        {
            var inputs = new NativeArray<VpPreprocessActionInput>(preInputs.Count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var outputs = new NativeArray<VpPreprocessedAction>(preInputs.Count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

            for (int i = 0; i < preInputs.Count; i++)
                inputs[i] = preInputs[i];

            var job = new PreprocessActionJob { inputs = inputs, outputs = outputs };
            int batchSize = math.max(1, preInputs.Count / 4);
            job.Schedule(preInputs.Count, batchSize).Complete();

            for (int i = 0; i < outputs.Length; i++)
                preprocessed.Add(outputs[i]);

            inputs.Dispose();
            outputs.Dispose();
        }

        return new ActionWorkItem
        {
            target = target,
            steps = steps,
            preprocessed = preprocessed,
            stepIndex = 0,
            activateActions = activateActions ?? new List<VpActionCommand>(),
            objectPath = modelLoader.defaultObjectPath,
            objectPathPassword = objectPathPassword
        };
    }

    private IEnumerator ModelActionLoop()
    {
        while (true)
        {
            if (!sliceActionApplication)
            {
                while (actionQueue.Count > 0)
                {
                    var item = actionQueue.Dequeue();
                    if (item == null || !item.isValid) continue;

                    for (int i = item.stepIndex; i < item.steps.Count; i++)
                        ApplyOneActionStep(item, item.steps[i]);

                    StoreActivateActions(item);
                }

                yield return null;
                continue;
            }

            float start = Time.realtimeSinceStartup;
            float budgetMs = Mathf.Max(0.1f, modelWorkBudgetMs);

            while (actionQueue.Count > 0)
            {
                var item = actionQueue.Peek();
                if (item == null || !item.isValid)
                {
                    actionQueue.Dequeue();
                    continue;
                }

                while (item.stepIndex < item.steps.Count)
                {
                    ApplyOneActionStep(item, item.steps[item.stepIndex]);
                    item.stepIndex++;

                    float elapsedMs = (Time.realtimeSinceStartup - start) * 1000f;
                    if (elapsedMs >= budgetMs)
                        break;
                }

                if (item.stepIndex >= item.steps.Count)
                {
                    StoreActivateActions(item);
                    actionQueue.Dequeue();
                }

                if ((Time.realtimeSinceStartup - start) * 1000f >= budgetMs)
                    break;
            }

            yield return null;
        }
    }

    private void ApplyOneActionStep(ActionWorkItem item, ActionApplyStep step)
    {
        if (item == null || item.target == null) return;

        if (step.isPreprocessed)
        {
            if (step.preprocessedIndex >= 0 && step.preprocessedIndex < item.preprocessed.Count)
                ApplyPreprocessedCreateAction(item.target, item.preprocessed[step.preprocessedIndex]);
        }
        else if (step.command != null)
        {
            VpActionExecutor.ExecuteCreate(item.target, step.command, item.objectPath, item.objectPathPassword, this);
        }
    }

    private void StoreActivateActions(ActionWorkItem item)
    {
        if (item == null || item.target == null) return;
        if (item.activateActions == null || item.activateActions.Count == 0) return;

        var act = item.target.GetComponent<VpActivateActions>();
        if (act == null) act = item.target.AddComponent<VpActivateActions>();
        act.actions.AddRange(item.activateActions);

        if (logActivateActions)
            Debug.Log($"[VP Activate] {item.target.name} stored {item.activateActions.Count} actions");
    }

    // -------------------------
    // Priority computation
    // -------------------------
    private float ComputeModelPriority(UnityEngine.Vector3 objPos, UnityEngine.Vector3 camPos)
    {
        float sqr = (objPos - camPos).sqrMagnitude;

        if (nearBoostRadius > 0f)
        {
            float r2 = nearBoostRadius * nearBoostRadius;
            if (sqr <= r2)
                return nearBoostPriority + sqr;
        }

        float pri = sqr;

        if (prioritizeFrustum && IsInViewFrustum(objPos))
            pri -= frustumBonus;

        return pri;
    }

    private float ComputeTerrainPriority(int centerCellX, int centerCellY, int tileX, int tileZ)
    {
        int tileSpan = Mathf.Max(1, terrainTileCellSpan);
        float centerX = tileX * tileSpan + tileSpan * 0.5f;
        float centerZ = tileZ * tileSpan + tileSpan * 0.5f;

        float dx = Mathf.Abs(centerX - centerCellX);
        float dz = Mathf.Abs(centerZ - centerCellY);

        return Mathf.Max(dx, dz) * 1000f + (dx + dz);
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

        float vpUnitsPerUnity = GetVpUnitsPerUnityUnit();
        float vpX = (-u.x) * vpUnitsPerUnity;
        float vpZ = (u.z) * vpUnitsPerUnity;

        float cellSize = Mathf.Max(1f, VpCellSizeVpUnits);

        int cx = Mathf.FloorToInt(vpX / cellSize);
        int cy = Mathf.FloorToInt(vpZ / cellSize);

        return (cx, cy);
    }

    private void BuildDesiredCellsWithJob(int centerX, int centerY, int radius)
    {
        int clampedRadius = Mathf.Max(0, radius);
        int gridSize = clampedRadius * 2 + 1;
        int cellCount = gridSize * gridSize;

        desiredCells.Clear();
        queuedCellHeap.Clear();

        foreach (var queued in queuedCells)
        {
            int dx = Mathf.Abs(queued.cx - centerX);
            int dy = Mathf.Abs(queued.cy - centerY);
            float score = Mathf.Max(dx, dy) * 100 + (dx + dy);
            queuedCellHeap.Push(queued, score);
        }

        if (cellCount == 0)
            return;

        EnsureDesiredCellBuffer(cellCount);
        if (!desiredCellBuffer.IsCreated)
            return;

        var job = new DesiredCellJob
        {
            centerX = centerX,
            centerY = centerY,
            radius = clampedRadius,
            results = desiredCellBuffer
        };

        int batchSize = Mathf.Max(1, clampedRadius);
        JobHandle handle = job.Schedule(cellCount, batchSize);
        handle.Complete();

        for (int i = 0; i < desiredCellBuffer.Length; i++)
        {
            var cell = desiredCellBuffer[i];
            var coord = (cell.cx, cell.cy);

            desiredCells.Add(coord);

            if (loadedCells.Contains(coord) || queuedCells.Contains(coord) || queryingCells.Contains(coord))
                continue;

            float score = cell.chebyshev * 100 + cell.manhattan;
            queuedCells.Add(coord);
            queuedCellHeap.Push(coord, score);
        }
    }

    private (int cx, int cy) DequeueClosestQueuedCell()
    {
        (int cx, int cy) best = (int.MinValue, int.MinValue);
        while (queuedCellHeap.Count > 0)
        {
            best = queuedCellHeap.PopMin();
            if (!queuedCells.Contains(best))
                continue;

            return best;
        }

        return best;
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

            if (cellRoots.TryGetValue(c, out var root) && root != null)
            {
                if (useTemplateClones && enablePooling)
                    ReturnCellChildrenToPool(root);

                Destroy(root);
            }

            cellRoots.Remove(c);

            if (logCellLoads) Debug.Log($"[VP] Unloaded cell ({c.cx},{c.cy})");
        }
    }

    private void ReturnCellChildrenToPool(GameObject cellRoot)
    {
        if (cellRoot == null) return;

        for (int i = cellRoot.transform.childCount - 1; i >= 0; i--)
        {
            var child = cellRoot.transform.GetChild(i).gameObject;
            if (child == null) continue;

            var id = child.GetComponent<VpModelId>();
            if (id != null && !string.IsNullOrWhiteSpace(id.modelId))
            {
                ReleaseToPool(id.modelId, child);
            }
            else
            {
                Destroy(child);
            }
        }
    }

    private void EnsureDesiredCellBuffer(int cellCount)
    {
        if (desiredCellBuffer.IsCreated && desiredCellBuffer.Length == cellCount)
            return;

        if (desiredCellBuffer.IsCreated)
            desiredCellBuffer.Dispose();

        if (cellCount > 0)
            desiredCellBuffer = new NativeArray<DesiredCellData>(cellCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
    }

    // -------------------------
    // Terrain (same as your version)
    // -------------------------
    private void BuildDesiredTerrainTiles(int centerCellX, int centerCellY, int radiusCells)
    {
        desiredTerrainTiles.Clear();
        queuedTerrainHeap.Clear();

        foreach (var queued in queuedTerrainTiles)
        {
            float score = ComputeTerrainPriority(centerCellX, centerCellY, queued.tx, queued.tz);
            queuedTerrainHeap.Push(queued, score);
        }

        int tileSpan = Mathf.Max(1, terrainTileCellSpan);
        int minTileX = Mathf.FloorToInt((centerCellX - radiusCells) / (float)tileSpan);
        int maxTileX = Mathf.FloorToInt((centerCellX + radiusCells) / (float)tileSpan);
        int minTileZ = Mathf.FloorToInt((centerCellY - radiusCells) / (float)tileSpan);
        int maxTileZ = Mathf.FloorToInt((centerCellY + radiusCells) / (float)tileSpan);

        for (int tz = minTileZ; tz <= maxTileZ; tz++)
        {
            for (int tx = minTileX; tx <= maxTileX; tx++)
            {
                var coord = (tx, tz);
                desiredTerrainTiles.Add(coord);

                if (loadedTerrainTiles.Contains(coord) || queuedTerrainTiles.Contains(coord) || queryingTerrainTiles.Contains(coord))
                    continue;

                float score = ComputeTerrainPriority(centerCellX, centerCellY, tx, tz);
                queuedTerrainTiles.Add(coord);
                queuedTerrainHeap.Push(coord, score);
            }
        }
    }

    private (int tx, int tz) DequeueNextTerrainTile()
    {
        (int tx, int tz) best = (int.MinValue, int.MinValue);
        while (queuedTerrainHeap.Count > 0)
        {
            best = queuedTerrainHeap.PopMin();
            if (!queuedTerrainTiles.Contains(best))
                continue;

            return best;
        }

        return best;
    }

    private void UnloadFarTerrainTiles(int centerCellX, int centerCellY, int radiusCells)
    {
        int tileSpan = Mathf.Max(1, terrainTileCellSpan);
        var toUnload = new List<(int tx, int tz)>();

        foreach (var tile in loadedTerrainTiles)
        {
            float centerX = tile.tx * tileSpan + tileSpan * 0.5f;
            float centerZ = tile.tz * tileSpan + tileSpan * 0.5f;

            float dx = Mathf.Abs(centerX - centerCellX);
            float dz = Mathf.Abs(centerZ - centerCellY);

            if (Mathf.Max(dx, dz) > radiusCells)
                toUnload.Add(tile);
        }

        foreach (var tile in toUnload)
        {
            loadedTerrainTiles.Remove(tile);
            queuedTerrainTiles.Remove(tile);
            queryingTerrainTiles.Remove(tile);
            desiredTerrainTiles.Remove(tile);
            terrainTileNodes.Remove(tile);

            int tileSpanLocal = Mathf.Max(1, terrainTileCellSpan);
            for (int cz = 0; cz < tileSpanLocal; cz++)
            {
                for (int cx = 0; cx < tileSpanLocal; cx++)
                    terrainCellCache.Remove((tile.tx * tileSpanLocal + cx, tile.tz * tileSpanLocal + cz));
            }

            if (terrainTiles.TryGetValue(tile, out var go) && go != null)
                Destroy(go);

            terrainTiles.Remove(tile);
        }
    }

    private IEnumerator QueryTerrainTile(int tileX, int tileZ)
    {
        var terrainTask = vpClient.QueryTerrainAsync(tileX, tileZ, terrainFetchAllNodes);
        yield return WaitForTask(terrainTask);

        inFlightTerrainQueries--;
        queryingTerrainTiles.Remove((tileX, tileZ));

        if (terrainTask.IsFaulted || terrainTask.IsCanceled)
        {
            string message = terrainTask.IsFaulted
                ? terrainTask.Exception?.GetBaseException().Message
                : "Terrain query cancelled";
            Debug.LogWarning($"[VP] Failed QueryTerrain ({tileX},{tileZ}): {message}");
            yield break;
        }

        if (dropTerrainFromUnloadedTiles &&
            !queuedTerrainTiles.Contains((tileX, tileZ)) &&
            !desiredTerrainTiles.Contains((tileX, tileZ)))
        {
            yield break;
        }

        BuildTerrainTile(tileX, tileZ, terrainTask.Result);
    }

    private void BuildTerrainTile(int tileX, int tileZ, TerrainNode[] nodes, bool rebuildNeighbors = true)
    {
        if (!streamTerrain)
            return;

        if (terrainRoot == null)
            terrainRoot = new GameObject("VP Terrain Root");

        var key = (tileX, tileZ);
        terrainTileNodes[key] = nodes;

        if (terrainTiles.TryGetValue(key, out var existing) && existing != null)
            Destroy(existing);

        var mesh = BuildTerrainMesh(tileX, tileZ, nodes, out var materials);
        if (mesh == null)
            return;

        var go = new GameObject($"VP_Terrain_{tileX}_{tileZ}");
        go.transform.SetParent(terrainRoot.transform, false);

        var mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = mesh;

        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterials = materials.ToArray();

        if (addTerrainColliders)
        {
            var mc = go.AddComponent<MeshCollider>();
            mc.sharedMesh = mesh;
        }

        terrainTiles[key] = go;
        loadedTerrainTiles.Add(key);

        if (rebuildNeighbors)
        {
            RebuildNeighborTile(tileX - 1, tileZ);
            RebuildNeighborTile(tileX + 1, tileZ);
            RebuildNeighborTile(tileX, tileZ - 1);
            RebuildNeighborTile(tileX, tileZ + 1);
        }
    }

    private void RebuildNeighborTile(int tileX, int tileZ)
    {
        var key = (tileX, tileZ);
        if (!terrainTileNodes.TryGetValue(key, out var nodes))
            return;

        if (!loadedTerrainTiles.Contains(key))
            return;

        BuildTerrainTile(tileX, tileZ, nodes, false);
    }

    private Mesh BuildTerrainMesh(int tileX, int tileZ, TerrainNode[] nodes, out List<Material> materials)
    {
        materials = new List<Material>();

        if (nodes == null || nodes.Length == 0 || terrainTileCellSpan <= 0 || terrainNodeCellSpan <= 0)
            return null;

        int tileSpan = terrainTileCellSpan;
        int nodeSpan = terrainNodeCellSpan;

        var cellData = new TerrainCellData[tileSpan, tileSpan];

        foreach (var node in nodes)
        {
            for (int cz = 0; cz < nodeSpan; cz++)
            {
                for (int cx = 0; cx < nodeSpan; cx++)
                {
                    int idx = cz * nodeSpan + cx;
                    if (node.Cells == null || idx >= node.Cells.Length)
                        continue;

                    int cellX = node.X * nodeSpan + cx;
                    int cellZ = node.Z * nodeSpan + cz;

                    if (cellX < 0 || cellX >= tileSpan || cellZ < 0 || cellZ >= tileSpan)
                        continue;

                    var cell = new TerrainCellData
                    {
                        hasData = true,
                        height = node.Cells[idx].Height,
                        texture = node.Cells[idx].Texture,
                        isHole = node.Cells[idx].IsHole,
                        rotation = ExtractTerrainRotation(node.Cells[idx])
                    };

                    cellData[cellX, cellZ] = cell;

                    int worldCX = tileX * tileSpan + cellX;
                    int worldCZ = tileZ * tileSpan + cellZ;
                    terrainCellCache[(worldCX, worldCZ)] = new TerrainCellCacheEntry
                    {
                        hasData = cell.hasData,
                        isHole = cell.isHole,
                        height = cell.height
                    };
                }
            }
        }

        float cellSizeUnity = GetUnityUnitsPerVpCell();
        if (cellSizeUnity <= 0f) cellSizeUnity = 1f;

        bool TryGetCellHeight(int worldCX, int worldCZ, out float h)
        {
            if (terrainCellCache.TryGetValue((worldCX, worldCZ), out var cachedCell) && cachedCell.hasData && !cachedCell.isHole)
            {
                h = cachedCell.height;
                return true;
            }

            int localCX = worldCX - tileX * tileSpan;
            int localCZ = worldCZ - tileZ * tileSpan;
            if (localCX >= 0 && localCX < tileSpan && localCZ >= 0 && localCZ < tileSpan)
            {
                var c = cellData[localCX, localCZ];
                if (c.hasData && !c.isHole)
                {
                    h = c.height;
                    return true;
                }
            }

            h = 0f;
            return false;
        }

        float[,] heightGrid = new float[tileSpan + 1, tileSpan + 1];
        for (int vx = 0; vx <= tileSpan; vx++)
        {
            for (int vz = 0; vz <= tileSpan; vz++)
            {
                int worldCX = tileX * tileSpan + vx;
                int worldCZ = tileZ * tileSpan + vz;
                int ownerCX = worldCX;
                int ownerCZ = worldCZ;

                float hExact;
                if (TryGetCellHeight(ownerCX, ownerCZ, out hExact) ||
                    TryGetCellHeight(ownerCX - 1, ownerCZ, out hExact) ||
                    TryGetCellHeight(ownerCX, ownerCZ - 1, out hExact) ||
                    TryGetCellHeight(ownerCX - 1, ownerCZ - 1, out hExact))
                {
                    heightGrid[vx, vz] = hExact;
                    continue;
                }

                float foundH = 0f;
                bool found = false;
                for (int radius = 1; radius <= 2 && !found; radius++)
                {
                    for (int dz = -radius; dz <= radius && !found; dz++)
                    {
                        for (int dx = -radius; dx <= radius && !found; dx++)
                        {
                            int wx = ownerCX + dx;
                            int wz = ownerCZ + dz;
                            if (TryGetCellHeight(wx, wz, out float hh))
                            {
                                foundH = hh;
                                found = true;
                            }
                        }
                    }
                }

                heightGrid[vx, vz] = found ? foundH : 0f;
            }
        }

        float unityUnitsPerVpUnit = GetUnityUnitsPerVpUnit();

        var normalsGrid = new UnityEngine.Vector3[tileSpan + 1, tileSpan + 1];
        for (int vx = 0; vx <= tileSpan; vx++)
        {
            for (int vz = 0; vz <= tileSpan; vz++)
            {
                float hL = heightGrid[Mathf.Max(0, vx - 1), vz] * unityUnitsPerVpUnit;
                float hR = heightGrid[Mathf.Min(tileSpan, vx + 1), vz] * unityUnitsPerVpUnit;
                float hD = heightGrid[vx, Mathf.Max(0, vz - 1)] * unityUnitsPerVpUnit;
                float hU = heightGrid[vx, Mathf.Min(tileSpan, vz + 1)] * unityUnitsPerVpUnit;

                float dx = (hR - hL) * 0.5f / cellSizeUnity;
                float dz = (hU - hD) * 0.5f / cellSizeUnity;

                normalsGrid[vx, vz] = new UnityEngine.Vector3(-dx, 1f, dz).normalized;
            }
        }

        var vertices = new List<UnityEngine.Vector3>(tileSpan * tileSpan * 4);
        var normals = new List<UnityEngine.Vector3>(tileSpan * tileSpan * 4);
        var uvs = new List<Vector2>(tileSpan * tileSpan * 4);
        var trianglesByTex = new Dictionary<ushort, List<int>>();

        for (int z = 0; z < tileSpan; z++)
        {
            for (int x = 0; x < tileSpan; x++)
            {
                var cell = cellData[x, z];
                if (!cell.hasData || cell.isHole)
                    continue;

                float unityX = (tileX * tileSpan + x) * cellSizeUnity;
                float unityZ = (tileZ * tileSpan + z) * cellSizeUnity;

                float h00 = heightGrid[x, z] * unityUnitsPerVpUnit + terrainHeightOffset;
                float h10 = heightGrid[x + 1, z] * unityUnitsPerVpUnit + terrainHeightOffset;
                float h01 = heightGrid[x, z + 1] * unityUnitsPerVpUnit + terrainHeightOffset;
                float h11 = heightGrid[x + 1, z + 1] * unityUnitsPerVpUnit + terrainHeightOffset;

                int vStart = vertices.Count;
                vertices.Add(new UnityEngine.Vector3(-unityX, h00, unityZ));
                vertices.Add(new UnityEngine.Vector3(-(unityX + cellSizeUnity), h10, unityZ));
                vertices.Add(new UnityEngine.Vector3(-unityX, h01, unityZ + cellSizeUnity));
                vertices.Add(new UnityEngine.Vector3(-(unityX + cellSizeUnity), h11, unityZ + cellSizeUnity));

                normals.Add(normalsGrid[x, z]);
                normals.Add(normalsGrid[x + 1, z]);
                normals.Add(normalsGrid[x, z + 1]);
                normals.Add(normalsGrid[x + 1, z + 1]);

                Vector2 uv0 = new Vector2(0f, 1f);
                Vector2 uv1 = new Vector2(1f, 1f);
                Vector2 uv2 = new Vector2(0f, 0f);
                Vector2 uv3 = new Vector2(1f, 0f);

                RotateUvQuarter(ref uv0, ref uv1, ref uv2, ref uv3, cell.rotation);

                uvs.Add(uv0);
                uvs.Add(uv1);
                uvs.Add(uv2);
                uvs.Add(uv3);

                if (!trianglesByTex.TryGetValue(cell.texture, out var tris))
                {
                    tris = new List<int>();
                    trianglesByTex[cell.texture] = tris;
                }

                tris.AddRange(new[]
                {
                    vStart, vStart + 1, vStart + 2,
                    vStart + 1, vStart + 3, vStart + 2
                });
            }
        }

        if (vertices.Count == 0 || trianglesByTex.Count == 0)
            return null;

        var mesh = new Mesh
        {
            indexFormat = vertices.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16,
            name = $"VP_Terrain_{tileX}_{tileZ}"
        };

        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.subMeshCount = trianglesByTex.Count;

        int subMesh = 0;
        foreach (var kvp in trianglesByTex)
        {
            mesh.SetTriangles(kvp.Value, subMesh++);
            materials.Add(GetTerrainMaterial(kvp.Key));
        }

        mesh.RecalculateBounds();
        return mesh;
    }

    // -------------------------
    // Preprocess input builders (same as your version)
    // -------------------------
    private bool TryBuildPreprocessInput(VpActionCommand cmd, out VpPreprocessActionInput input)
    {
        input = default;

        if (cmd == null || string.IsNullOrWhiteSpace(cmd.verb))
            return false;

        string verb = cmd.verb.Trim().ToLowerInvariant();

        switch (verb)
        {
            case "ambient":
                if (cmd.positional == null || cmd.positional.Count == 0) return false;
                input.type = VpPreprocessedActionType.Ambient;
                input.input0 = new float4(ParseFloat(cmd.positional[0], 1f), 0f, 0f, 0f);
                return true;

            case "diffuse":
                if (cmd.positional == null || cmd.positional.Count == 0) return false;
                input.type = VpPreprocessedActionType.Diffuse;
                input.input0 = new float4(ParseFloat(cmd.positional[0], 1f), 0f, 0f, 0f);
                return true;

            case "visible":
                if (cmd.positional == null || cmd.positional.Count == 0) return false;
                input.type = VpPreprocessedActionType.Visible;
                input.flags = ParseBool(cmd.positional[0]) ? 1 : 0;
                return true;

            case "scale":
                if (cmd.positional == null || cmd.positional.Count == 0) return false;
                input.type = VpPreprocessedActionType.Scale;
                BuildScaleInputs(cmd, ref input);
                return true;

            case "shear":
                if (cmd.positional == null || cmd.positional.Count == 0) return false;
                input.type = VpPreprocessedActionType.Shear;
                BuildShearInputs(cmd, ref input);
                return true;

            default:
                return false;
        }
    }

    private void BuildScaleInputs(VpActionCommand cmd, ref VpPreprocessActionInput input)
    {
        const float DefaultScale = 1f;

        float x = DefaultScale, y = DefaultScale, z = DefaultScale;

        if (cmd.positional.Count == 1)
        {
            float s = ParseFloat(cmd.positional[0], DefaultScale);
            x = y = z = s;
        }
        else if (cmd.positional.Count >= 3)
        {
            x = ParseFloat(cmd.positional[0], DefaultScale);
            y = ParseFloat(cmd.positional[1], DefaultScale);
            z = ParseFloat(cmd.positional[2], DefaultScale);
        }
        else
        {
            x = ParseFloat(cmd.positional[0], DefaultScale);
            y = ParseFloat(cmd.positional[1], DefaultScale);
            z = DefaultScale;
        }

        input.input0 = new float4(x, y, z, 0f);
    }

    private void BuildShearInputs(VpActionCommand cmd, ref VpPreprocessActionInput input)
    {
        float zPlus = GetPositionalFloat(cmd, 0, 0f);
        float xPlus = GetPositionalFloat(cmd, 1, 0f);
        float yPlus = GetPositionalFloat(cmd, 2, 0f);
        float yMinus = GetPositionalFloat(cmd, 3, 0f);
        float zMinus = GetPositionalFloat(cmd, 4, 0f);
        float xMinus = GetPositionalFloat(cmd, 5, 0f);

        input.input0 = new float4(zPlus, xPlus, yPlus, yMinus);
        input.input1 = new float3(zMinus, xMinus, xMinus);
    }

    private float ParseFloat(string s, float fallback)
    {
        if (string.IsNullOrWhiteSpace(s))
            return fallback;

        if (float.TryParse(s.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v))
            return v;

        if (float.TryParse(s.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out v))
            return v;

        return fallback;
    }

    private float GetPositionalFloat(VpActionCommand cmd, int index, float fallback)
    {
        if (cmd.positional == null || cmd.positional.Count <= index)
            return fallback;

        return ParseFloat(cmd.positional[index], fallback);
    }

    private bool ParseBool(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return false;

        string v = s.Trim().ToLowerInvariant();
        return v == "yes" || v == "true" || v == "1" || v == "on";
    }

    private void ApplyPreprocessedCreateAction(GameObject target, VpPreprocessedAction action)
    {
        if (target == null || !action.valid)
            return;

        switch (action.type)
        {
            case VpPreprocessedActionType.Ambient:
                VpActionExecutor.ApplyAmbient(target, action.value0);
                break;

            case VpPreprocessedActionType.Diffuse:
                VpActionExecutor.ApplyDiffuse(target, action.value0);
                break;

            case VpPreprocessedActionType.Visible:
                VpActionExecutor.ApplyVisible(target, action.value0 > 0.5f);
                break;

            case VpPreprocessedActionType.Scale:
                VpActionExecutor.ApplyScale(target, new UnityEngine.Vector3(action.data0.x, action.data0.y, action.data0.z));
                break;

            case VpPreprocessedActionType.Shear:
                VpActionExecutor.ApplyShear(target, action.data0.x, action.data0.y, action.data0.z, action.data1.x, action.data1.y, action.value0);
                break;
        }
    }

    // -------------------------
    // Utilities
    // -------------------------
    private IEnumerator WaitForTask(Task task)
    {
        while (!task.IsCompleted)
            yield return null;
    }

    private float GetClampedUnityUnitsPerVpUnit() => Mathf.Max(0.0001f, unityUnitsPerVpUnit);
    private float GetUnityUnitsPerVpUnit() => GetClampedUnityUnitsPerVpUnit();
    private float GetVpUnitsPerUnityUnit() => 1f / GetClampedUnityUnitsPerVpUnit();
    private float GetUnityUnitsPerVpCell() => VpCellSizeVpUnits * GetUnityUnitsPerVpUnit();

    private void MigrateLegacyScaleIfNeeded()
    {
        if (scaleMigrated)
            return;

        if (legacyVpUnitsPerUnityUnit > 0f)
            unityUnitsPerVpUnit = 1f / Mathf.Max(0.0001f, legacyVpUnitsPerUnityUnit);

        scaleMigrated = true;
        legacyVpUnitsPerUnityUnit = 0f;
    }

    private UnityEngine.Vector3 VPtoUnity(VpNet.Vector3 vpPos)
    {
        float unityUnitsPerVpUnit = GetUnityUnitsPerVpUnit();
        return new UnityEngine.Vector3(
            -(float)vpPos.X * unityUnitsPerVpUnit,
            (float)vpPos.Y * unityUnitsPerVpUnit,
            (float)vpPos.Z * unityUnitsPerVpUnit
        );
    }

    private UnityEngine.Vector3 GetBaseScaleVector()
    {
        float unityUnitsPerVpUnit = GetUnityUnitsPerVpUnit();
        return new UnityEngine.Vector3(unityUnitsPerVpUnit, unityUnitsPerVpUnit, unityUnitsPerVpUnit);
    }

    private void ApplyModelBaseScale(GameObject target)
    {
        if (target == null) return;

        UnityEngine.Vector3 baseScale = GetBaseScaleVector();
        var scaleContext = target.GetComponent<VpModelScaleContext>();
        if (scaleContext == null)
            scaleContext = target.AddComponent<VpModelScaleContext>();

        scaleContext.baseScale = baseScale;
        target.transform.localScale = baseScale;
    }

    private void EnsureModelColliders(GameObject target)
    {
        if (!addModelColliders || target == null)
            return;

        foreach (var filter in target.GetComponentsInChildren<MeshFilter>(true))
        {
            if (filter == null || filter.sharedMesh == null)
                continue;

            if (filter.GetComponent<Collider>() != null)
                continue;

            var collider = filter.gameObject.AddComponent<MeshCollider>();
            collider.sharedMesh = filter.sharedMesh;
        }
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

    private byte ExtractTerrainRotation(object cell)
    {
        if (cell == null) return 0;

        var type = cell.GetType();
        var rotProp = type.GetProperty("Rotation") ?? type.GetProperty("TextureRotation");
        if (rotProp != null)
        {
            try
            {
                var val = rotProp.GetValue(cell);
                if (val is byte b) return b;
                if (val is int i) return (byte)i;
                if (val is short s) return (byte)s;
                if (val is sbyte sb) return (byte)sb;
                if (val is IConvertible conv)
                    return (byte)conv.ToInt32(System.Globalization.CultureInfo.InvariantCulture);
            }
            catch { }
        }

        return 0;
    }

    private void RotateUvQuarter(ref Vector2 uv0, ref Vector2 uv1, ref Vector2 uv2, ref Vector2 uv3, byte rotation)
    {
        int r = ((-rotation) % 4 + 4) % 4;
        if (r == 0) return;

        for (int i = 0; i < r; i++)
        {
            uv0 = new Vector2(uv0.y, 1f - uv0.x);
            uv1 = new Vector2(uv1.y, 1f - uv1.x);
            uv2 = new Vector2(uv2.y, 1f - uv2.x);
            uv3 = new Vector2(uv3.y, 1f - uv3.x);
        }
    }

    private Material GetTerrainMaterial(ushort textureId)
    {
        if (terrainMaterialCache.TryGetValue(textureId, out var cached) && cached != null)
            return cached;

        var mat = terrainMaterialTemplate != null
            ? new Material(terrainMaterialTemplate)
            : new Material(Shader.Find("Standard"));

        mat.name = $"Terrain_{textureId}";
        terrainMaterialCache[textureId] = mat;

        if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 0.0f);
        if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.0f);

        if (!terrainDownloadsInFlight.Contains(textureId))
            StartCoroutine(DownloadTerrainTexture(textureId, mat));

        return mat;
    }

    private IEnumerator DownloadTerrainTexture(ushort textureId, Material target)
    {
        if (target == null)
            yield break;

        string basePath = string.IsNullOrWhiteSpace(objectPath) ? null : objectPath.TrimEnd('/') + "/";
        if (string.IsNullOrEmpty(basePath))
            yield break;

        terrainDownloadsInFlight.Add(textureId);

        string[] exts = { "jpg", "png" };
        Texture2D texFound = null;
        foreach (var ext in exts)
        {
            string url = $"{basePath}textures/terrain{textureId}.{ext}";
            using (var req = UnityWebRequestTexture.GetTexture(url))
            {
                yield return req.SendWebRequest();
#if UNITY_2020_1_OR_NEWER
                bool hasError = req.result != UnityWebRequest.Result.Success;
#else
                bool hasError = req.isNetworkError || req.isHttpError;
#endif
                if (!hasError)
                {
                    texFound = DownloadHandlerTexture.GetContent(req);
                    break;
                }
            }
        }

        if (texFound != null)
        {
            texFound.wrapMode = TextureWrapMode.Repeat;
            target.mainTexture = texFound;
            if (target.HasProperty("_BaseMap"))
                target.SetTexture("_BaseMap", texFound);
        }
        else
        {
            Debug.LogWarning($"[VP] Failed to download terrain texture {textureId} (.jpg/.png)");
        }

        terrainDownloadsInFlight.Remove(textureId);
    }

    private void OnGUI()
    {
        if (!showDebugOverlay) return;

        GUI.Label(new Rect(10, 10, 1600, 22), $"Cam: {debugCamPos}  Cell: ({debugCamCell.cx},{debugCamCell.cy})");
        GUI.Label(new Rect(10, 32, 1600, 22), $"LoadedCells={loadedCells.Count} QueuedCells={queuedCells.Count} QueryingCells={queryingCells.Count}");
        GUI.Label(new Rect(10, 54, 1600, 22), $"Pending={(enable5x5Batching ? batchHeap.Count : modelHeap.Count)} InFlightModels={inFlightModelLoads} ActionQueue={actionQueue.Count}");
        GUI.Label(new Rect(10, 76, 1600, 22), $"BudgetMs={modelWorkBudgetMs} SliceActions={sliceActionApplication} ReprioCooldown={reprioritizeCooldownSeconds}s PeriodicReprio={periodicReprioritizeSeconds}s");
        GUI.Label(new Rect(10, 98, 1600, 22), $"unityUnitsPerVpUnit={unityUnitsPerVpUnit} vpUnitsPerUnityUnit={GetVpUnitsPerUnityUnit()} vpUnitsPerCell={VpCellSizeVpUnits} Frustum={prioritizeFrustum} NearBoostR={nearBoostRadius}");
        GUI.Label(new Rect(10, 120, 1600, 22), $"Batching={enable5x5Batching} RegionSize={batchRegionSizeCells} MaxBatchPerFrame={maxBatchInstancesPerFrame}");
        GUI.Label(new Rect(10, 142, 1600, 22), $"Templates={modelTemplateCache.Count} PoolModels={modelPools.Count} Pooling={enablePooling} TemplateClones={useTemplateClones}");
        if (streamTerrain)
            GUI.Label(new Rect(10, 164, 1600, 22), $"Terrain Loaded={loadedTerrainTiles.Count} Queued={queuedTerrainTiles.Count} Querying={queryingTerrainTiles.Count}");
    }

    private void OnDestroy()
    {
        if (desiredCellBuffer.IsCreated)
            desiredCellBuffer.Dispose();
    }

    // -------------------------
    // Simple min-heap priority queue
    // -------------------------
    private sealed class MinHeap<T>
    {
        private struct Node { public float pri; public T item; }
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
            if (heap.Count == 0) throw new InvalidOperationException("Heap empty");

            T min = heap[0].item;
            int last = heap.Count - 1;
            heap[0] = heap[last];
            heap.RemoveAt(last);
            if (heap.Count > 0) SiftDown(0);
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
