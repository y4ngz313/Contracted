using UnityEngine;

namespace Y4NGZCompany.Escalations.Bundy.HitReactions
{
    /// <summary>
    /// Issue #288 tier 1. The ADDITIVE half of Bundy's hit reactions: every registered hit nudges
    /// his spine/head for a fraction of a second, on every client, without touching the animator
    /// state machine, the timed-action system, the agent, or the chain-attack sequence. It is the
    /// only reaction that a hit is guaranteed to produce — the stun (tier 2) and knockdown (tier 3)
    /// staggers are earned off the stagger bar in BundyAIBase.Animation.cs.
    ///
    /// Mechanism: the vendored Procedural Hit Reactions System (see the header notes in
    /// BodyPartReactor.cs / HitReactionController.cs). BodyPartReactor.LateUpdate multiplies a
    /// decaying offset onto the bone's local rotation AFTER the Animator has written the authored
    /// pose, so whatever clip is playing keeps playing — a flinch cannot interrupt a swing.
    ///
    /// Everything is built at runtime: the ReactionProfile is a ScriptableObject instance created
    /// here (no bundle change), and the reactor GameObjects are children of this component's own
    /// host object, named for the bone they drive so HitReactionController.React(partName, ...)
    /// still resolves them the upstream way.
    ///
    /// Suppression is "stop feeding it", never "snap it off": SetSuppressed only gates NEW hits, so
    /// an in-flight offset finishes blending out through the profile's blendOutSpeed.
    /// </summary>
    internal sealed class BundyProceduralFlinch : MonoBehaviour
    {
        private const string HostObjectName = "BundyProceduralFlinch";
        private const string SpineBoneName = "spine_02";
        private const string ChestBoneName = "spine_03";
        private const string HeadBoneName = "head";

        private HitReactionController _controller;
        private BodyPartReactor _spineReactor;
        private BodyPartReactor _chestReactor;
        private BodyPartReactor _headReactor;
        private ReactionProfile _spineProfile;
        private ReactionProfile _headProfile;
        private bool _built;

        internal bool IsBuilt => _built;

        /// <summary>
        /// Idempotent: creates (or returns) the flinch host parented under <paramref name="root"/>.
        /// Returns null when the rig has no resolvable spine/head bones — the caller treats a
        /// missing flinch as "tier 1 unavailable", never as an error worth spamming.
        /// </summary>
        internal static BundyProceduralFlinch EnsureOn(Transform root, Animator animator)
        {
            if (root == null || animator == null)
                return null;

            BundyProceduralFlinch existing = root.GetComponentInChildren<BundyProceduralFlinch>(true);
            if (existing != null)
                return existing.IsBuilt ? existing : null;

            Transform host = new GameObject(HostObjectName).transform;
            host.SetParent(root, false);
            BundyProceduralFlinch flinch = host.gameObject.AddComponent<BundyProceduralFlinch>();
            if (!flinch.Build(animator))
            {
                // Leave the (inert) component in place so EnsureOn never rebuilds every frame on a
                // rig that cannot support the flinch.
                return null;
            }

            return flinch;
        }

        private bool Build(Animator animator)
        {
            // Same resolution the rest of the rig code uses (BundyChainsawHandIK.ResolveSpineBones /
            // ResolveHeadBones): the canonical armature first, the animator root as the fallback.
            Transform armature = FindChildRecursive(animator.transform, "Armature");
            Transform searchRoot = armature != null ? armature : animator.transform;

            Transform spine = FindChildRecursive(searchRoot, SpineBoneName);
            Transform chest = FindChildRecursive(searchRoot, ChestBoneName);
            Transform head = FindChildRecursive(searchRoot, HeadBoneName);
            if (spine == null && chest == null && head == null)
                return false;

            _controller = gameObject.AddComponent<HitReactionController>();
            _controller.animator = animator;

            // The upper body carries the weight of the flinch; the head snaps harder and shorter.
            // 2026-08-05 playtest: the first-pass values (spine 5-7deg/0.20s, head 9-11deg/0.16s)
            // read as no reaction at all in game — on a rig this large the flinch has to be
            // overtly theatrical before it registers from a firefight distance.
            _spineProfile = BuildProfile(new Vector3(14f, 18f, 9f), 0.32f);
            _headProfile = BuildProfile(new Vector3(20f, 24f, 12f), 0.24f);

            _spineReactor = BuildReactor(SpineBoneName, spine, _spineProfile, 1f);
            _chestReactor = BuildReactor(ChestBoneName, chest, _spineProfile, 0.75f);
            _headReactor = BuildReactor(HeadBoneName, head, _headProfile, 1f);

            _built = _spineReactor != null || _chestReactor != null || _headReactor != null;
            return _built;
        }

