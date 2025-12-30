using UnityEngine;

/// <summary>
/// Stores object-level metadata such as description and source model name.
/// This lets action handlers (e.g., sign) read author-provided text when no
/// explicit parameters are supplied.
/// </summary>
public class VpObjectMetadata : MonoBehaviour
{
    [TextArea]
    public string description;
    public string modelName;

    public static VpObjectMetadata GetOrAdd(GameObject go)
    {
        if (go == null) return null;
        var meta = go.GetComponent<VpObjectMetadata>();
        if (meta == null) meta = go.AddComponent<VpObjectMetadata>();
        return meta;
    }
}
