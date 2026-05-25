// Credit:
// This URP water component is a project-specific rewrite inspired by the retained
// "Low Poly Water" package source in Assets/LowPolyWater_Pack, originally from:
// https://assetstore.unity.com/packages/tools/particles-effects/lowpoly-water-107563
// It is intentionally simplified for this project and is not a direct copy.
using System;
using UnityEngine;
using UnityEngine.Serialization;

[ExecuteAlways]
[AddComponentMenu("Water/URP Low Poly Water")]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class UrpLowPolyWater : MonoBehaviour
{
    const string LegacyShaderPrefix = "LowPolyWater/";
    const int MaxGeneratedPlaneSearchRadius = 4;

    [Serializable]
    public struct GerstnerWave
    {
        public Vector2 direction;

        [Min(0f)]
        public float amplitude;

        [Min(0.001f)]
        public float waveLength;

        public float speed;

        [Range(0f, 1f)]
        public float steepness;

        public GerstnerWave(Vector2 direction, float amplitude, float waveLength, float speed, float steepness)
        {
            this.direction = direction;
            this.amplitude = Mathf.Max(0f, amplitude);
            this.waveLength = Mathf.Max(0.001f, waveLength);
            this.speed = speed;
            this.steepness = Mathf.Clamp01(steepness);
        }
    }

    public static UrpLowPolyWater ActiveSurface { get; private set; }

    [Header("Generated Plane")]
    [Range(2, 256)]
    public int resolution = 32;

    public Vector2 size = new Vector2(8f, 8f);

    public float baseHeight = 0.05f;

    [Header("Gerstner Waves")]
    public GerstnerWave[] waves = Array.Empty<GerstnerWave>();

    // Hidden legacy fields preserve older scenes authored before waves became a variable-size list.
    [SerializeField, HideInInspector]
    Vector2 primaryDirection = new Vector2(1f, 0.35f);

    [SerializeField, HideInInspector, Min(0f)]
    float primaryAmplitude = 0.35f;

    [SerializeField, HideInInspector, Min(0.001f)]
    float primaryWaveLength = 4f;

    [SerializeField, HideInInspector]
    float primarySpeed = 1.25f;

    [SerializeField, HideInInspector, Range(0f, 1f)]
    float primarySteepness = 0.35f;

    [SerializeField, HideInInspector]
    Vector2 secondaryDirection = new Vector2(-0.55f, 0.85f);

    [SerializeField, HideInInspector, Min(0f)]
    float secondaryAmplitude = 0.18f;

    [SerializeField, HideInInspector, Min(0.001f)]
    float secondaryWaveLength = 2.25f;

    [SerializeField, HideInInspector]
    float secondarySpeed = 1.7f;

    [SerializeField, HideInInspector, Range(0f, 1f)]
    float secondarySteepness = 0.2f;

    [SerializeField, HideInInspector, FormerlySerializedAs("legacyWaveSettingsUpgraded")]
    bool legacyWaveSettingsMigrated;

    [Header("Rendering")]
    [SerializeField]
    Material materialOverride;

    [SerializeField, HideInInspector]
    Mesh sourceMesh;

    MeshFilter meshFilter;
    MeshRenderer meshRenderer;
    Mesh generatedSourceMesh;
    Mesh runtimeMesh;
    Vector3[] animatedVertices = new Vector3[0];
    Vector3[] sourceBaseVertices = new Vector3[0];
    Vector3[] sourceAnimatedVertices = new Vector3[0];
    Vector3[] flatNormals = new Vector3[0];
    Vector2[] runtimeUvs = new Vector2[0];
    int[] flatTriangles = new int[0];
    int[] runtimeToSourceIndex = new int[0];
    float sampledBaseHeight;
    bool useGeneratedPlane;
    Bounds runtimeBounds;
    float lastAnimatedTime = float.NaN;

    // Called by Unity before the first frame if the component starts enabled.
    // Grabs component references and prepares the runtime mesh/material state.
    void Awake()
    {
        PrepareWaveSettings();
        Initialize();
    }

    // Called by Unity when the component is first added or reset from the Inspector.
    // Seeds the wave list with the same two defaults older scenes used.
    void Reset()
    {
        waves = CreateLegacyWaveArray();
        legacyWaveSettingsMigrated = true;
    }

    // Called by Unity when the component becomes active.
    // Registers this instance as the current water surface and builds an initial mesh state.
    void OnEnable()
    {
        ActiveSurface = this;
        PrepareWaveSettings();
        Initialize();
        AnimateMesh(Application.isPlaying ? Time.time : 0f);
    }

    // Called by Unity in the editor when serialized values change.
    // Rebuilds the water mesh immediately so Inspector edits preview without entering play mode.
    void OnValidate()
    {
        PrepareWaveSettings();
        Initialize();
        AnimateMesh(Application.isPlaying ? Time.time : 0f);
    }

    // Called by Unity once per rendered frame while the component is enabled.
    // Animates the water only in play mode to avoid unnecessary editor-time mesh churn.
    void Update()
    {
        if (!Application.isPlaying)
        {
            return;
        }

        AnimateMesh(Time.time);
    }

    // Called by Unity when the component is disabled or destroyed.
    // Restores the original mesh reference and releases any generated resources.
    void OnDisable()
    {
        if (ActiveSurface == this)
        {
            ActiveSurface = null;
        }

        RestoreSourceMesh();
        DestroyGeneratedSourceMesh();
    }

    // Called by World whenever the generated flat water object is rebuilt.
    // Switches this component into generated-plane mode and syncs world-owned settings.
    public void SyncFromWorld(int newResolution, Vector2 newSize, float newBaseHeight, Material assignedMaterial)
    {
        resolution = Mathf.Max(2, newResolution);
        size = new Vector2(
            Mathf.Max(0.01f, newSize.x),
            Mathf.Max(0.01f, newSize.y));
        baseHeight = newBaseHeight;
        materialOverride = assignedMaterial;
        useGeneratedPlane = true;

        PrepareWaveSettings();
        Initialize();
        AnimateMesh(Application.isPlaying ? Time.time : 0f);
    }

    // Migrates older two-wave setups and keeps each wave inside safe numeric ranges.
    void PrepareWaveSettings()
    {
        UpgradeLegacyWaveSettings();
        SanitizeWaveSettings();
    }

    // Copies the old primary/secondary wave fields into the new resizable wave list once.
    void UpgradeLegacyWaveSettings()
    {
        if (legacyWaveSettingsMigrated)
        {
            return;
        }

        if (waves != null && waves.Length > 0)
        {
            legacyWaveSettingsMigrated = true;
            return;
        }

        waves = CreateLegacyWaveArray();
        legacyWaveSettingsMigrated = true;
    }

    // Normalizes the new wave list so a bad value in the Inspector cannot destabilize the surface math.
    void SanitizeWaveSettings()
    {
        if (waves == null)
        {
            waves = Array.Empty<GerstnerWave>();
            return;
        }

        for (int i = 0; i < waves.Length; i++)
        {
            GerstnerWave wave = waves[i];
            wave.amplitude = Mathf.Max(0f, wave.amplitude);
            wave.waveLength = Mathf.Max(0.001f, wave.waveLength);
            wave.steepness = Mathf.Clamp01(wave.steepness);
            waves[i] = wave;
        }
    }

    // Builds the default two-wave set used before the water system supported an arbitrary wave count.
    GerstnerWave[] CreateLegacyWaveArray()
    {
        return new[]
        {
            new GerstnerWave(primaryDirection, primaryAmplitude, primaryWaveLength, primarySpeed, primarySteepness),
            new GerstnerWave(secondaryDirection, secondaryAmplitude, secondaryWaveLength, secondarySpeed, secondarySteepness)
        };
    }

    // Shared setup path used by Unity callbacks and SyncFromWorld.
    // Refreshes component references, decides which mesh to animate, and makes sure rendering is ready.
    void Initialize()
    {
        meshFilter = GetComponent<MeshFilter>();
        meshRenderer = GetComponent<MeshRenderer>();
        EnsureSourceMesh();
        RebuildFlatMesh();
        EnsureRenderableMaterial();
    }

    // Chooses the mesh this component should animate.
    // Called during initialization and can either use the assigned mesh or build a generated plane.
    void EnsureSourceMesh()
    {
        if (meshFilter == null)
        {
            return;
        }

        if (useGeneratedPlane)
        {
            EnsureGeneratedSourceMesh();
            sourceMesh = generatedSourceMesh;
            sampledBaseHeight = baseHeight;
            return;
        }

        if (meshFilter.sharedMesh != null && meshFilter.sharedMesh != runtimeMesh && meshFilter.sharedMesh.vertexCount > 0)
        {
            sourceMesh = meshFilter.sharedMesh;
        }

        if (sourceMesh == null || sourceMesh.vertexCount == 0)
        {
            // Fall back to a generated plane when there is no usable source mesh on the object.
            useGeneratedPlane = true;
            EnsureGeneratedSourceMesh();
            sourceMesh = generatedSourceMesh;
            sampledBaseHeight = baseHeight;
            return;
        }

        sampledBaseHeight = sourceMesh.bounds.center.y;
    }

    // Builds or refreshes the hidden source plane used by the generated flat-world water object.
    // Called whenever the World component syncs plane size, height, or resolution into this script.
    void EnsureGeneratedSourceMesh()
    {
        if (generatedSourceMesh == null)
        {
            generatedSourceMesh = new Mesh
            {
                name = "Generated Water Source",
                hideFlags = HideFlags.HideAndDontSave
            };
        }

        FlatMeshBuilder.BuildPlaneMesh(
            generatedSourceMesh,
            Mathf.Max(2, resolution),
            size,
            baseHeight);
    }

    // Converts the source mesh into a flat-shaded runtime copy that can be animated per vertex.
    // Called during initialization whenever the source mesh or generated plane may have changed.
    void RebuildFlatMesh()
    {
        if (meshFilter == null || sourceMesh == null)
        {
            return;
        }

        Vector3[] sourceVertices = sourceMesh.vertices;
        int[] sourceTriangles = sourceMesh.triangles;
        Vector2[] sourceUvs = sourceMesh.uv;

        if (sourceVertices == null || sourceVertices.Length == 0 || sourceTriangles == null || sourceTriangles.Length == 0)
        {
            return;
        }

        int vertexCount = sourceTriangles.Length;
        Vector3[] flatVertices = new Vector3[vertexCount];
        Vector2[] flatUvs = new Vector2[vertexCount];
        int[] sequentialTriangles = new int[vertexCount];
        int[] duplicatedSourceIndices = new int[vertexCount];

        for (int i = 0; i < sourceTriangles.Length; i++)
        {
            int sourceIndex = sourceTriangles[i];
            // Duplicate each triangle's vertices so neighboring triangles do not share normals.
            flatVertices[i] = sourceVertices[sourceIndex];
            flatUvs[i] = sourceUvs != null && sourceUvs.Length > sourceIndex ? sourceUvs[sourceIndex] : Vector2.zero;
            sequentialTriangles[i] = i;
            duplicatedSourceIndices[i] = sourceIndex;
        }

        if (runtimeMesh == null)
        {
            runtimeMesh = new Mesh
            {
                name = sourceMesh.name + " URP Low Poly"
            };
            runtimeMesh.MarkDynamic();
        }

        sourceBaseVertices = (Vector3[])sourceVertices.Clone();
        sourceAnimatedVertices = (Vector3[])sourceVertices.Clone();
        animatedVertices = (Vector3[])flatVertices.Clone();
        flatNormals = new Vector3[vertexCount];
        runtimeUvs = flatUvs;
        flatTriangles = sequentialTriangles;
        runtimeToSourceIndex = duplicatedSourceIndices;

        runtimeMesh.Clear();
        runtimeMesh.vertices = animatedVertices;
        runtimeMesh.triangles = flatTriangles;
        runtimeMesh.uv = runtimeUvs;
        UpdateFlatNormals(animatedVertices);
        UpdateRuntimeBounds();
        runtimeMesh.bounds = runtimeBounds;

        meshFilter.sharedMesh = runtimeMesh;
        lastAnimatedTime = float.NaN;
    }

    // Applies the current Gerstner wave surface to the runtime mesh vertices.
    // Called in play mode every frame and also once during setup to show the initial surface shape.
    void AnimateMesh(float timeSeconds)
    {
        if (runtimeMesh == null
            || sourceBaseVertices == null
            || sourceBaseVertices.Length == 0
            || sourceAnimatedVertices == null
            || runtimeToSourceIndex == null
            || runtimeToSourceIndex.Length == 0)
        {
            return;
        }

        if (Mathf.Approximately(timeSeconds, lastAnimatedTime))
        {
            return;
        }

        // First animate the unique source vertices, then copy those results into the duplicated
        // flat-shaded runtime mesh so rendering and physics can read from the same surface.
        for (int i = 0; i < sourceBaseVertices.Length; i++)
        {
            sourceAnimatedVertices[i] = EvaluateSurfacePoint(sourceBaseVertices[i], timeSeconds);
        }

        for (int i = 0; i < animatedVertices.Length; i++)
        {
            animatedVertices[i] = sourceAnimatedVertices[runtimeToSourceIndex[i]];
        }

        runtimeMesh.vertices = animatedVertices;
        UpdateFlatNormals(animatedVertices);
        runtimeMesh.bounds = runtimeBounds;
        lastAnimatedTime = timeSeconds;
    }

    // Samples the water height and normal directly under a world-space point.
    // Called by buoyancy during physics updates to decide where forces should be applied.
    public bool TryGetSurfaceDataAtWorldPosition(
        Vector3 worldPosition,
        float timeSeconds,
        out float surfaceHeight,
        out Vector3 surfaceNormal)
    {
        // Physics may query the surface between rendered frames, so advance the generated
        // mesh to the requested sample time before reading back height from it.
        AnimateMesh(timeSeconds);

        Vector3 localPosition = transform.InverseTransformPoint(worldPosition);
        Vector2 queryHalfSize = GetQueryableHalfSize();

        if (Mathf.Abs(localPosition.x) > queryHalfSize.x || Mathf.Abs(localPosition.z) > queryHalfSize.y)
        {
            surfaceHeight = 0f;
            surfaceNormal = Vector3.up;
            return false;
        }

        if (TrySampleRenderedGeneratedPlane(localPosition, out float localSurfaceHeight, out Vector3 localSurfaceNormal))
        {
            Vector3 renderedSurfacePoint = new Vector3(
                localPosition.x,
                localSurfaceHeight,
                localPosition.z);

            surfaceHeight = transform.TransformPoint(renderedSurfacePoint).y;
            surfaceNormal = transform.TransformDirection(localSurfaceNormal).normalized;
            return true;
        }

        if (TrySampleAnalyticSurface(localPosition, timeSeconds, out localSurfaceHeight, out localSurfaceNormal))
        {
            Vector3 analyticSurfacePoint = new Vector3(
                localPosition.x,
                localSurfaceHeight,
                localPosition.z);

            surfaceHeight = transform.TransformPoint(analyticSurfacePoint).y;
            surfaceNormal = transform.TransformDirection(localSurfaceNormal).normalized;
            return true;
        }

        surfaceHeight = 0f;
        surfaceNormal = Vector3.up;
        return false;
    }

    // Samples the actual currently rendered generated water mesh at a local-space XZ position.
    // Called by buoyancy so forces line up with the same triangle surface the player sees on screen.
    bool TrySampleRenderedGeneratedPlane(Vector3 localPosition, out float localSurfaceHeight, out Vector3 localSurfaceNormal)
    {
        int safeResolution = Mathf.Max(2, resolution);
        if (!useGeneratedPlane
            || sourceAnimatedVertices == null
            || sourceAnimatedVertices.Length != safeResolution * safeResolution)
        {
            localSurfaceHeight = 0f;
            localSurfaceNormal = Vector3.up;
            return false;
        }

        Vector2 baseHalfSize = GetBaseHalfSize();
        float normalizedX = Mathf.InverseLerp(-baseHalfSize.x, baseHalfSize.x, localPosition.x);
        float normalizedZ = Mathf.InverseLerp(-baseHalfSize.y, baseHalfSize.y, localPosition.z);

        float gridX = normalizedX * (safeResolution - 1f);
        float gridZ = normalizedZ * (safeResolution - 1f);

        int centerCellX = Mathf.Clamp(Mathf.FloorToInt(gridX), 0, safeResolution - 2);
        int centerCellZ = Mathf.Clamp(Mathf.FloorToInt(gridZ), 0, safeResolution - 2);

        float cellSizeX = (baseHalfSize.x * 2f) / Mathf.Max(safeResolution - 1f, 1f);
        float cellSizeZ = (baseHalfSize.y * 2f) / Mathf.Max(safeResolution - 1f, 1f);
        float minCellSize = Mathf.Max(Mathf.Min(cellSizeX, cellSizeZ), 0.0001f);
        int searchRadius = Mathf.Clamp(
            Mathf.CeilToInt(ComputeMaxHorizontalDisplacement() / minCellSize) + 1,
            1,
            MaxGeneratedPlaneSearchRadius);

        Vector2 pointXZ = new Vector2(localPosition.x, localPosition.z);

        for (int zOffset = -searchRadius; zOffset <= searchRadius; zOffset++)
        {
            int cellZ = centerCellZ + zOffset;
            if (cellZ < 0 || cellZ >= safeResolution - 1)
            {
                continue;
            }

            for (int xOffset = -searchRadius; xOffset <= searchRadius; xOffset++)
            {
                int cellX = centerCellX + xOffset;
                if (cellX < 0 || cellX >= safeResolution - 1)
                {
                    continue;
                }

                if (TrySampleGeneratedCell(pointXZ, cellX, cellZ, safeResolution, out localSurfaceHeight, out localSurfaceNormal))
                {
                    return true;
                }
            }
        }

        localSurfaceHeight = 0f;
        localSurfaceNormal = Vector3.up;
        return false;
    }

    // Samples the analytic Gerstner surface directly.
    // Called as a fallback when the visible mesh is not the generated grid or a projected cell lookup misses.
    bool TrySampleAnalyticSurface(Vector3 localPosition, float timeSeconds, out float localSurfaceHeight, out Vector3 localSurfaceNormal)
    {
        Vector2 parameterXZ = SolveSurfaceParameter(new Vector2(localPosition.x, localPosition.z), timeSeconds);
        Vector3 centerPoint = EvaluateSurfacePoint(new Vector3(parameterXZ.x, sampledBaseHeight, parameterXZ.y), timeSeconds);

        float sampleOffset = Mathf.Max(GetAnalyticSampleOffset(), 0.001f);
        Vector3 pointLeft = EvaluateSurfacePoint(new Vector3(parameterXZ.x - sampleOffset, sampledBaseHeight, parameterXZ.y), timeSeconds);
        Vector3 pointRight = EvaluateSurfacePoint(new Vector3(parameterXZ.x + sampleOffset, sampledBaseHeight, parameterXZ.y), timeSeconds);
        Vector3 pointDown = EvaluateSurfacePoint(new Vector3(parameterXZ.x, sampledBaseHeight, parameterXZ.y - sampleOffset), timeSeconds);
        Vector3 pointUp = EvaluateSurfacePoint(new Vector3(parameterXZ.x, sampledBaseHeight, parameterXZ.y + sampleOffset), timeSeconds);

        localSurfaceHeight = centerPoint.y;
        localSurfaceNormal = Vector3.Cross(pointUp - pointDown, pointRight - pointLeft).normalized;
        if (localSurfaceNormal.y < 0f)
        {
            localSurfaceNormal = -localSurfaceNormal;
        }

        return true;
    }

    // Solves for the base XZ parameter whose Gerstner-displaced point lands near the requested XZ position.
    // Called by analytic surface sampling so buoyancy can still query a horizontally displaced wave surface.
    Vector2 SolveSurfaceParameter(Vector2 targetXZ, float timeSeconds)
    {
        Vector2 parameterXZ = targetXZ;

        // Gerstner waves move vertices in XZ as well as Y, so we iteratively back-solve the
        // undisplaced parameter that produces the requested surface point in local space.
        for (int iteration = 0; iteration < 4; iteration++)
        {
            Vector3 surfacePoint = EvaluateSurfacePoint(new Vector3(parameterXZ.x, sampledBaseHeight, parameterXZ.y), timeSeconds);
            Vector2 error = targetXZ - new Vector2(surfacePoint.x, surfacePoint.z);
            parameterXZ += error;

            if (error.sqrMagnitude < 0.000001f)
            {
                break;
            }
        }

        return parameterXZ;
    }

    // Samples one generated grid cell by testing both of its rendered triangles in projected XZ space.
    // Called while sampling the visible generated mesh for buoyancy.
    bool TrySampleGeneratedCell(
        Vector2 pointXZ,
        int cellX,
        int cellZ,
        int safeResolution,
        out float localSurfaceHeight,
        out Vector3 localSurfaceNormal)
    {
        int bottomLeftIndex = cellX + cellZ * safeResolution;
        int bottomRightIndex = bottomLeftIndex + 1;
        int topLeftIndex = bottomLeftIndex + safeResolution;
        int topRightIndex = topLeftIndex + 1;

        if (TrySampleTriangleProjection(
            pointXZ,
            sourceAnimatedVertices[bottomLeftIndex],
            sourceAnimatedVertices[topLeftIndex],
            sourceAnimatedVertices[topRightIndex],
            out localSurfaceHeight,
            out localSurfaceNormal))
        {
            return true;
        }

        return TrySampleTriangleProjection(
            pointXZ,
            sourceAnimatedVertices[bottomLeftIndex],
            sourceAnimatedVertices[topRightIndex],
            sourceAnimatedVertices[bottomRightIndex],
            out localSurfaceHeight,
            out localSurfaceNormal);
    }

    // Samples one rendered triangle projected into XZ space and interpolates height from barycentric weights.
    // Called while matching buoyancy queries against the visible generated water surface.
    bool TrySampleTriangleProjection(
        Vector2 pointXZ,
        Vector3 vertexA,
        Vector3 vertexB,
        Vector3 vertexC,
        out float localSurfaceHeight,
        out Vector3 localSurfaceNormal)
    {
        Vector3 barycentric = ComputeBarycentricCoordinates(
            pointXZ,
            new Vector2(vertexA.x, vertexA.z),
            new Vector2(vertexB.x, vertexB.z),
            new Vector2(vertexC.x, vertexC.z));

        const float epsilon = -0.001f;
        if (barycentric.x < epsilon || barycentric.y < epsilon || barycentric.z < epsilon)
        {
            localSurfaceHeight = 0f;
            localSurfaceNormal = Vector3.up;
            return false;
        }

        localSurfaceHeight = vertexA.y * barycentric.x
            + vertexB.y * barycentric.y
            + vertexC.y * barycentric.z;

        localSurfaceNormal = Vector3.Cross(vertexB - vertexA, vertexC - vertexA).normalized;
        if (localSurfaceNormal.y < 0f)
        {
            localSurfaceNormal = -localSurfaceNormal;
        }

        return true;
    }

    // Evaluates the current multi-wave Gerstner surface at one source vertex.
    // Called by mesh animation and analytic fallback sampling so both paths share the same implementation.
    Vector3 EvaluateSurfacePoint(Vector3 sourceVertex, float timeSeconds)
    {
        Vector3 surfacePoint = sourceVertex;
        Vector2 anchorXZ = new Vector2(sourceVertex.x, sourceVertex.z);
        int waveCount = GetActiveWaveCount();

        for (int i = 0; i < waves.Length; i++)
        {
            ApplyGerstnerWave(ref surfacePoint, anchorXZ, timeSeconds, waves[i], waveCount);
        }

        return surfacePoint;
    }

    // Adds one Gerstner wave contribution into a displaced surface point.
    // Called by EvaluateSurfacePoint while iterating over the configurable wave list.
    void ApplyGerstnerWave(
        ref Vector3 surfacePoint,
        Vector2 anchorXZ,
        float timeSeconds,
        GerstnerWave wave,
        int totalWaveCount)
    {
        if (wave.amplitude <= 0f)
        {
            return;
        }

        Vector2 normalizedDirection = NormalizeDirection(wave.direction);
        float safeWaveLength = Mathf.Max(0.001f, wave.waveLength);
        float waveNumber = Mathf.PI * 2f / safeWaveLength;
        float clampedSteepness = Mathf.Clamp01(wave.steepness);
        float horizontalFactor = Mathf.Min(
            1f,
            clampedSteepness / Mathf.Max(waveNumber * wave.amplitude * Mathf.Max(totalWaveCount, 1), 0.0001f));
        float phase = waveNumber * Vector2.Dot(normalizedDirection, anchorXZ) + timeSeconds * wave.speed;
        float cosine = Mathf.Cos(phase);
        float sine = Mathf.Sin(phase);

        surfacePoint.x += normalizedDirection.x * (horizontalFactor * wave.amplitude * cosine);
        surfacePoint.z += normalizedDirection.y * (horizontalFactor * wave.amplitude * cosine);
        surfacePoint.y += wave.amplitude * sine;
    }

    // Computes barycentric coordinates for a 2D point inside one generated water triangle.
    // Called while sampling the rendered mesh so buoyancy interpolates the same plane the mesh draws.
    Vector3 ComputeBarycentricCoordinates(Vector2 point, Vector2 a, Vector2 b, Vector2 c)
    {
        Vector2 v0 = b - a;
        Vector2 v1 = c - a;
        Vector2 v2 = point - a;

        float denominator = v0.x * v1.y - v1.x * v0.y;
        if (Mathf.Abs(denominator) < 0.000001f)
        {
            return new Vector3(1f, 0f, 0f);
        }

        float inverseDenominator = 1f / denominator;
        float barycentricB = (v2.x * v1.y - v1.x * v2.y) * inverseDenominator;
        float barycentricC = (v0.x * v2.y - v2.x * v0.y) * inverseDenominator;
        float barycentricA = 1f - barycentricB - barycentricC;
        return new Vector3(barycentricA, barycentricB, barycentricC);
    }

    // Returns the true half-extents of the undisplaced source surface in local XZ space.
    // Called when seeding mesh-cell lookups and analytic sampling.
    Vector2 GetBaseHalfSize()
    {
        if (useGeneratedPlane || sourceMesh == null)
        {
            return new Vector2(
                Mathf.Max(0.01f, size.x) * 0.5f,
                Mathf.Max(0.01f, size.y) * 0.5f);
        }

        Bounds bounds = sourceMesh.bounds;
        return new Vector2(
            Mathf.Max(0.01f, bounds.extents.x),
            Mathf.Max(0.01f, bounds.extents.z));
    }

    // Returns the queryable half-extents after accounting for horizontal Gerstner displacement.
    // Called before buoyancy sampling so points near the water edge are not rejected too early.
    Vector2 GetQueryableHalfSize()
    {
        Vector2 baseHalfSize = GetBaseHalfSize();
        float horizontalPadding = ComputeMaxHorizontalDisplacement();
        return new Vector2(baseHalfSize.x + horizontalPadding, baseHalfSize.y + horizontalPadding);
    }

    // Returns the number of enabled Gerstner waves contributing to the surface.
    // Called when scaling steepness so the combined wave set stays stable.
    int GetActiveWaveCount()
    {
        int waveCount = 0;

        for (int i = 0; i < waves.Length; i++)
        {
            if (waves[i].amplitude > 0f)
            {
                waveCount++;
            }
        }

        return Mathf.Max(waveCount, 1);
    }

    // Computes the largest possible horizontal Gerstner offset from the active wave set.
    // Called when expanding bounds and deciding how widely generated cells should be searched.
    float ComputeMaxHorizontalDisplacement()
    {
        int waveCount = GetActiveWaveCount();
        float maxDisplacement = 0f;

        for (int i = 0; i < waves.Length; i++)
        {
            GerstnerWave wave = waves[i];
            maxDisplacement += ComputeWaveHorizontalDisplacement(wave.amplitude, wave.waveLength, wave.steepness, waveCount);
        }

        return maxDisplacement;
    }

    // Computes the horizontal displacement bound for one Gerstner wave.
    // Called by ComputeMaxHorizontalDisplacement while summing the active wave list.
    float ComputeWaveHorizontalDisplacement(float amplitude, float waveLength, float steepness, int totalWaveCount)
    {
        if (amplitude <= 0f)
        {
            return 0f;
        }

        float safeWaveLength = Mathf.Max(0.001f, waveLength);
        float waveNumber = Mathf.PI * 2f / safeWaveLength;
        float clampedSteepness = Mathf.Clamp01(steepness);
        float horizontalFactor = Mathf.Min(
            1f,
            clampedSteepness / Mathf.Max(waveNumber * amplitude * Mathf.Max(totalWaveCount, 1), 0.0001f));
        return horizontalFactor * amplitude;
    }

    // Computes the total possible vertical wave excursion for bounds padding.
    // Called while rebuilding runtime mesh bounds.
    float ComputeCombinedWaveAmplitude()
    {
        float totalAmplitude = 0f;

        for (int i = 0; i < waves.Length; i++)
        {
            totalAmplitude += Mathf.Max(0f, waves[i].amplitude);
        }

        return totalAmplitude;
    }

    // Chooses a small local-space step for analytic normal sampling.
    // Called when approximating a Gerstner normal outside the generated visible mesh lookup path.
    float GetAnalyticSampleOffset()
    {
        Vector2 baseHalfSize = GetBaseHalfSize();
        float baseCellSize = Mathf.Min(baseHalfSize.x * 2f, baseHalfSize.y * 2f) / Mathf.Max(resolution - 1f, 1f);
        return Mathf.Max(baseCellSize * 0.5f, 0.05f);
    }

    // Ensures a non-zero normalized 2D direction for wave evaluation.
    // Called before applying each Gerstner wave so bad inspector input does not break the surface math.
    Vector2 NormalizeDirection(Vector2 direction)
    {
        if (direction.sqrMagnitude < 0.000001f)
        {
            return Vector2.right;
        }

        return direction.normalized;
    }

    // Rebuilds one flat normal per triangle for the runtime mesh.
    // Called after creating the flat mesh and after each animated vertex update.
    void UpdateFlatNormals(Vector3[] vertices)
    {
        if (flatNormals == null || flatNormals.Length != vertices.Length)
        {
            flatNormals = new Vector3[vertices.Length];
        }

        for (int triangleIndex = 0; triangleIndex < flatTriangles.Length; triangleIndex += 3)
        {
            int a = flatTriangles[triangleIndex];
            int b = flatTriangles[triangleIndex + 1];
            int c = flatTriangles[triangleIndex + 2];

            Vector3 ab = vertices[b] - vertices[a];
            Vector3 ac = vertices[c] - vertices[a];
            // Assign the same normal to all three duplicated vertices to preserve the low-poly look.
            Vector3 normal = Vector3.Cross(ab, ac).normalized;
            if (normal.y < 0f)
            {
                normal = -normal;
            }

            flatNormals[a] = normal;
            flatNormals[b] = normal;
            flatNormals[c] = normal;
        }

        runtimeMesh.normals = flatNormals;
    }

    // Computes a conservative bounding box for the animated mesh.
    // Called after rebuilding the runtime mesh so we can reuse cached bounds during animation.
    void UpdateRuntimeBounds()
    {
        if (sourceMesh == null)
        {
            runtimeBounds = new Bounds(Vector3.zero, new Vector3(1f, 1f, 1f));
            return;
        }

        Bounds sourceBounds = sourceMesh.bounds;
        Vector3 boundsSize = sourceBounds.size;
        float horizontalPadding = ComputeMaxHorizontalDisplacement() * 2f;

        boundsSize.x += horizontalPadding;
        boundsSize.z += horizontalPadding;
        boundsSize.y = Mathf.Max(boundsSize.y, 0.02f) + ComputeCombinedWaveAmplitude() * 2f;
        runtimeBounds = new Bounds(sourceBounds.center, boundsSize);
    }

    // Picks the material that should render this water surface.
    // Called during initialization after the mesh path is ready.
    void EnsureRenderableMaterial()
    {
        if (meshRenderer == null)
        {
            return;
        }

        Material currentMaterial = meshRenderer.sharedMaterial;
        Material chosenMaterial = null;

        if (IsRenderableMaterial(materialOverride))
        {
            chosenMaterial = materialOverride;
        }
        else if (!useGeneratedPlane && IsRenderableMaterial(currentMaterial))
        {
            // Manually placed water objects can keep an already assigned supported material.
            chosenMaterial = currentMaterial;
        }

        meshRenderer.sharedMaterial = chosenMaterial;
        meshRenderer.enabled = chosenMaterial != null;
    }

    // Returns true when a material exists, uses a supported shader, and is not the old built-in pack shader.
    // Called while selecting whether a user-supplied material is safe to render in the current pipeline.
    bool IsRenderableMaterial(Material material)
    {
        return material != null
            && material.shader != null
            && material.shader.isSupported
            && !material.shader.name.StartsWith(LegacyShaderPrefix);
    }

    // Restores the original source mesh on the MeshFilter and destroys the animated runtime mesh.
    // Called when this component is disabled so it does not leave hidden generated data behind.
    void RestoreSourceMesh()
    {
        if (meshFilter != null && meshFilter.sharedMesh == runtimeMesh)
        {
            meshFilter.sharedMesh = sourceMesh != generatedSourceMesh ? sourceMesh : null;
        }

        if (runtimeMesh == null)
        {
            return;
        }

        if (Application.isPlaying)
        {
            Destroy(runtimeMesh);
        }
        else
        {
            DestroyImmediate(runtimeMesh);
        }

        runtimeMesh = null;
    }

    // Destroys the hidden generated plane source mesh used by the flat-world water path.
    // Called when this component stops managing a generated water surface.
    void DestroyGeneratedSourceMesh()
    {
        if (generatedSourceMesh == null)
        {
            return;
        }

        if (Application.isPlaying)
        {
            Destroy(generatedSourceMesh);
        }
        else
        {
            DestroyImmediate(generatedSourceMesh);
        }

        if (sourceMesh == generatedSourceMesh)
        {
            sourceMesh = null;
        }

        generatedSourceMesh = null;
    }
}
