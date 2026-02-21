using System;
using System.Collections;
using System.IO;
using System.IO.Compression;

namespace RWXLoader
{
    /// <summary>
    /// Resolves texture bytes using Virtual Paradise texture conventions:
    /// /textures/{name}, password query support, and per-texture ZIP archives.
    /// </summary>
    public class VirtualParadiseTextureResolver : IRwxTextureResolver
    {
        private readonly RWXAssetManager assetManager;
        private readonly string objectPath;
        private readonly string password;

        public VirtualParadiseTextureResolver(RWXAssetManager assetManager, string objectPath, string password = null)
        {
            this.assetManager = assetManager;
            this.objectPath = objectPath;
            this.password = password;
        }

        public IEnumerator ResolveTextureBytes(string textureName, RwxTextureUsage usage, Action<bool, RwxResolvedTextureData, string> onComplete)
        {
            if (assetManager == null)
            {
                onComplete?.Invoke(false, null, "Asset manager is not available");
                yield break;
            }

            if (string.IsNullOrEmpty(objectPath))
            {
                onComplete?.Invoke(false, null, "Object path is not configured");
                yield break;
            }

            string textureNameWithExt = EnsureTextureExtension(textureName, usage);
            bool isMask = usage == RwxTextureUsage.Mask;

            byte[] bytes = null;
            string resolvedName = textureNameWithExt;

            yield return TryResolveFromTextureZip(textureNameWithExt, isMask, (zipBytes, zipName) =>
            {
                bytes = zipBytes;
                resolvedName = string.IsNullOrEmpty(zipName) ? textureNameWithExt : zipName;
            });

            if (bytes == null || bytes.Length == 0)
            {
                yield return TryResolveFromDirectTexture(textureNameWithExt, (rawBytes, rawName) =>
                {
                    bytes = rawBytes;
                    resolvedName = string.IsNullOrEmpty(rawName) ? textureNameWithExt : rawName;
                });
            }

            bool success = bytes != null && bytes.Length > 0;
            onComplete?.Invoke(success, success ? new RwxResolvedTextureData(bytes, resolvedName) : null, success ? null : $"Unable to resolve texture '{textureName}'");
        }

        private IEnumerator TryResolveFromDirectTexture(string textureNameWithExt, Action<byte[], string> onComplete)
        {
            bool downloadSuccess = false;
            string localTexturePath = string.Empty;

            yield return assetManager.DownloadTexture(objectPath, textureNameWithExt, (success, result) =>
            {
                downloadSuccess = success;
                localTexturePath = result;
            }, password);

            if (downloadSuccess && File.Exists(localTexturePath))
            {
                byte[] fileBytes = null;
                yield return ReadFileBytesWhenStable(localTexturePath, bytes => fileBytes = bytes);
                onComplete?.Invoke(fileBytes, fileBytes != null ? textureNameWithExt : null);
                yield break;
            }

            onComplete?.Invoke(null, null);
        }

        private IEnumerator TryResolveFromTextureZip(string textureNameWithExt, bool isMask, Action<byte[], string> onComplete)
        {
            string baseName = Path.GetFileNameWithoutExtension(textureNameWithExt);
            string zipFileName = baseName + ".zip";

            bool downloadSuccess = false;
            string localZipPath = string.Empty;

            yield return assetManager.DownloadTexture(objectPath, zipFileName, (success, result) =>
            {
                downloadSuccess = success;
                localZipPath = result;
            }, password);

            if (!downloadSuccess)
            {
                onComplete?.Invoke(null, null);
                yield break;
            }

            byte[] zipProbeBytes = null;
            yield return ReadFileBytesWhenStable(localZipPath, bytes => zipProbeBytes = bytes);
            if (zipProbeBytes == null || zipProbeBytes.Length == 0)
            {
                onComplete?.Invoke(null, null);
                yield break;
            }

            string[] candidateNames = GetZipEntryCandidates(baseName, textureNameWithExt, isMask);

            // Password-protected ZIPs are handled by SharpZipLib through assetManager helpers.
            if (!string.IsNullOrEmpty(password))
            {
                foreach (string candidate in candidateNames)
                {
                    byte[] entryBytes = assetManager.ReadBytesFromZip(null, candidate, localZipPath, password);
                    if (entryBytes != null && entryBytes.Length > 0)
                    {
                        onComplete?.Invoke(entryBytes, candidate);
                        yield break;
                    }
                }

                onComplete?.Invoke(null, null);
                yield break;
            }

            // For non-password ZIPs, keep archive usage local to avoid shared-archive contention.
            using (var memoryStream = new MemoryStream(zipProbeBytes))
            using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Read))
            {
                foreach (string candidate in candidateNames)
                {
                    byte[] entryBytes = ReadBytesFromZipArchive(archive, candidate);
                    if (entryBytes != null && entryBytes.Length > 0)
                    {
                        onComplete?.Invoke(entryBytes, candidate);
                        yield break;
                    }
                }
            }

