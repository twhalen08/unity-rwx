using System;
using System.Collections;

namespace RWXLoader
{
    public class VirtualParadiseHttpZipSource : IRwxModelSource
    {
        private readonly RWXAssetManager assetManager;
        private readonly string objectPath;
        private readonly string password;
        private readonly bool enableDebugLogs;

        public VirtualParadiseHttpZipSource(RWXAssetManager assetManager, string objectPath, string password = null, bool enableDebugLogs = false)
        {
            this.assetManager = assetManager;
            this.objectPath = objectPath;
            this.password = password;
            this.enableDebugLogs = enableDebugLogs;
        }

        public IEnumerator ResolveModelPayload(string modelName, Action<bool, RwxModelPayload, string> onComplete)
        {
            bool downloadSuccess = false;
            string localZipPath = string.Empty;
            string downloadError = string.Empty;

            yield return assetManager.DownloadModel(objectPath, modelName, (success, result) =>
            {
                downloadSuccess = success;
                if (success)
                {
                    localZipPath = result;
                }
                else
                {
                    downloadError = result;
                }
            }, password);

            if (downloadSuccess)
            {
                bool zipResolved = false;
                RwxModelPayload zipPayload = null;
                string zipMessage = string.Empty;

                var zipSource = new ZipModelSource(assetManager, localZipPath, password, objectPath, enableDebugLogs);
                yield return zipSource.ResolveModelPayload(modelName, (success, payload, message) =>
                {
                    zipResolved = success;
                    zipPayload = payload;
                    zipMessage = message;
                });

                if (zipResolved && zipPayload != null)
                {
                    onComplete?.Invoke(true, zipPayload, zipMessage);
                    yield break;
                }
            }

            // Fallback path: some VP models are served as raw .rwx without ZIP packaging.
            bool rwxDownloadSuccess = false;
            string localRwxPath = string.Empty;
            string rwxDownloadError = string.Empty;

            yield return assetManager.DownloadModelRwx(objectPath, modelName, (success, result) =>
            {
                rwxDownloadSuccess = success;
                if (success)
                {
                    localRwxPath = result;
                }
                else
                {
                    rwxDownloadError = result;
                }
            }, password);

            if (!rwxDownloadSuccess)
            {
                string message = string.IsNullOrEmpty(downloadError) ? rwxDownloadError : $"ZIP: {downloadError}; RWX: {rwxDownloadError}";
                onComplete?.Invoke(false, null, message);
                yield break;
            }

            try
            {
                string rwxContent = System.IO.File.ReadAllText(localRwxPath);
                onComplete?.Invoke(true, new RwxModelPayload(
                    rwxContent,
                    null,
                    manager => manager.SetTextureSource(new VirtualParadiseTextureResolver(assetManager, objectPath, password))), localRwxPath);
            }
            catch (Exception ex)
            {
                onComplete?.Invoke(false, null, $"RWX read error: {ex.Message}");
            }
        }
    }
}
