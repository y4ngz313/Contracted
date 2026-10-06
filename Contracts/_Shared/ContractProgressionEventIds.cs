using System.Collections.Generic;

namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>
    /// The pure half of the progression publisher: event-id composition and the
    /// attribution fallback decision. Nothing here touches Unity or Netcode, so the
    /// dedupe keys and the "attributed actor vs. crew-wide fan-out" rule can be
    /// exercised by <c>tests/ContractProgressionEventRegression</c> off-engine.
    ///
    /// Every id is unique per (round, occurrence, credited player). Attributed events
    /// publish exactly one id; the crew-wide fallback publishes one id per participant.
    /// The <c>ulong.MaxValue</c> sentinel means "no identifiable actor" everywhere in
    /// this system - <see cref="ContractProgressionEvents.ResolveClientId"/> returns it
    /// for a null player, and <see cref="ContractProgressionEvents.Publish"/> drops it.
    /// </summary>
    internal static class ContractProgressionEventIds
    {
        /// <summary>Sentinel client id meaning "the actor could not be identified".</summary>
        internal const ulong UnknownPlayerClientId = ulong.MaxValue;

        /// <summary>
        /// True when <paramref name="actorClientId"/> is not the sentinel. False always selects the
        /// crew-wide fan-out fallback; true only means an id was recorded, not that its owner is
        /// still here. Sole crediting additionally requires the roster check in
        /// <c>ContractProgressionEvents.IsCreditableActor</c> (#406) - this half of the
        /// system is deliberately engine-free and cannot see the roster.
        /// </summary>
        internal static bool IsAttributable(ulong actorClientId)
        {
            return actorClientId != UnknownPlayerClientId;
        }

        /// <summary>
        /// #1201 (Lawson, 2026-09-22): whether one occurrence was a crew award - every player in
        /// the round received it - rather than something a player did. The host decides this
        /// once per occurrence at publish time and stamps it on every copy as
        /// <c>ContractProgressionEvent.CrewWide</c>; the report hides crew awards from its lines
        /// and its remainder while the shift total still counts them.
        ///
        /// Crew-wide means the credited set IS the participant set and holds at least two
        /// players. A solo round is never crew-wide (the one player sees everything), an
        /// attributed single actor never is, and a partial fan-out - a containment wave closed
        /// after someone died - is not, because the survivors did earn it and the dead did not.
        /// Participant ids are unique, so equal counts plus containment is set equality.
        /// </summary>
        internal static bool IsCrewWide(ICollection<ulong> credited, ICollection<ulong> participants)
        {
            if (credited == null || participants == null || credited.Count < 2 || credited.Count != participants.Count)
                return false;

            foreach (ulong participant in participants)
            {
                if (!credited.Contains(participant))
                    return false;
            }

            return true;
        }

        // ---- existing kinds (ids unchanged; only the credited player can now differ) ----

        internal static string BlackoutAuditRestored(string roundId, ulong playerClientId)
        {
            return $"blackout.restored.{roundId}.{playerClientId}";
        }

        /// <summary>#794: once per breaker zone per credited player.</summary>
        internal static string BlackoutBreakerRestored(string roundId, int zoneIndex, ulong playerClientId)
        {
            return $"blackout.breaker.{roundId}.{Clamp(zoneIndex, 0)}.{playerClientId}";
        }

        internal static string PestControlCompleted(string roundId, ulong playerClientId)
        {
            return $"pestcontrol.completed.{roundId}.{playerClientId}";
        }

        internal static string WasteDisposalCompleted(string roundId, ulong playerClientId)
        {
            return $"wastedisposal.completed.{roundId}.{playerClientId}";
        }

        /// <summary>
        /// The attributed shape: once per survey target per credited player, used only when the
        /// placer is known. The credited player is part of the key for the same reason it is on
        /// every other fallback-capable kind - without it the crew-wide fan-out emitted one id for
        /// the whole crew and a per-player statistic could never be credited.
        ///
        /// This shape means "this player placed it". The crew-wide payout for the same target is
        /// <see cref="SurveyDronePlacedCrewWide"/>, so the two are distinguishable by id alone.
        /// </summary>
        internal static string SurveyDronePlaced(string roundId, int targetIndex, ulong playerClientId)
        {
            return $"survey.drone.{roundId}.{Clamp(targetIndex, 0)}.{playerClientId}";
        }

        /// <summary>
        /// The crew-wide fan-out shape of <see cref="SurveyDronePlaced"/>: the same uniqueness key
        /// with a <c>.crew.</c> segment before the player. A trailing <c>.{playerClientId}</c>
        /// alone cannot separate "I placed it" from "the crew was paid out", because the fan-out
        /// gives every participant an individually suffixed id too. The segment makes publisher
        /// intent readable from the id with no wire change, so a consumer counting personal
        /// placements can reject this shape. Only the crew-wide fallback path composes ids here.
        /// </summary>
        internal static string SurveyDronePlacedCrewWide(string roundId, int targetIndex, ulong playerClientId)
        {
            return $"survey.drone.{roundId}.{Clamp(targetIndex, 0)}.crew.{playerClientId}";
        }

        internal static string ContractResolved(string roundId, string contractName, bool succeeded, ulong playerClientId)
        {
            string stage = succeeded ? "completed" : "failed";
            return $"contract.{stage}.{roundId}.{contractName}.{playerClientId}";
        }

        // ---- Whistleblower and breach drill (#1201: ids moved here from inline composition) ----
        //
        // The attributed shapes are unchanged and carry no player: one actor, one id. The fan-out
        // shapes used to be the same string for every participant, so the client mirror - which
        // dedupes on the id alone - kept the first copy and dropped the rest, and every other
        // client was never paid. Each fan-out copy now ends `.crew.{player}` like the survey
        // payout, which makes it unique per credited player and marks it as not personally done.

        internal static string WhistleblowerSighted(string roundId, ulong enemyNetworkId, int sightingIndex)
        {
            return $"whistleblower.sighting.{roundId}.{enemyNetworkId}.{Clamp(sightingIndex, 1)}";
        }

        internal static string WhistleblowerSightedCrewWide(string roundId, ulong enemyNetworkId, int sightingIndex, ulong playerClientId)
        {
            return $"whistleblower.sighting.{roundId}.{enemyNetworkId}.{Clamp(sightingIndex, 1)}.crew.{playerClientId}";
        }

        internal static string WhistleblowerNeutralized(string roundId, ulong enemyNetworkId)
        {
            return $"whistleblower.neutralized.{roundId}.{enemyNetworkId}";
        }

        internal static string WhistleblowerNeutralizedCrewWide(string roundId, ulong enemyNetworkId, ulong playerClientId)
        {
            return $"whistleblower.neutralized.{roundId}.{enemyNetworkId}.crew.{playerClientId}";
        }

        internal static string ShadowRaidDrillAttached(string roundId, ulong containerNetworkId)
        {
            return $"shadowraid.drill.attached.{roundId}.{containerNetworkId}";
        }

        internal static string ShadowRaidDrillAttachedCrewWide(string roundId, ulong containerNetworkId, ulong playerClientId)
        {
            return $"shadowraid.drill.attached.{roundId}.{containerNetworkId}.crew.{playerClientId}";
        }

        internal static string ShadowRaidDrillCompleted(string roundId, ulong containerNetworkId)
        {
            return $"shadowraid.drill.completed.{roundId}.{containerNetworkId}";
        }

        internal static string ShadowRaidDrillCompletedCrewWide(string roundId, ulong containerNetworkId, ulong playerClientId)
        {
            return $"shadowraid.drill.completed.{roundId}.{containerNetworkId}.crew.{playerClientId}";
        }

        // ---- kinds added by #406 ----

        /// <summary>One credit per survived wave per surviving participant.</summary>
        internal static string ContainmentBreachWaveSurvived(string roundId, int waveNumber, ulong playerClientId)
        {
            return $"containment.wave.{roundId}.{Clamp(waveNumber, 1)}.{playerClientId}";
        }

        /// <summary>Once per drive per round - the recovery is a one-shot objective.</summary>
        internal static string ShadowRaidHardDriveRecovered(string roundId, ulong driveNetworkId, ulong playerClientId)
        {
            return $"shadowraid.harddrive.{roundId}.{driveNetworkId}.{playerClientId}";
        }

        /// <summary>Repeatable: the malfunction can recur, so the repair index keys the credit.</summary>
        internal static string WasteDisposalRepaired(string roundId, int repairIndex, ulong playerClientId)
        {
            return $"wastedisposal.repaired.{roundId}.{Clamp(repairIndex, 1)}.{playerClientId}";
        }

        /// <summary>Repeatable: captures after the first, keyed by capture index so a consumer can cap.</summary>
        internal static string PestControlCapture(string roundId, int captureIndex, ulong playerClientId)
        {
            return $"pestcontrol.capture.{roundId}.{Clamp(captureIndex, 2)}.{playerClientId}";
        }

        // ---- kinds added by #805 (Lategame Upgrades-backed directives) ----
        //
        // LGU announces every progress transition through a ClientRpc whose body carries the
        // object and not the player who acted on it, and Contracted adds no networking of its
        // own for these contracts (#804). Nine of the ten steps therefore have no attributed
        // shape at all: the host fans them out to the crew, and every id says so with the same
        // `.crew.` segment the survey payout uses, so a consumer counting personally earned acts
        // rejects them by shape alone rather than having to know which contract they came from.
        //
        // The one exception is the extermination cull, which is counted off the vanilla
        // `EnemyAI.KillEnemy` postfix the report already attributes through its enemy-hit
        // ledger. It carries both shapes, exactly like the survey placement.

        /// <summary>The laptop's login screen was opened. Once per round per participant.</summary>
        internal static string LguLaptopLogin(string roundId, ulong playerClientId)
        {
            return $"lgu.data.login.{roundId}.crew.{playerClientId}";
        }

        /// <summary>survey.db left the laptop. Once per round per participant.</summary>
        internal static string LguDataRecovered(string roundId, ulong playerClientId)
        {
            return $"lgu.data.recovered.{roundId}.crew.{playerClientId}";
        }

        /// <summary>The attributed cull shape: this player landed the killing blow on the nth bug.</summary>
        internal static string LguHoardingBugExterminated(string roundId, int killIndex, ulong playerClientId)
        {
            return $"lgu.extermination.bug.{roundId}.{Clamp(killIndex, 1)}.{playerClientId}";
        }

        /// <summary>The crew-wide payout for the nth bug, used when no killer could be resolved.</summary>
        internal static string LguHoardingBugExterminatedCrewWide(string roundId, int killIndex, ulong playerClientId)
        {
            return $"lgu.extermination.bug.{roundId}.{Clamp(killIndex, 1)}.crew.{playerClientId}";
        }

        internal static string LguNestDestroyed(string roundId, ulong playerClientId)
        {
            return $"lgu.extermination.nest.{roundId}.crew.{playerClientId}";
        }

        internal static string LguScavengerTreated(string roundId, ulong playerClientId)
        {
            return $"lgu.extraction.treated.{roundId}.crew.{playerClientId}";
        }

        internal static string LguScavengerRecovered(string roundId, ulong playerClientId)
        {
            return $"lgu.extraction.recovered.{roundId}.crew.{playerClientId}";
        }

        /// <summary>Repeatable: the altar takes several items, so the placement index keys the credit.</summary>
        internal static string LguRitualItemPlaced(string roundId, int itemIndex, ulong playerClientId)
        {
            return $"lgu.exorcism.item.{roundId}.{Clamp(itemIndex, 1)}.crew.{playerClientId}";
        }

        internal static string LguDemonIdentified(string roundId, ulong playerClientId)
        {
            return $"lgu.exorcism.demon.{roundId}.crew.{playerClientId}";
        }

        /// <summary>Repeatable: three wires, so the cut index keys the credit.</summary>
        internal static string LguWireCut(string roundId, int wireIndex, ulong playerClientId)
        {
            return $"lgu.defusal.wire.{roundId}.{Clamp(wireIndex, 1)}.crew.{playerClientId}";
        }

        internal static string LguDeviceDefused(string roundId, ulong playerClientId)
        {
            return $"lgu.defusal.defused.{roundId}.crew.{playerClientId}";
        }

        // ---- kinds added by #1195 (side objectives) ----
        //
        // Keyed by source and entry as well as the round, so #1198's trials can publish through
        // the same kinds without colliding with an interior's objective. The crew-wide shape carries
        // the `.crew.` segment every fan-out uses, so a per-player statistic can reject it by shape.

        /// <summary>Once per step per round per credited player: a step reached again after the
        /// count fell is the same id.</summary>
        internal static string SideObjectiveStepCompleted(string roundId, string source, string entry, int step, ulong playerClientId, bool crewWide)
        {
            return $"side.step.{roundId}.{source}.{entry}.{Clamp(step, 1)}.{Player(playerClientId, crewWide)}";
        }

        internal static string SideObjectiveCompleted(string roundId, string source, string entry, ulong playerClientId, bool crewWide)
        {
            return $"side.completed.{roundId}.{source}.{entry}.{Player(playerClientId, crewWide)}";
        }

        internal static string SideObjectiveRewardClaimed(string roundId, string source, string entry, ulong playerClientId, bool crewWide)
        {
            return $"side.claimed.{roundId}.{source}.{entry}.{Player(playerClientId, crewWide)}";
        }

        private static string Player(ulong playerClientId, bool crewWide)
        {
            return crewWide ? "crew." + playerClientId : playerClientId.ToString();
        }

        private static int Clamp(int value, int minimum)
        {
            return value < minimum ? minimum : value;
        }
    }
}
