using System;
using System.Collections;

namespace RWXLoader
{
    public enum RwxTextureUsage
    {
        Diffuse,
        Mask,
        Normal,
        Specular
    }

    public class RwxResolvedTextureData
    {
        public byte[] Bytes { get; }
        public string ResolvedName { get; }

        public RwxResolvedTextureData(byte[] bytes, string resolvedName)
        {
            Bytes = bytes;
            ResolvedName = resolvedName;
        }
    }

    public interface IRwxTextureResolver
    {
        IEnumerator ResolveTextureBytes(string textureName, RwxTextureUsage usage, Action<bool, RwxResolvedTextureData, string> onComplete);
    }
}
