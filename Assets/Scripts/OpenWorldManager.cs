using System;
using System.Collections.Generic;
using UnityEngine;

[AddComponentMenu("World/Open World Manager")]
public class OpenWorldManager : MonoBehaviour
{
    public enum HazardBiome
    {
        Iceberg,
        Tree,
        Magma
    }

    const float MinChunkSize = 10f;
    const float MinTerrainSampleSize = 0.0001f;
    const float BoatSearchIntervalSeconds = 0.75f;
    const float WaterPaddingChunks = 2f;
    const string GroundObjectName = "Ground";

    public static OpenWorldManager Instance { get; private set; }

    [Header("Scene References")]
    [SerializeField] Transform boatRoot;
    [SerializeField] string boatRootName = "BoatParent";
    [SerializeField] World sceneWorld;
    [SerializeField] Transform generatedChunksRoot;
    [SerializeField] Transform generatedHazardsRoot;
    [SerializeField] Transform generatedEffectsRoot;

    [Header("Terrain")]
    [SerializeField] int worldSeed = 457;
    [SerializeField] float waterHeight;
    [SerializeField] float terrainBaseHeight = -10f;
    [SerializeField] float deepOceanDepth = 24f;
    [SerializeField] float islandRise = 30f;
    [SerializeField] float islandThreshold = 0.58f;
    [SerializeField] float islandBlend = 0.14f;
    [SerializeField] float spawnSafeRadius = 180f;
    [SerializeField, Range(0f, 1f)] float outerIslandHeightMultiplier = 0.35f;
    [SerializeField, Range(0f, 1f)] float outerShorelineDetailMultiplier = 0.45f;

    [Header("Chunks")]
    [SerializeField] float chunkSize = 140f;
    [SerializeField] int chunkResolution = 40;
    [SerializeField] int activeChunkRadius = 2;
    [SerializeField] int preloadChunkBuffer = 1;

    [Header("Difficulty")]
    [SerializeField] float borderStartDistance = 5000f;
    [SerializeField] float borderThickness = 260f;
    [SerializeField] float interiorIcebergStartDistance = 0f;
    [SerializeField] float maxWaveStrengthMultiplier = 2.35f;
    [SerializeField] float hazardSpawnExclusionRadius = 32f;

    [Header("Hazards")]
    [SerializeField] GameObject[] icebergPrefabs = Array.Empty<GameObject>();
    [SerializeField] GameObject[] treePrefabs = Array.Empty<GameObject>();
    [SerializeField] GameObject[] magmaPrefabs = Array.Empty<GameObject>();
    [SerializeField] Vector2 icebergScaleRange = new Vector2(0.85f, 1.8f);
    [SerializeField] Vector2 treeScaleRange = new Vector2(3f, 5f);
    [SerializeField] Vector2 magmaScaleRange = new Vector2(0.85f, 1.8f);
    [SerializeField] float biomePatchScale = 0.0016f;
    [SerializeField] float biomePatchSizeNoiseScale = 0.0008f;
    [SerializeField] Vector2 biomePatchScaleMultiplierRange = new Vector2(0.75f, 1.35f);
    [SerializeField] float biomeDensityPatchScale = 0.0024f;
    [SerializeField, Range(0f, 1f)] float biomeDensityMin = 0.7f;
    [SerializeField, Range(0f, 1f)] float biomeDensityMax = 0.72f;
    [SerializeField, Range(0f, 1f)] float distanceDensityBias = 0.35f;
    [SerializeField, Range(0f, 1f)] float icebergBiomeShare = 0.5f;
    [SerializeField, Range(0f, 1f)] float treeBiomeShare = 0.12f;
    [SerializeField] float treeBiomeCountMultiplier = 6f;
    [SerializeField, Range(0.1f, 2f)] float treeBiomeSpacingMultiplier = 0.2f;
    [SerializeField] int treeBiomeMaxCount = 14;
    [SerializeField] float treeBiomeMinSpacing = 6f;

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

    readonly Dictionary<Vector2Int, OpenWorldChunk> loadedChunks = new Dictionary<Vector2Int, OpenWorldChunk>();

