using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GameNetcodeStuff;
using UnityEngine;

using Y4NGZCompany.Bootstrap;
namespace Y4NGZCompany.Contracts._Shared
{
    internal static class MoonContractRoundStats
    {
        internal const int MaxNotesPerPlayer = 3;
        // Hard ceiling on a rendered note line, decoration included.
        internal const int NoteWordLimit = 12;
        // #555: the bullet the report renders in front of every note line. ASCII on purpose
        // - the endgame report draws in the vanilla bitmap terminal face, and a typographic
        // bullet - "*" was the old one, a small right-pointing triangle the obvious upgrade -
        // cannot be confirmed present in that atlas from outside the game. A missing glyph
        // renders as a blank box on every line of every slot, so this stays ASCII.
        internal const string NoteBullet = ">";
        // Sits below Combat (84) and above Exposure (58): a contributed employee fact
        // fills an empty slot but never outranks a death, objective, scrap or kill note.
        private const int MaximumExternalNotePriority = 64;
        private const float StepMeters = 0.76f;
        private const float ThreatSampleIntervalSeconds = 0.6f;
        private const float ThreatRememberSeconds = 14f;
        private const float ThreatAttributionDistance = 18f;
        private static readonly Dictionary<ulong, PlayerRoundStats> PlayerStatsByClientId = new Dictionary<ulong, PlayerRoundStats>();
        private static readonly Dictionary<GrabbableObject, ulong> FirstScrapDiscovererByObject = new Dictionary<GrabbableObject, ulong>();
        private static readonly Dictionary<GrabbableObject, ulong> LastScrapHolderByObject = new Dictionary<GrabbableObject, ulong>();
        private static readonly Dictionary<int, ulong> LastEnemyHitByClientId = new Dictionary<int, ulong>();
        private static readonly HashSet<int> CountedKilledEnemies = new HashSet<int>();
        private static readonly List<string> ObjectiveFacts = new List<string>();
        private static readonly HashSet<string> ConsumedProgressionEventIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static int _snapshotShipScrapValue;
        private static int _snapshotCasualties;
        private static int _snapshotEnemyKills;
        /// <summary>#944 S11 U6: the last summary line actually printed, so the host's double
        /// EndOfGameClientRpc invocation prints it once. Cleared by <see cref="Reset"/>.</summary>
        private static string _lastCapturedSummary;
        private static float _nextThreatSampleRealtime;
        // Frozen copy of every stem the note authors below can emit, in the same order.
        // BuildNoteCatalogAudit measures these, so a reworded line here and a reworded
        // line down there must move together or the audit stops describing the report.
        // Stems are held to seven words so the two-word " (45 XP)" decoration still
        // lands inside the twelve-word rendering budget - see PerformanceReportXpAmounts.
        private static readonly string[] NoteCatalogSamples =
        {
            "Disconnected before extraction.",
            "Died to Bracken inside.",
            "Died inside; cause unknown.",
            "4 defuse inputs, 2 mistakes.",
            "3 defuse inputs, no mistakes.",
            "Piloted the cart 2m 37s",
            "$206 scrap delivered.",
            "$206 scrap found.",
            "2 hostiles killed.",
            "2m 37s on Shadow Raid.",
            "2m 37s on Blackout Audit.",
            "540 steps surveyed.",
            "2m 37s chasing Whistleblower leads.",
            "2m 37s through the breach.",
            "2m 37s on Pest Control.",
            "2m 37s hauling waste.",
            "2m 37s on the Defuse site.",
            "2m 37s escorting the payload.",
            "2m 37s inside the facility.",
            "540 steps covered.",
            // Sub-objective stems. #1202: verb first, past tense, no trailing period, and a
            // repeat count inside the sentence except on the two kinds that kept a trailing xN.
            "Planted 3 survey beacons",
            "Cracked the container",
            "Survived wave 3",
            "Recovered the hard drive",
            "Fixed the incinerator x2",
            "Trapped 2 specimens",
            "Restored 2 breaker boxes",
            "Pulled the audit lever",
            "Crew placed 3 ritual items"
        };

        internal static void Reset(string reason = null)
        {
            PlayerStatsByClientId.Clear();
            FirstScrapDiscovererByObject.Clear();
            LastScrapHolderByObject.Clear();
            LastEnemyHitByClientId.Clear();
            CountedKilledEnemies.Clear();
            ObjectiveFacts.Clear();
            ConsumedProgressionEventIds.Clear();
            PerformanceReportNoteContributions.Clear(reason);
            PerformanceReportXpAmounts.Clear(reason);
            PerformanceReportXpLedger.Clear(reason);
            _snapshotShipScrapValue = 0;
            _snapshotCasualties = 0;
            _snapshotEnemyKills = 0;
            _nextThreatSampleRealtime = 0f;
            // #944 S11 U6: a new round may legitimately produce a byte-identical summary, so the
            // dedupe memory is cleared with the rest of the round state.
            _lastCapturedSummary = null;
            if (!string.IsNullOrWhiteSpace(reason))
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.ReportStats] Reset round stats ({reason}).");
        }

        internal static void UpdateRuntime()
        {
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null)
                return;

            float delta = Mathf.Clamp(Time.deltaTime, 0f, 0.25f);
            for (int i = 0; i < players.Length; i++)
                UpdatePlayer(players[i], delta);

