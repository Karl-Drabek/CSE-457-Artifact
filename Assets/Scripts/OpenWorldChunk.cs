using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider))]
public class OpenWorldChunk : MonoBehaviour
{
    const float MinSpacing = 0.001f;
    const float WaterlineClearance = 1f;
    const float CandidatePadding = 8f;
    const float AdditionalSinkDepth = 10f;

    OpenWorldManager manager;
    Vector2Int chunkCoord;
    Transform hazardRoot;
    MeshFilter meshFilter;
    MeshRenderer meshRenderer;
    MeshCollider meshCollider;
    Mesh chunkMesh;

    public void Initialize(OpenWorldManager owner, Vector2Int coord)
    {
        manager = owner;
        chunkCoord = coord;
        name = "Chunk_" + coord.x + "_" + coord.y;

        EnsureComponents();
        CreateHazardRoot();
        BuildTerrain();
        SpawnHazards();
    }

    void OnDestroy()
    {
        if (hazardRoot != null)
        {
            Destroy(hazardRoot.gameObject);
        }
    }

    void EnsureComponents()
    {
        meshFilter = GetComponent<MeshFilter>();
        meshRenderer = GetComponent<MeshRenderer>();
        meshCollider = GetComponent<MeshCollider>();

        if (meshFilter.sharedMesh == null)
        {
            meshFilter.sharedMesh = new Mesh
            {
                name = "Open World Chunk Mesh"
            };
        }

        chunkMesh = meshFilter.sharedMesh;
        meshRenderer.sharedMaterial = manager != null ? manager.GroundMaterial : null;
    }

    void CreateHazardRoot()
    {
        if (manager == null || manager.GeneratedHazardsRoot == null)
        {
            return;
        }

        hazardRoot = new GameObject("Hazards_" + chunkCoord.x + "_" + chunkCoord.y).transform;
        hazardRoot.SetParent(manager.GeneratedHazardsRoot, false);
    }

    void BuildTerrain()
    {
        if (manager == null || chunkMesh == null)
        {
            return;
        }

        int resolution = manager.ChunkResolution;
        float chunkSize = manager.ChunkSize;
        int vertexCount = resolution * resolution;

        Vector3[] vertices = new Vector3[vertexCount];
        Vector3[] normals = new Vector3[vertexCount];
        Vector2[] uvs = new Vector2[vertexCount];
        int[] triangles = new int[(resolution - 1) * (resolution - 1) * 6];
        float normalSampleOffset = chunkSize / Mathf.Max((resolution - 1f) * 1.5f, 1f);
        Vector3 chunkCenter = manager.GetChunkCenter(chunkCoord);
        transform.position = chunkCenter;

        int triangleIndex = 0;

        for (int z = 0; z < resolution; z++)
        {
            for (int x = 0; x < resolution; x++)
            {
                int index = x + (z * resolution);
                Vector2 percent = new Vector2(x, z) / (resolution - 1f);
                float localX = (percent.x - 0.5f) * chunkSize;
                float localZ = (percent.y - 0.5f) * chunkSize;

                Vector2 worldXZ = new Vector2(chunkCenter.x + localX, chunkCenter.z + localZ);
                float height = manager.EvaluateTerrainHeight(worldXZ);
                vertices[index] = new Vector3(localX, height, localZ);
                normals[index] = EvaluateTerrainNormal(worldXZ, normalSampleOffset);
                uvs[index] = percent;

                if (x == resolution - 1 || z == resolution - 1)
                {
                    continue;
                }

                triangles[triangleIndex++] = index;
                triangles[triangleIndex++] = index + resolution;
                triangles[triangleIndex++] = index + resolution + 1;

                triangles[triangleIndex++] = index;
                triangles[triangleIndex++] = index + resolution + 1;
                triangles[triangleIndex++] = index + 1;
            }
        }

        chunkMesh.Clear();
        chunkMesh.indexFormat = vertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        chunkMesh.vertices = vertices;
        chunkMesh.triangles = triangles;
        chunkMesh.normals = normals;
        chunkMesh.uv = uvs;
        chunkMesh.RecalculateBounds();

        meshCollider.sharedMesh = null;
        meshCollider.sharedMesh = chunkMesh;
    }

