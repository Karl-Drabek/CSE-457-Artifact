using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

[DisallowMultipleComponent]
public class ObstacleCompassBarUI : MonoBehaviour
{
    sealed class CompassLabelVisual
    {
        public RectTransform rectTransform;
        public TMP_Text text;
        public float headingDegrees;
    }

    sealed class CompassTickVisual
    {
        public RectTransform rectTransform;
        public Image image;
        public float headingDegrees;
        public bool major;
    }

    [Header("References")]
    [SerializeField] VoyageCycleController voyageController;
    [SerializeField] RectTransform trackRect;
    [SerializeField] RectTransform markerRect;
    [SerializeField] Image markerIcon;
    [SerializeField] TMP_Text distanceLabel;
    [SerializeField] TMP_Text directionLabel;

    [Header("Presentation")]
    [SerializeField, Range(5f, 180f)] float halfAngleVisibleRange = 75f;
    [SerializeField] bool showOnlyDuringVoyage = true;
    [SerializeField] bool autoCreateMarkerIcon = true;
    [SerializeField] string distanceSuffix = "m";
    [SerializeField] Color compassLabelColor = new Color(1f, 1f, 1f, 0.92f);
    [SerializeField] Color compassTickColor = new Color(1f, 1f, 1f, 0.55f);
    [SerializeField] Color markerBackgroundColor = Color.black;
    [SerializeField, Min(8f)] float compassLabelFontSize = 18f;
    [SerializeField, Min(1f)] float majorTickHeight = 18f;
    [SerializeField, Min(1f)] float minorTickHeight = 10f;
    [SerializeField, Min(1f)] float tickWidth = 2f;
    [SerializeField] Vector2 markerSize = new Vector2(28f, 28f);
    [SerializeField, Min(0f)] float markerIconPadding = 3f;
    [SerializeField] Color markerOutlineColor = Color.white;
    [SerializeField, Min(0f)] float markerOutlineWidth = 2f;
    [SerializeField] float markerVerticalOffset;
    [SerializeField] Vector2 obstacleDistanceOffset = new Vector2(42f, 0f);
    [SerializeField] float labelVerticalOffset = 12f;
    [SerializeField] float tickVerticalOffset = -10f;

    static readonly string[] CompassDirections = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
    readonly List<CompassLabelVisual> compassLabels = new List<CompassLabelVisual>();
    readonly List<CompassTickVisual> compassTicks = new List<CompassTickVisual>();

    RectTransform generatedRibbonRoot;
    RectTransform ribbonLabelRoot;
    RectTransform ribbonTickRoot;
    RectTransform ribbonContainer;
    RectTransform generatedMarkerRect;
    RectTransform generatedMarkerFillRect;
    RectTransform generatedMarkerIconRect;
    Image generatedMarkerBackground;
    Image generatedMarkerOutlineImage;
    static Sprite fallbackMarkerSprite;
    static Texture2D fallbackMarkerTexture;
    static Sprite circularMarkerSprite;
    static Texture2D circularMarkerTexture;

    void Awake()
    {
        ResolveVoyageController();
        RefreshCompass();
    }

    void OnEnable()
    {
        ResolveVoyageController();
        RefreshCompass();
    }

    void Update()
    {
        RefreshCompass();
    }

    void ResolveVoyageController()
    {
        if (voyageController == null)
        {
            voyageController = FindFirstObjectByType<VoyageCycleController>();
        }
    }

