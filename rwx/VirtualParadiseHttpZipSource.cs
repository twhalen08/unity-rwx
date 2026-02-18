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

            if (!downloadSuccess)
            {
                onComplete?.Invoke(false, null, downloadError);
                yield break;
            }

            var zipSource = new ZipModelSource(assetManager, localZipPath, password, objectPath, enableDebugLogs);
            yield return zipSource.ResolveModelPayload(modelName, onComplete);
        }
    }
}
