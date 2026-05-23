using UnityEngine;
using UnityEngine.Rendering;

[ExecuteAlways]
[AddComponentMenu("World/Procedural Ground Surface")]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class ProceduralGroundSurface : MonoBehaviour
{
    [Header("Generated Plane")]
    [Range(2, 256)]
    public int resolution = 32;

    public Vector2 size = new Vector2(10f, 10f);

    public float seaLevel = 0.05f;

    [Header("Terrain Shape")]
    public float baseOceanDepth = 1.4f;

    public float broadNoiseScale = 0.18f;

    public float broadNoiseStrength = 1.6f;

    public float detailNoiseScale = 0.55f;

    public float detailNoiseStrength = 0.35f;

    [Header("Normals")]
    [Min(0.001f)]
    public float normalSampleDistance = 0.15f;

    MeshFilter meshFilter;
    Mesh generatedMesh;

    // Called by Unity before the first frame if the component starts enabled.
    // Caches component references and ensures the shared terrain mesh exists.
    void Awake()
    {
        Initialize();
    }

    // Called by Unity when the component becomes active.
    // Rebuilds the terrain immediately so edit-mode and play-mode activation stay in sync.
    void OnEnable()
    {
        Initialize();
        RebuildMesh();
    }

    // Called by Unity in the editor when serialized values change.
    // Rebuilds the terrain mesh so procedural parameter tweaks preview in the Inspector.
    void OnValidate()
    {
        Initialize();
        RebuildMesh();
    }

    // Called by World whenever the generated ground object is rebuilt.
    // Copies world-owned settings into this component before rebuilding the terrain mesh.
    public void SyncFromWorld(int newResolution, Vector2 newSize, float newSeaLevel)
    {
        resolution = Mathf.Max(2, newResolution);
        size = new Vector2(
            Mathf.Max(0.01f, newSize.x),
            Mathf.Max(0.01f, newSize.y));
        seaLevel = newSeaLevel;

        Initialize();
        RebuildMesh();
    }

    // Shared setup path used by Unity lifecycle callbacks and World.SyncFromWorld.
    // Ensures the MeshFilter and generated shared mesh are ready for terrain output.
    void Initialize()
    {
        meshFilter = GetComponent<MeshFilter>();
        if (meshFilter == null)
        {
            return;
        }

        if (meshFilter.sharedMesh == null)
        {
            meshFilter.sharedMesh = new Mesh
            {
                name = "Ground Mesh"
            };
        }

        generatedMesh = meshFilter.sharedMesh;
    }

    // Rebuilds the terrain mesh from the current procedural height field.
    // Called after initialization whenever resolution, size, sea level, or noise settings change.
    void RebuildMesh()
    {
        if (generatedMesh == null)
        {
            return;
        }

        int safeResolution = Mathf.Max(2, resolution);
        int vertexCount = safeResolution * safeResolution;

        Vector3[] vertices = new Vector3[vertexCount];
        Vector3[] normals = new Vector3[vertexCount];
        Vector2[] uvs = new Vector2[vertexCount];
        int[] triangles = new int[(safeResolution - 1) * (safeResolution - 1) * 6];

        Vector2 clampedSize = new Vector2(
            Mathf.Max(0.01f, size.x),
            Mathf.Max(0.01f, size.y));
        float sampleOffset = Mathf.Max(0.001f, normalSampleDistance);

        int triangleIndex = 0;

        for (int y = 0; y < safeResolution; y++)
        {
            for (int x = 0; x < safeResolution; x++)
            {
                int i = x + y * safeResolution;
                Vector2 percent = new Vector2(x, y) / (safeResolution - 1f);

                float xPos = (percent.x - 0.5f) * clampedSize.x;
                float zPos = (percent.y - 0.5f) * clampedSize.y;
                float terrainHeight = EvaluateTerrainHeight(xPos, zPos);

                vertices[i] = new Vector3(xPos, terrainHeight, zPos);
                normals[i] = EvaluateTerrainNormal(xPos, zPos, sampleOffset);
                uvs[i] = percent;

                if (x == safeResolution - 1 || y == safeResolution - 1)
                {
                    continue;
                }

                triangles[triangleIndex++] = i;
                triangles[triangleIndex++] = i + safeResolution;
                triangles[triangleIndex++] = i + safeResolution + 1;

                triangles[triangleIndex++] = i;
                triangles[triangleIndex++] = i + safeResolution + 1;
                triangles[triangleIndex++] = i + 1;
            }
        }

        generatedMesh.Clear();
        generatedMesh.indexFormat = vertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        generatedMesh.vertices = vertices;
        generatedMesh.triangles = triangles;
        generatedMesh.normals = normals;
        generatedMesh.uv = uvs;
        generatedMesh.RecalculateBounds();
    }

    // Evaluates the static terrain height field at a local-space XZ position.
    // Called while rebuilding each ground vertex and again during normal sampling around that point.
    float EvaluateTerrainHeight(float x, float z)
    {
        Vector2 position = new Vector2(x, z);

        // Keep the function easy to read:
        // start below sea level, add a broad shape layer, then add a smaller detail layer.
        float broadShape = FractalNoise2D(position * broadNoiseScale) * 2f - 1f;
        float detailShape = FractalNoise2D(position * detailNoiseScale + new Vector2(37.1f, 61.7f)) * 2f - 1f;

        float terrainHeight = seaLevel - baseOceanDepth;
        terrainHeight += broadShape * broadNoiseStrength;
        terrainHeight += detailShape * detailNoiseStrength;
        return terrainHeight;
    }

    // Approximates the terrain normal by sampling the height field around a local-space position.
    // Called while rebuilding the mesh so lighting follows cliffs, slopes, and island shorelines.
    Vector3 EvaluateTerrainNormal(float x, float z, float sampleOffset)
    {
        float heightLeft = EvaluateTerrainHeight(x - sampleOffset, z);
        float heightRight = EvaluateTerrainHeight(x + sampleOffset, z);
        float heightDown = EvaluateTerrainHeight(x, z - sampleOffset);
        float heightUp = EvaluateTerrainHeight(x, z + sampleOffset);

        Vector3 tangentX = new Vector3(2f * sampleOffset, heightRight - heightLeft, 0f);
        Vector3 tangentZ = new Vector3(0f, heightUp - heightDown, 2f * sampleOffset);
        return Vector3.Cross(tangentZ, tangentX).normalized;
    }

    // Adds several octaves of smooth value noise together for broad shapes plus smaller variation.
    // Called by EvaluateTerrainHeight for the two terrain layers.
    static float FractalNoise2D(Vector2 point)
    {
        float total = 0f;
        float amplitude = 0.5f;
        float amplitudeSum = 0f;
        Vector2 samplePoint = point;

        for (int octave = 0; octave < 4; octave++)
        {
            total += ValueNoise2D(samplePoint) * amplitude;
            amplitudeSum += amplitude;
            samplePoint = samplePoint * 2.01f + new Vector2(19.19f, 47.47f);
            amplitude *= 0.5f;
        }

        return total / Mathf.Max(amplitudeSum, 0.0001f);
    }

    // Computes a single octave of smooth value noise from a 2D point.
    // Called internally by FractalNoise2D for each octave accumulation step.
    static float ValueNoise2D(Vector2 point)
    {
        Vector2 cell = new Vector2(
            Mathf.Floor(point.x),
            Mathf.Floor(point.y));
        Vector2 local = point - cell;
        local = new Vector2(
            SmoothHermite01(local.x),
            SmoothHermite01(local.y));

        float n00 = Hash2D(cell);
        float n10 = Hash2D(cell + new Vector2(1f, 0f));
        float n01 = Hash2D(cell + new Vector2(0f, 1f));
        float n11 = Hash2D(cell + new Vector2(1f, 1f));

        float nx0 = Mathf.Lerp(n00, n10, local.x);
        float nx1 = Mathf.Lerp(n01, n11, local.x);
        return Mathf.Lerp(nx0, nx1, local.y);
    }

    // Hermite smoothing curve used to soften interpolation between value-noise lattice points.
    // Called by ValueNoise2D before blending the sampled cell corners.
    static float SmoothHermite01(float t)
    {
        return t * t * (3f - 2f * t);
    }

    // Generates a stable pseudo-random value for a 2D lattice point.
    // Called by ValueNoise2D when seeding each corner of the sampled cell.
    static float Hash2D(Vector2 point)
    {
        float dot = point.x * 127.1f + point.y * 311.7f;
        return Mathf.Repeat(Mathf.Sin(dot) * 43758.5453f, 1f);
    }
}
