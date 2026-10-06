using System;
using System.Collections.Generic;

using GameNetcodeStuff;
using UnityEngine;

using Y4NGZCompany.Bootstrap;
namespace Y4NGZCompany.Contracts._Shared
{
    public sealed class ContractProgressionEvent
    {
        public int SchemaVersion { get; internal set; }
        public string EventId { get; internal set; }
        public ContractProgressionEventKind Kind { get; internal set; }
        public ulong PlayerClientId { get; internal set; }
        public string ContractId { get; internal set; }
        public string ContractType { get; internal set; }
        public string SourceLabel { get; internal set; }
        public string RiskLevel { get; internal set; }
        public int Count { get; internal set; }
        public float Seconds { get; internal set; }
        public bool ContractSucceeded { get; internal set; }
        /// <summary>
        /// #1201: every player in the round received this same occurrence, so it is a crew award
        /// and not something this player did (<see cref="ContractProgressionEventIds.IsCrewWide"/>).
        /// The host decides it once per occurrence and it rides the wire as a trailing byte, so
        /// every copy on every peer agrees. Additive like the kinds since #406: SchemaVersion stays
        /// 1, an older peer ignores the byte, and a consumer that reads no such property treats
        /// every award as its own. The report hides crew awards; the payout is unchanged.
        /// </summary>
        public bool CrewWide { get; internal set; }
    }

    public static class ContractProgressionEvents
    {
        public const int CurrentSchemaVersion = 1;
        public static event Action<ContractProgressionEvent> Published;

        internal static void PublishDefuseCorrectWire(ulong playerClientId, ulong bombNetworkId, int wireSlot)
        {
            Publish(new ContractProgressionEvent
            {
                SchemaVersion = CurrentSchemaVersion,
                EventId = $"defuse.wire.{GetRoundId()}.{bombNetworkId}.{wireSlot}",
                Kind = ContractProgressionEventKind.DefuseCorrectWire,
                PlayerClientId = playerClientId,
                ContractId = "Defuse",
                ContractType = "Defuse",
                RiskLevel = GetRiskLevel(),
                Count = 1
            });
        }

        internal static void PublishDefuseCorrectCode(ulong playerClientId, ulong bombNetworkId)
        {
            Publish(new ContractProgressionEvent
            {
                SchemaVersion = CurrentSchemaVersion,
                EventId = $"defuse.code.{GetRoundId()}.{bombNetworkId}",
                Kind = ContractProgressionEventKind.DefuseCorrectCode,
                PlayerClientId = playerClientId,
                ContractId = "Defuse",
                ContractType = "Defuse",
                RiskLevel = GetRiskLevel(),
                Count = 1
            });
        }

        internal static void PublishPayloadPilotSeconds(ulong playerClientId, ulong cartNetworkId, float seconds)
        {
            if (seconds <= 0f)
                return;

            Publish(new ContractProgressionEvent
            {
                SchemaVersion = CurrentSchemaVersion,
                EventId = $"payload.pilot.{GetRoundId()}.{cartNetworkId}.{playerClientId}",
                Kind = ContractProgressionEventKind.PayloadPilotSeconds,
                PlayerClientId = playerClientId,
                ContractId = "Payload",
                ContractType = "Payload",
                RiskLevel = GetRiskLevel(),
                Seconds = seconds,
                ContractSucceeded = true
            });
        }

        // --- Host-authoritative sub-objectives (fanned out by ContractProgressionNetwork). ---

        internal static void PublishWhistleblowerSighted(ulong playerClientId, ulong enemyNetworkId, int sightingIndex)
        {
            string roundId = GetRoundId();
            PublishWhistleblowerSighted(
                playerClientId,
                ContractProgressionEventIds.WhistleblowerSighted(roundId, enemyNetworkId, sightingIndex),
                sightingIndex,
                crewWide: false);
        }

        /// <summary>The spotter is unknown: every participant is credited, one id per player (#1201).</summary>
        internal static void PublishWhistleblowerSightedToAll(ulong enemyNetworkId, int sightingIndex)
        {
            string roundId = GetRoundId();
            List<ulong> credited = Credit(EnumerateParticipants(), out bool crewWide);
            foreach (ulong playerClientId in credited)
            {
                PublishWhistleblowerSighted(
                    playerClientId,
                    ContractProgressionEventIds.WhistleblowerSightedCrewWide(roundId, enemyNetworkId, sightingIndex, playerClientId),
                    sightingIndex,
                    crewWide);
            }
        }

        private static void PublishWhistleblowerSighted(ulong playerClientId, string eventId, int sightingIndex, bool crewWide)
        {
            ContractProgressionNetwork.BroadcastServer(new ContractProgressionEvent
            {
                SchemaVersion = CurrentSchemaVersion,
                EventId = eventId,
                Kind = ContractProgressionEventKind.WhistleblowerSighted,
                PlayerClientId = playerClientId,
                ContractId = "Whistleblower",
                ContractType = "Whistleblower",
                RiskLevel = GetRiskLevel(),
                Count = Mathf.Max(1, sightingIndex),
                CrewWide = crewWide
            });
        }

        internal static void PublishWhistleblowerNeutralized(ulong playerClientId, ulong enemyNetworkId)
        {
            PublishWhistleblowerNeutralized(
                playerClientId,
                ContractProgressionEventIds.WhistleblowerNeutralized(GetRoundId(), enemyNetworkId),
                crewWide: false);
        }

        /// <summary>No known killer: every participant is credited, one id per player (#1201).</summary>
        internal static void PublishWhistleblowerNeutralizedToAll(ulong enemyNetworkId)
        {
            string roundId = GetRoundId();
            List<ulong> credited = Credit(EnumerateParticipants(), out bool crewWide);
            foreach (ulong playerClientId in credited)
            {
                PublishWhistleblowerNeutralized(
                    playerClientId,
                    ContractProgressionEventIds.WhistleblowerNeutralizedCrewWide(roundId, enemyNetworkId, playerClientId),
                    crewWide);
            }
        }

