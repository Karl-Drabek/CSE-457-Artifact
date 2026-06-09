// Credit:
// This URP water component is a project-specific rewrite inspired by the retained
// "Low Poly Water" package source in Assets/LowPolyWater_Pack, originally from:
// https://assetstore.unity.com/packages/tools/particles-effects/lowpoly-water-107563
// It is intentionally simplified for this project and is not a direct copy.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Serialization;

[ExecuteAlways]
[AddComponentMenu("Water/URP Low Poly Water")]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
/// <summary>
/// Low-poly water surface used by the generated world. World.cs owns the
/// authoritative settings and pushes them into this component for each chunk.
/// </summary>
public class UrpLowPolyWater : MonoBehaviour
{
    const string LegacyShaderPrefix = "LowPolyWater/";
    const int MaxGeneratedPlaneSearchRadius = 4;
    const int GizmoCircleSegments = 32;
    const float MinWhirlpoolRadius = 0.001f;
    const float WhirlpoolVisualSpinSpeedScale = 0.55f;
    const float WhirlpoolVisualPullScale = 0.08f;
    const float WhirlpoolVisualDepthScale = 0.2f;
    const float WhirlpoolVisualSpiralTurns = 5f;
    const float WhirlpoolMaxVisualAmplitudeRatio = 0.12f;
    const float MinWhirlpoolDirectionDistance = 0.0001f;
    const float MinWhitecapCreaseRange = 0.001f;
    const string WaterUpdateSampleName = "UrpLowPolyWater.Update";
    const string AnimateMeshSampleName = "UrpLowPolyWater.AnimateMesh";
    const string SampleSurfaceSampleName = "UrpLowPolyWater.TryGetSurfaceDataAtWorldPosition";
    const string UpdateFlatSurfaceDataSampleName = "UrpLowPolyWater.UpdateFlatSurfaceData";

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

    [Serializable]
    public struct WhirlpoolFeature
    {
        // Water features are authored in world XZ space so they can line up with the same map coordinates as wind.
        public Vector2 centerXZ;

        [Min(MinWhirlpoolRadius)]
        public float radius;

        [Min(0f)]
        public float depth;

        // Tangential water velocity around the whirlpool center in meters per second.
        public float spinSpeed;

        // Inward water velocity toward the whirlpool center in meters per second.
        [Min(0f)]
        public float pullStrength;

        public WhirlpoolFeature(Vector2 centerXZ, float radius, float depth, float spinSpeed, float pullStrength)
        {
            this.centerXZ = centerXZ;
            this.radius = Mathf.Max(MinWhirlpoolRadius, radius);
            this.depth = Mathf.Max(0f, depth);
            this.spinSpeed = spinSpeed;
            this.pullStrength = Mathf.Max(0f, pullStrength);
        }
    }

    static readonly HashSet<UrpLowPolyWater> ActiveSurfaces = new HashSet<UrpLowPolyWater>();

    public static UrpLowPolyWater ActiveSurface => GetAnyActiveSurface();
    public static int ActiveSurfaceCount => CountActiveSurfaces();

    [SerializeField, HideInInspector, Range(2, 256)]
    public int resolution = 32;

    [SerializeField, HideInInspector]
    public Vector2 size = new Vector2(8f, 8f);

    [SerializeField, HideInInspector]
    public float baseHeight = 0.05f;

    [SerializeField, HideInInspector]
    public GerstnerWave[] waves = Array.Empty<GerstnerWave>();

    [SerializeField, HideInInspector]
    public WhirlpoolFeature[] whirlpools = Array.Empty<WhirlpoolFeature>();

    [SerializeField, HideInInspector]
    public bool enableWhitecaps;

    [SerializeField, HideInInspector, Min(0f)]
    public float whitecapHeightThreshold = 0.2f;

    [FormerlySerializedAs("whitecapSlopeAngle")]
    [SerializeField, HideInInspector, Range(0f, 180f)]
    public float whitecapCreaseAngle = 20f;

    [SerializeField, HideInInspector, Range(1, 16)]
    public int whitecapTriangleStride = 2;

    [FormerlySerializedAs("whitecapSlopeBlend")]
    [SerializeField, HideInInspector, Min(0f)]
    public float whitecapCreaseBlendAngle = 12f;

    [SerializeField, HideInInspector, Range(0f, 1f)]
    public float whitecapStrength = 1f;

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

    [SerializeField, HideInInspector]
    Material materialOverride;

    [SerializeField, HideInInspector]
    Mesh sourceMesh;

    [SerializeField, HideInInspector]
    bool clipSamplingToSourceTriangles;

