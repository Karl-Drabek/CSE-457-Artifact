using System.Collections.Generic;
using UnityEngine;

public class BoatPiece : MonoBehaviour
{
    [Header("Hierarchy")]
    public BoatPiece parentPiece;
    public List<BoatPiece> childPieces = new List<BoatPiece>();

    [Header("Piece State")]
    public bool isRootHull = false;
    public bool isBroken = false;

    [Header("Durability")]
    public float maxDurability = 100f;
    public float currentDurability;

    [Header("Break Physics")]
    public float breakForce = 2f;

    [Header("Cleanup")]
    public float destroyAfterBreakingSeconds = 3f;

    private void Awake()
    {
        currentDurability = maxDurability;
    }

    public void AttachTo(BoatPiece newParent)
    {
        if (newParent == null)
        {
            return;
        }

        if (newParent == this)
        {
            Debug.LogWarning("Cannot attach a BoatPiece to itself.");
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

        // Keep world position/rotation when reparenting
        transform.SetParent(newParent.transform, true);
    }

    public void TakeDamage(float amount)
    {
        if (isBroken)
        {
            return;
        }

        currentDurability -= amount;
        currentDurability = Mathf.Clamp(currentDurability, 0f, maxDurability);

        Debug.Log(gameObject.name + " durability: " + currentDurability);

        if (currentDurability <= 0f)
        {
            BreakOff();
        }
    }

    public void BreakOff()
    {
        if (isBroken)
        {
            return;
        }

        isBroken = true;

        // Copy children so the list can safely change while looping
        List<BoatPiece> childrenCopy = new List<BoatPiece>(childPieces);

        // Anything attached to this piece also breaks off
        foreach (BoatPiece child in childrenCopy)
        {
            child.BreakOff();
        }

        childPieces.Clear();

        // Remove this piece from its parent
        if (parentPiece != null)
        {
            parentPiece.childPieces.Remove(this);
            parentPiece = null;
        }

        // Detach from boat hierarchy
        transform.SetParent(null, true);

        // Add physics so it becomes a loose broken piece
        Rigidbody rb = GetComponent<Rigidbody>();

        if (rb == null)
        {
            rb = gameObject.AddComponent<Rigidbody>();
        }

        rb.isKinematic = false;
        rb.useGravity = true;

        // Optional little push so the break is visible
        rb.AddForce(Random.insideUnitSphere * breakForce, ForceMode.Impulse);

        Debug.Log(gameObject.name + " broke off.");

        // Remove broken piece after a few seconds
        Destroy(gameObject, destroyAfterBreakingSeconds);
    }
}