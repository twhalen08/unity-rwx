using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class VpColorState : MonoBehaviour
{
    public bool hasColorOverride;
    public bool tint;
    public Color color = Color.white;
    public bool hasAppliedColorBefore;
    public readonly Dictionary<int, Color> baseColors = new();
}
