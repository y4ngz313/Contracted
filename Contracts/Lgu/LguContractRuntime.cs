using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using UnityEngine;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts._Shared;
using Y4NGZCompany.Core.Compatibility;

namespace Y4NGZCompany.Contracts.Lgu
{
    /// <summary>
    /// #804. Objectives, HUD text, CCTV markers and run state for the five Lategame
    /// Upgrades-backed directives.
    ///
    /// Lategame Upgrades spawns and plays these contracts; Contracted only reads them. Every
    /// value on this page is derived on the peer that renders it, from state LGU has already
    /// replicated: its progress ClientRpcs execute on every machine, so the postfixes in
    /// <see cref="LategameUpgradesContractObjects"/> latch the same flags everywhere without a
    /// single byte of Contracted networking, and the live objects supply the rest. No
    /// NetworkBehaviour, no weavable file, nothing that throws when LGU is absent - with LGU
    /// missing every latch stays false and every object lookup returns null, which renders as an
    /// untouched objective list.
    ///
    /// The one thing local derivation cannot do is reconstruct a round a peer arrived late to.
    /// Four bytes and an optional serial ride along on the existing assignment snapshot for
    /// that; see <see cref="GetSnapshotPayload"/>.
    /// </summary>
    internal static class LguContractRuntime
    {
        /// <summary>Distance from an objective object before it counts as found. Matches the
        /// Defuse case's own locate radius so "Locate" means the same thing on every card.</summary>
        private const float LocateDistanceMeters = 6f;

        /// <summary>Enemy LGU releases for an extermination contract, spelled as
        /// <c>ContractObject.Start</c> spells it for <c>Tools.SpawnMob</c>.</summary>
        private const string HoardingBugEnemyName = "Hoarding bug";

        /// <summary>Sentinel for a byte field the host could not fill in.</summary>
        internal const byte UnknownByte = byte.MaxValue;

        [Flags]
        private enum ProgressFlags : byte
        {
            None = 0,
            LoginOpened = 1 << 0,
            DataRecovered = 1 << 1,
            NestDestroyed = 1 << 2,
            ScavengerHealed = 1 << 3,
            DemonKnown = 1 << 4,
            RitualFailed = 1 << 5,
            SerialKnown = 1 << 6,
            DeviceDetonated = 1 << 7,
        }

        private static ProgressFlags _flags;

        // Per-client reading aids, deliberately not replicated - the same call Defuse (#688) and
        // Pest Control (#672) made. A locate step is a note to the player holding the card, not
        // contract state, and none of them can stall completion: every one is implied by a later
        // step that is replicated.
        private static readonly bool[] Located = new bool[5];

        // #1197: the five locate rows' keys, indexed by LguContractObject, shared by the rows and
        // the pity timer's target read so the two can never name different rows.
        private static readonly string[] LocateKeys =
        {
            "lgu.data.locate",
            "lgu.extermination.locate",
            "lgu.extraction.locate",
            "lgu.exorcism.locate",
            "lgu.defusal.locate",
        };

        private static string _demonName;
        private static int _demonIndex = -1;
        private static int _ritualTotal = LategameUpgradesMembers.DefaultRitualItemCount;
        private static int _ritualRemaining = -1;
        private static bool _ritualComplete;

        /// <summary>
        /// True once an LGU ritual ClientRpc has executed on <em>this</em> peer and shown a
        /// populated <c>currentRitual</c>. Local only, never replicated and never derived from
        /// the snapshot trailer: a late joiner's list is empty because it missed the RPC, not
        /// because the ritual is finished, and the trailer's own count is that peer's only
        /// source of truth until an RPC is observed. See <see cref="LguProgressRules"/>.
        /// </summary>
        private static bool _ritualListObservedLocally;

        private static string _deviceSerial;
        private static int _wiresRemaining = -1;
        private static bool _wiresComplete;

        /// <summary>The same latch for the defusal wire order, which carries the extra hazard
        /// that LGU never clears <c>ContractManager.bombOrder</c> between rounds.</summary>
        private static bool _bombOrderObservedLocally;

        private static bool _scavengerDelivered;

        private static int _hoardingBugKills;
        private static readonly HashSet<int> CountedHoardingBugs = new HashSet<int>();

