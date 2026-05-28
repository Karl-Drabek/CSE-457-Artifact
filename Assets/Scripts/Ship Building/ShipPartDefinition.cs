using UnityEngine;

[CreateAssetMenu(fileName = "NewShipPart", menuName = "Ship Builder/Ship Part Definition")]
public class ShipPartDefinition : ScriptableObject
{
    public string displayName;
    public ShipPartType partType;
    public GameObject prefab;
    public Sprite icon;
}