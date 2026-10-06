using System;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using GameNetcodeStuff;
using LethalLib.Modules;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts._Shared;
namespace Y4NGZCompany.Contracts.Whistleblower
{
    internal static class WhistleblowerEnemyRegistry
    {
        internal const string EnemyName = "Whistleblower";
        private const float VisualScale = 1.52f;
        // #486: the vanilla-friendly variant is the scavenger mesh rescaled to 1.638 m at root
        // scale 1. A vanilla player is 2.58 m in world units (mesh 15.51 units tall, bind-pose
        // bones at 6.7396, metarig at world scale 1.1216, measured from the ripped Player
        // prefab and LOD1 mesh), so player height is 2.58 / 1.638 = 1.576. The
        // spine_001..spine_004 bone-length ratio between the two rigs gives the same 1.576.
        // Committed here rather than measured at runtime so every machine agrees; the
        // runtime only logs a check (WhistleblowerEnemyAI.LogVanillaVisualHeightCheck).
        private const float VanillaVisualScale = 1.576f;

        // #984: the three emitter children, named because the runtime resolves them BY NAME
        // (WhistleblowerEnemyAI.ResolveAudioSourcesByRole) rather than by "first AudioSource
        // in the hierarchy", which used to be able to hand the SFX emitter to creatureVoice.
        internal const string VoiceSourceName = "WhistleblowerVoice";
        internal const string SfxSourceName = "WhistleblowerSFX";
        internal const string FootstepSourceName = "WhistleblowerFootsteps";

        // Voice and combat SFX sit at chest/head height; footsteps come off the floor. The
        // emitter children live in LOCAL space under a 1.52/1.576-scaled root, so the foot
        // emitter's local y is the world height divided by that scale.
        private const float EmitterLocalHeight = 2f;
        private const float FootstepWorldHeight = 0.05f;

        internal static EnemyType EnemyType { get; private set; }
        internal static GameObject EnemyPrefab { get; private set; }
        internal static bool Registered { get; private set; }
        private static Material _runtimeVisualMaterial;
        private static Material _fallbackTrimMaterial;
        private static Material _fallbackSkinMaterial;
        private static bool _loggedBundledShaderRepair;
        private static bool _visualRepairFailureLogged;

        internal static void Register()
        {
            if (Registered)
                return;

            GameObject prefab = ContractAssetLoader.WhistleblowerVisualPrefab;
            if (prefab == null)
            {
                ContractsBootstrap.Log?.LogWarning("[MoonContracts] Whistleblower enemy was not registered because WhistleblowerVisual is missing.");
                return;
            }

            EnemyPrefab = prefab;
            EnemyPrefab.name = "LGU_WhistleblowerEnemy";
            EnemyType = CreateEnemyType(EnemyPrefab);
            ConfigurePrefab(EnemyPrefab, EnemyType);

            LethalLib.Modules.NetworkPrefabs.RegisterNetworkPrefab(EnemyPrefab);
            TerminalNode terminalNode = CreateTerminalNode();
            TerminalKeyword terminalKeyword = CreateTerminalKeyword(terminalNode);
            Enemies.RegisterEnemy(
                EnemyType,
                0,
                Levels.LevelTypes.All,
                Enemies.SpawnType.Default,
                terminalNode,
                terminalKeyword);

            Registered = true;
            ContractsBootstrap.Log?.LogInfo("[MoonContracts] Whistleblower registered as a real interior EnemyType with natural rarity 0.");
        }

        private static EnemyType CreateEnemyType(GameObject prefab)
        {
            var enemyType = ScriptableObject.CreateInstance<EnemyType>();
            enemyType.enemyName = EnemyName;
            enemyType.enemyPrefab = prefab;
            enemyType.PowerLevel = 0f;
            enemyType.MaxCount = 1;
            enemyType.canDie = true;
            enemyType.canBeStunned = false;
            enemyType.canBeDestroyed = false;
            enemyType.destroyOnDeath = false;
            enemyType.spawningDisabled = true;
            enemyType.stunTimeMultiplier = 1f;
            enemyType.doorSpeedMultiplier = 1f;
            enemyType.stunGameDifficultyMultiplier = 1f;
            enemyType.pushPlayerForce = 0f;
            enemyType.pushPlayerDistance = 0f;
            enemyType.disableAnimatorWhenFar = false;
            enemyType.isOutsideEnemy = false;
            enemyType.isDaytimeEnemy = false;
            enemyType.probabilityCurve = AnimationCurve.Constant(0f, 1f, 1f);
            enemyType.numberSpawnedFalloff = AnimationCurve.Linear(0f, 1f, 1f, 0f);
            enemyType.useNumberSpawnedFalloff = false;
            return enemyType;
        }

