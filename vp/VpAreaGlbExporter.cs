using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using RWXLoader;
using UnityEngine;
using VpNet;

/// <summary>
/// Utility MonoBehaviour that exports a square VP area to GLB.
/// </summary>
public class VpAreaGlbExporter : MonoBehaviour
{
    [Header("VP Login")]
    public string userName = "Tom";
    public string botName = "Unity";
    public string applicationName = "Unity";
    public string worldName = "VP-Build";

    [Header("Area (VP cells)")]
    public int centerCellX = 0;
    public int centerCellZ = 0;
    public int radiusCells = 1;
    public bool includeModels = true;
    public bool includeTerrain = true;
    public string outputPath = "vp-area.glb";

    [Header("VP Units")]
    public float vpUnitsPerCell = 2000f;
    public float vpUnitsPerUnityUnit = 0.5f;

    [Header("Object Server")]
    public string objectPath = "http://objects.virtualparadise.org/vpbuild/";
    public string objectPathPassword = "";

    [Header("Model Loader")]
    public RWXLoaderAdvanced modelLoader;
    public bool addModelColliders = false;

    [Header("Terrain")]
    public int terrainTileCellSpan = 32;
    public int terrainNodeCellSpan = 8;
    public float terrainHeightOffset = -0.01f;
    public Material terrainMaterialTemplate;
    public bool addTerrainColliders = false;
    private readonly Dictionary<(int tx, int tz), TerrainNode[]> terrainTileNodes = new();

    [Header("Logging")]
    public bool logProgress = true;
    public bool autoStartOnPlay = true;

    private VirtualParadiseClient vpClient;
    private VpTerrainBuilder terrainBuilder;
    private GameObject exportRoot;
    private GameObject modelsRoot;
    private GameObject terrainRoot;
    private bool exporting;

    public event Action<string> OnLog;

    [ContextMenu("Export GLB")]
    public void StartExport()
    {
        _ = ExportAsync();
    }

    private void Start()
    {
        if (autoStartOnPlay)
        {
            Log($"[VP Export] Auto-starting export. Center=({centerCellX},{centerCellZ}) Radius={radiusCells} IncludeModels={includeModels} IncludeTerrain={includeTerrain}");
            _ = ExportAsync();
        }
    }

