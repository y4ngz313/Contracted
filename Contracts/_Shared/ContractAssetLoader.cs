using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using LethalLib.Modules;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Escalations._Shared;
using Y4NGZCompany.Escalations.BlacksiteRetrieval;
using Y4NGZCompany.Escalations.Congregation;
using Y4NGZCompany.Contracts.Payload;
using Y4NGZCompany.Contracts.Whistleblower;
using Y4NGZCompany.Contracts.ShadowRaid;
namespace Y4NGZCompany.Contracts._Shared
{
    internal static partial class ContractAssetLoader
    {
        private const string BundleFileName = "lgucontracts.bundle";
        private const string PestControlTrapArtKitName = "PestControlTrapArtKit";
        private const string WhistleblowerVisualName = "WhistleblowerRuntimeVisual";
        private const string WhistleblowerVanillaVisualName = "WhistleblowerRuntimeVisualVanilla";
        // Unity names an imported Avatar "<fbx file name>Avatar", so the vanilla model's Avatar is
        // "WhistleblowerVanillaAvatar" and the default model's is "WhistleblowerAvatar". Kept in
        // step with WhistleblowerAssetBuilder.VanillaAvatarNameFragment.
        private const string WhistleblowerVanillaAvatarNameFragment = "WhistleblowerVanilla";
        private const string WhistleblowerControllerName = "WhistleblowerVisual";
        private const string BlacksiteSoldierName = "BlacksiteSoldier";
        private const string BlacksiteSoldierControllerName = "BlacksiteInsurgentRuntime";
        private const string BlacksiteHelicopterName = "BlacksiteHelicopter";
        private const string BlacksiteCrateName = "BlacksiteCargoCrate";
        private const string PayloadCartName = "PayloadCart";
        private const string CultistRedName = "CultistRed";
        private const string CultistBlackName = "CultistBlack";
        private const string CultistWhiteName = "CultistWhite";
        private const string SeraphimLivePrefabName = "Ophaim_fullmesh (1)";
        private const string SeraphimPrefabName = "Ophaim_fullmesh";
        private const string SeraphimPreferredPathFragment = "/URP_HDRP/";
        private const string SeraphimEyePartName = "Ophanim_Eye";
        private const string SeraphimFirstRingPartName = "Ophanim_First_Ring";
        private const string SeraphimSecondRingPartName = "Ophanim_Second_Ring";
        private const string SeraphimThirdRingPartName = "Ophanim_Third_Ring";
        private const string SeraphimFeathersPartName = "Ophaim_feathers";
        private const string SeraphimControllerName = "Ophanim";
        private const string SeraphimHolyEffectName = "HolyEffect";
        private const string SeraphimExecutionEffectName = "HolyEffect5";
        private const string SeraphimBloodEffectName = "BloodEffect3";
        private const string SeraphimBeamCueName = "SFX_SpellCast01Heal";
        private const string SeraphimExecutionImpactName = "SeraphimExecutionImpact";
        private const string BreachDrillSparkEffectName = "vfx_Sparks_01";
        private const string BreachHardDriveVisualName = "BreachHardDrive";
        private const string SurveyBeaconVisualName = "SurveyBeacon";
        private const string BreachContainerDoorOpenClipName = "universfield-opening-metal-door-199581";
        private const string ContainmentLockdownBarsName = "ContainmentLockdownBars";
        private const string ContainmentLockdownBarsModelName = "Metal_fence";
        private const string ContainmentLockdownBarsOpacityTextureName = "DefaultMaterial_1001_opacity";
        private const string ContainmentLockdownDoorsName = "ContainmentLockdownDoors";
        private const string ContainmentLockdownDoorsTrimName = "ContainmentLockdownDoorsTrim";
        private const string DefaultBreachContainerVisualName = "Shipping Container 20 Foot";
        private const string BreachSmallContainerVisualName = "Shipping Container 10 Foot";
        private const string BreachMediumContainerVisualName = "Shipping Container 20 Foot";
        private const string BreachLargeContainerVisualName = "Shipping Container 40 Foot";
        private static readonly HashSet<string> PreparedInteriorSupportPrefabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<int> RegisteredBlacksiteNetworkPrefabs = new HashSet<int>();
        private static readonly HashSet<int> RegisteredInteriorSupportPrefabs = new HashSet<int>();
        private static GameObject _hiddenPrefabHolder;

        internal static AssetBundle Bundle { get; private set; }
        internal static GameObject WhistleblowerVisualPrefab { get; private set; }
        internal static bool WhistleblowerVisualIsVanillaVariant { get; private set; }
        internal static Avatar WhistleblowerAvatar { get; private set; }
        internal static Texture2D WhistleblowerBaseColor { get; private set; }
        internal static RuntimeAnimatorController WhistleblowerController { get; private set; }
        internal static AnimationClip WhistleblowerNervousIdleClip { get; private set; }
        internal static AnimationClip WhistleblowerCombatIdleClip { get; private set; }
        internal static AnimationClip WhistleblowerBreathingIdleClip { get; private set; }
        internal static AnimationClip WhistleblowerWalkingClip { get; private set; }
        internal static AnimationClip WhistleblowerRunningClip { get; private set; }
        internal static AnimationClip WhistleblowerBoxingForwardClip { get; private set; }
        internal static AnimationClip WhistleblowerBoxingBackwardClip { get; private set; }
        internal static AnimationClip WhistleblowerBoxingLeftClip { get; private set; }
        internal static GameObject PestControlTrapArtKitPrefab { get; private set; }
        internal static AnimationClip PestLeverThirdPersonPushClip { get; private set; }
        internal static AnimationClip PestLeverThirdPersonPullClip { get; private set; }
        internal static AnimationClip PestLeverFirstPersonPushClip { get; private set; }
        internal static AnimationClip PestLeverFirstPersonPullClip { get; private set; }
        internal static AnimationClip WhistleblowerBoxingRightClip { get; private set; }
        internal static AnimationClip WhistleblowerStepBackClip { get; private set; }
        internal static AnimationClip WhistleblowerPunchLeftClip { get; private set; }
        internal static AnimationClip WhistleblowerPunchRightClip { get; private set; }
        internal static AnimationClip WhistleblowerEvadeClip { get; private set; }
        internal static AnimationClip WhistleblowerHeadHitClip { get; private set; }
        internal static AnimationClip WhistleblowerBodyHitClip { get; private set; }
        internal static AnimationClip WhistleblowerRibHitClip { get; private set; }
        internal static AnimationClip WhistleblowerFallingDownClip { get; private set; }
        internal static AnimationClip WhistleblowerFallFlatClip { get; private set; }
        internal static AnimationClip WhistleblowerCrawlBackwardsClip { get; private set; }
        internal static AnimationClip WhistleblowerGetUpFrontClip { get; private set; }
        internal static AnimationClip WhistleblowerGetUpBackClip { get; private set; }
        internal static AnimationClip WhistleblowerStumbleClip { get; private set; }
        internal static AnimationClip WhistleblowerDeathClip { get; private set; }
        internal static GameObject BlacksiteSoldierPrefab { get; private set; }
        internal static GameObject BlacksiteShipPrefab { get; private set; }
        private static AssetBundle _blacksiteShipBundle;
        private static AssetBundle _congregantBundle;
        internal static GameObject BlacksiteCratePrefab { get; private set; }
        internal static GameObject PayloadCartPrefab { get; private set; }
        internal static GameObject WasteDisposalIncineratorPrefab { get; private set; }
        internal static RuntimeAnimatorController PayloadPushController { get; private set; }
        internal static AnimationClip PayloadPushClip { get; private set; }
        internal static RuntimeAnimatorController BlacksiteSoldierController { get; private set; }
        internal static EnemyType BlacksiteSoldierEnemyType { get; private set; }
        internal static AudioClip[] BlacksiteGunshotSingleClips { get; private set; } = Array.Empty<AudioClip>();
        internal static AudioClip BlacksiteGunshotBurstClip { get; private set; }
        internal static AudioClip BlacksiteGrenadeClip { get; private set; }
        internal static AudioClip BlacksiteHelicopterRotorClip { get; private set; }
        internal static Texture2D BlacksiteSoldierBodyTexture { get; private set; }
        internal static Texture2D BlacksiteSoldierNormalTexture { get; private set; }
        internal static Texture2D BlacksiteSoldierLensTexture { get; private set; }
        internal static Texture2D BlacksiteSoldierWeaponTexture { get; private set; }
        internal static AudioClip[] BlacksiteFootstepClips { get; private set; } = Array.Empty<AudioClip>();
        internal static AudioClip BlacksiteBatonEquipClip { get; private set; }
        internal static AudioClip BlacksiteBatonStowClip { get; private set; }
        internal static AudioClip[] BlacksiteGearFoleyClips { get; private set; } = Array.Empty<AudioClip>();
        internal static AudioClip[] BlacksiteHitReactionClips { get; private set; } = Array.Empty<AudioClip>();
        internal static AudioClip[] BlacksiteDeathReactionClips { get; private set; } = Array.Empty<AudioClip>();
        internal static AudioClip[] BlacksiteVoiceAffirmativeClips { get; private set; } = Array.Empty<AudioClip>();
        internal static AudioClip[] BlacksiteVoiceContactClips { get; private set; } = Array.Empty<AudioClip>();
        internal static AudioClip[] BlacksiteVoiceCommandClips { get; private set; } = Array.Empty<AudioClip>();
        internal static AudioClip[] BlacksiteVoiceDeployClips { get; private set; } = Array.Empty<AudioClip>();
        internal static AudioClip[] BlacksiteVoiceHitClips { get; private set; } = Array.Empty<AudioClip>();
        internal static AudioClip[] BlacksiteVoiceRetreatClips { get; private set; } = Array.Empty<AudioClip>();
        private static readonly Dictionary<BlacksiteVoiceEvent, AudioClip[]> BlacksiteVoiceClips =
            new Dictionary<BlacksiteVoiceEvent, AudioClip[]>();
        internal static GameObject CultistRedPrefab { get; private set; }
        internal static GameObject CultistBlackPrefab { get; private set; }
        internal static GameObject CultistWhitePrefab { get; private set; }
        internal static RuntimeAnimatorController CultistRedController { get; private set; }
        internal static RuntimeAnimatorController CultistBlackController { get; private set; }
        internal static RuntimeAnimatorController CultistWhiteController { get; private set; }
        internal static EnemyType CultistRedEnemyType { get; private set; }
        internal static EnemyType CultistBlackEnemyType { get; private set; }
        internal static EnemyType CultistWhiteEnemyType { get; private set; }
        internal static AudioClip CultistDetectionSuspenseClip { get; private set; }
        internal static AudioClip[] CultistFootstepClips { get; private set; } = Array.Empty<AudioClip>();
        internal static bool CultistAssetsReady => CultistRedPrefab != null && CultistBlackPrefab != null && CultistWhitePrefab != null;
        internal static GameObject SeraphimPrefab { get; private set; }
        internal static GameObject SeraphimEyePartPrefab { get; private set; }
        internal static GameObject SeraphimFirstRingPartPrefab { get; private set; }
        internal static GameObject SeraphimSecondRingPartPrefab { get; private set; }
        internal static GameObject SeraphimThirdRingPartPrefab { get; private set; }
        internal static GameObject SeraphimFeathersPartPrefab { get; private set; }
        internal static RuntimeAnimatorController SeraphimController { get; private set; }
        internal static AudioClip SeraphimHolyChantClip { get; private set; }
        internal static AudioClip[] SeraphimVoiceClips { get; private set; } = Array.Empty<AudioClip>();
        internal static GameObject SeraphimHolyEffectPrefab { get; private set; }
        internal static GameObject SeraphimExecutionEffectPrefab { get; private set; }
        internal static GameObject SeraphimBloodEffectPrefab { get; private set; }
        internal static AudioClip SeraphimBeamCueClip { get; private set; }
        internal static AudioClip SeraphimExecutionImpactClip { get; private set; }
        internal static EnemyType SeraphimEnemyType { get; private set; }
        internal static bool SeraphimAssetsReady => SeraphimPrefab != null;
        internal static Texture2D SeraphimBodyAlbedoTexture { get; private set; }
        internal static Texture2D SeraphimBodyVar2AlbedoTexture { get; private set; }
        internal static Texture2D SeraphimRingAlbedoTexture { get; private set; }
        internal static Texture2D SeraphimRingVar2AlbedoTexture { get; private set; }
        internal static Texture2D SeraphimEyeAlbedoTexture { get; private set; }
        internal static Texture2D SeraphimEyeVar2AlbedoTexture { get; private set; }
        internal static Texture2D SeraphimEyeEmissionTexture { get; private set; }
        internal static Texture2D SeraphimEyeVar2EmissionTexture { get; private set; }
        internal static Texture2D SeraphimEyelashAlbedoTexture { get; private set; }
        internal static Texture2D SeraphimFeatherAlbedoTexture { get; private set; }
        internal static Texture2D SeraphimBodyNormalTexture { get; private set; }
        internal static Texture2D SeraphimBodyVar2NormalTexture { get; private set; }
        internal static Texture2D SeraphimRingNormalTexture { get; private set; }
        internal static Texture2D SeraphimEyeNormalTexture { get; private set; }
        internal static Texture2D SeraphimEyeVar2NormalTexture { get; private set; }
        internal static Texture2D SeraphimFeatherNormalTexture { get; private set; }
        internal static GameObject BreachDrillSparkEffectPrefab { get; private set; }
        internal static GameObject BreachHardDriveVisualPrefab { get; private set; }
        /// <summary>#758/#759: authored Survey Beacon handheld. Null on an older bundle; the survey spawn then falls back to the Radar Booster.</summary>
        internal static GameObject SurveyBeaconVisualPrefab { get; private set; }
        internal static Sprite SurveyBeaconIconSprite { get; private set; }
        internal static AudioClip BreachContainerDoorOpenClip { get; private set; }
        internal static GameObject BreachContainerVisualPrefab { get; private set; }
        internal static GameObject BreachSmallContainerVisualPrefab { get; private set; }
        internal static GameObject BreachMediumContainerVisualPrefab { get; private set; }
        internal static GameObject BreachLargeContainerVisualPrefab { get; private set; }
        internal static GameObject ContainmentLockdownBarsPrefab { get; private set; }
        internal static Material ContainmentLockdownBarsMaterial { get; private set; }
        internal static Texture2D ContainmentLockdownBarsOpacityTexture { get; private set; }
        internal static GameObject ContainmentLockdownDoorsPrefab { get; private set; }
        internal static Material ContainmentLockdownDoorsMaterial { get; private set; }
        internal static Material ContainmentLockdownDoorsTrimMaterial { get; private set; }

