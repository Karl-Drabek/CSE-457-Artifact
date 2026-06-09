using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

[ExecuteAlways]
[AddComponentMenu("World/Procedural Ground Surface")]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class ProceduralGroundSurface : MonoBehaviour
{
    [SerializeField, HideInInspector, Range(2, 256)]
    public int resolution = 32;

    [SerializeField, HideInInspector]
    public Vector2 size = new Vector2(10f, 10f);

    [FormerlySerializedAs("seaLevel")]
    [SerializeField, HideInInspector]
    public float baseHeight = -1.35f;

    MeshFilter meshFilter;
    Mesh generatedMesh;

    // Called by Unity before the first frame if the component starts enabled.
    // Caches component references and ensures the shared terrain mesh exists.
    void Awake()
    {
        Initialize();
    }

    // Called by Unity when the component becomes active.
    // Keeps the existing mesh and only creates one if this object has none yet.
    void OnEnable()
    {
        Initialize();
        RebuildMeshIfMissing();
    }

    // Called by Unity in the editor when serialized values change.
    // Keeps the existing mesh stable so scene edits do not constantly regenerate the ground.
    void OnValidate()
    {
        Initialize();
        RebuildMeshIfMissing();
    }

    // Called by World whenever the generated ground object is rebuilt.
    // Copies world-owned settings into this component before rebuilding the terrain mesh.
    public void SyncFromWorld(int newResolution, Vector2 newSize, float newBaseHeight)
    {
        resolution = Mathf.Max(2, newResolution);
        size = new Vector2(
            Mathf.Max(0.01f, newSize.x),
            Mathf.Max(0.01f, newSize.y));
        baseHeight = newBaseHeight;

        Initialize();
        RebuildMesh();
    }

    [ContextMenu("Rebuild Ground Mesh")]
    void RebuildGroundMesh()
    {
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

    void RebuildMeshIfMissing()
    {
        if (generatedMesh == null || generatedMesh.vertexCount == 0)
        {
            RebuildMesh();
        }
    }

    // Rebuilds the terrain mesh from the current procedural height field.
    // Called after initialization whenever resolution, size, height, or noise settings change.
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
        int triangleIndex = 0;

        for (int y = 0; y < safeResolution; y++)
        {
            for (int x = 0; x < safeResolution; x++)
            {
                int i = x + y * safeResolution;
                Vector2 percent = new Vector2(x, y) / (safeResolution - 1f);

                float xPos = (percent.x - 0.5f) * clampedSize.x;
                float zPos = (percent.y - 0.5f) * clampedSize.y;
                float terrainHeight = baseHeight;

                vertices[i] = new Vector3(xPos, terrainHeight, zPos);
                normals[i] = Vector3.up;
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

}
