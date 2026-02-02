using System;
using UnityEngine;

namespace RWXLoader
{
    public class RWXTextureProcessor : MonoBehaviour
    {
        // Cache shader/property IDs (no GC)
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        private static readonly int MaskTexId = Shader.PropertyToID("_MaskTex");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int CutoffId = Shader.PropertyToID("_Cutoff");
        private static readonly int MaskInvertId = Shader.PropertyToID("_MaskInvert");
        private static readonly int MaskFlipYId = Shader.PropertyToID("_MaskFlipY");

        private Shader _maskedCutoutShader;

        private void Awake()
        {
            _maskedCutoutShader = Shader.Find("RWX/MaskedCutout");
            if (_maskedCutoutShader == null)
                Debug.LogError("[RWXTextureProcessor] Shader 'RWX/MaskedCutout' not found. Create it in Assets/Shaders.");
        }

        public void ApplyTexturesWithMask(Material material, Texture2D mainTexture, Texture2D maskTexture, RWXMaterial rwxMaterial)
        {
            if (material == null) return;

            // Tint/opacity (same behavior you had)
            Color tintColor;
            if (rwxMaterial != null && rwxMaterial.tint)
            {
                Color baseColor = rwxMaterial.GetEffectiveColor();
                tintColor = new Color(baseColor.r, baseColor.g, baseColor.b, rwxMaterial.opacity);
            }
            else
            {
                float a = rwxMaterial != null ? rwxMaterial.opacity : 1f;
                tintColor = new Color(1f, 1f, 1f, a);
            }

            material.SetColor(ColorId, tintColor);

            // Main only
            if (mainTexture != null && maskTexture == null)
            {
                material.SetTexture(MainTexId, mainTexture);
                material.mainTexture = mainTexture;
                return;
            }

            // Mask only (rare, but keep sane)
            if (mainTexture == null && maskTexture != null)
            {
                material.SetTexture(MainTexId, maskTexture);
                material.mainTexture = maskTexture;
                return;
            }

            // Both main + mask: use masked shader (NO CPU combine)
            if (mainTexture != null && maskTexture != null)
            {
                if (_maskedCutoutShader != null && material.shader != _maskedCutoutShader)
                    material.shader = _maskedCutoutShader;

                material.SetTexture(MainTexId, mainTexture);
                material.SetTexture(MaskTexId, maskTexture);
                material.mainTexture = mainTexture;

                // Your confirmed behavior:
                material.SetFloat(MaskFlipYId, 1f);

                // Your inversion rules
                bool invert = ShouldInvertMask(maskTexture);
                material.SetFloat(MaskInvertId, invert ? 1f : 0f);

                // Use your manager’s alphaTest if you want: expose it via rwxMaterial or pass as param.
                // If you can't, default to 0.2f here.
                material.SetFloat(CutoffId, 0.2f);

                // Ensure render queue is appropriate for cutout
                material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
                return;
            }
        }

        private static bool ShouldInvertMask(Texture2D maskTexture)
        {
            if (maskTexture == null) return false;
            string n = maskTexture.name ?? string.Empty;
            return n.IndexOf("tbtree", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   n.IndexOf("003m", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
