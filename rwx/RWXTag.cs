using System.Collections.Generic;
using UnityEngine;

namespace RWXLoader
{
    public struct RWXTag
    {
        public int TagId;
        public string TextureName;
    }

    /// <summary>
    /// Lightweight registry for associating tag metadata with renderers without attaching per-mesh components.
    /// </summary>
    public static class RWXTagRegistry
    {
        private static readonly Dictionary<int, RWXTag> RendererTags = new Dictionary<int, RWXTag>();

        public static void Register(Renderer renderer, int tagId, string textureName)
        {
            if (renderer == null) return;
            RendererTags[renderer.GetInstanceID()] = new RWXTag
            {
                TagId = tagId,
                TextureName = textureName
            };
        }

        public static bool TryGet(Renderer renderer, out RWXTag tag)
        {
            if (renderer != null && RendererTags.TryGetValue(renderer.GetInstanceID(), out tag))
            {
                return true;
            }

            tag = default;
            return false;
        }

        public static void Clear()
        {
            RendererTags.Clear();
        }
    }
}