        internal static void ResetRunState()
        {
            _flags = ProgressFlags.None;
            for (int i = 0; i < Located.Length; i++)
                Located[i] = false;

            _demonName = null;
            _demonIndex = -1;
            _ritualTotal = LategameUpgradesMembers.DefaultRitualItemCount;
            _ritualRemaining = -1;
            _ritualComplete = false;
            _ritualListObservedLocally = false;

            _deviceSerial = null;
            _wiresRemaining = -1;
            _wiresComplete = false;
            _bombOrderObservedLocally = false;

            _scavengerDelivered = false;

            _hoardingBugKills = 0;
            CountedHoardingBugs.Clear();

            LategameUpgradesContractObjects.ResetRunState();
        }

        /// <summary>
        /// Per-frame tick, host and client alike: everything here is derived locally, so a
        /// client that skipped it would render a card that never moved. Cheap by construction -
        /// one cached component lookup (re-scanned at most once a second), one distance
        /// comparison, and for two of the five contracts one field read.
        /// </summary>
        internal static void UpdateRuntime(MoonContractType contract)
        {
            switch (contract)
            {
                case MoonContractType.LguDataRetrieval:
                    TrackLocate(LguContractObject.Laptop);
                    break;

                case MoonContractType.LguExtermination:
                    TrackLocate(LguContractObject.Nest);
                    break;

                case MoonContractType.LguExtraction:
                {
                    Component scavenger = TrackLocate(LguContractObject.Scavenger);
                    if (!_scavengerDelivered && scavenger != null)
                    {
                        GrabbableObject grabbable = scavenger.GetComponent<GrabbableObject>();
                        if (grabbable != null && grabbable.isInShipRoom)
                        {
                            _scavengerDelivered = true;
                            // Derived per peer from the item's own isInShipRoom, so every machine
                            // flips it; only the host turns that into a progression credit.
                            if (ShouldPublish(MoonContractType.LguExtraction))
                                ContractProgressionEvents.PublishLguScavengerRecovered();
                        }
                    }
                    break;
                }

                case MoonContractType.LguExorcism:
                {
                    Component pentagram = TrackLocate(LguContractObject.Pentagram);
                    // Gated on the local-observation latch, never on DemonKnown: the snapshot
                    // trailer sets DemonKnown for a late joiner, whose currentRitual is empty
                    // because it missed SyncAltarClientRpc - and an empty list polled here
                    // would otherwise read as a finished ritual and complete the contract with
                    // nothing placed. Until an RPC executes on this peer the trailer's own
                    // count is the only truth.
                    if (pentagram != null && _ritualListObservedLocally && !_ritualComplete)
                        ObserveRitual(pentagram, fromRpc: false);
                    break;
                }

                case MoonContractType.LguDefusal:
                {
                    Component device = TrackLocate(LguContractObject.Device);
                    // Same gate as the ritual, and for the same reason twice over: bombOrder is
                    // written only in SyncBombDetailsClientRpc's execute stage, and LGU clears
                    // neither it nor SerialNumber between rounds (ResetAllValues touches only
                    // contractType and contractLevel). A polled count is therefore either a
                    // late joiner's empty list or last round's leftovers until an RPC has
                    // executed here.
                    if (_bombOrderObservedLocally && !_wiresComplete)
                        ObserveBombProgress(fromRpc: false);

                    // The countdown is local to each peer and LGU never announces its expiry:
                    // BombDefusalScript.Update simply deactivates the device. A cached component
                    // whose object has gone inactive with wires still to cut is that detonation.
                    if (device != null
                        && !_wiresComplete
                        && !Has(ProgressFlags.DeviceDetonated)
                        && !device.gameObject.activeInHierarchy)
                    {
                        MarkDetonated("countdown expired");
                    }
                    break;
                }
            }
        }

        /// <summary>
        /// The objective list for an LGU-backed directive. The shared closing line is appended
        /// by <c>MoonContractState.GetObjectives</c>; nothing here authors a delivery row.
        /// </summary>
        internal static List<MoonContractObjective> GetObjectives(MoonContractType contract)
        {
            switch (contract)
            {
                case MoonContractType.LguDataRetrieval:
                    return GetDataRetrievalObjectives();
                case MoonContractType.LguExtermination:
                    return GetExterminationObjectives();
                case MoonContractType.LguExtraction:
                    return GetExtractionObjectives();
                case MoonContractType.LguExorcism:
                    return GetExorcismObjectives();
                case MoonContractType.LguDefusal:
                    return GetDefusalObjectives();
                default:
                    return new List<MoonContractObjective>();
            }
        }

