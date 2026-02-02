using UnityEngine;

[DisallowMultipleComponent]
public sealed class VpModelScaleContext : MonoBehaviour
{
    /// <summary>
    /// Base scale applied to match VP units to Unity units. Action-driven scale commands are applied on top of this.
    /// </summary>
    public UnityEngine.Vector3 baseScale = UnityEngine.Vector3.one;
}
