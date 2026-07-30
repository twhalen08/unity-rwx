using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using UnityEngine;
using Unity.Profiling;

namespace RWXLoader
{
    public class RWXLoaderAdvanced : MonoBehaviour
    {
        [Header("RWX Loading")]
        public string defaultObjectPath = "http://objects.virtualparadise.org/vpbuild/";
        public string objectPathPassword = "";
        public Transform parentTransform;

        [Header("Incremental model construction")]
        [Tooltip("Maximum main-thread construction time per frame. Zero disables the time limit.")]
        public float buildTimeBudgetMs = 2.5f;
        [Tooltip("Maximum parsed vertices consumed per frame. Zero disables the limit.")]
        public int buildVertexBudget = 4000;
        [Tooltip("Maximum parsed triangles consumed per frame. Zero disables the limit.")]
        public int buildTriangleBudget = 4000;
        
        [Header("Debug")]
        public bool enableDebugLogs = true;
        
        private RWXParser parser;
        private RWXMeshBuilder meshBuilder;
        private RWXMaterialManager materialManager;
        private RWXAssetManager assetManager;

        private static readonly Dictionary<string, GameObject> modelPrefabCache = new();
        private static Transform cacheContainer;
        private static readonly ProfilerMarker ArchiveResolutionMarker = new ProfilerMarker("RWX.ArchiveResolution");
        private static readonly ProfilerMarker TokenizationMarker = new ProfilerMarker("RWX.Tokenization");
        private static readonly ProfilerMarker IntermediateMarker = new ProfilerMarker("RWX.IntermediateGeneration");
        private static readonly ProfilerMarker MeshMarker = new ProfilerMarker("RWX.MeshGeneration");
        private static readonly ProfilerMarker MaterialMarker = new ProfilerMarker("RWX.MaterialBinding");
        private static readonly ProfilerMarker InstantiationMarker = new ProfilerMarker("RWX.Instantiation");

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
                using (InstantiationMarker.Auto()) instance = Instantiate(prefab, parentTransform);
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

            var modelSource = new VirtualParadiseHttpZipSource(assetManager, objectPath, password, enableDebugLogs);

            GameObject modelObject = null;
            string sourceError = null;
            yield return LoadModelFromSourceCoroutine(modelSource, modelName, (loadedObject, error) =>
            {
                modelObject = loadedObject;
                sourceError = error;
            });

            if (!string.IsNullOrEmpty(sourceError))
            {
                onComplete?.Invoke(null, sourceError);
                yield break;
            }

            // Cache the parsed prefab so future loads are instant
            if (modelObject != null)
            {
                CachePrefab(objectPath, modelName, modelObject);

                // Instantiate a live copy for the caller
                using (InstantiationMarker.Auto()) modelObject = Instantiate(modelObject, parentTransform);
                modelObject.name = modelName;
                modelObject.SetActive(activateOnInstantiate);
            }

            if (enableDebugLogs)
                Debug.Log($"Successfully loaded model: {modelName}");

            onComplete?.Invoke(modelObject, "Success");
        }

