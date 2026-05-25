using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

[RequireComponent(typeof(Rigidbody))]
public class WaterBuoyancy : MonoBehaviour
{
    const float MinDensity = 0.0001f;
    const float MinSubmergenceRange = 0.001f;
    const float MinWeightSum = 0.0001f;
    const float DefaultEdgeInset = 0.08f;

    [Serializable]
    public struct SamplePoint
    {
        public Vector3 localPosition;

        [Min(0f)]
        public float weight;

        public SamplePoint(Vector3 localPosition, float weight)
        {
            this.localPosition = localPosition;
            this.weight = Mathf.Max(0f, weight);
        }
    }

    public enum SampleMode
    {
        Lattice,
        Raycast
    }

    static readonly SamplePoint[] FallbackSamplePoints =
    {
        new SamplePoint(new Vector3(-0.4f, -0.4f, -0.4f), 1f),
        new SamplePoint(new Vector3(0f, -0.4f, -0.4f), 1f),
        new SamplePoint(new Vector3(0.4f, -0.4f, -0.4f), 1f),
        new SamplePoint(new Vector3(-0.4f, -0.4f, 0f), 1f),
        new SamplePoint(new Vector3(0f, -0.4f, 0f), 1f),
        new SamplePoint(new Vector3(0.4f, -0.4f, 0f), 1f),
        new SamplePoint(new Vector3(-0.4f, -0.4f, 0.4f), 1f),
        new SamplePoint(new Vector3(0f, -0.4f, 0.4f), 1f),
        new SamplePoint(new Vector3(0.4f, -0.4f, 0.4f), 1f),
        new SamplePoint(new Vector3(-0.4f, 0.4f, -0.4f), 1f),
        new SamplePoint(new Vector3(0f, 0.4f, -0.4f), 1f),
        new SamplePoint(new Vector3(0.4f, 0.4f, -0.4f), 1f),
        new SamplePoint(new Vector3(-0.4f, 0.4f, 0f), 1f),
        new SamplePoint(new Vector3(0f, 0.4f, 0f), 1f),
        new SamplePoint(new Vector3(0.4f, 0.4f, 0f), 1f),
        new SamplePoint(new Vector3(-0.4f, 0.4f, 0.4f), 1f),
        new SamplePoint(new Vector3(0f, 0.4f, 0.4f), 1f),
        new SamplePoint(new Vector3(0.4f, 0.4f, 0.4f), 1f)
    };

    [SerializeField]
    // Auto-generated points usually behave better than hand-placed ones for boxy objects.
    bool autoGenerateSamplePoints = true;

    [SerializeField]
    SamplePoint[] manualSamplePoints = Array.Empty<SamplePoint>();

    // Hidden migration fields keep older scenes loading after the sampling redesign.
    [SerializeField, HideInInspector, FormerlySerializedAs("samplePoints")]
    Vector3[] legacySamplePoints = Array.Empty<Vector3>();

    [FormerlySerializedAs("fluidDensity")]
    [FormerlySerializedAs("density")]
    [FormerlySerializedAs("mass")]
    [FormerlySerializedAs("displacedMass")]
    [Min(MinDensity)]
    public float objectDensity = 1f;

    [Min(0.001f)]
    public float maxSubmergence = 0.5f;

    [Range(0f, 1f)]
    public float surfaceNormalInfluence = 0.2f;

    [Min(0f)]
    public float waterDrag = 2f;

    [Min(0f)]
    public float waterAngularDrag = 1f;

    [SerializeField]
    SampleMode sampleMode = SampleMode.Raycast;

    [Range(2, 10)]
    public int horizontalSampleCount = 3;

    [Range(2, 6)]
    public int verticalSampleCount = 3;

    [Range(0f, 0.45f)]
    public float verticalEdgeInset = DefaultEdgeInset;

    [Range(0f, 0.45f)]
    public float horizontalEdgeInset = DefaultEdgeInset;

