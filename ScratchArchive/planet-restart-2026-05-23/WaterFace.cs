using UnityEngine;
using UnityEngine.Rendering;

public class WaterFace
{
    static readonly Vector3[] DefaultFaceDirections =
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
    readonly Vector3[] faceDirections;
    readonly float planetRadius;
    readonly float waterLevelOffset;
    readonly float waveAmplitude;
    readonly float secondaryWaveAmplitude;
    readonly float waveFrequency;
    readonly float secondaryWaveFrequency;
    readonly float waveSpeed;
    readonly float secondaryWaveSpeed;
    readonly bool useFlatShading;
    readonly Color deepColor;
    readonly Color shallowColor;
    readonly Color foamColor;
    readonly float foamThreshold;
    readonly float foamAmount;
    readonly float foamSoftness;
    readonly float colorNoiseFrequency;
    readonly float colorVariation;
    readonly float detailDriftSpeed;
    readonly float foamNoiseFrequency;

    Vector3[] sphereDirections;
    int[] smoothTriangles;
    Vector2[] smoothUvs;

    Vector3[] smoothVertices;
    Vector3[] smoothNormals;
    Color[] smoothColors;

    Vector3[] flatVertices;
    Vector3[] flatNormals;
    Vector2[] flatUvs;
    int[] flatTriangles;
    Color[] flatColors;

