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
        if (rightClick.WasPressedThisFrame())
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
        else if (rightClick.WasReleasedThisFrame() && moveable != null)
        {
            moveable.SetSelected();
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
            Instantiate(obj, moveable.GetPosition(), Quaternion.identity);
            moveable.SetSelected();
            moveable = null;
        }
    }

    public static bool CameraToMouseRay(out RaycastHit RayHit)
    {
        Ray ray = Instance.mainCamera.ScreenPointToRay(Mouse.current.position.ReadValue());
        return Physics.Raycast(ray, out RayHit);
        //return false;

    }


}