    Vector3 EvaluateTerrainNormal(Vector2 worldXZ, float sampleOffset)
    {
        float heightLeft = manager.EvaluateTerrainHeight(worldXZ + new Vector2(-sampleOffset, 0f));
        float heightRight = manager.EvaluateTerrainHeight(worldXZ + new Vector2(sampleOffset, 0f));
        float heightDown = manager.EvaluateTerrainHeight(worldXZ + new Vector2(0f, -sampleOffset));
        float heightUp = manager.EvaluateTerrainHeight(worldXZ + new Vector2(0f, sampleOffset));

        Vector3 tangentX = new Vector3(sampleOffset * 2f, heightRight - heightLeft, 0f);
        Vector3 tangentZ = new Vector3(0f, heightUp - heightDown, sampleOffset * 2f);
        return Vector3.Cross(tangentZ, tangentX).normalized;
    }

    void SpawnHazards()
    {
        if (manager == null || hazardRoot == null)
        {
            return;
        }

        Vector3 chunkCenter = manager.GetChunkCenter(chunkCoord);
        if (manager.IsInBorderZone(chunkCenter))
        {
            SpawnIcebergs(manager.GetBorderIcebergCount(chunkCenter), manager.GetBorderIcebergSpacing(chunkCenter), true);
            return;
        }

        if (!manager.ShouldSpawnInteriorIcebergs(chunkCenter))
        {
            return;
        }

        SpawnIcebergs(manager.GetInteriorIcebergCount(chunkCenter), manager.GetInteriorIcebergSpacing(chunkCenter), false);
    }

    void SpawnIcebergs(int requestedCount, float minSpacing, bool isBorderIceberg)
    {
        if (requestedCount <= 0)
        {
            return;
        }

        float chunkSize = manager.ChunkSize;
        Vector3 chunkCenter = manager.GetChunkCenter(chunkCoord);
        List<Vector3> acceptedPositions = new List<Vector3>();
        int maxAttempts = Mathf.Max(requestedCount * 8, 8);

        for (int attempt = 0; attempt < maxAttempts && acceptedPositions.Count < requestedCount; attempt++)
        {
            float offsetX = Mathf.Lerp(
                -(chunkSize * 0.5f) + CandidatePadding,
                (chunkSize * 0.5f) - CandidatePadding,
                Hash01(attempt, 17));
            float offsetZ = Mathf.Lerp(
                -(chunkSize * 0.5f) + CandidatePadding,
                (chunkSize * 0.5f) - CandidatePadding,
                Hash01(attempt, 53));

            Vector3 position = new Vector3(
                chunkCenter.x + offsetX,
                manager.WaterHeight,
                chunkCenter.z + offsetZ);
            Vector2 worldXZ = new Vector2(position.x, position.z);
            float terrainHeight = manager.EvaluateTerrainHeight(worldXZ);

            if (terrainHeight >= manager.WaterHeight - WaterlineClearance)
            {
                continue;
            }

            if (manager.IsInsideHazardSpawnExclusion(position))
            {
                continue;
            }

            if (!PassesSpacing(position, acceptedPositions, minSpacing))
            {
                continue;
            }

            acceptedPositions.Add(position);
            SpawnHazardInstance(attempt, position, isBorderIceberg);
        }
    }

    bool PassesSpacing(Vector3 position, List<Vector3> positions, float minSpacing)
    {
        float safeSpacing = Mathf.Max(minSpacing, MinSpacing);
        for (int i = 0; i < positions.Count; i++)
        {
            if ((positions[i] - position).sqrMagnitude < safeSpacing * safeSpacing)
            {
                return false;
            }
        }

        return true;
    }

