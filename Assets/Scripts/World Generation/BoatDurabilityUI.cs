using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class BoatDurabilityUI : MonoBehaviour
{
    [Header("UI")]
    [Tooltip("Parent RectTransform that will hold one row per boat piece.")]
    [SerializeField] private RectTransform barContainer;
    [SerializeField] private float barHeight = 20f;
    [SerializeField] private float labelWidth = 140f;
    [SerializeField] private float fontSize = 17f;

    [Header("Update Settings")]
    [SerializeField] private float refreshRate = 0.1f;

    private float nextRefreshTime;

    private class HealthBarRow
    {
        public GameObject root;
        public TextMeshProUGUI label;
        public Image fill;
    }

    private readonly Dictionary<BoatPiece, HealthBarRow> _rows = new();

    void Awake()
    {
        if (barContainer == null) return;

        if (!barContainer.TryGetComponent(out VerticalLayoutGroup vlg))
            vlg = barContainer.gameObject.AddComponent<VerticalLayoutGroup>();
        vlg.spacing = 4f;
        vlg.childAlignment = TextAnchor.UpperLeft;
        vlg.childForceExpandWidth = true;
        vlg.childForceExpandHeight = false;
        vlg.childControlWidth = true;
        vlg.childControlHeight = true;
        vlg.padding = new RectOffset(6, 6, 6, 6);

        if (!barContainer.TryGetComponent(out ContentSizeFitter csf))
            csf = barContainer.gameObject.AddComponent<ContentSizeFitter>();
        csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
    }

    void Update()
    {
        if (Time.time < nextRefreshTime) return;
        nextRefreshTime = Time.time + refreshRate;
        RefreshDurabilityBars();
    }

    void RefreshDurabilityBars()
    {
        // barContainer or its RectTransform may be destroyed when the scene changes
        if (barContainer == null || !barContainer) return;

        BoatPiece[] pieces = FindObjectsByType<BoatPiece>(FindObjectsSortMode.None);
        var currentSet = new HashSet<BoatPiece>(pieces);

        // Remove rows for pieces that no longer exist or whose UI was destroyed
        var toRemove = new List<BoatPiece>();
        foreach (var kvp in _rows)
        {
            if (kvp.Key == null || !currentSet.Contains(kvp.Key) || kvp.Value.root == null || !kvp.Value.root)
            {
                if (kvp.Value.root != null && kvp.Value.root) Destroy(kvp.Value.root);
                toRemove.Add(kvp.Key);
            }
        }
        foreach (var p in toRemove) _rows.Remove(p);

        var nameCounts = new Dictionary<string, int>();

        foreach (BoatPiece piece in pieces)
        {
            if (piece == null) continue;

            string baseName = piece.gameObject.name.Replace("(Clone)", "").Trim();
            if (!nameCounts.ContainsKey(baseName)) nameCounts[baseName] = 0;
            nameCounts[baseName]++;
            string displayName = baseName + " " + nameCounts[baseName];

            if (!_rows.TryGetValue(piece, out HealthBarRow row))
            {
                row = CreateRow();
                _rows[piece] = row;
            }

            // Skip if the UI row was destroyed (e.g. parent canvas destroyed)
            if (row.fill == null || !row.fill || row.label == null || !row.label) continue;

            float health = piece.maxDurability > 0f
                ? Mathf.Clamp01(piece.currentDurability / piece.maxDurability)
                : 0f;

            row.label.fontSize = fontSize;

            if (piece.isBroken)
            {
                row.label.text = displayName + "  <color=#FF4444>BROKEN</color>";
                row.fill.rectTransform.anchorMax = new Vector2(0f, 1f);
            }
            else
            {
                row.label.text = displayName;
                row.fill.rectTransform.anchorMax = new Vector2(health, 1f);
            }
        }
    }

    static Sprite _roundedSprite;

    static Sprite GetRoundedSprite()
    {
        if (_roundedSprite != null) return _roundedSprite;

        const int size = 32;
        const int radius = 7;

        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };

        var pixels = new Color32[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)(RoundedRectAlpha(x, y, size, size, radius) * 255));

        tex.SetPixels32(pixels);
        tex.Apply();

        _roundedSprite = Sprite.Create(
            tex,
            new Rect(0, 0, size, size),
            new Vector2(0.5f, 0.5f),
            100f, 0,
            SpriteMeshType.FullRect,
            new Vector4(radius, radius, radius, radius));

        return _roundedSprite;
    }

    static float RoundedRectAlpha(int x, int y, int w, int h, int r)
    {
        bool inL = x < r, inR = x >= w - r, inB = y < r, inT = y >= h - r;
        if ((inL || inR) && (inB || inT))
        {
            float cx = inL ? r : w - r - 1;
            float cy = inB ? r : h - r - 1;
            float dist = Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
            return Mathf.Clamp01(r - dist + 0.5f);
        }
        return 1f;
    }

    HealthBarRow CreateRow()
    {
        // Horizontal row
        var rowGo = new GameObject("HealthBarRow", typeof(RectTransform));
        rowGo.transform.SetParent(barContainer, false);

        var hlg = rowGo.AddComponent<HorizontalLayoutGroup>();
        hlg.spacing = 8f;
        hlg.childAlignment = TextAnchor.MiddleLeft;
        hlg.childForceExpandWidth = false;
        hlg.childForceExpandHeight = true;
        hlg.childControlWidth = true;
        hlg.childControlHeight = true;

        var rowLe = rowGo.AddComponent<LayoutElement>();
        rowLe.minHeight = barHeight;
        rowLe.preferredHeight = barHeight;

        // Label
        var labelGo = new GameObject("Label", typeof(RectTransform));
        labelGo.transform.SetParent(rowGo.transform, false);
        var labelText = labelGo.AddComponent<TextMeshProUGUI>();
        labelText.fontSize = fontSize;
        labelText.alignment = TextAlignmentOptions.MidlineLeft;
        labelText.color = Color.white;
        labelText.textWrappingMode = TextWrappingModes.NoWrap;
        var labelLe = labelGo.AddComponent<LayoutElement>();
        labelLe.minWidth = labelWidth;
        labelLe.preferredWidth = labelWidth;
        labelLe.flexibleWidth = 0f;

        // Bar background — rounded corners generated from code, Mask clips the fill
        var bgGo = new GameObject("BarBackground", typeof(RectTransform));
        bgGo.transform.SetParent(rowGo.transform, false);
        var bgImg = bgGo.AddComponent<Image>();
        bgImg.sprite = GetRoundedSprite();
        bgImg.type = Image.Type.Sliced;
        bgImg.color = new Color(0.75f, 0.1f, 0.1f, 1f);
        var bgMask = bgGo.AddComponent<Mask>();
        bgMask.showMaskGraphic = true;
        var bgLe = bgGo.AddComponent<LayoutElement>();
        bgLe.flexibleWidth = 1f;
        bgLe.minHeight = barHeight;

        // Fill — green rect clipped to the rounded container
        var fillGo = new GameObject("Fill", typeof(RectTransform));
        fillGo.transform.SetParent(bgGo.transform, false);
        var fillImg = fillGo.AddComponent<Image>();
        fillImg.color = new Color(0.2f, 0.8f, 0.25f);
        var fillRT = fillGo.GetComponent<RectTransform>();
        fillRT.anchorMin = Vector2.zero;
        fillRT.anchorMax = Vector2.one;
        fillRT.offsetMin = Vector2.zero;
        fillRT.offsetMax = Vector2.zero;

        return new HealthBarRow { root = rowGo, label = labelText, fill = fillImg };
    }
}
