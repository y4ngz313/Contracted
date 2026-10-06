// BundyEnvironmentalNoiseAdapter.cs — #611 task 1.3.
//
// Bundy's side of the Y4NGZCore world-noise bus.
//
// Ship Systems owns the battery, and the battery's repair and restore are two of the most
// informative noises on Bundy's bus — but Bundy is an escalation and the battery is ship
// systems, so until now ShipSystemsController reached across with
// `using Y4NGZCompany.Escalations.Bundy.AI` and called BundyWorldNoisePatchSupport.Report with
// a BundyWorldNoise enum value. That is a compile-time dependency in the forbidden direction,
// and worse, an enum passed by ordinal: reordering BundyWorldNoise would silently reassign
// every noise the other assembly emits.
//
// Publishers now call Y4NGZCore.Modules.Environment.EnvironmentalNoise.Report with a stable
// string kind and no knowledge of who listens. This adapter is the listener.

using GameNetcodeStuff;
using UnityEngine;
using Y4NGZCompany.Escalations.Bundy.AI;
using Y4NGZCompany.Escalations._Shared;
using Y4NGZCore.Modules;
using Y4NGZCore.Modules.Environment;

namespace Y4NGZCompany.Escalations.Bundy
{
    /// <summary>
    /// Receives <see cref="EnvironmentalNoiseEvent"/>s and hands them to the active Bundy.
    /// </summary>
    internal sealed class BundyEnvironmentalNoiseAdapter : IEnvironmentalNoiseService
    {
        private static BundyEnvironmentalNoiseAdapter _registered;

        /// <summary>
        /// Publishes this listener under
        /// <see cref="ModuleCapabilities.EnvironmentalNoise"/>. Idempotent.
        /// </summary>
        internal static void EnsureRegistered()
        {
            if (_registered != null)
                return;

            _registered = new BundyEnvironmentalNoiseAdapter();
            ModuleCapabilityRegistry.Register(
                ModuleCapabilities.EnvironmentalNoise, _registered);
        }

        public bool Report(EnvironmentalNoiseEvent noise)
        {
            if (!noise.IsValid)
                return false;

            if (!MoonEscalationState.TryGetActiveStrangerBundy(out BundyAIBase bundy) || bundy == null)
                return false;

            BundyWorldNoise meaning = Bucket(noise.NoiseKind);
            bundy.OnWorldNoiseEvent(
                new Vector3(noise.Position.X, noise.Position.Y, noise.Position.Z),
                meaning,
                ResolvePlayer(noise.CausedByClientId));
            return true;
        }

        /// <summary>
        /// Maps a stable kind ID onto one of the twelve meanings the AI knows.
        ///
        /// <para><b>Unknown kinds are bucketed, never passed through.</b>
        /// <c>BundyAIBase</c>'s per-event rate-limit table is sized to exactly
        /// <see cref="BundyWorldNoiseCatalog.MeaningCount"/> entries and indexed by the enum
        /// ordinal, so an out-of-range value is either an index crash or — if it happened to
        /// land in range — an event stealing another meaning's throttle slot. The contract says
        /// an unrecognized kind is a low-confidence generic noise, and
        /// <see cref="BundyWorldNoise.ScanPing"/> is exactly that in this catalogue: a bounded
        /// 35 m radius and 0.5 confidence, "a short pulse, easy to miss". Sharing its throttle
        /// slot is deliberate — it means a publisher emitting an unknown kind at high frequency
        /// cannot out-report the events the AI actually reasons about.</para>
        /// </summary>
        internal static BundyWorldNoise Bucket(string noiseKind)
        {
            switch (noiseKind)
            {
                case EnvironmentalNoiseKinds.ShipTeleporter: return BundyWorldNoise.ShipTeleporter;
                case EnvironmentalNoiseKinds.BreakerFlip: return BundyWorldNoise.BreakerFlip;
                case EnvironmentalNoiseKinds.BigDoorOpen: return BundyWorldNoise.BigDoorOpen;
                case EnvironmentalNoiseKinds.ShipHorn: return BundyWorldNoise.ShipHorn;
                case EnvironmentalNoiseKinds.TurretFire: return BundyWorldNoise.TurretFire;
                case EnvironmentalNoiseKinds.TerminalUse: return BundyWorldNoise.TerminalUse;
                case EnvironmentalNoiseKinds.ScanPing: return BundyWorldNoise.ScanPing;
                case EnvironmentalNoiseKinds.WalkieTransmit: return BundyWorldNoise.WalkieTransmit;
                case EnvironmentalNoiseKinds.CctvStationUse: return BundyWorldNoise.CctvStationUse;
                case EnvironmentalNoiseKinds.ShipTurretFire: return BundyWorldNoise.ShipTurretFire;
                case EnvironmentalNoiseKinds.BatteryRestore: return BundyWorldNoise.BatteryRestore;
                case EnvironmentalNoiseKinds.BatteryRepair: return BundyWorldNoise.BatteryRepair;
                default: return BundyWorldNoise.ScanPing;
            }
        }

        /// <summary>
        /// Resolves an NGO client ID back to a player.
        ///
        /// <para><b>Searched on <c>actualClientId</c>, never indexed by it.</b> The contract
        /// spells out why: <c>playerClientId</c> is a 0..3 slot index and
        /// <c>actualClientId</c> is the transport ID, and they coincide only in single player.
        /// Indexing <c>allPlayerScripts</c> by a transport ID attributes a client's noise to the
        /// wrong crew member — or throws — the moment a second player joins.</para>
        /// </summary>
        private static PlayerControllerB ResolvePlayer(ulong clientId)
        {
            if (clientId == EnvironmentalNoiseEvent.UnknownPlayerClientId)
                return null;

            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null)
                return null;

            for (int i = 0; i < players.Length; i++)
            {
                if (players[i] != null && players[i].actualClientId == clientId)
                    return players[i];
            }

            return null;
        }
    }
}
