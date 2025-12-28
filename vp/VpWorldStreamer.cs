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

    [Header("LOD & Instancing")]
    [Tooltip("Cells within this radius instantiate full GameObjects.")]
    public int fullDetailCellRadius = 1;

    [Tooltip("Cells beyond full detail but within this radius render as GPU-instanced batches.")]
    public int instancedCellRadius = 3;

    [Tooltip("Cells beyond instancing but still loaded keep lightweight proxies/colliders for interaction.")]
    public int proxyCellRadius = 4;

    [Tooltip("Enable GPU instancing for mid-range objects.")]
    public bool enableGpuInstancing = true;

    [Tooltip("Optional override mesh for proxy renderers; if null, a box collider is used without rendering.")]
    public Mesh proxyMesh;

    [Tooltip("Optional material for proxy renderers when a proxy mesh is provided.")]
    public Material proxyMaterial;

    [Tooltip("Uniform scale applied to proxy meshes/colliders.")]
    public float proxySize = 0.5f;

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
    private RWXLoaderAdvanced templateLoader;

    private enum CellLodState
    {
        Full,
        Instanced,
        Proxy
    }

    private sealed class ModelInstance
    {
        public string modelName;
        public UnityEngine.Vector3 position;
        public Quaternion rotation;
        public string action;
    }

    private sealed class RendererTemplate
    {
        public Mesh mesh;
        public Material material;
        public Matrix4x4 localToRoot;
        public int subMeshIndex;
    }

    private sealed class ModelTemplate
    {
        public List<RendererTemplate> renderers = new();
        public UnityEngine.Vector3 baseScale = UnityEngine.Vector3.one;
    }

    private sealed class InstancedBatch
    {
        public Mesh mesh;
        public Material material;
        public int subMeshIndex;
        public List<Matrix4x4> matrices = new();
    }

    private sealed class CellContent
    {
        public GameObject root;
        public (int cx, int cy) cell;
        public List<ModelInstance> instances = new();
        public List<GameObject> fullObjects = new();
        public Dictionary<(Mesh mesh, Material mat, int subMesh), InstancedBatch> instancedBatches = new();
        public List<GameObject> proxies = new();
        public CellLodState lodState;
    }

    private readonly Dictionary<(int cx, int cy), CellContent> cellContents = new();
    private readonly Dictionary<string, ModelTemplate> templateCache = new();
    private readonly HashSet<string> templatesLoading = new();

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
        public ModelInstance instanceRef;
    }

    // Model priority heap: smallest priority loads first
    private readonly MinHeap<PendingModelLoad> modelHeap = new MinHeap<PendingModelLoad>();

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
        StartCoroutine(LodUpdateLoop());
    }

    private IEnumerator LodUpdateLoop()
    {
        while (true)
        {
            UpdateCellLods();
            yield return null;
        }
    }

    private void UpdateCellLods()
    {
        var camCell = GetCameraCell();
        foreach (var kvp in cellContents)
        {
            var cell = kvp.Key;
            var content = kvp.Value;
            if (content == null || content.root == null)
                continue;

            int dx = Mathf.Abs(cell.cx - camCell.cx);
            int dy = Mathf.Abs(cell.cy - camCell.cy);
            int cheb = Mathf.Max(dx, dy);

            CellLodState target = CellLodState.Proxy;
            if (cheb <= fullDetailCellRadius)
                target = CellLodState.Full;
            else if (cheb <= instancedCellRadius)
                target = CellLodState.Instanced;
            else if (cheb <= proxyCellRadius)
                target = CellLodState.Proxy;
            else
                target = CellLodState.Proxy;

            if (content.lodState == target)
            {
                // Instanced batches still need to be maintained/drawn each frame
                if (target == CellLodState.Instanced)
                {
                    BuildInstancedBatches(content);
                    RenderInstancedBatches(content);
                }
                continue;
            }

            ApplyLod(content, target);
            content.lodState = target;
        }
    }

    private void ApplyLod(CellContent content, CellLodState target)
    {
        CleanupCellContent(content);

        switch (target)
        {
            case CellLodState.Full:
                EnqueueFullLoads(content);
                break;
            case CellLodState.Instanced:
                BuildInstancedBatches(content);
                RenderInstancedBatches(content);
                break;
            case CellLodState.Proxy:
                BuildProxies(content);
                break;
        }
    }

    private void EnqueueFullLoads(CellContent content)
    {
        if (content == null || content.root == null)
            return;

        UnityEngine.Vector3 camPos = GetCameraWorldPos();
        foreach (var inst in content.instances)
        {
            float pri = ComputeModelPriority(inst.position, camPos);
            modelHeap.Push(new PendingModelLoad
            {
                cell = GetCellKey(content),
                modelName = inst.modelName,
                position = inst.position,
                rotation = inst.rotation,
                action = inst.action,
                instanceRef = inst
            }, pri);
        }
    }

    private (int cx, int cy) GetCellKey(CellContent content)
    {
        if (content == null)
            return (0, 0);

        return content.cell;
    }

    private void BuildInstancedBatches(CellContent content)
    {
        if (!enableGpuInstancing || content == null || content.root == null)
            return;

        content.instancedBatches.Clear();

        foreach (var inst in content.instances)
        {
            var template = GetOrLoadTemplate(inst.modelName);
            if (template == null)
                continue;

            foreach (var rt in template.renderers)
            {
                var key = (rt.mesh, rt.material, rt.subMeshIndex);
                if (!content.instancedBatches.TryGetValue(key, out var batch))
                {
                    batch = new InstancedBatch { mesh = rt.mesh, material = rt.material, subMeshIndex = rt.subMeshIndex };
                    content.instancedBatches[key] = batch;
                }

                Matrix4x4 trs = Matrix4x4.TRS(inst.position, inst.rotation, template.baseScale);
                Matrix4x4 final = trs * rt.localToRoot;
                batch.matrices.Add(final);
            }
        }

        RenderInstancedBatches(content);
    }

    private void RenderInstancedBatches(CellContent content)
    {
        if (content == null)
            return;

        foreach (var batch in content.instancedBatches.Values)
        {
            if (batch.mesh == null || batch.material == null || batch.matrices.Count == 0)
                continue;

            int count = batch.matrices.Count;
            int start = 0;
            const int MaxBatch = 1023;
            while (start < count)
            {
                int size = Mathf.Min(MaxBatch, count - start);
                Graphics.DrawMeshInstanced(batch.mesh, batch.subMeshIndex, batch.material, batch.matrices.GetRange(start, size));
                start += size;
            }
        }
    }

    private ModelTemplate GetOrLoadTemplate(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            return null;

        string key = modelName.ToLowerInvariant();
        if (templateCache.TryGetValue(key, out var cached) && cached != null)
            return cached;

        if (templatesLoading.Contains(key))
            return null;

        templatesLoading.Add(key);
        StartCoroutine(LoadTemplateCoroutine(modelName, key));
        return null;
    }

    private IEnumerator LoadTemplateCoroutine(string modelName, string key)
    {
        bool done = false;
        GameObject prefab = null;
        var loader = templateLoader != null ? templateLoader : modelLoader;

        loader.LoadModelFromRemote(
            modelName,
            loader.defaultObjectPath,
            (go, err) =>
            {
                prefab = go;
                done = true;
            },
            objectPathPassword,
            false);

        while (!done)
            yield return null;

        templatesLoading.Remove(key);

        if (prefab == null)
            yield break;

        var template = BuildTemplateFromPrefab(prefab);
        templateCache[key] = template;

        // Cache prefab disabled instance so instancing stays lightweight
        Destroy(prefab);
    }

    private ModelTemplate BuildTemplateFromPrefab(GameObject prefab)
    {
        var template = new ModelTemplate();
        var scaleContext = prefab.GetComponent<VpModelScaleContext>();
        template.baseScale = scaleContext != null ? scaleContext.baseScale : GetBaseScaleVector();

        foreach (var mr in prefab.GetComponentsInChildren<MeshRenderer>(true))
        {
            var mf = mr.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null || mr.sharedMaterials == null)
                continue;

            for (int i = 0; i < mr.sharedMaterials.Length; i++)
            {
                var mat = mr.sharedMaterials[i];
                if (mat == null)
                    continue;

                template.renderers.Add(new RendererTemplate
                {
                    mesh = mf.sharedMesh,
                    material = mat,
                    localToRoot = Matrix4x4.TRS(mr.transform.localPosition, mr.transform.localRotation, mr.transform.localScale),
                    subMeshIndex = i
                });
            }
        }

        return template;
    }

    private void BuildProxies(CellContent content)
    {
        if (content == null || content.root == null)
            return;

        foreach (var inst in content.instances)
        {
            var proxy = new GameObject($"Proxy_{inst.modelName}");
            proxy.transform.SetParent(content.root.transform, false);
            proxy.transform.localPosition = inst.position;
            proxy.transform.localRotation = inst.rotation;

            Vector3 size = Vector3.one * proxySize;
            var collider = proxy.AddComponent<BoxCollider>();
            collider.size = size;

            if (proxyMesh != null && proxyMaterial != null)
            {
                var mf = proxy.AddComponent<MeshFilter>();
                mf.sharedMesh = proxyMesh;
                var mr = proxy.AddComponent<MeshRenderer>();
                mr.sharedMaterial = proxyMaterial;
                proxy.transform.localScale = size;
            }
            else
            {
                proxy.transform.localScale = size;
            }

            content.proxies.Add(proxy);
        }
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

        if (templateLoader == null)
        {
            var templateGO = new GameObject("RWX Template Loader");
            templateLoader = templateGO.AddComponent<RWXLoaderAdvanced>();
        }

        templateLoader.defaultObjectPath = modelLoader.defaultObjectPath;
        templateLoader.objectPathPassword = modelLoader.objectPathPassword;
        templateLoader.enableDebugLogs = false;
        templateLoader.parentTransform = null;

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

                if (dropModelsFromUnloadedCells)
                {
                    if (!cellRoots.TryGetValue(req.cell, out var cellRoot) || cellRoot == null)
                        continue;

                    if (cellContents.TryGetValue(req.cell, out var content) && content.lodState != CellLodState.Full)
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

                    if (cellContents.TryGetValue(req.cell, out var content) && content.lodState != CellLodState.Full)
                        parent = null;

                    inFlightModelLoads++;
                    startedThisFrame++;
                    StartCoroutine(LoadOneModelBudgeted(req, parent));
                }
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

        // Create / reuse cell root + content record
        if (!cellRoots.TryGetValue(key, out var root) || root == null)
        {
            root = new GameObject($"VP_Cell_{cellX}_{cellY}");
            cellRoots[key] = root;
        }

        if (!cellContents.TryGetValue(key, out var content) || content == null)
        {
            content = new CellContent { root = root, lodState = CellLodState.Full, cell = key };
            cellContents[key] = content;
        }
        else
        {
            content.root = root;
            content.cell = key;
        }

        content.instances.Clear();
        content.instancedBatches.Clear();
        content.proxies.Clear();
        content.fullObjects.Clear();

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

            content.instances.Add(new ModelInstance
            {
                modelName = modelName,
                position = pos,
                rotation = rot,
                action = obj.Action
            });

            float pri = ComputeModelPriority(pos, camPos);
            var instance = content.instances[content.instances.Count - 1];

            modelHeap.Push(new PendingModelLoad
            {
                cell = key,
                modelName = modelName,
                position = pos,
                rotation = rot,
                action = obj.Action,
                instanceRef = instance
            }, pri);
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
    private IEnumerator LoadOneModelBudgeted(PendingModelLoad req, Transform parent)
    {
        string modelId = Path.GetFileNameWithoutExtension(req.modelName);

        bool completed = false;
        GameObject loadedObject = null;
        string errorMessage = null;
        List<VpActionCommand> createActions = null;
        List<VpActionCommand> activateActions = null;

        if (!string.IsNullOrWhiteSpace(req.action))
        {
            VpActionParser.Parse(req.action, out createActions, out activateActions);
        }

        createActions ??= new List<VpActionCommand>();
        activateActions ??= new List<VpActionCommand>();

        bool activateOnInstantiate = createActions.Count == 0;

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

        inFlightModelLoads--;

        if (loadedObject == null)
        {
            Debug.LogError($"RWX load failed: {req.modelName} (normalized='{modelId}') → {errorMessage}");
            yield break;
        }

        // Phase 1: cheap transform setup
        loadedObject.transform.localPosition = req.position;
        loadedObject.transform.localRotation = req.rotation;
        ApplyModelBaseScale(loadedObject);
        EnsureModelColliders(loadedObject);

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
        if (createActions.Count > 0 || activateActions.Count > 0)
        {
            if (logCreateActions && createActions.Count > 0)
                Debug.Log($"[VP Create] {loadedObject.name} will run {createActions.Count} actions");

            if (!sliceActionApplication)
            {
                foreach (var a in createActions)
                    VpActionExecutor.ExecuteCreate(loadedObject, a, modelLoader.defaultObjectPath, objectPathPassword, this);
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
                    for (int i = 0; i < preprocessOutputs.Length; i++)
                        preprocessedActionResults.Add(preprocessOutputs[i]);
                }

                if (preprocessInputs.IsCreated) preprocessInputs.Dispose();
                if (preprocessOutputs.IsCreated) preprocessOutputs.Dispose();

                float start = Time.realtimeSinceStartup;

                for (int i = 0; i < actionApplySteps.Count; i++)
                {
                    var step = actionApplySteps[i];

                    if (step.isPreprocessed)
                    {
                        if (step.preprocessedIndex >= 0 && step.preprocessedIndex < preprocessedActionResults.Count)
                            ApplyPreprocessedCreateAction(loadedObject, preprocessedActionResults[step.preprocessedIndex]);
                    }
                    else if (step.command != null)
                    {
                        VpActionExecutor.ExecuteCreate(loadedObject, step.command, modelLoader.defaultObjectPath, objectPathPassword, this);
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
                var act = loadedObject.GetComponent<VpActivateActions>();
                if (act == null) act = loadedObject.AddComponent<VpActivateActions>();
                act.actions.AddRange(activateActions);

                if (logActivateActions)
                    Debug.Log($"[VP Activate] {loadedObject.name} stored {activateActions.Count} actions");
            }
        }

        if (!activateOnInstantiate && loadedObject != null && !loadedObject.activeSelf)
        {
            loadedObject.SetActive(true);
        }

        // Track as a fully instantiated object
        if (cellContents.TryGetValue(req.cell, out var content) && content != null)
            content.fullObjects.Add(loadedObject);
    }

    // -------------------------
    // Priority computation
    // -------------------------
    private float ComputeModelPriority(UnityEngine.Vector3 objPos, UnityEngine.Vector3 camPos)
    {
        float pri = (objPos - camPos).sqrMagnitude;

        // Encourage loading near objects sooner when LODs are tighter
        float cellDistance = GetCellDistanceFromCamera(objPos);
        if (cellDistance <= fullDetailCellRadius)
            pri *= 0.5f;
        else if (cellDistance <= instancedCellRadius)
            pri *= 0.75f;

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
            if (cellContents.TryGetValue(c, out var content) && content != null)
            {
                CleanupCellContent(content);
                cellContents.Remove(c);
            }

            if (logCellLoads) Debug.Log($"[VP] Unloaded cell ({c.cx},{c.cy})");
        }
    }

    private void CleanupCellContent(CellContent content)
    {
        if (content == null)
            return;

        foreach (var go in content.fullObjects)
            if (go != null) Destroy(go);

        foreach (var proxy in content.proxies)
            if (proxy != null) Destroy(proxy);

        content.fullObjects.Clear();
        content.proxies.Clear();
        content.instancedBatches.Clear();
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

    private float GetCellDistanceFromCamera(UnityEngine.Vector3 worldPos)
    {
        float unityUnitsPerCell = GetUnityUnitsPerVpCell();
        float distance = (worldPos - GetCameraWorldPos()).magnitude;
        if (unityUnitsPerCell <= 0.0001f)
            return 0f;
        return distance / unityUnitsPerCell;
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
