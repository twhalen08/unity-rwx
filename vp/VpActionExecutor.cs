using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using RWXLoader;
using UnityEngine;

public static class VpActionExecutor
{
    private const float VpShearToUnity = 0.5f;

    private static readonly Dictionary<string, Texture2D> _textureCache = new Dictionary<string, Texture2D>();
    private static readonly int _MainTexId = Shader.PropertyToID("_MainTex");
    private static readonly int _BaseMapId = Shader.PropertyToID("_BaseMap");
    private static readonly int _RwxTagId = Shader.PropertyToID("_RWXTag");

    /// <summary>
    /// Convenience wrapper (optional) so older call sites can use Execute(...)
    /// </summary>
    public static void Execute(GameObject target, VpActionCommand cmd, string objectPath, string password, MonoBehaviour host)
    {
        ExecuteCreate(target, cmd, objectPath, password, host);
    }

    public static void ApplyAmbient(GameObject target, float ambient)
    {
        if (target == null) return;

        ambient = Mathf.Clamp01(ambient);

        foreach (var r in target.GetComponentsInChildren<Renderer>(true))
        {
            foreach (var m in r.materials)
            {
                if (m == null) continue;

                if (m.HasProperty("_Color"))
                {
                    Color c = m.color;
                    m.color = new Color(c.r * ambient, c.g * ambient, c.b * ambient, c.a);
                }
                else if (m.HasProperty("_BaseColor"))
                {
                    Color c = m.GetColor("_BaseColor");
                    m.SetColor("_BaseColor", new Color(c.r * ambient, c.g * ambient, c.b * ambient, c.a));
                }
            }
        }
    }

    public static void ApplyDiffuse(GameObject target, float diffuse)
    {
        if (target == null) return;

        diffuse = Mathf.Max(0f, diffuse);

        foreach (var r in target.GetComponentsInChildren<Renderer>(true))
        {
            foreach (var m in r.materials)
            {
                if (m == null) continue;

                if (m.HasProperty("_Color"))
                {
                    Color c = m.color;
                    m.color = new Color(
                        Mathf.Clamp01(c.r * diffuse),
                        Mathf.Clamp01(c.g * diffuse),
                        Mathf.Clamp01(c.b * diffuse),
                        c.a
                    );
                }
                else if (m.HasProperty("_BaseColor"))
                {
                    Color c = m.GetColor("_BaseColor");
                    m.SetColor("_BaseColor", new Color(
                        Mathf.Clamp01(c.r * diffuse),
                        Mathf.Clamp01(c.g * diffuse),
                        Mathf.Clamp01(c.b * diffuse),
                        c.a
                    ));
                }
            }
        }
    }

    public static void ApplyVisible(GameObject target, bool visible)
    {
        if (target == null) return;

        foreach (var r in target.GetComponentsInChildren<Renderer>(true))
            r.enabled = visible;
    }

    public static void ApplyScale(GameObject target, Vector3 scale)
    {
        if (target == null) return;

        const float MinScale = 0.1f;
        target.transform.localScale = new Vector3(
            Mathf.Max(MinScale, scale.x),
            Mathf.Max(MinScale, scale.y),
            Mathf.Max(MinScale, scale.z));
    }

    public static void ApplyShear(GameObject target, float zPlus, float xPlus, float yPlus, float yMinus, float zMinus, float xMinus)
    {
        ApplyShearToAllMeshes_VpMatrix_ObjectLocal(target, zPlus, xPlus, yPlus, yMinus, zMinus, xMinus);
    }

    public static void ExecuteCreate(GameObject target, VpActionCommand cmd, string objectPath, string password, MonoBehaviour host)
    {
        if (target == null || cmd == null) return;

        switch (cmd.verb)
        {
            case "texture":
                ExecuteTexture(target, cmd, objectPath, password, host);
                break;

            case "normalmap":
                ExecuteNormalMap(target, cmd, objectPath, password, host);
                break;

            case "ambient":
                ExecuteAmbient(target, cmd);
                break;

            case "diffuse":
                ExecuteDiffuse(target, cmd);
                break;

            case "light":
                ExecuteLight(target, cmd);
                break;

            case "scale":
                ExecuteScale(target, cmd);
                break;

            case "visible":
                ExecuteVisible(target, cmd);
                break;

            case "shear":
                ExecuteShear(target, cmd);
                break;
        }
    }

