using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

[ExecuteAlways]
public class World : MonoBehaviour
{
    // Keep the generated scene hierarchy predictable so the world can rebuild in-place.
    const string GroundObjectName = "Ground";
    const string WaterObjectName = "Water";

    [Range(2, 256)]
    public int resolution = 32;

    [Header("Ground")]
    public Vector2 groundSize = new Vector2(10f, 10f);

    [SerializeField]
    Material groundMaterial;

    [Header("Water")]
    public bool renderWater = true;

    public Vector2 waterSize = new Vector2(8f, 8f);

    public float waterHeight = 0.05f;

    [SerializeField]
    Material waterMaterial;

    [SerializeField, HideInInspector]
    MeshFilter groundFilter;

    [SerializeField, HideInInspector]
    ProceduralGroundSurface groundSurface;

    [SerializeField, HideInInspector]
    MeshFilter waterFilter;

    [SerializeField, HideInInspector]
    UrpLowPolyWater waterSurface;

    // Called by Unity when the component becomes active in the scene or after a domain reload.
    // Rebuilds the generated ground and water children so the hierarchy stays in sync.
    void OnEnable()
    {
        Rebuild();
    }

    // Called by Unity in the editor when serialized fields change in the Inspector.
    // Rebuilds immediately so size, material, and resolution edits preview in edit mode.
    void OnValidate()
    {
        Rebuild();
    }

    // Central rebuild path used by Unity lifecycle callbacks and the context menu.
    // Ensures the two generated child objects exist and then refreshes their meshes/materials.
    void Rebuild()
    {
        // Ground owns its own terrain generator so the height-field logic stays isolated
        // from the world object that only manages child-object lifetime and shared settings.
        groundFilter = EnsureMeshChild(
            GroundObjectName,
            ref groundFilter,
            groundMaterial);

        if (groundFilter != null)
        {
            groundSurface = groundFilter.GetComponent<ProceduralGroundSurface>();
            if (groundSurface == null)
            {
                groundSurface = groundFilter.gameObject.AddComponent<ProceduralGroundSurface>();
            }

            groundSurface.SyncFromWorld(
                Mathf.Max(2, resolution),
                groundSize,
                waterHeight);
        }

        waterFilter = EnsureMeshChild(
            WaterObjectName,
            ref waterFilter,
            waterMaterial);

        if (waterFilter == null)
        {
            return;
        }

        // Water owns its own animated surface component because it needs per-frame updates.
        waterSurface = waterFilter.GetComponent<UrpLowPolyWater>();
        if (waterSurface == null)
        {
            waterSurface = waterFilter.gameObject.AddComponent<UrpLowPolyWater>();
        }

        waterFilter.gameObject.SetActive(renderWater);
        if (!renderWater)
        {
            return;
        }

        // Pass the world-level settings into the water component so it can manage its own mesh.
        waterSurface.SyncFromWorld(
            Mathf.Max(2, resolution),
            waterSize,
            waterHeight,
            waterMaterial);
    }

    // Ensures a named child exists with a MeshFilter and MeshRenderer, then assigns a material.
    // Called by Rebuild for both ground and water whenever the world refreshes.
    MeshFilter EnsureMeshChild(
        string objectName,
        ref MeshFilter cachedFilter,
        Material assignedMaterial)
    {
        // Reuse children instead of recreating them so inspector references remain stable.
        Transform child = transform.Find(objectName);
        if (child == null)
        {
            child = new GameObject(objectName).transform;
            child.SetParent(transform, false);
        }

        child.localPosition = Vector3.zero;
        child.localRotation = Quaternion.identity;
        child.localScale = Vector3.one;

        cachedFilter = child.GetComponent<MeshFilter>();
        if (cachedFilter == null)
        {
            cachedFilter = child.gameObject.AddComponent<MeshFilter>();
        }

        MeshRenderer renderer = child.GetComponent<MeshRenderer>();
        if (renderer == null)
        {
            renderer = child.gameObject.AddComponent<MeshRenderer>();
        }

        if (cachedFilter.sharedMesh == null)
        {
            cachedFilter.sharedMesh = new Mesh
            {
                name = objectName + " Mesh"
            };
        }

        renderer.sharedMaterial = assignedMaterial;
        // Leave generated meshes invisible until the user assigns a supported material explicitly.
        renderer.enabled = IsRenderableMaterial(assignedMaterial);

        return cachedFilter;
    }

    // Returns true when a material exists and its shader is supported by the current render pipeline.
    // Called while rebuilding so unassigned or unsupported materials do not render as accidental pink fallbacks.
    bool IsRenderableMaterial(Material material)
    {
        return material != null
            && material.shader != null
            && material.shader.isSupported;
    }
}
