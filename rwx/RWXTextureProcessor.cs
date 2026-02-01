using System;
using UnityEngine;

namespace RWXLoader
{
    /// <summary>
    /// Handles texture processing operations like combining textures with masks
    /// </summary>
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

        /// <summary>
        /// Combines a main texture with a mask texture for alpha channel
        /// RWX masks: Black = transparent, White = opaque
        /// </summary>
        public Texture2D CombineTextureWithMask(Texture2D mainTexture, Texture2D maskTexture)
        {
            // Create a new texture with the same dimensions as the main texture
            Texture2D combinedTexture = new Texture2D(mainTexture.width, mainTexture.height, TextureFormat.RGBA32, false);
            
            // Set the combined texture name for debugging
            combinedTexture.name = mainTexture.name + "_combined";
            
            // Scale mask to match main texture size if needed
            Texture2D scaledMask = maskTexture;
            if (maskTexture.width != mainTexture.width || maskTexture.height != mainTexture.height)
            {
                scaledMask = ScaleTexture(maskTexture, mainTexture.width, mainTexture.height);
            }
            
            // CONFIRMED: Mask is upside down, apply vertical flip only
            scaledMask = FlipTextureVertically(scaledMask);

            Color[] mainPixels = mainTexture.GetPixels();
            Color[] maskPixels = scaledMask.GetPixels();
            Color[] combinedPixels = new Color[mainPixels.Length];

            for (int i = 0; i < mainPixels.Length; i++)
            {
                Color mainColor = mainPixels[i];
                Color maskColor = maskPixels[i];
                
                // Handle different mask types - some masks are inverted
                float maskGrayscale = (maskColor.r + maskColor.g + maskColor.b) / 3f;
                
                // Detect mask type based on texture name patterns
                bool shouldInvertMask = false;
                if (maskTexture.name.Contains("tbtree") || maskTexture.name.Contains("003m"))
                {
                    // These masks appear to need inversion
                    shouldInvertMask = true;
                }
                
                float alpha;
                if (shouldInvertMask)
                {
                    // INVERTED: White = transparent (alpha = 0), Black = opaque (alpha = 1)
                    alpha = 1.0f - maskGrayscale;
                }
                else
                {
                    // NORMAL: Black = transparent (alpha = 0), White = opaque (alpha = 1)
                    alpha = maskGrayscale;
                }
                
                // For smoother edges, you could use: float alpha = maskGrayscale;
                // But sharp cutoff usually works better for leaf textures
                
                combinedPixels[i] = new Color(mainColor.r, mainColor.g, mainColor.b, alpha);
            }

            combinedTexture.SetPixels(combinedPixels);
            combinedTexture.Apply();

            // Clean up scaled mask if we created one
            if (scaledMask != maskTexture)
            {
                Object.DestroyImmediate(scaledMask);
            }

            return combinedTexture;
        }

        /// <summary>
        /// Scales a texture to target dimensions using bilinear filtering
        /// </summary>
        public Texture2D ScaleTexture(Texture2D source, int targetWidth, int targetHeight)
        {
            Texture2D result = new Texture2D(targetWidth, targetHeight, source.format, false);
            result.name = source.name + "_scaled"; // Preserve the name with scaling indicator
            Color[] pixels = result.GetPixels();
            
            for (int y = 0; y < targetHeight; y++)
            {
                for (int x = 0; x < targetWidth; x++)
                {
                    float u = (float)x / targetWidth;
                    float v = (float)y / targetHeight;
                    pixels[y * targetWidth + x] = source.GetPixelBilinear(u, v);
                }
            }
            
            result.SetPixels(pixels);
            result.Apply();
            return result;
        }

        /// <summary>
        /// Applies textures and masks to a material with proper handling
        /// </summary>
        public void ApplyTexturesWithMask(Material material, Texture2D mainTexture, Texture2D maskTexture, RWXMaterial rwxMaterial)
        {
            if (material == null) return;

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

            if (mainTexture != null && maskTexture == null)
            {
                material.SetTexture(MainTexId, mainTexture);
                material.mainTexture = mainTexture;
                return;
            }

            if (mainTexture == null && maskTexture != null)
            {
                material.SetTexture(MainTexId, maskTexture);
                material.mainTexture = maskTexture;
                return;
            }

            if (mainTexture != null && maskTexture != null)
            {
                if (_maskedCutoutShader != null && material.shader != _maskedCutoutShader)
                    material.shader = _maskedCutoutShader;

                material.SetTexture(MainTexId, mainTexture);
                material.SetTexture(MaskTexId, maskTexture);
                material.mainTexture = mainTexture;

                material.SetFloat(MaskFlipYId, 1f);

                bool invert = ShouldInvertMask(maskTexture);
                material.SetFloat(MaskInvertId, invert ? 1f : 0f);

                material.SetFloat(CutoffId, 0.2f);
                material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
            }
        }