        /// <summary>
        /// Parses RWX content from memory and creates a GameObject
        /// </summary>
        private IEnumerator LoadModelFromSourceCoroutine(
            IRwxModelSource modelSource,
            string modelName,
            Action<GameObject, string> onComplete)
        {
            bool resolveSuccess = false;
            RwxModelPayload payload = null;
            string resolveResult = string.Empty;

            ArchiveResolutionMarker.Begin();
            yield return modelSource.ResolveModelPayload(modelName, (success, resolvedPayload, message) =>
            {
                resolveSuccess = success;
                payload = resolvedPayload;
                resolveResult = message;
            });
            ArchiveResolutionMarker.End();

            if (!resolveSuccess || payload == null)
            {
                string error = $"Failed to resolve model payload for {modelName}: {resolveResult}";
                Debug.LogError(error);
                onComplete?.Invoke(null, resolveResult);
                yield break;
            }

            if (enableDebugLogs)
            {
                Debug.Log($"Resolved RWX payload for {modelName} ({payload.RwxContent.Length} characters)");
            }

            // Tokenization and intermediate generation are CLR-only and never touch a
            // UnityEngine.Object, so streamed models can safely use the thread pool.
            Task<RWXParsedModel> parseTask = Task.Run(() =>
            {
                using (TokenizationMarker.Auto())
                using (IntermediateMarker.Auto())
                    return new RWXIntermediateParser().ParseContent(payload.RwxContent);
            });
            while (!parseTask.IsCompleted) yield return null;
            if (parseTask.IsFaulted)
            {
                string error = $"Failed to parse RWX model: {parseTask.Exception?.GetBaseException().Message}";
                Debug.LogError(error);
                onComplete?.Invoke(null, error);
                yield break;
            }

            payload.ConfigureMaterialManager(materialManager);
            GameObject modelObject = null;
            string buildError = null;
            yield return BuildParsedModelCoroutine(parseTask.Result, modelName, (go, error) => { modelObject = go; buildError = error; });
            if (!string.IsNullOrEmpty(buildError)) { onComplete?.Invoke(null, buildError); yield break; }

            onComplete?.Invoke(modelObject, null);
        }

        private GameObject ParseModelFromPayload(RwxModelPayload payload, string modelName)
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

            payload.ConfigureMaterialManager(materialManager);

            // Reset parser state for new model
            parser?.Reset();

            var commands = parser.ParseToIntermediate(payload.RwxContent);

            if (enableDebugLogs)
                Debug.Log($"Parsing {commands.Count} RWX commands");

            parser.ApplyIntermediateCommands(commands, context);

            if (enableDebugLogs)
            {
                Debug.Log($"Created model with {context.vertices.Count} vertices");
            }

            return rootObject;
        }

        /// <summary>Main-thread stage. Commands are consumed in bounded batches.</summary>
        private IEnumerator BuildParsedModelCoroutine(RWXParsedModel model, string modelName, Action<GameObject, string> onComplete)
        {
            if (materialManager == null || meshBuilder == null || parser == null)
            {
                onComplete(null, "Components not properly initialized");
                yield break;
            }

            GameObject root;
            using (InstantiationMarker.Auto()) root = new GameObject(modelName);
            var context = new RWXParseContext { rootObject = root, currentObject = root };
            parser.Reset();
            float sliceStart = Time.realtimeSinceStartup;
            int vertices = 0, triangles = 0;
            for (int i = 0; i < model.Commands.Count; i++)
            {
                RWXParsedCommand command = model.Commands[i];
                ProfilerMarker marker = command.Type == RWXCommandType.Material ? MaterialMarker : MeshMarker;
                using (marker.Auto()) parser.ApplyIntermediateCommand(command, context);
                vertices += command.EstimatedVertices;
                triangles += command.EstimatedTriangles;
                float elapsed = (Time.realtimeSinceStartup - sliceStart) * 1000f;
                if ((buildTimeBudgetMs > 0 && elapsed >= buildTimeBudgetMs) ||
                    (buildVertexBudget > 0 && vertices >= buildVertexBudget) ||
                    (buildTriangleBudget > 0 && triangles >= buildTriangleBudget))
                {
                    yield return null;
                    sliceStart = Time.realtimeSinceStartup;
                    vertices = triangles = 0;
                }
            }
            using (MeshMarker.Auto()) parser.FinalizeIntermediateModel(context);
            onComplete(root, null);
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

            var zipSource = new ZipModelSource(assetManager, zipPath, objectPathPassword, enableDebugLogs: enableDebugLogs);
            if (!zipSource.TryResolveModelPayload(modelName, out var payload, out var message) || payload == null)
            {
                Debug.LogError(message);
                return null;
            }

            try
            {
                return ParseModelFromPayload(payload, modelName);
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to parse RWX model: {e.Message}");
                return null;
            }
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