        private static List<MoonContractObjective> GetDataRetrievalObjectives()
        {
            bool recovered = Has(ProgressFlags.DataRecovered);
            bool located = IsLocateRowComplete(LguContractObject.Laptop);

            return new List<MoonContractObjective>
            {
                new MoonContractObjective("Locate the laptop", located ? 1 : 0, 1, key: LocateKeys[(int)LguContractObject.Laptop]),
                // Both of the remaining steps complete on the same signal. LGU announces the
                // login screen opening and the successful exit, and nothing in between: a
                // correct password only activates a panel on the interacting player's own
                // screen. Splitting them keeps the card readable without inventing a signal.
                new MoonContractObjective("Log in to the laptop", recovered ? 1 : 0, 1, key: "lgu.data.login"),
                new MoonContractObjective("Recover the survey database", recovered ? 1 : 0, 1, key: "lgu.data.recover"),
            };
        }

        private static List<MoonContractObjective> GetExterminationObjectives()
        {
            bool destroyed = Has(ProgressFlags.NestDestroyed);
            bool located = IsLocateRowComplete(LguContractObject.Nest);
            int total = Mathf.Max(1, LategameUpgradesContractObjects.GetExterminationSpawnCount());
            // Destroying the nest closes the cull as well. LGU lets a crew clean the nest
            // whenever they like and a bug can be lost to a landmine, an eyeless dog or the
            // day ending; without this the count could sit one short forever and the card
            // would never reach its closing line.
            int killed = destroyed ? total : Mathf.Min(_hoardingBugKills, total);

            return new List<MoonContractObjective>
            {
                new MoonContractObjective("Locate the nest", located ? 1 : 0, 1, key: LocateKeys[(int)LguContractObject.Nest]),
                // Explicit key: the running total is a volatile payload and must not read as a
                // new objective on every kill (#520).
                new MoonContractObjective(
                    $"Exterminate the hoarding bugs ({killed}/{total})",
                    killed,
                    total,
                    key: "lgu.extermination.cull"),
                new MoonContractObjective("Destroy the nest", destroyed ? 1 : 0, 1, key: "lgu.extermination.destroy"),
            };
        }

        private static List<MoonContractObjective> GetExtractionObjectives()
        {
            bool healed = Has(ProgressFlags.ScavengerHealed);
            bool located = IsLocateRowComplete(LguContractObject.Scavenger);

            return new List<MoonContractObjective>
            {
                new MoonContractObjective("Locate the scavenger", located ? 1 : 0, 1, key: LocateKeys[(int)LguContractObject.Scavenger]),
                new MoonContractObjective("Treat the scavenger with a medkit", healed ? 1 : 0, 1, key: "lgu.extraction.treat"),
                new MoonContractObjective("Carry the scavenger to the ship", _scavengerDelivered ? 1 : 0, 1, key: "lgu.extraction.carry"),
            };
        }

        private static List<MoonContractObjective> GetExorcismObjectives()
        {
            bool failed = Has(ProgressFlags.RitualFailed);
            bool located = IsLocateRowComplete(LguContractObject.Pentagram);
            int total = Mathf.Max(1, _ritualTotal);
            int placed = _ritualComplete
                ? total
                : _ritualRemaining < 0 ? 0 : Mathf.Clamp(total - _ritualRemaining, 0, total);

            return new List<MoonContractObjective>
            {
                new MoonContractObjective("Locate the pentagram", located ? 1 : 0, 1, key: LocateKeys[(int)LguContractObject.Pentagram]),
                // Completes on the ritual, not on the label. The demon's name comes from an
                // optional reflected member, and a status line that waits for it would let one
                // unresolved field stop the whole card from ever closing; the Defuse case's
                // timer line settles the same way. The text still says UNKNOWN until it is read.
                new MoonContractObjective(
                    "DEMON · " + (string.IsNullOrEmpty(_demonName) ? "UNKNOWN" : _demonName.ToUpperInvariant()),
                    _ritualComplete ? 1 : 0,
                    1,
                    kind: MoonContractObjectiveKind.Status,
                    key: "lgu.exorcism.demon"),
                // A wrong item calls ghost girls onto the crew, so the failure is lethal.
                new MoonContractObjective(
                    $"Place the ritual items ({placed}/{total})",
                    placed,
                    total,
                    failed,
                    key: "lgu.exorcism.ritual",
                    lethal: failed),
            };
        }

