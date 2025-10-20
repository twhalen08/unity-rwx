using UnityEngine;

namespace RWXLoader
{
    /// <summary>
    /// Custom BMP decoder for files that Unity can't handle natively
    /// </summary>
    public class RWXBmpDecoder : MonoBehaviour
    {
        /// <summary>
        /// Custom BMP decoder for files that Unity can't handle
        /// </summary>
        public Texture2D DecodeBmpTexture(byte[] bmpData, string fileName)
        {
            try
            {
                // Check BMP signature
                if (bmpData.Length < 54 || bmpData[0] != 0x42 || bmpData[1] != 0x4D)
                {
                    return null;
                }

                // Read BMP header
                int fileSize = System.BitConverter.ToInt32(bmpData, 2);
                int dataOffset = System.BitConverter.ToInt32(bmpData, 10);
                int headerSize = System.BitConverter.ToInt32(bmpData, 14);
                int width = System.BitConverter.ToInt32(bmpData, 18);
                int height = System.BitConverter.ToInt32(bmpData, 22);
                short planes = System.BitConverter.ToInt16(bmpData, 26);
                short bitsPerPixel = System.BitConverter.ToInt16(bmpData, 28);
                int compression = System.BitConverter.ToInt32(bmpData, 30);

                // Only support uncompressed BMPs for now
                if (compression != 0)
                {
                    return null;
                }

                // Support common bit depths including 1-bit for masks
                if (bitsPerPixel != 1 && bitsPerPixel != 8 && bitsPerPixel != 24 && bitsPerPixel != 32)
                {
                    return null;
                }

                // Create texture
                int absHeight = Mathf.Abs(height);
                Texture2D texture = new Texture2D(width, absHeight, TextureFormat.RGBA32, false);
                Color[] pixels = new Color[width * absHeight];

                // BMP coordinate system: 
                // - Positive height = bottom-up (most common)
                // - Negative height = top-down (rare)
                // Unity uses top-down, so we need to flip for positive height
                bool isBottomUp = height > 0;

                if (bitsPerPixel == 1)
                {
                    // 1-bit monochrome BMP (perfect for masks)
                    
                    // Calculate row size with padding (each row is padded to 4-byte boundary)
                    int rowSizeInBits = width;
                    int rowSizeInBytes = (rowSizeInBits + 7) / 8; // Round up to nearest byte
                    int paddedRowSize = ((rowSizeInBytes + 3) / 4) * 4; // Pad to 4-byte boundary

                    // Read pixel data
                    for (int y = 0; y < absHeight; y++)
                    {
                        // Calculate source row (BMP stores bottom-up for positive height)
                        int sourceY = isBottomUp ? (absHeight - 1 - y) : y;
                        int rowStart = dataOffset + (sourceY * paddedRowSize);
                        
                        for (int x = 0; x < width; x++)
                        {
                            int byteIndex = rowStart + (x / 8);
                            int bitIndex = 7 - (x % 8); // MSB first
                            
                            if (byteIndex >= bmpData.Length)
                            {
                                Object.DestroyImmediate(texture);
                                return null;
                            }
                            
                            // Extract bit value (1 = white, 0 = black in typical 1-bit BMPs)
                            bool isWhite = ((bmpData[byteIndex] >> bitIndex) & 1) == 1;
                            float grayValue = isWhite ? 1f : 0f;
                            Color pixel = new Color(grayValue, grayValue, grayValue, 1f);
                            
                            // Unity texture coordinates (top-down, left-to-right)
                            int targetIndex = y * width + x;
                            pixels[targetIndex] = pixel;
                        }
                    }
                }
                else
                {
                    // Handle other bit depths (8, 24, 32)
                    int bytesPerPixel = bitsPerPixel / 8;
                    int rowSize = ((width * bitsPerPixel + 31) / 32) * 4;

                    // Read pixel data
                    for (int y = 0; y < absHeight; y++)
                    {
                        // Calculate source row (BMP stores bottom-up for positive height)
                        int sourceY = isBottomUp ? (absHeight - 1 - y) : y;
                        
                        for (int x = 0; x < width; x++)
                        {
                            int pixelIndex = dataOffset + (sourceY * rowSize) + (x * bytesPerPixel);
                            
                            if (pixelIndex + bytesPerPixel > bmpData.Length)
                            {
                                Object.DestroyImmediate(texture);
                                return null;
                            }

                            Color pixel = Color.white;

                            if (bitsPerPixel == 8)
                            {
                                // 8-bit grayscale (assuming no palette for simplicity)
                                float gray = bmpData[pixelIndex] / 255f;
                                pixel = new Color(gray, gray, gray, 1f);
                            }
                            else if (bitsPerPixel == 24)
                            {
                                // 24-bit BGR (note: BMP uses BGR, not RGB)
                                float b = bmpData[pixelIndex] / 255f;
                                float g = bmpData[pixelIndex + 1] / 255f;
                                float r = bmpData[pixelIndex + 2] / 255f;
                                pixel = new Color(r, g, b, 1f);
                            }
                            else if (bitsPerPixel == 32)
                            {
                                // 32-bit BGRA (note: BMP uses BGR, not RGB)
                                float b = bmpData[pixelIndex] / 255f;
                                float g = bmpData[pixelIndex + 1] / 255f;
                                float r = bmpData[pixelIndex + 2] / 255f;
                                float a = bmpData[pixelIndex + 3] / 255f;
                                pixel = new Color(r, g, b, a);
                            }

                            // Unity texture coordinates (top-down, left-to-right)
                            int targetIndex = y * width + x;
                            pixels[targetIndex] = pixel;
                        }
                    }
                }

                texture.SetPixels(pixels);
                texture.Apply();
                
                // Set the texture name for debugging
                texture.name = System.IO.Path.GetFileNameWithoutExtension(fileName);

                return texture;
            }
            catch (System.Exception e)
            {
                return null;
            }
        }

