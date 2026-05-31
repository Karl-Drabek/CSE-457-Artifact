using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Rigidbody))]
public class ShipController : MonoBehaviour
{
    const float MinInputMagnitude = 0.01f;
    const float MinForwardSpeed = 0.01f;

    [Header("Thrust")]
    [Min(0f)]
    public float forwardAcceleration = 18f;

    [Min(0f)]
    public float reverseAcceleration = 10f;

    [Min(0f)]
    public float maxForwardSpeed = 12f;

    [Min(0f)]
    public float maxReverseSpeed = 4f;

    [Header("Steering")]
    [Min(0f)]
    public float turnTorque = 12f;

    [Min(0f)]
    public float steerResponse = 3f;

    [Min(0f)]
    public float turnDamping = 2.5f;

    [Header("Stability")]
    [Min(0f)]
    public float lateralDrag = 3f;

    [Min(0f)]
    public float uprightTorque = 4f;

    [SerializeField]
    Vector3 localForwardAxis = Vector3.right;

    Rigidbody body;
    InputAction moveAction;
    Vector2 moveInput;

    void Awake()
    {
        body = GetComponent<Rigidbody>();
        moveAction = InputSystem.actions.FindAction("Move");
    }

    void OnEnable()
    {
        if (moveAction == null)
        {
            moveAction = InputSystem.actions.FindAction("Move");
        }
    }

    void Update()
    {
        moveInput = moveAction != null ? moveAction.ReadValue<Vector2>() : Vector2.zero;
    }

    void FixedUpdate()
    {
        if (body == null)
        {
            return;
        }

        Vector3 forward = transform.TransformDirection(localForwardAxis.normalized);
        Vector3 planarForward = Vector3.ProjectOnPlane(forward, Vector3.up).normalized;
        if (planarForward.sqrMagnitude <= 0f)
        {
            planarForward = transform.forward;
        }

        ApplyThrust(planarForward);
        ApplySteering(planarForward);
        ApplyLateralDrag(planarForward);
        ApplyUprightTorque();
    }

    void ApplyThrust(Vector3 planarForward)
    {
        if (Mathf.Abs(moveInput.y) <= MinInputMagnitude)
        {
            return;
        }

        float forwardSpeed = Vector3.Dot(body.linearVelocity, planarForward);
        bool movingForward = moveInput.y > 0f;
        float maxSpeed = movingForward ? maxForwardSpeed : maxReverseSpeed;
        float acceleration = movingForward ? forwardAcceleration : reverseAcceleration;

        if ((movingForward && forwardSpeed >= maxSpeed)
            || (!movingForward && forwardSpeed <= -maxSpeed))
        {
            return;
        }

        body.AddForce(planarForward * (moveInput.y * acceleration), ForceMode.Acceleration);
    }

    void ApplySteering(Vector3 planarForward)
    {
        if (Mathf.Abs(moveInput.x) <= MinInputMagnitude)
        {
            return;
        }

        float forwardSpeed = Vector3.Dot(body.linearVelocity, planarForward);
        float steeringStrength = Mathf.Clamp01(Mathf.Abs(forwardSpeed) / Mathf.Max(maxForwardSpeed, MinForwardSpeed));
        steeringStrength = Mathf.Max(steeringStrength, 0.25f);

        float targetYawSpeed = moveInput.x * turnTorque * steeringStrength;
        float yawError = targetYawSpeed - body.angularVelocity.y;
        body.AddTorque(Vector3.up * (yawError * steerResponse), ForceMode.Acceleration);
    }

    void ApplyLateralDrag(Vector3 planarForward)
    {
        Vector3 lateralVelocity = Vector3.Project(body.linearVelocity, Vector3.Cross(Vector3.up, planarForward).normalized);
        body.AddForce(-lateralVelocity * lateralDrag, ForceMode.Acceleration);

        float yawVelocity = body.angularVelocity.y;
        body.AddTorque(Vector3.up * (-yawVelocity * turnDamping), ForceMode.Acceleration);
    }

    void ApplyUprightTorque()
    {
        Vector3 tiltAxis = Vector3.Cross(transform.up, Vector3.up);
        body.AddTorque(tiltAxis * uprightTorque, ForceMode.Acceleration);
    }
}
