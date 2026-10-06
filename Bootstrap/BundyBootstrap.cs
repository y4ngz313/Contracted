using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Y4NGZCompany.Escalations.Bundy;
using Y4NGZCompany.Escalations.Bundy.AI;
using LethalLib.Modules;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

namespace Y4NGZCompany.Bootstrap
{
    public static class BundyPlugin
    {
        internal const string BundleFilename = "bundy.bundle";
        internal const string ChainsawPrefabName = "BundyChainsaw";

        internal static ManualLogSource Log;
        internal static BundyConfig ModConfig;
        internal static AssetBundle ModAssets;
        // Resolved via reflection by MoonEscalationState (The Stranger) — keep the field name stable.
        internal static EnemyType ChainsawEnemyType;

        internal static void Initialize(
            ConfigFile config,
            ManualLogSource logger,
            GameObject hostGameObject,
            Harmony harmony)
        {
            Log = logger;
            Y4NGZCore.Diagnostics.ModuleLog.Bundy = logger;

            // #374 / #612 task 1.4: forced-critical windows normally expire from the owning
            // Bundy's Update; the host outlives every Bundy instance, so this is the backstop
            // that frees a player whose Bundy died or despawned mid-window. Registered as the
            // first thing this bootstrap does, matching the pre-split host's unconditional
            // per-frame call — everything below can throw without losing the backstop.
            Y4NGZCore.Lifecycle.ModuleTickRegistry.RegisterTick(
                Y4NGZCore.Lifecycle.ModuleHarmonyIds.Contracted,
                Y4NGZCore.Lifecycle.ModuleTickOrder.BundyForcedCriticalBackstop,
                "BundyForcedCriticalFinisherBackstop",
                Y4NGZCompany.Escalations.Bundy.AI.BundyAIBase.TickForcedCriticalFinisherBackstop);

            // #453: Bundy's three known writers of the local player's interact-gating flags.
            // These used to be typeof entries inside LocalInputStateWatchdog's static table,
            // which made that one array a compile-time edge to all three future DLLs at once.
            Y4NGZCore.Diagnostics.InputWatchdogWriterRegistry.Register(
                typeof(Y4NGZCompany.Escalations.Bundy.BundyVictimInteractionsBridge),
                "Bundy",
                "ClearVictimSpecialAnimationForDeath");
            Y4NGZCore.Diagnostics.InputWatchdogWriterRegistry.Register(
                typeof(Y4NGZCompany.Escalations.Bundy.BundyVictimPlayableController),
                "Bundy",
                "RefreshPlayerLockState",
                "RestorePlayerState",
                "ClearBundyLocks");

            // TEMP unique build marker (warning level) so we can confirm THIS DLL is the one loaded.
            // Search BepInEx for: "BundyBuildMarker"
            try
            {
                string asmLoc = Assembly.GetExecutingAssembly().Location;
                string built = !string.IsNullOrEmpty(asmLoc) && File.Exists(asmLoc)
                    ? File.GetLastWriteTime(asmLoc).ToString("yyyy-MM-dd HH:mm:ss")
                    : "<unknown>";
                Log.LogWarning($"BundyBuildMarker LIFECYCLE-DIAG-2026-05-29 v{BundyPluginInfo.PLUGIN_VERSION} dll='{asmLoc}' built={built}");
            }
            catch (Exception markerEx) { Log.LogWarning($"BundyBuildMarker (failed to read dll time): {markerEx.Message}"); }

            try
            {
                ModConfig = new BundyConfig(config);
                harmony.PatchAll(typeof(BundyShotgunProbePatch));

                // #393: the finisher collision suppressor's round-boundary reset used to ride on
                // BloodFX's, which is no longer part of this assembly. Isolated for the same reason
                // as the patches below: losing it leaks ignored collider pairs across a round, not
                // the enemy registration.
                try
                {
                    harmony.PatchAll(typeof(Escalations.Bundy.BundyFinisherRoundLifecyclePatches));
                }
                catch (Exception finisherLifecyclePatchEx)
                {
                    Log.LogWarning(
                        "[Bundy] Finisher round-lifecycle patch failed to apply; suppressed collider "
                        + $"pairs will not be cleared at a round boundary. {finisherLifecyclePatchEx.Message}");
                }

                // Isolated: this one hooks a vanilla ClientRpc, so a signature drift in a future game
                // build must not take the bundle load and enemy registration below down with it.
                try
                {
                    harmony.PatchAll(typeof(BundyTeleporterRevealPatch));
                }
                catch (Exception teleporterPatchEx)
                {
                    Log.LogWarning(
                        "[Bundy] Teleporter reveal patch failed to apply; the ship teleporter will stay a "
                        + $"silent escape from the Stranger hunt. {teleporterPatchEx.Message}");
                }

                // Isolated for the same reason: losing this one costs the spike-trap interaction,
                // not the enemy registration. Without it a slam instakills Bundy into a frozen
                // walking corpse (#318).
                try
                {
                    harmony.PatchAll(typeof(BundySpikeTrapPatch));
                }
                catch (Exception spikeTrapPatchEx)
                {
                    Log.LogWarning(
                        "[Bundy] Spike trap patch failed to apply; a spike roof trap slam will instakill "
                        + $"Bundy into a frozen corpse instead of knocking him down. {spikeTrapPatchEx.Message}");
                }

                // #374: isolated because this targets v81's explicit IHittable implementation.
                // Signature drift must cost only Bundy's cosmetic damage blood, not enemy registration.
                try
                {
                    harmony.PatchAll(typeof(BundyDamageBloodPatch));
                }
                catch (Exception damageBloodPatchEx)
                {
                    Log.LogWarning(
                        "[Bundy] Damage-blood patch failed to apply; hits will still deal damage but "
                        + $"will not mark Bundy's body. {damageBloodPatchEx.Message}");
                }
                // Job 3b. Registered one class at a time and individually guarded: these hook seven
                // separate vanilla types, and a signature drift in any one of them must cost exactly
                // that event rather than the rest of the bus. A missing hook degrades his hearing;
                // a thrown exception here would cost the enemy registration below.
                TryPatchWorldNoiseHook(harmony, typeof(BundyBreakerNoisePatch), "breaker levers");
                TryPatchWorldNoiseHook(harmony, typeof(BundyBigDoorNoisePatch), "big doors");
                TryPatchWorldNoiseHook(harmony, typeof(BundyShipHornNoisePatch), "ship horn");
                TryPatchWorldNoiseHook(harmony, typeof(BundyTurretNoisePatch), "turret fire");
                TryPatchWorldNoiseHook(harmony, typeof(BundyTerminalNoisePatch), "terminal");
                TryPatchWorldNoiseHook(harmony, typeof(BundyScanPingNoisePatch), "scan ping");
                TryPatchWorldNoiseHook(harmony, typeof(BundyWalkieNoisePatch), "walkie");

                BundyVictimInteractionsBridge.TryInitialize();
                BundyVictimInteractionsBridge.TryRegisterVictimPack();
                // #321 (B4): registration usually defers here (the Interactions API initializes
                // after this plugin), in which case the retry inside IsAvailable performs the real
                // registration and preloads from there. Preload is idempotent and no-ops until the
                // pack is in, so calling it on both seams guarantees the first finisher of a
                // session never pays for a synchronous bundle load that can stall into a T-pose.
                BundyVictimInteractionsBridge.TryPreloadVictimInteractions();
                if (DebugLogsEnabled)
                    Log.LogInfo("[Bundy] BundySyncDebug targeted finisher sync diagnostics build active (2026-05-26).");

                if (!LoadBundle()) return;

                BundyPairedActionCatalog.Register(ModAssets);
                BundyVictimPlayableController.RegisterClips(ModAssets);
                BundyAttackerPlayableController.RegisterClips(ModAssets);
                BundyAudioCatalog.Register(ModAssets);
                if (DebugLogsEnabled)
                {
                    BundyChainsawPackProofDebug.EnableLiveSpawnHotkeys = true;
                    hostGameObject.AddComponent<BundyChainsawPackProofDebug>();
                    Log.LogInfo("[Bundy] Bundy live-spawn trigger active because DebugLogs is enabled: Ctrl+Alt+F7 host-spawns live Chainsaw Bundy. Plain F3-F7 are left to tools such as CCTV. The #374 decal gate is driven from the Bundy tab in Y4NGZDebugTools, not a key.");
                }

                GameObject chainsawPrefab = LoadPrefab(ChainsawPrefabName);

                RegisterChainsaw(chainsawPrefab);

                Log.LogInfo($"{BundyPluginInfo.PLUGIN_NAME} v{BundyPluginInfo.PLUGIN_VERSION} scaffold loaded.");
            }
            catch (Exception e)
            {
                Log.LogError($"[{BundyPluginInfo.PLUGIN_NAME}] Initialization failed: {e}");
            }
        }

