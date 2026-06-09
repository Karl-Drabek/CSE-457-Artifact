using UnityEngine;
using UnityEngine.InputSystem;

public class BuildCameraController : MonoBehaviour
{
    [Header("Rotation")]
    public float rotationSpeed = 0.3f;
    public float keyboardRotationSpeed = 60f;

    [Header("Pan")]
    public float panSpeed = 3f;
    public float keyboardPanSpeed = 5f;

    [Header("Zoom")]
    public float scrollZoomSpeed = 60f;
    public float keyboardZoomSpeed = 5f;
    public float minZoom = 2f;
    public float maxZoom = 30f;

    Vector3 lastMousePosition;

    void Start()
    {
    }

    void Update()
    {
        HandleRotation();
        HandlePan();
        HandleZoom();
    }

    void HandleRotation()
    {
        // Right click drag to rotate
        if (Mouse.current != null && Mouse.current.rightButton.isPressed)
        {
            Vector2 mouseDelta = Mouse.current.delta.ReadValue();
            RotateInPlace(mouseDelta.x * rotationSpeed, mouseDelta.y * rotationSpeed);
        }

        // Keyboard fallback: arrow keys or WASD to rotate
        float horizontal = 0f;
        float vertical = 0f;

        if (Keyboard.current != null)
        {
            if (Keyboard.current.leftArrowKey.isPressed || Keyboard.current.aKey.isPressed)
                horizontal = -1f;
            if (Keyboard.current.rightArrowKey.isPressed || Keyboard.current.dKey.isPressed)
                horizontal = 1f;
            if (Keyboard.current.upArrowKey.isPressed || Keyboard.current.wKey.isPressed)
                vertical = -1f;
            if (Keyboard.current.downArrowKey.isPressed || Keyboard.current.sKey.isPressed)
                vertical = 1f;
        }

        if (horizontal != 0f || vertical != 0f)
        {
            RotateInPlace(
                horizontal * keyboardRotationSpeed * Time.deltaTime,
                vertical * keyboardRotationSpeed * Time.deltaTime
            );
        }
    }

    void HandlePan()
    {
        // Middle mouse drag to pan
        if (Mouse.current != null && Mouse.current.middleButton.isPressed)
        {
            Vector2 mouseDelta = Mouse.current.delta.ReadValue();
            Vector3 right = transform.right * (-mouseDelta.x * panSpeed * Time.deltaTime);
            Vector3 up = transform.up * (-mouseDelta.y * panSpeed * Time.deltaTime);
            Vector3 pan = right + up;
            transform.position += pan;
        }

        // Keyboard fallback: Q/E to pan left/right, R/F to pan up/down
        if (Keyboard.current != null)
        {
            Vector3 pan = Vector3.zero;

            if (Keyboard.current.qKey.isPressed)
                pan += transform.right * -keyboardPanSpeed * Time.deltaTime;
            if (Keyboard.current.eKey.isPressed)
                pan += transform.right * keyboardPanSpeed * Time.deltaTime;
            if (Keyboard.current.rKey.isPressed)
                pan += transform.up * keyboardPanSpeed * Time.deltaTime;
            if (Keyboard.current.fKey.isPressed)
                pan += transform.up * -keyboardPanSpeed * Time.deltaTime;

            if (pan != Vector3.zero)
            {
                transform.position += pan;
            }
        }
    }

    void HandleZoom()
    {
        // Scroll wheel to zoom
        if (Mouse.current != null)
        {
            float scroll = Mouse.current.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.01f)
            {
                Zoom(scroll * scrollZoomSpeed * Time.deltaTime);
            }
        }

        // Keyboard fallback: Z to zoom in, X to zoom out
        if (Keyboard.current != null)
        {
            if (Keyboard.current.zKey.isPressed)
                Zoom(keyboardZoomSpeed * Time.deltaTime);
            if (Keyboard.current.xKey.isPressed)
                Zoom(-keyboardZoomSpeed * Time.deltaTime);
        }
    }

    void RotateInPlace(float horizontalDelta, float verticalDelta)
    {
        transform.Rotate(Vector3.up, horizontalDelta, Space.World);

        float currentPitch = transform.localEulerAngles.x;
        if (currentPitch > 180f) currentPitch -= 360f;
        float newPitch = Mathf.Clamp(currentPitch - verticalDelta, -80f, 80f); // changed + to -
        transform.localEulerAngles = new Vector3(newPitch, transform.localEulerAngles.y, 0f);
    }

    void Zoom(float amount)
    {
        Vector3 newPosition = transform.position + transform.forward * amount;
        transform.position = newPosition;
    }
}