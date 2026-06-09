using Borodar.FarlandSkies.LowPoly;
using System;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Small scene-facing controller for the high-level voyage loop.
/// Attach this to a GameObject in the home scene and the ship editor scene,
/// then wire UI buttons directly to the public methods on this component.
/// </summary>
[DisallowMultipleComponent]
public class VoyageCycleController : MonoBehaviour
{
    const string DefaultTitleScreenObjectName = "Start Screen";
    const string DefaultHomeMenuObjectName = "Main menu";
    const string DefaultVoyageUiObjectName = "Voyage UI";
    const string DefaultDayOverDisplayObjectName = "Day Over Display";

    [Serializable]
    sealed class UITextBinding
    {
        [SerializeField] TMP_Text tmpText;
        [SerializeField] Text legacyText;

        public bool IsAssigned => tmpText != null || legacyText != null;

        public void SetText(string value)
        {
            if (tmpText != null)
            {
                tmpText.text = value;
            }

            if (legacyText != null)
            {
                legacyText.text = value;
            }
        }
    }

    enum TimeDisplayMode
    {
        NormalizedDayPercent,
        TwentyFourHourClock
    }

    enum VoyagePhase
    {
        Home,
        EditingShip,
        Sailing,
        VoyageComplete
    }

    struct VoyageState
    {
        public int dayNumber;
        public int gold;
        public int lastVoyageReward;
        public int lastVoyageBonusGold;
        public float lastVoyageMaxDistance;
        public int lastVoyageDestroyedObstacleCount;
        public int lastVoyageConfiguredObstacleCount;
        public int currentObjectiveIndex;
        public bool lastVoyageObjectiveCompleted;
        public float lastVoyageInitialDistanceToObjective;
        public float lastVoyageMinDistanceToObjective;
        public string lastVoyageObjectiveName;
        public bool needsInitialDistanceCapture;
        public float previousTimeOfDay;
        public VoyagePhase phase;
        public bool obstacleResetPending;
        public bool homePoseCaptured;
        public bool homeUiUnlocked;
        public Vector3 homeBoatPosition;
        public Quaternion homeBoatRotation;
    }

    struct BoatLockState
    {
        public Transform boatRoot;
        public Rigidbody body;
        public RigidbodyConstraints constraints;
        public ShipController shipController;
        public bool shipControllerWasEnabled;
        public bool captured;
    }

    static bool stateInitialized;
    static VoyageState state;

    [Header("Scenes")]
    [SerializeField] string homeSceneName = "SampleScene";
    [SerializeField] string shipEditorSceneName = "ShipBuilding";

    [Header("Starting Values")]
    [SerializeField, Min(1)] int startingDayNumber = 1;
    [SerializeField, Min(0)] int startingGold = 0;

    [Header("Day Cycle")]
    [SerializeField, Range(0f, 100f)] float homeTimeOfDay = 12f;
    [SerializeField, Range(0f, 100f)] float voyageStartTimeOfDay = 12f;
    [SerializeField, Range(0f, 100f)] float voyageEndTimeOfDay = 80f;
    [SerializeField, Min(1f)] float voyageDayLengthSeconds = 180f;
    [SerializeField] bool pauseCycleAtHome = true;
    [SerializeField] bool pauseCycleWhenVoyageEnds = true;
    [SerializeField] bool autoReturnHomeWhenVoyageEnds = true;

    [Header("Rewards")]
    [SerializeField, Min(0f)] float goldPerDistanceUnit = 1f;

    [Header("Boat")]
    [SerializeField] Transform boatSpawnPoint;
    [SerializeField] bool alignBoatToSpawnRotation = true;
    [SerializeField] bool lockBoatUntilSetSail = true;
    [SerializeField] bool restoreBoatHomePoseOnReturn = true;

    [Header("Canvas UI")]
    [SerializeField] GameObject titleScreenRoot;
    [SerializeField] GameObject homeMenuRoot;
    [SerializeField] GameObject voyageUiRoot;
    [SerializeField] GameObject dayOverDisplayRoot;
    [SerializeField] UITextBinding dayText = new UITextBinding();
    [SerializeField] string dayTextFormat = "Day {0}";
    [SerializeField] UITextBinding goldText = new UITextBinding();
    [SerializeField] string goldTextFormat = "{0}";
    [SerializeField] UITextBinding distanceText = new UITextBinding();
    [SerializeField] string distanceTextFormat = "Distance: {0}m";
    [SerializeField] UITextBinding timeText = new UITextBinding();
    [SerializeField] TimeDisplayMode timeDisplayMode = TimeDisplayMode.TwentyFourHourClock;
    [SerializeField] string normalizedTimeTextFormat = "{0:0}";
    [SerializeField] string clockTimeTextFormat = "{0}";

    [Header("Day Over UI")]
    [SerializeField] UITextBinding dayOverTitleText = new UITextBinding();
    [SerializeField] string dayOverTitleTextFormat = "Day {0} Over";
    [SerializeField] UITextBinding dayOverDistanceText = new UITextBinding();
    [SerializeField] string dayOverDistanceTextFormat = "Distance sailed: {0}m";
    [SerializeField] UITextBinding dayOverObstacleText = new UITextBinding();
    [SerializeField] string dayOverObstacleTextFormat = "Objective: {0}";
    [SerializeField] UITextBinding dayOverGoldText = new UITextBinding();
    [SerializeField] string dayOverGoldTextFormat = "Gold earned: {0}";

    [Header("Home Menu UI")]
    [SerializeField] UITextBinding homeMenuDayText = new UITextBinding();
    [SerializeField] string homeMenuDayTextFormat = "Day {0}";
    [SerializeField] UITextBinding homeMenuGoldText = new UITextBinding();
    [SerializeField] string homeMenuGoldTextFormat = "Gold: {0}";

    BoatLockState boatLockState;
    GameObject generatedDayOverDisplayRoot;
    TMP_Text generatedDayOverTitleLabel;
    TMP_Text generatedDayOverDistanceLabel;
    TMP_Text generatedDayOverObjectiveNameLabel;
    Image generatedDayOverProgressFill;
    TMP_Text generatedDayOverGoldLabel;
    TMP_Text generatedDayOverBonusGoldLabel;
    GameObject generatedHomeMenuStatsRoot;
    TMP_Text generatedHomeMenuDayLabel;
    TMP_Text generatedHomeMenuGoldLabel;

    public int DayNumber => state.dayNumber;
    public int Gold => state.gold;
    public int LastVoyageReward => state.lastVoyageReward;
    public float LastVoyageMaxDistance => state.lastVoyageMaxDistance;
    public float TimeOfDay => GetTimeOfDay();
    public bool VoyageInProgress => state.phase == VoyagePhase.Sailing;
    public bool VoyageHasEnded => state.phase == VoyagePhase.VoyageComplete;