        // Job 3b. One world-noise hook, patched in isolation. Every one of these targets a vanilla
        // type we do not control, so the failure mode that matters is a future game build renaming a
        // method: that must cost the one event and log why, not throw out of Initialize.
        private static void TryPatchWorldNoiseHook(Harmony harmony, Type patchType, string what)
        {
            try
            {
                harmony.PatchAll(patchType);
            }
            catch (Exception ex)
            {
                Log.LogWarning(
                    $"[Bundy] World-noise hook for {what} failed to apply; that event will not reach "
                    + $"his perception. {ex.Message}");
            }
        }

        private static bool LoadBundle()
        {
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (string.IsNullOrEmpty(assemblyDir))
            {
                Log.LogError("[Bundy] Could not resolve plugin assembly directory.");
                return false;
            }

            string bundlePath = Path.Combine(assemblyDir, BundleFilename);
            if (!File.Exists(bundlePath))
            {
                Log.LogError($"[Bundy] Missing asset bundle: {bundlePath}");
                Log.LogError($"[Bundy] Expected Unity build output: C:\\Unity\\Projects\\LethalModAssets_v2\\AssetBundles\\StandaloneWindows\\{BundleFilename}");
                return false;
            }

            LogBundleFingerprint(bundlePath);

            ModAssets = AssetBundle.LoadFromFile(bundlePath);
            if (ModAssets == null)
            {
                Log.LogError($"[Bundy] AssetBundle.LoadFromFile returned null for {bundlePath}.");
                return false;
            }

            Log.LogInfo($"[Bundy] Asset bundle loaded: {bundlePath}");
            return true;
        }

        private static void LogBundleFingerprint(string bundlePath)
        {
            try
            {
                var info = new FileInfo(bundlePath);
                string sha256 = ComputeSha256(bundlePath);
                Log.LogWarning($"[Bundy] Asset bundle fingerprint: length={info.Length}, modifiedUtc={info.LastWriteTimeUtc:O}, sha256={sha256}, path='{bundlePath}'");
            }
            catch (Exception e)
            {
                Log.LogWarning($"[Bundy] Failed to fingerprint asset bundle '{bundlePath}': {e.GetType().Name}: {e.Message}");
            }
        }

        private static string ComputeSha256(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha256 = SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", string.Empty);
            }
        }

        private static GameObject LoadPrefab(string prefabName)
        {
            GameObject prefab = ModAssets.LoadAsset<GameObject>(prefabName);
            if (prefab != null)
            {
                Log.LogInfo($"[Bundy] Loaded prefab '{prefabName}'.");
                return prefab;
            }

            Log.LogError($"[Bundy] Prefab '{prefabName}' not found in {BundleFilename}. Available assets:");
            foreach (string assetName in ModAssets.GetAllAssetNames())
                Log.LogError("  - " + assetName);

            return null;
        }

