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
                onComplete?.Invoke(File.ReadAllBytes(localTexturePath), textureNameWithExt);
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

            ZipArchive archive = assetManager.LoadZipArchive(localZipPath);
            if (archive == null)
            {
                onComplete?.Invoke(null, null);
                yield break;
            }

            try
            {
                string[] candidateNames = GetZipEntryCandidates(baseName, textureNameWithExt, isMask);
                foreach (string candidate in candidateNames)
                {
                    byte[] entryBytes = assetManager.ReadBytesFromZip(archive, candidate, localZipPath, password);
                    if (entryBytes != null && entryBytes.Length > 0)
                    {
                        onComplete?.Invoke(entryBytes, candidate);
                        yield break;
                    }
                }
            }
            finally
            {
                assetManager.UnloadZipArchive(localZipPath);
            }

            onComplete?.Invoke(null, null);
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
