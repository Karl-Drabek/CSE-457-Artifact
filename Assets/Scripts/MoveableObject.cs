using UnityEngine;

public class MoveableObject : MonoBehaviour
{
    private bool selected;
    private Vector3 startingPos;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        selected = false;
        startingPos = transform.position;
    }

    // Update is called once per frame
    void Update()
    {
        if (selected)
        {
            RaycastHit rayHit;
            if (ShipBuildController.CameraToMouseRay(out rayHit))
            {
                transform.position = rayHit.point + Vector3.up * transform.localScale.y;
            }
        }
    }

    public void SetSelected() 
    {
        selected = !selected;
        gameObject.layer = selected ? 2 : 0;
    }

    public Vector3 GetPosition()
    {
        return transform.position;
    }
}