        private static void RegisterChainsaw(GameObject prefab)
        {
            if (prefab == null) return;

            if (!PreparePrefab<BundyAIChainsaw>(prefab, "Bundy Chainsaw", "Chainsaw Bundy scaffold", "BundyChainsaw"))
                return;

            EnsureChainsawAccuRigSkinGuard(prefab);
            EnsureChainsawMaterialDiagnostics(prefab);
            EnsureChainsawFacialController(prefab);
            EnsureChainsawFootGrounding(prefab);
            ConfigureChainsawAnimationDiagnosticMode(prefab);
            EnsureChainsawHandIK(prefab);
            EnsureChainsawPropSpin(prefab);
            EnsureChainsawRumble(prefab);
            EnsureChainsawBladeHitbox(prefab);

            ChainsawEnemyType = CreateEnemyType(prefab, "BundyChainsaw");
            var ai = prefab.GetComponent<BundyAIChainsaw>();
            if (ai != null)
            {
                ai.ConfigureRigLabMode(false);
                ai.enemyType = ChainsawEnemyType;
            }

            TerminalNode node = CreateTerminalNode(
                "Chainsaw Bundy",
                "Chainsaw Bundy\n\nDanger level: unknown\n\nChainsaw locomotion and basic attacks are enabled. Paired finishers are still gated until chainsaw victim clips are imported.\n\n");
            TerminalKeyword keyword = CreateTerminalKeyword("chainsaw bundy", node);

            // Spawn weight is hard-clamped to 0: a vanilla-spawned Bundy has no escalation targeting
            // (no Stranger assignment, and the old fallback targeter is deleted), so he would idle
            // forever. The LethalLib registration itself is kept for its network-prefab registration.
            RegisterEnemy(ChainsawEnemyType, 0, node, keyword, "Bundy Chainsaw");
        }

        private static bool PreparePrefab<TAI>(GameObject prefab, string scanHeader, string scanSubText, string hashKey)
            where TAI : BundyAIBase
        {
            if (prefab.GetComponent<NetworkObject>() == null)
            {
                Log.LogError($"[Bundy] Prefab '{prefab.name}' is missing NetworkObject. Skipping registration.");
                return false;
            }

            AssignUniqueNetworkHash(prefab, hashKey);

            TAI ai = prefab.GetComponent<TAI>();
            if (ai == null)
            {
                ai = prefab.AddComponent<TAI>();
                Log.LogWarning($"[Bundy] Added missing {typeof(TAI).Name} to prefab '{prefab.name}' at runtime.");
            }

            WireEnemyAIFields(prefab, ai);
            EnsureBehaviourStates(ai);
            EnsureScanNode(prefab, scanHeader, scanSubText);
            EnsureEnemyCollision(prefab, ai);

            DumpPrefabComponents(prefab);

            return true;
        }

        // Wiring diagnostic: dump every component on the root and the Visual (animator) GameObject
        // of the patched prefab. Search: "BundyComponentDump"
        internal static bool DebugLogsEnabled => ModConfig?.DebugLogs?.Value ?? false;

        // #277: routine wire-up receipts used to print unconditionally at warning level on every
        // boot. They are success paths, so they go through the Bundy log gate as info instead.
        private static void LogWireUpReceipt(string message)
        {
            if (BundyLogGate.Allows(BundyLogCategory.Core, 1))
                Log.LogInfo(message);
        }

        private static void DumpPrefabComponents(GameObject prefab)
        {
            if (!DebugLogsEnabled)
                return;

            try
            {
                DumpComponents("root:" + prefab.name, prefab);

                Animator animator = prefab.GetComponent<Animator>() ?? prefab.GetComponentInChildren<Animator>(true);
                if (animator != null && animator.gameObject != prefab)
                    DumpComponents("visual:" + animator.gameObject.name, animator.gameObject);
                else if (animator == null)
                    Log.LogWarning($"BundyComponentDump prefab '{prefab.name}' has NO Animator (no Visual to dump).");
            }
            catch (Exception e)
            {
                Log.LogWarning($"BundyComponentDump exception for '{prefab?.name}': {e.GetType().Name}: {e.Message}");
            }
        }

        private static void DumpComponents(string label, GameObject go)
        {
            var sb = new StringBuilder();
            sb.Append("BundyComponentDump [").Append(label).Append("] activeSelf=").Append(go.activeSelf).Append(": ");
            Component[] comps = go.GetComponents<Component>();
            for (int i = 0; i < comps.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(comps[i] != null ? comps[i].GetType().Name : "<missing-script>");
            }
            Log.LogWarning(sb.ToString());
        }

        private static void EnsureBehaviourStates(BundyAIBase ai)
        {
            ai.enemyBehaviourStates = BundyAIBase.CreateSafeBehaviourStates();
            ai.currentBehaviourStateIndex = BundyAIBase.BehaviourStateIdle;
            ai.previousBehaviourStateIndex = BundyAIBase.BehaviourStateIdle;
            ai.currentBehaviourState = ai.enemyBehaviourStates[BundyAIBase.BehaviourStateIdle];
        }

        private static void WireEnemyAIFields(GameObject prefab, EnemyAI ai)
        {
            Animator animator = prefab.GetComponent<Animator>() ?? prefab.GetComponentInChildren<Animator>(true);
            if (animator != null)
            {
                ai.creatureAnimator = animator;
                if (ai is BundyAIBase bundyAi)
                    EnsureAnimationEventForwarder(animator, bundyAi);
            }
            else Log.LogWarning($"[Bundy] Prefab '{prefab.name}' has no Animator.");

            NavMeshAgent navAgent = prefab.GetComponent<NavMeshAgent>() ?? prefab.GetComponentInChildren<NavMeshAgent>(true);
            if (navAgent != null) ai.agent = navAgent;
            else Log.LogWarning($"[Bundy] Prefab '{prefab.name}' has no NavMeshAgent.");

            AudioSource sfx = FindAudioSource(prefab, "CreatureSFX");
            if (sfx != null) ai.creatureSFX = sfx;
            else Log.LogWarning($"[Bundy] Prefab '{prefab.name}' has no CreatureSFX AudioSource.");

            AudioSource voice = FindAudioSource(prefab, "CreatureVoice") ?? FindAudioSource(prefab, "Footsteps");
            if (voice != null) ai.creatureVoice = voice;
            else Log.LogWarning($"[Bundy] Prefab '{prefab.name}' has no CreatureVoice/Footsteps AudioSource.");

            Transform eye = prefab.transform.Find("EyeTransform");
            if (eye == null)
            {
                var eyeGo = new GameObject("EyeTransform");
                eyeGo.transform.SetParent(prefab.transform, false);
                eyeGo.transform.localPosition = new Vector3(0f, 1.6f, 0.15f);
                eye = eyeGo.transform;
                Log.LogWarning($"[Bundy] Created missing EyeTransform on prefab '{prefab.name}'.");
            }
            ai.eye = eye;
        }

