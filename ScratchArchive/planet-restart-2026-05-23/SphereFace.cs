using UnityEngine;

public class SphereFace
{
    readonly Mesh mesh;
    readonly int resolution;
    readonly Vector3 localUp;
    readonly Vector3 axisA;
    readonly Vector3 axisB;
    readonly float planetRadius;

    public SphereFace(Mesh mesh, int resolution, Vector3 localUp, float planetRadius)
    {
        this.mesh = mesh;
        this.resolution = resolution;
        this.localUp = localUp;
        this.planetRadius = planetRadius;

        axisA = new Vector3(localUp.y, localUp.z, localUp.x);
        axisB = Vector3.Cross(localUp, axisA);
    }

    public void ConstructMesh()
    {
        Vector3[] vertices = new Vector3[resolution * resolution];
        Vector3[] normals = new Vector3[resolution * resolution];
        int[] triangles = new int[(resolution - 1) * (resolution - 1) * 6];
        int triIndex = 0;

        for (int y = 0; y < resolution; y++)
        {
            for (int x = 0; x < resolution; x++)
            {
                int i = x + y * resolution;
                Vector2 percent = new Vector2(x, y) / (resolution - 1f);
                Vector3 pointOnUnitCube = localUp
                    + (percent.x - 0.5f) * 2f * axisA
                    + (percent.y - 0.5f) * 2f * axisB;
                Vector3 pointOnUnitSphere = PointOnCubeToPointOnSphere(pointOnUnitCube);

                vertices[i] = pointOnUnitSphere * planetRadius;
                normals[i] = pointOnUnitSphere;

                if (x != resolution - 1 && y != resolution - 1)
                {
                    triangles[triIndex++] = i;
                    triangles[triIndex++] = i + resolution + 1;
                    triangles[triIndex++] = i + resolution;

                    triangles[triIndex++] = i;
                    triangles[triIndex++] = i + 1;
                    triangles[triIndex++] = i + resolution + 1;
                }
            }
        }

        mesh.Clear();
        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.normals = normals;
        mesh.RecalculateBounds();
    }

    Vector3 PointOnCubeToPointOnSphere(Vector3 pointOnUnitCube)
    {
        float x2 = pointOnUnitCube.x * pointOnUnitCube.x;
        float y2 = pointOnUnitCube.y * pointOnUnitCube.y;
        float z2 = pointOnUnitCube.z * pointOnUnitCube.z;

        return new Vector3(
            pointOnUnitCube.x * Mathf.Sqrt(1f - (y2 + z2) * 0.5f + (y2 * z2) / 3f),
            pointOnUnitCube.y * Mathf.Sqrt(1f - (x2 + z2) * 0.5f + (x2 * z2) / 3f),
            pointOnUnitCube.z * Mathf.Sqrt(1f - (x2 + y2) * 0.5f + (x2 * y2) / 3f)
        );
    }
}
