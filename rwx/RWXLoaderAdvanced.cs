using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace RWXLoader
{
    public class RWXLoaderAdvanced : MonoBehaviour
    {
        [Header("RWX Loading")]
        public string defaultObjectPath = "http://objects.virtualparadise.org/vpbuild/";
        public string objectPathPassword = "";
        public Transform parentTransform;

        [Header("Debug")]
        public bool enableDebugLogs = true;

        [Header("Performance")]
        [Tooltip("Max milliseconds of RWX parsing work per frame before yielding.")]
        [Min(0.1f)]
        public float parseFrameBudgetMs = 4f;

        [Tooltip("If true, only one model will parse/build at a time globally (prevents '2 instances' big frames).")]
        public bool throttleConcurrentLoads = true;

        private RWXParser parser;
        private RWXMeshBuilder meshBuilder;
        private RWXMaterialManager materialManager;
        private RWXAssetManager assetManager;

        private static readonly Dictionary<string, GameObject> modelPrefabCache = new Dictionary<string, GameObject>();
        private static Transform cacheContainer;

        // Global gate to prevent 2 heavy model builds in the same frame
        private static readonly SemaphoreSlim _loadGate = new SemaphoreSlim(1, 1);

        private void Awake()
        {
            InitializeAssetManager();
        }

        private void Start()
        {
            materialManager = GetComponent<RWXMaterialManager>();
            if (materialManager == null)
                materialManager = gameObject.AddComponent<RWXMaterialManager>();

            meshBuilder = new RWXMeshBuilder(materialManager);
            parser = new RWXParser(meshBuilder);
        }

        private void InitializeAssetManager()
        {
            assetManager = RWXAssetManager.Instance;
            if (assetManager == null)
            {
                GameObject assetManagerGO = new GameObject("RWXAssetManager");
                assetManager = assetManagerGO.AddComponent<RWXAssetManager>();
            }
        }

        public void LoadModelFromRemote(
            string modelName,
            string objectPath = null,
            Action<GameObject, string> onComplete = null,
            string password = null,
            bool activateOnInstantiate = true)
        {
            if (string.IsNullOrEmpty(objectPath))
                objectPath = defaultObjectPath;

            if (string.IsNullOrEmpty(password))
                password = objectPathPassword;

            if (TryInstantiateFromCache(objectPath, modelName, activateOnInstantiate, out GameObject cachedInstance))
            {
                onComplete?.Invoke(cachedInstance, "Success (cached)");
                return;
            }

            StartCoroutine(LoadModelFromRemoteCoroutine(modelName, objectPath, password, onComplete, activateOnInstantiate));
        }

        private bool TryInstantiateFromCache(string objectPath, string modelName, bool activateOnInstantiate, out GameObject instance)
        {
            instance = null;
            string cacheKey = GetCacheKey(objectPath, modelName);

            if (modelPrefabCache.TryGetValue(cacheKey, out GameObject prefab) && prefab != null)
            {
                instance = Instantiate(prefab, parentTransform);
                instance.name = prefab.name;
                instance.SetActive(activateOnInstantiate);
                return true;
            }

            return false;
        }

        private string GetCacheKey(string objectPath, string modelName)
        {
            return (objectPath.TrimEnd('/') + "/" + modelName).ToLowerInvariant();
        }

        private Transform GetOrCreateCacheContainer()
        {
            if (cacheContainer == null)
            {
                var containerGO = new GameObject("RWX Model Cache");
                DontDestroyOnLoad(containerGO);
                cacheContainer = containerGO.transform;
            }

            return cacheContainer;
        }

        private IEnumerator LoadModelFromRemoteCoroutine(
            string modelName,
            string objectPath,
            string password,
            Action<GameObject, string> onComplete,
            bool activateOnInstantiate)
        {
            if (enableDebugLogs)
                UnityEngine.Debug.Log($"Loading model '{modelName}' from object path: {objectPath}");

            if (assetManager == null)
                InitializeAssetManager();

            if (assetManager == null)
            {
                onComplete?.Invoke(null, "Failed to initialize asset manager");
                yield break;
            }

            // Throttle so you don't build 2 models in the same frame
            if (throttleConcurrentLoads)
            {
                var waitTask = _loadGate.WaitAsync();
                while (!waitTask.IsCompleted)
                    yield return null;
            }

            string localZipPath = "";
            ZipArchive archive = null;

            try
            {
                // ---------- Download ----------
                bool downloadSuccess = false;
                string downloadError = "";

                yield return assetManager.DownloadModel(objectPath, modelName, (success, result) =>
                {
                    downloadSuccess = success;
                    if (success) localZipPath = result;
                    else downloadError = result;
                }, password);

                if (!downloadSuccess)
                {
                    onComplete?.Invoke(null, downloadError);
                    yield break;
                }

                // ---------- Load ZIP (no yields here, so try/catch OK) ----------
                try
                {
                    archive = assetManager.LoadZipArchive(localZipPath);
                }
                catch (Exception e)
                {
                    onComplete?.Invoke(null, $"LoadZipArchive failed: {e.Message}");
                    yield break;
                }

                if (archive == null)
                {
                    onComplete?.Invoke(null, $"Failed to load ZIP archive: {localZipPath}");
                    yield break;
                }

                // ---------- Read RWX (no yields here, so try/catch OK) ----------
                string rwxContent = null;
                string rwxFileName = modelName + ".rwx";

                try
                {
                    rwxContent = assetManager.ReadTextFromZip(archive, rwxFileName, localZipPath, password);

                    if (string.IsNullOrEmpty(rwxContent))
                    {
                        rwxFileName = modelName + ".RWX";
                        rwxContent = assetManager.ReadTextFromZip(archive, rwxFileName, localZipPath, password);
                    }

                    if (string.IsNullOrEmpty(rwxContent))
                    {
                        rwxFileName = FindFirstRwxEntry(archive);
                        if (!string.IsNullOrEmpty(rwxFileName))
                            rwxContent = assetManager.ReadTextFromZip(archive, rwxFileName, localZipPath, password);
                    }
                }
                catch (Exception e)
                {
                    onComplete?.Invoke(null, $"ReadTextFromZip failed: {e.Message}");
                    yield break;
                }

                if (string.IsNullOrEmpty(rwxContent))
                {
                    onComplete?.Invoke(null, "RWX file not found in ZIP");
                    yield break;
                }

                // ---------- Parse (yields happen inside parse coroutine; parse coroutine has NO try/catch around yields) ----------
                GameObject builtPrefab = null;
                string parseError = null;

                yield return ParseRWXFromMemoryCoroutine(
                    rwxContent,
                    modelName,
                    objectPath,
                    password,
                    go => builtPrefab = go,
                    err => parseError = err,
                    parseFrameBudgetMs);

                if (builtPrefab == null)
                {
                    onComplete?.Invoke(null, parseError ?? "Parse failed (unknown error)");
                    yield break;
                }

                // ---------- Cache + Instantiate (no yields here) ----------
                try
                {
                    CachePrefab(objectPath, modelName, builtPrefab);

                    CachePrefab(objectPath, modelName, builtPrefab);

                    // builtPrefab is inactive, so clones start inactive too (no flash)
                    var instance = Instantiate(builtPrefab, parentTransform);
                    instance.name = modelName;

                    if (activateOnInstantiate)
                        instance.SetActive(true);
                    else
                        instance.SetActive(false);



                    onComplete?.Invoke(instance, "Success");
                }
                catch (Exception e)
                {
                    onComplete?.Invoke(null, $"Instantiate failed: {e.Message}");
                    yield break;
                }
            }
            finally
            {
                // cleanup
                try
                {
                    if (!string.IsNullOrEmpty(localZipPath))
                        assetManager.UnloadZipArchive(localZipPath);
                }
                catch { /* ignore */ }

                if (throttleConcurrentLoads)
                {
                    try { _loadGate.Release(); } catch { /* ignore */ }
                }
            }
        }

        /// <summary>
        /// Time-sliced RWX parse (no string.Split), yields every budgetMs.
        /// IMPORTANT: No try/catch may contain a yield return.
        /// </summary>
        private IEnumerator ParseRWXFromMemoryCoroutine(
    string rwxContent,
    string modelName,
    string objectPath,
    string password,
    Action<GameObject> onBuilt,
    Action<string> onError,
    float budgetMs)
        {
            if (materialManager == null || meshBuilder == null || parser == null)
            {
                onError?.Invoke("Components not properly initialized");
                onBuilt?.Invoke(null);
                yield break;
            }

            // Create the root INACTIVE so nothing can render while we yield during parsing.
            GameObject rootObject = new GameObject(modelName);
            rootObject.SetActive(false);

            // Optional: keep parse-time objects out of the scene root (and survive scene loads if you want)
            // This also prevents clutter and makes it very obvious these are "prefabs".
            try
            {
                Transform container = GetOrCreateCacheContainer();
                rootObject.transform.SetParent(container, false);
            }
            catch { /* ignore */ }

            var context = new RWXParseContext
            {
                rootObject = rootObject,
                currentObject = rootObject
            };

            materialManager.SetTextureSource(objectPath, password);
            parser.Reset();

            float sliceStart = Time.realtimeSinceStartup;
            int linesProcessed = 0;

            using (var sr = new StringReader(rwxContent))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;

                    try
                    {
                        parser.ProcessLine(line, context);
                    }
                    catch (Exception e)
                    {
                        onError?.Invoke($"Parse error line {linesProcessed}: {e.Message}");
                        Destroy(rootObject);
                        onBuilt?.Invoke(null);
                        yield break;
                    }

                    linesProcessed++;

                    if (((Time.realtimeSinceStartup - sliceStart) * 1000f) >= budgetMs)
                    {
                        yield return null;
                        sliceStart = Time.realtimeSinceStartup;
                    }
                }
            }

            // Give Unity a breath before final commit
            yield return null;

            try
            {
                meshBuilder.FinalCommit(context);
            }
            catch (Exception e)
            {
                onError?.Invoke($"FinalCommit failed: {e.Message}");
                Destroy(rootObject);
                onBuilt?.Invoke(null);
                yield break;
            }

            if (enableDebugLogs)
                UnityEngine.Debug.Log($"Parsed RWX '{modelName}' lines={linesProcessed} vertices={context.vertices.Count}");

            // Keep it inactive: this is your prefab template.
            rootObject.SetActive(false);

            onBuilt?.Invoke(rootObject);
        }


        private string FindFirstRwxEntry(ZipArchive archive)
        {
            foreach (var entry in archive.Entries)
            {
                if (entry == null || string.IsNullOrEmpty(entry.FullName))
                    continue;

                if (entry.FullName.EndsWith(".rwx", StringComparison.OrdinalIgnoreCase))
                    return entry.FullName;
            }

            return null;
        }

        private void CachePrefab(string objectPath, string modelName, GameObject modelObject)
        {
            string cacheKey = GetCacheKey(objectPath, modelName);

            if (modelPrefabCache.ContainsKey(cacheKey))
                return;

            Transform container = GetOrCreateCacheContainer();
            modelObject.transform.SetParent(container, false);
            modelObject.SetActive(false);
            modelPrefabCache[cacheKey] = modelObject;
        }

        // --- Utility methods unchanged (but included so the file compiles) ---

        public GameObject LoadModelFromZip(string zipPath, string modelName)
        {
            if (assetManager == null)
                InitializeAssetManager();

            ZipArchive archive = assetManager.LoadZipArchive(zipPath);
            if (archive == null)
            {
                UnityEngine.Debug.LogError($"Failed to load ZIP archive: {zipPath}");
                return null;
            }

            try
            {
                string rwxFileName = modelName + ".rwx";
                string rwxContent = assetManager.ReadTextFromZip(archive, rwxFileName, zipPath, objectPathPassword);

                if (string.IsNullOrEmpty(rwxContent))
                {
                    rwxFileName = modelName + ".RWX";
                    rwxContent = assetManager.ReadTextFromZip(archive, rwxFileName, zipPath, objectPathPassword);
                }

                if (string.IsNullOrEmpty(rwxContent))
                {
                    UnityEngine.Debug.LogError($"RWX file not found in ZIP: {rwxFileName}");
                    return null;
                }

                return ParseRWXFromMemorySync_NoSplit(rwxContent, modelName, objectPathPassword);
            }
            finally
            {
                try { assetManager.UnloadZipArchive(zipPath); } catch { }
            }
        }

        private GameObject ParseRWXFromMemorySync_NoSplit(string rwxContent, string modelName, string password)
        {
            if (materialManager == null || meshBuilder == null || parser == null)
            {
                UnityEngine.Debug.LogError("Components not properly initialized");
                return null;
            }

            GameObject rootObject = new GameObject(modelName);

            var context = new RWXParseContext
            {
                rootObject = rootObject,
                currentObject = rootObject
            };

            materialManager.SetTextureSource(null, password);
            parser.Reset();

            using (var sr = new StringReader(rwxContent))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    parser.ProcessLine(line, context);
                }
            }

            meshBuilder.FinalCommit(context);
            return rootObject;
        }

        public List<string> ListModelsInZip(string zipPath)
        {
            if (assetManager == null)
                InitializeAssetManager();

            ZipArchive archive = assetManager.LoadZipArchive(zipPath);
            if (archive == null)
                return new List<string>();

            try
            {
                var models = new List<string>();
                var files = assetManager.ListZipContents(archive);

                foreach (string file in files)
                {
                    if (file.EndsWith(".rwx", StringComparison.OrdinalIgnoreCase))
                    {
                        string modelName = Path.GetFileNameWithoutExtension(file);
                        models.Add(modelName);
                    }
                }

                return models;
            }
            finally
            {
                try { assetManager.UnloadZipArchive(zipPath); } catch { }
            }
        }

        public IEnumerator PreloadModels(string[] modelNames, string objectPath = null, Action<int, int> onProgress = null)
        {
            if (string.IsNullOrEmpty(objectPath))
                objectPath = defaultObjectPath;

            string password = objectPathPassword;

            if (assetManager == null)
                InitializeAssetManager();

            for (int i = 0; i < modelNames.Length; i++)
            {
                yield return assetManager.DownloadModel(objectPath, modelNames[i], (success, result) =>
                {
                    if (success)
                    {
                        if (enableDebugLogs)
                            UnityEngine.Debug.Log($"Preloaded model: {modelNames[i]}");
                    }
                    else
                    {
                        UnityEngine.Debug.LogWarning($"Failed to preload model {modelNames[i]}: {result}");
                    }
                }, password);

                onProgress?.Invoke(i + 1, modelNames.Length);
                yield return null;
            }
        }

        public string GetCacheInfo(string objectPath = null)
        {
            if (string.IsNullOrEmpty(objectPath))
                objectPath = defaultObjectPath;

            if (assetManager == null)
                InitializeAssetManager();

            string cachePath = assetManager.GetCachePath(objectPath);
            string modelsPath = Path.Combine(cachePath, "models");
            string texturesPath = Path.Combine(cachePath, "textures");

            int modelCount = Directory.Exists(modelsPath) ? Directory.GetFiles(modelsPath, "*.zip").Length : 0;
            int textureCount = Directory.Exists(texturesPath) ? Directory.GetFiles(texturesPath).Length : 0;

            return $"Cache Path: {cachePath}\nModels: {modelCount}\nTextures: {textureCount}";
        }

        public void ClearCache(string objectPath = null)
        {
            if (string.IsNullOrEmpty(objectPath))
                objectPath = defaultObjectPath;

            if (assetManager == null)
                InitializeAssetManager();

            string cachePath = assetManager.GetCachePath(objectPath);
            if (Directory.Exists(cachePath))
            {
                Directory.Delete(cachePath, true);
                UnityEngine.Debug.Log($"Cleared cache: {cachePath}");
            }
        }
    }
}
