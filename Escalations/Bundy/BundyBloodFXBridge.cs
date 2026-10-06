using System.Runtime.CompilerServices;
using System.Text;
using BepInEx.Bootstrap;
using GameNetcodeStuff;
using UnityEngine;
using Y4NGZCompany.Experience.BloodFX;

namespace Y4NGZCompany.Escalations.Bundy
{
    /// <summary>
    /// Live wound pose for a running finisher chute, in Bundy's own type system. Mirrors
    /// <c>BloodFXWoundPose</c> so the closure Bundy builds never names a Y4NGZBloodFX type;
    /// <see cref="BundyBloodFXBridge"/> adapts it at the boundary.
    /// </summary>
    internal delegate bool BundyWoundPose(out Vector3 origin, out Vector3 direction);

    /// <summary>Bundy-side mirror of BloodFX's decal-reception verdict.</summary>
    internal enum BundyDecalReception
    {
        Blocked = 0,
        OpaqueDecalBand = 1,
        Transparent = 2
    }

    /// <summary>
    /// Bundy-side mirror of <c>BloodFXBodyDecals.Profile</c>. Bundy's curated skeleton table is a
    /// static field initializer, so it is built the first time anything touches
    /// <see cref="BundyBloodSkeleton"/> - long before the availability check that guards the
    /// placement call. Holding Bundy's own value type there keeps that class loadable with the
    /// blood plugin absent.
    /// </summary>
    internal readonly struct BundyBloodProfile
    {
        internal readonly float Radius;
        internal readonly float MinWidth;
        internal readonly float MaxWidth;
        internal readonly float Depth;
        internal readonly float FrontMargin;
        internal readonly float HeavyScale;

        internal BundyBloodProfile(
            float radius,
            float minWidth,
            float maxWidth,
            float depth,
            float frontMargin,
            float heavyScale)
        {
            Radius = radius;
            MinWidth = minWidth;
            MaxWidth = maxWidth;
            Depth = depth;
            FrontMargin = frontMargin;
            HeavyScale = heavyScale;
        }
    }

    /// <summary>
    /// The single seam between Bundy and Y4NGZBloodFX (#393). BloodFX ships as its own plugin and
    /// is a soft dependency: a profile without it must still load Bundy, and every blood call site
    /// already has a fallback (the legacy runtime particle rigs) or is purely cosmetic.
    ///
    /// <b>How the guard works.</b> The CLR resolves the types a method names when that method is
    /// JIT-compiled, not when the assembly loads, so a missing Y4NGZBloodFX only faults the exact
    /// methods that touch its types. Every entry point here is therefore split in two: a public
    /// wrapper that names no BloodFX type and returns the absent-plugin answer, and a private
    /// <c>*Impl</c> marked <see cref="MethodImplOptions.NoInlining"/> that does the real work. The
    /// attribute is load-bearing - an inlined Impl would drag its type references back into the
    /// wrapper and fault the guard itself.
    ///
    /// Bundy code must never name a BloodFX type directly. Anything new goes through here.
    /// </summary>
    internal static class BundyBloodFXBridge
    {
        private const string BloodFXPluginGuid = "com.y4ngz.bloodfx";

        private static bool? _available;

        /// <summary>
        /// Whether the blood plugin is loaded in this profile. Resolved once, from the chainloader
        /// registry rather than from a type probe, so the answer itself costs no type resolution.
        /// Plugin.cs declares BloodFX a soft dependency, which guarantees it has chainloaded before
        /// anything Bundy runs can ask.
        /// </summary>
        internal static bool Available =>
            _available ??= Chainloader.PluginInfos.ContainsKey(BloodFXPluginGuid);

        // ------------------------------------------------------------------ settings

        /// <summary>
        /// The dedupe/rate-limit window BloodFX applies to a single wound. A compile-time constant
        /// in BloodFX, so reading it bakes the literal and takes no runtime dependency.
        /// </summary>
        internal const float DuplicateSuppressionSeconds = BloodFXDirector.DuplicateSuppressionSeconds;

