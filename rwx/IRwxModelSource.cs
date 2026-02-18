using System;
using System.Collections;

namespace RWXLoader
{
    public interface IRwxModelSource
    {
        IEnumerator ResolveModelPayload(string modelName, Action<bool, RwxModelPayload, string> onComplete);
    }

    public class RwxModelPayload
    {
        public string RwxContent { get; }
        public Func<string, byte[]> AssetBytesResolver { get; }
        public Action<RWXMaterialManager> MaterialManagerConfigurer { get; }

        public RwxModelPayload(
            string rwxContent,
            Func<string, byte[]> assetBytesResolver = null,
            Action<RWXMaterialManager> materialManagerConfigurer = null)
        {
            RwxContent = rwxContent;
            AssetBytesResolver = assetBytesResolver;
            MaterialManagerConfigurer = materialManagerConfigurer;
        }

        public void ConfigureMaterialManager(RWXMaterialManager manager)
        {
            MaterialManagerConfigurer?.Invoke(manager);
        }

        public byte[] TryReadAssetBytes(string assetName)
        {
            return AssetBytesResolver?.Invoke(assetName);
        }
    }
}
