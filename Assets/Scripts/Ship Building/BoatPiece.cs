using System.Collections.Generic;
using UnityEngine;

public class BoatPiece : MonoBehaviour
{
    public BoatPiece parentPiece;
    public List<BoatPiece> childPieces = new List<BoatPiece>();

    public bool isRootHull = false;
    public bool isBroken = false;

    public void AttachTo(BoatPiece newParent)
    {
        if (newParent == null)
        {
            return;
        }

        if (parentPiece != null)
        {
            parentPiece.childPieces.Remove(this);
        }

        parentPiece = newParent;

        if (!newParent.childPieces.Contains(this))
        {
            newParent.childPieces.Add(this);
        }

        transform.SetParent(newParent.transform, true);
    }

    public void BreakOff()
    {
        if (isBroken)
        {
            return;
        }

        isBroken = true;

        List<BoatPiece> childrenCopy = new List<BoatPiece>(childPieces);

        foreach (BoatPiece child in childrenCopy)
        {
            child.BreakOff();
        }

        childPieces.Clear();

        if (parentPiece != null)
        {
            parentPiece.childPieces.Remove(this);
            parentPiece = null;
        }

        transform.SetParent(null, true);
    }
}