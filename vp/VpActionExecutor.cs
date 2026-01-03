using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using RWXLoader;
using UnityEngine;

/// <summary>
/// VpActionExecutor
/// ----------------
/// Fixes:
///  - Stops runaway material instancing (uses MaterialPropertyBlock + sharedMaterials)
///  - Tagged actions (texture/color/opacity) apply per-material-index correctly
///  - Prevents "overrides disappearing" after walking around (no renderer.materials churn)
///  - Adds bounded texture cache with eviction to control memory growth
///  - Standard shader alpha handling via shared *variant* materials (cached), not per-renderer instances
/// </summary>
public static class VpActionExecutor
{
    private const float VpShearToUnity = 0.5f;

    // -------------------------
    // Texture cache (bounded)
    // -------------------------
    private const int MaxCachedTextures = 512;

    private struct TextureCacheEntry
    {
        public Texture2D tex;
        public LinkedListNode<string> lruNode;
    }

    private static readonly Dictionary<string, TextureCacheEntry> _textureCache = new Dictionary<string, TextureCacheEntry>(1024);
    private static readonly LinkedList<string> _textureLru = new LinkedList<string>();

    // -------------------------
    // Material variant caches (avoid instancing per renderer)
    // -------------------------
    private enum AlphaVariantMode : byte { Opaque = 0, Cutout = 1, Transparent = 2 }

    private readonly struct MaterialVariantKey : IEquatable<MaterialVariantKey>
    {
        public readonly int baseId;
        public readonly AlphaVariantMode mode;
        public MaterialVariantKey(Material baseMat, AlphaVariantMode mode)
        {
            baseId = baseMat != null ? baseMat.GetInstanceID() : 0;
            this.mode = mode;
        }
        public bool Equals(MaterialVariantKey other) => baseId == other.baseId && mode == other.mode;
        public override bool Equals(object obj) => obj is MaterialVariantKey o && Equals(o);
        public override int GetHashCode() => (baseId * 397) ^ (int)mode;
    }

    private static readonly Dictionary<MaterialVariantKey, Material> _materialVariants = new Dictionary<MaterialVariantKey, Material>(2048);

    // -------------------------
    // Shader property ids
    // -------------------------
    private static readonly int _MainTexId = Shader.PropertyToID("_MainTex");
    private static readonly int _BaseMapId = Shader.PropertyToID("_BaseMap");
    private static readonly int _ColorId = Shader.PropertyToID("_Color");
    private static readonly int _BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int _CutoffId = Shader.PropertyToID("_Cutoff");

    public static void Execute(GameObject target, VpActionCommand cmd, string objectPath, string password, MonoBehaviour host, string description = null)
    {
        ExecuteCreate(target, cmd, objectPath, password, host, description);
    }

    private static VpActionGate GetOrAddGate(GameObject target)
    {
        if (target == null) return null;
        var gate = target.GetComponent<VpActionGate>();
        if (gate == null) gate = target.AddComponent<VpActionGate>();
        return gate;
    }

    private static float NormalizeVpShear(float v)
    {
        // VP clamps shear inputs to [-20, 20]
        v = Mathf.Clamp(v, -20f, 20f);

        // VP scale: 20 => 1.0, 10 => 0.5, etc.
        return v / 20f;
    }

    // ============================================================
    // Dispatch
    // ============================================================
    public static void ExecuteCreate(GameObject target, VpActionCommand cmd, string objectPath, string password, MonoBehaviour host, string description = null)
    {
        if (target == null || cmd == null) return;

        switch (cmd.verb)
        {
            case "texture":    ExecuteTexture(target, cmd, objectPath, password, host); break;
            case "normalmap":  ExecuteNormalMap(target, cmd, objectPath, password, host); break;
            case "ambient":    ExecuteAmbient(target, cmd); break;
            case "diffuse":    ExecuteDiffuse(target, cmd); break;
            case "opacity":    ExecuteOpacity(target, cmd); break;
            case "light":      ExecuteLight(target, cmd); break;
            case "scale":      ExecuteScale(target, cmd); break;
            case "visible":    ExecuteVisible(target, cmd); break;
            case "shear":      ExecuteShear(target, cmd); break;
            case "color":      ExecuteColor(target, cmd); break;
            case "sign":       ExecuteSign(target, cmd, description); break;
        }
    }

