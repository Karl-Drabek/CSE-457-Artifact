using UnityEngine;

public class MoveableObject : MonoBehaviour
{
    private bool selected;
    private Vector3 startingPos;

    private float opacity;

    [SerializeField]
    [Range(0f, 1f)]
    float selected_opacity = 0.8f;
    const float DEFAULT_OPACITY = 0f;
    Renderer renderer;


    void Awake()
    {
        selected = false;
        startingPos = transform.position;
        opacity = 0f;
        renderer = GetComponent<Renderer>();
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
        SetOpacity(selected_opacity);
    }

    public void SetDeselected()
    {
        selected = false;
        SetOpacity(DEFAULT_OPACITY);
        gameObject.layer = 0;
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

    void SetOpacity(float opacity)
    {
        Material[] materials = renderer.materials;
        for (int j = 0; j < materials.Length; j++)
        {
            Material mat = materials[j];
            if (opacity > 0f)
            {
                ShipBuildController.SetMaterialTransparent(mat);
                
            }
            else
            {
                ShipBuildController.SetMaterialOpaque(mat);
            }
            Color color = mat.color;
            color.a = opacity;
            mat.color = color;
        }
        renderer.materials = materials;
    }
}
