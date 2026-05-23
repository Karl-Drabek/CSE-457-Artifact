using UnityEngine;

[ExecuteAlways]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class PlanetWater : MonoBehaviour
{
    [Range(2, 256)]
    public int resolution = 48;

    [SerializeField]
    Material materialOverride;

    [SerializeField]
    bool useFlatShading;

    [SerializeField]
    float waterLevelOffset = 0.02f;

    [SerializeField]
    float waveAmplitude = 0.008f;

    [SerializeField]
    float secondaryWaveAmplitude = 0.003f;

    [SerializeField]
    float waveFrequency = 6f;

    [SerializeField]
    float secondaryWaveFrequency = 10f;

    [SerializeField]
    float waveSpeed = 0.8f;

    [SerializeField]
    float secondaryWaveSpeed = 1.25f;

    [Header("Look")]
    [SerializeField]
    Color baseColor = new Color(0.12f, 0.42f, 0.68f, 0.88f);

    [SerializeField]
    Color fresnelColor = new Color(0.8f, 0.95f, 1f, 0.55f);

    [SerializeField, Range(0f, 1f)]
    float surfaceAlpha = 0.88f;

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
        Rebuild();
    }

    void OnEnable()
    {
        Rebuild();
    }

    void OnValidate()
    {
        Rebuild();
    }

    void Update()
    {
        if (!Application.isPlaying)
        {
            return;
        }

        GenerateMesh(Time.time);
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

    void Rebuild()
    {
        EnsureSetup();

        waterSurface = new WaterFace(
            meshFilter.sharedMesh,
            resolution,
            planetRadius,
            waterLevelOffset,
            waveAmplitude,
            secondaryWaveAmplitude,
            waveFrequency,
            secondaryWaveFrequency,
            waveSpeed,
            secondaryWaveSpeed,
            useFlatShading
        );

        GenerateMesh(Application.isPlaying ? Time.time : 0f);
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

    void GenerateMesh(float timeSeconds)
    {
        if (waterSurface == null)
        {
            return;
        }

        waterSurface.ConstructMesh(timeSeconds);
    }

    Material GetRenderableMaterial()
    {
        if (IsSupportedMaterial(materialOverride))
        {
            return materialOverride;
        }

        if (generatedMaterial == null)
        {
            Shader shader = FindBestShader();
            if (shader == null)
            {
                return null;
            }

            generatedMaterial = new Material(shader)
            {
                name = "PlanetWaterGenerated",
                hideFlags = HideFlags.HideAndDontSave
            };
        }

        UpdateGeneratedMaterial(generatedMaterial);
        return generatedMaterial;
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

        shader = Shader.Find("Sprites/Default");
        if (shader != null && shader.isSupported)
        {
            return shader;
        }

        shader = Shader.Find("Standard");
        if (shader != null && shader.isSupported)
        {
            return shader;
        }

        return null;
    }

    void UpdateGeneratedMaterial(Material material)
    {
        if (material == null || material.shader == null)
        {
            return;
        }

        if (material.shader.name == "Custom/PlanetWater")
        {
            material.SetColor("_BaseColor", baseColor);
            material.SetColor("_FresnelColor", fresnelColor);
            material.SetFloat("_Alpha", surfaceAlpha);
            material.SetFloat("_FresnelPower", fresnelPower);
            return;
        }

        Color fallbackColor = baseColor;
        fallbackColor.a = surfaceAlpha;

        if (material.HasProperty("_Color"))
        {
            material.SetColor("_Color", fallbackColor);
            return;
        }

        if (material.HasProperty("_BaseColor"))
        {
            material.SetColor("_BaseColor", fallbackColor);
        }
    }
}