        private static void PublishWhistleblowerNeutralized(ulong playerClientId, string eventId, bool crewWide)
        {
            ContractProgressionNetwork.BroadcastServer(new ContractProgressionEvent
            {
                SchemaVersion = CurrentSchemaVersion,
                EventId = eventId,
                Kind = ContractProgressionEventKind.WhistleblowerNeutralized,
                PlayerClientId = playerClientId,
                ContractId = "Whistleblower",
                ContractType = "Whistleblower",
                RiskLevel = GetRiskLevel(),
                Count = 1,
                ContractSucceeded = true,
                CrewWide = crewWide
            });
        }

        /// <summary>
        /// #1076. Their ship left the moon with the contract objective aboard.
        ///
        /// This must NOT ride a NetworkVariable, a NetworkList or a ClientRpc on the mission, the
        /// ship, the cart, the witness or the drive, because every one of those objects is
        /// despawned inside the same synchronous departure chain
        /// (<c>BlacksiteShipController.Update</c> -> <c>ConfirmDepartureServer</c> ->
        /// <c>MoonEscalationState.NotifyBlacksiteShipDeparted</c> -> <c>TryDespawnObject</c> ->
        /// <c>Despawn(destroy: true)</c>). NGO flushes variable deltas at PostLateUpdate over the
        /// spawned-object list, so a delta written on an object that left that list earlier in the
        /// frame is never sent: the client would receive only the destroy message. That is exactly
        /// how the cart's own <c>_failed</c> write can be lost, and why the departure fact needs a
        /// carrier with no object behind it at all.
        ///
        /// This channel is that carrier. It is a named message, not an object RPC, so nothing has
        /// to still be spawned; the host keeps a round-keyed ledger and replays it to a client that
        /// registered late, which is what makes a late joiner resolve the failed row (#406).
        ///
        /// Crew-wide, so it fans out per participant the way ContractCompleted/ContractFailed does:
        /// <c>Publish</c> drops an event with no identifiable player, and no single player owns a
        /// departure. Every copy carries the same <paramref name="objectiveKind"/>, and the
        /// subscriber ignores the player id, so ANY copy that lands raises the latch - including a
        /// copy addressed to a player who was not in the lobby when the reader joined.
        /// </summary>
        /// <param name="objectiveKind">The <c>BlacksiteObjectiveKind</c> name, as a token.</param>
        /// <param name="contractName">The contract the departure voids.</param>
        /// <summary>
        /// #1076: whether a BlacksiteObjectiveDeparted event id was minted for THIS round. The id
        /// carries the same seed.level round stamp the ledger is keyed by, so a copy replayed to a
        /// client that joined in orbit after the round (the only late-join window the game has)
        /// can be told apart from a live one and dropped, instead of voiding the next round's card.
        /// </summary>
        internal static bool IsBlacksiteDepartureForCurrentRound(string eventId)
        {
            return !string.IsNullOrEmpty(eventId)
                && eventId.StartsWith("blacksite.departed." + GetRoundId() + ".", StringComparison.Ordinal);
        }

        internal static void PublishBlacksiteObjectiveDeparted(string objectiveKind, string contractName)
        {
            if (string.IsNullOrWhiteSpace(objectiveKind))
                return;

            string roundId = GetRoundId();
            string risk = GetRiskLevel();
            string contract = string.IsNullOrWhiteSpace(contractName) ? "Blacksite" : contractName;
            List<ulong> credited = Credit(EnumerateParticipants(), out bool crewWide);
            foreach (ulong playerClientId in credited)
            {
                ContractProgressionNetwork.BroadcastServer(new ContractProgressionEvent
                {
                    SchemaVersion = CurrentSchemaVersion,
                    EventId = $"blacksite.departed.{roundId}.{objectiveKind}.{playerClientId}",
                    Kind = ContractProgressionEventKind.BlacksiteObjectiveDeparted,
                    PlayerClientId = playerClientId,
                    ContractId = contract,
                    ContractType = contract,
                    // The payload. SourceLabel is the only free-form string on the wire format and
                    // it already carries a kind token for other publishers.
                    SourceLabel = objectiveKind,
                    RiskLevel = risk,
                    Count = 1,
                    ContractSucceeded = false,
                    CrewWide = crewWide
                });
            }
        }

        /// <summary>
        /// Attribution: the player who set the drone down. <c>MoonContractState</c> calls
        /// <see cref="PublishSurveyDronePlaced(ulong,int,int,string)"/> with the placer, or
        /// <see cref="PublishSurveyDronePlacedToAll"/> when the placer is unknown.
        ///
        /// The id is keyed on the credited player like every other fallback-capable kind. It used
        /// to be keyed on the target alone, which made the crew-wide fan-out emit one shared id and
        /// left a consumer no way to tell an individually attributed placement from a crew payout -
        /// so a per-player survey statistic could never be credited. Per-player ids do not change
        /// the once-per-target semantics: a consumer filters to its local player before deduping,
        /// so each machine still sees exactly one survey credit per target.
        ///
        /// The two entry points compose different id shapes, which is what makes publisher intent
        /// readable downstream with no wire change: an attributed placement ends <c>.{player}</c>
        /// with no <c>.crew.</c> segment, while a fan-out payout carries <c>.crew.{player}</c>. A
        /// per-player statistic that should only count placements the player actually made can
        /// therefore reject the fan-out shape. Both shapes stay unique per (round, target, player),
        /// so dedupe and once-per-target are untouched either way.
        /// </summary>
        internal static void PublishSurveyDronePlaced(ulong playerClientId, int targetIndex, int placedCount, string targetLabel)
        {
            PublishSurveyDronePlaced(playerClientId, targetIndex, placedCount, targetLabel, attributed: true, crewWide: false);
        }

