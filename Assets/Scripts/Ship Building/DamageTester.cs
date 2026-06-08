using UnityEngine;
using UnityEngine.InputSystem;

public class DamageTester : MonoBehaviour
{
    public float damageAmount = 25f;

    void Update()
    {
        if (Keyboard.current == null)
        {
            return;
        }

        if (Keyboard.current.tKey.wasPressedThisFrame)
        {
            BoatPiece piece = GetComponent<BoatPiece>();

            if (piece != null)
            {
                piece.TakeDamage(damageAmount);
            }
        }
    }
}