    [Range(-1f, 1f)]
    public float xEdgeOffset = 0f;

    [Range(-1f, 1f)]
    public float zEdgeOffset = 0f;

    [SerializeField, HideInInspector, FormerlySerializedAs("sampleEdgeInset")]
    float legacySampleEdgeInset = DefaultEdgeInset;

    Rigidbody body;
    Collider[] cachedColliders = Array.Empty<Collider>();
    SamplePoint[] autoSamplePoints = Array.Empty<SamplePoint>();

    void Awake()
    {
        RefreshState();
    }

    void OnEnable()
    {
        RefreshState();
    }

    void OnValidate()
    {
        RefreshState();
    }

    void Reset()
    {
        objectDensity = 1f;
        sampleMode = SampleMode.Raycast;
        verticalEdgeInset = DefaultEdgeInset;
        horizontalEdgeInset = DefaultEdgeInset;
        xEdgeOffset = 0f;
        zEdgeOffset = 0f;

        RefreshState();
    }

    void FixedUpdate()
    {
        if (body == null)
        {
            return;
        }

        if (!TryResolveWaterSurface(out UrpLowPolyWater water))
        {
            return;
        }

        SamplePoint[] activeSamplePoints = GetActiveSamplePoints();
        if (activeSamplePoints.Length == 0)
        {
            return;
        }

        float fullSubmersionBuoyancyStrength = GetFullSubmersionBuoyancyStrength();
        float gravityMagnitude = Physics.gravity.magnitude;
        float totalWeight = GetTotalWeight(activeSamplePoints);
        float submergedFractionSum = 0f;
        bool hasSubmergedPoint = false;

        // Sample the water at physics time so buoyancy is tied to the same timeline as the rigidbody.
        float timeSeconds = Time.fixedTime;

        for (int i = 0; i < activeSamplePoints.Length; i++)
        {
            SamplePoint samplePoint = activeSamplePoints[i];
            float normalizedWeight = GetNormalizedWeight(samplePoint.weight, totalWeight, activeSamplePoints.Length);
            if (normalizedWeight <= 0f)
            {
                continue;
            }

            Vector3 worldPoint = transform.TransformPoint(samplePoint.localPosition);
            if (!water.TryGetSurfaceDataAtWorldPosition(worldPoint, timeSeconds, out float waterHeight, out Vector3 waterNormal))
            {
                continue;
            }

            float submergenceDepth = waterHeight - worldPoint.y;
            if (submergenceDepth <= 0f)
            {
                continue;
            }

            float submergence = Mathf.Clamp01(submergenceDepth / Mathf.Max(maxSubmergence, MinSubmergenceRange));
            submergedFractionSum += submergence * normalizedWeight;
            hasSubmergedPoint = true;

            // Applying force at each point lets buoyancy create roll and pitch instead of only moving up.
            Vector3 buoyancyDirection = Vector3.Slerp(Vector3.up, waterNormal, surfaceNormalInfluence).normalized;
            Vector3 buoyancyForce = buoyancyDirection * (gravityMagnitude * fullSubmersionBuoyancyStrength * normalizedWeight * submergence);
            body.AddForceAtPosition(buoyancyForce, worldPoint, ForceMode.Force);
        }

        if (hasSubmergedPoint)
        {
            // Weighted submergence keeps damping aligned with the same point weighting used for buoyancy.
            float weightedSubmergence = Mathf.Clamp01(submergedFractionSum);

            if (waterDrag > 0f)
            {
                // Apply drag once at the rigidbody level so it does not multiply by sample count
                // or spike from large corner velocities while the body is rotating in the water.
                body.AddForce(-body.linearVelocity * (waterDrag * weightedSubmergence), ForceMode.Acceleration);
            }

            if (waterAngularDrag > 0f)
            {
                body.AddTorque(-body.angularVelocity * (waterAngularDrag * weightedSubmergence), ForceMode.Acceleration);
            }
        }
    }