    void Awake()
    {
        EnsureStateInitialized();
        RefreshBoundUi();
    }

    void OnEnable()
    {
        EnsureStateInitialized();
        SceneManager.sceneLoaded += HandleSceneLoaded;
        ApplyStateToCurrentScene();
        RefreshBoundUi();
    }

    void OnDisable()
    {
        SceneManager.sceneLoaded -= HandleSceneLoaded;
        ReleaseBoatMovementLock();
    }

    void OnValidate()
    {
        EnsureStateInitialized();
        RefreshBoundUi();
    }

    void Update()
    {
        if (!Application.isPlaying)
        {
            RefreshBoundUi();
            return;
        }

        TryCaptureHomePoseIfNeeded();
        UpdateVoyageProgress();
        SyncBoatStateForCurrentPhase();
        RefreshBoundUi();
    }

    /// <summary>
    /// Returns the current sky-cycle time in the 0..100 range.
    /// </summary>
    public float GetTimeOfDay()
    {
        if (TryGetCycleManager(out SkyboxCycleManager cycleManager))
        {
            return Mathf.Repeat(cycleManager.CycleProgress, 100f);
        }

        if (TryGetDayNightCycle(out SkyboxDayNightCycle dayNightCycle))
        {
            return Mathf.Repeat(dayNightCycle.TimeOfDay, 100f);
        }

        return 0f;
    }

    public int GetDayNumber()
    {
        return state.dayNumber;
    }

    public int GetGold()
    {
        return state.gold;
    }

    public string GetDayDisplayText()
    {
        return FormatDisplayText(dayTextFormat, state.dayNumber);
    }

    public string GetGoldDisplayText()
    {
        return FormatDisplayText(goldTextFormat, state.gold);
    }

    public float GetCurrentVoyageDistance()
    {
        if (!TryGetBoatRoot(out Transform boatRoot))
        {
            return 0f;
        }

        return GetDistanceFromVoyageReference(boatRoot.position);
    }

    public string GetDistanceDisplayText()
    {
        return FormatDisplayText(distanceTextFormat, Mathf.RoundToInt(GetCurrentVoyageDistance()));
    }

    public string GetTimeDisplayText()
    {
        float currentTimeOfDay = GetTimeOfDay();
        if (timeDisplayMode == TimeDisplayMode.NormalizedDayPercent)
        {
            return FormatDisplayText(normalizedTimeTextFormat, currentTimeOfDay);
        }

        return FormatDisplayText(clockTimeTextFormat, FormatTimeOfDayAsClock(currentTimeOfDay));
    }

    public int GetLastVoyageReward()
    {
        return state.lastVoyageReward;
    }

    public int GetLastVoyageBonusGold()
    {
        return state.lastVoyageBonusGold;
    }

    public float GetLastVoyageObjectiveProgress()
    {
        if (state.lastVoyageObjectiveCompleted)
        {
            return 1f;
        }

        float initial = state.lastVoyageInitialDistanceToObjective;
        if (initial <= 0.001f)
        {
            return 0f;
        }

        return Mathf.Clamp01(1f - state.lastVoyageMinDistanceToObjective / initial);
    }

    public string GetCurrentObjectiveName()
    {
        if (World.Instance != null && World.Instance.TryGetObjectiveDefinition(state.currentObjectiveIndex, out World.ObstacleTargetDefinition def))
        {
            return !string.IsNullOrWhiteSpace(def.displayName) ? def.displayName.Trim() : "Objective " + (state.currentObjectiveIndex + 1);
        }

        return string.Empty;
    }

    public float GetLastVoyageMaxDistance()
    {
        return state.lastVoyageMaxDistance;
    }

    public int GetLastVoyageDestroyedObstacleCount()
    {
        return state.lastVoyageDestroyedObstacleCount;
    }

    public int GetLastVoyageConfiguredObstacleCount()
    {
        return state.lastVoyageConfiguredObstacleCount;
    }

    public bool LastVoyageHitAnyObstacle()
    {
        return state.lastVoyageDestroyedObstacleCount > 0;
    }

    public string GetLastVoyageTitleDisplayText()
    {
        return FormatDisplayText(dayOverTitleTextFormat, state.dayNumber);
    }

    public string GetLastVoyageDistanceDisplayText()
    {
        return FormatDisplayText(dayOverDistanceTextFormat, Mathf.RoundToInt(state.lastVoyageMaxDistance));
    }

    public string GetLastVoyageObstacleDisplayText()
    {
        return FormatDisplayText(dayOverObstacleTextFormat, GetLastVoyageObstacleSummaryValue());
    }

    public string GetLastVoyageRewardDisplayText()
    {
        return FormatDisplayText(dayOverGoldTextFormat, state.lastVoyageReward);
    }

    public int GetConfiguredObstacleCount()
    {
        return World.Instance != null ? World.Instance.GetConfiguredObstacleCount() : 0;
    }

    public int GetRemainingObstacleCount()
    {
        return World.Instance != null ? World.Instance.GetRemainingObstacleCount() : 0;
    }

    public int GetDestroyedObstacleCount()
    {
        return World.Instance != null ? World.Instance.GetDestroyedObstacleCount() : 0;
    }

    public bool HasRemainingObstacles()
    {
        return World.Instance != null && World.Instance.HasRemainingObstacles();
    }

    public bool TryGetNearestObstacleTrackingInfo(out World.ObstacleTrackingInfo trackingInfo)
    {
        trackingInfo = default;
        return World.Instance != null && World.Instance.TryGetNearestObstacleTracking(out trackingInfo);
    }

    public bool TryGetBoatPlanarForward(out Vector3 planarForward)
    {
        if (!TryGetBoatRoot(out Transform boatRoot))
        {
            planarForward = Vector3.forward;
            return false;
        }

        ShipController shipController = boatRoot.GetComponent<ShipController>();
        if (shipController == null)
        {
            shipController = boatRoot.GetComponentInParent<ShipController>();
        }

        if (shipController != null)
        {
            planarForward = shipController.PlanarForward;
            return true;
        }

        planarForward = Vector3.ProjectOnPlane(boatRoot.right, Vector3.up);
        if (planarForward.sqrMagnitude <= 0.0001f)
        {
            planarForward = Vector3.ProjectOnPlane(boatRoot.forward, Vector3.up);
        }

        if (planarForward.sqrMagnitude <= 0.0001f)
        {
            planarForward = Vector3.forward;
            return true;
        }

        planarForward.Normalize();
        return true;
    }

    public float GetNearestObstacleDistance()
    {
        return TryGetNearestObstacleTrackingInfo(out World.ObstacleTrackingInfo trackingInfo)
            ? trackingInfo.planarDistance
            : 0f;
    }