    void RefreshCompass()
    {
        ResolveVoyageController();
        EnsureMarkerVisuals();
        bool hasTrackRibbon = trackRect != null;

        bool shouldShow = voyageController != null
            && (!showOnlyDuringVoyage || voyageController.VoyageInProgress);
        if (!shouldShow)
        {
            SetMarkerVisible(false);
            SetRibbonVisible(false);
            UpdateDistanceLabel(null, null);
            UpdateDirectionLabel(null, hasTrackRibbon);
            return;
        }

        bool hasBoatHeading = voyageController.TryGetBoatPlanarForward(out Vector3 boatPlanarForward);
        if (hasBoatHeading)
        {
            UpdateDirectionLabel(GetCompassDirectionLabel(boatPlanarForward), hasTrackRibbon);
            UpdateCompassRibbon(boatPlanarForward);
        }
        else
        {
            SetRibbonVisible(false);
            UpdateDirectionLabel(null, hasTrackRibbon);
        }

        if (!voyageController.TryGetNearestObstacleTrackingInfo(out World.ObstacleTrackingInfo trackingInfo))
        {
            SetMarkerVisible(false);
            UpdateDistanceLabel(null, null);
            return;
        }

        RectTransform activeMarkerRect = ResolveMarkerRect();
        if (trackRect == null || activeMarkerRect == null)
        {
            SetMarkerVisible(false);
            UpdateDistanceLabel(trackingInfo.planarDistance, null);
            return;
        }

        SetMarkerVisible(true);
        UpdateMarkerPosition(trackingInfo, activeMarkerRect);
        UpdateMarkerVisuals(trackingInfo);
        UpdateDistanceLabel(trackingInfo.planarDistance, activeMarkerRect);
    }

    void UpdateCompassRibbon(Vector3 boatPlanarForward)
    {
        if (trackRect == null)
        {
            SetRibbonVisible(false);
            return;
        }

        EnsureCompassRibbonVisuals();
        SyncRibbonRootSizes();
        ApplyRibbonScaleCompensation();
        SetRibbonVisible(true);

        float boatHeadingDegrees = GetClockwiseDegreesFromNorth(boatPlanarForward);

        for (int i = 0; i < compassLabels.Count; i++)
        {
            CompassLabelVisual labelVisual = compassLabels[i];
            float signedAngle = Mathf.DeltaAngle(boatHeadingDegrees, labelVisual.headingDegrees);
            bool shouldBeVisible = Mathf.Abs(signedAngle) <= halfAngleVisibleRange;
            if (labelVisual.rectTransform.gameObject.activeSelf != shouldBeVisible)
            {
                labelVisual.rectTransform.gameObject.SetActive(shouldBeVisible);
            }

            if (!shouldBeVisible)
            {
                continue;
            }

            Vector2 anchoredPosition = labelVisual.rectTransform.anchoredPosition;
            anchoredPosition.x = GetTrackPositionForSignedAngle(signedAngle);
            anchoredPosition.y = labelVerticalOffset;
            labelVisual.rectTransform.anchoredPosition = anchoredPosition;
        }

        for (int i = 0; i < compassTicks.Count; i++)
        {
            CompassTickVisual tickVisual = compassTicks[i];
            float signedAngle = Mathf.DeltaAngle(boatHeadingDegrees, tickVisual.headingDegrees);
            bool shouldBeVisible = Mathf.Abs(signedAngle) <= halfAngleVisibleRange;
            if (tickVisual.rectTransform.gameObject.activeSelf != shouldBeVisible)
            {
                tickVisual.rectTransform.gameObject.SetActive(shouldBeVisible);
            }

            if (!shouldBeVisible)
            {
                continue;
            }

            Vector2 anchoredPosition = tickVisual.rectTransform.anchoredPosition;
            anchoredPosition.x = GetTrackPositionForSignedAngle(signedAngle);
            anchoredPosition.y = tickVerticalOffset;
            tickVisual.rectTransform.anchoredPosition = anchoredPosition;
            tickVisual.rectTransform.sizeDelta = new Vector2(tickWidth, tickVisual.major ? majorTickHeight : minorTickHeight);
            tickVisual.image.color = compassTickColor;
        }
    }

