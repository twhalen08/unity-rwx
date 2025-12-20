using System.Collections;
using System.Collections.Generic;
using System.IO;
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
            string[] extensions = { textureExtension, ".jpg", ".jpeg", ".png", ".bmp", ".tga" };
            
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
                bool isMask = fileName.ToLower().Contains("mask") || fileName.ToLower().Contains("_m") || fileName.EndsWith("m.bmp") || fileName.ToLower().Contains("tl01m");
                
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
                // Create texture with appropriate format
                Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                
                // Try to load the image data
                if (texture.LoadImage(data))
                {
                    // Set the texture name for debugging
                    texture.name = Path.GetFileNameWithoutExtension(fileName);
                    return texture;
                }
                else
                {
                    Object.DestroyImmediate(texture);
                    
                    // For BMP files, try custom decoder
                    if (fileName.EndsWith(".bmp"))
                    {
                        RWXBmpDecoder bmpDecoder = GetComponent<RWXBmpDecoder>();
                        if (bmpDecoder != null)
                        {
                            // FIXED: Use regular BMP decoder without rotation for all textures and masks
                            // The automatic rotation was causing mask orientation issues
                            texture = bmpDecoder.DecodeBmpTexture(data, fileName);
                            
                            // Set the texture name for debugging
                            if (texture != null)
                            {
                                texture.name = Path.GetFileNameWithoutExtension(fileName);
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
