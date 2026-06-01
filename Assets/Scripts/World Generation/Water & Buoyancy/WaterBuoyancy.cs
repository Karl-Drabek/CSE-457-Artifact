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
    const float MinWaterFlowSpeed = 0.0001f;
    const float MinRelativeWaterSpeed = 0.0001f;
    const float MinAngularSpeed = 0.0001f;

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
        Raycast,
        Manual
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
    public float objectDensity = 0.25f;

    [Min(0.001f)]
    public float maxSubmergence = 2f;

    [Range(0f, 1f)]
    public float surfaceNormalInfluence = 0.1f;

    [Min(0f)]
    public float waterDrag = 5f;

    [FormerlySerializedAs("generalAngularDrag")]
    [Min(0f)]
    public float waterAngularDrag = 5f;

    [SerializeField]
    public SampleMode sampleMode = SampleMode.Raycast;

    [Range(2, 20)]
    public int horizontalSampleCount = 8;

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

    [Range(-5f, 5f)]
    public float hull_height = 1f;

    [Min(0f)]
    public float pitchDamping = 2f;

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
        waterAngularDrag = 1f;
        //sampleMode = SampleMode.Raycast;
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

            if (waterDrag > 0f)
            {
                ApplyPointWaterFlowForce(water, worldPoint, normalizedWeight, submergence, timeSeconds);
            }
        }

        if (hasSubmergedPoint)
        {
            // Weighted submergence keeps damping aligned with the same point weighting used for buoyancy.
            float weightedSubmergence = Mathf.Clamp01(submergedFractionSum);

            if (waterDrag > 0f)
            {
                // Keep baseline damping at the rigidbody level so first water contact does not create sharp torque spikes.
                body.AddForce(-body.linearVelocity * (waterDrag * weightedSubmergence), ForceMode.Acceleration);
            }

            ApplyWaterAngularDrag(weightedSubmergence);
            if (pitchDamping > 0f)
            {
                float pitchVelocity = body.angularVelocity.x;
                body.AddTorque(-Vector3.right * (pitchVelocity * pitchDamping * weightedSubmergence), ForceMode.Acceleration);
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
        waterAngularDrag = Mathf.Max(0f, waterAngularDrag);

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

    // Applies localized current forces without turning calm-water splash contact into a torque spike.
    void ApplyPointWaterFlowForce(UrpLowPolyWater water, Vector3 worldPoint, float normalizedWeight, float submergence, float timeSeconds)
    {
        Vector3 waterVelocity = water.GetFlowVelocityAtWorldPosition(worldPoint, timeSeconds);
        if (waterVelocity.sqrMagnitude <= MinWaterFlowSpeed * MinWaterFlowSpeed)
        {
            return;
        }

        // Use point velocity here so the whirlpool can push different parts of the hull differently
        // and generate the expected turning force without relying on center-of-mass motion alone.
        Vector3 pointVelocity = body.GetPointVelocity(worldPoint);
        Vector3 relativePlanarVelocity = Vector3.ProjectOnPlane(pointVelocity - waterVelocity, Vector3.up);
        if (relativePlanarVelocity.sqrMagnitude <= MinRelativeWaterSpeed * MinRelativeWaterSpeed)
        {
            return;
        }

        Vector3 waterFlowForce = -relativePlanarVelocity * (waterDrag * normalizedWeight * submergence);
        body.AddForceAtPosition(waterFlowForce, worldPoint, ForceMode.Acceleration);
    }

    // Applies one shared angular damping term once the hull has some submergence.
    void ApplyWaterAngularDrag(float weightedSubmergence)
    {
        if (waterAngularDrag <= 0f || body.angularVelocity.sqrMagnitude <= MinAngularSpeed * MinAngularSpeed)
        {
            return;
        }

        body.AddTorque(-body.angularVelocity * (waterAngularDrag * weightedSubmergence), ForceMode.Acceleration);
    }

    // Rebuilds the currently selected auto-generated sample layout from this object's colliders.
    void RefreshAutoSamplePoints()
    {
        EnsureSetup();

        if (sampleMode == SampleMode.Manual)
        {
            return;
        }

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
        // Use local bounds directly so rotation doesn't skew the grid
        if (!TryGetCombinedLocalBounds(out Bounds localBounds))
        {
            return Array.Empty<SamplePoint>();
        }

        Vector3 localMin = localBounds.min;
        Vector3 localMax = localBounds.max;

        float rayStartLocalY = localMin.y - 0.5f;
        float rayLength = localBounds.size.y + 1f;

        List<SamplePoint> generatedPoints = new List<SamplePoint>(safeHorizontalCount * safeHorizontalCount);

        for (int zIndex = 0; zIndex < safeHorizontalCount; zIndex++)
        {
            float zT = Mathf.Lerp(inset + zOffset, 1f - inset + zOffset, zIndex / (safeHorizontalCount - 1f));
            float localZ = Mathf.Lerp(localMin.z, localMax.z, zT);

            for (int xIndex = 0; xIndex < safeHorizontalCount; xIndex++)
            {
                float xT = Mathf.Lerp(inset + xOffset, 1f - inset + xOffset, xIndex / (safeHorizontalCount - 1f));
                float localX = Mathf.Lerp(localMin.x, localMax.x, xT);

                Vector3 localRayOrigin = new Vector3(localX, rayStartLocalY, localZ);
                Vector3 worldRayOrigin = transform.TransformPoint(localRayOrigin);
                Vector3 worldRayDirection = transform.up;

                RaycastHit[] hits = Physics.RaycastAll(worldRayOrigin, worldRayDirection, rayLength, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                if (hits.Length == 0)
                {
                    continue;
                }

                RaycastHit closestHit = default;
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

                if (!foundHit)
                {
                    continue;
                }

                float weight = calculateWeight(localMin.y, transform.InverseTransformPoint(closestHit.point).y);
                weight = Mathf.Max(0.2f, weight);
                generatedPoints.Add(new SamplePoint(transform.InverseTransformPoint(closestHit.point), weight));
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
    public bool TryGetCombinedWorldBounds(out Bounds combined)
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

    bool TryGetCombinedLocalBounds(out Bounds combined)
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

            // Get bounds in local space directly depending on collider type
            Bounds localBounds;
            if (collider is BoxCollider box)
            {
                localBounds = new Bounds(box.center, box.size);
            }
            else if (collider is SphereCollider sphere)
            {
                float diameter = sphere.radius * 2f;
                localBounds = new Bounds(sphere.center, new Vector3(diameter, diameter, diameter));
            }
            else if (collider is CapsuleCollider capsule)
            {
                float diameter = capsule.radius * 2f;
                Vector3 size = new Vector3(diameter, capsule.height, diameter);
                localBounds = new Bounds(capsule.center, size);
            }
            else if (collider is MeshCollider mesh && mesh.sharedMesh != null)
            {
                localBounds = mesh.sharedMesh.bounds;
            }
            else
            {
                continue;
            }

            if (!hasBounds)
            {
                combined = localBounds;
                hasBounds = true;
            }
            else
            {
                combined.Encapsulate(localBounds);
            }
        }

        return hasBounds;
    }

    public void SetManualPoints(Vector3[] points, float local_min_y)
    {
        autoGenerateSamplePoints = false;
        sampleMode = SampleMode.Manual;
        SamplePoint[] samples = new SamplePoint[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            Vector3 point = points[i];
            samples[i] = new SamplePoint(point, Mathf.Max(0.1f, calculateWeight(local_min_y, point.y)));
        }
        manualSamplePoints = samples;
        ClearAutoGeneratedPoints();
        RefreshState();
    }

    void ClearAutoGeneratedPoints()
    {
        autoSamplePoints = Array.Empty<SamplePoint>();
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

        DrawGizmoPoints(activeSamplePoints, new Color(0.15f, 0.8f, 1f, 0.9f));
    }

    void DrawGizmoPoints(SamplePoint[] points, Color color)
    {
        if (points == null || points.Length == 0)
        {
            return;
        }

        float totalWeight = GetTotalWeight(points);
        Gizmos.color = color;

        for (int i = 0; i < points.Length; i++)
        {
            float normalizedWeight = GetNormalizedWeight(points[i].weight, totalWeight, points.Length);
            float visualWeight = Mathf.Clamp(normalizedWeight * Mathf.Max(points.Length, 1), 0.5f, 2.5f);
            float gizmoSize = 0.035f + (0.015f * visualWeight);
            Gizmos.DrawSphere(transform.TransformPoint(points[i].localPosition), gizmoSize);
        }
    }

    public void RecalculateManualPointWeights()
    {
        if (manualSamplePoints == null || manualSamplePoints.Length == 0)
        {
            return;
        }

        if (!TryGetCombinedLocalBounds(out Bounds worldBounds))
        {
            return;
        }

        // Convert bounds to local space so comparison matches stored local positions
        Vector3 localMin = transform.InverseTransformPoint(worldBounds.min);
        Vector3 localMax = transform.InverseTransformPoint(worldBounds.max);

        for (int i = 0; i < manualSamplePoints.Length; i++)
        {
            float weight = calculateWeight(localMin.y, manualSamplePoints[i].localPosition.y);
            weight = Mathf.Max(0.2f, weight);
            manualSamplePoints[i] = new SamplePoint(manualSamplePoints[i].localPosition, Mathf.Max(0.2f, weight));
        }
    }

    public float calculateWeight(float localMin_y, float sample_y_position)
    {
        float hull_level = Mathf.InverseLerp(localMin_y, hull_height, sample_y_position);
        return 1f - Mathf.Abs(hull_level - 0.3f) * 2f;
    }
}