        private static void EnsureEnemyCollision(GameObject prefab, EnemyAI ai)
        {
            int enemyLayer = LayerMask.NameToLayer("Enemies");
            if (enemyLayer >= 0)
                prefab.layer = enemyLayer;
            else
                Log.LogWarning("[Bundy] Layer 'Enemies' was not found; leaving root collision layer unchanged.");

            TrySetTag(prefab, "Enemy");

            CapsuleCollider body = prefab.GetComponent<CapsuleCollider>();
            if (body == null)
                body = prefab.AddComponent<CapsuleCollider>();

            body.isTrigger = false;
            body.direction = 1;
            body.center = new Vector3(0f, 1.0f, 0f);
            body.radius = 0.38f;
            body.height = 2.0f;

            Transform collision = prefab.transform.Find("Collision");
            if (collision == null)
            {
                GameObject go = new GameObject("Collision");
                go.transform.SetParent(prefab.transform, false);
                collision = go.transform;
                Log.LogWarning($"[Bundy] Created missing EnemyAICollisionDetect hitbox on prefab '{prefab.name}'.");
            }

            if (enemyLayer >= 0)
                collision.gameObject.layer = enemyLayer;
            TrySetTag(collision.gameObject, "Enemy");

            CapsuleCollider hitbox = collision.GetComponent<CapsuleCollider>();
            if (hitbox == null)
                hitbox = collision.gameObject.AddComponent<CapsuleCollider>();

            hitbox.isTrigger = true;
            hitbox.direction = 1;
            hitbox.center = new Vector3(0f, 1.05f, 0f);
            hitbox.radius = 0.72f;
            hitbox.height = 2.15f;

            EnemyAICollisionDetect detect = collision.GetComponent<EnemyAICollisionDetect>();
            if (detect == null)
                detect = collision.gameObject.AddComponent<EnemyAICollisionDetect>();

            detect.mainScript = ai;
            detect.alwaysAllowHitting = true;
            detect.canCollideWithEnemies = false;
            detect.onlyCollideWhenGrounded = false;
        }

        private static void TrySetTag(GameObject gameObject, string tag)
        {
            try
            {
                gameObject.tag = tag;
            }
            catch (UnityException)
            {
                Log.LogWarning($"[Bundy] Tag '{tag}' was not found; leaving '{gameObject.name}' tag unchanged.");
            }
        }

        private static void EnsureAnimationEventForwarder(Animator animator, BundyAIBase ai)
        {
            if (animator == null || ai == null)
                return;

            var forwarder = animator.GetComponent<BundyAnimationEventForwarder>();
            if (forwarder == null)
            {
                animator.gameObject.AddComponent<BundyAnimationEventForwarder>();
                Log.LogInfo($"[Bundy] Added AnimationEvent forwarder to '{animator.gameObject.name}'.");
            }
        }