    void EnsureCompassRibbonVisuals()
    {
        RectTransform targetContainer = ResolveRibbonContainer();
        if (generatedRibbonRoot != null && generatedRibbonRoot.parent == targetContainer)
        {
            return;
        }

        if (generatedRibbonRoot != null)
        {
            if (Application.isPlaying)
            {
                Destroy(generatedRibbonRoot.gameObject);
            }
            else
            {
                DestroyImmediate(generatedRibbonRoot.gameObject);
            }
        }

        ClearGeneratedRibbonReferences();

        ribbonContainer = targetContainer;
        if (ribbonContainer == null)
        {
            return;
        }

        generatedRibbonRoot = CreateRibbonChild("CompassRibbon", ribbonContainer);
        generatedRibbonRoot.gameObject.AddComponent<RectMask2D>();
        MarkIgnoreLayout(generatedRibbonRoot);
        ribbonTickRoot = CreateRibbonChild("Ticks", generatedRibbonRoot);
        MarkIgnoreLayout(ribbonTickRoot);
        ribbonLabelRoot = CreateRibbonChild("Labels", generatedRibbonRoot);
        MarkIgnoreLayout(ribbonLabelRoot);
        generatedRibbonRoot.SetAsLastSibling();

        TMP_FontAsset fontAsset = ResolveCompassFontAsset();
        Color labelColor = directionLabel != null ? directionLabel.color : compassLabelColor;
        float fontSize = directionLabel != null && directionLabel.fontSize > 0f
            ? directionLabel.fontSize
            : compassLabelFontSize;

        for (int i = 0; i < CompassDirections.Length; i++)
        {
            CompassLabelVisual labelVisual = new CompassLabelVisual
            {
                headingDegrees = i * 45f
            };

            RectTransform labelRect = CreateRibbonChild("Dir_" + CompassDirections[i], ribbonLabelRoot);
            TMP_Text labelText = labelRect.gameObject.AddComponent<TextMeshProUGUI>();
            labelText.text = CompassDirections[i];
            labelText.alignment = TextAlignmentOptions.Center;
            labelText.color = labelColor;
            labelText.fontSize = fontSize;
            labelText.textWrappingMode = TextWrappingModes.NoWrap;
            labelText.overflowMode = TextOverflowModes.Overflow;
            labelText.raycastTarget = false;
            if (fontAsset != null)
            {
                labelText.font = fontAsset;
            }

            labelRect.sizeDelta = new Vector2(44f, Mathf.Max(fontSize + 8f, 24f));
            MarkIgnoreLayout(labelRect);
            labelVisual.rectTransform = labelRect;
            labelVisual.text = labelText;
            compassLabels.Add(labelVisual);
        }

        for (int i = 0; i < 16; i++)
        {
            CompassTickVisual tickVisual = new CompassTickVisual
            {
                headingDegrees = i * 22.5f,
                major = i % 2 == 0
            };

            RectTransform tickRect = CreateRibbonChild(tickVisual.major ? "MajorTick_" + i : "MinorTick_" + i, ribbonTickRoot);
            Image tickImage = tickRect.gameObject.AddComponent<Image>();
            tickImage.color = compassTickColor;
            tickImage.raycastTarget = false;

            tickRect.sizeDelta = new Vector2(tickWidth, tickVisual.major ? majorTickHeight : minorTickHeight);
            MarkIgnoreLayout(tickRect);
            tickVisual.rectTransform = tickRect;
            tickVisual.image = tickImage;
            compassTicks.Add(tickVisual);
        }
    }

    RectTransform ResolveRibbonContainer()
    {
        return trackRect;
    }

