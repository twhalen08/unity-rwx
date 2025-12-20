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
        /// Optionally appends a password query parameter to a download URL.
        /// Keeps the base URL untouched when no password is provided.
        /// </summary>
        private string AppendPasswordQuery(string baseUrl, string password)
        {
            if (string.IsNullOrEmpty(password))
            {
                return baseUrl;
            }

            string separator = baseUrl.Contains("?") ? "&" : "?";
            return $"{baseUrl}{separator}password={UnityWebRequest.EscapeURL(password)}";
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
        public IEnumerator DownloadModel(string objectPath, string modelName, System.Action<bool, string> onComplete, string password = null)
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
            string downloadUrl = AppendPasswordQuery(objectPath.TrimEnd('/') + "/models/" + encodedFileName, password);

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
        public IEnumerator DownloadTexture(string objectPath, string textureName, System.Action<bool, string> onComplete, string password = null)
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
            string downloadUrl = AppendPasswordQuery(objectPath.TrimEnd('/') + "/textures/" + encodedTextureName, password);

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
        /// Reads a file from a ZIP archive, falling back to password-protected readers when needed.
        /// </summary>
        public string ReadTextFromZip(ZipArchive archive, string fileName, string zipPath = null, string password = null)
        {
            try
            {
                // If we have a password, prefer a password-aware reader first so encrypted archives are handled reliably.
                if (!string.IsNullOrEmpty(password))
                {
                    string passwordResult = TryReadTextWithPassword(zipPath, fileName, password);
                    if (!string.IsNullOrEmpty(passwordResult))
                    {
                        return passwordResult;
                    }
                }

                var entry = FindZipEntry(archive, fileName);

                if (entry != null)
                {
                    using (var stream = entry.Open())
                    using (var reader = new StreamReader(stream))
                    {
                        return reader.ReadToEnd();
                    }
                }

                return null;
            }
            catch (Exception)
            {
                // If the regular reader fails and a password is available, try a password-aware path as a fallback
                if (!string.IsNullOrEmpty(password))
                {
                    try
                    {
                        return TryReadTextWithPassword(zipPath, fileName, password);
                    }
                    catch (Exception)
                    {
                        // Ignore and fall through
                    }
                }

                return null;
            }
        }

        /// <summary>
        /// Reads binary data from a ZIP archive, with optional password support for encrypted archives.
        /// </summary>
        public byte[] ReadBytesFromZip(ZipArchive archive, string fileName, string zipPath = null, string password = null)
        {
            try
            {
                // Password-protected archives should be read with a password-aware helper first.
                if (!string.IsNullOrEmpty(password))
                {
                    byte[] protectedBytes = TryReadEntryWithPassword(zipPath, BuildZipNameCandidates(fileName), password);
                    if (protectedBytes != null && protectedBytes.Length > 0)
                    {
                        return protectedBytes;
                    }
                }

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

                return null;
            }
            catch (Exception)
            {
                if (!string.IsNullOrEmpty(password))
                {
                    try
                    {
                        return TryReadEntryWithPassword(zipPath, BuildZipNameCandidates(fileName), password);
                    }
                    catch (Exception)
                    {
                        return null;
                    }
                }

                return null;
            }
        }

        private string TryReadTextWithPassword(string zipPath, string fileName, string password)
        {
            byte[] protectedBytes = TryReadEntryWithPassword(zipPath, BuildZipNameCandidates(fileName), password);
            if (protectedBytes != null && protectedBytes.Length > 0)
            {
                using (var reader = new StreamReader(new MemoryStream(protectedBytes)))
                {
                    return reader.ReadToEnd();
                }
            }

            return null;
        }

        /// <summary>
        /// Finds a ZIP entry by trying exact, decoded, case-insensitive, and path-based matches.
        /// </summary>
        private ZipArchiveEntry FindZipEntry(ZipArchive archive, string fileName)
        {
            if (archive == null || string.IsNullOrEmpty(fileName))
            {
                return null;
            }

            string decodedName = UnityWebRequest.UnEscapeURL(fileName);

            // First try exact lookups to handle matching full paths inside the archive
            foreach (string candidate in new[] { fileName, decodedName })
            {
                if (string.IsNullOrEmpty(candidate))
                {
                    continue;
                }

                var directEntry = archive.GetEntry(candidate);
                if (directEntry != null)
                {
                    return directEntry;
                }
            }

            // Normalize requested names for comparison
            string trimmedFileName = fileName.Trim();
            string trimmedDecodedName = decodedName.Trim();
            string targetFileName = Path.GetFileName(trimmedFileName);
            string targetDecodedFileName = Path.GetFileName(trimmedDecodedName);
            string targetNameWithoutExt = Path.GetFileNameWithoutExtension(trimmedFileName);
            string targetDecodedNameWithoutExt = Path.GetFileNameWithoutExtension(trimmedDecodedName);

            // Fall back to case-insensitive comparisons against file names only, with trimmed and decoded variants
            foreach (var e in archive.Entries)
            {
                string entryFileName = Path.GetFileName(e.FullName)?.Trim();
                string decodedEntryFileName = UnityWebRequest.UnEscapeURL(entryFileName ?? string.Empty).Trim();
                string entryNameWithoutExt = Path.GetFileNameWithoutExtension(entryFileName);
                string decodedEntryNameWithoutExt = Path.GetFileNameWithoutExtension(decodedEntryFileName);

                bool namesMatch =
                    string.Equals(entryFileName, targetFileName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(entryFileName, targetDecodedFileName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(decodedEntryFileName, targetFileName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(decodedEntryFileName, targetDecodedFileName, StringComparison.OrdinalIgnoreCase);

                bool namesWithoutExtMatch =
                    string.Equals(entryNameWithoutExt, targetNameWithoutExt, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(entryNameWithoutExt, targetDecodedNameWithoutExt, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(decodedEntryNameWithoutExt, targetNameWithoutExt, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(decodedEntryNameWithoutExt, targetDecodedNameWithoutExt, StringComparison.OrdinalIgnoreCase);

                if (namesMatch || namesWithoutExtMatch)
                {
                    return e;
                }
            }

            return null;
        }

        /// <summary>
        /// Fallback for reading password-protected ZIP files using reflection so we don't hard-require an extra dependency.
        /// Supports Ionic.Zip (DotNetZip) and SharpZipLib when present in the project.
        /// </summary>
        private byte[] TryReadEntryWithPassword(string zipPath, List<string> candidateNames, string password)
        {
            if (string.IsNullOrEmpty(zipPath) || candidateNames == null || candidateNames.Count == 0 || string.IsNullOrEmpty(password))
            {
                return null;
            }

            foreach (var candidate in candidateNames)
            {
                byte[] data = TryReadEntryWithDotNetZip(zipPath, candidate, password);
                if (data != null && data.Length > 0)
                {
                    return data;
                }
            }

            foreach (var candidate in candidateNames)
            {
                byte[] data = TryReadEntryWithSharpZipLib(zipPath, candidate, password);
                if (data != null && data.Length > 0)
                {
                    return data;
                }
            }

            return null;
        }

        private Type ResolveType(params string[] typeNames)
        {
            foreach (var typeName in typeNames)
            {
                if (string.IsNullOrEmpty(typeName))
                {
                    continue;
                }

                // Try direct resolution first (works when the assembly-qualified name is correct)
                var direct = Type.GetType(typeName);
                if (direct != null)
                {
                    return direct;
                }

                // Fall back to scanning loaded assemblies to handle alternate assembly names
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var resolved = assembly.GetType(typeName);
                    if (resolved != null)
                    {
                        return resolved;
                    }
                }
            }

            return null;
        }

        private byte[] TryReadEntryWithDotNetZip(string zipPath, string fileName, string password)
        {
            try
            {
                var zipType = ResolveType(
                    "Ionic.Zip.ZipFile, DotNetZip",
                    "Ionic.Zip.ZipFile, Ionic.Zip",
                    "Ionic.Zip.ZipFile");
                if (zipType == null)
                {
                    return null;
                }

                var readMethod = zipType.GetMethod("Read", new[] { typeof(string) });
                if (readMethod == null)
                {
                    return null;
                }

                object zipInstance = readMethod.Invoke(null, new object[] { zipPath });
                using (zipInstance as IDisposable)
                {
                    var passwordProp = zipType.GetProperty("Password");
                    passwordProp?.SetValue(zipInstance, password);

                    var entriesProp = zipType.GetProperty("Entries");
                    var entries = entriesProp?.GetValue(zipInstance) as System.Collections.IEnumerable;
                    if (entries == null)
                    {
                        return null;
                    }

                    foreach (var entry in entries)
                    {
                        var entryType = entry.GetType();
                        string entryName = entryType.GetProperty("FileName")?.GetValue(entry) as string;
                        if (!IsZipNameMatch(entryName, fileName))
                        {
                            continue;
                        }

                        var openReader = entryType.GetMethod("OpenReader", Type.EmptyTypes);
                        if (openReader == null)
                        {
                            continue;
                        }

                        using (var stream = openReader.Invoke(entry, null) as Stream)
                        using (var memoryStream = new MemoryStream())
                        {
                            stream?.CopyTo(memoryStream);
                            return memoryStream.ToArray();
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Ignore and fall back to other strategies
            }

            return null;
        }

        private byte[] TryReadEntryWithSharpZipLib(string zipPath, string fileName, string password)
        {
            try
            {
                var zipType = ResolveType(
                    "ICSharpCode.SharpZipLib.Zip.ZipFile, ICSharpCode.SharpZipLib",
                    "ICSharpCode.SharpZipLib.Zip.ZipFile");
                if (zipType == null)
                {
                    return null;
                }

                object zipInstance = Activator.CreateInstance(zipType, zipPath);
                using (zipInstance as IDisposable)
                {
                    var passwordProp = zipType.GetProperty("Password");
                    passwordProp?.SetValue(zipInstance, password);

                    var getEnumerator = zipType.GetMethod("GetEnumerator");
                    var enumerator = getEnumerator?.Invoke(zipInstance, null) as System.Collections.IEnumerator;
                    if (enumerator == null)
                    {
                        return null;
                    }

                    while (enumerator.MoveNext())
                    {
                        var entry = enumerator.Current;
                        var entryType = entry.GetType();
                        string entryName = entryType.GetProperty("Name")?.GetValue(entry) as string;
                        if (!IsZipNameMatch(entryName, fileName))
                        {
                            continue;
                        }

                        var getInputStream = zipType.GetMethod("GetInputStream", new[] { entryType });
                        using (var stream = getInputStream?.Invoke(zipInstance, new object[] { entry }) as Stream)
                        using (var memoryStream = new MemoryStream())
                        {
                            stream?.CopyTo(memoryStream);
                            return memoryStream.ToArray();
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Ignore and let callers know we couldn't decrypt
            }

            return null;
        }

        /// <summary>
        /// Builds a list of candidate names for matching a ZIP entry, including decoded and case variations.
        /// </summary>
        private List<string> BuildZipNameCandidates(string fileName)
        {
            var candidates = new List<string>();

            if (string.IsNullOrEmpty(fileName))
            {
                return candidates;
            }

            string decoded = UnityWebRequest.UnEscapeURL(fileName);
            string trimmed = fileName.Trim();
            string trimmedDecoded = decoded.Trim();

            void Add(string value)
            {
                if (!string.IsNullOrEmpty(value) && !candidates.Contains(value))
                {
                    candidates.Add(value);
                }
            }

            Add(trimmed);
            Add(trimmedDecoded);
            Add(trimmed.ToLowerInvariant());
            Add(trimmed.ToUpperInvariant());
            Add(trimmedDecoded.ToLowerInvariant());
            Add(trimmedDecoded.ToUpperInvariant());

            // Try toggling extensions to account for .rwx/.RWX or missing extensions
            string lowerExt = Path.ChangeExtension(trimmed, ".rwx");
            string upperExt = Path.ChangeExtension(trimmed, ".RWX");
            string decodedLowerExt = Path.ChangeExtension(trimmedDecoded, ".rwx");
            string decodedUpperExt = Path.ChangeExtension(trimmedDecoded, ".RWX");

            Add(lowerExt);
            Add(upperExt);
            Add(decodedLowerExt);
            Add(decodedUpperExt);

            // Allow matching by name only when callers include a path component
            string baseName = Path.GetFileName(trimmed);
            string baseNameDecoded = Path.GetFileName(trimmedDecoded);

            Add(baseName);
            Add(baseNameDecoded);
            Add(baseName.ToLowerInvariant());
            Add(baseName.ToUpperInvariant());
            Add(baseNameDecoded.ToLowerInvariant());
            Add(baseNameDecoded.ToUpperInvariant());

            return candidates;
        }

        private bool IsZipNameMatch(string entryName, string requestedName)
        {
            if (string.IsNullOrEmpty(entryName) || string.IsNullOrEmpty(requestedName))
            {
                return false;
            }

            string decodedEntryFileName = UnityWebRequest.UnEscapeURL(entryName.Trim());
            string decodedRequested = UnityWebRequest.UnEscapeURL(requestedName.Trim());

            string entryFileName = Path.GetFileName(decodedEntryFileName);
            string entryNameWithoutExt = Path.GetFileNameWithoutExtension(decodedEntryFileName);
            string requestedFileName = Path.GetFileName(decodedRequested);
            string requestedWithoutExt = Path.GetFileNameWithoutExtension(decodedRequested);

            bool namesMatch = string.Equals(entryFileName, requestedFileName, StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(entryFileName, decodedRequested, StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(decodedEntryFileName, requestedFileName, StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(decodedEntryFileName, decodedRequested, StringComparison.OrdinalIgnoreCase);

            bool namesWithoutExtMatch = string.Equals(entryNameWithoutExt, requestedWithoutExt, StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(entryNameWithoutExt, Path.GetFileNameWithoutExtension(decodedRequested), StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(Path.GetFileNameWithoutExtension(decodedEntryFileName), requestedWithoutExt, StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(Path.GetFileNameWithoutExtension(decodedEntryFileName), Path.GetFileNameWithoutExtension(decodedRequested), StringComparison.OrdinalIgnoreCase);

            return namesMatch || namesWithoutExtMatch;
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