        private static ReactionProfile BuildProfile(Vector3 rotationStrengthDegrees, float duration)
        {
            ReactionProfile profile = ScriptableObject.CreateInstance<ReactionProfile>();
            profile.hideFlags = HideFlags.HideAndDontSave;
            profile.reactionDuration = duration;
            profile.blendInSpeed = 16f;
            profile.blendOutSpeed = 5f;
            profile.blendCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

            profile.useRotation = true;
            profile.rotationStrength = rotationStrengthDegrees;
            // RandomOnly, deliberately. The direction-based modes build their target from
            // Quaternion.LookRotation(effDir) * origRot, which on this generic (non-humanoid) rig
            // produces an unbounded reorientation of the bone rather than a lean away from the shot.
            // Random keeps the offset bounded by rotationStrength, which is the tunable the owner
            // actually wants. Flip to RandomAndDirection once the directional look is playtested.
            profile.rotationMode = ReactionMode.RandomOnly;
            profile.directionRotationMultiplier = 0f;

            // Translating a spine bone slides the mesh off the skeleton; rotation only.
            profile.useTranslation = false;

            profile.additive = true;                 // layer on top of the authored pose, never replace it
            profile.offsetsFromCurrentPose = false;  // offsets are relative to the bind pose sampled in Start
            profile.blendResetMode = ReactionProfile.BlendResetMode.HardReset;
            // Sustained fire must keep punching him, so every hit restarts the timer instead of
            // being swallowed while a previous flinch is still live.
            profile.hitAcceptance = ReactionProfile.HitAcceptance.RestartTimer;
            profile.rotationAxisMask = Vector3.one;
            profile.translationAxisMask = Vector3.one;
            return profile;
        }

        private BodyPartReactor BuildReactor(string boneName, Transform bone, ReactionProfile profile, float intensity)
        {
            if (bone == null)
                return null;

            // Named for the bone so HitReactionController.React(partName, ...) resolves it.
            GameObject holder = new GameObject(boneName);
            holder.transform.SetParent(transform, false);
            BodyPartReactor reactor = holder.AddComponent<BodyPartReactor>();
            reactor.bone = bone;
            reactor.profile = profile;
            reactor.intensityMultiplier = intensity;
            reactor.durationMultiplier = 1f;
            // Before the first React(): the reactor is built and hit on the same frame, so Start()
            // has not run and origRot would still be the default all-zero quaternion (see change 6
            // in BodyPartReactor.cs).
            reactor.CaptureBaseline();
            _controller.reactors.Add(reactor);
            return reactor;
        }

        /// <summary>
        /// Plays one flinch. <paramref name="hitDirection"/> is the world-space incoming direction
        /// (source -> Bundy); it is unused while the profiles run in RandomOnly, but is still routed
        /// through so flipping the profile to a directional mode needs no call-site change.
        /// </summary>
        internal void Flinch(int force, Vector3 hitDirection, float intensityScale)
        {
            // Suppression lives at the call site (BundyAIBase.PlayProceduralFlinch), which simply
            // stops calling this. That IS the blend-out: BodyPartReactor decays its own weight when
            // no new hit arrives, so nothing ever snaps to zero mid-pose.
            if (!_built)
                return;

            // 0.4 at force 1 (shovel) up to 1.0 at force 5 (point-blank shotgun); the clamp keeps a
            // freak force (the ship turret's 10) from doubling the offset instead of capping it.
            float forceScale = Mathf.Clamp(0.4f + 0.6f * (Mathf.Max(0, force) / 5f), 0.2f, 1f);
            float scale = forceScale * Mathf.Clamp(intensityScale, 0f, 3f);
            if (scale <= 0f)
                return;

            Vector3 direction = hitDirection.sqrMagnitude > 1e-6f ? hitDirection.normalized : -transform.forward;
            Vector3 point = transform.position;

            ApplyReactor(_spineReactor, 1f * scale, direction, point);
            ApplyReactor(_chestReactor, 0.75f * scale, direction, point);
            ApplyReactor(_headReactor, 1f * scale, direction, point);
        }

        private static void ApplyReactor(BodyPartReactor reactor, float intensity, Vector3 direction, Vector3 point)
        {
            if (reactor == null)
                return;

            // The reactor clamps its own multiplier to [0, 3] through the inspector Range attribute
            // only; do it here so a config-scaled value can never leave the authored band.
            reactor.intensityMultiplier = Mathf.Clamp(intensity, 0f, 3f);
            reactor.React(direction, point);
        }

        private static Transform FindChildRecursive(Transform root, string name)
        {
            if (root == null)
                return null;

            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (string.Equals(child.name, name, System.StringComparison.OrdinalIgnoreCase))
                    return child;

                Transform found = FindChildRecursive(child, name);
                if (found != null)
                    return found;
            }

            return null;
        }

        private void OnDestroy()
        {
            if (_spineProfile != null)
                Destroy(_spineProfile);
            if (_headProfile != null)
                Destroy(_headProfile);
        }
    }
}