    MeshFilter meshFilter;
    MeshRenderer meshRenderer;
    Mesh generatedSourceMesh;
    Mesh runtimeMesh;
    Vector3[] animatedVertices = new Vector3[0];
    Vector3[] sourceBaseVertices = new Vector3[0];
    Vector3[] sourceAnimatedVertices = new Vector3[0];
    Vector3[] sourceWhitecapNormalSums = new Vector3[0];
    int[] sourceWhitecapNormalCounts = new int[0];
    float[] sourceWhitecapCreaseAngles = new float[0];
    float[] sourceWhitecapMasks = new float[0];
    Vector3[] flatNormals = new Vector3[0];
    Color[] runtimeColors = new Color[0];
    Vector2[] runtimeUvs = new Vector2[0];
    int[] flatTriangles = new int[0];
    int[] runtimeToSourceIndex = new int[0];
    bool[] sourceGeneratedCellActivity = Array.Empty<bool>();
    float sampledBaseHeight;
    bool useGeneratedPlane;
    Bounds runtimeBounds;
    float lastAnimatedTime = float.NaN;

    // Called by Unity before the first frame if the component starts enabled.
    // Grabs component references and prepares the runtime mesh/material state.
    void Awake()
    {
        PrepareSurfaceSettings();
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
        ActiveSurfaces.Add(this);
        PrepareSurfaceSettings();
        DestroyLegacyWhitecapParticleObjects();
        Initialize();
        AnimateMesh(Application.isPlaying ? Time.time : 0f);
    }

    // Called by Unity in the editor when serialized values change.
    // Rebuilds the water mesh immediately so Inspector edits preview without entering play mode.
    void OnValidate()
    {
        PrepareSurfaceSettings();
        DestroyLegacyWhitecapParticleObjects();
        Initialize();
        AnimateMesh(Application.isPlaying ? Time.time : 0f);
    }

    // Called by Unity once per rendered frame while the component is enabled.
    // Animates the water only in play mode to avoid unnecessary editor-time mesh churn.
    // Whitecaps ride on the animated vertex colors, so there is no separate spray system here now.
    void Update()
    {
        using (new ProfileScope(WaterUpdateSampleName))
        {
        if (!Application.isPlaying)
        {
            return;
        }

        if (meshRenderer != null && !meshRenderer.isVisible)
        {
            return;
        }

        AnimateMesh(Time.time);
        }
    }

    // Called by Unity when the component is disabled or destroyed.
    // Restores the original mesh reference and releases any generated resources.
    void OnDisable()
    {
        ActiveSurfaces.Remove(this);

        RestoreSourceMesh();
        DestroyGeneratedSourceMesh();
    }

    public static bool TryResolveSurfaceAtWorldPosition(Vector3 worldPosition, out UrpLowPolyWater water)
    {
        foreach (UrpLowPolyWater candidate in ActiveSurfaces)
        {
            if (candidate == null || !candidate.isActiveAndEnabled)
            {
                continue;
            }

            if (candidate.ContainsWorldPosition(worldPosition))
            {
                water = candidate;
                return true;
            }
        }

        water = GetAnyActiveSurface();
        return water != null;
    }

    static UrpLowPolyWater GetAnyActiveSurface()
    {
        foreach (UrpLowPolyWater surface in ActiveSurfaces)
        {
            if (surface != null && surface.isActiveAndEnabled)
            {
                return surface;
            }
        }

        return null;
    }

    static int CountActiveSurfaces()
    {
        int count = 0;
        foreach (UrpLowPolyWater surface in ActiveSurfaces)
        {
            if (surface != null && surface.isActiveAndEnabled)
            {
                count++;
            }
        }

        return count;
    }

    public bool ContainsWorldPosition(Vector3 worldPosition)
    {
        Vector3 localPosition = transform.InverseTransformPoint(worldPosition);
        Vector2 queryHalfSize = GetQueryableHalfSize();
        return Mathf.Abs(localPosition.x) <= queryHalfSize.x
            && Mathf.Abs(localPosition.z) <= queryHalfSize.y;
    }

