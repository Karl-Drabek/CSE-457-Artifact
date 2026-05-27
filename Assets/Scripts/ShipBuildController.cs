using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.InputSystem;


public class ShipBuildController : MonoBehaviour
{
    [SerializeField]
    private GameObject obj;

    private Camera mainCamera;
    public static ShipBuildController Instance;

    private InputAction leftClick;
    private InputAction rightClick;
    private InputAction middleMouse;


    private MoveableObject moveable = null;


    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        leftClick = InputSystem.actions.FindAction("Click");
        rightClick = InputSystem.actions.FindAction("RightClick");
        middleMouse = InputSystem.actions.FindAction("MiddleClick");
    }

    void Awake()
    {
        mainCamera = Camera.main;
        Instance = this;
    }

    // Update is called once per frame
    void Update()
    {
        if (mainCamera == null || obj == null)
        {
            return;
        }
        if (rightClick.WasPressedThisFrame() && moveable == null)
        {
            RaycastHit RayHit;
            if (CameraToMouseRay(out RayHit))
            {
                GameObject targetHit = RayHit.transform.gameObject;

                if (targetHit.tag != "Moveable") { return; }

                moveable = targetHit.GetComponent<MoveableObject>();
                moveable.SetSelected();
            }
        }
        else if (rightClick.WasPressedThisFrame() && moveable != null) 
        {
            Destroy(moveable.gameObject);
            moveable = null;
        }
        else if (rightClick.WasReleasedThisFrame() && moveable != null)
        {
            moveable.SetDeselected();
            moveable = null;
        }
        else if (leftClick.WasPressedThisFrame() && moveable == null)
        {
            RaycastHit RayHit;
            if (CameraToMouseRay(out RayHit))
            {
                GameObject target = RayHit.transform.gameObject;
                Vector3 pos = RayHit.point;
                GameObject new_object;
                if (target != null)
                {
                    pos = pos + Vector3.up * target.transform.localScale.y;
                    new_object = Instantiate(obj, pos, Quaternion.identity);
                    moveable = new_object.GetComponent<MoveableObject>();
                    moveable.SetSelected();
                }
            }
        }
        else if (leftClick.WasPressedThisFrame() && moveable != null)
        {
            Instantiate(obj, moveable.GetPosition(), moveable.gameObject.transform.rotation);
        }
    }

    public static bool CameraToMouseRay(out RaycastHit RayHit)
    {
        Ray ray = Instance.mainCamera.ScreenPointToRay(Mouse.current.position.ReadValue());
        return Physics.Raycast(ray, out RayHit);
    }


    // For URP Lit shader, other shaders will have different property names
    public static void SetMaterialTransparent(Material mat)
    {
        mat.SetFloat("_Surface", 1f);
        mat.SetFloat("_Blend", 0f);
        mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.SetFloat("_ZWrite", 0f);
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.DisableKeyword("_ALPHATEST_ON");
        mat.DisableKeyword("_ALPHABLEND_ON");
        mat.EnableKeyword("_ALPHAPREMULTIPLY_ON");
        mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
    }

    public static void SetMaterialOpaque(Material mat)
    {
        mat.SetFloat("_Surface", 0f);
        mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
        mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.Zero);
        mat.SetFloat("_ZWrite", 1f);
        mat.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.DisableKeyword("_ALPHATEST_ON");
        mat.DisableKeyword("_ALPHABLEND_ON");
        mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry;
    }


}
