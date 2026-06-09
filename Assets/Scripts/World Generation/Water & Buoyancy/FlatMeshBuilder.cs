using UnityEngine;
using UnityEngine.Rendering;

public static class FlatMeshBuilder
{
    // Builds a flat XZ grid mesh with upward-facing normals.
    // Called by World when creating or resizing the generated ground plane.
    public static void BuildPlaneMesh(Mesh mesh, int resolution, Vector2 size, float height)
    {
        if (mesh == null)
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

        // Build a regular XZ grid so both ground and water can share the same topology.
        for (int y = 0; y < safeResolution; y++)
        {
            for (int x = 0; x < safeResolution; x++)
            {
                int i = x + y * safeResolution;
                Vector2 percent = new Vector2(x, y) / (safeResolution - 1f);

                float xPos = (percent.x - 0.5f) * clampedSize.x;
                float zPos = (percent.y - 0.5f) * clampedSize.y;

                vertices[i] = new Vector3(xPos, height, zPos);
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

        mesh.Clear();
        mesh.indexFormat = vertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.normals = normals;
        mesh.uv = uvs;
        mesh.RecalculateBounds();
    }
}