        private static List<MoonContractObjective> GetDefusalObjectives()
        {
            bool detonated = Has(ProgressFlags.DeviceDetonated);
            bool located = IsLocateRowComplete(LguContractObject.Device);
            int total = LategameUpgradesMembers.BombWireCount;
            int cut = _wiresComplete
                ? total
                : _wiresRemaining < 0 ? 0 : Mathf.Clamp(total - _wiresRemaining, 0, total);

            return new List<MoonContractObjective>
            {
                new MoonContractObjective("Locate the device", located ? 1 : 0, 1, key: LocateKeys[(int)LguContractObject.Device]),
                // Both status lines close on the outcome rather than on their own label, so an
                // unresolved optional member costs the detail and never the completion.
                new MoonContractObjective(
                    "SERIAL · " + (string.IsNullOrEmpty(_deviceSerial) ? "PENDING" : _deviceSerial.ToUpperInvariant()),
                    _wiresComplete ? 1 : 0,
                    1,
                    kind: MoonContractObjectiveKind.Status,
                    key: "lgu.defusal.serial"),
                // The deadline is never failed on its own: a detonation is one event and the
                // wire line below carries it, lethal tier and all. Marking it twice would put
                // two failures on the card for one bomb.
                new MoonContractObjective(
                    BuildDeadlineText(detonated),
                    _wiresComplete ? 1 : 0,
                    1,
                    kind: MoonContractObjectiveKind.Status,
                    key: "lgu.defusal.timer"),
                new MoonContractObjective(
                    $"Cut the wires ({cut}/{total})",
                    cut,
                    total,
                    detonated,
                    key: "lgu.defusal.wires",
                    lethal: detonated),
            };
        }

        /// <summary>The device's own countdown, mirroring the Defuse case's wording for the two
        /// states where a number would be a lie.</summary>
        private static string BuildDeadlineText(bool detonated)
        {
            if (detonated)
                return "DEADLINE · DEVICE LOST";
            if (_wiresComplete)
                return "DEADLINE · SUSPENDED";

            Component device = LategameUpgradesContractObjects.FindActiveObject(LguContractObject.Device);
            float seconds = LategameUpgradesContractObjects.GetBombTimerSeconds(device);
            if (seconds < 0f)
                return "DEADLINE · --:--";

            int whole = Mathf.Max(0, Mathf.FloorToInt(seconds));
            return $"DEADLINE · {whole / 60:00}:{whole % 60:00}";
        }

        /// <summary>
        /// The CCTV blip for the active directive: the object's own transform, labelled with the
        /// noun the objective list uses. False when there is nothing to walk to yet.
        /// </summary>
        internal static bool TryGetCctvMarker(MoonContractType contract, out string label, out Vector3 position, out bool isComplete)
            => TryGetCctvMarker(contract, out label, out position, out isComplete, out _);

        internal static bool TryGetCctvMarker(MoonContractType contract, out string label, out Vector3 position, out bool isComplete, out Component target)
        {
            target = null;
            label = null;
            position = Vector3.zero;
            isComplete = false;

            LguContractObject kind;
            switch (contract)
            {
                case MoonContractType.LguDataRetrieval:
                    kind = LguContractObject.Laptop;
                    label = "LAPTOP";
                    isComplete = Has(ProgressFlags.DataRecovered);
                    break;
                case MoonContractType.LguExtermination:
                    kind = LguContractObject.Nest;
                    label = "NEST";
                    isComplete = Has(ProgressFlags.NestDestroyed);
                    break;
                case MoonContractType.LguExtraction:
                    kind = LguContractObject.Scavenger;
                    label = "SCAVENGER";
                    isComplete = _scavengerDelivered;
                    break;
                case MoonContractType.LguExorcism:
                    kind = LguContractObject.Pentagram;
                    label = "PENTAGRAM";
                    isComplete = _ritualComplete;
                    break;
                case MoonContractType.LguDefusal:
                    kind = LguContractObject.Device;
                    label = "DEVICE";
                    isComplete = _wiresComplete;
                    break;
                default:
                    return false;
            }

            target = LategameUpgradesContractObjects.FindActiveObject(kind);
            if (target == null || !target.gameObject.activeInHierarchy)
                return false;

            position = target.transform.position + Vector3.up * 0.45f;
            return true;
        }

        /// <summary>
        /// #1197. The directive's locate row for the pity timer: its key, whether it reads found
        /// (the same term the row itself uses), and the directive's object from the cache only.
        /// Never scans; the per-frame <see cref="TrackLocate"/> keeps that cache filled. False for
        /// a contract that is not an LGU directive.
        /// </summary>
        internal static bool TryGetLocateState(MoonContractType contract, out string rowKey, out bool located, out Component target)
        {
            rowKey = null;
            located = false;
            target = null;
            if (!TryGetObjectKind(contract, out LguContractObject kind))
                return false;

            rowKey = LocateKeys[(int)kind];
            located = IsLocateRowComplete(kind);
            target = LategameUpgradesContractObjects.GetCachedObject(kind);
            return true;
        }

