using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(WaterBuoyancy))]
sealed class WaterBuoyancyEditor : Editor
{
    const float WeightEpsilon = 0.0001f;
    const float MoveHandleSizeScale = 0.08f;
    const float PlacementPreviewSizeScale = 0.09f;
    const float GeometryEpsilon = 0.000001f;

    static readonly Color ManualPointColor = new Color(0.15f, 0.8f, 1f, 0.95f);
    static readonly Color PlacementPreviewColor = new Color(0.25f, 0.95f, 1f, 0.95f);

    enum InspectorSamplingMode
    {
        Lattice,
        Raycast,
        Manual
    }

    struct MeshHit
    {
        public Vector3 point;
        public Vector3 normal;
        public float distance;
    }

    const string LocalPositionFieldName = "localPosition";
    const string WeightFieldName = "weight";

    SerializedProperty autoGenerateSamplePointsProperty;
    SerializedProperty manualSamplePointsProperty;
    SerializedProperty objectDensityProperty;
    SerializedProperty maxSubmergenceProperty;
    SerializedProperty surfaceNormalInfluenceProperty;
    SerializedProperty waterDragProperty;
    SerializedProperty waterAngularDragProperty;
    SerializedProperty sampleModeProperty;
    SerializedProperty horizontalSampleCountProperty;
    SerializedProperty verticalSampleCountProperty;
    SerializedProperty verticalEdgeInsetProperty;
    SerializedProperty horizontalEdgeInsetProperty;
    SerializedProperty xEdgeOffsetProperty;
    SerializedProperty zEdgeOffsetProperty;

    bool scenePlacementEnabled;
    bool pointEditingEnabled;
    Mesh bakedSkinnedMesh;

    WaterBuoyancy TargetBuoyancy => (WaterBuoyancy)target;

    void OnEnable()
    {
        autoGenerateSamplePointsProperty = serializedObject.FindProperty("autoGenerateSamplePoints");
        manualSamplePointsProperty = serializedObject.FindProperty("manualSamplePoints");
        objectDensityProperty = serializedObject.FindProperty("objectDensity");
        maxSubmergenceProperty = serializedObject.FindProperty("maxSubmergence");
        surfaceNormalInfluenceProperty = serializedObject.FindProperty("surfaceNormalInfluence");
        waterDragProperty = serializedObject.FindProperty("waterDrag");
        waterAngularDragProperty = serializedObject.FindProperty("waterAngularDrag");
        sampleModeProperty = serializedObject.FindProperty("sampleMode");
        horizontalSampleCountProperty = serializedObject.FindProperty("horizontalSampleCount");
        verticalSampleCountProperty = serializedObject.FindProperty("verticalSampleCount");
        verticalEdgeInsetProperty = serializedObject.FindProperty("verticalEdgeInset");
        horizontalEdgeInsetProperty = serializedObject.FindProperty("horizontalEdgeInset");
        xEdgeOffsetProperty = serializedObject.FindProperty("xEdgeOffset");
        zEdgeOffsetProperty = serializedObject.FindProperty("zEdgeOffset");

        if (bakedSkinnedMesh == null)
        {
            bakedSkinnedMesh = new Mesh
            {
                name = "WaterBuoyancyEditor_BakedMesh",
                hideFlags = HideFlags.HideAndDontSave
            };
        }
    }

    void OnDisable()
    {
        DisableSceneTools();

        if (bakedSkinnedMesh != null)
        {
            DestroyImmediate(bakedSkinnedMesh);
            bakedSkinnedMesh = null;
        }
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Buoyancy", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(objectDensityProperty, new GUIContent("Density"));
        EditorGUILayout.PropertyField(maxSubmergenceProperty);
        EditorGUILayout.PropertyField(surfaceNormalInfluenceProperty);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Damping", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(waterDragProperty);
        EditorGUILayout.PropertyField(waterAngularDragProperty);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Sampling", EditorStyles.boldLabel);
        InspectorSamplingMode inspectorSamplingMode = GetInspectorSamplingMode();
        EditorGUI.BeginChangeCheck();
        inspectorSamplingMode = (InspectorSamplingMode)EditorGUILayout.EnumPopup("Mode", inspectorSamplingMode);
        if (EditorGUI.EndChangeCheck())
        {
            ApplyInspectorSamplingMode(inspectorSamplingMode);
        }

        if (inspectorSamplingMode == InspectorSamplingMode.Lattice)
        {
            DisableSceneTools();
            EditorGUILayout.PropertyField(horizontalSampleCountProperty);
            EditorGUILayout.PropertyField(verticalSampleCountProperty);
            EditorGUILayout.PropertyField(verticalEdgeInsetProperty);
            EditorGUILayout.PropertyField(horizontalEdgeInsetProperty);
        }
        else if (inspectorSamplingMode == InspectorSamplingMode.Raycast)
        {
            DisableSceneTools();
            EditorGUILayout.PropertyField(horizontalSampleCountProperty);
            EditorGUILayout.PropertyField(horizontalEdgeInsetProperty);
            EditorGUILayout.PropertyField(xEdgeOffsetProperty);
            EditorGUILayout.PropertyField(zEdgeOffsetProperty);
        }
        else
        {
            DrawManualSamplesInspector();
        }

        serializedObject.ApplyModifiedProperties();
    }