        private static void ConfigurePrefab(GameObject prefab, EnemyType enemyType)
        {
            SetTagSafe(prefab, "Enemy");
            prefab.layer = ResolveLayer("Enemies", 19);
            prefab.transform.localScale = Vector3.one
                * (ContractAssetLoader.WhistleblowerVisualIsVanillaVariant ? VanillaVisualScale : VisualScale);

            var networkObject = prefab.GetComponent<NetworkObject>();
            if (networkObject == null)
                networkObject = prefab.AddComponent<NetworkObject>();
            AssignStableNetworkHash(networkObject, "WhistleblowerEnemy");

            var agent = prefab.GetComponent<NavMeshAgent>();
            if (agent == null)
                agent = prefab.AddComponent<NavMeshAgent>();
            agent.speed = 3.35f;
            agent.angularSpeed = 460f;
            agent.acceleration = 14f;
            agent.stoppingDistance = 1.65f;
            agent.radius = 0.44f;
            agent.height = 2.55f;
            agent.baseOffset = 0f;
            agent.autoBraking = true;
            agent.updateRotation = true;
            agent.enabled = false;

            var ai = prefab.GetComponent<WhistleblowerEnemyAI>();
            if (ai == null)
                ai = prefab.AddComponent<WhistleblowerEnemyAI>();
            ai.enemyType = enemyType;
            ai.agent = agent;
            ai.creatureAnimator = prefab.GetComponentInChildren<Animator>(true);
            ConfigureAnimator(ai.creatureAnimator);
            ConfigureAnimationDriver(prefab, ai.creatureAnimator);
            NormalizeVisualMaterials(prefab);
            ai.creatureVoice = EnsureAudioSource(prefab, VoiceSourceName, 0.9f, 2f, 32f, EmitterLocalHeight);
            // #984: source volume 1.0, like vanilla's movementAudio. The one-shot scalars
            // carry the loudness from here (WhistleblowerEnemyAI.SfxSourceGainCompensation
            // holds the pre-#984 combat foley where it was).
            ai.creatureSFX = EnsureAudioSource(prefab, SfxSourceName, 1f, 1.5f, 20f, EmitterLocalHeight);
            // #984: footsteps get their own emitter, at his feet rather than 3.15 m above
            // them, so a step reads as a body on the floor next to you and so that a swing
            // or blunt hit can no longer leave its randomised pitch on the next footstep.
            EnsureAudioSource(
                prefab,
                FootstepSourceName,
                1f,
                1.5f,
                20f,
                FootstepWorldHeight / Mathf.Max(0.01f, prefab.transform.localScale.y));
            ai.eye = FindChildRecursive(prefab.transform, "Head")
                ?? FindChildRecursive(prefab.transform, "head")
                ?? prefab.transform;
            ai.enemyBehaviourStates = WhistleblowerEnemyAI.CreateBehaviourStates();

            EnsureBodyCollider(prefab, ai);
            EnsureCollisionChild(prefab, ai);
            DisableInteractionTrigger(prefab);
            EnsureScanNode(prefab);
        }

        internal static int RepairVisualInstance(GameObject root)
        {
            if (root == null)
                return 0;

            Animator animator = root.GetComponentInChildren<Animator>(true);
            ConfigureAnimator(animator);
            ConfigureAnimationDriver(root, animator);
            int repaired = NormalizeVisualMaterials(root);
            if (UseRuntimeFallbackVisual(root))
                EnsureFallbackVisual(root);
            return UseRuntimeFallbackVisual(root) ? Mathf.Max(1, repaired) : repaired;
        }

