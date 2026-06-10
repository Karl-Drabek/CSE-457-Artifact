using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using Borodar.FarlandSkies.LowPoly;

/// <summary>
/// Main ship-building controller used by the ShipBuilding scene.
/// It handles part selection, preview placement, snap-point placement,
/// gold spending, and moving the finished boat into the sail scene.
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

    [Header("Gold UI")]
    [SerializeField] TMP_Text goldText;
    [SerializeField] string goldTextFormat = "Gold: {0}";
    [SerializeField] TMP_Text goldMessageText;
    [SerializeField] float goldMessageSeconds = 1.5f;

    [Header("UI Elements")]
    [SerializeField] Button hullButton;
    [SerializeField] GameObject selectionPanel;
    [SerializeField] GameObject buttPrefab;

    ShipPartDefinition selectedPart;
    GameObject previewObject;
    bool hullPlaced;

    Vector3[] buoyancy_vertices = { };
    Vector3 hull_base_point = Vector3.zero;
    float hull_local_height = 0f;

    float goldMessageHideTime = -1f;

    const string VISUAL = "VisualComponent";

    void Awake()
    {
        if (selectionPanel != null)
        {
            selectionPanel.SetActive(false);
        }

        if (goldMessageText != null)
        {
            goldMessageText.text = "";
        }

        RefreshGoldUi();

        // If a persistent boat already exists, adopt it.
        GameObject existingBoat = GameObject.Find("BoatParent");
        if (existingBoat != null && existingBoat.transform != shipRoot)
        {
            shipRoot = existingBoat.transform;
            hullPlaced = true;

            Rigidbody adoptedBody = shipRoot.GetComponent<Rigidbody>();
            if (adoptedBody != null)
            {
                adoptedBody.isKinematic = true;
            }
        }
    }

    void Update()
    {
        RefreshGoldUi();

        if (goldMessageText != null && goldMessageHideTime > 0f && Time.time >= goldMessageHideTime)
        {
            goldMessageText.text = "";
            goldMessageHideTime = -1f;
        }

        if (Mouse.current == null)
        {
            return;
        }

        bool pointerOverUi = IsPointerOverUi();

        if (!pointerOverUi)
        {
            UpdatePreviewPosition();
        }

        if (Mouse.current.leftButton.wasPressedThisFrame && !pointerOverUi)
        {
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
            Debug.LogWarning("Invalid part index selected.", this);
            return;
        }

        selectedPart = availableParts[index];
        CreatePreview();
    }

    public void SwitchToSailScene()
    {
        if (!hullPlaced)
        {
            Debug.Log("Cannot save ship yet because no hull has been placed.", this);
            return;
        }

        VoyageCycleController.SetPhaseToHome();
        StartCoroutine(LoadSailSceneAndTransferBoat());
    }

    void TryPlaceSelectedPart()
    {
        if (selectedPart == null || previewObject == null)
        {
            return;
        }

        if (selectedPart.partType == ShipPartType.Hull)
        {
            TryPlaceHull();
            return;
        }

        if (!hullPlaced)
        {
            Debug.Log("Place the hull first.", this);
            return;
        }

        TryPlacePartOnBoatPiece();
    }

    void TryPlacePartOnBoatPiece()
    {
        if (Camera.main == null || Mouse.current == null)
        {
            return;
        }

        Ray ray = Camera.main.ScreenPointToRay(Mouse.current.position.ReadValue());

        if (!Physics.Raycast(ray, out RaycastHit hit))
        {
            return;
        }

        BoatPiece targetPiece = hit.collider.GetComponentInParent<BoatPiece>();
        if (targetPiece == null)
        {
            return;
        }

        if (!TrySpendGoldForSelectedPart())
        {
            return;
        }

        Quaternion rotation = Quaternion.FromToRotation(Vector3.up, hit.normal);
        GameObject obj = Instantiate(selectedPart.prefab, hit.point, rotation, shipRoot);

        BoatPiece newPiece = obj.GetComponent<BoatPiece>();
        if (newPiece != null)
        {
            newPiece.AttachTo(targetPiece);
        }
        else
        {
            Debug.LogWarning("Placed part does not have a BoatPiece component.", this);
        }

        SetUpPart(obj);
    }

    void TryPlaceHull()
    {
        if (hullPlaced)
        {
            Debug.Log("A hull has already been placed.", this);
            return;
        }

        if (Camera.main == null || Mouse.current == null)
        {
            return;
        }

        Ray ray = Camera.main.ScreenPointToRay(Mouse.current.position.ReadValue());

        if (!Physics.Raycast(ray, out RaycastHit hit))
        {
            return;
        }

        if (hit.collider.GetComponentInParent<BoatPiece>() != null)
        {
            return;
        }

        if (!TrySpendGoldForSelectedPart())
        {
            return;
        }

        GameObject hullObj = Instantiate(
            selectedPart.prefab,
            hit.point,
            Quaternion.FromToRotation(Vector3.up, hit.normal),
            shipRoot);

        BoatPiece hullPiece = hullObj.GetComponent<BoatPiece>();
        if (hullPiece != null)
        {
            hullPiece.isRootHull = true;
        }

        SetUpPart(hullObj);
    }

    bool TrySpendGoldForSelectedPart()
    {
        if (selectedPart == null)
        {
            return false;
        }

        int cost = GetSelectedPartGoldCost();

        if (!VoyageCycleController.TrySpendGold(cost))
        {
            ShowGoldMessage("Not enough gold");
            RefreshGoldUi();
            RefreshSelectionButtons();
            return false;
        }

        RefreshGoldUi();
        RefreshSelectionButtons();
        return true;
    }

    int GetSelectedPartGoldCost()
    {
        return GetPartGoldCost(selectedPart);
    }

    int GetPartGoldCost(ShipPartDefinition part)
    {
        if (part == null || part.prefab == null)
        {
            return 0;
        }

        BoatPiece boatPiece = part.prefab.GetComponent<BoatPiece>();

        if (boatPiece == null)
        {
            return 0;
        }

        return Mathf.Max(0, boatPiece.goldCost);
    }

    bool CanAffordPart(ShipPartDefinition part)
    {
        if (part == null)
        {
            return false;
        }

        return VoyageCycleController.CanAffordGold(GetPartGoldCost(part));
    }

    void RefreshGoldUi()
    {
        if (goldText == null)
        {
            return;
        }

        goldText.text = string.Format(goldTextFormat, VoyageCycleController.GetGoldAmount());
    }

    void ShowGoldMessage(string message)
    {
        if (goldMessageText == null)
        {
            Debug.Log(message, this);
            return;
        }

        goldMessageText.text = message;
        goldMessageHideTime = Time.time + goldMessageSeconds;
    }

    void RefreshSelectionButtons()
    {
        if (selectionPanel == null || !selectionPanel.activeSelf)
        {
            return;
        }

        foreach (Button button in selectionPanel.GetComponentsInChildren<Button>())
        {
            TextMeshProUGUI text = button.GetComponentInChildren<TextMeshProUGUI>();
            if (text == null)
            {
                continue;
            }

            for (int i = 0; i < availableParts.Length; i++)
            {
                ShipPartDefinition part = availableParts[i];
                int cost = GetPartGoldCost(part);
                string expectedText = part.displayName + " - " + cost + "g";

                if (text.text == expectedText)
                {
                    button.interactable = CanAffordPart(part);
                    break;
                }
            }
        }
    }

    void SetUpPart(GameObject obj)
    {
        ApplyMaterial(obj, selectedPart.material);

        BoatPiece piece = obj.GetComponent<BoatPiece>();
        if (piece != null)
        {
            piece.pieceMass = selectedPart.mass;
            piece.localCenterOfMass = selectedPart.centerOfMass;
        }

        if (selectedPart.partType == ShipPartType.Hull)
        {
            hullPlaced = true;

            if (hullButton != null)
            {
                hullButton.interactable = false;
            }

            if (selectionPanel != null)
            {
                selectionPanel.SetActive(false);
            }

            GetHullMeshPoints(obj.GetComponentInChildren<MeshFilter>());
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

        foreach (Collider collider in previewObject.GetComponentsInChildren<Collider>())
        {
            collider.enabled = false;
        }

        foreach (Rigidbody rb in previewObject.GetComponentsInChildren<Rigidbody>())
        {
            rb.isKinematic = true;
            rb.detectCollisions = false;
        }

        ApplyPreviewMaterial(previewObject);
        previewObject.SetActive(false);
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

        bool hitsBoatPiece = hit.collider.GetComponentInParent<BoatPiece>() != null;

        if (selectedPart.partType == ShipPartType.Hull)
        {
            previewObject.SetActive(!hitsBoatPiece);

            if (!hitsBoatPiece)
            {
                previewObject.transform.position = hit.point;
                previewObject.transform.rotation = Quaternion.FromToRotation(Vector3.up, hit.normal);
            }
        }
        else
        {
            previewObject.SetActive(hitsBoatPiece);

            if (hitsBoatPiece)
            {
                previewObject.transform.position = hit.point;
                previewObject.transform.rotation = Quaternion.FromToRotation(Vector3.up, hit.normal);
            }
        }
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

    void ApplyMaterial(GameObject target, Material material)
    {
        if (material == null || target == null)
        {
            return;
        }

        bool appliedAny = false;

        foreach (Renderer rend in target.GetComponentsInChildren<Renderer>())
        {
            if (rend.CompareTag(VISUAL))
            {
                rend.material = material;
                appliedAny = true;
            }
        }

        if (!appliedAny)
        {
            foreach (Renderer rend in target.GetComponentsInChildren<Renderer>())
            {
                rend.material = material;
            }
        }
    }

    void GetHullMeshPoints(MeshFilter meshFilter)
    {
        if (meshFilter == null || meshFilter.sharedMesh == null)
        {
            return;
        }

        Transform meshTransform = meshFilter.transform;
        Vector3[] normals = meshFilter.sharedMesh.normals;
        Vector3[] vertices = meshFilter.sharedMesh.vertices;
        List<Vector3> worldPointsOnHull = new List<Vector3>();

        for (int i = 0; i < normals.Length; i++)
        {
            Vector3 worldPoint = meshTransform.TransformPoint(vertices[i]);

            if (!worldPointsOnHull.Contains(worldPoint))
            {
                worldPointsOnHull.Add(worldPoint);
            }
        }

        buoyancy_vertices = worldPointsOnHull.ToArray();
        hull_base_point = meshTransform.TransformPoint(meshFilter.sharedMesh.bounds.min);
        hull_local_height = meshFilter.sharedMesh.bounds.max.y * meshTransform.localScale.y;
    }

    void ApplyHullMeshPoints(WaterBuoyancy buoyancy)
    {
        if (buoyancy == null || buoyancy_vertices == null || buoyancy_vertices.Length == 0)
        {
            return;
        }

        for (int i = 0; i < buoyancy_vertices.Length; i++)
        {
            buoyancy_vertices[i] = buoyancy.transform.InverseTransformPoint(buoyancy_vertices[i]);
        }

        hull_base_point = buoyancy.transform.InverseTransformPoint(hull_base_point);
        buoyancy.hull_height = hull_local_height;
        buoyancy.SetManualPoints(buoyancy_vertices, hull_base_point.y);
    }

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

    void BuildPartSelectionPanel(ShipPartType partType)
    {
        foreach (Transform child in selectionPanel.transform)
        {
            Destroy(child.gameObject);
        }

        for (int i = 0; i < availableParts.Length; i++)
        {
            ShipPartDefinition part = availableParts[i];

            if (part.partType == partType)
            {
                GameObject buttObj = Instantiate(buttPrefab, selectionPanel.transform);
                Button butt = buttObj.GetComponent<Button>();
                TextMeshProUGUI buttText = butt.GetComponentInChildren<TextMeshProUGUI>();

                int cost = GetPartGoldCost(part);

                if (buttText != null)
                {
                    buttText.text = part.displayName + " - " + cost + "g";
                }

                if (butt != null)
                {
                    butt.interactable = CanAffordPart(part);

                    int index = i;
                    butt.onClick.AddListener(() => SelectPart(index));
                }
            }
        }

        selectionPanel.SetActive(true);
    }

    static bool TryGetCycleManager(out SkyboxCycleManager cycleManager)
    {
        cycleManager = GameObject.FindAnyObjectByType<SkyboxCycleManager>();
        return cycleManager != null;
    }

    IEnumerator LoadSailSceneAndTransferBoat()
    {
        GameObject skybox = GameObject.FindWithTag("Skybox");

        if (skybox != null && skybox.scene.name == "DontDestroyOnLoad")
        {
            Destroy(skybox);
        }

        MakeBoatPersistent();

        if (TryGetCycleManager(out SkyboxCycleManager cycleManager))
        {
            cycleManager.CycleProgress = 24f;
            cycleManager.Paused = false;
        }

        AsyncOperation operation = SceneManager.LoadSceneAsync(sailSceneName, LoadSceneMode.Additive);
        yield return operation;

        Scene sailScene = SceneManager.GetSceneByName(sailSceneName);

        if (!sailScene.isLoaded)
        {
            Debug.LogWarning("Sail scene did not load: " + sailSceneName, this);
            yield break;
        }

        Camera builderCamera = Camera.main;

        if (builderCamera != null)
        {
            builderCamera.gameObject.SetActive(false);
        }

        selectedPart = null;
        DestroyPreview();

        SceneManager.SetActiveScene(sailScene);

        BoatFollowCamera followCamera = FindAnyObjectByType<BoatFollowCamera>();

        if (followCamera != null)
        {
            followCamera.target = shipRoot;
        }

        yield return SceneManager.UnloadSceneAsync(builderSceneName);
    }

    void MakeBoatPersistent()
    {
        if (shipRoot.parent != null)
        {
            shipRoot.SetParent(null);
        }

        shipRoot.gameObject.name = "BoatParent";
        NormalizeBoatRoot();
        PrepareBoatForSailing();
        DontDestroyOnLoad(shipRoot.gameObject);
    }

    void NormalizeBoatRoot()
    {
        Renderer[] renderers = shipRoot.GetComponentsInChildren<Renderer>();

        if (renderers.Length == 0)
        {
            return;
        }

        Bounds combined = renderers[0].bounds;

        for (int i = 1; i < renderers.Length; i++)
        {
            combined.Encapsulate(renderers[i].bounds);
        }

        Vector3 delta = combined.center - shipRoot.position;

        if (delta.sqrMagnitude < 0.0001f)
        {
            return;
        }

        shipRoot.position += delta;

        foreach (Transform child in shipRoot)
        {
            child.localPosition -= delta;
        }
    }

    void PrepareBoatForSailing()
    {
        Rigidbody body = shipRoot.GetComponent<Rigidbody>();

        if (body == null)
        {
            body = shipRoot.gameObject.AddComponent<Rigidbody>();
        }

        body.isKinematic = false;
        body.useGravity = true;
        body.linearVelocity = Vector3.zero;
        body.angularVelocity = Vector3.zero;
        body.constraints = RigidbodyConstraints.FreezeAll;

        BoatMassManager massManager = shipRoot.GetComponent<BoatMassManager>();

        if (massManager == null)
        {
            massManager = shipRoot.gameObject.AddComponent<BoatMassManager>();
        }

        massManager.RecalculateMass();

        WaterBuoyancy buoyancy = shipRoot.GetComponent<WaterBuoyancy>();

        if (buoyancy == null)
        {
            buoyancy = shipRoot.gameObject.AddComponent<WaterBuoyancy>();
        }

        ApplyHullMeshPoints(buoyancy);

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