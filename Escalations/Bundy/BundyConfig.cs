using BepInEx.Configuration;
using Y4NGZCompany.Core;

namespace Y4NGZCompany.Escalations.Bundy
{
    // #694: Bundy's tuning surface was retired from the player-facing config. The
    // handful of settings players actually reach for live in the "The Stranger"
    // section; everything else is a FixedSetting holding what was the bind default,
    // so the runtime code and its .Value call sites are unchanged.
    public class BundyConfig
    {
        public FixedSetting<int> ChainsawSpawnWeight;
        public FixedSetting<bool> DebugLogs;
        public FixedSetting<bool> RuntimePerformanceTiming;
        public FixedSetting<float> RuntimePerformanceReportSeconds;
        public FixedSetting<float> RuntimePerformanceSlowCallMilliseconds;
        // Job 1: verbosity axis + per-workstream category reopeners, both under the DebugLogs master.
        public FixedSetting<int> LogVerbosity;
        public FixedSetting<bool> LogPerception;
        public FixedSetting<bool> LogMovement;
        public FixedSetting<bool> LogCombat;
        public FixedSetting<bool> LogAnim;
        public FixedSetting<bool> LogAudio;
        public FixedSetting<bool> LogSiege;
        public FixedSetting<bool> ChainsawStaticVisualTestMode;
        public FixedSetting<bool> ChainsawPinAccuRigHipTranslation;
        public FixedSetting<bool> ChainsawAttackGripDiagnostics;
        public FixedSetting<bool> ChainsawForceDisableAttackHandIK;
        public FixedSetting<bool> ChainsawDisableGenericGripSolver;
        public FixedSetting<bool> ChainsawMaterialDiagnostics;
        public FixedSetting<float> ChainsawBodySkinTintR;
        public FixedSetting<float> ChainsawBodySkinTintG;
        public FixedSetting<float> ChainsawBodySkinTintB;
        public FixedSetting<float> ChainsawHairTintR;
        public FixedSetting<float> ChainsawHairTintG;
        public FixedSetting<float> ChainsawHairTintB;
        public FixedSetting<float> ChainsawArmElbowsOutDegrees;
        public FixedSetting<float> ChainsawClavicleProtractLeftDegrees;
        public FixedSetting<float> ChainsawClavicleProtractRightDegrees;
        public FixedSetting<bool> ChainsawLockLocomotionDirection;
        public FixedSetting<bool> ChainsawVelocityGatedIdleDemotion;
        public FixedSetting<bool> ChainsawSquaredLocomotionClips;
        public FixedSetting<bool> ChainsawFacingYawOffsetEnabled;
        public FixedSetting<bool> ChainsawFacingYawOffsetAtSpine;
        public FixedSetting<float> ChainsawFacingYawOffsetDegrees;
        public FixedSetting<float> ChainsawFacingYawOffsetIdleDegrees;
        public FixedSetting<float> ChainsawFacingYawOffsetSmoothingSeconds;
        public FixedSetting<bool> ChainsawFootGroundingEnabled;
        public FixedSetting<bool> ChainsawFootIKEnabled;
        public FixedSetting<bool> ChainsawFootIKSlopeAlign;
        public FixedSetting<float> ChainsawFootIKMaxLift;
        public FixedSetting<bool> ChainsawHeadAimEnabled;
        public FixedSetting<float> ChainsawHeadAimYawClampDegrees;
        public FixedSetting<float> ChainsawHeadAimPitchClampDegrees;
        public FixedSetting<float> ChainsawHeadAimSmoothingSeconds;
        public FixedSetting<bool> ChainsawMaterialLowSpecTest;
        public FixedSetting<bool> ChainsawMaterialNormalOffTest;
        public FixedSetting<bool> ChainsawMaterialMatteSkinTest;
        public FixedSetting<bool> ChainsawMaterialHeadMatchTest;
        public ConfigEntry<float> ChainsawWalkSpeed;
        public ConfigEntry<float> ChainsawRunSpeed;
        // Issue #205: the overhead sprint.
        public ConfigEntry<float> ChainsawSprintSpeed;
        public FixedSetting<float> ChainsawSprintMinDistance;
        public FixedSetting<float> ChainsawSprintNoContactSeconds;
        public ConfigEntry<float> ChainsawSprintChance;
        public FixedSetting<float> ChainsawSprintRollIntervalSeconds;
        public FixedSetting<float> ChainsawSprintCooldownSeconds;
        public FixedSetting<float> ChainsawSprintMaxSeconds;
        public FixedSetting<bool> ChainsawSprintAfterKnockdownTaunt;
        public FixedSetting<bool> ChainsawSprintIntroSpeedRamp;
        public FixedSetting<bool> ChainsawForceSprint;
        public FixedSetting<float> ChainsawVisualYawCorrection;
        public FixedSetting<float> ChainsawRunVisualYawCorrection;
        public FixedSetting<float> ChainsawRunVisualRollCorrection;
        public FixedSetting<bool> ChainsawRumbleEnabled;
        public FixedSetting<float> ChainsawRumbleFrequencyHz;
        public FixedSetting<float> ChainsawRumbleAmplitudeDegrees;
        public FixedSetting<float> ChainsawRumbleRevGain;
        public FixedSetting<float> ChainsawAttackStartRange;
        public FixedSetting<float> ChainsawLungeSpeed;
        public FixedSetting<float> ChainsawLungeFacingMaxSpeed;
        public FixedSetting<float> ChainsawLungeBladeStandoff;
        public FixedSetting<float> ChainsawLungeCommitFraction;
        public FixedSetting<float> ChainsawLungeWindupHoldSeconds;
        public FixedSetting<float> ChainsawChainAttackBlendSeconds;
        public FixedSetting<float> ChainsawChainAttackEntryOffsetFraction;
        public FixedSetting<float> ChainsawChainAttackHandoffFraction;
        public FixedSetting<bool> CombatManeuverAttackGuard;
        public FixedSetting<float> CombatManeuverAttackDwellSeconds;
        public FixedSetting<float> CombatManeuverMinWindowSeconds;
        public FixedSetting<float> CombatManeuverArcStepDegrees;
        public FixedSetting<float> CombatManeuverArrivalHoldSeconds;
        public FixedSetting<float> ChainsawAttackVisualExitBlendSeconds;
        public FixedSetting<float> ChainsawAttackVisualExitSwingThreshold;
        public FixedSetting<float> ChainsawAttackVisualExitHeavyThreshold;
        public FixedSetting<float> RunThreshold;
        public FixedSetting<float> StopDistance;
        public FixedSetting<float> StrangerPingIntervalSeconds;
        public FixedSetting<float> PingPositionNoiseMeters;
        public FixedSetting<float> SearchWindowSeconds;
        public FixedSetting<float> HearingRadiusMeters;
        public FixedSetting<float> SightMemoryGraceSeconds;
        public FixedSetting<float> StrangerSightProximityMeters;
        public FixedSetting<float> PursuitMemorySeconds;
        public FixedSetting<bool> HuntDwellEnabled;
        public FixedSetting<float> NoiseContactHuntDwellSeconds;
        public FixedSetting<bool> WorldNoiseBusEnabled;
        public FixedSetting<float> WorldNoiseRadiusScale;
        public FixedSetting<float> WorldNoiseLiveConfidenceThreshold;
        public FixedSetting<float> WorldNoisePositionNoiseMeters;
        public FixedSetting<float> WorldNoiseAttributionRadiusMeters;
        public FixedSetting<float> WorldNoiseEventCooldownSeconds;
        public FixedSetting<bool> CrossBoundaryPathSuppressionEnabled;
        public FixedSetting<float> CrossingRouteCacheSeconds;
        public FixedSetting<bool> LongRangeAsyncReachabilityEnabled;
        public FixedSetting<bool> SmartBoundaryLinkGateEnabled;
        // Job 5a drop-down follow is core pursuit behaviour, not a feature toggle: the old
        // DropDownFollowEnabled key was retired when the gate was removed (stale entries are
        // ignored). The re-tuned policy gates follow the never-re-defaulted-always-renamed rule:
        // PathDetourRatio -> DetourRatio, MinHeightMeters -> MinDropMeters,
        // CooldownSeconds -> MinIntervalSeconds.
        public FixedSetting<bool> DropDownFollowForceTrigger;
        public FixedSetting<float> DropDownFollowDetourRatio;
        public FixedSetting<float> DropDownFollowMinDetourMeters;
        public FixedSetting<float> DropDownFollowMinDropMeters;
        public FixedSetting<float> DropDownFollowMaxHeightMeters;
        public FixedSetting<float> DropDownFollowMinIntervalSeconds;
        public FixedSetting<float> DropDownFollowGravity;
        public FixedSetting<float> DropDownFollowLaunchUpSpeed;
        public FixedSetting<float> DropDownFollowMaxHorizontalSpeed;
        public FixedSetting<float> DropDownFollowMaxTraversalSeconds;
        public FixedSetting<bool> ExitLurkEnabled;
        public FixedSetting<float> ExitLurkMaxPathMeters;
        public FixedSetting<float> ExitLurkMaxTargetDistanceMeters;
        public FixedSetting<float> ExitLurkAbandonDistanceFactor;
        public FixedSetting<float> ExitLurkRaceMargin;
        public FixedSetting<float> ExitLurkCommitSeconds;
        public FixedSetting<float> ExitLurkCooldownSeconds;
        public FixedSetting<int> SearchRoamPointCount;
        public FixedSetting<float> SearchRoamRadiusMeters;
        public FixedSetting<float> StrangerEscalationStartSeconds;
        public FixedSetting<float> StrangerEscalationDurationSeconds;
        public FixedSetting<float> EscalatedStrangerPingIntervalSeconds;
        public FixedSetting<float> EscalatedPingPositionNoiseMeters;
        public FixedSetting<bool> FarSprintEnabled;
        public FixedSetting<float> FarSprintMinDistanceMeters;
        public FixedSetting<bool> TeleporterRevealEnabled;
        public FixedSetting<bool> ShipSiegeFallbackEnabled;
        public FixedSetting<float> ShipSiegeProwlRadiusMeters;
        public FixedSetting<float> ShipSiegePoundIntervalSeconds;
        // #288 stagger bar. StaggerKnockdownDamageThreshold / StaggerDamageAccumWindowSeconds were
        // retired with the old accumulator (schema v12) — never re-defaulted, always renamed.
        public ConfigEntry<float> StaggerBarMax;
        public ConfigEntry<float> StaggerBarDamagePerForce;
        public FixedSetting<float> StaggerBarDamageMin;
        public FixedSetting<float> StaggerBarDamageMax;
        public ConfigEntry<float> StaggerBarStunThresholdPercent;
        public FixedSetting<float> StaggerBarRegenDelaySeconds;
        public FixedSetting<float> StaggerBarRegenPerSecond;
        public FixedSetting<float> StaggerBarStunDeviceCost;
        public FixedSetting<float> StaggerCooldownSeconds;
        public FixedSetting<bool> ProceduralFlinchEnabled;
        public FixedSetting<float> ProceduralFlinchIntensityScale;
        public FixedSetting<float> KnockdownDownPoseFraction;
        public FixedSetting<float> KnockdownGetUpSlowdown;
        public FixedSetting<float> KnockdownHoldFallbackSeconds;
        public FixedSetting<float> KnockdownMaxHoldSeconds;
        public ConfigEntry<float> KnockdownImmunitySeconds;
        public FixedSetting<float> KnockdownHoldAnimatorSpeed;
        public FixedSetting<float> KnockdownHoldMaxCreepFraction;
        public FixedSetting<bool> KnockdownRootPin;
        public FixedSetting<int> SpikeTrapHitForce;
        public FixedSetting<float> PostActionSeamWindowSeconds;
        public FixedSetting<float> NavigationObstacleMaxVerticalGap;
        public FixedSetting<float> SawStallRecoveryGraceSeconds;
        public FixedSetting<float> SearchChatterActiveIntervalSeconds;
        public FixedSetting<float> SearchChatterIdleIntervalSeconds;
        public FixedSetting<float> StaggerKnockdownImpactSlowdown;
        public FixedSetting<float> StaggerKnockdownSlowdownStartFraction;
        public FixedSetting<bool> StaggerMovementLock;
        public FixedSetting<float> BasicAttackRange;
        public FixedSetting<float> FinisherAttackRange;
        public FixedSetting<float> AttackCooldown;
        public FixedSetting<float> NonTargetInterruptRange;
        public FixedSetting<float> MultiAttackNearbyRange;
        public FixedSetting<bool> ExperimentalChainsawRealPlayerFinishers;
        public FixedSetting<bool> ExperimentalChainsawIdentityOverlays;
        public FixedSetting<int> ChainsawFinisherPresentationMode;
        public FixedSetting<bool> ChainsawFinisherVictimUsesInteractionsApi;
        public ConfigEntry<int> ChainsawFinisherChancePercent;
        public ConfigEntry<bool> ChainsawFinisherUsesBloodFX;
        public FixedSetting<float> PressUpThrowVictimOffsetX;
        public FixedSetting<float> PressUpThrowVictimOffsetY;
        public FixedSetting<float> PressUpThrowVictimOffsetZ;
        public FixedSetting<float> PressUpThrowVictimEulerY;
        public FixedSetting<float> ChainsawFinisherVictimOffsetX;
        public FixedSetting<float> ChainsawFinisherVictimOffsetY;
        public FixedSetting<float> ChainsawFinisherVictimOffsetZ;
        public FixedSetting<float> ChainsawFinisherVictimYawOffset;
        public FixedSetting<float> ChainsawFinisherVictimProxyScale;
        public FixedSetting<bool> ChainsawFinisherPairAlignment;
        public FixedSetting<float> ChainsawFinisherPairSeparation;
        public FixedSetting<float> PressUpThrowCarryAnchorOffsetX;
        public FixedSetting<float> PressUpThrowCarryAnchorOffsetY;
        public FixedSetting<float> PressUpThrowCarryAnchorOffsetZ;
        public FixedSetting<bool> ChainsawSawStallCycle;
        public FixedSetting<bool> ChainsawSwingAudioModulation;
        public ConfigEntry<float> ChainsawLoopBaseVolume;
        public FixedSetting<bool> ChainsawNearFieldLayer;
        public FixedSetting<float> ChainsawNearFieldMaxDistance;
        public FixedSetting<float> ChainsawNearFieldVolume;
        public FixedSetting<float> ChainsawContactMaxDistance;
        public FixedSetting<bool> ChainsawContactBladeEmitter;
        public FixedSetting<bool> ChainsawContactOverridesSpeechDuck;
        public FixedSetting<bool> ChainsawBladeProximityModulation;
        public FixedSetting<float> ChainsawBladeProximityNearGain;
        public FixedSetting<bool> ChainsawLayeredEngineLoop;
        public FixedSetting<bool> ChainsawSwingWhoosh;
        public FixedSetting<float> ChainsawSwingWhooshVolume;
        public FixedSetting<bool> ChainsawFinisherCuttingAudio;
        public FixedSetting<float> ChainsawFinisherGoreVolume;
        public FixedSetting<bool> ChainsawTauntLoopOscillation;
        public FixedSetting<bool> BundyTauntStompFootsteps;
        public FixedSetting<float> BundyTauntStompVolumeScale;
        public FixedSetting<float> BundyVoiceMaxDistance;
        public FixedSetting<float> BundyVoiceOutdoorRangeScale;
        public FixedSetting<float> BundyVoiceOutdoorVolumeScale;
        public FixedSetting<float> BundyVoiceOccludedCutoff;
        public FixedSetting<bool> BundyVoiceDistanceReverb;
        public FixedSetting<bool> BundyVoiceDistanceSpread;
        public FixedSetting<bool> ChainsawAttackRevOneShot;
        public ConfigEntry<float> BundyVoiceVolume;
        // #761. One vanilla-friendly presentation switch: every vocal cue is silenced on this
        // client and a burlap sack is parented to the head bone. Client-local on purpose — it
        // is cosmetic, so each player keeps their own choice and no peer needs to agree.
        public ConfigEntry<bool> VanillaFriendlyStranger;
        public FixedSetting<float> BundyVoiceAnchorHeight;
        public ConfigEntry<float> BundyFootstepVolume;
        public FixedSetting<bool> BundyVanillaFootsteps;
        public FixedSetting<float> BundyHeavyGaitFootstepNoiseScale;
        public FixedSetting<float> BundyHeavyGaitFootstepRangeScale;
        public FixedSetting<bool> BundyBloodLayer;
        public FixedSetting<float> BundyBloodLayerVolume;
        public FixedSetting<float> BundyBloodLayerGapSeconds;
        public FixedSetting<bool> BundyMatchVanillaVoiceSpatial;
        public FixedSetting<bool> BundyDistanceAirAbsorption;
        public FixedSetting<bool> BundyAudioRangeExtension;
        public FixedSetting<float> ChainsawLoopMaxDistance;
        public FixedSetting<float> ChainsawLoopEscalatedMaxDistance;
        public FixedSetting<float> BundyFootstepMaxDistance;
        public FixedSetting<bool> BundyVanillaVoiceClipTest;

