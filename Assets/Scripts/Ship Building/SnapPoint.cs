using UnityEngine;

public class SnapPoint : MonoBehaviour
{
    public ShipPartType acceptsPartType;
    public bool occupied;

    [SerializeField] private GameObject debugVisual;

    public Transform AttachTransform
    {
        get { return transform; }
    }

    public void SetVisible(bool visible)
    {
        if (debugVisual != null)
        {
            debugVisual.SetActive(visible);
        }
    }
}