        private static void EnsureChainsawHandIK(GameObject prefab)
        {
            Animator animator = prefab.GetComponent<Animator>() ?? prefab.GetComponentInChildren<Animator>(true);
            Transform target = FindChildRecursive(prefab.transform, "BundyChainsaw_RightHandIKTarget");
            Transform prop = FindChildRecursive(prefab.transform, "BundyProp_Chainsaw");
            if (animator == null || target == null)
            {
                Log.LogWarning(
                    $"[Bundy] Chainsaw hand IK skipped for '{prefab.name}': " +
                    $"animator={(animator != null ? animator.name : "<missing>")}, " +
                    $"target={(target != null ? target.name : "<missing>")}.");
                return;
            }

            RepairChainsawHandIKTarget(prefab, target);

            var ik = animator.GetComponent<BundyChainsawHandIK>();
            bool existed = ik != null;
            if (ik == null)
            {
                ik = animator.gameObject.AddComponent<BundyChainsawHandIK>();
            }

            ik.animator = animator;
            ik.rightHandTarget = target;
            ik.chainsawProp = prop;
            Transform canonicalArmature = FindChildRecursive(animator.transform, "Armature");
            bool canonicalGenericRig = (animator.avatar == null || !animator.avatar.isHuman) &&
                canonicalArmature != null &&
                FindChildRecursive(canonicalArmature, "upperarm_r") != null &&
                FindChildRecursive(canonicalArmature, "lowerarm_r") != null &&
                FindChildRecursive(canonicalArmature, "hand_r") != null;
            // Diagnostic isolation for the R28 arm failure: no solver-disabled
            // in-game comparison existed before 2026-07-16, so the runtime IK
            // could never be separated from body weights/bindposes as a cause.
            bool solverDisabled = ModConfig?.ChainsawDisableGenericGripSolver?.Value ?? false;
            ik.overrideAnimatedRightHandTarget = canonicalGenericRig && !solverDisabled;
            ik.rightHandTargetLocalAnchor = new Vector3(0.0689f, 0.1238f, -0.2321f);
            ik.genericTwoBoneGrip = canonicalGenericRig && !solverDisabled;
            ik.attackGripDiagnostics = ModConfig?.ChainsawAttackGripDiagnostics?.Value ?? true;
            bool configuredAttackIkDisable = ModConfig?.ChainsawForceDisableAttackHandIK?.Value ?? false;
            // The direct-sampled canonical rig has no Humanoid Avatar, so the old
            // Animator IK path never ran. Its generic solver rotates the upper/lower
            // arm while preserving both segment lengths and is safe during attacks.
            // #277: ChainsawDisableAttackHandIK was read and logged but never applied; it is now a
            // forcing override on top of the rig auto-detection (effective disable = config OR
            // non-canonical rig).
            ik.disableBasicAttackIK = configuredAttackIkDisable || !canonicalGenericRig;
            ik.rightHandPositionWeight = canonicalGenericRig ? 0.92f : 0.85f;
            ik.rightHandRotationWeight = 0f;
            ik.basicAttackPositionWeight = canonicalGenericRig ? 0.72f : 0f;
            ik.basicAttackRotationWeight = 0f;
            ik.basicAttackPeakPositionWeight = canonicalGenericRig ? 0.86f : 0f;
            ik.basicAttackPeakRotationWeight = 0f;
            ik.basicAttackBlendSeconds = 0.12f;
            // Head-only tracking keeps Bundy's attention on the player without bending the spine,
            // which previously caused sideways leaning/wobble during locomotion.
            ik.lookAtHeadWeight = 0.85f;
            ik.lookAtBodyWeight = 0f;
            ik.stateGatedIK = true;
            ik.directBoneFallback = false;
            ik.maxIkReach = 0.82f;

            LogWireUpReceipt(
                $"[Bundy] Chainsaw hand IK wired for '{prefab.name}': animator='{animator.name}', " +
                $"target='{GetRelativePath(prefab.transform, target)}', componentExisted={existed}, stateGated={ik.stateGatedIK}, " +
                $"prop='{(prop != null ? GetRelativePath(prefab.transform, prop) : "<missing>")}', " +
                $"locoWeights=({ik.rightHandPositionWeight:F2},{ik.rightHandRotationWeight:F2}), " +
                $"attackWeights=({ik.basicAttackPositionWeight:F2},{ik.basicAttackRotationWeight:F2}), " +
                $"attackPeak=({ik.basicAttackPeakPositionWeight:F2},{ik.basicAttackPeakRotationWeight:F2}), attackBlend={ik.basicAttackBlendSeconds:F2}s, " +
                $"maxReach={ik.maxIkReach:F2}m, " +
                $"attackGripDiagnostics={ik.attackGripDiagnostics}, disableBasicAttackIK={ik.disableBasicAttackIK} " +
                $"(canonicalGeneric={canonicalGenericRig}, configDisable={configuredAttackIkDisable}, solverDisabled={solverDisabled}), " +
                $"lookAtWeights=(head={ik.lookAtHeadWeight:F2},body={ik.lookAtBodyWeight:F2}) [locomotion-only], " +
                $"genericTwoBone={ik.genericTwoBoneGrip}, targetAnchor={ik.rightHandTargetLocalAnchor}, " +
                $"directFallback={ik.directBoneFallback} (forced off).");
        }

        private static void EnsureChainsawFacialController(GameObject prefab)
        {
            if (prefab == null)
                return;

            // AssetBundle MonoBehaviours resolve by assembly identity. The Unity
            // proof project's serialized BundyLipsync therefore becomes a missing
            // script in-game; always add the controller from this production DLL.
            BundyLipsync face = prefab.GetComponent<BundyLipsync>();
            bool existed = face != null;
            if (face == null)
                face = prefab.AddComponent<BundyLipsync>();

            face.autoBlink = true;
            face.animateVisemes = true;
            // Lawson 2026-07-22 round 2: peakJaw=0.85 proven in-game yet the mouth still read
            // as barely moving — typical syllables landed at weight 30-50 and closed between
            // words. Full peak, hotter curve, a 0.4 floor on audible syllables, and a slower
            // release keep the mouth clearly open for the duration of a line.
            face.maxJaw = 1f;
            face.envelopeExponent = 0.5f;
            face.speechFloor = 0.4f;
            face.attackSpeed = 30f;
            face.releaseSpeed = 5.5f;
            face.firstBlinkDelaySeconds = 1.25f;
            face.blinkCloseSeconds = 0.14f;
            face.blinkOpenSeconds = 0.20f;
            face.blinkIntervalMin = 2.5f;
            face.blinkIntervalMax = 5f;
            face.logFirstBlinkDiagnostics = true;
            face.logFirstSpeechDiagnostics = true;
            face.RebuildBindings();
            LogWireUpReceipt(
                $"[Bundy] CC5 facial controller wired from production assembly for '{prefab.name}': " +
                $"componentExisted={existed}, resolved={face.IsResolved}, renderers={face.BoundRendererCount}, " +
                $"channels={face.BoundChannelCount}, jawTargets={face.GetBoundTargetCount("Jaw_Open")}, " +
                $"blinkTargets=({face.GetBoundTargetCount("Eye_Blink_L")},{face.GetBoundTargetCount("Eye_Blink_R")}), " +
                $"voice='{(face.voiceSource != null ? face.voiceSource.name : "<missing>")}'.");
        }

        private static void EnsureChainsawMaterialDiagnostics(GameObject prefab)
        {
            Transform visual = FindChildRecursive(prefab.transform, "Visual") ?? prefab.transform;
            var diagnostics = visual.GetComponent<BundySkinMaterialDiagnostics>();
            bool existed = diagnostics != null;
            if (diagnostics == null)
                diagnostics = visual.gameObject.AddComponent<BundySkinMaterialDiagnostics>();

            diagnostics.diagnosticsEnabled = ModConfig?.ChainsawMaterialDiagnostics?.Value ?? false;
            diagnostics.lowSpecTest = ModConfig?.ChainsawMaterialLowSpecTest?.Value ?? false;
            diagnostics.normalOffTest = ModConfig?.ChainsawMaterialNormalOffTest?.Value ?? false;
            diagnostics.matteSkinTest = ModConfig?.ChainsawMaterialMatteSkinTest?.Value ?? false;
            diagnostics.headMatchTest = ModConfig?.ChainsawMaterialHeadMatchTest?.Value ?? false;

            LogWireUpReceipt(
                $"[Bundy] Skin material diagnostics wired for '{prefab.name}': " +
                $"visual='{GetRelativePath(prefab.transform, visual)}', componentExisted={existed}, " +
                $"diagnostics={diagnostics.diagnosticsEnabled}, lowSpecTest={diagnostics.lowSpecTest}, " +
                $"normalOffTest={diagnostics.normalOffTest}, matteSkinTest={diagnostics.matteSkinTest}, " +
                $"headMatchTest={diagnostics.headMatchTest}.");
        }

