using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;
using UnityEngine.Networking;

namespace RWXLoader
{
    public class RWXAssetManager : MonoBehaviour
    {
        [Header("Asset Management")]
        public string localCacheRoot = "RWXCache";
        
        private Dictionary<string, string> cachedObjectPaths = new Dictionary<string, string>();
        private Dictionary<string, ZipArchive> loadedZipArchives = new Dictionary<string, ZipArchive>();
        
        public static RWXAssetManager Instance { get; private set; }

        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
                DontDestroyOnLoad(gameObject);
                InitializeCacheDirectory();
            }
            else
            {
                Destroy(gameObject);
            }
        }

        private void InitializeCacheDirectory()
        {
            string cacheRoot = Path.Combine(Application.persistentDataPath, localCacheRoot);
            if (!Directory.Exists(cacheRoot))
            {
                Directory.CreateDirectory(cacheRoot);
            }
        }

        /// <summary>
        /// Sanitizes a URL to create a valid folder name
        /// </summary>
        private string SanitizeUrlForFolderName(string url)
        {
            // Remove protocol and replace invalid characters
            string sanitized = url.Replace("http://", "http---")
                                 .Replace("https://", "https---")
                                 .Replace("/", "-")
                                 .Replace("\\", "-")
                                 .Replace(":", "-")
                                 .Replace("?", "-")
                                 .Replace("*", "-")
                                 .Replace("\"", "-")
                                 .Replace("<", "-")
                                 .Replace(">", "-")
                                 .Replace("|", "-");
            
            // Remove trailing dashes
            sanitized = sanitized.TrimEnd('-');
            
            return sanitized;
        }

        /// <summary>
        /// Replaces invalid filename characters so we can store files reliably on disk.
        /// </summary>
        private string SanitizeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }

            return name;
        }

        /// <summary>
        /// Gets or creates the local cache directory for an object path
        /// </summary>
        private string GetCacheDirectory(string objectPath)
        {
            if (cachedObjectPaths.ContainsKey(objectPath))
            {
                return cachedObjectPaths[objectPath];
            }

            string sanitizedName = SanitizeUrlForFolderName(objectPath);
            string cacheDir = Path.Combine(Application.persistentDataPath, localCacheRoot, sanitizedName);
            
            // Create directory structure
            if (!Directory.Exists(cacheDir))
            {
                Directory.CreateDirectory(cacheDir);
                Directory.CreateDirectory(Path.Combine(cacheDir, "models"));
                Directory.CreateDirectory(Path.Combine(cacheDir, "textures"));
            }

            cachedObjectPaths[objectPath] = cacheDir;
            return cacheDir;
        }

        /// <summary>
        /// Downloads and caches a model from the remote server
        /// </summary>
        public IEnumerator DownloadModel(string objectPath, string modelName, System.Action<bool, string> onComplete)
        {
            string cacheDir = GetCacheDirectory(objectPath);
            string sanitizedModelName = SanitizeFileName(modelName);
            string localZipPath = Path.Combine(cacheDir, "models", $"{sanitizedModelName}.zip");
            
            // Check if already cached
            if (File.Exists(localZipPath))
            {
                onComplete?.Invoke(true, localZipPath);
                yield break;
            }

            // Construct download URL with models subdirectory
            string encodedFileName = UnityWebRequest.EscapeURL(modelName + ".zip");
            string downloadUrl = objectPath.TrimEnd('/') + "/models/" + encodedFileName;

            using (UnityWebRequest request = UnityWebRequest.Get(downloadUrl))
            {
                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    try
                    {
                        // Save to cache
                        File.WriteAllBytes(localZipPath, request.downloadHandler.data);
                        onComplete?.Invoke(true, localZipPath);
                    }
                    catch (Exception e)
                    {
                        onComplete?.Invoke(false, $"Save error: {e.Message}");
                    }
                }
                else
                {
                    onComplete?.Invoke(false, request.error);
                }
            }
        }

        /// <summary>
        /// Downloads and caches a texture from the remote server
        /// </summary>
        public IEnumerator DownloadTexture(string objectPath, string textureName, System.Action<bool, string> onComplete)
        {
            string cacheDir = GetCacheDirectory(objectPath);
            string sanitizedTextureName = SanitizeFileName(textureName);
            string localTexturePath = Path.Combine(cacheDir, "textures", sanitizedTextureName);
            
            // Check if already cached
            if (File.Exists(localTexturePath))
            {
                onComplete?.Invoke(true, localTexturePath);
                yield break;
            }

            // Construct download URL with textures subdirectory
            string encodedTextureName = UnityWebRequest.EscapeURL(textureName);
            string downloadUrl = objectPath.TrimEnd('/') + "/textures/" + encodedTextureName;

            // Use regular UnityWebRequest.Get for all files (including ZIP files)
            // UnityWebRequestTexture.GetTexture() is only for image files, not ZIP archives
            using (UnityWebRequest request = UnityWebRequest.Get(downloadUrl))
            {
                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    try
                    {
                        // Save to cache
                        File.WriteAllBytes(localTexturePath, request.downloadHandler.data);
                        onComplete?.Invoke(true, localTexturePath);
                    }
                    catch (Exception e)
                    {
                        onComplete?.Invoke(false, $"Save error: {e.Message}");
                    }
                }
                else
                {
                    onComplete?.Invoke(false, request.error);
                }
            }
        }

        /// <summary>
        /// Loads a ZIP archive into memory for reading
        /// </summary>
        public ZipArchive LoadZipArchive(string zipPath)
        {
            if (loadedZipArchives.ContainsKey(zipPath))
            {
                return loadedZipArchives[zipPath];
            }

            try
            {
                byte[] zipData = File.ReadAllBytes(zipPath);
                MemoryStream memoryStream = new MemoryStream(zipData);
                ZipArchive archive = new ZipArchive(memoryStream, ZipArchiveMode.Read);
                
                loadedZipArchives[zipPath] = archive;
                return archive;
            }
            catch (Exception e)
            {
                return null;
            }
        }

        /// <summary>
        /// Reads a file from a ZIP archive
        /// </summary>
        public string ReadTextFromZip(ZipArchive archive, string fileName)
        {
            try
            {
                var entry = FindZipEntry(archive, fileName);

                if (entry != null)
                {
                    using (var stream = entry.Open())
                    using (var reader = new StreamReader(stream))
                    {
                        return reader.ReadToEnd();
                    }
                }
                else
                {
                    return null;
                }
            }
            catch (Exception e)
            {
                return null;
            }
        }

        /// <summary>
        /// Reads binary data from a ZIP archive
        /// </summary>
        public byte[] ReadBytesFromZip(ZipArchive archive, string fileName)
        {
            try
            {
                var entry = FindZipEntry(archive, fileName);

                if (entry != null)
                {
                    using (var stream = entry.Open())
                    using (var memoryStream = new MemoryStream())
                    {
                        stream.CopyTo(memoryStream);
                        return memoryStream.ToArray();
                    }
                }
                else
                {
                    return null;
                }
            }
            catch (Exception e)
            {
                return null;
            }
        }

        /// <summary>
        /// Finds a ZIP entry by trying exact, decoded, case-insensitive, and path-based matches.
        /// </summary>
        private ZipArchiveEntry FindZipEntry(ZipArchive archive, string fileName)
        {
            // First try exact lookups to handle matching full paths inside the archive
            var entry = archive.GetEntry(fileName);
            if (entry != null)
                return entry;

            string decodedName = UnityWebRequest.UnEscapeURL(fileName);
            if (!string.Equals(decodedName, fileName, StringComparison.Ordinal))
            {
                entry = archive.GetEntry(decodedName);
                if (entry != null)
                    return entry;
            }

            // Fall back to case-insensitive comparisons against file names only
            foreach (var e in archive.Entries)
            {
                string entryFileName = Path.GetFileName(e.FullName);

                if (string.Equals(entryFileName, fileName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(entryFileName, decodedName, StringComparison.OrdinalIgnoreCase))
                {
                    return e;
                }
            }

            return null;
        }

        /// <summary>
        /// Lists all files in a ZIP archive
        /// </summary>
        public List<string> ListZipContents(ZipArchive archive)
        {
            var files = new List<string>();
            foreach (var entry in archive.Entries)
            {
                files.Add(entry.Name);
            }
            return files;
        }

        /// <summary>
        /// Cleans up loaded ZIP archives
        /// </summary>
        public void UnloadZipArchive(string zipPath)
        {
            if (loadedZipArchives.ContainsKey(zipPath))
            {
                loadedZipArchives[zipPath].Dispose();
                loadedZipArchives.Remove(zipPath);
            }
        }

        /// <summary>
        /// Cleans up all loaded ZIP archives
        /// </summary>
        public void UnloadAllZipArchives()
        {
            foreach (var archive in loadedZipArchives.Values)
            {
                archive.Dispose();
            }
            loadedZipArchives.Clear();
        }

        private void OnDestroy()
        {
            UnloadAllZipArchives();
        }

        /// <summary>
        /// Gets the local cache path for debugging
        /// </summary>
        public string GetCachePath(string objectPath)
        {
            return GetCacheDirectory(objectPath);
        }
    }
}
