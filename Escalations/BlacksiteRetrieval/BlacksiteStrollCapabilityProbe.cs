using System.Collections.Generic;
using UnityEngine;

using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #1219 E4: what the bound rifle controller's StandingPatrol tree can carry the patrol stroll on.
    /// The runtime cannot read a blend tree's children (that API is editor-only), so the tree is
    /// played: a hidden Animator bound to the same controller is put in StandingPatrol at each probe
    /// point and <see cref="Animator.GetCurrentAnimatorClipInfo(int, List{AnimatorClipInfo})"/> reports the
    /// children that state blends there and their weights. A clip counts only as a weight inside that
    /// state, never because the controller merely references it. The selection itself is
    /// <see cref="BlacksiteLocomotionSpeedTable.SelectStroll"/>; the result is cached per controller
    /// asset and reported in one bind line each. The live soldier's animator is never touched, and
    /// every peer derives the same answer from the same asset, so nothing is replicated.
    /// </summary>
    internal sealed class BlacksiteStrollCapabilityProbe : IBlacksiteBlendSampler
    {
        private static readonly int StandingPatrolHash = Animator.StringToHash("StandingPatrol");
        private static readonly int MoveXHash = Animator.StringToHash("MoveX");
        private static readonly int MoveZHash = Animator.StringToHash("MoveZ");
        private static readonly int SpeedHash = Animator.StringToHash("Speed");
        private static readonly Dictionary<RuntimeAnimatorController, BlacksiteStrollCapability> Probed =
            new Dictionary<RuntimeAnimatorController, BlacksiteStrollCapability>();

        private readonly Animator _animator;
        private readonly bool _hasSpeed;
        private readonly List<AnimatorClipInfo> _clips = new List<AnimatorClipInfo>(16);

        private BlacksiteStrollCapabilityProbe(Animator animator, bool hasSpeed)
        {
            _animator = animator;
            _hasSpeed = hasSpeed;
        }

        /// <summary>The capability of <paramref name="controller"/>, probed on first use and cached.</summary>
        internal static BlacksiteStrollCapability Resolve(RuntimeAnimatorController controller)
        {
            if (controller == null)
                return BlacksiteStrollCapability.None;
            if (Probed.TryGetValue(controller, out BlacksiteStrollCapability known))
                return known;
            BlacksiteStrollCapability capability = Probe(controller);
            Probed[controller] = capability;
            Report(controller, capability);
            return capability;
        }

        private static BlacksiteStrollCapability Probe(RuntimeAnimatorController controller)
        {
            var host = new GameObject("BlacksiteStrollCapabilityProbe") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                var animator = host.AddComponent<Animator>();
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.applyRootMotion = false;
                animator.runtimeAnimatorController = controller;
                bool hasMoveX = false, hasMoveZ = false, hasSpeed = false;
                foreach (AnimatorControllerParameter parameter in animator.parameters)
                {
                    if (parameter.type != AnimatorControllerParameterType.Float) continue;
                    hasMoveX |= parameter.nameHash == MoveXHash;
                    hasMoveZ |= parameter.nameHash == MoveZHash;
                    hasSpeed |= parameter.nameHash == SpeedHash;
                }
                if (!hasMoveX || !hasMoveZ || !animator.HasState(0, StandingPatrolHash))
                    return BlacksiteStrollCapability.None;
                return BlacksiteLocomotionSpeedTable.SelectStroll(new BlacksiteStrollCapabilityProbe(animator, hasSpeed));
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        public void Sample(float moveX, float moveZ)
        {
            _animator.SetFloat(MoveXHash, moveX);
            _animator.SetFloat(MoveZHash, moveZ);
            if (_hasSpeed)
                _animator.SetFloat(SpeedHash, Mathf.Sqrt(moveX * moveX + moveZ * moveZ));
            _animator.Play(StandingPatrolHash, 0, 0f);
            _animator.Update(0f);
            _clips.Clear();
            // A transition the parameters triggered out of the state would report another state's
            // clips; only StandingPatrol's own blend counts.
            if (_animator.GetCurrentAnimatorStateInfo(0).shortNameHash == StandingPatrolHash)
                _animator.GetCurrentAnimatorClipInfo(0, _clips);
        }

        public float Weight(string clip)
        {
            float weight = 0f;
            for (int i = 0; i < _clips.Count; i++)
            {
                AnimationClip played = _clips[i].clip;
                if (played != null && played.name == clip)
                    weight += _clips[i].weight;
            }
            return weight;
        }

        private static void Report(RuntimeAnimatorController controller, in BlacksiteStrollCapability capability)
        {
            if (!capability.IsValid)
            {
                ContractsBootstrap.Log?.LogWarning(
                    $"[MoonContracts.Blacksite] Soldier controller '{controller.name}' stroll=none: its StandingPatrol tree carries no"
                    + $" stroll row of BlacksiteLocomotionSpeedTable.StrollProfiles ({ProfileNames()}); the patrol stroll"
                    + " blends as authored and may mix in the run.");
                return;
            }
            BlacksiteStrollProfile profile = BlacksiteLocomotionSpeedTable.StrollProfiles[capability.Profile];
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Blacksite] Soldier controller '{controller.name}' stroll={profile.Name}:"
                + $" walk={profile.OtherWalkClip}@({profile.OtherWalkX:0.00}, {profile.OtherWalkZ:0.00})"
                + $"+{profile.WalkClip}@({profile.WalkX:0.00}, {profile.WalkZ:0.00}), run={profile.RunClip}@(0.00, {BlacksiteLocomotionSpeedTable.RunBlendZ:0.00}),"
                + $" straightAhead=(0.000, {BlacksiteLocomotionSpeedTable.StrollBlendRadius:0.000}),"
                + $" minimumWalkWeight={capability.WalkWeight:0.000}, maximumRunWeight={capability.RunWeight:0.000} (limit {BlacksiteLocomotionSpeedTable.MaxStrollRunWeight:0.00}).");
        }

        private static string ProfileNames()
        {
            var names = new string[BlacksiteLocomotionSpeedTable.StrollProfiles.Length];
            for (int i = 0; i < names.Length; i++)
                names[i] = BlacksiteLocomotionSpeedTable.StrollProfiles[i].OtherWalkClip + "+"
                    + BlacksiteLocomotionSpeedTable.StrollProfiles[i].WalkClip + "+" + BlacksiteLocomotionSpeedTable.StrollProfiles[i].RunClip;
            return string.Join(", ", names);
        }
    }
}