        private static void EnsureChainsawFootGrounding(GameObject prefab)
        {
            Animator animator = prefab.GetComponent<Animator>() ?? prefab.GetComponentInChildren<Animator>(true);
            var ai = prefab.GetComponent<BundyAIBase>();
            if (animator == null || ai == null)
            {
                Log.LogWarning(
                    $"[Bundy] Foot grounding skipped for '{prefab.name}': " +
                    $"animator={(animator != null)}, ai={(ai != null)}.");
                return;
            }

            var grounding = animator.GetComponent<BundyChainsawFootGrounding>();
            bool existed = grounding != null;
            if (grounding == null)
                grounding = animator.gameObject.AddComponent<BundyChainsawFootGrounding>();

            grounding.animator = animator;
            grounding.ai = ai;
            LogWireUpReceipt(
                $"[Bundy] Foot grounding wired for '{prefab.name}': " +
                $"visual='{GetRelativePath(prefab.transform, animator.transform)}', componentExisted={existed}, " +
                $"enabledCfg={ModConfig?.ChainsawFootGroundingEnabled?.Value ?? false}, " +
                $"maxDown={grounding.maxDownOffset:F2}, maxUp={grounding.maxUpOffset:F2}, smooth={grounding.smoothSpeed:F1}.");

            var footIK = animator.GetComponent<BundyChainsawFootIK>();
            bool ikExisted = footIK != null;
            if (footIK == null)
                footIK = animator.gameObject.AddComponent<BundyChainsawFootIK>();

            footIK.animator = animator;
            footIK.ai = ai;
            LogWireUpReceipt(
                $"[Bundy] Foot IK wired for '{prefab.name}': componentExisted={ikExisted}, " +
                $"enabledCfg={ModConfig?.ChainsawFootIKEnabled?.Value ?? false}, " +
                $"maxLift={ModConfig?.ChainsawFootIKMaxLift?.Value ?? 0.45f:F2}, " +
                $"slopeAlign={ModConfig?.ChainsawFootIKSlopeAlign?.Value ?? true}.");
        }

        private static void EnsureChainsawBladeHitbox(GameObject prefab)
        {
            var ai = prefab.GetComponent<BundyAIBase>();
            Transform prop = FindChildRecursive(prefab.transform, "BundyProp_Chainsaw");
            if (ai == null || prop == null)
            {
                Log.LogWarning(
                    $"[Bundy] Chainsaw blade hitbox skipped for '{prefab.name}': " +
                    $"ai={(ai != null)}, prop={(prop != null ? GetRelativePath(prefab.transform, prop) : "<missing>")}.");
                return;
            }

            Transform bladeRoot = FindChainsawBladeRoot(prop);
            var hitbox = bladeRoot.GetComponent<BundyChainsawBladeHitbox>();
            bool existed = hitbox != null;
            if (hitbox == null)
                hitbox = bladeRoot.gameObject.AddComponent<BundyChainsawBladeHitbox>();

            hitbox.Configure(ai, bladeRoot);
            hitbox.radius = 0.24f;
            hitbox.padding = 0.1f;
            hitbox.segmentTrimFraction = 0.08f;
            LogWireUpReceipt(
                $"[Bundy] Chainsaw blade hitbox wired for '{prefab.name}': " +
                $"prop='{GetRelativePath(prefab.transform, prop)}', bladeRoot='{GetRelativePath(prefab.transform, bladeRoot)}', componentExisted={existed}, " +
                $"radius={hitbox.radius:F2}, padding={hitbox.padding:F2}, trim={hitbox.segmentTrimFraction:F2}.");
        }

        private static void EnsureChainsawPropSpin(GameObject prefab)
        {
            Transform prop = FindChildRecursive(prefab.transform, "BundyProp_Chainsaw");
            Transform custom = prop != null ? FindChildRecursive(prop, "BundyCustomChainsaw_Animated") : null;
            Animator animator = custom != null ? custom.GetComponent<Animator>() ?? custom.GetComponentInChildren<Animator>(true) : null;
            if (prop == null || custom == null || animator == null)
            {
                Log.LogWarning(
                    $"[Bundy] Chainsaw prop spin skipped for '{prefab.name}': " +
                    $"prop={(prop != null ? GetRelativePath(prefab.transform, prop) : "<missing>")}, " +
                    $"custom={(custom != null ? GetRelativePath(prefab.transform, custom) : "<missing>")}, animator={(animator != null)}.");
                return;
            }

            var spin = prop.GetComponent<BundyChainsawPropSpin>();
            bool existed = spin != null;
            if (spin == null)
                spin = prop.gameObject.AddComponent<BundyChainsawPropSpin>();

            spin.propAnimator = animator;
            spin.startStopped = true;
            spin.spinSpeed = 1f;
            spin.spinStateName = "Anim_Chainsaw_On";

            LogWireUpReceipt(
                $"[Bundy] Chainsaw prop spin wired for '{prefab.name}': " +
                $"prop='{GetRelativePath(prefab.transform, prop)}', custom='{GetRelativePath(prefab.transform, custom)}', " +
                $"animator='{animator.name}', componentExisted={existed}, state='{spin.spinStateName}'.");
        }