        internal static void PublishSurveyDronePlacedToAll(int targetIndex, int placedCount, string targetLabel)
        {
            List<ulong> credited = Credit(EnumerateParticipants(), out bool crewWide);
            foreach (ulong playerClientId in credited)
                PublishSurveyDronePlaced(playerClientId, targetIndex, placedCount, targetLabel, attributed: false, crewWide);
        }

        private static void PublishSurveyDronePlaced(
            ulong playerClientId, int targetIndex, int placedCount, string targetLabel, bool attributed, bool crewWide)
        {
            string roundId = GetRoundId();
            ContractProgressionNetwork.BroadcastServer(new ContractProgressionEvent
            {
                SchemaVersion = CurrentSchemaVersion,
                EventId = attributed
                    ? ContractProgressionEventIds.SurveyDronePlaced(roundId, targetIndex, playerClientId)
                    : ContractProgressionEventIds.SurveyDronePlacedCrewWide(roundId, targetIndex, playerClientId),
                Kind = ContractProgressionEventKind.SurveyDronePlaced,
                PlayerClientId = playerClientId,
                ContractId = "Survey",
                ContractType = "Survey",
                SourceLabel = NormalizeLabel(targetLabel),
                RiskLevel = GetRiskLevel(),
                Count = Mathf.Max(1, placedCount),
                CrewWide = crewWide
            });
        }

        /// <summary>
        /// Attribution: the player who flipped the final breaker (or pulled the audit lever).
        /// The crew-wide fan-out is kept as the explicit fallback for the paths where the actor
        /// is genuinely unknowable - a host zone snapshot, or a breaker whose interaction actor
        /// never reached this machine.
        /// </summary>
        internal static void PublishBlackoutAuditRestored(string source, ulong actorClientId)
        {
            string label = NormalizeLabel(source);
            string roundId = GetRoundId();
            string risk = GetRiskLevel();
            List<ulong> credited = Credit(ResolveCredited(actorClientId), out bool crewWide);
            foreach (ulong playerClientId in credited)
            {
                ContractProgressionNetwork.BroadcastServer(new ContractProgressionEvent
                {
                    SchemaVersion = CurrentSchemaVersion,
                    EventId = ContractProgressionEventIds.BlackoutAuditRestored(roundId, playerClientId),
                    Kind = ContractProgressionEventKind.BlackoutAuditRestored,
                    PlayerClientId = playerClientId,
                    ContractId = "BlackoutAudit",
                    ContractType = "BlackoutAudit",
                    SourceLabel = label,
                    RiskLevel = risk,
                    Count = 1,
                    ContractSucceeded = true,
                    CrewWide = crewWide
                });
            }
        }

        /// <summary>
        /// #794: one credit per restored breaker zone. Attribution: the player who flipped it,
        /// resolved through the same latch the full restore uses; the crew-wide fan-out is
        /// the fallback when the actor is unknowable. <paramref name="restoredCount"/> rides
        /// in <c>Count</c> as progress; the id is unique per zone and credited player.
        /// </summary>
        internal static void PublishBlackoutBreakerRestored(int zoneIndex, int zoneCount, int restoredCount, ulong actorClientId)
        {
            string roundId = GetRoundId();
            string risk = GetRiskLevel();
            string label = $"zone {Mathf.Max(1, zoneIndex + 1)}/{Mathf.Max(1, zoneCount)}";
            List<ulong> credited = Credit(ResolveCredited(actorClientId), out bool crewWide);
            foreach (ulong playerClientId in credited)
            {
                ContractProgressionNetwork.BroadcastServer(new ContractProgressionEvent
                {
                    SchemaVersion = CurrentSchemaVersion,
                    EventId = ContractProgressionEventIds.BlackoutBreakerRestored(roundId, zoneIndex, playerClientId),
                    Kind = ContractProgressionEventKind.BlackoutBreakerRestored,
                    PlayerClientId = playerClientId,
                    ContractId = "BlackoutAudit",
                    ContractType = "BlackoutAudit",
                    SourceLabel = label,
                    RiskLevel = risk,
                    Count = Mathf.Max(1, restoredCount),
                    CrewWide = crewWide
                });
            }
        }

        /// <summary>
        /// One credit per wave survived, per participant still alive when the wave closed.
        /// Waves close when the next one spawns and when the contract completes, so the final
        /// wave is credited too.
        /// </summary>
        internal static void PublishContainmentBreachWaveSurvived(int waveNumber, int totalWaves)
        {
            string roundId = GetRoundId();
            string risk = GetRiskLevel();
            int wave = Mathf.Max(1, waveNumber);
            // #1201: crew-wide only when every participant is still alive to be credited. A wave
            // closed after a death credits the survivors for something they did and stays visible.
            List<ulong> credited = Credit(EnumerateLivingParticipants(), out bool crewWide);
            foreach (ulong playerClientId in credited)
            {
                ContractProgressionNetwork.BroadcastServer(new ContractProgressionEvent
                {
                    SchemaVersion = CurrentSchemaVersion,
                    EventId = ContractProgressionEventIds.ContainmentBreachWaveSurvived(roundId, wave, playerClientId),
                    Kind = ContractProgressionEventKind.ContainmentBreachWaveSurvived,
                    PlayerClientId = playerClientId,
                    ContractId = "ContainmentBreach",
                    ContractType = "ContainmentBreach",
                    SourceLabel = $"wave {wave}/{Mathf.Max(wave, totalWaves)}",
                    RiskLevel = risk,
                    Count = wave,
                    ContractSucceeded = true,
                    CrewWide = crewWide
                });
            }
        }

