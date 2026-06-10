using UnityEngine;

[DisallowMultipleComponent]
public class VoyageObstacle : MonoBehaviour
{
    [SerializeField] Sprite compassIcon;

    World owner;
    bool destructionReported;

    public string ObstacleId { get; private set; }
    public string DisplayName { get; private set; }
    public Sprite CompassIcon => compassIcon;

    public void Configure(World world, string obstacleId, string displayName, Sprite assignedCompassIcon)
    {
        owner = world;
        ObstacleId = obstacleId;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? gameObject.name : displayName;
        if (assignedCompassIcon != null)
        {
            compassIcon = assignedCompassIcon;
        }

        destructionReported = false;
    }

    /// <summary>
    /// Records this obstacle as defeated and immediately destroys the GameObject.
    /// Use when no death animation is needed.
    /// </summary>
    public void DestroyObstacle()
    {
        ReportDefeated();

        if (Application.isPlaying)
        {
            Destroy(gameObject);
        }
        else
        {
            DestroyImmediate(gameObject);
        }
    }

    /// <summary>
    /// Records this obstacle as defeated without destroying the GameObject.
    /// Use when a death animation (e.g. sinking) will handle the final Destroy.
    /// </summary>
    public void ReportDefeated()
    {
        if (destructionReported) return;
        if (owner != null)
        {
            owner.HandleObjectiveObstacleDestroyed(this);
            destructionReported = true;
        }
    }

    void OnDestroy()
    {
        if (owner == null)
        {
            return;
        }

        if (!Application.isPlaying)
        {
            owner.UnregisterObjectiveObstacle(this);
            return;
        }

        if (owner.SuppressObstacleDestructionReporting)
        {
            owner.UnregisterObjectiveObstacle(this);
            return;
        }

        if (destructionReported)
        {
            owner.UnregisterObjectiveObstacle(this);
            return;
        }

        owner.HandleObjectiveObstacleDestroyed(this);
        destructionReported = true;
    }
}