        /// <summary>
        /// Decodes BMP texture specifically for masks with 90-degree clockwise rotation
        /// </summary>
        public Texture2D DecodeBmpMask(byte[] bmpData, string fileName)
        {
            Texture2D texture = DecodeBmpTexture(bmpData, fileName);
            
            if (texture != null)
            {
                // Rotate 90 degrees clockwise for masks
                texture = RotateTexture90Clockwise(texture);
            }
            
            return texture;
        }

        /// <summary>
        /// Decodes BMP texture specifically for double-sided masks with rotation and horizontal flip
        /// </summary>
        public Texture2D DecodeBmpDoubleSidedMask(byte[] bmpData, string fileName)
        {
            Texture2D texture = DecodeBmpTexture(bmpData, fileName);
            
            if (texture != null)
            {
                // First rotate 90 degrees clockwise
                texture = RotateTexture90Clockwise(texture);
                
                // Then flip horizontally to fix double-sided alignment
                texture = FlipTextureHorizontally(texture);
                
            }
            
            return texture;
        }

        /// <summary>
        /// Rotates a texture 90 degrees clockwise
        /// </summary>
        public Texture2D RotateTexture90Clockwise(Texture2D originalTexture)
        {
            Color[] originalPixels = originalTexture.GetPixels();
            int originalWidth = originalTexture.width;
            int originalHeight = originalTexture.height;
            string originalName = originalTexture.name;
            
            // Create new texture with swapped dimensions
            Texture2D rotatedTexture = new Texture2D(originalHeight, originalWidth, originalTexture.format, false);
            rotatedTexture.name = originalName; // Preserve the name
            Color[] rotatedPixels = new Color[originalPixels.Length];
            
            // Rotate 90 degrees clockwise
            for (int y = 0; y < originalHeight; y++)
            {
                for (int x = 0; x < originalWidth; x++)
                {
                    // Original pixel position
                    int originalIndex = y * originalWidth + x;
                    
                    // New position after 90-degree clockwise rotation
                    // (x, y) -> (y, width - 1 - x)
                    int newX = y;
                    int newY = originalWidth - 1 - x;
                    int rotatedIndex = newY * originalHeight + newX;
                    
                    rotatedPixels[rotatedIndex] = originalPixels[originalIndex];
                }
            }
            
            rotatedTexture.SetPixels(rotatedPixels);
            rotatedTexture.Apply();
            
            // Clean up original texture
            Object.DestroyImmediate(originalTexture);
            
            return rotatedTexture;
        }

        /// <summary>
        /// Flips a texture horizontally
        /// </summary>
        public Texture2D FlipTextureHorizontally(Texture2D originalTexture)
        {
            Color[] originalPixels = originalTexture.GetPixels();
            int width = originalTexture.width;
            int height = originalTexture.height;
            string originalName = originalTexture.name;
            
            Texture2D flippedTexture = new Texture2D(width, height, originalTexture.format, false);
            flippedTexture.name = originalName; // Preserve the name
            Color[] flippedPixels = new Color[originalPixels.Length];
            
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int originalIndex = y * width + x;
                    int flippedIndex = y * width + (width - 1 - x);
                    flippedPixels[flippedIndex] = originalPixels[originalIndex];
                }
            }
            
            flippedTexture.SetPixels(flippedPixels);
            flippedTexture.Apply();
            
            // Clean up original texture
            Object.DestroyImmediate(originalTexture);
            
            return flippedTexture;
        }

        /// <summary>
        /// Alternative method that also flips horizontally if needed for specific BMP formats
        /// </summary>
        public Texture2D DecodeBmpTextureWithFlip(byte[] bmpData, string fileName, bool flipHorizontal = false)
        {
            Texture2D texture = DecodeBmpTexture(bmpData, fileName);
            
            if (texture != null && flipHorizontal)
            {
                // Flip horizontally if needed
                Color[] pixels = texture.GetPixels();
                Color[] flippedPixels = new Color[pixels.Length];
                
                int width = texture.width;
                int height = texture.height;
                
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int sourceIndex = y * width + x;
                        int targetIndex = y * width + (width - 1 - x);
                        flippedPixels[targetIndex] = pixels[sourceIndex];
                    }
                }
                
                texture.SetPixels(flippedPixels);
                texture.Apply();
                
            }
            
            return texture;
        }
    }
}
