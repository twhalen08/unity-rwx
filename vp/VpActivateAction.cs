using System.Collections.Generic;
using UnityEngine;

public class VpActivateActions : MonoBehaviour
{
    public List<VpActionCommand> actions = new();

    public void Activate()
    {
        foreach (var a in actions)
        {
            Debug.Log($"[VP Activate] {name} -> {a}");
            // TODO: execute verbs (move/web/etc). For now: log.
        }
    }

    private void OnMouseDown()
    {
        Activate();
    }
}
