using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

/// <summary>
/// Main ship-building controller used by the ShipBuilding scene.
/// It handles part selection, preview placement, snap-point placement,
/// and moving the finished boat into the sail scene.
/// </summary>
public class ShipBuilder : MonoBehaviour
{
    [Header("Available Parts")]
    [SerializeField] ShipPartDefinition[] availableParts;

    [Header("Scene References")]
    [SerializeField] Transform shipRoot;

    [Header("Preview")]
    [SerializeField] Material previewMaterial;

    [Header("Scene Transfer")]
    [SerializeField] string sailSceneName = "SampleScene";
    [SerializeField] string builderSceneName = "ShipBuilding";

    ShipPartDefinition selectedPart;
    GameObject previewObject;
    bool hullPlaced;

    void Start()
    {
        UpdateSnapPointVisibility();
    }

    void Update()
    {
        if (Mouse.current == null)
        {
            return;
        }

        bool pointerOverUi = IsPointerOverUi();
        if (!pointerOverUi)
        {
            UpdatePreviewPosition();
        }

        if (!Mouse.current.leftButton.wasPressedThisFrame || pointerOverUi)
        {
            return;
        }

        TryPlaceSelectedPart();
    }

    /// <summary>
    /// Called by the part-selection UI.
    /// </summary>
    public void SelectPart(int index)
    {
        if (index < 0 || index >= availableParts.Length)
        {
            Debug.LogWarning("Invalid part index selected.", this);
            return;
        }

        selectedPart = availableParts[index];
        CreatePreview();
        UpdateSnapPointVisibility();
    }

    /// <summary>
    /// Called by the Save Ship button in the builder scene.
    /// The method name is kept for the existing scene button hookup.
    /// </summary>
    public void SwitchToSailScene()
    {
        if (!hullPlaced)
        {
            Debug.Log("Cannot save ship yet because no hull has been placed.", this);
            return;
        }

        StartCoroutine(LoadSailSceneAndTransferBoat());
    }

    void TryPlaceSelectedPart()
    {
        if (selectedPart == null)
        {
            return;
        }

        if (!TryGetHoveredSnapPoint(out SnapPoint snapPoint))
        {
            return;
        }

        if (snapPoint.occupied || snapPoint.acceptsPartType != selectedPart.partType)
        {
            return;
        }

        if (selectedPart.partType == ShipPartType.Hull && hullPlaced)
        {
            Debug.Log("A hull has already been placed.", this);
            return;
        }

        Instantiate(
            selectedPart.prefab,
            snapPoint.AttachTransform.position,
            snapPoint.AttachTransform.rotation,
            shipRoot);

        snapPoint.occupied = true;

        if (selectedPart.partType == ShipPartType.Hull)
        {
            hullPlaced = true;
        }

        selectedPart = null;
        DestroyPreview();
        UpdateSnapPointVisibility();
    }

    bool TryGetHoveredSnapPoint(out SnapPoint snapPoint)
    {
        snapPoint = null;

        if (Camera.main == null || Mouse.current == null)
        {
            return false;
        }

        Ray ray = Camera.main.ScreenPointToRay(Mouse.current.position.ReadValue());
        if (!Physics.Raycast(ray, out RaycastHit hit))
        {
            return false;
        }

        snapPoint = hit.collider.GetComponent<SnapPoint>();
        if (snapPoint == null)
        {
            snapPoint = hit.collider.GetComponentInParent<SnapPoint>();
        }

        return snapPoint != null;
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

        foreach (Collider collider in previewObject.GetComponentsInChildren<Collider>())
        {
            collider.enabled = false;
        }

        foreach (SnapPoint snapPoint in previewObject.GetComponentsInChildren<SnapPoint>())
        {
            snapPoint.enabled = false;
            snapPoint.SetVisible(false);
        }

        ApplyPreviewMaterial(previewObject);
        previewObject.SetActive(false);
    }

    void ApplyPreviewMaterial(GameObject target)
    {
        if (previewMaterial == null || target == null)
        {
            return;
        }

        foreach (Renderer renderer in target.GetComponentsInChildren<Renderer>())
        {
            renderer.material = previewMaterial;
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

        Ray ray = Camera.main.ScreenPointToRay(Mouse.current.position.ReadValue());
        if (!Physics.Raycast(ray, out RaycastHit hit))
        {
            previewObject.SetActive(false);
            return;
        }

        previewObject.SetActive(true);

        SnapPoint snapPoint = hit.collider.GetComponent<SnapPoint>() ?? hit.collider.GetComponentInParent<SnapPoint>();
        if (snapPoint != null && !snapPoint.occupied && snapPoint.acceptsPartType == selectedPart.partType)
        {
            previewObject.transform.position = snapPoint.AttachTransform.position;
            previewObject.transform.rotation = snapPoint.AttachTransform.rotation;
            return;
        }

        previewObject.transform.position = hit.point;
        previewObject.transform.rotation = Quaternion.identity;
    }

    void UpdateSnapPointVisibility()
    {
        SnapPoint[] snapPoints = FindObjectsByType<SnapPoint>(FindObjectsSortMode.None);
        foreach (SnapPoint snapPoint in snapPoints)
        {
            bool shouldShow = selectedPart != null
                && !snapPoint.occupied
                && snapPoint.acceptsPartType == selectedPart.partType;
            snapPoint.SetVisible(shouldShow);
        }
    }

    IEnumerator LoadSailSceneAndTransferBoat()
    {
        AsyncOperation operation = SceneManager.LoadSceneAsync(sailSceneName, LoadSceneMode.Additive);
        yield return operation;

        Scene sailScene = SceneManager.GetSceneByName(sailSceneName);
        if (!sailScene.isLoaded)
        {
            Debug.LogWarning("Sail scene did not load: " + sailSceneName, this);
            yield break;
        }

        MoveBoatToSailScene(sailScene);

        Camera builderCamera = Camera.main;
        if (builderCamera != null)
        {
            builderCamera.gameObject.SetActive(false);
        }

        selectedPart = null;
        DestroyPreview();
        UpdateSnapPointVisibility();

        SceneManager.SetActiveScene(sailScene);

        BoatFollowCamera followCamera = FindAnyObjectByType<BoatFollowCamera>();
        if (followCamera != null)
        {
            followCamera.target = shipRoot;
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

    // Adds the runtime components the sailing scene expects. This only happens
    // when the player saves the builder result into the main scene.
    void PrepareBoatForSailing()
    {
        Rigidbody body = shipRoot.GetComponent<Rigidbody>();
        if (body == null)
        {
            body = shipRoot.gameObject.AddComponent<Rigidbody>();
        }

        body.centerOfMass = new Vector3(0f, -5f, -0.3f);

        if (shipRoot.GetComponent<WaterBuoyancy>() == null)
        {
            WaterBuoyancy buoyancy = shipRoot.gameObject.AddComponent<WaterBuoyancy>();
            buoyancy.waterAngularDrag = 5f;
            buoyancy.objectDensity = 0.25f;
            buoyancy.zEdgeOffset = -0.3f;
            buoyancy.hull_height = -2.2f;
        }

        if (shipRoot.GetComponent<ShipController>() == null)
        {
            shipRoot.gameObject.AddComponent<ShipController>();
        }
    }

    static bool IsPointerOverUi()
    {
        return EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
    }
}