        /// <summary>
        /// Resolves the runtime fallback visual under <paramref name="root"/>, if any.
        /// #716/B10: callers cache this instead of walking the rig every frame.
        /// </summary>
        internal static WhistleblowerFallbackVisual FindFallbackVisual(GameObject root)
        {
            return root != null ? root.GetComponentInChildren<WhistleblowerFallbackVisual>(true) : null;
        }

        internal static void UpdateFallbackPose(WhistleblowerFallbackVisual fallback, bool scared, bool dead, float speed)
        {
            if (fallback != null)
                fallback.ApplyPose(scared, dead, speed);
        }

        private static void ConfigureAnimator(Animator animator)
        {
            if (animator == null)
            {
                ContractsBootstrap.Log?.LogWarning("[MoonContracts] Whistleblower visual has no Animator.");
                return;
            }

            bool needsBundledAvatar = animator.avatar == null
                || !animator.avatar.isValid
                || !animator.avatar.isHuman;

            if (needsBundledAvatar && ContractAssetLoader.WhistleblowerAvatar != null)
            {
                animator.avatar = ContractAssetLoader.WhistleblowerAvatar;
                ContractsBootstrap.Log?.LogInfo("[MoonContracts] Assigned bundled humanoid Avatar to Whistleblower animator.");
            }

            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.enabled = true;
            if (animator.runtimeAnimatorController == null && ContractAssetLoader.WhistleblowerController != null)
                animator.runtimeAnimatorController = ContractAssetLoader.WhistleblowerController;
            if (animator.avatar == null || !animator.avatar.isValid || !animator.avatar.isHuman)
                ContractsBootstrap.Log?.LogWarning("[MoonContracts] Whistleblower animator still has no valid humanoid Avatar; animations will not retarget.");
        }

        private static void ConfigureAnimationDriver(GameObject root, Animator animator)
        {
            if (root == null || animator == null)
                return;

            var driver = root.GetComponent<WhistleblowerAnimationDriver>();
            if (driver == null)
                driver = root.AddComponent<WhistleblowerAnimationDriver>();
            driver.Configure(animator);
        }

        private static int NormalizeVisualMaterials(GameObject prefab)
        {
            if (prefab == null)
                return 0;

            // The vanilla-friendly variant ships its own material and base colour; overriding it
            // with the Whistleblower_BaseColor runtime material would repaint it as the default model.
            Material material = ContractAssetLoader.WhistleblowerVisualIsVanillaVariant
                ? null
                : CreateRuntimeVisualMaterial(prefab);
            int repaired = 0;
            foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null || renderer.GetComponentInParent<ScanNodeProperties>() != null)
                    continue;
                if (renderer.GetComponentInParent<WhistleblowerFallbackVisual>() != null)
                    continue;

                EnsureHierarchyActive(renderer.transform, prefab.transform);
                renderer.gameObject.layer = 0;
                renderer.enabled = true;
                renderer.forceRenderingOff = false;
                renderer.allowOcclusionWhenDynamic = false;
                renderer.shadowCastingMode = ShadowCastingMode.On;
                renderer.receiveShadows = true;
                renderer.SetPropertyBlock(null);

                var skinned = renderer as SkinnedMeshRenderer;
                if (skinned != null)
                {
                    skinned.updateWhenOffscreen = true;
                    skinned.localBounds = new Bounds(new Vector3(0f, 1.35f, 0f), new Vector3(8f, 8f, 8f));
                }

                if (material != null)
                {
                    Material[] materials = renderer.sharedMaterials;
                    if (materials == null || materials.Length == 0)
                    {
                        renderer.sharedMaterial = material;
                    }
                    else
                    {
                        for (int i = 0; i < materials.Length; i++)
                            materials[i] = material;
                        renderer.sharedMaterials = materials;
                    }
                }

                if (material == null)
                    RepairBundledMaterialShaders(renderer);

                if (UseRuntimeFallbackVisual(prefab))
                    renderer.enabled = false;

                repaired++;
            }