        /// <summary>
        /// The named "Recover the hard drive" objective. Attribution: the player holding the
        /// drive on the server when it was reported found; crew-wide when it was reported by a
        /// path with no carrier (an item already in the ship at spawn, for instance).
        /// </summary>
        internal static void PublishShadowRaidHardDriveRecovered(ulong actorClientId, ulong driveNetworkId, string source)
        {
            string label = NormalizeLabel(source);
            string roundId = GetRoundId();
            string risk = GetRiskLevel();
            List<ulong> credited = Credit(ResolveCredited(actorClientId), out bool crewWide);
            foreach (ulong playerClientId in credited)
            {
                ContractProgressionNetwork.BroadcastServer(new ContractProgressionEvent
                {
                    SchemaVersion = CurrentSchemaVersion,
                    EventId = ContractProgressionEventIds.ShadowRaidHardDriveRecovered(roundId, driveNetworkId, playerClientId),
                    Kind = ContractProgressionEventKind.ShadowRaidHardDriveRecovered,
                    PlayerClientId = playerClientId,
                    ContractId = "ShadowRaid",
                    ContractType = "ShadowRaid",
                    SourceLabel = label,
                    RiskLevel = risk,
                    Count = 1,
                    ContractSucceeded = true,
                    CrewWide = crewWide
                });
            }
        }

        internal static void PublishShadowRaidDrillAttached(ulong playerClientId, ulong containerNetworkId)
        {
            PublishShadowRaidDrillAttached(
                playerClientId,
                ContractProgressionEventIds.ShadowRaidDrillAttached(GetRoundId(), containerNetworkId),
                crewWide: false);
        }

        /// <summary>Nobody was seen carrying the drill: every participant is credited (#1201).</summary>
        internal static void PublishShadowRaidDrillAttachedToAll(ulong containerNetworkId)
        {
            string roundId = GetRoundId();
            List<ulong> credited = Credit(EnumerateParticipants(), out bool crewWide);
            foreach (ulong playerClientId in credited)
            {
                PublishShadowRaidDrillAttached(
                    playerClientId,
                    ContractProgressionEventIds.ShadowRaidDrillAttachedCrewWide(roundId, containerNetworkId, playerClientId),
                    crewWide);
            }
        }

        private static void PublishShadowRaidDrillAttached(ulong playerClientId, string eventId, bool crewWide)
        {
            ContractProgressionNetwork.BroadcastServer(new ContractProgressionEvent
            {
                SchemaVersion = CurrentSchemaVersion,
                EventId = eventId,
                Kind = ContractProgressionEventKind.ShadowRaidDrillAttached,
                PlayerClientId = playerClientId,
                ContractId = "ShadowRaid",
                ContractType = "ShadowRaid",
                RiskLevel = GetRiskLevel(),
                Count = 1,
                CrewWide = crewWide
            });
        }

        internal static void PublishShadowRaidDrillCompleted(ulong playerClientId, ulong containerNetworkId, float seconds)
        {
            PublishShadowRaidDrillCompleted(
                playerClientId,
                ContractProgressionEventIds.ShadowRaidDrillCompleted(GetRoundId(), containerNetworkId),
                seconds,
                crewWide: false);
        }

        /// <summary>Nobody was seen carrying the drill: every participant is credited (#1201).</summary>
        internal static void PublishShadowRaidDrillCompletedToAll(ulong containerNetworkId, float seconds)
        {
            string roundId = GetRoundId();
            List<ulong> credited = Credit(EnumerateParticipants(), out bool crewWide);
            foreach (ulong playerClientId in credited)
            {
                PublishShadowRaidDrillCompleted(
                    playerClientId,
                    ContractProgressionEventIds.ShadowRaidDrillCompletedCrewWide(roundId, containerNetworkId, playerClientId),
                    seconds,
                    crewWide);
            }
        }

        private static void PublishShadowRaidDrillCompleted(ulong playerClientId, string eventId, float seconds, bool crewWide)
        {
            ContractProgressionNetwork.BroadcastServer(new ContractProgressionEvent
            {
                SchemaVersion = CurrentSchemaVersion,
                EventId = eventId,
                Kind = ContractProgressionEventKind.ShadowRaidDrillCompleted,
                PlayerClientId = playerClientId,
                ContractId = "ShadowRaid",
                ContractType = "ShadowRaid",
                RiskLevel = GetRiskLevel(),
                Count = 1,
                Seconds = Mathf.Max(0f, seconds),
                ContractSucceeded = true,
                CrewWide = crewWide
            });
        }

        /// <summary>
        /// Attribution: the player who pulled the lever that dropped the cage on the capture.
        /// Crew-wide fallback covers the debug drop API, a pull whose sender was lost, and - via
        /// the roster check in <see cref="ResolveCredited"/> - a puller who disconnected before
        /// the cage landed.
        /// </summary>
        internal static void PublishPestControlCompleted(int trapsArmed, string source, ulong actorClientId)
        {
            string label = NormalizeLabel(source);
            string roundId = GetRoundId();
            string risk = GetRiskLevel();
            List<ulong> credited = Credit(ResolveCredited(actorClientId), out bool crewWide);
            foreach (ulong playerClientId in credited)
            {
                ContractProgressionNetwork.BroadcastServer(new ContractProgressionEvent
                {
                    SchemaVersion = CurrentSchemaVersion,
                    EventId = ContractProgressionEventIds.PestControlCompleted(roundId, playerClientId),
                    Kind = ContractProgressionEventKind.PestControlCompleted,
                    PlayerClientId = playerClientId,
                    ContractId = "PestControl",
                    ContractType = "PestControl",
                    SourceLabel = label,
                    RiskLevel = risk,
                    Count = Mathf.Max(1, trapsArmed),
                    ContractSucceeded = true,
                    CrewWide = crewWide
                });
            }
        }

