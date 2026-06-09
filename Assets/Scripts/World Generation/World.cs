using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Serialization;
#if UNITY_EDITOR
using UnityEditor;
#endif

[ExecuteAlways]
/// <summary>
/// Single source of truth for the generated world.
/// All user-facing tuning lives here, and this component pushes those settings
/// into the generated water, hazards, floaters, and border wall.
/// </summary>
public class World : MonoBehaviour
{
    public enum HazardBiome
    {
        Ice,
        Forest,
        Volcanic
    }

    const string GroundObjectName = "Ground";
    const string WaterObjectName = "Water";
    const string GeneratedChunksRootName = "GeneratedChunks";
    const string GeneratedHazardsRootName = "GeneratedHazards";
    const string GeneratedEffectsRootName = "GeneratedEffects";
    const string GeneratedObjectiveObstaclesRootName = "GeneratedObjectiveObstacles";
    const string FinalBorderObjectiveId = "ice_wall";
    const string FinalBorderObjectiveName = "Ice Wall";
    const float MinChunkSize = 10f;
    const float MinTerrainSampleSize = 0.0001f;
    const float MinHazardSpacing = 0.001f;
    const float HazardCandidatePadding = 8f;
    const float HazardAdditionalSinkDepth = 10f;
    static readonly HazardBiome[] InteriorBiomeOrder =
    {
        HazardBiome.Ice,
        HazardBiome.Forest,
        HazardBiome.Volcanic
    };
    const string WorldUpdateSampleName = "World.Update";
    const string SyncGeneratedContentSampleName = "World.SyncGeneratedContent";
    const string ProcessPendingChunkBuildsSampleName = "World.ProcessPendingChunkBuilds";
    const string CreateGeneratedChunkSampleName = "World.CreateGeneratedChunk";
    const string SpawnChunkHazardsSampleName = "World.SpawnChunkHazards";
    const string SpawnChunkFloatingObjectsSampleName = "World.SpawnChunkFloatingObjects";

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
    public struct WeightedPrefab
    {
        public GameObject prefab;
        [Min(0.01f)] public float weight;
    }

    [Serializable]
    public struct ObstacleTargetDefinition
    {
        public string displayName;
        public GameObject prefab;
        public Vector3 localPosition;
        public Vector3 localEulerAngles;
        public Sprite compassIcon;
        public int bonusGold;
    }

    [Serializable]
    public struct BiomeContentSettings
    {
        public WeightedPrefab[] hazardPrefabs;
        public WeightedPrefab[] floatingPrefabs;
        public Vector2 scaleRange;
        [Range(0f, 1f)] public float biomeWeight;
        [Min(0f)] public float hazardDensity;
        [Min(0.1f)] public float hazardCountMultiplier;
        [Min(0.1f)] public float hazardSpacingMultiplier;
        [Min(1)] public int maxHazardsPerChunk;
        [Min(1f)] public float minHazardSpacing;
        [Tooltip("Probability that a valid biome-specific floating-object spawn opportunity turns into a spawned object.")]
        [Range(0f, 1f)] public float floatingDensity;
    }

    [Serializable]
    public struct OpenWorldSettings
    {
        [Header("World")]
        public int worldSeed;

        [Header("Chunks")]
        public float chunkSize;
        public int chunkResolution;

        [Header("Difficulty")]
        public float borderStartDistance;
        public float borderThickness;
        public float interiorHazardStartDistance;
        public float hazardSpawnExclusionRadius;

        [Header("Border Wall")]
        public bool renderBorderWall;
        public float borderWallHeight;
        public float borderWallSubmergedDepth;
        public float borderWallEdgeBlendFraction;
        public float borderWallTopNoiseHeight;
        public float borderWallRadiusNoise;
        public float borderWallNoiseScale;

        [Header("Wave Distance Scaling")]
        public float maxWaveHeightMultiplier;
        public float maxWaveLengthMultiplier;
        public float waveStrengthRangeMin;
        public float waveStrengthRangeMax;
        public float waveStrengthFadePower;

        [Header("Hazards")]
        public BiomeContentSettings iceBiome;
        public BiomeContentSettings forestBiome;
        public BiomeContentSettings volcanicBiome;
        public float biomePatchScale;
        public float biomePatchSizeNoiseScale;
        public Vector2 biomePatchScaleMultiplierRange;
        public float biomeDensityPatchScale;
        public float biomeDensityMin;
        public float biomeDensityMax;
        public float distanceDensityBias;

        [Header("Floating Objects")]
        public WeightedPrefab[] generalFloatingPrefabs;
        public float generalFloatingObjectDensity;
        public int generalFloatingObjectsPerChunk;
        public int biomeFloatingObjectsPerChunk;
        public float floatingObjectMinSpacing;
    }

    public struct WaveDistanceProfile
    {
        public float heightMultiplier;
        public float lengthMultiplier;
        public float influence01;

        public WaveDistanceProfile(float heightMultiplier, float lengthMultiplier, float influence01)
        {
            this.heightMultiplier = heightMultiplier;
            this.lengthMultiplier = lengthMultiplier;
            this.influence01 = influence01;
        }
    }

    public struct ObstacleTrackingInfo
    {
        public VoyageObstacle obstacle;
        public string obstacleId;
        public string obstacleName;
        public bool isBorderWallTarget;
        public Sprite compassIcon;
        public Vector3 worldPosition;
        public Quaternion worldRotation;
        public Vector3 planarDirection;
        public float planarDistance;
        public float signedAngleFromReference;
    }

    struct WaveDistanceSettings
    {
        public float maxWaveHeightMultiplier;
        public float maxWaveLengthMultiplier;
        public float waveStrengthRangeMin;
        public float waveStrengthRangeMax;
        public float waveStrengthFadePower;
    }

    // Runtime state for one generated chunk. Each chunk owns its water mesh,
    // optional border wall/underside meshes, and spawned child objects.
    sealed class GeneratedChunkState
    {
        public Vector2Int coord;
        public GameObject chunkObject;
        public Transform hazardRoot;
        public Transform floatingRoot;
        public bool floatingObjectsSpawned;
        public GameObject waterObject;
        public MeshFilter waterMeshFilter;
        public MeshRenderer waterMeshRenderer;
        public UrpLowPolyWater waterSurface;
        public Mesh waterSourceMesh;
        public GameObject borderWallObject;
        public MeshFilter borderWallMeshFilter;
        public MeshRenderer borderWallMeshRenderer;
        public MeshCollider borderWallMeshCollider;
        public Mesh borderWallMesh;
        public GameObject undersideObject;
        public MeshFilter undersideMeshFilter;
        public MeshRenderer undersideMeshRenderer;
        public Mesh undersideMesh;
        public int presentationHash;
    }

    // Shared streaming state used by both play mode and editor preview.
    sealed class GeneratedWorldStreamState
    {
        public readonly Dictionary<Vector2Int, GeneratedChunkState> generatedChunks = new Dictionary<Vector2Int, GeneratedChunkState>();
        public readonly Queue<Vector2Int> pendingChunkBuildQueue = new Queue<Vector2Int>();
        public readonly HashSet<Vector2Int> pendingChunkBuildSet = new HashSet<Vector2Int>();

        public OpenWorldSettings settings;
        public Transform generatedChunksRoot;
        public Transform generatedHazardsRoot;
        public Transform generatedEffectsRoot;
        public Vector3 spawnPoint;
        public bool spawnPointCaptured;
        public bool configured;
        public int settingsHash;
        public int lastLoadedChunkRadius = -1;
        public Vector2Int centerChunkCoord;
        public bool centerChunkCoordValid;
    }

    [FormerlySerializedAs("resolution")]
    [Tooltip("Single render detail used for the streamed water, wall, and underside chunk geometry.")]
    [SerializeField, Range(2, 256)] int surfaceResolution = 32;

    [Header("Water")]
    public bool renderWater = true;

    [SerializeField] float waterHeight = 0.05f;

    [SerializeField]
    Material waterMaterial;

    [Header("Water Surface")]
    [SerializeField] UrpLowPolyWater.GerstnerWave[] waterWaves =
    {
        new UrpLowPolyWater.GerstnerWave(new Vector2(1f, 0.35f), 0.35f, 4f, 1.25f, 0.35f),
        new UrpLowPolyWater.GerstnerWave(new Vector2(-0.55f, 0.85f), 0.18f, 2.25f, 1.7f, 0.2f)
    };
    [SerializeField] UrpLowPolyWater.WhirlpoolFeature[] waterWhirlpools = Array.Empty<UrpLowPolyWater.WhirlpoolFeature>();
    [SerializeField] bool enableWaterWhitecaps;
    [SerializeField, Min(0f)] float whitecapHeightThreshold = 0.2f;
    [SerializeField, Range(0f, 180f)] float whitecapCreaseAngle = 20f;
    [SerializeField, Range(1, 16)] int whitecapTriangleStride = 2;
    [SerializeField, Min(0f)] float whitecapCreaseBlendAngle = 12f;
    [SerializeField, Range(0f, 1f)] float whitecapStrength = 1f;
    [Header("Water Distance Scaling")]
    [FormerlySerializedAs("maxWaveStrengthMultiplier")]
    [Tooltip("Maximum height multiplier applied to waves once the outer wave range is fully reached.")]
    [SerializeField, Min(1f)] float maxWaveHeightMultiplier = 2.35f;
    [Tooltip("Maximum wavelength multiplier applied to waves once the outer wave range is fully reached.")]
    [SerializeField, Min(1f)] float maxWaveLengthMultiplier = 1.75f;
    [Tooltip("Distance from the world origin where wave scaling begins.")]
    [SerializeField, Min(0f)] float waveStrengthRangeMin = 0f;
    [Tooltip("Distance from the world origin where wave scaling reaches its full multiplier.")]
    [SerializeField, Min(0f)] float waveStrengthRangeMax = 5000f;
    [Tooltip("Shapes how abruptly the wave scaling fades in across the configured distance range. 1 keeps the default smooth fade, higher values push the boost farther out.")]
    [SerializeField, Min(0.1f)] float waveStrengthFadePower = 1f;

    [Header("Scene References")]
    [SerializeField] Transform boatRoot;
    [SerializeField] string boatRootName = "BoatParent";
    [SerializeField] Transform generatedChunksRoot;
    [SerializeField] Transform generatedHazardsRoot;
    [SerializeField] Transform generatedEffectsRoot;
    [SerializeField] Transform generatedObjectiveObstaclesRoot;

    [Header("Open World")]
    [SerializeField] int worldSeed = 457;
    [Header("Open World Chunks")]
    [Tooltip("Physical world-space size of each streamed world chunk.")]
    [SerializeField, Min(10f)] float chunkSize = 140f;
    [FormerlySerializedAs("activeChunkRadius")]
    [FormerlySerializedAs("renderChunkRadius")]
    [FormerlySerializedAs("legacyRenderChunkRadius")]
    [FormerlySerializedAs("manualRenderChunkRadius")]
    [Tooltip("How many chunks are visible in each direction from the center chunk.")]
    [SerializeField, Min(0)] int visibleChunkRadius = 5;
    [Tooltip("Extra chunk rings kept loaded beyond the visible radius.")]
    [SerializeField, Min(0)] int preloadChunkBuffer = 1;

    [Header("Editor Preview")]
    [SerializeField] bool previewGenerationInEditor = true;
    [SerializeField, Min(1)] int maxEditorPreviewChunkBuildsPerRefresh = 4;

    [Header("Open World Difficulty")]
    [SerializeField] float borderStartDistance = 5000f;
    [SerializeField] float borderThickness = 260f;
    [FormerlySerializedAs("interiorIcebergStartDistance")]
    [SerializeField] float interiorHazardStartDistance = 0f;
    [SerializeField] float hazardSpawnExclusionRadius = 32f;
    [Header("Border Wall")]
    [SerializeField] bool renderBorderWall = true;
    [SerializeField] Material borderWallMaterial;
    [SerializeField, Min(1f)] float borderWallHeight = 72f;
    [SerializeField, Min(1f)] float borderWallSubmergedDepth = 32f;
    [SerializeField, Range(0.02f, 0.49f)] float borderWallEdgeBlendFraction = 0.16f;
    [SerializeField, Min(0f)] float borderWallTopNoiseHeight = 8f;
    [SerializeField, Min(0f)] float borderWallRadiusNoise = 10f;
    [SerializeField, Min(0.0001f)] float borderWallNoiseScale = 0.0035f;

    [Header("Open World Hazards")]
    [FormerlySerializedAs("icePrefabs")]
    [FormerlySerializedAs("icebergPrefabs")]
    [SerializeField] BiomeContentSettings iceBiome = new BiomeContentSettings
    {
        scaleRange = new Vector2(0.85f, 1.8f),
        biomeWeight = 0.4f,
        hazardDensity = 1f,
        hazardCountMultiplier = 1f,
        hazardSpacingMultiplier = 1f,
        maxHazardsPerChunk = 6,
        minHazardSpacing = 14f,
        floatingDensity = 1f
    };
    [FormerlySerializedAs("forestPrefabs")]
    [FormerlySerializedAs("treePrefabs")]
    [SerializeField] BiomeContentSettings forestBiome = new BiomeContentSettings
    {
        scaleRange = new Vector2(3f, 5f),
        biomeWeight = 0.25f,
        hazardDensity = 1.15f,
        hazardCountMultiplier = 1.15f,
        hazardSpacingMultiplier = 0.8f,
        maxHazardsPerChunk = 14,
        minHazardSpacing = 6f,
        floatingDensity = 1f
    };
    [FormerlySerializedAs("volcanicPrefabs")]
    [FormerlySerializedAs("magmaPrefabs")]
    [SerializeField] BiomeContentSettings volcanicBiome = new BiomeContentSettings
    {
        scaleRange = new Vector2(0.85f, 1.8f),
        biomeWeight = 0.35f,
        hazardDensity = 1f,
        hazardCountMultiplier = 1f,
        hazardSpacingMultiplier = 1f,
        maxHazardsPerChunk = 6,
        minHazardSpacing = 14f,
        floatingDensity = 1f
    };
    [SerializeField] float biomePatchScale = 0.0016f;
    [SerializeField] float biomePatchSizeNoiseScale = 0.0008f;
    [SerializeField] Vector2 biomePatchScaleMultiplierRange = new Vector2(0.75f, 1.35f);
    [SerializeField] float biomeDensityPatchScale = 0.0024f;
    [SerializeField, Range(0f, 1f)] float biomeDensityMin = 0.7f;
    [SerializeField, Range(0f, 1f)] float biomeDensityMax = 0.72f;
    [SerializeField, Range(0f, 1f)] float distanceDensityBias = 0.35f;

    [Header("Floating Objects")]
    [SerializeField] WeightedPrefab[] generalFloatingPrefabs = Array.Empty<WeightedPrefab>();
    [Tooltip("Probability that a valid general floating-object spawn opportunity turns into a spawned object.")]
    [SerializeField, Range(0f, 1f)] float generalFloatingObjectDensity = 1f;
    [Tooltip("Maximum number of general floating-object spawn opportunities per chunk.")]
    [SerializeField, Min(0)] int generalFloatingObjectsPerChunk = 1;
    [Tooltip("Maximum number of biome-specific floating-object spawn opportunities per chunk.")]
    [SerializeField, Min(0)] int biomeFloatingObjectsPerChunk = 1;
    [SerializeField, Min(1f)] float floatingObjectMinSpacing = 18f;

    [Header("Objective Obstacles")]
    [SerializeField] bool previewObstacleTargetsInEditor = true;
    [SerializeField] ObstacleTargetDefinition[] obstacleTargets = Array.Empty<ObstacleTargetDefinition>();
    [SerializeField] Sprite finalBorderObjectiveCompassIcon;

    [Header("Atmosphere")]
    [SerializeField] bool enableDistanceFog = true;
    [SerializeField] Color calmFogColor = new Color(0.76f, 0.82f, 0.88f, 1f);
    [SerializeField] Color outerFogColor = new Color(0.86f, 0.92f, 0.96f, 1f);
    [SerializeField, Range(0.1f, 0.95f)] float calmFogStartFraction = 0.62f;
    [SerializeField, Range(0.1f, 0.99f)] float calmFogEndFraction = 0.94f;
    [SerializeField, Range(0.05f, 0.9f)] float outerFogStartFraction = 0.42f;
    [SerializeField, Range(0.1f, 0.98f)] float outerFogEndFraction = 0.78f;

    [Header("Win")]
    [SerializeField] bool freezeTimeOnWin = true;

    [Header("Runtime Streaming")]
    [SerializeField, Min(1)] int maxChunkBuildsPerFrame = 2;

    readonly GeneratedWorldStreamState runtimeStreamState = new GeneratedWorldStreamState();
    readonly GeneratedWorldStreamState editorPreviewState = new GeneratedWorldStreamState();
    readonly List<VoyageObstacle> spawnedObjectiveObstacles = new List<VoyageObstacle>();
    static readonly HashSet<string> destroyedObjectiveObstacleIds = new HashSet<string>(StringComparer.Ordinal);
    static int currentSpawnObjectiveIndex;

    bool hasWon;
    float nextBoatSearchTime;
    bool suppressObstacleDestructionReporting;
    bool runtimeObjectiveObstacleSyncPending;
#if UNITY_EDITOR
    bool editorPreviewRefreshQueued;
#endif
    bool editorPreviewActive;
    public static World Instance { get; private set; }
    public Transform BoatRoot => boatRoot;
    public bool HasWon => hasWon;
    internal bool SuppressObstacleDestructionReporting => suppressObstacleDestructionReporting;

    void OnEnable()
    {
        Instance = this;
        ResolveSceneReferences();
        Rebuild();

        if (Application.isPlaying)
        {
            QueueRuntimeObjectiveObstacleSync();
            ClearGeneratedContent(editorPreviewState, true, true);
            editorPreviewActive = false;
            return;
        }

        QueueEditorPreviewRefresh();
    }

    void Start()
    {
        if (!Application.isPlaying)
        {
            return;
        }

        ProcessPendingRuntimeObjectiveObstacleSync();
    }

    void OnDisable()
    {
        if (Instance == this)
        {
            Instance = null;
        }

#if UNITY_EDITOR
        editorPreviewRefreshQueued = false;
        EditorApplication.delayCall -= RunQueuedEditorPreviewRefresh;
#endif

        if (Application.isPlaying)
        {
            ClearObjectiveObstacles(false);
            return;
        }

        ClearObjectiveObstacles(true);
        ClearGeneratedContent(editorPreviewState, true, true);
        ClearPreviewGeneratedChildren(generatedObjectiveObstaclesRoot);
        ClearPreviewGeneratedChildren(transform.Find(GeneratedObjectiveObstaclesRootName));
        editorPreviewActive = false;
    }

    void OnValidate()
    {
        ResolveSceneReferences();
        Rebuild();

        if (Application.isPlaying)
        {
            QueueRuntimeObjectiveObstacleSync();
            return;
        }

        QueueEditorPreviewRefresh();
    }

    // Runtime update is intentionally small: resolve the boat, sync the chunk
    // stream around it, then refresh atmosphere.
    void Update()
    {
        using (new ProfileScope(WorldUpdateSampleName))
        {
        if (!Application.isPlaying)
        {
            return;
        }

        if (generatedChunksRoot == null || generatedHazardsRoot == null || generatedEffectsRoot == null)
        {
            ResolveSceneReferences();
        }

        ProcessPendingRuntimeObjectiveObstacleSync();
        TryResolveBoatRoot();

        Vector3 anchorWorldPosition = ResolveRuntimeAnchorWorldPosition();

        SyncOpenWorldRuntime(
            CreateOpenWorldSettings(),
            anchorWorldPosition,
            GetRenderChunkRadius(),
            GetLoadedChunkRadius(),
            false,
            generatedChunksRoot,
            generatedHazardsRoot,
            generatedEffectsRoot);

        UpdateDistanceFog();
        }
    }

