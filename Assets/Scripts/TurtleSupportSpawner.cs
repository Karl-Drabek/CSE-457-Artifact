using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Builds an infinite-looking stack of turtles under the world disc.
/// The stack layout is deterministic, but only the turtles near the current viewer are instantiated.
/// </summary>
[ExecuteAlways]
public class TurtleSupportSpawner : MonoBehaviour
{
    struct TurtlePose
    {
        public Vector3 localPosition;
        public Quaternion localRotation;
        public float localScale;
    }

    const int PoseSafetyLimit = 10000;

    [Header("Source")]
    [SerializeField] GameObject turtlePrefab;
    [SerializeField] World world;
    [SerializeField] Transform viewerTarget;

    [Header("Stack Shape")]
    [SerializeField, Min(10f)] float fallbackDiscRadius = 430f;
    [SerializeField, Range(0.1f, 2f)] float topTurtleRadiusFraction = 0.9f;
    [SerializeField, Range(0.1f, 1f)] float scaleFalloff = 0.9f;
    [SerializeField, Range(0.05f, 1f)] float minimumScaleFraction = 0.35f;
    [SerializeField, Range(0f, 2f)] float topClearanceMultiplier = 0.35f;
    [SerializeField, Range(0.1f, 1.5f)] float verticalSpacingMultiplier = 0.62f;
    [SerializeField, Range(0f, 1f)] float forwardOffsetMultiplier = 0.16f;
    [SerializeField, Range(0f, 1f)] float sideOffsetMultiplier = 0.06f;
    [SerializeField, Range(0f, 45f)] float yawStep = 14f;

    [Header("Streaming")]
    [SerializeField] bool rebuildInEditor = true;
    [SerializeField, Min(0)] int turtlesAboveViewer = 2;
    [SerializeField, Min(1)] int turtlesBelowViewer = 6;
    [SerializeField, Min(1)] int editorPreviewCount = 8;

    readonly Dictionary<int, Transform> activeTurtles = new Dictionary<int, Transform>();
    readonly List<TurtlePose> poseCache = new List<TurtlePose>();
    readonly List<int> turtleIndicesToRemove = new List<int>();

    float prefabLength;
    float prefabHeight;
    int currentStartIndex = -1;
    int currentEndIndex = -1;
    int currentLayoutHash = int.MinValue;
    bool prefabMeasurementsValid;

#if UNITY_EDITOR
    bool editorRefreshQueued;
#endif

    void OnEnable()
    {
        InvalidateLayout();
        QueueRefresh();
    }

    void OnValidate()
    {
        InvalidateLayout();
        QueueRefresh();
    }

    void LateUpdate()
    {
        if (!Application.isPlaying)
        {
            return;
        }

        RefreshVisibleTurtles();
    }

    [ContextMenu("Rebuild Turtles")]
    public void RebuildTurtles()
    {
        InvalidateLayout();
        QueueRefresh();
    }

    void RefreshVisibleTurtles()
    {
        ResolveWorldReference();

        int layoutHash = BuildLayoutHash();
        if (layoutHash != currentLayoutHash)
        {
            ClearPoseCache();
            ClearSpawnedTurtles();
            currentLayoutHash = layoutHash;
        }

        if (turtlePrefab == null)
        {
            ClearSpawnedTurtles();
            return;
        }

        if (!EnsurePrefabMeasurements())
        {
            ClearSpawnedTurtles();
            return;
        }

        GetDesiredVisibleRange(out int desiredStartIndex, out int desiredEndIndex);
        if (desiredEndIndex < desiredStartIndex)
        {
            ClearSpawnedTurtles();
            return;
        }

        if (desiredStartIndex == currentStartIndex && desiredEndIndex == currentEndIndex)
        {
            return;
        }

        EnsurePoseCache(desiredEndIndex);
        SyncVisibleTurtles(desiredStartIndex, desiredEndIndex);

        currentStartIndex = desiredStartIndex;
        currentEndIndex = desiredEndIndex;
    }

