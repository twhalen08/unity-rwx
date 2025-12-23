using System;
using System.Collections.Generic;
using Unity.Mathematics;

public enum VpActionPhase
{
    Create,
    Activate
}


public class VpActionCommand
{
    public string raw;
    public string verb;                      // e.g. "texture"
    public List<string> positional = new();   // e.g. ["wood1"]
    public Dictionary<string, string> kv = new(); // e.g. { "color":"blue" }

    public override string ToString() => raw ?? base.ToString();
}

public enum VpPreprocessedActionType
{
    None,
    Ambient,
    Diffuse,
    Visible,
    Scale,
    Shear
}

public struct VpPreprocessedAction
{
    public VpPreprocessedActionType type;
    public float4 input0;
    public float3 data0;
    public float3 data1;
    public float value0;
    public bool valid;
}

public struct VpPreprocessActionInput
{
    public VpPreprocessedActionType type;
    public float4 input0;
    public float3 input1;
    public int flags;
}