        /// <summary>
        /// Captures after the first. <paramref name="captureIndex"/> is the running capture
        /// number (2 for the second capture), so the consumer can cap repeat credit.
        /// </summary>
        internal static void PublishPestControlCapture(int captureIndex, int capturedTotal, string source, ulong actorClientId)
        {
            string label = NormalizeLabel(source);
            string roundId = GetRoundId();
            string risk = GetRiskLevel();
            int index = Mathf.Max(2, captureIndex);
            List<ulong> credited = Credit(ResolveCredited(actorClientId), out bool crewWide);
            foreach (ulong playerClientId in credited)
            {
                ContractProgressionNetwork.BroadcastServer(new ContractProgressionEvent
                {
                    SchemaVersion = CurrentSchemaVersion,
                    EventId = ContractProgressionEventIds.PestControlCapture(roundId, index, playerClientId),
                    Kind = ContractProgressionEventKind.PestControlCapture,
                    PlayerClientId = playerClientId,
                    ContractId = "PestControl",
                    ContractType = "PestControl",
                    SourceLabel = label,
                    RiskLevel = risk,
                    Count = Mathf.Max(index, capturedTotal),
                    ContractSucceeded = true,
                    CrewWide = crewWide
                });
            }
        }

        /// <summary>
        /// Attribution rule (one deterministic rule, as documented on the issue): the player
        /// whose START press committed the burn that just completed. Loaders are not used -
        /// a "dominant loader" is ambiguous when two people load equal value, and the start
        /// press is a single unambiguous server-observed act. Crew-wide fallback covers a burn
        /// started by the debug API, and - via the roster check in <see cref="ResolveCredited"/> -
        /// one whose starter disconnected before it finished. A starter who died mid-burn still
        /// keeps the credit.
        /// </summary>
        internal static void PublishWasteDisposalCompleted(int disposedCount, string source, ulong actorClientId)
        {
            string label = NormalizeLabel(source);
            string roundId = GetRoundId();
            string risk = GetRiskLevel();
            List<ulong> credited = Credit(ResolveCredited(actorClientId), out bool crewWide);
            foreach (ulong playerClientId in credited)
            {
                ContractProgressionNetwork.BroadcastServer(new ContractProgressionEvent
                {
                    SchemaVersion = CurrentSchemaVersion,
                    EventId = ContractProgressionEventIds.WasteDisposalCompleted(roundId, playerClientId),
                    Kind = ContractProgressionEventKind.WasteDisposalCompleted,
                    PlayerClientId = playerClientId,
                    ContractId = "WasteDisposal",
                    ContractType = "WasteDisposal",
                    SourceLabel = label,
                    RiskLevel = risk,
                    Count = Mathf.Max(1, disposedCount),
                    ContractSucceeded = true,
                    CrewWide = crewWide
                });
            }
        }

        /// <summary>
        /// The repeatable skill-gated malfunction repair. Attribution: the player who held the
        /// repair interact to completion.
        /// </summary>
        internal static void PublishWasteDisposalRepaired(int repairIndex, string source, ulong actorClientId)
        {
            string label = NormalizeLabel(source);
            string roundId = GetRoundId();
            string risk = GetRiskLevel();
            int index = Mathf.Max(1, repairIndex);
            List<ulong> credited = Credit(ResolveCredited(actorClientId), out bool crewWide);
            foreach (ulong playerClientId in credited)
            {
                ContractProgressionNetwork.BroadcastServer(new ContractProgressionEvent
                {
                    SchemaVersion = CurrentSchemaVersion,
                    EventId = ContractProgressionEventIds.WasteDisposalRepaired(roundId, index, playerClientId),
                    Kind = ContractProgressionEventKind.WasteDisposalRepaired,
                    PlayerClientId = playerClientId,
                    ContractId = "WasteDisposal",
                    ContractType = "WasteDisposal",
                    SourceLabel = label,
                    RiskLevel = risk,
                    Count = index,
                    CrewWide = crewWide
                });
            }
        }

        // --- #805: the Lategame Upgrades-backed directives -------------------------------
        //
        // Every one of these is called from a latch in LguContractRuntime that already gates
        // on being the host, and each fires exactly once per transition, so the crew fan-out
        // below is the whole publish. The contract enum name is used verbatim for ContractId
        // and ContractType, matching what PublishContractResolved writes for the same round.

        /// <summary>
        /// The shared body for the nine LGU steps with no attributable actor. LGU's progress
        /// ClientRpcs carry the object, never the player who touched it, and #804 deliberately
        /// added no Contracted networking to these contracts, so there is nothing on the host
        /// that names a hand. Crediting the crew is the honest answer rather than picking one.
        /// </summary>
        private static void PublishLguCrewWide(
            MoonContractType contract,
            ContractProgressionEventKind kind,
            Func<string, ulong, string> composeId,
            string source,
            int count,
            bool succeeded)
        {
            string contractName = contract.ToString();
            string label = NormalizeLabel(source);
            string roundId = GetRoundId();
            string risk = GetRiskLevel();
            List<ulong> credited = Credit(EnumerateParticipants(), out bool crewWide);
            foreach (ulong playerClientId in credited)
            {
                ContractProgressionNetwork.BroadcastServer(new ContractProgressionEvent
                {
                    SchemaVersion = CurrentSchemaVersion,
                    EventId = composeId(roundId, playerClientId),
                    Kind = kind,
                    PlayerClientId = playerClientId,
                    ContractId = contractName,
                    ContractType = contractName,
                    SourceLabel = label,
                    RiskLevel = risk,
                    Count = Mathf.Max(1, count),
                    ContractSucceeded = succeeded,
                    CrewWide = crewWide
                });
            }
        }