        private static bool TryGetObjectKind(MoonContractType contract, out LguContractObject kind)
        {
            switch (contract)
            {
                case MoonContractType.LguDataRetrieval: kind = LguContractObject.Laptop; return true;
                case MoonContractType.LguExtermination: kind = LguContractObject.Nest; return true;
                case MoonContractType.LguExtraction: kind = LguContractObject.Scavenger; return true;
                case MoonContractType.LguExorcism: kind = LguContractObject.Pentagram; return true;
                case MoonContractType.LguDefusal: kind = LguContractObject.Device; return true;
                default: kind = LguContractObject.Laptop; return false;
            }
        }

        /// <summary>A locate row reads found once this client reached the object, or once a later,
        /// replicated step implies someone did.</summary>
        private static bool IsLocateRowComplete(LguContractObject kind)
        {
            if (IsLocated(kind))
                return true;
            switch (kind)
            {
                case LguContractObject.Laptop: return Has(ProgressFlags.LoginOpened) || Has(ProgressFlags.DataRecovered);
                case LguContractObject.Nest: return Has(ProgressFlags.NestDestroyed);
                case LguContractObject.Scavenger: return Has(ProgressFlags.ScavengerHealed);
                case LguContractObject.Pentagram: return Has(ProgressFlags.DemonKnown);
                case LguContractObject.Device: return Has(ProgressFlags.SerialKnown);
                default: return false;
            }
        }

        // ----- latches, called from the LGU ClientRpc postfixes -----------------------------
        //
        // Every one is a set-once write or a re-read of live state, never an increment: netcode
        // runs a ClientRpc body twice on the host (once to send, once to execute), so a counting
        // latch would double-count there.

        internal static void NotifyLaptopOpened(Component laptop)
        {
            Cache(LguContractObject.Laptop, laptop);
            PublishLoginOpened();
        }

        private static void PublishLoginOpened()
        {
            if (TrySet(ProgressFlags.LoginOpened) && ShouldPublish(MoonContractType.LguDataRetrieval))
                ContractProgressionEvents.PublishLguLaptopLogin();
        }

        /// <summary>
        /// <paramref name="succeeded"/> false is not a failure. LGU sends it for any exit from
        /// the login screen - the escape key included - and sends nothing at all for a wrong
        /// password, so treating it as one would void the contract for a player who simply
        /// backed out. Data Retrieval has no failure signal; the objective list has no failed
        /// state to match.
        /// </summary>
        internal static void NotifyLaptopExited(Component laptop, bool succeeded)
        {
            Cache(LguContractObject.Laptop, laptop);
            PublishLoginOpened();
            if (!succeeded)
                return;

            if (TrySet(ProgressFlags.DataRecovered) && ShouldPublish(MoonContractType.LguDataRetrieval))
                ContractProgressionEvents.PublishLguDataRecovered();
        }

        internal static void NotifyNestDestroyed()
        {
            if (TrySet(ProgressFlags.NestDestroyed) && ShouldPublish(MoonContractType.LguExtermination))
                ContractProgressionEvents.PublishLguNestDestroyed();
        }

        internal static void NotifyScavengerHealed(Component scavenger)
        {
            Cache(LguContractObject.Scavenger, scavenger);
            if (TrySet(ProgressFlags.ScavengerHealed) && ShouldPublish(MoonContractType.LguExtraction))
                ContractProgressionEvents.PublishLguScavengerTreated();
        }

        internal static void NotifyDemonChosen(Component pentagram, int index)
        {
            Cache(LguContractObject.Pentagram, pentagram);
            AdoptDemon(index);
            // The same RPC body fills currentRitual, so this is where the local-observation
            // latch is earned. On the host the send stage arrives first and still sees the
            // empty field initialiser; that reads as zero, which sets nothing.
            ObserveRitual(pentagram, fromRpc: true);
        }

        internal static void NotifyRitualItemPlaced(Component pentagram)
        {
            Cache(LguContractObject.Pentagram, pentagram);
            ObserveRitual(pentagram, fromRpc: true);
        }

        internal static void NotifyRitualFailed()
        {
            if (Has(ProgressFlags.RitualFailed))
                return;

            Set(ProgressFlags.RitualFailed);
            ContractsBootstrap.Log?.LogInfo("[MoonContracts.LGU] Exorcism ritual failed; a wrong item was placed on the altar.");
        }

        internal static void NotifyDeviceDetails(Component device, string serial)
        {
            Cache(LguContractObject.Device, device);
            // The serial arrives as the RPC's own argument, which is the only unambiguous
            // source there is: ContractManager.SerialNumber is a shared field that survives
            // the round, so it is read for comparison and never adopted.
            AdoptSerial(serial);
            ObserveBombProgress(fromRpc: true);
        }

