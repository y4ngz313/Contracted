using System;
using System.Collections.Generic;
using UnityEngine;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    [DisallowMultipleComponent]
    internal sealed class BlacksiteSoldierRigBinding : MonoBehaviour
    {
        internal const string VisualRootName = "BlacksiteVisualRoot";
        internal const string HeadAnchorName = "BlacksiteHeadAnchor";
        internal const string ChestAnchorName = "BlacksiteChestAnchor";
        internal const string PelvisAnchorName = "BlacksitePelvisAnchor";
        internal const string LeftHandAnchorName = "BlacksiteLeftHandAnchor";
        internal const string RightHandAnchorName = "BlacksiteRightHandAnchor";
        internal const string WeaponRootName = "BlacksiteWeaponRoot";
        internal const string MuzzleAnchorName = "BlacksiteMuzzleAnchor";
        internal const string CarryAnchorName = "BlacksiteHardDriveCarryAnchor";
        internal const string HitCapsuleName = "BlacksiteHitCapsule";
        internal const string VoiceSourceName = "BlacksiteVoiceAudioSource";
        internal const string WeaponSourceName = "BlacksiteWeaponAudioSource";
        internal const string FootstepSourceName = "BlacksiteFootstepAudioSource";
        internal const string HelmetLightName = "BlacksiteHelmetLight";
        internal const string MuzzleLightName = "BlacksiteMuzzleLight";
        internal const string MuzzleFlashName = "BlacksiteMuzzleFlashVisual";
        internal const string ImpactEffectName = "BlacksiteImpactEffect";
        internal const string MagazineName = "BlacksiteMagazineVisual";

        internal Transform VisualRoot { get; private set; }
        internal Animator Animator { get; private set; }
        internal Transform Head { get; private set; }
        internal Transform Chest { get; private set; }
        internal Transform Pelvis { get; private set; }
        internal Transform LeftHand { get; private set; }
        internal Transform RightHand { get; private set; }
        internal Transform WeaponRoot { get; private set; }
        internal Transform Muzzle { get; private set; }
        internal Transform CarryAnchor { get; private set; }
        internal CapsuleCollider HitCapsule { get; private set; }
        internal AudioSource VoiceSource { get; private set; }
        internal AudioSource WeaponSource { get; private set; }
        internal AudioSource FootstepSource { get; private set; }
        internal Light HelmetLight { get; private set; }
        internal Light MuzzleLight { get; private set; }
        internal Renderer MuzzleFlash { get; private set; }
        internal ParticleSystem ImpactEffect { get; private set; }
        internal Transform Magazine { get; private set; }
        internal bool IsValid { get; private set; }
        internal string Failure { get; private set; } = string.Empty;

        private readonly Dictionary<string, Transform> _hierarchyByName =
            new Dictionary<string, Transform>(StringComparer.Ordinal);
        private readonly HashSet<string> _duplicateNames =
            new HashSet<string>(StringComparer.Ordinal);

        internal static bool TryBind(GameObject root, out BlacksiteSoldierRigBinding binding, out string failure)
        {
            binding = null;
            failure = "soldier root is null";
            if (root == null)
                return false;

            binding = root.GetComponent<BlacksiteSoldierRigBinding>();
            if (binding == null)
                binding = root.AddComponent<BlacksiteSoldierRigBinding>();
            bool valid = binding.Resolve();
            failure = binding.Failure;
            return valid;
        }

        internal bool Resolve()
        {
            var failures = new List<string>();
            CollectHierarchy();
            VisualRoot = RequireUnique(VisualRootName, failures);
            Head = RequireUnique(HeadAnchorName, failures);
            Chest = RequireUnique(ChestAnchorName, failures);
            Pelvis = RequireUnique(PelvisAnchorName, failures);
            LeftHand = RequireUnique(LeftHandAnchorName, failures);
            RightHand = RequireUnique(RightHandAnchorName, failures);
            WeaponRoot = RequireUnique(WeaponRootName, failures);
            Muzzle = RequireUnique(MuzzleAnchorName, failures);
            CarryAnchor = RequireUnique(CarryAnchorName, failures);
            Transform hitObject = RequireUnique(HitCapsuleName, failures);
            Transform voiceObject = RequireUnique(VoiceSourceName, failures);
            Transform weaponAudioObject = RequireUnique(WeaponSourceName, failures);
            Transform footstepObject = RequireUnique(FootstepSourceName, failures);
            Transform helmetObject = RequireUnique(HelmetLightName, failures);
            Transform muzzleLightObject = RequireUnique(MuzzleLightName, failures);
            Transform muzzleFlashObject = RequireUnique(MuzzleFlashName, failures);
            Transform impactObject = RequireUnique(ImpactEffectName, failures);
            Magazine = RequireUnique(MagazineName, failures);

            Animator = VisualRoot != null ? VisualRoot.GetComponentInChildren<Animator>(true) : null;
            if (Animator == null)
                failures.Add("visual root has no Animator");
            else
            {
                if (Animator.avatar == null || !Animator.avatar.isValid || (!Animator.avatar.isHuman && Animator.avatar.name != "BlacksiteBatonAvatar"))
                    failures.Add("Animator avatar is missing or is not an approved soldier avatar");
                if (Animator.runtimeAnimatorController == null)
                    failures.Add("Animator controller is missing");
                if (Animator.applyRootMotion)
                    failures.Add("Animator root motion must be disabled");
            }

            HitCapsule = RequireComponent<CapsuleCollider>(hitObject, HitCapsuleName, failures);
            VoiceSource = RequireComponent<AudioSource>(voiceObject, VoiceSourceName, failures);
            WeaponSource = RequireComponent<AudioSource>(weaponAudioObject, WeaponSourceName, failures);
            FootstepSource = RequireComponent<AudioSource>(footstepObject, FootstepSourceName, failures);
            HelmetLight = RequireComponent<Light>(helmetObject, HelmetLightName, failures);
            MuzzleLight = RequireComponent<Light>(muzzleLightObject, MuzzleLightName, failures);
            MuzzleFlash = RequireComponent<Renderer>(muzzleFlashObject, MuzzleFlashName, failures);
            ImpactEffect = RequireComponent<ParticleSystem>(impactObject, ImpactEffectName, failures);

            if (VisualRoot != null && !HasUsableBounds(VisualRoot))
                failures.Add("visual render bounds are missing or collapsed");
            if (Muzzle != null && WeaponRoot != null && !Muzzle.IsChildOf(WeaponRoot))
                failures.Add("muzzle anchor is not parented below the weapon root");
            if (CarryAnchor != null && LeftHand != null && !CarryAnchor.IsChildOf(LeftHand))
                failures.Add("hard-drive carry anchor is not parented below the left hand");

            IsValid = failures.Count == 0;
            Failure = IsValid ? string.Empty : string.Join("; ", failures);
            _hierarchyByName.Clear();
            _duplicateNames.Clear();
            return IsValid;
        }

        private void CollectHierarchy()
        {
            _hierarchyByName.Clear();
            _duplicateNames.Clear();
            Transform[] transforms = GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform candidate = transforms[i];
                if (candidate == null)
                    continue;
                if (_hierarchyByName.ContainsKey(candidate.name))
                {
                    _duplicateNames.Add(candidate.name);
                    continue;
                }
                _hierarchyByName[candidate.name] = candidate;
            }
        }

        private Transform RequireUnique(string objectName, List<string> failures)
        {
            if (_duplicateNames.Contains(objectName))
            {
                failures.Add($"binding '{objectName}' appears more than once");
                return null;
            }

            if (!_hierarchyByName.TryGetValue(objectName, out Transform found) || found == null)
            {
                failures.Add($"missing binding '{objectName}'");
                return null;
            }

            return found;
        }

        private static T RequireComponent<T>(Transform target, string bindingName, List<string> failures)
            where T : Component
        {
            if (target == null)
                return null;
            T component = target.GetComponent<T>();
            if (component == null)
                failures.Add($"binding '{bindingName}' has no {typeof(T).Name}");
            return component;
        }

        private static bool HasUsableBounds(Transform root)
        {
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || renderer is ParticleSystemRenderer)
                    continue;
                Bounds bounds = renderer is SkinnedMeshRenderer skinned
                    ? skinned.localBounds
                    : renderer.bounds;
                if (bounds.size.sqrMagnitude > 0.0001f)
                    return true;
            }
            return false;
        }
    }
}