    void EnsureMarkerVisuals()
    {
        if (trackRect == null)
        {
            return;
        }

        if (markerRect == null && markerIcon != null)
        {
            markerRect = markerIcon.rectTransform;
        }

        if (markerIcon == null && markerRect != null)
        {
            markerIcon = markerRect.GetComponent<Image>();
        }

        if (markerRect != null || !autoCreateMarkerIcon)
        {
            SyncGeneratedMarkerPresentation();
            return;
        }

        // Outer container — no Image, no Mask
        generatedMarkerRect = CreateRibbonChild("ObstacleMarker", trackRect);
        generatedMarkerRect.sizeDelta = markerSize;
        generatedMarkerRect.anchoredPosition = new Vector2(0f, markerVerticalOffset);
        MarkIgnoreLayout(generatedMarkerRect);

        // Outline circle — fills the container, drawn behind the fill
        RectTransform outlineRect = CreateRibbonChild("OutlineCircle", generatedMarkerRect);
        outlineRect.anchorMin = Vector2.zero;
        outlineRect.anchorMax = Vector2.one;
        outlineRect.pivot = new Vector2(0.5f, 0.5f);
        outlineRect.anchoredPosition = Vector2.zero;
        outlineRect.sizeDelta = Vector2.zero;
        generatedMarkerOutlineImage = outlineRect.gameObject.AddComponent<Image>();
        generatedMarkerOutlineImage.sprite = GetCircularMarkerSprite();
        generatedMarkerOutlineImage.color = markerOutlineColor;
        generatedMarkerOutlineImage.raycastTarget = false;
        generatedMarkerOutlineImage.preserveAspect = false;

        // Fill circle — inset from outline, clips the icon via Mask
        generatedMarkerFillRect = CreateRibbonChild("FillCircle", generatedMarkerRect);
        generatedMarkerFillRect.anchorMin = Vector2.zero;
        generatedMarkerFillRect.anchorMax = Vector2.one;
        generatedMarkerFillRect.pivot = new Vector2(0.5f, 0.5f);
        generatedMarkerFillRect.anchoredPosition = Vector2.zero;
        generatedMarkerFillRect.sizeDelta = new Vector2(-markerOutlineWidth * 2f, -markerOutlineWidth * 2f);
        generatedMarkerBackground = generatedMarkerFillRect.gameObject.AddComponent<Image>();
        generatedMarkerBackground.sprite = GetCircularMarkerSprite();
        generatedMarkerBackground.color = markerBackgroundColor;
        generatedMarkerBackground.raycastTarget = false;
        generatedMarkerBackground.preserveAspect = false;
        Mask markerMask = generatedMarkerFillRect.gameObject.AddComponent<Mask>();
        markerMask.showMaskGraphic = true;

        // Icon — child of fill circle, clipped to the circular mask
        generatedMarkerIconRect = CreateRibbonChild("Icon", generatedMarkerFillRect);
        generatedMarkerIconRect.anchorMin = Vector2.zero;
        generatedMarkerIconRect.anchorMax = Vector2.one;
        generatedMarkerIconRect.pivot = new Vector2(0.5f, 0.5f);
        generatedMarkerIconRect.anchoredPosition = Vector2.zero;
        MarkIgnoreLayout(generatedMarkerIconRect);

        markerRect = generatedMarkerRect;
        markerIcon = generatedMarkerIconRect.gameObject.AddComponent<Image>();
        markerIcon.raycastTarget = false;
        markerIcon.preserveAspect = true;
        markerIcon.color = Color.white;
        generatedMarkerRect.SetAsLastSibling();
        SyncGeneratedMarkerPresentation();
    }

    void SyncGeneratedMarkerPresentation()
    {
        if (generatedMarkerRect != null)
        {
            generatedMarkerRect.sizeDelta = markerSize;
        }

        if (generatedMarkerOutlineImage != null)
        {
            generatedMarkerOutlineImage.sprite = GetCircularMarkerSprite();
            generatedMarkerOutlineImage.color = markerOutlineColor;
        }

        if (generatedMarkerFillRect != null)
        {
            generatedMarkerFillRect.sizeDelta = new Vector2(-markerOutlineWidth * 2f, -markerOutlineWidth * 2f);
        }

        if (generatedMarkerBackground != null)
        {
            generatedMarkerBackground.sprite = GetCircularMarkerSprite();
            generatedMarkerBackground.color = markerBackgroundColor;
        }

        if (generatedMarkerIconRect != null)
        {
            generatedMarkerIconRect.offsetMin = new Vector2(markerIconPadding, markerIconPadding);
            generatedMarkerIconRect.offsetMax = new Vector2(-markerIconPadding, -markerIconPadding);
        }
    }

