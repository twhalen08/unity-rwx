using UnityEngine;
using RWXLoader;
using System.Linq;
using System.IO;
using System.Collections.Generic;

/// <summary>
/// Demo script showing how to load RWX models from remote object paths
/// This demonstrates the full remote loading workflow with caching
/// </summary>
public class RWXRemoteDemo : MonoBehaviour
{
    [Header("Remote Object Path Settings")]
    [Tooltip("Base URL of the object server (e.g., http://objects.virtualparadise.org/vpbuild/)")]
    public string objectPath = "http://objects.virtualparadise.org/vpbuild/";
    [Tooltip("Password for password-protected object paths (used for model and texture ZIP files)")]
    public string objectPathPassword = "";
    
    [Header("Model Settings")]
    [Tooltip("Name of the model to load (without .rwx extension)")]
    public string modelName = "couch1a";
    
    [Tooltip("Load the model automatically when the scene starts")]
    public bool loadOnStart = true;
    
    [Tooltip("Scale factor for the loaded model")]
    public float modelScale = 10f;
    
    [Header("Debug")]
    [Tooltip("Enable detailed debug logging")]
    public bool enableDebugLogs = true;

    [Header("VP Action Testing")]
    [Tooltip("Optional VP action string to apply to the loaded model (supports create/activate parsing).")]
    [TextArea(2, 5)]
    public string action = "";
    [Tooltip("Apply parsed actions automatically after the model is loaded.")]
    public bool applyActionsOnLoad = true;
    [Tooltip("Log parsed action details for troubleshooting.")]
    public bool logActionDetails = true;

    private RWXLoaderAdvanced loader;
    private GameObject loadedModel;

    void Start()
    {
        SetupRemoteLoader();
        
        if (loadOnStart && !string.IsNullOrEmpty(modelName))
        {
            LoadRemoteModel();
        }
    }

    void SetupRemoteLoader()
    {
        // Find existing loader or create new one
        loader = FindFirstObjectByType<RWXLoaderAdvanced>();
        if (loader == null)
        {
            GameObject loaderObj = new GameObject("RWX Remote Loader");
            loader = loaderObj.AddComponent<RWXLoaderAdvanced>();
        }

        // Configure the loader
        loader.defaultObjectPath = objectPath;
        loader.objectPathPassword = objectPathPassword;
        loader.enableDebugLogs = enableDebugLogs;
        loader.parentTransform = transform;

        EnsureAssetManager();
    }

    [ContextMenu("Load Remote Model")]
    public void LoadRemoteModel()
    {
        if (string.IsNullOrEmpty(modelName))
        {
            Debug.LogError("Model name is empty!");
            return;
        }

        if (string.IsNullOrEmpty(objectPath))
        {
            Debug.LogError("Object path is empty!");
            return;
        }

        if (loader == null)
        {
            SetupRemoteLoader();
        }

        // Clear any existing model
        ClearLoadedModel();


        // Load the model from remote with caching
        loader.LoadModelFromRemote(modelName, objectPath, OnModelLoaded, objectPathPassword);
    }

    void OnModelLoaded(GameObject model, string result)
    {
        if (model != null)
        {
            loadedModel = model;
            
            // Position and scale the model
            model.transform.position = transform.position;
            model.transform.rotation = transform.rotation;
            model.transform.localScale = UnityEngine.Vector3.one * modelScale;

            if (enableDebugLogs)
            {
                LogModelInfo(model);
            }

            if (applyActionsOnLoad)
            {
                ApplyActionsToLoadedModel();
            }
            
        }
        else
        {
            Debug.LogError($"Failed to load remote model '{modelName}': {result}");
            ShowTroubleshootingInfo();
        }
    }

