using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

[ExecuteAlways]
public class World : MonoBehaviour {

    [Range(2,256)]
    public int resolution = 10;

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
    MeshFilter[] meshFilters;
    SphereFace[] terrainFaces;

#if UNITY_EDITOR
    bool editorSyncQueued;
#endif
     
	private void OnValidate()
	{
        if (Application.isPlaying)
        {
            Initialize();
            GenerateMesh();
            return;
        }

#if UNITY_EDITOR
        QueueEditorSync();
#endif
	}

    private void OnEnable()
    {
        if (!Application.isPlaying)
        {
#if UNITY_EDITOR
            QueueEditorSync();
#endif
            return;
        }

        Initialize();
        GenerateMesh();
    }

    void Reset()
    {
#if UNITY_EDITOR
        QueueEditorSync();
#endif
    }

    [ContextMenu("Rebuild World Children")]
    void RebuildWorldChildren()
    {
#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            QueueEditorSync();
            return;
        }
#endif

        Initialize();
        GenerateMesh();
    }

    void Initialize()
    {
        EnsureMeshFilterArray(ref meshFilters);
        terrainFaces = new SphereFace[6];

        Vector3[] directions = { Vector3.up, Vector3.down, Vector3.left, Vector3.right, Vector3.forward, Vector3.back };

        for (int i = 0; i < 6; i++)
        {
            if (meshFilters[i] == null)
            {
                meshFilters[i] = CreateMeshFilter("terrain_mesh");
            }

            MeshRenderer renderer = meshFilters[i].GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = material;
                renderer.enabled = material != null;
            }

            terrainFaces[i] = new SphereFace(
                meshFilters[i].sharedMesh,
                resolution,
                directions[i],
                planetRadius
            );
        }

        CleanupRootWaterArtifacts();
        CleanupLegacyWaterMeshes();

        waterObject = EnsureWaterChild();
        if (waterObject != null)
        {
            waterObject.transform.SetParent(transform, false);
            waterObject.transform.localPosition = Vector3.zero;
            waterObject.transform.localRotation = Quaternion.identity;
            waterObject.transform.localScale = Vector3.one;
            waterObject.gameObject.SetActive(renderWater);
            waterObject.SyncFromWorld(planetRadius);
        }
    }

    void GenerateMesh()
    {
        foreach (SphereFace face in terrainFaces)
        {
            face.ConstructMesh();
        }

        if (waterObject != null)
        {
            waterObject.gameObject.SetActive(renderWater);
            if (renderWater)
            {
                waterObject.SyncFromWorld(planetRadius);
            }
        }
    }

    MeshFilter CreateMeshFilter(string objectName)
    {
        GameObject meshObj = new GameObject(objectName);
        meshObj.transform.parent = transform;
        meshObj.transform.localPosition = Vector3.zero;
        meshObj.transform.localRotation = Quaternion.identity;
        meshObj.transform.localScale = Vector3.one;

        meshObj.AddComponent<MeshRenderer>();
        MeshFilter meshFilter = meshObj.AddComponent<MeshFilter>();
        meshFilter.sharedMesh = new Mesh();
        return meshFilter;
    }

    void EnsureMeshFilterArray(ref MeshFilter[] filters)
    {
        if (filters == null || filters.Length != 6)
        {
            filters = new MeshFilter[6];
        }
    }

    PlanetWater EnsureWaterChild()
    {
        Transform waterChild = transform.Find("Planet Water");
        if (waterChild == null)
        {
            GameObject waterGameObject = new GameObject("Planet Water");
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                Undo.RegisterCreatedObjectUndo(waterGameObject, "Create Planet Water");
                Undo.SetTransformParent(waterGameObject.transform, transform, "Parent Planet Water");
                waterGameObject.transform.localPosition = Vector3.zero;
                waterGameObject.transform.localRotation = Quaternion.identity;
                waterGameObject.transform.localScale = Vector3.one;
            }
            else
#endif
            {
                waterGameObject.transform.SetParent(transform, false);
            }

            waterChild = waterGameObject.transform;
        }

        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            Transform child = transform.GetChild(i);
            if (child == waterChild)
            {
                continue;
            }

            if (child.name != "Planet Water")
            {
                continue;
            }

            if (Application.isPlaying)
            {
                Destroy(child.gameObject);
            }
            else
            {
                DisableLegacyObject(child.gameObject, "Planet Water (disabled duplicate)");
            }
        }

        waterChild.SetParent(transform, false);
        waterChild.localPosition = Vector3.zero;
        waterChild.localRotation = Quaternion.identity;
        waterChild.localScale = Vector3.one;

        PlanetWater childWater = waterChild.GetComponent<PlanetWater>();
        if (childWater == null)
        {
            childWater = waterChild.gameObject.AddComponent<PlanetWater>();
        }

        return childWater;
    }

    void CleanupLegacyWaterMeshes()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            Transform child = transform.GetChild(i);
            if (child.name != "water_mesh")
            {
                continue;
            }

            if (Application.isPlaying)
            {
                Destroy(child.gameObject);
            }
            else
            {
                DisableLegacyObject(child.gameObject, "water_mesh (legacy disabled)");
            }
        }
    }

    void DisableLegacyObject(GameObject target, string renamedObject)
    {
        target.name = renamedObject;
        target.SetActive(false);
    }

    void CleanupRootWaterArtifacts()
    {
        PlanetWater selfWater = GetComponent<PlanetWater>();
        if (selfWater != null)
        {
            if (Application.isPlaying)
            {
                Destroy(selfWater);
            }
            else
            {
                DestroyImmediate(selfWater);
            }
        }

        MeshFilter rootMeshFilter = GetComponent<MeshFilter>();
        if (rootMeshFilter != null)
        {
            if (Application.isPlaying)
            {
                Destroy(rootMeshFilter);
            }
            else
            {
                DestroyImmediate(rootMeshFilter);
            }
        }

        MeshRenderer rootMeshRenderer = GetComponent<MeshRenderer>();
        if (rootMeshRenderer != null)
        {
            if (Application.isPlaying)
            {
                Destroy(rootMeshRenderer);
            }
            else
            {
                DestroyImmediate(rootMeshRenderer);
            }
        }
    }

#if UNITY_EDITOR
    void QueueEditorSync()
    {
        if (editorSyncQueued)
        {
            return;
        }

        editorSyncQueued = true;
        EditorApplication.delayCall += RunEditorSync;
    }

    void RunEditorSync()
    {
        EditorApplication.delayCall -= RunEditorSync;
        editorSyncQueued = false;

        if (this == null)
        {
            return;
        }

        Initialize();
        GenerateMesh();

        EditorUtility.SetDirty(this);
        if (waterObject != null)
        {
            EditorUtility.SetDirty(waterObject);
        }

        if (gameObject.scene.IsValid())
        {
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
        }
    }
#endif
}
