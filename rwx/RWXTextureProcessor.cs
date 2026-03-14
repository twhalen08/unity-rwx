using UnityEngine;
using System;
using System.Collections;

namespace RWXLoader
{
    /// <summary>
    /// Handles texture processing operations like combining textures with masks
    /// </summary>
    public class RWXTextureProcessor : MonoBehaviour
    {
        private readonly System.Collections.Generic.Dictionary<string, Texture2D> combinedTextureCache = new System.Collections.Generic.Dictionary<string, Texture2D>();
        /// <summary>
        /// Combines a main texture with a mask texture for alpha channel
        /// RWX masks: Black = transparent, White = opaque
        /// </summary>
        public Texture2D CombineTextureWithMask(Texture2D mainTexture, Texture2D maskTexture)
        {
            string cacheKey = BuildCombinedCacheKey(mainTexture, maskTexture);
            if (combinedTextureCache.TryGetValue(cacheKey, out Texture2D cached) && cached != null)
            {
                return cached;
            }

            Texture2D combinedTexture = new Texture2D(mainTexture.width, mainTexture.height, TextureFormat.RGBA32, false);
            combinedTexture.name = mainTexture.name + "_combined";

            Color32[] mainPixels = mainTexture.GetPixels32();
            Color32[] maskPixels = maskTexture.GetPixels32();
            Color32[] combinedPixels = new Color32[mainPixels.Length];

            bool shouldInvertMask = ShouldInvertMask(maskTexture);
            int width = mainTexture.width;
            int height = mainTexture.height;
            int maskWidth = maskTexture.width;
            int maskHeight = maskTexture.height;

            for (int y = 0; y < height; y++)
            {
                int srcY = height - 1 - y; // vertical flip
                int maskY = (srcY * maskHeight) / height;
                int rowStart = y * width;

                for (int x = 0; x < width; x++)
                {
                    int i = rowStart + x;
                    int maskX = (x * maskWidth) / width;
                    int maskIdx = maskY * maskWidth + maskX;

                    Color32 mainColor = mainPixels[i];
                    Color32 maskColor = maskPixels[maskIdx];
                    int grayscale = (maskColor.r + maskColor.g + maskColor.b) / 3;
                    byte alpha = shouldInvertMask ? (byte)(255 - grayscale) : (byte)grayscale;
                    combinedPixels[i] = new Color32(mainColor.r, mainColor.g, mainColor.b, alpha);
                }
            }

            combinedTexture.SetPixels32(combinedPixels);
            combinedTexture.Apply();
            combinedTextureCache[cacheKey] = combinedTexture;
            return combinedTexture;
        }

        /// <summary>
        /// Combines a main texture with a mask texture for alpha channel over multiple frames.
        /// This avoids long frame stalls on large textures.
        /// </summary>
        public IEnumerator CombineTextureWithMaskAsync(
            Texture2D mainTexture,
            Texture2D maskTexture,
            Action<Texture2D> onComplete,
            int rowsPerFrame = 64)
        {
            string cacheKey = BuildCombinedCacheKey(mainTexture, maskTexture);
            if (combinedTextureCache.TryGetValue(cacheKey, out Texture2D cached) && cached != null)
            {
                onComplete?.Invoke(cached);
                yield break;
            }

            Texture2D combinedTexture = new Texture2D(mainTexture.width, mainTexture.height, TextureFormat.RGBA32, false);
            combinedTexture.name = mainTexture.name + "_combined";

            Color32[] mainPixels = mainTexture.GetPixels32();
            Color32[] maskPixels = maskTexture.GetPixels32();
            Color32[] combinedPixels = new Color32[mainPixels.Length];

            bool shouldInvertMask = ShouldInvertMask(maskTexture);
            int width = mainTexture.width;
            int height = mainTexture.height;
            int maskWidth = maskTexture.width;
            int maskHeight = maskTexture.height;

            for (int y = 0; y < height; y++)
            {
                int srcY = height - 1 - y; // vertical flip
                int maskY = (srcY * maskHeight) / height;
                int rowStart = y * width;

                for (int x = 0; x < width; x++)
                {
                    int i = rowStart + x;
                    int maskX = (x * maskWidth) / width;
                    int maskIdx = maskY * maskWidth + maskX;

                    Color32 mainColor = mainPixels[i];
                    Color32 maskColor = maskPixels[maskIdx];
                    int grayscale = (maskColor.r + maskColor.g + maskColor.b) / 3;
                    byte alpha = shouldInvertMask ? (byte)(255 - grayscale) : (byte)grayscale;
                    combinedPixels[i] = new Color32(mainColor.r, mainColor.g, mainColor.b, alpha);
                }

                if ((y + 1) % rowsPerFrame == 0)
                {
                    yield return null;
                }
            }

            combinedTexture.SetPixels32(combinedPixels);
            combinedTexture.Apply();
            combinedTextureCache[cacheKey] = combinedTexture;
            onComplete?.Invoke(combinedTexture);
        }

