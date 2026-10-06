using System.Collections.Generic;

using UnityEngine;

using Y4NGZCompany.Contracts.Whistleblower;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #965 S14 U3. The last leg of the escort: the witness goes aboard with his escort and the
    /// board says so.
    ///
    /// The witness is an agent, not a prop: there is no cargo anchor, no stage, and nothing for
    /// LateUpdateReplica to parent. #1219 C2: Carried becomes Loaded by the custody event of his
    /// escort's completed board crossing (<see cref="LoadWitnessRecordServer"/>), the same crossing
    /// a soldier boards by - never by where his body stands.
    ///
    /// #874 (2026-09-20) replaces what happened next. The first cut took him OUT OF THE WORLD -
    /// every renderer off, every collider off, the agent parked, the body warped onto the boarding
    /// anchor once per tick - so that he "rode the departure invisibly, exactly as a boarded
    /// soldier does". Both halves of that are now wrong. A boarded soldier is a visible passenger
    /// (<see cref="BlacksiteLifecycle"/>), and so is he: the ship's passenger registry owns his
    /// transform frame by frame from a seat measured in hull-local space, his renderers and
    /// colliders are never touched, and the departure probes know his colliders are cargo. The
    /// per-tick <c>NavMeshAgent.Warp</c> is gone with it - the ship releases its own navigation on
    /// the first frame of a departure, so a Warp aboard a climbing hull is a call that can fail and
    /// leave a body on the moon.
    ///
    /// The record's CarrierId is cleared at the same transition. Once he is aboard the SHIP holds
    /// him, not a man; leaving the escort's id on the record is what let round 3 resolve a
    /// despawned soldier and call it escortDead.
    /// </summary>
    internal sealed partial class BlacksiteObjectiveManifestController
    {
        // Records whose witness the transport is carrying.
        private readonly HashSet<ulong> _witnessAboard = new HashSet<ulong>();
        private readonly List<ulong> _witnessReleaseBuffer = new List<ulong>();

        /// <summary>
        /// One server tick of witness boarding. Called from the manifest's own UpdateServer
        /// BEFORE the Refresh loop, so the hull-relative hold is applied before anything asks
        /// whether the body is still inside the hold.
        /// </summary>
        internal void TickWitnessBoardingServer()
        {
            if (_mission == null || !_mission.IsServer || Ship == null)
                return;
            for (int i = 0; i < Records.Count; i++)
            {
                BlacksiteObjectiveSnapshot record = Records[i];
                if (!TickWitnessRecordServer(ref record))
                    continue;
                Records[i] = record;
            }
        }

        /// <summary>
        /// The per-record tick, separated so the record is the only thing this needs: the native
        /// fixture drives it with a record of its own, because a non-empty NetworkList cannot be
        /// enumerated headlessly. It hands back a passenger whose record left Loaded and re-asserts
        /// a Loaded witness's seat; it never writes the record (#1219 C2: Carried becomes Loaded
        /// only by <see cref="LoadWitnessRecordServer"/>). Returns true when the record changed.
        /// </summary>
        private bool TickWitnessRecordServer(ref BlacksiteObjectiveSnapshot record)
        {
            // A passenger is handed back the moment its record is anything but Loaded, whatever
            // path took it there (back to Carried, a cleanup to Available, the witness terminal to
            // Unavailable), before any phase filter below.
            if (record.Phase != BlacksiteObjectivePhase.Loaded && _witnessAboard.Contains(record.ObjectId))
                ReleaseWitnessAboardServer(Resolve(record.ObjectId)?.GetComponent<WhistleblowerEnemyAI>(), record.ObjectId);

            if (record.Kind != BlacksiteObjectiveKind.Witness || record.Phase != BlacksiteObjectivePhase.Loaded)
                return false;

            WhistleblowerEnemyAI witness = Resolve(record.ObjectId)?.GetComponent<WhistleblowerEnemyAI>();
            if (witness == null)
                return false;

            // He is aboard. The record is the truth and the body is re-asserted against it, the
            // same relationship LateUpdateReplica has with a carried prop; the ship's own
            // LateUpdate is what carries him with the hull once it lifts.
            HoldWitnessAboardServer(witness);
            return false;
        }

        /// <summary>
        /// #1219 C2: the witness half of the board crossing's custody event - <paramref name="escort"/>
        /// completed a board crossing holding him (#1293 R2: attached), so
        /// his Carried record becomes Loaded and he is seated. Per record, like
        /// <see cref="TickWitnessRecordServer"/>, so the native fixture can drive it headlessly.
        /// Returns true when the record changed and must be written back.
        /// </summary>
        private bool LoadWitnessRecordServer(ref BlacksiteObjectiveSnapshot record, BlacksiteSoldierAI escort)
        {
            if (record.Kind != BlacksiteObjectiveKind.Witness || record.Phase != BlacksiteObjectivePhase.Carried
                || escort == null || record.CarrierId != escort.NetworkObjectId)
                return false;
            WhistleblowerEnemyAI witness = Resolve(record.ObjectId)?.GetComponent<WhistleblowerEnemyAI>();
            // #874 (2026-09-20): a dead or neutralized witness is the crew winning the contract -
            // Refresh's terminal - and one no longer in custody or still in the facility (his
            // world flag, which is his carrier's while he is held) was not carried up.
            if (witness == null || witness.isEnemyDead || witness.Neutralized
                || !witness.IsInCustody || witness.IsInsideFacility)
                return false;

            record.Phase = BlacksiteObjectivePhase.Loaded;
            record.Outside = BlacksiteWorld.Outside(witness);
            record.Position = HoldWitnessAboardServer(witness);
            // #874 (2026-09-20): the transport holds him now. Nothing downstream may resolve an
            // escort from this record again - the escort boards, is despawned with the round, or
            // walks back to a post, and none of those is a custody break for a man already home.
            record.CarrierId = NoCarrier;
            BlacksiteDiagnostics.Log("Custody",
                $"event=aboard, witness={record.ObjectId}"
                + $", member={escort.MemberIndex}"
                + $", point={BlacksiteDiagnosticFormat.V(record.Position)}");
            // #1271 S8: the escort reports the delivery, once: the record has just left Carried.
            escort.AnnounceCargo();
            return true;
        }

        /// <summary>
        /// Puts the witness on the transport's passenger manifest and returns the world point of
        /// his seat. He must already be physically aboard. The shared reservation map keeps his
        /// seat clear for the six returning soldiers, and the hull carries it after departure.
        ///
        /// #1293 R2: the seat is the carrier's deliberate release. In this one server step the
        /// passenger seat takes his body and <see cref="WhistleblowerEnemyAI.EnterShipHoldServer"/>
        /// ends the attachment, so exactly one writer holds him at any time. Nothing reads it as a
        /// break: no backoff is stamped, no escape runs, and the alert belt counts a Loaded witness
        /// as held (BlacksiteRetrievalMissionController.WitnessHeld).
        /// </summary>
        private Vector3 HoldWitnessAboardServer(WhistleblowerEnemyAI witness)
        {
            if (Ship == null || witness == null)
                return Vector3.zero;
            Vector3 seat = Ship.SeatPassengerServer(witness.NetworkObjectId, witness, Ship.BoardingSlot(witness));
            if (_witnessAboard.Add(witness.NetworkObjectId))
                witness.EnterShipHoldServer();
            return seat;
        }

        /// <summary>
        /// Hands a witness back to the world after his record has left Loaded. Nothing has to be
        /// un-hidden - he was never hidden - so this is the passenger registration and the agent
        /// stand-down, in that order: the ship stops writing his transform before he is told to
        /// walk again.
        /// </summary>
        private void ReleaseWitnessAboardServer(WhistleblowerEnemyAI witness, ulong objectId)
        {
            _witnessAboard.Remove(objectId);
            Ship?.ReleasePassengerServer(objectId);
            if (witness != null)
                witness.LeaveShipHoldServer();
        }

        /// <summary>
        /// #874 (2026-09-20): hand every passenger witness back, whatever the reason the ride
        /// ended. Called by the mission cleanup and by the transport's own despawn, so a round
        /// that ends mid-escort, an aborted mission and a destroyed ship all leave him standing
        /// in the world with his own agent rather than frozen where the hull used to be.
        /// Idempotent and safe after the ship is gone.
        /// </summary>
        internal void ReleaseWitnessHoldsServer()
        {
            if (_witnessAboard.Count == 0) return;
            _witnessReleaseBuffer.Clear();
            _witnessReleaseBuffer.AddRange(_witnessAboard);
            for (int i = 0; i < _witnessReleaseBuffer.Count; i++)
            {
                ulong id = _witnessReleaseBuffer[i];
                ReleaseWitnessAboardServer(Resolve(id)?.GetComponent<WhistleblowerEnemyAI>(), id);
            }
        }
    }
}