    // -------------------------
    // TEXTURE
    // -------------------------
    private static void ExecuteTexture(GameObject target, VpActionCommand cmd, string objectPath, string password, MonoBehaviour host)
    {
        if (host == null)
        {
            Debug.LogWarning("[VP] Cannot run texture action: coroutine host is null.");
            return;
        }

        string tex = null;
        int? tagFilter = null;

        if (cmd.positional != null && cmd.positional.Count > 0)
            tex = cmd.positional[0];

        if (string.IsNullOrWhiteSpace(tex) && cmd.kv != null)
        {
            if (cmd.kv.TryGetValue("name", out var name))
                tex = name;
            else if (cmd.kv.TryGetValue("texture", out var t))
                tex = t;
        }

        if (cmd.kv != null && tagFilter == null && cmd.kv.TryGetValue("tag", out var tagString) && int.TryParse(tagString, out var parsedTag))
        {
            tagFilter = parsedTag;
        }

        if (string.IsNullOrWhiteSpace(tex))
        {
            Debug.LogWarning($"[VP] texture action missing texture name: {cmd.raw}");
            return;
        }

        host.StartCoroutine(ApplyTextureCoroutine(target, tex.Trim(), objectPath, password, host, tagFilter));
    }

    private static IEnumerator ApplyTextureCoroutine(GameObject target, string textureName, string objectPath, string password, MonoBehaviour host, int? tagFilter)
    {
        if (RWXAssetManager.Instance == null)
        {
            var go = new GameObject("RWX Asset Manager");
            go.AddComponent<RWXAssetManager>();
        }

        var assetMgr = RWXAssetManager.Instance;
        if (assetMgr == null)
        {
            Debug.LogError("[VP] RWXAssetManager missing.");
            yield break;
        }

        // ---- Cache check ----
        string cacheKey = MakeTextureCacheKey(objectPath, textureName);
        if (_textureCache.TryGetValue(cacheKey, out var cachedTex) && cachedTex != null)
        {
            ApplyTextureToAllRenderers(target, cachedTex, tagFilter);
            yield break;
        }

        // candidates (NOW includes DDS / DDS.GZ)
        List<string> candidates = BuildTextureCandidates(textureName);

        string localPath = null;
        string lastError = null;

        foreach (var candidate in candidates)
        {
            bool done = false;
            bool ok = false;
            string result = null;

            yield return assetMgr.DownloadTexture(objectPath, candidate, (success, res) =>
            {
                ok = success;
                result = res;
                done = true;
            }, password);

            while (!done)
                yield return null;

            if (ok && File.Exists(result))
            {
                localPath = result;
                break;
            }

            lastError = result;
        }

        if (string.IsNullOrEmpty(localPath))
        {
            Debug.LogWarning($"[VP] texture '{textureName}' download failed. Last error: {lastError}");
            yield break;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(localPath);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[VP] texture read failed '{localPath}': {e.Message}");
            yield break;
        }

        // ---------- IMPORTANT: DDS must NOT use Texture2D.LoadImage ----------
        string ext = Path.GetExtension(localPath).ToLowerInvariant();
        bool isDds = ext == ".dds" || localPath.EndsWith(".dds.gz", StringComparison.OrdinalIgnoreCase);

        Texture2D tex = null;

        if (isDds)
        {
            // Ensure we have a RWXTextureLoader component to decode DDS
            RWXTextureLoader loader = host.GetComponent<RWXTextureLoader>();
            if (loader == null) loader = host.gameObject.AddComponent<RWXTextureLoader>();

            // Pass filename so loader knows if it’s .dds.gz and will gzip-decompress
            string fileName = Path.GetFileName(localPath);

            tex = loader.LoadTextureFromBytes(bytes, fileName, isMask: false, isDoubleSided: false);

            if (tex == null)
            {
                // Extra hint: are we even looking at DDS bytes?
                string sig4 = bytes.Length >= 4 ? System.Text.Encoding.ASCII.GetString(bytes, 0, 4) : "????";
                Debug.LogWarning($"[VP] DDS decode failed for '{textureName}' at '{localPath}' sig='{sig4}' len={bytes.Length}. Check DDSDBG logs from RWXTextureLoader.");
                yield break;
            }
        }
        else
        {
            // Normal images (jpg/png/bmp...) can use Unity decode
            tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: true);
            if (!tex.LoadImage(bytes))
            {
                Debug.LogWarning($"[VP] Unity couldn't decode texture '{textureName}' at '{localPath}'");
                yield break;
            }
        }

        tex.name = Path.GetFileNameWithoutExtension(localPath);