        internal static void NotifyDeviceWireCut(Component device)
        {
            Cache(LguContractObject.Device, device);
            ObserveBombProgress(fromRpc: true);

            // A wrong cut leaves the order untouched and deactivates the device on the spot.
            // Only the execute stage sees that; the host's send-stage call finds it still
            // active and does nothing, which is correct - the execute stage follows.
            if (!_wiresComplete && device != null && !device.gameObject.activeInHierarchy)
                MarkDetonated("wrong wire");
        }

        /// <summary>
        /// Counts a hoarding bug's death for the extermination cull. Reached from the existing
        /// <c>EnemyAI.KillEnemy</c> postfix, which vanilla drives through
        /// <c>KillEnemyClientRpc</c> on every peer, so the count is derived identically
        /// everywhere. Deduplicated by instance id because <c>KillEnemy</c> can be re-entered
        /// for the same corpse.
        /// </summary>
        internal static void NotifyEnemyKilled(EnemyAI enemy)
        {
            if (enemy == null || enemy.enemyType == null)
                return;
            if (!string.Equals(enemy.enemyType.enemyName, HoardingBugEnemyName, StringComparison.OrdinalIgnoreCase))
                return;
            if (MoonContractState.GetCurrentContract() != MoonContractType.LguExtermination)
                return;
            if (!CountedHoardingBugs.Add(enemy.GetInstanceID()))
                return;

            _hoardingBugKills++;

            // The one LGU step with an actor: the report's enemy-hit ledger already names who
            // last struck this bug, and that is the player its own "hostiles killed" line would
            // credit. A bug nobody hit resolves to the sentinel and pays the crew.
            if (!NetRole.IsServerOrHost())
                return;

            // Clamped to the same total the objective row uses. The raw counter is allowed to
            // run past it - LGU's own spawn count is a configured target, and a nest can be
            // reinforced or a stray bug can wander in - but the cull is only worth N credits,
            // so a 25th kill on a 20-bug contract must neither publish nor advance the index
            // the published id is keyed on.
            int total = Mathf.Max(1, LategameUpgradesContractObjects.GetExterminationSpawnCount());
            if (_hoardingBugKills > total)
                return;

            ContractProgressionEvents.PublishLguHoardingBugExterminated(
                _hoardingBugKills,
                total,
                MoonContractRoundStats.ResolveEnemyKillerClientId(enemy));
        }

        // ----- late join --------------------------------------------------------------------

        /// <summary>
        /// True when <paramref name="contract"/> is one this page tracks, so the trailer has a
        /// shape to carry. It is not a test for progress: an all-zero payload is written from
        /// the moment an LGU directive is assigned, which is what lets a late joiner tell
        /// "nothing has happened yet" apart from "no trailer arrived".
        /// </summary>
        internal static bool HasSnapshotPayload(MoonContractType contract)
        {
            return MoonContractLguBridge.IsLguBacked(contract);
        }

        /// <summary>
        /// The host's view of this round's LGU progress, as four bytes and an optional serial.
        ///
        /// The bitmask covers everything a peer learns from an RPC it may have missed. The three
        /// counters are here because LGU's own per-peer copies are useless to a late joiner:
        /// <c>currentRitual</c> and <c>ContractManager.bombOrder</c> are written by ClientRpcs,
        /// so a peer that joined afterwards holds an empty list, which reads as "finished"
        /// rather than "not started".
        /// </summary>
        internal static void GetSnapshotPayload(
            out byte flags,
            out byte demonIndex,
            out byte ritualRemaining,
            out byte wiresRemaining,
            out string serial)
        {
            flags = (byte)_flags;
            demonIndex = _demonIndex >= 0 && _demonIndex < UnknownByte ? (byte)_demonIndex : UnknownByte;
            ritualRemaining = _ritualComplete
                ? (byte)0
                : _ritualRemaining >= 0 && _ritualRemaining < UnknownByte ? (byte)_ritualRemaining : UnknownByte;
            wiresRemaining = _wiresComplete
                ? (byte)0
                : _wiresRemaining >= 0 && _wiresRemaining < UnknownByte ? (byte)_wiresRemaining : UnknownByte;
            serial = _deviceSerial;
        }

