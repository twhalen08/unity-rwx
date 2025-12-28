using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(CharacterController))]
public class VpPlayerController : MonoBehaviour
{
    [Header("Camera")]
    public Transform cameraPivot;        // Child pivot at eye level
    public float eyeHeight = 1.6f;

    [Header("Look")]
    public float arrowTurnSpeedDeg = 200f;
    public float pageLookSpeedDeg = 100f;
    public float mouseSensitivity = 0.12f;
    public float minPitch = -80f;
    public float maxPitch = 80f;

    [Header("Move")]
    public float walkSpeed = 5.5f;
    public float runMultiplier = 1.8f;   // LEFT CTRL
    public float acceleration = 18f;

    [Header("Vertical / Fly")]
    public float verticalSpeed = 5.0f;   // + / - movement

    [Header("Gravity / Jump")]
    public float gravity = -25f;
    public float jumpHeight = 1.25f;
    public float groundedStickForce = -2f;

    [Header("Collision")]
    public float groundCheckDistance = 0.2f;
    public bool holdShiftForNoClip = true;

    [Header("Debug")]
    public bool showDebug = false;

    private CharacterController cc;

    private float yaw;
    private float pitch;

    private Vector3 horizontalVel;
    private float verticalVel;

    // MMB toggle state
    private bool mouseLookEnabled = false;

    private void Awake()
    {
        cc = GetComponent<CharacterController>();
        EnsureCameraPivot();
        ApplyEyeHeight();

        yaw = transform.rotation.eulerAngles.y;

        pitch = cameraPivot.localRotation.eulerAngles.x;
        if (pitch > 180f) pitch -= 360f;
        pitch = Mathf.Clamp(pitch, minPitch, maxPitch);
    }

    private void EnsureCameraPivot()
    {
        if (cameraPivot == null)
        {
            var existing = transform.Find("CameraPivot");
            if (existing != null)
                cameraPivot = existing;
            else
            {
                var go = new GameObject("CameraPivot");
                go.transform.SetParent(transform, false);
                cameraPivot = go.transform;
            }
        }

        var cam = GetComponentInChildren<Camera>(true);
        if (cam != null && cam.transform.parent != cameraPivot)
            cam.transform.SetParent(cameraPivot, true);
    }

    private void ApplyEyeHeight()
    {
        var lp = cameraPivot.localPosition;
        lp.x = 0f;
        lp.z = 0f;
        lp.y = eyeHeight;
        cameraPivot.localPosition = lp;
    }

    private void Update()
    {
        if (!gameObject.activeInHierarchy || !cc.enabled)
            return;

        var kb = Keyboard.current;
        var mouse = Mouse.current;
        if (kb == null) return;

        ApplyEyeHeight();

        // Toggle mouse look on MMB click
        if (mouse != null && mouse.middleButton.wasPressedThisFrame)
            ToggleMouseLook();

        // Shift = noclip
        bool noclip = holdShiftForNoClip && (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed);
        cc.detectCollisions = !noclip;

        HandleLook(kb, mouse);
        HandleMove(kb, noclip);
    }

    private void ToggleMouseLook()
    {
        mouseLookEnabled = !mouseLookEnabled;

        if (mouseLookEnabled)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
        else
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }

    private void HandleLook(Keyboard kb, Mouse mouse)
    {
        if (mouseLookEnabled && mouse != null)
        {
            Vector2 delta = mouse.delta.ReadValue();
            yaw += delta.x * mouseSensitivity;
            pitch -= delta.y * mouseSensitivity;
        }
        else
        {
            float turn = 0f;
            if (kb.leftArrowKey.isPressed) turn -= 1f;
            if (kb.rightArrowKey.isPressed) turn += 1f;
            yaw += turn * arrowTurnSpeedDeg * Time.deltaTime;

            float look = 0f;
            if (kb.pageUpKey.isPressed) look += 1f;
            if (kb.pageDownKey.isPressed) look -= 1f;
            pitch += look * pageLookSpeedDeg * Time.deltaTime;
        }

        pitch = Mathf.Clamp(pitch, minPitch, maxPitch);
        transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        cameraPivot.localRotation = Quaternion.Euler(pitch, 0f, 0f);
    }

    private void HandleMove(Keyboard kb, bool noclip)
    {
        float x = 0f, z = 0f;
        if (kb.aKey.isPressed) x -= 1f;
        if (kb.dKey.isPressed) x += 1f;
        if (kb.wKey.isPressed) z += 1f;
        if (kb.sKey.isPressed) z -= 1f;

        Vector3 input = new Vector3(x, 0f, z);
        if (input.sqrMagnitude > 1f) input.Normalize();

        bool run = kb.leftCtrlKey.isPressed;
        float speed = walkSpeed * (run ? runMultiplier : 1f);

        Vector3 desired = transform.TransformDirection(input) * speed;
        horizontalVel = Vector3.MoveTowards(horizontalVel, desired, acceleration * Time.deltaTime);

        // + / - vertical
        float up = 0f;
        bool plus = kb.equalsKey.isPressed || kb.numpadPlusKey.isPressed;
        bool minus = kb.minusKey.isPressed || kb.numpadMinusKey.isPressed;
        if (plus) up += 1f;
        if (minus) up -= 1f;

        if (noclip)
        {
            Vector3 move = horizontalVel;
            move.y = up * verticalSpeed;
            cc.Move(move * Time.deltaTime);
            verticalVel = 0f;
            return;
        }

        bool grounded = IsGrounded();

        if (grounded && verticalVel < 0f)
            verticalVel = groundedStickForce;

        if (grounded && kb.spaceKey.wasPressedThisFrame)
            verticalVel = Mathf.Sqrt(2f * Mathf.Abs(gravity) * jumpHeight);

        verticalVel += gravity * Time.deltaTime;

        Vector3 motion = horizontalVel;
        motion.y = verticalVel + up * verticalSpeed;

        CollisionFlags flags = cc.Move(motion * Time.deltaTime);
        if ((flags & CollisionFlags.Above) != 0 && verticalVel > 0f)
            verticalVel = 0f;
    }

    private bool IsGrounded()
    {
        Vector3 origin = transform.position + cc.center;
        float radius = Mathf.Max(0.01f, cc.radius * 0.95f);
        float dist = (cc.height * 0.5f) - cc.radius + groundCheckDistance;
        return Physics.SphereCast(origin, radius, Vector3.down, out _, dist, ~0, QueryTriggerInteraction.Ignore);
    }

    private void OnGUI()
    {
        if (!showDebug) return;
        GUI.Label(new Rect(10, 10, 1000, 22),
            $"MouseLook:{mouseLookEnabled}  Ctrl(run):{Keyboard.current.leftCtrlKey.isPressed}  Shift(noclip):{!cc.detectCollisions}");
    }
}
