using System;
using System.IO;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Contracts.Whistleblower
{
    // A scoped asset update avoids rebuilding unrelated contracts from mutable
    // authoring sources. Accept all four roots together or retain the old visuals.
    internal static class WhistleblowerVisualAssets
    {
        internal const string FileName = "y4ngz-whistleblower.bundle";
        private static bool _attempted;
        private static AssetBundle _bundle;
        internal static GameObject VanillaPrefab { get; private set; }
        internal static AnimationClip CombatIdle { get; private set; }
        internal static AnimationClip PunchLeft { get; private set; }
        internal static AnimationClip PunchRight { get; private set; }

        internal static void Load()
        {
            if (_attempted) return;
            _attempted = true;
            string path = Path.Combine(Path.GetDirectoryName(typeof(WhistleblowerVisualAssets).Assembly.Location), FileName);
            if (!File.Exists(path)) return;
            AssetBundle candidate = null;
            try
            {
                candidate = AssetBundle.LoadFromFile(path);
                if (candidate == null) throw new InvalidDataException("Bundle could not be opened.");
                var prefab = candidate.LoadAsset<GameObject>("WhistleblowerRuntimeVisualVanilla");
                var idle = candidate.LoadAsset<AnimationClip>("Whistleblower_CombatIdle");
                var left = candidate.LoadAsset<AnimationClip>("Whistleblower_PunchLeft");
                var right = candidate.LoadAsset<AnimationClip>("Whistleblower_PunchRight");
                var animator = prefab != null ? prefab.GetComponentInChildren<Animator>(true) : null;
                if (animator == null || animator.avatar == null || !animator.avatar.isHuman || !animator.avatar.isValid
                    || idle == null || left == null || right == null
                    || !idle.humanMotion || !left.humanMotion || !right.humanMotion
                    || left.length < 0.1f || right.length < 0.1f)
                    throw new InvalidDataException("Corrected prefab or humanoid hand clips are incomplete.");
                _bundle = candidate; // Keep dependencies alive for all spawned witnesses.
                VanillaPrefab = prefab;
                CombatIdle = idle;
                PunchLeft = left;
                PunchRight = right;
                ContractsBootstrap.Log?.LogInfo("[MoonContracts] Loaded corrected Whistleblower rig and hand poses from " + FileName + ".");
            }
            catch (Exception exception)
            {
                candidate?.Unload(true);
                ContractsBootstrap.Log?.LogWarning("[MoonContracts] Whistleblower visual sidecar unavailable; using shared bundle visuals. " + exception.Message);
            }
        }
    }
}