    // Called by World whenever the generated flat water object is rebuilt.
    // Switches this component into generated-plane mode and syncs world-owned settings.
    public void SyncFromWorld(
        int newResolution,
        Vector2 newSize,
        float newBaseHeight,
        Material assignedMaterial,
        GerstnerWave[] assignedWaves,
        WhirlpoolFeature[] assignedWhirlpools,
        bool assignedEnableWhitecaps,
        float assignedWhitecapHeightThreshold,
        float assignedWhitecapCreaseAngle,
        int assignedWhitecapTriangleStride,
        float assignedWhitecapCreaseBlendAngle,
        float assignedWhitecapStrength,
        Mesh assignedSourceMesh = null,
        bool assignedClipSamplingToSourceTriangles = false)
    {
        resolution = Mathf.Max(2, newResolution);
        size = new Vector2(
            Mathf.Max(0.01f, newSize.x),
            Mathf.Max(0.01f, newSize.y));
        baseHeight = newBaseHeight;
        materialOverride = assignedMaterial;
        waves = assignedWaves != null ? (GerstnerWave[])assignedWaves.Clone() : Array.Empty<GerstnerWave>();
        whirlpools = assignedWhirlpools != null ? (WhirlpoolFeature[])assignedWhirlpools.Clone() : Array.Empty<WhirlpoolFeature>();
        enableWhitecaps = assignedEnableWhitecaps;
        whitecapHeightThreshold = assignedWhitecapHeightThreshold;
        whitecapCreaseAngle = assignedWhitecapCreaseAngle;
        whitecapTriangleStride = assignedWhitecapTriangleStride;
        whitecapCreaseBlendAngle = assignedWhitecapCreaseBlendAngle;
        whitecapStrength = assignedWhitecapStrength;
        clipSamplingToSourceTriangles = assignedClipSamplingToSourceTriangles;
        useGeneratedPlane = assignedSourceMesh == null;
        sourceMesh = assignedSourceMesh;

        if (!useGeneratedPlane)
        {
            meshFilter ??= GetComponent<MeshFilter>();
            if (meshFilter != null)
            {
                meshFilter.sharedMesh = assignedSourceMesh;
            }
        }

        PrepareSurfaceSettings();
        Initialize();
        AnimateMesh(Application.isPlaying ? Time.time : 0f);
    }

    // Migrates older two-wave setups and keeps each wave inside safe numeric ranges.
    void PrepareSurfaceSettings()
    {
        UpgradeLegacyWaveSettings();
        SanitizeWaveSettings();
        SanitizeWhirlpoolSettings();
        SanitizeWhitecapSettings();
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

    // Keeps whirlpool settings numerically safe for both rendering and water sampling.
    void SanitizeWhirlpoolSettings()
    {
        if (whirlpools == null)
        {
            whirlpools = Array.Empty<WhirlpoolFeature>();
            return;
        }

        for (int i = 0; i < whirlpools.Length; i++)
        {
            WhirlpoolFeature whirlpool = whirlpools[i];
            whirlpool.radius = Mathf.Max(MinWhirlpoolRadius, whirlpool.radius);
            whirlpool.depth = Mathf.Max(0f, whirlpool.depth);
            whirlpool.pullStrength = Mathf.Max(0f, whirlpool.pullStrength);
            whirlpools[i] = whirlpool;
        }
    }

    // Whitecaps are currently driven entirely by the runtime mesh's vertex-color foam mask.
    void SanitizeWhitecapSettings()
    {
        whitecapHeightThreshold = Mathf.Max(0f, whitecapHeightThreshold);
        whitecapCreaseAngle = Mathf.Clamp(whitecapCreaseAngle, 0f, 180f);
        whitecapTriangleStride = Mathf.Max(1, whitecapTriangleStride);
        whitecapCreaseBlendAngle = Mathf.Max(0f, whitecapCreaseBlendAngle);
        whitecapStrength = Mathf.Clamp01(whitecapStrength);
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
        RebuildGeneratedCellActivity();
        RebuildFlatMesh();
        EnsureRenderableMaterial();
    }

    void RebuildGeneratedCellActivity()
    {
        int safeResolution = Mathf.Max(2, resolution);
        int cellCount = Mathf.Max(0, (safeResolution - 1) * (safeResolution - 1));
        if (sourceMesh == null || sourceMesh.vertexCount != safeResolution * safeResolution || cellCount == 0)
        {
            sourceGeneratedCellActivity = Array.Empty<bool>();
            return;
        }

        bool[] cellActivity = new bool[cellCount];
        int[] sourceTriangles = sourceMesh.triangles;
        if (sourceTriangles == null)
        {
            sourceGeneratedCellActivity = cellActivity;
            return;
        }

        for (int i = 0; i + 2 < sourceTriangles.Length; i += 3)
        {
            int bottomLeftIndex = Mathf.Min(sourceTriangles[i], Mathf.Min(sourceTriangles[i + 1], sourceTriangles[i + 2]));
            int cellX = bottomLeftIndex % safeResolution;
            int cellZ = bottomLeftIndex / safeResolution;
            if (cellX < 0 || cellZ < 0 || cellX >= safeResolution - 1 || cellZ >= safeResolution - 1)
            {
                continue;
            }

            cellActivity[cellX + cellZ * (safeResolution - 1)] = true;
        }

        sourceGeneratedCellActivity = cellActivity;
    }

    // Cleans up children left behind by older whitecap particle experiments when scenes reload.
    void DestroyLegacyWhitecapParticleObjects()
    {
        DestroyChildIfPresent("Whitecap Particles");
        DestroyChildIfPresent("Whitecap Crest Particles");
    }

    void DestroyChildIfPresent(string childName)
    {
        Transform child = transform.Find(childName);
        if (child == null)
        {
            return;
        }

        if (Application.isPlaying)
        {
            Destroy(child.gameObject);
        }
        else
        {
            DestroyImmediate(child.gameObject);
        }
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
        sourceWhitecapNormalSums = new Vector3[sourceVertices.Length];
        sourceWhitecapNormalCounts = new int[sourceVertices.Length];
        sourceWhitecapCreaseAngles = new float[sourceVertices.Length];
        sourceWhitecapMasks = new float[sourceVertices.Length];
        animatedVertices = (Vector3[])flatVertices.Clone();
        flatNormals = new Vector3[vertexCount];
        runtimeColors = new Color[vertexCount];
        runtimeUvs = flatUvs;
        flatTriangles = sequentialTriangles;
        runtimeToSourceIndex = duplicatedSourceIndices;

        runtimeMesh.Clear();
        runtimeMesh.vertices = animatedVertices;
        runtimeMesh.triangles = flatTriangles;
        runtimeMesh.uv = runtimeUvs;
        UpdateFlatSurfaceData(animatedVertices);
        UpdateRuntimeBounds();
        runtimeMesh.bounds = runtimeBounds;

        meshFilter.sharedMesh = runtimeMesh;
        lastAnimatedTime = float.NaN;
    }

    // Applies the current Gerstner wave surface to the runtime mesh vertices.
    // Called in play mode every frame and also once during setup to show the initial surface shape.
    void AnimateMesh(float timeSeconds)
    {
        using (new ProfileScope(AnimateMeshSampleName))
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
        UpdateFlatSurfaceData(animatedVertices);
        runtimeMesh.bounds = runtimeBounds;
        lastAnimatedTime = timeSeconds;
        }
    }