        internal static void PublishLguLaptopLogin()
        {
            PublishLguCrewWide(
                MoonContractType.LguDataRetrieval,
                ContractProgressionEventKind.LguLaptopLogin,
                ContractProgressionEventIds.LguLaptopLogin,
                "laptop login screen",
                1,
                false);
        }

        internal static void PublishLguDataRecovered()
        {
            PublishLguCrewWide(
                MoonContractType.LguDataRetrieval,
                ContractProgressionEventKind.LguDataRecovered,
                ContractProgressionEventIds.LguDataRecovered,
                "survey.db",
                1,
                true);
        }

        /// <summary>
        /// One credit per hoarding bug the cull accounts for. Attribution: the crew member the
        /// report's enemy-hit ledger last recorded against that bug, which is the same actor its
        /// own "hostiles killed" line uses; a bug lost to a landmine, an eyeless dog or the day
        /// ending has no hitter and pays the crew instead.
        /// </summary>
        internal static void PublishLguHoardingBugExterminated(int killIndex, int totalBugs, ulong actorClientId)
        {
            const MoonContractType Contract = MoonContractType.LguExtermination;
            string contractName = Contract.ToString();
            string roundId = GetRoundId();
            string risk = GetRiskLevel();
            int index = Mathf.Max(1, killIndex);
            string label = $"bug {index}/{Mathf.Max(index, totalBugs)}";
            bool attributed = IsCreditableActor(actorClientId);
            List<ulong> credited = Credit(ResolveCredited(actorClientId), out bool crewWide);
            foreach (ulong playerClientId in credited)
            {
                ContractProgressionNetwork.BroadcastServer(new ContractProgressionEvent
                {
                    SchemaVersion = CurrentSchemaVersion,
                    EventId = attributed
                        ? ContractProgressionEventIds.LguHoardingBugExterminated(roundId, index, playerClientId)
                        : ContractProgressionEventIds.LguHoardingBugExterminatedCrewWide(roundId, index, playerClientId),
                    Kind = ContractProgressionEventKind.LguHoardingBugExterminated,
                    PlayerClientId = playerClientId,
                    ContractId = contractName,
                    ContractType = contractName,
                    SourceLabel = label,
                    RiskLevel = risk,
                    Count = index,
                    CrewWide = crewWide
                });
            }
        }

        internal static void PublishLguNestDestroyed()
        {
            PublishLguCrewWide(
                MoonContractType.LguExtermination,
                ContractProgressionEventKind.LguNestDestroyed,
                ContractProgressionEventIds.LguNestDestroyed,
                "bug nest",
                1,
                true);
        }

        internal static void PublishLguScavengerTreated()
        {
            PublishLguCrewWide(
                MoonContractType.LguExtraction,
                ContractProgressionEventKind.LguScavengerTreated,
                ContractProgressionEventIds.LguScavengerTreated,
                "medkit",
                1,
                false);
        }

        internal static void PublishLguScavengerRecovered()
        {
            PublishLguCrewWide(
                MoonContractType.LguExtraction,
                ContractProgressionEventKind.LguScavengerRecovered,
                ContractProgressionEventIds.LguScavengerRecovered,
                "carried aboard",
                1,
                true);
        }

        /// <summary>One credit per item the altar accepted; <paramref name="placedIndex"/> keys it.</summary>
        internal static void PublishLguRitualItemPlaced(int placedIndex, int totalItems)
        {
            int index = Mathf.Max(1, placedIndex);
            int total = Mathf.Max(index, totalItems);
            PublishLguCrewWide(
                MoonContractType.LguExorcism,
                ContractProgressionEventKind.LguRitualItemPlaced,
                (roundId, playerClientId) => ContractProgressionEventIds.LguRitualItemPlaced(roundId, index, playerClientId),
                $"item {index}/{total}",
                index,
                index >= total);
        }

        internal static void PublishLguDemonIdentified(string demonName)
        {
            PublishLguCrewWide(
                MoonContractType.LguExorcism,
                ContractProgressionEventKind.LguDemonIdentified,
                ContractProgressionEventIds.LguDemonIdentified,
                demonName,
                1,
                false);
        }

        /// <summary>One credit per wire the device accepted; <paramref name="cutIndex"/> keys it.</summary>
        internal static void PublishLguWireCut(int cutIndex, int totalWires)
        {
            int index = Mathf.Max(1, cutIndex);
            int total = Mathf.Max(index, totalWires);
            PublishLguCrewWide(
                MoonContractType.LguDefusal,
                ContractProgressionEventKind.LguWireCut,
                (roundId, playerClientId) => ContractProgressionEventIds.LguWireCut(roundId, index, playerClientId),
                $"wire {index}/{total}",
                index,
                false);
        }

        internal static void PublishLguDeviceDefused(string serial)
        {
            PublishLguCrewWide(
                MoonContractType.LguDefusal,
                ContractProgressionEventKind.LguDeviceDefused,
                ContractProgressionEventIds.LguDeviceDefused,
                serial,
                1,
                true);
        }

        // --- #1195: side objectives (not contract events) -------------------------------------
        //
        // Published by the host only, from SideObjectiveWesleyInteriorSource. ContractId and
        // ContractType stay empty on purpose: a side objective runs on rounds with no contract, and
        // naming whatever contract happens to be active would credit it to that contract. The
        // attributed and crew-wide id shapes follow the extermination cull: an actor the host could
        // name is credited alone with a plain id, anyone else goes to the crew with `.crew.` ids.
        // CrewWide (#1201) is decided like every other fan-out, through Credit: an attributed step
        // credits one player and is never crew-wide; a `.crew.` fan-out is crew-wide only when it
        // reached every participant, so a solo round never is.

