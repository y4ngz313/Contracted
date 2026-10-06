using System;
using System.Collections.Generic;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #874 Q4 (2026-09-24): the one extraction sequence. Each step is a fact about the ship and
    /// the cargo, not a clock, and exactly one step is current. Replicated by the ship as one byte,
    /// so a late joiner reads the same step the host is in; the hull is spawned per round, so the
    /// next round starts at <see cref="Raid"/>.
    /// </summary>
    internal enum BlacksiteExtractionStep : byte
    {
        /// <summary>No pickup requested: the raid is running.</summary>
        Raid,
        /// <summary>The recall is committed: the primary is staged or lost, or the squad withdraws.</summary>
        PickupRequested,
        /// <summary>Hover only: the ship is away and flying back to its station.</summary>
        ShipReturning,
        /// <summary>The ship is on station: grounded, or hovering with its cable down.</summary>
        ShipReady,
        /// <summary>Every owed item is lifted; #1293 R2: the step ends when nothing is owed, on no clock.</summary>
        CargoLift,
        /// <summary>Members climb through the ship's ladder/rope queue, the only serialiser.</summary>
        PersonnelClimb,
        /// <summary>The hull has completed its departure and the manifest confirmed it.</summary>
        Departed,
    }

    /// <summary>#874 Q4: what the host measured this tick. Unity-free, so the fixtures drive it.</summary>
    internal readonly struct BlacksiteExtractionFacts
    {
        internal readonly bool PickupRequested;
        internal readonly bool Hovering;
        /// <summary>The hull is Landed or Boarding (IsGrounded): on station in either mode.</summary>
        internal readonly bool OnStation;
        /// <summary>A hovering hull's rope is offered; always true on the ground.</summary>
        internal readonly bool CableDown;
        /// <summary>Something is still owed to the ship (#1293 R2: CargoOwed), or the crate or the hook has yet to go aboard.</summary>
        internal readonly bool CargoPending;
        /// <summary>The hull has finished its departure flight.</summary>
        internal readonly bool Departed;
        /// <summary>#1219 A1: the ShipReady wait (<see cref="BlacksiteExtractionSequence.ShipLandingTimeoutSeconds"/> from the pickup request) expired: stop waiting for the ship.</summary>
        internal readonly bool ReadyExpired;
        internal BlacksiteExtractionFacts(bool pickupRequested, bool hovering, bool onStation, bool cableDown,
            bool cargoPending, bool departed, bool readyExpired)
        {
            PickupRequested = pickupRequested; Hovering = hovering; OnStation = onStation; CableDown = cableDown;
            CargoPending = cargoPending; Departed = departed; ReadyExpired = readyExpired;
        }
    }

    /// <summary>
    /// #874 (2026-09-23) / #1293 R2: which terms hold the lift-off, for the doorway box and for the ship's ladder/rope
    /// column (round 1 of 2026-09-23 held a hovering ship 210 s on a bare <c>ladder=True</c>), printed by the departure
    /// refusal line. Every term ends on its own event, so the veto has no clock: <see cref="Occupant"/> a member, the
    /// crate or the hook holds the ship FIFO (its crossing or trip ends it; the ledger's stall answer releases a member
    /// who stops progressing); <see cref="Player"/> a living, controlled player in a box or on the rappel cable (he
    /// steps out); <see cref="CargoOnCable"/> the cache or the hook is on the rope (its trip ends); <see cref="Cart"/> a
    /// cart in a box while a player or an NPC is attached to it (the attachment ends). A member in a box holds nothing:
    /// the departure verdict is reached only once every unboarded member is stranded, and nothing ends a stranded body.
    /// </summary>
    [System.Flags]
    internal enum BlacksiteLiftOffHold : byte { None = 0, Occupant = 1, Player = 2, CargoOnCable = 4, Cart = 8 }

    /// <summary>#1293 R2: what one body found in a lift-off box is, for <see cref="BlacksiteLiftOffHolds.Of"/>.</summary>
    internal enum BlacksiteLiftOffBody : byte { Player, Member, Cart, Enemy }

    internal static class BlacksiteLiftOffHolds
    {
        /// <summary>The [UAT] spelling: <c>none</c>, or the held terms joined by '|'.</summary>
        internal static string Label(BlacksiteLiftOffHold hold)
        {
            if (hold == BlacksiteLiftOffHold.None) return "none";
            var parts = new List<string>(4);
            if ((hold & BlacksiteLiftOffHold.Occupant) != 0) parts.Add("occupant");
            if ((hold & BlacksiteLiftOffHold.Player) != 0) parts.Add("player");
            if ((hold & BlacksiteLiftOffHold.CargoOnCable) != 0) parts.Add("cargoOnCable");
            if ((hold & BlacksiteLiftOffHold.Cart) != 0) parts.Add("cart");
            return string.Join("|", parts);
        }

        /// <summary>
        /// #1293 R2 (A1d): the lift-off veto's fold for one body in the doorway box or the ladder column. A body holds
        /// only on a term that ends by its own event. <paramref name="active"/> is a living, controlled player (he steps
        /// out) or a cart someone is attached to (the attachment ends). A dead or uncontrolled player, an unattached
        /// cart, an enemy and any member hold nothing: nothing of ours ends them, so they would hold the lift-off for ever.
        /// </summary>
        internal static BlacksiteLiftOffHold Of(BlacksiteLiftOffBody body, bool active)
        {
            if (!active) return BlacksiteLiftOffHold.None;
            switch (body)
            {
                case BlacksiteLiftOffBody.Player: return BlacksiteLiftOffHold.Player;
                case BlacksiteLiftOffBody.Cart: return BlacksiteLiftOffHold.Cart;
                default: return BlacksiteLiftOffHold.None;
            }
        }

        /// <summary>
        /// #1293 R2 (A1d): the hover rope's own terms: the ship FIFO's holder (its crossing or trip ends it, or the ledger's
        /// stall answer releases it), a player on the rappel cable, and the cache or the hook on the rope (its trip ends).
        /// </summary>
        internal static BlacksiteLiftOffHold Rope(bool fifoHeld, bool playerOnRope, bool cargoOnCable)
            => (fifoHeld ? BlacksiteLiftOffHold.Occupant : BlacksiteLiftOffHold.None)
                | (playerOnRope ? BlacksiteLiftOffHold.Player : BlacksiteLiftOffHold.None)
                | (cargoOnCable ? BlacksiteLiftOffHold.CargoOnCable : BlacksiteLiftOffHold.None);
    }

    /// <summary>
    /// #1219 A1 / #1233 S4 (rule 1): what physically holds a hover ship on station after its drop:
    /// something is on the rope, or a player would be left on the hull. A member still aboard is
    /// not a veto; it keeps the grace clock from starting.
    /// </summary>
    [System.Flags]
    internal enum BlacksiteStationVeto : byte { None = 0, MemberOnRope = 1, PlayerOnRope = 2, CargoOnCable = 4, PlayerAboard = 8 }

    /// <summary>
    /// #1262 P2 (D2): what the ship measured for one living member, cheapest fact first. Any fact
    /// but <see cref="Stranded"/> holds the climb; a member with neither a route nor a session
    /// toward the ship is stranded, and a climb whose every unboarded member is stranded leaves.
    /// </summary>
    internal enum BlacksiteBoardingFact : byte { Boarded, Crossing, Queued, Elevator, Entrance, Route, Stranded }

    /// <summary>
    /// #1219 A1 / #1262 P2: the departure verdict while members climb. <see cref="AllBoarded"/> and
    /// <see cref="Stranded"/> both lift off; Stranded leaves the unboarded, who have neither route
    /// nor session toward the ship, as survivors.
    /// </summary>
    internal enum BlacksiteDepartureVerdict : byte { Hold, AllBoarded, Stranded }

    /// <summary>
    /// #874 Q4 (shape A of the 2026-09-24 review): the extraction used to be five wall clocks that
    /// each assumed something else was present - a 60 s delivery window that needed a grounded
    /// ship, a 60 s optional window armed at the recall, a 20 s manifest boarding token and the
    /// ship's own 20 s ladder queue - and on 23 September round 1 they abandoned a staged cart and
    /// apparatus because the ship they waited for was the thing they kept away (log 15521-15523),
    /// then took 112 s to board six men. This is the one owner that replaces them.
    ///
    /// Landing modes (Lawson's decision 1): a HOVER ship leaves after deployment and returns at
    /// <see cref="BlacksiteExtractionStep.PickupRequested"/>; a GROUND ship stays landed for the
    /// whole raid, so its sequence never enters <see cref="BlacksiteExtractionStep.ShipReturning"/>.
    /// </summary>
    internal static class BlacksiteExtractionSequence
    {
        /// <summary>#1219 A1: the ship's landing bound - the escalation aborts a landing not grounded in this long, and the
        /// pickup request stops waiting for the ship after it (the ShipReady wait). One number, two users.</summary>
        internal const float ShipLandingTimeoutSeconds = 90f;

        /// <summary>
        /// #1219 A1 (D2): a hover ship leaves this long after the last living, uncaptured member is
        /// off the rope, provided nothing physical is still on it. Round 3 of 2026-09-26 held six
        /// minutes past its last rope exit on a guard-posted term.
        /// </summary>
        internal const float StationLeaveGraceSeconds = 5f;

        /// <summary>#1219 A5: the largest squad the deployment can spawn.</summary>
        internal const int MaxSquadMembers = 10;

        /// <summary>
        /// #1219 A1 (D2) / #1233 S4 (rule 1): the hover station rule. The ship leaves once nobody
        /// has been aboard for <see cref="StationLeaveGraceSeconds"/> (<paramref name="secondsSinceSquadOff"/>
        /// is negative while anyone living and uncaptured is aboard) and no physical veto holds.
        /// </summary>
        internal static bool StationLeave(float secondsSinceSquadOff, BlacksiteStationVeto vetoes)
            => secondsSinceSquadOff >= StationLeaveGraceSeconds && vetoes == BlacksiteStationVeto.None;

        /// <summary>#1219 A1: the [UAT] spelling of the station vetoes: <c>none</c>, or the held ones joined by '|'.</summary>
        internal static string Label(BlacksiteStationVeto vetoes)
        {
            if (vetoes == BlacksiteStationVeto.None) return "none";
            var parts = new List<string>(4);
            if ((vetoes & BlacksiteStationVeto.MemberOnRope) != 0) parts.Add("memberOnRope");
            if ((vetoes & BlacksiteStationVeto.PlayerOnRope) != 0) parts.Add("playerOnRope");
            if ((vetoes & BlacksiteStationVeto.CargoOnCable) != 0) parts.Add("cargoOnCable");
            if ((vetoes & BlacksiteStationVeto.PlayerAboard) != 0) parts.Add("playerAboard");
            return string.Join("|", parts);
        }

        /// <summary>
        /// #1219 A1 / #1262 P2 (D2): the one departure authority, on facts and no clock. Only the
        /// climb can depart; every living member aboard (an empty roster included) is AllBoarded;
        /// any unboarded member a route or a session still holds is Hold; otherwise Stranded.
        /// </summary>
        internal static BlacksiteDepartureVerdict Departure(BlacksiteExtractionStep step, ReadOnlySpan<BlacksiteBoardingFact> living)
        {
            if (step != BlacksiteExtractionStep.PersonnelClimb) return BlacksiteDepartureVerdict.Hold;
            var verdict = BlacksiteDepartureVerdict.AllBoarded;
            for (int i = 0; i < living.Length; i++)
            {
                if (living[i] == BlacksiteBoardingFact.Boarded) continue;
                if (living[i] != BlacksiteBoardingFact.Stranded) return BlacksiteDepartureVerdict.Hold;
                verdict = BlacksiteDepartureVerdict.Stranded;
            }
            return verdict;
        }

        /// <summary>#1219 A1: the [UAT][Extraction] <c>reason=</c> spelling of a departing verdict.</summary>
        internal static string Token(BlacksiteDepartureVerdict verdict)
            => verdict == BlacksiteDepartureVerdict.AllBoarded ? "allBoarded"
             : verdict == BlacksiteDepartureVerdict.Stranded ? "stranded" : "hold";

        private static readonly string[] BoardingFactTokens = { "boarded", "crossing", "queued", "elevator", "entrance", "route", "stranded" };
        /// <summary>#1262 P2: the [UAT][Extraction] <c>waitingFor=</c> spelling of a member's fact.</summary>
        internal static string Token(BlacksiteBoardingFact fact) => BoardingFactTokens[(int)fact];

        /// <summary>
        /// The next step, one transition per call so every transition is its own log line.
        /// Returns <paramref name="step"/> unchanged when nothing has changed.
        /// </summary>
        internal static BlacksiteExtractionStep Next(BlacksiteExtractionStep step, in BlacksiteExtractionFacts facts)
        {
            if (step == BlacksiteExtractionStep.Departed) return step;
            if (facts.Departed) return BlacksiteExtractionStep.Departed;
            switch (step)
            {
                case BlacksiteExtractionStep.Raid:
                    return facts.PickupRequested ? BlacksiteExtractionStep.PickupRequested : step;
                case BlacksiteExtractionStep.PickupRequested:
                    if (Ready(facts) || facts.ReadyExpired) return BlacksiteExtractionStep.ShipReady;
                    return facts.Hovering && !facts.OnStation ? BlacksiteExtractionStep.ShipReturning : step;
                case BlacksiteExtractionStep.ShipReturning:
                    return Ready(facts) || facts.ReadyExpired ? BlacksiteExtractionStep.ShipReady : step;
                case BlacksiteExtractionStep.ShipReady:
                    return BlacksiteExtractionStep.CargoLift;
                case BlacksiteExtractionStep.CargoLift:
                    return !facts.CargoPending ? BlacksiteExtractionStep.PersonnelClimb : step;
                default:
                    return step;
            }
        }

        /// <summary>On station with the rope offered: the ShipReady wait's predicate.</summary>
        internal static bool Ready(in BlacksiteExtractionFacts facts) => facts.OnStation && (!facts.Hovering || facts.CableDown);

        /// <summary>Members may climb aboard: the lift is over and the ship's queue serialises them.</summary>
        internal static bool BoardingOpen(BlacksiteExtractionStep step) => step >= BlacksiteExtractionStep.PersonnelClimb;

        /// <summary>The [UAT][Extraction] spelling.</summary>
        internal static string Token(BlacksiteExtractionStep step)
        {
            switch (step)
            {
                case BlacksiteExtractionStep.PickupRequested: return "PickupRequested";
                case BlacksiteExtractionStep.ShipReturning: return "ShipReturning";
                case BlacksiteExtractionStep.ShipReady: return "ShipReady";
                case BlacksiteExtractionStep.CargoLift: return "CargoLift";
                case BlacksiteExtractionStep.PersonnelClimb: return "PersonnelClimb";
                case BlacksiteExtractionStep.Departed: return "Departed";
                default: return "Raid";
            }
        }
    }
}