            if (Time.realtimeSinceStartup >= _nextThreatSampleRealtime)
            {
                _nextThreatSampleRealtime = Time.realtimeSinceStartup + ThreatSampleIntervalSeconds;
                UpdateNearbyThreats(players);
            }
        }

        internal static void CaptureEndOfRound(int playersDead, int bodiesInsured, int playerCount)
        {
            UpdateRuntime();
            CapturePlayerEndStates();
            CaptureDeliveredScrapContributions();
            _snapshotShipScrapValue = Mathf.Max(0, MoonContractState.GetShipScrapValue());
            _snapshotCasualties = Mathf.Max(0, playersDead);
            _snapshotEnemyKills = PlayerStatsByClientId.Values.Sum(stats => Mathf.Max(0, stats.MonstersKilled));
            // #944 S11 U6: the second caller is netcode, not code. The only call site is the
            // StartOfRound.EndOfGameClientRpc prefix (MoonContractPatches.cs:920), and on the host a
            // ClientRpc method body runs twice - once as the send, once as the local invoke - so the
            // capture ran twice and printed the identical summary twice (run 1 lines 38294-38295,
            // 28 ms apart; run 2 lines 44870-44871, 27 ms apart). The capture itself is idempotent
            // and stays; only the line is deduplicated, and on the value it reports rather than on a
            // frame number, because the two invocations are not guaranteed to share a frame.
            string summary =
                "[MoonContracts.ReportStats] Captured round summary: " +
                $"players={Mathf.Max(1, playerCount)}, casualties={_snapshotCasualties}, insured={Mathf.Max(0, bodiesInsured)}, " +
                $"shipScrap=${_snapshotShipScrapValue}, kills={_snapshotEnemyKills}, objectiveFacts={ObjectiveFacts.Count}.";
            if (string.Equals(summary, _lastCapturedSummary, StringComparison.Ordinal))
                return;
            _lastCapturedSummary = summary;
            ContractsBootstrap.Log?.LogInfo(summary);
        }

        internal static IReadOnlyList<string> BuildPlayerNotes(
            MoonContractType contract,
            bool completed,
            int casualties,
            int revisedQuota)
        {
            // #555: whether the employee came back is stated first, then the economy, then
            // combat, then the softer presence/activity lines, because the report around it is
            // a ledger. Priority both orders the rendered lines and decides which survive the
            // Take(MaxNotesPerPlayer), so the tiers below encode that reading order:
            // survival (120/118) > scrap (100/96) > paid objectives (92-88) > kills (84) >
            // contributed employee facts (<=64) > presence (58-44) > movement (32).
            var indexedNotes = new List<IndexedPerformanceNote>();
            var players = PlayerStatsByClientId.Values
                .OrderBy(stats => stats.PlayerIndex)
                .ThenBy(stats => stats.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (players.Count == 0)
                return Array.Empty<string>();

            for (int i = 0; i < players.Count; i++)
            {
                PlayerRoundStats stats = players[i];
                // One line per category keeps the slot varied, except for sub-objectives: those are
                // the itemised list, so each distinct one keeps its own line.
                List<string> lines = BuildPlayerNoteCandidates(stats, contract, completed, casualties, revisedQuota)
                    .OrderByDescending(candidate => candidate.Priority)
                    .ThenBy(candidate => candidate.Order)
                    .GroupBy(candidate => candidate.Category == NoteCategory.SubObjective
                        ? (object)candidate.Text
                        : candidate.Category)
                    .Select(group => group.First())
                    .OrderByDescending(candidate => candidate.Priority)
                    .ThenBy(candidate => candidate.Order)
                    .Select(candidate => candidate.Text)
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .Take(MaxNotesPerPlayer)
                    .ToList();

                string text = lines.Count > 0
                    ? string.Join("\n", lines.Take(MaxNotesPerPlayer))
                    : string.Empty;
                indexedNotes.Add(new IndexedPerformanceNote(stats.PlayerIndex, text));
            }

            int minimumSlotCount = StartOfRound.Instance?.allPlayerScripts?.Length ?? 0;
            return PerformanceReportNoteSlots.Build(minimumSlotCount, indexedNotes);
        }

        internal static string CalculateGrade(
            MoonContractType contract,
            bool hasContract,
            bool completed,
            bool hazardPayApplied,
            int casualties,
            int quotaBeforeModifiers,
            int revisedQuota,
            int directivePercent)
        {
            float score = 62f;
            if (hasContract)
                score += completed ? 18f : -18f;
            else
                score += 2f;

            int quotaBasis = Mathf.Max(1, quotaBeforeModifiers);
            float scrapRatio = Mathf.Clamp01(_snapshotShipScrapValue / (float)quotaBasis);
            score += scrapRatio * 18f;
            score -= Mathf.Max(0, casualties) * 12f;
            score += Mathf.Min(8f, _snapshotEnemyKills * 2f);
            score += Mathf.Min(8f, ObjectiveFacts.Count * 2f);
            if (hazardPayApplied)
                score += 4f;
            if (hasContract && directivePercent <= 0 && completed)
                score += 3f;
            if (revisedQuota < quotaBeforeModifiers)
                score += 2f;

            return ScoreToGrade(score);
        }

        internal static string BuildNoteCatalogAudit()
        {
            // Worst realistic decoration: a four-digit grant still costs exactly two words.
            string sampleSuffix = PerformanceReportXpAmounts.FormatSuffix(1250);
            int suffixWords = CountWords(sampleSuffix);

            var lines = new List<string>();
            int totalWords = 0;
            int maximum = 0;
            for (int i = 0; i < NoteCatalogSamples.Length; i++)
            {
                int words = CountWords(NoteCatalogSamples[i]);
                totalWords += words;
                maximum = Mathf.Max(maximum, words);
                lines.Add($"note.{i + 1:00} | words={words} | decorated={words + suffixWords} | {NoteCatalogSamples[i]}{sampleSuffix}");
            }

            float average = NoteCatalogSamples.Length == 0 ? 0f : totalWords / (float)NoteCatalogSamples.Length;
            // hardMax covers the WHOLE rendered line, decoration included: a line that will be
            // decorated has its prose capped at hardMax minus the suffix before the suffix is
            // appended, so nothing reaches the HUD over hardMax. That rule is what holds the
            // external path too - a note contributed by a sibling plugin carries no word limit
            // of its own, and this catalog cannot measure text it does not author.
            // decoratedMax below is the worst case for Company-authored stems only.
            lines.Add(
                $"SUMMARY | count={NoteCatalogSamples.Length} average={average:0.00} max={maximum} "
                + $"xpSuffixWords={suffixWords} decoratedMax={maximum + suffixWords} "
                + "targetAverage=3-6 targetMax=7 hardMax=12");
            return string.Join("\n", lines);
        }

        /// <summary>
        /// Turns a published sub-objective into a readable ledger line for the crew member it was
        /// credited to. Idempotent per <c>EventId</c>: the host publishes locally and mirrors the
        /// same event out, so a machine can legitimately see one event more than once.
        /// </summary>
        internal static void RecordProgressionEvent(ContractProgressionEvent progressionEvent)
        {
            if (progressionEvent == null || string.IsNullOrWhiteSpace(progressionEvent.EventId))
                return;

            // #1202: the wording lives in the engine-free PerformanceReportWording so the
            // regression suite can run it. What is resolved here is the count-independent merge
            // key (the line as it reads for a single act) plus the identity - kind, and the
            // audit-lever variant - the note build renders from.
            bool auditLever = PerformanceReportWording.IsAuditLeverSource(progressionEvent.SourceLabel);
            string key = PerformanceReportWording.MergeKey(progressionEvent.Kind, auditLever);
            if (string.IsNullOrWhiteSpace(key))
                return;

            if (!ConsumedProgressionEventIds.Add(progressionEvent.EventId))
                return;

            // #1201 (Lawson, 2026-09-22): a crew award - every player in the round received this
            // same occurrence - is not something this player did, so it writes no line on any
            // peer for any slot. Its XP still counts in the shift total; the ledger keeps it out
            // of the remainder. A kind with both attributed and crew occurrences therefore shows
            // only the attributed count. Solo rounds are never crew-wide, so they show everything.
            if (progressionEvent.CrewWide)
                return;

            PlayerRoundStats stats = GetOrCreatePlayerStats(progressionEvent.PlayerClientId);
            if (stats == null)
                return;

            ContractProgressionEventKind kind = progressionEvent.Kind;

            // Wave survival is a high-water mark, not a tally: surviving waves 1-3 is one
            // achievement ("Survived wave 3"), and three separate lines would swallow the
            // whole three-line slot. Every other kind accumulates into "{label} x{n}".
            if (IsHighWaterSubObjective(kind))
            {
                SubObjectiveCredit wave = stats.SubObjectives.FirstOrDefault(credit => credit.Kinds.Contains(kind));
                int reached = Mathf.Max(1, progressionEvent.Count);
                if (wave == null)
                {
                    stats.SubObjectives.Add(new SubObjectiveCredit(kind, auditLever, key, reached, true));
                    return;
                }

                if (reached <= wave.Count)
                    return;

                wave.Count = reached;
                return;
            }

            // Merge by key, not by kind: PestControlCompleted and PestControlCapture both read
            // "Trapped a specimen", so the second capture turns that one line into "Trapped 2
            // specimens" instead of writing a near-duplicate beside it. The merged line remembers
            // every kind that fed it so its XP decoration can sum them.
            SubObjectiveCredit existing = stats.SubObjectives
                .FirstOrDefault(credit => string.Equals(credit.Key, key, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                // #794: a completion marker that shares a line - the full blackout restore
                // that follows the last breaker - joins the line for its XP but is not another
                // act of it, so it does not raise the count.
                if (!PerformanceReportWording.IsCompletionMarker(kind))
                    existing.Count++;
                existing.Kinds.Add(kind);
                return;
            }

            stats.SubObjectives.Add(new SubObjectiveCredit(
                kind, auditLever, key, 1, PerformanceReportWording.CountIsInsideLabel(kind)));
        }

        private static bool IsHighWaterSubObjective(ContractProgressionEventKind kind)
        {
            return kind == ContractProgressionEventKind.ContainmentBreachWaveSurvived;
        }

        private static string AppendSource(string label, string source)
        {
            string trimmed = string.IsNullOrWhiteSpace(source) ? string.Empty : source.Trim();
            if (trimmed.Length == 0 || string.Equals(trimmed, "host zone snapshot", StringComparison.OrdinalIgnoreCase))
                return label;

            return $"{label} ({trimmed})";
        }

        internal static void RecordBlackoutBreakerRestored(int restoredCount, int totalCount)
        {
            AddObjectiveFact($"* Breakers restored: {Mathf.Max(0, restoredCount)}/{Mathf.Max(1, totalCount)}");
        }

        internal static void RecordSurveyDronePlaced(int placedCount, int totalCount)
        {
            AddObjectiveFact($"* Survey beacons placed: {Mathf.Max(0, placedCount)}/{Mathf.Max(1, totalCount)}");
        }

        internal static void RecordDefuseWireCut(PlayerControllerB player, bool accepted, int wireSlot)
        {
            PlayerRoundStats stats = GetOrCreatePlayerStats(player);
            if (stats != null)
            {
                stats.DefuseWireCuts++;
                if (!accepted)
                    stats.DefuseMistakes++;
            }

            AddObjectiveFact(accepted
                ? $"* Wire {Mathf.Max(1, wireSlot + 1)} accepted"
                : $"* Wire {Mathf.Max(1, wireSlot + 1)} rejected");
        }

        internal static void RecordDefuseCodeSubmitted(PlayerControllerB player, bool accepted)
        {
            PlayerRoundStats stats = GetOrCreatePlayerStats(player);
            if (stats != null)
            {
                stats.DefuseCodeSubmits++;
                if (!accepted)
                    stats.DefuseMistakes++;
            }

            AddObjectiveFact(accepted ? "* Defuse code accepted" : "* Defuse code denied");
        }

        internal static void RecordPayloadPilotSeconds(PlayerControllerB player, float seconds)
        {
            PlayerRoundStats stats = GetOrCreatePlayerStats(player);
            if (stats != null)
                stats.PayloadPilotSeconds += Mathf.Max(0f, seconds);
        }

        internal static void RecordScrapGrabbed(GrabbableObject item)
        {
            if (!IsValuableScrap(item) || item.playerHeldBy == null)
                return;

            PlayerRoundStats stats = GetOrCreatePlayerStats(item.playerHeldBy);
            if (stats == null)
                return;

            LastScrapHolderByObject[item] = stats.ClientId;
            if (FirstScrapDiscovererByObject.ContainsKey(item))
                return;

            FirstScrapDiscovererByObject[item] = stats.ClientId;
            stats.ScrapDiscoveredValue += Mathf.Max(0, item.scrapValue);
        }

        internal static void RecordEnemyHit(EnemyAI enemy, PlayerControllerB player)
        {
            if (enemy == null || player == null)
                return;

            PlayerRoundStats stats = GetOrCreatePlayerStats(player);
            if (stats == null)
                return;

            LastEnemyHitByClientId[enemy.GetInstanceID()] = stats.ClientId;
        }

        /// <summary>
        /// #805: the crew member the enemy-hit ledger last recorded against <paramref name="enemy"/>,
        /// or the unknown-player sentinel. This is the same ledger the report's own "hostiles
        /// killed" line is built on, so the LGU extermination cull credits exactly the player the
        /// kill would already have been shown against. A bug lost to a landmine, an eyeless dog or
        /// the day ending was never hit by anyone and returns the sentinel, which routes the
        /// credit to the crew.
        /// </summary>
        internal static ulong ResolveEnemyKillerClientId(EnemyAI enemy)
        {
            if (enemy == null)
                return ContractProgressionEventIds.UnknownPlayerClientId;

            return LastEnemyHitByClientId.TryGetValue(enemy.GetInstanceID(), out ulong clientId)
                ? clientId
                : ContractProgressionEventIds.UnknownPlayerClientId;
        }

        internal static void NotifyEnemyKilled(EnemyAI enemy)
        {
            if (enemy == null)
                return;

            int enemyId = enemy.GetInstanceID();
            if (!CountedKilledEnemies.Add(enemyId))
                return;

            if (LastEnemyHitByClientId.TryGetValue(enemyId, out ulong clientId)
                && PlayerStatsByClientId.TryGetValue(clientId, out PlayerRoundStats stats))
            {
                stats.MonstersKilled++;
                AddObjectiveFact("* Monster neutralized");
            }
            else
            {
                _snapshotEnemyKills++;
                AddObjectiveFact("* Monster neutralized");
            }
        }

        private static void UpdatePlayer(PlayerControllerB player, float deltaSeconds)
        {
            PlayerRoundStats stats = GetOrCreatePlayerStats(player);
            if (stats == null || player == null || player.transform == null)
                return;

            stats.Name = GetPlayerName(player);
            if (!player.isPlayerControlled || player.isPlayerDead)
            {
                stats.HasLastPosition = false;
                return;
            }

            if (player.isInsideFactory)
                stats.TimeInsideFactorySeconds += deltaSeconds;

            Vector3 position = player.transform.position;
            stats.LastAliveInsideFactory = player.isInsideFactory;
            stats.LastKnownPosition = position;
            stats.HasLastKnownPosition = true;
            if (stats.HasLastPosition)
            {
                float distance = Vector3.Distance(position, stats.LastPosition);
                if (distance > 0.02f && distance < 8f)
                {
                    stats.DistanceMovedMeters += distance;
                    stats.StepsTaken = Mathf.FloorToInt(stats.DistanceMovedMeters / StepMeters);
                }
            }

            stats.LastPosition = position;
            stats.HasLastPosition = true;
        }

        private static PlayerRoundStats GetOrCreatePlayerStats(PlayerControllerB player)
        {
            if (player == null)
                return null;

            ulong clientId = player.actualClientId;
            if (!PlayerStatsByClientId.TryGetValue(clientId, out PlayerRoundStats stats))
            {
                stats = new PlayerRoundStats
                {
                    ClientId = clientId,
                    PlayerIndex = player.playerClientId > int.MaxValue ? int.MaxValue : (int)player.playerClientId,
                    Name = GetPlayerName(player),
                };
                PlayerStatsByClientId[clientId] = stats;
            }

            return stats;
        }

        private static PlayerRoundStats GetOrCreatePlayerStats(ulong clientId)
        {
            if (clientId == ulong.MaxValue)
                return null;
            if (PlayerStatsByClientId.TryGetValue(clientId, out PlayerRoundStats existing))
                return existing;

            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null)
                return null;

            for (int i = 0; i < players.Length; i++)
            {
                if (players[i] != null && players[i].actualClientId == clientId)
                    return GetOrCreatePlayerStats(players[i]);
            }

            return null;
        }

        private static void CapturePlayerEndStates()
        {
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null)
                return;

            for (int i = 0; i < players.Length; i++)
            {
                PlayerControllerB player = players[i];
                if (player == null || (!player.isPlayerControlled && !player.isPlayerDead && !player.disconnectedMidGame))
                    continue;

                PlayerRoundStats stats = GetOrCreatePlayerStats(player);
                if (stats == null)
                    continue;

                stats.Name = GetPlayerName(player);
                stats.Died = player.isPlayerDead;
                stats.Disconnected = player.disconnectedMidGame;
                if (stats.Died)
                {
                    stats.HasDeathLocation = true;
                    stats.DiedInsideFactory = player.isInsideFactory || stats.LastAliveInsideFactory;
                    stats.DeathEnemyName = ResolveLikelyDeathEnemyName(player, stats);
                }
            }
        }

        private static string GetPlayerName(PlayerControllerB player)
        {
            if (player == null)
                return "UNKNOWN";

            if (!string.IsNullOrWhiteSpace(player.playerUsername))
                return player.playerUsername.Trim();

            return $"P{player.playerClientId + 1}";
        }

        private static void UpdateNearbyThreats(PlayerControllerB[] players)
        {
            if (players == null)
                return;

            for (int i = 0; i < players.Length; i++)
            {
                PlayerControllerB player = players[i];
                if (player == null || !player.isPlayerControlled || player.isPlayerDead || player.transform == null)
                    continue;

                PlayerRoundStats stats = GetOrCreatePlayerStats(player);
                if (stats == null)
                    continue;

                if (TryFindClosestEnemyName(player.transform.position, ThreatAttributionDistance, out string enemyName))
                {
                    stats.LastThreatName = enemyName;
                    stats.LastThreatRealtime = Time.realtimeSinceStartup;
                }
            }
        }

        private static string ResolveLikelyDeathEnemyName(PlayerControllerB player, PlayerRoundStats stats)
        {
            if (stats == null)
                return string.Empty;

            Vector3 position = stats.HasLastKnownPosition
                ? stats.LastKnownPosition
                : (player != null && player.transform != null ? player.transform.position : Vector3.zero);
            if (stats.HasLastKnownPosition
                && TryFindClosestEnemyName(position, ThreatAttributionDistance, out string currentEnemyName))
            {
                return currentEnemyName;
            }

            if (!string.IsNullOrWhiteSpace(stats.LastThreatName)
                && Time.realtimeSinceStartup - stats.LastThreatRealtime <= ThreatRememberSeconds)
            {
                return stats.LastThreatName;
            }

            return string.Empty;
        }

        private static bool TryFindClosestEnemyName(Vector3 position, float maxDistance, out string enemyName)
        {
            enemyName = string.Empty;
            List<EnemyAI> enemies = RoundManager.Instance?.SpawnedEnemies;
            if (enemies == null || enemies.Count == 0)
                return false;

            float bestDistance = Mathf.Max(1f, maxDistance);
            EnemyAI bestEnemy = null;
            for (int i = 0; i < enemies.Count; i++)
            {
                EnemyAI enemy = enemies[i];
                if (enemy == null || enemy.isEnemyDead || enemy.transform == null)
                    continue;

                float distance = Vector3.Distance(position, enemy.transform.position);
                if (distance > bestDistance)
                    continue;

                string name = GetEnemyDisplayName(enemy);
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                bestDistance = distance;
                bestEnemy = enemy;
                enemyName = name;
            }

            return bestEnemy != null;
        }

        private static string GetEnemyDisplayName(EnemyAI enemy)
        {
            if (enemy == null)
                return string.Empty;

            // #716/E5: enemyName is the internal id ("Flowerman"), not the name the note
            // catalogue promises ("Bracken"). Unknown ids pass through unchanged.
            if (enemy.enemyType != null && !string.IsNullOrWhiteSpace(enemy.enemyType.enemyName))
                return EnemyDisplayNames.Resolve(enemy.enemyType.enemyName);

            return string.Empty;
        }

        internal static IEnumerable<PerformanceReportNoteEntry> BuildXpNoteCandidates(int playerIndex)
        {
            PlayerRoundStats stats = PlayerStatsByClientId.Values.FirstOrDefault(player => player.PlayerIndex == playerIndex);
            if (stats == null)
                yield break;
            // General XP sources come from the payout authority. Only Company knows the
            // objective counts, death and unpaid action facts. Do not repeat scrap or kills.
            var notes = new List<NoteCandidate>();
            int order = 0;
            AddSurvivalNoteCandidate(notes, stats, ref order);
            AddSubObjectiveNoteCandidates(notes, stats, ref order);
            if (stats.DefuseMistakes > 0)
                AddDefuseNoteCandidate(notes, stats, ref order);
            if (PerformanceReportXpAmounts.AmountFor(playerIndex, PerformanceReportXpAmounts.LineKeys.PayloadPilot) > 0)
                AddPayloadNoteCandidate(notes, stats, MoonContractType.Payload, true, ref order);
            foreach (NoteCandidate note in notes)
            {
                if (note.Category != NoteCategory.SubObjective && note.Category != NoteCategory.ContractObjective
                    && note.Category != NoteCategory.Survival)
                    continue;
                string label = note.Amount > 0
                    ? note.Text.Substring(0, note.Text.Length - PerformanceReportXpAmounts.FormatSuffix(note.Amount).Length)
                    : note.Text;
                yield return new PerformanceReportNoteEntry(label, note.Amount,
                    note.Category == NoteCategory.Survival ? 900 : note.Amount > 0 ? 110 : 40);
            }
        }

        private static IReadOnlyList<NoteCandidate> BuildPlayerNoteCandidates(
            PlayerRoundStats stats,
            MoonContractType contract,
            bool completed,
            int casualties,
            int revisedQuota)
        {
            var candidates = new List<NoteCandidate>();
            if (stats == null)
                return candidates;

            int order = 0;
            AddSurvivalNoteCandidate(candidates, stats, ref order);
            AddSubObjectiveNoteCandidates(candidates, stats, ref order);
            AddDefuseNoteCandidate(candidates, stats, ref order);
            AddPayloadNoteCandidate(candidates, stats, contract, completed, ref order);
            AddScrapNoteCandidate(candidates, stats, ref order);
            AddCombatNoteCandidate(candidates, stats, ref order);
            AddContractPresenceNoteCandidate(candidates, stats, contract, completed, ref order);
            AddInteriorNoteCandidate(candidates, stats, ref order);
            AddMovementNoteCandidate(candidates, stats, ref order);
            AddExternalNoteCandidates(candidates, stats, ref order);
            return candidates;
        }

        // Facts contributed by sibling plugins (Y4NGZUpgrades employee record). They
        // share one category so the best one fills a leftover slot instead of
        // evicting a contract fact derived here.
        private static void AddExternalNoteCandidates(List<NoteCandidate> candidates, PlayerRoundStats stats, ref int order)
        {
            IReadOnlyList<PerformanceReportNoteContributions.Contribution> contributions =
                PerformanceReportNoteContributions.ForPlayer(stats.PlayerIndex);
            for (int i = 0; i < contributions.Count; i++)
            {
                AddNoteCandidate(
                    candidates,
                    NoteCategory.EmployeeRecord,
                    Mathf.Clamp(contributions[i].Priority, 1, MaximumExternalNotePriority),
                    ref order,
                    stats.PlayerIndex,
                    PerformanceReportXpAmounts.LineKeys.EmployeeRecord,
                    contributions[i].Text);
            }
        }

        // Each rewarded sub-objective earns its own line rather than sharing one slot, so a crew
        // member who planted three drones and mounted the drill reads both back on the report.
        private static void AddSubObjectiveNoteCandidates(List<NoteCandidate> candidates, PlayerRoundStats stats, ref int order)
        {
            for (int i = 0; i < stats.SubObjectives.Count; i++)
            {
                SubObjectiveCredit credit = stats.SubObjectives[i];
                string text = PerformanceReportWording.BuildActLine(
                    NoteBullet, credit.LabelKind, credit.AuditLever, credit.Count, credit.CountIsInLabel);
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                // One line can be fed by more than one kind (the Pest Control captures), so
                // the decoration is the sum of every contributing kind's recorded XP.
                int amount = 0;
                foreach (ContractProgressionEventKind kind in credit.Kinds)
                {
                    amount += PerformanceReportXpAmounts.AmountFor(
                        stats.PlayerIndex,
                        PerformanceReportXpAmounts.SubObjective(kind.ToString()));
                }

                AddNoteCandidate(candidates, NoteCategory.SubObjective, 92, ref order, amount, text);
            }
        }

        private static void AddSurvivalNoteCandidate(List<NoteCandidate> candidates, PlayerRoundStats stats, ref int order)
        {
            // Death and disconnection are never XP-bearing, so they carry no line key: they
            // stay plain. They also outrank every economy line, because the slot is capped at
            // MaxNotesPerPlayer and a dead player whose death line was evicted by three scrap
            // and objective lines reads as though they walked back onto the ship.
            if (stats.Died)
            {
                AddNoteCandidate(candidates, NoteCategory.Survival, 120, ref order, 0, BuildDeathFlavorLine(stats));
                return;
            }

            if (stats.Disconnected)
            {
                AddNoteCandidate(
                    candidates,
                    NoteCategory.Survival,
                    118,
                    ref order,
                    0,
                    NoteBullet + " Disconnected before extraction.");
            }
        }

        private static string BuildDeathFlavorLine(PlayerRoundStats stats)
        {
            string location = stats.HasDeathLocation
                ? (stats.DiedInsideFactory ? "inside" : "outside")
                : "on site";
            string enemy = string.IsNullOrWhiteSpace(stats.DeathEnemyName) ? string.Empty : stats.DeathEnemyName.Trim();

            if (!string.IsNullOrWhiteSpace(enemy))
            {
                return $"{NoteBullet} Died to {enemy} {location}.";
            }

            return $"{NoteBullet} Died {location}; cause unknown.";
        }

        private static void AddDefuseNoteCandidate(List<NoteCandidate> candidates, PlayerRoundStats stats, ref int order)
        {
            int actions = Mathf.Max(0, stats.DefuseWireCuts) + Mathf.Max(0, stats.DefuseCodeSubmits);
            if (actions <= 0)
                return;

            if (stats.DefuseMistakes > 0)
            {
                AddNoteCandidate(
                    candidates,
                    NoteCategory.ContractObjective,
                    90,
                    ref order,
                    stats.PlayerIndex,
                    PerformanceReportXpAmounts.LineKeys.DefuseInputs,
                    $"{NoteBullet} {PerformanceReportWording.FormatCount(actions, "defuse input")}, {PerformanceReportWording.FormatCount(stats.DefuseMistakes, "mistake")}.");
                return;
            }

            AddNoteCandidate(
                candidates,
                NoteCategory.ContractObjective,
                88,
                ref order,
                stats.PlayerIndex,
                PerformanceReportXpAmounts.LineKeys.DefuseInputs,
                $"{NoteBullet} {PerformanceReportWording.FormatCount(actions, "defuse input")}, no mistakes.");
        }

        private static void AddCombatNoteCandidate(List<NoteCandidate> candidates, PlayerRoundStats stats, ref int order)
        {
            if (stats.MonstersKilled <= 0)
                return;

            AddNoteCandidate(
                candidates,
                NoteCategory.Combat,
                84,
                ref order,
                stats.PlayerIndex,
                PerformanceReportXpAmounts.LineKeys.CombatKills,
                $"{NoteBullet} {PerformanceReportWording.FormatCount(stats.MonstersKilled, "hostile")} killed.");
        }

        private static void AddScrapNoteCandidate(List<NoteCandidate> candidates, PlayerRoundStats stats, ref int order)
        {
            if (stats.ScrapDeliveredValue > 0)
            {
                AddNoteCandidate(
                    candidates,
                    NoteCategory.ScrapContribution,
                    100,
                    ref order,
                    stats.PlayerIndex,
                    PerformanceReportXpAmounts.LineKeys.ScrapDelivered,
                    $"{NoteBullet} ${stats.ScrapDeliveredValue.ToString(CultureInfo.InvariantCulture)} scrap delivered.");
                return;
            }

            if (stats.ScrapDiscoveredValue > 0)
            {
                AddNoteCandidate(
                    candidates,
                    NoteCategory.ScrapContribution,
                    96,
                    ref order,
                    stats.PlayerIndex,
                    PerformanceReportXpAmounts.LineKeys.ScrapDiscovered,
                    $"{NoteBullet} ${stats.ScrapDiscoveredValue.ToString(CultureInfo.InvariantCulture)} scrap found.");
            }
        }

        private static void AddPayloadNoteCandidate(
            List<NoteCandidate> candidates,
            PlayerRoundStats stats,
            MoonContractType contract,
            bool completed,
            ref int order)
        {
            if (contract != MoonContractType.Payload || !completed || stats.PayloadPilotSeconds < 1f)
                return;

            AddNoteCandidate(
                candidates,
                NoteCategory.ContractObjective,
                89,
                ref order,
                stats.PlayerIndex,
                PerformanceReportXpAmounts.LineKeys.PayloadPilot,
                PerformanceReportWording.BuildPayloadLine(NoteBullet, FormatDurationCompact(stats.PayloadPilotSeconds)));
        }

        private static void AddContractPresenceNoteCandidate(
            List<NoteCandidate> candidates,
            PlayerRoundStats stats,
            MoonContractType contract,
            bool completed,
            ref int order)
        {
            if (stats.TimeInsideFactorySeconds < 20f && stats.StepsTaken < 80)
                return;

            // #794 (Lawson, 2026-09-03): a crew member who earned a sub-objective line already
            // has the contract named on the report; the presence line was a second line about
            // the same work. One act, one line.
            if (stats.SubObjectives.Count > 0)
                return;

            string duration = FormatDurationCompact(stats.TimeInsideFactorySeconds);
            string key = PerformanceReportXpAmounts.LineKeys.ContractPresence;
            switch (contract)
            {
                case MoonContractType.ShadowRaid:
                    AddNoteCandidate(
                        candidates, NoteCategory.Exposure, 58, ref order, stats.PlayerIndex, key,
                        $"{NoteBullet} {duration} on Shadow Raid.");
                    break;
                case MoonContractType.BlackoutAudit:
                    AddNoteCandidate(
                        candidates, NoteCategory.Exposure, 58, ref order, stats.PlayerIndex, key,
                        $"{NoteBullet} {duration} on Blackout Audit.");
                    break;
                case MoonContractType.Survey:
                    AddNoteCandidate(
                        candidates, NoteCategory.Exposure, 56, ref order, stats.PlayerIndex, key,
                        $"{NoteBullet} {PerformanceReportWording.FormatCount(stats.StepsTaken, "step")} surveyed.");
                    break;
                case MoonContractType.Whistleblower:
                    AddNoteCandidate(
                        candidates, NoteCategory.Exposure, 56, ref order, stats.PlayerIndex, key,
                        $"{NoteBullet} {duration} chasing Whistleblower leads.");
                    break;
                case MoonContractType.ContainmentBreach:
                    AddNoteCandidate(
                        candidates, NoteCategory.Exposure, 56, ref order, stats.PlayerIndex, key,
                        $"{NoteBullet} {duration} through the breach.");
                    break;
                case MoonContractType.PestControl:
                    AddNoteCandidate(
                        candidates, NoteCategory.Exposure, 56, ref order, stats.PlayerIndex, key,
                        $"{NoteBullet} {duration} on Pest Control.");
                    break;
                case MoonContractType.WasteDisposal:
                    AddNoteCandidate(
                        candidates, NoteCategory.Exposure, 56, ref order, stats.PlayerIndex, key,
                        $"{NoteBullet} {duration} hauling waste.");
                    break;
                case MoonContractType.Defuse:
                    AddNoteCandidate(
                        candidates, NoteCategory.Exposure, 56, ref order, stats.PlayerIndex, key,
                        $"{NoteBullet} {duration} on the Defuse site.");
                    break;
                case MoonContractType.Payload:
                    AddNoteCandidate(
                        candidates, NoteCategory.Exposure, 56, ref order, stats.PlayerIndex, key,
                        $"{NoteBullet} {duration} escorting the payload.");
                    break;
                // #805: the five Lategame Upgrades-backed directives. These are the same
                // presence filler as their neighbours and not an attribution line: who did what
                // on an LGU contract is carried by the sub-objective credits above, which
                // suppress this line for anyone who earned one. What is left to say about a
                // crew member who earned none is that they were there, doing the job.
                case MoonContractType.LguDataRetrieval:
                    AddNoteCandidate(
                        candidates, NoteCategory.Exposure, 56, ref order, stats.PlayerIndex, key,
                        $"{NoteBullet} {duration} hunting the survey laptop.");
                    break;
                case MoonContractType.LguExtermination:
                    AddNoteCandidate(
                        candidates, NoteCategory.Exposure, 56, ref order, stats.PlayerIndex, key,
                        $"{NoteBullet} {duration} working the bug nest.");
                    break;
                case MoonContractType.LguExtraction:
                    AddNoteCandidate(
                        candidates, NoteCategory.Exposure, 56, ref order, stats.PlayerIndex, key,
                        $"{NoteBullet} {duration} searching for the scavenger.");
                    break;
                case MoonContractType.LguExorcism:
                    AddNoteCandidate(
                        candidates, NoteCategory.Exposure, 56, ref order, stats.PlayerIndex, key,
                        $"{NoteBullet} {duration} gathering ritual items.");
                    break;
                case MoonContractType.LguDefusal:
                    AddNoteCandidate(
                        candidates, NoteCategory.Exposure, 56, ref order, stats.PlayerIndex, key,
                        $"{NoteBullet} {duration} on the armed device.");
                    break;
            }
        }

        private static void AddInteriorNoteCandidate(List<NoteCandidate> candidates, PlayerRoundStats stats, ref int order)
        {
            if (stats.TimeInsideFactorySeconds < 30f)
                return;

            string duration = FormatDurationCompact(stats.TimeInsideFactorySeconds);
            AddNoteCandidate(
                candidates,
                NoteCategory.Exposure,
                44,
                ref order,
                stats.PlayerIndex,
                PerformanceReportXpAmounts.LineKeys.InteriorTime,
                $"{NoteBullet} {duration} inside the facility.");
        }

        private static void AddMovementNoteCandidate(List<NoteCandidate> candidates, PlayerRoundStats stats, ref int order)
        {
            if (stats.StepsTaken < 120)
                return;

            AddNoteCandidate(
                candidates,
                NoteCategory.Activity,
                32,
                ref order,
                stats.PlayerIndex,
                PerformanceReportXpAmounts.LineKeys.MovementSteps,
                $"{NoteBullet} {PerformanceReportWording.FormatCount(stats.StepsTaken, "step")} covered.");
        }

        /// <summary>
        /// Adds one note candidate, decorated with the XP a sibling plugin recorded for this
        /// player and line key. With nothing recorded (or a key nobody recognises) the line is
        /// stored exactly as authored, which is what makes the seam inert by default.
        /// </summary>
        private static void AddNoteCandidate(
            List<NoteCandidate> candidates,
            NoteCategory category,
            int priority,
            ref int order,
            int playerIndex,
            string lineKey,
            string text)
        {
            AddNoteCandidate(
                candidates,
                category,
                priority,
                ref order,
                PerformanceReportXpAmounts.AmountFor(playerIndex, lineKey),
                text);
        }

        /// <summary>
        /// Overload for lines whose amount is already resolved: sub-objective lines can be fed
        /// by several event kinds, and survival lines are deliberately never decorated.
        /// </summary>
        private static void AddNoteCandidate(
            List<NoteCandidate> candidates,
            NoteCategory category,
            int priority,
            ref int order,
            int amount,
            string text)
        {
            if (candidates == null || string.IsNullOrWhiteSpace(text))
                return;

            // A decorated line spends part of the twelve-word budget on its suffix, so the
            // prose is capped at twelve minus the suffix. This matters for text this repo did
            // not author: a contributed note arrives with no word limit of its own, and
            // capping it at twelve before appending would have rendered fourteen words.
            // Company's own stems top out at six, so the tighter cap never bites them.
            string suffix = amount > 0 ? PerformanceReportXpAmounts.FormatSuffix(amount) : string.Empty;
            int budget = NoteWordLimit - CountWords(suffix);
            string normalized = EnforceNoteWordLimit(NormalizeNoteBullet(text), budget) + suffix;

            candidates.Add(new NoteCandidate(category, priority, order++, normalized, amount));
        }

        /// <summary>
        /// Puts every candidate line behind the same <see cref="NoteBullet"/>. The authors in
        /// this file already emit it, so this is a no-op for them; it exists for text this repo
        /// did not author, which may still be written to the older "* " contract or carry no
        /// bullet at all. One slot must never render two different bullets. Only the leading
        /// glyph is rewritten - the contributed line's own words pass through verbatim.
        /// </summary>
        private static string NormalizeNoteBullet(string text)
        {
            string trimmed = (text ?? string.Empty).Trim();
            if (trimmed.Length == 0)
                return trimmed;

            if (trimmed.StartsWith(NoteBullet, StringComparison.Ordinal))
                trimmed = trimmed.Substring(NoteBullet.Length);
            // The escape rather than the glyph: this file is asserted to be free of exotic
            // bullet characters, because everything it emits has to exist in the report atlas.
            else if (trimmed[0] == '*' || trimmed[0] == '-' || trimmed[0] == '\u2022')
                trimmed = trimmed.Substring(1);

            trimmed = trimmed.TrimStart();
            return trimmed.Length == 0 ? string.Empty : NoteBullet + " " + trimmed;
        }

        private static string EnforceNoteWordLimit(string text, int limit = NoteWordLimit)
        {
            limit = Mathf.Max(1, limit);
            string[] words = text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length <= limit)
                return text;

            ContractsBootstrap.Log?.LogWarning(
                $"[MoonContracts.ReportStats] Performance note exceeded {limit} words: '{text}'");
            return string.Join(" ", words.Take(limit));
        }

        private static int CountWords(string text)
        {
            return string.IsNullOrWhiteSpace(text)
                ? 0
                : text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
        }

        private static int StableHash(string value)
        {
            unchecked
            {
                int hash = 17;
                if (!string.IsNullOrEmpty(value))
                {
                    for (int i = 0; i < value.Length; i++)
                        hash = hash * 31 + value[i];
                }

                return hash == int.MinValue ? 0 : Mathf.Abs(hash);
            }
        }

        private static string FormatDurationCompact(float seconds)
        {
            seconds = Mathf.Max(0f, seconds);
            int totalSeconds = Mathf.RoundToInt(seconds);
            int minutes = totalSeconds / 60;
            int remainingSeconds = totalSeconds % 60;
            if (minutes <= 0)
                return $"{remainingSeconds.ToString(CultureInfo.InvariantCulture)}s";
            return $"{minutes.ToString(CultureInfo.InvariantCulture)}m {remainingSeconds.ToString("00", CultureInfo.InvariantCulture)}s";
        }

        private static void AddObjectiveFact(string fact)
        {
            if (string.IsNullOrWhiteSpace(fact))
                return;

            fact = fact.Trim();
            if (ObjectiveFacts.Contains(fact, StringComparer.OrdinalIgnoreCase))
                return;

            ObjectiveFacts.Add(fact);
            if (ObjectiveFacts.Count > 24)
                ObjectiveFacts.RemoveAt(0);
        }

        private static void CaptureDeliveredScrapContributions()
        {
            foreach (PlayerRoundStats stats in PlayerStatsByClientId.Values)
                stats.ScrapDeliveredValue = 0;

            GrabbableObject[] scrap = UnityEngine.Object.FindObjectsOfType<GrabbableObject>();
            for (int i = 0; i < scrap.Length; i++)
            {
                GrabbableObject item = scrap[i];
                if (!IsValuableScrap(item) || (!item.isInShipRoom && !item.isInElevator))
                    continue;

                if (LastScrapHolderByObject.TryGetValue(item, out ulong clientId)
                    && PlayerStatsByClientId.TryGetValue(clientId, out PlayerRoundStats stats))
                {
                    stats.ScrapDeliveredValue += Mathf.Max(0, item.scrapValue);
                }
            }
        }

        private static bool IsValuableScrap(GrabbableObject item)
        {
            return item != null
                   && item.itemProperties != null
                   && item.itemProperties.isScrap
                   && item.scrapValue > 0;
        }

        private static string FormatInsideTime(float seconds)
        {
            seconds = Mathf.Max(0f, seconds);
            int totalSeconds = Mathf.RoundToInt(seconds);
            int minutes = totalSeconds / 60;
            int remainingSeconds = totalSeconds % 60;
            return $"{minutes.ToString(CultureInfo.InvariantCulture)}:{remainingSeconds.ToString("00", CultureInfo.InvariantCulture)}";
        }

        private static string ScoreToGrade(float score)
        {
            score = Mathf.Clamp(score, 0f, 100f);
            if (score >= 97f) return "S+";
            if (score >= 93f) return "S";
            if (score >= 90f) return "S-";
            if (score >= 87f) return "A+";
            if (score >= 83f) return "A";
            if (score >= 80f) return "A-";
            if (score >= 77f) return "B+";
            if (score >= 73f) return "B";
            if (score >= 70f) return "B-";
            if (score >= 67f) return "C+";
            if (score >= 63f) return "C";
            if (score >= 60f) return "C-";
            if (score >= 57f) return "D+";
            if (score >= 53f) return "D";
            if (score >= 50f) return "D-";
            return "F";
        }

        private sealed class PlayerRoundStats
        {
            internal ulong ClientId;
            internal int PlayerIndex;
            internal string Name;
            internal float TimeInsideFactorySeconds;
            internal int StepsTaken;
            internal float DistanceMovedMeters;
            internal int MonstersKilled;
            internal int DefuseWireCuts;
            internal int DefuseCodeSubmits;
            internal int DefuseMistakes;
            internal float PayloadPilotSeconds;
            internal int ScrapDiscoveredValue;
            internal int ScrapDeliveredValue;
            internal bool Died;
            internal bool Disconnected;
            internal bool LastAliveInsideFactory;
            internal bool HasLastKnownPosition;
            internal Vector3 LastKnownPosition;
            internal bool HasDeathLocation;
            internal bool DiedInsideFactory;
            internal string DeathEnemyName;
            internal string LastThreatName;
            internal float LastThreatRealtime;
            internal bool HasLastPosition;
            internal Vector3 LastPosition;
            internal readonly List<SubObjectiveCredit> SubObjectives = new List<SubObjectiveCredit>();
        }

        private sealed class SubObjectiveCredit
        {
            // #1202: the count-independent merge key (the line as it reads for one act). The
            // rendered wording is rebuilt from LabelKind and Count at note-build time, so it
            // cannot be stored here: a merge changes it.
            internal readonly string Key;
            internal readonly ContractProgressionEventKind LabelKind;
            // #794: the audit-lever restore reads as its own act, unlike every other
            // BlackoutAuditRestored, so the variant travels with the credit.
            internal readonly bool AuditLever;
            internal int Count;
            // Suppresses the "xN" suffix for credits whose Count the label itself carries: the
            // wave survival milestone, and every kind that counts inside its sentence (#1202).
            internal readonly bool CountIsInLabel;
            // Every event kind that merged into this line, so the XP decoration can sum the
            // amounts recorded under each kind's line key.
            internal readonly HashSet<ContractProgressionEventKind> Kinds =
                new HashSet<ContractProgressionEventKind>();

            internal SubObjectiveCredit(
                ContractProgressionEventKind kind, bool auditLever, string key, int count, bool countIsInLabel)
            {
                Key = key;
                LabelKind = kind;
                AuditLever = auditLever;
                Count = count;
                CountIsInLabel = countIsInLabel;
                Kinds.Add(kind);
            }
        }

        private enum NoteCategory
        {
            Survival,
            SubObjective,
            ContractObjective,
            ScrapContribution,
            Combat,
            Exposure,
            Activity,
            EmployeeRecord
        }

        private readonly struct NoteCandidate
        {
            internal readonly NoteCategory Category;
            internal readonly int Priority;
            internal readonly int Order;
            internal readonly string Text;
            internal readonly int Amount;

            internal NoteCandidate(NoteCategory category, int priority, int order, string text, int amount)
            {
                Category = category;
                Priority = priority;
                Order = order;
                Text = text;
                Amount = amount;
            }
        }
    }
}
