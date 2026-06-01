using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(WaterBuoyancy))]
sealed class WaterBuoyancyEditor : Editor
{
    const float WeightEpsilon = 0.0001f;
    const float MoveHandleSizeScale = 0.08f;
    const float PlacementPreviewSizeScale = 0.09f;

    static readonly Color ManualPointColor = new Color(0.15f, 0.8f, 1f, 0.95f);
    static readonly Color PlacementPreviewColor = new Color(0.25f, 0.95f, 1f, 0.95f);

    enum InspectorSamplingMode
    {
        Lattice,
        Raycast,
        Manual
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
    SerializedProperty pitchDampingProperty;

    static bool scenePlacementEnabled;
    static bool pointEditingEnabled;
    Mesh bakedSkinnedMesh;
    static Tool previousTool = Tool.None;

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
        pitchDampingProperty = serializedObject.FindProperty("pitchDamping");

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
        EditorGUILayout.PropertyField(pitchDampingProperty);

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
            EditorGUILayout.PropertyField(horizontalSampleCountProperty);
            EditorGUILayout.PropertyField(horizontalEdgeInsetProperty);
            EditorGUILayout.PropertyField(xEdgeOffsetProperty);
            EditorGUILayout.PropertyField(zEdgeOffsetProperty);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Hull Shape", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("hull_height"),
                new GUIContent("Hull Height", "Maximum local y value of the hull"));
            if (GUILayout.Button("Recalculate Weights"))
            {
                TargetBuoyancy.RecalculateManualPointWeights();
                EditorUtility.SetDirty(TargetBuoyancy);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(scenePlacementEnabled ? "Stop Adding Points" : "Add Points On Mesh"))
                {
                    if (scenePlacementEnabled)
                        DisableScenePlacement();
                    else
                        EnableScenePlacement();
                }

