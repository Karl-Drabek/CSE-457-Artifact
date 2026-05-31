using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

[RequireComponent(typeof(Rigidbody))]
public class WindForce : MonoBehaviour
{
    const float MinWeightSum = 0.0001f;
    const float MinWindSpeed = 0.0001f;
    const float MinTriangleArea = 0.000001f;
    const float MinMeshExtent = 0.0001f;
    const float DefaultEdgeInset = 0.08f;
    const float DefaultRayBoundsPadding = 0.25f;

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

    public enum WindApplicationMode
    {
        PointSamples,
        SurfaceTriangles,
        SurfaceRaycast
    }

    public enum SampleMode
    {
        Lattice,
        Raycast
    }

    public enum MeshSourceMode
    {
        ObjectHierarchy,
        CustomMeshRoot
    }

    struct MeshHit
    {
        public Vector3 point;
        public Vector3 normal;
        public float distance;
    }

    [Min(0f)]
    public float forceMultiplier = 1f;

    // When disabled, any computed wind force is projected onto the horizontal plane to remove lift/downforce.
    public bool allowVerticalForce;

    // When enabled, the body motion contributes to apparent wind, which can add drag or lift.
    public bool useApparentWind;

    public WindApplicationMode applicationMode = WindApplicationMode.SurfaceTriangles;

    public MeshSourceMode meshSource = MeshSourceMode.ObjectHierarchy;

    [FormerlySerializedAs("renderRoot")]
    public Transform renderRoot;

    public bool doubleSidedSurfaces;

    [SerializeField]
    bool autoGenerateSamplePoints;

    [SerializeField, FormerlySerializedAs("samplePoints")]
    SamplePoint[] manualSamplePoints = Array.Empty<SamplePoint>();

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
    public float xEdgeOffset;

    [Range(-1f, 1f)]
    public float zEdgeOffset;

    [Range(1, 16)]
    public int triangleSampleStride = 1;

    [Range(1, 24)]
    public int surfaceRayColumns = 6;

    [Range(1, 24)]
    public int surfaceRayRows = 6;

    [Min(0f)]
    public float surfaceRayBoundsPadding = DefaultRayBoundsPadding;

    [SerializeField, HideInInspector, FormerlySerializedAs("sampleEdgeInset")]
    float legacySampleEdgeInset = DefaultEdgeInset;

    Rigidbody body;
    Transform cachedMeshRoot;
    MeshFilter[] cachedMeshFilters = Array.Empty<MeshFilter>();
    SkinnedMeshRenderer[] cachedSkinnedMeshes = Array.Empty<SkinnedMeshRenderer>();
    Mesh[] bakedSkinnedMeshes = Array.Empty<Mesh>();
    SamplePoint[] autoSamplePoints = Array.Empty<SamplePoint>();
    bool autoSamplePointsGenerated;

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

    void OnDisable()
    {
        ReleaseBakedMeshes();
    }

    void Reset()
    {
        forceMultiplier = 1f;
        allowVerticalForce = false;
        useApparentWind = false;
        applicationMode = WindApplicationMode.SurfaceTriangles;
        meshSource = MeshSourceMode.ObjectHierarchy;
        doubleSidedSurfaces = false;
        sampleMode = SampleMode.Raycast;
        verticalEdgeInset = DefaultEdgeInset;
        horizontalEdgeInset = DefaultEdgeInset;
        surfaceRayColumns = 6;
        surfaceRayRows = 6;
        surfaceRayBoundsPadding = DefaultRayBoundsPadding;
        triangleSampleStride = 1;
        RefreshState();
    }

    void FixedUpdate()
    {
        EnsureSetup();
        if (body == null)
        {
            return;
        }

        if (!TryResolveWindField(out WindField windField))
        {
            return;
        }

        switch (applicationMode)
        {
            case WindApplicationMode.SurfaceTriangles:
                // Triangle mode samples actual surface area and normals directly from the chosen mesh source.
                ApplyTriangleWind(windField);
                break;
            case WindApplicationMode.SurfaceRaycast:
                // Raycast mode only hits the upwind-exposed surface, which makes it useful to compare against triangles.
                ApplySurfaceRaycastWind(windField);
                break;
            default:
                // Point mode is the cheapest approximation and matches the buoyancy-style workflow.
                ApplyPointSampleWind(windField);
                break;
        }
    }

