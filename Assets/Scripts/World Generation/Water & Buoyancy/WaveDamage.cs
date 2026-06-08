using UnityEngine;

public class WaveDamageManager : MonoBehaviour
{
    [Header("Water")]
    [SerializeField] private UrpLowPolyWater water;

    [Header("Base Damage")]
    [SerializeField] private float damagePerSecond = 3f;

    [Tooltip("A sample point must be this far under water before damage starts.")]
    [SerializeField] private float damageDepthThreshold = 0.05f;

    [Tooltip("Extra damage for deeper submersion.")]
    [SerializeField] private float depthDamageMultiplier = 1.5f;

    [Header("Impact Damage")]
    [Tooltip("Relative speed needed before water impact damage starts.")]
    [SerializeField] private float minImpactSpeed = 1.5f;

    [Tooltip("How much impact speed increases damage.")]
    [SerializeField] private float impactDamageMultiplier = 2.5f;

    [Tooltip("If true, only counts motion into the water surface instead of sideways movement.")]
    [SerializeField] private bool useNormalImpactOnly = true;

    [Header("Sampling")]
    [SerializeField] private bool damageBrokenPieces = false;

    void Awake()
    {
        if (water == null)
        {
            water = UrpLowPolyWater.ActiveSurface;
        }

        if (water == null)
        {
            water = FindAnyObjectByType<UrpLowPolyWater>();
        }
    }

    void Update()
    {
        if (water == null)
        {
            return;
        }

        BoatPiece[] pieces = FindObjectsByType<BoatPiece>(FindObjectsSortMode.None);

        foreach (BoatPiece piece in pieces)
        {
            DamagePieceFromWater(piece);
        }
    }

    void DamagePieceFromWater(BoatPiece piece)
    {
        if (piece == null)
        {
            return;
        }

        if (piece.isBroken && !damageBrokenPieces)
        {
            return;
        }

        Collider[] colliders = piece.GetComponentsInChildren<Collider>();

        if (colliders == null || colliders.Length == 0)
        {
            return;
        }

        Rigidbody rb = piece.GetComponentInParent<Rigidbody>();

        float totalDamageThisFrame = 0f;
        int colliderCount = 0;

        foreach (Collider col in colliders)
        {
            if (col == null || col.isTrigger)
            {
                continue;
            }

            totalDamageThisFrame += GetDamageForCollider(col, rb);
            colliderCount++;
        }

        if (colliderCount > 0)
        {
            totalDamageThisFrame /= colliderCount;
        }

        if (totalDamageThisFrame > 0f)
        {
            piece.TakeDamage(totalDamageThisFrame);
        }
    }

    float GetDamageForCollider(Collider col, Rigidbody rb)
    {
        Bounds bounds = col.bounds;

        Vector3 center = bounds.center;
        Vector3 bottom = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);

        Vector3 front = new Vector3(bounds.center.x, bounds.center.y, bounds.max.z);
        Vector3 back = new Vector3(bounds.center.x, bounds.center.y, bounds.min.z);
        Vector3 left = new Vector3(bounds.min.x, bounds.center.y, bounds.center.z);
        Vector3 right = new Vector3(bounds.max.x, bounds.center.y, bounds.center.z);

        Vector3[] samplePoints =
        {
            center,
            bottom,
            front,
            back,
            left,
            right
        };

        float damage = 0f;

        foreach (Vector3 point in samplePoints)
        {
            damage += GetDamageForPoint(point, rb);
        }

        return damage / samplePoints.Length;
    }

    float GetDamageForPoint(Vector3 worldPoint, Rigidbody rb)
    {
        bool foundSurface = water.TryGetSurfaceDataAtWorldPosition(
            worldPoint,
            Time.time,
            out float waterHeight,
            out Vector3 waterNormal
        );

        if (!foundSurface)
        {
            return 0f;
        }

        float depthUnderWater = waterHeight - worldPoint.y;

        if (depthUnderWater < damageDepthThreshold)
        {
            return 0f;
        }

        Vector3 waterVelocity = water.GetFlowVelocityAtWorldPosition(worldPoint, Time.time);
        Vector3 pointVelocity = Vector3.zero;

        if (rb != null)
        {
            pointVelocity = rb.GetPointVelocity(worldPoint);
        }

        Vector3 relativeVelocity = pointVelocity - waterVelocity;

        float impactSpeed;

        if (useNormalImpactOnly)
        {
            // Measures how hard the piece is moving into the water surface.
            impactSpeed = Mathf.Max(0f, -Vector3.Dot(relativeVelocity, waterNormal.normalized));
        }
        else
        {
            // Counts all relative motion through the water.
            impactSpeed = relativeVelocity.magnitude;
        }

        float depthFactor = 1f + depthUnderWater * depthDamageMultiplier;

        float impactFactor = 1f;

        if (impactSpeed > minImpactSpeed)
        {
            impactFactor += (impactSpeed - minImpactSpeed) * impactDamageMultiplier;
        }
        else
        {
            // If the piece is calmly submerged, give it much less damage.
            impactFactor = 0.2f;
        }

        return damagePerSecond * depthFactor * impactFactor * Time.deltaTime;
    }
}