    void SpawnHazardInstance(int attempt, Vector3 position, bool isBorderIceberg)
    {
        GameObject[] prefabs = SelectHazardPool(
            isBorderIceberg,
            attempt,
            position,
            out Vector2 scaleRange,
            out bool spawnedAsBorderIceberg,
            out string hazardPrefix);
        if (prefabs.Length == 0)
        {
            return;
        }

        int prefabIndex = Mathf.Clamp(Mathf.FloorToInt(Hash01(attempt, 89) * prefabs.Length), 0, prefabs.Length - 1);
        GameObject prefab = prefabs[prefabIndex];
        if (prefab == null)
        {
            return;
        }

        Quaternion rotation = Quaternion.Euler(
            0f,
            Hash01(attempt, 131) * 360f,
            0f);
        GameObject iceberg = Instantiate(prefab, position, rotation, hazardRoot);
        iceberg.name = spawnedAsBorderIceberg
            ? "Border_" + prefab.name
            : hazardPrefix + prefab.name;

        float scale = Mathf.Lerp(scaleRange.x, scaleRange.y, Hash01(attempt, 173));
        iceberg.transform.localScale *= scale;

        EnsureCollider(iceberg);

        if (TryGetRendererBounds(iceberg, out Bounds bounds))
        {
            float burialFactor = spawnedAsBorderIceberg
                ? Mathf.Lerp(0.3f, 0.5f, Hash01(attempt, 197))
                : Mathf.Lerp(0.2f, 0.35f, Hash01(attempt, 197));
            float extraSinkDepth = IsPlatformHazard(prefab.name) ? 0f : AdditionalSinkDepth;
            Vector3 adjustedPosition = iceberg.transform.position;
            adjustedPosition.y = manager.WaterHeight - (bounds.extents.y * burialFactor) - extraSinkDepth;
            iceberg.transform.position = adjustedPosition;
        }

        if (spawnedAsBorderIceberg)
        {
            iceberg.AddComponent<OpenWorldBorderIceberg>();
        }
    }

    GameObject[] SelectHazardPool(
        bool isBorderIceberg,
        int attempt,
        Vector3 position,
        out Vector2 scaleRange,
        out bool spawnedAsBorderIceberg,
        out string hazardPrefix)
    {
        if (isBorderIceberg)
        {
            scaleRange = manager.IcebergScaleRange;
            spawnedAsBorderIceberg = true;
            hazardPrefix = "Iceberg_";
            return manager.IcebergPrefabs;
        }

        GameObject[] icebergPrefabs = manager.IcebergPrefabs;
        GameObject[] treePrefabs = manager.TreePrefabs;
        GameObject[] magmaPrefabs = manager.MagmaPrefabs;
        if (icebergPrefabs.Length == 0)
        {
            scaleRange = Vector2.one;
            spawnedAsBorderIceberg = false;
            hazardPrefix = "Hazard_";
            return System.Array.Empty<GameObject>();
        }

        if (treePrefabs.Length == 0 && magmaPrefabs.Length == 0)
        {
            scaleRange = manager.IcebergScaleRange;
            spawnedAsBorderIceberg = false;
            hazardPrefix = "Iceberg_";
            return icebergPrefabs;
        }

        OpenWorldManager.HazardBiome biome = manager.GetInteriorHazardBiome(position);

        bool useMagma = biome == OpenWorldManager.HazardBiome.Magma && magmaPrefabs.Length > 0;
        bool useTree = biome == OpenWorldManager.HazardBiome.Tree && treePrefabs.Length > 0;

        scaleRange = useMagma
            ? manager.MagmaScaleRange
            : (useTree ? manager.TreeScaleRange : manager.IcebergScaleRange);
        spawnedAsBorderIceberg = false;
        hazardPrefix = useMagma ? "Magma_" : (useTree ? "Tree_" : "Iceberg_");
        return useMagma ? magmaPrefabs : (useTree ? treePrefabs : icebergPrefabs);
    }

    bool IsPlatformHazard(string prefabName)
    {
        return prefabName.Contains("Platform", System.StringComparison.OrdinalIgnoreCase);
    }

    void EnsureCollider(GameObject iceberg)
    {
        if (iceberg.GetComponentInChildren<Collider>() != null)
        {
            return;
        }

        MeshFilter meshFilter = iceberg.GetComponentInChildren<MeshFilter>();
        if (meshFilter == null || meshFilter.sharedMesh == null)
        {
            return;
        }

        MeshCollider collider = iceberg.AddComponent<MeshCollider>();
        collider.sharedMesh = meshFilter.sharedMesh;
    }

    bool TryGetRendererBounds(GameObject target, out Bounds bounds)
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

    float Hash01(int attempt, int salt)
    {
        float seed = (chunkCoord.x * 127.1f)
            + (chunkCoord.y * 311.7f)
            + (attempt * 17.17f)
            + (salt * 29.29f)
            + (manager.WorldSeed * 0.113f);
        return Mathf.Repeat(Mathf.Sin(seed) * 43758.5453f, 1f);
    }
}