    public WaterFace(
        Mesh mesh,
        int resolution,
        Vector3[] faceDirections,
        float planetRadius,
        float waterLevelOffset,
        float waveAmplitude,
        float secondaryWaveAmplitude,
        float waveFrequency,
        float secondaryWaveFrequency,
        float waveSpeed,
        float secondaryWaveSpeed,
        bool useFlatShading,
        Color deepColor,
        Color shallowColor,
        Color foamColor,
        float foamThreshold,
        float foamAmount,
        float foamSoftness,
        float colorNoiseFrequency,
        float colorVariation,
        float detailDriftSpeed,
        float foamNoiseFrequency)
    {
        this.mesh = mesh;
        this.resolution = Mathf.Max(2, resolution);
        this.faceDirections = faceDirections != null && faceDirections.Length > 0
            ? faceDirections
            : DefaultFaceDirections;
        this.planetRadius = planetRadius;
        this.waterLevelOffset = waterLevelOffset;
        this.waveAmplitude = waveAmplitude;
        this.secondaryWaveAmplitude = secondaryWaveAmplitude;
        this.waveFrequency = waveFrequency;
        this.secondaryWaveFrequency = secondaryWaveFrequency;
        this.waveSpeed = waveSpeed;
        this.secondaryWaveSpeed = secondaryWaveSpeed;
        this.useFlatShading = useFlatShading;
        this.deepColor = deepColor;
        this.shallowColor = shallowColor;
        this.foamColor = foamColor;
        this.foamThreshold = Mathf.Clamp01(foamThreshold);
        this.foamAmount = Mathf.Clamp01(foamAmount);
        this.foamSoftness = Mathf.Max(0.0001f, foamSoftness);
        this.colorNoiseFrequency = Mathf.Max(0.0001f, colorNoiseFrequency);
        this.colorVariation = Mathf.Clamp01(colorVariation);
        this.detailDriftSpeed = detailDriftSpeed;
        this.foamNoiseFrequency = Mathf.Max(0.0001f, foamNoiseFrequency);

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
        int totalVertices = faceDirections.Length * verticesPerFace;
        int totalTriangleIndices = faceDirections.Length * trianglesPerFace;

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
        smoothColors = new Color[totalVertices];

        int triangleIndex = 0;
        for (int faceIndex = 0; faceIndex < faceDirections.Length; faceIndex++)
        {
            Vector3 localUp = faceDirections[faceIndex];
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
                    Vector3 pointOnUnitCube = localUp
                        + (percent.x - 0.5f) * 2f * axisA
                        + (percent.y - 0.5f) * 2f * axisB;
                    Vector3 pointOnUnitSphere = PointOnCubeToPointOnSphere(pointOnUnitCube);

                    sphereDirections[vertexIndex] = pointOnUnitSphere;
                    smoothUvs[vertexIndex] = GetSphereUv(pointOnUnitSphere);

                    if (x != resolution - 1 && y != resolution - 1)
                    {
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
    }

    void UpdateSmoothSurface(float timeSeconds)
    {
        for (int i = 0; i < sphereDirections.Length; i++)
        {
            smoothVertices[i] = GetWaterPoint(sphereDirections[i], timeSeconds);
            smoothColors[i] = EvaluateWaterColor(sphereDirections[i], timeSeconds);
        }

        if (useFlatShading)
        {
            return;
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
            flatColors = new Color[flatVertexCount];
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

            flatColors[i] = smoothColors[indexA];
            flatColors[i + 1] = smoothColors[indexB];
            flatColors[i + 2] = smoothColors[indexC];

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
        mesh.colors = flatColors;
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
        mesh.colors = smoothColors;
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

    Vector2 GetSphereUv(Vector3 pointOnUnitSphere)
    {
        float u = Mathf.Atan2(pointOnUnitSphere.z, pointOnUnitSphere.x) / (2f * Mathf.PI) + 0.5f;
        float v = Mathf.Asin(pointOnUnitSphere.y) / Mathf.PI + 0.5f;
        return new Vector2(u, v);
    }

    Vector3 GetWaterPoint(Vector3 pointOnUnitSphere, float timeSeconds)
    {
        float radius = planetRadius + waterLevelOffset + GetWaveHeight(pointOnUnitSphere, timeSeconds);
        return pointOnUnitSphere * radius;
    }

    Color EvaluateWaterColor(Vector3 pointOnUnitSphere, float timeSeconds)
    {
        float maxWaveHeight = Mathf.Max(
            waveAmplitude + secondaryWaveAmplitude + Mathf.Abs(secondaryWaveAmplitude * 0.5f),
            0.0001f);
        float normalizedHeight = Mathf.InverseLerp(
            -maxWaveHeight,
            maxWaveHeight,
            GetWaveHeight(pointOnUnitSphere, timeSeconds));

        Vector3 colorSamplePoint = pointOnUnitSphere * colorNoiseFrequency;
        Vector3 colorWarp = GetNoiseWarp(
            pointOnUnitSphere,
            colorNoiseFrequency * 0.55f,
            timeSeconds,
            detailDriftSpeed * 0.85f);
        float detailNoise = FractalNoise(
            colorSamplePoint
            + colorWarp
            + PrimaryWaveDirection * (timeSeconds * detailDriftSpeed));
        float depthLerp = Mathf.Clamp01(normalizedHeight + (detailNoise - 0.5f) * colorVariation);
        Color waterColor = Color.Lerp(deepColor, shallowColor, depthLerp);

        float crestMask = Mathf.SmoothStep(0.72f, 1f, normalizedHeight);
        float foamNoise = RidgedNoise(
            pointOnUnitSphere * foamNoiseFrequency
            + colorWarp * 1.75f
            + SecondaryWaveDirection * (timeSeconds * detailDriftSpeed * 1.35f));
        float foamSignal = Mathf.Clamp01(crestMask + (foamNoise - 0.5f) * 0.22f);
        float foamLerp = Mathf.SmoothStep(
            foamThreshold,
            Mathf.Min(foamThreshold + foamSoftness, 0.9999f),
            foamSignal) * foamAmount;
        waterColor = Color.Lerp(waterColor, foamColor, foamLerp);
        waterColor.a = 1f;
        return waterColor;
    }

    float GetWaveHeight(Vector3 pointOnUnitSphere, float timeSeconds)
    {
        float primaryWave = Mathf.Sin(Vector3.Dot(pointOnUnitSphere, PrimaryWaveDirection) * waveFrequency + timeSeconds * waveSpeed);
        float secondaryWave = Mathf.Sin(Vector3.Dot(pointOnUnitSphere, SecondaryWaveDirection) * secondaryWaveFrequency + timeSeconds * secondaryWaveSpeed);
        float tertiaryWave = Mathf.Sin(Vector3.Dot(pointOnUnitSphere, TertiaryWaveDirection) * (waveFrequency + secondaryWaveFrequency) * 0.5f - timeSeconds * waveSpeed * 0.6f);

        float waveHeight = primaryWave * waveAmplitude;
        waveHeight += secondaryWave * secondaryWaveAmplitude;
        waveHeight += tertiaryWave * (secondaryWaveAmplitude * 0.5f);

        return waveHeight;
    }

    Vector3 CalculateNormal(Vector3 pointOnUnitSphere, float timeSeconds)
    {
        Vector3 tangent = Vector3.Cross(Vector3.up, pointOnUnitSphere);
        if (tangent.sqrMagnitude < 1e-6f)
        {
            tangent = Vector3.Cross(Vector3.right, pointOnUnitSphere);
        }
        tangent.Normalize();

        Vector3 bitangent = Vector3.Cross(pointOnUnitSphere, tangent).normalized;
        float sampleOffset = GetNormalSampleOffset();

        Vector3 tangentForward = GetWaterPoint((pointOnUnitSphere + tangent * sampleOffset).normalized, timeSeconds);
        Vector3 tangentBack = GetWaterPoint((pointOnUnitSphere - tangent * sampleOffset).normalized, timeSeconds);
        Vector3 bitangentForward = GetWaterPoint((pointOnUnitSphere + bitangent * sampleOffset).normalized, timeSeconds);
        Vector3 bitangentBack = GetWaterPoint((pointOnUnitSphere - bitangent * sampleOffset).normalized, timeSeconds);

        Vector3 normal = Vector3.Cross(tangentForward - tangentBack, bitangentForward - bitangentBack).normalized;
        if (Vector3.Dot(normal, pointOnUnitSphere) < 0f)
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

    float FractalNoise(Vector3 point)
    {
        float total = 0f;
        float amplitude = 0.5f;
        float amplitudeSum = 0f;
        Vector3 samplePoint = point;

        for (int octave = 0; octave < 5; octave++)
        {
            total += ValueNoise(samplePoint) * amplitude;
            amplitudeSum += amplitude;
            samplePoint = samplePoint * 2.12f + new Vector3(17.17f, 31.31f, 47.47f);
            amplitude *= 0.55f;
        }

        return total / Mathf.Max(amplitudeSum, 0.0001f);
    }

    float RidgedNoise(Vector3 point)
    {
        return 1f - Mathf.Abs(FractalNoise(point) * 2f - 1f);
    }

    Vector3 GetNoiseWarp(Vector3 pointOnUnitSphere, float frequency, float timeSeconds, float driftSpeed)
    {
        Vector3 basePoint = pointOnUnitSphere * Mathf.Max(frequency, 0.0001f);
        float timeOffset = timeSeconds * driftSpeed;

        return new Vector3(
            ValueNoise(basePoint + new Vector3(11.3f, -4.7f, 2.1f) + PrimaryWaveDirection * timeOffset) - 0.5f,
            ValueNoise(basePoint + new Vector3(-7.9f, 13.6f, -5.4f) + SecondaryWaveDirection * timeOffset) - 0.5f,
            ValueNoise(basePoint + new Vector3(3.8f, 6.2f, 17.5f) - TertiaryWaveDirection * timeOffset) - 0.5f
        ) * 1.35f;
    }

    float ValueNoise(Vector3 point)
    {
        Vector3 cell = new Vector3(
            Mathf.Floor(point.x),
            Mathf.Floor(point.y),
            Mathf.Floor(point.z));
        Vector3 local = point - cell;
        local = new Vector3(
            SmoothHermite01(local.x),
            SmoothHermite01(local.y),
            SmoothHermite01(local.z));

        float n000 = Hash(cell + new Vector3(0f, 0f, 0f));
        float n100 = Hash(cell + new Vector3(1f, 0f, 0f));
        float n010 = Hash(cell + new Vector3(0f, 1f, 0f));
        float n110 = Hash(cell + new Vector3(1f, 1f, 0f));
        float n001 = Hash(cell + new Vector3(0f, 0f, 1f));
        float n101 = Hash(cell + new Vector3(1f, 0f, 1f));
        float n011 = Hash(cell + new Vector3(0f, 1f, 1f));
        float n111 = Hash(cell + new Vector3(1f, 1f, 1f));

        float nx00 = Mathf.Lerp(n000, n100, local.x);
        float nx10 = Mathf.Lerp(n010, n110, local.x);
        float nx01 = Mathf.Lerp(n001, n101, local.x);
        float nx11 = Mathf.Lerp(n011, n111, local.x);

        float nxy0 = Mathf.Lerp(nx00, nx10, local.y);
        float nxy1 = Mathf.Lerp(nx01, nx11, local.y);

        return Mathf.Lerp(nxy0, nxy1, local.z);
    }

    float SmoothHermite01(float t)
    {
        return t * t * (3f - 2f * t);
    }

    float Hash(Vector3 point)
    {
        float dot = point.x * 127.1f + point.y * 311.7f + point.z * 74.7f;
        return Mathf.Repeat(Mathf.Sin(dot) * 43758.5453f, 1f);
    }
}
