using System.IO;
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
                materialManager.SetTextureSource(objectPath);
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

            string[] lines = content.Split(new[] { '\n', '\r' }, System.StringSplitOptions.RemoveEmptyEntries);

            foreach (string line in lines)
            {
                parser.ProcessLine(line.Trim(), context);
            }

            // Commit any remaining mesh
            meshBuilder.FinalCommit(context);

            // Apply final scale (RWX uses decameter units)
            rootObject.transform.localScale = Vector3.one * 10f;

            return rootObject;
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
