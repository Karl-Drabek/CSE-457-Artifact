using UnityEngine;

/// <summary>
/// Adds HP to any hazard or objective obstacle.
/// Collision damage is physics-based: damage = relativeVelocity × collidingMass × damagePerImpulseUnit.
/// The same damage value is applied to both this obstacle and the boat piece that hit it.
/// When HP reaches zero, reports the defeat and sinks the obstacle.
/// </summary>
[DisallowMultipleComponent]
public class ObstacleHealth : MonoBehaviour
{
    [Header("Health")]
    [SerializeField, Min(1f)] float maxHealth = 200f;

    [Header("Collision Damage")]
    [Tooltip("Scales how much damage each unit of collision impulse (mass × normal impact speed) deals to this obstacle.")]
    [SerializeField, Min(0f)] float damagePerImpulseUnit = 0.5f;
    [Tooltip("Scales how much damage each unit of collision impulse deals to the boat. When 0, uses the same value as Damage Per Impulse Unit.")]
    [SerializeField, Min(0f)] float boatDamagePerImpulseUnit = 0f;
    [Tooltip("Minimum speed component along the contact normal required before any damage is dealt. Prevents damage from slow grazes.")]
    [SerializeField, Min(0f)] float minImpactSpeed = 1f;

    [Header("Death")]
    [Tooltip("Extra downward impulse applied when the obstacle starts sinking.")]
    [SerializeField, Min(0f)] float sinkDownForce = 6f;
    [Tooltip("Seconds before the sinking object is destroyed.")]
    [SerializeField, Min(0f)] float sinkDuration = 4f;

    float currentHealth;
    bool dead;

    public float MaxHealth => maxHealth;
    public float CurrentHealth => currentHealth;
    public float HealthFraction => maxHealth > 0f ? Mathf.Clamp01(currentHealth / maxHealth) : 0f;

    /// <summary>Called by World when spawning to set health and optional damage scales.</summary>
    public void Configure(float health, float dmgPerImpulse = 0f, float boatDmgPerImpulse = 0f)
    {
        maxHealth = Mathf.Max(1f, health);
        currentHealth = maxHealth;
        if (dmgPerImpulse > 0f)
            damagePerImpulseUnit = dmgPerImpulse;
        if (boatDmgPerImpulse > 0f)
            boatDamagePerImpulseUnit = boatDmgPerImpulse;
    }

    /// <summary>Called by World to restore persisted health from a previous voyage.</summary>
    public void SetCurrentHealth(float health)
    {
        currentHealth = Mathf.Clamp(health, 0f, maxHealth);
    }

    void Awake()
    {
        currentHealth = maxHealth;
    }

    public void TakeDamage(float amount)
    {
        if (dead || currentHealth <= 0f) return;
        currentHealth = Mathf.Max(0f, currentHealth - amount);
        if (currentHealth <= 0f) Die();
    }

    void OnCollisionEnter(Collision collision)
    {
        if (dead) return;

        // Use only the normal component of relative velocity so tangential sliding
        // doesn't count as damage — only the head-on impact matters.
        float impactSpeed = NormalImpactSpeed(collision);
        if (impactSpeed < minImpactSpeed) return;

        float mass = collision.rigidbody != null ? collision.rigidbody.mass : 1f;
        float obstacleDamage = impactSpeed * mass * damagePerImpulseUnit;
        float effectiveBoatRate = boatDamagePerImpulseUnit > 0f ? boatDamagePerImpulseUnit : damagePerImpulseUnit;
        float boatDamage = impactSpeed * mass * effectiveBoatRate;

        if (obstacleDamage <= 0f && boatDamage <= 0f) return;

        BoatPiece piece = FindBoatPiece(collision);
        if (piece != null && boatDamage > 0f)
            piece.TakeDamage(boatDamage);

        TakeDamage(obstacleDamage);
    }

    static float NormalImpactSpeed(Collision collision)
    {
        if (collision.contactCount == 0) return collision.relativeVelocity.magnitude;
        return Mathf.Abs(Vector3.Dot(collision.relativeVelocity, collision.GetContact(0).normal));
    }

    static BoatPiece FindBoatPiece(Collision collision)
    {
        // Primary: contact.otherCollider is the exact boat child collider that touched this obstacle.
        for (int i = 0; i < collision.contactCount; i++)
        {
            Collider otherCol = collision.GetContact(i).otherCollider;
            if (otherCol == null) continue;
            BoatPiece piece = otherCol.GetComponentInParent<BoatPiece>();
            if (piece != null) return piece;
        }

        // Fallback: collider sits on the Rigidbody root with no BoatPiece ancestor.
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

    void Die()
    {
        if (dead) return;
        dead = true;

        VoyageObstacle voyageObstacle = GetComponent<VoyageObstacle>();
        if (voyageObstacle != null)
            voyageObstacle.ReportDefeated();

        StartSinking();
    }

    void StartSinking()
    {
        // Detach so World's ClearObjectiveObstacles doesn't cull it mid-animation
        transform.SetParent(null, true);

        foreach (Collider col in GetComponentsInChildren<Collider>(true))
            col.enabled = false;

        foreach (WaterBuoyancy buoyancy in GetComponentsInChildren<WaterBuoyancy>(true))
            buoyancy.enabled = false;

        Rigidbody rb = GetComponent<Rigidbody>();
        if (rb == null)
            rb = gameObject.AddComponent<Rigidbody>();

        rb.isKinematic = false;
        rb.useGravity = true;
        rb.linearDamping = 0.3f;
        rb.angularDamping = 2f;
        rb.AddForce(Vector3.down * sinkDownForce, ForceMode.Impulse);

        Destroy(gameObject, sinkDuration);
    }
}