        tex.wrapMode = TextureWrapMode.Repeat;
        tex.filterMode = FilterMode.Bilinear;
        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;

        _textureCache[cacheKey] = tex;

        ApplyTextureToAllRenderers(target, tex, tagFilter);

        Debug.Log($"[VP] Applied texture '{tex.name}' to instance '{target.name}' (cachedKey='{cacheKey}')");
    }

    private static List<string> BuildTextureCandidates(string textureName)
    {
        var list = new List<string>();

        void Add(string s)
        {
            if (!string.IsNullOrWhiteSpace(s) && !list.Contains(s))
                list.Add(s);
        }

        string t = textureName.Trim();
        Add(t);

        string ext = Path.GetExtension(t);
        if (!string.IsNullOrEmpty(ext))
            return list;

        Add(t + ".jpg");
        Add(t + ".jpeg");
        Add(t + ".png");
        Add(t + ".bmp");

        Add(t + ".dds");
        Add(t + ".dds.gz");

        Add(t + ".JPG");
        Add(t + ".PNG");
        Add(t + ".DDS");
        Add(t + ".DDS.GZ");

        return list;
    }

    private static void ApplyTextureToAllRenderers(GameObject root, Texture2D tex, int? tagFilter)
    {
        if (root == null || tex == null) return;

        var renderers = root.GetComponentsInChildren<Renderer>(includeInactive: true);
        var block = new MaterialPropertyBlock();

        foreach (var r in renderers)
        {
            if (r == null) continue;

            block.Clear();
            r.GetPropertyBlock(block);

            int rendererTag = block.GetInt(_RwxTagId);
            if (rendererTag == 0 && RWXRendererTagStore.TryGetTag(r, out var storedTag))
            {
                rendererTag = storedTag;
            }

            if (tagFilter.HasValue && rendererTag != tagFilter.Value)
                continue;

            block.SetTexture(_MainTexId, tex);
            block.SetTexture(_BaseMapId, tex);
            if (rendererTag != 0)
            {
                block.SetInt(_RwxTagId, rendererTag);
            }

            r.SetPropertyBlock(block);
        }
    }

    // -------------------------
    // NORMALMAP
    // -------------------------
    private static void ExecuteNormalMap(GameObject target, VpActionCommand cmd, string objectPath, string password, MonoBehaviour host)
    {
        if (host == null)
        {
            Debug.LogWarning("[VP] Cannot run normalmap action: coroutine host is null.");
            return;
        }

        if (cmd.positional == null || cmd.positional.Count == 0)
        {
            Debug.LogWarning($"[VP] normalmap missing filename: {cmd.raw}");
            return;
        }

        string normalName = cmd.positional[0];
        host.StartCoroutine(ApplyNormalMapCoroutine(target, normalName, objectPath, password, host));
    }

    private static IEnumerator ApplyNormalMapCoroutine(GameObject target, string textureName, string objectPath, string password, MonoBehaviour host)
    {
        var assetMgr = RWXAssetManager.Instance;
        if (assetMgr == null)
            yield break;

        List<string> candidates = BuildTextureCandidates(textureName);

        string localPath = null;

        foreach (var c in candidates)
        {
            bool done = false;
            bool ok = false;
            string result = null;

            yield return assetMgr.DownloadTexture(objectPath, c, (success, res) =>
            {
                ok = success;
                result = res;
                done = true;
            }, password);

            while (!done) yield return null;

            if (ok && File.Exists(result))
            {
                localPath = result;
                break;
            }
        }

        if (localPath == null)
        {
            Debug.LogWarning($"[VP] normalmap '{textureName}' not found");
            yield break;
        }

        byte[] bytes = File.ReadAllBytes(localPath);

        string ext = Path.GetExtension(localPath).ToLowerInvariant();
        bool isDds = ext == ".dds" || localPath.EndsWith(".dds.gz", StringComparison.OrdinalIgnoreCase);

        Texture2D tex = null;

        if (isDds)
        {
            RWXTextureLoader loader = host.GetComponent<RWXTextureLoader>();
            if (loader == null) loader = host.gameObject.AddComponent<RWXTextureLoader>();

            string fileName = Path.GetFileName(localPath);

            tex = loader.LoadTextureFromBytes(bytes, fileName, isMask: false, isDoubleSided: false);
            if (tex == null)
            {
                string sig4 = bytes.Length >= 4 ? System.Text.Encoding.ASCII.GetString(bytes, 0, 4) : "????";
                Debug.LogWarning($"[VP] DDS normalmap decode failed '{textureName}' at '{localPath}' sig='{sig4}' len={bytes.Length}");
                yield break;
            }
        }
        else
        {
            tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
            if (!tex.LoadImage(bytes))
            {
                Debug.LogWarning($"[VP] Unity couldn't decode normalmap '{textureName}' at '{localPath}'");
                yield break;
            }
        }

        tex.name = Path.GetFileNameWithoutExtension(localPath);
        tex.Apply(true, false);

        ApplyNormalMapToRenderers(target, tex);
    }

    private static void ApplyNormalMapToRenderers(GameObject root, Texture2D normal)
    {
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
        {
            foreach (var m in r.materials)
            {
                if (m == null) continue;

                if (m.HasProperty("_BumpMap"))
                {
                    m.EnableKeyword("_NORMALMAP");
                    m.SetTexture("_BumpMap", normal);
                }
            }
        }
    }

    // -------------------------
    // AMBIENT / DIFFUSE
    // -------------------------
    private static void ExecuteAmbient(GameObject target, VpActionCommand cmd)
    {
        if (cmd.positional == null || cmd.positional.Count == 0) return;
        float ambient = ParseFloat(cmd.positional[0], 1f);
        ApplyAmbient(target, ambient);
    }

    private static void ExecuteDiffuse(GameObject target, VpActionCommand cmd)
    {
        if (cmd.positional == null || cmd.positional.Count == 0) return;
        float diffuse = ParseFloat(cmd.positional[0], 1f);
        ApplyDiffuse(target, diffuse);
    }

    // -------------------------
    // LIGHT
    // -------------------------
    private static void ExecuteLight(GameObject target, VpActionCommand cmd)
    {
        if (target == null || cmd == null) return;

        Color color = ParseColor(GetValue(cmd, "color"), Color.white);
        float radius = Mathf.Max(0.01f, GetFloat(cmd, "radius", 10f));
        float brightness = Mathf.Max(0.01f, GetFloat(cmd, "brightness", 0.5f));
        float fxTime = Mathf.Max(0.01f, GetFloat(cmd, "time", 1f));
        float maxDistance = Mathf.Max(0f, GetFloat(cmd, "maxdist", 1000f));
        float spotAngle = Mathf.Max(0.01f, GetFloat(cmd, "angle", 45f));
        string fx = GetValue(cmd, "fx")?.ToLowerInvariant();
        string type = GetValue(cmd, "type")?.ToLowerInvariant();

        var lightObj = new GameObject("vp-light");
        lightObj.transform.SetParent(target.transform, worldPositionStays: false);

        var light = lightObj.AddComponent<Light>();
        light.type = type == "spot" ? LightType.Spot : LightType.Point;
        light.range = radius;
        light.color = color;
        light.intensity = brightness;

        if (light.type == LightType.Spot)
        {
            light.spotAngle = spotAngle;
            lightObj.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        }

        if (!string.IsNullOrWhiteSpace(fx) || maxDistance > 0f)
        {
            var effect = lightObj.AddComponent<VpLightEffect>();
            effect.Initialize(light, fx, brightness, fxTime, maxDistance, color);
        }
    }

    // -------------------------
    // SCALE
    // -------------------------
    private static void ExecuteScale(GameObject target, VpActionCommand cmd)
    {
        if (target == null) return;

        const float MinScale = 0.1f;

        float x = 1f, y = 1f, z = 1f;

        if (cmd.positional == null || cmd.positional.Count == 0)
            return;

        if (cmd.positional.Count == 1)
        {
            float s = Mathf.Max(MinScale, ParseFloat(cmd.positional[0], 1f));
            x = y = z = s;
        }
        else if (cmd.positional.Count >= 3)
        {
            x = Mathf.Max(MinScale, ParseFloat(cmd.positional[0], 1f));
            y = Mathf.Max(MinScale, ParseFloat(cmd.positional[1], 1f));
            z = Mathf.Max(MinScale, ParseFloat(cmd.positional[2], 1f));
        }
        else
        {
            x = Mathf.Max(MinScale, ParseFloat(cmd.positional[0], 1f));
            y = Mathf.Max(MinScale, ParseFloat(cmd.positional[1], 1f));
            z = 1f;
        }

        target.transform.localScale = new Vector3(x, y, z);
    }

    private static float ParseFloat(string s, float fallback)
    {
        if (string.IsNullOrWhiteSpace(s))
            return fallback;

        if (float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
            return v;

        if (float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out v))
            return v;

        return fallback;
    }

    private static float GetFloat(VpActionCommand cmd, string key, float fallback)
    {
        if (cmd == null || cmd.kv == null)
            return fallback;

        if (!cmd.kv.TryGetValue(key, out string s) || string.IsNullOrWhiteSpace(s))
            return fallback;

        return ParseFloat(s, fallback);
    }

    private static string GetValue(VpActionCommand cmd, string key)
    {
        if (cmd == null || cmd.kv == null)
            return null;

        if (cmd.kv.TryGetValue(key, out string val))
            return val;

        return null;
    }

    private static Color ParseColor(string s, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(s))
            return fallback;

        string trimmed = s.Trim();

        if (ColorUtility.TryParseHtmlString(trimmed, out Color c))
            return c;

        if (!trimmed.StartsWith("#") && (trimmed.Length == 6 || trimmed.Length == 8))
        {
            if (ColorUtility.TryParseHtmlString("#" + trimmed, out c))
                return c;
        }

        if (TryParseRgbList(trimmed, out c))
            return c;

        return fallback;
    }

    private static bool TryParseRgbList(string s, out Color color)
    {
        color = Color.white;

        var parts = s.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 && parts.Length != 4)
            return false;

        float[] vals = new float[parts.Length];
        bool anyAboveOne = false;

        for (int i = 0; i < parts.Length; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float v) &&
                !float.TryParse(parts[i], NumberStyles.Float, CultureInfo.CurrentCulture, out v))
            {
                return false;
            }

            vals[i] = v;
            if (v > 1f)
                anyAboveOne = true;
        }

        float scale = anyAboveOne ? 1f / 255f : 1f;
        float r = vals[0] * scale;
        float g = vals[1] * scale;
        float b = vals[2] * scale;
        float a = (parts.Length == 4 ? vals[3] * scale : 1f);

        color = new Color(r, g, b, a);
        return true;
    }

    // -------------------------
    // VISIBLE
    // -------------------------
    private static void ExecuteVisible(GameObject target, VpActionCommand cmd)
    {
        if (target == null) return;

        if (cmd.positional == null || cmd.positional.Count == 0)
            return;

        string v = cmd.positional[0].Trim().ToLowerInvariant();

        bool visible =
            v == "yes" ||
            v == "true" ||
            v == "1" ||
            v == "on";

        ApplyVisible(target, visible);
    }

    // -------------------------
    // SHEAR
    // -------------------------
    private static void ExecuteShear(GameObject target, VpActionCommand cmd)
    {
        if (target == null || cmd == null)
            return;

        float zPlus = GetPosFloat(cmd, 0, 0f);
        float xPlus = GetPosFloat(cmd, 1, 0f);
        float yPlus = GetPosFloat(cmd, 2, 0f);
        float yMinus = GetPosFloat(cmd, 3, 0f);
        float zMinus = GetPosFloat(cmd, 4, 0f);
        float xMinus = GetPosFloat(cmd, 5, 0f);

        ApplyShear(target, zPlus, xPlus, yPlus, yMinus, zMinus, xMinus);
    }

    private static void ApplyShearToAllMeshes_VpMatrix_ObjectLocal(
        GameObject root,
        float zPlus, float xPlus, float yPlus,
        float yMinus, float zMinus, float xMinus)
    {
        if (root == null) return;

        var filters = root.GetComponentsInChildren<MeshFilter>(includeInactive: true);
        if (filters == null || filters.Length == 0) return;

        for (int f = 0; f < filters.Length; f++)
        {
            var mf = filters[f];
            if (mf == null) continue;

            var shared = mf.sharedMesh;
            if (shared == null) continue;

            var mesh = UnityEngine.Object.Instantiate(shared);
            mesh.name = shared.name + "_sheared";
            mf.sharedMesh = mesh;

            Matrix4x4 meshLocalToRootLocal =
                root.transform.worldToLocalMatrix * mf.transform.localToWorldMatrix;

            Matrix4x4 rootLocalToMeshLocal =
                mf.transform.worldToLocalMatrix * root.transform.localToWorldMatrix;

            ApplyShearToMesh_VpMatrix_ObjectLocal(
                mesh,
                meshLocalToRootLocal,
                rootLocalToMeshLocal,
                zPlus, xPlus, yPlus,
                yMinus, zMinus, xMinus
            );
        }
    }

    private static void ApplyShearToMesh_VpMatrix_ObjectLocal(
        Mesh mesh,
        Matrix4x4 meshLocalToRootLocal,
        Matrix4x4 rootLocalToMeshLocal,
        float zPlus, float xPlus, float yPlus,
        float yMinus, float zMinus, float xMinus)
    {
        var verts = mesh.vertices;
        if (verts == null || verts.Length == 0)
            return;

        for (int i = 0; i < verts.Length; i++)
        {
            Vector3 p = meshLocalToRootLocal.MultiplyPoint3x4(verts[i]);

            float x0 = -p.x;
            float y0 = p.y;
            float z0 = p.z;

            float x1 = x0 + (xPlus * z0) - (xMinus * y0);
            float y1 = y0 + (yPlus * x0) - (yMinus * z0);
            float z1 = z0 + (zPlus * y0) - (zMinus * x0);

            p.x = -x1;
            p.y = y1;
            p.z = z1;

            verts[i] = rootLocalToMeshLocal.MultiplyPoint3x4(p);
        }

        mesh.vertices = verts;
        mesh.RecalculateBounds();
        mesh.RecalculateNormals();
        mesh.RecalculateTangents();
    }

    private static float GetPosFloat(VpActionCommand cmd, int index, float fallback)
    {
        if (cmd.positional == null || cmd.positional.Count <= index)
            return fallback;

        string s = cmd.positional[index];
        if (string.IsNullOrWhiteSpace(s))
            return fallback;

        if (float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
            return v;

        if (float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out v))
            return v;

        return fallback;
    }

    private static string MakeTextureCacheKey(string objectPath, string textureName)
    {
        string op = (objectPath ?? string.Empty).Trim().TrimEnd('/').ToLowerInvariant();
        string tn = (textureName ?? string.Empty).Trim().ToLowerInvariant();
        return op + "||" + tn;
    }
}

