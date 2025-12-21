using System;
using System.Collections.Generic;

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