        /// <summary>Damage dealt to Bundy may place replicated body decals.</summary>
        internal static bool DamageBloodEnabled => Available && DamageBloodEnabledImpl();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool DamageBloodEnabledImpl() =>
            BloodFXBootstrap.BloodFXEnabled && BloodFXBootstrap.BundyDamageBloodEnabled;

        /// <summary>Blood is on and its body decal prefabs are loaded. Forces the bundle load.</summary>
        internal static bool BodyDecalsReady => Available && BodyDecalsReadyImpl();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool BodyDecalsReadyImpl() =>
            BloodFXBootstrap.BloodFXEnabled && BloodFXAssets.IsAvailable && BloodFXAssets.HasBodyPrefabs;

        /// <summary>Body decal prefabs are loaded, ignoring the master switch. Probe paths only.</summary>
        internal static bool BodyPrefabsAvailable => Available && BodyPrefabsAvailableImpl();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool BodyPrefabsAvailableImpl() =>
            BloodFXAssets.IsAvailable && BloodFXAssets.HasBodyPrefabs;

        /// <summary>Most blood decals one creature body may carry. Zero when blood is absent.</summary>
        internal static int CreatureBodyDecalCap => Available ? CreatureBodyDecalCapImpl() : 0;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int CreatureBodyDecalCapImpl() => BloodFXBootstrap.CreatureBodyDecalCapValue;

        /// <summary>Whether a sustained finisher chute can run at all.</summary>
        internal static bool FinisherGoreReady => Available && FinisherGoreReadyImpl();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool FinisherGoreReadyImpl() => BloodFXDirector.FinisherGoreReady;

        // ---------------------------------------------------------------- diagnostics

        /// <summary>
        /// Whether blood's verbose trace channel is on. Callers gate expensive trace-string
        /// construction on this; false with blood absent, which skips the work entirely.
        /// </summary>
        internal static bool Verbose => Available && VerboseImpl();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool VerboseImpl() => BloodFXBootstrap.Verbose;

