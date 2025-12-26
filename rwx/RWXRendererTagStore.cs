using System.Runtime.CompilerServices;
using UnityEngine;

namespace RWXLoader
{
    /// <summary>
    /// Lightweight tag storage without adding MonoBehaviours to every renderer.
    /// Uses a ConditionalWeakTable so entries are released when renderers are destroyed.
    /// </summary>
    public static class RWXRendererTagStore
    {
        private class TagHolder
        {
            public int Tag;
        }

        private static readonly ConditionalWeakTable<Renderer, TagHolder> Tags = new ConditionalWeakTable<Renderer, TagHolder>();

        public static void SetTag(Renderer renderer, int tag)
        {
            if (renderer == null) return;
            Tags.GetOrCreateValue(renderer).Tag = tag;
        }

        public static bool TryGetTag(Renderer renderer, out int tag)
        {
            if (renderer != null && Tags.TryGetValue(renderer, out var holder))
            {
                tag = holder.Tag;
                return true;
            }

            tag = 0;
            return false;
        }
    }
}
