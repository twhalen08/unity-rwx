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
        private static readonly Dictionary<int, RWXTag> ObjectTags = new Dictionary<int, RWXTag>();

        public static void Register(Renderer renderer, int tagId, string textureName)
        {
            if (renderer == null) return;
            RendererTags[renderer.GetInstanceID()] = new RWXTag
            {
                TagId = tagId,
                TextureName = textureName
            };
        }

        /// <summary>
        /// Look up tag data for a renderer, falling back to ancestor GameObjects that were tagged.
        /// </summary>
        public static bool TryGetWithParents(Renderer renderer, out RWXTag tag)
        {
            if (TryGet(renderer, out tag))
            {
                return true;
            }

            Transform current = renderer != null ? renderer.transform : null;
            while (current != null)
            {
                if (TryGet(current.gameObject, out tag))
                {
                    return true;
                }
                current = current.parent;
            }

            tag = default;
            return false;
        }

        public static void Register(GameObject obj, int tagId, string textureName)
        {
            if (obj == null) return;
            ObjectTags[obj.GetInstanceID()] = new RWXTag
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

        public static bool TryGet(GameObject obj, out RWXTag tag)
        {
            if (obj != null && ObjectTags.TryGetValue(obj.GetInstanceID(), out tag))
            {
                return true;
            }

            tag = default;
            return false;
        }

        public static void Clear()
        {
            RendererTags.Clear();
            ObjectTags.Clear();
        }
    }
}