    /// <summary>
    /// Main runtime entry point used to keep the generated world centered on the
    /// current sailing anchor.
    /// </summary>
    public void SyncOpenWorldRuntime(
        OpenWorldSettings settings,
        Vector3 anchorWorldPosition,
        int renderChunkRadius,
        int loadedChunkRadius,
        bool resetSpawnPoint,
        Transform generatedChunksRoot,
        Transform generatedHazardsRoot,
        Transform generatedEffectsRoot)
    {
        EnsureRuntimeWorldReady();

        SyncGeneratedContent(
            runtimeStreamState,
            settings,
            anchorWorldPosition,
            Mathf.Max(renderChunkRadius, 0),
            Mathf.Max(loadedChunkRadius, Mathf.Max(renderChunkRadius, 0)),
            resetSpawnPoint,
            generatedChunksRoot,
            generatedHazardsRoot,
            generatedEffectsRoot,
            false,
            Mathf.Max(1, maxChunkBuildsPerFrame));
    }

    public void ClearRuntimeOpenWorldGeneratedContent()
    {
        ClearGeneratedContent(runtimeStreamState, Application.isPlaying ? false : true, false);
    }

    /// <summary>
    /// Uses the same generation pipeline as play mode, but limited to editor
    /// preview chunk counts so the scene remains responsive.
    /// </summary>
    public void RebuildEditorOpenWorldPreview(
        OpenWorldSettings settings,
        Vector3 anchorWorldPosition,
        int renderChunkRadius,
        Transform generatedChunksRoot,
        Transform generatedHazardsRoot,
        Transform generatedEffectsRoot)
    {
        if (Application.isPlaying)
        {
            return;
        }

        EnsureRuntimeWorldReady();
        SyncGeneratedContent(
            editorPreviewState,
            settings,
            anchorWorldPosition,
            Mathf.Max(renderChunkRadius, 0),
            Mathf.Max(renderChunkRadius, 0),
            false,
            generatedChunksRoot,
            generatedHazardsRoot,
            generatedEffectsRoot,
            true,
            Mathf.Max(1, maxEditorPreviewChunkBuildsPerRefresh));
        editorPreviewActive = editorPreviewState.generatedChunks.Count > 0;
    }

    public void ClearEditorOpenWorldPreview()
    {
        if (Application.isPlaying)
        {
            return;
        }

        ClearGeneratedContent(editorPreviewState, true, true);
        editorPreviewActive = false;
        ApplyBaseWorldPresentation();
    }

    public bool IsBoatTransform(Transform candidate)
    {
        if (candidate == null)
        {
            return false;
        }

        if (boatRoot == null)
        {
            return candidate.root.name == boatRootName;
        }

        return candidate == boatRoot
            || candidate.root == boatRoot
            || candidate.root == boatRoot.root;
    }

    public void HandleBorderCollision(GameObject borderIceberg, Transform collisionTransform)
    {
        if (hasWon || !IsBoatTransform(collisionTransform))
        {
            return;
        }

        if (!CanUseIceWallAsFinalObjective())
        {
            return;
        }

        hasWon = true;
        Debug.Log("Victory: Boat reached the ice world border via " + borderIceberg.name + ".");

        if (freezeTimeOnWin && Application.isPlaying)
        {
            Time.timeScale = 0f;
        }
    }

    public float GetDanger01(Vector3 worldPosition)
    {
        return GetOpenWorldDanger01(worldPosition);
    }

    public float GetBorderFactor(Vector3 worldPosition)
    {
        return GetOpenWorldBorderFactor(worldPosition);
    }

    public bool IsInBorderZone(Vector3 worldPosition)
    {
        return IsInOpenWorldBorderZone(worldPosition);
    }

    /// <summary>
    /// Returns the radius of the playable disc before the border wall begins.
    /// Scene helper scripts can use this to size decorative supports to the world.
    /// </summary>
    public float GetPlayableRadius()
    {
        return Mathf.Max(0f, borderStartDistance);
    }

    /// <summary>
    /// Returns how far the border wall extends below the waterline.
    /// Helpers below the map can use this as a rough underside depth reference.
    /// </summary>
    public float GetBorderWallSubmergedDepth()
    {
        return Mathf.Max(0f, borderWallSubmergedDepth);
    }

    public float GetWaveStrengthMultiplier(Vector3 worldPosition)
    {
        return GetOpenWorldWaveStrengthMultiplier(worldPosition);
    }

    public float GetWaveLengthMultiplier(Vector3 worldPosition)
    {
        return GetOpenWorldWaveLengthMultiplier(worldPosition);
    }

    public float GetMaximumWaveStrengthMultiplier()
    {
        return GetOpenWorldMaximumWaveStrengthMultiplier();
    }

    public float GetMaximumWaveLengthMultiplier()
    {
        return GetOpenWorldMaximumWaveLengthMultiplier();
    }

    public WaveDistanceProfile GetWaveDistanceProfile(Vector3 worldPosition)
    {
        return GetOpenWorldWaveDistanceProfile(worldPosition);
    }

    public WaveDistanceProfile GetMaximumWaveDistanceProfile()
    {
        return GetOpenWorldMaximumWaveDistanceProfile();
    }

    public float GetOpenWorldDanger01(Vector3 worldPosition)
    {
        if (!runtimeStreamState.configured)
        {
            return 0f;
        }

        return GetDanger01(
            worldPosition,
            runtimeStreamState.settings,
            runtimeStreamState.spawnPoint,
            runtimeStreamState.spawnPointCaptured);
    }

    public float GetOpenWorldBorderFactor(Vector3 worldPosition)
    {
        if (!runtimeStreamState.configured)
        {
            return 0f;
        }

        return GetBorderFactor(
            worldPosition,
            runtimeStreamState.settings,
            runtimeStreamState.spawnPoint,
            runtimeStreamState.spawnPointCaptured);
    }

    public bool IsInOpenWorldBorderZone(Vector3 worldPosition)
    {
        return GetOpenWorldBorderFactor(worldPosition) > 0f;
    }

    public void ResetObstacleTargets()
    {
        destroyedObjectiveObstacleIds.Clear();
        ResolveSceneReferences();

        if (Application.isPlaying)
        {
            QueueRuntimeObjectiveObstacleSync();
            ProcessPendingRuntimeObjectiveObstacleSync();
            return;
        }

        SyncObjectiveObstacles(true);
    }

    public int GetConfiguredObstacleCount()
    {
        return CountConfiguredObstacleDefinitions();
    }

    public int GetDestroyedObstacleCount()
    {
        int destroyedCount = 0;
        for (int i = 0; i < obstacleTargets.Length; i++)
        {
            if (!IsValidObstacleDefinition(obstacleTargets[i]))
            {
                continue;
            }

            if (destroyedObjectiveObstacleIds.Contains(GetObstacleId(i, obstacleTargets[i])))
            {
                destroyedCount++;
            }
        }

        return destroyedCount;
    }

    public int GetRemainingObstacleCount()
    {
        int remainingCount = 0;
        for (int i = 0; i < spawnedObjectiveObstacles.Count; i++)
        {
            VoyageObstacle obstacle = spawnedObjectiveObstacles[i];
            if (obstacle != null && obstacle.isActiveAndEnabled)
            {
                remainingCount++;
            }
        }

        return remainingCount;
    }

    public bool HasRemainingObstacles()
    {
        return GetRemainingObstacleCount() > 0;
    }

    public void SetCurrentObjectiveIndex(int index)
    {
        currentSpawnObjectiveIndex = Mathf.Max(0, index);
    }

    public bool TryGetObjectiveDefinition(int index, out ObstacleTargetDefinition definition)
    {
        definition = default;
        if (obstacleTargets == null || index < 0 || index >= obstacleTargets.Length)
        {
            return false;
        }

        if (!IsValidObstacleDefinition(obstacleTargets[index]))
        {
            return false;
        }

        definition = obstacleTargets[index];
        return true;
    }

    public bool TryGetNearestObstacleTracking(out ObstacleTrackingInfo trackingInfo)
    {
        trackingInfo = default;

        if (!TryResolveBoatRoot() || boatRoot == null)
        {
            return false;
        }

        return TryGetNearestObstacleTracking(
            boatRoot.position,
            GetBoatHeadingVector(boatRoot),
            out trackingInfo);
    }

    public bool TryGetNearestObstacleTracking(
        Vector3 referenceWorldPosition,
        Vector3 referenceForward,
        out ObstacleTrackingInfo trackingInfo)
    {
        trackingInfo = default;

        Vector3 planarForward = Vector3.ProjectOnPlane(referenceForward, Vector3.up);
        if (planarForward.sqrMagnitude <= 0.0001f)
        {
            planarForward = Vector3.forward;
        }
        else
        {
            planarForward.Normalize();
        }

        VoyageObstacle nearestObstacle = null;
        Vector3 nearestPlanarOffset = Vector3.zero;
        float nearestDistanceSqr = float.PositiveInfinity;

        for (int i = 0; i < spawnedObjectiveObstacles.Count; i++)
        {
            VoyageObstacle obstacle = spawnedObjectiveObstacles[i];
            if (obstacle == null || !obstacle.isActiveAndEnabled)
            {
                continue;
            }

            Vector3 planarOffset = Vector3.ProjectOnPlane(obstacle.transform.position - referenceWorldPosition, Vector3.up);
            float distanceSqr = planarOffset.sqrMagnitude;
            if (distanceSqr >= nearestDistanceSqr)
            {
                continue;
            }

            nearestObstacle = obstacle;
            nearestPlanarOffset = planarOffset;
            nearestDistanceSqr = distanceSqr;
        }

        if (nearestObstacle == null)
        {
            return TryGetIceWallTracking(referenceWorldPosition, planarForward, out trackingInfo);
        }

        Vector3 planarDirection = nearestDistanceSqr <= 0.0001f
            ? planarForward
            : nearestPlanarOffset / Mathf.Sqrt(nearestDistanceSqr);
        float signedAngle = Vector3.SignedAngle(planarForward, planarDirection, Vector3.up);

        trackingInfo = new ObstacleTrackingInfo
        {
            obstacle = nearestObstacle,
            obstacleId = nearestObstacle.ObstacleId,
            obstacleName = nearestObstacle.DisplayName,
            isBorderWallTarget = false,
            compassIcon = nearestObstacle.CompassIcon,
            worldPosition = nearestObstacle.transform.position,
            worldRotation = nearestObstacle.transform.rotation,
            planarDirection = planarDirection,
            planarDistance = Mathf.Sqrt(nearestDistanceSqr),
            signedAngleFromReference = signedAngle
        };
        return true;
    }

    bool CanUseIceWallAsFinalObjective()
    {
        return !HasRemainingObstacles();
    }

    bool TryGetIceWallTracking(
        Vector3 referenceWorldPosition,
        Vector3 planarForward,
        out ObstacleTrackingInfo trackingInfo)
    {
        trackingInfo = default;

        if (!CanUseIceWallAsFinalObjective())
        {
            return false;
        }

        GetTrackingOpenWorldContext(
            out OpenWorldSettings settings,
            out Vector3 spawnPoint,
            out bool spawnPointCaptured);

        Vector3 worldCenter = spawnPointCaptured ? spawnPoint : transform.position;
        Vector3 outward = Vector3.ProjectOnPlane(referenceWorldPosition - worldCenter, Vector3.up);
        if (outward.sqrMagnitude <= 0.0001f)
        {
            outward = planarForward;
        }

        if (outward.sqrMagnitude <= 0.0001f)
        {
            outward = Vector3.forward;
        }

        outward.Normalize();

        Vector3 samplePoint = worldCenter + (outward * Mathf.Max(settings.borderStartDistance, 1f));
        float wallRadius = GetBorderInnerRadius(
            new Vector2(samplePoint.x, samplePoint.z),
            settings,
            spawnPoint,
            spawnPointCaptured);
        Vector3 wallPosition = worldCenter + (outward * wallRadius);
        Vector3 planarOffset = Vector3.ProjectOnPlane(wallPosition - referenceWorldPosition, Vector3.up);
        float planarDistance = planarOffset.magnitude;
        Vector3 planarDirection = planarDistance <= 0.0001f
            ? outward
            : planarOffset / planarDistance;

        trackingInfo = new ObstacleTrackingInfo
        {
            obstacle = null,
            obstacleId = FinalBorderObjectiveId,
            obstacleName = FinalBorderObjectiveName,
            isBorderWallTarget = true,
            compassIcon = finalBorderObjectiveCompassIcon,
            worldPosition = wallPosition,
            worldRotation = Quaternion.LookRotation(outward, Vector3.up),
            planarDirection = planarDirection,
            planarDistance = planarDistance,
            signedAngleFromReference = Vector3.SignedAngle(planarForward, planarDirection, Vector3.up)
        };
        return true;
    }

    void GetTrackingOpenWorldContext(
        out OpenWorldSettings settings,
        out Vector3 spawnPoint,
        out bool spawnPointCaptured)
    {
        if (Application.isPlaying && runtimeStreamState.configured)
        {
            settings = runtimeStreamState.settings;
            spawnPoint = runtimeStreamState.spawnPoint;
            spawnPointCaptured = runtimeStreamState.spawnPointCaptured;
            return;
        }

        settings = CreateOpenWorldSettings();
        spawnPoint = transform.position;
        spawnPointCaptured = false;
    }

    public float GetOpenWorldWaveStrengthMultiplier(Vector3 worldPosition)
    {
        return GetOpenWorldWaveDistanceProfile(worldPosition).heightMultiplier;
    }

    public float GetOpenWorldWaveLengthMultiplier(Vector3 worldPosition)
    {
        return GetOpenWorldWaveDistanceProfile(worldPosition).lengthMultiplier;
    }

    public float GetOpenWorldMaximumWaveStrengthMultiplier()
    {
        return GetOpenWorldMaximumWaveDistanceProfile().heightMultiplier;
    }

    public float GetOpenWorldMaximumWaveLengthMultiplier()
    {
        return GetOpenWorldMaximumWaveDistanceProfile().lengthMultiplier;
    }

    public WaveDistanceProfile GetOpenWorldWaveDistanceProfile(Vector3 worldPosition)
    {
        return EvaluateWaveDistanceProfile(worldPosition, GetActiveWaveDistanceSettings());
    }

    public WaveDistanceProfile GetOpenWorldMaximumWaveDistanceProfile()
    {
        WaveDistanceSettings settings = GetActiveWaveDistanceSettings();
        return new WaveDistanceProfile(
            Mathf.Max(1f, settings.maxWaveHeightMultiplier),
            Mathf.Max(1f, settings.maxWaveLengthMultiplier),
            1f);
    }

    WaveDistanceSettings GetActiveWaveDistanceSettings()
    {
        if (Application.isPlaying && runtimeStreamState.configured)
        {
            return CreateWaveDistanceSettings(runtimeStreamState.settings);
        }

        return new WaveDistanceSettings
        {
            maxWaveHeightMultiplier = maxWaveHeightMultiplier,
            maxWaveLengthMultiplier = maxWaveLengthMultiplier,
            waveStrengthRangeMin = waveStrengthRangeMin,
            waveStrengthRangeMax = waveStrengthRangeMax,
            waveStrengthFadePower = waveStrengthFadePower
        };
    }

    static WaveDistanceSettings CreateWaveDistanceSettings(OpenWorldSettings settings)
    {
        return new WaveDistanceSettings
        {
            maxWaveHeightMultiplier = settings.maxWaveHeightMultiplier,
            maxWaveLengthMultiplier = settings.maxWaveLengthMultiplier,
            waveStrengthRangeMin = settings.waveStrengthRangeMin,
            waveStrengthRangeMax = settings.waveStrengthRangeMax,
            waveStrengthFadePower = settings.waveStrengthFadePower
        };
    }

    WaveDistanceProfile EvaluateWaveDistanceProfile(Vector3 worldPosition, WaveDistanceSettings settings)
    {
        float influence01 = GetWaveDistanceInfluence01(worldPosition, settings);
        return new WaveDistanceProfile(
            Mathf.Lerp(1f, Mathf.Max(1f, settings.maxWaveHeightMultiplier), influence01),
            Mathf.Lerp(1f, Mathf.Max(1f, settings.maxWaveLengthMultiplier), influence01),
            influence01);
    }

    float GetWaveDistanceInfluence01(Vector3 worldPosition, WaveDistanceSettings settings)
    {
        float startDistance = Mathf.Min(settings.waveStrengthRangeMin, settings.waveStrengthRangeMax);
        float endDistance = Mathf.Max(settings.waveStrengthRangeMin, settings.waveStrengthRangeMax);
        float distanceFromOrigin = new Vector2(worldPosition.x, worldPosition.z).magnitude;

        if (endDistance <= startDistance)
        {
            return distanceFromOrigin >= endDistance ? 1f : 0f;
        }

        float normalizedDistance = Mathf.InverseLerp(startDistance, endDistance, distanceFromOrigin);
        float smoothedDistance = SmoothStep01(normalizedDistance);
        return Mathf.Pow(smoothedDistance, Mathf.Max(0.1f, settings.waveStrengthFadePower));
    }

    void Rebuild()
    {
        ApplyWorldPresentation();
    }

    void ApplyGeneratedWorldPresentation()
    {
        ApplyWorldPresentation();
    }

    void ApplyBaseWorldPresentation()
    {
        ApplyWorldPresentation();
    }

    // The generated chunk system is now the only visible world presentation.
    // Keep the legacy Ground as a simple background floor, but keep the legacy
    // Water hidden because the chunked water system is now the only active one.
    void ApplyWorldPresentation()
    {
        SetLegacySurfaceActive(GroundObjectName, true);
        SetLegacySurfaceActive(WaterObjectName, false);
    }

    void EnsureRuntimeWorldReady()
    {
        Rebuild();
    }

    void ResolveSceneReferences()
    {
        generatedChunksRoot = ResolveGeneratedRootReference(generatedChunksRoot, GeneratedChunksRootName);
        generatedHazardsRoot = ResolveGeneratedRootReference(generatedHazardsRoot, GeneratedHazardsRootName);
        generatedEffectsRoot = ResolveGeneratedRootReference(generatedEffectsRoot, GeneratedEffectsRootName);
        generatedObjectiveObstaclesRoot = ResolveObjectiveObstacleRootReference(generatedObjectiveObstaclesRoot, GeneratedObjectiveObstaclesRootName);
    }

    Transform ResolveObjectiveObstacleRootReference(Transform currentRoot, string rootName)
    {
        Transform directChild = transform.Find(rootName);
        if (directChild != null)
        {
            return directChild;
        }

        if (currentRoot != null)
        {
            return currentRoot;
        }

        if (!Application.isPlaying)
        {
            return null;
        }

        GameObject rootObject = new GameObject(rootName);
        Transform root = rootObject.transform;
        root.SetParent(transform, false);
        root.localPosition = Vector3.zero;
        root.localRotation = Quaternion.identity;
        root.localScale = Vector3.one;

        if (!Application.isPlaying)
        {
            ApplyPreviewHideFlagsToGameObjects(rootObject);
        }

        return root;
    }

