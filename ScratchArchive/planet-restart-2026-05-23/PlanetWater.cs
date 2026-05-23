using UnityEngine;

[ExecuteAlways]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class PlanetWater : MonoBehaviour
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

    [Range(2, 256)]
    public int resolution = 96;

    [SerializeField]
    Material materialOverride;

    [SerializeField]
    bool useFlatShading = true;

    [SerializeField]
    float waterLevelOffset = 0f;

    [SerializeField]
    float waveAmplitude = 0.01f;

    [SerializeField]
    float secondaryWaveAmplitude = 0.005f;

    [SerializeField]
    float waveFrequency = 8f;

    [SerializeField]
    float secondaryWaveFrequency = 15f;

    [SerializeField]
    float waveSpeed = 1f;

    [SerializeField]
    float secondaryWaveSpeed = 1.6f;

    [Header("Look")]
    [SerializeField]
    Color deepColor = new Color(0.08f, 0.34f, 0.53f, 0.92f);

    [SerializeField]
    Color shallowColor = new Color(0.21f, 0.62f, 0.78f, 0.96f);

    [SerializeField]
    Color foamColor = new Color(0.88f, 0.97f, 1f, 1f);

    [SerializeField, Range(0f, 1f)]
    float foamThreshold = 0.82f;

    [SerializeField, Range(0f, 1f)]
    float foamAmount = 0.24f;

    [SerializeField, Range(0.01f, 0.5f)]
    float foamSoftness = 0.08f;

    [SerializeField]
    float colorNoiseFrequency = 7.5f;

    [SerializeField, Range(0f, 1f)]
    float colorVariation = 0.28f;

    [SerializeField]
    float detailDriftSpeed = 0.18f;

    [SerializeField]
    float foamNoiseFrequency = 18f;

    [SerializeField]
    Color fresnelColor = new Color(0.72f, 0.92f, 1f, 0.55f);

    [SerializeField, Range(0f, 1f)]
    float surfaceAlpha = 0.9f;

    [SerializeField, Range(0f, 1f)]
    float ambientStrength = 0.35f;

    [SerializeField, Range(0f, 1f)]
    float specularStrength = 0.18f;

    [SerializeField, Range(1f, 128f)]
    float specularPower = 48f;

    [SerializeField, Range(0.25f, 8f)]
    float fresnelPower = 2.2f;

    [SerializeField, HideInInspector]
    float planetRadius = 1f;

    MeshFilter meshFilter;
    MeshRenderer meshRenderer;
    WaterFace waterSurface;
    Material generatedMaterial;

    public void SyncFromWorld(float newPlanetRadius)
    {
        planetRadius = Mathf.Max(0.01f, newPlanetRadius);
        EnsureSetup();
        RebuildWaterSurface();
        GenerateMesh(Application.isPlaying ? Time.time : 0f);
    }

    void OnEnable()
    {
        MigrateLegacyLookSettings();
        EnsureSetup();
        RebuildWaterSurface();
        GenerateMesh(Application.isPlaying ? Time.time : 0f);
    }

    void OnValidate()
    {
        MigrateLegacyLookSettings();
        EnsureSetup();
        RebuildWaterSurface();
        GenerateMesh(Application.isPlaying ? Time.time : 0f);
    }

    void Update()
    {
        if (!Application.isPlaying)
        {
            return;
        }

        GenerateMesh(Time.time);
    }

    void EnsureSetup()
    {
        meshFilter = GetComponent<MeshFilter>();
        meshRenderer = GetComponent<MeshRenderer>();

        if (materialOverride != null && !IsSupportedMaterial(materialOverride))
        {
            materialOverride = null;
        }

        if (meshFilter.sharedMesh == null)
        {
            meshFilter.sharedMesh = new Mesh
            {
                name = "Planet Water Mesh"
            };
        }

        meshRenderer.sharedMaterial = GetRenderableMaterial();
    }

    void MigrateLegacyLookSettings()
    {
        bool matchesLegacyDefaults =
            Mathf.Approximately(foamThreshold, 0.72f) &&
            Mathf.Approximately(foamAmount, 0.45f) &&
            Mathf.Approximately(foamSoftness, 0.1f) &&
            Mathf.Approximately(colorNoiseFrequency, 4.5f) &&
            Mathf.Approximately(colorVariation, 0.6f) &&
            Mathf.Approximately(detailDriftSpeed, 0.12f) &&
            Mathf.Approximately(foamNoiseFrequency, 9f);

        if (!matchesLegacyDefaults)
        {
            return;
        }

        foamThreshold = 0.82f;
        foamAmount = 0.24f;
        foamSoftness = 0.08f;
        colorNoiseFrequency = 7.5f;
        colorVariation = 0.28f;
        detailDriftSpeed = 0.18f;
        foamNoiseFrequency = 18f;
    }

    void RebuildWaterSurface()
    {
        waterSurface = new WaterFace(
            meshFilter.sharedMesh,
            resolution,
            FaceDirections,
            planetRadius,
            waterLevelOffset,
            waveAmplitude,
            secondaryWaveAmplitude,
            waveFrequency,
            secondaryWaveFrequency,
            waveSpeed,
            secondaryWaveSpeed,
            useFlatShading,
            deepColor,
            shallowColor,
            foamColor,
            foamThreshold,
            foamAmount,
            foamSoftness,
            colorNoiseFrequency,
            colorVariation,
            detailDriftSpeed,
            foamNoiseFrequency
        );
    }

    void GenerateMesh(float timeSeconds)
    {
        if (waterSurface == null)
        {
            return;
        }

        waterSurface.ConstructMesh(timeSeconds);
    }

    void OnDisable()
    {
        if (generatedMaterial == null)
        {
            return;
        }

        if (Application.isPlaying)
        {
            Destroy(generatedMaterial);
        }
        else
        {
            DestroyImmediate(generatedMaterial);
        }

        generatedMaterial = null;
    }

    Material GetRenderableMaterial()
    {
        if (IsSupportedMaterial(materialOverride))
        {
            return materialOverride;
        }

        if (generatedMaterial == null)
        {
            generatedMaterial = CreateGeneratedWaterMaterial();
        }

        UpdateGeneratedMaterial(generatedMaterial);
        return generatedMaterial;
    }

    Material CreateGeneratedWaterMaterial()
    {
        Shader shader = FindBestShader();

        Material material = new Material(shader)
        {
            name = "PlanetWaterGenerated",
            hideFlags = HideFlags.HideAndDontSave
        };

        UpdateGeneratedMaterial(material);
        return material;
    }

    bool IsSupportedMaterial(Material material)
    {
        if (material == null || material.shader == null)
        {
            return false;
        }

        if (!material.shader.isSupported)
        {
            return false;
        }

        if (material.shader.name.StartsWith("LowPolyWater/"))
        {
            return false;
        }

        return true;
    }

    Shader FindBestShader()
    {
        Shader shader = Shader.Find("Custom/PlanetWater");
        if (shader != null && shader.isSupported)
        {
            return shader;
        }

        shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader != null && shader.isSupported)
        {
            return shader;
        }

        shader = Shader.Find("Standard");
        if (shader != null && shader.isSupported)
        {
            return shader;
        }

        return Shader.Find("Sprites/Default");
    }

    void UpdateGeneratedMaterial(Material material)
    {
        if (material == null || material.shader == null)
        {
            return;
        }

        if (material.shader.name == "Custom/PlanetWater")
        {
            material.SetColor("_FresnelColor", fresnelColor);
            material.SetFloat("_Alpha", surfaceAlpha);
            material.SetFloat("_AmbientStrength", ambientStrength);
            material.SetFloat("_SpecularStrength", specularStrength);
            material.SetFloat("_SpecularPower", specularPower);
            material.SetFloat("_FresnelPower", fresnelPower);
            return;
        }

        if (material.shader.name == "Universal Render Pipeline/Lit")
        {
            material.SetColor("_BaseColor", shallowColor);
            material.SetFloat("_Smoothness", 0.82f);
            material.SetFloat("_Metallic", 0f);
            return;
        }

        material.color = shallowColor;
    }
}