            onComplete?.Invoke(null, null);
        }

        private static byte[] ReadBytesFromZipArchive(ZipArchive archive, string requestedName)
        {
            if (archive == null || string.IsNullOrEmpty(requestedName))
            {
                return null;
            }

            ZipArchiveEntry direct = archive.GetEntry(requestedName);
            if (direct != null)
            {
                using (var stream = direct.Open())
                using (var ms = new MemoryStream())
                {
                    stream.CopyTo(ms);
                    return ms.ToArray();
                }
            }

            string requestedFileName = Path.GetFileName(requestedName);
            string requestedNoExt = Path.GetFileNameWithoutExtension(requestedName);

            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (entry == null || string.IsNullOrEmpty(entry.FullName))
                {
                    continue;
                }

                string entryFileName = Path.GetFileName(entry.FullName);
                string entryNoExt = Path.GetFileNameWithoutExtension(entryFileName);

                bool match =
                    string.Equals(entry.FullName, requestedName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(entryFileName, requestedFileName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(entryNoExt, requestedNoExt, StringComparison.OrdinalIgnoreCase);

                if (!match)
                {
                    continue;
                }

                using (var stream = entry.Open())
                using (var ms = new MemoryStream())
                {
                    stream.CopyTo(ms);
                    return ms.ToArray();
                }
            }

            return null;
        }

        private static IEnumerator ReadFileBytesWhenStable(string filePath, Action<byte[]> onComplete)
        {
            const int maxAttempts = 6;
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                if (!File.Exists(filePath))
                {
                    yield return null;
                    continue;
                }

                long sizeA = new FileInfo(filePath).Length;
                if (sizeA <= 0)
                {
                    yield return null;
                    continue;
                }

                yield return null;

                long sizeB = new FileInfo(filePath).Length;
                if (sizeA != sizeB)
                {
                    continue;
                }

                byte[] bytes = null;
                try
                {
                    bytes = File.ReadAllBytes(filePath);
                }
                catch (IOException)
                {
                    bytes = null;
                }
                catch (UnauthorizedAccessException)
                {
                    bytes = null;
                }

                if (bytes != null && bytes.Length > 0)
                {
                    onComplete?.Invoke(bytes);
                    yield break;
                }
            }

            onComplete?.Invoke(null);
        }

        private static string EnsureTextureExtension(string textureName, RwxTextureUsage usage)
        {
            if (string.IsNullOrEmpty(textureName))
            {
                return textureName;
            }

            string extension = Path.GetExtension(textureName);
            if (!string.IsNullOrEmpty(extension))
            {
                return textureName;
            }

            return usage == RwxTextureUsage.Mask ? textureName + ".bmp" : textureName + ".jpg";
        }

        private static string[] GetZipEntryCandidates(string baseName, string textureNameWithExt, bool isMask)
        {
            if (isMask)
            {
                return new[]
                {
                    textureNameWithExt,
                    baseName + ".bmp",
                    baseName + ".BMP",
                    baseName
                };
            }

            return new[]
            {
                textureNameWithExt,
                baseName + ".jpg",
                baseName + ".JPG",
                baseName + ".jpeg",
                baseName + ".png",
                baseName + ".dds",
                baseName + ".DDS",
                baseName + ".dds.gz",
                baseName + ".DDS.GZ",
                baseName
            };
        }
    }
}
