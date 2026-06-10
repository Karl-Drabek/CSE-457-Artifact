using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

public class BoatDurabilityUI : MonoBehaviour
{
    [Header("UI")]
    [SerializeField] private TextMeshProUGUI durabilityText;

    [Header("Update Settings")]
    [SerializeField] private float refreshRate = 0.1f;

    [Header("Layout")]
    [SerializeField] private int itemsPerColumn = 12;
    [SerializeField] private int columnWidth = 28;

    [Header("Broken Display")]
    [SerializeField] private float brokenDisplaySeconds = 2f;

    [Header("Root Hull Game Over")]
    [SerializeField] private string shipBuildingSceneName = "ShipBuilding";
    [SerializeField] private float returnToBuilderDelay = 2f;
    [SerializeField] private string boatParentObjectName = "BoatParent";

    private float nextRefreshTime;
    private bool isReturningToBuilder = false;

    private readonly StringBuilder builder = new StringBuilder();

    private class PieceUIInfo
    {
        public string displayName;
        public int currentDurability;
        public int maxDurability;
        public bool isBroken;
        public float brokenTime = -1f;
    }

    private readonly Dictionary<int, PieceUIInfo> pieceInfos = new Dictionary<int, PieceUIInfo>();

    void Update()
    {
        if (Time.time < nextRefreshTime)
        {
            return;
        }

        nextRefreshTime = Time.time + refreshRate;
        RefreshDurabilityList();
    }

    void RefreshDurabilityList()
    {
        if (durabilityText == null)
        {
            return;
        }

        BoatPiece[] pieces = FindObjectsByType<BoatPiece>(FindObjectsSortMode.None);
        Dictionary<string, int> nameCounts = new Dictionary<string, int>();
        HashSet<int> seenThisFrame = new HashSet<int>();

        foreach (BoatPiece piece in pieces)
        {
            if (piece == null)
            {
                continue;
            }

            int id = piece.GetInstanceID();
            seenThisFrame.Add(id);

            string baseName = piece.gameObject.name.Replace("(Clone)", "").Trim();

            if (!nameCounts.ContainsKey(baseName))
            {
                nameCounts[baseName] = 0;
            }

            nameCounts[baseName]++;

            string displayName = baseName + " " + nameCounts[baseName];

            if (!pieceInfos.ContainsKey(id))
            {
                pieceInfos[id] = new PieceUIInfo();
            }

            PieceUIInfo info = pieceInfos[id];

            info.displayName = displayName;
            info.currentDurability = Mathf.CeilToInt(piece.currentDurability);
            info.maxDurability = Mathf.CeilToInt(piece.maxDurability);
            info.isBroken = piece.isBroken;

            if (piece.isBroken && info.brokenTime < 0f)
            {
                info.brokenTime = Time.time;
            }

            // NEW IMPORTANT PART:
            // If the root hull breaks, go back to the ship builder scene.
            if (piece.isRootHull && piece.isBroken && !isReturningToBuilder)
            {
                isReturningToBuilder = true;
                Invoke(nameof(ReturnToShipBuilder), returnToBuilderDelay);
            }
        }

        List<int> idsToRemove = new List<int>();

        foreach (KeyValuePair<int, PieceUIInfo> pair in pieceInfos)
        {
            int id = pair.Key;
            PieceUIInfo info = pair.Value;

            bool pieceStillExists = seenThisFrame.Contains(id);

            if (!pieceStillExists && !info.isBroken)
            {
                idsToRemove.Add(id);
                continue;
            }

            if (info.isBroken && Time.time - info.brokenTime >= brokenDisplaySeconds)
            {
                idsToRemove.Add(id);
            }
        }

        foreach (int id in idsToRemove)
        {
            pieceInfos.Remove(id);
        }

        builder.Clear();
        builder.AppendLine("<b>Boat Durability</b>");

        if (pieceInfos.Count == 0)
        {
            builder.AppendLine("No boat pieces found.");
            durabilityText.text = builder.ToString();
            return;
        }

        List<string> lines = new List<string>();

        foreach (PieceUIInfo info in pieceInfos.Values)
        {
            string line;

            if (info.isBroken)
            {
                line =
                    "<color=red>" +
                    info.displayName +
                    ": BROKEN" +
                    "</color>";
            }
            else
            {
                line =
                    "<color=white>" +
                    info.displayName +
                    ": " +
                    info.currentDurability +
                    " / " +
                    info.maxDurability +
                    "</color>";
            }

            lines.Add(line);
        }

        for (int i = 0; i < lines.Count; i += itemsPerColumn)
        {
            for (int row = 0; row < itemsPerColumn; row++)
            {
                int firstIndex = i + row;
                int secondIndex = i + itemsPerColumn + row;
                int thirdIndex = i + itemsPerColumn * 2 + row;

                if (firstIndex < lines.Count)
                {
                    builder.Append(PadRichTextLine(lines[firstIndex], columnWidth));
                }

                if (secondIndex < lines.Count)
                {
                    builder.Append(PadRichTextLine(lines[secondIndex], columnWidth));
                }

                if (thirdIndex < lines.Count)
                {
                    builder.Append(lines[thirdIndex]);
                }

                builder.AppendLine();
            }

            if (i + itemsPerColumn * 3 < lines.Count)
            {
                builder.AppendLine();
            }
        }

        durabilityText.text = builder.ToString();
    }

    private void ReturnToShipBuilder()
    {
        GameObject boatParent = GameObject.Find(boatParentObjectName);

        if (boatParent != null)
        {
            Destroy(boatParent);
        }

        SceneManager.LoadScene(shipBuildingSceneName, LoadSceneMode.Single);
    }

    string PadRichTextLine(string text, int width)
    {
        string plainText = RemoveRichTextTags(text);
        int paddingNeeded = Mathf.Max(1, width - plainText.Length);

        return text + new string(' ', paddingNeeded);
    }

    string RemoveRichTextTags(string text)
    {
        bool insideTag = false;
        StringBuilder plain = new StringBuilder();

        foreach (char c in text)
        {
            if (c == '<')
            {
                insideTag = true;
                continue;
            }

            if (c == '>')
            {
                insideTag = false;
                continue;
            }

            if (!insideTag)
            {
                plain.Append(c);
            }
        }

        return plain.ToString();
    }
}