using UnityEngine;

public class VpActionGate : MonoBehaviour
{
    // Desired final states (set by actions)
    public bool desiredVisible = true;
    public bool desiredSolid = true;

    private int _pending;
    private Renderer[] _renderers;
    private Collider[] _colliders;

    public int Pending => _pending;

    private void Cache()
    {
        if (_renderers == null || _renderers.Length == 0)
            _renderers = GetComponentsInChildren<Renderer>(true);

        if (_colliders == null || _colliders.Length == 0)
            _colliders = GetComponentsInChildren<Collider>(true);
    }

    public void HideNow()
    {
        Cache();

        for (int i = 0; i < _renderers.Length; i++)
            if (_renderers[i]) _renderers[i].enabled = false;

        // During gating we also disable colliders to avoid "invisible walls"
        for (int i = 0; i < _colliders.Length; i++)
            if (_colliders[i]) _colliders[i].enabled = false;
    }

    public void BeginAction()
    {
        if (_pending == 0)
            HideNow();

        _pending++;
    }

    public void EndAction()
    {
        _pending = Mathf.Max(0, _pending - 1);
        if (_pending != 0) return;

        Cache();

        // Restore to desired VP states, not "always on"
        for (int i = 0; i < _renderers.Length; i++)
            if (_renderers[i]) _renderers[i].enabled = desiredVisible;

        for (int i = 0; i < _colliders.Length; i++)
            if (_colliders[i]) _colliders[i].enabled = desiredSolid;
    }

    // Call when visible/solid actions run
    public void SetVisible(bool visible)
    {
        desiredVisible = visible;
        if (_pending == 0)
        {
            Cache();
            for (int i = 0; i < _renderers.Length; i++)
                if (_renderers[i]) _renderers[i].enabled = desiredVisible;
        }
    }

    public void SetSolid(bool solid)
    {
        desiredSolid = solid;
        if (_pending == 0)
        {
            Cache();
            for (int i = 0; i < _colliders.Length; i++)
                if (_colliders[i]) _colliders[i].enabled = desiredSolid;
        }
    }
}