        public BundyConfig(ConfigFile cfg)
        {
            ChainsawSpawnWeight = FixedSetting.Of(0);

            DebugLogs = FixedSetting.Of(false);
            RuntimePerformanceTiming = FixedSetting.Of(false);
            RuntimePerformanceReportSeconds = FixedSetting.Of(10f);
            RuntimePerformanceSlowCallMilliseconds = FixedSetting.Of(2f);
            LogVerbosity = FixedSetting.Of(1);
            LogPerception = FixedSetting.Of(false);
            LogMovement = FixedSetting.Of(false);
            LogCombat = FixedSetting.Of(false);
            LogAnim = FixedSetting.Of(false);
            LogAudio = FixedSetting.Of(false);
            LogSiege = FixedSetting.Of(false);
            ChainsawStaticVisualTestMode = FixedSetting.Of(false);
            ChainsawPinAccuRigHipTranslation = FixedSetting.Of(true);
            // #716 F6: intentionally fixed. This is not a user-facing switch but a sub-selector
            // *inside* the diagnostics surface: MaybeLogAttackGripDiagnostic already requires
            // RigDiagnosticsEnabled (BundyLogGate: the DebugLogs master, which is itself fixed
            // false above, plus Anim verbosity 2). Leaving it true means a developer who reopens
            // DebugLogs gets the grip lines with it; binding it as a .cfg key would publish a
            // control that can never produce output while the master is off.
            ChainsawAttackGripDiagnostics = FixedSetting.Of(true);
            // Renamed from ChainsawDisableAttackHandIK (#277): the old key was a no-op for its
            // whole life, so existing config files persist its old default (true); binding the
            // old name would silently force attack IK off on every pre-existing install now
            // that the override is actually wired. A fresh key gets a fresh default.
            ChainsawForceDisableAttackHandIK = FixedSetting.Of(false);
            ChainsawDisableGenericGripSolver = FixedSetting.Of(false);
            ChainsawMaterialDiagnostics = FixedSetting.Of(false);
            ChainsawBodySkinTintR = FixedSetting.Of(0.72f);
            ChainsawBodySkinTintG = FixedSetting.Of(0.81f);
            ChainsawBodySkinTintB = FixedSetting.Of(0.91f);
            ChainsawArmElbowsOutDegrees = FixedSetting.Of(8f);
            ChainsawClavicleProtractLeftDegrees = FixedSetting.Of(0f);
            ChainsawClavicleProtractRightDegrees = FixedSetting.Of(0f);
            ChainsawLockLocomotionDirection = FixedSetting.Of(false);
            ChainsawVelocityGatedIdleDemotion = FixedSetting.Of(true);
            ChainsawSquaredLocomotionClips = FixedSetting.Of(true);
            ChainsawFacingYawOffsetEnabled = FixedSetting.Of(true);
            ChainsawFacingYawOffsetAtSpine = FixedSetting.Of(true);
            ChainsawFacingYawOffsetDegrees = FixedSetting.Of(-46f);
            ChainsawFacingYawOffsetIdleDegrees = FixedSetting.Of(0f);
            ChainsawFacingYawOffsetSmoothingSeconds = FixedSetting.Of(0.25f);
            ChainsawFootGroundingEnabled = FixedSetting.Of(false);
            ChainsawFootIKEnabled = FixedSetting.Of(false);
            ChainsawFootIKSlopeAlign = FixedSetting.Of(true);
            ChainsawFootIKMaxLift = FixedSetting.Of(0.45f);
            ChainsawHeadAimEnabled = FixedSetting.Of(true);
            ChainsawHeadAimYawClampDegrees = FixedSetting.Of(60f);
            ChainsawHeadAimPitchClampDegrees = FixedSetting.Of(25f);
            ChainsawHeadAimSmoothingSeconds = FixedSetting.Of(0.2f);
            ChainsawHairTintR = FixedSetting.Of(0.70f);
            ChainsawHairTintG = FixedSetting.Of(0.85f);
            ChainsawHairTintB = FixedSetting.Of(1.00f);
            ChainsawMaterialLowSpecTest = FixedSetting.Of(false);
            ChainsawMaterialNormalOffTest = FixedSetting.Of(false);
            ChainsawMaterialMatteSkinTest = FixedSetting.Of(false);
            ChainsawMaterialHeadMatchTest = FixedSetting.Of(false);

            ChainsawWalkSpeed = cfg.Bind(ContractedConfigSections.TheStranger, "Walk Speed", 1.7f,
                "Host only. The Stranger's walking speed. The walk animation is authored for the " +
                "default; large changes make his feet slide.");
            ChainsawRunSpeed = cfg.Bind(ContractedConfigSections.TheStranger, "Run Speed", 4.25f,
                "Host only. The Stranger's pursuit run speed. Values below 4.25 are raised at runtime.");
            ChainsawSprintSpeed = cfg.Bind(ContractedConfigSections.TheStranger, "Sprint Speed", 7.65f,
                new ConfigDescription(
                    "Host only. Ground speed during the overhead chainsaw sprint. The sprint animation is " +
                    "authored for exactly 7.65; changing it slides his feet in proportion to the difference.",
                    new AcceptableValueRange<float>(4.25f, 12f)));
            ChainsawSprintMinDistance = FixedSetting.Of(18f);
            ChainsawSprintNoContactSeconds = FixedSetting.Of(25f);
            ChainsawSprintChance = cfg.Bind(ContractedConfigSections.TheStranger, "Sprint Chance", 0.35f,
                new ConfigDescription(
                    "Host only. Chance the no-contact sprint fires on each re-check. 1 = always, 0 = never " +
                    "(the after-knockdown sprint still fires).",
                    new AcceptableValueRange<float>(0f, 1f)));
            ChainsawSprintRollIntervalSeconds = FixedSetting.Of(8f);
            ChainsawSprintCooldownSeconds = FixedSetting.Of(20f);
            ChainsawSprintMaxSeconds = FixedSetting.Of(12f);
            ChainsawSprintAfterKnockdownTaunt = FixedSetting.Of(true);
            ChainsawSprintIntroSpeedRamp = FixedSetting.Of(true);
            ChainsawForceSprint = FixedSetting.Of(false);
            ChainsawVisualYawCorrection = FixedSetting.Of(0f);
            ChainsawRunVisualYawCorrection = FixedSetting.Of(0f);
            ChainsawRunVisualRollCorrection = FixedSetting.Of(0f);
            // Cosmetic engine vibration on the saw prop (BundyChainsawRumble). Client-local, no
            // netcode, no gameplay effect. Amplitude and frequency are binds because this has to be
            // calibrated against the eye at normal viewing distance, not against a spec sheet.
            ChainsawRumbleEnabled = FixedSetting.Of(true);
            ChainsawRumbleFrequencyHz = FixedSetting.Of(11f);
            ChainsawRumbleAmplitudeDegrees = FixedSetting.Of(0.7f);
            ChainsawRumbleRevGain = FixedSetting.Of(1.6f);
            RunThreshold = FixedSetting.Of(5.0f);
            StopDistance = FixedSetting.Of(1.75f);
            StrangerPingIntervalSeconds = FixedSetting.Of(7f);
            PingPositionNoiseMeters = FixedSetting.Of(12f);
            SearchWindowSeconds = FixedSetting.Of(25f);
            HearingRadiusMeters = FixedSetting.Of(14f);
            SightMemoryGraceSeconds = FixedSetting.Of(2.5f);
            StrangerSightProximityMeters = FixedSetting.Of(3.5f);
            PursuitMemorySeconds = FixedSetting.Of(9f);
            HuntDwellEnabled = FixedSetting.Of(true);
            NoiseContactHuntDwellSeconds = FixedSetting.Of(8f);
            WorldNoiseBusEnabled = FixedSetting.Of(true);
            WorldNoiseRadiusScale = FixedSetting.Of(1f);
            WorldNoiseLiveConfidenceThreshold = FixedSetting.Of(0.9f);
            WorldNoisePositionNoiseMeters = FixedSetting.Of(8f);
            WorldNoiseAttributionRadiusMeters = FixedSetting.Of(4f);
            WorldNoiseEventCooldownSeconds = FixedSetting.Of(3f);
            CrossBoundaryPathSuppressionEnabled = FixedSetting.Of(true);
            CrossingRouteCacheSeconds = FixedSetting.Of(5f);
            LongRangeAsyncReachabilityEnabled = FixedSetting.Of(true);
            SmartBoundaryLinkGateEnabled = FixedSetting.Of(true);
            // Job 5a drop-down follow is core behaviour: the old AI/DropDownFollowEnabled toggle
            // is retired, not rebound (stale entries are ignored). The three re-tuned gates bind
            // fresh keys per the schema-v12 rule (never re-defaulted, always renamed): an existing
            // config file persists the old defaults, so rebinding the old names would silently keep
            // the old, rarely-firing feel on every pre-existing install.
            DropDownFollowForceTrigger = FixedSetting.Of(false);
            DropDownFollowDetourRatio = FixedSetting.Of(1.25f);
            DropDownFollowMinDetourMeters = FixedSetting.Of(6f);
            DropDownFollowMinDropMeters = FixedSetting.Of(2f);
            DropDownFollowMaxHeightMeters = FixedSetting.Of(12f);
            DropDownFollowMinIntervalSeconds = FixedSetting.Of(3f);
            DropDownFollowGravity = FixedSetting.Of(22f);
            DropDownFollowLaunchUpSpeed = FixedSetting.Of(1.5f);
            DropDownFollowMaxHorizontalSpeed = FixedSetting.Of(9f);
            DropDownFollowMaxTraversalSeconds = FixedSetting.Of(3.5f);
            ExitLurkEnabled = FixedSetting.Of(true);
            ExitLurkMaxPathMeters = FixedSetting.Of(60f);
            ExitLurkMaxTargetDistanceMeters = FixedSetting.Of(40f);
            ExitLurkAbandonDistanceFactor = FixedSetting.Of(1.5f);
            ExitLurkRaceMargin = FixedSetting.Of(1.35f);
            ExitLurkCommitSeconds = FixedSetting.Of(20f);
            ExitLurkCooldownSeconds = FixedSetting.Of(45f);
            SearchRoamPointCount = FixedSetting.Of(3);
            SearchRoamRadiusMeters = FixedSetting.Of(10f);
            StrangerEscalationStartSeconds = FixedSetting.Of(120f);
            StrangerEscalationDurationSeconds = FixedSetting.Of(300f);
            EscalatedStrangerPingIntervalSeconds = FixedSetting.Of(3.5f);
            EscalatedPingPositionNoiseMeters = FixedSetting.Of(4f);
            FarSprintEnabled = FixedSetting.Of(true);
            FarSprintMinDistanceMeters = FixedSetting.Of(60f);
            PostActionSeamWindowSeconds = FixedSetting.Of(0.5f);
            NavigationObstacleMaxVerticalGap = FixedSetting.Of(3f);
            TeleporterRevealEnabled = FixedSetting.Of(true);
            ShipSiegeFallbackEnabled = FixedSetting.Of(true);
            ShipSiegeProwlRadiusMeters = FixedSetting.Of(3.5f);
            ShipSiegePoundIntervalSeconds = FixedSetting.Of(3.5f);
            BasicAttackRange = FixedSetting.Of(2.25f);
            // ---- #288 stagger bar -------------------------------------------------------------
            // Bundy carries an internal 100-point poise bar, simulated identically on every client
            // (vanilla replicates HitEnemy to all of them with the same force). Every hit drains it
            // and produces a procedural flinch; crossing the stun threshold adds the small stagger;
            // emptying it is the ONLY thing that knocks him down.
            StaggerBarMax = cfg.Bind(ContractedConfigSections.TheStranger, "Stagger Bar Max", 100f,
                new ConfigDescription(
                    "Host only. Size of The Stranger's internal stagger (poise) bar. Every point has to be " +
                    "shot off before he can be knocked down, and it refills between fights.",
                    new AcceptableValueRange<float>(10f, 1000f)));
            StaggerBarDamagePerForce = cfg.Bind(ContractedConfigSections.TheStranger, "Stagger Damage Per Force", 5f,
                new ConfigDescription(
                    "Host only. Stagger-bar points drained per point of weapon hit force. At the default 5, " +
                    "a shovel (force 1) costs 5 and a point-blank shotgun (force 5) costs 25.",
                    new AcceptableValueRange<float>(0.1f, 100f)));
            StaggerBarDamageMin = FixedSetting.Of(4f);
            StaggerBarDamageMax = FixedSetting.Of(25f);
            StaggerBarStunThresholdPercent = cfg.Bind(ContractedConfigSections.TheStranger, "Stagger Stun Threshold Percent", 50f,
                new ConfigDescription(
                    "Host only. At or below this percentage of Stagger Bar Max, a hit also plays a small " +
                    "stagger on top of the flinch. Above it, hits only flinch.",
                    new AcceptableValueRange<float>(0f, 100f)));
            StaggerBarRegenDelaySeconds = FixedSetting.Of(4f);
            StaggerBarRegenPerSecond = FixedSetting.Of(5f);
            StaggerBarStunDeviceCost = FixedSetting.Of(30f);
            StaggerCooldownSeconds = FixedSetting.Of(1.6f);
            ProceduralFlinchEnabled = FixedSetting.Of(true);
            ProceduralFlinchIntensityScale = FixedSetting.Of(1f);
            StaggerKnockdownImpactSlowdown = FixedSetting.Of(0.65f);
            StaggerKnockdownSlowdownStartFraction = FixedSetting.Of(0.5f);
            KnockdownDownPoseFraction = FixedSetting.Of(0.60f);
            KnockdownGetUpSlowdown = FixedSetting.Of(0.55f);
            KnockdownHoldFallbackSeconds = FixedSetting.Of(2.6f);
            KnockdownMaxHoldSeconds = FixedSetting.Of(6f);
            KnockdownImmunitySeconds = cfg.Bind(ContractedConfigSections.TheStranger, "Knockdown Immunity Seconds", 30f,
                new ConfigDescription(
                    "Host only. After recovering from a knockdown The Stranger cannot be knocked down again " +
                    "for this many seconds; knockdown-tier damage during the window plays a small stagger " +
                    "instead. 0 disables the immunity.",
                    new AcceptableValueRange<float>(0f, 120f)));
            KnockdownHoldAnimatorSpeed = FixedSetting.Of(0.06f);
            KnockdownHoldMaxCreepFraction = FixedSetting.Of(0.12f);
            KnockdownRootPin = FixedSetting.Of(true);
            SpikeTrapHitForce = FixedSetting.Of(5);
            StaggerMovementLock = FixedSetting.Of(true);
            ChainsawAttackStartRange = FixedSetting.Of(5.0f);
            ChainsawLungeSpeed = FixedSetting.Of(11.0f);
            ChainsawLungeFacingMaxSpeed = FixedSetting.Of(0f);
            ChainsawLungeBladeStandoff = FixedSetting.Of(0f);
            ChainsawLungeCommitFraction = FixedSetting.Of(0.85f);
            ChainsawLungeWindupHoldSeconds = FixedSetting.Of(0.30f);
            ChainsawChainAttackBlendSeconds = FixedSetting.Of(0.30f);
            ChainsawChainAttackEntryOffsetFraction = FixedSetting.Of(0.15f);
            ChainsawChainAttackHandoffFraction = FixedSetting.Of(0.70f);
            CombatManeuverAttackGuard = FixedSetting.Of(true);
            CombatManeuverAttackDwellSeconds = FixedSetting.Of(0.45f);
            CombatManeuverMinWindowSeconds = FixedSetting.Of(0.55f);
            CombatManeuverArcStepDegrees = FixedSetting.Of(65f);
            CombatManeuverArrivalHoldSeconds = FixedSetting.Of(0.35f);
            ChainsawAttackVisualExitBlendSeconds = FixedSetting.Of(0.04f);
            ChainsawAttackVisualExitSwingThreshold = FixedSetting.Of(0.82f);
            ChainsawAttackVisualExitHeavyThreshold = FixedSetting.Of(0.83f);
            FinisherAttackRange = FixedSetting.Of(2.75f);
            AttackCooldown = FixedSetting.Of(2.0f);
            NonTargetInterruptRange = FixedSetting.Of(8.0f);
            MultiAttackNearbyRange = FixedSetting.Of(3.25f);
            // #716 F6: both intentionally fixed - they gate shipped finisher presentation, not
            // diagnostics. "RealPlayerFinishers" is what makes the shipped presentation mode 0
            // (ClonedPlayer) show the victim's own body; turning it off downgrades every chainsaw
            // finisher to the authored placeholder proxy. "IdentityOverlays" is the equivalent
            // selector for presentation mode 2 and is inert unless ChainsawFinisherPresentationMode
            // is changed. Neither is a knob a player should be able to break the kill cam with.
            ExperimentalChainsawRealPlayerFinishers = FixedSetting.Of(true);
            ExperimentalChainsawIdentityOverlays = FixedSetting.Of(true);
            ChainsawFinisherPresentationMode = FixedSetting.Of(0);
            ChainsawFinisherVictimUsesInteractionsApi = FixedSetting.Of(true);
            ChainsawFinisherChancePercent = cfg.Bind(ContractedConfigSections.TheStranger, "Finisher Chance Percent", 75,
                new ConfigDescription(
                    "Host only. Percent chance that a qualifying kill becomes a chainsaw finisher " +
                    "instead of a plain basic-attack kill.",
                    new AcceptableValueRange<int>(0, 100)));
            ChainsawFinisherUsesBloodFX = cfg.Bind(ContractedConfigSections.TheStranger, "Finisher Uses BloodFX", true,
                "Routes the chainsaw finisher's gore through BloodVFX (HDRP decals and authored " +
                "particles) when that mod is installed. Turn off to use the legacy particle spray.");
            PressUpThrowVictimOffsetX = FixedSetting.Of(0f);
            PressUpThrowVictimOffsetY = FixedSetting.Of(0f);
            PressUpThrowVictimOffsetZ = FixedSetting.Of(0f);
            PressUpThrowVictimEulerY = FixedSetting.Of(0f);
            ChainsawFinisherVictimOffsetX = FixedSetting.Of(0f);
            ChainsawFinisherVictimOffsetY = FixedSetting.Of(0f);
            ChainsawFinisherVictimOffsetZ = FixedSetting.Of(1.15f);
            ChainsawFinisherVictimYawOffset = FixedSetting.Of(180f);
            ChainsawFinisherVictimProxyScale = FixedSetting.Of(1.75f);
            ChainsawFinisherPairAlignment = FixedSetting.Of(true);
            ChainsawFinisherPairSeparation = FixedSetting.Of(1.25f);
            PressUpThrowCarryAnchorOffsetX = FixedSetting.Of(0f);
            PressUpThrowCarryAnchorOffsetY = FixedSetting.Of(0.46f);
            PressUpThrowCarryAnchorOffsetZ = FixedSetting.Of(0.56f);
            ChainsawSawStallCycle = FixedSetting.Of(true);
            SawStallRecoveryGraceSeconds = FixedSetting.Of(1.5f);
            SearchChatterActiveIntervalSeconds = FixedSetting.Of(13.5f);
            SearchChatterIdleIntervalSeconds = FixedSetting.Of(27.5f);
            ChainsawSwingAudioModulation = FixedSetting.Of(true);
            ChainsawLoopBaseVolume = cfg.Bind(ContractedConfigSections.TheStranger, "Chainsaw Volume", 0.65f,
                new ConfigDescription(
                    "Base playback volume for the chainsaw engine loop. The resting loop sits at this level " +
                    "and swings/contact swell above it toward 1.0.",
                    new AcceptableValueRange<float>(0.3f, 1f)));
            ChainsawNearFieldLayer = FixedSetting.Of(true);
            ChainsawNearFieldMaxDistance = FixedSetting.Of(7f);
            ChainsawNearFieldVolume = FixedSetting.Of(0.6f);
            ChainsawContactBladeEmitter = FixedSetting.Of(true);
            ChainsawContactMaxDistance = FixedSetting.Of(30f);
            ChainsawContactOverridesSpeechDuck = FixedSetting.Of(true);
            ChainsawBladeProximityModulation = FixedSetting.Of(true);
            ChainsawBladeProximityNearGain = FixedSetting.Of(0.45f);
            ChainsawLayeredEngineLoop = FixedSetting.Of(true);
            ChainsawSwingWhoosh = FixedSetting.Of(true);
            ChainsawSwingWhooshVolume = FixedSetting.Of(0.7f);
            ChainsawFinisherCuttingAudio = FixedSetting.Of(true);
            ChainsawFinisherGoreVolume = FixedSetting.Of(1.8f);
            ChainsawTauntLoopOscillation = FixedSetting.Of(true);
            BundyTauntStompFootsteps = FixedSetting.Of(true);
            BundyTauntStompVolumeScale = FixedSetting.Of(1.35f);
            ChainsawAttackRevOneShot = FixedSetting.Of(true);
            BundyVoiceVolume = cfg.Bind(ContractedConfigSections.TheStranger, "Voice Volume", 0.75f,
                new ConfigDescription(
                    "Playback volume for The Stranger's voicelines.",
                    new AcceptableValueRange<float>(0f, 1f)));
            VanillaFriendlyStranger = cfg.Bind(ContractedConfigSections.TheStranger, "Vanilla-Friendly Stranger", true,
                "Vanilla-friendly presentation: The Stranger never speaks (every voiceline is muted on this " +
                "client) and uses the withered employee suit and damaged mask. If its optional body bundle is " +
                "unavailable, he wears the existing sack instead. Chainsaw, footstep and hit audio are unchanged. " +
                "Turn off for the original voiced appearance. Applies per player; takes effect on the next spawn.");
            BundyVoiceAnchorHeight = FixedSetting.Of(1.7f);
            BundyFootstepVolume = cfg.Bind(ContractedConfigSections.TheStranger, "Footstep Volume", 0.9f,
                new ConfigDescription(
                    "Playback volume for The Stranger's footsteps.",
                    new AcceptableValueRange<float>(0f, 1f)));
            BundyVanillaFootsteps = FixedSetting.Of(true);
            BundyHeavyGaitFootstepNoiseScale = FixedSetting.Of(1.75f);
            BundyHeavyGaitFootstepRangeScale = FixedSetting.Of(1.5f);
            BundyBloodLayer = FixedSetting.Of(true);
            BundyBloodLayerVolume = FixedSetting.Of(0.45f);
            BundyBloodLayerGapSeconds = FixedSetting.Of(0.06f);
            BundyMatchVanillaVoiceSpatial = FixedSetting.Of(false);
            BundyDistanceAirAbsorption = FixedSetting.Of(true);
            BundyAudioRangeExtension = FixedSetting.Of(true);
            ChainsawLoopMaxDistance = FixedSetting.Of(60f);
            ChainsawLoopEscalatedMaxDistance = FixedSetting.Of(85f);
            BundyVoiceMaxDistance = FixedSetting.Of(40f);
            BundyVoiceOutdoorRangeScale = FixedSetting.Of(1.4f);
            BundyVoiceOccludedCutoff = FixedSetting.Of(2600f);
            BundyVoiceOutdoorVolumeScale = FixedSetting.Of(1.3f);
            BundyVoiceDistanceReverb = FixedSetting.Of(true);
            BundyVoiceDistanceSpread = FixedSetting.Of(true);
            BundyFootstepMaxDistance = FixedSetting.Of(35f);
            BundyVanillaVoiceClipTest = FixedSetting.Of(false);
        }
    }
}