        /// <summary>The <paramref name="step"/>th step of the objective was reached for the first time this round.</summary>
        internal static void PublishSideObjectiveStepCompleted(string source, string entry, string title, int step, ulong actorClientId)
        {
            int index = Mathf.Max(1, step);
            PublishSideObjective(
                ContractProgressionEventKind.SideObjectiveStepCompleted,
                (roundId, playerClientId, crew) => ContractProgressionEventIds.SideObjectiveStepCompleted(roundId, source, entry, index, playerClientId, crew),
                title,
                index,
                false,
                actorClientId);
        }

        internal static void PublishSideObjectiveCompleted(string source, string entry, string title, int required, ulong actorClientId)
        {
            PublishSideObjective(
                ContractProgressionEventKind.SideObjectiveCompleted,
                (roundId, playerClientId, crew) => ContractProgressionEventIds.SideObjectiveCompleted(roundId, source, entry, playerClientId, crew),
                title,
                Mathf.Max(1, required),
                true,
                actorClientId);
        }

        internal static void PublishSideObjectiveRewardClaimed(string source, string entry, string title, ulong actorClientId)
        {
            PublishSideObjective(
                ContractProgressionEventKind.SideObjectiveRewardClaimed,
                (roundId, playerClientId, crew) => ContractProgressionEventIds.SideObjectiveRewardClaimed(roundId, source, entry, playerClientId, crew),
                title,
                1,
                true,
                actorClientId);
        }

        private static void PublishSideObjective(
            ContractProgressionEventKind kind,
            Func<string, ulong, bool, string> composeId,
            string title,
            int count,
            bool succeeded,
            ulong actorClientId)
        {
            string label = NormalizeLabel(title);
            string roundId = GetRoundId();
            string risk = GetRiskLevel();
            bool attributed = IsCreditableActor(actorClientId);
            List<ulong> credited = Credit(ResolveCredited(actorClientId), out bool crewWide);
            foreach (ulong playerClientId in credited)
            {
                ContractProgressionNetwork.BroadcastServer(new ContractProgressionEvent
                {
                    SchemaVersion = CurrentSchemaVersion,
                    EventId = composeId(roundId, playerClientId, !attributed),
                    Kind = kind,
                    PlayerClientId = playerClientId,
                    ContractId = string.Empty,
                    ContractType = string.Empty,
                    SourceLabel = label,
                    RiskLevel = risk,
                    Count = count,
                    ContractSucceeded = succeeded,
                    CrewWide = crewWide
                });
            }
        }

        internal static void PublishContractResolved(MoonContractType contract, bool succeeded, string source)
        {
            if (contract == MoonContractType.None)
                return;

            string contractName = contract.ToString();
            string label = NormalizeLabel(source);
            string roundId = GetRoundId();
            string risk = GetRiskLevel();
            List<ulong> credited = Credit(EnumerateParticipants(), out bool crewWide);
            foreach (ulong playerClientId in credited)
            {
                ContractProgressionNetwork.BroadcastServer(new ContractProgressionEvent
                {
                    SchemaVersion = CurrentSchemaVersion,
                    EventId = ContractProgressionEventIds.ContractResolved(roundId, contractName, succeeded, playerClientId),
                    Kind = succeeded
                        ? ContractProgressionEventKind.ContractCompleted
                        : ContractProgressionEventKind.ContractFailed,
                    PlayerClientId = playerClientId,
                    ContractId = contractName,
                    ContractType = contractName,
                    SourceLabel = label,
                    RiskLevel = risk,
                    Count = 1,
                    ContractSucceeded = succeeded,
                    CrewWide = crewWide
                });
            }
        }

        /// <summary>
        /// #1201: materializes one occurrence's credited players and decides, once, whether the
        /// occurrence is a crew award (<see cref="ContractProgressionEventIds.IsCrewWide"/>). The
        /// list is built before any copy is published so every copy of the occurrence carries the
        /// same flag.
        /// </summary>
        private static List<ulong> Credit(IEnumerable<ulong> credited, out bool crewWide)
        {
            var list = new List<ulong>(credited);
            crewWide = list.Count >= 2
                && ContractProgressionEventIds.IsCrewWide(list, new List<ulong>(EnumerateParticipants()));
            return list;
        }

        internal static void Publish(ContractProgressionEvent progressionEvent)
        {
            if (progressionEvent == null
                || progressionEvent.SchemaVersion != CurrentSchemaVersion
                || string.IsNullOrWhiteSpace(progressionEvent.EventId))
            {
                // #406: a malformed event used to vanish without a trace. Say so at debug level -
                // a missing XP line is otherwise indistinguishable from an unwired sub-objective.
                ContractsBootstrap.Log?.LogDebug(
                    "[MoonContracts.Progression] Dropped a progression event: null, wrong schema version, or empty event id.");
                return;
            }

            if (!ContractProgressionEventIds.IsAttributable(progressionEvent.PlayerClientId))
            {
                ContractsBootstrap.Log?.LogDebug(
                    $"[MoonContracts.Progression] Dropped '{progressionEvent.EventId}' ({progressionEvent.Kind}): "
                    + "no identifiable player (client id sentinel). A crew-wide fan-out was expected here.");
                return;
            }

            // The report ledger is fed from the same funnel the XP consumer patches, so the
            // end-of-round lines are exactly the sub-objectives that were rewarded.
            MoonContractRoundStats.RecordProgressionEvent(progressionEvent);

            try
            {
                Published?.Invoke(progressionEvent);
            }
            catch (Exception ex)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Progression] Event listener failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Client ids of every player who joined this session, including the dead: a contract
        /// outcome belongs to the whole crew, not just the survivors standing at the ship.
        /// </summary>
        internal static IEnumerable<ulong> EnumerateParticipants()
        {
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null)
                yield break;