    void LogModelInfo(GameObject model)
    {
        int totalVertices = 0;
        int totalTriangles = 0;
        int meshCount = 0;

        MeshFilter[] meshFilters = model.GetComponentsInChildren<MeshFilter>();
        foreach (MeshFilter mf in meshFilters)
        {
            if (mf.sharedMesh != null)
            {
                totalVertices += mf.sharedMesh.vertexCount;
                totalTriangles += mf.sharedMesh.triangles.Length / 3;
                meshCount++;
            }
        }

        Debug.Log($"Remote Model Statistics:");
        Debug.Log($"  - Mesh objects: {meshCount}");
        Debug.Log($"  - Total vertices: {totalVertices}");
        Debug.Log($"  - Total triangles: {totalTriangles}");
        Debug.Log($"  - Child objects: {model.transform.childCount}");
        
        // Check for textures
        MeshRenderer[] renderers = model.GetComponentsInChildren<MeshRenderer>();
        int texturedObjects = 0;
        foreach (MeshRenderer renderer in renderers)
        {
            if (renderer.material != null && renderer.material.mainTexture != null)
            {
                texturedObjects++;
            }
        }
        Debug.Log($"  - Objects with textures: {texturedObjects}/{renderers.Length}");
        
        // Detailed hierarchy analysis for troubleshooting
        if (modelName.ToLower().Contains("tree"))
        {
            LogTreeHierarchy(model.transform, 0);
        }
    }

    void LogTreeHierarchy(Transform parent, int depth)
    {
        string indent = new string(' ', depth * 2);
        UnityEngine.Vector3 pos = parent.position;
        UnityEngine.Vector3 localPos = parent.localPosition;
        
        MeshFilter mf = parent.GetComponent<MeshFilter>();
        MeshRenderer mr = parent.GetComponent<MeshRenderer>();
        
        string meshInfo = mf != null ? $" [Mesh: {mf.sharedMesh?.vertexCount} verts]" : "";
        string materialInfo = mr != null && mr.material != null ? $" [Mat: {mr.material.name}]" : "";
        
        Debug.Log($"{indent}{parent.name} - Pos: {pos:F2}, LocalPos: {localPos:F2}{meshInfo}{materialInfo}");
        
        // Check if this object is at origin when it shouldn't be
        if (depth > 0 && localPos.magnitude < 0.1f && parent.name.ToLower().Contains("green"))
        {
            Debug.LogWarning($"{indent}⚠️ GREEN PART AT ORIGIN: {parent.name} - This might be the issue!");
        }
        
        for (int i = 0; i < parent.childCount; i++)
        {
            LogTreeHierarchy(parent.GetChild(i), depth + 1);
        }
    }

    void ShowTroubleshootingInfo()
    {
        Debug.Log("=== RWX REMOTE LOADING TROUBLESHOOTING ===");
        Debug.Log($"1. Check if the object path is accessible: {objectPath}");
        Debug.Log($"2. Verify the model exists: {objectPath}models/{modelName}.zip");
        Debug.Log($"3. Check your internet connection");
        Debug.Log($"4. Look for CORS issues in the browser console if running WebGL");
        Debug.Log($"5. Cache location: {loader?.GetCacheInfo(objectPath)}");
        Debug.Log("==========================================");
    }

    void EnsureAssetManager()
    {
        if (RWXAssetManager.Instance == null)
        {
            var manager = new GameObject("RWX Asset Manager");
            manager.AddComponent<RWXAssetManager>();
        }
    }

    [ContextMenu("Clear Loaded Model")]
    public void ClearLoadedModel()
    {
        if (loadedModel != null)
        {
            if (Application.isPlaying)
            {
                Destroy(loadedModel);
            }
            else
            {
                DestroyImmediate(loadedModel);
            }
            loadedModel = null;
            
        }
    }

    [ContextMenu("Show Cache Info")]
    public void ShowCacheInfo()
    {
        if (loader != null)
        {
            string info = loader.GetCacheInfo(objectPath);
            Debug.Log($"Cache Info for {objectPath}:\n{info}");
        }
        else
        {
            Debug.Log($"Cache would be located at: {System.IO.Path.Combine(Application.persistentDataPath, "RWXCache")}");
        }
    }