        // Cosmetic engine vibration. Sits on the prop itself, alongside BundyChainsawPropSpin — the two
        // do not conflict: spin drives the Animator on the BundyCustomChainsaw_Animated CHILD (the
        // blade-chain loop) and never touches the prop transform, while the rumble only ever writes the
        // prop's localRotation.
        private static void EnsureChainsawRumble(GameObject prefab)
        {
            Transform prop = FindChildRecursive(prefab.transform, "BundyProp_Chainsaw");
            if (prop == null)
            {
                Log.LogWarning($"[Bundy] Chainsaw rumble skipped for '{prefab.name}': BundyProp_Chainsaw not found.");
                return;
            }

            var rumble = prop.GetComponent<BundyChainsawRumble>();
            bool existed = rumble != null;
            if (rumble == null)
                rumble = prop.gameObject.AddComponent<BundyChainsawRumble>();

            rumble.prop = prop;
            rumble.bladeAxis = BundyChainsawRumble.DefaultBladeAxis;

            LogWireUpReceipt(
                $"[Bundy] Chainsaw rumble wired for '{prefab.name}': " +
                $"prop='{GetRelativePath(prefab.transform, prop)}', componentExisted={existed}, " +
                $"axis={rumble.bladeAxis}, " +
                $"enabled={ModConfig?.ChainsawRumbleEnabled?.Value}, " +
                $"freq={ModConfig?.ChainsawRumbleFrequencyHz?.Value}Hz, " +
                $"amplitude={ModConfig?.ChainsawRumbleAmplitudeDegrees?.Value}deg, " +
                $"revGain={ModConfig?.ChainsawRumbleRevGain?.Value}.");
        }

        private static Transform FindChainsawBladeRoot(Transform prop)
        {
            if (prop == null)
                return null;

            string[] exactNames =
            {
                "ChainsawBlade",
                "Chainsaw_Blade",
                "Blade",
                "Bar",
                "Chain",
                "ChainsawMesh",
                "Chainsaw_mesh",
            };

            for (int i = 0; i < exactNames.Length; i++)
            {
                Transform match = FindChildRecursive(prop, exactNames[i]);
                if (match != null)
                    return match;
            }

            Transform containsMatch = FindChildNameContaining(prop, "blade") ??
                FindChildNameContaining(prop, "bar") ??
                FindChildNameContaining(prop, "chain") ??
                FindChildNameContaining(prop, "saw");
            return containsMatch ?? prop;
        }

