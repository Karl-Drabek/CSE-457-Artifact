using UnityEngine;

public class OpenWorldBorderIceberg : MonoBehaviour
{
    void OnCollisionEnter(Collision collision)
    {
        if (OpenWorldManager.Instance == null)
        {
            return;
        }

        OpenWorldManager.Instance.HandleBorderCollision(gameObject, collision.transform);
    }

    void OnTriggerEnter(Collider other)
    {
        if (OpenWorldManager.Instance == null)
        {
            return;
        }

        OpenWorldManager.Instance.HandleBorderCollision(gameObject, other.transform);
    }
}