    public Transform GetSamplingMeshRoot()
    {
        if (meshSource == MeshSourceMode.CustomMeshRoot && renderRoot != null)
        {
            return renderRoot;
        }

        return transform;
    }

    void RefreshState()
    {
        EnsureSetup();
        UpgradeLegacyInsetSettings();
        SanitizeSettings();
        RefreshMeshSourceCache();
        RefreshAutoSamplePoints();
    }

    void EnsureSetup()
    {
        body = GetComponent<Rigidbody>();
        RefreshMeshSourceCacheIfNeeded();
    }

    void RefreshMeshSourceCacheIfNeeded()
    {
        Transform meshRoot = GetSamplingMeshRoot();
        if (meshRoot == cachedMeshRoot)
        {
            return;
        }

        RefreshMeshSourceCache();
    }

    void RefreshMeshSourceCache()
    {
        cachedMeshRoot = GetSamplingMeshRoot();
        cachedMeshFilters = cachedMeshRoot != null
            ? cachedMeshRoot.GetComponentsInChildren<MeshFilter>()
            : Array.Empty<MeshFilter>();
        cachedSkinnedMeshes = cachedMeshRoot != null
            ? cachedMeshRoot.GetComponentsInChildren<SkinnedMeshRenderer>()
            : Array.Empty<SkinnedMeshRenderer>();

        EnsureBakedMeshes();
        autoSamplePointsGenerated = false;
    }

    void EnsureBakedMeshes()
    {
        if (cachedSkinnedMeshes == null)
        {
            cachedSkinnedMeshes = Array.Empty<SkinnedMeshRenderer>();
        }

        if (bakedSkinnedMeshes.Length == cachedSkinnedMeshes.Length)
        {
            for (int i = 0; i < bakedSkinnedMeshes.Length; i++)
            {
                if (bakedSkinnedMeshes[i] == null)
                {
                    bakedSkinnedMeshes[i] = CreateBakedMesh(i);
                }
            }

            return;
        }

        ReleaseBakedMeshes();
        bakedSkinnedMeshes = new Mesh[cachedSkinnedMeshes.Length];
        for (int i = 0; i < bakedSkinnedMeshes.Length; i++)
        {
            bakedSkinnedMeshes[i] = CreateBakedMesh(i);
        }
    }

    Mesh CreateBakedMesh(int index)
    {
        return new Mesh
        {
            name = "WindForce_BakedSkinnedMesh_" + index,
            hideFlags = HideFlags.HideAndDontSave
        };
    }

    void ReleaseBakedMeshes()
    {
        for (int i = 0; i < bakedSkinnedMeshes.Length; i++)
        {
            if (bakedSkinnedMeshes[i] == null)
            {
                continue;
            }

            if (Application.isPlaying)
            {
                Destroy(bakedSkinnedMeshes[i]);
            }
            else
            {
                DestroyImmediate(bakedSkinnedMeshes[i]);
            }
        }

        bakedSkinnedMeshes = Array.Empty<Mesh>();
    }

    void BakeSkinnedMeshes()
    {
        for (int i = 0; i < cachedSkinnedMeshes.Length; i++)
        {
            SkinnedMeshRenderer skinnedMesh = cachedSkinnedMeshes[i];
            Mesh bakedMesh = i < bakedSkinnedMeshes.Length ? bakedSkinnedMeshes[i] : null;
            if (skinnedMesh == null || bakedMesh == null || skinnedMesh.sharedMesh == null)
            {
                continue;
            }

            skinnedMesh.BakeMesh(bakedMesh);
        }
    }

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