        private static bool ShouldInvertMask(Texture2D maskTexture)
        {
            if (maskTexture == null) return false;
            string n = maskTexture.name ?? string.Empty;
            return n.IndexOf("tbtree", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   n.IndexOf("003m", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Applies a single texture to a material
        /// </summary>
        public void ApplyTextureToMaterial(Material material, Texture2D texture, RWXMaterial rwxMaterial)
        {
            material.mainTexture = texture;
            
            // Apply tint if enabled
            if (rwxMaterial.tint)
            {
                Color baseColor = rwxMaterial.GetEffectiveColor();
                material.color = new Color(baseColor.r, baseColor.g, baseColor.b, rwxMaterial.opacity);
            }
            else
            {
                material.color = new Color(1f, 1f, 1f, rwxMaterial.opacity);
            }
        }

        /// <summary>
        /// Flips a texture vertically (upside down)
        /// </summary>
        public Texture2D FlipTextureVertically(Texture2D originalTexture)
        {
            Color[] originalPixels = originalTexture.GetPixels();
            int width = originalTexture.width;
            int height = originalTexture.height;
            string originalName = originalTexture.name;
            
            // Create new texture with same dimensions
            Texture2D flippedTexture = new Texture2D(width, height, originalTexture.format, false);
            flippedTexture.name = originalName; // Preserve the name
            Color[] flippedPixels = new Color[originalPixels.Length];
            
            // Flip vertically only (upside down)
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    // Original pixel position
                    int originalIndex = y * width + x;
                    
                    // New position after vertical flip
                    // (x, y) -> (x, height - 1 - y)
                    int newX = x;
                    int newY = height - 1 - y;
                    int flippedIndex = newY * width + newX;
                    
                    flippedPixels[flippedIndex] = originalPixels[originalIndex];
                }
            }
            
            flippedTexture.SetPixels(flippedPixels);
            flippedTexture.Apply();
            
            // Don't destroy the original texture here since it might be the input mask
            // The caller will handle cleanup
            
            return flippedTexture;
        }

        /// <summary>
        /// Rotates a texture 180 degrees (flips upside down and left-right)
        /// </summary>
        public Texture2D RotateTexture180(Texture2D originalTexture)
        {
            Color[] originalPixels = originalTexture.GetPixels();
            int width = originalTexture.width;
            int height = originalTexture.height;
            string originalName = originalTexture.name;
            
            // Create new texture with same dimensions
            Texture2D rotatedTexture = new Texture2D(width, height, originalTexture.format, false);
            rotatedTexture.name = originalName; // Preserve the name
            Color[] rotatedPixels = new Color[originalPixels.Length];
            
            // Rotate 180 degrees (flip both horizontally and vertically)
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    // Original pixel position
                    int originalIndex = y * width + x;
                    
                    // New position after 180-degree rotation
                    // (x, y) -> (width - 1 - x, height - 1 - y)
                    int newX = width - 1 - x;
                    int newY = height - 1 - y;
                    int rotatedIndex = newY * width + newX;
                    
                    rotatedPixels[rotatedIndex] = originalPixels[originalIndex];
                }
            }
            
            rotatedTexture.SetPixels(rotatedPixels);
            rotatedTexture.Apply();
            
            // Don't destroy the original texture here since it might be the input mask
            // The caller will handle cleanup
            
            return rotatedTexture;
        }

        /// <summary>
        /// Rotates a texture 90 degrees counter-clockwise
        /// </summary>
        public Texture2D RotateTexture90CounterClockwise(Texture2D originalTexture)
        {
            Color[] originalPixels = originalTexture.GetPixels();
            int originalWidth = originalTexture.width;
            int originalHeight = originalTexture.height;
            string originalName = originalTexture.name;
            
            // Create new texture with swapped dimensions
            Texture2D rotatedTexture = new Texture2D(originalHeight, originalWidth, originalTexture.format, false);
            rotatedTexture.name = originalName; // Preserve the name
            Color[] rotatedPixels = new Color[originalPixels.Length];
            
            // Rotate 90 degrees counter-clockwise
            for (int y = 0; y < originalHeight; y++)
            {
                for (int x = 0; x < originalWidth; x++)
                {
                    // Original pixel position
                    int originalIndex = y * originalWidth + x;
                    
                    // New position after 90-degree counter-clockwise rotation
                    // (x, y) -> (height - 1 - y, x)
                    int newX = originalHeight - 1 - y;
                    int newY = x;
                    int rotatedIndex = newY * originalHeight + newX;
                    
                    rotatedPixels[rotatedIndex] = originalPixels[originalIndex];
                }
            }
            
            rotatedTexture.SetPixels(rotatedPixels);
            rotatedTexture.Apply();
            
            // Don't destroy the original texture here since it might be the input mask
            // The caller will handle cleanup
            
            return rotatedTexture;
        }
    }
}
