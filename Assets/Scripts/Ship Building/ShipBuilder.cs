using System.Collections;
using UnityEngine;
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

    private ShipPartDefinition selectedPart;
    private bool hullPlaced = false;

    private GameObject previewObject;

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
        UpdateSnapPointVisibility();
    }

    void TryPlaceSelectedPart()
    {
        if (selectedPart == null)
        {
            Debug.Log("No part selected.");
            return;
        }

        TryPlaceAtSnapPoint();
    }

    void TryPlaceAtSnapPoint()
    {
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

        SnapPoint snapPoint = hit.collider.GetComponent<SnapPoint>();

        if (snapPoint == null)
        {
            snapPoint = hit.collider.GetComponentInParent<SnapPoint>();
        }

        if (snapPoint == null)
        {
            Debug.Log("Clicked object is not a snap point.");
            return;
        }

        if (snapPoint.occupied)
        {
            Debug.Log("Snap point already occupied.");
            return;
        }

        if (snapPoint.acceptsPartType != selectedPart.partType)
        {
            Debug.Log("This part does not fit this snap point.");
            return;
        }

        if (selectedPart.partType == ShipPartType.Hull && hullPlaced)
        {
            Debug.Log("Hull already placed.");
            return;
        }

        Instantiate(
            selectedPart.prefab,
            snapPoint.AttachTransform.position,
            snapPoint.AttachTransform.rotation,
            shipRoot
        );

        snapPoint.occupied = true;

        if (selectedPart.partType == ShipPartType.Hull)
        {
            hullPlaced = true;
        }

        selectedPart = null;
        DestroyPreview();
        UpdateSnapPointVisibility();
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

        Collider[] colliders = previewObject.GetComponentsInChildren<Collider>();
        foreach (Collider collider in colliders)
        {
            collider.enabled = false;
        }

        SnapPoint[] snapPoints = previewObject.GetComponentsInChildren<SnapPoint>();
        foreach (SnapPoint snapPoint in snapPoints)
        {
            snapPoint.enabled = false;
            snapPoint.SetVisible(false);
        }

        ApplyPreviewMaterial(previewObject);

        previewObject.SetActive(false);
    }

    void ApplyPreviewMaterial(GameObject target)
    {
        if (previewMaterial == null)
        {
            Debug.LogWarning("Preview material is not assigned.");
            return;
        }

        Renderer[] renderers = target.GetComponentsInChildren<Renderer>();

        foreach (Renderer renderer in renderers)
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

        Vector2 mousePosition = Mouse.current.position.ReadValue();
        Ray ray = Camera.main.ScreenPointToRay(mousePosition);

        if (!Physics.Raycast(ray, out RaycastHit hit))
        {
            previewObject.SetActive(false);
            return;
        }

        previewObject.SetActive(true);

        SnapPoint snapPoint = hit.collider.GetComponent<SnapPoint>();

        if (snapPoint == null)
        {
            snapPoint = hit.collider.GetComponentInParent<SnapPoint>();
        }

        if (snapPoint != null &&
            !snapPoint.occupied &&
            snapPoint.acceptsPartType == selectedPart.partType)
        {
            previewObject.transform.position = snapPoint.AttachTransform.position;
            previewObject.transform.rotation = snapPoint.AttachTransform.rotation;
        }
        else
        {
            previewObject.transform.position = hit.point;
            previewObject.transform.rotation = Quaternion.identity;
        }
    }

    void UpdateSnapPointVisibility()
    {
        SnapPoint[] snapPoints = FindObjectsByType<SnapPoint>(FindObjectsSortMode.None);

        foreach (SnapPoint snapPoint in snapPoints)
        {
            bool shouldShow =
                selectedPart != null &&
                !snapPoint.occupied &&
                snapPoint.acceptsPartType == selectedPart.partType;

            snapPoint.SetVisible(shouldShow);
        }
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
        UpdateSnapPointVisibility();

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

        rb.centerOfMass = new Vector3(0f, -5f, -0.3f);

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
}