using UnityEngine;

public class BoatFollowCamera : MonoBehaviour
{
    const float MinDistance = 1f;

    public Transform target;

    [SerializeField]
    Vector3 targetForwardAxis = Vector3.right;

    [Min(MinDistance)]
    public float distance = 12f;

    [Min(0f)]
    public float heightOffset = 4f;

    [Min(0f)]
    public float followSmoothTime = 0.2f;

    [Min(0f)]
    public float rotationSmoothTime = 0.12f;

    [Header("View")]
    public float pitch = 18f;

    Vector3 followVelocity;
    Transform previousTarget;
    bool frozen;

    /// <summary>Lock the camera in place — stops following the target until Unfreeze is called.</summary>
    public void Freeze() => frozen = true;

    /// <summary>Resume following the target.</summary>
    public void Unfreeze()
    {
        frozen = false;
        previousTarget = null; // force snap-free resume
    }

    void LateUpdate()
    {
        if (frozen) return;

        if (target == null)
        {
            previousTarget = null;
            return;
        }

        bool justAssigned = target != previousTarget;
        previousTarget = target;

        Vector3 localForwardAxis = targetForwardAxis.sqrMagnitude > 0f
            ? targetForwardAxis.normalized
            : Vector3.forward;

        Vector3 targetForward = Vector3.ProjectOnPlane(
            target.TransformDirection(localForwardAxis),
            Vector3.up).normalized;
        if (targetForward.sqrMagnitude <= 0f)
        {
            targetForward = Vector3.forward;
        }

        Quaternion desiredRotation = Quaternion.LookRotation(targetForward, Vector3.up) * Quaternion.Euler(pitch, 0f, 0f);
        Vector3 focusPoint = target.position + Vector3.up * heightOffset;
        Vector3 desiredPosition = focusPoint - desiredRotation * Vector3.forward * Mathf.Max(distance, MinDistance);

        if (justAssigned)
        {
            followVelocity = Vector3.zero;
            transform.SetPositionAndRotation(desiredPosition,
                Quaternion.LookRotation(focusPoint - desiredPosition, Vector3.up));
            return;
        }

        transform.position = Vector3.SmoothDamp(
            transform.position,
            desiredPosition,
            ref followVelocity,
            Mathf.Max(0.0001f, followSmoothTime));

        Quaternion lookRotation = Quaternion.LookRotation(focusPoint - transform.position, Vector3.up);
        float rotationLerp = rotationSmoothTime <= 0f ? 1f : Time.deltaTime / rotationSmoothTime;
        transform.rotation = Quaternion.Slerp(transform.rotation, lookRotation, Mathf.Clamp01(rotationLerp));
    }
}