    public async Task ExportAsync()
    {
        if (exporting)
        {
            Log("[VP Export] Export already in progress.");
            return;
        }

        exporting = true;

        try
        {
            Log("[VP Export] Preparing export pipeline...");
            SetupModelLoader();
            SetupTerrainBuilder();

            await ConnectAndLogin();

            exportRoot = new GameObject("VP Export Root");
            modelsRoot = new GameObject("Models");
            modelsRoot.transform.SetParent(exportRoot.transform, false);
            terrainRoot = new GameObject("Terrain");
            terrainRoot.transform.SetParent(exportRoot.transform, false);

            var tasks = new List<Task>();
            if (includeModels) tasks.Add(LoadCellsAndModels());
            if (includeTerrain) tasks.Add(LoadTerrainTiles());

            await Task.WhenAll(tasks);

            bool exported = TryExportGlb(exportRoot, outputPath);
            Log(exported
                ? $"[VP Export] GLB written to '{outputPath}'."
                : "[VP Export] Failed to export GLB. Ensure a GLTF exporter package (UnityGLTF/Siccity) is present.");
        }
        catch (Exception ex)
        {
            Log($"[VP Export] Error: {ex.Message}");
        }
        finally
        {
            exporting = false;
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

        Log($"[VP Export] Connecting to world '{worldName}' as {userName}...");
        await vpClient.LoginAndEnterAsync("", true);
        Log($"[VP Export] Connected & entered '{worldName}' as {userName}");
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

    private void SetupTerrainBuilder()
    {
        if (includeTerrain && terrainMaterialTemplate == null)
        {
            terrainMaterialTemplate = new Material(Shader.Find("Standard"))
            {
                name = "VP Terrain Material"
            };
        }

        terrainBuilder = new VpTerrainBuilder(
            GetUnityUnitsPerVpCell,
            GetUnityUnitsPerVpUnit,
            StartCoroutine,
            Shader.Find,
            Debug.LogWarning,
            Log)
        {
            TerrainTileCellSpan = terrainTileCellSpan,
            TerrainNodeCellSpan = terrainNodeCellSpan,
            TerrainHeightOffset = terrainHeightOffset,
            TerrainMaterialTemplate = terrainMaterialTemplate,
            ObjectPath = objectPath,
            ObjectPathPassword = objectPathPassword
        };
    }

    private async Task LoadCellsAndModels()
    {
        for (int cz = centerCellZ - radiusCells; cz <= centerCellZ + radiusCells; cz++)
        {
            for (int cx = centerCellX - radiusCells; cx <= centerCellX + radiusCells; cx++)
            {
                try
                {
                    Log($"[VP Export] Loading cell ({cx},{cz})...");
                    var cell = await vpClient.QueryCellAsync(cx, cz);
                    var cellRoot = new GameObject($"Cell_{cx}_{cz}");
                    cellRoot.transform.SetParent(modelsRoot.transform, false);

                    foreach (var obj in cell.Objects)
                    {
                        if (string.IsNullOrEmpty(obj.Model))
                            continue;

                        await SpawnModelAsync(
                            obj.Model,
                            obj.Action,
                            obj.Position,
                            obj.Rotation,
                            obj.Angle,
                            cellRoot.transform);
                    }

                    Log($"[VP Export] Finished cell ({cx},{cz}) with {cell.Objects.Count} objects.");
                }
                catch (Exception ex)
                {
                    Log($"[VP Export] Failed to load cell ({cx},{cz}): {ex.Message}");
                }
            }
        }
    }

    private async Task SpawnModelAsync(string modelName, string action, VpNet.Vector3 position, VpNet.Vector3 rotation, double angle, Transform parent)
    {
        string normalizedModelName = NormalizeModelName(modelName);
        VpActionParser.Parse(action, out var createActions, out var activateActions);
        bool activateOnInstantiate = createActions.Count == 0;

        var loadTask = new TaskCompletionSource<GameObject>();

        modelLoader.parentTransform = parent;
        modelLoader.LoadModelFromRemote(
            normalizedModelName,
            modelLoader.defaultObjectPath,
            (go, errMsg) =>
            {
                if (go == null)
                {
                    loadTask.TrySetException(new InvalidOperationException(errMsg ?? "Unknown model load failure"));
                }
                else
                {
                    loadTask.TrySetResult(go);
                }
            },
            objectPathPassword,
            activateOnInstantiate);

        var loadedObject = await loadTask.Task;

        loadedObject.transform.localPosition = VPtoUnity(position);
        loadedObject.transform.localRotation = ConvertVpRotationToUnity(rotation, angle, normalizedModelName);
        ApplyModelBaseScale(loadedObject);
        EnsureModelColliders(loadedObject);

        foreach (var a in createActions)
            VpActionExecutor.ExecuteCreate(loadedObject, a, modelLoader.defaultObjectPath, objectPathPassword, this);

        foreach (var a in activateActions)
            VpActionExecutor.ExecuteCreate(loadedObject, a, modelLoader.defaultObjectPath, objectPathPassword, this);

        if (activateActions.Count > 0)
        {
            var act = loadedObject.GetComponent<VpActivateActions>() ?? loadedObject.AddComponent<VpActivateActions>();
            act.actions.AddRange(activateActions);
        }
    }

    private async Task LoadTerrainTiles()
    {
        int tileSpan = Mathf.Max(1, terrainTileCellSpan);
        int minTileX = Mathf.FloorToInt((centerCellX - radiusCells) / (float)tileSpan);
        int maxTileX = Mathf.FloorToInt((centerCellX + radiusCells) / (float)tileSpan);
        int minTileZ = Mathf.FloorToInt((centerCellZ - radiusCells) / (float)tileSpan);
        int maxTileZ = Mathf.FloorToInt((centerCellZ + radiusCells) / (float)tileSpan);

        var tasks = new List<Task>();
        for (int tz = minTileZ; tz <= maxTileZ; tz++)
        {
            for (int tx = minTileX; tx <= maxTileX; tx++)
            {
                tasks.Add(BuildTerrainTileAsync(tx, tz));
            }
        }

        await Task.WhenAll(tasks);
    }

    private async Task BuildTerrainTileAsync(int tileX, int tileZ)
    {
        try
        {
            terrainBuilder.TerrainTileCellSpan = terrainTileCellSpan;
            terrainBuilder.TerrainNodeCellSpan = terrainNodeCellSpan;
            terrainBuilder.TerrainHeightOffset = terrainHeightOffset;
            terrainBuilder.TerrainMaterialTemplate = terrainMaterialTemplate;
            terrainBuilder.ObjectPath = objectPath;

            Log($"[VP Export] Loading terrain tile ({tileX},{tileZ})...");
            var nodes = await vpClient.QueryTerrainAsync(tileX, tileZ, Enumerable.Repeat(-1, 16).ToArray());
            terrainTileNodes[(tileX, tileZ)] = nodes;

            var mesh = terrainBuilder.BuildTerrainMesh(tileX, tileZ, nodes, out var materials);
            if (mesh == null)
            {
                Log($"[VP Export] Terrain tile ({tileX},{tileZ}) returned no mesh.");
                return;
            }

            var go = new GameObject($"Terrain_{tileX}_{tileZ}");
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

            RebuildNeighborTile(tileX - 1, tileZ);
            RebuildNeighborTile(tileX + 1, tileZ);
            RebuildNeighborTile(tileX, tileZ - 1);
            RebuildNeighborTile(tileX, tileZ + 1);
        }
        catch (Exception ex)
        {
            Log($"[VP Export] Failed to build terrain tile ({tileX},{tileZ}): {ex.Message}");
        }
    }

    private void RebuildNeighborTile(int tileX, int tileZ)
    {
        var key = (tileX, tileZ);
        if (!terrainTileNodes.TryGetValue(key, out var nodes))
            return;

        try
        {
            var mesh = terrainBuilder.BuildTerrainMesh(tileX, tileZ, nodes, out var materials);
            if (mesh == null)
                return;

            var existing = terrainRoot.transform.Find($"Terrain_{tileX}_{tileZ}");
            GameObject go;

            if (existing != null)
            {
                go = existing.gameObject;
                var mf = go.GetComponent<MeshFilter>() ?? go.AddComponent<MeshFilter>();
                mf.sharedMesh = mesh;

                var mr = go.GetComponent<MeshRenderer>() ?? go.AddComponent<MeshRenderer>();
                mr.sharedMaterials = materials.ToArray();

                if (addTerrainColliders)
                {
                    var mc = go.GetComponent<MeshCollider>() ?? go.AddComponent<MeshCollider>();
                    mc.sharedMesh = mesh;
                }
            }
            else
            {
                go = new GameObject($"Terrain_{tileX}_{tileZ}");
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
            }
        }
        catch (Exception ex)
        {
            Log($"[VP Export] Failed to rebuild neighbor terrain ({tileX},{tileZ}): {ex.Message}");
        }
    }

    private bool TryExportGlb(GameObject root, string path)
    {
        if (root == null || string.IsNullOrWhiteSpace(path))
            return false;

        string directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var colliders = root.GetComponentsInChildren<Collider>(true);
        var colliderStates = colliders.Select(c => (collider: c, enabled: c.enabled)).ToList();
        foreach (var c in colliders) c.enabled = false;

        bool exported = TryExportWithGltfast(root, path)
            || TryExportWithUnityGLTF(root, path)
            || TryExportWithSiccity(root, path);

        foreach (var pair in colliderStates)
            pair.collider.enabled = pair.enabled;

        return exported;
    }

    private bool TryExportWithUnityGLTF(GameObject root, string path)
    {
        string[] possibleTypes =
        {
            "GLTF.GLTFSceneExporter, UnityGLTF",
            "UnityGLTF.GLTFSceneExporter, UnityGLTF",
            "GLTF.GLTFSceneExporter",
            "UnityGLTF.GLTFSceneExporter"
        };

        foreach (var typeName in possibleTypes)
        {
            var type = Type.GetType(typeName);
            if (type == null)
                continue;

            object exporter = null;
            var ctorTransform = type.GetConstructor(new[] { typeof(Transform[]) });
            if (ctorTransform != null)
                exporter = ctorTransform.Invoke(new object[] { new[] { root.transform } });

            var ctorGo = type.GetConstructor(new[] { typeof(GameObject[]) });
            if (exporter == null && ctorGo != null)
                exporter = ctorGo.Invoke(new object[] { new[] { root } });

            if (exporter == null && type.GetConstructor(Type.EmptyTypes) != null)
                exporter = Activator.CreateInstance(type);

            if (exporter == null)
                continue;

            var methods = new[]
            {
                type.GetMethod("SaveGLB", new[] { typeof(string) }),
                type.GetMethod("ExportGLB", new[] { typeof(string) }),
                type.GetMethod("SaveGLBToPath", new[] { typeof(string) }),
                type.GetMethod("ExportScene", new[] { typeof(string), typeof(string) })
            };

            foreach (var method in methods.Where(m => m != null))
            {
                try
                {
                    if (method.GetParameters().Length == 1)
                    {
                        method.Invoke(exporter, new object[] { path });
                        return true;
                    }

                    if (method.GetParameters().Length == 2)
                    {
                        string dir = Path.GetDirectoryName(path) ?? ".";
                        string file = Path.GetFileName(path);
                        method.Invoke(exporter, new object[] { dir, file });
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Log($"[VP Export] UnityGLTF export attempt failed via {method.Name}: {ex.Message}");
                }
            }
        }

        return false;
    }

    private bool TryExportWithGltfast(GameObject root, string path)
    {
        Type exportType = FindType(
            "GLTFast.Export.GltfExport, glTFast",
            "GLTFast.GltfExport, glTFast",
            "GLTFast.Export.GltfExport",
            "GLTFast.GltfExport");
        if (exportType == null)
        {
            Log("[VP Export] glTFast exporter not found, falling back.");
            return false;
        }
        else
        {
            Log($"[VP Export] glTFast exporter detected: {exportType.FullName}");
        }

        object exportInstance = null;
        try
        {
            exportInstance = Activator.CreateInstance(exportType);
        }
        catch (Exception ex)
        {
            Log($"[VP Export] glTFast: failed to create exporter: {ex.Message}");
            return false;
        }

        object exportSettings = null;
        Type exportSettingsType = FindType(
            "GLTFast.Export.ExportSettings, glTFast",
            "GLTFast.Export.ExportSettings");
        if (exportSettingsType != null)
        {
            try
            {
                Log($"[VP Export] glTFast ExportSettings detected: {exportSettingsType.FullName}");
                exportSettings = Activator.CreateInstance(exportSettingsType);
                var formatProp = exportSettingsType.GetProperty("Format");
                if (formatProp != null && formatProp.PropertyType.IsEnum)
                {
                    var enumValue = Enum.Parse(formatProp.PropertyType, "Glb", ignoreCase: true);
                    formatProp.SetValue(exportSettings, enumValue);
                }
                else
                {
                    Log("[VP Export] glTFast ExportSettings.Format property not found; continuing with defaults.");
                }
            }
            catch (Exception ex)
            {
                Log($"[VP Export] glTFast: failed to prepare export settings: {ex.Message}");
            }
        }

        bool added = false;
        var addSceneMethods = exportType.GetMethods().Where(m => m.Name == "AddScene").ToList();
        if (addSceneMethods.Count == 0)
            Log("[VP Export] glTFast AddScene method not found; falling back.");

        foreach (var method in addSceneMethods)
        {
            try
            {
                var parameters = method.GetParameters();
                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(GameObject))
                {
                    var result = method.Invoke(exportInstance, new object[] { root });
                    added = result is bool b ? b : true;
                }
                else if (parameters.Length == 1 && parameters[0].ParameterType == typeof(GameObject[]))
                {
                    var result = method.Invoke(exportInstance, new object[] { new[] { root } });
                    added = result is bool b ? b : true;
                }
                else if (parameters.Length == 2 && parameters[0].ParameterType == typeof(GameObject) && exportSettings != null && parameters[1].ParameterType.IsInstanceOfType(exportSettings))
                {
                    var result = method.Invoke(exportInstance, new object[] { root, exportSettings });
                    added = result is bool b ? b : true;
                }
                else if (parameters.Length == 2 && parameters[0].ParameterType == typeof(GameObject[]) && exportSettings != null && parameters[1].ParameterType.IsInstanceOfType(exportSettings))
                {
                    var result = method.Invoke(exportInstance, new object[] { new[] { root }, exportSettings });
                    added = result is bool b ? b : true;
                }
            }
            catch (Exception ex)
            {
                Log($"[VP Export] glTFast AddScene failed via {method}: {ex.Message}");
            }

            if (added)
                break;
        }

        if (!added)
        {
            Log("[VP Export] glTFast AddScene returned false; skipping glTFast.");
            return false;
        }

        var saveMethods = new[]
        {
            ("SaveToFileAndDispose", new[] { typeof(string) }),
            ("SaveToFile", new[] { typeof(string) })
        };

        foreach (var (name, sig) in saveMethods)
        {
            var method = exportType.GetMethod(name, sig);
            if (method == null)
                continue;

            try
            {
                var result = method.Invoke(exportInstance, new object[] { path });
                if (result == null || (result is bool b && b))
                {
                    Log("[VP Export] glTFast export succeeded.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log($"[VP Export] glTFast {name} failed: {ex.Message}");
            }
        }

        return false;
    }

    private Type FindType(params string[] names)
    {
        foreach (var name in names)
        {
            var t = Type.GetType(name);
            if (t != null) return t;
        }

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (var name in names)
            {
                var t = asm.GetType(name, false);
                if (t != null) return t;
            }

            try
            {
                var match = asm.GetTypes().FirstOrDefault(t => names.Any(n => t.FullName == n || t.Name == n || t.FullName?.EndsWith(n, StringComparison.OrdinalIgnoreCase) == true));
                if (match != null) return match;
            }
            catch { }
        }

        return null;
    }

    private bool TryExportWithSiccity(GameObject root, string path)
    {
        var exporterType = Type.GetType("Siccity.GLTFUtility.Exporter, GLTFUtility");
        if (exporterType == null)
            exporterType = Type.GetType("Siccity.GLTFUtility.Exporter");

        var exportMethod = exporterType?.GetMethod("ExportGameObject", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(GameObject) }, null);
        if (exportMethod == null)
            return false;

        try
        {
            exportMethod.Invoke(null, new object[] { path, root });
            return true;
        }
        catch (Exception ex)
        {
            Log($"[VP Export] Siccity GLTF export failed: {ex.Message}");
            return false;
        }
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

    private float GetUnityUnitsPerVpUnit()
    {
        return 1f / Mathf.Max(0.0001f, vpUnitsPerUnityUnit);
    }

    private float GetUnityUnitsPerVpCell()
    {
        return vpUnitsPerCell * GetUnityUnitsPerVpUnit();
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

    private string NormalizeModelName(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            return modelName;

        return modelName.EndsWith(".rwx", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(modelName)
            : modelName;
    }

    private void Log(string message)
    {
        if (logProgress)
            Debug.Log(message);

        OnLog?.Invoke(message);
    }
}
