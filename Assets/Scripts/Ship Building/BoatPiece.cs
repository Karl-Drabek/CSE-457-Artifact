using System;
using System.Collections.Generic;
using UnityEngine;

public class BoatPiece : MonoBehaviour
{
    /// <summary>Fired when the root hull piece breaks. VoyageCycleController ends the round.</summary>
    public static event Action OnHullBroken;

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

    [Header("Mass")]
    public float pieceMass = 1f;
    public Vector3 localCenterOfMass = Vector3.zero;

    [Header("Gold Cost")]
    public int goldCost = 10;

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

        if (isRootHull)
        {
            OnHullBroken?.Invoke();
        }

        BoatMassManager massManager = GetComponentInParent<BoatMassManager>();

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

        if (massManager != null)
        {
            massManager.RecalculateMass();
        }

        Rigidbody rb = GetComponent<Rigidbody>();

        if (rb == null)
        {
            rb = gameObject.AddComponent<Rigidbody>();
        }

        rb.isKinematic = false;
        rb.useGravity = true;

        rb.AddForce(UnityEngine.Random.insideUnitSphere * breakForce, ForceMode.Impulse);

        Debug.Log(gameObject.name + " broke off.");

        Destroy(gameObject, destroyAfterBreakingSeconds);
    }
}