    // Rebuilds cached references, upgrades older serialized data, and refreshes auto samples.
    void RefreshState()
    {
        EnsureSetup();
        UpgradeLegacyManualSamplePoints();
        UpgradeLegacyInsetSettings();
        SanitizeSampleSettings();
        RefreshAutoSamplePoints();
    }

    // Finds the active water surface, with a scene lookup fallback for editor setup order issues.
    bool TryResolveWaterSurface(out UrpLowPolyWater water)
    {
        water = UrpLowPolyWater.ActiveSurface;
        if (water != null)
        {
            return true;
        }

        water = FindAnyObjectByType<UrpLowPolyWater>();
        return water != null;
    }

    // Caches the rigidbody and collider set used by buoyancy and sample generation.
    void EnsureSetup()
    {
        body = GetComponent<Rigidbody>();
        cachedColliders = GetComponentsInChildren<Collider>();
    }

    // Converts older position-only sample data into the current weighted manual format.
    void UpgradeLegacyManualSamplePoints()
    {
        if ((manualSamplePoints != null && manualSamplePoints.Length > 0)
            || legacySamplePoints == null
            || legacySamplePoints.Length == 0)
        {
            return;
        }

        manualSamplePoints = new SamplePoint[legacySamplePoints.Length];
        for (int i = 0; i < legacySamplePoints.Length; i++)
        {
            manualSamplePoints[i] = new SamplePoint(legacySamplePoints[i], 1f);
        }

        legacySamplePoints = Array.Empty<Vector3>();
    }

    // Copies the old single inset setting into the newer horizontal and vertical fields once.
    void UpgradeLegacyInsetSettings()
    {
        if (!Mathf.Approximately(verticalEdgeInset, DefaultEdgeInset)
            || !Mathf.Approximately(horizontalEdgeInset, DefaultEdgeInset)
            || Mathf.Approximately(legacySampleEdgeInset, DefaultEdgeInset))
        {
            return;
        }

        verticalEdgeInset = legacySampleEdgeInset;
        horizontalEdgeInset = legacySampleEdgeInset;
    }

    // Clamps serialized settings into safe ranges before they are used at runtime or in gizmos.
    void SanitizeSampleSettings()
    {
        objectDensity = Mathf.Max(MinDensity, objectDensity);

        if (manualSamplePoints == null)
        {
            manualSamplePoints = Array.Empty<SamplePoint>();
        }
        else
        {
            for (int i = 0; i < manualSamplePoints.Length; i++)
            {
                SamplePoint samplePoint = manualSamplePoints[i];
                samplePoint.weight = Mathf.Max(0f, samplePoint.weight);
                manualSamplePoints[i] = samplePoint;
            }
        }

        verticalEdgeInset = Mathf.Clamp(verticalEdgeInset, 0f, 0.45f);
        horizontalEdgeInset = Mathf.Clamp(horizontalEdgeInset, 0f, 0.45f);
        xEdgeOffset = Mathf.Clamp(xEdgeOffset, -1f, 1f);
        zEdgeOffset = Mathf.Clamp(zEdgeOffset, -1f, 1f);
        horizontalSampleCount = Mathf.Max(2, horizontalSampleCount);
        verticalSampleCount = Mathf.Max(2, verticalSampleCount);
    }

    // Returns the currently active point set so physics and gizmos stay in sync.
    SamplePoint[] GetActiveSamplePoints()
    {
        if (!autoGenerateSamplePoints)
        {
            return manualSamplePoints;
        }

        if (autoSamplePoints.Length == 0)
        {
            RefreshAutoSamplePoints();
        }

        return autoSamplePoints;
    }

    // Water density is fixed at 1, so a denser object displaces less water for the same rigidbody mass.
    float GetFullSubmersionBuoyancyStrength()
    {
        return Mathf.Max(0f, body.mass) / objectDensity;
    }