    void GetDesiredVisibleRange(out int startIndex, out int endIndex)
    {
        if (!TryGetViewerLocalPosition(out Vector3 viewerLocalPosition))
        {
            startIndex = 0;
            endIndex = Mathf.Max(0, editorPreviewCount - 1);
            return;
        }

        int centerIndex = FindClosestPoseIndex(viewerLocalPosition);
        startIndex = Mathf.Max(0, centerIndex - turtlesAboveViewer);
        endIndex = Mathf.Max(startIndex, centerIndex + turtlesBelowViewer);
    }

    int FindClosestPoseIndex(Vector3 viewerLocalPosition)
    {
        EnsurePoseCoverageForViewer(viewerLocalPosition.y);

        int closestIndex = 0;
        float closestDistanceSqr = float.MaxValue;
        for (int i = 0; i < poseCache.Count; i++)
        {
            float distanceSqr = (poseCache[i].localPosition - viewerLocalPosition).sqrMagnitude;
            if (distanceSqr < closestDistanceSqr)
            {
                closestDistanceSqr = distanceSqr;
                closestIndex = i;
            }
        }

        return closestIndex;
    }

    void EnsurePoseCoverageForViewer(float viewerLocalY)
    {
        float extraDepth = GetMinimumVerticalStep() * (turtlesBelowViewer + 2);
        float targetBottomY = viewerLocalY - extraDepth;

        while ((poseCache.Count == 0 || poseCache[poseCache.Count - 1].localPosition.y > targetBottomY) && poseCache.Count < PoseSafetyLimit)
        {
            AppendNextPose();
        }
    }

    void EnsurePoseCache(int endIndex)
    {
        while (poseCache.Count <= endIndex && poseCache.Count < PoseSafetyLimit)
        {
            AppendNextPose();
        }
    }

    void AppendNextPose()
    {
        if (!prefabMeasurementsValid)
        {
            return;
        }

        if (poseCache.Count == 0)
        {
            poseCache.Add(new TurtlePose
            {
                localPosition = Vector3.down * (prefabHeight * GetTopWorldScale() * topClearanceMultiplier),
                localRotation = GetTurtleRotation(0),
                localScale = GetTopWorldScale()
            });
            return;
        }

        int previousIndex = poseCache.Count - 1;
        TurtlePose previousPose = poseCache[previousIndex];
        Vector3 nextPosition = previousPose.localPosition + GetStepOffset(previousIndex, previousPose.localScale, previousPose.localRotation);

        poseCache.Add(new TurtlePose
        {
            localPosition = nextPosition,
            localRotation = GetTurtleRotation(previousIndex + 1),
            localScale = GetNextScale(previousPose.localScale)
        });
    }

    Vector3 GetStepOffset(int index, float currentScale, Quaternion rotation)
    {
        float verticalStep = prefabHeight * currentScale * verticalSpacingMultiplier;
        float forwardStep = prefabLength * currentScale * forwardOffsetMultiplier;
        float sideStep = prefabLength * currentScale * sideOffsetMultiplier;
        float sideSign = (index % 2 == 0) ? 1f : -1f;

        Vector3 offset = Vector3.down * verticalStep;
        offset += (rotation * Vector3.forward) * forwardStep;
        offset += (rotation * Vector3.right) * (sideStep * sideSign);
        return offset;
    }

    Quaternion GetTurtleRotation(int index)
    {
        float yaw = ((index % 2 == 0) ? 1f : -1f) * yawStep * index;
        return Quaternion.Euler(0f, yaw, 0f);
    }

    float GetTopWorldScale()
    {
        float discRadius = ResolveDiscRadius();
        return Mathf.Max(0.01f, (discRadius * topTurtleRadiusFraction) / Mathf.Max(prefabLength, 0.001f));
    }

    float GetMinimumWorldScale()
    {
        return Mathf.Max(0.01f, GetTopWorldScale() * minimumScaleFraction);
    }