    [ContextMenu("Clear Cache")]
    public void ClearCache()
    {
        if (loader != null)
        {
            loader.ClearCache(objectPath);
            Debug.Log($"Cleared cache for {objectPath}");
        }
    }

    [ContextMenu("Test Connection")]
    public void TestConnection()
    {
        StartCoroutine(TestConnectionCoroutine());
    }

    [ContextMenu("Apply Actions To Loaded Model")]
    public void ApplyActionsToLoadedModel()
    {
        if (loadedModel == null)
        {
            Debug.LogWarning("No loaded model available to apply actions to.");
            return;
        }

        if (string.IsNullOrWhiteSpace(action))
        {
            Debug.LogWarning("Action string is empty - nothing to parse.");
            return;
        }

        VpActionParser.Parse(action, out List<VpActionCommand> createActions, out List<VpActionCommand> activateActions);

        if (createActions.Count == 0 && activateActions.Count == 0)
        {
            Debug.LogWarning("Parsed action string but found no create or activate commands.");
            return;
        }

        if (logActionDetails)
        {
            Debug.Log($"[VP Action] Parsed actions for '{modelName}': create={createActions.Count}, activate={activateActions.Count}");

            foreach (var c in createActions)
            {
                Debug.Log($"  CREATE -> {c}");
            }

            foreach (var a in activateActions)
            {
                Debug.Log($"  ACTIVATE -> {a}");
            }
        }

        EnsureAssetManager();

        foreach (var c in createActions)
        {
            VpActionExecutor.ExecuteCreate(loadedModel, c, objectPath, objectPathPassword, this);
        }

        if (activateActions.Count > 0)
        {
            var act = loadedModel.GetComponent<VpActivateActions>() ?? loadedModel.AddComponent<VpActivateActions>();
            act.actions.Clear();
            act.actions.AddRange(activateActions);
        }
    }

