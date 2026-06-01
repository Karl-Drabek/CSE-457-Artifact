using UnityEngine;

public class MoveableObject : MonoBehaviour
{
    private bool selected;


    void Awake()
    {
        selected = false;
    }

    // Update is called once per frame
    void Update()
    {
        if (selected)
        {
            RaycastHit rayHit;
            if (ShipBuildController.CameraToMouseRay(out rayHit))
            {
                Reorient(rayHit);
            }
        }
    }

    public void SetSelected() 
    {
        selected = true;
        gameObject.layer = 2;
    }

    public void SetDeselected()
    {
        selected = false;
        gameObject.layer = 0;
    }

    public void Delete()
    {
        selected = false;
        Destroy(gameObject);
    }

    public Vector3 GetPosition()
    {
        return transform.position;
    }

    public void Reorient(RaycastHit rayHit)
    {
        Vector3 norm = rayHit.normal.normalized;
        gameObject.transform.up = norm;
        transform.rotation = Quaternion.FromToRotation(Vector3.up, norm);
        transform.position = rayHit.point + norm * transform.localScale.y;
    }
}
