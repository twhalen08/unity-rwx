using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Unity.Burst;
using Unity.Collections;
using UnityEngine;
using Unity.Jobs;
using Unity.Mathematics;
using VpNet;
using RWXLoader;
using UnityEngine.Networking;

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
    public float vpUnitsPerUnityUnit = 0.5f;

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

    [Header("Terrain")]
    [Tooltip("Load and render VP terrain tiles around the camera.")]
    public bool streamTerrain = true;

    [Tooltip("How many VP terrain cells make up one terrain tile (default VP tile = 32).")]
    public int terrainTileCellSpan = 32;

    [Tooltip("How many VP terrain cells make up one terrain node (default VP node = 8).")]
    public int terrainNodeCellSpan = 8;

    [Tooltip("Add a MeshCollider to each generated terrain tile.")]
    public bool addTerrainColliders = true;

    [Tooltip("Optional material template for terrain. If null a Standard material is created.")]
    public Material terrainMaterialTemplate;

    [Tooltip("Vertical offset applied to all generated terrain vertices (negative to lower).")]
    public float terrainHeightOffset = -0.01f;

    [Tooltip("Maximum terrain tile queries in-flight at once.")]
    public int maxConcurrentTerrainQueries = 1;

    [Tooltip("If true, skip building terrain tiles whose root was unloaded before completion.")]
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

    private struct DesiredCellData
    {
        public int cx;
        public int cy;
        public int chebyshev;
        public int manhattan;
    }

    private struct DesiredCellJob : IJobParallelFor
    {
        [ReadOnly]
        public int centerX;

        [ReadOnly]
        public int centerY;

        [ReadOnly]
        public int radius;

        [WriteOnly]
        public NativeArray<DesiredCellData> results;

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
        public int groupVersion;
    }

    private struct SpawnedModelInstance
    {
        public (int cx, int cy) cell;
        public GameObject instance;
        public List<VpActionCommand> createActions;
        public List<VpActionCommand> activateActions;
    }

    // Model priority heap: smallest priority loads first
    private readonly MinHeap<PendingModelLoad> modelHeap = new MinHeap<PendingModelLoad>();
    private readonly Dictionary<string, List<PendingModelLoad>> pendingModelGroups = new Dictionary<string, List<PendingModelLoad>>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> pendingModelGroupVersions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> pendingModelGroupBestPriority = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

    private int inFlightCellQueries = 0;
    private int inFlightModelLoads = 0;
    private int inFlightTerrainQueries = 0;

    private (int cx, int cy) lastCameraCell = (int.MinValue, int.MinValue);
    private (int cx, int cy) lastTerrainCameraCell = (int.MinValue, int.MinValue);

    private float nextReprioritizeTime = 0f;

    // Debug
    private (int cx, int cy) debugCamCell;
    private UnityEngine.Vector3 debugCamPos;

    private readonly List<VpPreprocessedAction> preprocessedActionResults = new();
    private readonly List<VpPreprocessActionInput> preprocessedActionInputs = new();
    private readonly List<ActionApplyStep> actionApplySteps = new();

    private void Start()
    {
        if (targetCamera == null) targetCamera = Camera.main;

        SetupModelLoader();

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
        if (streamTerrain)
            StartCoroutine(TerrainStreamingLoop());
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

                BuildDesiredCellsWithJob(camCell.cx, camCell.cy, loadRadius);

                // Unload far cells if enabled
                if (unloadRadius >= 0)
                    UnloadFarCells(camCell.cx, camCell.cy, unloadRadius);

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

                if (!pendingModelGroups.TryGetValue(req.modelName, out var pendingInstances) ||
                    pendingInstances == null ||
                    pendingInstances.Count == 0)
                {
                    pendingModelGroupBestPriority.Remove(req.modelName);
                    pendingModelGroupVersions.Remove(req.modelName);
                    pendingModelGroups.Remove(req.modelName);
                    continue;
                }

                if (!pendingModelGroupVersions.TryGetValue(req.modelName, out var groupVersion) ||
                    req.groupVersion != groupVersion)
                {
                    continue;
                }

                if (dropModelsFromUnloadedCells)
                {
                    bool hasValidInstance = false;
                    for (int i = 0; i < pendingInstances.Count; i++)
                    {
                        if (cellRoots.TryGetValue(pendingInstances[i].cell, out var cellRoot) && cellRoot != null)
                        {
                            hasValidInstance = true;
                            break;
                        }
                    }

                    if (!hasValidInstance)
                    {
                        pendingInstances.Clear();
                        pendingModelGroupBestPriority.Remove(req.modelName);
                        pendingModelGroupVersions.Remove(req.modelName);
                        pendingModelGroups.Remove(req.modelName);
                        continue;
                    }
                }

                inFlightModelLoads++;
                startedThisFrame++;
                StartCoroutine(LoadOneModelBudgeted(req.modelName, pendingInstances));
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
                if (next.tx == int.MinValue)
                    break;

                queuedTerrainTiles.Remove(next);
                queryingTerrainTiles.Add(next);
                inFlightTerrainQueries++;

                StartCoroutine(QueryTerrainTile(next.tx, next.tz));
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
                action = obj.Action,
                groupVersion = 0
            };

            if (!pendingModelGroups.TryGetValue(modelName, out var group))
            {
                group = new List<PendingModelLoad>();
                pendingModelGroups[modelName] = group;
                pendingModelGroupVersions[modelName] = 0;
            }

            group.Add(req);

            float pri = ComputeModelPriority(pos, camPos);
            if (!pendingModelGroupBestPriority.TryGetValue(modelName, out var bestPri) || pri < bestPri)
            {
                int nextVersion = pendingModelGroupVersions[modelName] + 1;
                pendingModelGroupVersions[modelName] = nextVersion;
                pendingModelGroupBestPriority[modelName] = pri;
                req.groupVersion = nextVersion;
                modelHeap.Push(req, pri);
            }
            else if (group.Count == 1)
            {
                pendingModelGroupBestPriority[modelName] = pri;
                modelHeap.Push(req, pri);
            }
        }
    }

    // -------------------------
    // Terrain query + build
    // -------------------------
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
            // Tile was unloaded while we were fetching; drop it silently.
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
        if (cellSizeUnity <= 0f)
            cellSizeUnity = 1f;

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

                // Prefer deterministic owner first, then the three adjacent corner cells
                float hExact;
                if (TryGetCellHeight(ownerCX, ownerCZ, out hExact) ||
                    TryGetCellHeight(ownerCX - 1, ownerCZ, out hExact) ||
                    TryGetCellHeight(ownerCX, ownerCZ - 1, out hExact) ||
                    TryGetCellHeight(ownerCX - 1, ownerCZ - 1, out hExact))
                {
                    heightGrid[vx, vz] = hExact;
                    continue;
                }

                // Deterministic nearest-neighbor fallback (search expanding radius)
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

                // If still not found, stick with 0 to avoid mismatched averages
                heightGrid[vx, vz] = found ? foundH : 0f;
            }
        }

        float unityUnitsPerVpUnit = GetUnityUnitsPerVpUnit();

        // Smooth normals from height grid (shared heights, separate UVs)
        var normalsGrid = new UnityEngine.Vector3[tileSpan + 1, tileSpan + 1];
        for (int vx = 0; vx <= tileSpan; vx++)
        {
            for (int vz = 0; vz <= tileSpan; vz++)
            {
                float hC = heightGrid[vx, vz] * unityUnitsPerVpUnit;
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
        var uvs = new List<UnityEngine.Vector2>(tileSpan * tileSpan * 4);
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

                // VP terrain textures are flipped vertically relative to Unity by default
                UnityEngine.Vector2 uv0 = new UnityEngine.Vector2(0f, 1f);
                UnityEngine.Vector2 uv1 = new UnityEngine.Vector2(1f, 1f);
                UnityEngine.Vector2 uv2 = new UnityEngine.Vector2(0f, 0f);
                UnityEngine.Vector2 uv3 = new UnityEngine.Vector2(1f, 0f);

                // Rotate in-place for quarter turns (VP uses 0-3)
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
    // Model load + actions (budgeted)
    // -------------------------
    private IEnumerator LoadOneModelBudgeted(string modelName, List<PendingModelLoad> pendingInstances)
    {
        string modelId = Path.GetFileNameWithoutExtension(modelName);

        bool completed = false;
        GameObject loadedObject = null;
        string errorMessage = null;
        const bool activateOnInstantiate = false;

        modelLoader.parentTransform = null;

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

        inFlightModelLoads--;

        if (loadedObject == null)
        {
            Debug.LogError($"RWX load failed: {modelName} (normalized='{modelId}') → {errorMessage}");
            yield break;
        }

        loadedObject.transform.SetParent(null, false);
        loadedObject.SetActive(false);

        UnityEngine.Vector3 camPos = GetCameraWorldPos();
        pendingInstances.Sort((a, b) =>
            ComputeModelPriority(a.position, camPos).CompareTo(ComputeModelPriority(b.position, camPos)));

        var spawnedInstances = new List<SpawnedModelInstance>();
        int index = 0;
        float spawnStart = Time.realtimeSinceStartup;

        while (index < pendingInstances.Count)
        {
            var req = pendingInstances[index];
            index++;

            Transform instanceParent = null;
            if (dropModelsFromUnloadedCells)
            {
                if (!cellRoots.TryGetValue(req.cell, out var cellRoot) || cellRoot == null)
                    continue;
                instanceParent = cellRoot.transform;
            }
            else
            {
                if (cellRoots.TryGetValue(req.cell, out var cellRoot) && cellRoot != null)
                    instanceParent = cellRoot.transform;
            }

            var instance = Instantiate(loadedObject);
            instance.transform.SetParent(instanceParent, false);
            instance.transform.localPosition = req.position;
            instance.transform.localRotation = req.rotation;
            ApplyModelBaseScale(instance);
            EnsureModelColliders(instance);

            List<VpActionCommand> createActions = null;
            List<VpActionCommand> activateActions = null;

            if (!string.IsNullOrWhiteSpace(req.action))
            {
                VpActionParser.Parse(req.action, out createActions, out activateActions);
            }

            createActions ??= new List<VpActionCommand>();
            activateActions ??= new List<VpActionCommand>();

            spawnedInstances.Add(new SpawnedModelInstance
            {
                cell = req.cell,
                instance = instance,
                createActions = createActions,
                activateActions = activateActions
            });

            float elapsedMs = (Time.realtimeSinceStartup - spawnStart) * 1000f;
            if (elapsedMs >= modelWorkBudgetMs)
            {
                yield return null;
                spawnStart = Time.realtimeSinceStartup;
            }
        }

        if (spawnedInstances.Count == 0)
        {
            Destroy(loadedObject);
            pendingInstances.Clear();
            pendingModelGroupBestPriority.Remove(modelName);
            pendingModelGroupVersions.Remove(modelName);
            pendingModelGroups.Remove(modelName);
            yield break;
        }

        // Give Unity a frame before heavier work
        yield return null;

        for (int i = 0; i < spawnedInstances.Count; i++)
        {
            var spawned = spawnedInstances[i];
            var instance = spawned.instance;

            if (dropModelsFromUnloadedCells)
            {
                if (!cellRoots.TryGetValue(spawned.cell, out var cellRoot) || cellRoot == null)
                {
                    Destroy(instance);
                    continue;
                }
            }

            var createActions = spawned.createActions;
            var activateActions = spawned.activateActions;

            if (createActions.Count > 0 || activateActions.Count > 0)
            {
                if (logCreateActions && createActions.Count > 0)
                    Debug.Log($"[VP Create] {instance.name} will run {createActions.Count} actions");

                if (!sliceActionApplication)
                {
                    foreach (var a in createActions)
                        VpActionExecutor.ExecuteCreate(instance, a, modelLoader.defaultObjectPath, objectPathPassword, this);
                }
                else
                {
                    PreparePreprocessedActions(createActions, out var preprocessInputs, out var preprocessOutputs);

                    if (preprocessInputs.IsCreated && preprocessInputs.Length > 0)
                    {
                        var preprocessJob = new PreprocessActionJob
                        {
                            inputs = preprocessInputs,
                            outputs = preprocessOutputs
                        };

                        int batchSize = math.max(1, preprocessInputs.Length / 4);
                        preprocessJob.Schedule(preprocessInputs.Length, batchSize).Complete();

                        preprocessedActionResults.Clear();
                        for (int j = 0; j < preprocessOutputs.Length; j++)
                            preprocessedActionResults.Add(preprocessOutputs[j]);
                    }

                    if (preprocessInputs.IsCreated) preprocessInputs.Dispose();
                    if (preprocessOutputs.IsCreated) preprocessOutputs.Dispose();

                    float start = Time.realtimeSinceStartup;

                    for (int j = 0; j < actionApplySteps.Count; j++)
                    {
                        var step = actionApplySteps[j];

                        if (step.isPreprocessed)
                        {
                            if (step.preprocessedIndex >= 0 && step.preprocessedIndex < preprocessedActionResults.Count)
                                ApplyPreprocessedCreateAction(instance, preprocessedActionResults[step.preprocessedIndex]);
                        }
                        else if (step.command != null)
                        {
                            VpActionExecutor.ExecuteCreate(instance, step.command, modelLoader.defaultObjectPath, objectPathPassword, this);
                        }

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
                    var act = instance.GetComponent<VpActivateActions>();
                    if (act == null) act = instance.AddComponent<VpActivateActions>();
                    act.actions.AddRange(activateActions);

                    if (logActivateActions)
                        Debug.Log($"[VP Activate] {instance.name} stored {activateActions.Count} actions");
                }
            }

            if (instance != null && !instance.activeSelf)
            {
                instance.SetActive(true);
            }
        }

        Destroy(loadedObject);
        pendingInstances.Clear();
        pendingModelGroupBestPriority.Remove(modelName);
        pendingModelGroupVersions.Remove(modelName);
        pendingModelGroups.Remove(modelName);
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

        modelHeap.Clear();
        pendingModelGroupBestPriority.Clear();

        foreach (var kvp in pendingModelGroups)
        {
            var group = kvp.Value;
            if (group == null || group.Count == 0)
                continue;

            float bestPri = float.MaxValue;
            int bestIndex = -1;

            for (int i = 0; i < group.Count; i++)
            {
                float pri = ComputeModelPriority(group[i].position, camPos);
                if (pri < bestPri)
                {
                    bestPri = pri;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0)
                continue;

            int nextVersion = pendingModelGroupVersions.TryGetValue(kvp.Key, out var version)
                ? version + 1
                : 1;

            pendingModelGroupVersions[kvp.Key] = nextVersion;
            pendingModelGroupBestPriority[kvp.Key] = bestPri;

            var req = group[bestIndex];
            req.groupVersion = nextVersion;
            modelHeap.Push(req, bestPri);
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

        float vpUnitsPerUnity = GetClampedVpUnitsPerUnityUnit();
        float vpX = (-u.x) * vpUnitsPerUnity;
        float vpZ = (u.z) * vpUnitsPerUnity;

        float cellSize = Mathf.Max(1f, vpUnitsPerCell);

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
                Destroy(root);

            cellRoots.Remove(c);

            if (logCellLoads) Debug.Log($"[VP] Unloaded cell ({c.cx},{c.cy})");
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

            // Remove cached heights for this tile
            int tileSpan = Mathf.Max(1, terrainTileCellSpan);
            for (int cz = 0; cz < tileSpan; cz++)
            {
                for (int cx = 0; cx < tileSpan; cx++)
                {
                    terrainCellCache.Remove((tile.tx * tileSpan + cx, tile.tz * tileSpan + cz));
                }
            }

            if (terrainTiles.TryGetValue(tile, out var go) && go != null)
                Destroy(go);

            terrainTiles.Remove(tile);
        }
    }

    private struct ActionApplyStep
    {
        public bool isPreprocessed;
        public int preprocessedIndex;
        public VpActionCommand command;
    }

    private void PreparePreprocessedActions(
        List<VpActionCommand> createActions,
        out NativeArray<VpPreprocessActionInput> inputs,
        out NativeArray<VpPreprocessedAction> outputs)
    {
        inputs = default;
        outputs = default;

        preprocessedActionInputs.Clear();
        preprocessedActionResults.Clear();
        actionApplySteps.Clear();

        if (createActions == null || createActions.Count == 0)
            return;

        for (int i = 0; i < createActions.Count; i++)
        {
            var cmd = createActions[i];
            if (TryBuildPreprocessInput(cmd, out var input))
            {
                actionApplySteps.Add(new ActionApplyStep
                {
                    isPreprocessed = true,
                    preprocessedIndex = preprocessedActionInputs.Count,
                    command = null
                });
                preprocessedActionInputs.Add(input);
            }
            else
            {
                actionApplySteps.Add(new ActionApplyStep
                {
                    isPreprocessed = false,
                    preprocessedIndex = -1,
                    command = cmd
                });
            }
        }

        if (preprocessedActionInputs.Count == 0)
            return;

        inputs = new NativeArray<VpPreprocessActionInput>(preprocessedActionInputs.Count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
        outputs = new NativeArray<VpPreprocessedAction>(preprocessedActionInputs.Count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

        for (int i = 0; i < preprocessedActionInputs.Count; i++)
            inputs[i] = preprocessedActionInputs[i];
    }

    private bool TryBuildPreprocessInput(VpActionCommand cmd, out VpPreprocessActionInput input)
    {
        input = default;

        if (cmd == null || string.IsNullOrWhiteSpace(cmd.verb))
            return false;

        string verb = cmd.verb.Trim().ToLowerInvariant();

        switch (verb)
        {
            case "ambient":
                if (cmd.positional == null || cmd.positional.Count == 0)
                    return false;
                input.type = VpPreprocessedActionType.Ambient;
                input.input0 = new float4(ParseFloat(cmd.positional[0], 1f), 0f, 0f, 0f);
                return true;

            case "diffuse":
                if (cmd.positional == null || cmd.positional.Count == 0)
                    return false;
                input.type = VpPreprocessedActionType.Diffuse;
                input.input0 = new float4(ParseFloat(cmd.positional[0], 1f), 0f, 0f, 0f);
                return true;

            case "visible":
                if (cmd.positional == null || cmd.positional.Count == 0)
                    return false;
                input.type = VpPreprocessedActionType.Visible;
                input.flags = ParseBool(cmd.positional[0]) ? 1 : 0;
                return true;

            case "scale":
                if (cmd.positional == null || cmd.positional.Count == 0)
                    return false;
                input.type = VpPreprocessedActionType.Scale;
                BuildScaleInputs(cmd, ref input);
                return true;

            case "shear":
                if (cmd.positional == null || cmd.positional.Count == 0)
                    return false;
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

    private float GetClampedVpUnitsPerUnityUnit()
    {
        return Mathf.Max(0.0001f, vpUnitsPerUnityUnit);
    }

    private float GetUnityUnitsPerVpUnit()
    {
        return 1f / GetClampedVpUnitsPerUnityUnit();
    }

    private float GetUnityUnitsPerVpCell()
    {
        return vpUnitsPerCell * GetUnityUnitsPerVpUnit();
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

    private void RotateUvQuarter(ref UnityEngine.Vector2 uv0, ref UnityEngine.Vector2 uv1, ref UnityEngine.Vector2 uv2, ref UnityEngine.Vector2 uv3, byte rotation)
    {
        // VP rotation increases clockwise; we flipped the UVs vertically earlier,
        // so reverse the rotation direction here to stay aligned with VP (0-3).
        int r = ((-rotation) % 4 + 4) % 4;
        if (r == 0) return;

        for (int i = 0; i < r; i++)
        {
            // 90° clockwise rotation: (u,v) -> (v, 1-u)
            uv0 = new UnityEngine.Vector2(uv0.y, 1f - uv0.x);
            uv1 = new UnityEngine.Vector2(uv1.y, 1f - uv1.x);
            uv2 = new UnityEngine.Vector2(uv2.y, 1f - uv2.x);
            uv3 = new UnityEngine.Vector2(uv3.y, 1f - uv3.x);
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

        // Force smoothness down to match VP terrain visuals
        if (mat.HasProperty("_Glossiness"))
            mat.SetFloat("_Glossiness", 0.0f);
        if (mat.HasProperty("_Smoothness"))
            mat.SetFloat("_Smoothness", 0.0f);

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
        GUI.Label(new Rect(10, 54, 1600, 22), $"ModelPending={modelHeap.Count} InFlightModels={inFlightModelLoads}");
        GUI.Label(new Rect(10, 76, 1600, 22), $"BudgetMs={modelWorkBudgetMs} SliceActions={sliceActionApplication} ReprioCooldown={reprioritizeCooldownSeconds}s");
        GUI.Label(new Rect(10, 98, 1600, 22), $"vpUnitsPerUnityUnit={vpUnitsPerUnityUnit} unityUnitsPerVpUnit={GetUnityUnitsPerVpUnit()} vpUnitsPerCell={vpUnitsPerCell} Frustum={prioritizeFrustum}");
        if (streamTerrain)
            GUI.Label(new Rect(10, 120, 1600, 22), $"Terrain Loaded={loadedTerrainTiles.Count} Queued={queuedTerrainTiles.Count} Querying={queryingTerrainTiles.Count}");
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