    // Sums the positive weights used to divide buoyancy across manual points.
    float GetTotalWeight(SamplePoint[] samplePoints)
    {
        if (samplePoints == null)
        {
            return 0f;
        }

        float totalWeight = 0f;
        for (int i = 0; i < samplePoints.Length; i++)
        {
            totalWeight += Mathf.Max(0f, samplePoints[i].weight);
        }

        return totalWeight;
    }

    // Converts one point weight into a normalized share of the overall buoyancy force.
    float GetNormalizedWeight(float pointWeight, float totalWeight, int sampleCount)
    {
        if (sampleCount <= 0)
        {
            return 0f;
        }

        if (totalWeight <= MinWeightSum)
        {
            return 1f / sampleCount;
        }

        return Mathf.Max(0f, pointWeight) / totalWeight;
    }

    // Rebuilds the currently selected auto-generated sample layout from this object's colliders.
    void RefreshAutoSamplePoints()
    {
        EnsureSetup();

        if (cachedColliders == null || cachedColliders.Length == 0)
        {
            autoSamplePoints = FallbackSamplePoints;
            return;
        }

        if (!TryGetCombinedWorldBounds(out Bounds worldBounds))
        {
            autoSamplePoints = FallbackSamplePoints;
            return;
        }

        int safeHorizontalCount = Mathf.Max(2, horizontalSampleCount);
        int safeVerticalCount = Mathf.Max(2, verticalSampleCount);

        autoSamplePoints = sampleMode == SampleMode.Raycast
            ? GenerateRaycastSamplePoints(worldBounds, safeHorizontalCount, horizontalEdgeInset, xEdgeOffset, zEdgeOffset)
            : GenerateLatticePoints(worldBounds, safeHorizontalCount, safeVerticalCount, verticalEdgeInset, horizontalEdgeInset);

        if (autoSamplePoints.Length == 0)
        {
            autoSamplePoints = FallbackSamplePoints;
        }
    }

    // Builds a surface-following sample grid by raycasting upward through this object's colliders.
    SamplePoint[] GenerateRaycastSamplePoints(Bounds worldBounds, int safeHorizontalCount, float inset, float xOffset, float zOffset)
    {
        float rayStartY = worldBounds.min.y - 0.5f;
        float rayLength = worldBounds.size.y + 1f;
        List<SamplePoint> generatedPoints = new List<SamplePoint>(safeHorizontalCount * safeHorizontalCount);

        for (int zIndex = 0; zIndex < safeHorizontalCount; zIndex++)
        {
            float zT = Mathf.Lerp(inset + zOffset, 1f - inset + zOffset, zIndex / (safeHorizontalCount - 1f));
            float z = Mathf.Lerp(worldBounds.min.z, worldBounds.max.z, zT);

            for (int xIndex = 0; xIndex < safeHorizontalCount; xIndex++)
            {
                float xT = Mathf.Lerp(inset + xOffset, 1f - inset + xOffset, xIndex / (safeHorizontalCount - 1f));
                float x = Mathf.Lerp(worldBounds.min.x, worldBounds.max.x, xT);

                Vector3 rayOrigin = new Vector3(x, rayStartY, z);
                if (!TryGetOwnRaycastHit(rayOrigin, rayLength, out RaycastHit hit))
                {
                    continue;
                }

                generatedPoints.Add(new SamplePoint(transform.InverseTransformPoint(hit.point), 1f));
            }
        }

        return generatedPoints.ToArray();
    }