    void SanitizeSettings()
    {
        // Point-sample mode is retained only for older serialized data and upgrades into triangles automatically.
        if (applicationMode == WindApplicationMode.PointSamples)
        {
            applicationMode = WindApplicationMode.SurfaceTriangles;
        }

        if (manualSamplePoints == null)
        {
            manualSamplePoints = Array.Empty<SamplePoint>();
        }
        else
        {
            for (int i = 0; i < manualSamplePoints.Length; i++)
            {
                SamplePoint point = manualSamplePoints[i];
                point.weight = Mathf.Max(0f, point.weight);
                manualSamplePoints[i] = point;
            }
        }

        forceMultiplier = Mathf.Max(0f, forceMultiplier);
        verticalEdgeInset = Mathf.Clamp(verticalEdgeInset, 0f, 0.45f);
        horizontalEdgeInset = Mathf.Clamp(horizontalEdgeInset, 0f, 0.45f);
        xEdgeOffset = Mathf.Clamp(xEdgeOffset, -1f, 1f);
        zEdgeOffset = Mathf.Clamp(zEdgeOffset, -1f, 1f);
        horizontalSampleCount = Mathf.Max(2, horizontalSampleCount);
        verticalSampleCount = Mathf.Max(2, verticalSampleCount);
        triangleSampleStride = Mathf.Max(1, triangleSampleStride);
        surfaceRayColumns = Mathf.Max(1, surfaceRayColumns);
        surfaceRayRows = Mathf.Max(1, surfaceRayRows);
        surfaceRayBoundsPadding = Mathf.Max(0f, surfaceRayBoundsPadding);
    }

    bool TryResolveWindField(out WindField windField)
    {
        windField = WindField.ActiveField;
        if (windField != null)
        {
            return true;
        }

        windField = FindAnyObjectByType<WindField>();
        return windField != null;
    }

