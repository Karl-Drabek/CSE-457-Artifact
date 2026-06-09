using UnityEngine;

/// <summary>
/// Simple collision bridge that tells World when the boat touches the generated border wall.
/// </summary>
public class OpenWorldBorderIceberg : MonoBehaviour
{
    void OnCollisionEnter(Collision collision)
    {
        if (World.Instance == null)
        {
            return;
        }

        World.Instance.HandleBorderCollision(gameObject, collision.transform);
    }

    void OnTriggerEnter(Collider other)
    {
        if (World.Instance == null)
        {
            return;
        }

        World.Instance.HandleBorderCollision(gameObject, other.transform);
    }
}