    // Fills the collider bounds with a 3D lattice, which works well for simple volumetric shapes.
    SamplePoint[] GenerateLatticePoints(Bounds worldBounds, int safeHorizontalCount, int safeVerticalCount, float verticalInset, float horizontalInset)
    {
        List<SamplePoint> generatedPoints = new List<SamplePoint>(safeHorizontalCount * safeHorizontalCount * safeVerticalCount);

        for (int yIndex = 0; yIndex < safeVerticalCount; yIndex++)
        {
            float yT = Mathf.Lerp(verticalInset, 1f - verticalInset, yIndex / (safeVerticalCount - 1f));
            float y = Mathf.Lerp(worldBounds.min.y, worldBounds.max.y, yT);

            for (int zIndex = 0; zIndex < safeHorizontalCount; zIndex++)
            {
                float zT = Mathf.Lerp(horizontalInset, 1f - horizontalInset, zIndex / (safeHorizontalCount - 1f));
                float z = Mathf.Lerp(worldBounds.min.z, worldBounds.max.z, zT);

                for (int xIndex = 0; xIndex < safeHorizontalCount; xIndex++)
                {
                    float xT = Mathf.Lerp(horizontalInset, 1f - horizontalInset, xIndex / (safeHorizontalCount - 1f));
                    float x = Mathf.Lerp(worldBounds.min.x, worldBounds.max.x, xT);
                    generatedPoints.Add(new SamplePoint(transform.InverseTransformPoint(new Vector3(x, y, z)), 1f));
                }
            }
        }

        return generatedPoints.ToArray();
    }

    // Keeps raycast sampling from snapping to unrelated colliders outside this buoyancy hierarchy.
    bool TryGetOwnRaycastHit(Vector3 rayOrigin, float rayLength, out RaycastHit closestHit)
    {
        RaycastHit[] hits = Physics.RaycastAll(rayOrigin, Vector3.up, rayLength, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        closestHit = default;

        bool foundHit = false;
        float closestDistance = float.PositiveInfinity;
        for (int hitIndex = 0; hitIndex < hits.Length; hitIndex++)
        {
            RaycastHit hit = hits[hitIndex];
            if (!IsOwnedCollider(hit.collider) || hit.distance >= closestDistance)
            {
                continue;
            }

            closestDistance = hit.distance;
            closestHit = hit;
            foundHit = true;
        }

        return foundHit;
    }

    // Returns true when a collider belongs to this buoyancy object or one of its children.
    bool IsOwnedCollider(Collider collider)
    {
        if (collider == null || cachedColliders == null)
        {
            return false;
        }

        return Array.IndexOf(cachedColliders, collider) >= 0;
    }

    // Combines all non-trigger collider bounds into one world-space box for auto-sample generation.
    bool TryGetCombinedWorldBounds(out Bounds combined)
    {
        bool hasBounds = false;
        combined = default;

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

        return hasBounds;
    }

    // Draws the effective point layout so sample placement is visible while tuning buoyancy.
    void OnDrawGizmosSelected()
    {
        if (!Application.isPlaying && autoGenerateSamplePoints && autoSamplePoints.Length == 0)
        {
            RefreshAutoSamplePoints();
        }

        SamplePoint[] activeSamplePoints = Application.isPlaying
            ? GetActiveSamplePoints()
            : (autoGenerateSamplePoints ? autoSamplePoints : manualSamplePoints);

        if (activeSamplePoints == null || activeSamplePoints.Length == 0)
        {
            return;
        }

        float totalWeight = GetTotalWeight(activeSamplePoints);

        // Draw the effective sample set so buoyancy stability issues are easy to inspect in the editor.
        Gizmos.color = new Color(0.15f, 0.8f, 1f, 0.9f);
        for (int i = 0; i < activeSamplePoints.Length; i++)
        {
            float normalizedWeight = GetNormalizedWeight(activeSamplePoints[i].weight, totalWeight, activeSamplePoints.Length);
            float visualWeight = Mathf.Clamp(normalizedWeight * Mathf.Max(activeSamplePoints.Length, 1), 0.5f, 2.5f);
            float gizmoSize = 0.035f + (0.015f * visualWeight);
            Gizmos.DrawSphere(transform.TransformPoint(activeSamplePoints[i].localPosition), gizmoSize);
        }
    }
}
