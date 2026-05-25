using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

[RequireComponent(typeof(Rigidbody))]
public class WaterBuoyancy : MonoBehaviour
{
    const float MinDensity = 0.0001f;
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

    [SerializeField]
    // Auto-generated points usually behave better than hand-placed ones for boxy objects.
    bool autoGenerateSamplePoints = true;

    [SerializeField]
    SamplePoint[] manualSamplePoints = new SamplePoint[0];

    [SerializeField, HideInInspector, FormerlySerializedAs("samplePoints")]
    Vector3[] legacySamplePoints = new Vector3[0];

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
    Collider[] cachedColliders = new Collider[0];
    SamplePoint[] autoSamplePoints = new SamplePoint[0];

    // Called by Unity before the first physics update if the component starts enabled.
    // Caches required components and prepares the current buoyancy sample layout.
    void Awake()
    {
        EnsureSetup();
        UpgradeLegacyManualSamplePoints();
        UpgradeLegacyInsetSettings();
        SanitizeSampleSettings();
        RefreshAutoSamplePoints();
    }

    // Called by Unity when the component becomes active.
    // Refreshes references and sample points in case the object changed while disabled.
    void OnEnable()
    {
        EnsureSetup();
        UpgradeLegacyManualSamplePoints();
        UpgradeLegacyInsetSettings();
        SanitizeSampleSettings();
        RefreshAutoSamplePoints();
    }

    // Called by Unity in the editor when serialized fields change.
    // Rebuilds the sample layout immediately so gizmos and buoyancy settings stay accurate.
    void OnValidate()
    {
        EnsureSetup();
        UpgradeLegacyManualSamplePoints();
        UpgradeLegacyInsetSettings();
        SanitizeSampleSettings();
        RefreshAutoSamplePoints();
    }

    // Called by Unity when the component is first added or reset from the Inspector.
    // Starts with neutral object density where 1 means the object matches the water density.
    void Reset()
    {
        EnsureSetup();
        objectDensity = 1f;
        sampleMode = SampleMode.Raycast;
        verticalEdgeInset = DefaultEdgeInset;
        horizontalEdgeInset = DefaultEdgeInset;
        xEdgeOffset = 0f;
        zEdgeOffset = 0f;

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

        SamplePoint[] activeSamplePoints = GetActiveSamplePoints();
        if (activeSamplePoints == null || activeSamplePoints.Length == 0)
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

            float submergence = Mathf.Clamp01(submergenceDepth / Mathf.Max(maxSubmergence, 0.001f));
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

    // Caches the rigidbody and collider set used by the buoyancy calculations.
    // Called by Unity lifecycle methods before generating sample points or applying forces.
    void EnsureSetup()
    {
        body = GetComponent<Rigidbody>();
        cachedColliders = GetComponentsInChildren<Collider>();
    }

    // Converts legacy position-only manual samples into weighted samples.
    // Called from Unity setup hooks so existing scenes keep their authored points.
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

        legacySamplePoints = new Vector3[0];
    }

    // Copies the old single inset setting into the split vertical/horizontal inset values.
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

    // Makes sure manual sample weights and scalar settings stay inside valid ranges.
    // Called during editor validation and startup before sampling or force application.
    void SanitizeSampleSettings()
    {
        objectDensity = Mathf.Max(MinDensity, objectDensity);

        if (manualSamplePoints == null)
        {
            manualSamplePoints = new SamplePoint[0];
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

    // Returns either the user-authored weighted points or the current auto-generated layout.
    // Called from the physics loop and gizmo drawing so both systems use the same points.
    SamplePoint[] GetActiveSamplePoints()
    {
        if (!autoGenerateSamplePoints)
        {
            return manualSamplePoints ?? new SamplePoint[0];
        }

        if (autoSamplePoints == null || autoSamplePoints.Length == 0)
        {
            RefreshAutoSamplePoints();
        }

        return autoSamplePoints;
    }

    // Water density is fixed at 1, so object density alone determines the displaced water mass.
    // Rigidbody mass still controls the object's weight through Unity's normal gravity.
    float GetFullSubmersionBuoyancyStrength()
    {
        return Mathf.Max(0f, body.mass) / Mathf.Max(MinDensity, objectDensity);
    }

    // Sums all positive point weights so manual samples can split buoyancy proportionally.
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

    // Converts an individual sample weight into its normalized share of the total buoyancy.
    float GetNormalizedWeight(float pointWeight, float totalWeight, int sampleCount)
    {
        if (sampleCount <= 0)
        {
            return 0f;
        }

        if (totalWeight <= 0.0001f)
        {
            return 1f / sampleCount;
        }

        return Mathf.Max(0f, pointWeight) / totalWeight;
    }

    // Rebuilds the auto-generated sample layout from the object's collider bounds.
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

        int safeHorizontalCount = Mathf.Max(2, horizontalSampleCount);
        int safeVerticalCount = Mathf.Max(2, verticalSampleCount);

        autoSamplePoints = sampleMode == SampleMode.Raycast
            ? GenerateRaycastSamplePoints(worldBounds, safeHorizontalCount, horizontalEdgeInset, xEdgeOffset, zEdgeOffset)
            : GenerateLatticePoints(worldBounds, safeHorizontalCount, safeVerticalCount, verticalEdgeInset, horizontalEdgeInset);

        if (autoSamplePoints == null || autoSamplePoints.Length == 0)
        {
            autoSamplePoints = CreateFallbackSamplePoints();
        }
    }

    // Projects sampling columns upward through this object's colliders and keeps only hits on the object itself.
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

    // Fills the collider bounds volume with a 3D point lattice for boxier or fully volumetric sampling.
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

    // Finds the nearest upward raycast hit that belongs to this object rather than another collider in the scene.
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

    // Returns true when a collider belongs to the cached buoyancy body hierarchy.
    bool IsOwnedCollider(Collider collider)
    {
        if (collider == null || cachedColliders == null)
        {
            return false;
        }

        for (int i = 0; i < cachedColliders.Length; i++)
        {
            if (cachedColliders[i] == collider)
            {
                return true;
            }
        }

        return false;
    }

    // Combines all non-trigger collider bounds into one world-space box for automatic sample generation.
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
    SamplePoint[] CreateFallbackSamplePoints()
    {
        // Fallback points approximate a small box volume when there are no colliders to sample from.
        return new[]
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
    }

    // Called by Unity when the object is selected in the Scene view.
    // Draws the effective sample points so buoyancy layout issues are easy to debug visually.
    void OnDrawGizmosSelected()
    {
        SamplePoint[] activeSamplePoints = Application.isPlaying
            ? GetActiveSamplePoints()
            : (autoGenerateSamplePoints ? autoSamplePoints : manualSamplePoints);

        if (activeSamplePoints == null)
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