    Vector3 spawnPoint;
    bool spawnPointCaptured;
    bool worldConfigured;
    bool hasWon;
    float nextBoatSearchTime;
    Material groundMaterial;
    UrpLowPolyWater activeWater;

    public Transform BoatRoot => boatRoot;
    public Transform GeneratedHazardsRoot => generatedHazardsRoot;
    public int WorldSeed => worldSeed;
    public float WaterHeight => waterHeight;
    public float ChunkSize => Mathf.Max(MinChunkSize, chunkSize);
    public int ChunkResolution => Mathf.Max(2, chunkResolution);
    public Material GroundMaterial => groundMaterial;
    public GameObject[] IcebergPrefabs => icebergPrefabs ?? Array.Empty<GameObject>();
    public GameObject[] TreePrefabs => treePrefabs ?? Array.Empty<GameObject>();
    public GameObject[] MagmaPrefabs => magmaPrefabs ?? Array.Empty<GameObject>();
    public Vector2 IcebergScaleRange => icebergScaleRange;
    public Vector2 TreeScaleRange => treeScaleRange;
    public Vector2 MagmaScaleRange => magmaScaleRange;
    public bool HasWon => hasWon;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("Multiple OpenWorldManager instances detected. Keeping the latest enabled instance.");
        }

        Instance = this;
        ResolveSceneReferences();
    }

    void OnEnable()
    {
        Instance = this;
        ResolveSceneReferences();
    }

    void Update()
    {
        ResolveSceneReferences();
        if (!TryResolveBoatRoot())
        {
            return;
        }

        CaptureSpawnPointIfNeeded();
        ConfigureSceneWorldIfNeeded();
        FollowBoatWithWorldSurface();
        UpdateDistanceFog();
        RefreshLoadedChunks();
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

        hasWon = true;
        Debug.Log("Victory: Boat reached the iceberg world border via " + borderIceberg.name + ".");

        if (freezeTimeOnWin && Application.isPlaying)
        {
            Time.timeScale = 0f;
        }
    }

    public Vector2Int WorldToChunkCoord(Vector3 worldPosition)
    {
        float safeChunkSize = Mathf.Max(MinChunkSize, chunkSize);
        return new Vector2Int(
            Mathf.FloorToInt((worldPosition.x / safeChunkSize) + 0.5f),
            Mathf.FloorToInt((worldPosition.z / safeChunkSize) + 0.5f));
    }

    public Vector3 GetChunkCenter(Vector2Int chunkCoord)
    {
        float safeChunkSize = Mathf.Max(MinChunkSize, chunkSize);
        return new Vector3(chunkCoord.x * safeChunkSize, 0f, chunkCoord.y * safeChunkSize);
    }

    public float GetDanger01(Vector3 worldPosition)
    {
        if (!spawnPointCaptured)
        {
            return 0f;
        }

        Vector2 offset = new Vector2(
            worldPosition.x - spawnPoint.x,
            worldPosition.z - spawnPoint.z);
        float distanceMetric = Mathf.Max(Mathf.Abs(offset.x), Mathf.Abs(offset.y));
        float safeMaxDistance = Mathf.Max(borderStartDistance + borderThickness, 1f);
        return Mathf.Clamp01(distanceMetric / safeMaxDistance);
    }

    public float GetBorderFactor(Vector3 worldPosition)
    {
        if (!spawnPointCaptured)
        {
            return 0f;
        }

        Vector2 offset = new Vector2(
            worldPosition.x - spawnPoint.x,
            worldPosition.z - spawnPoint.z);
        float distanceMetric = Mathf.Max(Mathf.Abs(offset.x), Mathf.Abs(offset.y));
        return Mathf.Clamp01((distanceMetric - borderStartDistance) / Mathf.Max(borderThickness, 0.001f));
    }

    public bool IsInBorderZone(Vector3 worldPosition)
    {
        return GetBorderFactor(worldPosition) > 0f;
    }

    public float GetWaveStrengthMultiplier(Vector3 worldPosition)
    {
        float danger = GetDanger01(worldPosition);
        float easedDanger = SmoothStep01(danger);
        return Mathf.Lerp(1f, Mathf.Max(1f, maxWaveStrengthMultiplier), easedDanger);
    }

    public float GetMaximumWaveStrengthMultiplier()
    {
        return Mathf.Max(1f, maxWaveStrengthMultiplier);
    }

    public HazardBiome GetInteriorHazardBiome(Vector3 worldPosition)
    {
        float patchNoise = GetHazardBiomePatchNoise(worldPosition);

        bool hasIcebergs = icebergPrefabs.Length > 0;
        bool hasTrees = treePrefabs.Length > 0;
        bool hasMagma = magmaPrefabs.Length > 0;

        if (!hasIcebergs && !hasTrees && !hasMagma)
        {
            return HazardBiome.Iceberg;
        }

        float icebergShare = hasIcebergs ? Mathf.Clamp01(icebergBiomeShare) : 0f;
        float treeShare = hasTrees ? Mathf.Clamp01(treeBiomeShare) : 0f;
        float magmaShare = hasMagma ? Mathf.Max(0f, 1f - icebergShare - treeShare) : 0f;

        float totalShare = icebergShare + treeShare + magmaShare;
        if (totalShare <= 0.0001f)
        {
            if (hasIcebergs)
            {
                return HazardBiome.Iceberg;
            }

            if (hasTrees)
            {
                return HazardBiome.Tree;
            }

            return HazardBiome.Magma;
        }

        icebergShare /= totalShare;
        treeShare /= totalShare;

        if (patchNoise < icebergShare && hasIcebergs)
        {
            return HazardBiome.Iceberg;
        }

        if (patchNoise < icebergShare + treeShare && hasTrees)
        {
            return HazardBiome.Tree;
        }

        if (hasMagma)
        {
            return HazardBiome.Magma;
        }

        return hasTrees ? HazardBiome.Tree : HazardBiome.Iceberg;
    }

    public float GetInteriorHazardDensity01(Vector3 worldPosition)
    {
        float patchNoise = GetHazardDensityPatchNoise(worldPosition);
        float danger = GetDanger01(worldPosition);
        float biasedNoise = Mathf.Lerp(patchNoise, 1f, Mathf.Clamp01(danger * distanceDensityBias));
        return Mathf.Lerp(biomeDensityMin, biomeDensityMax, biasedNoise);
    }

    public float EvaluateTerrainHeight(Vector2 worldXZ)
    {
        Vector2 centered = spawnPointCaptured
            ? worldXZ - new Vector2(spawnPoint.x, spawnPoint.z)
            : worldXZ;
        float danger = GetDanger01(new Vector3(worldXZ.x, 0f, worldXZ.y));

        float broadNoise = FractalNoise2D(centered * 0.0022f, worldSeed + 11);
        float clusterNoise = FractalNoise2D(centered * 0.0045f, worldSeed + 37);
        float ridgeNoise = FractalNoise2D(centered * 0.011f, worldSeed + 71);
        float detailNoise = FractalNoise2D(centered * 0.026f, worldSeed + 113) * 2f - 1f;

        float islandSignal = (broadNoise * 0.7f) + (clusterNoise * 0.3f);
        float islandMask = Mathf.Clamp01(Mathf.InverseLerp(
            islandThreshold - islandBlend,
            islandThreshold + islandBlend,
            islandSignal));
        islandMask = SmoothStep01(islandMask);

        float oceanFloor = terrainBaseHeight - Mathf.Lerp(deepOceanDepth * 0.8f, deepOceanDepth, danger);
        float islandShape = Mathf.Max(0f, ridgeNoise * 1.25f - 0.2f);
        float farWaterIslandScale = Mathf.Lerp(1f, outerIslandHeightMultiplier, SmoothStep01(danger));
        float farWaterDetailScale = Mathf.Lerp(1f, outerShorelineDetailMultiplier, SmoothStep01(danger));
        float islandHeight = islandMask * islandRise * (0.35f + islandShape) * farWaterIslandScale;
        float shorelineDetail = detailNoise * Mathf.Lerp(0.8f, 3.2f, islandMask) * farWaterDetailScale;
        float terrainHeight = oceanFloor + islandHeight + shorelineDetail;

        float safeRadius = Mathf.Max(spawnSafeRadius, 0f);
        if (safeRadius > 0f)
        {
            float spawnDistance = centered.magnitude;
            float flattenFactor = 1f - Mathf.Clamp01(spawnDistance / safeRadius);
            if (flattenFactor > 0f)
            {
                float flattenedFloor = waterHeight - (deepOceanDepth * 0.9f);
                terrainHeight = Mathf.Lerp(terrainHeight, flattenedFloor, SmoothStep01(flattenFactor));
            }
        }

        float borderFactor = GetBorderFactor(new Vector3(worldXZ.x, 0f, worldXZ.y));
        if (borderFactor > 0f)
        {
            float borderShelfHeight = waterHeight - Mathf.Lerp(8f, 18f, borderFactor);
            terrainHeight = Mathf.Min(terrainHeight, borderShelfHeight);
        }

        return terrainHeight;
    }

    public bool ShouldSpawnInteriorIcebergs(Vector3 worldPosition)
    {
        if (!spawnPointCaptured)
        {
            return false;
        }

        Vector2 offset = new Vector2(
            worldPosition.x - spawnPoint.x,
            worldPosition.z - spawnPoint.z);
        float distanceMetric = Mathf.Max(Mathf.Abs(offset.x), Mathf.Abs(offset.y));
        return distanceMetric >= interiorIcebergStartDistance && icebergPrefabs.Length > 0;
    }

    public int GetInteriorIcebergCount(Vector3 chunkCenter)
    {
        float danger = GetDanger01(chunkCenter);
        float density = GetInteriorHazardDensity01(chunkCenter);
        HazardBiome biome = GetInteriorHazardBiome(chunkCenter);
        float biomeCountMultiplier = GetInteriorHazardBiomeCountMultiplier(chunkCenter);
        float baseCount = Mathf.Lerp(2f, 6f, danger) * biomeCountMultiplier;
        float densityCountMultiplier = Mathf.Lerp(0.75f, 1.2f, density);
        int maxCount = biome == HazardBiome.Tree
            ? Mathf.Max(6, treeBiomeMaxCount)
            : 6;
        return Mathf.Clamp(Mathf.RoundToInt(baseCount * densityCountMultiplier), 1, maxCount);
    }

    public int GetBorderIcebergCount(Vector3 chunkCenter)
    {
        float borderFactor = Mathf.Max(GetBorderFactor(chunkCenter), 0.1f);
        return Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(6f, 14f, borderFactor)), 6, 14);
    }

    public float GetInteriorIcebergSpacing(Vector3 chunkCenter)
    {
        float danger = GetDanger01(chunkCenter);
        float density = GetInteriorHazardDensity01(chunkCenter);
        HazardBiome biome = GetInteriorHazardBiome(chunkCenter);
        float biomeSpacingMultiplier = GetInteriorHazardBiomeSpacingMultiplier(chunkCenter);
        float baseSpacing = Mathf.Lerp(30f, 18f, danger);
        float densitySpacingMultiplier = Mathf.Lerp(1.15f, 0.82f, density);
        float minSpacing = biome == HazardBiome.Tree
            ? Mathf.Max(1f, treeBiomeMinSpacing)
            : 14f;
        return Mathf.Max(baseSpacing * densitySpacingMultiplier * biomeSpacingMultiplier, minSpacing);
    }

    public float GetBorderIcebergSpacing(Vector3 chunkCenter)
    {
        float borderFactor = Mathf.Max(GetBorderFactor(chunkCenter), 0.1f);
        return Mathf.Lerp(22f, 12f, borderFactor);
    }

    void ResolveSceneReferences()
    {
        if (sceneWorld == null)
        {
            sceneWorld = FindFirstObjectByType<World>();
        }

        if (generatedChunksRoot == null)
        {
            Transform child = transform.Find("GeneratedChunks");
            if (child != null)
            {
                generatedChunksRoot = child;
            }
        }

        if (generatedHazardsRoot == null)
        {
            Transform child = transform.Find("GeneratedHazards");
            if (child != null)
            {
                generatedHazardsRoot = child;
            }
        }

        if (generatedEffectsRoot == null)
        {
            Transform child = transform.Find("GeneratedEffects");
            if (child != null)
            {
                generatedEffectsRoot = child;
            }
        }
    }

    bool TryResolveBoatRoot()
    {
        if (boatRoot != null)
        {
            return true;
        }

        if (Time.unscaledTime < nextBoatSearchTime)
        {
            return false;
        }

        nextBoatSearchTime = Time.unscaledTime + BoatSearchIntervalSeconds;

        GameObject namedRoot = GameObject.Find(boatRootName);
        if (namedRoot != null)
        {
            boatRoot = namedRoot.transform;
            return true;
        }

        ShipController controller = FindFirstObjectByType<ShipController>();
        if (controller != null)
        {
            boatRoot = controller.transform;
            return true;
        }

        return false;
    }

    void CaptureSpawnPointIfNeeded()
    {
        if (spawnPointCaptured || boatRoot == null)
        {
            return;
        }

        spawnPoint = boatRoot.position;
        spawnPointCaptured = true;
    }

    void ConfigureSceneWorldIfNeeded()
    {
        if (worldConfigured || sceneWorld == null)
        {
            return;
        }

        Transform groundTransform = sceneWorld.transform.Find(GroundObjectName);
        if (groundTransform != null)
        {
            groundTransform.gameObject.SetActive(false);
            MeshRenderer groundRenderer = groundTransform.GetComponent<MeshRenderer>();
            if (groundRenderer != null)
            {
                groundMaterial = groundRenderer.sharedMaterial;
            }
        }

        activeWater = sceneWorld.GetComponentInChildren<UrpLowPolyWater>(true);
        if (activeWater != null)
        {
            MeshRenderer waterRenderer = activeWater.GetComponent<MeshRenderer>();
            Material waterMaterial = activeWater.GetComponent<MeshRenderer>() != null
                ? waterRenderer.sharedMaterial
                : null;
            float waterSpan = Mathf.Max(ChunkSize * ((GetLoadedChunkRadius() * 2f) + WaterPaddingChunks), 200f);
            activeWater.SyncFromWorld(
                Mathf.Max(activeWater.resolution, ChunkResolution),
                new Vector2(waterSpan, waterSpan),
                activeWater.baseHeight,
                waterMaterial);
            waterHeight = activeWater.baseHeight;
        }

        worldConfigured = true;
    }

    void FollowBoatWithWorldSurface()
    {
        if (sceneWorld == null || boatRoot == null)
        {
            return;
        }

        Vector3 worldPosition = sceneWorld.transform.position;
        worldPosition.x = boatRoot.position.x;
        worldPosition.z = boatRoot.position.z;
        sceneWorld.transform.position = worldPosition;
    }

    void RefreshLoadedChunks()
    {
        if (boatRoot == null || generatedChunksRoot == null || generatedHazardsRoot == null)
        {
            return;
        }

        Vector2Int centerCoord = WorldToChunkCoord(boatRoot.position);
        HashSet<Vector2Int> neededCoords = new HashSet<Vector2Int>();

        int loadedChunkRadius = GetLoadedChunkRadius();

        for (int z = -loadedChunkRadius; z <= loadedChunkRadius; z++)
        {
            for (int x = -loadedChunkRadius; x <= loadedChunkRadius; x++)
            {
                Vector2Int chunkCoord = new Vector2Int(centerCoord.x + x, centerCoord.y + z);
                neededCoords.Add(chunkCoord);

                if (loadedChunks.ContainsKey(chunkCoord))
                {
                    continue;
                }

                CreateChunk(chunkCoord);
            }
        }

        List<Vector2Int> loadedCoords = new List<Vector2Int>(loadedChunks.Keys);
        for (int i = 0; i < loadedCoords.Count; i++)
        {
            Vector2Int chunkCoord = loadedCoords[i];
            if (neededCoords.Contains(chunkCoord))
            {
                continue;
            }

            if (loadedChunks.TryGetValue(chunkCoord, out OpenWorldChunk chunk))
            {
                loadedChunks.Remove(chunkCoord);
                if (chunk != null)
                {
                    Destroy(chunk.gameObject);
                }
            }
        }
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
        return Mathf.Max(ChunkSize * (activeChunkRadius + 0.5f), ChunkSize);
    }

    int GetLoadedChunkRadius()
    {
        return Mathf.Max(activeChunkRadius + Mathf.Max(preloadChunkBuffer, 0), 0);
    }

    void CreateChunk(Vector2Int chunkCoord)
    {
        GameObject chunkObject = new GameObject("Chunk_" + chunkCoord.x + "_" + chunkCoord.y);
        chunkObject.transform.SetParent(generatedChunksRoot, false);

        OpenWorldChunk chunk = chunkObject.AddComponent<OpenWorldChunk>();
        chunk.Initialize(this, chunkCoord);
        loadedChunks.Add(chunkCoord, chunk);
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

    float SampleChunk01(Vector3 worldPosition, int seed)
    {
        Vector2 centered = spawnPointCaptured
            ? new Vector2(worldPosition.x - spawnPoint.x, worldPosition.z - spawnPoint.z)
            : new Vector2(worldPosition.x, worldPosition.z);
        Vector2 gridPoint = centered / Mathf.Max(ChunkSize, 1f);
        return Hash2D(gridPoint, worldSeed + seed);
    }

    public bool IsInsideHazardSpawnExclusion(Vector3 worldPosition)
    {
        if (!spawnPointCaptured)
        {
            return false;
        }

        Vector2 offset = new Vector2(
            worldPosition.x - spawnPoint.x,
            worldPosition.z - spawnPoint.z);
        float safeRadius = Mathf.Max(0f, hazardSpawnExclusionRadius);
        return offset.sqrMagnitude < safeRadius * safeRadius;
    }

    float GetHazardBiomePatchNoise(Vector3 worldPosition)
    {
        Vector2 centered = spawnPointCaptured
            ? new Vector2(worldPosition.x - spawnPoint.x, worldPosition.z - spawnPoint.z)
            : new Vector2(worldPosition.x, worldPosition.z);
        float patchScaleMultiplier = GetBiomePatchScaleMultiplier(centered);
        float effectivePatchScale = biomePatchScale * patchScaleMultiplier;
        float primary = FractalNoise2D(centered * effectivePatchScale + new Vector2(11.7f, 29.3f), worldSeed + 211);
        float secondary = FractalNoise2D(centered * (effectivePatchScale * 1.9f) + new Vector2(57.1f, 83.9f), worldSeed + 307);
        return Mathf.Clamp01((primary * 0.7f) + (secondary * 0.3f));
    }

    float GetHazardDensityPatchNoise(Vector3 worldPosition)
    {
        Vector2 centered = spawnPointCaptured
            ? new Vector2(worldPosition.x - spawnPoint.x, worldPosition.z - spawnPoint.z)
            : new Vector2(worldPosition.x, worldPosition.z);
        float primary = FractalNoise2D(centered * biomeDensityPatchScale + new Vector2(73.2f, 17.6f), worldSeed + 401);
        float secondary = FractalNoise2D(centered * (biomeDensityPatchScale * 1.7f) + new Vector2(19.4f, 61.8f), worldSeed + 503);
        return Mathf.Clamp01((primary * 0.65f) + (secondary * 0.35f));
    }

    float GetBiomePatchScaleMultiplier(Vector2 centeredWorldXZ)
    {
        float sizeNoise = FractalNoise2D(centeredWorldXZ * biomePatchSizeNoiseScale + new Vector2(41.3f, 13.9f), worldSeed + 353);
        return Mathf.Lerp(
            Mathf.Min(biomePatchScaleMultiplierRange.x, biomePatchScaleMultiplierRange.y),
            Mathf.Max(biomePatchScaleMultiplierRange.x, biomePatchScaleMultiplierRange.y),
            sizeNoise);
    }

    float GetInteriorHazardBiomeCountMultiplier(Vector3 worldPosition)
    {
        return GetInteriorHazardBiome(worldPosition) == HazardBiome.Tree
            ? Mathf.Max(1f, treeBiomeCountMultiplier)
            : 1f;
    }

    float GetInteriorHazardBiomeSpacingMultiplier(Vector3 worldPosition)
    {
        return GetInteriorHazardBiome(worldPosition) == HazardBiome.Tree
            ? Mathf.Clamp(treeBiomeSpacingMultiplier, 0.1f, 2f)
            : 1f;
    }

}