        private static bool ShouldInvertMask(Texture2D maskTexture)
        {
            return maskTexture != null && (maskTexture.name.Contains("tbtree") || maskTexture.name.Contains("003m"));
        }

        private static string BuildCombinedCacheKey(Texture2D mainTexture, Texture2D maskTexture)
        {
            string mainId = mainTexture != null ? mainTexture.GetInstanceID().ToString() : "null";
            string maskId = maskTexture != null ? maskTexture.GetInstanceID().ToString() : "null";
            return mainId + "::" + maskId;
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
            if (mainTexture != null && maskTexture != null)
            {
                // Combine main texture with mask for alpha channel
                Texture2D combinedTexture = CombineTextureWithMask(mainTexture, maskTexture);
                material.mainTexture = combinedTexture;
                
                // For Standard shader, also set the albedo texture
                if (material.shader.name.Contains("Standard"))
                {
                    material.SetTexture("_MainTex", combinedTexture);
                    material.SetTexture("_AlbedoMap", combinedTexture);
                }
                
            }
            else if (mainTexture != null)
            {
                // Just apply main texture
                material.mainTexture = mainTexture;
                
                // For Standard shader, also set the albedo texture
                if (material.shader.name.Contains("Standard"))
                {
                    material.SetTexture("_MainTex", mainTexture);
                    material.SetTexture("_AlbedoMap", mainTexture);
                }
                
            }
            else if (maskTexture != null)
            {
                // Use mask as main texture (grayscale)
                material.mainTexture = maskTexture;
                
                // For Standard shader, also set the albedo texture
                if (material.shader.name.Contains("Standard"))
                {
                    material.SetTexture("_MainTex", maskTexture);
                    material.SetTexture("_AlbedoMap", maskTexture);
                }
                
            }

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
            
            // Force material to update
            material.EnableKeyword("_MAINTEX_ON");
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
        /// Applies textures and masks to a material while spreading heavy mask processing over multiple frames.
        /// </summary>
        public IEnumerator ApplyTexturesWithMaskAsync(Material material, Texture2D mainTexture, Texture2D maskTexture, RWXMaterial rwxMaterial)
        {
            if (mainTexture != null && maskTexture != null)
            {
                Texture2D combinedTexture = null;
                yield return CombineTextureWithMaskAsync(mainTexture, maskTexture, texture => combinedTexture = texture);
                material.mainTexture = combinedTexture;

                if (material.shader.name.Contains("Standard"))
                {
                    material.SetTexture("_MainTex", combinedTexture);
                    material.SetTexture("_AlbedoMap", combinedTexture);
                }
            }
            else if (mainTexture != null)
            {
                material.mainTexture = mainTexture;

                if (material.shader.name.Contains("Standard"))
                {
                    material.SetTexture("_MainTex", mainTexture);
                    material.SetTexture("_AlbedoMap", mainTexture);
                }
            }
            else if (maskTexture != null)
            {
                material.mainTexture = maskTexture;

                if (material.shader.name.Contains("Standard"))
                {
                    material.SetTexture("_MainTex", maskTexture);
                    material.SetTexture("_AlbedoMap", maskTexture);
                }
            }

            if (rwxMaterial.tint)
            {
                Color baseColor = rwxMaterial.GetEffectiveColor();
                material.color = new Color(baseColor.r, baseColor.g, baseColor.b, rwxMaterial.opacity);
            }
            else
            {
                material.color = new Color(1f, 1f, 1f, rwxMaterial.opacity);
            }

            material.EnableKeyword("_MAINTEX_ON");
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
        private void OnDestroy()
        {
            foreach (var kv in combinedTextureCache)
            {
                if (kv.Value != null)
                {
                    Object.DestroyImmediate(kv.Value);
                }
            }
            combinedTextureCache.Clear();
        }

    }
}
