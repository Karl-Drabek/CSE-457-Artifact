using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

public class ShipBuilder : MonoBehaviour
{
    [Header("Available Parts")]
    [SerializeField] private ShipPartDefinition[] availableParts;

    [Header("Scene References")]
    [SerializeField] private Transform shipRoot;

    [Header("Preview")]
    [SerializeField] private Material previewMaterial;

    [Header("Scene Transfer")]
    [SerializeField] private string sailSceneName = "SampleScene";
    [SerializeField] private string builderSceneName = "ShipBuilding";

    [Header("UI Elements")]
    [SerializeField] private Button hullButton;
    [SerializeField] private GameObject selectionPanel;
    [SerializeField] private GameObject buttPrefab;

    private ShipPartDefinition selectedPart;
    private bool hullPlaced = false;
    private Vector3[] buoyancy_vertices = { };
    private Vector3 hull_base_point = Vector3.zero;
    private float hull_local_height = 0f;
    private Vector3 weightedCenterOfMassAccumulated = Vector3.zero;
    private float totalMass = 0f;
    private string VISUAL = "VisualComponent";

    private GameObject previewObject;

    void Start()
    {
    }
    
    void Awake()
    {
        selectionPanel.SetActive(false);
    }

    void Update()
    {
        if (Mouse.current == null)
        {
            return;
        }

        bool pointerOverUI = EventSystem.current != null &&
                             EventSystem.current.IsPointerOverGameObject();

        if (!pointerOverUI)
        {
            UpdatePreviewPosition();
        }

        if (Mouse.current.leftButton.wasPressedThisFrame)
        {
            if (pointerOverUI)
            {
                return;
            }

            TryPlaceSelectedPart();
        }
        else if (Mouse.current.rightButton.wasPressedThisFrame)
        {
            DestroyPreview();
        }
    }

    public void SelectPart(int index)
    {
        if (index < 0 || index >= availableParts.Length)
        {
            Debug.LogWarning("Invalid part index selected.");
            return;
        }

        selectedPart = availableParts[index];
        Debug.Log("Selected part: " + selectedPart.displayName);

        CreatePreview();
    }

    void TryPlaceSelectedPart()
    {
        if (selectedPart == null || previewObject == null)
        {
            Debug.Log("No part selected.");
            return;
        }

        TryPlacePart();
    }

    void TryPlacePart()
    {
        if (selectedPart == null || previewObject == null)
        {
            return;
        }

        if (Camera.main == null || Mouse.current == null)
        {
            return;
        }

        Vector2 mousePosition = Mouse.current.position.ReadValue();
        Ray ray = Camera.main.ScreenPointToRay(mousePosition);

        if (!Physics.Raycast(ray, out RaycastHit hit))
        {
            return;
        }

        // This is the boat piece the player clicked on.
        // For example: hull, mast, sail, etc.
        BoatPiece clickedPiece = hit.collider.GetComponentInParent<BoatPiece>();

        // -------------------------
        // Placing the hull
        // -------------------------
        if (selectedPart.partType == ShipPartType.Hull)
        {
            if (hullPlaced)
            {
                Debug.Log("Hull already placed.");
                return;
            }

            GameObject hullObj = Instantiate(
                selectedPart.prefab,
                previewObject.transform.position,
                previewObject.transform.rotation,
                shipRoot
            );

            BoatPiece hullPiece = hullObj.GetComponent<BoatPiece>();

            if (hullPiece != null)
            {
                hullPiece.isRootHull = true;
            }

            SetUpPart(hullObj);
            return;
        }

        // -------------------------
        // Placing anything non-hull
        // -------------------------
        if (!hullPlaced)
        {
            Debug.Log("Hull not yet placed.");
            return;
        }

        if (clickedPiece == null)
        {
            Debug.Log("Must place this part on an existing boat piece.");
            return;
        }

        GameObject obj = Instantiate(
            selectedPart.prefab,
            previewObject.transform.position,
            previewObject.transform.rotation,
            shipRoot
        );

        BoatPiece newPiece = obj.GetComponent<BoatPiece>();

        if (newPiece != null)
        {
            newPiece.AttachTo(clickedPiece);
        }
        else
        {
            Debug.LogWarning("Placed part does not have a BoatPiece component.");
        }

        SetUpPart(obj);

        selectedPart = null;
        DestroyPreview();
    }

