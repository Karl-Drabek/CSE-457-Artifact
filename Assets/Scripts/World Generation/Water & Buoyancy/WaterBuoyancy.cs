using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Serialization;

[RequireComponent(typeof(Rigidbody))]
/// <summary>
/// Applies buoyancy by sampling the generated water surface at a set of hull
/// points and pushing forces back into the rigidbody.
/// </summary>
public class WaterBuoyancy : MonoBehaviour
{
    const float MinDensity = 0.0001f;
    const float MinSubmergenceRange = 0.001f;
    const float MinWeightSum = 0.0001f;
    const float DefaultEdgeInset = 0.08f;
    const float MinWaterFlowSpeed = 0.0001f;
    const float MinRelativeWaterSpeed = 0.0001f;
    const float MinAngularSpeed = 0.0001f;
    const string BuoyancyFixedUpdateSampleName = "WaterBuoyancy.FixedUpdate";
    const string RefreshAutoSamplePointsSampleName = "WaterBuoyancy.RefreshAutoSamplePoints";

    struct ProfileScope : IDisposable
    {
        readonly bool active;

        public ProfileScope(string sampleName)
        {
            active = !string.IsNullOrEmpty(sampleName);
            if (active)
            {
                Profiler.BeginSample(sampleName);
            }
        }

        public void Dispose()
        {
            if (active)
            {
                Profiler.EndSample();
            }
        }
    }

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
        RenderedMeshSurface,
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

    [Range(4, 256)]
    public int renderedMeshSampleCount = 32;

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
    Mesh bakedSkinnedSamplingMesh;

    void Awake()
    {
        RefreshState();
    }

    void OnEnable()
    {
        RefreshState();
    }

