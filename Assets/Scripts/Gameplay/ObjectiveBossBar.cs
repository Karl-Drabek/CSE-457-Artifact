using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Shows a boss-bar at the top of the screen for the nearest active objective.
/// Reads health from ObstacleHealth (for regular objectives) or World's border wall health (for the ice wall).
/// Attach to any GameObject in the sailing scene; it builds its own UI on Start.
/// </summary>
public class ObjectiveBossBar : MonoBehaviour
{
    [SerializeField] VoyageCycleController voyageController;
    [SerializeField, Min(0.02f)] float refreshRate = 0.05f;

    float nextRefresh;
    GameObject barRoot;
    RectTransform fillRect;
    TextMeshProUGUI nameLabel;
    TextMeshProUGUI healthLabel;

    void Start()
    {
        if (voyageController == null)
            voyageController = FindFirstObjectByType<VoyageCycleController>();
        BuildUI();
    }

    void Update()
    {
        if (Time.time < nextRefresh) return;
        nextRefresh = Time.time + refreshRate;
        Refresh();
    }

    void Refresh()
    {
        // UI was destroyed (scene change while this component persists) — rebuild it
        if (barRoot == null || !barRoot || fillRect == null || !fillRect)
        {
            barRoot = null;
            fillRect = null;
            nameLabel = null;
            healthLabel = null;
            BuildUI();
            if (barRoot == null) return;
        }

        if (voyageController == null || !voyageController.VoyageInProgress)
        {
            SetVisible(false);
            return;
        }

        if (!voyageController.TryGetNearestObstacleTrackingInfo(out World.ObstacleTrackingInfo info))
        {
            SetVisible(false);
            return;
        }

        float frac;
        string healthText;

        if (info.isBorderWallTarget && World.Instance != null)
        {
            frac = World.Instance.GetBorderWallHealthFraction();
            float maxH = World.Instance.GetBorderWallMaxHealth();
            healthText = Mathf.CeilToInt(frac * maxH) + " / " + Mathf.CeilToInt(maxH);
        }
        else if (info.obstacle != null)
        {
            ObstacleHealth oh = info.obstacle.GetComponent<ObstacleHealth>();
            if (oh == null) { SetVisible(false); return; }
            frac = oh.HealthFraction;
            healthText = Mathf.CeilToInt(oh.CurrentHealth) + " / " + Mathf.CeilToInt(oh.MaxHealth);
        }
        else
        {
            SetVisible(false);
            return;
        }

        SetVisible(true);
        fillRect.anchorMax = new Vector2(frac, 1f);
        if (nameLabel != null) nameLabel.text = info.obstacleName;
        if (healthLabel != null) healthLabel.text = healthText;
    }

    void SetVisible(bool visible)
    {
        if (barRoot != null && barRoot && barRoot.activeSelf != visible)
            barRoot.SetActive(visible);
    }

    void BuildUI()
    {
        Canvas canvas = FindFirstObjectByType<Canvas>();
        if (canvas == null) return;

        barRoot = new GameObject("ObjectiveBossBar", typeof(RectTransform));
        barRoot.transform.SetParent(canvas.transform, false);

        RectTransform rootRT = barRoot.GetComponent<RectTransform>();
        rootRT.anchorMin = new Vector2(0.25f, 1f);
        rootRT.anchorMax = new Vector2(0.75f, 1f);
        rootRT.pivot = new Vector2(0.5f, 1f);
        rootRT.sizeDelta = new Vector2(0f, 58f);
        rootRT.anchoredPosition = new Vector2(0f, -10f);

        barRoot.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.65f);

        // Objective name
        var nameLabelGo = new GameObject("Name", typeof(RectTransform));
        nameLabelGo.transform.SetParent(barRoot.transform, false);
        nameLabel = nameLabelGo.AddComponent<TextMeshProUGUI>();
        nameLabel.alignment = TextAlignmentOptions.Center;
        nameLabel.fontSize = 15f;
        nameLabel.color = Color.white;
        nameLabel.textWrappingMode = TextWrappingModes.NoWrap;
        nameLabel.raycastTarget = false;
        var nameRT = nameLabelGo.GetComponent<RectTransform>();
        nameRT.anchorMin = new Vector2(0f, 0.54f);
        nameRT.anchorMax = new Vector2(1f, 1f);
        nameRT.offsetMin = new Vector2(8f, 2f);
        nameRT.offsetMax = new Vector2(-8f, -2f);

        // Bar background (red = lost health)
        var barBgGo = new GameObject("BarBg", typeof(RectTransform));
        barBgGo.transform.SetParent(barRoot.transform, false);
        barBgGo.AddComponent<Image>().color = new Color(0.7f, 0.1f, 0.1f, 1f);
        var barBgRT = barBgGo.GetComponent<RectTransform>();
        barBgRT.anchorMin = new Vector2(0f, 0f);
        barBgRT.anchorMax = new Vector2(1f, 0.52f);
        barBgRT.offsetMin = new Vector2(8f, 6f);
        barBgRT.offsetMax = new Vector2(-8f, 0f);

        // Green fill (remaining health, anchor-width controlled)
        var fillGo = new GameObject("Fill", typeof(RectTransform));
        fillGo.transform.SetParent(barBgGo.transform, false);
        fillGo.AddComponent<Image>().color = new Color(0.2f, 0.8f, 0.25f);
        fillRect = fillGo.GetComponent<RectTransform>();
        fillRect.anchorMin = Vector2.zero;
        fillRect.anchorMax = Vector2.one;
        fillRect.offsetMin = Vector2.zero;
        fillRect.offsetMax = Vector2.zero;

        // Health text over bar
        var healthLabelGo = new GameObject("HealthText", typeof(RectTransform));
        healthLabelGo.transform.SetParent(barBgGo.transform, false);
        healthLabel = healthLabelGo.AddComponent<TextMeshProUGUI>();
        healthLabel.alignment = TextAlignmentOptions.Center;
        healthLabel.fontSize = 11f;
        healthLabel.color = Color.white;
        healthLabel.textWrappingMode = TextWrappingModes.NoWrap;
        healthLabel.raycastTarget = false;
        var healthRT = healthLabelGo.GetComponent<RectTransform>();
        healthRT.anchorMin = Vector2.zero;
        healthRT.anchorMax = Vector2.one;
        healthRT.offsetMin = Vector2.zero;
        healthRT.offsetMax = Vector2.zero;

        barRoot.SetActive(false);
    }
}