    public float GetNearestObstacleSignedAngle()
    {
        return TryGetNearestObstacleTrackingInfo(out World.ObstacleTrackingInfo trackingInfo)
            ? trackingInfo.signedAngleFromReference
            : 0f;
    }

    public float GetVoyageStartTimeOfDay()
    {
        return voyageStartTimeOfDay;
    }

    public float GetVoyageEndTimeOfDay()
    {
        return voyageEndTimeOfDay;
    }

    public float GetVoyageDayLengthSeconds()
    {
        return voyageDayLengthSeconds;
    }

    /// <summary>
    /// Lets UI or other scene scripts change the sail start time.
    /// </summary>
    public void SetVoyageStartTimeOfDay(float timeOfDay)
    {
        voyageStartTimeOfDay = Mathf.Repeat(timeOfDay, 100f);
    }

    /// <summary>
    /// Lets UI or other scene scripts change the sail end time.
    /// </summary>
    public void SetVoyageEndTimeOfDay(float timeOfDay)
    {
        voyageEndTimeOfDay = Mathf.Repeat(timeOfDay, 100f);
    }

    /// <summary>
    /// Lets UI or other scene scripts change how long the sail day lasts in real seconds.
    /// </summary>
    public void SetVoyageDayLengthSeconds(float seconds)
    {
        voyageDayLengthSeconds = Mathf.Max(1f, seconds);
    }

    /// <summary>
    /// Convenience setter when the UI wants to change the whole voyage window at once.
    /// </summary>
    public void ConfigureVoyageDay(float startTimeOfDay, float endTimeOfDay, float lengthSeconds)
    {
        SetVoyageStartTimeOfDay(startTimeOfDay);
        SetVoyageEndTimeOfDay(endTimeOfDay);
        SetVoyageDayLengthSeconds(lengthSeconds);
    }

    /// <summary>
    /// Enters the home state and makes sure the main sailing scene is loaded.
    /// Useful for a title-screen Play button.
    /// </summary>
    public void PlayGame()
    {
        EnsureStateInitialized();
        state.homeUiUnlocked = true;
        EnterHomeState();

        if (!IsActiveScene(homeSceneName))
        {
            SceneManager.LoadScene(homeSceneName);
            return;
        }

        ApplyStateToCurrentScene();
    }

    /// <summary>
    /// Opens the ship editor scene.
    /// </summary>
    public void EditShip()
    {
        EnsureStateInitialized();
        state.homeUiUnlocked = true;
        state.phase = VoyagePhase.EditingShip;
        state.previousTimeOfDay = homeTimeOfDay;
        ApplyHomeTimeAndPause();
        SceneManager.LoadScene(shipEditorSceneName);
    }

    /// <summary>
    /// Saves the current ship through the existing builder scene pipeline.
    /// </summary>
    public void SaveShip()
    {
        EnsureStateInitialized();
        state.homeUiUnlocked = true;
        EnterHomeState();
        state.homePoseCaptured = false;

        ShipBuilder shipBuilder = FindFirstObjectByType<ShipBuilder>();
        if (shipBuilder != null)
        {
            shipBuilder.SwitchToSailScene();
            return;
        }

        ShipBuildController buildController = FindFirstObjectByType<ShipBuildController>();
        if (buildController != null)
        {
            buildController.SwitchToSailScene();
            return;
        }

        Debug.LogWarning("VoyageCycleController could not find ShipBuilder or ShipBuildController to save the ship.", this);
    }

    /// <summary>
    /// Starts the next voyage day.
    /// </summary>
    public void SetSail()
    {
        EnsureStateInitialized();

        state.homeUiUnlocked = true;
        state.phase = VoyagePhase.Sailing;
        state.obstacleResetPending = true;
        state.lastVoyageReward = 0;
        state.lastVoyageBonusGold = 0;
        state.lastVoyageMaxDistance = 0f;
        state.lastVoyageDestroyedObstacleCount = 0;
        state.lastVoyageConfiguredObstacleCount = 0;
        state.lastVoyageObjectiveCompleted = false;
        state.lastVoyageInitialDistanceToObjective = 0f;
        state.lastVoyageMinDistanceToObjective = 0f;
        state.needsInitialDistanceCapture = true;
        state.previousTimeOfDay = voyageStartTimeOfDay;

        PrepareBoatForVoyageStart();
        ApplyVoyageStartTimeAndUnpause(resetTimeOfDay: true);
        TryResetObstaclesForVoyage();
        TryCaptureHomePoseIfNeeded(forceCapture: true);
        ReleaseBoatMovementLock();
        ApplyUiState();
    }

    /// <summary>
    /// Returns to the home state after the day is over.
    /// If the voyage just ended, this advances the day counter for the next run.
    /// </summary>
    public void ReturnHome()
    {
        EnsureStateInitialized();

        bool completedDay = state.phase == VoyagePhase.VoyageComplete;
        if (completedDay)
        {
            state.dayNumber += 1;
        }

        state.homeUiUnlocked = true;
        EnterHomeState();

        if (restoreBoatHomePoseOnReturn)
        {
            RestoreHomeBoatPose();
        }

        if (!IsActiveScene(homeSceneName))
        {
            SceneManager.LoadScene(homeSceneName);
            return;
        }

        ApplyStateToCurrentScene();
    }

    void HandleSceneLoaded(Scene scene, LoadSceneMode loadMode)
    {
        ApplyStateToCurrentScene();
        RefreshBoundUi();
    }

    void ApplyStateToCurrentScene()
    {
        if (!Application.isPlaying)
        {
            RefreshBoundUi();
            return;
        }

        switch (state.phase)
        {
            case VoyagePhase.Home:
                ApplyHomeTimeAndPause();
                ApplyHomeBoatReadyState();
                break;

            case VoyagePhase.EditingShip:
                ApplyHomeTimeAndPause();
                ReleaseBoatMovementLock();
                break;

            case VoyagePhase.Sailing:
                ReleaseBoatMovementLock();
                ApplyVoyageStartTimeAndUnpause(resetTimeOfDay: false);
                TryResetObstaclesForVoyage();
                break;

            case VoyagePhase.VoyageComplete:
                ApplyVoyageEndTimeAndPause();
                ApplyVoyageCompleteBoatState();
                break;
        }

        ApplyUiState();
        RefreshBoundUi();
    }

