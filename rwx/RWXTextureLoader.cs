using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace RWXLoader
{
    /// <summary>
    /// Handles texture loading from various sources (local files, remote URLs, ZIP archives)
    /// </summary>
    public class RWXTextureLoader : MonoBehaviour
    {
        [Header("Settings")]
        public string textureFolder = "Textures";
        public string textureExtension = ".jpg";

        private readonly Dictionary<string, Texture2D> textureCache = new Dictionary<string, Texture2D>();
        private IRwxTextureResolver textureResolver;

        private void Start()
        {
        }

        public void SetTextureSource(IRwxTextureResolver resolver)
        {
            textureResolver = resolver;
        }

        public void SetTextureSource(string objectPath, string password)
        {
            textureResolver = CreateDefaultResolver(objectPath, password);
        }

        /// <summary>
        /// Ensures texture name has proper extension - defaults to .jpg if no extension specified
        /// For masks, defaults to .bmp
        /// </summary>
        private string EnsureTextureExtension(string textureName, bool isMask = false)
        {
            if (string.IsNullOrEmpty(textureName))
                return textureName;

            // Check if texture already has an extension
            string extension = Path.GetExtension(textureName);
            if (!string.IsNullOrEmpty(extension))
            {
                return textureName; // Already has extension
            }

            // No extension found, add default extension
            return textureName + (isMask ? ".bmp" : ".jpg");
        }

        private bool IsMaskFile(string fileName)
        {
            string lowerName = fileName.ToLower();
            return lowerName.Contains("mask") || lowerName.Contains("_m") || fileName.EndsWith("m.bmp") || lowerName.Contains("tl01m");
        }

        private bool TryDecompressGzip(byte[] data, out byte[] decompressedData)
        {
            try
            {
                using (var inputStream = new MemoryStream(data))
                using (var gzipStream = new GZipStream(inputStream, CompressionMode.Decompress))
                using (var outputStream = new MemoryStream())
                {
                    gzipStream.CopyTo(outputStream);
                    decompressedData = outputStream.ToArray();
                    return true;
                }
            }
            catch
            {
                decompressedData = Array.Empty<byte>();
                return false;
            }
        }

        private Texture2D LoadDdsTexture(byte[] data, string fileName)
        {
            // Basic DDS loader supporting common compressed formats
            try
            {
                if (data == null || data.Length < 128)
                {
                    return null;
                }

                if (!(data[0] == 'D' && data[1] == 'D' && data[2] == 'S' && data[3] == ' '))
                {
                    return null;
                }

                int height = BitConverter.ToInt32(data, 12);
                int width = BitConverter.ToInt32(data, 16);
                int mipMapCount = Math.Max(1, BitConverter.ToInt32(data, 28));
                string fourCC = System.Text.Encoding.ASCII.GetString(data, 84, 4);
                int pixelFormatFlags = BitConverter.ToInt32(data, 80);
                int rgbBitCount = BitConverter.ToInt32(data, 88);
                int alphaMask = BitConverter.ToInt32(data, 104);
                int headerSize = 128;

                TextureFormat format;
                bool isBlockCompressed = true;
                int bytesPerPixel = 0;
                if (fourCC == "DX10" && data.Length >= 148)
                {
                    // DX10 header provides DXGI format
                    int dxgiFormat = BitConverter.ToInt32(data, 128);
                    switch (dxgiFormat)
                    {
                        case 71: // BC1_UNORM
                            format = TextureFormat.DXT1;
                            break;
                        case 74: // BC2_UNORM
                            format = TextureFormat.DXT5; // Closest supported in Unity runtime
                            break;
                        case 77: // BC3_UNORM
                            format = TextureFormat.DXT5;
                            break;
                        case 80: // BC5_UNORM
                            format = TextureFormat.BC5;
                            break;
                        case 98: // BC7_UNORM
                            format = TextureFormat.BC7;
                            break;
                        case 28: // R8G8B8A8_UNORM (uncompressed)
                        case 87: // R8G8B8A8_UNORM_SRGB
                            format = TextureFormat.RGBA32;
                            isBlockCompressed = false;
                            bytesPerPixel = 4;
                            break;
                        case 80: // BC5_UNORM
                            format = TextureFormat.BC5;
                            break;
                        case 98: // BC7_UNORM
                            format = TextureFormat.BC7;
                            break;
                        default:
                            return null;
                    }
                    headerSize = 148; // DDS header + DX10 header
                }
                else
                {
                    switch (fourCC)
                    {
                        case "DXT1":
                            format = TextureFormat.DXT1;
                            break;
                        case "DXT3":
                            format = TextureFormat.DXT5; // Unity doesn't expose DXT3 separately at runtime; use DXT5 fallback
                            break;
                        case "DXT5":
                            format = TextureFormat.DXT5;
                            break;
                        case "ATI2":
                        case "BC5 ":
                            format = TextureFormat.BC5;
                            break;
                        case "DX10": // DX10 without extra header (unlikely, but guard)
                            format = TextureFormat.RGBA32;
                            isBlockCompressed = false;
                            bytesPerPixel = 4;
                            break;
                        case "ARGB": // Uncompressed legacy
                            format = TextureFormat.RGBA32;
                            isBlockCompressed = false;
                            bytesPerPixel = 4;
                            break;
                        default:
                        {
                            // Handle uncompressed DDS without a FourCC (RGB/RGBA)
                            const int DDPF_RGB = 0x40;
                            if (string.IsNullOrWhiteSpace(fourCC) && (pixelFormatFlags & DDPF_RGB) == DDPF_RGB && rgbBitCount == 32)
                            {
                                format = TextureFormat.RGBA32;
                                isBlockCompressed = false;
                                bytesPerPixel = 4;
                                break;
                            }

                            return null; // Unsupported DDS format
                        }
                    }
                }

                if (!SystemInfo.SupportsTextureFormat(format))
                {
                    // Fallbacks for platforms lacking BC5/BC7
                    if ((format == TextureFormat.BC5 || format == TextureFormat.BC7) && SystemInfo.SupportsTextureFormat(TextureFormat.DXT5))
                    {
                        format = TextureFormat.DXT5;
                    }
                    else if (SystemInfo.SupportsTextureFormat(TextureFormat.DXT1))
                    {
                        format = TextureFormat.DXT1;
                    }
                    else if (SystemInfo.SupportsTextureFormat(TextureFormat.RGBA32))
                    {
                        format = TextureFormat.RGBA32;
                        isBlockCompressed = false;
                        bytesPerPixel = 4;
                    }
                    else
                    {
                        return null;
                    }
                }

                if (data.Length <= headerSize)
                {
                    return null;
                }

                byte[] dxtData = new byte[data.Length - headerSize];
                Buffer.BlockCopy(data, headerSize, dxtData, 0, dxtData.Length);

                // Ensure mipmap count matches available data to avoid LoadRawTextureData failures
                int blockSize = (format == TextureFormat.DXT1) ? 8 : 16;
                int computedMipCount = 0;
                int offset = 0;
                int mipWidth = width;
                int mipHeight = height;

                while (computedMipCount < mipMapCount)
                {
                    int mipSize;
                    if (isBlockCompressed)
                    {
                        int blockCountX = Math.Max(1, (mipWidth + 3) / 4);
                        int blockCountY = Math.Max(1, (mipHeight + 3) / 4);
                        mipSize = blockCountX * blockCountY * blockSize;
                    }
                    else
                    {
                        mipSize = mipWidth * mipHeight * Math.Max(1, bytesPerPixel);
                    }

                    if (offset + mipSize > dxtData.Length)
                    {
                        break;
                    }

                    offset += mipSize;
                    computedMipCount++;
                    mipWidth = Math.Max(1, mipWidth / 2);
                    mipHeight = Math.Max(1, mipHeight / 2);
                }

                if (computedMipCount == 0)
                {
                    return null;
                }

                int usedDataLength = offset;
                if (usedDataLength < dxtData.Length)
                {
                    byte[] trimmedData = new byte[usedDataLength];
                    Buffer.BlockCopy(dxtData, 0, trimmedData, 0, usedDataLength);
                    dxtData = trimmedData;
                }

                bool hasMipMaps = computedMipCount > 1;
                Texture2D texture = new Texture2D(width, height, format, hasMipMaps);
                texture.LoadRawTextureData(dxtData);
                texture.Apply(false, true);
                texture.name = Path.GetFileNameWithoutExtension(fileName);
                return texture;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Loads texture synchronously from local files
        /// </summary>
        public Texture2D LoadTextureSync(string textureName)
        {
            return LoadTextureSync(textureName, false);
        }

        /// <summary>
        /// Loads texture synchronously from local files with double-sided support
        /// </summary>
        public Texture2D LoadTextureSync(string textureName, bool isDoubleSided)
        {
            string cacheKey = textureName + (isDoubleSided ? "_DS" : "");
            if (textureCache.TryGetValue(cacheKey, out Texture2D cachedTexture))
            {
                return cachedTexture;
            }

            // Try multiple file extensions
            string[] extensions = { textureExtension, ".jpg", ".jpeg", ".png", ".bmp", ".tga", ".dds", ".dds.gz" };
            
            // Try multiple base paths (including cached textures)
            List<string> basePathsList = new List<string>
            {
                Path.Combine(Application.streamingAssetsPath, textureFolder),
                Path.Combine(Application.persistentDataPath, textureFolder),
                Path.Combine(Application.dataPath, textureFolder),
                textureFolder // Relative to project root
            };

            foreach (string basePath in basePathsList)
            {

                // First try with the texture name as-is (in case it already has extension)
                string directPath = Path.Combine(basePath, textureName);
                if (File.Exists(directPath))
                {
                    Texture2D texture = LoadTextureFromFile(directPath, isDoubleSided);
                    if (texture != null)
                    {
                        textureCache[cacheKey] = texture;
                        return texture;
                    }
                }

                // Then try adding extensions if the texture name doesn't have one
                if (string.IsNullOrEmpty(Path.GetExtension(textureName)))
                {
                    foreach (string ext in extensions)
                    {
                        string fullPath = Path.Combine(basePath, textureName + ext);
                        if (File.Exists(fullPath))
                        {
                            Texture2D texture = LoadTextureFromFile(fullPath, isDoubleSided);
                            if (texture != null)
                            {
                                textureCache[cacheKey] = texture;
                                return texture;
                            }
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Loads texture from file path
        /// </summary>
        public Texture2D LoadTextureFromFile(string filePath)
        {
            return LoadTextureFromFile(filePath, false);
        }

        /// <summary>
        /// Loads texture from file path with double-sided support
        /// </summary>
        public Texture2D LoadTextureFromFile(string filePath, bool isDoubleSided)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    return null;
                }

                byte[] fileData = File.ReadAllBytes(filePath);
                string fileName = Path.GetFileName(filePath);
                
                // Determine if this is a mask based on file name patterns
                string effectiveFileName = fileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(fileName) : fileName;
                bool isMask = IsMaskFile(effectiveFileName);

                if (fileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) && TryDecompressGzip(fileData, out byte[] decompressedData))
                {
                    fileData = decompressedData;
                    fileName = effectiveFileName;
                }

                return LoadTextureFromBytes(fileData, fileName, isMask, isDoubleSided);
            }
            catch (System.Exception e)
            {
                return null;
            }
        }

        /// <summary>
        /// Loads texture from byte array with enhanced error handling
        /// </summary>
        public Texture2D LoadTextureFromBytes(byte[] data, string fileName, bool isMask)
        {
            return LoadTextureFromBytes(data, fileName, isMask, false);
        }

        /// <summary>
        /// Loads texture from byte array with enhanced error handling and double-sided support
        /// </summary>
        public Texture2D LoadTextureFromBytes(byte[] data, string fileName, bool isMask, bool isDoubleSided)
        {
            try
            {
                string effectiveFileName = fileName;
                byte[] workingData = data;

                if (fileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) && TryDecompressGzip(data, out byte[] decompressedData))
                {
                    workingData = decompressedData;
                    effectiveFileName = Path.GetFileNameWithoutExtension(fileName);
                }

                if (effectiveFileName.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                {
                    Texture2D ddsTexture = LoadDdsTexture(workingData, effectiveFileName);
                    if (ddsTexture != null)
                    {
                        return ddsTexture;
                    }
                }

                // Create texture with appropriate format
                Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);

                // Try to load the image data
                if (texture.LoadImage(workingData))
                {
                    // Set the texture name for debugging
                    texture.name = Path.GetFileNameWithoutExtension(effectiveFileName);
                    return texture;
                }
                else
                {
                    Object.DestroyImmediate(texture);
                    
                    // For BMP files, try custom decoder
                    if (effectiveFileName.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
                    {
                        RWXBmpDecoder bmpDecoder = GetComponent<RWXBmpDecoder>();
                        if (bmpDecoder != null)
                        {
                            // FIXED: Use regular BMP decoder without rotation for all textures and masks
                            // The automatic rotation was causing mask orientation issues
                            texture = bmpDecoder.DecodeBmpTexture(workingData, effectiveFileName);
                            
                            // Set the texture name for debugging
                            if (texture != null)
                            {
                                texture.name = Path.GetFileNameWithoutExtension(effectiveFileName);
                            }
                            
                            return texture;
                        }
                    }
                    
                    return null;
                }
            }
            catch (System.Exception e)
            {
                return null;
            }
        }

        /// <summary>
        /// Loads texture/mask from ZIP archive first, then falls back to individual download
        /// </summary>
        public IEnumerator LoadTextureFromZipOrRemote(string textureName, bool isMask, System.Action<Texture2D> onComplete)
        {
            return LoadTextureFromZipOrRemote(textureName, isMask, false, onComplete);
        }

        /// <summary>
        /// Loads texture/mask from ZIP archive first, then falls back to individual download with double-sided support
        /// </summary>
        public IEnumerator LoadTextureFromZipOrRemote(string textureName, bool isMask, bool isDoubleSided, System.Action<Texture2D> onComplete)
        {
            string textureNameWithExt = EnsureTextureExtension(textureName, isMask);

            if (textureResolver == null)
            {
                onComplete?.Invoke(null);
                yield break;
            }

            bool resolved = false;
            RwxResolvedTextureData resolvedData = null;

            yield return textureResolver.ResolveTextureBytes(textureNameWithExt, isMask ? RwxTextureUsage.Mask : RwxTextureUsage.Diffuse, (success, data, _) =>
            {
                resolved = success;
                resolvedData = data;
            });

            if (!resolved || resolvedData?.Bytes == null || resolvedData.Bytes.Length == 0)
            {
                onComplete?.Invoke(null);
                yield break;
            }

            Texture2D resolvedTexture = LoadTextureFromBytes(resolvedData.Bytes, resolvedData.ResolvedName ?? textureNameWithExt, isMask, isDoubleSided);
            if (resolvedTexture != null)
            {
                string cacheKey = textureName + (isDoubleSided ? "_DS" : "");
                textureCache[cacheKey] = resolvedTexture;
                onComplete?.Invoke(resolvedTexture);
                yield break;
            }

            onComplete?.Invoke(null);
        }

        private static IRwxTextureResolver CreateDefaultResolver(string objectPath, string password)
        {
            if (string.IsNullOrEmpty(objectPath))
            {
                return null;
            }

            RWXAssetManager assetManager = RWXAssetManager.Instance;
            return assetManager == null ? null : new VirtualParadiseTextureResolver(assetManager, objectPath, password);
        }

        /// <summary>
        /// Clears the texture cache
        /// </summary>
        public void ClearCache()
        {
            foreach (var texture in textureCache.Values)
            {
                if (texture != null)
                {
                    Object.DestroyImmediate(texture);
                }
            }
            textureCache.Clear();
        }
    }
}
