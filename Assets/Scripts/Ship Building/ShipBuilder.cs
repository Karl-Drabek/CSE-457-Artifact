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


    [Header("UI Elements")]
    [SerializeField] Button hullButton;
    [SerializeField] GameObject selectionPanel;
    [SerializeField] GameObject buttPrefab;

    ShipPartDefinition selectedPart;
    GameObject previewObject;
    bool hullPlaced;
    Vector3[] buoyancy_vertices = { };
    Vector3 hull_base_point = Vector3.zero;
    Vector3 hull_top_point = Vector3.zero;
    const string VISUAL = "VisualComponent";

    void Awake()
    {
        if (selectionPanel != null)
        {
            selectionPanel.SetActive(false);
        }

        // If a persistent boat already exists (player returned from sailing), adopt it —
        // but only if it still has a BoatMassManager (hull intact). If the hull broke,
        // BoatMassManager was removed; treat that remnant as destroyed and start fresh.
        GameObject existingBoat = GameObject.Find("BoatParent");
        if (existingBoat != null && existingBoat.transform != shipRoot)
        {
            if (existingBoat.GetComponent<BoatMassManager>() != null)
            {
                shipRoot = existingBoat.transform;
                hullPlaced = true;

                Rigidbody adoptedBody = shipRoot.GetComponent<Rigidbody>();
                if (adoptedBody != null)
                {
                    adoptedBody.isKinematic = true;
                }
            }
            else
            {
                // Hull was destroyed — clean up the rootless remnant so it doesn't linger.
                Destroy(existingBoat);
            }
        }
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

        if (Mouse.current.leftButton.wasPressedThisFrame && !pointerOverUi)
        {
            TryPlaceSelectedPart();
        }
        else if (Mouse.current.rightButton.wasPressedThisFrame)
        {
            DestroyPreview();
        }
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

        // Ensure the voyage state is Home so HandleSceneLoaded initialises the sail
        // scene correctly (locks the boat at spawn, shows home menu, etc.).
        VoyageCycleController.SetPhaseToHome();
        StartCoroutine(LoadSailSceneAndTransferBoat());
    }

    void TryPlaceSelectedPart()
    {
        if (selectedPart == null || previewObject == null)
        {
            return;
        }

        // Hull is the first piece — place it anywhere on the ground via a plain raycast.
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

        // Prefer a compatible snap point if one is hovered.
        if (TryGetHoveredSnapPoint(out SnapPoint snapPoint)
            && !snapPoint.occupied
            && snapPoint.acceptsPartType == selectedPart.partType)
        {
            GameObject snapObj = Instantiate(
                selectedPart.prefab,
                snapPoint.AttachTransform.position,
                snapPoint.AttachTransform.rotation,
                shipRoot);

            BoatPiece snapPiece = snapObj.GetComponent<BoatPiece>();
            if (snapPiece != null)
            {
                BoatPiece parentPiece = snapPoint.GetComponentInParent<BoatPiece>();
                snapPiece.AttachTo(parentPiece);
            }

            snapPoint.occupied = true;
            SetUpPart(snapObj);
            selectedPart = null;
            DestroyPreview();
            UpdateSnapPointVisibility();
            return;
        }

        // Fallback: place directly on any existing BoatPiece (for prefabs without SnapPoints).
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
        selectedPart = null;
        DestroyPreview();
        UpdateSnapPointVisibility();
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

        // Don't place on an existing boat piece
        if (hit.collider.GetComponentInParent<BoatPiece>() != null)
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

        foreach (Collider collider in previewObject.GetComponentsInChildren<Collider>())
        {
            collider.enabled = false;
        }

        foreach (Rigidbody rb in previewObject.GetComponentsInChildren<Rigidbody>())
        {
            rb.isKinematic = true;
            rb.detectCollisions = false;
        }

        foreach (SnapPoint sp in previewObject.GetComponentsInChildren<SnapPoint>())
        {
            sp.enabled = false;
            sp.SetVisible(false);
        }

        ApplyPreviewMaterial(previewObject);
        previewObject.SetActive(false);
        UpdateSnapPointVisibility();
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

        // Prefer a compatible snap point.
        SnapPoint snapPoint = hit.collider.GetComponent<SnapPoint>()
            ?? hit.collider.GetComponentInParent<SnapPoint>();

        if (snapPoint != null && !snapPoint.occupied && snapPoint.acceptsPartType == selectedPart.partType)
        {
            previewObject.SetActive(true);
            previewObject.transform.position = snapPoint.AttachTransform.position;
            previewObject.transform.rotation = snapPoint.AttachTransform.rotation;
            return;
        }

        bool hitsBoatPiece = hit.collider.GetComponentInParent<BoatPiece>() != null;

        if (selectedPart.partType == ShipPartType.Hull)
        {
            // Hull preview only on non-boat surfaces.
            previewObject.SetActive(!hitsBoatPiece);
            if (!hitsBoatPiece)
            {
                previewObject.transform.position = hit.point;
                previewObject.transform.rotation = Quaternion.FromToRotation(Vector3.up, hit.normal);
            }
        }
        else
        {
            // Non-hull preview only when hovering over an existing BoatPiece.
            previewObject.SetActive(hitsBoatPiece);
            if (hitsBoatPiece)
            {
                previewObject.transform.position = hit.point;
                previewObject.transform.rotation = Quaternion.FromToRotation(Vector3.up, hit.normal);
            }
        }
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

        // No VISUAL-tagged renderer found — apply to every renderer so the material is never silently skipped.
        if (!appliedAny)
        {
            foreach (Renderer rend in target.GetComponentsInChildren<Renderer>())
            {
                rend.material = material;
            }
        }
    }

    // Get all mesh points on the hull with respect to the hull mesh.
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
        hull_top_point  = meshTransform.TransformPoint(meshFilter.sharedMesh.bounds.max);
    }

    // Transform the hull points to root space and assign as manual buoyancy sample points.
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
        // Convert the world-space hull top into the buoyancy root's local space so
        // hull_height matches the same coordinate frame as the sample points.
        buoyancy.hull_height = buoyancy.transform.InverseTransformPoint(hull_top_point).y;
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
                buttText.text = part.displayName;
                int index = i;
                butt.onClick.AddListener(() => SelectPart(index));
            }
        }

        selectionPanel.SetActive(true);
    }

    IEnumerator LoadSailSceneAndTransferBoat()
    {
        // Destroy the skybox that was persisted from the sail scene (DontDestroyOnLoad)
        // so we don't end up with two skyboxes when the sail scene loads its own.
        GameObject persistedSkybox = GameObject.FindWithTag("Skybox");
        if (persistedSkybox != null && persistedSkybox.scene.name == "DontDestroyOnLoad")
        {
            Destroy(persistedSkybox);
        }

        // Prepare and persist the boat before any scene changes.
        MakeBoatPersistent();

        // Reset the skybox cycle manager so it starts fresh in the sail scene.
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
        UpdateSnapPointVisibility();

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

        shipRoot.gameObject.name = "BoatParent"; // Found by VoyageCycleController and World by this name
        NormalizeBoatRoot();
        PrepareBoatForSailing();
        DontDestroyOnLoad(shipRoot.gameObject);
    }

    // Moves shipRoot to the world-space center of all child renderers, then shifts
    // direct children back by the same delta so nothing moves visually. This ensures
    // the camera (which tracks shipRoot) points at the actual geometric center of the boat.
    void NormalizeBoatRoot()
    {
        Renderer[] renderers = shipRoot.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) return;

        Bounds combined = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            combined.Encapsulate(renderers[i].bounds);

        Vector3 delta = combined.center - shipRoot.position;
        if (delta.sqrMagnitude < 0.0001f) return;

        shipRoot.position += delta;
        foreach (Transform child in shipRoot)
            child.localPosition -= delta;
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

        body.isKinematic = false;
        body.useGravity = true;
        body.linearVelocity = Vector3.zero;
        body.angularVelocity = Vector3.zero;
        // Freeze until VoyageCycleController releases the lock when SetSail is pressed.
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

    static bool TryGetCycleManager(out SkyboxCycleManager cycleManager)
    {
        cycleManager = FindAnyObjectByType<SkyboxCycleManager>();
        return cycleManager != null;
    }

    static bool IsPointerOverUi()
    {
        return EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
    }
}