    void OnDisable()
    {
        if (bakedSkinnedSamplingMesh != null)
        {
            if (Application.isPlaying)
            {
                Destroy(bakedSkinnedSamplingMesh);
            }
            else
            {
                DestroyImmediate(bakedSkinnedSamplingMesh);
            }

            bakedSkinnedSamplingMesh = null;
        }
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
        using (new ProfileScope(BuoyancyFixedUpdateSampleName))
        {
        if (body == null)
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
            if (!UrpLowPolyWater.TryResolveSurfaceAtWorldPosition(worldPoint, out UrpLowPolyWater water))
            {
                continue;
            }

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
        renderedMeshSampleCount = Mathf.Max(4, renderedMeshSampleCount);
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

    // Rebuilds the currently selected auto-generated sample layout from either colliders
    // or rendered meshes, depending on the active sampling mode.
    void RefreshAutoSamplePoints()
    {
        using (new ProfileScope(RefreshAutoSamplePointsSampleName))
        {
        EnsureSetup();

        if (sampleMode == SampleMode.RenderedMeshSurface)
        {
            autoSamplePoints = GenerateRenderedMeshSurfaceSamplePoints(renderedMeshSampleCount);
            if (autoSamplePoints.Length == 0)
            {
                autoSamplePoints = FallbackSamplePoints;
            }

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

        switch (sampleMode)
        {
            case SampleMode.Raycast:
                autoSamplePoints = GenerateRaycastSamplePoints(safeHorizontalCount, horizontalEdgeInset, xEdgeOffset, zEdgeOffset);
                break;

            default:
                autoSamplePoints = GenerateLatticePoints(worldBounds, safeHorizontalCount, safeVerticalCount, verticalEdgeInset, horizontalEdgeInset);
                break;
        }

        if (autoSamplePoints.Length == 0)
        {
            autoSamplePoints = FallbackSamplePoints;
        }
        }
    }

    // Builds a surface-following sample grid by raycasting upward through the rendered
    // boat mesh when possible, with collider fallback only if no usable mesh exists.
    SamplePoint[] GenerateRaycastSamplePoints(int safeHorizontalCount, float inset, float xOffset, float zOffset)
    {
        if (!TryGetRaycastSamplingLocalBounds(out Bounds localBounds))
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

                if (!TryGetRaycastSampleHit(new Ray(worldRayOrigin, worldRayDirection), rayLength, out Vector3 hitWorldPoint))
                {
                    continue;
                }

                Vector3 localHitPoint = transform.InverseTransformPoint(hitWorldPoint);
                float weight = CalculateWeight(localMin, localMax, localHitPoint.y);
                weight = Mathf.Max(0.2f, weight);
                generatedPoints.Add(new SamplePoint(localHitPoint, weight));
            }
        }

        return generatedPoints.ToArray();
    }

    // Samples points directly from the rendered mesh vertices so large visible hulls can
    // use their actual rendered shape instead of a collider-derived grid.
    SamplePoint[] GenerateRenderedMeshSurfaceSamplePoints(int sampleCount)
    {
        if (!TryCollectRenderedMeshVertices(out List<Vector3> localVertices, out Bounds localBounds))
        {
            return Array.Empty<SamplePoint>();
        }

        int clampedSampleCount = Mathf.Min(sampleCount, localVertices.Count);
        if (clampedSampleCount <= 0)
        {
            return Array.Empty<SamplePoint>();
        }

        List<SamplePoint> generatedPoints = new List<SamplePoint>(clampedSampleCount);
        float step = localVertices.Count / (float)clampedSampleCount;
        for (int i = 0; i < clampedSampleCount; i++)
        {
            int vertexIndex = Mathf.Min(Mathf.FloorToInt(i * step), localVertices.Count - 1);
            Vector3 localPoint = localVertices[vertexIndex];
            float weight = Mathf.Max(0.2f, CalculateWeight(localBounds.min, localBounds.max, localPoint.y));
            generatedPoints.Add(new SamplePoint(localPoint, weight));
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
    bool TryGetOwnRaycastHit(Vector3 rayOrigin, Vector3 rayDirection, float rayLength, out RaycastHit closestHit)
    {
        RaycastHit[] hits = Physics.RaycastAll(rayOrigin, rayDirection, rayLength, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
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

    bool TryGetRaycastSampleHit(Ray worldRay, float rayLength, out Vector3 hitWorldPoint)
    {
        if (TryGetNearestRenderedMeshHit(worldRay, out hitWorldPoint))
        {
            return true;
        }

        if (TryGetOwnRaycastHit(worldRay.origin, worldRay.direction, rayLength, out RaycastHit colliderHit))
        {
            hitWorldPoint = colliderHit.point;
            return true;
        }

        hitWorldPoint = default;
        return false;
    }

    bool TryGetRaycastSamplingLocalBounds(out Bounds localBounds)
    {
        if (TryGetCombinedRenderedMeshLocalBounds(out localBounds))
        {
            return true;
        }

        return TryGetCombinedLocalBounds(out localBounds);
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

            if (!TryEncapsulateWorldBoundsInLocalSpace(collider.bounds, ref combined, ref hasBounds))
            {
                continue;
            }
        }

        return hasBounds;
    }

    public void SetManualPoints(Vector3[] points, float local_min_y)
    {
        autoGenerateSamplePoints = false;
        sampleMode = SampleMode.Manual;
        Vector3 localMin = new Vector3(0f, local_min_y, 0f);
        SamplePoint[] samples = new SamplePoint[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            Vector3 point = points[i];
            samples[i] = new SamplePoint(point, Mathf.Max(0.1f, CalculateWeight(localMin, Vector3.zero, point.y)));
        }
        manualSamplePoints = samples;
        ClearAutoGeneratedPoints();
        RefreshState();
    }

    void ClearAutoGeneratedPoints()
    {
        autoSamplePoints = Array.Empty<SamplePoint>();
    }


    bool TryGetCombinedRenderedMeshLocalBounds(out Bounds combinedLocalBounds)
    {
        combinedLocalBounds = default;
        bool hasBounds = false;

        MeshFilter[] meshFilters = GetComponentsInChildren<MeshFilter>();
        for (int i = 0; i < meshFilters.Length; i++)
        {
            MeshFilter meshFilter = meshFilters[i];
            if (meshFilter == null || meshFilter.sharedMesh == null || !meshFilter.sharedMesh.isReadable)
            {
                continue;
            }

            AppendMeshVertices(meshFilter.sharedMesh, meshFilter.transform.localToWorldMatrix, null, ref combinedLocalBounds, ref hasBounds);
        }

        SkinnedMeshRenderer[] skinnedMeshes = GetComponentsInChildren<SkinnedMeshRenderer>();
        for (int i = 0; i < skinnedMeshes.Length; i++)
        {
            SkinnedMeshRenderer skinnedMesh = skinnedMeshes[i];
            if (skinnedMesh == null || skinnedMesh.sharedMesh == null)
            {
                continue;
            }

            Mesh bakedMesh = GetOrCreateBakedSkinnedSamplingMesh();
            skinnedMesh.BakeMesh(bakedMesh);
            AppendMeshVertices(bakedMesh, skinnedMesh.transform.localToWorldMatrix, null, ref combinedLocalBounds, ref hasBounds);
        }

        return hasBounds;
    }

    bool TryCollectRenderedMeshVertices(out List<Vector3> localVertices, out Bounds combinedLocalBounds)
    {
        localVertices = new List<Vector3>();
        combinedLocalBounds = default;
        bool hasBounds = false;

        MeshFilter[] meshFilters = GetComponentsInChildren<MeshFilter>();
        for (int i = 0; i < meshFilters.Length; i++)
        {
            MeshFilter meshFilter = meshFilters[i];
            if (meshFilter == null || meshFilter.sharedMesh == null || !meshFilter.sharedMesh.isReadable)
            {
                continue;
            }

            AppendMeshVertices(meshFilter.sharedMesh, meshFilter.transform.localToWorldMatrix, localVertices, ref combinedLocalBounds, ref hasBounds);
        }

        SkinnedMeshRenderer[] skinnedMeshes = GetComponentsInChildren<SkinnedMeshRenderer>();
        for (int i = 0; i < skinnedMeshes.Length; i++)
        {
            SkinnedMeshRenderer skinnedMesh = skinnedMeshes[i];
            if (skinnedMesh == null || skinnedMesh.sharedMesh == null)
            {
                continue;
            }

            Mesh bakedMesh = GetOrCreateBakedSkinnedSamplingMesh();
            skinnedMesh.BakeMesh(bakedMesh);
            AppendMeshVertices(bakedMesh, skinnedMesh.transform.localToWorldMatrix, localVertices, ref combinedLocalBounds, ref hasBounds);
        }

        return localVertices.Count > 0 && hasBounds;
    }

    void AppendMeshVertices(
        Mesh mesh,
        Matrix4x4 sourceLocalToWorld,
        List<Vector3> localVertices,
        ref Bounds combinedLocalBounds,
        ref bool hasBounds)
    {
        if (mesh == null)
        {
            return;
        }

        Vector3[] vertices = mesh.vertices;
        if (vertices == null || vertices.Length == 0)
        {
            return;
        }

        Matrix4x4 toRootLocal = transform.worldToLocalMatrix * sourceLocalToWorld;
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 localVertex = toRootLocal.MultiplyPoint3x4(vertices[i]);
            localVertices?.Add(localVertex);
            EncapsulateLocalPoint(localVertex, ref combinedLocalBounds, ref hasBounds);
        }
    }

    bool TryGetNearestRenderedMeshHit(Ray worldRay, out Vector3 hitWorldPoint)
    {
        hitWorldPoint = default;
        bool foundHit = false;
        float closestDistance = float.PositiveInfinity;

        MeshFilter[] meshFilters = GetComponentsInChildren<MeshFilter>();
        for (int i = 0; i < meshFilters.Length; i++)
        {
            MeshFilter meshFilter = meshFilters[i];
            if (meshFilter == null || meshFilter.sharedMesh == null || !meshFilter.sharedMesh.isReadable)
            {
                continue;
            }

            if (!TryIntersectMesh(worldRay, meshFilter.sharedMesh, meshFilter.transform.localToWorldMatrix, out Vector3 candidateHitPoint, out float candidateDistance)
                || candidateDistance >= closestDistance)
            {
                continue;
            }

            closestDistance = candidateDistance;
            hitWorldPoint = candidateHitPoint;
            foundHit = true;
        }

        SkinnedMeshRenderer[] skinnedMeshes = GetComponentsInChildren<SkinnedMeshRenderer>();
        for (int i = 0; i < skinnedMeshes.Length; i++)
        {
            SkinnedMeshRenderer skinnedMesh = skinnedMeshes[i];
            if (skinnedMesh == null || skinnedMesh.sharedMesh == null)
            {
                continue;
            }

            Mesh bakedMesh = GetOrCreateBakedSkinnedSamplingMesh();
            skinnedMesh.BakeMesh(bakedMesh);
            if (!TryIntersectMesh(worldRay, bakedMesh, skinnedMesh.transform.localToWorldMatrix, out Vector3 candidateHitPoint, out float candidateDistance)
                || candidateDistance >= closestDistance)
            {
                continue;
            }

            closestDistance = candidateDistance;
            hitWorldPoint = candidateHitPoint;
            foundHit = true;
        }

        return foundHit;
    }

    bool TryIntersectMesh(Ray worldRay, Mesh mesh, Matrix4x4 localToWorld, out Vector3 hitWorldPoint, out float hitWorldDistance)
    {
        hitWorldPoint = default;
        hitWorldDistance = float.PositiveInfinity;

        if (mesh == null || !mesh.isReadable)
        {
            return false;
        }

        Vector3[] vertices = mesh.vertices;
        int[] triangles = mesh.triangles;
        if (vertices == null || vertices.Length == 0 || triangles == null || triangles.Length < 3)
        {
            return false;
        }

        Matrix4x4 worldToLocal = localToWorld.inverse;
        Vector3 localRayOrigin = worldToLocal.MultiplyPoint3x4(worldRay.origin);
        Vector3 localRayDirection = worldToLocal.MultiplyVector(worldRay.direction).normalized;
        Ray localRay = new Ray(localRayOrigin, localRayDirection);

        if (!mesh.bounds.IntersectRay(localRay))
        {
            return false;
        }

        bool foundHit = false;
        for (int triangleIndex = 0; triangleIndex < triangles.Length; triangleIndex += 3)
        {
            Vector3 a = vertices[triangles[triangleIndex]];
            Vector3 b = vertices[triangles[triangleIndex + 1]];
            Vector3 c = vertices[triangles[triangleIndex + 2]];

            if (!TryIntersectTriangle(localRay, a, b, c, out float localDistance))
            {
                continue;
            }

            Vector3 localPoint = localRay.origin + (localRay.direction * localDistance);
            Vector3 worldPoint = localToWorld.MultiplyPoint3x4(localPoint);
            float worldDistance = Vector3.Distance(worldRay.origin, worldPoint);
            if (worldDistance >= hitWorldDistance)
            {
                continue;
            }

            hitWorldDistance = worldDistance;
            hitWorldPoint = worldPoint;
            foundHit = true;
        }

        return foundHit;
    }

    static bool TryIntersectTriangle(Ray ray, Vector3 a, Vector3 b, Vector3 c, out float distance)
    {
        distance = 0f;

        Vector3 edgeAB = b - a;
        Vector3 edgeAC = c - a;
        Vector3 perpendicular = Vector3.Cross(ray.direction, edgeAC);
        float determinant = Vector3.Dot(edgeAB, perpendicular);
        if (Mathf.Abs(determinant) < MinWeightSum)
        {
            return false;
        }

        float inverseDeterminant = 1f / determinant;
        Vector3 triangleToRay = ray.origin - a;
        float u = Vector3.Dot(triangleToRay, perpendicular) * inverseDeterminant;
        if (u < 0f || u > 1f)
        {
            return false;
        }

        Vector3 q = Vector3.Cross(triangleToRay, edgeAB);
        float v = Vector3.Dot(ray.direction, q) * inverseDeterminant;
        if (v < 0f || (u + v) > 1f)
        {
            return false;
        }

        float hitDistance = Vector3.Dot(edgeAC, q) * inverseDeterminant;
        if (hitDistance < 0f)
        {
            return false;
        }

        distance = hitDistance;
        return true;
    }

    Mesh GetOrCreateBakedSkinnedSamplingMesh()
    {
        if (bakedSkinnedSamplingMesh == null)
        {
            bakedSkinnedSamplingMesh = new Mesh
            {
                name = "WaterBuoyancy_BakedSkinnedSamplingMesh"
            };
        }

        return bakedSkinnedSamplingMesh;
    }

    bool TryEncapsulateWorldBoundsInLocalSpace(Bounds worldBounds, ref Bounds combinedLocalBounds, ref bool hasBounds)
    {
        Vector3 min = worldBounds.min;
        Vector3 max = worldBounds.max;

        Vector3[] corners =
        {
            new Vector3(min.x, min.y, min.z),
            new Vector3(max.x, min.y, min.z),
            new Vector3(min.x, max.y, min.z),
            new Vector3(max.x, max.y, min.z),
            new Vector3(min.x, min.y, max.z),
            new Vector3(max.x, min.y, max.z),
            new Vector3(min.x, max.y, max.z),
            new Vector3(max.x, max.y, max.z)
        };

        for (int i = 0; i < corners.Length; i++)
        {
            Vector3 localCorner = transform.InverseTransformPoint(corners[i]);
            EncapsulateLocalPoint(localCorner, ref combinedLocalBounds, ref hasBounds);
        }

        return hasBounds;
    }

    void EncapsulateLocalPoint(Vector3 localPoint, ref Bounds combinedLocalBounds, ref bool hasBounds)
    {
        if (!hasBounds)
        {
            combinedLocalBounds = new Bounds(localPoint, Vector3.zero);
            hasBounds = true;
            return;
        }

        combinedLocalBounds.Encapsulate(localPoint);
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

        if (!TryGetCombinedLocalBounds(out Bounds localBounds))
        {
            return;
        }

        Vector3 localMin = localBounds.min;
        Vector3 localMax = localBounds.max;

        for (int i = 0; i < manualSamplePoints.Length; i++)
        {
            float weight = CalculateWeight(localMin, localMax, manualSamplePoints[i].localPosition.y);
            weight = Mathf.Max(0.2f, weight);
            manualSamplePoints[i] = new SamplePoint(manualSamplePoints[i].localPosition, Mathf.Max(0.2f, weight));
        }
    }

    public float CalculateWeight(Vector3 localMin, Vector3 localMax, float sampleYPosition)
    {
        float hullLevel = Mathf.InverseLerp(localMin.y, hull_height, sampleYPosition);
        return 1f - Mathf.Abs(hullLevel - 0.3f) * 2f;
    }

}
