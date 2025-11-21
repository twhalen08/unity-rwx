using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace RWXLoader
{
    public class RWXLoaderAdvanced : MonoBehaviour
    {
        [Header("RWX Loading")]
        public string defaultObjectPath = "http://objects.virtualparadise.org/vpbuild/";
        public Transform parentTransform;
        
        [Header("Debug")]
        public bool enableDebugLogs = true;
        
        private RWXParser parser;
        private RWXMeshBuilder meshBuilder;
        private RWXMaterialManager materialManager;
        private RWXAssetManager assetManager;

        private static readonly Dictionary<string, GameObject> modelPrefabCache = new();
        private static Transform cacheContainer;

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
        public void LoadModelFromRemote(string modelName, string objectPath = null, System.Action<GameObject, string> onComplete = null)
        {
            if (string.IsNullOrEmpty(objectPath))
            {
                objectPath = defaultObjectPath;
            }

            // Fast path: reuse a cached prefab instead of reparsing/downloading
            if (TryInstantiateFromCache(objectPath, modelName, out GameObject cachedInstance))
            {
                onComplete?.Invoke(cachedInstance, "Success (cached)");
                return;
            }

            StartCoroutine(LoadModelFromRemoteCoroutine(modelName, objectPath, onComplete));
        }

        private bool TryInstantiateFromCache(string objectPath, string modelName, out GameObject instance)
        {
            instance = null;
            string cacheKey = GetCacheKey(objectPath, modelName);

            if (modelPrefabCache.TryGetValue(cacheKey, out GameObject prefab) && prefab != null)
            {
                instance = Instantiate(prefab, parentTransform);
                instance.name = prefab.name;
                instance.SetActive(true);
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

        private IEnumerator LoadModelFromRemoteCoroutine(string modelName, string objectPath, System.Action<GameObject, string> onComplete)
        {
            if (enableDebugLogs)
                Debug.Log($"Loading model '{modelName}' from object path: {objectPath}");

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

            // Step 1: Download model if not cached
            bool downloadSuccess = false;
            string localZipPath = "";
            string downloadError = "";

            yield return assetManager.DownloadModel(objectPath, modelName, (success, result) =>
            {
                downloadSuccess = success;
                if (success)
                {
                    localZipPath = result;
                }
                else
                {
                    downloadError = result;
                }
            });

            if (!downloadSuccess)
            {
                Debug.LogError($"Failed to download model {modelName}: {downloadError}");
                onComplete?.Invoke(null, downloadError);
                yield break;
            }

            // Step 2: Load ZIP archive into memory
            ZipArchive archive = assetManager.LoadZipArchive(localZipPath);
            if (archive == null)
            {
                string error = $"Failed to load ZIP archive: {localZipPath}";
                Debug.LogError(error);
                onComplete?.Invoke(null, error);
                yield break;
            }

            // Step 3: Find and load RWX file from ZIP
            string rwxFileName = $"{modelName}.rwx";
            string rwxContent = assetManager.ReadTextFromZip(archive, rwxFileName);
            
            if (string.IsNullOrEmpty(rwxContent))
            {
                // Try alternative naming conventions
                rwxFileName = $"{modelName}.RWX";
                rwxContent = assetManager.ReadTextFromZip(archive, rwxFileName);
            }

            if (string.IsNullOrEmpty(rwxContent))
            {
                string error = $"RWX file not found in ZIP: {rwxFileName}";
                Debug.LogError(error);
                onComplete?.Invoke(null, error);
                yield break;
            }

            if (enableDebugLogs)
                Debug.Log($"Found RWX file: {rwxFileName} ({rwxContent.Length} characters)");

            // Step 4: Parse RWX content and create GameObject
            GameObject modelObject = null;
            try
            {
                modelObject = ParseRWXFromMemory(rwxContent, modelName, archive, objectPath);
            }
            catch (Exception e)
            {
                string error = $"Failed to parse RWX model: {e.Message}";
                Debug.LogError(error);
                onComplete?.Invoke(null, error);
                yield break;
            }

            // Cache the parsed prefab so future loads are instant
            if (modelObject != null)
            {
                CachePrefab(objectPath, modelName, modelObject);

                // Instantiate a live copy for the caller
                modelObject = Instantiate(modelObject, parentTransform);
                modelObject.name = modelName;
                modelObject.SetActive(true);
            }

            if (enableDebugLogs)
                Debug.Log($"Successfully loaded model: {modelName}");

            onComplete?.Invoke(modelObject, "Success");
        }

        /// <summary>
        /// Parses RWX content from memory and creates a GameObject
        /// </summary>
        private GameObject ParseRWXFromMemory(string rwxContent, string modelName, ZipArchive archive, string objectPath)
        {
            // Create root object
            GameObject rootObject = new GameObject(modelName);
            
            // Initialize parse context
            var context = new RWXParseContext();
            context.rootObject = rootObject;
            context.currentObject = rootObject;

            // Ensure components are initialized
            if (materialManager == null || meshBuilder == null || parser == null)
            {
                Debug.LogError("Components not properly initialized");
                return null;
            }

            // Set up material manager to load textures from remote
            materialManager.SetTextureSource(objectPath);

            // Reset parser state for new model
            parser?.Reset();

            // Parse RWX content line by line
            string[] lines = rwxContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            
            if (enableDebugLogs)
                Debug.Log($"Parsing {lines.Length} lines of RWX content");

            foreach (string line in lines)
            {
                parser.ProcessLine(line, context);
            }

            // Finalize mesh building
            meshBuilder.FinalCommit(context);

            if (enableDebugLogs)
            {
                Debug.Log($"Created model with {context.vertices.Count} vertices");
            }

            return rootObject;
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
            string rwxContent = assetManager.ReadTextFromZip(archive, rwxFileName);
            
            if (string.IsNullOrEmpty(rwxContent))
            {
                rwxFileName = $"{modelName}.RWX";
                rwxContent = assetManager.ReadTextFromZip(archive, rwxFileName);
            }

            if (string.IsNullOrEmpty(rwxContent))
            {
                Debug.LogError($"RWX file not found in ZIP: {rwxFileName}");
                return null;
            }

            return ParseRWXFromMemory(rwxContent, modelName, archive, null);
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
                });

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
