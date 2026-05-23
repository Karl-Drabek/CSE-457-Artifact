using UnityEngine;

[ExecuteAlways]
public class World : MonoBehaviour
{
    const string TerrainObjectName = "Planet Terrain";
    const string WaterObjectName = "Planet Water";

    [Range(2, 256)]
    public int resolution = 32;

    [SerializeField]
    Material material;

    [Min(0.01f)]
    public float planetRadius = 1f;

    [Header("Water")]
    [SerializeField]
    bool renderWater = true;

    [SerializeField]
    PlanetWater waterObject;

    [SerializeField, HideInInspector]
    MeshFilter terrainFilter;

    Material generatedMaterial;

    void OnEnable()
    {
        Rebuild();
    }

    void OnValidate()
    {
        Rebuild();
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

    [ContextMenu("Rebuild Planet")]
    void RebuildPlanet()
    {
        Rebuild();
    }

    void Rebuild()
    {
        MeshRenderer terrainRenderer = EnsureTerrainChild();
        waterObject = EnsureWaterChild();

        DisableLegacyArtifacts();

        terrainRenderer.sharedMaterial = GetRenderableMaterial();
        terrainRenderer.enabled = terrainRenderer.sharedMaterial != null;

        SphereFace terrain = new SphereFace(
            terrainFilter.sharedMesh,
            resolution,
            planetRadius);
        terrain.ConstructMesh();

        if (waterObject == null)
        {
            return;
        }

        waterObject.transform.SetParent(transform, false);
        waterObject.transform.localPosition = Vector3.zero;
        waterObject.transform.localRotation = Quaternion.identity;
        waterObject.transform.localScale = Vector3.one;
        waterObject.gameObject.SetActive(renderWater);

        if (renderWater)
        {
            waterObject.SyncFromWorld(planetRadius);
        }
    }

    MeshRenderer EnsureTerrainChild()
    {
        Transform terrainChild = FindOrCreateNamedChild(TerrainObjectName);

        terrainFilter = terrainChild.GetComponent<MeshFilter>();
        if (terrainFilter == null)
        {
            terrainFilter = terrainChild.gameObject.AddComponent<MeshFilter>();
        }

        MeshRenderer terrainRenderer = terrainChild.GetComponent<MeshRenderer>();
        if (terrainRenderer == null)
        {
            terrainRenderer = terrainChild.gameObject.AddComponent<MeshRenderer>();
        }

        if (terrainFilter.sharedMesh == null)
        {
            terrainFilter.sharedMesh = new Mesh
            {
                name = "Planet Terrain Mesh"
            };
        }

        return terrainRenderer;
    }

    PlanetWater EnsureWaterChild()
    {
        Transform waterChild = FindOrCreateNamedChild(WaterObjectName);
        PlanetWater childWater = waterChild.GetComponent<PlanetWater>();
        if (childWater == null)
        {
            childWater = waterChild.gameObject.AddComponent<PlanetWater>();
        }

        return childWater;
    }

    Transform FindOrCreateNamedChild(string childName)
    {
        Transform primaryChild = null;

        for (int i = 0; i < transform.childCount; i++)
        {
            Transform child = transform.GetChild(i);
            if (child.name != childName)
            {
                continue;
            }

            if (primaryChild == null)
            {
                primaryChild = child;
                continue;
            }

            DisableLegacyObject(child.gameObject, childName + " (legacy)");
        }

        if (primaryChild != null)
        {
            primaryChild.SetParent(transform, false);
            primaryChild.localPosition = Vector3.zero;
            primaryChild.localRotation = Quaternion.identity;
            primaryChild.localScale = Vector3.one;
            return primaryChild;
        }

        GameObject childObject = new GameObject(childName);
        childObject.transform.SetParent(transform, false);
        return childObject.transform;
    }

    void DisableLegacyArtifacts()
    {
        PlanetWater rootWater = GetComponent<PlanetWater>();
        if (rootWater != null)
        {
            rootWater.enabled = false;
        }

        MeshRenderer rootRenderer = GetComponent<MeshRenderer>();
        if (rootRenderer != null)
        {
            rootRenderer.enabled = false;
        }

        MeshFilter rootMesh = GetComponent<MeshFilter>();
        if (rootMesh != null)
        {
            rootMesh.sharedMesh = null;
        }

        for (int i = 0; i < transform.childCount; i++)
        {
            Transform child = transform.GetChild(i);
            if (terrainFilter != null && child == terrainFilter.transform)
            {
                continue;
            }

            if (waterObject != null && child == waterObject.transform)
            {
                continue;
            }

            if (child.name == "mesh" || child.name == "terrain_mesh" || child.name == "water_mesh")
            {
                DisableLegacyObject(child.gameObject, child.name + " (legacy)");
            }
        }
    }

    void DisableLegacyObject(GameObject target, string legacyName)
    {
        target.name = legacyName;
        target.SetActive(false);
    }

    Material GetRenderableMaterial()
    {
        if (IsSupportedMaterial(material))
        {
            return material;
        }

        if (generatedMaterial == null)
        {
            Shader shader = FindBestTerrainShader();
            if (shader == null)
            {
                return null;
            }

            generatedMaterial = new Material(shader)
            {
                name = "PlanetTerrainGenerated",
                hideFlags = HideFlags.HideAndDontSave
            };
        }

        UpdateGeneratedMaterial(generatedMaterial);
        return generatedMaterial;
    }

    bool IsSupportedMaterial(Material candidate)
    {
        return candidate != null && candidate.shader != null && candidate.shader.isSupported;
    }

    Shader FindBestTerrainShader()
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
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

    void UpdateGeneratedMaterial(Material target)
    {
        if (target == null || target.shader == null)
        {
            return;
        }

        Color terrainColor = new Color(0.45f, 0.5f, 0.42f, 1f);

        if (target.shader.name == "Universal Render Pipeline/Lit")
        {
            target.SetColor("_BaseColor", terrainColor);
            target.SetFloat("_Smoothness", 0.05f);
            target.SetFloat("_Metallic", 0f);
            return;
        }

        if (target.HasProperty("_Color"))
        {
            target.SetColor("_Color", terrainColor);
            return;
        }

        if (target.HasProperty("_BaseColor"))
        {
            target.SetColor("_BaseColor", terrainColor);
        }
    }
}
