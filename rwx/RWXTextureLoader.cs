using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace RWXLoader
{
    /// <summary>
    /// Handles texture loading from various sources (local files, remote URLs, ZIP archives)
    /// </summary>
    public class RWXTextureLoader : MonoBehaviour
    {
        private struct PreparedTextureData
        {
            public byte[] Data;
            public string FileName;
            public bool IsMask;

            public bool IsValid => Data != null && Data.Length > 0 && !string.IsNullOrEmpty(FileName);
        }

        private struct PreparedDdsData
        {
            public int Width;
            public int Height;
            public int MipMapCount;
            public TextureFormat Format;
            public bool IsBlockCompressed;
            public int BytesPerPixel;
            public byte[] RawData;

            public bool IsValid => Width > 0 && Height > 0 && RawData != null && RawData.Length > 0;
        }

        private struct PreparedBmpData
        {
            public RWXBmpDecoder.BmpDecodedData Decoded;
            public string FileName;

            public bool IsValid => Decoded.IsValid && !string.IsNullOrEmpty(FileName);
        }

        private struct AsyncTextureResult
        {
            public PreparedTextureData Prepared;
            public PreparedDdsData DdsData;
            public PreparedBmpData BmpData;
            public AsyncTextureKind Kind;
        }

        private enum AsyncTextureKind
        {
            Generic,
            Dds,
            Bmp
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

        private PreparedTextureData PrepareTextureData(byte[] data, string fileName, bool isMask)
        {
            string effectiveFileName = fileName;
            byte[] workingData = data;

            if (fileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) && TryDecompressGzip(data, out byte[] decompressedData))
            {
                workingData = decompressedData;
                effectiveFileName = Path.GetFileNameWithoutExtension(fileName);
            }

            return new PreparedTextureData
            {
                Data = workingData,
                FileName = effectiveFileName,
                IsMask = isMask
            };
        }

        private PreparedTextureData ReadTextureDataFromFile(string filePath, bool? isMaskOverride = null)
        {
            if (!File.Exists(filePath))
            {
                return new PreparedTextureData();
            }

            byte[] fileData = File.ReadAllBytes(filePath);
            string fileName = Path.GetFileName(filePath);
            string effectiveFileName = fileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(fileName)
                : fileName;
            bool isMask = isMaskOverride ?? IsMaskFile(effectiveFileName);

            return PrepareTextureData(fileData, fileName, isMask);
        }

        private static bool RequiresMainThreadDecode(string fileName)
        {
            return fileName.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".dds.gz", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".bmp.gz", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsBmpFileName(string fileName)
        {
            return fileName.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDdsFileName(string fileName)
        {
            return fileName.EndsWith(".dds", StringComparison.OrdinalIgnoreCase);
        }

        private static string ToFileUri(string filePath)
        {
            return new Uri(filePath).AbsoluteUri;
        }

        private Texture2D LoadDdsTexture(byte[] data, string fileName)
        {
            // Basic DDS loader supporting common compressed formats
            try
            {
                if (!TryPrepareDdsData(data, fileName, out PreparedDdsData prepared))
                {
                    return null;
                }

                return CreateTextureFromPreparedDdsData(prepared, fileName);
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
                PreparedTextureData prepared = ReadTextureDataFromFile(filePath);
                if (!prepared.IsValid)
                {
                    return null;
                }

                return CreateTextureFromPreparedData(prepared, isDoubleSided);
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
                PreparedTextureData prepared = PrepareTextureData(data, fileName, isMask);
                return CreateTextureFromPreparedData(prepared, isDoubleSided);
            }
            catch (System.Exception e)
            {
                return null;
            }
        }

        private Texture2D CreateTextureFromPreparedData(PreparedTextureData prepared, bool isDoubleSided)
        {
            if (!prepared.IsValid)
            {
                return null;
            }

            if (IsDdsFileName(prepared.FileName))
            {
                Texture2D ddsTexture = LoadDdsTexture(prepared.Data, prepared.FileName);
                if (ddsTexture != null)
                {
                    return ddsTexture;
                }
            }

            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);

            if (texture.LoadImage(prepared.Data))
            {
                texture.name = Path.GetFileNameWithoutExtension(prepared.FileName);
                return texture;
            }

            Object.DestroyImmediate(texture);

            if (IsBmpFileName(prepared.FileName))
            {
                RWXBmpDecoder bmpDecoder = GetComponent<RWXBmpDecoder>();
                if (bmpDecoder != null)
                {
                    texture = bmpDecoder.DecodeBmpTexture(prepared.Data, prepared.FileName);

                    if (texture != null)
                    {
                        texture.name = Path.GetFileNameWithoutExtension(prepared.FileName);
                    }

                    return texture;
                }
            }

            return null;
        }

        private Texture2D CreateTextureFromPreparedDdsData(PreparedDdsData prepared, string fileName)
        {
            if (!prepared.IsValid)
            {
                return null;
            }

            TextureFormat format = prepared.Format;

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

            bool hasMipMaps = prepared.MipMapCount > 1;
            Texture2D texture = new Texture2D(prepared.Width, prepared.Height, format, hasMipMaps);
            texture.LoadRawTextureData(prepared.RawData);
            texture.Apply(false, true);
            texture.name = Path.GetFileNameWithoutExtension(fileName);
            return texture;
        }

        private Texture2D CreateTextureFromDecodedBmp(PreparedBmpData prepared)
        {
            if (!prepared.IsValid)
            {
                return null;
            }

            Texture2D texture = new Texture2D(prepared.Decoded.Width, prepared.Decoded.Height, TextureFormat.RGBA32, false);
            texture.SetPixels32(prepared.Decoded.Pixels);
            texture.Apply();
            texture.name = Path.GetFileNameWithoutExtension(prepared.FileName);
            return texture;
        }

        private bool TryPrepareDdsData(byte[] data, string fileName, out PreparedDdsData prepared)
        {
            prepared = default;

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
                    case 71:
                        format = TextureFormat.DXT1;
                        break;
                    case 74:
                    case 77:
                        format = TextureFormat.DXT5;
                        break;
                    case 80:
                        format = TextureFormat.BC5;
                        break;
                    case 98:
                        format = TextureFormat.BC7;
                        break;
                    case 28:
                    case 87:
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

            prepared = new PreparedDdsData
            {
                Width = width,
                Height = height,
                MipMapCount = computedMipCount,
                Format = format,
                IsBlockCompressed = isBlockCompressed,
                BytesPerPixel = bytesPerPixel,
                RawData = dxtData
            };
            return prepared.IsValid;
        }

        private AsyncTextureResult PrepareAsyncTextureResult(PreparedTextureData prepared)
        {
            if (!prepared.IsValid)
            {
                return new AsyncTextureResult { Kind = AsyncTextureKind.Generic };
            }

            if (IsBmpFileName(prepared.FileName))
            {
                RWXBmpDecoder.BmpDecodedData decoded = RWXBmpDecoder.DecodeBmpPixels(prepared.Data);
                return new AsyncTextureResult
                {
                    Kind = AsyncTextureKind.Bmp,
                    BmpData = new PreparedBmpData
                    {
                        Decoded = decoded,
                        FileName = prepared.FileName
                    }
                };
            }

            if (IsDdsFileName(prepared.FileName) && TryPrepareDdsData(prepared.Data, prepared.FileName, out PreparedDdsData ddsData))
            {
                return new AsyncTextureResult
                {
                    Kind = AsyncTextureKind.Dds,
                    DdsData = ddsData
                };
            }

            return new AsyncTextureResult
            {
                Kind = AsyncTextureKind.Generic,
                Prepared = prepared
            };
        }

        public IEnumerator LoadTextureLocalAsync(string textureName, bool isDoubleSided, System.Action<Texture2D> onComplete)
        {
            string cacheKey = textureName + (isDoubleSided ? "_DS" : "");
            if (textureCache.TryGetValue(cacheKey, out Texture2D cachedTexture))
            {
                onComplete?.Invoke(cachedTexture);
                yield break;
            }

            string[] extensions = { textureExtension, ".jpg", ".jpeg", ".png", ".bmp", ".tga", ".dds", ".dds.gz" };
            List<string> basePathsList = new List<string>
            {
                Path.Combine(Application.streamingAssetsPath, textureFolder),
                Path.Combine(Application.persistentDataPath, textureFolder),
                Path.Combine(Application.dataPath, textureFolder),
                textureFolder
            };

            if (!string.IsNullOrEmpty(currentObjectPath) && assetManager != null)
            {
                string cachePath = assetManager.GetCachePath(currentObjectPath);
                string texturesCachePath = Path.Combine(cachePath, "textures");
                basePathsList.Insert(0, texturesCachePath);
            }

            foreach (string basePath in basePathsList)
            {
                string directPath = Path.Combine(basePath, textureName);
                if (File.Exists(directPath))
                {
                    Texture2D texture = null;
                    yield return LoadTextureFromFileAsync(directPath, isDoubleSided, loaded => texture = loaded);
                    if (texture != null)
                    {
                        textureCache[cacheKey] = texture;
                        onComplete?.Invoke(texture);
                        yield break;
                    }
                }

                if (string.IsNullOrEmpty(Path.GetExtension(textureName)))
                {
                    foreach (string ext in extensions)
                    {
                        string fullPath = Path.Combine(basePath, textureName + ext);
                        if (File.Exists(fullPath))
                        {
                            Texture2D texture = null;
                            yield return LoadTextureFromFileAsync(fullPath, isDoubleSided, loaded => texture = loaded);
                            if (texture != null)
                            {
                                textureCache[cacheKey] = texture;
                                onComplete?.Invoke(texture);
                                yield break;
                            }
                        }
                    }
                }
            }

            onComplete?.Invoke(null);
        }

        private IEnumerator LoadTextureFromFileAsync(string filePath, bool isDoubleSided, System.Action<Texture2D> onComplete)
        {
            if (!RequiresMainThreadDecode(filePath))
            {
                using (UnityWebRequest request = UnityWebRequestTexture.GetTexture(ToFileUri(filePath)))
                {
                    yield return request.SendWebRequest();
                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        onComplete?.Invoke(null);
                        yield break;
                    }

                    Texture2D texture = DownloadHandlerTexture.GetContent(request);
                    if (texture != null)
                    {
                        texture.name = Path.GetFileNameWithoutExtension(filePath);
                    }
                    onComplete?.Invoke(texture);
                    yield break;
                }
            }

            Task<AsyncTextureResult> task = Task.Run(() =>
            {
                PreparedTextureData prepared = ReadTextureDataFromFile(filePath);
                return PrepareAsyncTextureResult(prepared);
            });
            while (!task.IsCompleted)
            {
                yield return null;
            }

            if (task.IsFaulted)
            {
                onComplete?.Invoke(null);
                yield break;
            }

            AsyncTextureResult result = task.Result;
            Texture2D texture = null;
            switch (result.Kind)
            {
                case AsyncTextureKind.Bmp:
                    texture = CreateTextureFromDecodedBmp(result.BmpData);
                    break;
                case AsyncTextureKind.Dds:
                    texture = CreateTextureFromPreparedDdsData(result.DdsData, Path.GetFileName(filePath));
                    break;
                default:
                    texture = CreateTextureFromPreparedData(result.Prepared, isDoubleSided);
                    break;
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
                    yield return LoadTextureFromBytesAsync(textureData, foundFileName, isMask, isDoubleSided, loaded => texture = loaded);
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

        private IEnumerator LoadTextureFromBytesAsync(byte[] data, string fileName, bool isMask, bool isDoubleSided, System.Action<Texture2D> onComplete)
        {
            Task<AsyncTextureResult> task = Task.Run(() =>
            {
                PreparedTextureData prepared = PrepareTextureData(data, fileName, isMask);
                return PrepareAsyncTextureResult(prepared);
            });

            while (!task.IsCompleted)
            {
                yield return null;
            }

            if (task.IsFaulted)
            {
                onComplete?.Invoke(null);
                yield break;
            }

            AsyncTextureResult result = task.Result;
            Texture2D texture = null;
            switch (result.Kind)
            {
                case AsyncTextureKind.Bmp:
                    texture = CreateTextureFromDecodedBmp(result.BmpData);
                    break;
                case AsyncTextureKind.Dds:
                    texture = CreateTextureFromPreparedDdsData(result.DdsData, fileName);
                    break;
                default:
                    texture = CreateTextureFromPreparedData(result.Prepared, isDoubleSided);
                    break;
            }

            onComplete?.Invoke(texture);
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
                Texture2D texture = null;
                yield return LoadTextureFromFileAsync(localTexturePath, isDoubleSided, loaded => texture = loaded);
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