                using (new EditorGUI.DisabledScope(manualSamplePointsProperty.arraySize == 0))
                {
                    if (GUILayout.Button("Clear Manual Points"))
                    {
                        manualSamplePointsProperty.ClearArray();
                        serializedObject.ApplyModifiedProperties();
                        SceneView.RepaintAll();
                    }
                }
            }

            if (!MeshPointPlacementUtility.HasPlaceableMesh(TargetBuoyancy))
            {
                EditorGUILayout.HelpBox("No MeshFilter or SkinnedMeshRenderer found for scene-click placement.", MessageType.Warning);
            }
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
        Component placementTarget = TargetBuoyancy;
        int unreadableStaticMeshCount = MeshPointPlacementUtility.CountUnreadableStaticMeshes(placementTarget);

        EditorGUILayout.HelpBox(
            "Use scene placement to add points and point editing to drag existing points in the Scene view. The array values remain directly editable in the Inspector.",
            MessageType.Info);

        EditorGUILayout.PropertyField(manualSamplePointsProperty, new GUIContent("Sample Points"), true);

        EditorGUILayout.LabelField("Total Weight", GetTotalWeight().ToString("0.###"));

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(scenePlacementEnabled ? "Stop Scene Placement" : "Start Scene Placement"))
            {
                if (scenePlacementEnabled)
                    DisableScenePlacement();
                else
                    EnableScenePlacement();
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

        if (unreadableStaticMeshCount > 0)
        {
            EditorGUILayout.HelpBox(
                "Some MeshFilter meshes on this hierarchy have Read/Write disabled, so scene-click placement will skip them. Enable Read/Write in the mesh import settings if you want to place points on those meshes.",
                MessageType.Warning);
        }

        if (!MeshPointPlacementUtility.HasPlaceableMesh(placementTarget))
        {
            string missingMeshMessage = unreadableStaticMeshCount > 0
                ? "No readable MeshFilter or SkinnedMeshRenderer was found on this object or its children for scene-click placement."
                : "No MeshFilter or SkinnedMeshRenderer was found on this object or its children for scene-click placement.";
            EditorGUILayout.HelpBox(missingMeshMessage, MessageType.Warning);
        }
    }

    // Scene interaction is disabled outside manual mode so auto-generated layouts stay read-only.
    void OnSceneGUI()
    {
        serializedObject.Update();

        InspectorSamplingMode samplingMode = GetInspectorSamplingMode();
        bool allowSceneTools = samplingMode == InspectorSamplingMode.Manual
            || samplingMode == InspectorSamplingMode.Raycast;
        if (!allowSceneTools)
        {
            DisableSceneTools();
            serializedObject.ApplyModifiedProperties();
            return;
        }

        // Draw manual points in orange whenever in raycast mode
        if (samplingMode == InspectorSamplingMode.Raycast)
        {
            DrawManualPointGizmos();
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


void DrawManualPointGizmos()
{
    if (manualSamplePointsProperty.arraySize == 0)
    {
        return;
    }

    float totalWeight = GetTotalWeight();

    for (int i = 0; i < manualSamplePointsProperty.arraySize; i++)
    {
        SerializedProperty pointProperty = manualSamplePointsProperty.GetArrayElementAtIndex(i);
        SerializedProperty positionProperty = pointProperty.FindPropertyRelative(LocalPositionFieldName);
        SerializedProperty weightProperty = pointProperty.FindPropertyRelative(WeightFieldName);

        Vector3 worldPosition = TargetBuoyancy.transform.TransformPoint(positionProperty.vector3Value);
        float gizmoSize = 0.1f;

        // Draw faint version through geometry so occluded points are still visible
        using (new Handles.DrawingScope(new Color(1f, 0.5f, 0.1f, 0.15f)))
        {
            Handles.zTest = UnityEngine.Rendering.CompareFunction.Greater;
            Handles.SphereHandleCap(0, worldPosition, Quaternion.identity, gizmoSize, EventType.Repaint);
        }

        // Draw full opacity version on top when not occluded
        using (new Handles.DrawingScope(new Color(1f, 0.5f, 0.1f, 0.9f)))
        {
            Handles.zTest = UnityEngine.Rendering.CompareFunction.LessEqual;
            Handles.SphereHandleCap(0, worldPosition, Quaternion.identity, gizmoSize, EventType.Repaint);
        }
    }

    // Always reset zTest after drawing so other scene handles aren't affected
    Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;
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
        if (!MeshPointPlacementUtility.HasPlaceableMesh(TargetBuoyancy))
        {
            return;
        }

        Event currentEvent = Event.current;

        int controlId = GUIUtility.GetControlID(FocusType.Keyboard);

        if (currentEvent.type == EventType.Layout)
        {
            HandleUtility.AddDefaultControl(controlId);
            return;
        }

        HandleUtility.AddDefaultControl(controlId);

        //if (currentEvent.type == EventType.Repaint)
        //{
        //    Handles.BeginGUI();
        //    GUILayout.BeginArea(new Rect(10, 10, 280, 60));
        //    GUI.color = new Color(0.2f, 0.8f, 1f, 0.95f);
        //    GUILayout.BeginVertical(EditorStyles.helpBox);
        //    GUILayout.Label("Point Placement Active", EditorStyles.boldLabel);
        //    GUILayout.Label("Left click mesh to place  |  Esc or Right click to exit");
        //    GUILayout.EndVertical();
        //    GUI.color = Color.white;
        //    GUILayout.EndArea();
        //    Handles.EndGUI();
        //}

        if (currentEvent.alt)
        {
            return;
        }


        if (currentEvent.type == EventType.MouseDown && currentEvent.button == 0)
        {
            Ray ray = HandleUtility.GUIPointToWorldRay(currentEvent.mousePosition);
            if (MeshPointPlacementUtility.TryGetNearestMeshHit(TargetBuoyancy, bakedSkinnedMesh, ray, out MeshPointPlacementUtility.MeshHit hit))
            {
                Vector3 localPosition = TargetBuoyancy.transform.InverseTransformPoint(hit.point);
                // Weight based on Y position within bounds at placement time
                if (TargetBuoyancy.TryGetCombinedWorldBounds(out Bounds worldBounds))
                {
                    // Convert bounds to local space so comparison matches stored local positions
                    Vector3 localMin = TargetBuoyancy.transform.InverseTransformPoint(worldBounds.min);
                    Vector3 localMax = TargetBuoyancy.transform.InverseTransformPoint(worldBounds.max);

                    float weight = TargetBuoyancy.calculateWeight(localMin.y, localPosition.y);
                    weight = Mathf.Max(0.2f, weight);
                    AddManualPoint(localPosition, weight);
                }
                else
                {
                    AddManualPoint(localPosition, 1f);
                }

                GUIUtility.hotControl = controlId;
                currentEvent.Use();
                float previewSize = HandleUtility.GetHandleSize(hit.point) * PlacementPreviewSizeScale;
                using (new Handles.DrawingScope(PlacementPreviewColor))
                {
                    Handles.SphereHandleCap(0, hit.point, Quaternion.identity, previewSize, EventType.Repaint);
                    Handles.DrawLine(hit.point, hit.point + (hit.normal * previewSize * 2f));
                }

            }
        }

        if ((currentEvent.type == EventType.MouseDown && currentEvent.button == 1)
            || (currentEvent.type == EventType.KeyDown && currentEvent.keyCode == KeyCode.Escape))
        {
            DisableScenePlacement();
            currentEvent.Use();
        }

        if (currentEvent.type == EventType.MouseMove || currentEvent.type == EventType.MouseDrag)
        {
            SceneView.RepaintAll();
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
        if (scenePlacementEnabled && Tools.current == Tool.None)
        {
            Tools.current = previousTool;
        }
        scenePlacementEnabled = false;
        pointEditingEnabled = false;
    }

    void EnableScenePlacement()
    {
        scenePlacementEnabled = true;
        pointEditingEnabled = false;
        previousTool = Tools.current;
        Tools.current = Tool.None; // stops Unity consuming mouse clicks for selection/transform
        SceneView.lastActiveSceneView?.Focus(); // pull focus to scene view
        SceneView.RepaintAll();
    }

    void DisableScenePlacement()
    {
        scenePlacementEnabled = false;
        if (Tools.current == Tool.None)
        {
            Tools.current = previousTool;
        }
        SceneView.RepaintAll();
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
