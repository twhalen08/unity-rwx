using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using UnityEngine;

namespace RWXLoader
{
    /// <summary>
    /// Handles texture loading from various sources (local files, remote URLs, ZIP archives)
    /// </summary>
    public class RWXTextureLoader : MonoBehaviour
    {
        private struct PreparedTextureData
        {
            public string EffectiveFileName;
            public byte[] ImageData;
            public bool HasDdsData;
            public DdsPreparedData DdsData;
            public bool HasBmpData;
            public BmpPreparedData BmpData;
        }

        private struct DdsPreparedData
        {
            public int Width;
            public int Height;
            public TextureFormat Format;
            public bool HasMipMaps;
            public byte[] RawData;
            public string TextureName;
        }

        private struct BmpPreparedData
        {
            public int Width;
            public int Height;
            public Color32[] Pixels;
            public string TextureName;
        }

        [Header("Settings")]
        public string textureFolder = "Textures";
        public string textureExtension = ".jpg";

        private readonly Dictionary<string, Texture2D> textureCache = new Dictionary<string, Texture2D>();
        private RWXAssetManager assetManager;
        private string currentObjectPath;
        private string objectPathPassword;

        private void Start()
        {
            assetManager = RWXAssetManager.Instance;
        }

        public void SetTextureSource(string objectPath, string password)
        {
            currentObjectPath = objectPath;
            objectPathPassword = password;
            if (assetManager == null)
            {
                assetManager = RWXAssetManager.Instance;
            }
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

        private bool TryPrepareDdsTextureData(byte[] data, string fileName, out DdsPreparedData prepared)
        {
            prepared = new DdsPreparedData();
            try
            {
                if (data == null || data.Length < 128)
                {
                    return false;
                }

                if (!(data[0] == 'D' && data[1] == 'D' && data[2] == 'S' && data[3] == ' '))
                {
                    return false;
                }

                int height = BitConverter.ToInt32(data, 12);
                int width = BitConverter.ToInt32(data, 16);
                int mipMapCount = Math.Max(1, BitConverter.ToInt32(data, 28));
                string fourCC = System.Text.Encoding.ASCII.GetString(data, 84, 4);
                int pixelFormatFlags = BitConverter.ToInt32(data, 80);
                int rgbBitCount = BitConverter.ToInt32(data, 88);
                int headerSize = 128;

                TextureFormat format;
                bool isBlockCompressed = true;
                int bytesPerPixel = 0;
                if (fourCC == "DX10" && data.Length >= 148)
                {
                    int dxgiFormat = BitConverter.ToInt32(data, 128);
                    switch (dxgiFormat)
                    {
                        case 71: // BC1_UNORM
                            format = TextureFormat.DXT1;
                            break;
                        case 74: // BC2_UNORM
                            format = TextureFormat.DXT5;
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
                        default:
                            return false;
                    }
                    headerSize = 148;
                }
                else
                {
                    switch (fourCC)
                    {
                        case "DXT1":
                            format = TextureFormat.DXT1;
                            break;
                        case "DXT3":
                            format = TextureFormat.DXT5;
                            break;
                        case "DXT5":
                            format = TextureFormat.DXT5;
                            break;
                        case "ATI2":
                        case "BC5 ":
                            format = TextureFormat.BC5;
                            break;
                        case "DX10":
                            format = TextureFormat.RGBA32;
                            isBlockCompressed = false;
                            bytesPerPixel = 4;
                            break;
                        case "ARGB":
                            format = TextureFormat.RGBA32;
                            isBlockCompressed = false;
                            bytesPerPixel = 4;
                            break;
                        default:
                        {
                            const int DDPF_RGB = 0x40;
                            if (string.IsNullOrWhiteSpace(fourCC) && (pixelFormatFlags & DDPF_RGB) == DDPF_RGB && rgbBitCount == 32)
                            {
                                format = TextureFormat.RGBA32;
                                isBlockCompressed = false;
                                bytesPerPixel = 4;
                                break;
                            }

                            return false;
                        }
                    }
                }

                if (!SystemInfo.SupportsTextureFormat(format))
                {
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
                        return false;
                    }
                }

                if (data.Length <= headerSize)
                {
                    return false;
                }

                byte[] dxtData = new byte[data.Length - headerSize];
                Buffer.BlockCopy(data, headerSize, dxtData, 0, dxtData.Length);

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
                    return false;
                }

                int usedDataLength = offset;
                if (usedDataLength < dxtData.Length)
                {
                    byte[] trimmedData = new byte[usedDataLength];
                    Buffer.BlockCopy(dxtData, 0, trimmedData, 0, usedDataLength);
                    dxtData = trimmedData;
                }

                prepared = new DdsPreparedData
                {
                    Width = width,
                    Height = height,
                    Format = format,
                    HasMipMaps = computedMipCount > 1,
                    RawData = dxtData,
                    TextureName = Path.GetFileNameWithoutExtension(fileName)
                };

                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool TryPrepareBmpTextureData(byte[] bmpData, string fileName, out BmpPreparedData prepared)
        {
            prepared = new BmpPreparedData();
            try
            {
                if (bmpData.Length < 54 || bmpData[0] != 0x42 || bmpData[1] != 0x4D)
                {
                    return false;
                }

                int dataOffset = System.BitConverter.ToInt32(bmpData, 10);
                int width = System.BitConverter.ToInt32(bmpData, 18);
                int height = System.BitConverter.ToInt32(bmpData, 22);
                short bitsPerPixel = System.BitConverter.ToInt16(bmpData, 28);
                int compression = System.BitConverter.ToInt32(bmpData, 30);

                if (compression != 0)
                {
                    return false;
                }

                if (bitsPerPixel != 1 && bitsPerPixel != 8 && bitsPerPixel != 24 && bitsPerPixel != 32)
                {
                    return false;
                }

                int absHeight = Math.Abs(height);
                int absWidth = Math.Abs(width);
                Color32[] pixels = new Color32[absWidth * absHeight];

                bool isBottomUp = height > 0;

                if (bitsPerPixel == 1)
                {
                    int rowSizeInBits = absWidth;
                    int rowSizeInBytes = (rowSizeInBits + 7) / 8;
                    int paddedRowSize = ((rowSizeInBytes + 3) / 4) * 4;

                    for (int y = 0; y < absHeight; y++)
                    {
                        int sourceY = isBottomUp ? (absHeight - 1 - y) : y;
                        int rowStart = dataOffset + (sourceY * paddedRowSize);

                        for (int x = 0; x < absWidth; x++)
                        {
                            int byteIndex = rowStart + (x / 8);
                            int bitIndex = 7 - (x % 8);

                            if (byteIndex >= bmpData.Length)
                            {
                                return false;
                            }

                            bool isWhite = ((bmpData[byteIndex] >> bitIndex) & 1) == 1;
                            byte gray = (byte)(isWhite ? 255 : 0);
                            int targetIndex = y * absWidth + x;
                            pixels[targetIndex] = new Color32(gray, gray, gray, 255);
                        }
                    }
                }
                else
                {
                    int bytesPerPixel = bitsPerPixel / 8;
                    int rowSize = ((absWidth * bitsPerPixel + 31) / 32) * 4;

                    for (int y = 0; y < absHeight; y++)
                    {
                        int sourceY = isBottomUp ? (absHeight - 1 - y) : y;

                        for (int x = 0; x < absWidth; x++)
                        {
                            int pixelIndex = dataOffset + (sourceY * rowSize) + (x * bytesPerPixel);

                            if (pixelIndex + bytesPerPixel > bmpData.Length)
                            {
                                return false;
                            }

                            byte b = bmpData[pixelIndex];
                            byte g = bmpData[pixelIndex + 1];
                            byte r = bmpData[pixelIndex + 2];
                            byte a = bytesPerPixel == 4 ? bmpData[pixelIndex + 3] : (byte)255;

                            int targetIndex = y * absWidth + x;
                            pixels[targetIndex] = new Color32(r, g, b, a);
                        }
                    }
                }

                prepared = new BmpPreparedData
                {
                    Width = absWidth,
                    Height = absHeight,
                    Pixels = pixels,
                    TextureName = Path.GetFileNameWithoutExtension(fileName)
                };

                return true;
            }
            catch
            {
                return false;
            }
        }

        private PreparedTextureData PrepareTextureData(byte[] data, string fileName)
        {
            var prepared = new PreparedTextureData
            {
                EffectiveFileName = fileName,
                ImageData = data,
                HasDdsData = false,
                HasBmpData = false
            };

            if (fileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) && TryDecompressGzip(data, out byte[] decompressedData))
            {
                prepared.ImageData = decompressedData;
                prepared.EffectiveFileName = Path.GetFileNameWithoutExtension(fileName);
            }

            string effectiveFileName = prepared.EffectiveFileName;

            if (effectiveFileName.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            {
                if (TryPrepareDdsTextureData(prepared.ImageData, effectiveFileName, out DdsPreparedData ddsData))
                {
                    prepared.HasDdsData = true;
                    prepared.DdsData = ddsData;
                    return prepared;
                }
            }

            if (effectiveFileName.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
            {
                if (TryPrepareBmpTextureData(prepared.ImageData, effectiveFileName, out BmpPreparedData bmpData))
                {
                    prepared.HasBmpData = true;
                    prepared.BmpData = bmpData;
                }
            }

            return prepared;
        }

        private Texture2D CreateTextureFromPreparedData(PreparedTextureData prepared, bool isMask, bool isDoubleSided)
        {
            _ = isMask;
            _ = isDoubleSided;

            if (prepared.HasDdsData)
            {
                var dds = prepared.DdsData;
                Texture2D texture = new Texture2D(dds.Width, dds.Height, dds.Format, dds.HasMipMaps);
                texture.LoadRawTextureData(dds.RawData);
                texture.Apply(false, true);
                texture.name = dds.TextureName;
                return texture;
            }

            Texture2D imageTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (prepared.ImageData != null && imageTexture.LoadImage(prepared.ImageData))
            {
                imageTexture.name = Path.GetFileNameWithoutExtension(prepared.EffectiveFileName);
                return imageTexture;
            }

            Object.DestroyImmediate(imageTexture);

            if (prepared.HasBmpData)
            {
                var bmp = prepared.BmpData;
                Texture2D bmpTexture = new Texture2D(bmp.Width, bmp.Height, TextureFormat.RGBA32, false);
                bmpTexture.SetPixels32(bmp.Pixels);
                bmpTexture.Apply();
                bmpTexture.name = bmp.TextureName;
                return bmpTexture;
            }

            return null;
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

            // Also try the asset manager's cache if available
            if (!string.IsNullOrEmpty(currentObjectPath) && assetManager != null)
            {
                string cachePath = assetManager.GetCachePath(currentObjectPath);
                string texturesCachePath = Path.Combine(cachePath, "textures");
                basePathsList.Insert(0, texturesCachePath);
            }
            
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
                PreparedTextureData prepared = Task.Run(() => PrepareTextureData(data, fileName)).GetAwaiter().GetResult();
                return CreateTextureFromPreparedData(prepared, isMask, isDoubleSided);
            }
            catch (System.Exception e)
            {
                return null;
            }
        }

        public IEnumerator LoadTextureFromBytesAsync(byte[] data, string fileName, bool isMask, bool isDoubleSided, System.Action<Texture2D> onComplete)
        {
            Task<PreparedTextureData> prepareTask = Task.Run(() => PrepareTextureData(data, fileName));

            while (!prepareTask.IsCompleted)
            {
                yield return null;
            }

            if (prepareTask.IsFaulted)
            {
                onComplete?.Invoke(null);
                yield break;
            }

            yield return null;

            Texture2D texture = CreateTextureFromPreparedData(prepareTask.Result, isMask, isDoubleSided);
            onComplete?.Invoke(texture);
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

            // First, try to load from individual zipped texture/mask
            Texture2D texture = null;
            yield return LoadTextureFromZip(textureNameWithExt, isMask, isDoubleSided, (loadedTexture) => {
                texture = loadedTexture;
            });
            
            if (texture != null)
            {
                string cacheKey = textureName + (isDoubleSided ? "_DS" : "");
                textureCache[cacheKey] = texture; // Cache with original name + double-sided flag
                onComplete?.Invoke(texture);
                yield break;
            }

            // If ZIP loading failed, fall back to individual download
            yield return LoadTextureRemoteCoroutine(textureName, isMask, isDoubleSided, onComplete);
        }

        /// <summary>
        /// Attempts to load texture from an individual zipped texture archive
        /// </summary>
        private IEnumerator LoadTextureFromZip(string textureNameWithExt, bool isMask, bool isDoubleSided, System.Action<Texture2D> onComplete)
        {
            if (assetManager == null)
            {
                onComplete?.Invoke(null);
                yield break;
            }

            // Get the base name without extension for the ZIP file name
            string baseName = Path.GetFileNameWithoutExtension(textureNameWithExt);
            string zipFileName = baseName + ".zip";
            
            // Download the individual zipped texture/mask from textures folder
            bool downloadSuccess = false;
            string localZipPath = "";

            yield return assetManager.DownloadTexture(currentObjectPath, zipFileName, (success, result) =>
            {
                downloadSuccess = success;
                localZipPath = result;
            }, objectPathPassword);

            if (!downloadSuccess)
            {
                onComplete?.Invoke(null);
                yield break;
            }

            // Load the ZIP archive
            var archive = assetManager.LoadZipArchive(localZipPath);
            if (archive == null)
            {
                onComplete?.Invoke(null);
                yield break;
            }

            try
            {
                // For masks, try multiple possible file names inside the ZIP
                string[] possibleNames;
                if (isMask)
                {
                    possibleNames = new string[] {
                        textureNameWithExt,  // e.g., "t_tl01m.bmp"
                        baseName + ".bmp",   // e.g., "t_tl01m.bmp"
                        baseName + ".BMP",   // uppercase variant
                        baseName,            // just the base name
                    };
                }
                else
                {
                    possibleNames = new string[] {
                        textureNameWithExt,  // e.g., "t_leaves12.jpg"
                        baseName + ".jpg",   // e.g., "t_leaves12.jpg"
                        baseName + ".JPG",   // uppercase variant
                        baseName + ".jpeg",  // alternative extension
                        baseName + ".png",   // alternative extension
                        baseName + ".dds",   // DDS texture
                        baseName + ".DDS",   // uppercase DDS
                        baseName + ".dds.gz",// compressed DDS
                        baseName + ".DDS.GZ",// uppercase compressed DDS
                        baseName,            // just the base name
                    };
                }

                byte[] textureData = null;
                string foundFileName = null;

                // Try each possible file name
                foreach (string fileName in possibleNames)
                {
                    textureData = assetManager.ReadBytesFromZip(archive, fileName, localZipPath, objectPathPassword);
                    if (textureData != null && textureData.Length > 0)
                    {
                        foundFileName = fileName;
                        break;
                    }
                }
                
                if (textureData != null && textureData.Length > 0)
                {
                    // Try to create texture from byte data
                    Texture2D texture = LoadTextureFromBytes(textureData, foundFileName, isMask, isDoubleSided);
                    if (texture != null)
                    {
                        onComplete?.Invoke(texture);
                        yield break;
                    }
                }
            }
            finally
            {
                // Clean up the ZIP archive
                assetManager.UnloadZipArchive(localZipPath);
            }

            onComplete?.Invoke(null);
        }

        /// <summary>
        /// Downloads texture individually from remote source
        /// </summary>
        private IEnumerator LoadTextureRemoteCoroutine(string textureName, bool isMask, System.Action<Texture2D> onComplete)
        {
            return LoadTextureRemoteCoroutine(textureName, isMask, false, onComplete);
        }

        /// <summary>
        /// Downloads texture individually from remote source with double-sided support
        /// </summary>
        private IEnumerator LoadTextureRemoteCoroutine(string textureName, bool isMask, bool isDoubleSided, System.Action<Texture2D> onComplete)
        {
            // Ensure texture has proper extension for remote download
            string textureNameWithExt = EnsureTextureExtension(textureName, isMask);

            bool downloadSuccess = false;
            string localTexturePath = "";

            yield return assetManager.DownloadTexture(currentObjectPath, textureNameWithExt, (success, result) =>
            {
                downloadSuccess = success;
                localTexturePath = result;
            }, objectPathPassword);

            if (downloadSuccess && File.Exists(localTexturePath))
            {
                byte[] fileData = File.ReadAllBytes(localTexturePath);
                Texture2D texture = LoadTextureFromBytes(fileData, textureNameWithExt, isMask, isDoubleSided);
                if (texture != null)
                {
                    string cacheKey = textureName + (isDoubleSided ? "_DS" : "");
                    textureCache[cacheKey] = texture; // Cache with original name + double-sided flag
                    onComplete?.Invoke(texture);
                }
                else
                {
                    onComplete?.Invoke(null);
                }
            }
            else
            {
                onComplete?.Invoke(null);
            }
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