    void ApplyPointSampleWind(WindField windField)
    {
        SamplePoint[] activeSamplePoints = GetActiveSamplePoints();
        if (activeSamplePoints == null || activeSamplePoints.Length == 0)
        {
            return;
        }

        float totalWeight = GetTotalWeight(activeSamplePoints);
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
            Vector3 apparentWind = GetSampledWindVelocity(windField, worldPoint, timeSeconds);
            float windSpeed = apparentWind.magnitude;
            if (windSpeed <= MinWindSpeed)
            {
                continue;
            }

            float windStrength = windField.GetWindStrengthMultiplierAtWorldPosition(worldPoint, timeSeconds);
            if (windStrength <= 0f)
            {
                continue;
            }

            // This keeps point mode simple: each point gets a weighted share of drag-like force in the wind direction.
            Vector3 force = ConstrainAppliedForce(apparentWind * (windSpeed * forceMultiplier * normalizedWeight * windStrength));
            if (force.sqrMagnitude <= 0f)
            {
                continue;
            }

            body.AddForceAtPosition(force, worldPoint, ForceMode.Force);
        }
    }

    void ApplyTriangleWind(WindField windField)
    {
        if (!HasSamplingMesh())
        {
            return;
        }

        BakeSkinnedMeshes();
        float timeSeconds = Time.fixedTime;
        int safeStride = Mathf.Max(1, triangleSampleStride);

        for (int i = 0; i < cachedMeshFilters.Length; i++)
        {
            MeshFilter meshFilter = cachedMeshFilters[i];
            if (meshFilter == null || !CanReadMesh(meshFilter.sharedMesh))
            {
                continue;
            }

            ApplyTriangleWindForMesh(meshFilter.sharedMesh, meshFilter.transform.localToWorldMatrix, windField, timeSeconds, safeStride);
        }

        for (int i = 0; i < cachedSkinnedMeshes.Length; i++)
        {
            SkinnedMeshRenderer skinnedMesh = cachedSkinnedMeshes[i];
            Mesh bakedMesh = i < bakedSkinnedMeshes.Length ? bakedSkinnedMeshes[i] : null;
            if (skinnedMesh == null || bakedMesh == null || skinnedMesh.sharedMesh == null)
            {
                continue;
            }

            ApplyTriangleWindForMesh(bakedMesh, skinnedMesh.transform.localToWorldMatrix, windField, timeSeconds, safeStride);
        }
    }

    void ApplyTriangleWindForMesh(
        Mesh mesh,
        Matrix4x4 localToWorld,
        WindField windField,
        float timeSeconds,
        int stride)
    {
        Vector3[] vertices = mesh.vertices;
        int[] triangles = mesh.triangles;
        if (vertices == null || vertices.Length == 0 || triangles == null || triangles.Length < 3)
        {
            return;
        }

        int triangleStep = 3 * stride;
        for (int triangleIndex = 0; triangleIndex < triangles.Length; triangleIndex += triangleStep)
        {
            int aIndex = triangles[triangleIndex];
            int bIndex = triangles[triangleIndex + 1];
            int cIndex = triangles[triangleIndex + 2];

            Vector3 worldA = localToWorld.MultiplyPoint3x4(vertices[aIndex]);
            Vector3 worldB = localToWorld.MultiplyPoint3x4(vertices[bIndex]);
            Vector3 worldC = localToWorld.MultiplyPoint3x4(vertices[cIndex]);

            Vector3 areaVector = Vector3.Cross(worldB - worldA, worldC - worldA);
            float doubledArea = areaVector.magnitude;
            if (doubledArea <= MinTriangleArea)
            {
                continue;
            }

            float representedArea = (doubledArea * 0.5f) * stride;
            Vector3 normal = areaVector / doubledArea;
            Vector3 centroid = (worldA + worldB + worldC) / 3f;
            Vector3 averageWind = (
                GetSampledWindVelocity(windField, worldA, timeSeconds)
                + GetSampledWindVelocity(windField, worldB, timeSeconds)
                + GetSampledWindVelocity(windField, worldC, timeSeconds)) / 3f;
            float windStrength = (
                windField.GetWindStrengthMultiplierAtWorldPosition(worldA, timeSeconds)
                + windField.GetWindStrengthMultiplierAtWorldPosition(worldB, timeSeconds)
                + windField.GetWindStrengthMultiplierAtWorldPosition(worldC, timeSeconds)) / 3f;

            // Applying force at the triangle centroid lets the rigidbody pick up roll and yaw from sail pressure.
            ApplySurfaceForce(centroid, normal, averageWind, representedArea, windStrength);
        }
    }

    void ApplySurfaceRaycastWind(WindField windField)
    {
        if (!HasSamplingMesh() || !TryGetCombinedWorldBounds(out Bounds worldBounds))
        {
            return;
        }

        BakeSkinnedMeshes();

        float timeSeconds = Time.fixedTime;
        Vector3 boundsCenter = worldBounds.center;
        Vector3 centerWind = GetSampledWindVelocity(windField, boundsCenter, timeSeconds);
        float centerWindSpeed = centerWind.magnitude;
        if (centerWindSpeed <= MinWindSpeed)
        {
            return;
        }

        Vector3 rayDirection = centerWind / centerWindSpeed;
        BuildRaycastBasis(rayDirection, out Vector3 axisU, out Vector3 axisV);
        GetProjectedBounds(worldBounds, rayDirection, axisU, axisV, out float minDepth, out float maxDepth, out float minU, out float maxU, out float minV, out float maxV);

        // Each ray stands in for one cell of a wind-aligned sampling plane in front of the object.
        float padding = Mathf.Max(surfaceRayBoundsPadding, 0f);
        float startDepth = minDepth - padding;
        float maxDistance = (maxDepth - minDepth) + (padding * 2f);
        float uSize = Mathf.Max(maxU - minU, MinMeshExtent);
        float vSize = Mathf.Max(maxV - minV, MinMeshExtent);
        float representedArea = (uSize / surfaceRayColumns) * (vSize / surfaceRayRows);

        for (int row = 0; row < surfaceRayRows; row++)
        {
            float v = GetCenteredGridValue(minV, maxV, row, surfaceRayRows);

            for (int column = 0; column < surfaceRayColumns; column++)
            {
                float u = GetCenteredGridValue(minU, maxU, column, surfaceRayColumns);
                Vector3 rayOrigin = (rayDirection * startDepth) + (axisU * u) + (axisV * v);
                Ray ray = new Ray(rayOrigin, rayDirection);
                if (!TryGetOwnMeshRayHit(ray, maxDistance, out MeshHit hit))
                {
                    continue;
                }

                Vector3 apparentWind = GetSampledWindVelocity(windField, hit.point, timeSeconds);
                float windStrength = windField.GetWindStrengthMultiplierAtWorldPosition(hit.point, timeSeconds);
                // Applying force at the hit point lets exposed wind patches create torque instead of only translation.
                ApplySurfaceForce(hit.point, hit.normal, apparentWind, representedArea, windStrength);
            }
        }
    }

    void ApplySurfaceForce(Vector3 worldPoint, Vector3 surfaceNormal, Vector3 apparentWind, float representedArea, float windStrength)
    {
        if (representedArea <= 0f || windStrength <= 0f)
        {
            return;
        }

        float windSpeed = apparentWind.magnitude;
        if (windSpeed <= MinWindSpeed)
        {
            return;
        }

        Vector3 normalizedNormal = surfaceNormal.normalized;
        float normalSpeed = Vector3.Dot(normalizedNormal, apparentWind);
        if (doubleSidedSurfaces)
        {
            // Double-sided mode makes sail tests less sensitive to triangle winding during experimentation.
            if (Mathf.Abs(normalSpeed) <= MinWindSpeed)
            {
                return;
            }

            Vector3 forceDirection = normalSpeed < 0f ? -normalizedNormal : normalizedNormal;
            float forceMagnitude = normalSpeed * normalSpeed * representedArea * forceMultiplier * windStrength;
            Vector3 force = ConstrainAppliedForce(forceDirection * forceMagnitude);
            if (force.sqrMagnitude > 0f)
            {
                body.AddForceAtPosition(force, worldPoint, ForceMode.Force);
            }

            return;
        }

        float incomingSpeed = -normalSpeed;
        if (incomingSpeed <= MinWindSpeed)
        {
            return;
        }

        float oneSidedForce = incomingSpeed * incomingSpeed * representedArea * forceMultiplier * windStrength;
        Vector3 flattenedForce = ConstrainAppliedForce((-normalizedNormal) * oneSidedForce);
        if (flattenedForce.sqrMagnitude > 0f)
        {
            body.AddForceAtPosition(flattenedForce, worldPoint, ForceMode.Force);
        }
    }

    Vector3 GetSampledWindVelocity(WindField windField, Vector3 worldPoint, float timeSeconds)
    {
        Vector3 windVelocity = windField.GetWindVelocityAtWorldPosition(worldPoint, timeSeconds);
        if (!useApparentWind)
        {
            return windVelocity;
        }

        return windVelocity - body.GetPointVelocity(worldPoint);
    }

    Vector3 ConstrainAppliedForce(Vector3 force)
    {
        if (allowVerticalForce)
        {
            return force;
        }

        force.y = 0f;
        return force;
    }

    SamplePoint[] GetActiveSamplePoints()
    {
        if (!autoGenerateSamplePoints)
        {
            return manualSamplePoints;
        }

        if (!autoSamplePointsGenerated)
        {
            RefreshAutoSamplePoints();
        }

        return autoSamplePoints;
    }

    void RefreshAutoSamplePoints()
    {
        autoSamplePoints = Array.Empty<SamplePoint>();
        autoSamplePointsGenerated = true;

        if (!autoGenerateSamplePoints || !HasSamplingMesh() || !TryGetCombinedWorldBounds(out Bounds worldBounds))
        {
            return;
        }

        int safeHorizontalCount = Mathf.Max(2, horizontalSampleCount);
        int safeVerticalCount = Mathf.Max(2, verticalSampleCount);

        if (sampleMode == SampleMode.Raycast)
        {
            BakeSkinnedMeshes();
            autoSamplePoints = GenerateRaycastSamplePoints(worldBounds, safeHorizontalCount, horizontalEdgeInset, xEdgeOffset, zEdgeOffset);
        }
        else
        {
            autoSamplePoints = GenerateLatticePoints(worldBounds, safeHorizontalCount, safeVerticalCount, verticalEdgeInset, horizontalEdgeInset);
        }
    }

    SamplePoint[] GenerateRaycastSamplePoints(Bounds worldBounds, int sampleCount, float inset, float xOffset, float zOffset)
    {
        float rayStartY = worldBounds.min.y - 0.5f;
        float rayLength = worldBounds.size.y + 1f;
        List<SamplePoint> generatedPoints = new List<SamplePoint>(sampleCount * sampleCount);

        for (int zIndex = 0; zIndex < sampleCount; zIndex++)
        {
            float zT = Mathf.Lerp(inset + zOffset, 1f - inset + zOffset, zIndex / (sampleCount - 1f));
            float z = Mathf.Lerp(worldBounds.min.z, worldBounds.max.z, zT);

            for (int xIndex = 0; xIndex < sampleCount; xIndex++)
            {
                float xT = Mathf.Lerp(inset + xOffset, 1f - inset + xOffset, xIndex / (sampleCount - 1f));
                float x = Mathf.Lerp(worldBounds.min.x, worldBounds.max.x, xT);
                Ray ray = new Ray(new Vector3(x, rayStartY, z), Vector3.up);
                if (!TryGetOwnMeshRayHit(ray, rayLength, out MeshHit hit))
                {
                    continue;
                }

                generatedPoints.Add(new SamplePoint(transform.InverseTransformPoint(hit.point), 1f));
            }
        }

        return generatedPoints.ToArray();
    }

    SamplePoint[] GenerateLatticePoints(Bounds worldBounds, int horizontalCount, int verticalCount, float verticalInset, float horizontalInset)
    {
        List<SamplePoint> generatedPoints = new List<SamplePoint>(horizontalCount * horizontalCount * verticalCount);

        for (int yIndex = 0; yIndex < verticalCount; yIndex++)
        {
            float yT = Mathf.Lerp(verticalInset, 1f - verticalInset, yIndex / (verticalCount - 1f));
            float y = Mathf.Lerp(worldBounds.min.y, worldBounds.max.y, yT);

            for (int zIndex = 0; zIndex < horizontalCount; zIndex++)
            {
                float zT = Mathf.Lerp(horizontalInset, 1f - horizontalInset, zIndex / (horizontalCount - 1f));
                float z = Mathf.Lerp(worldBounds.min.z, worldBounds.max.z, zT);

                for (int xIndex = 0; xIndex < horizontalCount; xIndex++)
                {
                    float xT = Mathf.Lerp(horizontalInset, 1f - horizontalInset, xIndex / (horizontalCount - 1f));
                    float x = Mathf.Lerp(worldBounds.min.x, worldBounds.max.x, xT);
                    Vector3 worldPoint = new Vector3(x, y, z);
                    generatedPoints.Add(new SamplePoint(transform.InverseTransformPoint(worldPoint), 1f));
                }
            }
        }

        return generatedPoints.ToArray();
    }

    bool TryGetCombinedWorldBounds(out Bounds combined)
    {
        combined = default;
        bool hasBounds = false;

        for (int i = 0; i < cachedMeshFilters.Length; i++)
        {
            MeshFilter meshFilter = cachedMeshFilters[i];
            if (meshFilter == null || meshFilter.sharedMesh == null)
            {
                continue;
            }

            Bounds worldBounds = WindForceBoundsUtility.TransformBounds(meshFilter.sharedMesh.bounds, meshFilter.transform.localToWorldMatrix);
            if (!hasBounds)
            {
                combined = worldBounds;
                hasBounds = true;
            }
            else
            {
                combined.Encapsulate(worldBounds);
            }
        }

        for (int i = 0; i < cachedSkinnedMeshes.Length; i++)
        {
            SkinnedMeshRenderer skinnedMesh = cachedSkinnedMeshes[i];
            if (skinnedMesh == null || skinnedMesh.sharedMesh == null)
            {
                continue;
            }

            Bounds worldBounds = skinnedMesh.bounds;
            if (!hasBounds)
            {
                combined = worldBounds;
                hasBounds = true;
            }
            else
            {
                combined.Encapsulate(worldBounds);
            }
        }

        return hasBounds;
    }

    bool HasSamplingMesh()
    {
        RefreshMeshSourceCacheIfNeeded();
        return cachedMeshFilters.Length > 0 || cachedSkinnedMeshes.Length > 0;
    }

    bool TryGetOwnMeshRayHit(Ray worldRay, float maxDistance, out MeshHit closestHit)
    {
        closestHit = default;
        bool foundHit = false;
        float closestDistance = maxDistance;

        for (int i = 0; i < cachedMeshFilters.Length; i++)
        {
            MeshFilter meshFilter = cachedMeshFilters[i];
            if (meshFilter == null || !CanReadMesh(meshFilter.sharedMesh))
            {
                continue;
            }

            if (!TryIntersectMesh(worldRay, meshFilter.sharedMesh, meshFilter.transform.localToWorldMatrix, maxDistance, out MeshHit hit)
                || hit.distance >= closestDistance)
            {
                continue;
            }

            closestDistance = hit.distance;
            closestHit = hit;
            foundHit = true;
        }

        for (int i = 0; i < cachedSkinnedMeshes.Length; i++)
        {
            SkinnedMeshRenderer skinnedMesh = cachedSkinnedMeshes[i];
            Mesh bakedMesh = i < bakedSkinnedMeshes.Length ? bakedSkinnedMeshes[i] : null;
            if (skinnedMesh == null || bakedMesh == null || skinnedMesh.sharedMesh == null)
            {
                continue;
            }

            if (!TryIntersectMesh(worldRay, bakedMesh, skinnedMesh.transform.localToWorldMatrix, maxDistance, out MeshHit hit)
                || hit.distance >= closestDistance)
            {
                continue;
            }

            closestDistance = hit.distance;
            closestHit = hit;
            foundHit = true;
        }

        return foundHit;
    }

    static bool TryIntersectMesh(Ray worldRay, Mesh mesh, Matrix4x4 localToWorld, float maxDistance, out MeshHit closestHit)
    {
        closestHit = default;
        if (!CanReadMesh(mesh))
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
        Vector3 localOrigin = worldToLocal.MultiplyPoint3x4(worldRay.origin);
        Vector3 localDirection = worldToLocal.MultiplyVector(worldRay.direction).normalized;
        Ray localRay = new Ray(localOrigin, localDirection);

        if (!mesh.bounds.IntersectRay(localRay))
        {
            return false;
        }

        bool foundHit = false;
        float closestWorldDistance = maxDistance;

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
            if (worldDistance >= closestWorldDistance)
            {
                continue;
            }

            Vector3 localNormal = Vector3.Cross(b - a, c - a);
            if (localNormal.sqrMagnitude <= MinTriangleArea)
            {
                continue;
            }

            Vector3 worldNormal = worldToLocal.transpose.MultiplyVector(localNormal).normalized;
            closestWorldDistance = worldDistance;
            closestHit = new MeshHit
            {
                point = worldPoint,
                normal = worldNormal,
                distance = worldDistance
            };
            foundHit = true;
        }

        return foundHit;
    }

    static bool CanReadMesh(Mesh mesh)
    {
        return mesh != null && mesh.isReadable;
    }

    static bool TryIntersectTriangle(Ray ray, Vector3 a, Vector3 b, Vector3 c, out float distance)
    {
        distance = 0f;

        Vector3 edgeAB = b - a;
        Vector3 edgeAC = c - a;
        Vector3 perpendicular = Vector3.Cross(ray.direction, edgeAC);
        float determinant = Vector3.Dot(edgeAB, perpendicular);
        if (Mathf.Abs(determinant) < MinTriangleArea)
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

    static void BuildRaycastBasis(Vector3 rayDirection, out Vector3 axisU, out Vector3 axisV)
    {
        Vector3 referenceAxis = Mathf.Abs(Vector3.Dot(rayDirection, Vector3.up)) > 0.98f
            ? Vector3.right
            : Vector3.up;

        axisU = Vector3.Cross(rayDirection, referenceAxis).normalized;
        axisV = Vector3.Cross(axisU, rayDirection).normalized;
    }

    static void GetProjectedBounds(
        Bounds bounds,
        Vector3 rayDirection,
        Vector3 axisU,
        Vector3 axisV,
        out float minDepth,
        out float maxDepth,
        out float minU,
        out float maxU,
        out float minV,
        out float maxV)
    {
        Vector3 center = bounds.center;
        Vector3 extents = bounds.extents;
        Vector3[] corners =
        {
            center + new Vector3(-extents.x, -extents.y, -extents.z),
            center + new Vector3(-extents.x, -extents.y, extents.z),
            center + new Vector3(-extents.x, extents.y, -extents.z),
            center + new Vector3(-extents.x, extents.y, extents.z),
            center + new Vector3(extents.x, -extents.y, -extents.z),
            center + new Vector3(extents.x, -extents.y, extents.z),
            center + new Vector3(extents.x, extents.y, -extents.z),
            center + new Vector3(extents.x, extents.y, extents.z)
        };

        minDepth = float.PositiveInfinity;
        maxDepth = float.NegativeInfinity;
        minU = float.PositiveInfinity;
        maxU = float.NegativeInfinity;
        minV = float.PositiveInfinity;
        maxV = float.NegativeInfinity;

        for (int i = 0; i < corners.Length; i++)
        {
            Vector3 corner = corners[i];
            float depth = Vector3.Dot(corner, rayDirection);
            float u = Vector3.Dot(corner, axisU);
            float v = Vector3.Dot(corner, axisV);

            minDepth = Mathf.Min(minDepth, depth);
            maxDepth = Mathf.Max(maxDepth, depth);
            minU = Mathf.Min(minU, u);
            maxU = Mathf.Max(maxU, u);
            minV = Mathf.Min(minV, v);
            maxV = Mathf.Max(maxV, v);
        }
    }

    static float GetCenteredGridValue(float min, float max, int index, int count)
    {
        if (count <= 1)
        {
            return (min + max) * 0.5f;
        }

        return Mathf.Lerp(min, max, (index + 0.5f) / count);
    }

    void OnDrawGizmosSelected()
    {
        if (applicationMode != WindApplicationMode.PointSamples)
        {
            return;
        }

        if (!Application.isPlaying && autoGenerateSamplePoints && !autoSamplePointsGenerated)
        {
            RefreshState();
        }

        SamplePoint[] activeSamplePoints = Application.isPlaying
            ? GetActiveSamplePoints()
            : (autoGenerateSamplePoints ? autoSamplePoints : manualSamplePoints);

        if (activeSamplePoints == null || activeSamplePoints.Length == 0)
        {
            return;
        }

        float totalWeight = GetTotalWeight(activeSamplePoints);
        Gizmos.color = new Color(1f, 0.72f, 0.12f, 0.9f);

        for (int i = 0; i < activeSamplePoints.Length; i++)
        {
            float normalizedWeight = GetNormalizedWeight(activeSamplePoints[i].weight, totalWeight, activeSamplePoints.Length);
            float visualWeight = Mathf.Clamp(normalizedWeight * Mathf.Max(activeSamplePoints.Length, 1), 0.5f, 2.5f);
            float gizmoSize = 0.035f + (0.015f * visualWeight);
            Gizmos.DrawSphere(transform.TransformPoint(activeSamplePoints[i].localPosition), gizmoSize);
        }
    }
}

static class WindForceBoundsUtility
{
    public static Bounds TransformBounds(Bounds localBounds, Matrix4x4 localToWorld)
    {
        Vector3 center = localToWorld.MultiplyPoint3x4(localBounds.center);
        Vector3 extents = localBounds.extents;

        Vector3 axisX = localToWorld.MultiplyVector(new Vector3(extents.x, 0f, 0f));
        Vector3 axisY = localToWorld.MultiplyVector(new Vector3(0f, extents.y, 0f));
        Vector3 axisZ = localToWorld.MultiplyVector(new Vector3(0f, 0f, extents.z));

        Vector3 worldExtents = new Vector3(
            Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x),
            Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y),
            Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z));

        return new Bounds(center, worldExtents * 2f);
    }
}
