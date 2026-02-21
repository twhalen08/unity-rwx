using System;
using System.Collections;
using System.IO.Compression;
using UnityEngine;

namespace RWXLoader
{
    public class ZipModelSource : IRwxModelSource
    {
        private readonly RWXAssetManager assetManager;
        private readonly string zipPath;
        private readonly string password;
        private readonly string textureObjectPath;
        private readonly bool enableDebugLogs;

        public ZipModelSource(RWXAssetManager assetManager, string zipPath, string password = null, string textureObjectPath = null, bool enableDebugLogs = false)
        {
            this.assetManager = assetManager;
            this.zipPath = zipPath;
            this.password = password;
            this.textureObjectPath = textureObjectPath;
            this.enableDebugLogs = enableDebugLogs;
        }

        public IEnumerator ResolveModelPayload(string modelName, Action<bool, RwxModelPayload, string> onComplete)
        {
            bool success = TryResolveModelPayload(modelName, out var payload, out var message);
            onComplete?.Invoke(success, payload, message);
            yield break;
        }

        public bool TryResolveModelPayload(string modelName, out RwxModelPayload payload, out string message)
        {
            payload = null;
            message = null;

            if (assetManager == null)
            {
                message = "Asset manager is not available";
                return false;
            }

            ZipArchive archive = assetManager.LoadZipArchive(zipPath);
            if (archive == null)
            {
                message = $"Failed to load ZIP archive: {zipPath}";
                return false;
            }

            string rwxFileName = $"{modelName}.rwx";
            string rwxContent = assetManager.ReadTextFromZip(archive, rwxFileName, zipPath, password);

            if (string.IsNullOrEmpty(rwxContent))
            {
                rwxFileName = $"{modelName}.RWX";
                rwxContent = assetManager.ReadTextFromZip(archive, rwxFileName, zipPath, password);
            }

            if (string.IsNullOrEmpty(rwxContent))
            {
                rwxFileName = FindFirstRwxEntry(archive);
                if (!string.IsNullOrEmpty(rwxFileName))
                {
                    rwxContent = assetManager.ReadTextFromZip(archive, rwxFileName, zipPath, password);
                    if (enableDebugLogs && !string.IsNullOrEmpty(rwxContent))
                    {
                        Debug.Log($"Fallback RWX file used: {rwxFileName}");
                    }
                }
            }

            if (string.IsNullOrEmpty(rwxContent))
            {
                message = $"RWX file not found in ZIP; attempted {modelName}.rwx and fallback entries";
                return false;
            }

            payload = new RwxModelPayload(
                rwxContent,
                assetName => assetManager.ReadBytesFromZip(archive, assetName, zipPath, password),
                manager =>
                {
                    if (!string.IsNullOrEmpty(textureObjectPath))
                    {
                        manager.SetTextureSource(new VirtualParadiseTextureResolver(assetManager, textureObjectPath, password));
                    }
                });

            message = rwxFileName;
            return true;
        }

        private string FindFirstRwxEntry(ZipArchive archive)
        {
            foreach (var entry in archive.Entries)
            {
                if (entry == null || string.IsNullOrEmpty(entry.FullName))
                {
                    continue;
                }

                if (entry.FullName.EndsWith(".rwx", StringComparison.OrdinalIgnoreCase))
                {
                    return entry.FullName;
                }
            }

            return null;
        }
    }
}
