using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Unity.SharpZipLib.Zip;
using UnityEngine;
using UnityEngine.Networking;

namespace RWXLoader
{
    public class RWXAssetManager : MonoBehaviour
    {
        [Header("Asset Management")]
        public string localCacheRoot = "RWXCache";

        private readonly Dictionary<string, string> cachedObjectPaths = new Dictionary<string, string>();
        private readonly Dictionary<string, ZipArchive> loadedZipArchives = new Dictionary<string, ZipArchive>();

        [Header("Debug")]
        public bool enableDebugLogs = false;

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

        private string SanitizeUrlForFolderName(string url)
        {
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

            return sanitized.TrimEnd('-');
        }

        private string SanitizeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }

            return name;
        }

        private string AppendPasswordQuery(string baseUrl, string password)
        {
            if (string.IsNullOrEmpty(password))
            {
                return baseUrl;
            }

            string separator = baseUrl.Contains("?") ? "&" : "?";
            return $"{baseUrl}{separator}password={UnityWebRequest.EscapeURL(password)}";
        }

        private string GetCacheDirectory(string objectPath)
        {
            if (cachedObjectPaths.TryGetValue(objectPath, out var existing))
            {
                return existing;
            }

            string sanitizedName = SanitizeUrlForFolderName(objectPath);
            string cacheDir = Path.Combine(Application.persistentDataPath, localCacheRoot, sanitizedName);

            if (!Directory.Exists(cacheDir))
            {
                Directory.CreateDirectory(cacheDir);
                Directory.CreateDirectory(Path.Combine(cacheDir, "models"));
                Directory.CreateDirectory(Path.Combine(cacheDir, "textures"));
            }

            cachedObjectPaths[objectPath] = cacheDir;
            return cacheDir;
        }

        public IEnumerator DownloadModel(string objectPath, string modelName, Action<bool, string> onComplete, string password = null)
        {
            string cacheDir = GetCacheDirectory(objectPath);
            string sanitizedModelName = SanitizeFileName(modelName);
            string localZipPath = Path.Combine(cacheDir, "models", $"{sanitizedModelName}.zip");

            if (File.Exists(localZipPath))
            {
                onComplete?.Invoke(true, localZipPath);
                yield break;
            }

            string encodedFileName = UnityWebRequest.EscapeURL(modelName + ".zip");
            string downloadUrl = AppendPasswordQuery(objectPath.TrimEnd('/') + "/models/" + encodedFileName, password);

            using (UnityWebRequest request = UnityWebRequest.Get(downloadUrl))
            {
                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    try
                    {
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


        public IEnumerator DownloadModelRwx(string objectPath, string modelName, Action<bool, string> onComplete, string password = null)
        {
            string cacheDir = GetCacheDirectory(objectPath);
            string sanitizedModelName = SanitizeFileName(modelName);
            string localRwxPath = Path.Combine(cacheDir, "models", $"{sanitizedModelName}.rwx");

            if (File.Exists(localRwxPath))
            {
                onComplete?.Invoke(true, localRwxPath);
                yield break;
            }

            string encodedFileName = UnityWebRequest.EscapeURL(modelName + ".rwx");
            string downloadUrl = AppendPasswordQuery(objectPath.TrimEnd('/') + "/models/" + encodedFileName, password);

            using (UnityWebRequest request = UnityWebRequest.Get(downloadUrl))
            {
                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    try
                    {
                        File.WriteAllBytes(localRwxPath, request.downloadHandler.data);
                        onComplete?.Invoke(true, localRwxPath);
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

        public IEnumerator DownloadTexture(string objectPath, string textureName, Action<bool, string> onComplete, string password = null)
        {
            string cacheDir = GetCacheDirectory(objectPath);
            string sanitizedTextureName = SanitizeFileName(textureName);
            string localTexturePath = Path.Combine(cacheDir, "textures", sanitizedTextureName);

            if (File.Exists(localTexturePath))
            {
                onComplete?.Invoke(true, localTexturePath);
                yield break;
            }

            string encodedTextureName = UnityWebRequest.EscapeURL(textureName);
            string downloadUrl = AppendPasswordQuery(objectPath.TrimEnd('/') + "/textures/" + encodedTextureName, password);

            using (UnityWebRequest request = UnityWebRequest.Get(downloadUrl))
            {
                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    try
                    {
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
        /// Loads a ZIP archive into memory for reading (non-encrypted zips only).
        /// Encrypted zips should be read via SharpZipLib helpers.
        /// </summary>
        public ZipArchive LoadZipArchive(string zipPath)
        {
            if (loadedZipArchives.TryGetValue(zipPath, out var existing))
            {
                return existing;
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
                if (enableDebugLogs)
                    Debug.LogWarning($"LoadZipArchive failed for '{zipPath}': {e.Message}");
                return null;
            }
        }

        public string ReadTextFromZip(ZipArchive archive, string fileName, string zipPath = null, string password = null)
        {
            try
            {
                // ✅ If password is provided, use SharpZipLib direct (works for encrypted zips).
                if (!string.IsNullOrEmpty(password))
                {
                    var bytes = TryReadEntryWithSharpZipLibDirect(zipPath, fileName, password);
                    if (bytes != null && bytes.Length > 0)
                    {
                        using var reader = new StreamReader(new MemoryStream(bytes));
                        return reader.ReadToEnd();
                    }

                    if (enableDebugLogs)
                        Debug.LogWarning($"SharpZipLib password read returned null for '{fileName}' in '{zipPath}'.");
                }

                // Non-password or fallback for non-encrypted zips:
                var entry = FindZipEntry(archive, fileName);
                if (entry != null)
                {
                    using var stream = entry.Open();
                    using var reader = new StreamReader(stream);
                    return reader.ReadToEnd();
                }

                return null;
            }
            catch (Exception ex)
            {
                if (enableDebugLogs)
                    Debug.LogWarning($"ReadTextFromZip failed for '{fileName}' (zip='{zipPath}'): {ex.Message}");
                return null;
            }
        }

        public byte[] ReadBytesFromZip(ZipArchive archive, string fileName, string zipPath = null, string password = null)
        {
            try
            {
                // ✅ If password is provided, use SharpZipLib direct.
                if (!string.IsNullOrEmpty(password))
                {
                    var bytes = TryReadEntryWithSharpZipLibDirect(zipPath, fileName, password);
                    if (bytes != null && bytes.Length > 0)
                    {
                        return bytes;
                    }

                    if (enableDebugLogs)
                        Debug.LogWarning($"SharpZipLib password byte read returned null for '{fileName}' in '{zipPath}'.");
                }

                var entry = FindZipEntry(archive, fileName);
                if (entry != null)
                {
                    using var stream = entry.Open();
                    using var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    return ms.ToArray();
                }

                return null;
            }
            catch (Exception ex)
            {
                if (enableDebugLogs)
                    Debug.LogWarning($"ReadBytesFromZip failed for '{fileName}' (zip='{zipPath}'): {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Direct SharpZipLib reader for password-protected archives.
        /// Tries both full path match and basename match via IsZipNameMatch.
        /// </summary>
        private byte[] TryReadEntryWithSharpZipLibDirect(string zipPath, string requestedName, string password)
        {
            if (string.IsNullOrEmpty(zipPath) || string.IsNullOrEmpty(requestedName) || string.IsNullOrEmpty(password))
            {
                return null;
            }

            try
            {
                using var fs = File.OpenRead(zipPath);
                using var zf = new Unity.SharpZipLib.Zip.ZipFile(fs);

                zf.Password = password;

                // Try a few candidate names (decoded, case, ext variants)
                var candidates = BuildZipNameCandidates(requestedName);

                foreach (ZipEntry e in zf)
                {
                    if (e == null || !e.IsFile) continue;

                    // e.Name is the full path in the zip
                    foreach (var cand in candidates)
                    {
                        if (IsZipNameMatch(e.Name, cand))
                        {
                            using var s = zf.GetInputStream(e);
                            using var ms = new MemoryStream();
                            s.CopyTo(ms);
                            return ms.ToArray();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (enableDebugLogs)
                    Debug.LogWarning($"SharpZipLibDirect failed for '{requestedName}' in '{zipPath}': {ex.Message}");
            }

            return null;
        }

        private ZipArchiveEntry FindZipEntry(ZipArchive archive, string fileName)
        {
            if (archive == null || string.IsNullOrEmpty(fileName))
            {
                return null;
            }

            string decodedName = UnityWebRequest.UnEscapeURL(fileName);

            foreach (string candidate in new[] { fileName, decodedName })
            {
                if (string.IsNullOrEmpty(candidate))
                    continue;

                var directEntry = archive.GetEntry(candidate);
                if (directEntry != null)
                    return directEntry;
            }

            string trimmedFileName = fileName.Trim();
            string trimmedDecodedName = decodedName.Trim();
            string targetFileName = Path.GetFileName(trimmedFileName);
            string targetDecodedFileName = Path.GetFileName(trimmedDecodedName);
            string targetNameWithoutExt = Path.GetFileNameWithoutExtension(trimmedFileName);
            string targetDecodedNameWithoutExt = Path.GetFileNameWithoutExtension(trimmedDecodedName);

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

        private List<string> BuildZipNameCandidates(string fileName)
        {
            var candidates = new List<string>();
            if (string.IsNullOrEmpty(fileName)) return candidates;

            string decoded = UnityWebRequest.UnEscapeURL(fileName);
            string trimmed = fileName.Trim();
            string trimmedDecoded = decoded.Trim();

            void Add(string v)
            {
                if (!string.IsNullOrEmpty(v) && !candidates.Contains(v))
                    candidates.Add(v);
            }

            Add(trimmed);
            Add(trimmedDecoded);
            Add(trimmed.ToLowerInvariant());
            Add(trimmed.ToUpperInvariant());
            Add(trimmedDecoded.ToLowerInvariant());
            Add(trimmedDecoded.ToUpperInvariant());

            // ext variants
            Add(Path.ChangeExtension(trimmed, ".rwx"));
            Add(Path.ChangeExtension(trimmed, ".RWX"));
            Add(Path.ChangeExtension(trimmedDecoded, ".rwx"));
            Add(Path.ChangeExtension(trimmedDecoded, ".RWX"));

            // basename variants
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

            string decodedEntry = UnityWebRequest.UnEscapeURL(entryName.Trim());
            string decodedReq = UnityWebRequest.UnEscapeURL(requestedName.Trim());

            // Compare by filename and by full name (path) in a forgiving way
            string entryFileName = Path.GetFileName(decodedEntry);
            string requestedFileName = Path.GetFileName(decodedReq);

            string entryNoExt = Path.GetFileNameWithoutExtension(decodedEntry);
            string reqNoExt = Path.GetFileNameWithoutExtension(decodedReq);

            // full path compare (case-insensitive)
            if (string.Equals(decodedEntry, decodedReq, StringComparison.OrdinalIgnoreCase))
                return true;

            // filename compare
            if (string.Equals(entryFileName, requestedFileName, StringComparison.OrdinalIgnoreCase))
                return true;

            // no-ext compare
            if (string.Equals(entryNoExt, reqNoExt, StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        /// <summary>
        /// Lists all files in a ZIP archive (full paths)
        /// </summary>
        public List<string> ListZipContents(ZipArchive archive)
        {
            var files = new List<string>();
            if (archive == null) return files;

            foreach (var entry in archive.Entries)
            {
                // ✅ FullName so folders aren't hidden
                files.Add(entry.FullName);
            }
            return files;
        }

        public void UnloadZipArchive(string zipPath)
        {
            if (loadedZipArchives.TryGetValue(zipPath, out var a))
            {
                a.Dispose();
                loadedZipArchives.Remove(zipPath);
            }
        }

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

        public string GetCachePath(string objectPath)
        {
            return GetCacheDirectory(objectPath);
        }
    }
}