    void SetUpPart(GameObject obj)
    {
        ApplyMaterial(obj, selectedPart.material);
        Transform trans = obj.transform;
        // Transform into world space
        Vector3 worldPoint = trans.TransformPoint(selectedPart.centerOfMass);
        // Get point with respect to the ship root
        Vector3 rootPoint = shipRoot.InverseTransformPoint(worldPoint);
        weightedCenterOfMassAccumulated += selectedPart.mass * rootPoint;
        totalMass += selectedPart.mass;

        // Only allowed to place 1 hull. We should add the ability to change hull.
        if (selectedPart.partType == ShipPartType.Hull)
        {
            hullPlaced = true;
            // Disable hull button
            hullButton.interactable = false;
            selectionPanel.SetActive(false);
            GetHullMeshPoints(previewObject.GetComponentInChildren<MeshFilter>());
            Debug.Log(previewObject);
            DestroyPreview();
            selectedPart = null;
        }
    }

    void CreatePreview()
    {
        DestroyPreview();

        if (selectedPart == null || selectedPart.prefab == null)
        {
            return;
        }

        previewObject = Instantiate(selectedPart.prefab);
        previewObject.name = selectedPart.displayName + " Preview";

        IgnoreRaycast(previewObject);
        ApplyMaterial(previewObject, previewMaterial);

        previewObject.SetActive(false);
    }

    void IgnoreRaycast(GameObject obj)
    {
        obj.layer = 2;
        Transform[] childTransforms = obj.GetComponentsInChildren<Transform>();
        foreach (Transform tran in childTransforms)
        {
            tran.gameObject.layer = 2;
        }
    }

    void ApplyMaterial(GameObject target, Material material)
    {
        if (material == null)
        {
            Debug.LogWarning("Material is not assigned.");
            return;
        }

        Renderer[] renderers = target.GetComponentsInChildren<Renderer>();
        foreach (Renderer rend in renderers)
        {
            if (rend.CompareTag(VISUAL))
            {
                rend.material = material;
                return;
            }
        }
    }

    void DestroyPreview()
    {
        if (previewObject != null)
        {
            Destroy(previewObject);
            previewObject = null;
        }
    }

    void UpdatePreviewPosition()
    {
        if (previewObject == null || selectedPart == null || Camera.main == null || Mouse.current == null)
        {
            return;
        }

        Vector2 mousePosition = Mouse.current.position.ReadValue();
        Ray ray = Camera.main.ScreenPointToRay(mousePosition);

        if (!Physics.Raycast(ray, out RaycastHit hit))
        {
            previewObject.SetActive(false);
            return;
        }

        previewObject.SetActive(true);
        Reorient(hit, previewObject);
    }

    void Reorient(RaycastHit rayHit, GameObject gobj)
    {
        Vector3 norm = rayHit.normal.normalized;
        gobj.transform.up = norm;
        gobj.transform.rotation = Quaternion.FromToRotation(Vector3.up, norm);
        //gobj.transform.position = rayHit.point + norm * gobj.transform.localScale.y;
        gobj.transform.position = rayHit.point;
    }

    // Get all mesh points on the hull with respect to the hull mesh
    void GetHullMeshPoints(MeshFilter meshFilter)
    {
        Mesh mesh = meshFilter.mesh;
        Transform meshTransform = meshFilter.transform;

        if (mesh == null || meshFilter.sharedMesh == null)
        {
            return;
        }
        
        buoyancy_vertices = meshFilter.sharedMesh.vertices;
        Vector3[] normals = meshFilter.sharedMesh.normals;
        List<Vector3> world_points_on_hull = new List<Vector3>();

        // Also get the hull height for buoyancy weight calculations while we're at it
        for (int i = 0; i < normals.Length; i++)
        {
            Vector3 worldNormal = meshTransform.TransformDirection(normals[i]);
            //if (worldNormal.y < -0.1f)
            //{
            Vector3 worldPoint = meshTransform.transform.TransformPoint(buoyancy_vertices[i]);
            if (!world_points_on_hull.Contains(worldPoint))
            {
                world_points_on_hull.Add(worldPoint);
            }
            //}
        }
        buoyancy_vertices = world_points_on_hull.ToArray();
        hull_base_point = meshTransform.TransformPoint(meshFilter.sharedMesh.bounds.min);
        float localMaxHeight = meshFilter.sharedMesh.bounds.max.y * meshTransform.localScale.y;
        hull_local_height = localMaxHeight;
    }

