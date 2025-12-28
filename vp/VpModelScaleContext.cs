using UnityEngine;

[DisallowMultipleComponent]
public sealed class VpModelScaleContext : MonoBehaviour
{
    /// <summary>
    /// Base scale applied to match VP units to Unity units. Action-driven scale commands are applied on top of this.
    /// </summary>
    public Vector3 baseScale = Vector3.one;
}