        private static Transform FindChildNameContaining(Transform parent, string needle)
        {
            if (parent == null || string.IsNullOrEmpty(needle))
                return null;

            foreach (Transform child in parent)
            {
                if (child.name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                    return child;

                Transform nested = FindChildNameContaining(child, needle);
                if (nested != null)
                    return nested;
            }

            return null;
        }

        private static void RepairChainsawHandIKTarget(GameObject prefab, Transform target)
        {
            if (prefab == null || target == null || target.parent == null)
                return;

            Transform parent = target.parent;
            Vector3 before = target.localPosition;
            // The baked source clips carry a centimetre-space target at
            // (0.7925, 0.6808, -0.4218). Dividing by the prop's 100 scale still
            // left it 1.6-1.9 m from the canonical hand. This anchor is the real
            // hand position captured in prop space from Chainsaw_Idle and is also
            // stable in Chainsaw_WalkForward (within 2 mm).
            Vector3 repaired = new Vector3(0.0689f, 0.1238f, -0.2321f);
            target.localPosition = repaired;
            LogWireUpReceipt(
                $"[Bundy] Repaired chainsaw hand IK target for '{prefab.name}': parent='{parent.name}', " +
                $"localPosition {before} -> {repaired} (canonical idle grip anchor).");
        }

        private static void EnsureChainsawAccuRigSkinGuard(GameObject prefab)
        {
            Transform visual = FindChildRecursive(prefab.transform, "Visual");
            if (visual == null)
            {
                Log.LogWarning($"[Bundy] AccuRig skin guard skipped for '{prefab.name}': missing Visual child.");
                return;
            }

            if (FindChildRecursive(visual, "CC_Base_Hip") == null && FindChildRecursive(visual, "CC_Base_Pelvis") == null)
            {
                Log.LogInfo($"[Bundy] AccuRig skin guard skipped for '{prefab.name}': Visual is not an AccuRig hierarchy.");
                return;
            }

            Transform canonicalArmature = FindChildRecursive(visual, "Armature");
            if (canonicalArmature != null &&
                FindChildRecursive(canonicalArmature, "pelvis") != null &&
                FindChildRecursive(canonicalArmature, "hand_l") != null &&
                FindChildRecursive(canonicalArmature, "hand_r") != null)
            {
                Log.LogInfo(
                    $"[Bundy] AccuRig skin guard skipped for '{prefab.name}': the live body uses the canonical " +
                    "Armature/pelvis/hand_l/hand_r rig; CC_Base bones belong only to the nested facial proof.");
                return;
            }

            var guard = visual.GetComponent<BundyAccuRigSkinGuard>();
            bool existed = guard != null;
            if (guard == null)
                guard = visual.gameObject.AddComponent<BundyAccuRigSkinGuard>();

            guard.PinHipTranslation = ModConfig?.ChainsawPinAccuRigHipTranslation?.Value ?? true;
            guard.ConfigurePrefabVisual();
            LogWireUpReceipt(
                $"[Bundy] AccuRig skin guard wired for '{prefab.name}': visual='{GetRelativePath(prefab.transform, visual)}', " +
                $"componentExisted={existed}, pinHipTranslation={guard.PinHipTranslation}. It pins the AccuRig FBX basis after Animator updates to prevent exploded skinned meshes.");
        }

        private static void ConfigureChainsawAnimationDiagnosticMode(GameObject prefab)
        {
            if (ModConfig?.ChainsawStaticVisualTestMode?.Value != true)
                return;

            Animator animator = prefab.GetComponent<Animator>() ?? prefab.GetComponentInChildren<Animator>(true);
            if (animator == null)
            {
                Log.LogWarning($"[Bundy] Chainsaw static visual test mode requested for '{prefab.name}', but no Animator was found.");
                return;
            }

            string controllerName = animator.runtimeAnimatorController != null
                ? animator.runtimeAnimatorController.name
                : "<none>";
            animator.runtimeAnimatorController = null;
            animator.enabled = false;
            Log.LogWarning(
                $"[Bundy] Chainsaw static visual test mode is ON for '{prefab.name}'; disabled Animator '{animator.name}' controller '{controllerName}'. " +
                "If this rest mesh is normal, the deformation is animation/avatar translation, not rendering/material/layer setup.");
        }

        private static AudioSource FindAudioSource(GameObject prefab, string childName)
        {
            Transform child = prefab.transform.Find(childName);
            return child != null ? child.GetComponent<AudioSource>() : null;
        }

        private static Transform FindChildRecursive(Transform parent, string childName)
        {
            if (parent == null)
                return null;

            foreach (Transform child in parent)
            {
                if (child.name == childName)
                    return child;

                Transform nested = FindChildRecursive(child, childName);
                if (nested != null)
                    return nested;
            }

            return null;
        }

        private static string GetRelativePath(Transform root, Transform target)
        {
            if (root == null || target == null)
                return "<null>";
            if (root == target)
                return "<root>";

            var names = new Stack<string>();
            for (Transform current = target; current != null && current != root; current = current.parent)
                names.Push(current.name);

            return names.Count > 0 ? string.Join("/", names.ToArray()) : target.name;
        }

        private static void EnsureScanNode(GameObject prefab, string headerText, string subText)
        {
            Transform scanNode = prefab.transform.Find("ScanNode");
            if (scanNode == null)
            {
                var go = new GameObject("ScanNode");
                go.transform.SetParent(prefab.transform, false);
                go.transform.localPosition = new Vector3(0f, 1.5f, 0f);

                int scanLayer = LayerMask.NameToLayer("ScanNode");
                if (scanLayer >= 0) go.layer = scanLayer;
                try { go.tag = "DoNotSet"; } catch (UnityException) { }

                var box = go.AddComponent<BoxCollider>();
                box.isTrigger = true;
                box.size = new Vector3(0.6f, 0.6f, 0.6f);

                scanNode = go.transform;
                Log.LogWarning($"[Bundy] Created missing ScanNode on prefab '{prefab.name}'.");
            }

            var props = scanNode.GetComponent<ScanNodeProperties>();
            if (props == null) props = scanNode.gameObject.AddComponent<ScanNodeProperties>();

            props.headerText = headerText;
            props.subText = subText;
            props.maxRange = 13;
            props.minRange = 1;
            props.nodeType = 1;
            props.creatureScanID = -1;
        }

        private static EnemyType CreateEnemyType(GameObject prefab, string enemyName)
        {
            var enemyType = ScriptableObject.CreateInstance<EnemyType>();
            enemyType.enemyName = enemyName;
            enemyType.enemyPrefab = prefab;
            enemyType.PowerLevel = 3f;
            enemyType.DiversityPowerLevel = 1;
            enemyType.MaxCount = 1;
            enemyType.canDie = true;
            enemyType.canBeStunned = true;
            enemyType.canBeDestroyed = true;
            enemyType.stunTimeMultiplier = 1f;
            enemyType.stunGameDifficultyMultiplier = 1f;
            enemyType.destroyOnDeath = false;
            enemyType.doorSpeedMultiplier = 1f;
            enemyType.isOutsideEnemy = true;
            enemyType.isDaytimeEnemy = false;
            enemyType.normalizedTimeInDayToLeave = 1f;
            enemyType.probabilityCurve = new AnimationCurve(
                new Keyframe(0f, 1f), new Keyframe(1f, 1f));
            enemyType.numberSpawnedFalloff = new AnimationCurve(
                new Keyframe(0f, 1f), new Keyframe(1f, 1f));
            return enemyType;
        }

        private static TerminalNode CreateTerminalNode(string creatureName, string displayText)
        {
            var node = ScriptableObject.CreateInstance<TerminalNode>();
            node.displayText = displayText;
            node.creatureName = creatureName;
            node.maxCharactersToType = 35;
            node.clearPreviousText = true;
            return node;
        }

        private static TerminalKeyword CreateTerminalKeyword(string word, TerminalNode node)
        {
            var keyword = ScriptableObject.CreateInstance<TerminalKeyword>();
            keyword.word = word;
            keyword.isVerb = false;
            keyword.specialKeywordResult = node;
            return keyword;
        }

        private static void RegisterEnemy(EnemyType enemyType, int spawnWeight, TerminalNode node, TerminalKeyword keyword, string label)
        {
            LethalLib.Modules.NetworkPrefabs.RegisterNetworkPrefab(enemyType.enemyPrefab);

            Enemies.RegisterEnemy(
                enemyType,
                spawnWeight,
                Levels.LevelTypes.All,
                Enemies.SpawnType.Outside,
                node,
                keyword
            );

            Log.LogInfo($"[Bundy] Registered {label}: spawnWeight={spawnWeight}, levels=All, spawnType=Outside.");
        }

        private static void AssignUniqueNetworkHash(GameObject prefab, string hashKey)
        {
            try
            {
                var netObj = prefab.GetComponent<NetworkObject>();
                if (netObj == null) return;

                byte[] bytes = MD5.Create().ComputeHash(
                    Encoding.UTF8.GetBytes(BundyPluginInfo.PLUGIN_GUID + "." + hashKey));
                uint hash = BitConverter.ToUInt32(bytes, 0);

                var prop = typeof(NetworkObject).GetProperty("GlobalObjectIdHash",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null && prop.CanWrite)
                {
                    prop.SetValue(netObj, hash);
                }
                else
                {
                    var field = typeof(NetworkObject).GetField("GlobalObjectIdHash",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (field != null) field.SetValue(netObj, hash);
                    else Log.LogWarning($"[Bundy] Could not locate NetworkObject.GlobalObjectIdHash on '{prefab.name}'.");
                }

                Log.LogInfo($"[Bundy] {prefab.name} NetworkObject GlobalObjectIdHash = {hash}");
            }
            catch (Exception e)
            {
                Log.LogError($"[Bundy] AssignUniqueNetworkHash failed for '{prefab.name}': {e}");
            }
        }
    }
}



