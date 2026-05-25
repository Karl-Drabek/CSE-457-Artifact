using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class WaterBuoyancy : MonoBehaviour
{
    [SerializeField]
    // Auto-generated points usually behave better than hand-placed ones for boxy objects.
    bool autoGenerateSamplePoints = true;

    [SerializeField]
    Vector3[] samplePoints = new Vector3[0];

    [Header("Buoyancy")]
    [Min(0.01f)]
    public float displacedMass = 1f;

    [Min(0.001f)]
    public float maxSubmergence = 0.5f;

    [Range(0f, 1f)]
    public float surfaceNormalInfluence = 0.2f;

    [Header("Damping")]
    [Min(0f)]
    public float waterDrag = 2f;

    [Min(0f)]
    public float waterAngularDrag = 1f;


    public enum SampleMode
    {
        Lattice,
        Raycast
    }
    [Header("Auto Samples")]
    [SerializeField]
    SampleMode sampleMode = SampleMode.Raycast;

    [Range(2, 10)]
    public int horizontalSampleCount = 3;

    [Range(2, 6)]
    public int verticalSampleCount = 3;

    [Range(0f, 0.45f)]
    public float verticalEdgeInset = 0.08f;

    [Range(0f, 0.45f)]
    public float horizontalEdgeInset = 0.08f;

    [Range(-1f, 1f)]
    public float xEdgeOffset = 0f;

    [Range(-1f, 1f)]
    public float zEdgeOffset = 0f;


    Rigidbody body;
    Collider[] cachedColliders;
    Vector3[] autoSamplePoints = new Vector3[0];

    // Called by Unity before the first physics update if the component starts enabled.
    // Caches required components and prepares the current buoyancy sample layout.
    void Awake()
    {
        EnsureSetup();
        RefreshAutoSamplePoints();
    }

    // Called by Unity when the component becomes active.
    // Refreshes references and sample points in case the object changed while disabled.
    void OnEnable()
    {
        EnsureSetup();
        RefreshAutoSamplePoints();
    }

    // Called by Unity in the editor when serialized fields change.
    // Rebuilds the sample layout immediately so gizmos and buoyancy settings stay accurate.
    void OnValidate()
    {
        EnsureSetup();
        RefreshAutoSamplePoints();
    }

    // Called by Unity when the component is first added or reset from the Inspector.
    // Sets a sensible default displaced mass and regenerates the sample layout.
    void Reset()
    {
        EnsureSetup();
        if (body != null)
        {
            displacedMass = Mathf.Max(0.01f, body.mass);
        }

        RefreshAutoSamplePoints();
    }

    // Called by Unity once per physics step.
    // Samples the water surface, applies distributed buoyancy, then adds whole-body water damping.
    void FixedUpdate()
    {
        if (body == null)
        {
            return;
        }

        UrpLowPolyWater water = UrpLowPolyWater.ActiveSurface;
        if (water == null)
        {
            // Fall back to a scene search in case the active surface was not registered yet.
            water = FindAnyObjectByType<UrpLowPolyWater>();
            if (water == null)
            {
                return;
            }
        }

        Vector3[] activeSamplePoints = GetActiveSamplePoints();
        if (activeSamplePoints == null || activeSamplePoints.Length == 0)
        {
            return;
        }

        // Sample the water at physics time so buoyancy is tied to the same timeline as the rigidbody.
        float timeSeconds = Time.fixedTime;
        float gravityMagnitude = Mathf.Abs(Physics.gravity.y);
        // Split the object's displaced mass across all sample points so total lift stays stable.
        float buoyancyPerPoint = gravityMagnitude * displacedMass / activeSamplePoints.Length;
        float submergedFractionSum = 0f;
        int submergedPointCount = 0;

        for (int i = 0; i < activeSamplePoints.Length; i++)
        {
            Vector3 worldPoint = transform.TransformPoint(activeSamplePoints[i]);
            if (!water.TryGetSurfaceDataAtWorldPosition(worldPoint, timeSeconds, out float waterHeight, out Vector3 waterNormal))
            {
                continue;
            }

            float submergenceDepth = waterHeight - worldPoint.y;
            if (submergenceDepth <= 0f)
            {
                continue;
            }

            float submergence = Mathf.Clamp01(submergenceDepth / Mathf.Max(maxSubmergence, 0.001f));
            submergedFractionSum += submergence;
            submergedPointCount++;

            // Applying force at each point lets buoyancy create roll and pitch instead of only moving up.
            Vector3 buoyancyDirection = Vector3.Slerp(Vector3.up, waterNormal, surfaceNormalInfluence).normalized;
            Vector3 buoyancyForce = buoyancyDirection * (buoyancyPerPoint * submergence);
            body.AddForceAtPosition(buoyancyForce, worldPoint, ForceMode.Force);
        }

        if (submergedPointCount > 0)
        {
            // Average submergence gives us stronger damping as more of the object sinks into the water.
            float averageSubmergence = submergedFractionSum / activeSamplePoints.Length;

            if (waterDrag > 0f)
            {
                // Apply drag once at the rigidbody level so it does not multiply by sample count
                // or spike from large corner velocities while the body is rotating in the water.
                body.AddForce(-body.linearVelocity * (waterDrag * averageSubmergence), ForceMode.Acceleration);
            }

            if (waterAngularDrag > 0f)
            {
                body.AddTorque(-body.angularVelocity * (waterAngularDrag * averageSubmergence), ForceMode.Acceleration);
            }
        }
    }

    // Caches the rigidbody and collider set used by the buoyancy calculations.
    // Called by Unity lifecycle methods before generating sample points or applying forces.
    void EnsureSetup()
    {
        body = GetComponent<Rigidbody>();
        if (cachedColliders == null || cachedColliders.Length == 0)
        {
            cachedColliders = GetComponentsInChildren<Collider>();
        }
    }

    // Returns either the user-authored sample points or the current auto-generated lattice.
    // Called from the physics loop and gizmo drawing so both systems use the same points.
    Vector3[] GetActiveSamplePoints()
    {
        if (!autoGenerateSamplePoints && samplePoints != null && samplePoints.Length > 0)
        {
            return samplePoints;
        }

        if (autoSamplePoints == null || autoSamplePoints.Length == 0)
        {
            RefreshAutoSamplePoints();
        }

        return autoSamplePoints;
    }

    // Rebuilds the auto-generated sample lattice from the object's collider bounds.
    // Called after setup changes so buoyancy forces remain distributed across the current shape.
    void RefreshAutoSamplePoints()
    {
        EnsureSetup();

        if (cachedColliders == null || cachedColliders.Length == 0)
        {
            autoSamplePoints = CreateFallbackSamplePoints();
            return;
        }

        Bounds worldBounds = GetCombinedWorldBounds();
        if (worldBounds.size == Vector3.zero)
        {
            autoSamplePoints = CreateFallbackSamplePoints();
            return;
        }

        float verticalInset = Mathf.Clamp(verticalEdgeInset, 0f, 0.45f);
        float horizontalInset = Mathf.Clamp(horizontalEdgeInset, 0f, 0.45f);
        float xoffset = Mathf.Clamp(xEdgeOffset, -1f, 1f);
        float zoffset = Mathf.Clamp(zEdgeOffset, -1f, 1f);
        int safeHorizontalCount = Mathf.Max(2, horizontalSampleCount);
        int safeVerticalCount = Mathf.Max(2, verticalSampleCount);

        autoSamplePoints = sampleMode == SampleMode.Raycast
            ? GenerateRaycastSamplePoints(worldBounds, safeHorizontalCount, xoffset, zoffset, horizontalInset)
            : GenerateLatticePoints(worldBounds, safeHorizontalCount, safeVerticalCount, verticalInset, horizontalInset);

        if (autoSamplePoints == null || autoSamplePoints.Length == 0)
        {
            autoSamplePoints = CreateFallbackSamplePoints();
        }
    }

    Vector3[] GenerateRaycastSamplePoints(Bounds worldBounds, int horizCount, float xoffset, float zoffset, float horizontalInset)
    {
        float rayStartY = worldBounds.min.y - 0.5f;
        float rayLength = worldBounds.size.y + 1f;

        List<Vector3> generatedPoints = new List<Vector3>();

        for (int zIndex = 0; zIndex < horizCount; zIndex++)
        {
            float zT = Mathf.Lerp(horizontalInset + zoffset, 1f - horizontalInset + zoffset, zIndex / (horizCount - 1f));
            float z = Mathf.Lerp(worldBounds.min.z, worldBounds.max.z, zT);

            for (int xIndex = 0; xIndex < horizCount; xIndex++)
            {
                float xT = Mathf.Lerp(horizontalInset + xoffset, 1f - horizontalInset + xoffset, xIndex / (horizCount - 1f));
                float x = Mathf.Lerp(worldBounds.min.x, worldBounds.max.x, xT);

                Vector3 rayOrigin = new Vector3(x, rayStartY, z);
                if (Physics.Raycast(rayOrigin, Vector3.up, out RaycastHit hit, rayLength))
                {
                    // Convert to local space for storage
                    generatedPoints.Add(transform.InverseTransformPoint(hit.point));
                }
                // If no hit, this column of the ship simply has no sample point
            }
        }

        return generatedPoints.ToArray();
    }

    Vector3[] GenerateLatticePoints(Bounds worldBounds, int horizCount, int vertCount, float verticalInset, float horizontalInset)
    {
        List<Vector3> generatedPoints = new List<Vector3>(horizCount * horizCount * vertCount);

        for (int yIndex = 0; yIndex < vertCount; yIndex++)
        {
            float yT = Mathf.Lerp(verticalInset, 1f - verticalInset, yIndex / (vertCount - 1f));
            float y = Mathf.Lerp(worldBounds.min.y, worldBounds.max.y, yT);

            for (int zIndex = 0; zIndex < horizCount; zIndex++)
            {
                float zT = Mathf.Lerp(horizontalInset, 1f - horizontalInset, zIndex / (horizCount - 1f));
                float z = Mathf.Lerp(worldBounds.min.z, worldBounds.max.z, zT);

                for (int xIndex = 0; xIndex < horizCount; xIndex++)
                {
                    float xT = Mathf.Lerp(horizontalInset, 1f - horizontalInset, xIndex / (horizCount - 1f));
                    float x = Mathf.Lerp(worldBounds.min.x, worldBounds.max.x, xT);
                    generatedPoints.Add(transform.InverseTransformPoint(new Vector3(x, y, z)));
                }
            }
        }

        return generatedPoints.ToArray();
    }

    Bounds GetCombinedWorldBounds()
    {
        bool hasBounds = false;
        Bounds combined = default;

        for (int i = 0; i < cachedColliders.Length; i++)
        {
            Collider collider = cachedColliders[i];
            if (collider == null || collider.isTrigger)
            {
                continue;
            }

            if (!hasBounds)
            {
                combined = collider.bounds;
                hasBounds = true;
            }
            else
            {
                combined.Encapsulate(collider.bounds);
            }
        }

        return combined;
    }

    // Returns a simple box-shaped sample set when there are no non-trigger colliders to inspect.
    // Called as a fallback so buoyancy can still operate in a basic way on incomplete objects.
    Vector3[] CreateFallbackSamplePoints()
    {
        // Fallback points approximate a small box volume when there are no colliders to sample from.
        return new[]
        {
            new Vector3(-0.4f, -0.4f, -0.4f),
            new Vector3(0f, -0.4f, -0.4f),
            new Vector3(0.4f, -0.4f, -0.4f),
            new Vector3(-0.4f, -0.4f, 0f),
            new Vector3(0f, -0.4f, 0f),
            new Vector3(0.4f, -0.4f, 0f),
            new Vector3(-0.4f, -0.4f, 0.4f),
            new Vector3(0f, -0.4f, 0.4f),
            new Vector3(0.4f, -0.4f, 0.4f),
            new Vector3(-0.4f, 0.4f, -0.4f),
            new Vector3(0f, 0.4f, -0.4f),
            new Vector3(0.4f, 0.4f, -0.4f),
            new Vector3(-0.4f, 0.4f, 0f),
            new Vector3(0f, 0.4f, 0f),
            new Vector3(0.4f, 0.4f, 0f),
            new Vector3(-0.4f, 0.4f, 0.4f),
            new Vector3(0f, 0.4f, 0.4f),
            new Vector3(0.4f, 0.4f, 0.4f)
        };
    }

    // Called by Unity when the object is selected in the Scene view.
    // Draws the effective sample points so buoyancy layout issues are easy to debug visually.
    void OnDrawGizmosSelected()
    {
        Vector3[] activeSamplePoints = Application.isPlaying ? GetActiveSamplePoints() : (autoGenerateSamplePoints ? autoSamplePoints : samplePoints);
        if (activeSamplePoints == null)
        {
            return;
        }

        // Draw the effective sample set so buoyancy stability issues are easy to inspect in the editor.
        Gizmos.color = new Color(0.15f, 0.8f, 1f, 0.9f);
        for (int i = 0; i < activeSamplePoints.Length; i++)
        {
            Gizmos.DrawSphere(transform.TransformPoint(activeSamplePoints[i]), 0.05f);
        }
    }
}
