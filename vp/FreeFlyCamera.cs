using UnityEngine;

public class FreeFlyCamera : MonoBehaviour
{
    [Header("Look")]
    public float mouseSensitivity = 2.0f;
    public bool rightMouseToLook = true;
    public KeyCode toggleCursorKey = KeyCode.Escape;

    [Header("Move")]
    public float moveSpeed = 10f;
    public float sprintMultiplier = 4f;
    public float verticalSpeed = 8f;

    [Header("Smoothing (optional)")]
    public bool smoothMovement = true;
    public float smoothTime = 0.06f;

    private float yaw;
    private float pitch;

    private UnityEngine.Vector3 velocity;     // SmoothDamp velocity
    private UnityEngine.Vector3 desiredMove;  // target velocity per frame

    private bool cursorLocked = true;

    private void Start()
    {
        var euler = transform.rotation.eulerAngles;
        yaw = euler.y;
        pitch = euler.x;

        LockCursor(true);
    }

    private void Update()
    {
        if (Input.GetKeyDown(toggleCursorKey))
            LockCursor(!cursorLocked);

        bool looking = !rightMouseToLook || Input.GetMouseButton(1);
        if (cursorLocked && looking)
            HandleLook();

        HandleMove();
    }

    private void HandleLook()
    {
        float mx = Input.GetAxisRaw("Mouse X") * mouseSensitivity;
        float my = Input.GetAxisRaw("Mouse Y") * mouseSensitivity;

        yaw += mx;
        pitch -= my;
        pitch = Mathf.Clamp(pitch, -89f, 89f);

        transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
    }

    private void HandleMove()
    {
        float x = Input.GetAxisRaw("Horizontal"); // A/D
        float z = Input.GetAxisRaw("Vertical");   // W/S

        float up = 0f;
        if (Input.GetKey(KeyCode.E)) up += 1f;
        if (Input.GetKey(KeyCode.Q)) up -= 1f;

        float speed = moveSpeed;
        if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
            speed *= sprintMultiplier;

        UnityEngine.Vector3 move =
            transform.right * x +
            transform.forward * z;

        move = move.normalized * speed;
        UnityEngine.Vector3 vertical = UnityEngine.Vector3.up * (up * verticalSpeed);

        desiredMove = move + vertical;

        if (smoothMovement)
        {
            UnityEngine.Vector3 current = velocity;
            velocity = UnityEngine.Vector3.SmoothDamp(current, desiredMove, ref current, smoothTime);
            transform.position += velocity * Time.deltaTime;
        }
        else
        {
            transform.position += desiredMove * Time.deltaTime;
        }
    }

    private void LockCursor(bool locked)
    {
        cursorLocked = locked;

        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }
}
