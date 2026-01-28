using System;
using UnityEngine;

namespace RWXLoader
{
    /// <summary>
    /// Custom BMP decoder for files that Unity can't handle natively
    /// </summary>
    public class RWXBmpDecoder : MonoBehaviour
    {
        public struct BmpDecodedData
        {
            public int Width;
            public int Height;
            public Color32[] Pixels;

            public bool IsValid => Width > 0 && Height > 0 && Pixels != null && Pixels.Length == Width * Height;
        }

        public static BmpDecodedData DecodeBmpPixels(byte[] bmpData)
        {
            try
            {
                if (bmpData.Length < 54 || bmpData[0] != 0x42 || bmpData[1] != 0x4D)
                {
                    return default;
                }

                int dataOffset = System.BitConverter.ToInt32(bmpData, 10);
                int width = System.BitConverter.ToInt32(bmpData, 18);
                int height = System.BitConverter.ToInt32(bmpData, 22);
                short bitsPerPixel = System.BitConverter.ToInt16(bmpData, 28);
                int compression = System.BitConverter.ToInt32(bmpData, 30);

                if (compression != 0)
                {
                    return default;
                }

                if (bitsPerPixel != 1 && bitsPerPixel != 8 && bitsPerPixel != 24 && bitsPerPixel != 32)
                {
                    return default;
                }

                int absHeight = Math.Abs(height);
                bool isBottomUp = height > 0;
                Color32[] pixels = new Color32[width * absHeight];

                if (bitsPerPixel == 1)
                {
                    int rowSizeInBits = width;
                    int rowSizeInBytes = (rowSizeInBits + 7) / 8;
                    int paddedRowSize = ((rowSizeInBytes + 3) / 4) * 4;

                    for (int y = 0; y < absHeight; y++)
                    {
                        int sourceY = isBottomUp ? (absHeight - 1 - y) : y;
                        int rowStart = dataOffset + (sourceY * paddedRowSize);

                        for (int x = 0; x < width; x++)
                        {
                            int byteIndex = rowStart + (x / 8);
                            int bitIndex = 7 - (x % 8);

                            if (byteIndex >= bmpData.Length)
                            {
                                return default;
                            }

                            bool isWhite = ((bmpData[byteIndex] >> bitIndex) & 1) == 1;
                            byte grayValue = isWhite ? (byte)255 : (byte)0;
                            int targetIndex = y * width + x;
                            pixels[targetIndex] = new Color32(grayValue, grayValue, grayValue, 255);
                        }
                    }
                }
                else
                {
                    int bytesPerPixel = bitsPerPixel / 8;
                    int rowSize = ((width * bitsPerPixel + 31) / 32) * 4;

                    for (int y = 0; y < absHeight; y++)
                    {
                        int sourceY = isBottomUp ? (absHeight - 1 - y) : y;

                        for (int x = 0; x < width; x++)
                        {
                            int pixelIndex = dataOffset + (sourceY * rowSize) + (x * bytesPerPixel);

                            if (pixelIndex + bytesPerPixel > bmpData.Length)
                            {
                                return default;
                            }

                            byte r;
                            byte g;
                            byte b;
                            byte a = 255;

                            if (bitsPerPixel == 8)
                            {
                                byte gray = bmpData[pixelIndex];
                                r = gray;
                                g = gray;
                                b = gray;
                            }
                            else if (bitsPerPixel == 24)
                            {
                                b = bmpData[pixelIndex];
                                g = bmpData[pixelIndex + 1];
                                r = bmpData[pixelIndex + 2];
                            }
                            else
                            {
                                b = bmpData[pixelIndex];
                                g = bmpData[pixelIndex + 1];
                                r = bmpData[pixelIndex + 2];
                                a = bmpData[pixelIndex + 3];
                            }

                            int targetIndex = y * width + x;
                            pixels[targetIndex] = new Color32(r, g, b, a);
                        }
                    }
                }

                return new BmpDecodedData
                {
                    Width = width,
                    Height = absHeight,
                    Pixels = pixels
                };
            }
            catch (System.Exception)
            {
                return default;
            }
        }

        /// <summary>
        /// Custom BMP decoder for files that Unity can't handle
        /// </summary>
        public Texture2D DecodeBmpTexture(byte[] bmpData, string fileName)
        {
            try
            {
                BmpDecodedData decoded = DecodeBmpPixels(bmpData);
                if (!decoded.IsValid)
                {
                    return null;
                }

                Texture2D texture = new Texture2D(decoded.Width, decoded.Height, TextureFormat.RGBA32, false);
                texture.SetPixels32(decoded.Pixels);
                texture.Apply();
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