    RectTransform CreateRibbonChild(string objectName, Transform parent)
    {
        GameObject childObject = new GameObject(objectName, typeof(RectTransform));
        RectTransform childRect = childObject.GetComponent<RectTransform>();
        childRect.SetParent(parent, false);
        childRect.anchorMin = new Vector2(0.5f, 0.5f);
        childRect.anchorMax = new Vector2(0.5f, 0.5f);
        childRect.pivot = new Vector2(0.5f, 0.5f);
        childRect.anchoredPosition = Vector2.zero;
        childRect.localScale = Vector3.one;
        childRect.localRotation = Quaternion.identity;
        return childRect;
    }

    TMP_FontAsset ResolveCompassFontAsset()
    {
        if (directionLabel != null && directionLabel.font != null)
        {
            return directionLabel.font;
        }

        return TMP_Settings.defaultFontAsset;
    }

    void SyncRibbonRootSizes()
    {
        if (trackRect == null || generatedRibbonRoot == null)
        {
            return;
        }

        Vector2 trackSize = trackRect.rect.size;
        generatedRibbonRoot.anchorMin = Vector2.zero;
        generatedRibbonRoot.anchorMax = Vector2.one;
        generatedRibbonRoot.pivot = new Vector2(0.5f, 0.5f);
        generatedRibbonRoot.anchoredPosition = Vector2.zero;
        generatedRibbonRoot.localRotation = Quaternion.identity;
        generatedRibbonRoot.localScale = Vector3.one;
        generatedRibbonRoot.sizeDelta = Vector2.zero;

        if (ribbonLabelRoot != null)
        {
            ribbonLabelRoot.sizeDelta = trackSize;
            ribbonLabelRoot.anchoredPosition = Vector2.zero;
            ribbonLabelRoot.localScale = Vector3.one;
        }

        if (ribbonTickRoot != null)
        {
            ribbonTickRoot.sizeDelta = trackSize;
            ribbonTickRoot.anchoredPosition = Vector2.zero;
            ribbonTickRoot.localScale = Vector3.one;
        }
    }

    void ApplyRibbonScaleCompensation()
    {
        if (generatedRibbonRoot == null)
        {
            return;
        }

        Vector3 lossyScale = generatedRibbonRoot.lossyScale;
        float safeScaleX = Mathf.Abs(lossyScale.x);
        float safeScaleY = Mathf.Abs(lossyScale.y);
        float horizontalCompensation = safeScaleX > 0.0001f
            ? safeScaleY / safeScaleX
            : 1f;

        Vector3 compensatedScale = new Vector3(horizontalCompensation, 1f, 1f);
        for (int i = 0; i < compassLabels.Count; i++)
        {
            compassLabels[i].rectTransform.localScale = compensatedScale;
        }

        for (int i = 0; i < compassTicks.Count; i++)
        {
            compassTicks[i].rectTransform.localScale = compensatedScale;
        }

        RectTransform activeMarkerRect = ResolveMarkerRect();
        if (activeMarkerRect != null && activeMarkerRect.IsChildOf(trackRect))
        {
            activeMarkerRect.localScale = compensatedScale;
        }
    }

    static void MarkIgnoreLayout(RectTransform rectTransform)
    {
        if (rectTransform == null)
        {
            return;
        }

        LayoutElement layoutElement = rectTransform.GetComponent<LayoutElement>();
        if (layoutElement == null)
        {
            layoutElement = rectTransform.gameObject.AddComponent<LayoutElement>();
        }

        layoutElement.ignoreLayout = true;
    }