    void UpdateVoyageProgress()
    {
        if (state.phase != VoyagePhase.Sailing)
        {
            return;
        }

        if (TryGetBoatRoot(out Transform boatRoot))
        {
            state.lastVoyageMaxDistance = Mathf.Max(
                state.lastVoyageMaxDistance,
                GetDistanceFromVoyageReference(boatRoot.position));
        }

        if (TryGetNearestObstacleTrackingInfo(out World.ObstacleTrackingInfo obstacleInfo))
        {
            if (state.needsInitialDistanceCapture)
            {
                state.lastVoyageInitialDistanceToObjective = obstacleInfo.planarDistance;
                state.lastVoyageMinDistanceToObjective = obstacleInfo.planarDistance;
                state.needsInitialDistanceCapture = false;
            }
            else
            {
                state.lastVoyageMinDistanceToObjective = Mathf.Min(
                    state.lastVoyageMinDistanceToObjective,
                    obstacleInfo.planarDistance);
            }
        }

        float currentTimeOfDay = GetTimeOfDay();
        if (HasCrossedTimeThreshold(state.previousTimeOfDay, currentTimeOfDay, voyageEndTimeOfDay))
        {
            CompleteVoyageDay();
            return;
        }

        state.previousTimeOfDay = currentTimeOfDay;
    }

    void CompleteVoyageDay()
    {
        if (state.phase == VoyagePhase.VoyageComplete)
        {
            return;
        }

        state.phase = VoyagePhase.VoyageComplete;

        // Capture the objective name before potentially advancing the index
        if (World.Instance != null && World.Instance.TryGetObjectiveDefinition(state.currentObjectiveIndex, out World.ObstacleTargetDefinition objectiveDef))
        {
            state.lastVoyageObjectiveName = !string.IsNullOrWhiteSpace(objectiveDef.displayName)
                ? objectiveDef.displayName.Trim()
                : "Objective " + (state.currentObjectiveIndex + 1);
        }
        else
        {
            state.lastVoyageObjectiveName = string.Empty;
        }

        state.lastVoyageDestroyedObstacleCount = GetDestroyedObstacleCount();
        state.lastVoyageConfiguredObstacleCount = GetConfiguredObstacleCount();
        bool objectiveCompleted = state.lastVoyageDestroyedObstacleCount > 0;
        state.lastVoyageObjectiveCompleted = objectiveCompleted;

        int distanceGold = Mathf.Max(0, Mathf.RoundToInt(state.lastVoyageMaxDistance * goldPerDistanceUnit));
        int bonusGold = 0;
        if (objectiveCompleted && World.Instance != null
            && World.Instance.TryGetObjectiveDefinition(state.currentObjectiveIndex, out World.ObstacleTargetDefinition bonusDef))
        {
            bonusGold = Mathf.Max(0, bonusDef.bonusGold);
        }
        state.lastVoyageBonusGold = bonusGold;
        state.lastVoyageReward = distanceGold + bonusGold;
        state.gold += state.lastVoyageReward;

        if (objectiveCompleted)
        {
            state.currentObjectiveIndex++;
        }
        state.previousTimeOfDay = voyageEndTimeOfDay;

        if (autoReturnHomeWhenVoyageEnds && !HasDayOverDisplayAvailable())
        {
            ReturnHome();
            return;
        }

        ApplyVoyageEndTimeAndPause();
        ApplyVoyageCompleteBoatState();
        ApplyUiState();
        RefreshBoundUi();
    }

    void EnterHomeState()
    {
        state.phase = VoyagePhase.Home;
        state.previousTimeOfDay = homeTimeOfDay;
        ApplyHomeTimeAndPause();
        RefreshBoundUi();
    }

    void EnsureStateInitialized()
    {
        if (stateInitialized)
        {
            return;
        }

        state.dayNumber = Mathf.Max(1, startingDayNumber);
        state.gold = Mathf.Max(0, startingGold);
        state.lastVoyageReward = 0;
        state.lastVoyageBonusGold = 0;
        state.lastVoyageMaxDistance = 0f;
        state.lastVoyageDestroyedObstacleCount = 0;
        state.lastVoyageConfiguredObstacleCount = 0;
        state.currentObjectiveIndex = 0;
        state.lastVoyageObjectiveCompleted = false;
        state.lastVoyageInitialDistanceToObjective = 0f;
        state.lastVoyageMinDistanceToObjective = 0f;
        state.lastVoyageObjectiveName = string.Empty;
        state.needsInitialDistanceCapture = false;
        state.previousTimeOfDay = homeTimeOfDay;
        state.phase = VoyagePhase.Home;
        state.obstacleResetPending = false;
        state.homePoseCaptured = false;
        state.homeUiUnlocked = false;
        state.homeBoatRotation = Quaternion.identity;
        stateInitialized = true;
        RefreshBoundUi();
    }

    /// <summary>
    /// Applies the paused home/editor lighting state.
    /// </summary>
    void ApplyHomeTimeAndPause()
    {
        SetTimeOfDay(homeTimeOfDay);
        PauseCycle(pauseCycleAtHome);
    }

    /// <summary>
    /// Applies the sailing day settings and optionally snaps to the configured start time.
    /// </summary>
    void ApplyVoyageStartTimeAndUnpause(bool resetTimeOfDay)
    {
        ConfigureVoyageCycleDuration();

        if (resetTimeOfDay)
        {
            SetTimeOfDay(voyageStartTimeOfDay);
        }

        PauseCycle(false);
    }

    void TryResetObstaclesForVoyage()
    {
        if (!state.obstacleResetPending || World.Instance == null)
        {
            return;
        }

        World.Instance.SetCurrentObjectiveIndex(state.currentObjectiveIndex);
        World.Instance.ResetObstacleTargets();
        state.obstacleResetPending = false;
    }

    /// <summary>
    /// Applies the paused end-of-day lighting state.
    /// </summary>
    void ApplyVoyageEndTimeAndPause()
    {
        SetTimeOfDay(voyageEndTimeOfDay);
        PauseCycle(pauseCycleWhenVoyageEnds);
    }

    void PauseCycle(bool shouldPause)
    {
        if (TryGetCycleManager(out SkyboxCycleManager cycleManager))
        {
            cycleManager.Paused = shouldPause;
        }
    }

    void ConfigureVoyageCycleDuration()
    {
        if (!TryGetCycleManager(out SkyboxCycleManager cycleManager))
        {
            return;
        }

        float cycleSegmentLength = GetForwardTimeOfDayDistance(voyageStartTimeOfDay, voyageEndTimeOfDay);
        if (cycleSegmentLength <= 0.0001f)
        {
            cycleSegmentLength = 100f;
        }

        cycleManager.CycleDuration = Mathf.Max(1f, voyageDayLengthSeconds * (100f / cycleSegmentLength));
    }

    void SetTimeOfDay(float timeOfDay)
    {
        float wrappedTime = Mathf.Repeat(timeOfDay, 100f);

        if (TryGetCycleManager(out SkyboxCycleManager cycleManager))
        {
            cycleManager.CycleProgress = wrappedTime;
        }

        if (TryGetDayNightCycle(out SkyboxDayNightCycle dayNightCycle))
        {
            dayNightCycle.TimeOfDay = wrappedTime;
        }
    }

