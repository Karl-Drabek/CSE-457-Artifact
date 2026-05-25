using UnityEngine;

public class ShipThrust : MonoBehaviour
{
    [Min(0f)]
    public float thrustForce = 40f;

    Rigidbody body;

    void Awake()
    {
        body = GetComponent<Rigidbody>();
    }

    void FixedUpdate()
    {
        body.AddForce(transform.right * thrustForce, ForceMode.Force);
    }
}