    void ClearGeneratedRibbonReferences()
    {
        compassLabels.Clear();
        compassTicks.Clear();
        generatedRibbonRoot = null;
        ribbonLabelRoot = null;
        ribbonTickRoot = null;
        ribbonContainer = null;
    }

    void SetRibbonVisible(bool isVisible)
    {
        if (generatedRibbonRoot != null && generatedRibbonRoot.gameObject.activeSelf != isVisible)
        {
            generatedRibbonRoot.gameObject.SetActive(isVisible);
        }
    }

    void SetMarkerVisible(bool isVisible)
    {
        RectTransform activeMarkerRect = ResolveMarkerRect();
        if (activeMarkerRect != null && activeMarkerRect.gameObject.activeSelf != isVisible)
        {
            activeMarkerRect.gameObject.SetActive(isVisible);
        }
    }

    RectTransform ResolveMarkerRect()
    {
        if (markerRect != null)
        {
            return markerRect;
        }

        if (markerIcon != null)
        {
            markerRect = markerIcon.rectTransform;
            return markerRect;
        }

        return null;
    }

    void UpdateMarkerPosition(World.ObstacleTrackingInfo trackingInfo, RectTransform activeMarkerRect)
    {
        float markerX = GetTrackPositionForSignedAngle(trackingInfo.signedAngleFromReference);
        Vector2 anchoredPosition = activeMarkerRect.anchoredPosition;

        if (activeMarkerRect.parent == trackRect)
        {
            anchoredPosition.x = markerX;
            anchoredPosition.y = markerVerticalOffset;
        }
        else
        {
            anchoredPosition.x = trackRect.anchoredPosition.x + markerX;
            anchoredPosition.y = trackRect.anchoredPosition.y + markerVerticalOffset;
        }

        activeMarkerRect.anchoredPosition = anchoredPosition;
    }

    float GetTrackPositionForSignedAngle(float signedAngle)
    {
        float clampedAngle = Mathf.Clamp(
            signedAngle,
            -Mathf.Max(halfAngleVisibleRange, 0.01f),
            Mathf.Max(halfAngleVisibleRange, 0.01f));
        float normalized = Mathf.InverseLerp(-halfAngleVisibleRange, halfAngleVisibleRange, clampedAngle);
        return Mathf.Lerp(-(trackRect.rect.width * 0.5f), trackRect.rect.width * 0.5f, normalized);
    }

    void UpdateMarkerVisuals(World.ObstacleTrackingInfo trackingInfo)
    {
        Image activeMarkerIcon = markerIcon;
        if (activeMarkerIcon != null)
        {
            activeMarkerIcon.sprite = trackingInfo.compassIcon != null
                ? trackingInfo.compassIcon
                : GetFallbackMarkerSprite();
            activeMarkerIcon.preserveAspect = true;
            activeMarkerIcon.rectTransform.localRotation = Quaternion.identity;
            return;
        }

        RectTransform activeMarkerRect = ResolveMarkerRect();
        if (activeMarkerRect != null)
        {
            activeMarkerRect.localRotation = Quaternion.identity;
        }
    }

    void UpdateDistanceLabel(float? distance, RectTransform markerAnchor)
    {
        if (distanceLabel == null)
        {
            return;
        }

        bool shouldShow = distance.HasValue;
        if (distanceLabel.gameObject.activeSelf != shouldShow)
        {
            distanceLabel.gameObject.SetActive(shouldShow);
        }

        if (!shouldShow)
        {
            distanceLabel.text = string.Empty;
            return;
        }

        distanceLabel.text = Mathf.RoundToInt(distance.Value) + distanceSuffix;
        if (markerAnchor != null)
        {
            distanceLabel.rectTransform.position = markerAnchor.position + (Vector3)obstacleDistanceOffset;
        }
    }

