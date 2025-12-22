using System.Collections;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace RWXLoader
{
    public class RWXLoaderMain : MonoBehaviour
    {
        [Header("Settings")]
        public string textureFolder = "Textures";
        public string textureExtension = ".jpg";
        public bool enableTextures = true;
        public bool useStandardShader = true;
        public float alphaTest = 0.2f;

        [Header("Remote Loading")]
        public string remoteObjectPath = "";

        private RWXMaterialManager materialManager;
        private RWXMeshBuilder meshBuilder;
        private RWXParser parser;

        private void Awake()
        {
            InitializeComponents();
        }

        private void InitializeComponents()
        {
            // Get or create the material manager component
            materialManager = GetComponent<RWXMaterialManager>();
            if (materialManager == null)
            {
                materialManager = gameObject.AddComponent<RWXMaterialManager>();
            }

            // Configure material manager settings
            materialManager.enableTextures = enableTextures;
            materialManager.useStandardShader = useStandardShader;
            materialManager.alphaTest = alphaTest;

            // Set up texture loader settings through the material manager's texture loader
            if (materialManager.textureLoader != null)
            {
                materialManager.textureLoader.textureFolder = textureFolder;
                materialManager.textureLoader.textureExtension = textureExtension;
            }

            // Set remote object path if specified
            if (!string.IsNullOrEmpty(remoteObjectPath))
            {
                SetRemoteObjectPath(remoteObjectPath);
            }

            meshBuilder = new RWXMeshBuilder(materialManager);
            parser = new RWXParser(meshBuilder);
        }

        private void Start()
        {
            // Ensure components are properly initialized after Start
            if (materialManager.textureLoader != null)
            {
                materialManager.textureLoader.textureFolder = textureFolder;
                materialManager.textureLoader.textureExtension = textureExtension;
            }
        }

        public void SetRemoteObjectPath(string objectPath)
        {
            remoteObjectPath = objectPath;
            if (materialManager != null)
            {
                materialManager.SetTextureSource(objectPath, null);
            }
        }

        public GameObject LoadRWXFile(string filePath)
        {
            if (!File.Exists(filePath))
            {
                Debug.LogError($"RWX file not found: {filePath}");
                return null;
            }

            string content = File.ReadAllText(filePath);
            return ParseRWX(Path.GetFileNameWithoutExtension(filePath), content);
        }

        /// <summary>
        /// Reads and parses an RWX file without blocking the main Unity thread.
        /// Heavy I/O and string handling are done on a background worker, while
        /// parsing is spread across multiple frames on the main thread.
        /// </summary>
        /// <param name="filePath">Absolute path to the RWX file.</param>
        /// <param name="onComplete">Invoked with the loaded GameObject when finished.</param>
        /// <param name="linesPerFrame">How many lines to process per frame.</param>
        /// <returns>Coroutine that yields until parsing is complete.</returns>
        public IEnumerator LoadRWXFileAsync(string filePath, System.Action<GameObject> onComplete = null, int linesPerFrame = 250)
        {
            if (!File.Exists(filePath))
            {
                Debug.LogError($"RWX file not found: {filePath}");
                yield break;
            }

            string content = null;

            // Offload file I/O to a background thread to keep the main loop responsive.
            var readTask = Task.Run(() => File.ReadAllText(filePath));
            while (!readTask.IsCompleted)
            {
                yield return null;
            }

            if (readTask.IsFaulted)
            {
                Debug.LogError($"Failed to read RWX file: {readTask.Exception}");
                yield break;
            }

            content = readTask.Result;
            yield return ParseRWXCoroutine(Path.GetFileNameWithoutExtension(filePath), content, onComplete, linesPerFrame);
        }

        public GameObject LoadRWXFromPersistentData(string fileName)
        {
            string filePath = Path.Combine(Application.persistentDataPath, "Models", fileName);
            return LoadRWXFile(filePath);
        }

        public GameObject ParseRWX(string name, string content)
        {
            var rootObject = new GameObject(name);
            var context = new RWXParseContext
            {
                rootObject = rootObject,
                currentObject = rootObject,
                currentMaterial = new RWXMaterial()
            };

            using (var reader = new StringReader(content))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (string.IsNullOrEmpty(line))
                    {
                        continue;
                    }

                    parser.ProcessLine(line, context);
                }
            }

            // Commit any remaining mesh
            meshBuilder.FinalCommit(context);

            // Apply final scale (RWX uses decameter units)
            rootObject.transform.localScale = Vector3.one * 10f;

            return rootObject;
        }

        private IEnumerator ParseRWXCoroutine(string name, string content, System.Action<GameObject> onComplete, int linesPerFrame)
        {
            var rootObject = new GameObject(name);
            var context = new RWXParseContext
            {
                rootObject = rootObject,
                currentObject = rootObject,
                currentMaterial = new RWXMaterial()
            };

            using (var reader = new StringReader(content))
            {
                string line;
                int processedThisFrame = 0;

                while ((line = reader.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (!string.IsNullOrEmpty(line))
                    {
                        parser.ProcessLine(line, context);
                    }

                    processedThisFrame++;
                    if (processedThisFrame >= linesPerFrame)
                    {
                        processedThisFrame = 0;
                        yield return null; // Let the main loop breathe
                    }
                }
            }

            meshBuilder.FinalCommit(context);
            rootObject.transform.localScale = Vector3.one * 10f;

            onComplete?.Invoke(rootObject);
        }

        public void ClearCache()
        {
            materialManager?.ClearCache();
        }

        private void OnDestroy()
        {
            ClearCache();
        }

        // Helper method to get the persistent data paths
        public string GetModelsPath()
        {
            return Path.Combine(Application.persistentDataPath, "Models");
        }

        public string GetTexturesPath()
        {
            return Path.Combine(Application.persistentDataPath, "Textures");
        }

        // Editor helper methods
        [ContextMenu("Load Test RWX")]
        public void LoadTestRWX()
        {
            string testPath = Path.Combine(Application.persistentDataPath, "Models", "couch1a.rwx");
            if (File.Exists(testPath))
            {
                GameObject loadedObject = LoadRWXFile(testPath);
                if (loadedObject != null)
                {
                    loadedObject.transform.SetParent(transform);
                }
            }
            else
            {
                Debug.LogError($"Test RWX file not found: {testPath}");
            }
        }

        [ContextMenu("Show Persistent Data Path")]
        public void ShowPersistentDataPath()
        {
            Debug.Log($"Persistent Data Path: {Application.persistentDataPath}");
            Debug.Log($"Models should be in: {GetModelsPath()}");
            Debug.Log($"Textures should be in: {GetTexturesPath()}");
        }

        // Update settings at runtime
        public void UpdateSettings()
        {
            if (materialManager != null)
            {
                materialManager.enableTextures = enableTextures;
                materialManager.useStandardShader = useStandardShader;
                materialManager.alphaTest = alphaTest;

                if (materialManager.textureLoader != null)
                {
                    materialManager.textureLoader.textureFolder = textureFolder;
                    materialManager.textureLoader.textureExtension = textureExtension;
                }
            }
        }

        // Validate settings in editor
        private void OnValidate()
        {
            if (Application.isPlaying)
            {
                UpdateSettings();
            }
        }
    }
}