    void SyncObjectiveObstacles(bool immediate)
    {
        ClearObjectiveObstacles(immediate);

        if (generatedObjectiveObstaclesRoot == null || obstacleTargets == null)
        {
            return;
        }

        if (Application.isPlaying)
        {
            // Only spawn the single current objective
            int index = currentSpawnObjectiveIndex;
            if (index >= 0 && index < obstacleTargets.Length)
            {
                ObstacleTargetDefinition definition = obstacleTargets[index];
                if (IsValidObstacleDefinition(definition))
                {
                    string obstacleId = GetObstacleId(index, definition);
                    if (!destroyedObjectiveObstacleIds.Contains(obstacleId))
                    {
                        SpawnObjectiveObstacleInstance(definition, obstacleId, immediate, index);
                    }
                }
            }
            return;
        }

        // Editor preview: show all objectives
        for (int i = 0; i < obstacleTargets.Length; i++)
        {
            ObstacleTargetDefinition definition = obstacleTargets[i];
            if (!IsValidObstacleDefinition(definition))
            {
                continue;
            }

            SpawnObjectiveObstacleInstance(definition, GetObstacleId(i, definition), immediate, i);
        }
    }

    void ClearObjectiveObstacles(bool immediate)
    {
        spawnedObjectiveObstacles.Clear();

        Transform obstacleRoot = generatedObjectiveObstaclesRoot != null
            ? generatedObjectiveObstaclesRoot
            : transform.Find(GeneratedObjectiveObstaclesRootName);
        if (obstacleRoot == null)
        {
            return;
        }

        suppressObstacleDestructionReporting = true;
        try
        {
            for (int i = obstacleRoot.childCount - 1; i >= 0; i--)
            {
                DestroyObjectSafe(obstacleRoot.GetChild(i).gameObject, immediate);
            }
        }
        finally
        {
            suppressObstacleDestructionReporting = false;
        }
    }

    void SpawnObjectiveObstacleInstance(
        ObstacleTargetDefinition definition,
        string obstacleId,
        bool previewMode,
        int definitionIndex)
    {
        GameObject obstacleObject = Instantiate(definition.prefab, generatedObjectiveObstaclesRoot);
        obstacleObject.name = "ObjectiveObstacle_" + GetObstacleDisplayName(definitionIndex, definition);
        obstacleObject.transform.localPosition = definition.localPosition;
        obstacleObject.transform.localRotation = Quaternion.Euler(definition.localEulerAngles);

        if (previewMode)
        {
            ApplyPreviewHideFlagsToGameObjects(obstacleObject);
        }

        EnsureCollider(obstacleObject);
        EnsureObjectiveObstacleRuntimeComponents(obstacleObject);

        VoyageObstacle obstacle = obstacleObject.GetComponent<VoyageObstacle>();
        if (obstacle == null)
        {
            obstacle = obstacleObject.AddComponent<VoyageObstacle>();
        }

        obstacle.Configure(this, obstacleId, GetObstacleDisplayName(definitionIndex, definition), definition.compassIcon);
        RegisterObjectiveObstacle(obstacle);
    }

    void EnsureObjectiveObstacleRuntimeComponents(GameObject obstacleObject)
    {
        if (obstacleObject == null)
        {
            return;
        }

        bool hasRootRigidbody = obstacleObject.GetComponent<Rigidbody>() != null;
        bool hasRootBuoyancy = obstacleObject.GetComponent<WaterBuoyancy>() != null;

        WaterBuoyancy childBuoyancy = obstacleObject.GetComponentInChildren<WaterBuoyancy>(true);
        if (childBuoyancy != null && childBuoyancy.gameObject != obstacleObject)
        {
            Debug.LogWarning(
                "Objective obstacle '" + obstacleObject.name + "' has WaterBuoyancy on a child object. "
                + "Floating objective obstacles should keep Rigidbody and WaterBuoyancy on the obstacle root.",
                obstacleObject);
        }

        Rigidbody childBody = obstacleObject.GetComponentInChildren<Rigidbody>(true);
        if (childBody != null && childBody.gameObject != obstacleObject)
        {
            Debug.LogWarning(
                "Objective obstacle '" + obstacleObject.name + "' has a Rigidbody on a child object. "
                + "Floating objective obstacles should keep Rigidbody and WaterBuoyancy on the obstacle root.",
                obstacleObject);
        }

        if (!hasRootRigidbody && !hasRootBuoyancy)
        {
            return;
        }

        EnsureFloatingRuntimeComponents(obstacleObject);
    }

    internal void RegisterObjectiveObstacle(VoyageObstacle obstacle)
    {
        if (obstacle == null || spawnedObjectiveObstacles.Contains(obstacle))
        {
            return;
        }

        spawnedObjectiveObstacles.Add(obstacle);
    }

    internal void UnregisterObjectiveObstacle(VoyageObstacle obstacle)
    {
        if (obstacle == null)
        {
            return;
        }

        spawnedObjectiveObstacles.Remove(obstacle);
    }

    internal void HandleObjectiveObstacleDestroyed(VoyageObstacle obstacle)
    {
        if (obstacle == null)
        {
            return;
        }

        if (!string.IsNullOrEmpty(obstacle.ObstacleId))
        {
            destroyedObjectiveObstacleIds.Add(obstacle.ObstacleId);
        }

        UnregisterObjectiveObstacle(obstacle);
    }

    int CountConfiguredObstacleDefinitions()
    {
        if (obstacleTargets == null)
        {
            return 0;
        }

        int count = 0;
        for (int i = 0; i < obstacleTargets.Length; i++)
        {
            if (IsValidObstacleDefinition(obstacleTargets[i]))
            {
                count++;
            }
        }

        return count;
    }

    static bool IsValidObstacleDefinition(ObstacleTargetDefinition definition)
    {
        return definition.prefab != null;
    }

    static string GetObstacleId(int definitionIndex, ObstacleTargetDefinition definition)
    {
        return "obstacle_" + definitionIndex + "_" + GetObstacleDisplayName(definitionIndex, definition);
    }

    static string GetObstacleDisplayName(int definitionIndex, ObstacleTargetDefinition definition)
    {
        if (!string.IsNullOrWhiteSpace(definition.displayName))
        {
            return definition.displayName.Trim();
        }

        if (definition.prefab != null)
        {
            return definition.prefab.name;
        }

        return "Obstacle_" + definitionIndex;
    }

    Transform ResolveGeneratedRootReference(Transform currentRoot, string rootName)
    {
        Transform directChild = transform.Find(rootName);
        if (directChild != null)
        {
            return directChild;
        }

        if (currentRoot != null)
        {
            return currentRoot;
        }

        if (!Application.isPlaying)
        {
            return null;
        }

        GameObject rootObject = new GameObject(rootName);
        Transform root = rootObject.transform;
        root.SetParent(transform, false);
        root.localPosition = Vector3.zero;
        root.localRotation = Quaternion.identity;
        root.localScale = Vector3.one;
        return root;
    }

    bool TryResolveBoatRoot()
    {
        if (boatRoot != null)
        {
            return true;
        }

        if (Time.unscaledTime < nextBoatSearchTime)
        {
            return boatRoot != null;
        }

        nextBoatSearchTime = Time.unscaledTime + 0.75f;

        if (TryFindPreferredBoatRoot(out Transform preferredBoatRoot))
        {
            AssignBoatRoot(preferredBoatRoot);
            return true;
        }

        return false;
    }

    bool TryFindPreferredBoatRoot(out Transform resolvedBoatRoot)
    {
        GameObject namedRoot = GameObject.Find(boatRootName);
        if (namedRoot != null)
        {
            resolvedBoatRoot = namedRoot.transform;
            return true;
        }

        ShipController controller = FindFirstObjectByType<ShipController>();
        if (controller != null)
        {
            resolvedBoatRoot = controller.transform;
            return true;
        }

        resolvedBoatRoot = null;
        return false;
    }

    Vector3 ResolveRuntimeAnchorWorldPosition()
    {
        if (boatRoot != null)
        {
            return boatRoot.position;
        }

        if (runtimeStreamState.spawnPointCaptured)
        {
            return runtimeStreamState.spawnPoint;
        }

        if (TryFindPreferredBoatRoot(out Transform preferredBoatRoot))
        {
            return preferredBoatRoot.position;
        }

        return transform.position;
    }

    Vector3 GetBoatHeadingVector(Transform referenceBoatRoot)
    {
        if (referenceBoatRoot == null)
        {
            return Vector3.forward;
        }

        ShipController shipController = referenceBoatRoot.GetComponent<ShipController>();
        if (shipController == null)
        {
            shipController = referenceBoatRoot.GetComponentInParent<ShipController>();
        }

        if (shipController != null)
        {
            return shipController.PlanarForward;
        }

        Vector3 fallbackForward = Vector3.ProjectOnPlane(referenceBoatRoot.right, Vector3.up);
        if (fallbackForward.sqrMagnitude <= 0.0001f)
        {
            fallbackForward = Vector3.ProjectOnPlane(referenceBoatRoot.forward, Vector3.up);
        }

        if (fallbackForward.sqrMagnitude <= 0.0001f)
        {
            return Vector3.forward;
        }

        return fallbackForward.normalized;
    }

    void AssignBoatRoot(Transform resolvedBoatRoot)
    {
        if (resolvedBoatRoot == null)
        {
            return;
        }

        boatRoot = resolvedBoatRoot;

        SyncBoatFollowCamera();
    }

    void SyncBoatFollowCamera()
    {
        if (boatRoot == null)
        {
            return;
        }

        BoatFollowCamera followCamera = FindFirstObjectByType<BoatFollowCamera>();
        if (followCamera == null)
        {
            return;
        }

        followCamera.target = boatRoot;
    }

    // Packs the current inspector values into the struct consumed by the
    // streaming/generation pipeline. This keeps the rest of the file working
    // against one settings object instead of many unrelated fields.
    OpenWorldSettings CreateOpenWorldSettings()
    {
        return new OpenWorldSettings
        {
            worldSeed = worldSeed,
            chunkSize = GetConfiguredChunkSize(),
            chunkResolution = Mathf.Max(2, surfaceResolution),
            borderStartDistance = borderStartDistance,
            borderThickness = borderThickness,
            interiorHazardStartDistance = interiorHazardStartDistance,
            hazardSpawnExclusionRadius = hazardSpawnExclusionRadius,
            renderBorderWall = renderBorderWall,
            borderWallHeight = borderWallHeight,
            borderWallSubmergedDepth = borderWallSubmergedDepth,
            borderWallEdgeBlendFraction = borderWallEdgeBlendFraction,
            borderWallTopNoiseHeight = borderWallTopNoiseHeight,
            borderWallRadiusNoise = borderWallRadiusNoise,
            borderWallNoiseScale = borderWallNoiseScale,
            maxWaveHeightMultiplier = maxWaveHeightMultiplier,
            maxWaveLengthMultiplier = maxWaveLengthMultiplier,
            waveStrengthRangeMin = waveStrengthRangeMin,
            waveStrengthRangeMax = waveStrengthRangeMax,
            waveStrengthFadePower = waveStrengthFadePower,
            iceBiome = iceBiome,
            forestBiome = forestBiome,
            volcanicBiome = volcanicBiome,
            biomePatchScale = biomePatchScale,
            biomePatchSizeNoiseScale = biomePatchSizeNoiseScale,
            biomePatchScaleMultiplierRange = biomePatchScaleMultiplierRange,
            biomeDensityPatchScale = biomeDensityPatchScale,
            biomeDensityMin = biomeDensityMin,
            biomeDensityMax = biomeDensityMax,
            distanceDensityBias = distanceDensityBias,
            generalFloatingPrefabs = generalFloatingPrefabs,
            generalFloatingObjectDensity = generalFloatingObjectDensity,
            generalFloatingObjectsPerChunk = generalFloatingObjectsPerChunk,
            biomeFloatingObjectsPerChunk = biomeFloatingObjectsPerChunk,
            floatingObjectMinSpacing = floatingObjectMinSpacing
        };
    }

    void UpdateDistanceFog()
    {
        if (!enableDistanceFog || boatRoot == null)
        {
            return;
        }

        float visibleRadius = GetVisibleWorldRadius();
        float danger = GetDanger01(boatRoot.position);
        float fogStartFraction = Mathf.Lerp(calmFogStartFraction, outerFogStartFraction, danger);
        float fogEndFraction = Mathf.Lerp(calmFogEndFraction, outerFogEndFraction, danger);
        float fogStart = visibleRadius * fogStartFraction;
        float fogEnd = visibleRadius * fogEndFraction;

        if (fogEnd <= fogStart)
        {
            fogEnd = fogStart + 1f;
        }

        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogColor = Color.Lerp(calmFogColor, outerFogColor, danger);
        RenderSettings.fogStartDistance = fogStart;
        RenderSettings.fogEndDistance = fogEnd;
    }

    float GetVisibleWorldRadius()
    {
        return Mathf.Max(GetConfiguredChunkSize() * (((GetRenderChunkRadius() * 2) + 1) * 0.5f), GetConfiguredChunkSize() * 0.5f);
    }

    int GetLoadedChunkRadius()
    {
        return GetRenderChunkRadius() + Mathf.Max(preloadChunkBuffer, 0);
    }

    int GetRenderChunkRadius()
    {
        return Mathf.Max(0, visibleChunkRadius);
    }

    static int GetChunkCountForRadius(int radius)
    {
        int diameter = (Mathf.Max(radius, 0) * 2) + 1;
        return diameter * diameter;
    }

    float GetConfiguredChunkSize()
    {
        return Mathf.Max(MinChunkSize, chunkSize);
    }

    static int CountGeneratedChildren(Transform root)
    {
        return root == null ? 0 : root.childCount;
    }

    static int CountActiveChunks(GeneratedWorldStreamState state)
    {
        if (state == null)
        {
            return 0;
        }

        int activeChunkCount = 0;
        foreach (GeneratedChunkState chunk in state.generatedChunks.Values)
        {
            if (chunk != null && chunk.chunkObject != null && chunk.chunkObject.activeInHierarchy)
            {
                activeChunkCount++;
            }
        }

        return activeChunkCount;
    }

    void RefreshEditorPreviewIfNeeded()
    {
        if (Application.isPlaying || !gameObject.scene.IsValid() || !gameObject.scene.isLoaded)
        {
            return;
        }

        if (!previewGenerationInEditor
            || generatedChunksRoot == null
            || generatedHazardsRoot == null)
        {
            ClearEditorOpenWorldPreview();
            return;
        }

        RebuildEditorOpenWorldPreview(
            CreateOpenWorldSettings(),
            GetEditorPreviewAnchorWorldPosition(),
            GetEditorPreviewRenderChunkRadius(),
            generatedChunksRoot,
            generatedHazardsRoot,
            generatedEffectsRoot);
    }

    Vector3 GetEditorPreviewAnchorWorldPosition()
    {
        if (boatRoot != null)
        {
            return boatRoot.position;
        }

        return transform.position;
    }

    int GetEditorPreviewRenderChunkRadius()
    {
        return GetRenderChunkRadius();
    }

    void QueueRuntimeObjectiveObstacleSync()
    {
        runtimeObjectiveObstacleSyncPending = true;
    }