        /// <summary>
        /// Folds the host's payload into this peer's latches. Bits are OR-ed and counters only
        /// ever move forward, so a snapshot that arrives after the peer has seen the RPCs itself
        /// - the broadcast repeats every five seconds - can never walk progress backwards.
        ///
        /// Because it only ever raises, the caller must have established that the trailer is
        /// this round's and this contract's; <c>MoonContractState.ReadLguProgressTrailer</c>
        /// does that from the type and round stamp the host writes alongside these bytes.
        /// Nothing here sets a local-observation latch: the flags say what the host saw, not
        /// what this peer's copy of LGU has filled in.
        /// </summary>
        internal static void ApplySnapshotPayload(
            byte flags,
            byte demonIndex,
            byte ritualRemaining,
            byte wiresRemaining,
            string serial)
        {
            _flags |= (ProgressFlags)flags;

            if (demonIndex != UnknownByte && _demonIndex < 0)
                AdoptDemon(demonIndex);

            if (ritualRemaining != UnknownByte && (_ritualRemaining < 0 || ritualRemaining < _ritualRemaining))
                ApplyRitualRemaining(ritualRemaining);

            if (!string.IsNullOrEmpty(serial))
                AdoptSerial(serial);

            if (wiresRemaining != UnknownByte && (_wiresRemaining < 0 || wiresRemaining < _wiresRemaining))
                ApplyWiresRemaining(wiresRemaining);
        }

        // ----- internals --------------------------------------------------------------------

        private static Component TrackLocate(LguContractObject kind)
        {
            Component target = LategameUpgradesContractObjects.FindActiveObject(kind);
            if (target == null || Located[(int)kind])
                return target;

            PlayerControllerB player = GameNetworkManager.Instance != null
                ? GameNetworkManager.Instance.localPlayerController
                : null;
            if (player == null || player.isPlayerDead || !player.isPlayerControlled)
                return target;

            Vector3 offset = player.transform.position - target.transform.position;
            if (offset.sqrMagnitude <= LocateDistanceMeters * LocateDistanceMeters)
                Located[(int)kind] = true;

            return target;
        }

        private static void Cache(LguContractObject kind, Component instance)
        {
            if (instance == null)
                return;

            // The postfix already holds the live component, so a scan is pointless work; seeding
            // the cache here also means the objective card can render on the very frame LGU
            // announced the object, rather than up to a second later.
            LategameUpgradesContractObjects.Seed(kind, instance);
        }

        /// <summary>
        /// Folds LGU's live ritual list into the latches under the rules in
        /// <see cref="LguProgressRules"/>. <paramref name="fromRpc"/> is true only on the
        /// <c>SyncAltarClientRpc</c> / <c>SyncCurrentRitualClientRpc</c> postfix paths, which
        /// are the one place a peer can prove it is looking at a list LGU filled in for it;
        /// that proof is what later licenses trusting a count of zero.
        /// </summary>
        private static void ObserveRitual(Component pentagram, bool fromRpc)
        {
            int remaining = LategameUpgradesContractObjects.GetRitualRemaining(pentagram);
            if (fromRpc && LguProgressRules.ObservesPopulatedList(remaining))
                _ritualListObservedLocally = true;

            if (LguProgressRules.IsCountTrustworthy(remaining, _ritualListObservedLocally))
                ApplyRitualRemaining(remaining);
        }

        /// <summary>
        /// The same shape for the defusal wire order, with the extra serial test: LGU rewrites
        /// <c>bombOrder</c> and <c>SerialNumber</c> together and clears neither at round end, so
        /// a wire count arriving with a serial other than the one latched this round is the
        /// previous round's residue. A serial that cannot be read at all is not evidence and
        /// does not veto the count - the local-observation latch already scopes it.
        /// </summary>
        private static void ObserveBombProgress(bool fromRpc)
        {
            if (!LategameUpgradesCompat.TryGetBombProgress(out int remaining, out string serial))
                return;
            if (LguProgressRules.IsStaleSerial(_deviceSerial, serial))
                return;

            if (fromRpc && LguProgressRules.ObservesPopulatedList(remaining))
                _bombOrderObservedLocally = true;

            if (LguProgressRules.IsCountTrustworthy(remaining, _bombOrderObservedLocally))
                ApplyWiresRemaining(remaining);
        }

        private static void AdoptDemon(int index)
        {
            if (index < 0 || _demonIndex == index)
                return;

            _demonIndex = index;
            if (LategameUpgradesContractObjects.TryGetDemon(index, out string name, out int itemCount))
            {
                _demonName = name;
                _ritualTotal = Mathf.Max(1, itemCount);
            }
            else
            {
                _ritualTotal = LategameUpgradesMembers.DefaultRitualItemCount;
            }

            if (_ritualRemaining < 0)
                _ritualRemaining = _ritualTotal;
            if (TrySet(ProgressFlags.DemonKnown) && ShouldPublish(MoonContractType.LguExorcism))
                ContractProgressionEvents.PublishLguDemonIdentified(_demonName);
        }

