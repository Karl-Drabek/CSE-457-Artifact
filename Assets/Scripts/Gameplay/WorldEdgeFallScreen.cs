using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Detects when the boat has fallen off the world edge and shows a pre-built end-of-game screen.
/// Link your own screen root and stat text fields in the inspector; this script only populates them.
/// </summary>
public class WorldEdgeFallScreen : MonoBehaviour
{
    [Header("References")]
    [SerializeField] VoyageCycleController voyageController;
    [Tooltip("The root GameObject of your end screen — will be activated when the trigger fires.")]
    [SerializeField] GameObject endScreenRoot;
    [Tooltip("Optional button to return to the main menu.")]
    [SerializeField] Button returnButton;

    [Header("Stat Text Fields")]
    [Tooltip("Displays number of days taken (e.g. '3').")]
    [SerializeField] TextMeshProUGUI dayText;
    [Tooltip("Displays total gold spent (e.g. '120').")]
    [SerializeField] TextMeshProUGUI goldSpentText;
    [Tooltip("Displays total distance sailed across all voyages in metres (e.g. '4823 m').")]
    [SerializeField] TextMeshProUGUI totalDistanceText;

    [Header("Trigger Settings")]
    [Tooltip("Y depth at which the end screen appears (should be well below the world floor).")]
    [SerializeField] float fallDepthTrigger = -80f;
    [SerializeField, Min(0.1f)] float checkRate = 0.5f;

    float nextCheck;
    bool shown;

    void Awake()
    {
        if (endScreenRoot != null)
            endScreenRoot.SetActive(false);
    }

    void Start()
    {
        if (voyageController == null)
            voyageController = FindFirstObjectByType<VoyageCycleController>();

        if (returnButton != null)
            returnButton.onClick.AddListener(ReturnToMenu);
    }

    void Update()
    {
        if (shown) return;
        if (Time.time < nextCheck) return;
        nextCheck = Time.time + checkRate;
        CheckFallDepth();
    }

    void CheckFallDepth()
    {
        if (World.Instance == null || World.Instance.BoatRoot == null) return;

        Vector3 pos = World.Instance.BoatRoot.position;

        float xzDist = new Vector2(pos.x, pos.z).magnitude;
        if (xzDist < World.Instance.GetPlayableRadius()) return;

        if (pos.y > fallDepthTrigger) return;

        ShowEndScreen();
    }

    void ShowEndScreen()
    {
        if (shown) return;
        shown = true;
        Time.timeScale = 0f;

        // Total distance includes the current voyage's distance so far (not yet tallied by CompleteVoyageDay)
        float currentVoyageDist = voyageController != null ? voyageController.LastVoyageMaxDistance : 0f;
        float totalDist = voyageController != null
            ? voyageController.TotalDistanceSailed + currentVoyageDist
            : 0f;

        int days = voyageController != null ? voyageController.DayNumber : 0;
        int goldSpent = voyageController != null ? voyageController.GoldSpent : 0;

        if (dayText != null)
            dayText.text = days.ToString();

        if (goldSpentText != null)
            goldSpentText.text = goldSpent.ToString();

        if (totalDistanceText != null)
            totalDistanceText.text = Mathf.RoundToInt(totalDist) + " m";

        if (endScreenRoot != null)
            endScreenRoot.SetActive(true);
    }

    void ReturnToMenu()
    {
        Time.timeScale = 1f;
        if (voyageController != null)
            voyageController.ReturnHome();
        else
            SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
    }
}
