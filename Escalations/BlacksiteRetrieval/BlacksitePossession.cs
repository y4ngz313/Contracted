using System.Collections.Generic;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #1076: how far the Blacksite squad has got with one kind of contract object, as one word a
    /// contract card can print.
    ///
    /// This whole file is free of UnityEngine and Unity.Netcode on purpose, for the same reason
    /// <see cref="BlacksiteActivityStatus"/> is: the ladder and the wording are the parts worth
    /// pinning, so they are a pure core the headless
    /// <c>tests/ContractHudPresentationRegression</c> compiles directly.
    /// <see cref="BlacksitePossessionReader"/> is the only adapter - it projects the replicated
    /// <c>BlacksiteRetrievalMissionController.ObjectiveSnapshots</c> list into the records below
    /// and adds NO new RPC, because that list is already on every peer.
    /// </summary>
    internal enum BlacksitePossession : byte
    {
        /// <summary>Nobody from the squad has it, or there is no mission.</summary>
        None = 0,
        /// <summary>A member has been assigned to it but is not holding it yet.</summary>
        Claimed = 1,
        /// <summary>A member is holding it - inside the facility or out on the surface.</summary>
        Held = 2,
        /// <summary>Put down at their guarded rendezvous, waiting for the winch.</summary>
        Staged = 3,
        /// <summary>Aboard their ship.</summary>
        Aboard = 4,
        /// <summary>
        /// Their ship left the moon with it.
        ///
        /// NOT a usable departure signal, and no contract card may treat it as one. The manifest
        /// writes the Extracted phase and despawns the object on the next line, inside the same
        /// synchronous chain that despawns the ship the mission lives on, so that write is never
        /// flushed to a client and the host's own mission is gone before the next HUD poll. It is
        /// here because the phase exists and the ladder has to map it; departure is latched from
        /// the progression channel instead (#1076).
        /// </summary>
        Gone = 5
    }

    /// <summary>
    /// #1076: the replicated bytes this file interprets, named.
    ///
    /// Mirrored rather than referenced for the reason <see cref="BlacksiteActivityWire"/> gives:
    /// <see cref="BlacksiteObjectiveKind"/> and <see cref="BlacksiteObjectivePhase"/> live in
    /// <c>BlacksiteObjectiveManifest.cs</c> beside a Vector3-carrying INetworkSerializable struct,
    /// which a headless fixture cannot compile. Both enums are wire format and therefore
    /// appended-only, so the numbers here cannot drift; the adapter casts the real enum rather
    /// than re-deriving it, so a reorder would break the wire long before it broke this file.
    /// </summary>
    internal static class BlacksitePossessionWire
    {
        // BlacksiteObjectiveKind
        internal const int KindShadowDrive = 0;
        internal const int KindSurvey = 1;
        internal const int KindApparatus = 2;
        internal const int KindPayload = 3;
        internal const int KindLoot = 4;
        internal const int KindSalvage = 5;
        internal const int KindWitness = 6;

        // BlacksiteObjectivePhase
        internal const int PhaseAvailable = 0;
        internal const int PhaseReserved = 1;
        internal const int PhaseCarried = 2;
        internal const int PhaseLoaded = 3;
        internal const int PhaseExtracted = 4;
        internal const int PhaseUnavailable = 5;
        internal const int PhaseStaged = 6;
    }

    /// <summary>
    /// #1076: one manifest record reduced to the three facts a contract card reads. The list it
    /// comes from is a NetworkList of a Unity struct, so the reduction happens in the adapter and
    /// only these primitives cross into the pure core.
    /// </summary>
    internal readonly struct BlacksitePossessionRecord
    {
        internal BlacksitePossessionRecord(int kind, int phase, ulong carrierId)
        {
            Kind = kind;
            Phase = phase;
            CarrierId = carrierId;
        }

        internal int Kind { get; }
        internal int Phase { get; }
        internal ulong CarrierId { get; }
    }

    /// <summary>#1076: the answer for one objective kind - how far they have got, and who has it.</summary>
    internal readonly struct BlacksitePossessionReading
    {
        internal BlacksitePossessionReading(BlacksitePossession possession, ulong carrierId)
        {
            Possession = possession;
            CarrierId = carrierId;
        }

        internal BlacksitePossession Possession { get; }

        /// <summary>The holding member's network object id while <see cref="IsHeld"/>, else 0.</summary>
        internal ulong CarrierId { get; }

        /// <summary>
        /// The squad physically has it and the crew can still take it back. Staged counts: the
        /// object is on the ground at their rendezvous under guard, which is a recovery the crew
        /// can still make, and Aboard counts for the same reason right up to departure.
        /// </summary>
        internal bool IsHeld => Possession == BlacksitePossession.Held
            || Possession == BlacksitePossession.Staged
            || Possession == BlacksitePossession.Aboard;

        /// <summary>It is on their ship. The last moment the crew can act on it.</summary>
        internal bool IsAboard => Possession == BlacksitePossession.Aboard;

        /// <summary>It left the moon with them.</summary>
        internal bool IsGone => Possession == BlacksitePossession.Gone;
    }

    /// <summary>
    /// #1076: the precedence ladder over one kind's records. Highest phase reached wins, because
    /// the manifest can hold more than one record of a kind (Survey plants several beacons) and
    /// the card speaks about the kind, not about a particular object. Pure: no clock, no state,
    /// no Unity.
    /// </summary>
    internal static class BlacksitePossessionPolicy
    {
        internal static BlacksitePossessionReading Resolve(IEnumerable<BlacksitePossessionRecord> records, int kind)
        {
            BlacksitePossession best = BlacksitePossession.None;
            ulong carrier = 0UL;
            if (records == null)
                return new BlacksitePossessionReading(best, carrier);

            foreach (BlacksitePossessionRecord record in records)
            {
                if (record.Kind != kind)
                    continue;

                BlacksitePossession possession = FromPhase(record.Phase);
                if (possession <= best)
                    continue;

                best = possession;
                // Only a Carried record names a live holder; every other phase has already
                // released its carrier back to NoCarrier, so the card would print a stale member.
                carrier = possession == BlacksitePossession.Held ? record.CarrierId : 0UL;
            }

            return new BlacksitePossessionReading(best, carrier);
        }

        /// <summary>
        /// The phase-to-possession map. Available and Unavailable are both "not theirs": the first
        /// is an objective nobody has claimed, the second one the board has given up on, and
        /// neither is something the crew has to be told about.
        /// </summary>
        internal static BlacksitePossession FromPhase(int phase)
        {
            switch (phase)
            {
                case BlacksitePossessionWire.PhaseReserved: return BlacksitePossession.Claimed;
                case BlacksitePossessionWire.PhaseCarried: return BlacksitePossession.Held;
                case BlacksitePossessionWire.PhaseStaged: return BlacksitePossession.Staged;
                case BlacksitePossessionWire.PhaseLoaded: return BlacksitePossession.Aboard;
                case BlacksitePossessionWire.PhaseExtracted: return BlacksitePossession.Gone;
                default: return BlacksitePossession.None;
            }
        }
    }
}
