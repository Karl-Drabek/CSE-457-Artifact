using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

public class ShipBuilder : MonoBehaviour
{
    [Header("Available Parts")]
    [SerializeField] private ShipPartDefinition[] availableParts;

    [Header("Scene References")]
    [SerializeField] private Transform shipRoot;
    [SerializeField] private Transform hullSpawnPoint;

    private ShipPartDefinition selectedPart;
    private bool hullPlaced = false;

    void Update()
    {
        if (Mouse.current == null)
        {
            return;
        }

        if (Mouse.current.leftButton.wasPressedThisFrame)
        {
            // Prevent clicks on UI from also placing parts in the scene
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
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
    }

    void TryPlaceSelectedPart()
    {
        if (selectedPart == null)
        {
            Debug.Log("No part selected.");
            return;
        }

        if (selectedPart.partType == ShipPartType.Hull)
        {
            TryPlaceHull();
            return;
        }

        TryPlaceAtSnapPoint();
    }

    void TryPlaceHull()
    {
        if (hullPlaced)
        {
            Debug.Log("Hull already placed.");
            return;
        }

        Instantiate(selectedPart.prefab, hullSpawnPoint.position, hullSpawnPoint.rotation, shipRoot);
        hullPlaced = true;
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

        Instantiate(selectedPart.prefab, snapPoint.AttachTransform.position, snapPoint.AttachTransform.rotation, shipRoot);
        snapPoint.occupied = true;
    }
}