            // #716/F5: RepairVisualIfNeeded retries every 1.5s for the whole round while
            // this returns 0, so the failure case says its piece once at Warning and then
            // drops to Debug. A successful repair stops the retry loop by itself.
            if (repaired > 0)
            {
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts] Repaired Whistleblower visual renderers on {prefab.name}: {repaired} renderer(s), material={material?.shader?.name ?? "none"}.");
            }
            else if (!_visualRepairFailureLogged)
            {
                _visualRepairFailureLogged = true;
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts] Whistleblower visual repair found no renderers on {prefab.name}; further attempts log at Debug.");
            }
            else
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts] Whistleblower visual repair found no renderers on {prefab.name}.");
            }

            return repaired;
        }

        private static void EnsureFallbackVisual(GameObject root)
        {
            if (root == null)
                return;

            Transform existing = root.transform.Find("WhistleblowerRuntimeFallbackVisual");
            WhistleblowerFallbackVisual fallback;
            if (existing == null)
            {
                var fallbackObject = new GameObject("WhistleblowerRuntimeFallbackVisual");
                fallbackObject.layer = 0;
                fallbackObject.transform.SetParent(root.transform, false);
                fallbackObject.transform.localPosition = Vector3.zero;
                fallbackObject.transform.localRotation = Quaternion.identity;
                fallbackObject.transform.localScale = Vector3.one * 0.78f;
                fallback = fallbackObject.AddComponent<WhistleblowerFallbackVisual>();
            }
            else
            {
                fallback = existing.GetComponent<WhistleblowerFallbackVisual>();
                if (fallback == null)
                    fallback = existing.gameObject.AddComponent<WhistleblowerFallbackVisual>();

                existing.gameObject.SetActive(true);
            }

            fallback.Build(
                CreateRuntimeVisualMaterial(root),
                GetFallbackTrimMaterial(),
                GetFallbackSkinMaterial());
        }

        private static bool UseRuntimeFallbackVisual(GameObject root)
        {
            if (ContractsBootstrap.WhistleblowerBalance == null || !ContractsBootstrap.WhistleblowerBalance.UseFallbackVisual.Value)
                return false;

            if (root == null)
                return true;

            return root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length == 0;
        }

        private static Material GetFallbackTrimMaterial()
        {
            if (_fallbackTrimMaterial != null)
                return _fallbackTrimMaterial;

            _fallbackTrimMaterial = CreateFallbackMaterial("Whistleblower_FallbackTrim", new Color(0.045f, 0.038f, 0.032f, 1f), 0.18f);
            return _fallbackTrimMaterial;
        }

        private static Material GetFallbackSkinMaterial()
        {
            if (_fallbackSkinMaterial != null)
                return _fallbackSkinMaterial;

            _fallbackSkinMaterial = CreateFallbackMaterial("Whistleblower_FallbackSkin", new Color(0.63f, 0.39f, 0.26f, 1f), 0.28f);
            return _fallbackSkinMaterial;
        }

        private static Material CreateFallbackMaterial(string name, Color color, float smoothness)
        {
            Shader shader = ResolveVisualShader();
            if (shader == null)
                return null;

            var material = new Material(shader) { name = name };
            SetColorIfPresent(material, "_BaseColor", color);
            SetColorIfPresent(material, "_Color", color);
            SetFloatIfPresent(material, "_Metallic", 0f);
            SetFloatIfPresent(material, "_Smoothness", smoothness);
            SetFloatIfPresent(material, "_SmoothnessRemapMax", smoothness);
            SetFloatIfPresent(material, "_Glossiness", smoothness);
            SetFloatIfPresent(material, "_SurfaceType", 0f);
            SetFloatIfPresent(material, "_AlphaCutoffEnable", 0f);
            material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.renderQueue = 2000;
            return material;
        }

        private static void EnsureHierarchyActive(Transform child, Transform stopAt)
        {
            for (Transform current = child; current != null; current = current.parent)
            {
                current.gameObject.SetActive(true);
                if (current == stopAt)
                    break;
            }
        }

        // The variant keeps its own bundled materials, but a bundled material can land in-game
        // with a broken shader binding — the failure mode CreateRuntimeVisualMaterial exists for.
        private static void RepairBundledMaterialShaders(Renderer renderer)
        {
            foreach (var material in renderer.sharedMaterials)
            {
                if (material == null)
                    continue;
                if (material.shader != null && material.shader.name != "Hidden/InternalErrorShader")
                    continue;

                Shader shader = ResolveVisualShader();
                if (shader == null)
                    return;

                material.shader = shader;
                if (!_loggedBundledShaderRepair)
                {
                    _loggedBundledShaderRepair = true;
                    ContractsBootstrap.Log?.LogWarning($"[MoonContracts] Whistleblower bundled material '{material.name}' lost its shader binding; re-pointed it at '{shader.name}'.");
                }
            }
        }

        private static Material CreateRuntimeVisualMaterial(GameObject prefab)
        {
            if (_runtimeVisualMaterial != null)
                return _runtimeVisualMaterial;

            Shader shader = ResolveVisualShader();
            if (shader == null)
            {
                ContractsBootstrap.Log?.LogWarning("[MoonContracts] Could not find a compatible shader for the Whistleblower runtime material.");
                return null;
            }

            _runtimeVisualMaterial = new Material(shader) { name = "Whistleblower_RuntimeVisual" };
            Texture baseColor = ContractAssetLoader.WhistleblowerBaseColor ?? FindBaseColorFromPrefab(prefab);

            if (baseColor != null)
            {
                SetTextureIfPresent(_runtimeVisualMaterial, "_BaseColorMap", baseColor);
                SetTextureIfPresent(_runtimeVisualMaterial, "_MainTex", baseColor);
                SetColorIfPresent(_runtimeVisualMaterial, "_BaseColor", Color.white);
                SetColorIfPresent(_runtimeVisualMaterial, "_Color", Color.white);
            }
            else
            {
                SetColorIfPresent(_runtimeVisualMaterial, "_BaseColor", new Color(0.78f, 0.36f, 0.18f, 1f));
                SetColorIfPresent(_runtimeVisualMaterial, "_Color", new Color(0.78f, 0.36f, 0.18f, 1f));
            }

            SetFloatIfPresent(_runtimeVisualMaterial, "_Metallic", 0f);
            SetFloatIfPresent(_runtimeVisualMaterial, "_Smoothness", 0.24f);
            SetFloatIfPresent(_runtimeVisualMaterial, "_SmoothnessRemapMax", 0.35f);
            SetFloatIfPresent(_runtimeVisualMaterial, "_Glossiness", 0.24f);
            SetFloatIfPresent(_runtimeVisualMaterial, "_SurfaceType", 0f);
            SetFloatIfPresent(_runtimeVisualMaterial, "_AlphaCutoffEnable", 0f);
            SetFloatIfPresent(_runtimeVisualMaterial, "_ZWrite", 1f);
            SetColorIfPresent(_runtimeVisualMaterial, "_EmissiveColor", Color.black);
            _runtimeVisualMaterial.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
            _runtimeVisualMaterial.DisableKeyword("_NORMALMAP");
            _runtimeVisualMaterial.DisableKeyword("_NORMALMAP_TANGENT_SPACE");
            _runtimeVisualMaterial.renderQueue = 2000;

            ContractsBootstrap.Log?.LogInfo($"[MoonContracts] Created Whistleblower runtime material with texture={baseColor?.name ?? "none"}.");
            return _runtimeVisualMaterial;
        }

        private static Shader ResolveVisualShader()
        {
            return Shader.Find("HDRP/Lit")
                ?? Shader.Find("High Definition Render Pipeline/Lit")
                ?? Shader.Find("HDRenderPipeline/Lit")
                ?? Shader.Find("Universal Render Pipeline/Lit")
                ?? Shader.Find("Standard");
        }

        private static Texture FindBaseColorFromPrefab(GameObject prefab)
        {
            if (prefab == null)
                return null;

            foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null)
                    continue;

                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null)
                        continue;

                    Texture texture = null;
                    if (material.HasProperty("_BaseColorMap"))
                        texture = material.GetTexture("_BaseColorMap");
                    if (texture == null && material.HasProperty("_MainTex"))
                        texture = material.GetTexture("_MainTex");
                    if (texture != null)
                        return texture;
                }
            }

            return null;
        }

        private static void AssignStableNetworkHash(NetworkObject networkObject, string hashKey)
        {
            if (networkObject == null)
                return;

            try
            {
                uint hash;
                using (MD5 md5 = MD5.Create())
                {
                    byte[] bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(ContractsBootstrap.PLUGIN_GUID + "." + hashKey));
                    hash = BitConverter.ToUInt32(bytes, 0);
                }

                Type type = typeof(NetworkObject);
                PropertyInfo prop = type.GetProperty("GlobalObjectIdHash", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null && prop.CanWrite)
                {
                    prop.SetValue(networkObject, hash);
                    ContractsBootstrap.Log?.LogInfo($"[MoonContracts] Whistleblower NetworkObject hash set to {hash}.");
                    return;
                }

                FieldInfo field = type.GetField("GlobalObjectIdHash", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? type.GetField("<GlobalObjectIdHash>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
                if (field != null)
                {
                    field.SetValue(networkObject, hash);
                    ContractsBootstrap.Log?.LogInfo($"[MoonContracts] Whistleblower NetworkObject hash set to {hash}.");
                    return;
                }

                ContractsBootstrap.Log?.LogWarning("[MoonContracts] Could not locate NetworkObject.GlobalObjectIdHash; Whistleblower network prefab hash was not overridden.");
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogError($"[MoonContracts] Failed assigning Whistleblower NetworkObject hash: {e}");
            }
        }

        private static void SetTextureIfPresent(Material material, string property, Texture texture)
        {
            if (material != null && texture != null && material.HasProperty(property))
                material.SetTexture(property, texture);
        }

        private static void SetColorIfPresent(Material material, string property, Color color)
        {
            if (material != null && material.HasProperty(property))
                material.SetColor(property, color);
        }

        private static void SetFloatIfPresent(Material material, string property, float value)
        {
            if (material != null && material.HasProperty(property))
                material.SetFloat(property, value);
        }

        private static AudioSource EnsureAudioSource(
            GameObject prefab,
            string childName,
            float volume,
            float minDistance,
            float maxDistance,
            float localHeight)
        {
            Transform child = FindChildRecursive(prefab.transform, childName);
            GameObject audioObject;
            if (child == null)
            {
                audioObject = new GameObject(childName);
                audioObject.transform.SetParent(prefab.transform, false);
            }
            else
            {
                audioObject = child.gameObject;
            }

            // #984: set every time rather than only on creation, so re-registering against an
            // already-configured prefab cannot leave an emitter at a stale height.
            audioObject.transform.localPosition = new Vector3(0f, localHeight, 0f);

            var audio = audioObject.GetComponent<AudioSource>();
            if (audio == null)
                audio = audioObject.AddComponent<AudioSource>();
            audio.spatialBlend = 1f;
            audio.volume = volume;
            audio.minDistance = minDistance;
            audio.maxDistance = maxDistance;
            audio.playOnAwake = false;
            return audio;
        }

        // #765: the root capsule used to be a trigger, so players walked straight through him.
        // It is solid now, sized in world metres against the prefab scale (the capsule lives in
        // local units) and carries its own EnemyAICollisionDetect so a weapon sweep that lands
        // on this collider still resolves to the enemy. Vanilla's shovel de-duplicates hits per
        // mainScript, so the trigger child below cannot double-count a swing. Player blocking
        // follows layer 19 the same way Bundy's root capsule does (BundyBootstrap.EnsureEnemyCollision).
        internal static void EnsureBodyCollider(GameObject prefab, EnemyAI ai)
        {
            float scale = Mathf.Max(0.01f, prefab.transform.localScale.y);
            var collider = prefab.GetComponent<CapsuleCollider>();
            if (collider == null)
                collider = prefab.AddComponent<CapsuleCollider>();
            collider.direction = 1;
            collider.height = 2.4f / scale;
            collider.radius = 0.42f / scale;
            collider.center = new Vector3(0f, 1.2f / scale, 0f);
            collider.isTrigger = false;

            var detect = prefab.GetComponent<EnemyAICollisionDetect>();
            if (detect == null)
                detect = prefab.AddComponent<EnemyAICollisionDetect>();
            detect.mainScript = ai;
            detect.onlyCollideWhenGrounded = false;
            detect.canCollideWithEnemies = false;
        }

        private static void EnsureCollisionChild(GameObject prefab, EnemyAI ai)
        {
            Transform collision = prefab.transform.Find("Collision");
            if (collision == null)
            {
                var obj = new GameObject("Collision");
                collision = obj.transform;
                collision.SetParent(prefab.transform, false);
                collision.localPosition = new Vector3(0f, 1.31f, 0f);
            }

            SetTagSafe(collision.gameObject, "Enemy");
            collision.gameObject.layer = ResolveLayer("Enemies", 19);

            var collider = collision.GetComponent<CapsuleCollider>();
            if (collider == null)
                collider = collision.gameObject.AddComponent<CapsuleCollider>();
            collider.enabled = true;
            collider.isTrigger = true;
            collider.center = Vector3.zero;
            collider.radius = 0.58f;
            collider.height = 2.62f;

            var rb = collision.GetComponent<Rigidbody>();
            if (rb == null)
                rb = collision.gameObject.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;

            var detect = collision.GetComponent<EnemyAICollisionDetect>();
            if (detect == null)
                detect = collision.gameObject.AddComponent<EnemyAICollisionDetect>();
            detect.mainScript = ai;
            detect.onlyCollideWhenGrounded = false;
        }

        private static void DisableInteractionTrigger(GameObject prefab)
        {
            Transform trigger = prefab.transform.Find("InteractionTrigger");
            if (trigger == null)
                return;

            trigger.localPosition = new Vector3(0f, 1.31f, 0f);

            var collider = trigger.GetComponent<SphereCollider>();
            if (collider != null)
                collider.enabled = false;

            var interact = trigger.GetComponent<InteractTrigger>();
            if (interact != null)
            {
                interact.interactable = false;
                interact.hoverTip = string.Empty;
                interact.disabledHoverTip = string.Empty;
                interact.disableTriggerMesh = true;
            }
        }

        private static void EnsureScanNode(GameObject prefab)
        {
            Transform scan = prefab.transform.Find("WhistleblowerScanNode");
            if (scan == null)
            {
                var obj = new GameObject("WhistleblowerScanNode");
                scan = obj.transform;
                scan.SetParent(prefab.transform, false);
            }

            scan.localPosition = new Vector3(0f, 1.86f, 0f);
            scan.gameObject.layer = 22;

            var collider = scan.GetComponent<SphereCollider>();
            if (collider == null)
                collider = scan.gameObject.AddComponent<SphereCollider>();
            collider.radius = 0.76f;
            collider.isTrigger = true;

            var scanNode = scan.GetComponent<ScanNodeProperties>();
            if (scanNode == null)
                scanNode = scan.gameObject.AddComponent<ScanNodeProperties>();
            scanNode.headerText = "WHISTLEBLOWER";
            scanNode.subText = "locate";
            scanNode.maxRange = 32;
            scanNode.minRange = 1;
            scanNode.requiresLineOfSight = false;
            scanNode.nodeType = 0;
        }

        private static TerminalNode CreateTerminalNode()
        {
            var node = ScriptableObject.CreateInstance<TerminalNode>();
            node.displayText = "Whistleblower\n\nDanger level: 55%\n\nA Company witness marked for termination. The target is uncooperative and will defend himself. Sustained damage will drive him to break contact behind a stun grenade; he can only run so many times.\n\n";
            node.creatureName = EnemyName;
            node.maxCharactersToType = 35;
            node.clearPreviousText = true;
            return node;
        }

        private static TerminalKeyword CreateTerminalKeyword(TerminalNode node)
        {
            var keyword = ScriptableObject.CreateInstance<TerminalKeyword>();
            keyword.word = "whistleblower";
            keyword.isVerb = false;
            keyword.specialKeywordResult = node;
            return keyword;
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
            try
            {
                obj.tag = tag;
            }
            catch
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts] Tag '{tag}' was not available for {obj.name}.");
            }
        }
    }
}