public class VpLightEffect : MonoBehaviour
{
    private Light _light;
    private string _effect;
    private float _brightness;
    private float _period;
    private float _maxDistance;
    private Color _baseColor;
    private float _time;

    public void Initialize(Light light, string effect, float brightness, float period, float maxDistance, Color baseColor)
    {
        _light = light;
        _effect = effect ?? string.Empty;
        _brightness = brightness;
        _period = Mathf.Max(0.01f, period);
        _maxDistance = Mathf.Max(0f, maxDistance);
        _baseColor = baseColor;
    }

    private void Awake()
    {
        if (_light == null)
            _light = GetComponent<Light>();

        if (_period <= 0f)
            _period = 1f;
    }

    private void Update()
    {
        if (_light == null)
            return;

        _time += Time.deltaTime;

        if (_maxDistance > 0f && Camera.main != null)
        {
            float dist = Vector3.Distance(Camera.main.transform.position, _light.transform.position);
            _light.enabled = dist <= _maxDistance;
        }
        else
        {
            _light.enabled = true;
        }

        switch (_effect)
        {
            case "blink":
                Blink();
                break;
            case "fadein":
                FadeIn();
                break;
            case "fadeout":
                FadeOut();
                break;
            case "fire":
                Fire();
                break;
            case "pulse":
                Pulse();
                break;
            case "rainbow":
                Rainbow();
                break;
            default:
                _light.color = _baseColor;
                _light.intensity = _brightness;
                break;
        }
    }

    private void Blink()
    {
        float phase = Mathf.Floor(_time / _period);
        bool on = ((int)phase % 2) == 0;
        _light.intensity = on ? _brightness : 0f;
        _light.color = _baseColor;
    }

    private void FadeIn()
    {
        float t = Mathf.Clamp01(_time / _period);
        _light.intensity = Mathf.Lerp(0f, _brightness, t);
        _light.color = _baseColor;
    }

    private void FadeOut()
    {
        float t = Mathf.Clamp01(_time / _period);
        _light.intensity = Mathf.Lerp(_brightness, 0f, t);
        _light.color = _baseColor;
    }

    private void Fire()
    {
        float flicker = UnityEngine.Random.Range(0.5f * _brightness, 1.5f * _brightness);
        _light.intensity = flicker;
        _light.color = _baseColor;
    }

    private void Pulse()
    {
        float sin = Mathf.Sin((_time / _period) * Mathf.PI * 2f);
        _light.intensity = _brightness * (0.5f + 0.5f * sin);
        _light.color = _baseColor;
    }

    private void Rainbow()
    {
        float h = (_time / _period) % 1f;
        _light.color = Color.HSVToRGB(h, 1f, 1f);
        _light.intensity = _brightness;
    }
}