    // Transform the hull points we got earlier to the root space and set our manual buoyancy points to use these
    void ApplyHullMeshPoints(WaterBuoyancy buoyancy)
    {
        if (buoyancy == null || buoyancy_vertices == null || buoyancy_vertices.Length == 0)
        {
            return;
        }
        for (int i = 0; i < buoyancy_vertices.Length; i++)
        {
            Vector3 rootLocalPoint = buoyancy.transform.InverseTransformPoint(buoyancy_vertices[i]);
            buoyancy_vertices[i] = rootLocalPoint;
        }
        hull_base_point = buoyancy.transform.InverseTransformPoint(hull_base_point);
        buoyancy.hull_height = hull_local_height;
        buoyancy.SetManualPoints(buoyancy_vertices, hull_base_point.y);
    }

    // Unity doesnt support enums passed into on clicks so we need these for the in scene buttons to call
    public void BuildHullSelectionPanel()
    {
        BuildPartSelectionPanel(ShipPartType.Hull);
    }

    public void BuildMastSelectionPanel()
    {
        BuildPartSelectionPanel(ShipPartType.Mast);
    }

    public void BuildSailSelectionPanel()
    {
        BuildPartSelectionPanel(ShipPartType.Sail);
    }

    // Build out selectable parts of the type associated with the clicked button
    void BuildPartSelectionPanel(ShipPartType partType)
    {
        // Clear existing buttons first
        foreach (Transform child in selectionPanel.transform)
        {
            Destroy(child.gameObject);
        }

        // Iterate through array of parts and add buttons for each in the selection panel
        for (int i = 0; i < availableParts.Length; i++)
        {
            ShipPartDefinition part = availableParts[i];
            if (part.partType == partType)
            {
                // Create button for part
                GameObject buttObj = Instantiate(buttPrefab, selectionPanel.transform);
                Button butt = buttObj.GetComponent<Button>();
                TextMeshProUGUI buttText = butt.GetComponentInChildren<TextMeshProUGUI>();
                buttText.text = part.displayName;
                int index = i;
                butt.onClick.AddListener(() => SelectPart(index));
            }
        }
        selectionPanel.SetActive(true);
    }

    public void SwitchToSailScene()
    {
        if (!hullPlaced)
        {
            Debug.Log("Cannot switch scenes: no hull has been placed.");
            return;
        }

        StartCoroutine(LoadAndActivateSailScene());
    }

    IEnumerator LoadAndActivateSailScene()
    {
        AsyncOperation op = SceneManager.LoadSceneAsync(sailSceneName, LoadSceneMode.Additive);

        yield return op;

        Scene loadedScene = SceneManager.GetSceneByName(sailSceneName);

        if (!loadedScene.isLoaded)
        {
            Debug.LogWarning("Sail scene did not load: " + sailSceneName);
            yield break;
        }

        MoveBoatToSailScene(loadedScene);

        Camera builderCamera = Camera.main;
        if (builderCamera != null)
        {
            builderCamera.gameObject.SetActive(false);
        }

        selectedPart = null;
        DestroyPreview();

        SceneManager.SetActiveScene(loadedScene);

        BoatFollowCamera boatFollowCamera = FindAnyObjectByType<BoatFollowCamera>();
        if (boatFollowCamera != null)
        {
            boatFollowCamera.target = shipRoot;
        }
        else
        {
            Debug.LogWarning("BoatFollowCamera not found in sail scene.");
        }

        yield return SceneManager.UnloadSceneAsync(builderSceneName);
    }

    void MoveBoatToSailScene(Scene targetScene)
    {
        if (shipRoot.parent != null)
        {
            shipRoot.SetParent(null);
        }

        PrepareBoatForSailing();

        SceneManager.MoveGameObjectToScene(shipRoot.gameObject, targetScene);
    }

    void PrepareBoatForSailing()
    {
        Rigidbody rb = shipRoot.GetComponent<Rigidbody>();

        if (rb == null)
        {
            rb = shipRoot.gameObject.AddComponent<Rigidbody>();
        }

        // Calulate weighted center of mass based on parts
        rb.centerOfMass = weightedCenterOfMassAccumulated / totalMass;
        rb.mass = totalMass;

        if (shipRoot.GetComponent<WaterBuoyancy>() == null)
        {
            WaterBuoyancy buoyancy = shipRoot.gameObject.AddComponent<WaterBuoyancy>();
            buoyancy.waterAngularDrag = 5f;
            buoyancy.objectDensity = 0.25f;
            ApplyHullMeshPoints(buoyancy);
        }

        if (shipRoot.GetComponent<ShipController>() == null)
        {
            shipRoot.gameObject.AddComponent<ShipController>();
        }
    }
}