    void TryCaptureHomePoseIfNeeded(bool forceCapture = false)
    {
        if (boatSpawnPoint != null)
        {
            state.homeBoatPosition = boatSpawnPoint.position;

            if (alignBoatToSpawnRotation)
            {
                state.homeBoatRotation = boatSpawnPoint.rotation;
            }
            else if (forceCapture && TryGetBoatRoot(out Transform spawnedBoatRoot))
            {
                state.homeBoatRotation = spawnedBoatRoot.rotation;
            }

            state.homePoseCaptured = true;
            return;
        }

        if (!forceCapture && state.homePoseCaptured)
        {
            return;
        }

        if (!TryGetBoatRoot(out Transform boatRoot))
        {
            return;
        }

        state.homeBoatPosition = boatRoot.position;
        state.homeBoatRotation = boatRoot.rotation;
        state.homePoseCaptured = true;
    }

    void RestoreHomeBoatPose()
    {
        if (!state.homePoseCaptured || !TryGetBoatRoot(out Transform boatRoot))
        {
            return;
        }

        boatRoot.position = state.homeBoatPosition;
        boatRoot.rotation = state.homeBoatRotation;

        Rigidbody body = boatRoot.GetComponent<Rigidbody>();
        if (body != null)
        {
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }
    }

    void SyncBoatStateForCurrentPhase()
    {
        if (state.phase == VoyagePhase.Home)
        {
            ApplyHomeBoatReadyState();
            return;
        }

        if (state.phase == VoyagePhase.VoyageComplete)
        {
            ApplyVoyageCompleteBoatState();
            return;
        }

        ReleaseBoatMovementLock();
    }

    void ApplyHomeBoatReadyState()
    {
        if (!TryGetBoatRoot(out Transform boatRoot))
        {
            return;
        }

        ApplyConfiguredBoatSpawnPose(boatRoot);
        TryCaptureHomePoseIfNeeded(forceCapture: boatSpawnPoint != null);

        if (lockBoatUntilSetSail)
        {
            LockBoatMovement(boatRoot);
        }
    }

    void ApplyVoyageCompleteBoatState()
    {
        if (restoreBoatHomePoseOnReturn)
        {
            RestoreHomeBoatPose();
        }

        if (lockBoatUntilSetSail && TryGetBoatRoot(out Transform boatRoot))
        {
            LockBoatMovement(boatRoot);
        }
    }

    void PrepareBoatForVoyageStart()
    {
        if (!TryGetBoatRoot(out Transform boatRoot))
        {
            return;
        }

        ApplyConfiguredBoatSpawnPose(boatRoot);
        ReleaseBoatMovementLock();
    }

    void ApplyConfiguredBoatSpawnPose(Transform boatRoot)
    {
        if (boatRoot == null || boatSpawnPoint == null)
        {
            return;
        }

        boatRoot.position = boatSpawnPoint.position;
        if (alignBoatToSpawnRotation)
        {
            boatRoot.rotation = boatSpawnPoint.rotation;
        }

        Rigidbody body = boatRoot.GetComponent<Rigidbody>();
        if (body != null)
        {
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }
    }

    void LockBoatMovement(Transform boatRoot)
    {
        if (boatRoot == null)
        {
            return;
        }

        if (boatLockState.captured && boatLockState.boatRoot != boatRoot)
        {
            ReleaseBoatMovementLock();
        }

        Rigidbody body = boatRoot.GetComponent<Rigidbody>();
        ShipController shipController = boatRoot.GetComponent<ShipController>();
        if (shipController == null)
        {
            shipController = boatRoot.GetComponentInParent<ShipController>();
        }

        if (!boatLockState.captured)
        {
            boatLockState.boatRoot = boatRoot;
            boatLockState.body = body;
            boatLockState.constraints = body != null ? body.constraints : RigidbodyConstraints.None;
            boatLockState.shipController = shipController;
            boatLockState.shipControllerWasEnabled = shipController != null && shipController.enabled;
            boatLockState.captured = true;
        }

        if (shipController != null && shipController.enabled)
        {
            shipController.enabled = false;
        }

        if (body == null)
        {
            return;
        }

        body.linearVelocity = Vector3.zero;
        body.angularVelocity = Vector3.zero;
        body.constraints = RigidbodyConstraints.FreezeAll;
        body.Sleep();
    }

    void ReleaseBoatMovementLock()
    {
        if (!boatLockState.captured)
        {
            return;
        }

        if (boatLockState.body != null)
        {
            boatLockState.body.constraints = boatLockState.constraints;
            boatLockState.body.WakeUp();
        }

        if (boatLockState.shipController != null)
        {
            boatLockState.shipController.enabled = boatLockState.shipControllerWasEnabled;
        }

        boatLockState = default;
    }

    bool TryGetBoatRoot(out Transform boatRoot)
    {
        if (World.Instance != null && World.Instance.BoatRoot != null)
        {
            boatRoot = World.Instance.BoatRoot;
            return true;
        }

        GameObject namedBoat = GameObject.Find("BoatParent");
        if (namedBoat != null)
        {
            boatRoot = namedBoat.transform;
            return true;
        }

        ShipController shipController = FindFirstObjectByType<ShipController>();
        if (shipController != null)
        {
            boatRoot = shipController.transform;
            return true;
        }

        boatRoot = null;
        return false;
    }

    static bool TryGetCycleManager(out SkyboxCycleManager cycleManager)
    {
        cycleManager = SkyboxCycleManager.Instance;
        return cycleManager != null;
    }

    static bool TryGetDayNightCycle(out SkyboxDayNightCycle dayNightCycle)
    {
        dayNightCycle = SkyboxDayNightCycle.Instance;
        return dayNightCycle != null;
    }

    void ApplyUiState()
    {
        if (!IsActiveScene(homeSceneName))
        {
            SetGeneratedDayOverDisplayActive(false);
            return;
        }

        ResolveUiRoots();
        EnsureGeneratedDayOverDisplay();

        bool hasDayOverDisplay = HasDayOverDisplayAvailable();
        bool showDayOverUi = state.phase == VoyagePhase.VoyageComplete && hasDayOverDisplay;
        bool showVoyageUi = state.phase == VoyagePhase.Sailing
            || (state.phase == VoyagePhase.VoyageComplete && !showDayOverUi);
        bool showHomeMenu = state.homeUiUnlocked
            && (state.phase == VoyagePhase.Home || state.phase == VoyagePhase.EditingShip);
        bool showTitleScreen = !state.homeUiUnlocked && state.phase == VoyagePhase.Home;

        SetActiveIfAssigned(voyageUiRoot, showVoyageUi);
        SetActiveIfAssigned(dayOverDisplayRoot, showDayOverUi);
        SetGeneratedDayOverDisplayActive(showDayOverUi);
        SetActiveIfAssigned(homeMenuRoot, showHomeMenu);
        SetActiveIfAssigned(titleScreenRoot, showTitleScreen);
        EnsureGeneratedHomeMenuStats(showHomeMenu || showTitleScreen);
    }