    float GetNextScale(float currentScale)
    {
        return Mathf.Max(GetMinimumWorldScale(), currentScale * scaleFalloff);
    }

    float GetMinimumVerticalStep()
    {
        return prefabHeight * GetMinimumWorldScale() * verticalSpacingMultiplier;
    }

    void SyncVisibleTurtles(int startIndex, int endIndex)
    {
        turtleIndicesToRemove.Clear();
        foreach (KeyValuePair<int, Transform> pair in activeTurtles)
        {
            if (pair.Key < startIndex || pair.Key > endIndex)
            {
                turtleIndicesToRemove.Add(pair.Key);
            }
        }

        for (int i = 0; i < turtleIndicesToRemove.Count; i++)
        {
            int turtleIndex = turtleIndicesToRemove[i];
            if (activeTurtles.TryGetValue(turtleIndex, out Transform turtle))
            {
                DestroyGeneratedObject(turtle.gameObject);
            }

            activeTurtles.Remove(turtleIndex);
        }

        for (int i = startIndex; i <= endIndex; i++)
        {
            if (!activeTurtles.TryGetValue(i, out Transform turtle))
            {
                turtle = CreateSceneTurtle(i);
                if (turtle == null)
                {
                    continue;
                }

                activeTurtles[i] = turtle;
            }

            ApplyPose(turtle, i);
        }
    }

    void ApplyPose(Transform turtle, int index)
    {
        TurtlePose pose = poseCache[index];
        turtle.localPosition = pose.localPosition;
        turtle.localRotation = pose.localRotation;
        turtle.localScale = Vector3.one * pose.localScale;
        turtle.name = $"Sea Turtle {index + 1}";
    }

    Transform CreateSceneTurtle(int index)
    {
        if (!TryInstantiateTurtleObject(true, out Transform turtleTransform))
        {
            return null;
        }

        turtleTransform.name = $"Sea Turtle {index + 1}";
        return turtleTransform;
    }

    bool TryInstantiateTurtleObject(bool parentUnderSpawner, out Transform turtleTransform)
    {
        turtleTransform = null;
        if (turtlePrefab == null)
        {
            return false;
        }

        Object instantiatedObject = Instantiate((Object)turtlePrefab);
        if (instantiatedObject is GameObject instantiatedGameObject)
        {
            turtleTransform = instantiatedGameObject.transform;
        }
        else if (instantiatedObject is Component instantiatedComponent)
        {
            turtleTransform = instantiatedComponent.transform;
        }

        if (turtleTransform == null)
        {
            return false;
        }

        if (parentUnderSpawner)
        {
            turtleTransform.SetParent(transform, false);
        }

        return true;
    }

    bool EnsurePrefabMeasurements()
    {
        if (prefabMeasurementsValid)
        {
            return true;
        }

        prefabLength = 0f;
        prefabHeight = 0f;

        if (!TryInstantiateTurtleObject(false, out Transform measureRoot) || measureRoot == null)
        {
            return false;
        }

        measureRoot.position = Vector3.zero;
        measureRoot.rotation = Quaternion.identity;
        measureRoot.localScale = Vector3.one;

        Renderer[] renderers = measureRoot.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
        {
            DestroyGeneratedObject(measureRoot.gameObject);
            return false;
        }

        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
        {
            bounds.Encapsulate(renderers[i].bounds);
        }

        prefabLength = Mathf.Max(bounds.size.x, bounds.size.z);
        prefabHeight = Mathf.Max(bounds.size.y, 0.001f);
        prefabMeasurementsValid = true;

        DestroyGeneratedObject(measureRoot.gameObject);
        return true;
    }

    bool TryGetViewerLocalPosition(out Vector3 viewerLocalPosition)
    {
        Transform viewer = ResolveViewerTarget();
        if (viewer == null)
        {
            viewerLocalPosition = Vector3.zero;
            return false;
        }

        viewerLocalPosition = transform.InverseTransformPoint(viewer.position);
        return true;
    }

