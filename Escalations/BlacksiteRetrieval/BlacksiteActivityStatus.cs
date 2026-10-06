namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #959 S13: what the Blacksite squad is doing, as one word the alert belt can print.
    ///
    /// This whole file is free of UnityEngine and Unity.Netcode on purpose. The ladder, the
    /// hysteresis and the wording are the parts worth pinning, so they are a pure core the
    /// headless <c>tests/ContractHudPresentationRegression</c> compiles directly. The mission
    /// controller is the only adapter: it reads the replicated state, hands this file primitives,
    /// and publishes the accepted answer on one <c>NetworkVariable&lt;byte&gt;</c>.
    ///
    /// Wire format: this enum travels as that byte, so members are APPENDED, never renumbered.
    /// </summary>
    internal enum BlacksiteActivityStatus : byte
    {
        /// <summary>No status published yet (a client before the host's first accepted tick).</summary>
        None = 0,
        /// <summary>Their ship is inbound, or the mission has not deployed anyone.</summary>
        Inbound = 1,
        /// <summary>Their ship is down and the squad is leaving it.</summary>
        Deploying = 2,
        /// <summary>The squad is crossing into the facility.</summary>
        Breaching = 3,
        /// <summary>The squad does not know where the objective is.</summary>
        Searching = 4,
        /// <summary>The squad knows where it is and is on its way.</summary>
        Closing = 5,
        /// <summary>A member is standing at the objective.</summary>
        AtObjective = 6,
        /// <summary>A member is working the container lock.</summary>
        Overriding = 7,
        /// <summary>A member holds the objective, still inside.</summary>
        Taken = 8,
        /// <summary>A member holds the objective and is outside with it.</summary>
        Hauling = 9,
        /// <summary>No longer emitted (#874 S6): the manifest has no winching phase. Kept for the wire.</summary>
        Loading = 10,
        /// <summary>The objective is aboard their ship.</summary>
        Loaded = 11,
        /// <summary>The crew has it, or it is in the crew ship, and the raid is still live.</summary>
        Contested = 12,
        /// <summary>The squad has been ordered off the moon.</summary>
        Withdrawing = 13,
        /// <summary>Terminal: they left with it.</summary>
        Extracted = 14,
        /// <summary>Terminal: the crew stopped them.</summary>
        Denied = 15,
        /// <summary>Terminal: the raid was called off.</summary>
        Aborted = 16,
        /// <summary>
        /// #966 S14 U4: the objective was in their hands and is not any more, and they cannot take
        /// it again yet. Only the witness escort can reach it today: it is fed from the custody
        /// re-take backoff, which no other objective kind has.
        /// </summary>
        Slipped = 17,
        // 18 was WithdrawingNoRoute, the squad rule's withdrawal, deleted with it (#1271 S1). Not reused.
    }

    /// <summary>
    /// #959 S13: the replicated integers this file interprets, named.
    ///
    /// Every value here mirrors a member of an enum that is itself wire format and therefore
    /// appended-only: <see cref="BlacksiteRetrievalMissionState"/>, <see cref="BlacksiteHardDriveState"/>
    /// and <see cref="BlacksiteRetrievalOutcome"/> ride <c>NetworkVariable&lt;int&gt;</c>s on the
    /// mission controller, <c>BlacksiteShipPhase</c> rides a <c>NetworkVariable&lt;byte&gt;</c> on
    /// the ship, and <c>BlacksiteSquadOrderKind</c> is the host's own order vocabulary. They are
    /// mirrored rather than referenced because those enums live in files that carry Vector3,
    /// NetworkObject and MonoBehaviour types, which a headless fixture cannot compile; mirroring
    /// is what keeps the ladder testable at all.
    /// </summary>
    internal static class BlacksiteActivityWire
    {
        // BlacksiteRetrievalMissionState
        internal const int MissionScheduled = 0;
        internal const int MissionInbound = 1;
        internal const int MissionDeploying = 2;
        internal const int MissionInfiltrating = 3;
        internal const int MissionApproachingExpectedDrive = 4;
        internal const int MissionOverriding = 5;
        internal const int MissionInspecting = 6;
        internal const int MissionTrackingMissingDrive = 7;
        internal const int MissionRecoveringDrive = 8;
        internal const int MissionEscortingCarrier = 9;
        internal const int MissionExtracting = 10;
        internal const int MissionRecovered = 11;
        internal const int MissionDenied = 12;
        internal const int MissionAborted = 13;

        // BlacksiteHardDriveState
        internal const int DriveAtExpectedTarget = 0;
        internal const int DriveLoose = 1;
        internal const int DrivePlayerHeld = 2;
        internal const int DriveSoldierHeld = 3;
        internal const int DriveInShip = 4;
        internal const int DriveExtracted = 5;
        internal const int DriveMissing = 6;

        // BlacksiteRetrievalOutcome
        internal const int OutcomePending = 0;
        internal const int OutcomeRecovered = 1;
        internal const int OutcomeDenied = 2;
        internal const int OutcomeAborted = 3;

        // BlacksiteShipPhase
        internal const int ShipInbound = 0;
        internal const int ShipLanded = 1;
        internal const int ShipBoarding = 2;
        internal const int ShipDeparting = 3;
        internal const int ShipDeparted = 4;
        internal const int ShipLeavingRendezvous = 5;
        internal const int ShipOffsite = 6;
        internal const int ShipReturning = 7;

        // BlacksiteSquadOrderKind
        internal const int OrderDeploy = 0;
        internal const int OrderInfiltrate = 1;
        internal const int OrderApproachObjective = 2;
        internal const int OrderOverrideObjective = 3;
        internal const int OrderInspectObjective = 4;
        internal const int OrderTrackDrive = 5;
        internal const int OrderRecoverDrive = 6;
        internal const int OrderContact = 7;
        internal const int OrderSearchLastKnown = 8;
        internal const int OrderEscortCarrier = 9;
        internal const int OrderExtract = 10;
        internal const int OrderBoard = 11;
        internal const int OrderHold = 12;
    }

    /// <summary>
    /// #959 S13: what the objective snapshot list says, reduced to the four facts the ladder
    /// reads. The list itself is a NetworkList of a Unity struct, so the reduction happens in the
    /// mission controller and only these booleans cross into the pure core. Loot records are
    /// excluded by the caller: a raider pocketing scrap is not the contract's objective.
    /// </summary>
    internal readonly struct BlacksiteActivitySnapshotFlags
    {
        internal BlacksiteActivitySnapshotFlags(bool loaded, bool carriedOutside, bool carriedInside, bool reserved)
        {
            Loaded = loaded;
            CarriedOutside = carriedOutside;
            CarriedInside = carriedInside;
            Reserved = reserved;
        }

        internal bool Loaded { get; }
        internal bool CarriedOutside { get; }
        internal bool CarriedInside { get; }
        internal bool Reserved { get; }
    }

    /// <summary>#959 S13: one belt segment, and whether it renders in the danger tier.</summary>
    internal readonly struct BlacksiteActivityLine
    {
        internal BlacksiteActivityLine(string text, bool lethal)
        {
            Text = text;
            Lethal = lethal;
        }

        internal string Text { get; }
        internal bool Lethal { get; }
    }

    /// <summary>
    /// #959 S13: the precedence ladder. First rung that matches wins, highest first.
    /// Pure: no clock, no state, no Unity.
    /// </summary>
    internal static class BlacksiteActivityPolicy
    {
        internal static BlacksiteActivityStatus Resolve(
            int missionState,
            int driveState,
            int outcome,
            int shipPhase,
            BlacksiteActivitySnapshotFlags snapshots,
            bool returning,
            int squadOrderKind,
            bool objectiveSlipped = false)
        {
            // 1. Terminal. One-way and never debounced: the raid is over and the belt must say so
            //    on the tick it happens.
            switch (outcome)
            {
                case BlacksiteActivityWire.OutcomeRecovered: return BlacksiteActivityStatus.Extracted;
                case BlacksiteActivityWire.OutcomeDenied: return BlacksiteActivityStatus.Denied;
                case BlacksiteActivityWire.OutcomeAborted: return BlacksiteActivityStatus.Aborted;
            }

            // 2. Withdrawal. Boarding, departing and departed are all "they are leaving"; so is the
            //    host's own Returning latch and the Extracting mission state, which is set on the
            //    same line that commits the withdrawal.
            if (returning
                || missionState == BlacksiteActivityWire.MissionExtracting
                || shipPhase == BlacksiteActivityWire.ShipBoarding
                || shipPhase == BlacksiteActivityWire.ShipDeparting
                || shipPhase == BlacksiteActivityWire.ShipDeparted)
            {
                return BlacksiteActivityStatus.Withdrawing;
            }

            // 3. Objective snapshots, latest phase first. #874 S6: Loaded comes from the manifest
            //    phase only (the cargo-motion byte read DRIVE ABOARD at t=+0). The manifest publishes
            //    the phase for every contract, so a Payload cart and a Survey beacon read the same
            //    way as a hard drive; the drive state only exists for Shadow Raid.
            if (snapshots.Loaded) return BlacksiteActivityStatus.Loaded;
            if (snapshots.CarriedOutside) return BlacksiteActivityStatus.Hauling;
            if (snapshots.CarriedInside) return BlacksiteActivityStatus.Taken;

            // 3b. #966 S14 U4: the objective was in their hands and is not any more, and the
            //     re-take backoff has not run out. It sits between the held snapshots above and
            //     the reserved one below on purpose: a slipped objective cannot also be held, so
            //     nothing above it is reachable at the same time, and it must beat Closing because
            //     an escort that has already been assigned again is still not allowed to take him.
            //     "Closing" during the backoff would tell the crew the squad is about to have him,
            //     which is exactly the window in which they cannot.
            if (objectiveSlipped) return BlacksiteActivityStatus.Slipped;

            if (snapshots.Reserved) return BlacksiteActivityStatus.Closing;

            // 4. Drive state. Shadow Raid's own channel, below the snapshots because the manifest
            //    is the newer and broader of the two.
            if (driveState == BlacksiteActivityWire.DriveSoldierHeld) return BlacksiteActivityStatus.Taken;
            if (driveState == BlacksiteActivityWire.DrivePlayerHeld
                || driveState == BlacksiteActivityWire.DriveInShip)
            {
                return BlacksiteActivityStatus.Contested;
            }

            // 5. Mission state, and 6. the squad order tiebreaker inside it.
            BlacksiteActivityStatus fromMission = FromMissionState(missionState);
            if (squadOrderKind == BlacksiteActivityWire.OrderBoard)
            {
                // A boarding order is a withdrawal the mission state has not caught up with yet.
                return BlacksiteActivityStatus.Withdrawing;
            }

            BlacksiteActivityStatus fromOrder = FromOrderKind(squadOrderKind);
            // The tiebreaker may only ADVANCE a coarse label. The order kind is per-member and the
            // mission state is not, so letting Infiltrate walk an active Closing back to Breaching
            // would make the belt stutter every time one member re-entered the facility.
            return CoarseRank(fromOrder) > CoarseRank(fromMission)
                ? fromOrder
                : fromMission;
        }

        /// <summary>
        /// Terminal statuses and the withdrawal are one-way, so they skip the dwell entirely. Every
        /// other status is a reading of a situation that can reverse, which is what the dwell is for.
        /// </summary>
        internal static bool BypassesDebounce(BlacksiteActivityStatus status)
        {
            return status == BlacksiteActivityStatus.Extracted
                || status == BlacksiteActivityStatus.Denied
                || status == BlacksiteActivityStatus.Aborted
                || status == BlacksiteActivityStatus.Withdrawing;
        }

        private static BlacksiteActivityStatus FromMissionState(int missionState)
        {
            switch (missionState)
            {
                case BlacksiteActivityWire.MissionOverriding: return BlacksiteActivityStatus.Overriding;
                case BlacksiteActivityWire.MissionInspecting: return BlacksiteActivityStatus.AtObjective;
                case BlacksiteActivityWire.MissionApproachingExpectedDrive: return BlacksiteActivityStatus.Closing;
                // RecoveringDrive means the objective is somewhere they have to go and find again;
                // the belt has no separate word for that and Searching is what it looks like.
                case BlacksiteActivityWire.MissionTrackingMissingDrive:
                case BlacksiteActivityWire.MissionRecoveringDrive: return BlacksiteActivityStatus.Searching;
                // EscortingCarrier is normally answered by rung 3 or 4; it is mapped here for the
                // window where the carrier record has been written but the phase has not.
                case BlacksiteActivityWire.MissionEscortingCarrier: return BlacksiteActivityStatus.Taken;
                case BlacksiteActivityWire.MissionInfiltrating: return BlacksiteActivityStatus.Breaching;
                case BlacksiteActivityWire.MissionDeploying: return BlacksiteActivityStatus.Deploying;
                case BlacksiteActivityWire.MissionScheduled:
                case BlacksiteActivityWire.MissionInbound: return BlacksiteActivityStatus.Inbound;
                default: return BlacksiteActivityStatus.None;
            }
        }

        private static BlacksiteActivityStatus FromOrderKind(int squadOrderKind)
        {
            switch (squadOrderKind)
            {
                case BlacksiteActivityWire.OrderSearchLastKnown:
                case BlacksiteActivityWire.OrderContact: return BlacksiteActivityStatus.Searching;
                case BlacksiteActivityWire.OrderInfiltrate: return BlacksiteActivityStatus.Breaching;
                default: return BlacksiteActivityStatus.None;
            }
        }

        /// <summary>
        /// How far along the coarse "where is the squad" scale a status sits. A status that names a
        /// specific interaction is off this scale entirely and the tiebreaker may never touch it.
        /// </summary>
        private static int CoarseRank(BlacksiteActivityStatus status)
        {
            switch (status)
            {
                case BlacksiteActivityStatus.None: return 0;
                case BlacksiteActivityStatus.Inbound: return 1;
                case BlacksiteActivityStatus.Deploying: return 2;
                case BlacksiteActivityStatus.Breaching: return 3;
                case BlacksiteActivityStatus.Searching: return 4;
                default: return int.MaxValue;
            }
        }
    }

    /// <summary>
    /// #959 S13: the hysteresis. The status folds in objective snapshot phases, which flip faster
    /// than mission state does, so the existing per-mission-state hold on the controller does not
    /// cover it. The clock is injected, so a fixture drives it without Unity.
    /// </summary>
    internal struct BlacksiteActivityDebounce
    {
        /// <summary>
        /// Minimum dwell. Sits ABOVE the manifest's 1.1 s pickup dwell
        /// (BlacksiteObjectiveManifestController, `_pickupAt[...] = Time.time + 1.1f`) so the two
        /// compose instead of fighting: the manifest debounces the acquisition, this debounces the
        /// label, and 1.1 s under 2.0 s means no double delay worth tuning out.
        /// </summary>
        internal const float MinimumDwellSeconds = 2f;

        /// <summary>
        /// Flip-back guard. Mirrors DriveStateFlipBackHoldSeconds (1.5 s) on the mission
        /// controller, doubled because that hold covers one mission state and this covers a label
        /// assembled from three independent channels, so it has twice as many ways to strobe.
        /// This is the #314 D12/D13 failure applied to the belt.
        /// </summary>
        internal const float FlipBackHoldSeconds = 3f;

        /// <summary>
        /// Sticky carrier. Mirrors ExtractionDriveLossGraceSeconds (2 s) on the mission controller
        /// exactly: one dropped carrier tick must not un-latch a running haul, on the belt for the
        /// same reason it must not un-latch the extraction.
        /// </summary>
        internal const float CarrierStickySeconds = 2f;

        /// <summary>
        /// Consecutive ticks a new answer must survive. Two, because the server tick is 0.25 s and
        /// one tick is where a mid-write snapshot list is observed; two consecutive readings cost
        /// 0.25 s of latency and remove that class of answer entirely.
        /// </summary>
        internal const int ConfirmationTicks = 2;

        private BlacksiteActivityStatus _current;
        private BlacksiteActivityStatus _previous;
        private BlacksiteActivityStatus _pending;
        private int _pendingTicks;
        private float _enteredAt;
        private bool _carrierMissing;
        private float _carrierMissingSince;

        internal BlacksiteActivityStatus Current => _current;

        /// <summary>
        /// Offers the freshly computed answer. Returns true and sets <paramref name="accepted"/>
        /// only when the published status must change.
        /// </summary>
        internal bool Tick(BlacksiteActivityStatus computed, bool carrierPresent, float now, out BlacksiteActivityStatus accepted)
        {
            accepted = _current;
            if (carrierPresent)
            {
                _carrierMissing = false;
            }
            else if (!_carrierMissing)
            {
                _carrierMissing = true;
                _carrierMissingSince = now;
            }

            if (computed == _current)
            {
                _pending = BlacksiteActivityStatus.None;
                _pendingTicks = 0;
                return false;
            }

            // The first status of a mission has no dwell to serve, and a one-way status never waits.
            if (_current == BlacksiteActivityStatus.None || BlacksiteActivityPolicy.BypassesDebounce(computed))
                return Accept(computed, now, out accepted);

            // Sticky carrier: a held objective survives a dropped carrier tick.
            if ((_current == BlacksiteActivityStatus.Taken || _current == BlacksiteActivityStatus.Hauling)
                && _carrierMissing
                && now - _carrierMissingSince < CarrierStickySeconds)
            {
                return false;
            }

            // Flip-back guard: never return to the status just left inside the hold.
            if (computed == _previous && now - _enteredAt < FlipBackHoldSeconds)
                return false;

            // Minimum dwell on the status currently shown.
            if (now - _enteredAt < MinimumDwellSeconds)
                return false;

            if (computed != _pending)
            {
                _pending = computed;
                _pendingTicks = 1;
                return false;
            }

            _pendingTicks++;
            if (_pendingTicks < ConfirmationTicks)
                return false;

            return Accept(computed, now, out accepted);
        }

        private bool Accept(BlacksiteActivityStatus status, float now, out BlacksiteActivityStatus accepted)
        {
            _previous = _current;
            _current = status;
            _enteredAt = now;
            _pending = BlacksiteActivityStatus.None;
            _pendingTicks = 0;
            accepted = status;
            return true;
        }
    }

    /// <summary>
    /// #959 S13: the wording. One line per status, with the objective's noun taken from the
    /// contract mode, so the belt says PAYLOAD on a Payload moon and DRIVE on a Shadow Raid
    /// instead of naming a hard drive on every contract.
    ///
    /// No label begins with "RECOVERY TEAM": the belt already prefixes the source title
    /// ("BLACKSITE RETRIEVAL"), and printing it twice is what the strip used to do. Every label is
    /// already uppercase, so the belt's unconditional ToUpperInvariant is a no-op, and none carries
    /// TMP markup.
    /// </summary>
    internal static class BlacksiteActivityText
    {
        private const string Payload = "PAYLOAD";
        private const string Drive = "DRIVE";
        private const string Witness = "WITNESS";
        private const string Cargo = "CARGO";

        internal static string Noun(BlacksiteContractMode mode)
        {
            switch (mode)
            {
                case BlacksiteContractMode.PayloadCart: return Payload;
                case BlacksiteContractMode.HardDrive: return Drive;
                case BlacksiteContractMode.WitnessEscort: return Witness;
                // Default and Excluded: the raid takes whatever the facility offers, and the belt
                // has no name for it beyond the one the manifest already uses.
                default: return Cargo;
            }
        }

        /// <summary>
        /// The stable identity the HUD's change detection keys on. It is spelled out per member
        /// rather than derived from the enum name so the call allocates nothing at 4 Hz and so a
        /// rename of an enum member can never silently change a published signature.
        /// </summary>
        internal static string StatusKey(BlacksiteActivityStatus status)
        {
            switch (status)
            {
                case BlacksiteActivityStatus.Inbound: return "blacksite.inbound";
                case BlacksiteActivityStatus.Deploying: return "blacksite.deploying";
                case BlacksiteActivityStatus.Breaching: return "blacksite.breaching";
                case BlacksiteActivityStatus.Searching: return "blacksite.searching";
                case BlacksiteActivityStatus.Closing: return "blacksite.closing";
                case BlacksiteActivityStatus.AtObjective: return "blacksite.atobjective";
                case BlacksiteActivityStatus.Overriding: return "blacksite.overriding";
                case BlacksiteActivityStatus.Taken: return "blacksite.taken";
                case BlacksiteActivityStatus.Hauling: return "blacksite.hauling";
                case BlacksiteActivityStatus.Loading: return "blacksite.loading";
                case BlacksiteActivityStatus.Loaded: return "blacksite.loaded";
                case BlacksiteActivityStatus.Contested: return "blacksite.contested";
                case BlacksiteActivityStatus.Slipped: return "blacksite.slipped";
                case BlacksiteActivityStatus.Withdrawing: return "blacksite.withdrawing";
                case BlacksiteActivityStatus.Extracted: return "blacksite.extracted";
                case BlacksiteActivityStatus.Denied: return "blacksite.denied";
                case BlacksiteActivityStatus.Aborted: return "blacksite.aborted";
                default: return "blacksite.none";
            }
        }

        // Producing rung per status; a status with none falls to default:
        // Extracted|Denied|Aborted <- outcome; Withdrawing <- rung 2, OrderBoard
        // Loaded <- snapshots.Loaded; Hauling <- snapshots.CarriedOutside; Slipped <- objectiveSlipped
        // Taken <- snapshots.CarriedInside, DriveSoldierHeld, MissionEscortingCarrier
        // Closing <- snapshots.Reserved, MissionApproachingExpectedDrive; Contested <- DrivePlayerHeld|DriveInShip
        // Searching <- MissionTracking/RecoveringDrive, OrderSearchLastKnown|Contact; Breaching <- MissionInfiltrating, OrderInfiltrate
        // AtObjective|Overriding|Deploying|Inbound <- MissionInspecting|Overriding|Deploying|Scheduled+Inbound
        // Loading <- none (#874 S6)
        internal static BlacksiteActivityLine Format(BlacksiteActivityStatus status, BlacksiteContractMode mode)
        {
            bool witness = mode == BlacksiteContractMode.WitnessEscort;
            string noun = Noun(mode);
            switch (status)
            {
                // A client that has not received the host's first accepted status yet is, by
                // construction, at the start of the raid: the mission is created in Inbound.
                case BlacksiteActivityStatus.None:
                case BlacksiteActivityStatus.Inbound:
                    return new BlacksiteActivityLine("TEAM INBOUND", false);
                case BlacksiteActivityStatus.Deploying:
                    return new BlacksiteActivityLine("TEAM DEPLOYING", false);
                case BlacksiteActivityStatus.Breaching:
                    return new BlacksiteActivityLine("TEAM BREACHING", false);
                case BlacksiteActivityStatus.Searching:
                    return new BlacksiteActivityLine("LOCATING THE " + noun, false);
                case BlacksiteActivityStatus.Closing:
                    return new BlacksiteActivityLine("CLOSING ON THE " + noun, false);
                case BlacksiteActivityStatus.AtObjective:
                    return new BlacksiteActivityLine("TEAM AT THE " + noun, false);
                case BlacksiteActivityStatus.Overriding:
                    return new BlacksiteActivityLine("BREACHING THE CACHE", false);
                case BlacksiteActivityStatus.Taken:
                    // S14 U4: a person is taken into custody, not picked up.
                    return new BlacksiteActivityLine(witness ? "WITNESS IN CUSTODY" : noun + " TAKEN", false);
                case BlacksiteActivityStatus.Hauling:
                    if (mode == BlacksiteContractMode.PayloadCart)
                        return new BlacksiteActivityLine("PUSHING PAYLOAD", false);
                    // S14 U4: a witness being marched off the moon is the failure clock running.
                    if (witness)
                        return new BlacksiteActivityLine("EXTRACTING WITNESS", true);
                    return new BlacksiteActivityLine("HAULING THE " + noun, false);
                case BlacksiteActivityStatus.Loaded:
                    return new BlacksiteActivityLine(noun + " ABOARD", witness);
                case BlacksiteActivityStatus.Contested:
                    return new BlacksiteActivityLine(noun + " CONTESTED", false);
                case BlacksiteActivityStatus.Slipped:
                    // #966 S14 U4: a custody break has its own status now, so WITNESS DROPPED no
                    // longer borrows Contested. Only the witness escort can produce it, but every
                    // mode still formats, because the coverage loop asks every pair.
                    return new BlacksiteActivityLine(witness ? "WITNESS DROPPED" : noun + " DROPPED", false);
                case BlacksiteActivityStatus.Withdrawing:
                    return new BlacksiteActivityLine("TEAM WITHDRAWING", false);
                case BlacksiteActivityStatus.Extracted:
                    // S14 U4: the witness leaving is a contract failure Stage 14 wires up; every
                    // other contract phrases the same terminal as the crew's loss.
                    return new BlacksiteActivityLine(witness ? "WITNESS EXTRACTED" : noun + " LOST", false);
                case BlacksiteActivityStatus.Denied:
                    return new BlacksiteActivityLine("TEAM DENIED", false);
                default:
                    return new BlacksiteActivityLine("TEAM WITHDREW", false);
            }
        }
    }
}