            for (int i = 0; i < players.Length; i++)
            {
                PlayerControllerB player = players[i];
                if (player == null || (!player.isPlayerControlled && !player.isPlayerDead))
                    continue;
                if (player.actualClientId == ulong.MaxValue)
                    continue;

                yield return player.actualClientId;
            }
        }

        /// <summary>
        /// The identity every event id is scoped to. The transport scopes its replay ledger to
        /// this, so the ledger self-expires at the next round instead of needing a lifecycle
        /// callback that would fire while round-end events are still being published.
        /// </summary>
        internal static string CurrentRoundId()
        {
            return GetRoundId();
        }

        /// <summary>
        /// Players who are still alive right now. Wave survival is the one credit that a corpse
        /// did not earn, so it does not use <see cref="EnumerateParticipants"/>.
        /// </summary>
        internal static IEnumerable<ulong> EnumerateLivingParticipants()
        {
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null)
                yield break;

            for (int i = 0; i < players.Length; i++)
            {
                PlayerControllerB player = players[i];
                if (player == null || !player.isPlayerControlled || player.isPlayerDead)
                    continue;
                if (!ContractProgressionEventIds.IsAttributable(player.actualClientId))
                    continue;

                yield return player.actualClientId;
            }
        }

        /// <summary>
        /// The attribution fallback in one place: an identifiable actor who is still in the
        /// session is credited alone, otherwise the whole crew is.
        ///
        /// #406: the sentinel test alone was not enough. Every attributed publisher latches an
        /// actor when the deed starts and publishes when it finishes, and those two moments are
        /// separated by a burn cycle, a cage drop, or a breaker resolve. A latched id whose owner
        /// disconnected in that window is still non-sentinel, so it used to short-circuit here and
        /// ship an event addressed to a client that no longer exists - the credit landed on nobody.
        /// Validating the actor against the live roster at the one choke point covers waste, pest,
        /// the blackout latch, and any future attributed publisher.
        ///
        /// Dead is not the same as departed. A player who did the deed and then died is still a
        /// participant and keeps the credit - dying afterwards must not void what was already
        /// earned - which is why the liveness test is <see cref="IsCreditableActor"/> (built on
        /// <see cref="EnumerateParticipants"/>, which accepts corpses) and not
        /// <see cref="EnumerateLivingParticipants"/>. Only leaving the session reroutes the credit
        /// to the crew.
        /// </summary>
        private static IEnumerable<ulong> ResolveCredited(ulong actorClientId)
        {
            if (IsCreditableActor(actorClientId))
            {
                yield return actorClientId;
                yield break;
            }

            foreach (ulong playerClientId in EnumerateParticipants())
                yield return playerClientId;
        }

        /// <summary>
        /// True when <paramref name="actorClientId"/> names a real player who is still in this
        /// session, alive or dead. False for the unknown-player sentinel and for anyone who
        /// disconnected, which is what routes their credit to the crew-wide fan-out.
        ///
        /// The roster test matches <see cref="EnumerateParticipants"/> exactly, so a credited
        /// actor is always someone the fallback would also have credited. Vanilla clears both
        /// <c>isPlayerControlled</c> and <c>isPlayerDead</c> on the slot a player vacates, so that
        /// pair is the connectivity signal; a corpse still has <c>isPlayerDead</c> set and passes.
        /// When the roster cannot be read at all (no <c>StartOfRound</c>) the actor is kept: an
        /// unreadable roster disproves nothing, and rejecting here would fall through to a
        /// fan-out that is equally empty and would publish nothing at all.
        /// </summary>
        internal static bool IsCreditableActor(ulong actorClientId)
        {
            if (!ContractProgressionEventIds.IsAttributable(actorClientId))
                return false;

            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null || players.Length == 0)
                return true;

            foreach (ulong playerClientId in EnumerateParticipants())
            {
                if (playerClientId == actorClientId)
                    return true;
            }

            return false;
        }

        internal static ulong ResolveClientId(PlayerControllerB player)
        {
            return player != null ? player.actualClientId : ulong.MaxValue;
        }

        private static string NormalizeLabel(string source)
        {
            return string.IsNullOrWhiteSpace(source) ? string.Empty : source.Trim();
        }

        // #716/B13: the round id is asked for once per event, and once per frame per client by
        // ContractProgressionNetwork.RequestReplay's round-change check. Seed and level only
        // change at a round boundary, so the two ints are the identity and the string is a
        // derived value: compare the ints, rebuild the string only when they move.
        private static int _cachedRoundIdSeed = int.MinValue;
        private static int _cachedRoundIdLevel = int.MinValue;
        private static string _cachedRoundId = string.Empty;

        private static string GetRoundId()
        {
            StartOfRound start = StartOfRound.Instance;
            int seed = start != null ? start.randomMapSeed : 0;
            int level = start?.currentLevel != null ? start.currentLevel.levelID : -1;
            if (seed != _cachedRoundIdSeed || level != _cachedRoundIdLevel)
            {
                _cachedRoundIdSeed = seed;
                _cachedRoundIdLevel = level;
                _cachedRoundId = $"{seed}.{level}";
            }

            return _cachedRoundId;
        }

        private static string GetRiskLevel()
        {
            string risk = StartOfRound.Instance?.currentLevel?.riskLevel;
            return string.IsNullOrWhiteSpace(risk) ? "C" : risk.Trim().ToUpperInvariant();
        }
    }
}
