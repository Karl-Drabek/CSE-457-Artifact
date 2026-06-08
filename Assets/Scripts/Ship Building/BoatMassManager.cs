using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class BoatMassManager : MonoBehaviour
{
    private Rigidbody rb;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
    }

    public void RecalculateMass()
    {
        if (rb == null)
        {
            rb = GetComponent<Rigidbody>();
        }

        BoatPiece[] pieces = GetComponentsInChildren<BoatPiece>();

        float totalMass = 0f;
        Vector3 weightedCenter = Vector3.zero;

        foreach (BoatPiece piece in pieces)
        {
            if (piece == null || piece.isBroken)
            {
                continue;
            }

            float mass = Mathf.Max(0f, piece.pieceMass);

            Vector3 worldCenter = piece.transform.TransformPoint(piece.localCenterOfMass);
            Vector3 rootLocalCenter = transform.InverseTransformPoint(worldCenter);

            weightedCenter += mass * rootLocalCenter;
            totalMass += mass;
        }

        if (totalMass <= 0f)
        {
            Debug.LogWarning("BoatMassManager: No remaining mass.");
            return;
        }

        rb.mass = totalMass;
        rb.centerOfMass = weightedCenter / totalMass;

        Debug.Log("Recalculated boat mass: " + rb.mass + ", COM: " + rb.centerOfMass);
    }
}