    // Manual mode is the only place we expose scene tools because those points are user-authored.
    void DrawManualSamplesInspector()
    {
        EditorGUILayout.HelpBox(
            "Use scene placement to add points and point editing to drag existing points in the Scene view. The array values remain directly editable in the Inspector.",
            MessageType.Info);

        EditorGUILayout.PropertyField(manualSamplePointsProperty, new GUIContent("Sample Points"), true);

        EditorGUILayout.LabelField("Total Weight", GetTotalWeight().ToString("0.###"));

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(scenePlacementEnabled ? "Stop Scene Placement" : "Start Scene Placement"))
            {
                scenePlacementEnabled = !scenePlacementEnabled;
                if (scenePlacementEnabled)
                {
                    pointEditingEnabled = false;
                }

                SceneView.RepaintAll();
            }

            if (GUILayout.Button(pointEditingEnabled ? "Stop Point Editing" : "Start Point Editing"))
            {
                pointEditingEnabled = !pointEditingEnabled;
                if (pointEditingEnabled)
                {
                    scenePlacementEnabled = false;
                }

                SceneView.RepaintAll();
            }
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Add Point At Pivot"))
            {
                AddManualPoint(Vector3.zero, 1f);
            }

            using (new EditorGUI.DisabledScope(manualSamplePointsProperty.arraySize == 0))
            {
                if (GUILayout.Button("Clear Points"))
                {
                    manualSamplePointsProperty.ClearArray();
                    SceneView.RepaintAll();
                }
            }
        }

        if (!HasPlaceableMesh())
        {
            EditorGUILayout.HelpBox("No MeshFilter or SkinnedMeshRenderer was found on this object or its children for scene-click placement.", MessageType.Warning);
        }
    }

    // Scene interaction is disabled outside manual mode so auto-generated layouts stay read-only.
    void OnSceneGUI()
    {
        serializedObject.Update();

        if (GetInspectorSamplingMode() != InspectorSamplingMode.Manual)
        {
            DisableSceneTools();
            serializedObject.ApplyModifiedProperties();
            return;
        }

        if (scenePlacementEnabled)
        {
            HandleScenePlacement();
        }
        else if (pointEditingEnabled)
        {
            DrawExistingPointHandles();
        }

        serializedObject.ApplyModifiedProperties();
    }

    void DrawExistingPointHandles()
    {
        float totalWeight = GetTotalWeight();

        for (int i = 0; i < manualSamplePointsProperty.arraySize; i++)
        {
            SerializedProperty pointProperty = manualSamplePointsProperty.GetArrayElementAtIndex(i);
            SerializedProperty positionProperty = pointProperty.FindPropertyRelative(LocalPositionFieldName);
            SerializedProperty weightProperty = pointProperty.FindPropertyRelative(WeightFieldName);

            Vector3 worldPosition = TargetBuoyancy.transform.TransformPoint(positionProperty.vector3Value);
            float handleSize = HandleUtility.GetHandleSize(worldPosition) * MoveHandleSizeScale;

            using (new Handles.DrawingScope(ManualPointColor))
            {
                EditorGUI.BeginChangeCheck();
                Vector3 movedWorldPosition = Handles.FreeMoveHandle(
                    worldPosition,
                    handleSize,
                    Vector3.zero,
                    Handles.SphereHandleCap);

                if (EditorGUI.EndChangeCheck())
                {
                    positionProperty.vector3Value = TargetBuoyancy.transform.InverseTransformPoint(movedWorldPosition);
                }

                float normalizedWeight = GetNormalizedWeight(weightProperty.floatValue, totalWeight, manualSamplePointsProperty.arraySize);
                string pointLabel = "P" + i + " (" + Mathf.RoundToInt(normalizedWeight * 100f) + "%)";
                Handles.Label(worldPosition + (Vector3.up * handleSize), pointLabel);
            }
        }
    }

    void HandleScenePlacement()
    {
        if (!HasPlaceableMesh())
        {
            return;
        }

        Event currentEvent = Event.current;
        if (currentEvent.alt)
        {
            return;
        }

        int controlId = GUIUtility.GetControlID(FocusType.Passive);
        HandleUtility.AddDefaultControl(controlId);

        Ray ray = HandleUtility.GUIPointToWorldRay(currentEvent.mousePosition);
        if (!TryGetNearestMeshHit(ray, out MeshHit hit))
        {
            if (currentEvent.type == EventType.MouseMove)
            {
                SceneView.RepaintAll();
            }

            return;
        }

        float previewSize = HandleUtility.GetHandleSize(hit.point) * PlacementPreviewSizeScale;
        using (new Handles.DrawingScope(PlacementPreviewColor))
        {
            Handles.SphereHandleCap(0, hit.point, Quaternion.identity, previewSize, EventType.Repaint);
            Handles.DrawLine(hit.point, hit.point + (hit.normal * previewSize * 2f));
        }

        if (currentEvent.type == EventType.MouseMove)
        {
            SceneView.RepaintAll();
        }

        if (currentEvent.type == EventType.MouseDown && currentEvent.button == 0 && !currentEvent.alt)
        {
            Vector3 localPosition = TargetBuoyancy.transform.InverseTransformPoint(hit.point);
            AddManualPoint(localPosition, 1f);
            currentEvent.Use();
        }
    }

    void AddManualPoint(Vector3 localPosition, float weight)
    {
        serializedObject.Update();

        int newIndex = manualSamplePointsProperty.arraySize;
        manualSamplePointsProperty.arraySize++;

        SerializedProperty pointProperty = manualSamplePointsProperty.GetArrayElementAtIndex(newIndex);
        pointProperty.FindPropertyRelative(LocalPositionFieldName).vector3Value = localPosition;
        pointProperty.FindPropertyRelative(WeightFieldName).floatValue = Mathf.Max(0f, weight);

        serializedObject.ApplyModifiedProperties();
        SceneView.RepaintAll();
    }

    bool HasPlaceableMesh()
    {
        return TargetBuoyancy.GetComponentsInChildren<MeshFilter>().Length > 0
            || TargetBuoyancy.GetComponentsInChildren<SkinnedMeshRenderer>().Length > 0;
    }

    // Finds the closest hit on any mesh under this buoyancy object so scene placement works on complex models.
    bool TryGetNearestMeshHit(Ray ray, out MeshHit closestHit)
    {
        closestHit = default;

        bool foundHit = false;
        float closestDistance = float.PositiveInfinity;

        MeshFilter[] meshFilters = TargetBuoyancy.GetComponentsInChildren<MeshFilter>();
        for (int i = 0; i < meshFilters.Length; i++)
        {
            MeshFilter meshFilter = meshFilters[i];
            if (meshFilter == null || meshFilter.sharedMesh == null)
            {
                continue;
            }

            if (!TryIntersectMesh(ray, meshFilter.sharedMesh, meshFilter.transform.localToWorldMatrix, out MeshHit hit))
            {
                continue;
            }

            if (hit.distance >= closestDistance)
            {
                continue;
            }

            closestDistance = hit.distance;
            closestHit = hit;
            foundHit = true;
        }

        SkinnedMeshRenderer[] skinnedMeshes = TargetBuoyancy.GetComponentsInChildren<SkinnedMeshRenderer>();
        for (int i = 0; i < skinnedMeshes.Length; i++)
        {
            SkinnedMeshRenderer skinnedMesh = skinnedMeshes[i];
            if (skinnedMesh == null || skinnedMesh.sharedMesh == null)
            {
                continue;
            }

            skinnedMesh.BakeMesh(bakedSkinnedMesh);
            if (!TryIntersectMesh(ray, bakedSkinnedMesh, skinnedMesh.transform.localToWorldMatrix, out MeshHit hit))
            {
                continue;
            }

            if (hit.distance >= closestDistance)
            {
                continue;
            }

            closestDistance = hit.distance;
            closestHit = hit;
            foundHit = true;
        }

        return foundHit;
    }

    // Intersects the scene ray against every triangle in one mesh, without depending on newer editor-only APIs.
    bool TryIntersectMesh(Ray worldRay, Mesh mesh, Matrix4x4 localToWorld, out MeshHit closestHit)
    {
        closestHit = default;
        if (mesh == null)
        {
            return false;
        }

        Vector3[] vertices = mesh.vertices;
        int[] triangles = mesh.triangles;
        if (vertices == null || vertices.Length == 0 || triangles == null || triangles.Length < 3)
        {
            return false;
        }

        Matrix4x4 worldToLocal = localToWorld.inverse;
        Vector3 localRayOrigin = worldToLocal.MultiplyPoint3x4(worldRay.origin);
        Vector3 localRayDirection = worldToLocal.MultiplyVector(worldRay.direction).normalized;
        Ray localRay = new Ray(localRayOrigin, localRayDirection);

        if (!mesh.bounds.IntersectRay(localRay))
        {
            return false;
        }

        bool foundHit = false;
        float closestWorldDistance = float.PositiveInfinity;

        for (int triangleIndex = 0; triangleIndex < triangles.Length; triangleIndex += 3)
        {
            Vector3 a = vertices[triangles[triangleIndex]];
            Vector3 b = vertices[triangles[triangleIndex + 1]];
            Vector3 c = vertices[triangles[triangleIndex + 2]];

            if (!TryIntersectTriangle(localRay, a, b, c, out float localDistance))
            {
                continue;
            }

            Vector3 localPoint = localRay.origin + (localRay.direction * localDistance);
            Vector3 worldPoint = localToWorld.MultiplyPoint3x4(localPoint);
            float worldDistance = Vector3.Distance(worldRay.origin, worldPoint);
            if (worldDistance >= closestWorldDistance)
            {
                continue;
            }

            Vector3 localNormal = Vector3.Cross(b - a, c - a);
            if (localNormal.sqrMagnitude <= GeometryEpsilon)
            {
                continue;
            }

            Vector3 worldNormal = worldToLocal.transpose.MultiplyVector(localNormal).normalized;
            if (Vector3.Dot(worldNormal, worldRay.direction) > 0f)
            {
                worldNormal = -worldNormal;
            }

            closestWorldDistance = worldDistance;
            closestHit = new MeshHit
            {
                point = worldPoint,
                normal = worldNormal,
                distance = worldDistance
            };
            foundHit = true;
        }

        return foundHit;
    }

    bool TryIntersectTriangle(Ray ray, Vector3 a, Vector3 b, Vector3 c, out float distance)
    {
        distance = 0f;

        Vector3 edgeAB = b - a;
        Vector3 edgeAC = c - a;
        Vector3 perpendicular = Vector3.Cross(ray.direction, edgeAC);
        float determinant = Vector3.Dot(edgeAB, perpendicular);

        // Moller-Trumbore ray-triangle test in local mesh space.
        if (Mathf.Abs(determinant) < GeometryEpsilon)
        {
            return false;
        }

        float inverseDeterminant = 1f / determinant;
        Vector3 triangleToRay = ray.origin - a;
        float u = Vector3.Dot(triangleToRay, perpendicular) * inverseDeterminant;
        if (u < 0f || u > 1f)
        {
            return false;
        }

        Vector3 q = Vector3.Cross(triangleToRay, edgeAB);
        float v = Vector3.Dot(ray.direction, q) * inverseDeterminant;
        if (v < 0f || (u + v) > 1f)
        {
            return false;
        }

        float hitDistance = Vector3.Dot(edgeAC, q) * inverseDeterminant;
        if (hitDistance < 0f)
        {
            return false;
        }

        distance = hitDistance;
        return true;
    }

    float GetTotalWeight()
    {
        float totalWeight = 0f;
        for (int i = 0; i < manualSamplePointsProperty.arraySize; i++)
        {
            SerializedProperty pointProperty = manualSamplePointsProperty.GetArrayElementAtIndex(i);
            SerializedProperty weightProperty = pointProperty.FindPropertyRelative(WeightFieldName);
            totalWeight += Mathf.Max(0f, weightProperty.floatValue);
        }

        return totalWeight;
    }

    float GetNormalizedWeight(float weight, float totalWeight, int pointCount)
    {
        if (pointCount <= 0)
        {
            return 0f;
        }

        if (totalWeight <= WeightEpsilon)
        {
            return 1f / pointCount;
        }

        return Mathf.Max(0f, weight) / totalWeight;
    }

    void DisableSceneTools()
    {
        scenePlacementEnabled = false;
        pointEditingEnabled = false;
    }

    InspectorSamplingMode GetInspectorSamplingMode()
    {
        if (!autoGenerateSamplePointsProperty.boolValue)
        {
            return InspectorSamplingMode.Manual;
        }

        return sampleModeProperty.enumValueIndex == (int)WaterBuoyancy.SampleMode.Lattice
            ? InspectorSamplingMode.Lattice
            : InspectorSamplingMode.Raycast;
    }

    void ApplyInspectorSamplingMode(InspectorSamplingMode mode)
    {
        switch (mode)
        {
            case InspectorSamplingMode.Manual:
                autoGenerateSamplePointsProperty.boolValue = false;
                break;
            case InspectorSamplingMode.Lattice:
                autoGenerateSamplePointsProperty.boolValue = true;
                sampleModeProperty.enumValueIndex = (int)WaterBuoyancy.SampleMode.Lattice;
                break;
            default:
                autoGenerateSamplePointsProperty.boolValue = true;
                sampleModeProperty.enumValueIndex = (int)WaterBuoyancy.SampleMode.Raycast;
                break;
        }
    }
}
