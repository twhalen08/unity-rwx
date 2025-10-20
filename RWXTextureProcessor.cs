using UnityEngine;

namespace RWXLoader
{
    /// <summary>
    /// Handles texture processing operations like combining textures with masks
    /// </summary>
    public class RWXTextureProcessor : MonoBehaviour
    {
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
                material.color = new Color(rwxMaterial.color.r, rwxMaterial.color.g, rwxMaterial.color.b, rwxMaterial.opacity);
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
                material.color = new Color(rwxMaterial.color.r, rwxMaterial.color.g, rwxMaterial.color.b, rwxMaterial.opacity);
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