        internal static void Load()
        {
            if (Bundle != null)
                return;

            string path = FindBundlePath();
            if (string.IsNullOrWhiteSpace(path))
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts] {BundleFileName} not found; Whistleblower will use scan marker only.");
                return;
            }

            Bundle = AssetBundle.LoadFromFile(path);
            if (Bundle == null)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts] AssetBundle.LoadFromFile returned null for {path}.");
                return;
            }

            PestControlTrapArtKitPrefab = LoadAssetByName<GameObject>(PestControlTrapArtKitName);
            if (PestControlTrapArtKitPrefab == null)
            {
                ContractsBootstrap.Log?.LogWarning(
                    "[MoonContracts.PestControl] PestControlTrapArtKit is missing from lgucontracts.bundle; " +
                    "the trap will use its primitive visual fallback.");
            }

            PestLeverThirdPersonPushClip = LoadAssetByName<AnimationClip>("Y4NGZ_PestLever_TP_Push");
            PestLeverThirdPersonPullClip = LoadAssetByName<AnimationClip>("Y4NGZ_PestLever_TP_Pull");
            PestLeverFirstPersonPushClip = LoadAssetByName<AnimationClip>("Y4NGZ_PestLever_FP_Push");
            PestLeverFirstPersonPullClip = LoadAssetByName<AnimationClip>("Y4NGZ_PestLever_FP_Pull");
            if (!PestControlLeverAnimationsReady)
            {
                ContractsBootstrap.Log?.LogWarning(
                    "[MoonContracts.PestControl] One or more Pest lever player clips are missing from " +
                    "lgucontracts.bundle; the trap will still function and its handle will still animate.");
            }

            // #393: the CCTVMainframeSupport prefab and its bundle diagnostics moved to
            // LethalCCTV (CctvFixtureAssets), which resolves the same asset for itself.
            // Optional visual only. The network prefab and gameplay footprint are code-owned, so
            // a missing or renamed art prefab falls back procedurally without disabling the contract.
            WasteDisposalIncineratorPrefab = LoadAssetByName<GameObject>("WasteDisposalIncinerator")
                                                ?? LoadAssetByName<GameObject>("WasteDisposalIncineratorKit")
                                                ?? LoadAssetByName<GameObject>("Incinerator");
            WhistleblowerVisualPrefab = LoadAssetByName<GameObject>(WhistleblowerVisualName);
            if (WhistleblowerVisualPrefab == null)
            {
                ContractsBootstrap.Log?.LogWarning("[MoonContracts] Whistleblower visual prefab missing from lgucontracts.bundle.");
                return;
            }

            // The default prefab gates the rest of Load(); the optional vanilla-friendly variant never does.
            WhistleblowerVisualPrefab = SelectConfiguredWhistleblowerVisualPrefab(WhistleblowerVisualPrefab);

            WhistleblowerAvatar = LoadWhistleblowerAvatar();
            if (WhistleblowerAvatar == null)
                ContractsBootstrap.Log?.LogWarning("[MoonContracts] No Avatar found in lgucontracts.bundle; Whistleblower animations may not play.");

            WhistleblowerBaseColor = LoadAssetByName<Texture2D>("Whistleblower_BaseColor");
            if (WhistleblowerBaseColor == null)
                ContractsBootstrap.Log?.LogWarning("[MoonContracts] Whistleblower_BaseColor texture missing from lgucontracts.bundle; runtime material will use prefab material fallback.");

            WhistleblowerController = LoadAssetByName<RuntimeAnimatorController>(WhistleblowerControllerName);
            if (WhistleblowerController == null)
                ContractsBootstrap.Log?.LogWarning("[MoonContracts] Whistleblower animator controller missing from lgucontracts.bundle; runtime clip driver will be used when possible.");

            LoadWhistleblowerAnimationClips();

            BlacksiteSoldierPrefab = SelectConfiguredBlacksiteSoldierPrefab(
                LoadAssetByName<GameObject>(BlacksiteSoldierName));
            string shipBundlePath = Path.Combine(Path.GetDirectoryName(typeof(ContractAssetLoader).Assembly.Location), "blacksite-ship.bundle");
            if (_blacksiteShipBundle == null && File.Exists(shipBundlePath)) _blacksiteShipBundle = AssetBundle.LoadFromFile(shipBundlePath);
            BlacksiteShipPrefab = _blacksiteShipBundle != null ? _blacksiteShipBundle.LoadAsset<GameObject>("BlacksiteShip") : null;
            if (!BlacksiteLandingGeometry.ConfigureShip(BlacksiteShipPrefab != null ? BlacksiteShipPrefab.transform : null, out var geometryError))
            {
                ContractsBootstrap.Log?.LogError("[Blacksite] Ship asset/DLL geometry mismatch: " + geometryError + ". Blacksite deployment is disabled until the matching bundle is installed.");
                BlacksiteShipPrefab = null;
            }
            BlacksiteCratePrefab = LoadConfiguredBlacksiteCratePrefab();
            ShadowRaidSurfaceAssets.ApplyCrate(BlacksiteCratePrefab);
            // Payload Haul cart. Null until PayloadCart is built into lgucontracts.bundle (W1);
            // PayloadContract.TrySpawnCart handles the null gracefully.
            PayloadCartPrefab = LoadAssetByName<GameObject>(PayloadCartName);
            PreparePayloadCartPrefab(PayloadCartPrefab);
            PayloadPushController = LoadAssetByName<RuntimeAnimatorController>("PayloadPushController");
            PayloadPushClip = LoadAssetByName<AnimationClip>("Y4NGZ_Body_Push_Loop");
            if (PayloadPushController == null)
                ContractsBootstrap.Log?.LogWarning("[MoonContracts.Payload] PayloadPushController missing from lgucontracts.bundle; push animation will fall back to the vanilla body anim.");
            BlacksiteSoldierController = LoadAssetByName<RuntimeAnimatorController>(BlacksiteSoldierControllerName);
            LoadBlacksiteTextures();
            LoadBlacksiteAudio();
            BlacksiteSoldierEnemyType = CreateBlacksiteSoldierEnemyType(BlacksiteSoldierPrefab);
            PrepareBlacksitePrefab(BlacksiteSoldierPrefab, BlacksiteSoldierName, soldier: true, transport: false, crate: false);
            PrepareBlacksitePrefab(BlacksiteShipPrefab, "BlacksiteShip", soldier: false, transport: true, crate: false);
            PrepareBlacksitePrefab(BlacksiteCratePrefab, BlacksiteCratePrefab != null ? BlacksiteCratePrefab.name : BlacksiteCrateName, soldier: false, transport: false, crate: true);
            LoadCultistAssets();
            LoadSeraphimAssets();
            LoadShadowRaidAssets();
            LoadSurveyAssets();
            ContainmentLockdownBarsPrefab = LoadAssetByName<GameObject>(ContainmentLockdownBarsName)
                ?? LoadAssetByName<GameObject>(ContainmentLockdownBarsModelName);
            ContainmentLockdownBarsMaterial = LoadAssetByName<Material>(ContainmentLockdownBarsName);
            ContainmentLockdownBarsOpacityTexture = LoadAssetByName<Texture2D>(ContainmentLockdownBarsOpacityTextureName);
            if (ContainmentLockdownBarsPrefab == null)
            {
                ContractsBootstrap.Log?.LogWarning("[MoonContracts] ContainmentLockdownBars visual missing from lgucontracts.bundle; containment lockdown gates will not spawn.");
            }
            else
            {
                int rendererCount = 0;
                try
                {
                    rendererCount = ContainmentLockdownBarsPrefab.GetComponentsInChildren<Renderer>(true)?.Length ?? 0;
                }
                catch
                {
                }

                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts] ContainmentLockdownBars visual loaded: prefab='{ContainmentLockdownBarsPrefab.name}', " +
                    $"renderers={rendererCount}, material={(ContainmentLockdownBarsMaterial != null ? ContainmentLockdownBarsMaterial.name : "none")}, " +
                    $"opacity={(ContainmentLockdownBarsOpacityTexture != null ? ContainmentLockdownBarsOpacityTexture.name : "none")}.");
            }

            LoadContainmentLockdownDoorAssets();

            ContractsBootstrap.Log?.LogInfo($"[MoonContracts] Loaded contract assets from {path}.");
        }

        internal static GameObject GetCultistPrefab(CultistRole role)
        {
            switch (role)
            {
                case CultistRole.Red:
                    return CultistRedPrefab;
                case CultistRole.White:
                    return CultistWhitePrefab;
                default:
                    return CultistBlackPrefab;
            }
        }

        internal static bool WhistleblowerAnimationClipsReady =>
            WhistleblowerNervousIdleClip != null
            && WhistleblowerCombatIdleClip != null
            && WhistleblowerBreathingIdleClip != null
            && WhistleblowerWalkingClip != null
            && WhistleblowerRunningClip != null
            && WhistleblowerBoxingForwardClip != null
            && WhistleblowerBoxingBackwardClip != null
            && WhistleblowerBoxingLeftClip != null
            && WhistleblowerBoxingRightClip != null
            && WhistleblowerStepBackClip != null
            && WhistleblowerPunchLeftClip != null
            && WhistleblowerPunchRightClip != null
            && WhistleblowerEvadeClip != null
            && WhistleblowerHeadHitClip != null
            && WhistleblowerBodyHitClip != null
            && WhistleblowerRibHitClip != null
            && WhistleblowerFallingDownClip != null
            && WhistleblowerFallFlatClip != null
            && WhistleblowerCrawlBackwardsClip != null
            && WhistleblowerGetUpFrontClip != null
            && WhistleblowerGetUpBackClip != null
            && WhistleblowerStumbleClip != null
            && WhistleblowerDeathClip != null;

        internal static bool PestControlLeverAnimationsReady =>
            PestLeverThirdPersonPushClip != null &&
            PestLeverThirdPersonPullClip != null &&
            PestLeverFirstPersonPushClip != null &&
            PestLeverFirstPersonPullClip != null;

        private static void LoadWhistleblowerAnimationClips()
        {
            WhistleblowerNervousIdleClip = LoadAssetByName<AnimationClip>("Whistleblower_NervousIdle");
            WhistleblowerCombatIdleClip = LoadAssetByName<AnimationClip>("Whistleblower_CombatIdle");
            WhistleblowerBreathingIdleClip = LoadAssetByName<AnimationClip>("Whistleblower_BreathingIdle");
            WhistleblowerWalkingClip = LoadAssetByName<AnimationClip>("Whistleblower_Walking");
            WhistleblowerRunningClip = LoadAssetByName<AnimationClip>("Whistleblower_Running");
            WhistleblowerBoxingForwardClip = LoadAssetByName<AnimationClip>("Whistleblower_BoxingForward");
            WhistleblowerBoxingBackwardClip = LoadAssetByName<AnimationClip>("Whistleblower_BoxingBackward");
            WhistleblowerBoxingLeftClip = LoadAssetByName<AnimationClip>("Whistleblower_BoxingLeft");
            WhistleblowerBoxingRightClip = LoadAssetByName<AnimationClip>("Whistleblower_BoxingRight");
            WhistleblowerStepBackClip = LoadAssetByName<AnimationClip>("Whistleblower_StepBack");
            WhistleblowerPunchLeftClip = LoadAssetByName<AnimationClip>("Whistleblower_PunchLeft");
            WhistleblowerPunchRightClip = LoadAssetByName<AnimationClip>("Whistleblower_PunchRight");
            WhistleblowerEvadeClip = LoadAssetByName<AnimationClip>("Whistleblower_Evade");
            WhistleblowerHeadHitClip = LoadAssetByName<AnimationClip>("Whistleblower_HeadHit");
            WhistleblowerBodyHitClip = LoadAssetByName<AnimationClip>("Whistleblower_BodyHit");
            WhistleblowerRibHitClip = LoadAssetByName<AnimationClip>("Whistleblower_RibHit");
            WhistleblowerFallingDownClip = LoadAssetByName<AnimationClip>("Whistleblower_FallingDown");
            WhistleblowerFallFlatClip = LoadAssetByName<AnimationClip>("Whistleblower_FallFlat");
            WhistleblowerCrawlBackwardsClip = LoadAssetByName<AnimationClip>("Whistleblower_CrawlBackwards");
            WhistleblowerGetUpFrontClip = LoadAssetByName<AnimationClip>("Whistleblower_GetUpFront");
            WhistleblowerGetUpBackClip = LoadAssetByName<AnimationClip>("Whistleblower_GetUpBack");
            WhistleblowerStumbleClip = LoadAssetByName<AnimationClip>("Whistleblower_Stumble");
            WhistleblowerDeathClip = LoadAssetByName<AnimationClip>("Whistleblower_Death");

            if (WhistleblowerVisualIsVanillaVariant && WhistleblowerVisualAssets.VanillaPrefab != null)
            {
                WhistleblowerCombatIdleClip = WhistleblowerVisualAssets.CombatIdle;
                WhistleblowerPunchLeftClip = WhistleblowerVisualAssets.PunchLeft;
                WhistleblowerPunchRightClip = WhistleblowerVisualAssets.PunchRight;
            }

            if (!WhistleblowerAnimationClipsReady)
            {
                ContractsBootstrap.Log?.LogWarning(
                    "[MoonContracts] Whistleblower 23-clip animation set is incomplete; " +
                    "the runtime playable driver will stay disabled.");
            }
        }

        internal static GameObject GetPreparedInteriorSupportPrefab(string assetName)
        {
            if (string.IsNullOrWhiteSpace(assetName) || Bundle == null)
                return null;

            // Preserve the former diagnostic prefab key after the runtime prefab was
            // given a unique name to avoid colliding with the source FBX in the bundle.
            if (string.Equals(assetName, WhistleblowerControllerName, StringComparison.OrdinalIgnoreCase))
                assetName = WhistleblowerVisualName;

            GameObject prefab = LoadAssetByName<GameObject>(assetName);
            if (prefab == null)
                return null;

            if (PreparedInteriorSupportPrefabs.Add(assetName))
                PrepareInteriorSupportPrefab(prefab, assetName);

            return prefab;
        }

        internal static GameObject GetBreachContainerVisualPrefab(string sizeName)
        {
            string normalized = string.IsNullOrWhiteSpace(sizeName)
                ? "medium"
                : sizeName.Trim().ToLowerInvariant();

            switch (normalized)
            {
                case "s":
                case "small":
                case "50":
                case "0.5":
                    return BreachSmallContainerVisualPrefab
                           ?? BreachMediumContainerVisualPrefab
                           ?? BreachContainerVisualPrefab;
                case "l":
                case "large":
                case "100":
                case "1":
                    return BreachLargeContainerVisualPrefab
                           ?? BreachMediumContainerVisualPrefab
                           ?? BreachContainerVisualPrefab;
                default:
                    return BreachMediumContainerVisualPrefab
                           ?? BreachContainerVisualPrefab
                           ?? BreachSmallContainerVisualPrefab;
            }
        }

        internal static bool PrepareRuntimeInteriorSupportPrefab(GameObject prefab, string assetName)
        {
            if (prefab == null || string.IsNullOrWhiteSpace(assetName))
                return false;

            if (PreparedInteriorSupportPrefabs.Add(assetName))
                return PrepareInteriorSupportPrefab(prefab, assetName);

            return IsInteriorSupportPrefabRegistered(prefab);
        }

        internal static bool IsInteriorSupportPrefabRegistered(GameObject prefab)
        {
            return prefab != null && RegisteredInteriorSupportPrefabs.Contains(prefab.GetInstanceID());
        }

        internal static AudioClip GetBundledAudioClip(params string[] names)
        {
            if (names == null)
                return null;

            for (int i = 0; i < names.Length; i++)
            {
                AudioClip clip = LoadAudioClip(names[i]);
                if (clip != null)
                    return clip;
            }

            return null;
        }

        internal static AudioClip[] GetBlacksiteVoiceClips(BlacksiteVoiceEvent voiceEvent)
        {
            return BlacksiteVoiceClips.TryGetValue(voiceEvent, out AudioClip[] clips)
                ? clips
                : Array.Empty<AudioClip>();
        }

        private static string FindBundlePath()
        {
            string assemblyDir = Path.GetDirectoryName(typeof(ContractsBootstrap).Assembly.Location) ?? string.Empty;
            string[] candidates =
            {
                Path.Combine(assemblyDir, BundleFileName),
                Path.Combine(Paths.PluginPath, "y4ngz-Y4NGZCompany", BundleFileName),
                Path.Combine(Paths.PluginPath, "y4ngz-Y4NGZUpgrades", BundleFileName),
                Path.Combine(Paths.PluginPath, BundleFileName),
            };

            return candidates.FirstOrDefault(File.Exists);
        }

        private static T LoadAssetByName<T>(string assetName) where T : UnityEngine.Object
        {
            return LoadAssetByName<T>(assetName, preferredPathFragment: null);
        }

        private static T LoadAssetByName<T>(string assetName, string preferredPathFragment) where T : UnityEngine.Object
        {
            if (Bundle == null)
                return null;

            string[] bundledNames = Bundle.GetAllAssetNames();
            if (!string.IsNullOrEmpty(preferredPathFragment))
            {
                string normalizedFragment = preferredPathFragment.Replace('\\', '/');
                foreach (string bundledName in bundledNames)
                {
                    string normalizedName = bundledName.Replace('\\', '/');
                    string fileName = Path.GetFileNameWithoutExtension(normalizedName);
                    if (!string.Equals(fileName, assetName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (normalizedName.IndexOf(normalizedFragment, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    T preferred = Bundle.LoadAsset<T>(bundledName);
                    if (preferred != null)
                        return preferred;
                }
            }

            if (string.IsNullOrEmpty(preferredPathFragment))
            {
                T direct = Bundle.LoadAsset<T>(assetName);
                if (direct != null)
                    return direct;
            }

            foreach (string bundledName in bundledNames)
            {
                string fileName = Path.GetFileNameWithoutExtension(bundledName);
                if (!string.Equals(fileName, assetName, StringComparison.OrdinalIgnoreCase))
                    continue;

                T asset = Bundle.LoadAsset<T>(bundledName);
                if (asset != null)
                    return asset;
            }

            return null;
        }

        private static GameObject SelectConfiguredWhistleblowerVisualPrefab(GameObject defaultPrefab)
        {
            WhistleblowerVisualIsVanillaVariant = false;
            if (ContractsBootstrap.WhistleblowerBalance == null
                || !ContractsBootstrap.WhistleblowerBalance.UseVanillaFriendlyModel.Value)
                return defaultPrefab;

            WhistleblowerVisualAssets.Load();
            GameObject variant = WhistleblowerVisualAssets.VanillaPrefab
                ?? LoadAssetByName<GameObject>(WhistleblowerVanillaVisualName);
            if (variant == null)
            {
                ContractsBootstrap.Log?.LogWarning(
                    $"[MoonContracts] 'Use Vanilla-Friendly Model' is on but '{WhistleblowerVanillaVisualName}' is not in {BundleFileName}; " +
                    $"falling back to '{WhistleblowerVisualName}'. The variant has to be built in the Unity project first " +
                    "(Y4NGZ/Contracts/Whistleblower/Build Vanilla Variant, then rebuild the bundle and copy it to runtime-assets).");
                return defaultPrefab;
            }

            WhistleblowerVisualIsVanillaVariant = true;
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts] Whistleblower visual prefab selected: '{variant.name}' (vanilla-friendly variant).");
            return variant;
        }

        private static GameObject SelectConfiguredBlacksiteSoldierPrefab(GameObject defaultPrefab)
        {
            const string variantName = "BlacksiteSoldierVanilla";
            if (!(ContractsBootstrap.BlacksiteRetrievalUseVanillaFriendlyModel?.Value ?? true))
                return defaultPrefab;

            GameObject variant = LoadAssetByName<GameObject>(variantName);
            if (variant == null)
            {
                ContractsBootstrap.Log?.LogWarning(
                    $"[MoonContracts.Blacksite] '{variantName}' is absent from {BundleFileName}; " +
                    $"falling back to '{BlacksiteSoldierName}'.");
                return defaultPrefab;
            }

            // PrepareBlacksitePrefab still validates the authored rig and registers the
            // canonical BlacksiteSoldier network hash, independent of each client's look.
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Blacksite] Soldier visual selected: '{variant.name}'.");
            return variant;
        }

        private static GameObject LoadConfiguredBlacksiteCratePrefab()
        {
            string configuredName = ContractsBootstrap.ShadowRaidRewardPrefabName?.Value;
            if (string.IsNullOrWhiteSpace(configuredName))
                configuredName = BlacksiteCrateName;

            GameObject prefab = null;
            if (!string.Equals(configuredName, BlacksiteCrateName, StringComparison.OrdinalIgnoreCase))
            {
                prefab = LoadAssetByName<GameObject>(configuredName);
                if (prefab == null)
                {
                    ContractsBootstrap.Log?.LogWarning($"[MoonContracts] Configured Blacksite reward/cache prefab '{configuredName}' was not found in {BundleFileName}; falling back to '{BlacksiteCrateName}'.");
                }
            }

            prefab = prefab ?? LoadAssetByName<GameObject>(BlacksiteCrateName);
            if (prefab != null)
            {
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts] Blacksite reward/cache prefab selected: '{prefab.name}'" +
                    $"{(!string.Equals(configuredName, prefab.name, StringComparison.OrdinalIgnoreCase) ? $" (requested '{configuredName}')" : string.Empty)}.");
            }

            return prefab;
        }

        private static void PrepareBlacksitePrefab(GameObject prefab, string name, bool soldier, bool transport, bool crate)
        {
            if (prefab == null)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts] {name} prefab missing from {BundleFileName}; Blacksite Retrieval will skip that asset.");
                return;
            }

            var networkObject = prefab.GetComponent<NetworkObject>();
            if (networkObject == null)
                networkObject = prefab.AddComponent<NetworkObject>();
            AssignStableNetworkHash(networkObject, name);
            networkObject.SynchronizeTransform = true;

            if (soldier)
            {
                RemoveDuplicateEnemyTransformAuthority(prefab, "Blacksite Soldier");
                if (!BlacksiteSoldierRigBinding.TryBind(prefab, out BlacksiteSoldierRigBinding rig, out string rigFailure))
                {
                    ContractsBootstrap.Log?.LogError(
                        $"[MoonContracts.Blacksite] Soldier prefab rejected because its authored rig contract is invalid: {rigFailure}. " +
                        "No runtime fuzzy repair will be attempted.");
                    if (BlacksiteSoldierEnemyType != null)
                        BlacksiteSoldierEnemyType.enemyPrefab = null;
                    BlacksiteSoldierEnemyType = null;
                    BlacksiteSoldierPrefab = null;
                    return;
                }
                SetTagSafe(prefab, "Enemy");
                prefab.layer = ResolveLayer("Enemies", 19);

                var agent = prefab.GetComponent<NavMeshAgent>();
                if (agent == null)
                    agent = prefab.AddComponent<NavMeshAgent>();
                agent.speed = 3.6f;
                agent.angularSpeed = 540f;
                agent.acceleration = 18f;
                agent.stoppingDistance = 7.5f;
                agent.radius = 0.45f;
                agent.height = 2.65f;
                agent.autoBraking = true;
                agent.updateRotation = true;
                agent.obstacleAvoidanceType = ObstacleAvoidanceType.HighQualityObstacleAvoidance;
                agent.enabled = false;

                var ai = prefab.GetComponent<BlacksiteSoldierAI>();
                if (ai == null)
                    ai = prefab.AddComponent<BlacksiteSoldierAI>();
                ai.enemyType = BlacksiteSoldierEnemyType;
                ai.agent = agent;
                ai.creatureAnimator = rig.Animator;
                ai.creatureSFX = rig.WeaponSource;
                ai.creatureVoice = rig.VoiceSource;
                ai.enemyBehaviourStates = CreateBlacksiteBehaviourStates();

                foreach (Animator animator in prefab.GetComponentsInChildren<Animator>(true))
                {
                    if (animator != null && animator.runtimeAnimatorController == null && BlacksiteSoldierController != null)
                        animator.runtimeAnimatorController = BlacksiteSoldierController;
                }

                EnsureSoldierCombatBindings(ai, rig);
                EnsureSoldierHitDetection(ai, rig);
                EnsureSoldierBodyCollision(ai);
            }
            else if (prefab.GetComponent<NetworkTransform>() == null)
            {
                prefab.AddComponent<NetworkTransform>();
            }

            if (transport)
            {
                if (prefab.GetComponent<BlacksiteShipController>() == null)
                    prefab.AddComponent<BlacksiteShipController>();
                if (prefab.GetComponent<BlacksiteRetrievalMissionController>() == null)
                    prefab.AddComponent<BlacksiteRetrievalMissionController>();
            }

            if (crate && prefab.GetComponent<ShadowRaidCargoCrate>() == null)
                prefab.AddComponent<ShadowRaidCargoCrate>();

            int prefabId = prefab.GetInstanceID();
            if (!RegisteredBlacksiteNetworkPrefabs.Add(prefabId))
                return;

            try
            {
                LethalLib.Modules.NetworkPrefabs.RegisterNetworkPrefab(prefab);
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts] Registered Blacksite network prefab '{name}'.");
            }
            catch (Exception e)
            {
                RegisteredBlacksiteNetworkPrefabs.Remove(prefabId);
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts] Blacksite prefab '{name}' registration skipped: {e.Message}");
            }
        }

        private static void RemoveDuplicateEnemyTransformAuthority(GameObject prefab, string label)
        {
            NetworkTransform[] transforms = prefab.GetComponentsInChildren<NetworkTransform>(true);
            if (transforms == null || transforms.Length == 0)
                return;

            foreach (NetworkTransform networkTransform in transforms)
            {
                if (networkTransform != null)
                    UnityEngine.Object.DestroyImmediate(networkTransform, allowDestroyingAssets: true);
            }

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts] Removed {transforms.Length} NetworkTransform component(s) from {label}; EnemyAI owns steady-state transform replication.");
        }

        private static bool PrepareInteriorSupportPrefab(GameObject prefab, string assetName)
        {
            if (prefab == null)
                return false;

            var networkObject = prefab.GetComponent<NetworkObject>();
            if (networkObject == null)
                networkObject = prefab.AddComponent<NetworkObject>();
            AssignStableNetworkHash(networkObject, "InteriorSupport", assetName);
            networkObject.SynchronizeTransform = true;

            if (prefab.GetComponent<NetworkTransform>() == null)
                prefab.AddComponent<NetworkTransform>();

            try
            {
                LethalLib.Modules.NetworkPrefabs.RegisterNetworkPrefab(prefab);
                RegisteredInteriorSupportPrefabs.Add(prefab.GetInstanceID());
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts] Registered interior support network prefab '{assetName}'.");
                return true;
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts] Interior support prefab '{assetName}' registration skipped: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Payload Haul cart. The bundled asset is visual-only (mesh + materials + HandleGrab/MagnetPoint
        /// children), so every runtime component is attached to the PREFAB here, at plugin init on host and
        /// client alike, and the prefab is registered with LethalLib. Adding the NetworkObject to the spawned
        /// instance instead left GlobalObjectIdHash at 0: clients received a CreateObjectMessage they could
        /// not resolve and the cart existed on the host only (#434).
        /// </summary>
        private static void PreparePayloadCartPrefab(GameObject prefab)
        {
            if (prefab == null)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Payload] {PayloadCartName} prefab missing from {BundleFileName}; the Payload Haul contract will skip the cart spawn.");
                return;
            }

            if (prefab.GetComponent<PayloadCartController>() == null)
                prefab.AddComponent<PayloadCartController>();

            if (prefab.GetComponent<PayloadCartVisualJuice>() == null)
                prefab.AddComponent<PayloadCartVisualJuice>();

            if (prefab.GetComponent<PayloadCartAudio>() == null)
                prefab.AddComponent<PayloadCartAudio>();

            if (prefab.GetComponent<BoxCollider>() == null)
                AddPayloadCartBoundsCollider(prefab);

            var networkObject = prefab.GetComponent<NetworkObject>();
            if (networkObject == null)
                networkObject = prefab.AddComponent<NetworkObject>();
            AssignStableNetworkHash(networkObject, "Payload", PayloadCartName);
            networkObject.SynchronizeTransform = true;

            if (prefab.GetComponent<NetworkTransform>() == null)
                prefab.AddComponent<NetworkTransform>();

            try
            {
                LethalLib.Modules.NetworkPrefabs.RegisterNetworkPrefab(prefab);
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts] Registered payload cart network prefab '{PayloadCartName}'.");
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts] Payload cart prefab '{PayloadCartName}' registration skipped: {e.Message}");
            }
        }

        /// <summary>
        /// Root collider for the payload cart, encapsulating the authored child renderers in root-local space
        /// (orientation-agnostic vs. the FBX import axes). Driven off the shared meshes rather than
        /// <see cref="Renderer.bounds"/> because this now runs on the prefab asset, which is never in a scene.
        /// TODO(W1): constrain to the cage body (~0.74 W x 1.30 L x 1.20 H), excluding the handle overhang.
        /// </summary>
        private static void AddPayloadCartBoundsCollider(GameObject prefab)
        {
            Renderer[] renderers = prefab.GetComponentsInChildren<Renderer>(includeInactive: true);
            Matrix4x4 worldToRoot = prefab.transform.worldToLocalMatrix;
            bool initialized = false;
            Bounds local = new Bounds(Vector3.zero, Vector3.zero);
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null || !TryResolveRendererMeshBounds(renderer, out Bounds meshBounds))
                    continue;

                Matrix4x4 rendererToRoot = worldToRoot * renderer.transform.localToWorldMatrix;
                Vector3 min = meshBounds.min;
                Vector3 max = meshBounds.max;
                Vector3[] corners =
                {
                    new Vector3(min.x, min.y, min.z),
                    new Vector3(min.x, min.y, max.z),
                    new Vector3(min.x, max.y, min.z),
                    new Vector3(min.x, max.y, max.z),
                    new Vector3(max.x, min.y, min.z),
                    new Vector3(max.x, min.y, max.z),
                    new Vector3(max.x, max.y, min.z),
                    new Vector3(max.x, max.y, max.z),
                };

                foreach (Vector3 corner in corners)
                {
                    Vector3 rootPoint = rendererToRoot.MultiplyPoint3x4(corner);
                    if (!initialized)
                    {
                        local = new Bounds(rootPoint, Vector3.zero);
                        initialized = true;
                    }
                    else
                    {
                        local.Encapsulate(rootPoint);
                    }
                }
            }

            BoxCollider box = prefab.AddComponent<BoxCollider>();
            if (!initialized)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Payload] {PayloadCartName} prefab exposed no readable mesh bounds; the cart collider keeps the BoxCollider default extents.");
                return;
            }

            box.center = local.center;
            box.size = local.size;
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Payload] {PayloadCartName} prefab collider fitted: center={box.center}, size={box.size}.");
        }

        private static bool TryResolveRendererMeshBounds(Renderer renderer, out Bounds bounds)
        {
            bounds = default;
            if (renderer is SkinnedMeshRenderer skinned)
            {
                if (skinned.sharedMesh == null)
                    return false;
                bounds = skinned.sharedMesh.bounds;
                return true;
            }

            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null)
                return false;

            bounds = filter.sharedMesh.bounds;
            return true;
        }

        private static EnemyType CreateBlacksiteSoldierEnemyType(GameObject prefab)
        {
            if (prefab == null)
                return null;

            var enemyType = ScriptableObject.CreateInstance<EnemyType>();
            enemyType.enemyName = "Blacksite Soldier";
            enemyType.enemyPrefab = prefab;
            enemyType.PowerLevel = 0f;
            enemyType.MaxCount = 10;
            enemyType.canDie = true;
            enemyType.canBeStunned = true;
            enemyType.canBeDestroyed = false;
            enemyType.destroyOnDeath = false;
            enemyType.stunTimeMultiplier = 0.75f;
            enemyType.doorSpeedMultiplier = 1.25f;
            enemyType.stunGameDifficultyMultiplier = 1f;
            enemyType.pushPlayerForce = 0f;
            enemyType.pushPlayerDistance = 0f;
            enemyType.disableAnimatorWhenFar = false;
            enemyType.isOutsideEnemy = true;
            enemyType.isDaytimeEnemy = false;
            enemyType.probabilityCurve = AnimationCurve.Constant(0f, 1f, 1f);
            enemyType.numberSpawnedFalloff = AnimationCurve.Linear(0f, 1f, 1f, 0f);
            enemyType.useNumberSpawnedFalloff = false;
            return enemyType;
        }

        private static void LoadCultistAssets()
        {
            if (!TryLoadCongregantVisualBundle())
            {
                CultistRedController = LoadAssetByName<RuntimeAnimatorController>("CultistRed");
                CultistBlackController = LoadAssetByName<RuntimeAnimatorController>("CultistBlack");
                CultistWhiteController = LoadAssetByName<RuntimeAnimatorController>("CultistWhite");
                CultistRedPrefab = LoadLegacyCultistPrefab(CultistRedName, CultistRedController);
                CultistBlackPrefab = LoadLegacyCultistPrefab(CultistBlackName, CultistBlackController);
                CultistWhitePrefab = LoadLegacyCultistPrefab(CultistWhiteName, CultistWhiteController);
            }

            CultistRedEnemyType = CreateCultistEnemyType(CultistRedPrefab, "Red Cultist", 8);
            CultistBlackEnemyType = CreateCultistEnemyType(CultistBlackPrefab, "Black Cultist", 8);
            CultistWhiteEnemyType = CreateCultistEnemyType(CultistWhitePrefab, "White Cultist", 3);
            LoadCultistAudio();

            PrepareCultistPrefab(CultistRedPrefab, CultistRedName, CultistRedEnemyType, CultistRedController, CultistRole.Red);
            PrepareCultistPrefab(CultistBlackPrefab, CultistBlackName, CultistBlackEnemyType, CultistBlackController, CultistRole.Black);
            PrepareCultistPrefab(CultistWhitePrefab, CultistWhiteName, CultistWhiteEnemyType, CultistWhiteController, CultistRole.White);

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts] Cultist assets loaded: red={(CultistRedPrefab != null)}, black={(CultistBlackPrefab != null)}, white={(CultistWhitePrefab != null)}.");
        }

        private static GameObject LoadLegacyCultistPrefab(string name, RuntimeAnimatorController controller)
        {
            GameObject prefab = LoadAssetByName<GameObject>(name);
            if (prefab == null)
                return null;

            // The legacy set has a unit actor root and an independently scaled Visual child.
            // Sample Idle: the saved bind pose and the renderer's padded bounds are not its standing height.
            Transform visual = prefab.transform.Find("Visual");
            SkinnedMeshRenderer body = visual != null ? visual.GetComponentInChildren<SkinnedMeshRenderer>(true) : null;
            AnimationClip idle = controller != null ? Array.Find(controller.animationClips, clip => clip != null && clip.name == "Idle") : null;
            if (visual == null || body == null || idle == null)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Cult] Legacy {name} lacks its Visual, body or Idle clip; refusing a mismatched collision fallback.");
                return null;
            }

            var baked = new Mesh();
            Vector3 originalScale = visual.localScale;
            bool normalized = false;
            try
            {
                // BakeMesh includes ancestor scale; measure at unit Visual scale so applying
                // body-to-root coordinates cannot count the legacy Visual scale twice.
                visual.localScale = Vector3.one;
                idle.SampleAnimation(visual.gameObject, 0f);
                body.BakeMesh(baked);
                // Baked bounds are conservative skin bounds, not the posed vertex extrema.
                float minimumY = float.PositiveInfinity, maximumY = float.NegativeInfinity;
                Matrix4x4 bodyToRoot = prefab.transform.worldToLocalMatrix * body.transform.localToWorldMatrix;
                foreach (Vector3 vertex in baked.vertices)
                {
                    float y = bodyToRoot.MultiplyPoint3x4(vertex).y;
                    minimumY = Mathf.Min(minimumY, y);
                    maximumY = Mathf.Max(maximumY, y);
                }
                float height = maximumY - minimumY;
                if (height <= 0f || float.IsNaN(height) || float.IsInfinity(height))
                {
                    ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Cult] Legacy {name} has no measurable Idle height; refusing a mismatched collision fallback.");
                    return null;
                }
                visual.localScale = Vector3.one * (2.62f / height);
                normalized = true;
            }
            finally
            {
                if (!normalized)
                    visual.localScale = originalScale;
                UnityEngine.Object.DestroyImmediate(baked);
            }
            return prefab;
        }

        // Sidecar clip contract: every clip is `<Role>Congregant_<Clip>_v<Revision>`. The revision is
        // read from the clip names because a RuntimeAnimatorController exposes no parameter defaults
        // (VisualRevision) without a live Animator.
        private const int MinimumCongregantVisualRevision = 12;
        private static readonly string[] RequiredCongregantClips =
        {
            "Idle", "Walk", "Run", "PrayEnter", "PrayLoop", "PrayExit",
            "Hit", "Death", "Flee", "KnifeInward", "KnifeOutward", "DrumSwing"
        };

        private static bool TryLoadCongregantVisualBundle()
        {
            string path = Path.Combine(Path.GetDirectoryName(typeof(ContractAssetLoader).Assembly.Location) ?? string.Empty, "y4ngz-congregants.bundle");
            if (!File.Exists(path))
            {
                ContractsBootstrap.Log?.LogWarning("[MoonContracts.Cult] Congregant visual bundle not found at " + path + "; retaining the legacy role set.");
                return false;
            }
            AssetBundle candidate = null;
            try
            {
                candidate = AssetBundle.LoadFromFile(path);
                if (candidate == null) throw new InvalidDataException("Bundle could not be loaded");
                var roles = new[] { "red", "black", "white" };
                var prefabs = roles.Select(role => candidate.LoadAsset<GameObject>("cultist" + role + ".prefab")).ToArray();
                var controllers = roles.Select(role => candidate.LoadAsset<RuntimeAnimatorController>("cultist" + role + ".controller")).ToArray();
                for (int i = 0; i < roles.Length; i++)
                {
                    if (prefabs[i] == null || prefabs[i].GetComponentInChildren<Animator>(true) == null)
                        throw new InvalidDataException("cultist" + roles[i] + ".prefab is missing or has no Animator");
                    if (controllers[i] == null)
                        throw new InvalidDataException("cultist" + roles[i] + ".controller is missing");
                    if (!HasCompleteCongregantClips(controllers[i], out string problem))
                        throw new InvalidDataException("cultist" + roles[i] + ".controller " + problem);
                }
                CultistRedPrefab = prefabs[0]; CultistBlackPrefab = prefabs[1]; CultistWhitePrefab = prefabs[2];
                CultistRedController = controllers[0]; CultistBlackController = controllers[1]; CultistWhiteController = controllers[2];
                _congregantBundle = candidate;
                ContractsBootstrap.Log?.LogInfo("[MoonContracts.Cult] Loaded complete congregant visuals and animations for all three roles.");
                return true;
            }
            catch (Exception e)
            {
                if (candidate != null) candidate.Unload(true);
                ContractsBootstrap.Log?.LogWarning("[MoonContracts.Cult] Congregant visual bundle rejected; retaining the complete legacy role set: " + e.Message);
                return false;
            }
        }

        /// <summary>True when the controller carries all twelve contract clips at VisualRevision
        /// <see cref="MinimumCongregantVisualRevision"/> or later; otherwise
        /// <paramref name="problem"/> names the missing and outdated clips.</summary>
        private static bool HasCompleteCongregantClips(RuntimeAnimatorController controller, out string problem)
        {
            var revisions = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (AnimationClip clip in controller.animationClips)
            {
                if (clip == null || !TryParseCongregantClipName(clip.name, out string motion, out int revision))
                    continue;
                revisions[motion] = revisions.TryGetValue(motion, out int seen) ? Math.Min(seen, revision) : revision;
            }

            string[] missing = RequiredCongregantClips.Where(name => !revisions.ContainsKey(name)).ToArray();
            string[] outdated = RequiredCongregantClips
                .Where(name => revisions.TryGetValue(name, out int revision) && revision < MinimumCongregantVisualRevision)
                .Select(name => name + " v" + revisions[name])
                .ToArray();
            if (missing.Length == 0 && outdated.Length == 0)
            {
                problem = null;
                return true;
            }

            var parts = new List<string>(2);
            if (missing.Length > 0) parts.Add("lacks clips " + string.Join(", ", missing));
            if (outdated.Length > 0) parts.Add("has clips below VisualRevision " + MinimumCongregantVisualRevision + ": " + string.Join(", ", outdated));
            problem = string.Join("; ", parts);
            return false;
        }

        private static bool TryParseCongregantClipName(string name, out string motion, out int revision)
        {
            const string Marker = "Congregant_";
            motion = null;
            revision = 0;
            int start = name.IndexOf(Marker, StringComparison.Ordinal);
            int suffix = name.LastIndexOf("_v", StringComparison.Ordinal);
            if (start < 0 || suffix <= start + Marker.Length
                || !int.TryParse(name.Substring(suffix + 2), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out revision))
                return false;
            start += Marker.Length;
            motion = name.Substring(start, suffix - start);
            return true;
        }

        private static void LoadSeraphimAssets()
        {
            if (!TryLoadSeraphimVisualBundle())
            {
                SeraphimPrefab = LoadAssetByName<GameObject>(SeraphimLivePrefabName, SeraphimPreferredPathFragment)
                                 ?? LoadAssetByName<GameObject>(SeraphimPrefabName, SeraphimPreferredPathFragment);
                SeraphimController = LoadAssetByName<RuntimeAnimatorController>(SeraphimControllerName);
                LoadSeraphimTextures();
            }
            SeraphimEyePartPrefab = null; // #816: production uses the validated animated full mesh.
            SeraphimFirstRingPartPrefab = null; // #816: production uses the validated animated full mesh.
            SeraphimSecondRingPartPrefab = null; // #816: production uses the validated animated full mesh.
            SeraphimThirdRingPartPrefab = null; // #816: production uses the validated animated full mesh.
            SeraphimFeathersPartPrefab = null; // #816: production uses the validated animated full mesh.
            SeraphimHolyEffectPrefab = LoadAssetByName<GameObject>(SeraphimHolyEffectName);
            SeraphimExecutionEffectPrefab = LoadAssetByName<GameObject>(SeraphimExecutionEffectName);
            SeraphimBloodEffectPrefab = LoadAssetByName<GameObject>(SeraphimBloodEffectName);
            SeraphimHolyChantClip = LoadContractAudioClip("Seraphim_HolyChant");
            SeraphimBeamCueClip = LoadContractAudioClip(SeraphimBeamCueName);
            SeraphimExecutionImpactClip = LoadContractAudioClip(SeraphimExecutionImpactName)
                                          ?? LoadContractAudioClip("universfield-fast-body-fall-impact-352725");
            SeraphimVoiceClips = LoadContractAudioClips(
                "Seraphim_Voice1",
                "Seraphim_Voice2",
                "Seraphim_Voice3",
                "Seraphim_Voice4",
                "Seraphim_Voice5",
                "SeraphimVoice5",
                "Seraphim_Voice6",
                "Seraphim_Voice7",
                "Seraphim_Voice8",
                "Seraphim_Voice9",
                "Seraphim_Voice10",
                "Seraphim_Voice11");

            SeraphimEnemyType = CreateSeraphimEnemyType(SeraphimPrefab);
            PrepareSeraphimPrefab(SeraphimPrefab);
            PrewarmSeraphimPrefab(SeraphimPrefab);
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts] Seraphim enemy assets loaded: prefab={(SeraphimPrefab != null)}, enemyType={(SeraphimEnemyType != null)}, controller={(SeraphimController != null)}, " +
                $"parts eye={(SeraphimEyePartPrefab != null)}, rings={(SeraphimFirstRingPartPrefab != null && SeraphimSecondRingPartPrefab != null && SeraphimThirdRingPartPrefab != null)}, feathers={(SeraphimFeathersPartPrefab != null)}, " +
                $"chant={(SeraphimHolyChantClip != null)}, voices={SeraphimVoiceClips.Length}, holyEffect={(SeraphimHolyEffectPrefab != null)}, executionEffect={(SeraphimExecutionEffectPrefab != null)}, " +
                $"bloodEffect={(SeraphimBloodEffectPrefab != null)}, beamCue={(SeraphimBeamCueClip != null)}, executionImpact={(SeraphimExecutionImpactClip != null)}, " +
                $"eyeAlbedo={(SeraphimEyeAlbedoTexture != null ? SeraphimEyeAlbedoTexture.name : "none")}, eyeEmission={(SeraphimEyeEmissionTexture != null ? SeraphimEyeEmissionTexture.name : "none")}, eyeNormal={(SeraphimEyeNormalTexture != null ? SeraphimEyeNormalTexture.name : "none")}.");
        }

        private static void LoadSeraphimTextures()
        {
            SeraphimBodyAlbedoTexture = LoadAssetByName<Texture2D>("T_Ophaim_var1_AlbedoTransparency");
            SeraphimBodyVar2AlbedoTexture = LoadAssetByName<Texture2D>("T_Ophaim_var2_AlbedoTransparency");
            SeraphimRingAlbedoTexture = LoadAssetByName<Texture2D>("T_var1_AlbedoTransparency");
            SeraphimRingVar2AlbedoTexture = LoadAssetByName<Texture2D>("T_Ring_var2_AlbedoTransparency");
            SeraphimEyeAlbedoTexture = LoadAssetByName<Texture2D>("T_Eye_Albedo2")
                                       ?? LoadAssetByName<Texture2D>("T_Eye_Albedo");
            SeraphimEyeVar2AlbedoTexture = LoadAssetByName<Texture2D>("T_eye_var2_AlbedoTransparency");
            SeraphimEyeEmissionTexture = LoadAssetByName<Texture2D>("T_Eye_Albedo")
                                         ?? SeraphimEyeAlbedoTexture;
            SeraphimEyeVar2EmissionTexture = LoadAssetByName<Texture2D>("T_eye_var2_Emission")
                                             ?? SeraphimEyeVar2AlbedoTexture;
            SeraphimEyelashAlbedoTexture = LoadAssetByName<Texture2D>("T_Eyelashes_Base_Color");
            SeraphimFeatherAlbedoTexture = LoadAssetByName<Texture2D>("T_feathers_Base_Color alpha")
                                           ?? LoadAssetByName<Texture2D>("T_feathers_Base_Color");
            SeraphimBodyNormalTexture = LoadAssetByName<Texture2D>("T_Ophaim_var1_Normal");
            SeraphimBodyVar2NormalTexture = LoadAssetByName<Texture2D>("T_Ophaim_var2_Normal");
            SeraphimRingNormalTexture = LoadAssetByName<Texture2D>("T_Ring_var2_Normal")
                                        ?? LoadAssetByName<Texture2D>("T_Ring_Normal_OpenGL");
            SeraphimEyeNormalTexture = LoadAssetByName<Texture2D>("T_Eye_Normal");
            SeraphimEyeVar2NormalTexture = LoadAssetByName<Texture2D>("T_eye_var2_Normal");
            SeraphimFeatherNormalTexture = LoadAssetByName<Texture2D>("T_Normal Map from Mesh feathers")
                                           ?? LoadAssetByName<Texture2D>("T_feathers_Normal");
        }

        /// <summary>
        /// Loads the containment lockdown sliding doors (issue #171). A missing prefab is not fatal:
        /// the director falls back to the legacy bar panel, which matters because
        /// lgucontracts.bundle ships via GitHub Releases and can lag behind the DLL.
        /// </summary>
        private static void LoadContainmentLockdownDoorAssets()
        {
            ContainmentLockdownDoorsPrefab = LoadAssetByName<GameObject>(ContainmentLockdownDoorsName);
            ContainmentLockdownDoorsMaterial = LoadAssetByName<Material>(ContainmentLockdownDoorsName);
            ContainmentLockdownDoorsTrimMaterial = LoadAssetByName<Material>(ContainmentLockdownDoorsTrimName);

            if (ContainmentLockdownDoorsPrefab == null)
            {
                ContractsBootstrap.Log?.LogWarning(
                    "[MoonContracts] ContainmentLockdownDoors visual missing from lgucontracts.bundle; " +
                    "containment lockdown will use the legacy dropping bar panel.");
                return;
            }

            int halfCount = 0;
            int rendererCount = 0;
            try
            {
                halfCount = ContainmentLockdownDoorsPrefab.transform.childCount;
                rendererCount = ContainmentLockdownDoorsPrefab.GetComponentsInChildren<Renderer>(true)?.Length ?? 0;
            }
            catch
            {
            }

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts] ContainmentLockdownDoors visual loaded: prefab='{ContainmentLockdownDoorsPrefab.name}', " +
                $"halves={halfCount}, renderers={rendererCount}, " +
                $"material={(ContainmentLockdownDoorsMaterial != null ? ContainmentLockdownDoorsMaterial.name : "none")}, " +
                $"trim={(ContainmentLockdownDoorsTrimMaterial != null ? ContainmentLockdownDoorsTrimMaterial.name : "none")}.");
        }

        private static void LoadSurveyAssets()
        {
            SurveyBeaconVisualPrefab = LoadAssetByName<GameObject>(SurveyBeaconVisualName, preferredPathFragment: "Contracts/Survey")
                                       ?? LoadAssetByName<GameObject>(SurveyBeaconVisualName);
            if (SurveyBeaconVisualPrefab == null)
                ContractsBootstrap.Log?.LogWarning("[MoonContracts.Survey] SurveyBeacon prefab is not in lgucontracts.bundle; survey drops use the vanilla Radar Booster fallback.");
            // #807: hotbar icon; an older bundle without it keeps the vanilla blank icon.
            SurveyBeaconIconSprite = LoadAssetByName<Sprite>("SurveyBeacon_Icon", preferredPathFragment: "Contracts/Survey")
                                     ?? LoadAssetByName<Sprite>("SurveyBeacon_Icon");
        }

        private static void LoadShadowRaidAssets()
        {
            BreachDrillSparkEffectPrefab = LoadAssetByName<GameObject>(BreachDrillSparkEffectName)
                                           ?? LoadAssetByName<GameObject>("Sparks_01")
                                           ?? LoadAssetByName<GameObject>("vfx_Sparks");
            BreachHardDriveVisualPrefab = LoadAssetByName<GameObject>(BreachHardDriveVisualName);
            BreachContainerDoorOpenClip = LoadAudioClip(BreachContainerDoorOpenClipName);
            string containerName = ContractsBootstrap.ShadowRaidContainerPrefabName?.Value;
            if (string.IsNullOrWhiteSpace(containerName))
                containerName = DefaultBreachContainerVisualName;
            BreachSmallContainerVisualPrefab = LoadAssetByName<GameObject>(BreachSmallContainerVisualName);
            BreachMediumContainerVisualPrefab = LoadAssetByName<GameObject>(BreachMediumContainerVisualName)
                                                ?? BreachSmallContainerVisualPrefab;
            BreachLargeContainerVisualPrefab = LoadAssetByName<GameObject>(BreachLargeContainerVisualName)
                                               ?? BreachMediumContainerVisualPrefab
                                               ?? BreachSmallContainerVisualPrefab;
            BreachContainerVisualPrefab = LoadAssetByName<GameObject>(containerName)
                                          ?? BreachMediumContainerVisualPrefab
                                          ?? LoadAssetByName<GameObject>(DefaultBreachContainerVisualName)
                                          ?? BreachSmallContainerVisualPrefab;
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.ShadowRaid] Shadow Raid assets loaded: sparkEffect={(BreachDrillSparkEffectPrefab != null ? BreachDrillSparkEffectPrefab.name : "none")}, " +
                $"hardDrive={(BreachHardDriveVisualPrefab != null ? BreachHardDriveVisualPrefab.name : "none")}, " +
                $"containerDefault={(BreachContainerVisualPrefab != null ? BreachContainerVisualPrefab.name : "none")}, " +
                $"small={(BreachSmallContainerVisualPrefab != null ? BreachSmallContainerVisualPrefab.name : "none")}, " +
                $"medium={(BreachMediumContainerVisualPrefab != null ? BreachMediumContainerVisualPrefab.name : "none")}, " +
                $"large={(BreachLargeContainerVisualPrefab != null ? BreachLargeContainerVisualPrefab.name : "none")}, " +
                $"doorOpenClip={(BreachContainerDoorOpenClip != null ? BreachContainerDoorOpenClip.name : "none")}.");
        }

        private static void PrepareSeraphimPrefab(GameObject prefab)
        {
            if (prefab == null)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts] {SeraphimPrefabName} prefab missing from {BundleFileName}; The Congregation finale will skip the Seraphim enemy until the bundle is rebuilt.");
                return;
            }

            var networkObject = prefab.GetComponent<NetworkObject>();
            if (networkObject == null)
                networkObject = prefab.AddComponent<NetworkObject>();
            AssignStableNetworkHash(networkObject, "CultSeraphim", SeraphimPrefabName);
            networkObject.SynchronizeTransform = true;

            // EnemyAI's serverPosition/SmoothDamp owns steady-state replication and every peer drives
            // the rise itself; a NetworkTransform on top wrote every client's transform as well.
            RemoveDuplicateEnemyTransformAuthority(prefab, "Seraphim");

            SetTagSafe(prefab, "Enemy");
            prefab.layer = ResolveLayer("Enemies", 19);

            var agent = prefab.GetComponent<NavMeshAgent>();
            if (agent == null)
                agent = prefab.AddComponent<NavMeshAgent>();
            agent.speed = 7.25f;
            agent.angularSpeed = 180f;
            agent.acceleration = 24f;
            agent.stoppingDistance = Mathf.Clamp(ContractsBootstrap.CultSeraphimAttackRange != null ? ContractsBootstrap.CultSeraphimAttackRange.Value * 0.45f : 18f, 8f, 26f);
            agent.radius = 1.6f;
            agent.height = 5.5f;
            agent.baseOffset = SeraphimEnemyAI.ResolveHoverHeight();
            agent.autoBraking = true;
            agent.updateRotation = false;
            agent.obstacleAvoidanceType = ObstacleAvoidanceType.HighQualityObstacleAvoidance;
            agent.enabled = false;

            var ai = prefab.GetComponent<SeraphimEnemyAI>();
            if (ai == null)
                ai = prefab.AddComponent<SeraphimEnemyAI>();
            ai.enemyType = SeraphimEnemyType;
            ai.agent = agent;
            ai.creatureAnimator = prefab.GetComponentInChildren<Animator>(true);
            ai.creatureSFX = EnsureAudioSource(prefab, "SeraphimEnemySFX", 0.88f, 16f, 150f);
            ai.creatureVoice = EnsureAudioSource(prefab, "SeraphimEnemyVoice", 0.92f, 18f, 170f);
            ai.enemyBehaviourStates = CreateSeraphimBehaviourStates();

            foreach (Animator animator in prefab.GetComponentsInChildren<Animator>(true))
            {
                if (animator == null)
                    continue;
                if (animator.runtimeAnimatorController == null && SeraphimController != null)
                    animator.runtimeAnimatorController = SeraphimController;
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }

            PrepareSeraphimMaterials(prefab);

            EnsureSeraphimHitDetection(prefab, ai);
            EnsureScanNode(prefab, "?#$0%[]", "SIGNAL CORRUPT", new Vector3(0f, 0.75f, 0f), 5.5f, 220, nodeType: 1);

            try
            {
                LethalLib.Modules.NetworkPrefabs.RegisterNetworkPrefab(prefab);
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts] Registered seraphim enemy network prefab '{SeraphimPrefabName}'.");
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts] Seraphim prefab '{SeraphimPrefabName}' registration skipped: {e.Message}");
            }
        }

        private static EnemyType CreateSeraphimEnemyType(GameObject prefab)
        {
            if (prefab == null)
                return null;

            var enemyType = ScriptableObject.CreateInstance<EnemyType>();
            enemyType.enemyName = "The Congregation Ophanim";
            enemyType.enemyPrefab = prefab;
            enemyType.PowerLevel = 0f;
            enemyType.MaxCount = 1;
            enemyType.canDie = true;
            enemyType.canBeStunned = true;
            enemyType.canBeDestroyed = false;
            enemyType.destroyOnDeath = false;
            enemyType.stunTimeMultiplier = 0.45f;
            enemyType.doorSpeedMultiplier = 1f;
            enemyType.stunGameDifficultyMultiplier = 1f;
            enemyType.pushPlayerForce = 0f;
            enemyType.pushPlayerDistance = 0f;
            enemyType.disableAnimatorWhenFar = false;
            enemyType.isOutsideEnemy = true;
            enemyType.isDaytimeEnemy = false;
            enemyType.probabilityCurve = AnimationCurve.Constant(0f, 1f, 1f);
            enemyType.numberSpawnedFalloff = AnimationCurve.Linear(0f, 1f, 1f, 0f);
            enemyType.useNumberSpawnedFalloff = false;
            return enemyType;
        }

        private static EnemyBehaviourState[] CreateSeraphimBehaviourStates()
        {
            return new[]
            {
                new EnemyBehaviourState { name = "Rising", IsAnimTrigger = false, parameterString = string.Empty },
                new EnemyBehaviourState { name = "Chasing", IsAnimTrigger = false, parameterString = string.Empty },
                new EnemyBehaviourState { name = "Attacking", IsAnimTrigger = false, parameterString = string.Empty },
                new EnemyBehaviourState { name = "Dead", IsAnimTrigger = false, parameterString = string.Empty },
            };
        }

        private static void EnsureSeraphimHitDetection(GameObject prefab, SeraphimEnemyAI ai)
        {
            if (prefab == null || ai == null)
                return;

            var rootCollider = prefab.GetComponent<CapsuleCollider>();
            if (rootCollider == null)
                rootCollider = prefab.AddComponent<CapsuleCollider>();
            rootCollider.enabled = true;
            rootCollider.isTrigger = true;
            rootCollider.center = Vector3.zero;
            rootCollider.radius = 0.42f;
            rootCollider.height = 0.62f;
            WireEnemyCollision(prefab, ai);

            Transform collision = prefab.transform.Find("SeraphimEnemyHitProxy");
            if (collision == null)
            {
                var obj = new GameObject("SeraphimEnemyHitProxy");
                collision = obj.transform;
                collision.SetParent(prefab.transform, false);
                collision.localPosition = Vector3.zero;
            }

            collision.gameObject.layer = ResolveLayer("Enemies", 19);
            SetTagSafe(collision.gameObject, "Enemy");

            var collider = collision.GetComponent<SphereCollider>();
            if (collider == null)
                collider = collision.gameObject.AddComponent<SphereCollider>();
            collider.enabled = true;
            collider.isTrigger = true;
            collider.center = Vector3.zero;
            collider.radius = 0.48f;

            var rb = collision.GetComponent<Rigidbody>();
            if (rb == null)
                rb = collision.gameObject.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;
            WireEnemyCollision(collision.gameObject, ai);
        }

        private static void LoadCultistAudio()
        {
            CultistDetectionSuspenseClip = LoadContractAudioClip("CultDetectionSuspense");
            CultistFootstepClips = LoadContractAudioClips(
                "Footstep Concrete Running 1_01",
                "Footstep Concrete Running 1_02",
                "Footstep Concrete Running 1_03",
                "Footstep Concrete Running 1_04",
                "Footstep Concrete Running 1_05");

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts] Cultist audio loaded: suspense={(CultistDetectionSuspenseClip != null)}, footsteps={CultistFootstepClips.Length}.");
        }

        private static void PrepareCultistPrefab(GameObject prefab, string name, EnemyType enemyType, RuntimeAnimatorController controller, CultistRole role)
        {
            if (prefab == null)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts] {name} prefab missing from {BundleFileName}; The Congregation will skip until the bundle is rebuilt.");
                return;
            }

            var networkObject = prefab.GetComponent<NetworkObject>();
            if (networkObject == null)
                networkObject = prefab.AddComponent<NetworkObject>();
            AssignStableNetworkHash(networkObject, "Cultist." + name);
            networkObject.SynchronizeTransform = true;

            // EnemyAI's serverPosition/SmoothDamp is the cultists' only transform path; a
            // NetworkTransform on top also wrote every client's transform.
            RemoveDuplicateEnemyTransformAuthority(prefab, name);

            SetTagSafe(prefab, "Enemy");
            prefab.layer = ResolveLayer("Enemies", 19);

            var agent = prefab.GetComponent<NavMeshAgent>();
            if (agent == null)
                agent = prefab.AddComponent<NavMeshAgent>();
            agent.speed = 1.65f;
            agent.angularSpeed = 720f;
            agent.acceleration = 12f;
            agent.stoppingDistance = 0.8f;
            agent.radius = 0.42f;
            agent.height = 2.70f;
            agent.baseOffset = 0f;
            agent.autoBraking = true;
            agent.updateRotation = true;
            agent.obstacleAvoidanceType = ObstacleAvoidanceType.MedQualityObstacleAvoidance;
            agent.enabled = false;

            var ai = prefab.GetComponent<CongregantAI>();
            if (ai == null)
                ai = prefab.AddComponent<CongregantAI>();
            ai.enemyType = enemyType;
            ai.agent = agent;
            ai.creatureAnimator = prefab.GetComponentInChildren<Animator>(true);
            ai.creatureSFX = EnsureAudioSource(prefab, name + "SFX", 0f, 3f, 35f);
            MuteAudioSource(ai.creatureSFX);
            ai.creatureVoice = ai.creatureSFX;
            ai.enemyBehaviourStates = CreateCultistBehaviourStates();

            foreach (Animator animator in prefab.GetComponentsInChildren<Animator>(true))
            {
                if (animator == null)
                    continue;
                if (animator.runtimeAnimatorController == null && controller != null)
                    animator.runtimeAnimatorController = controller;
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }

            CongregantCollision.Ensure(ai);
            EnsureScanNode(
                prefab,
                role == CultistRole.White ? "Cult Leader" : "Cultist",
                "The Congregation",
                new Vector3(0f, 1.38f, 0f),
                1.03f,
                32,
                nodeType: 1);

            try
            {
                LethalLib.Modules.NetworkPrefabs.RegisterNetworkPrefab(prefab);
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts] Registered cultist network prefab '{name}' ({role}).");
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts] Cultist prefab '{name}' registration skipped: {e.Message}");
            }
        }

        private static EnemyType CreateCultistEnemyType(GameObject prefab, string enemyName, int maxCount)
        {
            if (prefab == null)
                return null;

            var enemyType = ScriptableObject.CreateInstance<EnemyType>();
            enemyType.enemyName = enemyName;
            enemyType.enemyPrefab = prefab;
            enemyType.PowerLevel = 0f;
            enemyType.MaxCount = maxCount;
            enemyType.canDie = true;
            enemyType.canBeStunned = true;
            enemyType.canBeDestroyed = false;
            enemyType.destroyOnDeath = false;
            enemyType.stunTimeMultiplier = 0.85f;
            enemyType.doorSpeedMultiplier = 1f;
            enemyType.stunGameDifficultyMultiplier = 1f;
            enemyType.pushPlayerForce = 0f;
            enemyType.pushPlayerDistance = 0f;
            enemyType.disableAnimatorWhenFar = false;
            enemyType.isOutsideEnemy = true;
            enemyType.isDaytimeEnemy = false;
            enemyType.probabilityCurve = AnimationCurve.Constant(0f, 1f, 1f);
            enemyType.numberSpawnedFalloff = AnimationCurve.Linear(0f, 1f, 1f, 0f);
            enemyType.useNumberSpawnedFalloff = false;
            return enemyType;
        }

        private static EnemyBehaviourState[] CreateCultistBehaviourStates()
        {
            return new[]
            {
                new EnemyBehaviourState { name = "Traveling", IsAnimTrigger = false, parameterString = string.Empty },
                new EnemyBehaviourState { name = "Ritual", IsAnimTrigger = false, parameterString = string.Empty },
                new EnemyBehaviourState { name = "Stare", IsAnimTrigger = false, parameterString = string.Empty },
                new EnemyBehaviourState { name = "Aggro", IsAnimTrigger = false, parameterString = string.Empty },
                new EnemyBehaviourState { name = "Dead", IsAnimTrigger = false, parameterString = "Dead", boolValue = true },
            };
        }

        private static EnemyBehaviourState[] CreateBlacksiteBehaviourStates()
        {
            return new[]
            {
                new EnemyBehaviourState { name = "Deploying", IsAnimTrigger = false, parameterString = string.Empty },
                new EnemyBehaviourState { name = "Moving", IsAnimTrigger = false, parameterString = string.Empty },
                new EnemyBehaviourState { name = "Contact", IsAnimTrigger = true, parameterString = "Fire" },
                new EnemyBehaviourState { name = "Dead", IsAnimTrigger = false, parameterString = "Dead", boolValue = true },
            };
        }

        private static void LoadBlacksiteAudio()
        {
            BlacksiteGunshotSingleClips = LoadAudioClips("gunshot01", "gunshot02");
            BlacksiteGunshotBurstClip = LoadAudioClip("gunshot_3_round_burst");
            BlacksiteGrenadeClip = LoadAudioClip("grenade");
            BlacksiteHelicopterRotorClip = LoadAudioClip("blades");
            BlacksiteFootstepClips = LoadEmbeddedBlacksiteFootsteps();
            if (BlacksiteFootstepClips.Length == 0)
                BlacksiteFootstepClips = LoadAudioClips("BootsLinoleum_01", "BootsLinoleum_02", "BootsLinoleum_03", "BootsLinoleum_04", "BootsLinoleum_05", "footstep_01", "footstep_02");
            BlacksiteBatonEquipClip = LoadEmbeddedWav("item_equip_ufx_4.wav");
            BlacksiteBatonStowClip = LoadEmbeddedWav("item_equip_ufx_2.wav");
            BlacksiteGearFoleyClips = LoadAudioClips("Gear_01", "Gear_02", "Gear_03", "Gear_04", "Gear_05");
            BlacksiteHitReactionClips = LoadAudioClips("Hit_01", "Hit_02", "Hit_03");
            BlacksiteDeathReactionClips = LoadAudioClips(
                "Death_01", "Death_02", "Death_03", "Death_04", "Death_05", "Death_06", "Death_07");

            BlacksiteVoiceClips.Clear();
            BlacksiteVoiceClips[BlacksiteVoiceEvent.InitialEntry] = LoadAudioClips(
                "unitisinbound",
                "unitismovingin",
                "containmentproceeding",
                "readyweapons",
                "weaponsoffsafeprepforcontact",
                "weareinaninfestationzone");
            BlacksiteVoiceClips[BlacksiteVoiceEvent.PlayerContact] = LoadAudioClips(
                "chatter_alert1",
                "chatter_callcontacttarget1",
                "designatetargetas",
                "engaging",
                "executingfullresponse",
                "takehimdown");
            BlacksiteVoiceClips[BlacksiteVoiceEvent.MonsterContact] = LoadAudioClips(
                "chatter_callcontactparasitics",
                "readyweaponshostilesinbound",
                "visualonexogens",
                "outbreak");
            BlacksiteVoiceClips[BlacksiteVoiceEvent.TacticalAction] = LoadAudioClips(
                "cover",
                "coverme",
                "fixsightlinesmovein",
                "callhotpoint",
                "chatter_affirmative",
                "chatter_affirmative2",
                "combine-move-in");
            BlacksiteVoiceClips[BlacksiteVoiceEvent.DriveMissing] = LoadAudioClips(
                "chatter_confirmsectornotsterile",
                "sectorisnotsecure");
            BlacksiteVoiceClips[BlacksiteVoiceEvent.DriveAcquired] = LoadAudioClips(
                "overwatchconfirmhvtcontained",
                "containmentproceeding",
                "contained");
            BlacksiteVoiceClips[BlacksiteVoiceEvent.CasualtyEscalation] = LoadAudioClips(
                "heavyresistance",
                "overwatchrequestreinforcement",
                "overwatchrequestreserveactivation",
                "overwatchsectoroverrun",
                "isfinalteamunitbackup");
            BlacksiteVoiceClips[BlacksiteVoiceEvent.KillClear] = LoadAudioClips(
                "targetblackout",
                "chatter_affirmativewegothimnow",
                "sectorissecurenovison",
                "onedown",
                "combine-one-down-1",
                "targetsterlized");
            BlacksiteVoiceClips[BlacksiteVoiceEvent.GrenadeWarning] = Array.Empty<AudioClip>();
            BlacksiteVoiceClips[BlacksiteVoiceEvent.BackupRequested] = LoadAudioClips("overwatchrequestreinforcement", "overwatchrequestreserveactivation", "isfinalteamunitbackup");
            BlacksiteVoiceClips[BlacksiteVoiceEvent.BackupAcknowledged] = LoadAudioClips("unitismovingin", "executingfullresponse", "unitisclosing", "closing");
            BlacksiteVoiceClips[BlacksiteVoiceEvent.Searching] = LoadAudioClips("sectorisnotsecure", "chatter_confirmsectornotsterile", "unitisclosing", "fixsightlinesmovein", "lostcontact", "hasnegativemovement", "combine-negative-movement");
            BlacksiteVoiceClips[BlacksiteVoiceEvent.Reacquired] = LoadAudioClips("chatter_callcontacttarget1", "engaging", "chatter_callhotpoint", "designatetargetas", "targetcompromisedmovein");
            BlacksiteVoiceClips[BlacksiteVoiceEvent.CargoDeposited] = LoadAudioClips("overwatchconfirmhvtcontained", "containmentproceeding", "engagedincleanup", "delivered");
            BlacksiteVoiceClips[BlacksiteVoiceEvent.Pickup] = LoadAudioClips("unitisinbound", "unitismovingin", "containmentproceeding", "reportpositions");
            BlacksiteVoiceClips[BlacksiteVoiceEvent.Patrol] = LoadAudioClips("engagedincleanup", "unitisclosing", "Chatter_administer", "readyweapons", "sectorissecurenovison", "weareinaninfestationzone", "weaponsoffsafeprepforcontact", "containmentproceeding", "antiseptic", "combine-stay-alert", "stayalertreportsightlines");
            // #1271 S8: the chatter moments and the two response pools. A stem the bundle does not
            // carry yet is skipped by LoadAudioClips, so a pool stays short rather than failing.
            // bouncer, Aggro_01, Kill_01, Chatter_01 and Chatter_02 wait for Lawson to identify them.
            BlacksiteVoiceClips[BlacksiteVoiceEvent.ObjectiveSighted] = LoadAudioClips("priority1objective", "designatetargetas", "callhotpoint");
            BlacksiteVoiceClips[BlacksiteVoiceEvent.WitnessSighted] = LoadAudioClips("prioritytwoescapee", "targetcompromisedmovein");
            BlacksiteVoiceClips[BlacksiteVoiceEvent.WitnessTaken] = LoadAudioClips("contained", "overwatchconfirmhvtcontained", "chatter_affirmativewegothimnow");
            BlacksiteVoiceClips[BlacksiteVoiceEvent.ObjectiveTaken] = LoadAudioClips("containmentproceeding", "contained", "engagedincleanup");
            BlacksiteVoiceClips[BlacksiteVoiceEvent.ObjectiveLost] = LoadAudioClips("lostcontact", "sectorisnotsecure", "chatter_confirmsectornotsterile");
            BlacksiteVoiceClips[BlacksiteVoiceEvent.MemberBoarded] = LoadAudioClips("unitisinbound", "closing", "closing2");
            BlacksiteVoiceClips[BlacksiteVoiceEvent.ShipDeparting] = LoadAudioClips("thatsitwrapitup", "unitismovingin");
            BlacksiteVoiceClips[BlacksiteVoiceEvent.AcknowledgeOrder] = LoadAudioClips("copy", "copythat", "chatter_affirmative", "chatter_affirmative2");
            BlacksiteVoiceClips[BlacksiteVoiceEvent.AcknowledgeContact] = LoadAudioClips("closing", "closing2", "covering", "engaging", "combine-move-in");

            // Preserve the old public-facing pools as compatibility views while all new
            // scheduling goes through the event taxonomy above.
            BlacksiteVoiceAffirmativeClips = BlacksiteVoiceClips[BlacksiteVoiceEvent.TacticalAction];
            BlacksiteVoiceContactClips = ConcatUniqueAudio(
                BlacksiteVoiceClips[BlacksiteVoiceEvent.PlayerContact],
                BlacksiteVoiceClips[BlacksiteVoiceEvent.MonsterContact],
                BlacksiteVoiceClips[BlacksiteVoiceEvent.GrenadeWarning]);
            BlacksiteVoiceCommandClips = ConcatUniqueAudio(
                BlacksiteVoiceClips[BlacksiteVoiceEvent.TacticalAction],
                BlacksiteVoiceClips[BlacksiteVoiceEvent.DriveMissing]);
            BlacksiteVoiceDeployClips = BlacksiteVoiceClips[BlacksiteVoiceEvent.InitialEntry];
            BlacksiteVoiceHitClips = BlacksiteHitReactionClips;
            BlacksiteVoiceRetreatClips = BlacksiteVoiceClips[BlacksiteVoiceEvent.CasualtyEscalation];
            int contextualVoiceCount = BlacksiteVoiceClips.Values
                .SelectMany(clips => clips)
                .Where(clip => clip != null)
                .Select(clip => clip.name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts] Blacksite audio loaded: singles={BlacksiteGunshotSingleClips.Length}, " +
                $"burst={(BlacksiteGunshotBurstClip != null)}, grenade={(BlacksiteGrenadeClip != null)}, " +
                $"rotor={(BlacksiteHelicopterRotorClip != null)}, " +
                $"steps={BlacksiteFootstepClips.Length}, equip={(BlacksiteBatonEquipClip != null)}, " +
                $"stow={(BlacksiteBatonStowClip != null)}, gear={BlacksiteGearFoleyClips.Length}, " +
                $"hit={BlacksiteHitReactionClips.Length}, death={BlacksiteDeathReactionClips.Length}, voices={contextualVoiceCount}.");
        }

        private static AudioClip[] ConcatUniqueAudio(params AudioClip[][] pools)
        {
            return (pools ?? Array.Empty<AudioClip[]>())
                .Where(pool => pool != null)
                .SelectMany(pool => pool)
                .Where(clip => clip != null)
                .GroupBy(clip => clip.name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
        }

        private static void LoadBlacksiteTextures()
        {
            BlacksiteSoldierBodyTexture = LoadAssetByName<Texture2D>("Chaos");
            BlacksiteSoldierNormalTexture = null;
            BlacksiteSoldierLensTexture = LoadAssetByName<Texture2D>("Gas_mask");
            BlacksiteSoldierWeaponTexture = LoadAssetByName<Texture2D>("Chaos");
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts] Blacksite textures loaded: body={(BlacksiteSoldierBodyTexture != null)}, " +
                $"normal={(BlacksiteSoldierNormalTexture != null)}, lens={(BlacksiteSoldierLensTexture != null)}, weapon={(BlacksiteSoldierWeaponTexture != null)}.");
        }

        private static AudioClip[] LoadEmbeddedBlacksiteFootsteps()
        {
            string[] names =
            {
                "BootsLinoleum_01.wav",
                "BootsLinoleum_02.wav",
                "BootsLinoleum_03.wav",
                "BootsLinoleum_04.wav",
                "BootsLinoleum_05.wav"
            };

            var clips = new List<AudioClip>();
            foreach (string name in names)
            {
                AudioClip clip = LoadEmbeddedWav(name);
                if (clip != null)
                    clips.Add(clip);
            }

            if (clips.Count > 0)
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts] Loaded {clips.Count}/{names.Length} embedded Blacksite footstep clips.");

            return clips.ToArray();
        }

        private static AudioClip LoadEmbeddedWav(string fileName)
        {
            try
            {
                Assembly assembly = typeof(ContractAssetLoader).Assembly;
                string resourceName = assembly.GetManifestResourceNames()
                    .FirstOrDefault(name => name.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrWhiteSpace(resourceName))
                    return null;

                using (Stream stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                        return null;

                    return DecodePcmWav(stream, Path.GetFileNameWithoutExtension(fileName));
                }
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts] Failed loading embedded Blacksite clip '{fileName}': {e.Message}");
                return null;
            }
        }

        private static AudioClip DecodePcmWav(Stream stream, string clipName)
        {
            using (var reader = new BinaryReader(stream))
            {
                if (ReadFourCc(reader) != "RIFF")
                    return null;
                reader.ReadInt32();
                if (ReadFourCc(reader) != "WAVE")
                    return null;

                int channels = 0;
                int sampleRate = 0;
                int bitsPerSample = 0;
                ushort audioFormat = 0;
                byte[] pcmData = null;

                while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
                {
                    string chunkId = ReadFourCc(reader);
                    int chunkSize = reader.ReadInt32();
                    long nextChunk = reader.BaseStream.Position + chunkSize;

                    if (chunkId == "fmt ")
                    {
                        audioFormat = reader.ReadUInt16();
                        channels = reader.ReadUInt16();
                        sampleRate = reader.ReadInt32();
                        reader.ReadInt32();
                        reader.ReadUInt16();
                        bitsPerSample = reader.ReadUInt16();
                    }
                    else if (chunkId == "data")
                    {
                        pcmData = reader.ReadBytes(chunkSize);
                    }

                    reader.BaseStream.Position = nextChunk + (chunkSize % 2);
                }

                if (audioFormat != 1 || channels <= 0 || sampleRate <= 0 || pcmData == null)
                    return null;

                float[] samples = DecodePcmSamples(pcmData, bitsPerSample);
                if (samples == null || samples.Length == 0)
                    return null;

                int frames = samples.Length / channels;
                if (frames <= 0)
                    return null;

                var clip = AudioClip.Create(clipName, frames, channels, sampleRate, stream: false);
                clip.SetData(samples, 0);
                return clip;
            }
        }

        private static string ReadFourCc(BinaryReader reader)
        {
            char[] chars = reader.ReadChars(4);
            return new string(chars);
        }

        private static float[] DecodePcmSamples(byte[] data, int bitsPerSample)
        {
            int bytesPerSample = bitsPerSample / 8;
            if (bytesPerSample <= 0 || data.Length < bytesPerSample)
                return null;

            int sampleCount = data.Length / bytesPerSample;
            var samples = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                int offset = i * bytesPerSample;
                switch (bitsPerSample)
                {
                    case 16:
                        samples[i] = BitConverter.ToInt16(data, offset) / 32768f;
                        break;
                    case 24:
                        int sample24 = data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16);
                        if ((sample24 & 0x800000) != 0)
                            sample24 |= unchecked((int)0xFF000000);
                        samples[i] = Mathf.Clamp(sample24 / 8388608f, -1f, 1f);
                        break;
                    case 32:
                        samples[i] = Mathf.Clamp(BitConverter.ToInt32(data, offset) / 2147483648f, -1f, 1f);
                        break;
                    default:
                        return null;
                }
            }

            return samples;
        }

        private static AudioClip[] LoadAudioClips(params string[] names)
        {
            return LoadDistinctAudioClips(LoadAudioClip, names);
        }

        /// <summary>lgucontracts.bundle only. Cult and Seraphim clips never live in the Blacksite
        /// equipment bundle, whose lookup warns once for every name it lacks.</summary>
        private static AudioClip[] LoadContractAudioClips(params string[] names)
        {
            return LoadDistinctAudioClips(LoadContractAudioClip, names);
        }

        private static AudioClip[] LoadDistinctAudioClips(Func<string, AudioClip> load, string[] names)
        {
            AudioClip[] clips = names
                .Select(load)
                .Where(clip => clip != null)
                .GroupBy(clip => clip.name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            return clips ?? Array.Empty<AudioClip>();
        }

        private static AudioClip LoadAudioClip(string shortName)
        {
            if (string.IsNullOrWhiteSpace(shortName))
                return null;

            return BlacksiteEquipmentAssets.Load<AudioClip>(shortName)
                ?? LoadContractAudioClip(shortName);
        }

        private static AudioClip LoadContractAudioClip(string shortName)
        {
            if (string.IsNullOrWhiteSpace(shortName))
                return null;

            return LoadAssetByName<AudioClip>(shortName)
                ?? LoadAssetByName<AudioClip>(shortName + ".wav")
                ?? LoadAssetByName<AudioClip>(shortName + ".ogg")
                ?? LoadAssetByName<AudioClip>(shortName + ".mp3");
        }

        private static AudioSource EnsureAudioSource(GameObject prefab, string childName, float volume, float minDistance, float maxDistance)
        {
            Transform child = FindChildRecursive(prefab.transform, childName);
            GameObject audioObject;
            if (child == null)
            {
                audioObject = new GameObject(childName);
                audioObject.transform.SetParent(prefab.transform, false);
                audioObject.transform.localPosition = new Vector3(0f, 1.8f, 0f);
            }
            else
            {
                audioObject = child.gameObject;
            }

            var audio = audioObject.GetComponent<AudioSource>();
            if (audio == null)
                audio = audioObject.AddComponent<AudioSource>();
            audio.playOnAwake = false;
            audio.spatialBlend = 1f;
            audio.rolloffMode = AudioRolloffMode.Linear;
            audio.volume = volume;
            audio.minDistance = minDistance;
            audio.maxDistance = maxDistance;
            audio.priority = 76;
            return audio;
        }

        private static void MuteAudioSource(AudioSource audio)
        {
            if (audio == null)
                return;

            audio.playOnAwake = false;
            audio.Stop();
            audio.clip = null;
            audio.volume = 0f;
            audio.mute = true;
        }

        private static void EnsureScanNode(GameObject prefab, string headerText, string subText, Vector3 localPosition, float radius, int maxRange, int nodeType)
        {
            if (prefab == null)
                return;

            Transform scan = FindChildRecursive(prefab.transform, "ScanNode")
                             ?? FindChildRecursive(prefab.transform, "SeraphimScanNode")
                             ?? FindChildRecursive(prefab.transform, "CultistScanNode")
                             ?? FindChildRecursive(prefab.transform, prefab.name + "ScanNode");
            if (scan == null)
            {
                var obj = new GameObject("ScanNode");
                scan = obj.transform;
                scan.SetParent(prefab.transform, false);
            }

            scan.name = "ScanNode";
            scan.localPosition = localPosition;
            scan.localRotation = Quaternion.identity;
            scan.localScale = Vector3.one;
            scan.gameObject.layer = ResolveLayer("ScanNode", 22);
            try
            {
                scan.gameObject.tag = "DoNotSet";
            }
            catch
            {
            }

            SphereCollider collider = scan.GetComponent<SphereCollider>();
            if (collider == null)
                collider = scan.gameObject.AddComponent<SphereCollider>();
            collider.enabled = true;
            collider.isTrigger = true;
            collider.center = Vector3.zero;
            collider.radius = Mathf.Max(0.15f, radius);

            BoxCollider box = scan.GetComponent<BoxCollider>();
            if (box != null)
                box.enabled = false;

            ScanNodeProperties scanNode = scan.GetComponent<ScanNodeProperties>();
            if (scanNode == null)
                scanNode = scan.gameObject.AddComponent<ScanNodeProperties>();
            scanNode.headerText = headerText;
            scanNode.subText = subText;
            scanNode.maxRange = Mathf.Max(1, maxRange);
            scanNode.minRange = 1;
            scanNode.requiresLineOfSight = false;
            scanNode.nodeType = nodeType;
            scanNode.creatureScanID = -1;
            scanNode.scrapValue = 0;
        }

        private static bool TryResolveNavMeshPlacement(Vector3 origin, float sampleDistance, out Vector3 resolved)
        {
            float baseRadius = Mathf.Max(0.5f, sampleDistance);
            float[] radii = { baseRadius, baseRadius * 2f, baseRadius * 4f };
            for (int i = 0; i < radii.Length; i++)
            {
                if (NavMesh.SamplePosition(origin, out NavMeshHit hit, radii[i], NavMesh.AllAreas))
                {
                    resolved = hit.position;
                    return true;
                }
            }

            RoundManager round = RoundManager.Instance;
            if (round != null)
            {
                Vector3 projected = round.GetNavMeshPosition(origin, round.navHit, radii[radii.Length - 1]);
                if (NavMesh.SamplePosition(projected, out NavMeshHit projectedHit, baseRadius, NavMesh.AllAreas))
                {
                    resolved = projectedHit.position;
                    return true;
                }
            }

            resolved = origin;
            return false;
        }

        internal static bool TryActivateAgentOnNavMesh(GameObject root, float sampleDistance)
        {
            if (root == null)
                return false;

            var agent = root.GetComponent<NavMeshAgent>() ?? root.GetComponentInChildren<NavMeshAgent>(true);
            if (agent == null)
                return false;

            // Unity logs "Failed to create agent because it is not close enough to the NavMesh" from
            // NavMeshAgent.OnEnable, so the position has to be resolved *before* the agent is enabled.
            // A single SamplePosition at the requested radius is not always enough on procedural
            // interiors, so widen the search and fall back to RoundManager's own projection before
            // giving up; if nothing resolves, leave the agent disabled rather than enabling it
            // off-mesh (an off-mesh agent then errors on every CalculatePath/SetDestination).
            if (!TryResolveNavMeshPlacement(root.transform.position, sampleDistance, out Vector3 targetPosition))
                return false;

            root.transform.position = targetPosition;

            try
            {
                agent.enabled = true;
                if (agent.isOnNavMesh)
                    return true;

                if (agent.Warp(targetPosition))
                    return true;

                agent.enabled = false;
                return false;
            }
            catch
            {
                try
                {
                    agent.enabled = false;
                }
                catch
                {
                }

                return false;
            }
        }

        private static void EnsureSoldierHitDetection(BlacksiteSoldierAI ai, BlacksiteSoldierRigBinding rig)
        {
            if (ai == null || rig == null || !rig.IsValid || rig.HitCapsule == null)
                return;

            GameObject collision = rig.HitCapsule.gameObject;
            collision.layer = ResolveLayer("Enemies", 19);
            SetTagSafe(collision, "Enemy");
            CapsuleCollider collider = rig.HitCapsule;
            collider.enabled = true;
            collider.isTrigger = true;

            var rb = collision.GetComponent<Rigidbody>();
            if (rb == null)
                rb = collision.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;
            WireEnemyCollision(collision, ai);

            foreach (EnemyAICollisionDetect detect in ai.GetComponentsInChildren<EnemyAICollisionDetect>(true))
            {
                if (detect == null)
                    continue;
                detect.mainScript = ai;
                detect.canCollideWithEnemies = true;
                detect.onlyCollideWhenGrounded = false;
            }
        }

        // #874 S2.2: body presence, created on the prefab so every peer has it. Separate from the
        // trigger hit capsule above: no EnemyAICollisionDetect, no tag, no rigidbody.
        private static void EnsureSoldierBodyCollision(BlacksiteSoldierAI ai)
        {
            if (ai == null)
                return;

            CapsuleCollider body = EnemyBodyCollision.Ensure(ai.gameObject, BlacksiteBodyCollision.ObjectName,
                BlacksiteBodyCollision.Radius, BlacksiteBodyCollision.Height, BlacksiteBodyCollision.CenterY, kinematic: false);
            BlacksiteBodyCollision.AttachMarker(body);
            if (body == null)
            {
                ContractsBootstrap.Log?.LogError("[MoonContracts.Blacksite] Soldier body capsule could not be created; players will walk through soldiers.");
                return;
            }

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Blacksite] Soldier body capsule ready: layer={body.gameObject.layer}, " +
                $"radius={body.radius:0.00}, height={body.height:0.00}, centerY={body.center.y:0.00}, trigger={body.isTrigger}.");
        }

        private static void EnsureSoldierCombatBindings(BlacksiteSoldierAI ai, BlacksiteSoldierRigBinding rig)
        {
            if (ai == null || rig == null || !rig.IsValid)
                return;

            ai.ApplyRigBinding(rig);

            Animator animator = rig.Animator;
            var forwarder = animator.GetComponent<BlacksiteAnimationEventForwarder>();
            if (forwarder == null)
                forwarder = animator.gameObject.AddComponent<BlacksiteAnimationEventForwarder>();
            forwarder.Init(ai);
        }

        private static void WireEnemyCollision(GameObject host, EnemyAI ai)
        {
            if (host == null || ai == null)
                return;

            var detect = host.GetComponent<EnemyAICollisionDetect>();
            if (detect == null)
                detect = host.AddComponent<EnemyAICollisionDetect>();
            detect.mainScript = ai;
            detect.onlyCollideWhenGrounded = false;
        }

        private static Transform FindChildRecursive(Transform root, string childName)
        {
            if (root == null || string.IsNullOrWhiteSpace(childName))
                return null;
            if (string.Equals(root.name, childName, StringComparison.OrdinalIgnoreCase))
                return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindChildRecursive(root.GetChild(i), childName);
                if (found != null)
                    return found;
            }
            return null;
        }

        private static int ResolveLayer(string name, int fallback)
        {
            int layer = LayerMask.NameToLayer(name);
            return layer >= 0 ? layer : fallback;
        }

        private static void SetTagSafe(GameObject obj, string tag)
        {
            if (obj == null)
                return;
            try
            {
                obj.tag = tag;
            }
            catch
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts] Tag '{tag}' was not available for {obj.name}.");
            }
        }

        private static void AssignStableNetworkHash(NetworkObject networkObject, string hashKey)
        {
            AssignStableNetworkHash(networkObject, "Blacksite", hashKey);
        }

        /// <summary>
        /// Root for a runtime-built network prefab. NGO instantiates network prefab clones without ever
        /// activating them, so a template whose root is <c>SetActive(false)</c> replicates as an invisible,
        /// never-updating object on every client with no error (#454). The root therefore keeps
        /// <c>activeSelf == true</c> and is parked under a persistent inactive holder instead, reproducing the
        /// <c>activeSelf=True / activeInHierarchy=False</c> state a bundled prefab asset has: no Awake on the
        /// template, active clones on every peer.
        /// </summary>
        internal static GameObject CreateHiddenPrefabRoot(string name)
        {
            var root = new GameObject(name);
            ParkHiddenPrefabRoot(root);
            return root;
        }

        /// <summary>
        /// Deferred form of <see cref="CreateHiddenPrefabRoot"/>, for templates that must be assembled while
        /// active (renderer bounds only resolve on an active hierarchy) and hidden afterwards.
        /// </summary>
        internal static void ParkHiddenPrefabRoot(GameObject root)
        {
            if (root == null)
                return;

            root.transform.SetParent(GetHiddenPrefabHolder().transform, worldPositionStays: false);
            root.SetActive(true);
        }

        private static GameObject GetHiddenPrefabHolder()
        {
            if (_hiddenPrefabHolder == null)
            {
                _hiddenPrefabHolder = new GameObject("MoonContracts_NetworkPrefab_RuntimeHolder");
                _hiddenPrefabHolder.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(_hiddenPrefabHolder);
                _hiddenPrefabHolder.hideFlags = HideFlags.HideAndDontSave;
            }

            return _hiddenPrefabHolder;
        }

        /// <summary>One line per runtime-built network prefab, so a client log alone proves the registration.</summary>
        internal static void LogRegisteredPrefab(string logTag, GameObject prefab, bool registered)
        {
            if (prefab == null)
            {
                ContractsBootstrap.Log?.LogWarning($"{logTag} NETWORK_PREFAB_INIT name=<null> activeSelf=n/a hash=0 registered=no");
                return;
            }

            ContractsBootstrap.Log?.LogInfo(
                $"{logTag} NETWORK_PREFAB_INIT name={prefab.name} activeSelf={prefab.activeSelf} " +
                $"hash={ReadStableNetworkHash(prefab.GetComponent<NetworkObject>())} registered={(registered ? "yes" : "no")}");
        }

        internal static uint ReadStableNetworkHash(NetworkObject networkObject)
        {
            if (networkObject == null)
                return 0u;

            try
            {
                Type type = typeof(NetworkObject);
                PropertyInfo prop = type.GetProperty("GlobalObjectIdHash", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null && prop.CanRead)
                    return (uint)prop.GetValue(networkObject);

                FieldInfo field = type.GetField("GlobalObjectIdHash", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? type.GetField("<GlobalObjectIdHash>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
                return field != null ? (uint)field.GetValue(networkObject) : 0u;
            }
            catch
            {
                return 0u;
            }
        }

        internal static void AssignStableNetworkHash(NetworkObject networkObject, string scope, string hashKey)
        {
            if (networkObject == null)
                return;

            try
            {
                uint hash;
                using (MD5 md5 = MD5.Create())
                {
                    byte[] bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(ContractsBootstrap.PLUGIN_GUID + "." + scope + "." + hashKey));
                    hash = BitConverter.ToUInt32(bytes, 0);
                }

                Type type = typeof(NetworkObject);
                PropertyInfo prop = type.GetProperty("GlobalObjectIdHash", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null && prop.CanWrite)
                {
                    prop.SetValue(networkObject, hash);
                    ContractsBootstrap.Log?.LogInfo($"[MoonContracts] {scope} NetworkObject hash set for {hashKey}: {hash}.");
                    return;
                }

                FieldInfo field = type.GetField("GlobalObjectIdHash", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? type.GetField("<GlobalObjectIdHash>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
                if (field != null)
                {
                    field.SetValue(networkObject, hash);
                    ContractsBootstrap.Log?.LogInfo($"[MoonContracts] {scope} NetworkObject hash set for {hashKey}: {hash}.");
                    return;
                }

                ContractsBootstrap.Log?.LogWarning($"[MoonContracts] Could not locate NetworkObject.GlobalObjectIdHash for {scope} prefab '{hashKey}'.");
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogError($"[MoonContracts] Failed assigning {scope} NetworkObject hash for {hashKey}: {e}");
            }
        }

        private static Avatar LoadWhistleblowerAvatar()
        {
            var prefabAnimator = WhistleblowerVisualPrefab != null
                ? WhistleblowerVisualPrefab.GetComponentInChildren<Animator>(true)
                : null;
            if (prefabAnimator?.avatar != null)
                return prefabAnimator.avatar;

            // Both models ship a humanoid Avatar and LoadAllAssets order is unspecified, so an
            // unscoped pick can retarget the scavenger onto the Mixamo rig, or the reverse.
            Avatar[] avatars = Bundle.LoadAllAssets<Avatar>();
            var scopedAvatar = avatars.FirstOrDefault(avatar =>
                avatar != null
                && avatar.isHuman
                && IsWhistleblowerVanillaAvatar(avatar) == WhistleblowerVisualIsVanillaVariant);
            if (scopedAvatar != null)
                return scopedAvatar;

            ContractsBootstrap.Log?.LogWarning($"[MoonContracts] No humanoid Avatar in {BundleFileName} matches the selected Whistleblower model ('{(WhistleblowerVisualIsVanillaVariant ? WhistleblowerVanillaVisualName : WhistleblowerVisualName)}'); falling back to an unscoped Avatar, which may retarget the wrong rig.");

            var directAvatar = avatars.FirstOrDefault(avatar => avatar != null && avatar.isHuman)
                ?? avatars.FirstOrDefault();
            if (directAvatar != null)
                return directAvatar;

            foreach (GameObject obj in Bundle.LoadAllAssets<GameObject>())
            {
                var animator = obj != null ? obj.GetComponentInChildren<Animator>(true) : null;
                if (animator?.avatar != null)
                    return animator.avatar;
            }

            return null;
        }

        private static bool IsWhistleblowerVanillaAvatar(Avatar avatar)
        {
            return avatar != null
                && !string.IsNullOrEmpty(avatar.name)
                && avatar.name.IndexOf(WhistleblowerVanillaAvatarNameFragment, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