        /// <summary>Blood's config-gated trace channel. Silent when blood is absent.</summary>
        internal static void Trace(string message)
        {
            if (!Available)
                return;

            TraceImpl(message);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void TraceImpl(string message) => BloodFXBootstrap.Trace(message);

        /// <summary>Blood's short vector formatting, so trace lines read the same on both sides.</summary>
        internal static string Format(Vector3 value) =>
            Available ? FormatImpl(value) : value.ToString("0.###");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string FormatImpl(Vector3 value) => BloodFXDirector.Format(value);

        // -------------------------------------------------------------- decal registry

        /// <summary>Registry channel key for one creature's body decals.</summary>
        internal static long CreatureChannelFor(int ownerKey) =>
            Available ? CreatureChannelForImpl(ownerKey) : 0L;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static long CreatureChannelForImpl(int ownerKey) =>
            BloodFXDecalRegistry.ChannelFor(ownerKey, BodyChannel.Creature);

        /// <summary>Live decals in a channel.</summary>
        internal static int BodyCount(long channelKey) =>
            Available ? BodyCountImpl(channelKey) : 0;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int BodyCountImpl(long channelKey) => BloodFXDecalRegistry.BodyCount(channelKey);

        /// <summary>Destroy every decal in a channel. Teardown only.</summary>
        internal static void ClearBodyChannel(long channelKey)
        {
            if (!Available)
                return;

            ClearBodyChannelImpl(channelKey);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ClearBodyChannelImpl(long channelKey) =>
            BloodFXDecalRegistry.ClearBodyChannel(channelKey);

        // ------------------------------------------------------------- decal placement

        /// <summary>Place one body decal at an already-resolved contact point.</summary>
        internal static void PlaceBodyDecalAt(
            long channelKey,
            Transform anchor,
            Vector3 surface,
            Vector3 direction,
            in BundyBloodProfile profile,
            bool final,
            int cap,
            string label)
        {
            if (!Available)
                return;

            PlaceBodyDecalAtImpl(channelKey, anchor, surface, direction, in profile, final, cap, label);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PlaceBodyDecalAtImpl(
            long channelKey,
            Transform anchor,
            Vector3 surface,
            Vector3 direction,
            in BundyBloodProfile profile,
            bool final,
            int cap,
            string label)
        {
            var translated = new BloodFXBodyDecals.Profile(
                profile.Radius,
                profile.MinWidth,
                profile.MaxWidth,
                profile.Depth,
                profile.FrontMargin,
                profile.HeavyScale);

            BloodFXBodyDecals.PlaceAt(
                channelKey, anchor, surface, direction, in translated, final, cap, label);
        }

        /// <summary>#374 gate probe: force one decal onto a bone. Returns whether one landed.</summary>
        internal static bool PlaceGateProbeDecal(
            int ownerKey,
            Transform anchor,
            Vector3 outward,
            float size,
            float standoff,
            float depth,
            string label) =>
            Available && PlaceGateProbeDecalImpl(ownerKey, anchor, outward, size, standoff, depth, label);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool PlaceGateProbeDecalImpl(
            int ownerKey,
            Transform anchor,
            Vector3 outward,
            float size,
            float standoff,
            float depth,
            string label) =>
            BloodFXDirector.PlaceGateProbeDecal(ownerKey, anchor, outward, size, standoff, depth, label) != null;

        // -------------------------------------------------------------- material surfaces

        /// <summary>
        /// Make one per-instance material a decal receiver. Reports
        /// <see cref="BundyDecalReception.Blocked"/> with blood absent, which is what the caller
        /// already logs for a material that cannot take a decal.
        /// </summary>
        internal static BundyDecalReception MakeInstanceMaterialReceiveDecals(
            Material material,
            StringBuilder verbose) =>
            Available
                ? MakeInstanceMaterialReceiveDecalsImpl(material, verbose)
                : BundyDecalReception.Blocked;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static BundyDecalReception MakeInstanceMaterialReceiveDecalsImpl(
            Material material,
            StringBuilder verbose)
        {
            switch (BloodFXDecalSurfaces.MakeInstanceMaterialReceiveDecals(material, verbose))
            {
                case BloodFXDecalSurfaces.DecalReception.OpaqueDecalBand:
                    return BundyDecalReception.OpaqueDecalBand;
                case BloodFXDecalSurfaces.DecalReception.Transparent:
                    return BundyDecalReception.Transparent;
                default:
                    return BundyDecalReception.Blocked;
            }
        }

        // ---------------------------------------------------------------- finisher chute

        /// <summary>
        /// Open the sustained wound chute (#322). False when blood is absent or unready, which is
        /// the caller's cue to run its legacy particle rig instead.
        /// </summary>
        internal static bool BeginFinisherGore(
            int key,
            Transform sourceRoot,
            BundyWoundPose pose,
            PlayerControllerB victim,
            Transform bodyAnchor,
            int bodyIndex,
            float scale,
            string label) =>
            Available &&
            BeginFinisherGoreImpl(key, sourceRoot, pose, victim, bodyAnchor, bodyIndex, scale, label);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool BeginFinisherGoreImpl(
            int key,
            Transform sourceRoot,
            BundyWoundPose pose,
            PlayerControllerB victim,
            Transform bodyAnchor,
            int bodyIndex,
            float scale,
            string label)
        {
            var targets = new BloodFXFinisherTargets(victim, bodyAnchor, bodyIndex);
            return BloodFXDirector.BeginFinisherGore(
                key,
                sourceRoot,
                new BloodFXWoundPose(pose.Invoke),
                in targets,
                scale,
                label);
        }

        /// <summary>Close the chute owned by <paramref name="key"/>. Safe when none is open.</summary>
        internal static void EndFinisherGore(int key, string reason)
        {
            if (!Available)
                return;

            EndFinisherGoreImpl(key, reason);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void EndFinisherGoreImpl(int key, string reason) =>
            BloodFXDirector.EndFinisherGore(key, reason);
    }
}