    System.Collections.IEnumerator TestConnectionCoroutine()
    {
        string encodedFileName = UnityEngine.Networking.UnityWebRequest.EscapeURL(modelName + ".zip");
        string testUrl = objectPath.TrimEnd('/') + "/models/" + encodedFileName;
        if (!string.IsNullOrEmpty(objectPathPassword))
        {
            testUrl += (testUrl.Contains("?") ? "&" : "?") + "password=" + UnityEngine.Networking.UnityWebRequest.EscapeURL(objectPathPassword);
        }
        Debug.Log($"Testing connection to: {testUrl}");
        
        using (UnityEngine.Networking.UnityWebRequest request = UnityEngine.Networking.UnityWebRequest.Head(testUrl))
        {
            yield return request.SendWebRequest();
            
            if (request.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
            {
                Debug.Log($"✓ Connection successful! Model exists at: {testUrl}");
                Debug.Log($"Content-Length: {request.GetResponseHeader("Content-Length")} bytes");
            }
            else
            {
                Debug.LogError($"✗ Connection failed: {request.error}");
                Debug.LogError($"Response Code: {request.responseCode}");
            }
        }
    }

    // Quick load methods for common models
    [ContextMenu("Load Couch Model")]
    public void LoadCouchModel()
    {
        modelName = "couch1a";
        LoadRemoteModel();
    }

    [ContextMenu("Load Tree Model")]
    public void LoadTreeModel()
    {
        modelName = "tree01";
        LoadRemoteModel();
    }

    [ContextMenu("Load Tree01 Debug")]
    public void LoadTree01Debug()
    {
        modelName = "tree01";
        enableDebugLogs = true;
        LoadRemoteModel();
    }

    [ContextMenu("Load Bed Model")]
    public void LoadBedModel()
    {
        string filePath = System.IO.Path.Combine(Application.dataPath, "Models", "BED1.RWX");
        
        if (!System.IO.File.Exists(filePath))
        {
            Debug.LogError($"Bed model not found at: {filePath}");
            return;
        }
        
        Debug.Log($"🛏️ Loading bed model from: {filePath}");
        
        // Clear existing objects
        var existingObjects = GameObject.FindObjectsOfType<GameObject>()
            .Where(go => go.name.StartsWith("BED1") || go.name.StartsWith("Clump_"))
            .ToArray();
        
        foreach (var obj in existingObjects)
        {
            DestroyImmediate(obj);
        }
        
        // Use RWXLoaderMain for local file loading
        var loaderMain = new RWXLoaderMain();
        GameObject bedObject = loaderMain.LoadRWXFile(filePath);
        
        if (bedObject != null)
        {
            Debug.Log($"✅ Successfully loaded bed model: {bedObject.name}");
            
            // Position it at origin for easy viewing
            bedObject.transform.position = UnityEngine.Vector3.zero;
            bedObject.transform.rotation = Quaternion.identity;
            
            // Log the hierarchy
            LogGameObjectHierarchy(bedObject, 0);
        }
        else
        {
            Debug.LogError("❌ Failed to load bed model");
        }
    }

    private static void LogGameObjectHierarchy(GameObject obj, int depth)
    {
        string indent = new string(' ', depth * 2);
        UnityEngine.Vector3 pos = obj.transform.position;
        UnityEngine.Vector3 localPos = obj.transform.localPosition;
        UnityEngine.Vector3 localRot = obj.transform.localEulerAngles;
        UnityEngine.Vector3 localScale = obj.transform.localScale;
        
        MeshFilter mf = obj.GetComponent<MeshFilter>();
        MeshRenderer mr = obj.GetComponent<MeshRenderer>();
        
        string meshInfo = mf != null ? $" [Mesh: {mf.sharedMesh?.vertexCount} verts]" : "";
        string materialInfo = mr != null && mr.material != null ? $" [Mat: {mr.material.name}]" : "";
        
        Debug.Log($"{indent}{obj.name}");
        Debug.Log($"{indent}  Pos: {pos:F2}, LocalPos: {localPos:F2}");
        Debug.Log($"{indent}  Rot: {localRot:F1}, Scale: {localScale:F2}{meshInfo}{materialInfo}");
        
        for (int i = 0; i < obj.transform.childCount; i++)
        {
            LogGameObjectHierarchy(obj.transform.GetChild(i).gameObject, depth + 1);
        }
    }

    // Gizmo to show where model will appear
    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireCube(transform.position, UnityEngine.Vector3.one * 2f);
        
        Gizmos.color = Color.green;
        Gizmos.DrawRay(transform.position, transform.up * 3f);
        
        // Draw object path info
        if (enableDebugLogs)
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(transform.position + UnityEngine.Vector3.up * 4f, 0.5f);
        }
    }

    void OnGUI()
    {
        if (!enableDebugLogs) return;
        
        GUILayout.BeginArea(new Rect(10, 10, 420, 360));
        GUILayout.Label("RWX Remote Demo", GUI.skin.box);
        GUILayout.Label($"Status: {(loadedModel != null ? "Loaded" : "Not Loaded")}");

        GUILayout.Label("Object Path:");
        objectPath = GUILayout.TextField(objectPath ?? string.Empty);

        GUILayout.Label("Model Name:");
        modelName = GUILayout.TextField(modelName ?? string.Empty);

        GUILayout.Label("Object Path Password:");
        objectPathPassword = GUILayout.TextField(objectPathPassword ?? string.Empty);
        
        if (GUILayout.Button("Load Model"))
        {
            LoadRemoteModel();
        }
        
        if (GUILayout.Button("Clear Model"))
        {
            ClearLoadedModel();
        }
        
        if (GUILayout.Button("Test Connection"))
        {
            TestConnection();
        }
        
        GUILayout.Label("VP Action (optional):");
        action = GUILayout.TextArea(action ?? string.Empty, GUILayout.Height(60));
        applyActionsOnLoad = GUILayout.Toggle(applyActionsOnLoad, "Apply actions after load");
        
        if (GUILayout.Button("Apply Actions Now"))
        {
            ApplyActionsToLoadedModel();
        }
        
        GUILayout.EndArea();
    }
}