    Transform ResolveViewerTarget()
    {
        if (viewerTarget != null)
        {
            return viewerTarget;
        }

        if (Application.isPlaying)
        {
            Camera mainCamera = Camera.main;
            return mainCamera != null ? mainCamera.transform : null;
        }

#if UNITY_EDITOR
        SceneView sceneView = SceneView.lastActiveSceneView;
        if (sceneView != null && sceneView.camera != null)
        {
            return sceneView.camera.transform;
        }
#endif

        return null;
    }

    void ResolveWorldReference()
    {
        if (world == null)
        {
            world = FindWorldObject();
        }
    }

    static World FindWorldObject()
    {
#if UNITY_2023_1_OR_NEWER
        return FindFirstObjectByType<World>();
#else
        return FindObjectOfType<World>();
#endif
    }

    float ResolveDiscRadius()
    {
        if (world != null)
        {
            return world.GetPlayableRadius();
        }

        return fallbackDiscRadius;
    }

    int BuildLayoutHash()
    {
        unchecked
        {
            int hash = 17;
            hash = (hash * 31) + (turtlePrefab != null ? turtlePrefab.GetInstanceID() : 0);
            hash = (hash * 31) + ResolveDiscRadius().GetHashCode();
            hash = (hash * 31) + topTurtleRadiusFraction.GetHashCode();
            hash = (hash * 31) + scaleFalloff.GetHashCode();
            hash = (hash * 31) + minimumScaleFraction.GetHashCode();
            hash = (hash * 31) + topClearanceMultiplier.GetHashCode();
            hash = (hash * 31) + verticalSpacingMultiplier.GetHashCode();
            hash = (hash * 31) + forwardOffsetMultiplier.GetHashCode();
            hash = (hash * 31) + sideOffsetMultiplier.GetHashCode();
            hash = (hash * 31) + yawStep.GetHashCode();
            return hash;
        }
    }

    void InvalidateLayout()
    {
        currentLayoutHash = int.MinValue;
        currentStartIndex = -1;
        currentEndIndex = -1;
        prefabMeasurementsValid = false;
        ClearPoseCache();
    }

    void ClearPoseCache()
    {
        poseCache.Clear();
    }

    void ClearSpawnedTurtles()
    {
#if UNITY_EDITOR
        if (!Application.isPlaying && Selection.activeTransform != null && Selection.activeTransform.IsChildOf(transform))
        {
            Selection.activeGameObject = gameObject;
        }
#endif

        foreach (KeyValuePair<int, Transform> pair in activeTurtles)
        {
            if (pair.Value != null)
            {
                DestroyGeneratedObject(pair.Value.gameObject);
            }
        }

        activeTurtles.Clear();

        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            DestroyGeneratedObject(transform.GetChild(i).gameObject);
        }
    }

    void DestroyGeneratedObject(GameObject targetObject)
    {
        if (targetObject == null)
        {
            return;
        }

        if (Application.isPlaying)
        {
            Destroy(targetObject);
            return;
        }

#if UNITY_EDITOR
        DestroyImmediate(targetObject);
#else
        Destroy(targetObject);
#endif
    }

    void QueueRefresh()
    {
#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            QueueEditorRefresh();
        }
#endif
    }

#if UNITY_EDITOR
    void QueueEditorRefresh()
    {
        if (!rebuildInEditor || EditorApplication.isPlayingOrWillChangePlaymode || editorRefreshQueued)
        {
            return;
        }

        editorRefreshQueued = true;
        EditorApplication.delayCall += RefreshInEditorAfterDelay;
    }

    void RefreshInEditorAfterDelay()
    {
        editorRefreshQueued = false;

        if (this == null || gameObject == null)
        {
            return;
        }

        if (Application.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode || !rebuildInEditor)
        {
            return;
        }

        RefreshVisibleTurtles();
        SceneView.RepaintAll();
    }
#endif
}
