using System.Collections;
using System.IO;
using RWXLoader;
using UnityEngine;

public class SingleObjectTroubleshooter : MonoBehaviour
{
    [Header("Model Source")]
    public string modelName = "gr-ashtree01";
    public string objectPathOverride = "";
    public string localRwxPath = "gr-ashtree01.rwx"; // optional: use local file if present

    [Header("Action String")]
    [TextArea(2, 4)]
    public string action = "create texture gr-ah-foilagesheet01.png tag=2";
    public string objectPathPassword = "";

    [Header("Debug")]
    public bool reloadOnStart = true;

    private RWXLoaderAdvanced loader;
    private GameObject currentInstance;

    private void Awake()
    {
        loader = GetComponent<RWXLoaderAdvanced>();
        if (loader == null)
        {
            loader = gameObject.AddComponent<RWXLoaderAdvanced>();
        }

        if (!string.IsNullOrEmpty(objectPathOverride))
        {
            loader.defaultObjectPath = objectPathOverride;
        }

        if (!string.IsNullOrEmpty(objectPathPassword))
        {
            loader.objectPathPassword = objectPathPassword;
        }
    }

    private void Start()
    {
        if (reloadOnStart)
        {
            StartCoroutine(LoadAndApply());
        }
    }

    public IEnumerator LoadAndApply()
    {
        // Clear previous instance
        if (currentInstance != null)
        {
            Destroy(currentInstance);
            currentInstance = null;
        }

        // Try local file first
        string rwxText = null;
        if (!string.IsNullOrEmpty(localRwxPath) && File.Exists(localRwxPath))
        {
            rwxText = File.ReadAllText(localRwxPath);
        }

        if (!string.IsNullOrEmpty(rwxText))
        {
            currentInstance = loader.LoadModelFromText(rwxText, modelName);
        }
        else
        {
            bool done = false;
            GameObject loaded = null;
            string err = null;
            loader.LoadModelFromRemote(modelName, objectPathOverride, (go, msg) =>
            {
                loaded = go;
                err = msg;
                done = true;
            }, objectPathPassword);

            while (!done) yield return null;
            currentInstance = loaded;

            if (currentInstance == null)
            {
                Debug.LogError($"Failed to load model '{modelName}': {err}");
                yield break;
            }
        }

        ApplyActions();
        DumpRendererTags();
    }

    private void ApplyActions()
    {
        if (string.IsNullOrWhiteSpace(action) || currentInstance == null) return;

        VpActionParser.Parse(action, out var create, out var activate);

        foreach (var cmd in create)
        {
            VpActionExecutor.ExecuteCreate(currentInstance, cmd, loader.defaultObjectPath, loader.objectPathPassword, this);
        }

        // No dedicated activate executor; reuse create path for activate-phase commands
        foreach (var cmd in activate)
        {
            VpActionExecutor.ExecuteCreate(currentInstance, cmd, loader.defaultObjectPath, loader.objectPathPassword, this);
        }
    }

    private void DumpRendererTags()
    {
        if (currentInstance == null) return;

        var renderers = currentInstance.GetComponentsInChildren<Renderer>(true);
        var block = new MaterialPropertyBlock();

        foreach (var r in renderers)
        {
            if (r == null) continue;
            r.GetPropertyBlock(block);
            int tag = block.GetInt(Shader.PropertyToID("_RWXTag"));
            if (tag == 0 && RWXRendererTagStore.TryGetTag(r, out var stored))
            {
                tag = stored;
            }
            Debug.Log($"[Troubleshooter] Renderer '{r.name}' tag={tag}");
        }
    }
}
