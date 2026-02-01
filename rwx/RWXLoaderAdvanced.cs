using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading;
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

        private static readonly Dictionary<string, GameObject> modelPrefabCache = new();
        private static Transform cacheContainer;
        private static readonly SemaphoreSlim _loadGate = new(1, 1);

        private void Awake()
        {
            // Initialize asset manager first
            InitializeAssetManager();
        }

        private void Start()
        {
            // Initialize components
            materialManager = GetComponent<RWXMaterialManager>();
            if (materialManager == null)
            {
                materialManager = gameObject.AddComponent<RWXMaterialManager>();
            }

            meshBuilder = new RWXMeshBuilder(materialManager);
            parser = new RWXParser(meshBuilder);
        }

        private void InitializeAssetManager()
        {
            // Get or create asset manager
            assetManager = RWXAssetManager.Instance;
            if (assetManager == null)
            {
                GameObject assetManagerGO = new GameObject("RWXAssetManager");
                assetManager = assetManagerGO.AddComponent<RWXAssetManager>();
            }
        }

        /// <summary>
        /// Loads an RWX model from a remote object path with automatic downloading and caching
        /// </summary>
        /// <param name="modelName">Name of the model (without .rwx extension)</param>
        /// <param name="objectPath">Remote object server URL (optional, uses default if null)</param>
        /// <param name="onComplete">Callback when loading is complete</param>
        public void LoadModelFromRemote(
            string modelName,
            string objectPath = null,
            System.Action<GameObject, string> onComplete = null,
            string password = null,
            bool activateOnInstantiate = true)
        {
            if (string.IsNullOrEmpty(objectPath))
            {
                objectPath = defaultObjectPath;
            }

            if (string.IsNullOrEmpty(password))
            {
                password = objectPathPassword;
            }

            // Fast path: reuse a cached prefab instead of reparsing/downloading
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
            return $"{objectPath.TrimEnd('/')}/{modelName}".ToLowerInvariant();
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
            System.Action<GameObject, string> onComplete,
            bool activateOnInstantiate)
        {
            if (enableDebugLogs)
                Debug.Log($"Loading model '{modelName}' from object path: {objectPath}");

            Stopwatch totalWatch = enableDebugLogs ? Stopwatch.StartNew() : null;

            // Ensure asset manager is initialized
            if (assetManager == null)
            {
                InitializeAssetManager();
            }

            if (assetManager == null)
            {
                string error = "Failed to initialize asset manager";
                Debug.LogError(error);
                onComplete?.Invoke(null, error);
                yield break;
            }

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
                // Step 1: Download model if not cached
                bool downloadSuccess = false;
                string downloadError = "";

                Stopwatch downloadWatch = enableDebugLogs ? Stopwatch.StartNew() : null;
                yield return assetManager.DownloadModel(objectPath, modelName, (success, result) =>
                {
                    downloadSuccess = success;
                    if (success)
                        localZipPath = result;
                    else
                        downloadError = result;
                }, password);
                if (downloadWatch != null)
                {
                    downloadWatch.Stop();
                    Debug.Log($"[RWXLoaderAdvanced] Download '{modelName}' in {downloadWatch.ElapsedMilliseconds}ms");
                }

                if (!downloadSuccess)
                {
                    Debug.LogError($"Failed to download model {modelName}: {downloadError}");
                    onComplete?.Invoke(null, downloadError);
                    yield break;
                }

                // Step 2: Load ZIP archive into memory
                Stopwatch zipWatch = enableDebugLogs ? Stopwatch.StartNew() : null;
                try
                {
                    archive = assetManager.LoadZipArchive(localZipPath);
                }
                catch (Exception e)
                {
                    onComplete?.Invoke(null, $"LoadZipArchive failed: {e.Message}");
                    yield break;
                }
                if (zipWatch != null)
                {
                    zipWatch.Stop();
                    Debug.Log($"[RWXLoaderAdvanced] LoadZipArchive '{modelName}' in {zipWatch.ElapsedMilliseconds}ms");
                }
                if (archive == null)
                {
                    string error = $"Failed to load ZIP archive: {localZipPath}";
                    Debug.LogError(error);
                    onComplete?.Invoke(null, error);
                    yield break;
                }

                // Step 3: Find and load RWX file from ZIP
                string rwxFileName = $"{modelName}.rwx";
                Stopwatch rwxReadWatch = enableDebugLogs ? Stopwatch.StartNew() : null;
                string rwxContent = null;

                try
                {
                    rwxContent = assetManager.ReadTextFromZip(archive, rwxFileName, localZipPath, password);

                    if (string.IsNullOrEmpty(rwxContent))
                    {
                        rwxFileName = $"{modelName}.RWX";
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
                if (rwxReadWatch != null)
                {
                    rwxReadWatch.Stop();
                    Debug.Log($"[RWXLoaderAdvanced] Read RWX '{rwxFileName}' in {rwxReadWatch.ElapsedMilliseconds}ms");
                }

                if (string.IsNullOrEmpty(rwxContent))
                {
                    string error = $"RWX file not found in ZIP; attempted {modelName}.rwx and fallback entries";
                    Debug.LogError(error);
                    onComplete?.Invoke(null, error);
                    yield break;
                }

                if (enableDebugLogs)
                    Debug.Log($"Found RWX file: {rwxFileName} ({rwxContent.Length} characters)");

                // Step 4: Parse RWX content and create GameObject
                GameObject modelObject = null;
                string parseError = null;

                Stopwatch parseWatch = enableDebugLogs ? Stopwatch.StartNew() : null;
                yield return ParseRWXFromMemoryCoroutine(
                    rwxContent,
                    modelName,
                    objectPath,
                    password,
                    go => modelObject = go,
                    err => parseError = err,
                    parseFrameBudgetMs);
                if (parseWatch != null)
                {
                    parseWatch.Stop();
                    Debug.Log($"[RWXLoaderAdvanced] Parse '{modelName}' in {parseWatch.ElapsedMilliseconds}ms");
                }

                if (modelObject == null)
                {
                    onComplete?.Invoke(null, parseError ?? "Parse failed (unknown error)");
                    yield break;
                }

                // Cache the parsed prefab so future loads are instant
                CachePrefab(objectPath, modelName, modelObject);

                // Instantiate a live copy for the caller
                Stopwatch instantiateWatch = enableDebugLogs ? Stopwatch.StartNew() : null;
                modelObject = Instantiate(modelObject, parentTransform);
                modelObject.name = modelName;
                modelObject.SetActive(activateOnInstantiate);
                if (instantiateWatch != null)
                {
                    instantiateWatch.Stop();
                    Debug.Log($"[RWXLoaderAdvanced] Instantiate '{modelName}' in {instantiateWatch.ElapsedMilliseconds}ms");
                }

                if (enableDebugLogs)
                {
                    totalWatch?.Stop();
                    Debug.Log($"Successfully loaded model: {modelName} in {totalWatch?.ElapsedMilliseconds ?? 0}ms");
                }

                onComplete?.Invoke(modelObject, "Success");
            }
            finally
            {
                try
                {
                    if (!string.IsNullOrEmpty(localZipPath))
                        assetManager.UnloadZipArchive(localZipPath);
                }
                catch
                {
                    // Ignore cleanup exceptions.
                }

                if (throttleConcurrentLoads)
                {
                    try { _loadGate.Release(); } catch { }
                }
            }
        }

        /// <summary>
        /// Parses RWX content from memory and creates a GameObject
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

            GameObject rootObject = new GameObject(modelName);

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
                    if (line.Length == 0)
                        continue;

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
                Debug.Log($"Parsed RWX '{modelName}' lines={linesProcessed} vertices={context.vertices.Count}");

            onBuilt?.Invoke(rootObject);
        }

        private string FindFirstRwxEntry(ZipArchive archive)
        {
            foreach (var entry in archive.Entries)
            {
                if (entry == null || string.IsNullOrEmpty(entry.FullName))
                    continue;

                if (entry.FullName.EndsWith(".rwx", StringComparison.OrdinalIgnoreCase))
                {
                    return entry.FullName;
                }
            }

            return null;
        }

        private void CachePrefab(string objectPath, string modelName, GameObject modelObject)
        {
            string cacheKey = GetCacheKey(objectPath, modelName);

            // Avoid caching duplicates if already present
            if (modelPrefabCache.ContainsKey(cacheKey))
            {
                return;
            }

            // Keep a hidden prefab to instantiate from
            Transform container = GetOrCreateCacheContainer();
            modelObject.transform.SetParent(container, false);
            modelObject.SetActive(false);
            modelPrefabCache[cacheKey] = modelObject;
        }

        /// <summary>
        /// Loads an RWX model from a local ZIP file
        /// </summary>
        public GameObject LoadModelFromZip(string zipPath, string modelName)
        {
            if (assetManager == null)
            {
                InitializeAssetManager();
            }

            ZipArchive archive = assetManager.LoadZipArchive(zipPath);
            if (archive == null)
            {
                Debug.LogError($"Failed to load ZIP archive: {zipPath}");
                return null;
            }

            string rwxFileName = $"{modelName}.rwx";
            string rwxContent = assetManager.ReadTextFromZip(archive, rwxFileName, zipPath, objectPathPassword);

            if (string.IsNullOrEmpty(rwxContent))
            {
                rwxFileName = $"{modelName}.RWX";
                rwxContent = assetManager.ReadTextFromZip(archive, rwxFileName, zipPath, objectPathPassword);
            }

            if (string.IsNullOrEmpty(rwxContent))
            {
                Debug.LogError($"RWX file not found in ZIP: {rwxFileName}");
                return null;
            }

            return ParseRWXFromMemorySync_NoSplit(rwxContent, modelName, objectPathPassword);
        }

        private GameObject ParseRWXFromMemorySync_NoSplit(string rwxContent, string modelName, string password)
        {
            if (materialManager == null || meshBuilder == null || parser == null)
            {
                Debug.LogError("Components not properly initialized");
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
                    if (line.Length == 0)
                        continue;
                    parser.ProcessLine(line, context);
                }
            }

            meshBuilder.FinalCommit(context);
            return rootObject;
        }

        /// <summary>
        /// Lists all available models in a ZIP file
        /// </summary>
        public List<string> ListModelsInZip(string zipPath)
        {
            if (assetManager == null)
            {
                InitializeAssetManager();
            }

            ZipArchive archive = assetManager.LoadZipArchive(zipPath);
            if (archive == null)
            {
                return new List<string>();
            }

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

        /// <summary>
        /// Preloads multiple models for faster access
        /// </summary>
        public IEnumerator PreloadModels(string[] modelNames, string objectPath = null, System.Action<int, int> onProgress = null)
        {
            if (string.IsNullOrEmpty(objectPath))
            {
                objectPath = defaultObjectPath;
            }

            string password = objectPathPassword;

            if (assetManager == null)
            {
                InitializeAssetManager();
            }

            for (int i = 0; i < modelNames.Length; i++)
            {
                yield return assetManager.DownloadModel(objectPath, modelNames[i], (success, result) =>
                {
                    if (success)
                    {
                        if (enableDebugLogs)
                            Debug.Log($"Preloaded model: {modelNames[i]}");
                    }
                    else
                    {
                        Debug.LogWarning($"Failed to preload model {modelNames[i]}: {result}");
                    }
                }, password);

                onProgress?.Invoke(i + 1, modelNames.Length);
                yield return null; // Allow other operations
            }
        }

        /// <summary>
        /// Gets cache information for debugging
        /// </summary>
        public string GetCacheInfo(string objectPath = null)
        {
            if (string.IsNullOrEmpty(objectPath))
            {
                objectPath = defaultObjectPath;
            }

            if (assetManager == null)
            {
                InitializeAssetManager();
            }

            string cachePath = assetManager.GetCachePath(objectPath);
            string modelsPath = Path.Combine(cachePath, "models");
            string texturesPath = Path.Combine(cachePath, "textures");

            int modelCount = Directory.Exists(modelsPath) ? Directory.GetFiles(modelsPath, "*.zip").Length : 0;
            int textureCount = Directory.Exists(texturesPath) ? Directory.GetFiles(texturesPath).Length : 0;

            return $"Cache Path: {cachePath}\nModels: {modelCount}\nTextures: {textureCount}";
        }

        /// <summary>
        /// Clears the cache for a specific object path
        /// </summary>
        public void ClearCache(string objectPath = null)
        {
            if (string.IsNullOrEmpty(objectPath))
            {
                objectPath = defaultObjectPath;
            }

            if (assetManager == null)
            {
                InitializeAssetManager();
            }

            string cachePath = assetManager.GetCachePath(objectPath);
            if (Directory.Exists(cachePath))
            {
                Directory.Delete(cachePath, true);
                Debug.Log($"Cleared cache: {cachePath}");
            }
        }
    }
}
