using UnityEngine;

namespace RWXLoader
{
    /// <summary>
    /// Carries RWX-specific metadata onto the generated MeshRenderer so downstream systems
    /// (e.g., VP actions) can target subsets of renderers using RWX tags or texture names.
    /// </summary>
    public class RWXRendererMetadata : MonoBehaviour
    {
        public int tag;
        public string textureName;
    }
}
