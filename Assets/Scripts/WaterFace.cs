using UnityEngine;
using UnityEngine.Rendering;

public class WaterFace
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

    static readonly Vector3 PrimaryWaveDirection = new Vector3(0.82f, 0.24f, -0.52f).normalized;
    static readonly Vector3 SecondaryWaveDirection = new Vector3(-0.33f, 0.91f, 0.25f).normalized;
    static readonly Vector3 TertiaryWaveDirection = new Vector3(0.41f, -0.17f, 0.9f).normalized;

    readonly Mesh mesh;
    readonly int resolution;
    readonly float planetRadius;
    readonly float waterLevelOffset;
    readonly float waveAmplitude;
    readonly float secondaryWaveAmplitude;
    readonly float waveFrequency;
    readonly float secondaryWaveFrequency;
    readonly float waveSpeed;
    readonly float secondaryWaveSpeed;
    readonly bool useFlatShading;

    Vector3[] sphereDirections;
    int[] smoothTriangles;
    Vector2[] smoothUvs;

    Vector3[] smoothVertices;
    Vector3[] smoothNormals;

    Vector3[] flatVertices;
    Vector3[] flatNormals;
    Vector2[] flatUvs;
    int[] flatTriangles;

    public WaterFace(
        Mesh mesh,
        int resolution,
        float planetRadius,
        float waterLevelOffset,
        float waveAmplitude,
        float secondaryWaveAmplitude,
        float waveFrequency,
        float secondaryWaveFrequency,
        float waveSpeed,
        float secondaryWaveSpeed,
        bool useFlatShading)
    {
        this.mesh = mesh;
        this.resolution = Mathf.Max(2, resolution);
        this.planetRadius = Mathf.Max(0.01f, planetRadius);
        this.waterLevelOffset = waterLevelOffset;
        this.waveAmplitude = waveAmplitude;
        this.secondaryWaveAmplitude = secondaryWaveAmplitude;
        this.waveFrequency = waveFrequency;
        this.secondaryWaveFrequency = secondaryWaveFrequency;
        this.waveSpeed = waveSpeed;
        this.secondaryWaveSpeed = secondaryWaveSpeed;
        this.useFlatShading = useFlatShading;

        if (this.mesh != null)
        {
            this.mesh.MarkDynamic();
        }
    }

    public void ConstructMesh(float timeSeconds)
    {
        EnsureBaseBuffers();
        UpdateSmoothSurface(timeSeconds);

        if (useFlatShading)
        {
            BuildFlatSurface();
        }
        else
        {
            ApplySmoothSurface();
        }
    }

    void EnsureBaseBuffers()
    {
        int verticesPerFace = resolution * resolution;
        int trianglesPerFace = (resolution - 1) * (resolution - 1) * 6;
        int totalVertices = FaceDirections.Length * verticesPerFace;
        int totalTriangleIndices = FaceDirections.Length * trianglesPerFace;

        if (sphereDirections != null &&
            sphereDirections.Length == totalVertices &&
            smoothTriangles != null &&
            smoothTriangles.Length == totalTriangleIndices)
        {
            return;
        }

        sphereDirections = new Vector3[totalVertices];
        smoothTriangles = new int[totalTriangleIndices];
        smoothUvs = new Vector2[totalVertices];
        smoothVertices = new Vector3[totalVertices];
        smoothNormals = new Vector3[totalVertices];

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

                    sphereDirections[vertexIndex] = pointOnSphere;
                    smoothUvs[vertexIndex] = GetSphereUv(pointOnSphere);

                    if (x == resolution - 1 || y == resolution - 1)
                    {
                        continue;
                    }

                    smoothTriangles[triangleIndex++] = vertexIndex;
                    smoothTriangles[triangleIndex++] = vertexIndex + resolution + 1;
                    smoothTriangles[triangleIndex++] = vertexIndex + resolution;

                    smoothTriangles[triangleIndex++] = vertexIndex;
                    smoothTriangles[triangleIndex++] = vertexIndex + 1;
                    smoothTriangles[triangleIndex++] = vertexIndex + resolution + 1;
                }
            }
        }
    }

    void UpdateSmoothSurface(float timeSeconds)
    {
        for (int i = 0; i < sphereDirections.Length; i++)
        {
            smoothVertices[i] = GetWaterPoint(sphereDirections[i], timeSeconds);
        }

        for (int i = 0; i < sphereDirections.Length; i++)
        {
            smoothNormals[i] = CalculateNormal(sphereDirections[i], timeSeconds);
        }
    }

    void BuildFlatSurface()
    {
        int flatVertexCount = smoothTriangles.Length;
        if (flatVertices == null || flatVertices.Length != flatVertexCount)
        {
            flatVertices = new Vector3[flatVertexCount];
            flatNormals = new Vector3[flatVertexCount];
            flatUvs = new Vector2[flatVertexCount];
            flatTriangles = new int[flatVertexCount];
        }

        for (int i = 0; i < smoothTriangles.Length; i += 3)
        {
            int indexA = smoothTriangles[i];
            int indexB = smoothTriangles[i + 1];
            int indexC = smoothTriangles[i + 2];

            Vector3 vertexA = smoothVertices[indexA];
            Vector3 vertexB = smoothVertices[indexB];
            Vector3 vertexC = smoothVertices[indexC];
            Vector3 averageDirection = (sphereDirections[indexA] + sphereDirections[indexB] + sphereDirections[indexC]).normalized;
            Vector3 faceNormal = Vector3.Cross(vertexB - vertexA, vertexC - vertexA).normalized;

            if (Vector3.Dot(faceNormal, averageDirection) < 0f)
            {
                faceNormal = -faceNormal;
            }

            flatVertices[i] = vertexA;
            flatVertices[i + 1] = vertexB;
            flatVertices[i + 2] = vertexC;

            flatNormals[i] = faceNormal;
            flatNormals[i + 1] = faceNormal;
            flatNormals[i + 2] = faceNormal;

            flatUvs[i] = smoothUvs[indexA];
            flatUvs[i + 1] = smoothUvs[indexB];
            flatUvs[i + 2] = smoothUvs[indexC];

            flatTriangles[i] = i;
            flatTriangles[i + 1] = i + 1;
            flatTriangles[i + 2] = i + 2;
        }

        mesh.Clear();
        mesh.indexFormat = IndexFormat.UInt32;
        mesh.vertices = flatVertices;
        mesh.triangles = flatTriangles;
        mesh.normals = flatNormals;
        mesh.uv = flatUvs;
        mesh.RecalculateBounds();
    }

    void ApplySmoothSurface()
    {
        mesh.Clear();
        mesh.indexFormat = smoothVertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        mesh.vertices = smoothVertices;
        mesh.triangles = smoothTriangles;
        mesh.normals = smoothNormals;
        mesh.uv = smoothUvs;
        mesh.RecalculateBounds();
    }

    Vector3 GetWaterPoint(Vector3 pointOnSphere, float timeSeconds)
    {
        float radius = planetRadius + waterLevelOffset + GetWaveHeight(pointOnSphere, timeSeconds);
        return pointOnSphere * radius;
    }

    float GetWaveHeight(Vector3 pointOnSphere, float timeSeconds)
    {
        float primaryWave = Mathf.Sin(
            Vector3.Dot(pointOnSphere, PrimaryWaveDirection) * waveFrequency
            + timeSeconds * waveSpeed);

        float secondaryWave = Mathf.Sin(
            Vector3.Dot(pointOnSphere, SecondaryWaveDirection) * secondaryWaveFrequency
            - timeSeconds * secondaryWaveSpeed);

        float tertiaryWave = Mathf.Sin(
            Vector3.Dot(pointOnSphere, TertiaryWaveDirection) * (waveFrequency + secondaryWaveFrequency) * 0.5f
            + timeSeconds * (waveSpeed - secondaryWaveSpeed) * 0.35f);

        float waveHeight = primaryWave * waveAmplitude;
        waveHeight += secondaryWave * secondaryWaveAmplitude;
        waveHeight += tertiaryWave * (waveAmplitude * 0.25f);

        return waveHeight;
    }

    Vector3 CalculateNormal(Vector3 pointOnSphere, float timeSeconds)
    {
        Vector3 tangent = Vector3.Cross(Vector3.up, pointOnSphere);
        if (tangent.sqrMagnitude < 1e-6f)
        {
            tangent = Vector3.Cross(Vector3.right, pointOnSphere);
        }

        tangent.Normalize();

        Vector3 bitangent = Vector3.Cross(pointOnSphere, tangent).normalized;
        float sampleOffset = GetNormalSampleOffset();

        Vector3 tangentForward = GetWaterPoint((pointOnSphere + tangent * sampleOffset).normalized, timeSeconds);
        Vector3 tangentBack = GetWaterPoint((pointOnSphere - tangent * sampleOffset).normalized, timeSeconds);
        Vector3 bitangentForward = GetWaterPoint((pointOnSphere + bitangent * sampleOffset).normalized, timeSeconds);
        Vector3 bitangentBack = GetWaterPoint((pointOnSphere - bitangent * sampleOffset).normalized, timeSeconds);

        Vector3 normal = Vector3.Cross(tangentForward - tangentBack, bitangentForward - bitangentBack).normalized;
        if (Vector3.Dot(normal, pointOnSphere) < 0f)
        {
            normal = -normal;
        }

        return normal;
    }

    float GetNormalSampleOffset()
    {
        float longitudeStep = (2f * Mathf.PI) / Mathf.Max(resolution * 4f, 8f);
        float latitudeStep = Mathf.PI / Mathf.Max(resolution * 2f, 4f);
        return Mathf.Min(longitudeStep, latitudeStep);
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
