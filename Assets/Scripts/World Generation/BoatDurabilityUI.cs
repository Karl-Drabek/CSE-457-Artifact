using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;

public class BoatDurabilityUI : MonoBehaviour
{
    [Header("UI")]
    [SerializeField] private TextMeshProUGUI durabilityText;

    [Header("Update Settings")]
    [SerializeField] private float refreshRate = 0.1f;

    private float nextRefreshTime;
    private readonly StringBuilder builder = new StringBuilder();

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

        builder.Clear();
        builder.AppendLine("Boat Durability");

        if (pieces.Length == 0)
        {
            builder.AppendLine("No boat pieces found.");
            durabilityText.text = builder.ToString();
            return;
        }

        foreach (BoatPiece piece in pieces)
        {
            if (piece == null)
            {
                continue;
            }

            string baseName = piece.gameObject.name.Replace("(Clone)", "").Trim();

            if (!nameCounts.ContainsKey(baseName))
            {
                nameCounts[baseName] = 0;
            }

            nameCounts[baseName]++;

            string displayName = baseName + " " + nameCounts[baseName];

            builder.Append(displayName);
            builder.Append(": ");
            builder.Append(Mathf.CeilToInt(piece.currentDurability));
            builder.Append(" / ");
            builder.Append(Mathf.CeilToInt(piece.maxDurability));

            if (piece.isBroken)
            {
                builder.Append(" BROKEN");
            }

            builder.AppendLine();
        }

        durabilityText.text = builder.ToString();
    }
}