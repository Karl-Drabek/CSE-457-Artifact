using UnityEngine;

/// <summary>
/// Deals impulse-based collision damage to any BoatPiece that strikes this object.
/// Unlike ObstacleHealth, this component has no HP of its own — it acts as a pure hazard.
/// Requires a Rigidbody (kinematic) on the same GameObject so OnCollisionEnter fires.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Rigidbody))]
public class CollisionDamage : MonoBehaviour
{
    [Tooltip("Scales how much damage each unit of collision impulse (mass × normal impact speed) deals to the boat.")]
    [SerializeField, Min(0f)] float damagePerImpulseUnit = 0.5f;
    [Tooltip("Minimum speed component along the contact normal required before any damage is dealt. Prevents damage from slow grazes.")]
    [SerializeField, Min(0f)] float minImpactSpeed = 1f;

    void Awake()
    {
        Rigidbody rb = GetComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity = false;
    }

    void OnCollisionEnter(Collision collision)
    {
        // Project relative velocity onto the contact normal so tangential/sliding
        // motion doesn't count — only the head-on impact component matters.
        float impactSpeed = NormalImpactSpeed(collision);
        if (impactSpeed < minImpactSpeed) return;

        float mass = collision.rigidbody != null ? collision.rigidbody.mass : 1f;
        float damage = impactSpeed * mass * damagePerImpulseUnit;
        if (damage <= 0f) return;

        BoatPiece piece = FindBoatPiece(collision);
        piece?.TakeDamage(damage);
    }

    static float NormalImpactSpeed(Collision collision)
    {
        if (collision.contactCount == 0) return collision.relativeVelocity.magnitude;
        // Abs handles the fact that normal direction convention is unspecified across objects.
        return Mathf.Abs(Vector3.Dot(collision.relativeVelocity, collision.GetContact(0).normal));
    }

    static BoatPiece FindBoatPiece(Collision collision)
    {
        // Primary: contact.otherCollider is the exact boat child collider that touched this object.
        // Walk up its hierarchy to find the owning BoatPiece.
        for (int i = 0; i < collision.contactCount; i++)
        {
            Collider otherCol = collision.GetContact(i).otherCollider;
            if (otherCol == null) continue;
            BoatPiece piece = otherCol.GetComponentInParent<BoatPiece>();
            if (piece != null) return piece;
        }

        // Fallback: no BoatPiece ancestor found (e.g. collider sits on the Rigidbody root).
        // Find the piece whose collider surface is closest to the contact point.
        if (collision.rigidbody != null && collision.contactCount > 0)
        {
            Vector3 contact = collision.GetContact(0).point;
            BoatPiece[] all = collision.rigidbody.GetComponentsInChildren<BoatPiece>();
            BoatPiece nearest = null;
            float nearestSqr = float.MaxValue;
            foreach (BoatPiece p in all)
            {
                foreach (Collider col in p.GetComponentsInChildren<Collider>())
                {
                    if (col.isTrigger) continue;
                    float sqr = (col.ClosestPoint(contact) - contact).sqrMagnitude;
                    if (sqr < nearestSqr) { nearestSqr = sqr; nearest = p; }
                }
            }
            return nearest;
        }

        return null;
    }
}
