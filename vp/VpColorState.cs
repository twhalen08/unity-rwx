using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class VpColorState : MonoBehaviour
{
    public bool hasColorOverride;
    public bool tint;
    public Color color = Color.white;
    public bool hasOpacityOverride;
    public float opacity = 1f;
    public bool hasAppliedColorBefore;
    public readonly Dictionary<int, Color> baseColors = new();
    public int sequence;
    public int lastColorSeq;
    public int lastTextureSeq;
}