    void ProcessPendingRuntimeObjectiveObstacleSync()
    {
        if (!Application.isPlaying || !runtimeObjectiveObstacleSyncPending)
        {
            return;
        }

        runtimeObjectiveObstacleSyncPending = false;
        ResolveSceneReferences();
        SyncObjectiveObstacles(false);
    }

#if UNITY_EDITOR
    void QueueEditorPreviewRefresh()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            return;
        }

        if (editorPreviewRefreshQueued)
        {
            return;
        }

        editorPreviewRefreshQueued = true;
        EditorApplication.delayCall += RunQueuedEditorPreviewRefresh;
    }

    void RunQueuedEditorPreviewRefresh()
    {
        editorPreviewRefreshQueued = false;
        EditorApplication.delayCall -= RunQueuedEditorPreviewRefresh;

        if (this == null || Application.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
        {
            return;
        }

        RestoreSelectionToWorldIfNeeded();

        ResolveSceneReferences();
        RefreshEditorObjectiveObstaclePreview();
        RefreshEditorPreviewIfNeeded();
        SceneView.RepaintAll();

        if (editorPreviewState.pendingChunkBuildQueue.Count > 0)
        {
            QueueEditorPreviewRefresh();
        }
    }

    void RestoreSelectionToWorldIfNeeded()
    {
        if (Selection.activeTransform == null)
        {
            return;
        }

        if (!IsUnderGeneratedPreviewRoot(Selection.activeTransform, generatedChunksRoot)
            && !IsUnderGeneratedPreviewRoot(Selection.activeTransform, generatedHazardsRoot)
            && !IsUnderGeneratedPreviewRoot(Selection.activeTransform, generatedEffectsRoot)
            && !IsUnderGeneratedPreviewRoot(Selection.activeTransform, generatedObjectiveObstaclesRoot))
        {
            return;
        }

        Selection.activeGameObject = gameObject;
    }

    void RefreshEditorObjectiveObstaclePreview()
    {
        if (!previewObstacleTargetsInEditor)
        {
            generatedObjectiveObstaclesRoot = transform.Find(GeneratedObjectiveObstaclesRootName);
            ClearObjectiveObstacles(true);
            return;
        }

        generatedObjectiveObstaclesRoot = EnsureEditorObjectiveObstacleRootReference();
        SyncObjectiveObstacles(true);
    }

    Transform EnsureEditorObjectiveObstacleRootReference()
    {
        Transform directChild = transform.Find(GeneratedObjectiveObstaclesRootName);
        if (directChild != null)
        {
            return directChild;
        }

        if (generatedObjectiveObstaclesRoot != null)
        {
            return generatedObjectiveObstaclesRoot;
        }

        GameObject rootObject = new GameObject(GeneratedObjectiveObstaclesRootName);
        Transform root = rootObject.transform;
        root.SetParent(transform, false);
        root.localPosition = Vector3.zero;
        root.localRotation = Quaternion.identity;
        root.localScale = Vector3.one;
        ApplyPreviewHideFlagsToGameObjects(rootObject);
        return root;
    }

    void RestoreSelectionToWorldIfNeeded(List<GeneratedChunkState> chunks)
    {
        if (Selection.activeTransform == null || chunks == null)
        {
            return;
        }

        for (int i = 0; i < chunks.Count; i++)
        {
            if (RestoreSelectionToWorldIfNeeded(chunks[i]))
            {
                return;
            }
        }
    }

    bool RestoreSelectionToWorldIfNeeded(GeneratedChunkState chunk)
    {
        if (Selection.activeTransform == null || chunk == null)
        {
            return false;
        }

        if (!IsSameOrChildOf(Selection.activeTransform, chunk.chunkObject != null ? chunk.chunkObject.transform : null)
            && !IsSameOrChildOf(Selection.activeTransform, chunk.hazardRoot)
            && !IsSameOrChildOf(Selection.activeTransform, chunk.floatingRoot))
        {
            return false;
        }

        Selection.activeGameObject = gameObject;
        return true;
    }

    static bool IsUnderGeneratedPreviewRoot(Transform candidate, Transform root)
    {
        return candidate != null && root != null && candidate.IsChildOf(root);
    }

    static bool IsSameOrChildOf(Transform candidate, Transform root)
    {
        return candidate != null
            && root != null
            && (candidate == root || candidate.IsChildOf(root));
    }
#else
    void QueueEditorPreviewRefresh()
    {
    }
#endif

    // Shared chunk streaming pipeline used by both runtime play and editor preview.
    void SyncGeneratedContent(
        GeneratedWorldStreamState state,
        OpenWorldSettings settings,
        Vector3 anchorWorldPosition,
        int renderChunkRadius,
        int loadedChunkRadius,
        bool resetSpawnPoint,
        Transform generatedChunksRoot,
        Transform generatedHazardsRoot,
        Transform generatedEffectsRoot,
        bool previewMode,
        int buildBudget)
    {
        using (new ProfileScope(SyncGeneratedContentSampleName))
        {
        int settingsHash = ComputeSettingsHash(settings);
        bool rootsChanged = state.generatedChunksRoot != generatedChunksRoot
            || state.generatedHazardsRoot != generatedHazardsRoot
            || state.generatedEffectsRoot != generatedEffectsRoot;
        bool settingsChanged = !state.configured || state.settingsHash != settingsHash;
        bool spawnPointChanged = previewMode
            && (!state.spawnPointCaptured
                || (state.spawnPoint - anchorWorldPosition).sqrMagnitude > 0.0001f);

        if (rootsChanged || settingsChanged || resetSpawnPoint || spawnPointChanged)
        {
            ClearGeneratedContent(
                state,
                previewMode || !Application.isPlaying,
                previewMode);
        }

        state.settings = settings;
        state.generatedChunksRoot = generatedChunksRoot;
        state.generatedHazardsRoot = generatedHazardsRoot;
        state.generatedEffectsRoot = generatedEffectsRoot;
        state.configured = true;
        state.settingsHash = settingsHash;

        if (previewMode)
        {
            state.spawnPoint = anchorWorldPosition;
            state.spawnPointCaptured = true;
        }
        else
        {
            if (resetSpawnPoint)
            {
                state.spawnPointCaptured = false;
            }

            if (!state.spawnPointCaptured)
            {
                state.spawnPoint = anchorWorldPosition;
                state.spawnPointCaptured = true;
            }

        }

        ApplyGeneratedWorldPresentation();

        if (state.generatedChunksRoot == null)
        {
            ClearPendingChunkBuildQueue(state);
            if (previewMode)
            {
                editorPreviewActive = false;
            }
            return;
        }

        Vector2Int centerCoord = WorldToChunkCoord(anchorWorldPosition, settings);
        bool chunkTargetsChanged = !state.centerChunkCoordValid
            || state.centerChunkCoord != centerCoord
            || state.lastLoadedChunkRadius != loadedChunkRadius;
        if (chunkTargetsChanged)
        {
            UpdateChunkTargets(state, centerCoord, loadedChunkRadius, previewMode);
            state.centerChunkCoord = centerCoord;
            state.centerChunkCoordValid = true;
            state.lastLoadedChunkRadius = loadedChunkRadius;
        }

        ProcessPendingChunkBuilds(
            state,
            centerCoord,
            loadedChunkRadius,
            previewMode,
            Mathf.Max(1, buildBudget));
        RefreshChunkPresentation(state);
        UpdateChunkVisibility(state, centerCoord, renderChunkRadius, previewMode);

        if (previewMode)
        {
            editorPreviewActive = state.generatedChunks.Count > 0;
        }
        }
    }

    void ClearGeneratedContent(GeneratedWorldStreamState state, bool immediate, bool clearOrphanedPreviewObjects)
    {
        List<GeneratedChunkState> chunks = new List<GeneratedChunkState>(state.generatedChunks.Values);
        state.generatedChunks.Clear();
        ClearPendingChunkBuildQueue(state);
        state.centerChunkCoordValid = false;
        state.lastLoadedChunkRadius = -1;
        state.configured = false;
        state.settingsHash = 0;

#if UNITY_EDITOR
        if (immediate)
        {
            RestoreSelectionToWorldIfNeeded(chunks);
        }
#endif

        for (int i = 0; i < chunks.Count; i++)
        {
            DestroyGeneratedChunk(chunks[i], immediate);
        }

        if (!clearOrphanedPreviewObjects)
        {
            return;
        }

        ClearPreviewGeneratedChildren(state.generatedChunksRoot);
        ClearPreviewGeneratedChildren(state.generatedHazardsRoot);
        ClearPreviewGeneratedChildren(state.generatedEffectsRoot);

        if (state.generatedChunksRoot == null)
        {
            ClearPreviewGeneratedChildren(transform.Find(GeneratedChunksRootName));
        }

        if (state.generatedHazardsRoot == null)
        {
            ClearPreviewGeneratedChildren(transform.Find(GeneratedHazardsRootName));
        }

        if (state.generatedEffectsRoot == null)
        {
            ClearPreviewGeneratedChildren(transform.Find(GeneratedEffectsRootName));
        }
    }

    void UpdateChunkTargets(
        GeneratedWorldStreamState state,
        Vector2Int centerCoord,
        int loadedChunkRadius,
        bool previewMode)
    {
        HashSet<Vector2Int> neededCoords = new HashSet<Vector2Int>();
        List<Vector2Int> missingCoords = new List<Vector2Int>();

        for (int z = -loadedChunkRadius; z <= loadedChunkRadius; z++)
        {
            for (int x = -loadedChunkRadius; x <= loadedChunkRadius; x++)
            {
                Vector2Int coord = new Vector2Int(centerCoord.x + x, centerCoord.y + z);
                neededCoords.Add(coord);

                if (!state.generatedChunks.ContainsKey(coord))
                {
                    missingCoords.Add(coord);
                }
            }
        }

        List<Vector2Int> loadedCoords = new List<Vector2Int>(state.generatedChunks.Keys);
        for (int i = 0; i < loadedCoords.Count; i++)
        {
            Vector2Int coord = loadedCoords[i];
            if (neededCoords.Contains(coord))
            {
                continue;
            }

            if (!state.generatedChunks.TryGetValue(coord, out GeneratedChunkState chunk))
            {
                continue;
            }

            state.generatedChunks.Remove(coord);
#if UNITY_EDITOR
            if (previewMode || !Application.isPlaying)
            {
                RestoreSelectionToWorldIfNeeded(chunk);
            }
#endif
            DestroyGeneratedChunk(chunk, previewMode || !Application.isPlaying);
        }

        missingCoords.Sort((a, b) => CompareChunkDistanceToCenter(a, b, centerCoord));
        ClearPendingChunkBuildQueue(state);

        for (int i = 0; i < missingCoords.Count; i++)
        {
            EnqueueChunkBuild(state, missingCoords[i]);
        }
    }

    void ProcessPendingChunkBuilds(
        GeneratedWorldStreamState state,
        Vector2Int centerCoord,
        int loadedChunkRadius,
        bool previewMode,
        int buildBudget)
    {
        using (new ProfileScope(ProcessPendingChunkBuildsSampleName))
        {
        int safeBuildBudget = Mathf.Max(1, buildBudget);
        int builtChunkCount = 0;

        while (builtChunkCount < safeBuildBudget && state.pendingChunkBuildQueue.Count > 0)
        {
            Vector2Int coord = state.pendingChunkBuildQueue.Dequeue();
            state.pendingChunkBuildSet.Remove(coord);

            if (!IsChunkCoordInRadius(coord, centerCoord, loadedChunkRadius)
                || state.generatedChunks.ContainsKey(coord))
            {
                continue;
            }

            state.generatedChunks.Add(
                coord,
                CreateGeneratedChunk(
                    coord,
                    state.generatedChunksRoot,
                    state.generatedHazardsRoot,
                    state.generatedEffectsRoot,
                    state.settings,
                    state.spawnPoint,
                    state.spawnPointCaptured,
                    true,
                    previewMode));
            builtChunkCount++;
        }
        }
    }

    void UpdateChunkVisibility(
        GeneratedWorldStreamState state,
        Vector2Int centerCoord,
        int renderChunkRadius,
        bool previewMode)
    {
        foreach (KeyValuePair<Vector2Int, GeneratedChunkState> entry in state.generatedChunks)
        {
            bool shouldBeVisible = IsChunkCoordInRadius(entry.Key, centerCoord, renderChunkRadius);
            GeneratedChunkState chunk = entry.Value;

            if (chunk.chunkObject != null && chunk.chunkObject.activeSelf != shouldBeVisible)
            {
                chunk.chunkObject.SetActive(shouldBeVisible);
            }

            if (chunk.hazardRoot != null && chunk.hazardRoot.gameObject.activeSelf != shouldBeVisible)
            {
                chunk.hazardRoot.gameObject.SetActive(shouldBeVisible);
            }

            if (chunk.floatingRoot != null && chunk.floatingRoot.gameObject.activeSelf != shouldBeVisible)
            {
                chunk.floatingRoot.gameObject.SetActive(shouldBeVisible);
            }

            if (!previewMode && shouldBeVisible && !chunk.floatingObjectsSpawned)
            {
                SpawnChunkFloatingObjects(
                    chunk,
                    state.settings,
                    state.spawnPoint,
                    state.spawnPointCaptured,
                    false);
                chunk.floatingObjectsSpawned = true;
            }
        }
    }

    void EnqueueChunkBuild(GeneratedWorldStreamState state, Vector2Int coord)
    {
        if (!state.pendingChunkBuildSet.Add(coord))
        {
            return;
        }

        state.pendingChunkBuildQueue.Enqueue(coord);
    }

    void ClearPendingChunkBuildQueue(GeneratedWorldStreamState state)
    {
        state.pendingChunkBuildQueue.Clear();
        state.pendingChunkBuildSet.Clear();
    }

    static bool IsChunkCoordInRadius(Vector2Int coord, Vector2Int centerCoord, int loadedChunkRadius)
    {
        return Mathf.Abs(coord.x - centerCoord.x) <= loadedChunkRadius
            && Mathf.Abs(coord.y - centerCoord.y) <= loadedChunkRadius;
    }

    static int CompareChunkDistanceToCenter(Vector2Int a, Vector2Int b, Vector2Int centerCoord)
    {
        int aDx = a.x - centerCoord.x;
        int aDy = a.y - centerCoord.y;
        int bDx = b.x - centerCoord.x;
        int bDy = b.y - centerCoord.y;
        int aDistance = (aDx * aDx) + (aDy * aDy);
        int bDistance = (bDx * bDx) + (bDy * bDy);

        if (aDistance != bDistance)
        {
            return aDistance.CompareTo(bDistance);
        }

        if (a.x != b.x)
        {
            return a.x.CompareTo(b.x);
        }

        return a.y.CompareTo(b.y);
    }

    GeneratedChunkState CreateGeneratedChunk(
        Vector2Int chunkCoord,
        Transform chunkParent,
        Transform hazardParent,
        Transform effectsParent,
        OpenWorldSettings settings,
        Vector3 spawnPoint,
        bool spawnPointCaptured,
        bool includeHazards,
        bool previewMode)
    {
        using (new ProfileScope(CreateGeneratedChunkSampleName))
        {
        GameObject chunkObject = new GameObject("Chunk_" + chunkCoord.x + "_" + chunkCoord.y);
        chunkObject.transform.SetParent(chunkParent, false);
        chunkObject.transform.position = GetChunkCenter(chunkCoord, settings);

        GeneratedChunkState state = new GeneratedChunkState
        {
            coord = chunkCoord,
            chunkObject = chunkObject
        };

        if (renderWater)
        {
            state.waterObject = new GameObject("Water");
            state.waterObject.transform.SetParent(chunkObject.transform, false);
            state.waterMeshFilter = state.waterObject.AddComponent<MeshFilter>();
            state.waterMeshRenderer = state.waterObject.AddComponent<MeshRenderer>();
            state.waterSurface = state.waterObject.AddComponent<UrpLowPolyWater>();
        }

        if (renderBorderWall)
        {
            state.borderWallObject = new GameObject("BorderWall");
            state.borderWallObject.transform.SetParent(chunkObject.transform, false);
            state.borderWallMeshFilter = state.borderWallObject.AddComponent<MeshFilter>();
            state.borderWallMeshRenderer = state.borderWallObject.AddComponent<MeshRenderer>();
            state.borderWallMeshCollider = state.borderWallObject.AddComponent<MeshCollider>();
            state.borderWallObject.AddComponent<OpenWorldBorderIceberg>();
        }

        if (previewMode)
        {
            ApplyPreviewHideFlagsToGameObjects(chunkObject);
        }

        if (hazardParent != null)
        {
            state.hazardRoot = new GameObject("Hazards_" + chunkCoord.x + "_" + chunkCoord.y).transform;
            state.hazardRoot.SetParent(hazardParent, false);

            if (previewMode)
            {
                ApplyPreviewHideFlagsToGameObjects(state.hazardRoot.gameObject);
            }
        }

        if (effectsParent != null)
        {
            state.floatingRoot = new GameObject("Floaters_" + chunkCoord.x + "_" + chunkCoord.y).transform;
            state.floatingRoot.SetParent(effectsParent, false);

            if (previewMode)
            {
                ApplyPreviewHideFlagsToGameObjects(state.floatingRoot.gameObject);
            }
        }

        SyncChunkWaterSurface(state, settings, spawnPoint, spawnPointCaptured);
        SyncChunkBorderWall(state, settings, spawnPoint, spawnPointCaptured, previewMode);
        SyncChunkUndersideCap(state, settings, spawnPoint, spawnPointCaptured, previewMode);
        state.presentationHash = ComputeChunkPresentationHash(settings);

        if (includeHazards)
        {
            SpawnChunkHazards(state, settings, spawnPoint, spawnPointCaptured, previewMode);
        }

        return state;
        }
    }

    void DestroyGeneratedChunk(GeneratedChunkState chunk, bool immediate)
    {
        if (chunk == null)
        {
            return;
        }

        if (chunk.hazardRoot != null)
        {
            DestroyObjectSafe(chunk.hazardRoot.gameObject, immediate);
        }

        if (chunk.floatingRoot != null)
        {
            DestroyObjectSafe(chunk.floatingRoot.gameObject, immediate);
        }

        if (chunk.waterSourceMesh != null)
        {
            DestroyObjectSafe(chunk.waterSourceMesh, immediate);
        }

        if (chunk.borderWallMesh != null)
        {
            DestroyObjectSafe(chunk.borderWallMesh, immediate);
        }

        if (chunk.undersideMesh != null)
        {
            DestroyObjectSafe(chunk.undersideMesh, immediate);
        }

        if (chunk.chunkObject != null)
        {
            DestroyObjectSafe(chunk.chunkObject, immediate);
        }
    }

    // Builds or refreshes the water mesh that belongs to the chunk.
    void SyncChunkWaterSurface(
        GeneratedChunkState chunk,
        OpenWorldSettings settings,
        Vector3 spawnPoint,
        bool spawnPointCaptured)
    {
        if (chunk == null)
        {
            return;
        }

        if (!renderWater)
        {
            if (chunk.waterObject != null)
            {
                chunk.waterObject.SetActive(false);
            }

            return;
        }

        if (chunk.waterObject == null)
        {
            chunk.waterObject = new GameObject("Water");
            chunk.waterObject.transform.SetParent(chunk.chunkObject.transform, false);
            chunk.waterMeshFilter = chunk.waterObject.AddComponent<MeshFilter>();
            chunk.waterMeshRenderer = chunk.waterObject.AddComponent<MeshRenderer>();
            chunk.waterSurface = chunk.waterObject.AddComponent<UrpLowPolyWater>();
        }

        if (chunk.waterSurface == null)
        {
            chunk.waterSurface = chunk.waterObject.GetComponent<UrpLowPolyWater>();
            if (chunk.waterSurface == null)
            {
                chunk.waterSurface = chunk.waterObject.AddComponent<UrpLowPolyWater>();
            }
        }

        if (chunk.waterMeshFilter == null)
        {
            chunk.waterMeshFilter = chunk.waterObject.GetComponent<MeshFilter>();
            if (chunk.waterMeshFilter == null)
            {
                chunk.waterMeshFilter = chunk.waterObject.AddComponent<MeshFilter>();
            }
        }

        if (chunk.waterMeshRenderer == null)
        {
            chunk.waterMeshRenderer = chunk.waterObject.GetComponent<MeshRenderer>();
            if (chunk.waterMeshRenderer == null)
            {
                chunk.waterMeshRenderer = chunk.waterObject.AddComponent<MeshRenderer>();
            }
        }

        if (chunk.waterObject != null)
        {
            chunk.waterObject.transform.localPosition = Vector3.zero;
            chunk.waterObject.transform.localRotation = Quaternion.identity;
            chunk.waterObject.transform.localScale = Vector3.one;
        }

        chunk.waterObject.SetActive(true);

        if (chunk.waterMeshRenderer != null)
        {
            chunk.waterMeshRenderer.sharedMaterial = waterMaterial;
            chunk.waterMeshRenderer.enabled = IsRenderableMaterial(waterMaterial);
        }

        if (chunk.waterSourceMesh == null)
        {
            chunk.waterSourceMesh = new Mesh
            {
                name = "Open World Water Source Mesh"
            };
        }

        BuildChunkWaterSourceMesh(
            chunk.waterSourceMesh,
            settings,
            GetChunkCenter(chunk.coord, settings),
            spawnPoint,
            spawnPointCaptured);

        bool hasWaterGeometry = chunk.waterSourceMesh.vertexCount > 0
            && chunk.waterSourceMesh.triangles != null
            && chunk.waterSourceMesh.triangles.Length > 0;
        if (!hasWaterGeometry)
        {
            chunk.waterObject.SetActive(false);
            return;
        }

        chunk.waterSurface.SyncFromWorld(
            GetChunkResolution(settings),
            Vector2.one * GetChunkSize(settings),
            waterHeight,
            waterMaterial,
            waterWaves,
            waterWhirlpools,
            enableWaterWhitecaps,
            whitecapHeightThreshold,
            whitecapCreaseAngle,
            whitecapTriangleStride,
            whitecapCreaseBlendAngle,
            whitecapStrength,
            chunk.waterSourceMesh,
            true);
    }

    void BuildChunkWaterSourceMesh(
        Mesh targetMesh,
        OpenWorldSettings settings,
        Vector3 chunkCenter,
        Vector3 spawnPoint,
        bool spawnPointCaptured)
    {
        if (targetMesh == null)
        {
            return;
        }

        int chunkResolution = GetChunkResolution(settings);
        float chunkSize = GetChunkSize(settings);
        int vertexCount = chunkResolution * chunkResolution;
        float waterEdgePadding = GetWaterAreaPaddingDistance(settings, chunkSize, chunkResolution);
        Vector3[] vertices = new Vector3[vertexCount];
        Vector3[] normals = new Vector3[vertexCount];
        Vector2[] uvs = new Vector2[vertexCount];
        List<int> triangles = new List<int>((chunkResolution - 1) * (chunkResolution - 1) * 6);

        for (int z = 0; z < chunkResolution; z++)
        {
            for (int x = 0; x < chunkResolution; x++)
            {
                int index = x + (z * chunkResolution);
                Vector2 percent = new Vector2(x, z) / Mathf.Max(chunkResolution - 1f, 1f);
                float localX = (percent.x - 0.5f) * chunkSize;
                float localZ = (percent.y - 0.5f) * chunkSize;
                vertices[index] = new Vector3(localX, waterHeight, localZ);
                normals[index] = Vector3.up;
                uvs[index] = percent;
            }
        }

        for (int z = 0; z < chunkResolution - 1; z++)
        {
            for (int x = 0; x < chunkResolution - 1; x++)
            {
                Vector2 cellCenterWorldXZ = GetChunkCellCenterWorldXZ(chunkCenter, chunkSize, chunkResolution, x, z);
                if (!IsInsideWaterArea(cellCenterWorldXZ, settings, spawnPoint, spawnPointCaptured, waterEdgePadding))
                {
                    continue;
                }

                int index = x + (z * chunkResolution);
                triangles.Add(index);
                triangles.Add(index + chunkResolution);
                triangles.Add(index + chunkResolution + 1);

                triangles.Add(index);
                triangles.Add(index + chunkResolution + 1);
                triangles.Add(index + 1);
            }
        }

        targetMesh.Clear();
        if (triangles.Count == 0)
        {
            return;
        }

        targetMesh.indexFormat = vertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        targetMesh.vertices = vertices;
        targetMesh.triangles = triangles.ToArray();
        targetMesh.normals = normals;
        targetMesh.uv = uvs;
        targetMesh.RecalculateBounds();
    }

    // Builds the chunk-local slice of the circular iceberg wall.
    void SyncChunkBorderWall(
        GeneratedChunkState chunk,
        OpenWorldSettings settings,
        Vector3 spawnPoint,
        bool spawnPointCaptured,
        bool previewMode)
    {
        if (chunk == null)
        {
            return;
        }

        if (!settings.renderBorderWall || !spawnPointCaptured || settings.borderThickness <= 0f)
        {
            if (chunk.borderWallObject != null)
            {
                chunk.borderWallObject.SetActive(false);
            }

            return;
        }

        if (chunk.borderWallObject == null)
        {
            chunk.borderWallObject = new GameObject("BorderWall");
            chunk.borderWallObject.transform.SetParent(chunk.chunkObject.transform, false);
            chunk.borderWallMeshFilter = chunk.borderWallObject.AddComponent<MeshFilter>();
            chunk.borderWallMeshRenderer = chunk.borderWallObject.AddComponent<MeshRenderer>();
            chunk.borderWallMeshCollider = chunk.borderWallObject.AddComponent<MeshCollider>();
            chunk.borderWallObject.AddComponent<OpenWorldBorderIceberg>();
        }

        if (previewMode)
        {
            ApplyPreviewHideFlagsToGameObjects(chunk.borderWallObject);
        }

        if (chunk.borderWallMeshFilter == null)
        {
            chunk.borderWallMeshFilter = chunk.borderWallObject.GetComponent<MeshFilter>() ?? chunk.borderWallObject.AddComponent<MeshFilter>();
        }

        if (chunk.borderWallMeshRenderer == null)
        {
            chunk.borderWallMeshRenderer = chunk.borderWallObject.GetComponent<MeshRenderer>() ?? chunk.borderWallObject.AddComponent<MeshRenderer>();
        }

        if (chunk.borderWallMeshCollider == null)
        {
            chunk.borderWallMeshCollider = chunk.borderWallObject.GetComponent<MeshCollider>() ?? chunk.borderWallObject.AddComponent<MeshCollider>();
        }

        if (chunk.borderWallMesh == null)
        {
            chunk.borderWallMesh = new Mesh
            {
                name = "Open World Border Wall Mesh"
            };
            if (previewMode)
            {
                chunk.borderWallMesh.hideFlags = HideFlags.DontSaveInEditor;
            }
        }

        chunk.borderWallObject.transform.localPosition = Vector3.zero;
        chunk.borderWallObject.transform.localRotation = Quaternion.identity;
        chunk.borderWallObject.transform.localScale = Vector3.one;
        chunk.borderWallObject.SetActive(true);
        chunk.borderWallMeshFilter.sharedMesh = chunk.borderWallMesh;
        chunk.borderWallMeshRenderer.sharedMaterial = ResolveBorderWallMaterial();
        chunk.borderWallMeshRenderer.enabled = IsRenderableMaterial(chunk.borderWallMeshRenderer.sharedMaterial);
        chunk.borderWallMeshRenderer.shadowCastingMode = ShadowCastingMode.Off;
        chunk.borderWallMeshRenderer.receiveShadows = false;
        chunk.borderWallMeshCollider.enabled = !previewMode;

        BuildChunkBorderWallMesh(chunk, settings, spawnPoint, spawnPointCaptured);

        bool hasGeometry = chunk.borderWallMesh != null
            && chunk.borderWallMesh.vertexCount > 0
            && chunk.borderWallMesh.triangles != null
            && chunk.borderWallMesh.triangles.Length > 0;
        chunk.borderWallObject.SetActive(hasGeometry);

        if (chunk.borderWallMeshCollider != null && chunk.borderWallMeshCollider.enabled)
        {
            chunk.borderWallMeshCollider.sharedMesh = null;
            chunk.borderWallMeshCollider.sharedMesh = hasGeometry ? chunk.borderWallMesh : null;
        }
    }

    // Builds a flat underside cap so the world looks closed off from below.
    // This is visual only and intentionally has no collider.
    void SyncChunkUndersideCap(
        GeneratedChunkState chunk,
        OpenWorldSettings settings,
        Vector3 spawnPoint,
        bool spawnPointCaptured,
        bool previewMode)
    {
        if (chunk == null)
        {
            return;
        }

        if (!settings.renderBorderWall || !spawnPointCaptured || settings.borderThickness <= 0f)
        {
            if (chunk.undersideObject != null)
            {
                chunk.undersideObject.SetActive(false);
            }

            return;
        }

        if (chunk.undersideObject == null)
        {
            chunk.undersideObject = new GameObject("Underside");
            chunk.undersideObject.transform.SetParent(chunk.chunkObject.transform, false);
            chunk.undersideMeshFilter = chunk.undersideObject.AddComponent<MeshFilter>();
            chunk.undersideMeshRenderer = chunk.undersideObject.AddComponent<MeshRenderer>();
        }

        if (previewMode)
        {
            ApplyPreviewHideFlagsToGameObjects(chunk.undersideObject);
        }

        if (chunk.undersideMeshFilter == null)
        {
            chunk.undersideMeshFilter = chunk.undersideObject.GetComponent<MeshFilter>() ?? chunk.undersideObject.AddComponent<MeshFilter>();
        }

        if (chunk.undersideMeshRenderer == null)
        {
            chunk.undersideMeshRenderer = chunk.undersideObject.GetComponent<MeshRenderer>() ?? chunk.undersideObject.AddComponent<MeshRenderer>();
        }

        if (chunk.undersideMesh == null)
        {
            chunk.undersideMesh = new Mesh
            {
                name = "Open World Underside Mesh"
            };
            if (previewMode)
            {
                chunk.undersideMesh.hideFlags = HideFlags.DontSaveInEditor;
            }
        }

        chunk.undersideObject.transform.localPosition = Vector3.zero;
        chunk.undersideObject.transform.localRotation = Quaternion.identity;
        chunk.undersideObject.transform.localScale = Vector3.one;
        chunk.undersideObject.SetActive(true);
        chunk.undersideMeshFilter.sharedMesh = chunk.undersideMesh;
        chunk.undersideMeshRenderer.sharedMaterial = ResolveBorderWallMaterial();
        chunk.undersideMeshRenderer.enabled = IsRenderableMaterial(chunk.undersideMeshRenderer.sharedMaterial);
        chunk.undersideMeshRenderer.shadowCastingMode = ShadowCastingMode.Off;
        chunk.undersideMeshRenderer.receiveShadows = false;

        BuildChunkUndersideCapMesh(chunk, settings, spawnPoint, spawnPointCaptured);

        bool hasGeometry = chunk.undersideMesh != null
            && chunk.undersideMesh.vertexCount > 0
            && chunk.undersideMesh.triangles != null
            && chunk.undersideMesh.triangles.Length > 0;
        chunk.undersideObject.SetActive(hasGeometry);
    }

    void BuildChunkBorderWallMesh(
        GeneratedChunkState chunk,
        OpenWorldSettings settings,
        Vector3 spawnPoint,
        bool spawnPointCaptured)
    {
        if (chunk.borderWallMesh == null)
        {
            return;
        }

        int wallResolution = Mathf.Max(4, GetChunkResolution(settings));
        float chunkSize = GetChunkSize(settings);
        Vector3 chunkCenter = GetChunkCenter(chunk.coord, settings);
        float bottomY = waterHeight - Mathf.Max(1f, settings.borderWallSubmergedDepth);
        float undersideCapPadding = GetUndersideCapPaddingDistance(settings, chunkSize, wallResolution);
        List<Vector3> vertices = new List<Vector3>();
        List<Vector2> uvs = new List<Vector2>();
        List<int> triangles = new List<int>();

        for (int z = 0; z < wallResolution - 1; z++)
        {
            for (int x = 0; x < wallResolution - 1; x++)
            {
                Vector2Int cell = new Vector2Int(x, z);
                float centerX = (((x + 0.5f) / (wallResolution - 1f)) - 0.5f) * chunkSize;
                float centerZ = (((z + 0.5f) / (wallResolution - 1f)) - 0.5f) * chunkSize;
                Vector2 centerWorldXZ = new Vector2(chunkCenter.x + centerX, chunkCenter.z + centerZ);
                float centerPresence = GetBorderWallPresence01(centerWorldXZ, settings, spawnPoint, spawnPointCaptured);
                if (centerPresence <= 0.001f)
                {
                    continue;
                }

                Vector3 topBL = GetBorderWallTopVertex(chunkCenter, chunkSize, wallResolution, x, z, settings, spawnPoint, spawnPointCaptured);
                Vector3 topBR = GetBorderWallTopVertex(chunkCenter, chunkSize, wallResolution, x + 1, z, settings, spawnPoint, spawnPointCaptured);
                Vector3 topTL = GetBorderWallTopVertex(chunkCenter, chunkSize, wallResolution, x, z + 1, settings, spawnPoint, spawnPointCaptured);
                Vector3 topTR = GetBorderWallTopVertex(chunkCenter, chunkSize, wallResolution, x + 1, z + 1, settings, spawnPoint, spawnPointCaptured);

                AddQuad(vertices, uvs, triangles, topBL, topTL, topTR, topBR, Vector2.zero, Vector2.one);

                if (!IsInsideWaterArea(centerWorldXZ, settings, spawnPoint, spawnPointCaptured, undersideCapPadding))
                {
                    Vector3 bottomBL = new Vector3(topBL.x, bottomY, topBL.z);
                    Vector3 bottomBR = new Vector3(topBR.x, bottomY, topBR.z);
                    Vector3 bottomTL = new Vector3(topTL.x, bottomY, topTL.z);
                    Vector3 bottomTR = new Vector3(topTR.x, bottomY, topTR.z);
                    AddQuad(vertices, uvs, triangles, bottomBL, bottomBR, bottomTR, bottomTL, Vector2.zero, Vector2.one);
                }

                AddBorderWallSideIfNeeded(vertices, uvs, triangles, cell, new Vector2Int(0, -1), topBL, topBR, bottomY, chunkCenter, chunkSize, wallResolution, settings, spawnPoint, spawnPointCaptured);
                AddBorderWallSideIfNeeded(vertices, uvs, triangles, cell, new Vector2Int(1, 0), topBR, topTR, bottomY, chunkCenter, chunkSize, wallResolution, settings, spawnPoint, spawnPointCaptured);
                AddBorderWallSideIfNeeded(vertices, uvs, triangles, cell, new Vector2Int(0, 1), topTR, topTL, bottomY, chunkCenter, chunkSize, wallResolution, settings, spawnPoint, spawnPointCaptured);
                AddBorderWallSideIfNeeded(vertices, uvs, triangles, cell, new Vector2Int(-1, 0), topTL, topBL, bottomY, chunkCenter, chunkSize, wallResolution, settings, spawnPoint, spawnPointCaptured);
            }
        }

        chunk.borderWallMesh.Clear();
        if (vertices.Count == 0)
        {
            return;
        }

        chunk.borderWallMesh.indexFormat = vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        chunk.borderWallMesh.SetVertices(vertices);
        chunk.borderWallMesh.SetTriangles(triangles, 0);
        chunk.borderWallMesh.SetUVs(0, uvs);
        chunk.borderWallMesh.SetColors(BuildBorderWallVertexColors(vertices, bottomY));
        chunk.borderWallMesh.RecalculateNormals();
        chunk.borderWallMesh.RecalculateBounds();
    }

    void BuildChunkUndersideCapMesh(
        GeneratedChunkState chunk,
        OpenWorldSettings settings,
        Vector3 spawnPoint,
        bool spawnPointCaptured)
    {
        if (chunk.undersideMesh == null)
        {
            return;
        }

        int capResolution = Mathf.Max(4, GetChunkResolution(settings));
        float chunkSize = GetChunkSize(settings);
        Vector3 chunkCenter = GetChunkCenter(chunk.coord, settings);
        float bottomY = waterHeight - Mathf.Max(1f, settings.borderWallSubmergedDepth);
        float capPadding = GetUndersideCapPaddingDistance(settings, chunkSize, capResolution);
        List<Vector3> vertices = new List<Vector3>();
        List<Vector2> uvs = new List<Vector2>();
        List<int> triangles = new List<int>();

        for (int z = 0; z < capResolution - 1; z++)
        {
            for (int x = 0; x < capResolution - 1; x++)
            {
                Vector2 cellCenterWorldXZ = GetChunkCellCenterWorldXZ(chunkCenter, chunkSize, capResolution, x, z);
                if (!IsInsideWaterArea(cellCenterWorldXZ, settings, spawnPoint, spawnPointCaptured, capPadding))
                {
                    continue;
                }

                float minX = ((x / Mathf.Max(capResolution - 1f, 1f)) - 0.5f) * chunkSize;
                float maxX = ((((x + 1f)) / Mathf.Max(capResolution - 1f, 1f)) - 0.5f) * chunkSize;
                float minZ = ((z / Mathf.Max(capResolution - 1f, 1f)) - 0.5f) * chunkSize;
                float maxZ = ((((z + 1f)) / Mathf.Max(capResolution - 1f, 1f)) - 0.5f) * chunkSize;

                Vector3 bottomBL = new Vector3(minX, bottomY, minZ);
                Vector3 bottomBR = new Vector3(maxX, bottomY, minZ);
                Vector3 bottomTL = new Vector3(minX, bottomY, maxZ);
                Vector3 bottomTR = new Vector3(maxX, bottomY, maxZ);
                Vector2 uvMin = new Vector2(x / Mathf.Max(capResolution - 1f, 1f), z / Mathf.Max(capResolution - 1f, 1f));
                Vector2 uvMax = new Vector2((x + 1f) / Mathf.Max(capResolution - 1f, 1f), (z + 1f) / Mathf.Max(capResolution - 1f, 1f));
                AddDoubleSidedQuad(vertices, uvs, triangles, bottomBL, bottomTL, bottomTR, bottomBR, uvMin, uvMax);
            }
        }

        chunk.undersideMesh.Clear();
        if (vertices.Count == 0)
        {
            return;
        }

        chunk.undersideMesh.indexFormat = vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        chunk.undersideMesh.SetVertices(vertices);
        chunk.undersideMesh.SetTriangles(triangles, 0);
        chunk.undersideMesh.SetUVs(0, uvs);
        chunk.undersideMesh.SetColors(BuildBorderWallVertexColors(vertices, bottomY));
        chunk.undersideMesh.RecalculateNormals();
        chunk.undersideMesh.RecalculateBounds();
    }

    static float GetUndersideCapPaddingDistance(OpenWorldSettings settings, float chunkSize, int chunkResolution)
    {
        float cellSize = chunkSize / Mathf.Max(chunkResolution - 1f, 1f);
        return Mathf.Max(cellSize * 2f, GetBorderEdgeBlendDistance(settings));
    }

    void AddBorderWallSideIfNeeded(
        List<Vector3> vertices,
        List<Vector2> uvs,
        List<int> triangles,
        Vector2Int cell,
        Vector2Int direction,
        Vector3 topStart,
        Vector3 topEnd,
        float bottomY,
        Vector3 chunkCenter,
        float chunkSize,
        int wallResolution,
        OpenWorldSettings settings,
        Vector3 spawnPoint,
        bool spawnPointCaptured)
    {
        Vector2Int neighbor = cell + direction;
        if (IsBorderWallCellActive(neighbor.x, neighbor.y, chunkCenter, chunkSize, wallResolution, settings, spawnPoint, spawnPointCaptured))
        {
            return;
        }

        Vector3 bottomStart = new Vector3(topStart.x, bottomY, topStart.z);
        Vector3 bottomEnd = new Vector3(topEnd.x, bottomY, topEnd.z);
        AddDoubleSidedQuad(vertices, uvs, triangles, topStart, topEnd, bottomEnd, bottomStart, Vector2.zero, Vector2.one);
    }

    bool IsBorderWallCellActive(
        int cellX,
        int cellZ,
        Vector3 chunkCenter,
        float chunkSize,
        int wallResolution,
        OpenWorldSettings settings,
        Vector3 spawnPoint,
        bool spawnPointCaptured)
    {
        if (cellX < 0 || cellZ < 0 || cellX >= wallResolution - 1 || cellZ >= wallResolution - 1)
        {
            return false;
        }

        float centerX = (((cellX + 0.5f) / (wallResolution - 1f)) - 0.5f) * chunkSize;
        float centerZ = (((cellZ + 0.5f) / (wallResolution - 1f)) - 0.5f) * chunkSize;
        Vector2 centerWorldXZ = new Vector2(chunkCenter.x + centerX, chunkCenter.z + centerZ);
        return GetBorderWallPresence01(centerWorldXZ, settings, spawnPoint, spawnPointCaptured) > 0.001f;
    }

    Vector3 GetBorderWallTopVertex(
        Vector3 chunkCenter,
        float chunkSize,
        int wallResolution,
        int gridX,
        int gridZ,
        OpenWorldSettings settings,
        Vector3 spawnPoint,
        bool spawnPointCaptured)
    {
        float percentX = gridX / Mathf.Max(wallResolution - 1f, 1f);
        float percentZ = gridZ / Mathf.Max(wallResolution - 1f, 1f);
        float localX = (percentX - 0.5f) * chunkSize;
        float localZ = (percentZ - 0.5f) * chunkSize;
        Vector2 worldXZ = new Vector2(chunkCenter.x + localX, chunkCenter.z + localZ);
        float topY = GetBorderWallTopHeight(worldXZ, settings, spawnPoint, spawnPointCaptured);
        return new Vector3(localX, topY, localZ);
    }

    bool IsInsidePlayableArea(
        Vector2 worldXZ,
        OpenWorldSettings settings,
        Vector3 spawnPoint,
        bool spawnPointCaptured)
    {
        return GetPlayableAreaPresence01(worldXZ, settings, spawnPoint, spawnPointCaptured) > 0.001f;
    }

    bool IsInsideWaterArea(
        Vector2 worldXZ,
        OpenWorldSettings settings,
        Vector3 spawnPoint,
        bool spawnPointCaptured,
        float paddingDistance)
    {
        if (!spawnPointCaptured)
        {
            return true;
        }

        float radialDistance = GetDistanceFromWorldCenter(new Vector3(worldXZ.x, 0f, worldXZ.y), spawnPoint, spawnPointCaptured);
        float innerRadius = GetBorderInnerRadius(worldXZ, settings, spawnPoint, spawnPointCaptured);
        return radialDistance <= innerRadius + Mathf.Max(0f, paddingDistance);
    }

    static float GetWaterAreaPaddingDistance(OpenWorldSettings settings, float chunkSize, int chunkResolution)
    {
        float cellSize = chunkSize / Mathf.Max(chunkResolution - 1f, 1f);
        if (!settings.renderBorderWall || settings.borderThickness <= 0f)
        {
            return cellSize;
        }

        float wallOverlap = Mathf.Max(cellSize * 2f, GetBorderEdgeBlendDistance(settings));
        return cellSize + wallOverlap;
    }

    float GetPlayableAreaPresence01(
        Vector2 worldXZ,
        OpenWorldSettings settings,
        Vector3 spawnPoint,
        bool spawnPointCaptured)
    {
        if (!spawnPointCaptured)
        {
            return 1f;
        }

        float radialDistance = GetDistanceFromWorldCenter(new Vector3(worldXZ.x, 0f, worldXZ.y), spawnPoint, spawnPointCaptured);
        float innerRadius = GetBorderInnerRadius(worldXZ, settings, spawnPoint, spawnPointCaptured);
        if (radialDistance >= innerRadius)
        {
            return 0f;
        }

        float edgeBlend = GetBorderEdgeBlendDistance(settings);
        if (edgeBlend <= 0.001f)
        {
            return 1f;
        }

        float fadeStart = Mathf.Max(0f, innerRadius - edgeBlend);
        return 1f - SmoothStep01(Mathf.InverseLerp(fadeStart, innerRadius, radialDistance));
    }

    float GetBorderWallTopHeight(
        Vector2 worldXZ,
        OpenWorldSettings settings,
        Vector3 spawnPoint,
        bool spawnPointCaptured)
    {
        float presence = GetBorderWallPresence01(worldXZ, settings, spawnPoint, spawnPointCaptured);
        if (presence <= 0f)
        {
            return waterHeight;
        }

        Vector2 centered = spawnPointCaptured
            ? worldXZ - new Vector2(spawnPoint.x, spawnPoint.z)
            : worldXZ;
        float primaryNoise = (FractalNoise2D(centered * settings.borderWallNoiseScale + new Vector2(71.4f, 13.2f), settings.worldSeed + 881) * 2f) - 1f;
        float secondaryNoise = (FractalNoise2D(centered * (settings.borderWallNoiseScale * 2.1f) + new Vector2(19.3f, 47.7f), settings.worldSeed + 919) * 2f) - 1f;
        float topNoise = ((primaryNoise * 0.7f) + (secondaryNoise * 0.3f)) * settings.borderWallTopNoiseHeight;
        float crestHeight = Mathf.Max(4f, settings.borderWallHeight + topNoise);
        return waterHeight + (crestHeight * presence);
    }

    float GetBorderWallPresence01(
        Vector2 worldXZ,
        OpenWorldSettings settings,
        Vector3 spawnPoint,
        bool spawnPointCaptured)
    {
        if (!spawnPointCaptured || !settings.renderBorderWall || settings.borderThickness <= 0f)
        {
            return 0f;
        }

        float radialDistance = GetDistanceFromWorldCenter(new Vector3(worldXZ.x, 0f, worldXZ.y), spawnPoint, spawnPointCaptured);
        float innerRadius = GetBorderInnerRadius(worldXZ, settings, spawnPoint, spawnPointCaptured);
        float outerRadius = GetBorderOuterRadius(worldXZ, settings, spawnPoint, spawnPointCaptured);
        if (radialDistance < innerRadius || radialDistance > outerRadius)
        {
            return 0f;
        }

        float thickness = Mathf.Max(outerRadius - innerRadius, 0.001f);
        float edgeBlend = Mathf.Clamp(GetBorderEdgeBlendDistance(settings), 0.5f, thickness * 0.5f);
        float innerFade = edgeBlend >= thickness * 0.5f
            ? Mathf.InverseLerp(innerRadius, innerRadius + (thickness * 0.5f), radialDistance)
            : Mathf.InverseLerp(innerRadius, innerRadius + edgeBlend, radialDistance);
        float outerFade = edgeBlend >= thickness * 0.5f
            ? 1f - Mathf.InverseLerp(outerRadius - (thickness * 0.5f), outerRadius, radialDistance)
            : 1f - Mathf.InverseLerp(outerRadius - edgeBlend, outerRadius, radialDistance);
        return SmoothStep01(Mathf.Clamp01(Mathf.Min(innerFade, outerFade)));
    }

    float GetBorderInnerRadius(
        Vector2 worldXZ,
        OpenWorldSettings settings,
        Vector3 spawnPoint,
        bool spawnPointCaptured)
    {
        return Mathf.Max(0f, settings.borderStartDistance + GetBorderWallRadiusNoise(worldXZ, settings, spawnPoint, spawnPointCaptured));
    }

    float GetBorderOuterRadius(
        Vector2 worldXZ,
        OpenWorldSettings settings,
        Vector3 spawnPoint,
        bool spawnPointCaptured)
    {
        return GetBorderInnerRadius(worldXZ, settings, spawnPoint, spawnPointCaptured) + Mathf.Max(settings.borderThickness, 0.001f);
    }

    static float GetBorderEdgeBlendDistance(OpenWorldSettings settings)
    {
        return Mathf.Max(0f, settings.borderThickness * Mathf.Clamp01(settings.borderWallEdgeBlendFraction));
    }

    float GetBorderWallRadiusNoise(
        Vector2 worldXZ,
        OpenWorldSettings settings,
        Vector3 spawnPoint,
        bool spawnPointCaptured)
    {
        if (settings.borderWallRadiusNoise <= 0f || settings.borderWallNoiseScale <= 0f)
        {
            return 0f;
        }

        Vector2 centered = spawnPointCaptured
            ? worldXZ - new Vector2(spawnPoint.x, spawnPoint.z)
            : worldXZ;
        float primaryNoise = (FractalNoise2D(centered * (settings.borderWallNoiseScale * 0.85f) + new Vector2(103.1f, 29.7f), settings.worldSeed + 947) * 2f) - 1f;
        float secondaryNoise = (FractalNoise2D(centered * (settings.borderWallNoiseScale * 1.9f) + new Vector2(7.9f, 151.4f), settings.worldSeed + 983) * 2f) - 1f;
        return ((primaryNoise * 0.75f) + (secondaryNoise * 0.25f)) * settings.borderWallRadiusNoise;
    }

    Material ResolveBorderWallMaterial()
    {
        if (borderWallMaterial != null)
        {
            return borderWallMaterial;
        }

        WeightedPrefab[] icePrefabs = iceBiome.hazardPrefabs ?? Array.Empty<WeightedPrefab>();
        for (int i = 0; i < icePrefabs.Length; i++)
        {
            GameObject prefab = icePrefabs[i].prefab;
            if (prefab == null)
            {
                continue;
            }

            Renderer renderer = prefab.GetComponentInChildren<Renderer>(true);
            if (renderer != null && renderer.sharedMaterial != null)
            {
                return renderer.sharedMaterial;
            }
        }

        return waterMaterial;
    }

    static void AddQuad(
        List<Vector3> vertices,
        List<Vector2> uvs,
        List<int> triangles,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        Vector3 d,
        Vector2 uvMin,
        Vector2 uvMax)
    {
        int startIndex = vertices.Count;
        vertices.Add(a);
        vertices.Add(b);
        vertices.Add(c);
        vertices.Add(d);

        uvs.Add(new Vector2(uvMin.x, uvMin.y));
        uvs.Add(new Vector2(uvMin.x, uvMax.y));
        uvs.Add(new Vector2(uvMax.x, uvMax.y));
        uvs.Add(new Vector2(uvMax.x, uvMin.y));

        triangles.Add(startIndex);
        triangles.Add(startIndex + 1);
        triangles.Add(startIndex + 2);
        triangles.Add(startIndex);
        triangles.Add(startIndex + 2);
        triangles.Add(startIndex + 3);
    }

    static void AddDoubleSidedQuad(
        List<Vector3> vertices,
        List<Vector2> uvs,
        List<int> triangles,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        Vector3 d,
        Vector2 uvMin,
        Vector2 uvMax)
    {
        AddQuad(vertices, uvs, triangles, a, b, c, d, uvMin, uvMax);
        AddQuad(vertices, uvs, triangles, d, c, b, a, uvMin, uvMax);
    }

    static List<Color> BuildBorderWallVertexColors(List<Vector3> vertices, float bottomY)
    {
        List<Color> colors = new List<Color>(vertices.Count);
        if (vertices.Count == 0)
        {
            return colors;
        }

        float topY = bottomY;
        for (int i = 0; i < vertices.Count; i++)
        {
            if (vertices[i].y > topY)
            {
                topY = vertices[i].y;
            }
        }

        float heightRange = Mathf.Max(topY - bottomY, 0.001f);
        for (int i = 0; i < vertices.Count; i++)
        {
            float height01 = Mathf.Clamp01((vertices[i].y - bottomY) / heightRange);
            colors.Add(new Color(height01, height01, height01, 1f));
        }

        return colors;
    }

    // Spawns biome hazards for interior chunks only. The world border now uses
    // a dedicated wall mesh instead of hazard prefabs.
    void SpawnChunkHazards(
        GeneratedChunkState chunk,
        OpenWorldSettings settings,
        Vector3 spawnPoint,
        bool spawnPointCaptured,
        bool previewMode)
    {
        using (new ProfileScope(SpawnChunkHazardsSampleName))
        {
        Vector3 chunkCenter = GetChunkCenter(chunk.coord, settings);
        bool isBorderChunk = GetBorderFactor(chunkCenter, settings, spawnPoint, spawnPointCaptured) > 0f;

        if (isBorderChunk)
        {
            return;
        }

        if (!ShouldSpawnInteriorHazards(chunkCenter, settings, spawnPoint, spawnPointCaptured))
        {
            return;
        }

        SpawnHazards(
            chunk,
            settings,
            spawnPoint,
            spawnPointCaptured,
            GetInteriorHazardCount(chunkCenter, settings, spawnPoint, spawnPointCaptured),
            GetInteriorHazardSpacing(chunkCenter, settings, spawnPoint, spawnPointCaptured),
            false,
            previewMode);
        }
    }

    void SpawnHazards(
        GeneratedChunkState chunk,
        OpenWorldSettings settings,
        Vector3 spawnPoint,
        bool spawnPointCaptured,
        int requestedCount,
        float minSpacing,
        bool borderHazards,
        bool previewMode)
    {
        if (requestedCount <= 0 || chunk.hazardRoot == null)
        {
            return;
        }

        float chunkSize = GetChunkSize(settings);
        Vector3 chunkCenter = GetChunkCenter(chunk.coord, settings);
        List<Vector3> acceptedPositions = new List<Vector3>();
        int maxAttempts = Mathf.Max(requestedCount * 8, 8);
        int evaluatedSpawnOpportunities = 0;

        for (int attempt = 0;
            attempt < maxAttempts
            && acceptedPositions.Count < requestedCount
            && evaluatedSpawnOpportunities < requestedCount;
            attempt++)
        {
            float offsetX = Mathf.Lerp(
                -(chunkSize * 0.5f) + HazardCandidatePadding,
                (chunkSize * 0.5f) - HazardCandidatePadding,
                Hash01(chunk.coord, settings.worldSeed, attempt, 17));
            float offsetZ = Mathf.Lerp(
                -(chunkSize * 0.5f) + HazardCandidatePadding,
                (chunkSize * 0.5f) - HazardCandidatePadding,
                Hash01(chunk.coord, settings.worldSeed, attempt, 53));

            Vector3 hazardPosition = new Vector3(
                chunkCenter.x + offsetX,
                waterHeight,
                chunkCenter.z + offsetZ);

            if (!IsInsidePlayableArea(new Vector2(hazardPosition.x, hazardPosition.z), settings, spawnPoint, spawnPointCaptured))
            {
                continue;
            }

            if (IsInsideHazardSpawnExclusion(hazardPosition, settings, spawnPoint, spawnPointCaptured))
            {
                continue;
            }

            if (!PassesSpacing(hazardPosition, acceptedPositions, minSpacing))
            {
                continue;
            }

            // Treat requestedCount as the number of valid spawn opportunities, not a guaranteed fill target.
            evaluatedSpawnOpportunities++;
            float spawnProbability = GetHazardSpawnProbability(
                hazardPosition,
                settings,
                spawnPoint,
                spawnPointCaptured,
                borderHazards);
            if (!PassesHazardSpawnProbability(chunk.coord, settings.worldSeed, attempt, spawnProbability, borderHazards))
            {
                continue;
            }

            acceptedPositions.Add(hazardPosition);
            SpawnHazardInstance(
                chunk,
                settings,
                spawnPoint,
                spawnPointCaptured,
                hazardPosition,
                borderHazards,
                attempt,
                previewMode);
        }
    }

    // Spawns buoyant clutter that rides the generated water in visible chunks.
    void SpawnChunkFloatingObjects(
        GeneratedChunkState chunk,
        OpenWorldSettings settings,
        Vector3 spawnPoint,
        bool spawnPointCaptured,
        bool previewMode)
    {
        using (new ProfileScope(SpawnChunkFloatingObjectsSampleName))
        {
        if (previewMode
            || chunk.floatingRoot == null
            || !HasAnyFloatingPrefabs(settings))
        {
            return;
        }

        List<Vector3> occupiedPositions = CollectChunkObjectPositions(chunk.hazardRoot);
        float minSpacing = Mathf.Max(settings.floatingObjectMinSpacing, MinHazardSpacing);

        SpawnFloatingObjects(
            chunk,
            settings,
            spawnPoint,
            spawnPointCaptured,
            settings.generalFloatingPrefabs,
            Mathf.Max(0, settings.generalFloatingObjectsPerChunk),
            minSpacing,
            occupiedPositions,
            true);

        SpawnFloatingObjects(
            chunk,
            settings,
            spawnPoint,
            spawnPointCaptured,
            null,
            Mathf.Max(0, settings.biomeFloatingObjectsPerChunk),
            minSpacing,
            occupiedPositions,
            false);
        }
    }

    [ContextMenu("Log Performance Summary")]
    void LogPerformanceSummary()
    {
        OpenWorldSettings settings = CreateOpenWorldSettings();
        int renderRadius = GetRenderChunkRadius();
        int loadedRadius = GetLoadedChunkRadius();
        int estimatedVisibleChunks = GetChunkCountForRadius(renderRadius);
        int estimatedLoadedChunks = GetChunkCountForRadius(loadedRadius);
        int chunkResolution = GetChunkResolution(settings);
        int gridVerticesPerChunk = chunkResolution * chunkResolution;
        int gridTrianglesPerChunk = (chunkResolution - 1) * (chunkResolution - 1) * 2;
        int waterRuntimeVerticesPerChunk = gridTrianglesPerChunk * 3;
        int runtimeLoadedChunks = runtimeStreamState.generatedChunks.Count;
        int runtimeVisibleChunks = CountActiveChunks(runtimeStreamState);
        int runtimeHazardObjects = CountGeneratedChildren(runtimeStreamState.generatedHazardsRoot);
        int runtimeFloatingObjects = CountGeneratedChildren(runtimeStreamState.generatedEffectsRoot);

        string runtimeSummary = Application.isPlaying
            ? "Runtime loaded/visible chunks: " + runtimeLoadedChunks + "/" + runtimeVisibleChunks
                + ", active water surfaces: " + UrpLowPolyWater.ActiveSurfaceCount
                + ", hazards: " + runtimeHazardObjects
                + ", floaters: " + runtimeFloatingObjects
            : "Runtime chunk counts are available during Play Mode.";
        string chunkCountModeSummary = "Visible chunk radius: " + renderRadius + ".";

        string likelyBottleneck = renderWater
            ? "Water is likely the most expensive steady-state system here because each visible chunk owns its own animated water mesh."
            : "Chunk wall and underside generation are likely the main costs because water rendering is disabled.";

        Debug.Log(
            "[World] Performance summary for '" + name + "'\n"
            + "Chunk size: " + GetConfiguredChunkSize() + ", surface resolution: " + chunkResolution + "\n"
            + chunkCountModeSummary + "\n"
            + "Estimated visible/loaded chunks: " + estimatedVisibleChunks + "/" + estimatedLoadedChunks + "\n"
            + "Per-chunk grid vertices/triangles: " + gridVerticesPerChunk + "/" + gridTrianglesPerChunk + "\n"
            + "Per-chunk water runtime vertices: " + (renderWater ? waterRuntimeVerticesPerChunk : 0) + "\n"
            + "Estimated visible water runtime vertices per frame: " + (renderWater ? estimatedVisibleChunks * waterRuntimeVerticesPerChunk : 0) + "\n"
            + "Chunk build budget per frame: " + Mathf.Max(1, maxChunkBuildsPerFrame) + ", preload buffer: " + Mathf.Max(0, preloadChunkBuffer) + "\n"
            + runtimeSummary + "\n"
            + likelyBottleneck,
            this);
    }

    void SpawnFloatingObjects(
        GeneratedChunkState chunk,
        OpenWorldSettings settings,
        Vector3 spawnPoint,
        bool spawnPointCaptured,
        WeightedPrefab[] overridePrefabs,
        int requestedCount,
        float minSpacing,
        List<Vector3> occupiedPositions,
        bool useGeneralPool)
    {
        if (requestedCount <= 0 || chunk.floatingRoot == null)
        {
            return;
        }

        float chunkSize = GetChunkSize(settings);
        Vector3 chunkCenter = GetChunkCenter(chunk.coord, settings);
        int maxAttempts = Mathf.Max(requestedCount * 12, 12);
        int spawnedCount = 0;
        int evaluatedSpawnOpportunities = 0;

        for (int attempt = 0;
            attempt < maxAttempts
            && spawnedCount < requestedCount
            && evaluatedSpawnOpportunities < requestedCount;
            attempt++)
        {
            float offsetX = Mathf.Lerp(
                -(chunkSize * 0.5f) + HazardCandidatePadding,
                (chunkSize * 0.5f) - HazardCandidatePadding,
                Hash01(chunk.coord, settings.worldSeed, attempt, useGeneralPool ? 211 : 241));
            float offsetZ = Mathf.Lerp(
                -(chunkSize * 0.5f) + HazardCandidatePadding,
                (chunkSize * 0.5f) - HazardCandidatePadding,
                Hash01(chunk.coord, settings.worldSeed, attempt, useGeneralPool ? 223 : 257));

            Vector3 floatingPosition = new Vector3(
                chunkCenter.x + offsetX,
                waterHeight,
                chunkCenter.z + offsetZ);

            if (!IsInsidePlayableArea(new Vector2(floatingPosition.x, floatingPosition.z), settings, spawnPoint, spawnPointCaptured))
            {
                continue;
            }

            if (IsInsideHazardSpawnExclusion(floatingPosition, settings, spawnPoint, spawnPointCaptured))
            {
                continue;
            }

            if (!PassesSpacing(floatingPosition, occupiedPositions, minSpacing))
            {
                continue;
            }

            WeightedPrefab[] prefabs = useGeneralPool
                ? (overridePrefabs ?? Array.Empty<WeightedPrefab>())
                : SelectBiomeFloatingPool(floatingPosition, settings, spawnPoint, spawnPointCaptured);
            if (!HasAnyWeightedPrefabs(prefabs))
            {
                continue;
            }

            evaluatedSpawnOpportunities++;
            float spawnProbability = GetFloatingSpawnProbability(
                floatingPosition,
                settings,
                spawnPoint,
                spawnPointCaptured,
                useGeneralPool);
            if (!PassesFloatingSpawnProbability(chunk.coord, settings.worldSeed, attempt, spawnProbability, useGeneralPool))
            {
                continue;
            }

            occupiedPositions.Add(floatingPosition);
            SpawnFloatingObjectInstance(chunk, settings, prefabs, floatingPosition, attempt, useGeneralPool);
            spawnedCount++;
        }
    }

    WeightedPrefab[] SelectBiomeFloatingPool(
        Vector3 worldPosition,
        OpenWorldSettings settings,
        Vector3 spawnPoint,
        bool spawnPointCaptured)
    {
        HazardBiome biome = GetBorderFactor(worldPosition, settings, spawnPoint, spawnPointCaptured) > 0f
            ? HazardBiome.Ice
            : GetInteriorHazardBiome(worldPosition, settings, spawnPoint, spawnPointCaptured);
        return GetBiomeSettings(biome, settings).floatingPrefabs ?? Array.Empty<WeightedPrefab>();
    }

    float GetFloatingSpawnProbability(
        Vector3 worldPosition,
        OpenWorldSettings settings,
        Vector3 spawnPoint,
        bool spawnPointCaptured,
        bool useGeneralPool)
    {
        if (useGeneralPool)
        {
            return Mathf.Clamp01(settings.generalFloatingObjectDensity);
        }

        HazardBiome biome = GetBorderFactor(worldPosition, settings, spawnPoint, spawnPointCaptured) > 0f
            ? HazardBiome.Ice
            : GetInteriorHazardBiome(worldPosition, settings, spawnPoint, spawnPointCaptured);
        return Mathf.Clamp01(GetBiomeSettings(biome, settings).floatingDensity);
    }

    void SpawnFloatingObjectInstance(
        GeneratedChunkState chunk,
        OpenWorldSettings settings,
        WeightedPrefab[] prefabs,
        Vector3 position,
        int attempt,
        bool useGeneralPool)
    {
        if (!HasAnyWeightedPrefabs(prefabs) || chunk.floatingRoot == null)
        {
            return;
        }

        GameObject prefab = SelectWeightedPrefab(
            prefabs,
            Hash01(chunk.coord, settings.worldSeed, attempt, useGeneralPool ? 271 : 307));
        if (prefab == null)
        {
            return;
        }

        Quaternion rotation = Quaternion.Euler(
            0f,
            Hash01(chunk.coord, settings.worldSeed, attempt, useGeneralPool ? 331 : 359) * 360f,
            0f);
        GameObject floatingObject = Instantiate(prefab, position, rotation, chunk.floatingRoot);
        floatingObject.name = (useGeneralPool ? "Floating_" : "BiomeFloating_") + prefab.name;

        EnsureCollider(floatingObject);
        EnsureFloatingRuntimeComponents(floatingObject);
    }

    static bool HasAnyFloatingPrefabs(OpenWorldSettings settings)
    {
        return HasAnyWeightedPrefabs(settings.generalFloatingPrefabs)
            || HasAnyWeightedPrefabs(settings.iceBiome.floatingPrefabs)
            || HasAnyWeightedPrefabs(settings.forestBiome.floatingPrefabs)
            || HasAnyWeightedPrefabs(settings.volcanicBiome.floatingPrefabs);
    }

    static List<Vector3> CollectChunkObjectPositions(Transform root)
    {
        List<Vector3> positions = new List<Vector3>();
        if (root == null)
        {
            return positions;
        }

        for (int i = 0; i < root.childCount; i++)
        {
            positions.Add(root.GetChild(i).position);
        }

        return positions;
    }

    bool PassesSpacing(Vector3 position, List<Vector3> positions, float minSpacing)
    {
        float safeSpacing = Mathf.Max(minSpacing, MinHazardSpacing);
        for (int i = 0; i < positions.Count; i++)
        {
            if ((positions[i] - position).sqrMagnitude < safeSpacing * safeSpacing)
            {
                return false;
            }
        }

        return true;
    }

    void SpawnHazardInstance(
        GeneratedChunkState chunk,
        OpenWorldSettings settings,
        Vector3 spawnPoint,
        bool spawnPointCaptured,
        Vector3 position,
        bool borderHazard,
        int attempt,
        bool previewMode)
    {
        WeightedPrefab[] prefabs = SelectHazardPool(
            settings,
            borderHazard,
            spawnPoint,
            spawnPointCaptured,
            position,
            out Vector2 scaleRange,
            out bool spawnedAsBorderHazard,
            out string hazardPrefix);
        if (prefabs.Length == 0)
        {
            return;
        }

        GameObject prefab = SelectWeightedPrefab(prefabs, Hash01(chunk.coord, settings.worldSeed, attempt, 89));
        if (prefab == null)
        {
            return;
        }

        Quaternion rotation = Quaternion.Euler(
            0f,
            Hash01(chunk.coord, settings.worldSeed, attempt, 131) * 360f,
            0f);
        GameObject hazardObject = Instantiate(prefab, position, rotation, chunk.hazardRoot);
        hazardObject.name = spawnedAsBorderHazard
            ? "Border_" + prefab.name
            : hazardPrefix + prefab.name;

        if (previewMode)
        {
            ApplyPreviewHideFlagsToGameObjects(hazardObject);
        }

        float scale = Mathf.Lerp(scaleRange.x, scaleRange.y, Hash01(chunk.coord, settings.worldSeed, attempt, 173));
        hazardObject.transform.localScale *= scale;

        EnsureCollider(hazardObject);

        if (TryGetRendererBounds(hazardObject, out Bounds bounds))
        {
            float burialFactor = spawnedAsBorderHazard
                ? Mathf.Lerp(0.3f, 0.5f, Hash01(chunk.coord, settings.worldSeed, attempt, 197))
                : Mathf.Lerp(0.2f, 0.35f, Hash01(chunk.coord, settings.worldSeed, attempt, 197));
            float extraSinkDepth = IsPlatformHazard(prefab.name) ? 0f : HazardAdditionalSinkDepth;
            Vector3 adjustedPosition = hazardObject.transform.position;
            adjustedPosition.y = waterHeight - (bounds.extents.y * burialFactor) - extraSinkDepth;
            hazardObject.transform.position = adjustedPosition;
        }

        if (spawnedAsBorderHazard && !previewMode)
        {
            hazardObject.AddComponent<OpenWorldBorderIceberg>();
        }
    }

    WeightedPrefab[] SelectHazardPool(
        OpenWorldSettings settings,
        bool borderHazard,
        Vector3 spawnPoint,
        bool spawnPointCaptured,
        Vector3 position,
        out Vector2 scaleRange,
        out bool spawnedAsBorderHazard,
        out string hazardPrefix)
    {
        WeightedPrefab[] icePrefabs = settings.iceBiome.hazardPrefabs ?? Array.Empty<WeightedPrefab>();

        if (borderHazard)
        {
            scaleRange = settings.iceBiome.scaleRange;
            spawnedAsBorderHazard = true;
            hazardPrefix = "Ice_";
            return icePrefabs;
        }

        if (!HasAnyInteriorHazardPrefabs(settings))
        {
            scaleRange = Vector2.one;
            spawnedAsBorderHazard = false;
            hazardPrefix = "Hazard_";
            return Array.Empty<WeightedPrefab>();
        }

        HazardBiome biome = GetInteriorHazardBiome(position, settings, spawnPoint, spawnPointCaptured);
        BiomeContentSettings biomeSettings = GetBiomeSettings(biome, settings);
        if (!HasAnyWeightedPrefabs(biomeSettings.hazardPrefabs))
        {
            scaleRange = Vector2.one;
            spawnedAsBorderHazard = false;
            hazardPrefix = "Hazard_";
            return Array.Empty<WeightedPrefab>();
        }

        scaleRange = biomeSettings.scaleRange;
        spawnedAsBorderHazard = false;
        hazardPrefix = GetHazardPrefix(biome);
        return biomeSettings.hazardPrefabs;
    }

    static bool HasAnyInteriorHazardPrefabs(OpenWorldSettings settings)
    {
        return HasAnyWeightedPrefabs(settings.iceBiome.hazardPrefabs)
            || HasAnyWeightedPrefabs(settings.forestBiome.hazardPrefabs)
            || HasAnyWeightedPrefabs(settings.volcanicBiome.hazardPrefabs);
    }

    static BiomeContentSettings GetBiomeSettings(HazardBiome biome, OpenWorldSettings settings)
    {
        switch (biome)
        {
            case HazardBiome.Forest:
                return settings.forestBiome;
            case HazardBiome.Volcanic:
                return settings.volcanicBiome;
            default:
                return settings.iceBiome;
        }
    }

    static string GetHazardPrefix(HazardBiome biome)
    {
        switch (biome)
        {
            case HazardBiome.Forest:
                return "Forest_";
            case HazardBiome.Volcanic:
                return "Volcanic_";
            default:
                return "Ice_";
        }
    }

    static bool HasHazardPrefabsForBiome(HazardBiome biome, OpenWorldSettings settings)
    {
        return HasAnyWeightedPrefabs(GetBiomeSettings(biome, settings).hazardPrefabs);
    }

    static bool HasFloatingPrefabsForBiome(HazardBiome biome, OpenWorldSettings settings)
    {
        return HasAnyWeightedPrefabs(GetBiomeSettings(biome, settings).floatingPrefabs);
    }

    static bool HasBiomeContentForBiome(HazardBiome biome, OpenWorldSettings settings)
    {
        return HasHazardPrefabsForBiome(biome, settings)
            || HasFloatingPrefabsForBiome(biome, settings);
    }

    static float GetBiomeShare(HazardBiome biome, OpenWorldSettings settings)
    {
        return Mathf.Clamp01(GetBiomeSettings(biome, settings).biomeWeight);
    }

    static HazardBiome GetFirstAvailableInteriorBiome(OpenWorldSettings settings)
    {
        for (int i = 0; i < InteriorBiomeOrder.Length; i++)
        {
            HazardBiome biome = InteriorBiomeOrder[i];
            if (HasBiomeContentForBiome(biome, settings))
            {
                return biome;
            }
        }

        return HazardBiome.Ice;
    }

    static float GetAvailableBiomeWeightTotal(OpenWorldSettings settings)
    {
        float totalWeight = 0f;
        for (int i = 0; i < InteriorBiomeOrder.Length; i++)
        {
            HazardBiome biome = InteriorBiomeOrder[i];
            if (!HasBiomeContentForBiome(biome, settings))
            {
                continue;
            }

            totalWeight += Mathf.Max(0f, GetBiomeShare(biome, settings));
        }

        return totalWeight;
    }

    bool IsPlatformHazard(string prefabName)
    {
        return prefabName.Contains("Platform", StringComparison.OrdinalIgnoreCase);
    }

    void EnsureFloatingRuntimeComponents(GameObject floatingObject)
    {
        if (floatingObject == null)
        {
            return;
        }

        NormalizeFloatingColliders(floatingObject);

        Rigidbody body = floatingObject.GetComponent<Rigidbody>();
        if (body == null)
        {
            body = floatingObject.AddComponent<Rigidbody>();
        }

        body.isKinematic = false;
        body.useGravity = true;

        WaterBuoyancy buoyancy = floatingObject.GetComponent<WaterBuoyancy>();
        if (buoyancy == null)
        {
            buoyancy = floatingObject.AddComponent<WaterBuoyancy>();
        }
        else
        {
            buoyancy.enabled = false;
        }

        buoyancy.enabled = true;
    }

    void NormalizeFloatingColliders(GameObject floatingObject)
    {
        Collider[] colliders = floatingObject.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++)
        {
            if (colliders[i] is not MeshCollider meshCollider)
            {
                continue;
            }

            if (meshCollider.convex)
            {
                continue;
            }

            meshCollider.convex = true;
        }
    }

    void EnsureCollider(GameObject hazardObject)
    {
        if (hazardObject.GetComponentInChildren<Collider>() != null)
        {
            return;
        }

        MeshFilter hazardMeshFilter = hazardObject.GetComponentInChildren<MeshFilter>();
        if (hazardMeshFilter == null || hazardMeshFilter.sharedMesh == null)
        {
            return;
        }

        MeshCollider collider = hazardObject.AddComponent<MeshCollider>();
        collider.sharedMesh = hazardMeshFilter.sharedMesh;
    }

    static float GetDistanceFromWorldCenter(Vector3 worldPosition, Vector3 spawnPoint, bool spawnPointCaptured)
    {
        Vector2 offset = spawnPointCaptured
            ? new Vector2(worldPosition.x - spawnPoint.x, worldPosition.z - spawnPoint.z)
            : new Vector2(worldPosition.x, worldPosition.z);
        return offset.magnitude;
    }

    float GetDanger01(
        Vector3 worldPosition,
        OpenWorldSettings settings,
        Vector3 spawnPoint,
        bool spawnPointCaptured)
    {
        if (!spawnPointCaptured)
        {
            return 0f;
        }

        float distanceMetric = GetDistanceFromWorldCenter(worldPosition, spawnPoint, spawnPointCaptured);
        float safeMaxDistance = Mathf.Max(settings.borderStartDistance + settings.borderThickness, 1f);
        return Mathf.Clamp01(distanceMetric / safeMaxDistance);
    }

    float GetBorderFactor(
        Vector3 worldPosition,
        OpenWorldSettings settings,
        Vector3 spawnPoint,
        bool spawnPointCaptured)
    {
        if (!spawnPointCaptured)
        {
            return 0f;
        }

        float distanceMetric = GetDistanceFromWorldCenter(worldPosition, spawnPoint, spawnPointCaptured);
        return Mathf.Clamp01((distanceMetric - settings.borderStartDistance) / Mathf.Max(settings.borderThickness, 0.001f));
    }

    bool ShouldSpawnInteriorHazards(
        Vector3 worldPosition,
        OpenWorldSettings settings,
        Vector3 spawnPoint,
        bool spawnPointCaptured)
    {
        if (!spawnPointCaptured)
        {
            return false;
        }

        if (!HasAnyInteriorHazardPrefabs(settings))
        {
            return false;
        }

        float distanceMetric = GetDistanceFromWorldCenter(worldPosition, spawnPoint, spawnPointCaptured);
        return distanceMetric >= settings.interiorHazardStartDistance;
    }

    int GetInteriorHazardCount(
        Vector3 chunkCenter,
        OpenWorldSettings settings,
        Vector3 spawnPoint,
        bool spawnPointCaptured)
    {
        float danger = GetDanger01(chunkCenter, settings, spawnPoint, spawnPointCaptured);
        HazardBiome biome = GetInteriorHazardBiome(chunkCenter, settings, spawnPoint, spawnPointCaptured);
        BiomeContentSettings biomeSettings = GetBiomeSettings(biome, settings);
        float baseCount = Mathf.Lerp(2f, 6f, danger);
        int maxCount = Mathf.Max(0, biomeSettings.maxHazardsPerChunk);
        return Mathf.Clamp(
            Mathf.RoundToInt(baseCount
                * Mathf.Max(0.1f, biomeSettings.hazardCountMultiplier)),
            0,
            maxCount);
    }

    int GetBorderHazardCount(
        Vector3 chunkCenter,
        OpenWorldSettings settings,
        Vector3 spawnPoint,
        bool spawnPointCaptured)
    {
        BiomeContentSettings borderSettings = settings.iceBiome;
        float borderFactor = Mathf.Max(GetBorderFactor(chunkCenter, settings, spawnPoint, spawnPointCaptured), 0.1f);
        float baseCount = Mathf.Lerp(2f, Mathf.Max(2f, borderSettings.maxHazardsPerChunk), borderFactor);
        int maxCount = Mathf.Max(0, borderSettings.maxHazardsPerChunk);
        return Mathf.Clamp(
            Mathf.RoundToInt(baseCount
                * Mathf.Max(0.1f, borderSettings.hazardCountMultiplier)),
            0,
            maxCount);
    }

    float GetInteriorHazardSpacing(
        Vector3 chunkCenter,
        OpenWorldSettings settings,
        Vector3 spawnPoint,
        bool spawnPointCaptured)
    {
        float danger = GetDanger01(chunkCenter, settings, spawnPoint, spawnPointCaptured);
        HazardBiome biome = GetInteriorHazardBiome(chunkCenter, settings, spawnPoint, spawnPointCaptured);
        BiomeContentSettings biomeSettings = GetBiomeSettings(biome, settings);
        float baseSpacing = Mathf.Lerp(30f, 18f, danger);
        float minSpacing = Mathf.Max(1f, biomeSettings.minHazardSpacing);
        return Mathf.Max(
            baseSpacing * Mathf.Max(0.1f, biomeSettings.hazardSpacingMultiplier),
            minSpacing);
    }

    float GetBorderHazardSpacing(
        Vector3 chunkCenter,
        OpenWorldSettings settings,
        Vector3 spawnPoint,
        bool spawnPointCaptured)
    {
        BiomeContentSettings borderSettings = settings.iceBiome;
        float borderFactor = Mathf.Max(GetBorderFactor(chunkCenter, settings, spawnPoint, spawnPointCaptured), 0.1f);
        float baseSpacing = Mathf.Lerp(22f, 12f, borderFactor);
        return Mathf.Max(
            baseSpacing * Mathf.Max(0.1f, borderSettings.hazardSpacingMultiplier),
            Mathf.Max(1f, borderSettings.minHazardSpacing));
    }

    float GetHazardSpawnProbability(
        Vector3 worldPosition,
        OpenWorldSettings settings,
        Vector3 spawnPoint,
        bool spawnPointCaptured,
        bool borderHazards)
    {
        return borderHazards
            ? GetBorderHazardSpawnProbability(worldPosition, settings, spawnPoint, spawnPointCaptured)
            : GetInteriorHazardSpawnProbability(worldPosition, settings, spawnPoint, spawnPointCaptured);
    }

    float GetInteriorHazardSpawnProbability(
        Vector3 worldPosition,
        OpenWorldSettings settings,
        Vector3 spawnPoint,
        bool spawnPointCaptured)
    {
        float density = GetInteriorHazardDensity01(worldPosition, settings, spawnPoint, spawnPointCaptured);
        HazardBiome biome = GetInteriorHazardBiome(worldPosition, settings, spawnPoint, spawnPointCaptured);
        BiomeContentSettings biomeSettings = GetBiomeSettings(biome, settings);
        return Mathf.Clamp01(density * Mathf.Max(0f, biomeSettings.hazardDensity));
    }

    float GetBorderHazardSpawnProbability(
        Vector3 worldPosition,
        OpenWorldSettings settings,
        Vector3 spawnPoint,
        bool spawnPointCaptured)
    {
        BiomeContentSettings borderSettings = settings.iceBiome;
        float borderFactor = Mathf.Clamp01(GetBorderFactor(worldPosition, settings, spawnPoint, spawnPointCaptured));
        return Mathf.Clamp01(borderFactor * Mathf.Max(0f, borderSettings.hazardDensity));
    }

    bool IsInsideHazardSpawnExclusion(
        Vector3 worldPosition,
        OpenWorldSettings settings,
        Vector3 spawnPoint,
        bool spawnPointCaptured)
    {
        if (!spawnPointCaptured)
        {
            return false;
        }

        Vector2 offset = new Vector2(
            worldPosition.x - spawnPoint.x,
            worldPosition.z - spawnPoint.z);
        float safeRadius = Mathf.Max(0f, settings.hazardSpawnExclusionRadius);
        return offset.sqrMagnitude < safeRadius * safeRadius;
    }

    HazardBiome GetInteriorHazardBiome(
        Vector3 worldPosition,
        OpenWorldSettings settings,
        Vector3 spawnPoint,
        bool spawnPointCaptured)
    {
        float patchNoise = GetHazardBiomePatchNoise(worldPosition, settings, spawnPoint, spawnPointCaptured);

        HazardBiome fallbackBiome = GetFirstAvailableInteriorBiome(settings);
        float totalWeight = GetAvailableBiomeWeightTotal(settings);
        if (totalWeight <= 0.0001f)
        {
            return fallbackBiome;
        }

        float weightedNoise = patchNoise * totalWeight;
        float cumulativeWeight = 0f;

        for (int i = 0; i < InteriorBiomeOrder.Length; i++)
        {
            HazardBiome biome = InteriorBiomeOrder[i];
            if (!HasBiomeContentForBiome(biome, settings))
            {
                continue;
            }

            cumulativeWeight += Mathf.Max(0f, GetBiomeShare(biome, settings));
            if (weightedNoise <= cumulativeWeight)
            {
                return biome;
            }
        }

        return fallbackBiome;
    }

    float GetInteriorHazardDensity01(
        Vector3 worldPosition,
        OpenWorldSettings settings,
        Vector3 spawnPoint,
        bool spawnPointCaptured)
    {
        float patchNoise = GetHazardDensityPatchNoise(worldPosition, settings, spawnPoint, spawnPointCaptured);
        float danger = GetDanger01(worldPosition, settings, spawnPoint, spawnPointCaptured);
        float biasedNoise = Mathf.Lerp(patchNoise, 1f, Mathf.Clamp01(danger * settings.distanceDensityBias));
        float density = Mathf.Lerp(settings.biomeDensityMin, settings.biomeDensityMax, biasedNoise);
        return Mathf.Clamp01(density);
    }

    float GetHazardBiomePatchNoise(
        Vector3 worldPosition,
        OpenWorldSettings settings,
        Vector3 spawnPoint,
        bool spawnPointCaptured)
    {
        Vector2 centered = spawnPointCaptured
            ? new Vector2(worldPosition.x - spawnPoint.x, worldPosition.z - spawnPoint.z)
            : new Vector2(worldPosition.x, worldPosition.z);
        float patchScaleMultiplier = GetBiomePatchScaleMultiplier(centered, settings);
        float effectivePatchScale = settings.biomePatchScale * patchScaleMultiplier;
        float primary = FractalNoise2D(centered * effectivePatchScale + new Vector2(11.7f, 29.3f), settings.worldSeed + 211);
        float secondary = FractalNoise2D(centered * (effectivePatchScale * 1.9f) + new Vector2(57.1f, 83.9f), settings.worldSeed + 307);
        return Mathf.Clamp01((primary * 0.7f) + (secondary * 0.3f));
    }

    float GetHazardDensityPatchNoise(
        Vector3 worldPosition,
        OpenWorldSettings settings,
        Vector3 spawnPoint,
        bool spawnPointCaptured)
    {
        Vector2 centered = spawnPointCaptured
            ? new Vector2(worldPosition.x - spawnPoint.x, worldPosition.z - spawnPoint.z)
            : new Vector2(worldPosition.x, worldPosition.z);
        float primary = FractalNoise2D(centered * settings.biomeDensityPatchScale + new Vector2(73.2f, 17.6f), settings.worldSeed + 401);
        float secondary = FractalNoise2D(centered * (settings.biomeDensityPatchScale * 1.7f) + new Vector2(19.4f, 61.8f), settings.worldSeed + 503);
        return Mathf.Clamp01((primary * 0.65f) + (secondary * 0.35f));
    }

    static bool PassesHazardSpawnProbability(
        Vector2Int chunkCoord,
        int worldSeed,
        int attempt,
        float spawnProbability,
        bool borderHazards)
    {
        if (spawnProbability <= 0f)
        {
            return false;
        }

        if (spawnProbability >= 1f)
        {
            return true;
        }

        return Hash01(chunkCoord, worldSeed, attempt, borderHazards ? 67 : 71) <= spawnProbability;
    }

    static bool PassesFloatingSpawnProbability(
        Vector2Int chunkCoord,
        int worldSeed,
        int attempt,
        float spawnProbability,
        bool useGeneralPool)
    {
        if (spawnProbability <= 0f)
        {
            return false;
        }

        if (spawnProbability >= 1f)
        {
            return true;
        }

        return Hash01(chunkCoord, worldSeed, attempt, useGeneralPool ? 383 : 419) <= spawnProbability;
    }

    float GetBiomePatchScaleMultiplier(Vector2 centeredWorldXZ, OpenWorldSettings settings)
    {
        float sizeNoise = FractalNoise2D(centeredWorldXZ * settings.biomePatchSizeNoiseScale + new Vector2(41.3f, 13.9f), settings.worldSeed + 353);
        return Mathf.Lerp(
            Mathf.Min(settings.biomePatchScaleMultiplierRange.x, settings.biomePatchScaleMultiplierRange.y),
            Mathf.Max(settings.biomePatchScaleMultiplierRange.x, settings.biomePatchScaleMultiplierRange.y),
            sizeNoise);
    }

    Vector2Int WorldToChunkCoord(Vector3 worldPosition, OpenWorldSettings settings)
    {
        float safeChunkSize = GetChunkSize(settings);
        return new Vector2Int(
            Mathf.FloorToInt((worldPosition.x / safeChunkSize) + 0.5f),
            Mathf.FloorToInt((worldPosition.z / safeChunkSize) + 0.5f));
    }

    Vector3 GetChunkCenter(Vector2Int chunkCoord, OpenWorldSettings settings)
    {
        float safeChunkSize = GetChunkSize(settings);
        return new Vector3(chunkCoord.x * safeChunkSize, 0f, chunkCoord.y * safeChunkSize);
    }

    static Vector2 GetChunkCellCenterWorldXZ(Vector3 chunkCenter, float chunkSize, int chunkResolution, int cellX, int cellZ)
    {
        float centerX = (((cellX + 0.5f) / Mathf.Max(chunkResolution - 1f, 1f)) - 0.5f) * chunkSize;
        float centerZ = (((cellZ + 0.5f) / Mathf.Max(chunkResolution - 1f, 1f)) - 0.5f) * chunkSize;
        return new Vector2(chunkCenter.x + centerX, chunkCenter.z + centerZ);
    }

    float GetChunkSize(OpenWorldSettings settings)
    {
        return Mathf.Max(MinChunkSize, settings.chunkSize);
    }

    int GetChunkResolution(OpenWorldSettings settings)
    {
        return Mathf.Max(2, settings.chunkResolution);
    }

    void SetLegacySurfaceActive(string objectName, bool isActive)
    {
        Transform legacySurface = transform.Find(objectName);
        if (legacySurface == null)
        {
            return;
        }

        if (legacySurface.gameObject.activeSelf != isActive)
        {
            legacySurface.gameObject.SetActive(isActive);
        }
    }

    void RefreshChunkPresentation(GeneratedWorldStreamState state)
    {
        if (state == null)
        {
            return;
        }

        int presentationHash = ComputeChunkPresentationHash(state.settings);
        foreach (GeneratedChunkState chunk in state.generatedChunks.Values)
        {
            if (chunk == null)
            {
                continue;
            }

            if (chunk.presentationHash == presentationHash)
            {
                continue;
            }

            SyncChunkWaterSurface(
                chunk,
                state.settings,
                state.spawnPoint,
                state.spawnPointCaptured);
            SyncChunkBorderWall(
                chunk,
                state.settings,
                state.spawnPoint,
                state.spawnPointCaptured,
                !Application.isPlaying);
            SyncChunkUndersideCap(
                chunk,
                state.settings,
                state.spawnPoint,
                state.spawnPointCaptured,
                !Application.isPlaying);
            chunk.presentationHash = presentationHash;
        }
    }

    void ClearPreviewGeneratedChildren(Transform root)
    {
        if (root == null)
        {
            return;
        }

#if UNITY_EDITOR
        if (Selection.activeTransform != null && Selection.activeTransform.IsChildOf(root))
        {
            Selection.activeObject = null;
        }
#endif

        for (int i = root.childCount - 1; i >= 0; i--)
        {
            Transform child = root.GetChild(i);
            if ((child.hideFlags & HideFlags.DontSaveInEditor) == 0
                && (child.gameObject.hideFlags & HideFlags.DontSaveInEditor) == 0)
            {
                continue;
            }

            DestroyObjectSafe(child.gameObject, true);
        }
    }

    void ApplyPreviewHideFlagsToGameObjects(GameObject rootObject)
    {
        Transform[] transforms = rootObject.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < transforms.Length; i++)
        {
            transforms[i].gameObject.hideFlags = HideFlags.DontSaveInEditor;
        }
    }

    void DestroyObjectSafe(UnityEngine.Object target, bool immediate)
    {
        if (target == null)
        {
            return;
        }

        if (immediate)
        {
            DestroyImmediate(target);
        }
        else
        {
            Destroy(target);
        }
    }

    bool IsRenderableMaterial(Material material)
    {
        return material != null
            && material.shader != null
            && material.shader.isSupported;
    }

    static float SmoothStep01(float t)
    {
        t = Mathf.Clamp01(t);
        return t * t * (3f - (2f * t));
    }

    static float FractalNoise2D(Vector2 point, int seed)
    {
        float total = 0f;
        float amplitude = 0.5f;
        float amplitudeSum = 0f;
        Vector2 samplePoint = point;

        for (int octave = 0; octave < 4; octave++)
        {
            total += ValueNoise2D(samplePoint, seed + (octave * 131)) * amplitude;
            amplitudeSum += amplitude;
            samplePoint = (samplePoint * 2.03f) + new Vector2(17.11f, 43.73f);
            amplitude *= 0.5f;
        }

        return total / Mathf.Max(amplitudeSum, MinTerrainSampleSize);
    }

    static float ValueNoise2D(Vector2 point, int seed)
    {
        Vector2 cell = new Vector2(
            Mathf.Floor(point.x),
            Mathf.Floor(point.y));
        Vector2 local = point - cell;
        local = new Vector2(
            SmoothStep01(local.x),
            SmoothStep01(local.y));

        float n00 = Hash2D(cell, seed);
        float n10 = Hash2D(cell + new Vector2(1f, 0f), seed);
        float n01 = Hash2D(cell + new Vector2(0f, 1f), seed);
        float n11 = Hash2D(cell + new Vector2(1f, 1f), seed);

        float nx0 = Mathf.Lerp(n00, n10, local.x);
        float nx1 = Mathf.Lerp(n01, n11, local.x);
        return Mathf.Lerp(nx0, nx1, local.y);
    }

    static float Hash2D(Vector2 point, int seed)
    {
        float dot = (point.x * 127.1f) + (point.y * 311.7f) + (seed * 17.17f);
        return Mathf.Repeat(Mathf.Sin(dot) * 43758.5453f, 1f);
    }

    static float Hash01(Vector2Int chunkCoord, int worldSeed, int attempt, int salt)
    {
        float seed = (chunkCoord.x * 127.1f)
            + (chunkCoord.y * 311.7f)
            + (attempt * 17.17f)
            + (salt * 29.29f)
            + (worldSeed * 0.113f);
        return Mathf.Repeat(Mathf.Sin(seed) * 43758.5453f, 1f);
    }

    static bool HasAnyWeightedPrefabs(WeightedPrefab[] prefabs)
    {
        if (prefabs == null)
        {
            return false;
        }

        for (int i = 0; i < prefabs.Length; i++)
        {
            if (prefabs[i].prefab != null && prefabs[i].weight > 0f)
            {
                return true;
            }
        }

        return false;
    }

    static GameObject SelectWeightedPrefab(WeightedPrefab[] prefabs, float normalizedWeight)
    {
        if (!HasAnyWeightedPrefabs(prefabs))
        {
            return null;
        }

        float totalWeight = 0f;
        for (int i = 0; i < prefabs.Length; i++)
        {
            if (prefabs[i].prefab == null)
            {
                continue;
            }

            totalWeight += Mathf.Max(0f, prefabs[i].weight);
        }

        if (totalWeight <= 0f)
        {
            return null;
        }

        float threshold = Mathf.Clamp01(normalizedWeight) * totalWeight;
        float cumulativeWeight = 0f;
        GameObject fallbackPrefab = null;
        for (int i = 0; i < prefabs.Length; i++)
        {
            WeightedPrefab weightedPrefab = prefabs[i];
            if (weightedPrefab.prefab == null)
            {
                continue;
            }

            fallbackPrefab = weightedPrefab.prefab;
            cumulativeWeight += Mathf.Max(0f, weightedPrefab.weight);
            if (threshold <= cumulativeWeight)
            {
                return weightedPrefab.prefab;
            }
        }

        return fallbackPrefab;
    }

    static int ComputeSettingsHash(OpenWorldSettings settings)
    {
        unchecked
        {
            int hash = 17;
            hash = (hash * 31) + settings.worldSeed;
            hash = (hash * 31) + settings.chunkSize.GetHashCode();
            hash = (hash * 31) + settings.chunkResolution;
            hash = (hash * 31) + settings.borderStartDistance.GetHashCode();
            hash = (hash * 31) + settings.borderThickness.GetHashCode();
            hash = (hash * 31) + settings.interiorHazardStartDistance.GetHashCode();
            hash = (hash * 31) + settings.hazardSpawnExclusionRadius.GetHashCode();
            hash = (hash * 31) + settings.renderBorderWall.GetHashCode();
            hash = (hash * 31) + settings.borderWallHeight.GetHashCode();
            hash = (hash * 31) + settings.borderWallSubmergedDepth.GetHashCode();
            hash = (hash * 31) + settings.borderWallEdgeBlendFraction.GetHashCode();
            hash = (hash * 31) + settings.borderWallTopNoiseHeight.GetHashCode();
            hash = (hash * 31) + settings.borderWallRadiusNoise.GetHashCode();
            hash = (hash * 31) + settings.borderWallNoiseScale.GetHashCode();
            hash = (hash * 31) + settings.maxWaveHeightMultiplier.GetHashCode();
            hash = (hash * 31) + settings.maxWaveLengthMultiplier.GetHashCode();
            hash = (hash * 31) + settings.waveStrengthRangeMin.GetHashCode();
            hash = (hash * 31) + settings.waveStrengthRangeMax.GetHashCode();
            hash = (hash * 31) + settings.waveStrengthFadePower.GetHashCode();
            hash = (hash * 31) + settings.biomePatchScale.GetHashCode();
            hash = (hash * 31) + settings.biomePatchSizeNoiseScale.GetHashCode();
            hash = (hash * 31) + settings.biomePatchScaleMultiplierRange.GetHashCode();
            hash = (hash * 31) + settings.biomeDensityPatchScale.GetHashCode();
            hash = (hash * 31) + settings.biomeDensityMin.GetHashCode();
            hash = (hash * 31) + settings.biomeDensityMax.GetHashCode();
            hash = (hash * 31) + settings.distanceDensityBias.GetHashCode();
            hash = (hash * 31) + settings.generalFloatingObjectDensity.GetHashCode();
            hash = (hash * 31) + settings.generalFloatingObjectsPerChunk;
            hash = (hash * 31) + settings.biomeFloatingObjectsPerChunk;
            hash = (hash * 31) + settings.floatingObjectMinSpacing.GetHashCode();
            hash = AppendBiomeSettingsHash(hash, settings.iceBiome);
            hash = AppendBiomeSettingsHash(hash, settings.forestBiome);
            hash = AppendBiomeSettingsHash(hash, settings.volcanicBiome);
            hash = AppendWeightedPrefabArrayHash(hash, settings.generalFloatingPrefabs);
            return hash;
        }
    }

    int ComputeChunkPresentationHash(OpenWorldSettings settings)
    {
        unchecked
        {
            Material resolvedBorderWallMaterial = ResolveBorderWallMaterial();
            int hash = 17;
            hash = (hash * 31) + settings.chunkSize.GetHashCode();
            hash = (hash * 31) + settings.chunkResolution;
            hash = (hash * 31) + waterHeight.GetHashCode();
            hash = (hash * 31) + renderWater.GetHashCode();
            hash = (hash * 31) + renderBorderWall.GetHashCode();
            hash = (hash * 31) + (waterMaterial != null ? waterMaterial.GetInstanceID() : 0);
            hash = (hash * 31) + (resolvedBorderWallMaterial != null ? resolvedBorderWallMaterial.GetInstanceID() : 0);
            hash = (hash * 31) + enableWaterWhitecaps.GetHashCode();
            hash = (hash * 31) + whitecapHeightThreshold.GetHashCode();
            hash = (hash * 31) + whitecapCreaseAngle.GetHashCode();
            hash = (hash * 31) + whitecapTriangleStride;
            hash = (hash * 31) + whitecapCreaseBlendAngle.GetHashCode();
            hash = (hash * 31) + whitecapStrength.GetHashCode();
            hash = (hash * 31) + maxWaveHeightMultiplier.GetHashCode();
            hash = (hash * 31) + maxWaveLengthMultiplier.GetHashCode();
            hash = (hash * 31) + waveStrengthRangeMin.GetHashCode();
            hash = (hash * 31) + waveStrengthRangeMax.GetHashCode();
            hash = (hash * 31) + waveStrengthFadePower.GetHashCode();
            hash = AppendWaveArrayHash(hash, waterWaves);
            hash = AppendWhirlpoolArrayHash(hash, waterWhirlpools);
            return hash;
        }
    }

    static int AppendWaveArrayHash(int currentHash, UrpLowPolyWater.GerstnerWave[] waves)
    {
        unchecked
        {
            int hash = (currentHash * 31) + (waves?.Length ?? 0);
            if (waves == null)
            {
                return hash;
            }

            for (int i = 0; i < waves.Length; i++)
            {
                hash = (hash * 31) + waves[i].direction.GetHashCode();
                hash = (hash * 31) + waves[i].amplitude.GetHashCode();
                hash = (hash * 31) + waves[i].waveLength.GetHashCode();
                hash = (hash * 31) + waves[i].speed.GetHashCode();
                hash = (hash * 31) + waves[i].steepness.GetHashCode();
            }

            return hash;
        }
    }

    static int AppendWhirlpoolArrayHash(int currentHash, UrpLowPolyWater.WhirlpoolFeature[] whirlpools)
    {
        unchecked
        {
            int hash = (currentHash * 31) + (whirlpools?.Length ?? 0);
            if (whirlpools == null)
            {
                return hash;
            }

            for (int i = 0; i < whirlpools.Length; i++)
            {
                hash = (hash * 31) + whirlpools[i].centerXZ.GetHashCode();
                hash = (hash * 31) + whirlpools[i].radius.GetHashCode();
                hash = (hash * 31) + whirlpools[i].depth.GetHashCode();
                hash = (hash * 31) + whirlpools[i].spinSpeed.GetHashCode();
                hash = (hash * 31) + whirlpools[i].pullStrength.GetHashCode();
            }

            return hash;
        }
    }

    static int AppendBiomeSettingsHash(int currentHash, BiomeContentSettings settings)
    {
        unchecked
        {
            int hash = currentHash;
            hash = (hash * 31) + settings.scaleRange.GetHashCode();
            hash = (hash * 31) + settings.biomeWeight.GetHashCode();
            hash = (hash * 31) + settings.hazardDensity.GetHashCode();
            hash = (hash * 31) + settings.hazardCountMultiplier.GetHashCode();
            hash = (hash * 31) + settings.hazardSpacingMultiplier.GetHashCode();
            hash = (hash * 31) + settings.maxHazardsPerChunk;
            hash = (hash * 31) + settings.minHazardSpacing.GetHashCode();
            hash = (hash * 31) + settings.floatingDensity.GetHashCode();
            hash = AppendWeightedPrefabArrayHash(hash, settings.hazardPrefabs);
            hash = AppendWeightedPrefabArrayHash(hash, settings.floatingPrefabs);
            return hash;
        }
    }

    static int AppendWeightedPrefabArrayHash(int currentHash, WeightedPrefab[] prefabs)
    {
        unchecked
        {
            int hash = (currentHash * 31) + (prefabs?.Length ?? 0);
            if (prefabs == null)
            {
                return hash;
            }

            for (int i = 0; i < prefabs.Length; i++)
            {
                hash = (hash * 31) + (prefabs[i].prefab != null ? prefabs[i].prefab.GetInstanceID() : 0);
                hash = (hash * 31) + prefabs[i].weight.GetHashCode();
            }

            return hash;
        }
    }

    static bool TryGetRendererBounds(GameObject target, out Bounds bounds)
    {
        Renderer[] renderers = target.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
        {
            bounds = default;
            return false;
        }

        bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
        {
            bounds.Encapsulate(renderers[i].bounds);
        }

        return true;
    }
}
