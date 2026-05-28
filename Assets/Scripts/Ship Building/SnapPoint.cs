using UnityEngine;

public class SnapPoint : MonoBehaviour
{
    public ShipPartType acceptsPartType;
    public bool occupied;

    public Transform AttachTransform
    {
        get { return transform; }
    }
}