    void UpdateDirectionLabel(string directionText, bool hasTrackRibbon)
    {
        if (directionLabel == null)
        {
            return;
        }

        bool shouldUseStandaloneLabel = !hasTrackRibbon;
        if (directionLabel.gameObject.activeSelf != shouldUseStandaloneLabel)
        {
            directionLabel.gameObject.SetActive(shouldUseStandaloneLabel);
        }

        if (!shouldUseStandaloneLabel)
        {
            return;
        }

        directionLabel.text = directionText ?? string.Empty;
    }

    static string GetCompassDirectionLabel(Vector3 planarDirection)
    {
        Vector3 flattenedDirection = Vector3.ProjectOnPlane(planarDirection, Vector3.up);
        if (flattenedDirection.sqrMagnitude <= 0.0001f)
        {
            return string.Empty;
        }

        int sectorIndex = Mathf.RoundToInt(GetClockwiseDegreesFromNorth(flattenedDirection) / 45f) % CompassDirections.Length;
        return CompassDirections[sectorIndex];
    }

    static float GetClockwiseDegreesFromNorth(Vector3 planarDirection)
    {
        Vector3 flattenedDirection = Vector3.ProjectOnPlane(planarDirection, Vector3.up);
        if (flattenedDirection.sqrMagnitude <= 0.0001f)
        {
            return 0f;
        }

        float clockwiseDegreesFromNorth = Mathf.Atan2(flattenedDirection.x, flattenedDirection.z) * Mathf.Rad2Deg;
        return Mathf.Repeat(clockwiseDegreesFromNorth, 360f);
    }

    static Sprite GetFallbackMarkerSprite()
    {
        if (fallbackMarkerSprite != null)
        {
            return fallbackMarkerSprite;
        }

        if (fallbackMarkerTexture == null)
        {
            fallbackMarkerTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false)
            {
                name = "ObstacleCompassFallbackMarker",
                hideFlags = HideFlags.HideAndDontSave
            };
            fallbackMarkerTexture.SetPixel(0, 0, Color.white);
            fallbackMarkerTexture.Apply(false, true);
        }

        fallbackMarkerSprite = Sprite.Create(
            fallbackMarkerTexture,
            new Rect(0f, 0f, fallbackMarkerTexture.width, fallbackMarkerTexture.height),
            new Vector2(0.5f, 0.5f),
            1f);
        fallbackMarkerSprite.name = "ObstacleCompassFallbackMarker";
        fallbackMarkerSprite.hideFlags = HideFlags.HideAndDontSave;
        return fallbackMarkerSprite;
    }

    static Sprite GetCircularMarkerSprite()
    {
        if (circularMarkerSprite != null)
        {
            return circularMarkerSprite;
        }

        const int textureSize = 64;
        if (circularMarkerTexture == null)
        {
            circularMarkerTexture = new Texture2D(textureSize, textureSize, TextureFormat.RGBA32, false)
            {
                name = "ObstacleCompassCircleMask",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            Vector2 center = new Vector2((textureSize - 1) * 0.5f, (textureSize - 1) * 0.5f);
            float radius = textureSize * 0.5f;
            for (int y = 0; y < textureSize; y++)
            {
                for (int x = 0; x < textureSize; x++)
                {
                    float distance = Vector2.Distance(new Vector2(x, y), center);
                    float alpha = Mathf.Clamp01(radius - distance);
                    circularMarkerTexture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }

            circularMarkerTexture.Apply(false, true);
        }

        circularMarkerSprite = Sprite.Create(
            circularMarkerTexture,
            new Rect(0f, 0f, circularMarkerTexture.width, circularMarkerTexture.height),
            new Vector2(0.5f, 0.5f),
            circularMarkerTexture.width);
        circularMarkerSprite.name = "ObstacleCompassCircleMask";
        circularMarkerSprite.hideFlags = HideFlags.HideAndDontSave;
        return circularMarkerSprite;
    }
}
