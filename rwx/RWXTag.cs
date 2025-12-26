using UnityEngine;

namespace RWXLoader
{
    /// <summary>
    /// Marker component used to keep track of RWX tag assignments on generated meshes.
    /// Tags allow runtime actions to target specific sub-meshes for visual changes.
    /// </summary>
    public class RWXTag : MonoBehaviour
    {
        public int TagId;
        public string TextureName;
    }
}