    void ResolveUiRoots()
    {
        if (titleScreenRoot == null)
        {
            GameObject foundTitleScreen = FindSceneObjectByName(DefaultTitleScreenObjectName);
            if (foundTitleScreen != null)
            {
                titleScreenRoot = foundTitleScreen;
            }
        }

        if (homeMenuRoot == null)
        {
            GameObject foundHomeMenu = FindSceneObjectByName(DefaultHomeMenuObjectName);
            if (foundHomeMenu != null)
            {
                homeMenuRoot = foundHomeMenu;
            }
        }

        if (voyageUiRoot == null)
        {
            GameObject foundVoyageUi = FindSceneObjectByName(DefaultVoyageUiObjectName);
            if (foundVoyageUi != null)
            {
                voyageUiRoot = foundVoyageUi;
            }
        }

        if (dayOverDisplayRoot == null)
        {
            GameObject foundDayOverDisplay = FindSceneObjectByName(DefaultDayOverDisplayObjectName);
            if (foundDayOverDisplay != null)
            {
                dayOverDisplayRoot = foundDayOverDisplay;
            }
        }
    }

    GameObject FindSceneObjectByName(string objectName)
    {
        if (string.IsNullOrEmpty(objectName))
        {
            return null;
        }

        Scene activeScene = SceneManager.GetActiveScene();
        if (!activeScene.IsValid())
        {
            return null;
        }

        GameObject[] rootObjects = activeScene.GetRootGameObjects();
        for (int i = 0; i < rootObjects.Length; i++)
        {
            Transform foundTransform = FindChildByName(rootObjects[i].transform, objectName);
            if (foundTransform != null)
            {
                return foundTransform.gameObject;
            }
        }

        return null;
    }

    static Transform FindChildByName(Transform current, string objectName)
    {
        if (current == null)
        {
            return null;
        }

        if (current.name == objectName)
        {
            return current;
        }

        for (int i = 0; i < current.childCount; i++)
        {
            Transform foundChild = FindChildByName(current.GetChild(i), objectName);
            if (foundChild != null)
            {
                return foundChild;
            }
        }

        return null;
    }

    bool HasDayOverDisplayAvailable()
    {
        return dayOverDisplayRoot != null
            || dayOverTitleText.IsAssigned
            || dayOverDistanceText.IsAssigned
            || dayOverObstacleText.IsAssigned
            || dayOverGoldText.IsAssigned
            || (ShouldUseGeneratedDayOverDisplay() && ResolveUiCanvasRoot() != null);
    }

    bool ShouldUseGeneratedDayOverDisplay()
    {
        return !dayOverTitleText.IsAssigned
            && !dayOverDistanceText.IsAssigned
            && !dayOverObstacleText.IsAssigned
            && !dayOverGoldText.IsAssigned
            && (dayOverDisplayRoot == null || dayOverDisplayRoot.transform.childCount == 0);
    }

    RectTransform ResolveUiCanvasRoot()
    {
        Canvas canvas = null;

        if (dayOverDisplayRoot != null)
        {
            canvas = dayOverDisplayRoot.GetComponentInParent<Canvas>();
        }

        if (canvas == null && voyageUiRoot != null)
        {
            canvas = voyageUiRoot.GetComponentInParent<Canvas>();
        }

        if (canvas == null && homeMenuRoot != null)
        {
            canvas = homeMenuRoot.GetComponentInParent<Canvas>();
        }

        if (canvas == null && titleScreenRoot != null)
        {
            canvas = titleScreenRoot.GetComponentInParent<Canvas>();
        }

        if (canvas == null)
        {
            canvas = FindFirstObjectByType<Canvas>();
        }

        return canvas != null ? canvas.transform as RectTransform : null;
    }