        private static void ApplyRitualRemaining(int remaining)
        {
            if (remaining < 0)
                return;
            if (_ritualRemaining >= 0 && remaining > _ritualRemaining)
                return;

            int total = Mathf.Max(1, _ritualTotal);
            int before = _ritualRemaining < 0 ? 0 : Mathf.Clamp(total - _ritualRemaining, 0, total);

            _ritualRemaining = remaining;
            if (remaining == 0)
                _ritualComplete = true;

            // One credit per item the altar actually took. The loop matters because a single
            // read can cross more than one item: the counter is re-read off the live list rather
            // than incremented, so a peer whose first read lands late sees the jump.
            if (!ShouldPublish(MoonContractType.LguExorcism))
                return;

            int after = Mathf.Clamp(total - remaining, 0, total);
            for (int placed = before + 1; placed <= after; placed++)
                ContractProgressionEvents.PublishLguRitualItemPlaced(placed, total);
        }

        private static void AdoptSerial(string serial)
        {
            if (string.IsNullOrEmpty(serial) || string.Equals(_deviceSerial, serial, StringComparison.Ordinal))
                return;

            _deviceSerial = serial;
            Set(ProgressFlags.SerialKnown);
            if (_wiresRemaining < 0)
                _wiresRemaining = LategameUpgradesMembers.BombWireCount;
        }

        private static void ApplyWiresRemaining(int remaining)
        {
            if (remaining < 0 || remaining > LategameUpgradesMembers.BombWireCount)
                return;
            if (_wiresRemaining >= 0 && remaining > _wiresRemaining)
                return;

            int total = LategameUpgradesMembers.BombWireCount;
            int before = _wiresRemaining < 0 ? 0 : Mathf.Clamp(total - _wiresRemaining, 0, total);

            _wiresRemaining = remaining;
            if (remaining == 0)
                _wiresComplete = true;

            if (!ShouldPublish(MoonContractType.LguDefusal))
                return;

            int after = Mathf.Clamp(total - remaining, 0, total);
            for (int cut = before + 1; cut <= after; cut++)
                ContractProgressionEvents.PublishLguWireCut(cut, total);

            // The device is defused when the last wire goes; a separate credit because it is the
            // objective, not the third cut.
            if (after >= total && before < total)
                ContractProgressionEvents.PublishLguDeviceDefused(_deviceSerial);
        }

        private static void MarkDetonated(string reason)
        {
            if (Has(ProgressFlags.DeviceDetonated))
                return;

            Set(ProgressFlags.DeviceDetonated);
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.LGU] Defusal device detonated ({reason}).");
        }

        private static bool IsLocated(LguContractObject kind) => Located[(int)kind];

        private static bool Has(ProgressFlags flag) => (_flags & flag) != 0;

        private static void Set(ProgressFlags flag) => _flags |= flag;

        /// <summary>Sets <paramref name="flag"/> and reports whether this call is the one that
        /// set it. The progression publishers below hang off that transition: netcode runs a
        /// ClientRpc body twice on the host, and a latch that already held would otherwise
        /// publish the same step twice.
        ///
        /// The callers spell this <c>TrySet(f) &amp;&amp; ShouldPublish(x)</c>, so the latch is
        /// taken before the publish gate is consulted and a transition reached while the
        /// assignment does not match forfeits its progression credit permanently. That is
        /// deliberate: the flags are this peer's objective-card state and every peer must set
        /// them, while <see cref="ShouldPublish"/> is host-only. Testing the gate first would
        /// leave every client's card blank, which is a far worse bug than an uncredited step on
        /// a round where Contracted did not assign that directive in the first place.</summary>
        private static bool TrySet(ProgressFlags flag)
        {
            if (Has(flag))
                return false;

            _flags |= flag;
            return true;
        }

        // ----- progression publishing (#805) -------------------------------------------------
        //
        // Host only, and only while the matching directive is the one Contracted assigned: with
        // LGU's own shop left unsuppressed a crew can buy a contract Contracted never issued,
        // and its RPCs reach these same latches. The publishers themselves fan out crew-wide,
        // because an LGU progress ClientRpc names the object and never the hand (see
        // ContractProgressionEvents).

        private static bool ShouldPublish(MoonContractType expected)
        {
            return NetRole.IsServerOrHost() && MoonContractState.GetCurrentContract() == expected;
        }
    }
}
