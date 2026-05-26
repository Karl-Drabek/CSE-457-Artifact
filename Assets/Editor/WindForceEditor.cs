using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(WindForce))]
sealed class WindForceEditor : Editor
{
    enum InspectorWindApplicationMode
    {
        SurfaceTriangles,
        SurfaceRaycast
    }

    SerializedProperty forceMultiplierProperty;
    SerializedProperty allowVerticalForceProperty;
    SerializedProperty useApparentWindProperty;
    SerializedProperty applicationModeProperty;
    SerializedProperty meshSourceProperty;
    SerializedProperty renderRootProperty;
    SerializedProperty doubleSidedSurfacesProperty;
    SerializedProperty triangleSampleStrideProperty;
    SerializedProperty surfaceRayColumnsProperty;
    SerializedProperty surfaceRayRowsProperty;
    SerializedProperty surfaceRayBoundsPaddingProperty;

    WindForce TargetWindForce => (WindForce)target;

    void OnEnable()
    {
        forceMultiplierProperty = serializedObject.FindProperty("forceMultiplier");
        allowVerticalForceProperty = serializedObject.FindProperty("allowVerticalForce");
        useApparentWindProperty = serializedObject.FindProperty("useApparentWind");
        applicationModeProperty = serializedObject.FindProperty("applicationMode");
        meshSourceProperty = serializedObject.FindProperty("meshSource");
        renderRootProperty = serializedObject.FindProperty("renderRoot");
        doubleSidedSurfacesProperty = serializedObject.FindProperty("doubleSidedSurfaces");
        triangleSampleStrideProperty = serializedObject.FindProperty("triangleSampleStride");
        surfaceRayColumnsProperty = serializedObject.FindProperty("surfaceRayColumns");
        surfaceRayRowsProperty = serializedObject.FindProperty("surfaceRayRows");
        surfaceRayBoundsPaddingProperty = serializedObject.FindProperty("surfaceRayBoundsPadding");
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        UpgradeLegacyPointSampleMode();

        Component samplingTarget = GetSamplingTarget();
        int unreadableStaticMeshCount = MeshPointPlacementUtility.CountUnreadableStaticMeshes(samplingTarget);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Wind", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(forceMultiplierProperty);
        EditorGUILayout.PropertyField(
            allowVerticalForceProperty,
            new GUIContent("Allow Vertical Force", "When disabled, computed wind forces are flattened onto the XZ plane so wind cannot add lift or downforce."));
        EditorGUILayout.PropertyField(
            useApparentWindProperty,
            new GUIContent("Use Apparent Wind", "When enabled, the rigidbody's own point velocity is subtracted from the wind field. This can create drag or lift as the boat moves."));

        InspectorWindApplicationMode applicationMode = GetInspectorApplicationMode();
        EditorGUI.BeginChangeCheck();
        applicationMode = (InspectorWindApplicationMode)EditorGUILayout.EnumPopup("Force Model", applicationMode);
        if (EditorGUI.EndChangeCheck())
        {
            ApplyInspectorApplicationMode(applicationMode);
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Mesh Source", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(meshSourceProperty);
        if ((WindForce.MeshSourceMode)meshSourceProperty.enumValueIndex == WindForce.MeshSourceMode.CustomMeshRoot)
        {
            EditorGUILayout.PropertyField(renderRootProperty, new GUIContent("Custom Mesh Root"));

            if (renderRootProperty.objectReferenceValue == null)
            {
                EditorGUILayout.HelpBox(
                    "Custom Mesh Root is empty, so sampling falls back to this object's render hierarchy until you assign a separate sail or mesh root.",
                    MessageType.Info);
            }
        }

        if (applicationMode == InspectorWindApplicationMode.SurfaceTriangles)
        {
            DrawTriangleModeInspector();
        }
        else
        {
            DrawSurfaceRaycastInspector();
        }

        if (unreadableStaticMeshCount > 0)
        {
            EditorGUILayout.HelpBox(
                "Some MeshFilter meshes on the selected sampling hierarchy have Read/Write disabled, so these wind modes will skip them. Enable Read/Write in the mesh import settings or use a readable/skinned mesh source.",
                MessageType.Warning);
        }

        if (!MeshPointPlacementUtility.HasPlaceableMesh(samplingTarget))
        {
            string missingMeshMessage = unreadableStaticMeshCount > 0
                ? "No readable MeshFilter or SkinnedMeshRenderer was found on the selected sampling hierarchy. Static meshes with Read/Write disabled cannot be used for these wind modes."
                : "No MeshFilter or SkinnedMeshRenderer was found on the selected sampling hierarchy.";
            EditorGUILayout.HelpBox(missingMeshMessage, MessageType.Warning);
        }

        EditorGUILayout.HelpBox(
            "This component needs an active WindField in the scene to apply forces. Waterline filtering is configured on the WindField.",
            MessageType.None);

        serializedObject.ApplyModifiedProperties();
    }

    void DrawTriangleModeInspector()
    {
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Surface Triangles", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Each sampled triangle uses its area, normal, average local wind, and wind-field filtering, then applies force at the triangle centroid for torque. Triangle Stride 1 uses every triangle, 2 uses every other triangle, and larger values trade detail for speed.",
            MessageType.Info);
        EditorGUILayout.PropertyField(doubleSidedSurfacesProperty, new GUIContent("Double Sided Surfaces"));
        EditorGUILayout.PropertyField(
            triangleSampleStrideProperty,
            new GUIContent("Triangle Stride", "1 samples every triangle. 2 samples every other triangle. Higher values are faster but coarser."));
    }

    void DrawSurfaceRaycastInspector()
    {
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Surface Raycast", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "A grid of rays is cast along the current wind direction against the selected mesh hierarchy, and each hit applies force at that hit point for torque. More rows and columns catch more detail but cost more.",
            MessageType.Info);
        EditorGUILayout.PropertyField(doubleSidedSurfacesProperty, new GUIContent("Double Sided Surfaces"));
        EditorGUILayout.PropertyField(surfaceRayColumnsProperty, new GUIContent("Ray Columns"));
        EditorGUILayout.PropertyField(surfaceRayRowsProperty, new GUIContent("Ray Rows"));
        EditorGUILayout.PropertyField(surfaceRayBoundsPaddingProperty, new GUIContent("Ray Bounds Padding"));
    }

    void UpgradeLegacyPointSampleMode()
    {
        if ((WindForce.WindApplicationMode)applicationModeProperty.enumValueIndex != WindForce.WindApplicationMode.PointSamples)
        {
            return;
        }

        applicationModeProperty.enumValueIndex = (int)WindForce.WindApplicationMode.SurfaceTriangles;
    }

    InspectorWindApplicationMode GetInspectorApplicationMode()
    {
        return applicationModeProperty.enumValueIndex == (int)WindForce.WindApplicationMode.SurfaceRaycast
            ? InspectorWindApplicationMode.SurfaceRaycast
            : InspectorWindApplicationMode.SurfaceTriangles;
    }

    void ApplyInspectorApplicationMode(InspectorWindApplicationMode mode)
    {
        applicationModeProperty.enumValueIndex = mode == InspectorWindApplicationMode.SurfaceRaycast
            ? (int)WindForce.WindApplicationMode.SurfaceRaycast
            : (int)WindForce.WindApplicationMode.SurfaceTriangles;
    }

    Component GetSamplingTarget()
    {
        return TargetWindForce.GetSamplingMeshRoot();
    }
}
