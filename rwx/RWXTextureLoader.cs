using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
#if UNITY_EDITOR || UNITY_STANDALONE || UNITY_SERVER
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
#endif
using UnityEngine;

namespace RWXLoader
{
    /// <summary>
    /// Handles texture loading from various sources (local files, remote URLs, ZIP archives)
    /// </summary>
    public class RWXTextureLoader : MonoBehaviour
    {
        private struct DecodedTextureData
        {
            public int Width;
            public int Height;
            public TextureFormat Format;
            public byte[] PixelData;
            public bool HasMipMaps;
            public bool IsDds;
            public bool IsBlockCompressed;
            public int BytesPerPixel;
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
                if (!TryPrepareDdsTextureData(data, out DecodedTextureData decodedData))
                {
                    return null;
                }

                return CreateTextureFromDdsData(decodedData, fileName);
            }
            catch
            {
                return null;
            }
        }

        private Texture2D CreateTextureFromDdsData(DecodedTextureData decodedData, string fileName)
        {
            TextureFormat format = decodedData.Format;

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
                }
                else
                {
                    return null;
                }
            }

            Texture2D texture = new Texture2D(decodedData.Width, decodedData.Height, format, decodedData.HasMipMaps);
            texture.LoadRawTextureData(decodedData.PixelData);
            texture.Apply(false, true);
            texture.name = Path.GetFileNameWithoutExtension(fileName);
            return texture;
        }

        private bool TryPrepareDdsTextureData(byte[] data, out DecodedTextureData decodedData)
        {
            decodedData = default;

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
                    case "DXT5":
                        format = TextureFormat.DXT5;
                        break;
                    case "ATI2":
                    case "BC5 ":
                        format = TextureFormat.BC5;
                        break;
                    case "DX10":
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

            decodedData = new DecodedTextureData
            {
                Width = width,
                Height = height,
                Format = format,
                PixelData = dxtData,
                HasMipMaps = computedMipCount > 1,
                IsDds = true,
                IsBlockCompressed = isBlockCompressed,
                BytesPerPixel = bytesPerPixel
            };

            return true;
        }

        private bool TryDecodeTextureData(byte[] data, string fileName, out DecodedTextureData decodedData)
        {
            decodedData = default;
            string effectiveFileName = fileName;
            byte[] workingData = data;

            if (fileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) && TryDecompressGzip(data, out byte[] decompressedData))
            {
                workingData = decompressedData;
                effectiveFileName = Path.GetFileNameWithoutExtension(fileName);
            }

            if (effectiveFileName.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            {
                return TryPrepareDdsTextureData(workingData, out decodedData);
            }

            if (effectiveFileName.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
            {
                if (TryDecodeBmpToRgba32(workingData, out int bmpWidth, out int bmpHeight, out byte[] bmpPixels))
                {
                    decodedData = new DecodedTextureData
                    {
                        Width = bmpWidth,
                        Height = bmpHeight,
                        Format = TextureFormat.RGBA32,
                        PixelData = bmpPixels,
                        HasMipMaps = false,
                        IsDds = false,
                        IsBlockCompressed = false,
                        BytesPerPixel = 4
                    };
                    return true;
                }

                return false;
            }

            if (TryDecodeImageToRgba32(workingData, out int width, out int height, out byte[] pixels))
            {
                decodedData = new DecodedTextureData
                {
                    Width = width,
                    Height = height,
                    Format = TextureFormat.RGBA32,
                    PixelData = pixels,
                    HasMipMaps = false,
                    IsDds = false,
                    IsBlockCompressed = false,
                    BytesPerPixel = 4
                };
                return true;
            }

            return false;
        }

        private bool TryDecodeBmpToRgba32(byte[] bmpData, out int width, out int height, out byte[] pixels)
        {
            width = 0;
            height = 0;
            pixels = Array.Empty<byte>();

            if (bmpData.Length < 54 || bmpData[0] != 0x42 || bmpData[1] != 0x4D)
            {
                return false;
            }

            int dataOffset = BitConverter.ToInt32(bmpData, 10);
            int widthValue = BitConverter.ToInt32(bmpData, 18);
            int heightValue = BitConverter.ToInt32(bmpData, 22);
            short bitsPerPixel = BitConverter.ToInt16(bmpData, 28);
            int compression = BitConverter.ToInt32(bmpData, 30);

            if (compression != 0)
            {
                return false;
            }

            if (bitsPerPixel != 1 && bitsPerPixel != 8 && bitsPerPixel != 24 && bitsPerPixel != 32)
            {
                return false;
            }

            int absHeight = Math.Abs(heightValue);
            width = widthValue;
            height = absHeight;
            pixels = new byte[width * height * 4];

            bool isBottomUp = heightValue > 0;

            if (bitsPerPixel == 1)
            {
                int rowSizeInBytes = (width + 7) / 8;
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
                            return false;
                        }

                        bool isWhite = ((bmpData[byteIndex] >> bitIndex) & 1) == 1;
                        byte grayValue = isWhite ? (byte)255 : (byte)0;

                        int destIndex = (y * width + x) * 4;
                        pixels[destIndex] = grayValue;
                        pixels[destIndex + 1] = grayValue;
                        pixels[destIndex + 2] = grayValue;
                        pixels[destIndex + 3] = 255;
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
                            return false;
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

                        int destIndex = (y * width + x) * 4;
                        pixels[destIndex] = r;
                        pixels[destIndex + 1] = g;
                        pixels[destIndex + 2] = b;
                        pixels[destIndex + 3] = a;
                    }
                }
            }

            return true;
        }

        private bool TryDecodeImageToRgba32(byte[] imageData, out int width, out int height, out byte[] rgbaPixels)
        {
#if UNITY_EDITOR || UNITY_STANDALONE || UNITY_SERVER
            try
            {
                using (var stream = new MemoryStream(imageData))
                using (var bitmap = new Bitmap(stream))
                {
                    width = bitmap.Width;
                    height = bitmap.Height;
                    var rect = new Rectangle(0, 0, width, height);
                    var bitmapData = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    int stride = bitmapData.Stride;
                    int absStride = Math.Abs(stride);
                    byte[] source = new byte[absStride * height];
                    Marshal.Copy(bitmapData.Scan0, source, 0, source.Length);
                    bitmap.UnlockBits(bitmapData);

                    rgbaPixels = new byte[width * height * 4];
                    bool isBottomUp = stride > 0;

                    for (int y = 0; y < height; y++)
                    {
                        int sourceY = isBottomUp ? (height - 1 - y) : y;
                        int sourceRowStart = sourceY * absStride;

                        for (int x = 0; x < width; x++)
                        {
                            int sourceIndex = sourceRowStart + (x * 4);
                            int destIndex = (y * width + x) * 4;

                            byte b = source[sourceIndex];
                            byte g = source[sourceIndex + 1];
                            byte r = source[sourceIndex + 2];
                            byte a = source[sourceIndex + 3];

                            rgbaPixels[destIndex] = r;
                            rgbaPixels[destIndex + 1] = g;
                            rgbaPixels[destIndex + 2] = b;
                            rgbaPixels[destIndex + 3] = a;
                        }
                    }
                }

                return true;
            }
            catch
            {
                width = 0;
                height = 0;
                rgbaPixels = Array.Empty<byte>();
                return false;
            }
#else
            width = 0;
            height = 0;
            rgbaPixels = Array.Empty<byte>();
            return false;
#endif
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
                    if (effectiveFileName.EndsWith(".bmp"))
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
        /// Loads texture from file path asynchronously with double-sided support
        /// </summary>
        public IEnumerator LoadTextureFromFileAsync(string filePath, bool isDoubleSided, System.Action<Texture2D> onComplete)
        {
            if (!File.Exists(filePath))
            {
                onComplete?.Invoke(null);
                yield break;
            }

            Task<byte[]> readTask = Task.Run(() => File.ReadAllBytes(filePath));
            while (!readTask.IsCompleted)
            {
                yield return null;
            }

            if (readTask.IsFaulted)
            {
                onComplete?.Invoke(null);
                yield break;
            }

            byte[] fileData = readTask.Result;
            string fileName = Path.GetFileName(filePath);
            string effectiveFileName = fileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(fileName) : fileName;
            bool isMask = IsMaskFile(effectiveFileName);

            yield return LoadTextureFromBytesAsync(fileData, fileName, isMask, isDoubleSided, onComplete);
        }

        /// <summary>
        /// Loads texture from byte array asynchronously
        /// </summary>
        public IEnumerator LoadTextureFromBytesAsync(byte[] data, string fileName, bool isMask, System.Action<Texture2D> onComplete)
        {
            return LoadTextureFromBytesAsync(data, fileName, isMask, false, onComplete);
        }

        /// <summary>
        /// Loads texture from byte array asynchronously with double-sided support
        /// </summary>
        public IEnumerator LoadTextureFromBytesAsync(byte[] data, string fileName, bool isMask, bool isDoubleSided, System.Action<Texture2D> onComplete)
        {
            string effectiveFileName = fileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(fileName) : fileName;
            byte[] fallbackData = data;
            if (fileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) && TryDecompressGzip(data, out byte[] decompressedData))
            {
                fallbackData = decompressedData;
            }
            Task<DecodedTextureData?> decodeTask = Task.Run(() =>
            {
                if (TryDecodeTextureData(data, fileName, out DecodedTextureData decodedData))
                {
                    return (DecodedTextureData?)decodedData;
                }

                return null;
            });

            while (!decodeTask.IsCompleted)
            {
                yield return null;
            }

            if (decodeTask.IsFaulted)
            {
                onComplete?.Invoke(null);
                yield break;
            }

            DecodedTextureData? decodedResult = decodeTask.Result;
            if (!decodedResult.HasValue)
            {
                string lowerFileName = effectiveFileName.ToLowerInvariant();
                if (!lowerFileName.EndsWith(".png") && !lowerFileName.EndsWith(".jpg") && !lowerFileName.EndsWith(".jpeg") && !lowerFileName.EndsWith(".bmp"))
                {
                    Texture2D fallbackTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (fallbackTexture.LoadImage(fallbackData))
                    {
                        fallbackTexture.name = Path.GetFileNameWithoutExtension(effectiveFileName);
                        onComplete?.Invoke(fallbackTexture);
                        yield break;
                    }

                    Object.DestroyImmediate(fallbackTexture);
                }

                onComplete?.Invoke(null);
                yield break;
            }

            DecodedTextureData decodedDataResult = decodedResult.Value;
            Texture2D texture = null;
            if (decodedDataResult.IsDds)
            {
                texture = CreateTextureFromDdsData(decodedDataResult, effectiveFileName);
            }
            else
            {
                texture = new Texture2D(decodedDataResult.Width, decodedDataResult.Height, TextureFormat.RGBA32, false);
                texture.LoadRawTextureData(decodedDataResult.PixelData);
                texture.Apply();
                texture.name = Path.GetFileNameWithoutExtension(effectiveFileName);
            }

            if (texture == null && effectiveFileName.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
            {
                RWXBmpDecoder bmpDecoder = GetComponent<RWXBmpDecoder>();
                if (bmpDecoder != null)
                {
                    texture = bmpDecoder.DecodeBmpTexture(fallbackData, effectiveFileName);
                }
            }

            if (texture != null)
            {
                texture.name = Path.GetFileNameWithoutExtension(effectiveFileName);
            }

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
                    Texture2D texture = null;
                    yield return LoadTextureFromBytesAsync(textureData, foundFileName, isMask, isDoubleSided, (loadedTexture) =>
                    {
                        texture = loadedTexture;
                    });

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
                Texture2D texture = null;
                yield return LoadTextureFromBytesAsync(fileData, textureNameWithExt, isMask, isDoubleSided, (loadedTexture) =>
                {
                    texture = loadedTexture;
                });

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
