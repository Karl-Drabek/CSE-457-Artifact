using UnityEngine;
using UnityEngine.Rendering;

public class SphereFace
{
    static readonly Vector3[] FaceDirections =
    {
        Vector3.up,
        Vector3.down,
        Vector3.left,
        Vector3.right,
        Vector3.forward,
        Vector3.back
    };

    readonly Mesh mesh;
    readonly int resolution;
    readonly float planetRadius;

    public SphereFace(Mesh mesh, int resolution, float planetRadius)
    {
        this.mesh = mesh;
        this.resolution = Mathf.Max(2, resolution);
        this.planetRadius = Mathf.Max(0.01f, planetRadius);
    }

    public void ConstructMesh()
    {
        int verticesPerFace = resolution * resolution;
        int trianglesPerFace = (resolution - 1) * (resolution - 1) * 6;
        int totalVertices = FaceDirections.Length * verticesPerFace;

        Vector3[] vertices = new Vector3[totalVertices];
        Vector3[] normals = new Vector3[totalVertices];
        Vector2[] uvs = new Vector2[totalVertices];
        int[] triangles = new int[FaceDirections.Length * trianglesPerFace];

        int triangleIndex = 0;

        for (int faceIndex = 0; faceIndex < FaceDirections.Length; faceIndex++)
        {
            Vector3 localUp = FaceDirections[faceIndex];
            Vector3 axisA = new Vector3(localUp.y, localUp.z, localUp.x);
            Vector3 axisB = Vector3.Cross(localUp, axisA);
            int vertexOffset = faceIndex * verticesPerFace;

            for (int y = 0; y < resolution; y++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    int i = x + y * resolution;
                    int vertexIndex = vertexOffset + i;
                    Vector2 percent = new Vector2(x, y) / (resolution - 1f);

                    Vector3 pointOnCube = localUp
                        + (percent.x - 0.5f) * 2f * axisA
                        + (percent.y - 0.5f) * 2f * axisB;
                    Vector3 pointOnSphere = PointOnCubeToPointOnSphere(pointOnCube);

                    vertices[vertexIndex] = pointOnSphere * planetRadius;
                    normals[vertexIndex] = pointOnSphere;
                    uvs[vertexIndex] = GetSphereUv(pointOnSphere);

                    if (x == resolution - 1 || y == resolution - 1)
                    {
                        continue;
                    }

                    triangles[triangleIndex++] = vertexIndex;
                    triangles[triangleIndex++] = vertexIndex + resolution + 1;
                    triangles[triangleIndex++] = vertexIndex + resolution;

                    triangles[triangleIndex++] = vertexIndex;
                    triangles[triangleIndex++] = vertexIndex + 1;
                    triangles[triangleIndex++] = vertexIndex + resolution + 1;
                }
            }
        }

        mesh.Clear();
        mesh.indexFormat = totalVertices > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.normals = normals;
        mesh.uv = uvs;
        mesh.RecalculateBounds();
    }

    static Vector3 PointOnCubeToPointOnSphere(Vector3 pointOnCube)
    {
        float x2 = pointOnCube.x * pointOnCube.x;
        float y2 = pointOnCube.y * pointOnCube.y;
        float z2 = pointOnCube.z * pointOnCube.z;

        return new Vector3(
            pointOnCube.x * Mathf.Sqrt(1f - (y2 + z2) * 0.5f + (y2 * z2) / 3f),
            pointOnCube.y * Mathf.Sqrt(1f - (x2 + z2) * 0.5f + (x2 * z2) / 3f),
            pointOnCube.z * Mathf.Sqrt(1f - (x2 + y2) * 0.5f + (x2 * y2) / 3f)
        );
    }

    static Vector2 GetSphereUv(Vector3 pointOnSphere)
    {
        float u = Mathf.Atan2(pointOnSphere.z, pointOnSphere.x) / (2f * Mathf.PI) + 0.5f;
        float v = Mathf.Asin(pointOnSphere.y) / Mathf.PI + 0.5f;
        return new Vector2(u, v);
    }
}
