using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

/// <summary>
/// Older builder-test controller kept for BuilderTest.unity and MoveableObject.
/// The main ship-building scene uses ShipBuilder instead.
/// </summary>
public class ShipBuildController : MonoBehaviour
{
    const int SailSceneBuildIndex = 0;

    [SerializeField] GameObject obj;
    [SerializeField] GameObject shipParent;

    Camera mainCamera;
    InputAction leftClick;
    InputAction rightClick;
    MoveableObject moveable;

    public static ShipBuildController Instance { get; private set; }

    void Awake()
    {
        mainCamera = Camera.main;
        Instance = this;
    }

    void Start()
    {
        leftClick = InputSystem.actions.FindAction("Click");
        rightClick = InputSystem.actions.FindAction("RightClick");
    }

    void Update()
    {
        if (mainCamera == null || obj == null || leftClick == null || rightClick == null)
        {
            return;
        }

        if (rightClick.WasPressedThisFrame() && moveable == null)
        {
            TrySelectMoveable();
            return;
        }

        if (rightClick.WasPressedThisFrame() && moveable != null)
        {
            Destroy(moveable.gameObject);
            moveable = null;
            return;
        }

        if (rightClick.WasReleasedThisFrame() && moveable != null)
        {
            moveable.SetDeselected();
            moveable = null;
            return;
        }

        if (leftClick.WasPressedThisFrame() && moveable == null)
        {
            TryCreateMoveable();
            return;
        }

        if (leftClick.WasPressedThisFrame() && moveable != null)
        {
            Instantiate(obj, moveable.GetPosition(), moveable.transform.rotation, shipParent.transform);
        }
    }

    public static bool CameraToMouseRay(out RaycastHit rayHit)
    {
        rayHit = default;
        if (Instance == null || Instance.mainCamera == null || Mouse.current == null)
        {
            return false;
        }

        Ray ray = Instance.mainCamera.ScreenPointToRay(Mouse.current.position.ReadValue());
        return Physics.Raycast(ray, out rayHit);
    }

    /// <summary>
    /// Existing button hook used by BuilderTest. Saves the current boat and
    /// moves it into the sail scene.
    /// </summary>
    public void SwitchToSailScene()
    {
        StartCoroutine(LoadAndActivateSailScene());
    }

    IEnumerator LoadAndActivateSailScene()
    {
        AsyncOperation operation = SceneManager.LoadSceneAsync(SailSceneBuildIndex, LoadSceneMode.Additive);
        yield return operation;

        Scene sailScene = SceneManager.GetSceneByBuildIndex(SailSceneBuildIndex);
        if (!sailScene.isLoaded)
        {
            yield break;
        }

        MoveBoatToScene();

        if (mainCamera != null)
        {
            mainCamera.gameObject.SetActive(false);
        }

        if (moveable != null)
        {
            moveable.Delete();
            moveable = null;
        }

        SceneManager.SetActiveScene(sailScene);

        BoatFollowCamera followCamera = FindAnyObjectByType<BoatFollowCamera>();
        if (followCamera != null)
        {
            followCamera.target = shipParent.transform;
        }

        yield return SceneManager.UnloadSceneAsync(1);
    }

    public void MoveBoatToScene()
    {
        Scene sailScene = SceneManager.GetSceneByBuildIndex(SailSceneBuildIndex);
        if (shipParent.transform.parent != null)
        {
            shipParent.transform.SetParent(null);
        }

        AddBuoyancyToShip();
        AddShipMovement();
        SetShipPhysics();

        SceneManager.MoveGameObjectToScene(shipParent, sailScene);
    }

    void TrySelectMoveable()
    {
        if (!CameraToMouseRay(out RaycastHit rayHit))
        {
            return;
        }

        if (!rayHit.transform.CompareTag("Moveable"))
        {
            return;
        }

        moveable = rayHit.transform.GetComponent<MoveableObject>();
        if (moveable != null)
        {
            moveable.SetSelected();
        }
    }

    void TryCreateMoveable()
    {
        if (!CameraToMouseRay(out RaycastHit rayHit))
        {
            return;
        }

        GameObject target = rayHit.transform.gameObject;
        if (target == null)
        {
            return;
        }

        Vector3 spawnPosition = rayHit.point + (Vector3.up * target.transform.localScale.y);
        GameObject newObject = Instantiate(obj, spawnPosition, Quaternion.identity, shipParent.transform);
        moveable = newObject.GetComponent<MoveableObject>();
        if (moveable != null)
        {
            moveable.SetSelected();
        }
    }

    void SetShipPhysics()
    {
        Rigidbody rigidbody = shipParent.GetComponent<Rigidbody>();
        if (rigidbody == null)
        {
            rigidbody = shipParent.AddComponent<Rigidbody>();
        }

        rigidbody.centerOfMass = new Vector3(0f, -5f, -0.3f);
    }

    void AddBuoyancyToShip()
    {
        WaterBuoyancy buoyancy = shipParent.GetComponent<WaterBuoyancy>();
        if (buoyancy == null)
        {
            buoyancy = shipParent.AddComponent<WaterBuoyancy>();
        }

        buoyancy.waterAngularDrag = 5f;
        buoyancy.objectDensity = 0.25f;
        buoyancy.zEdgeOffset = -0.3f;
        buoyancy.hull_height = -2.2f;
    }

    void AddShipMovement()
    {
        if (shipParent.GetComponent<ShipController>() == null)
        {
            shipParent.AddComponent<ShipController>();
        }
    }

    // Utility methods used by MoveableObject for preview transparency.
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