    // Samples the water height and normal directly under a world-space point.
    // Called by buoyancy during physics updates to decide where forces should be applied.
    public bool TryGetSurfaceDataAtWorldPosition(
        Vector3 worldPosition,
        float timeSeconds,
        out float surfaceHeight,
        out Vector3 surfaceNormal)
    {
        using (new ProfileScope(SampleSurfaceSampleName))
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

        if (clipSamplingToSourceTriangles)
        {
            surfaceHeight = 0f;
            surfaceNormal = Vector3.up;
            return false;
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
    }

    // Returns the horizontal water flow at a point so buoyancy can react to currents and whirlpools.
    public Vector3 GetFlowVelocityAtWorldPosition(Vector3 worldPosition, float timeSeconds)
    {
        Vector3 localPosition = transform.InverseTransformPoint(worldPosition);
        Vector2 queryHalfSize = GetQueryableHalfSize();

        if (Mathf.Abs(localPosition.x) > queryHalfSize.x || Mathf.Abs(localPosition.z) > queryHalfSize.y)
        {
            return Vector3.zero;
        }

        return GetWhirlpoolFlowVelocity(new Vector2(worldPosition.x, worldPosition.z));
    }

    // Samples the actual currently rendered generated water mesh at a local-space XZ position.
    // Called by buoyancy so forces line up with the same triangle surface the player sees on screen.
    bool TrySampleRenderedGeneratedPlane(Vector3 localPosition, out float localSurfaceHeight, out Vector3 localSurfaceNormal)
    {
        int safeResolution = Mathf.Max(2, resolution);
        if (sourceAnimatedVertices == null
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
        int cellIndex = cellX + cellZ * Mathf.Max(safeResolution - 1, 1);
        if (sourceGeneratedCellActivity != null
            && sourceGeneratedCellActivity.Length > 0
            && (cellIndex < 0 || cellIndex >= sourceGeneratedCellActivity.Length || !sourceGeneratedCellActivity[cellIndex]))
        {
            localSurfaceHeight = 0f;
            localSurfaceNormal = Vector3.up;
            return false;
        }

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
        Vector3 worldAnchor = transform.TransformPoint(sourceVertex);
        Vector2 worldAnchorXZ = new Vector2(worldAnchor.x, worldAnchor.z);
        World.WaveDistanceProfile waveProfile = GetWaveDistanceProfile(worldAnchorXZ);
        int waveCount = GetActiveWaveCount();

        for (int i = 0; i < waves.Length; i++)
        {
            ApplyGerstnerWave(ref surfacePoint, worldAnchorXZ, timeSeconds, waves[i], waveCount, waveProfile);
        }

        ApplyWhirlpoolFeatures(ref surfacePoint, timeSeconds);
        return surfacePoint;
    }

    // Adds one Gerstner wave contribution into a displaced surface point.
    // Called by EvaluateSurfacePoint while iterating over the configurable wave list.
    void ApplyGerstnerWave(
        ref Vector3 surfacePoint,
        Vector2 worldAnchorXZ,
        float timeSeconds,
        GerstnerWave wave,
        int totalWaveCount,
        World.WaveDistanceProfile waveProfile)
    {
        float effectiveAmplitude = wave.amplitude * Mathf.Max(waveProfile.heightMultiplier, 0f);
        if (effectiveAmplitude <= 0f)
        {
            return;
        }

        Vector2 normalizedDirection = NormalizeDirection(wave.direction);
        float effectiveWaveLength = wave.waveLength * Mathf.Max(waveProfile.lengthMultiplier, 0.0001f);
        float safeWaveLength = Mathf.Max(0.001f, effectiveWaveLength);
        float waveNumber = Mathf.PI * 2f / safeWaveLength;
        float clampedSteepness = Mathf.Clamp01(wave.steepness);
        float horizontalFactor = Mathf.Min(
            1f,
            clampedSteepness / Mathf.Max(waveNumber * effectiveAmplitude * Mathf.Max(totalWaveCount, 1), 0.0001f));
        float phase = waveNumber * Vector2.Dot(normalizedDirection, worldAnchorXZ) + timeSeconds * wave.speed;
        float cosine = Mathf.Cos(phase);
        float sine = Mathf.Sin(phase);

        surfacePoint.x += normalizedDirection.x * (horizontalFactor * effectiveAmplitude * cosine);
        surfacePoint.z += normalizedDirection.y * (horizontalFactor * effectiveAmplitude * cosine);
        surfacePoint.y += effectiveAmplitude * sine;
    }

    // Adds world-space whirlpool depth and swirl offsets after the wave displacement is applied.
    void ApplyWhirlpoolFeatures(ref Vector3 surfacePoint, float timeSeconds)
    {
        if (whirlpools == null || whirlpools.Length == 0)
        {
            return;
        }

        Matrix4x4 localToWorld = transform.localToWorldMatrix;
        Matrix4x4 worldToLocal = transform.worldToLocalMatrix;
        Vector3 worldPoint = localToWorld.MultiplyPoint3x4(surfacePoint);
        Vector3 whirlpoolDisplacement = GetWhirlpoolSurfaceDisplacement(worldPoint, timeSeconds);
        if (whirlpoolDisplacement.sqrMagnitude <= 0.000000000001f)
        {
            return;
        }

        surfacePoint += worldToLocal.MultiplyVector(whirlpoolDisplacement);
    }

    Vector3 GetWhirlpoolSurfaceDisplacement(Vector3 worldPoint, float timeSeconds)
    {
        Vector3 totalDisplacement = Vector3.zero;
        Vector2 worldXZ = new Vector2(worldPoint.x, worldPoint.z);
        WhirlpoolFeature[] activeWhirlpools = whirlpools ?? Array.Empty<WhirlpoolFeature>();

        for (int i = 0; i < activeWhirlpools.Length; i++)
        {
            totalDisplacement += EvaluateWhirlpoolSurfaceDisplacement(activeWhirlpools[i], worldXZ, timeSeconds);
        }

        return totalDisplacement;
    }

    Vector3 EvaluateWhirlpoolSurfaceDisplacement(WhirlpoolFeature whirlpool, Vector2 worldXZ, float timeSeconds)
    {
        float distance = Vector2.Distance(worldXZ, whirlpool.centerXZ);
        if (distance >= whirlpool.radius)
        {
            return Vector3.zero;
        }

        float falloff = GetWhirlpoolFalloff(distance, whirlpool.radius);
        Vector3 displacement = new Vector3(0f, -whirlpool.depth * falloff, 0f);
        if (distance <= MinWhirlpoolDirectionDistance)
        {
            return displacement;
        }

        Vector2 radialDirection = (worldXZ - whirlpool.centerXZ) / distance;
        Vector2 tangentDirection = new Vector2(-radialDirection.y, radialDirection.x);
        float phase = (Mathf.Atan2(radialDirection.y, radialDirection.x) * WhirlpoolVisualSpiralTurns)
            - (timeSeconds * whirlpool.spinSpeed * WhirlpoolVisualSpinSpeedScale);

        float tangentialAmplitude = ComputeWhirlpoolVisualSpinAmplitude(whirlpool) * falloff;
        float radialAmplitude = ComputeWhirlpoolVisualPullAmplitude(whirlpool) * falloff;
        Vector2 visualOffset = (tangentDirection * (Mathf.Sin(phase) * tangentialAmplitude))
            + ((-radialDirection) * (Mathf.Cos(phase) * radialAmplitude));

        displacement.x += visualOffset.x;
        displacement.z += visualOffset.y;
        return displacement;
    }

    Vector3 GetWhirlpoolFlowVelocity(Vector2 worldXZ)
    {
        Vector3 totalVelocity = Vector3.zero;
        WhirlpoolFeature[] activeWhirlpools = whirlpools ?? Array.Empty<WhirlpoolFeature>();

        for (int i = 0; i < activeWhirlpools.Length; i++)
        {
            totalVelocity += EvaluateWhirlpoolFlowVelocity(activeWhirlpools[i], worldXZ);
        }

        return totalVelocity;
    }

    Vector3 EvaluateWhirlpoolFlowVelocity(WhirlpoolFeature whirlpool, Vector2 worldXZ)
    {
        float distance = Vector2.Distance(worldXZ, whirlpool.centerXZ);
        if (distance <= MinWhirlpoolDirectionDistance || distance >= whirlpool.radius)
        {
            return Vector3.zero;
        }

        float falloff = GetWhirlpoolFalloff(distance, whirlpool.radius);
        Vector2 radialDirection = (worldXZ - whirlpool.centerXZ) / distance;
        Vector2 tangentDirection = new Vector2(-radialDirection.y, radialDirection.x);
        Vector2 planarVelocity = (tangentDirection * (whirlpool.spinSpeed * falloff))
            + ((-radialDirection) * (whirlpool.pullStrength * falloff));
        return new Vector3(planarVelocity.x, 0f, planarVelocity.y);
    }

    float GetWhirlpoolFalloff(float distance, float radius)
    {
        return Mathf.SmoothStep(0f, 1f, 1f - Mathf.Clamp01(distance / Mathf.Max(radius, MinWhirlpoolRadius)));
    }

    float ComputeWhirlpoolVisualSpinAmplitude(WhirlpoolFeature whirlpool)
    {
        float amplitude = Mathf.Abs(whirlpool.spinSpeed) * (0.03f + (whirlpool.depth * WhirlpoolVisualDepthScale));
        return Mathf.Min(amplitude, whirlpool.radius * WhirlpoolMaxVisualAmplitudeRatio);
    }

    float ComputeWhirlpoolVisualPullAmplitude(WhirlpoolFeature whirlpool)
    {
        float amplitude = whirlpool.pullStrength * WhirlpoolVisualPullScale;
        return Mathf.Min(amplitude, whirlpool.radius * (WhirlpoolMaxVisualAmplitudeRatio * 0.6f));
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
        World.WaveDistanceProfile waveProfile = GetMaximumWaveDistanceProfile();

        for (int i = 0; i < waves.Length; i++)
        {
            GerstnerWave wave = waves[i];
            maxDisplacement += ComputeWaveHorizontalDisplacement(
                wave.amplitude * waveProfile.heightMultiplier,
                wave.waveLength * waveProfile.lengthMultiplier,
                wave.steepness,
                waveCount);
        }

        maxDisplacement += ComputeMaxWhirlpoolHorizontalDisplacement();
        return maxDisplacement;
    }

    float ComputeMaxWhirlpoolHorizontalDisplacement()
    {
        float maxDisplacement = 0f;
        WhirlpoolFeature[] activeWhirlpools = whirlpools ?? Array.Empty<WhirlpoolFeature>();

        for (int i = 0; i < activeWhirlpools.Length; i++)
        {
            WhirlpoolFeature whirlpool = activeWhirlpools[i];
            maxDisplacement += ComputeWhirlpoolVisualSpinAmplitude(whirlpool)
                + ComputeWhirlpoolVisualPullAmplitude(whirlpool);
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

    World.WaveDistanceProfile GetWaveDistanceProfile(Vector2 worldAnchorXZ)
    {
        if (World.Instance == null)
        {
            return new World.WaveDistanceProfile(1f, 1f, 0f);
        }

        return World.Instance.GetWaveDistanceProfile(new Vector3(worldAnchorXZ.x, sampledBaseHeight, worldAnchorXZ.y));
    }

    World.WaveDistanceProfile GetMaximumWaveDistanceProfile()
    {
        if (World.Instance == null)
        {
            return new World.WaveDistanceProfile(1f, 1f, 0f);
        }

        return World.Instance.GetMaximumWaveDistanceProfile();
    }

    // Computes the total possible vertical wave excursion for bounds padding.
    // Called while rebuilding runtime mesh bounds.
    float ComputeCombinedVerticalVariation()
    {
        float totalAmplitude = 0f;
        World.WaveDistanceProfile waveProfile = GetMaximumWaveDistanceProfile();

        for (int i = 0; i < waves.Length; i++)
        {
            totalAmplitude += Mathf.Max(0f, waves[i].amplitude * waveProfile.heightMultiplier);
        }

        float maxWhirlpoolDepth = 0f;
        WhirlpoolFeature[] activeWhirlpools = whirlpools ?? Array.Empty<WhirlpoolFeature>();
        for (int i = 0; i < activeWhirlpools.Length; i++)
        {
            maxWhirlpoolDepth = Mathf.Max(maxWhirlpoolDepth, Mathf.Max(0f, activeWhirlpools[i].depth));
        }

        return totalAmplitude + maxWhirlpoolDepth;
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

    // Rebuilds the flat-shaded normals and the vertex-color whitecap mask consumed by the water shader.
    // Called after creating the flat mesh and after each animated vertex update.
    void UpdateFlatSurfaceData(Vector3[] vertices)
    {
        using (new ProfileScope(UpdateFlatSurfaceDataSampleName))
        {
        if (flatNormals == null || flatNormals.Length != vertices.Length)
        {
            flatNormals = new Vector3[vertices.Length];
        }

        if (runtimeColors == null || runtimeColors.Length != vertices.Length)
        {
            runtimeColors = new Color[vertices.Length];
        }

        if (sourceAnimatedVertices == null || sourceAnimatedVertices.Length == 0)
        {
            runtimeMesh.normals = flatNormals;
            runtimeMesh.colors = runtimeColors;
            return;
        }

        bool useWhitecaps = enableWhitecaps;
        if (useWhitecaps)
        {
            if (sourceWhitecapNormalSums == null || sourceWhitecapNormalSums.Length != sourceAnimatedVertices.Length)
            {
                sourceWhitecapNormalSums = new Vector3[sourceAnimatedVertices.Length];
            }

            if (sourceWhitecapMasks == null || sourceWhitecapMasks.Length != sourceAnimatedVertices.Length)
            {
                sourceWhitecapMasks = new float[sourceAnimatedVertices.Length];
            }

            if (sourceWhitecapNormalCounts == null || sourceWhitecapNormalCounts.Length != sourceAnimatedVertices.Length)
            {
                sourceWhitecapNormalCounts = new int[sourceAnimatedVertices.Length];
            }

            if (sourceWhitecapCreaseAngles == null || sourceWhitecapCreaseAngles.Length != sourceAnimatedVertices.Length)
            {
                sourceWhitecapCreaseAngles = new float[sourceAnimatedVertices.Length];
            }

            Array.Clear(sourceWhitecapNormalSums, 0, sourceWhitecapNormalSums.Length);
            Array.Clear(sourceWhitecapNormalCounts, 0, sourceWhitecapNormalCounts.Length);
            Array.Clear(sourceWhitecapCreaseAngles, 0, sourceWhitecapCreaseAngles.Length);
            Array.Clear(sourceWhitecapMasks, 0, sourceWhitecapMasks.Length);
        }

        float baseHeight = sampledBaseHeight;
        float creaseBlendRange = Mathf.Max(whitecapCreaseBlendAngle, MinWhitecapCreaseRange);
        int safeStride = Mathf.Max(1, whitecapTriangleStride);

        for (int triangleIndex = 0; triangleIndex < flatTriangles.Length; triangleIndex += 3)
        {
            int a = flatTriangles[triangleIndex];
            int b = flatTriangles[triangleIndex + 1];
            int c = flatTriangles[triangleIndex + 2];
            int sourceA = runtimeToSourceIndex[a];
            int sourceB = runtimeToSourceIndex[b];
            int sourceC = runtimeToSourceIndex[c];

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

            if (useWhitecaps && triangleIndex % (safeStride * 3) == 0)
            {
                sourceWhitecapNormalSums[sourceA] += normal;
                sourceWhitecapNormalSums[sourceB] += normal;
                sourceWhitecapNormalSums[sourceC] += normal;
                sourceWhitecapNormalCounts[sourceA]++;
                sourceWhitecapNormalCounts[sourceB]++;
                sourceWhitecapNormalCounts[sourceC]++;
            }
        }

        if (useWhitecaps)
        {
            for (int triangleIndex = 0; triangleIndex < flatTriangles.Length; triangleIndex += 3)
            {
                if (triangleIndex % (safeStride * 3) != 0)
                {
                    continue;
                }

                int a = flatTriangles[triangleIndex];
                int b = flatTriangles[triangleIndex + 1];
                int c = flatTriangles[triangleIndex + 2];
                Vector3 normal = flatNormals[a];
                UpdateWhitecapCreaseAngle(runtimeToSourceIndex[a], normal);
                UpdateWhitecapCreaseAngle(runtimeToSourceIndex[b], normal);
                UpdateWhitecapCreaseAngle(runtimeToSourceIndex[c], normal);
            }

            for (int sourceIndex = 0; sourceIndex < sourceAnimatedVertices.Length; sourceIndex++)
            {
                if (sourceWhitecapNormalCounts[sourceIndex] <= 1)
                {
                    continue;
                }

                float creaseFactor = Mathf.Clamp01((sourceWhitecapCreaseAngles[sourceIndex] - whitecapCreaseAngle) / creaseBlendRange);
                float crestHeight = sourceAnimatedVertices[sourceIndex].y - baseHeight;
                float heightFactor = crestHeight <= whitecapHeightThreshold
                    ? 0f
                    : Mathf.Clamp01((crestHeight - whitecapHeightThreshold) / Mathf.Max(whitecapHeightThreshold, 0.0001f));
                sourceWhitecapMasks[sourceIndex] = Mathf.Clamp01(heightFactor * creaseFactor * whitecapStrength);
            }
        }

        for (int runtimeIndex = 0; runtimeIndex < vertices.Length; runtimeIndex++)
        {
            int sourceIndex = runtimeToSourceIndex[runtimeIndex];
            float whitecapMask = useWhitecaps && sourceIndex >= 0 && sourceWhitecapMasks != null && sourceIndex < sourceWhitecapMasks.Length
                ? sourceWhitecapMasks[sourceIndex]
                : 0f;
            runtimeColors[runtimeIndex] = new Color(1f, 1f, 1f, whitecapMask);
        }

        runtimeMesh.normals = flatNormals;
        runtimeMesh.colors = runtimeColors;
        }
    }

    void UpdateWhitecapCreaseAngle(int sourceIndex, Vector3 faceNormal)
    {
        if (sourceIndex < 0
            || sourceWhitecapNormalCounts == null
            || sourceWhitecapNormalSums == null
            || sourceWhitecapCreaseAngles == null
            || sourceIndex >= sourceWhitecapNormalCounts.Length
            || sourceIndex >= sourceWhitecapNormalSums.Length
            || sourceIndex >= sourceWhitecapCreaseAngles.Length
            || sourceWhitecapNormalCounts[sourceIndex] <= 1)
        {
            return;
        }

        Vector3 averageNormal = sourceWhitecapNormalSums[sourceIndex].normalized;
        if (averageNormal.sqrMagnitude <= 0.000001f)
        {
            return;
        }

        // The average normal sits roughly between neighboring faces, so doubling this deviation
        // gives a reasonable estimate of the local crease angle at the shared vertex.
        float creaseAngle = Vector3.Angle(faceNormal, averageNormal) * 2f;
        sourceWhitecapCreaseAngles[sourceIndex] = Mathf.Max(sourceWhitecapCreaseAngles[sourceIndex], creaseAngle);
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
        boundsSize.y = Mathf.Max(boundsSize.y, 0.02f) + ComputeCombinedVerticalVariation() * 2f;
        runtimeBounds = new Bounds(sourceBounds.center, boundsSize);
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.05f, 0.85f, 1f, 0.9f);
        WhirlpoolFeature[] activeWhirlpools = whirlpools ?? Array.Empty<WhirlpoolFeature>();

        for (int i = 0; i < activeWhirlpools.Length; i++)
        {
            WhirlpoolFeature whirlpool = activeWhirlpools[i];
            DrawCircleXZ(whirlpool.centerXZ, whirlpool.radius);

            Vector3 center = new Vector3(whirlpool.centerXZ.x, transform.position.y, whirlpool.centerXZ.y);
            Gizmos.DrawLine(center, center + (Vector3.down * Mathf.Max(whirlpool.depth, 0.05f)));

            if (Mathf.Abs(whirlpool.spinSpeed) > 0.0001f)
            {
                float markerRadius = Mathf.Max(whirlpool.radius * 0.45f, 0.25f);
                Vector3 tangentStart = center + (Vector3.right * markerRadius);
                Vector3 tangentEnd = tangentStart + (Vector3.forward * Mathf.Sign(whirlpool.spinSpeed) * markerRadius * 0.45f);
                Gizmos.DrawLine(center, tangentStart);
                Gizmos.DrawLine(tangentStart, tangentEnd);
            }

            if (whirlpool.pullStrength > 0f)
            {
                Vector3 pullStart = center + (Vector3.forward * Mathf.Max(whirlpool.radius * 0.55f, 0.3f));
                Gizmos.DrawLine(pullStart, center);
            }
        }
    }

    void DrawCircleXZ(Vector2 centerXZ, float radius)
    {
        float safeRadius = Mathf.Max(radius, MinWhirlpoolRadius);
        Vector3 previousPoint = new Vector3(centerXZ.x + safeRadius, transform.position.y, centerXZ.y);

        for (int segmentIndex = 1; segmentIndex <= GizmoCircleSegments; segmentIndex++)
        {
            float angle = (segmentIndex / (float)GizmoCircleSegments) * Mathf.PI * 2f;
            Vector3 nextPoint = new Vector3(
                centerXZ.x + (Mathf.Cos(angle) * safeRadius),
                transform.position.y,
                centerXZ.y + (Mathf.Sin(angle) * safeRadius));
            Gizmos.DrawLine(previousPoint, nextPoint);
            previousPoint = nextPoint;
        }
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