    // ============================================================
    // TAG HELPERS (tag is per MATERIAL)
    // ============================================================
    private static int ReadMaterialTag(Material material)
    {
        if (material == null) return 0;
        string tagValue = material.GetTag("RwxTag", false, "0") ?? "0";
        if (int.TryParse(tagValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            return parsed;
        return 0;
    }

    private static int? TryExtractTag(VpActionCommand cmd)
    {
        if (cmd == null) return null;

        if (cmd.kv != null && cmd.kv.TryGetValue("tag", out var tagStr) &&
            int.TryParse(tagStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            return parsed;

        if (cmd.positional == null) return null;

        for (int i = 0; i < cmd.positional.Count; i++)
        {
            string token = cmd.positional[i];
            if (string.IsNullOrWhiteSpace(token)) continue;

            token = token.Trim();
            if (token.StartsWith("tag=", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(token.Substring(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out int t))
                    return t;
            }

            if (token.Equals("tag", StringComparison.OrdinalIgnoreCase) && i + 1 < cmd.positional.Count)
            {
                if (int.TryParse(cmd.positional[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int t))
                    return t;
            }
        }

        return null;
    }

    // ============================================================
    // AMBIENT / DIFFUSE (MPB-based; no material instancing)
    // ============================================================
    public static void ApplyAmbient(GameObject target, float ambient)
    {
        if (target == null) return;
        ambient = Mathf.Clamp01(ambient);

        foreach (var r in target.GetComponentsInChildren<Renderer>(true))
        {
            if (r == null) continue;

            // apply uniformly to the whole renderer (all submeshes)
            var block = new MaterialPropertyBlock();
            r.GetPropertyBlock(block);

            // derive from current effective color (block if present else shared material)
            Color baseC = GetEffectiveRendererColor(r, block);
            Color outC = new Color(baseC.r * ambient, baseC.g * ambient, baseC.b * ambient, baseC.a);

            block.SetColor(_ColorId, outC);
            block.SetColor(_BaseColorId, outC);
            r.SetPropertyBlock(block);
        }
    }

    public static void ApplyDiffuse(GameObject target, float diffuse)
    {
        if (target == null) return;
        diffuse = Mathf.Max(0f, diffuse);

        foreach (var r in target.GetComponentsInChildren<Renderer>(true))
        {
            if (r == null) continue;

            var block = new MaterialPropertyBlock();
            r.GetPropertyBlock(block);

            Color baseC = GetEffectiveRendererColor(r, block);
            Color outC = new Color(
                Mathf.Clamp01(baseC.r * diffuse),
                Mathf.Clamp01(baseC.g * diffuse),
                Mathf.Clamp01(baseC.b * diffuse),
                baseC.a
            );

            block.SetColor(_ColorId, outC);
            block.SetColor(_BaseColorId, outC);
            r.SetPropertyBlock(block);
        }
    }

    public static void ApplyVisible(GameObject target, bool visible)
    {
        if (target == null) return;

        var gate = target.GetComponent<VpActionGate>();
        if (gate != null)
        {
            gate.SetVisible(visible);
            return;
        }

        foreach (var r in target.GetComponentsInChildren<Renderer>(true))
            r.enabled = visible;
    }

    // ============================================================
    // SCALE (object-wide; uses base scale context)
    // ============================================================
    public static void ApplyScale(GameObject target, Vector3 scale)
    {
        if (target == null) return;

        const float MinScale = 0.1f;

        Vector3 baseScale = Vector3.one;
        var scaleContext = target.GetComponent<VpModelScaleContext>();
        if (scaleContext != null)
            baseScale = scaleContext.baseScale;

        target.transform.localScale = new Vector3(
            Mathf.Max(MinScale, scale.x * baseScale.x),
            Mathf.Max(MinScale, scale.y * baseScale.y),
            Mathf.Max(MinScale, scale.z * baseScale.z)
        );
    }

    // ============================================================
    // SHEAR (as in your current; uses normalized VP shear already)
    // ============================================================
    public static void ApplyShear(GameObject target, float zPlus, float xPlus, float yPlus, float yMinus, float zMinus, float xMinus)
    {
        if (target == null) return;

        zPlus = Mathf.Clamp(zPlus, -20f, 20f);
        xPlus = Mathf.Clamp(xPlus, -20f, 20f);
        yPlus = Mathf.Clamp(yPlus, -20f, 20f);
        yMinus = Mathf.Clamp(yMinus, -20f, 20f);
        zMinus = Mathf.Clamp(zMinus, -20f, 20f);
        xMinus = Mathf.Clamp(xMinus, -20f, 20f);

        ApplyShearToAllMeshes_VpMatrix_ObjectLocal(target, zPlus, xPlus, yPlus, yMinus, zMinus, xMinus);
    }

    // ============================================================
    // TEXTURE (fixed: per material index + variant materials + bounded cache)
    // ============================================================
    private static void ExecuteTexture(GameObject target, VpActionCommand cmd, string objectPath, string password, MonoBehaviour host)
    {
        if (host == null)
        {
            Debug.LogWarning("[VP] Cannot run texture action: coroutine host is null.");
            return;
        }

        string tex = null;

        if (cmd.positional != null && cmd.positional.Count > 0)
            tex = cmd.positional[0];

        if (string.IsNullOrWhiteSpace(tex) && cmd.kv != null)
        {
            if (cmd.kv.TryGetValue("name", out var name))
                tex = name;
            else if (cmd.kv.TryGetValue("texture", out var t))
                tex = t;
        }

        if (string.IsNullOrWhiteSpace(tex))
        {
            Debug.LogWarning($"[VP] texture action missing texture name: {cmd.raw}");
            return;
        }

        int? tagOverride = TryExtractTag(cmd);

        var gate = GetOrAddGate(target);
        gate?.BeginAction();
        host.StartCoroutine(ApplyTextureCoroutine_Gated(target, tex.Trim(), objectPath, password, host, tagOverride, gate));
    }

    private static IEnumerator ApplyTextureCoroutine_Gated(GameObject target, string textureName, string objectPath, string password, MonoBehaviour host, int? tagOverride, VpActionGate gate)
    {
        try { yield return ApplyTextureCoroutine(target, textureName, objectPath, password, host, tagOverride); }
        finally { gate?.EndAction(); }
    }

    private static IEnumerator ApplyTextureCoroutine(GameObject target, string textureName, string objectPath, string password, MonoBehaviour host, int? tagOverride)
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

        string cacheKey = MakeTextureCacheKey(objectPath, textureName);
        if (TryGetCachedTexture(cacheKey, out var cachedTex))
        {
            ApplyTextureToRendererMaterials(target, cachedTex, tagOverride);
            yield break;
        }

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

            while (!done) yield return null;

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
        try { bytes = File.ReadAllBytes(localPath); }
        catch (Exception e)
        {
            Debug.LogWarning($"[VP] texture read failed '{localPath}': {e.Message}");
            yield break;
        }

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
                Debug.LogWarning($"[VP] DDS decode failed for '{textureName}' at '{localPath}' sig='{sig4}' len={bytes.Length}");
                yield break;
            }
        }
        else
        {
            tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: true, linear: false);
            if (!tex.LoadImage(bytes))
            {
                Debug.LogWarning($"[VP] Unity couldn't decode texture '{textureName}' at '{localPath}'");
                yield break;
            }
        }

        // IMPORTANT: name should be the *requested* name (keeps ".png" signal for heuristics)
        tex.name = Path.GetFileName(localPath); // includes extension if present
        tex.wrapMode = TextureWrapMode.Repeat;
        tex.filterMode = FilterMode.Bilinear;
        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;

        PutCachedTexture(cacheKey, tex);

        ApplyTextureToRendererMaterials(target, tex, tagOverride);
    }

    private static void ApplyTextureToRendererMaterials(GameObject root, Texture2D tex, int? requiredTag, bool forceWhiteColor = false, bool forceTransparentVariant = false)
    {
        if (root == null || tex == null) return;

        var renderers = root.GetComponentsInChildren<Renderer>(includeInactive: true);
        for (int ri = 0; ri < renderers.Length; ri++)
        {
            var r = renderers[ri];
            if (r == null) continue;

            var shared = r.sharedMaterials; // NO instancing
            if (shared == null || shared.Length == 0) continue;

            bool changedMaterials = false;

            // Reuse one block per renderer to avoid allocations
            var block = new MaterialPropertyBlock();

            for (int mi = 0; mi < shared.Length; mi++)
            {
                var baseMat = shared[mi];
                if (baseMat == null) continue;

                if (requiredTag.HasValue && ReadMaterialTag(baseMat) != requiredTag.Value)
                    continue;

                // We don't know final opacity here; treat as opaque for texture selection unless forced transparent.
                var desiredMode = forceTransparentVariant
                    ? AlphaVariantMode.Transparent
                    : GuessAlphaModeForTexture(baseMat, tex, alphaForThisMaterial: 1f);

                var variant = GetOrCreateVariant(baseMat, desiredMode);

                if (variant != baseMat)
                {
                    shared[mi] = variant;
                    changedMaterials = true;
                }

                // Apply texture via per-material-index property block (no material instancing)
                r.GetPropertyBlock(block, mi);
                block.SetTexture(_MainTexId, tex);
                block.SetTexture(_BaseMapId, tex);

                if (forceWhiteColor)
                {
                    block.SetColor(_ColorId, Color.white);
                    block.SetColor(_BaseColorId, Color.white);
                }
                r.SetPropertyBlock(block, mi);
            }

            if (changedMaterials)
                r.sharedMaterials = shared;
        }
    }


    private static AlphaVariantMode GuessAlphaModeForTexture(Material baseMat, Texture2D tex, float alphaForThisMaterial)
    {
        // If caller is only setting a texture, don't force Transparent just because texture has alpha.
        // For foliage we want CUTOUT, and later OPACITY can switch to TRANSPARENT if alpha<1.
        bool wantsTransparency = alphaForThisMaterial < 0.999f;

        if (wantsTransparency)
            return AlphaVariantMode.Transparent;

        // Heuristic: if the requested texture filename ends with .png, treat it as cutout for Standard.
        // (Your leaf sheet case: avoids black background / opaque rendering)
        string n = tex.name ?? "";
        bool looksPng = n.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

        // Only do this for Standard shader family.
        if (looksPng && IsStandardLike(baseMat))
            return AlphaVariantMode.Cutout;

        return AlphaVariantMode.Opaque;
    }

    private static bool IsStandardLike(Material m)
    {
        if (m == null || m.shader == null) return false;
        string sn = m.shader.name ?? "";
        return sn.IndexOf("Standard", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static Material GetOrCreateVariant(Material baseMat, AlphaVariantMode mode)
    {
        if (baseMat == null) return null;

        // If not Standard-like, don't mutate modes (leave as-is)
        if (!IsStandardLike(baseMat))
            return baseMat;

        // If base is already in the desired mode (by its own settings), we still return base.
        // (We can't reliably read mode for all shader graphs; stick to variants only.)
        var key = new MaterialVariantKey(baseMat, mode);
        if (_materialVariants.TryGetValue(key, out var existing) && existing != null)
            return existing;

        var v = new Material(baseMat)
        {
            name = baseMat.name + $"__VP_{mode}"
        };

        switch (mode)
        {
            case AlphaVariantMode.Opaque:
                ConfigureStandardOpaque(v);
                break;
            case AlphaVariantMode.Cutout:
                ConfigureStandardCutout(v, cutoff: 0.5f);
                break;
            case AlphaVariantMode.Transparent:
                ConfigureStandardTransparent(v);
                break;
        }

        _materialVariants[key] = v;
        return v;
    }

    private static void ConfigureStandardOpaque(Material m)
    {
        if (m == null) return;

        if (m.HasProperty("_Mode")) m.SetFloat("_Mode", 0f);
        if (m.HasProperty("_SrcBlend")) m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
        if (m.HasProperty("_DstBlend")) m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
        if (m.HasProperty("_ZWrite")) m.SetInt("_ZWrite", 1);

        m.DisableKeyword("_ALPHATEST_ON");
        m.DisableKeyword("_ALPHABLEND_ON");
        m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        m.renderQueue = -1;
    }

    private static void ConfigureStandardCutout(Material m, float cutoff)
    {
        if (m == null) return;

        if (m.HasProperty("_Mode")) m.SetFloat("_Mode", 1f);
        if (m.HasProperty(_CutoffId)) m.SetFloat(_CutoffId, cutoff);

        if (m.HasProperty("_SrcBlend")) m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
        if (m.HasProperty("_DstBlend")) m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
        if (m.HasProperty("_ZWrite")) m.SetInt("_ZWrite", 1);

        m.EnableKeyword("_ALPHATEST_ON");
        m.DisableKeyword("_ALPHABLEND_ON");
        m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        m.renderQueue = 2450;
    }

    private static void ConfigureStandardTransparent(Material m)
    {
        if (m == null) return;

        if (m.HasProperty("_Mode")) m.SetFloat("_Mode", 3f);

        if (m.HasProperty("_SrcBlend")) m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        if (m.HasProperty("_DstBlend")) m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        if (m.HasProperty("_ZWrite")) m.SetInt("_ZWrite", 0);

        m.DisableKeyword("_ALPHATEST_ON");
        m.EnableKeyword("_ALPHABLEND_ON");
        m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        m.renderQueue = 3000;
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

    // ============================================================
    // SIGN TEXTURE (text-to-texture renderer)
    // ============================================================
    private static void ExecuteSign(GameObject target, VpActionCommand cmd, string description)
    {
        if (target == null || cmd == null) return;

        var renderer = target.GetComponentInChildren<Renderer>(includeInactive: true);
        if (renderer == null)
        {
            Debug.LogWarning("[VP] sign action could not find a renderer target.");
            return;
        }

        string text = GetValue(cmd, "text");
        if (string.IsNullOrWhiteSpace(text))
            text = description ?? string.Empty;

        text = text?.Replace("\\n", "\n") ?? string.Empty;

        string rawBcolor = GetValue(cmd, "bcolor");
        Color textColor = ParseColor(GetValue(cmd, "color"), Color.white);
        Color backgroundColor = ParseColor(rawBcolor, new Color32(0, 0, 192, 255)); // VP default: medium blue
        if (!string.IsNullOrWhiteSpace(rawBcolor))
        {
            string trimmed = rawBcolor.Trim();
            string hex = trimmed.StartsWith("#") ? trimmed.Substring(1) : trimmed;
            if (string.Equals(hex, "000000", StringComparison.OrdinalIgnoreCase))
                backgroundColor = new Color(backgroundColor.r, backgroundColor.g, backgroundColor.b, 0f);
        }

        string align = GetValue(cmd, "align");
        TextAnchor anchor = ParseTextAnchor(align, TextAnchor.MiddleCenter);

        float paddingFraction = Mathf.Clamp01(ParseFloat(GetValue(cmd, "pad"), 0f));

        float margin = Mathf.Max(0f, ParseFloat(GetValue(cmd, "margin"), 0f));
        float hMargin = Mathf.Max(0f, ParseFloat(GetValue(cmd, "hmargin"), 0f));
        float vMargin = Mathf.Max(0f, ParseFloat(GetValue(cmd, "vmargin"), 0f));

        // Treat values >1 as percentages.
        if (margin > 1f) margin *= 0.01f;
        if (hMargin > 1f) hMargin *= 0.01f;
        if (vMargin > 1f) vMargin *= 0.01f;

        // Clamp margins so we don't erase the drawable area.
        const float MaxMarginFraction = 0.49f;
        hMargin = Mathf.Clamp(margin > 0f ? margin : hMargin, 0f, MaxMarginFraction);
        vMargin = Mathf.Clamp(margin > 0f ? margin : vMargin, 0f, MaxMarginFraction);

        // Use the largest of margin/pad for each axis.
        float padXFraction = Mathf.Clamp01(Mathf.Max(paddingFraction, hMargin));
        float padYFraction = Mathf.Clamp01(Mathf.Max(paddingFraction, vMargin));

        float scaleMultiplier = 1f;
        string scaleStr = GetValue(cmd, "scale");
        if (!string.IsNullOrWhiteSpace(scaleStr))
        {
            var parts = scaleStr.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0)
            {
                float best = 1f;
                for (int i = 0; i < parts.Length; i++)
                    best = Mathf.Max(best, ParseFloat(parts[i], 1f));
                scaleMultiplier = best;
            }
        }

        bool dropShadow = HasFlag(cmd, "shadow") && !HasFlag(cmd, "noshadow");
        Color shadowColor = new Color(0f, 0f, 0f, textColor.a * 0.75f);

        var signTexture = GenerateSignTexture(
            renderer,
            text,
            null,
            textColor,
            backgroundColor,
            512,
            padXFraction,
            padYFraction,
            anchor,
            scaleMultiplier,
            dropShadow,
            shadowColor);

        ApplyTextureToRendererMaterials(target, signTexture, requiredTag: 100, forceWhiteColor: true, forceTransparentVariant: true);
    }

    private static bool HasFlag(VpActionCommand cmd, string flag)
    {
        if (cmd == null || string.IsNullOrWhiteSpace(flag))
            return false;

        string f = flag.Trim().ToLowerInvariant();

        if (cmd.positional != null)
        {
            for (int i = 0; i < cmd.positional.Count; i++)
            {
                if (string.Equals(cmd.positional[i]?.Trim(), f, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        if (cmd.kv != null && cmd.kv.TryGetValue(f, out var val))
        {
            if (string.IsNullOrWhiteSpace(val))
                return true;

            string v = val.Trim().ToLowerInvariant();
            return v == "1" || v == "true" || v == "yes" || v == "on";
        }

        return false;
    }

    private static TextAnchor ParseTextAnchor(string align, TextAnchor fallback)
    {
        if (string.IsNullOrWhiteSpace(align))
            return fallback;

        switch (align.Trim().ToLowerInvariant())
        {
            case "left":
            case "west":
                return TextAnchor.MiddleLeft;
            case "right":
            case "east":
                return TextAnchor.MiddleRight;
            default:
                return fallback;
        }
    }

    public static Texture2D GenerateSignTexture(
        Renderer targetRenderer,
        string text,
        Font font,
        Color textColor,
        Color backgroundColor,
        int textureWidth = 512,
        float paddingXFraction = 0.05f,
        float paddingYFraction = 0.05f,
        TextAnchor anchor = TextAnchor.MiddleCenter,
        float scaleMultiplier = 1f,
        bool dropShadow = false,
        Color? shadowColor = null)
    {
        text ??= string.Empty;
        paddingXFraction = Mathf.Clamp01(paddingXFraction);
        paddingYFraction = Mathf.Clamp01(paddingYFraction);
        textureWidth = Mathf.Max(16, textureWidth);
        scaleMultiplier = Mathf.Max(0.01f, scaleMultiplier);

        // Fallback font so we always render something.
        if (font == null)
        {
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font == null)
                font = Font.CreateDynamicFontFromOSFont("Arial", 16);
        }

        // Use the renderer's mesh bounds (scaled) to establish the target aspect.
        Vector2 quadSize = GetRendererQuadSize(targetRenderer);
        float aspect = quadSize.x <= 0.0001f || quadSize.y <= 0.0001f
            ? 1f
            : quadSize.x / quadSize.y;

        int texWidth = textureWidth;
        int texHeight = Mathf.Max(16, Mathf.RoundToInt(texWidth / Mathf.Max(aspect, 0.001f)));

        float padX = texWidth * paddingXFraction;
        float padY = texHeight * paddingYFraction;
        float innerWidth = Mathf.Max(1f, texWidth - (padX * 2f));
        float innerHeight = Mathf.Max(1f, texHeight - (padY * 2f));

        // Measure text at a probe size.
        const int ProbeFontSize = 64;
        var generator = new TextGenerator();
        var settings = new TextGenerationSettings
        {
            textAnchor = anchor,
            generationExtents = new Vector2(innerWidth, innerHeight),
            pivot = new Vector2(0.5f, 0.5f),
            richText = true,
            font = font,
            color = textColor,
            fontSize = ProbeFontSize,
            lineSpacing = 1f,
            scaleFactor = 1f,
            horizontalOverflow = HorizontalWrapMode.Wrap,
            verticalOverflow = VerticalWrapMode.Overflow,
            resizeTextForBestFit = false,
            updateBounds = true
        };

        PopulateWithFont(font, text, settings, generator);
        Vector2 measured = generator.rectExtents.size;
        if (measured.x <= 0.001f || measured.y <= 0.001f)
            measured = new Vector2(1f, 1f);

        // Fit scale must honor BOTH width and height extents.
        float maxScaleFromWidth = innerWidth / measured.x;
        float maxScaleFromHeight = innerHeight / measured.y;
        float baseScale = Mathf.Min(maxScaleFromWidth, maxScaleFromHeight);

        // Allow scaling down, but clamp upscales so text stays inside the quad.
        float requestedScale = baseScale * scaleMultiplier;
        float clampedScale = Mathf.Clamp(requestedScale, 0.01f, baseScale);

        int finalFontSize = Mathf.Clamp(Mathf.RoundToInt(ProbeFontSize * clampedScale), 2, 4096);

        // Re-run generation at the final size to keep bounds tight and avoid cropping.
        settings.fontSize = finalFontSize;
        PopulateWithFont(font, text, settings, generator);

        // Second pass: use actual generated bounds to fill remaining space (without exceeding safety factor).
        Vector2 genSize = ComputeGeneratedTextSize(generator);
        float fillScale = Mathf.Min(
            innerWidth / Mathf.Max(1f, genSize.x),
            innerHeight / Mathf.Max(1f, genSize.y)
        );
        fillScale = Mathf.Clamp(fillScale, 0.9f, 3.0f); // bias toward filling while avoiding blowouts

        int adjustedFontSize = Mathf.Clamp(Mathf.RoundToInt(finalFontSize * fillScale), 2, 4096);
        if (adjustedFontSize != finalFontSize)
        {
            finalFontSize = adjustedFontSize;
            settings.fontSize = finalFontSize;
            PopulateWithFont(font, text, settings, generator);
        }

        var rt = RenderTexture.GetTemporary(texWidth, texHeight, 0, RenderTextureFormat.ARGB32);
        var prevRt = RenderTexture.active;
        RenderTexture.active = rt;

        GL.PushMatrix();
        GL.modelview = Matrix4x4.identity;
        // y-up so text isn't flipped.
        GL.LoadPixelMatrix(0, texWidth, 0, texHeight);
        GL.Clear(true, true, backgroundColor);

        Rect textRect = new Rect(padX, padY, innerWidth, innerHeight);
        DrawTextToRenderTexture(generator, font, textColor, shadowColor ?? new Color(0f, 0f, 0f, textColor.a * 0.75f), textRect, dropShadow, anchor);

        var output = new Texture2D(texWidth, texHeight, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            name = "sign-texture"
        };

        output.ReadPixels(new Rect(0, 0, texWidth, texHeight), 0, 0);
        output.Apply();

        GL.PopMatrix();
        RenderTexture.active = prevRt;
        RenderTexture.ReleaseTemporary(rt);

        return output;
    }

    private static Vector2 GetRendererQuadSize(Renderer renderer)
    {
        const float MinSize = 0.001f;

        if (renderer == null)
            return new Vector2(1f, 1f);

        var mf = renderer.GetComponent<MeshFilter>();
        var mesh = mf != null ? mf.sharedMesh : null;
        if (mesh != null)
        {
            Vector3 scaled = Vector3.Scale(mesh.bounds.size, mf.transform.lossyScale);

            // Use the two largest dimensions as width/height to ignore thickness.
            float[] dims = { Mathf.Abs(scaled.x), Mathf.Abs(scaled.y), Mathf.Abs(scaled.z) };
            Array.Sort(dims);

            float height = Mathf.Max(MinSize, dims[1]);
            float width = Mathf.Max(MinSize, dims[2]);
            return new Vector2(width, height);
        }

        // Fallback to renderer bounds in world space.
        var size = renderer.bounds.size;
        float[] worldDims = { Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z) };
        Array.Sort(worldDims);

        float worldHeight = Mathf.Max(MinSize, worldDims[1]);
        float worldWidth = Mathf.Max(MinSize, worldDims[2]);
        return new Vector2(worldWidth, worldHeight);
    }

    private static Vector2 ComputeGeneratedTextSize(TextGenerator generator)
    {
        if (generator == null) return Vector2.zero;
        var verts = generator.verts;
        if (verts == null || verts.Count == 0) return Vector2.zero;

        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;

        for (int i = 0; i < verts.Count; i++)
        {
            var p = verts[i].position;
            minX = Mathf.Min(minX, p.x);
            maxX = Mathf.Max(maxX, p.x);
            minY = Mathf.Min(minY, p.y);
            maxY = Mathf.Max(maxY, p.y);
        }

        return new Vector2(Mathf.Max(0f, maxX - minX), Mathf.Max(0f, maxY - minY));
    }

    private static void PopulateWithFont(Font font, string text, TextGenerationSettings settings, TextGenerator generator)
    {
        if (font == null || generator == null)
            return;

        bool rebuild = false;
        void OnRebuild(Font f) => rebuild = true;

        font.textureRebuildCallback += OnRebuild;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            rebuild = false;
            font.RequestCharactersInTexture(text, settings.fontSize, FontStyle.Normal);
            generator.Populate(text, settings);
            if (!rebuild) break;
        }

        font.textureRebuildCallback -= OnRebuild;
    }

    private static void DrawTextToRenderTexture(TextGenerator generator, Font font, Color textColor, Color shadowColor, Rect targetRect, bool dropShadow, TextAnchor anchor)
    {
        if (generator == null || font == null)
            return;

        var verts = generator.verts;
        if (verts == null || verts.Count == 0)
            return;

        int quadCount = verts.Count / 4;
        if (quadCount == 0)
            return;

        var mesh = new Mesh { name = "vp-sign-mesh" };
        var positions = new Vector3[quadCount * 4];
        var uvs = new Vector2[quadCount * 4];
        var colors = new Color[quadCount * 4];
        var indices = new int[quadCount * 6];

        // Compute actual text bounds from generated verts.
        float boundsMinX = float.MaxValue, boundsMaxX = float.MinValue;
        float boundsMinY = float.MaxValue, boundsMaxY = float.MinValue;
        for (int i = 0; i < verts.Count; i++)
        {
            Vector3 p = verts[i].position;
            boundsMinX = Mathf.Min(boundsMinX, p.x);
            boundsMaxX = Mathf.Max(boundsMaxX, p.x);
            boundsMinY = Mathf.Min(boundsMinY, p.y);
            boundsMaxY = Mathf.Max(boundsMaxY, p.y);
        }

        float textCenterX = (boundsMinX + boundsMaxX) * 0.5f;
        float textCenterY = (boundsMinY + boundsMaxY) * 0.5f;

        float offsetX = targetRect.center.x - textCenterX;
        float offsetY = targetRect.center.y - textCenterY;

        // Horizontal anchor adjustments
        if (anchor == TextAnchor.MiddleLeft)
            offsetX = targetRect.xMin - boundsMinX;
        else if (anchor == TextAnchor.MiddleRight)
            offsetX = targetRect.xMax - boundsMaxX;

        Vector3 offset = new Vector3(offsetX, offsetY, 0f);

        for (int qi = 0; qi < quadCount; qi++)
        {
            int vi = qi * 4;
            for (int j = 0; j < 4; j++)
            {
                int dst = vi + j;
                var v = verts[dst];
                positions[dst] = v.position + offset;
                uvs[dst] = v.uv0;
                colors[dst] = textColor;
            }

            int ii = qi * 6;
            indices[ii + 0] = vi + 0;
            indices[ii + 1] = vi + 1;
            indices[ii + 2] = vi + 2;
            indices[ii + 3] = vi + 2;
            indices[ii + 4] = vi + 3;
            indices[ii + 5] = vi + 0;
        }

        mesh.SetVertices(positions);
        mesh.SetUVs(0, uvs);
        mesh.SetColors(colors);
        mesh.SetTriangles(indices, 0);

        var baseMat = new Material(font.material) { color = textColor };
        var fontTex = font.material != null ? font.material.mainTexture : null;
        baseMat.mainTexture = fontTex;
        baseMat.SetTexture("_MainTex", fontTex);
        baseMat.SetInt("_ZWrite", 0);
        baseMat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
        baseMat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);

        GL.PushMatrix();
        Matrix4x4 prevModel = GL.modelview;
        GL.modelview = Matrix4x4.identity;
        GL.MultMatrix(Matrix4x4.identity);

        if (dropShadow)
        {
            var shadowMat = new Material(baseMat) { color = shadowColor };
            Vector3 shadowOffset = Vector3.one * Mathf.Max(1f, font.fontSize * 0.06f);
            GL.PushMatrix();
            GL.MultMatrix(Matrix4x4.Translate(shadowOffset));
            shadowMat.SetPass(0);
            Graphics.DrawMeshNow(mesh, Matrix4x4.identity);
            GL.PopMatrix();
            UnityEngine.Object.DestroyImmediate(shadowMat);
        }

        baseMat.SetPass(0);
        Graphics.DrawMeshNow(mesh, Matrix4x4.identity);
        GL.PopMatrix();
        GL.modelview = prevModel;

        UnityEngine.Object.DestroyImmediate(mesh);
        UnityEngine.Object.DestroyImmediate(baseMat);
    }

    // ============================================================
    // NORMALMAP (left mostly as-is; still uses renderer.materials -> but this is usually low volume)
    // If you want, we can also switch this to MPB+variant later.
    // ============================================================
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

        var gate = GetOrAddGate(target);
        gate?.BeginAction();

        host.StartCoroutine(ApplyNormalMapCoroutine_Gated(target, normalName, objectPath, password, host, gate));
    }

    private static IEnumerator ApplyNormalMapCoroutine_Gated(GameObject target, string textureName, string objectPath, string password, MonoBehaviour host, VpActionGate gate)
    {
        try { yield return ApplyNormalMapCoroutine(target, textureName, objectPath, password, host); }
        finally { gate?.EndAction(); }
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

        tex.name = Path.GetFileName(localPath);
        tex.Apply(true, false);

        ApplyNormalMapToRenderers(target, tex);
    }

    private static void ApplyNormalMapToRenderers(GameObject root, Texture2D normal)
    {
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
        {
            if (r == null) continue;

            // NOTE: this can instance materials. Usually normalmap usage is rare; keep for now.
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

    // ============================================================
    // AMBIENT / DIFFUSE dispatch
    // ============================================================
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

    // ============================================================
    // OPACITY
    //   - Tagged: per material index MPB + variant switch
    //   - Untagged: whole-object state + MPB (no material instancing)
    // ============================================================
    private static void ExecuteOpacity(GameObject target, VpActionCommand cmd)
    {
        if (target == null) return;

        float opacity = 1f;
        if (cmd.positional != null && cmd.positional.Count > 0)
            opacity = Mathf.Clamp01(ParseFloat(cmd.positional[0], 1f));

        int? tagOverride = TryExtractTag(cmd);

        if (tagOverride.HasValue)
        {
            ApplyOpacity_TaggedMaterials(target, opacity, tagOverride.Value);
            return;
        }

        ApplyOpacity(target, opacity);
    }

    private static void ApplyOpacity_TaggedMaterials(GameObject target, float opacity, int requiredTag)
    {
        if (target == null) return;

        var renderers = target.GetComponentsInChildren<Renderer>(true);
        for (int ri = 0; ri < renderers.Length; ri++)
        {
            var r = renderers[ri];
            if (r == null) continue;

            var shared = r.sharedMaterials;
            if (shared == null || shared.Length == 0) continue;

            bool changedMaterials = false;

            for (int mi = 0; mi < shared.Length; mi++)
            {
                var baseMat = shared[mi];
                if (baseMat == null) continue;
                if (ReadMaterialTag(baseMat) != requiredTag) continue;

                // If alpha != 1, we want transparent variant for Standard.
                AlphaVariantMode desiredMode = (opacity < 0.999f) ? AlphaVariantMode.Transparent : AlphaVariantMode.Opaque;
                var variant = GetOrCreateVariant(baseMat, desiredMode);

                if (variant != baseMat)
                {
                    shared[mi] = variant;
                    changedMaterials = true;
                }

                var block = new MaterialPropertyBlock();
                r.GetPropertyBlock(block, mi);

                // Preserve RGB from current effective color for that material index if present
                Color baseC = GetEffectiveMaterialIndexColor(r, mi, block);
                Color outC = new Color(baseC.r, baseC.g, baseC.b, opacity);

                block.SetColor(_ColorId, outC);
                block.SetColor(_BaseColorId, outC);

                r.SetPropertyBlock(block, mi);
            }

            if (changedMaterials)
                r.sharedMaterials = shared;
        }
    }

    public static void ApplyOpacity(GameObject target, float opacity)
    {
        if (target == null) return;

        var state = GetOrAddColorState(target);
        state.hasOpacityOverride = true;
        state.opacity = opacity;
        state.hasAppliedColorBefore = true;

        ApplyColorState(target, state, colorActive: state.hasColorOverride, clearTextures: false);
    }

    // ============================================================
    // COLOR
    //   - Tagged: MPB per material index
    //   - Untagged: state system but applied via MPB/sharedMaterials (no instancing)
    // ============================================================
    private static void ExecuteColor(GameObject target, VpActionCommand cmd)
    {
        if (target == null || cmd == null) return;

        bool tint = false;
        string colorStr = ExtractColorString(cmd, ref tint);

        if (string.IsNullOrWhiteSpace(colorStr))
        {
            Debug.LogWarning($"[VP] color action missing color value: {cmd.raw}");
            return;
        }

        Color color = ParseColor(colorStr, Color.white);
        int? tagOverride = TryExtractTag(cmd);

        if (tagOverride.HasValue)
        {
            ApplyColor_TaggedMaterials(target, color, tint, tagOverride.Value);
            return;
        }

        var state = GetOrAddColorState(target);
        state.hasColorOverride = true;
        state.tint = tint;
        state.color = color;
        state.hasAppliedColorBefore = true;
        state.sequence++;
        state.lastColorSeq = state.sequence;

        ApplyColorState(target, state, colorActive: true, clearTextures: !tint);
    }

    private static void ApplyColor_TaggedMaterials(GameObject target, Color color, bool tint, int requiredTag)
    {
        if (target == null) return;

        var renderers = target.GetComponentsInChildren<Renderer>(true);
        for (int ri = 0; ri < renderers.Length; ri++)
        {
            var r = renderers[ri];
            if (r == null) continue;

            var shared = r.sharedMaterials;
            if (shared == null || shared.Length == 0) continue;

            bool changedMaterials = false;

            for (int mi = 0; mi < shared.Length; mi++)
            {
                var baseMat = shared[mi];
                if (baseMat == null) continue;
                if (ReadMaterialTag(baseMat) != requiredTag) continue;

                // If color alpha < 1 => transparent variant.
                AlphaVariantMode desiredMode = (color.a < 0.999f) ? AlphaVariantMode.Transparent : AlphaVariantMode.Opaque;
                var variant = GetOrCreateVariant(baseMat, desiredMode);

                if (variant != baseMat)
                {
                    shared[mi] = variant;
                    changedMaterials = true;
                }

                var block = new MaterialPropertyBlock();
                r.GetPropertyBlock(block, mi);

                block.SetColor(_ColorId, color);
                block.SetColor(_BaseColorId, color);

                // If not tint, VP often implies "color replaces texture" for full object.
                // For TAGGED pieces, we keep textures; clearing textures per-tag can be very destructive.
                // So we do NOT clear textures here (matches what you observed: texture/tint already works).

                r.SetPropertyBlock(block, mi);
            }

            if (changedMaterials)
                r.sharedMaterials = shared;
        }
    }

    // ============================================================
    // COLOR STATE SYSTEM (rewritten to avoid renderer.materials instancing)
    // ============================================================
    private static void ApplyColorState(GameObject target, VpColorState state, bool colorActive, bool clearTextures)
    {
        if (target == null || state == null) return;

        var renderers = target.GetComponentsInChildren<Renderer>(includeInactive: true);

        foreach (var r in renderers)
        {
            if (r == null) continue;

            var shared = r.sharedMaterials;
            if (shared == null || shared.Length == 0) continue;

            // Base color is stored per renderer instance in your state object.
            Color baseColor = GetOrStoreBaseColor(r, shared, state);
            Color desired = colorActive ? state.color : baseColor;

            float alpha = state.hasOpacityOverride ? state.opacity : desired.a;
            desired = new Color(desired.r, desired.g, desired.b, alpha);

            bool changedMaterials = false;

            // Apply to each material index so alpha variants are correct for Standard.
            for (int mi = 0; mi < shared.Length; mi++)
            {
                var baseMat = shared[mi];
                if (baseMat == null) continue;

                // If alpha changes, need transparent variant.
                AlphaVariantMode desiredMode = (desired.a < 0.999f) ? AlphaVariantMode.Transparent : AlphaVariantMode.Opaque;
                var variant = GetOrCreateVariant(baseMat, desiredMode);
                if (variant != baseMat)
                {
                    shared[mi] = variant;
                    changedMaterials = true;
                }

                var block = new MaterialPropertyBlock();
                r.GetPropertyBlock(block, mi);

                if (clearTextures)
                {
                    block.SetTexture(_MainTexId, Texture2D.whiteTexture);
                    block.SetTexture(_BaseMapId, Texture2D.whiteTexture);
                }

                block.SetColor(_ColorId, desired);
                block.SetColor(_BaseColorId, desired);

                r.SetPropertyBlock(block, mi);
            }

            if (changedMaterials)
                r.sharedMaterials = shared;
        }
    }

    private static Color GetOrStoreBaseColor(Renderer renderer, Material[] sharedMaterials, VpColorState state)
    {
        if (renderer == null || state == null) return Color.white;

        int id = renderer.GetInstanceID();
        if (state.baseColors.TryGetValue(id, out var baseColor))
            return baseColor;

        baseColor = ReadMaterialColor(sharedMaterials);
        state.baseColors[id] = baseColor;
        return baseColor;
    }

    private static Color ReadMaterialColor(Material[] materials)
    {
        if (materials == null || materials.Length == 0)
            return Color.white;

        for (int i = 0; i < materials.Length; i++)
        {
            var material = materials[i];
            if (material == null) continue;

            if (material.HasProperty(_ColorId))
                return material.GetColor(_ColorId);

            if (material.HasProperty(_BaseColorId))
                return material.GetColor(_BaseColorId);
        }

        return Color.white;
    }

    // ============================================================
    // LIGHT / SCALE / VISIBLE / SHEAR dispatch
    // ============================================================
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

        ApplyScale(target, new Vector3(x, y, z));
    }

    private static void ExecuteVisible(GameObject target, VpActionCommand cmd)
    {
        if (target == null) return;
        if (cmd.positional == null || cmd.positional.Count == 0) return;

        string v = cmd.positional[0].Trim().ToLowerInvariant();
        bool visible = v == "yes" || v == "true" || v == "1" || v == "on";
        ApplyVisible(target, visible);
    }

    private static void ExecuteShear(GameObject target, VpActionCommand cmd)
    {
        if (target == null || cmd == null) return;

        float zPlus  = NormalizeVpShear(GetPosFloat(cmd, 0, 0f));
        float xPlus  = NormalizeVpShear(GetPosFloat(cmd, 1, 0f));
        float yPlus  = NormalizeVpShear(GetPosFloat(cmd, 2, 0f));
        float yMinus = NormalizeVpShear(GetPosFloat(cmd, 3, 0f));
        float zMinus = NormalizeVpShear(GetPosFloat(cmd, 4, 0f));
        float xMinus = NormalizeVpShear(GetPosFloat(cmd, 5, 0f));

        ApplyShear(target, zPlus, xPlus, yPlus, yMinus, zMinus, xMinus);
    }

    // ============================================================
    // Color-string extraction (unchanged)
    // ============================================================
    private static string ExtractColorString(VpActionCommand cmd, ref bool tint)
    {
        string colorStr = null;

        if (cmd.positional != null)
        {
            foreach (var token in cmd.positional)
            {
                if (string.IsNullOrWhiteSpace(token)) continue;

                string trimmed = token.Trim();

                if (trimmed.Equals("tint", StringComparison.OrdinalIgnoreCase))
                {
                    tint = true;
                    continue;
                }

                if (trimmed.StartsWith("tint=", StringComparison.OrdinalIgnoreCase))
                {
                    tint = true;
                    if (trimmed.Length > 5)
                        colorStr ??= trimmed.Substring(5);
                    continue;
                }

                colorStr ??= trimmed;
            }
        }

        if (cmd.kv != null)
        {
            if (cmd.kv.TryGetValue("tint", out var tintVal))
            {
                tint = true;
                if (string.IsNullOrWhiteSpace(colorStr))
                    colorStr = tintVal;
            }

            if (string.IsNullOrWhiteSpace(colorStr) && cmd.kv.TryGetValue("color", out var kvColor))
                colorStr = kvColor;
        }

        return colorStr;
    }

    // ============================================================
    // Helpers: effective colors
    // ============================================================
    private static Color GetEffectiveRendererColor(Renderer r, MaterialPropertyBlock block)
    {
        // If MPB has color, use it, else first shared material color
        if (block != null)
        {
            // MPB doesn't have HasProperty, but GetColor returns default if unset; we use a small heuristic:
            var c = block.GetColor(_ColorId);
            if (c != default) return c;
            c = block.GetColor(_BaseColorId);
            if (c != default) return c;
        }

        var shared = r != null ? r.sharedMaterials : null;
        return ReadMaterialColor(shared);
    }

    private static Color GetEffectiveMaterialIndexColor(Renderer r, int materialIndex, MaterialPropertyBlock blockForIndex)
    {
        if (blockForIndex != null)
        {
            var c = blockForIndex.GetColor(_ColorId);
            if (c != default) return c;
            c = blockForIndex.GetColor(_BaseColorId);
            if (c != default) return c;
        }

        var shared = r != null ? r.sharedMaterials : null;
        if (shared != null && materialIndex >= 0 && materialIndex < shared.Length && shared[materialIndex] != null)
        {
            var m = shared[materialIndex];
            if (m.HasProperty(_ColorId)) return m.GetColor(_ColorId);
            if (m.HasProperty(_BaseColorId)) return m.GetColor(_BaseColorId);
        }

        return Color.white;
    }

    // ============================================================
    // Texture cache: bounded LRU
    // ============================================================
    private static bool TryGetCachedTexture(string key, out Texture2D tex)
    {
        tex = null;
        if (string.IsNullOrEmpty(key)) return false;

        if (_textureCache.TryGetValue(key, out var entry) && entry.tex != null)
        {
            // bump LRU
            if (entry.lruNode != null)
            {
                _textureLru.Remove(entry.lruNode);
                _textureLru.AddFirst(entry.lruNode);
            }

            tex = entry.tex;
            return true;
        }

        return false;
    }

    private static void PutCachedTexture(string key, Texture2D tex)
    {
        if (string.IsNullOrEmpty(key) || tex == null) return;

        // update existing
        if (_textureCache.TryGetValue(key, out var existing))
        {
            // replace texture (destroy old)
            if (existing.tex != null && existing.tex != tex)
                UnityEngine.Object.Destroy(existing.tex);

            if (existing.lruNode != null)
            {
                _textureLru.Remove(existing.lruNode);
                _textureLru.AddFirst(existing.lruNode);
                existing.tex = tex;
                _textureCache[key] = existing;
            }
            else
            {
                var node = new LinkedListNode<string>(key);
                _textureLru.AddFirst(node);
                _textureCache[key] = new TextureCacheEntry { tex = tex, lruNode = node };
            }

            return;
        }

        // insert new
        var n = new LinkedListNode<string>(key);
        _textureLru.AddFirst(n);
        _textureCache[key] = new TextureCacheEntry { tex = tex, lruNode = n };

        // evict
        while (_textureCache.Count > MaxCachedTextures && _textureLru.Last != null)
        {
            string evictKey = _textureLru.Last.Value;
            _textureLru.RemoveLast();

            if (_textureCache.TryGetValue(evictKey, out var e))
            {
                if (e.tex != null)
                    UnityEngine.Object.Destroy(e.tex);
                _textureCache.Remove(evictKey);
            }
        }
    }

    // ============================================================
    // VpColorState hook
    // ============================================================
    private static VpColorState GetOrAddColorState(GameObject target)
    {
        var state = target.GetComponent<VpColorState>();
        if (state == null) state = target.AddComponent<VpColorState>();
        return state;
    }

    // ============================================================
    // Parsing helpers
    // ============================================================
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
                return false;

            vals[i] = v;
            if (v > 1f) anyAboveOne = true;
        }

        float scale = anyAboveOne ? 1f / 255f : 1f;
        float r = vals[0] * scale;
        float g = vals[1] * scale;
        float b = vals[2] * scale;
        float a = (parts.Length == 4 ? vals[3] * scale : 1f);

        color = new Color(r, g, b, a);
        return true;
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

    // ============================================================
    // SHEAR (your existing)
    // ============================================================
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

        zPlus = Mathf.Clamp(zPlus, -20f, 20f);
        xPlus = Mathf.Clamp(xPlus, -20f, 20f);
        yPlus = Mathf.Clamp(yPlus, -20f, 20f);
        yMinus = Mathf.Clamp(yMinus, -20f, 20f);
        zMinus = Mathf.Clamp(zMinus, -20f, 20f);
        xMinus = Mathf.Clamp(xMinus, -20f, 20f);

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


}

// ------------------------------------------------------------
// VpLightEffect unchanged
// ------------------------------------------------------------
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
            case "blink": Blink(); break;
            case "fadein": FadeIn(); break;
            case "fadeout": FadeOut(); break;
            case "fire": Fire(); break;
            case "pulse": Pulse(); break;
            case "rainbow": Rainbow(); break;
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