    void EnsureGeneratedDayOverDisplay()
    {
        if (!Application.isPlaying)
        {
            return;
        }

        if (!ShouldUseGeneratedDayOverDisplay())
        {
            SetGeneratedDayOverDisplayActive(false);
            return;
        }

        RectTransform canvasRoot = ResolveUiCanvasRoot();
        if (canvasRoot == null)
        {
            return;
        }

        if (generatedDayOverDisplayRoot != null
            && generatedDayOverDisplayRoot.transform.parent != canvasRoot)
        {
            Destroy(generatedDayOverDisplayRoot);
            generatedDayOverDisplayRoot = null;
            generatedDayOverTitleLabel = null;
            generatedDayOverDistanceLabel = null;
            generatedDayOverObjectiveNameLabel = null;
            generatedDayOverProgressFill = null;
            generatedDayOverGoldLabel = null;
            generatedDayOverBonusGoldLabel = null;
        }

        if (generatedDayOverDisplayRoot != null)
        {
            RefreshGeneratedDayOverDisplay();
            return;
        }

        GameObject overlayRoot = new GameObject("Generated Day Over Display", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        overlayRoot.transform.SetParent(canvasRoot, false);

        RectTransform overlayRect = overlayRoot.GetComponent<RectTransform>();
        overlayRect.anchorMin = Vector2.zero;
        overlayRect.anchorMax = Vector2.one;
        overlayRect.offsetMin = Vector2.zero;
        overlayRect.offsetMax = Vector2.zero;

        Image overlayBackground = overlayRoot.GetComponent<Image>();
        overlayBackground.color = new Color(0f, 0f, 0f, 0.55f);
        overlayBackground.raycastTarget = true;

        RectTransform panelRect = CreateDayOverUiRect("Panel", overlayRect, new Vector2(520f, 320f), Vector2.zero);
        Image panelBackground = panelRect.gameObject.AddComponent<Image>();
        panelBackground.color = new Color(0.08f, 0.1f, 0.14f, 0.95f);

        generatedDayOverTitleLabel = CreateDayOverLabel("Title", panelRect, new Vector2(0f, 110f), 36f);
        generatedDayOverDistanceLabel = CreateDayOverLabel("Distance", panelRect, new Vector2(0f, 54f), 22f);
        generatedDayOverObjectiveNameLabel = CreateDayOverLabel("ObjectiveName", panelRect, new Vector2(0f, 14f), 20f);

        // Progress bar
        RectTransform barBgRect = CreateDayOverUiRect("ProgressBarBg", panelRect, new Vector2(380f, 18f), new Vector2(0f, -14f));
        Image barBgImage = barBgRect.gameObject.AddComponent<Image>();
        barBgImage.color = new Color(0.18f, 0.22f, 0.3f, 1f);
        barBgImage.raycastTarget = false;

        RectTransform barFillRect = CreateDayOverUiRect("Fill", barBgRect, Vector2.zero, Vector2.zero);
        barFillRect.anchorMin = Vector2.zero;
        barFillRect.anchorMax = Vector2.one;
        barFillRect.offsetMin = Vector2.zero;
        barFillRect.offsetMax = Vector2.zero;
        generatedDayOverProgressFill = barFillRect.gameObject.AddComponent<Image>();
        generatedDayOverProgressFill.type = Image.Type.Filled;
        generatedDayOverProgressFill.fillMethod = Image.FillMethod.Horizontal;
        generatedDayOverProgressFill.fillOrigin = (int)Image.OriginHorizontal.Left;
        generatedDayOverProgressFill.raycastTarget = false;

        generatedDayOverGoldLabel = CreateDayOverLabel("Gold", panelRect, new Vector2(0f, -50f), 22f);
        generatedDayOverBonusGoldLabel = CreateDayOverLabel("BonusGold", panelRect, new Vector2(0f, -78f), 18f);

        CreateGeneratedDayOverContinueButton(panelRect);

        generatedDayOverDisplayRoot = overlayRoot;
        RefreshGeneratedDayOverDisplay();
    }

    RectTransform CreateDayOverUiRect(string objectName, Transform parent, Vector2 size, Vector2 anchoredPosition)
    {
        GameObject child = new GameObject(objectName, typeof(RectTransform), typeof(CanvasRenderer));
        child.transform.SetParent(parent, false);

        RectTransform rectTransform = child.GetComponent<RectTransform>();
        rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
        rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        rectTransform.pivot = new Vector2(0.5f, 0.5f);
        rectTransform.sizeDelta = size;
        rectTransform.anchoredPosition = anchoredPosition;
        return rectTransform;
    }

    TMP_Text CreateDayOverLabel(string objectName, Transform parent, Vector2 anchoredPosition, float fontSize)
    {
        RectTransform rectTransform = CreateDayOverUiRect(objectName, parent, new Vector2(440f, 40f), anchoredPosition);
        TextMeshProUGUI text = rectTransform.gameObject.AddComponent<TextMeshProUGUI>();
        text.alignment = TextAlignmentOptions.Center;
        text.fontSize = fontSize;
        text.color = Color.white;
        text.enableWordWrapping = false;
        text.raycastTarget = false;
        if (TMP_Settings.defaultFontAsset != null)
        {
            text.font = TMP_Settings.defaultFontAsset;
        }

        return text;
    }

    void CreateGeneratedDayOverContinueButton(Transform parent)
    {
        RectTransform buttonRect = CreateDayOverUiRect("ContinueButton", parent, new Vector2(180f, 46f), new Vector2(0f, -112f));
        Image buttonImage = buttonRect.gameObject.AddComponent<Image>();
        buttonImage.color = new Color(0.92f, 0.95f, 1f, 0.95f);

        Button button = buttonRect.gameObject.AddComponent<Button>();
        button.targetGraphic = buttonImage;
        button.onClick.AddListener(ReturnHome);

        TMP_Text label = CreateDayOverLabel("Label", buttonRect, Vector2.zero, 24f);
        label.rectTransform.sizeDelta = new Vector2(160f, 32f);
        label.text = "Continue";
        label.color = new Color(0.14f, 0.16f, 0.2f, 1f);
    }

    void RefreshGeneratedDayOverDisplay()
    {
        if (generatedDayOverTitleLabel != null)
        {
            generatedDayOverTitleLabel.text = GetLastVoyageTitleDisplayText();
        }

        if (generatedDayOverDistanceLabel != null)
        {
            generatedDayOverDistanceLabel.text = GetLastVoyageDistanceDisplayText();
        }

        if (generatedDayOverObjectiveNameLabel != null)
        {
            generatedDayOverObjectiveNameLabel.text = GetLastVoyageObstacleDisplayText();
        }

        if (generatedDayOverProgressFill != null)
        {
            float progress = GetLastVoyageObjectiveProgress();
            generatedDayOverProgressFill.fillAmount = progress;
            generatedDayOverProgressFill.color = state.lastVoyageObjectiveCompleted
                ? new Color(0.2f, 0.85f, 0.35f, 1f)
                : new Color(0.35f, 0.55f, 0.9f, 1f);
        }

        if (generatedDayOverGoldLabel != null)
        {
            generatedDayOverGoldLabel.text = GetLastVoyageRewardDisplayText();
        }

        if (generatedDayOverBonusGoldLabel != null)
        {
            bool hasBonus = state.lastVoyageBonusGold > 0;
            if (generatedDayOverBonusGoldLabel.gameObject.activeSelf != hasBonus)
            {
                generatedDayOverBonusGoldLabel.gameObject.SetActive(hasBonus);
            }

            if (hasBonus)
            {
                generatedDayOverBonusGoldLabel.text = "Objective bonus: +" + state.lastVoyageBonusGold + " gold";
                generatedDayOverBonusGoldLabel.color = new Color(1f, 0.85f, 0.3f, 1f);
            }
        }
    }

    void SetGeneratedDayOverDisplayActive(bool isActive)
    {
        if (generatedDayOverDisplayRoot != null && generatedDayOverDisplayRoot.activeSelf != isActive)
        {
            generatedDayOverDisplayRoot.SetActive(isActive);
        }
    }

    void EnsureGeneratedHomeMenuStats(bool show)
    {
        if (!Application.isPlaying)
        {
            return;
        }

        if (homeMenuDayText.IsAssigned || homeMenuGoldText.IsAssigned)
        {
            SetGeneratedHomeMenuStatsActive(false);
            return;
        }

        RectTransform canvasRoot = ResolveUiCanvasRoot();
        if (canvasRoot == null)
        {
            SetGeneratedHomeMenuStatsActive(false);
            return;
        }

        if (generatedHomeMenuStatsRoot == null)
        {
            GameObject statsRoot = new GameObject("Generated Home Menu Stats", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            statsRoot.transform.SetParent(canvasRoot, false);

            RectTransform statsRect = statsRoot.GetComponent<RectTransform>();
            statsRect.anchorMin = new Vector2(0f, 1f);
            statsRect.anchorMax = new Vector2(1f, 1f);
            statsRect.pivot = new Vector2(0.5f, 1f);
            statsRect.sizeDelta = new Vector2(0f, 40f);
            statsRect.anchoredPosition = Vector2.zero;

            Image statsBg = statsRoot.GetComponent<Image>();
            statsBg.color = new Color(0f, 0f, 0f, 0.5f);
            statsBg.raycastTarget = false;

            generatedHomeMenuDayLabel = CreateHomeStatLabel("DayLabel", statsRoot.transform,
                new Vector2(0f, 0f), new Vector2(0.5f, 1f));
            generatedHomeMenuDayLabel.alignment = TextAlignmentOptions.Left;

            generatedHomeMenuGoldLabel = CreateHomeStatLabel("GoldLabel", statsRoot.transform,
                new Vector2(0.5f, 0f), new Vector2(1f, 1f));
            generatedHomeMenuGoldLabel.alignment = TextAlignmentOptions.Right;

            generatedHomeMenuStatsRoot = statsRoot;
        }

        SetGeneratedHomeMenuStatsActive(show);
        RefreshGeneratedHomeMenuStats();
    }

    TMP_Text CreateHomeStatLabel(string objectName, Transform parent, Vector2 anchorMin, Vector2 anchorMax)
    {
        GameObject obj = new GameObject(objectName, typeof(RectTransform), typeof(CanvasRenderer));
        obj.transform.SetParent(parent, false);

        RectTransform rect = obj.GetComponent<RectTransform>();
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.offsetMin = new Vector2(10f, 4f);
        rect.offsetMax = new Vector2(-10f, -4f);
        rect.pivot = new Vector2(0.5f, 0.5f);

        TextMeshProUGUI text = obj.AddComponent<TextMeshProUGUI>();
        text.fontSize = 20f;
        text.color = Color.white;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.raycastTarget = false;
        if (TMP_Settings.defaultFontAsset != null)
        {
            text.font = TMP_Settings.defaultFontAsset;
        }

        return text;
    }

    void SetGeneratedHomeMenuStatsActive(bool active)
    {
        if (generatedHomeMenuStatsRoot != null && generatedHomeMenuStatsRoot.activeSelf != active)
        {
            generatedHomeMenuStatsRoot.SetActive(active);
        }
    }

    void RefreshGeneratedHomeMenuStats()
    {
        if (generatedHomeMenuDayLabel != null)
        {
            generatedHomeMenuDayLabel.text = FormatDisplayText(homeMenuDayTextFormat, state.dayNumber);
        }

        if (generatedHomeMenuGoldLabel != null)
        {
            generatedHomeMenuGoldLabel.text = FormatDisplayText(homeMenuGoldTextFormat, state.gold);
        }
    }

    static void SetActiveIfAssigned(GameObject target, bool isActive)
    {
        if (target != null && target.activeSelf != isActive)
        {
            target.SetActive(isActive);
        }
    }

    float GetDistanceFromVoyageReference(Vector3 worldPosition)
    {
        Vector3 referencePosition = GetVoyageDistanceReferencePosition();
        return GetPlanarDistance(worldPosition, referencePosition);
    }

    Vector3 GetVoyageDistanceReferencePosition()
    {
        if (boatSpawnPoint != null)
        {
            return boatSpawnPoint.position;
        }

        if (state.homePoseCaptured)
        {
            return state.homeBoatPosition;
        }

        return Vector3.zero;
    }

    static float GetPlanarDistance(Vector3 a, Vector3 b)
    {
        return new Vector2(a.x - b.x, a.z - b.z).magnitude;
    }

    static bool HasCrossedTimeThreshold(float previousTimeOfDay, float currentTimeOfDay, float threshold)
    {
        float wrappedThreshold = Mathf.Repeat(threshold, 100f);
        float previous = Mathf.Repeat(previousTimeOfDay, 100f);
        float current = Mathf.Repeat(currentTimeOfDay, 100f);

        if (Mathf.Approximately(previous, current))
        {
            return false;
        }

        if (previous < current)
        {
            return previous < wrappedThreshold && current >= wrappedThreshold;
        }

        return previous < wrappedThreshold || current >= wrappedThreshold;
    }

    static float GetForwardTimeOfDayDistance(float startTimeOfDay, float endTimeOfDay)
    {
        return Mathf.Repeat(endTimeOfDay - startTimeOfDay, 100f);
    }

    void RefreshBoundUi()
    {
        if (dayText.IsAssigned)
        {
            dayText.SetText(GetDayDisplayText());
        }

        if (goldText.IsAssigned)
        {
            goldText.SetText(GetGoldDisplayText());
        }

        if (distanceText.IsAssigned)
        {
            distanceText.SetText(GetDistanceDisplayText());
        }

        if (timeText.IsAssigned)
        {
            timeText.SetText(GetTimeDisplayText());
        }

        if (dayOverTitleText.IsAssigned)
        {
            dayOverTitleText.SetText(GetLastVoyageTitleDisplayText());
        }

        if (dayOverDistanceText.IsAssigned)
        {
            dayOverDistanceText.SetText(GetLastVoyageDistanceDisplayText());
        }

        if (dayOverObstacleText.IsAssigned)
        {
            dayOverObstacleText.SetText(GetLastVoyageObstacleDisplayText());
        }

        if (dayOverGoldText.IsAssigned)
        {
            dayOverGoldText.SetText(GetLastVoyageRewardDisplayText());
        }

        if (homeMenuDayText.IsAssigned)
        {
            homeMenuDayText.SetText(FormatDisplayText(homeMenuDayTextFormat, state.dayNumber));
        }

        if (homeMenuGoldText.IsAssigned)
        {
            homeMenuGoldText.SetText(FormatDisplayText(homeMenuGoldTextFormat, state.gold));
        }

        RefreshGeneratedDayOverDisplay();
        RefreshGeneratedHomeMenuStats();
    }

    string GetLastVoyageObstacleSummaryValue()
    {
        if (string.IsNullOrEmpty(state.lastVoyageObjectiveName))
        {
            return state.lastVoyageObjectiveCompleted ? "Completed" : "Not reached";
        }

        return state.lastVoyageObjectiveName + (state.lastVoyageObjectiveCompleted ? " — Completed!" : "");
    }

    static string FormatDisplayText(string format, object value)
    {
        if (string.IsNullOrEmpty(format))
        {
            return value != null ? value.ToString() : string.Empty;
        }

        try
        {
            return string.Format(format, value);
        }
        catch (FormatException)
        {
            return value != null ? value.ToString() : string.Empty;
        }
    }

    static string FormatTimeOfDayAsClock(float timeOfDay)
    {
        float wrappedTime = Mathf.Repeat(timeOfDay, 100f);
        float totalHours = wrappedTime * 0.24f;
        int hours = Mathf.FloorToInt(totalHours);
        int minutes = Mathf.FloorToInt((totalHours - hours) * 60f);

        if (minutes >= 60)
        {
            minutes = 0;
            hours += 1;
        }

        hours %= 24;
        return hours.ToString("00") + ":" + minutes.ToString("00");
    }

    bool IsActiveScene(string sceneName)
    {
        return SceneManager.GetActiveScene().name == sceneName;
    }
}
