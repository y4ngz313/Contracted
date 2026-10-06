using UnityEngine;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    public sealed partial class BlacksiteSoldierAI
    {
        private readonly System.Collections.Generic.Queue<(float At, float Metres)> _movementSamples = new System.Collections.Generic.Queue<(float, float)>();
        private Vector3 _diagnosticPrevious;
        private float _diagnosticMetres, _diagnosticNextSample, _movedMetresLast5s;
        private bool _diagnosticPositionSet;
        // #874 S4.0a: the five-second window is the right smoothing for the Decision heartbeat and
        // the wrong one for a stall. A member parked for one second inside a five-second window
        // still reports metres of movement, so the run's freezes were invisible in it. The
        // one-second trailing window is what stallOwner arbitrates against; the five-second one is
        // untouched so every Decision-line comparison against the old logs still holds.
        private readonly System.Collections.Generic.Queue<(float At, float Metres)> _motionSamples1s = new System.Collections.Generic.Queue<(float, float)>();
        private float _speedLast1s;
        private float _lastMotionAt;
        private bool _lastMotionAtSet;
        private string _lastStallOwner;
        private BlacksiteStallOwner _lastStallOwnerKind;
        private string _lastStallDetail;
        // #956 S12A U3: the pacing owner the last emitted line carried, for the change gate.
        private BlacksiteSpeedOwner _lastSpeedOwner = BlacksiteSpeedOwner.None;
        private bool _stallOwnerKnown;
        private float _nextMotionTraceAt;
        internal string LastStallOwner => _lastStallOwner ?? "none";

        /// <summary>
        /// #874 G5: the key every per-soldier diagnostic gate folds against. It is the soldier's
        /// NETWORK IDENTITY, not its squad ordinal: member indices run 0..3 WITHIN a squad, so on a
        /// moon carrying two squads every index is shared and one squad's stall would open, extend
        /// and cap the other squad's window. Identical by construction to
        /// <c>BlacksiteNavigator.MotionRequestKey</c>, which is keyed this way for the same reason,
        /// so a soldier folds against itself whichever gate is asking. <c>member=N</c> stays the
        /// printed label; the key is not the label.
        /// </summary>
        internal int DiagnosticIdentity
            => NetworkObject != null && NetworkObject.IsSpawned
                ? unchecked((int)NetworkObject.NetworkObjectId)
                : GetInstanceID();

        private void MeasureActualMovement()
        {
            if (!BlacksiteDiagnostics.Enabled) return;
            var position = transform.position;
            if (_diagnosticPositionSet) _diagnosticMetres += Vector3.Distance(position, _diagnosticPrevious);
            _diagnosticPositionSet = true; _diagnosticPrevious = position;
            if (Time.time >= _diagnosticNextSample)
            {
                _diagnosticNextSample = Time.time + .2f;
                _movementSamples.Enqueue((Time.time, _diagnosticMetres));
                while (_movementSamples.Count > 1 && _movementSamples.Peek().At < Time.time - 5f) _movementSamples.Dequeue();
                _movedMetresLast5s = _diagnosticMetres - _movementSamples.Peek().Metres;
                _motionSamples1s.Enqueue((Time.time, _diagnosticMetres));
                while (_motionSamples1s.Count > 1 && _motionSamples1s.Peek().At < Time.time - 1f) _motionSamples1s.Dequeue();
                var oldest = _motionSamples1s.Peek();
                float span = Time.time - oldest.At;
                _speedLast1s = span > .05f ? (_diagnosticMetres - oldest.Metres) / span : 0f;
            }
            // Owner arbitration runs every frame; the displacement windows above are resampled at
            // 5 Hz because that is what smooths them. A stallOwner change is therefore in the log
            // in the frame it happens, which is what makes an episode's start and end exact.
            TraceMotion();
        }

        // One line per second per alive server-authoritative member, plus one immediately on every
        // stallOwner change, so a stall's owner and its start are both in the log without any
        // cross-referencing. Everything below runs only under BlacksiteDiagnostics.Enabled, which
        // MeasureActualMovement has already established.
        private void TraceMotion()
        {
            // Windows are only ever opened on the host, so only the host retires them.
            if (!IsServerAuthority()) return;
            int captureKey = DiagnosticIdentity;
            // #874 G5: the expiry sweep runs BEFORE any open below, and it runs on the paths that
            // return early too. Before the open, because a re-open on the very frame the old
            // window expires would otherwise extend it and the close line would never print. On
            // the early-return paths, because a member that dies or is torn down mid-window still
            // has a window to retire, and Update keeps calling this pass for a dead member, so
            // this is where its close line comes from. A member DESPAWNED outright stops ticking
            // and its record is cleared by BlacksiteDiagnostics.Reset with the round.
            BlacksiteDiagnostics.CloseExpiredDetailWindow(captureKey, MemberIndex);
            if (!_initialized || isEnemyDead) return;
            var navigator = _navigator;
            if (navigator == null) return;
            BlacksiteMotionSample sample = navigator.BuildMotionSample(_speedLast1s);
            if (sample.Speed >= BlacksiteMotionPolicy.MovingSpeed || !_lastMotionAtSet)
            { _lastMotionAt = Time.time; _lastMotionAtSet = true; }
            // Compare the owner and its detail, not the printed token: composing the seven-owner token
            // once a frame per member for a whole mission is a string allocation the arbitration
            // does not need. The token is built only on the frame the line is actually emitted.
            BlacksiteStallOwner ownerKind = BlacksiteMotionPolicy.Classify(sample, out string detail);
            // #956 S12A U3: the speed owner is part of the change test, so a member whose pacing
            // owner changes while it is still classified moving has that change in the log on the
            // frame it happens, exactly like stallOwner. The enum comparison costs nothing; the
            // token is built only on the frame the line is emitted.
            BlacksiteSpeedOwner speedOwner = BlacksiteMotionPolicy.ClassifySpeedOwner(sample);
            // #874 G5: the two owner transitions are split out of the change test. A transition
            // is ALWAYS logged, capture window or not, because it is the line that names when a
            // stall started and who owns it. Everything else the old test folded in - a change of
            // the hold detail alone - is detail, and detail is only logged inside a window.
            bool stallOwnerTransition = !_stallOwnerKnown || ownerKind != _lastStallOwnerKind;
            bool transition = stallOwnerTransition || speedOwner != _lastSpeedOwner;
            bool changed = transition
                || !string.Equals(detail, _lastStallDetail, System.StringComparison.Ordinal);
            _stallOwnerKnown = true;
            _lastStallOwnerKind = ownerKind;
            _lastStallDetail = detail;
            _lastSpeedOwner = speedOwner;

            // #874 G5: open a detail capture window when the stall owner changes. The expiry sweep
            // above has already retired any window that ended this frame, so this either extends a
            // genuinely live window or opens a fresh one.
            if (stallOwnerTransition)
                BlacksiteDiagnostics.OpenDetailWindow(captureKey, MemberIndex, "stall");

            bool windowOpen = BlacksiteDiagnostics.IsDetailOpen(captureKey);
            bool heartbeatDue = Time.time >= _nextMotionTraceAt;

            // When the window is closed and nothing transitioned, silently advance
            // the heartbeat without emitting.
            if (!transition && !windowOpen)
            {
                if (heartbeatDue)
                    _nextMotionTraceAt = Time.time + 1f;
                return;
            }

            // `changed` subsumes `transition`, so a surviving frame here is either a detail
            // change or the heartbeat, and both have already been proven to be inside a window.
            if (!changed && !heartbeatDue) return;
            if (heartbeatDue)
                _nextMotionTraceAt = Time.time + 1f;
            // Refreshed on every change and on every heartbeat, so the [UAT][Decision] line that
            // joins on it can never print a stale owner.
            string owner = BlacksiteMotionPolicy.Describe(ownerKind, detail);
            _lastStallOwner = owner;
            BlacksiteDiagnostics.Log(
                "Motion",
                $"member={MemberIndex}, stallOwner={owner}, sinceMotion={Time.time - _lastMotionAt:0.00}"
                + $", speed={sample.Speed:0.00}, agentVelocity={navigator.AgentVelocity:0.00}, desiredSpeed={navigator.DesiredSpeed:0.00}, "
                + navigator.MotionDetails(sample, BehaviourGoal)
                // #956 S12A U3: appended LAST, after claimOffset=, so every key the score script
                // already parses keeps its position.
                + $", speedOwner={BlacksiteMotionPolicy.SpeedOwnerToken(speedOwner)}"
                // #955 S12A U2: appended after speedOwner=. A monotonic per-member count of the
                // identical-point re-issues the gate refused. It is deliberately NOT part of the
                // change test above: a counter that moves on its own would make every frame a
                // change and defeat the gate this line is folded by.
                + $", dropped={navigator.DroppedIssues}"
                // #874 S4: appended after dropped=, replacing #1015 S1's lease=/leaseFor=: who writes
                // the body, and the seconds left on the dwell ("-" when none).
                + $", writer={sample.BodyWriter}"
                + $", dwell={(sample.DwellUntil > sample.Now ? (sample.DwellUntil - sample.Now).ToString("0.0") : "-")}"
                // #874 P-S1: leg= and legRemaining=.
                + BlacksiteNavigator.MotionLegDetails(sample));
        }

        private float _lastBodyAimAngle, _lastRifleAimAngle;

        // #1293 R3: the member's published intent, written only by CommitBehaviour and the
        // initializing and dead branches. Host only, never replicated.
        private BlacksiteMemberIntent _intent;
        internal BlacksiteMemberIntent Intent => _intent;

        // [UAT][Decision] prints that row when it changes, never as a heartbeat.
        private void PublishIntent(BlacksiteBehaviour intent, string reason)
        {
            if (intent == _intent.Intent && reason == _intent.Reason) return;
            _intent = new BlacksiteMemberIntent(intent, reason, Time.time);
            if (BlacksiteDiagnostics.Enabled)
                BlacksiteDiagnostics.Log("Decision", $"member={MemberIndex}, intent={intent}, reason={reason}, since={_intent.Since:0.00}");
        }

        internal string CombatDecisionDetails
        {
            get
            {
                bool bound = _agent != null && _agent.enabled && _agent.isOnNavMesh;
                return $"movedMetresLast5s={_movedMetresLast5s:0.00}, behaviour={SelectedBehaviour}, goal={BehaviourGoal?.ToString() ?? "none"}, issued={(Navigator.HasIssuedDestination ? Navigator.IssuedDestination.ToString("0.00") : "none")}, stallOwner={LastStallOwner}, branch={_intent.Reason ?? BlacksiteDiagnosticFormat.None}, motionHeld={_motionHeld}, navBound={bound}, path={(bound && _agent.hasPath)}, remaining={Navigator.RemainingDistance:0.00}, bodyAngle={_lastBodyAimAngle:0.0}, rifleAngle={_lastRifleAimAngle:0.0}, {_aimPose?.FireReadinessDetails ?? "pose=missing"}";
            }
        }

        // #874 S2.1: the close-in's own evidence. A melee decision reported through the rifle
        // fields (unseen, magazine, weapon state, an empty moveGoal) said nothing about why an
        // actor two metres from its target was standing still, so the melee branch reports the
        // baton phase, the measured geometry, the stop distance in force and who owns it, the
        // navigation state and who owns movement.
        /// <summary>
        /// #1063 WP2: the collider <see cref="MeasureBatonContact"/> actually took this engagement's
        /// geometry from, with the vertical span the gate is decided on and the body's own size.
        /// One token, no spaces, so the comma-separated melee line stays parseable by score.ps1.
        ///
        /// <c>none</c> means the measurement fell back to the contact's transform root - the
        /// contact has no non-trigger collider, or its bounds are degenerate - which is a different
        /// fact from a badly placed collider and used to be indistinguishable on the line.
        /// </summary>
        private string BatonContactBodyDetails
        {
            get
            {
                var body = _batonContactBody;
                if (body == null) return BlacksiteDiagnosticFormat.None;
                Bounds bounds = body.bounds;
                return $"{body.name}|y={bounds.min.y:0.00}..{bounds.max.y:0.00}"
                    + $"|size={bounds.size.x:0.00}x{bounds.size.y:0.00}x{bounds.size.z:0.00}";
            }
        }

        internal string MeleeDecisionDetails
        {
            get
            {
                var navigator = Navigator;
                bool bound = _agent != null && _agent.enabled && _agent.isOnNavMesh;
                var state = _baton.Value;
                return $"baton={state.Phase}, batonElapsed={(float)(GetServerTime() - state.Started):0.00}s"
                    // #1063 WP2: the gap is signed, so a negative number is a contact BELOW rather
                    // than an unset field. `none` is the unset field, and it has to be a word
                    // because every number in the signed range is now a legitimate answer.
                    + $", range={_batonRange:0.00}, angle={_batonAngle:0.0}, verticalGap={(float.IsNaN(_batonVertical) ? BlacksiteDiagnosticFormat.None : _batonVertical.ToString("0.00"))}"
                    // #1063 WP2: WHICH collider the geometry above was measured against, and where
                    // that collider actually is. Three deployments of the 2026-09-15 session
                    // printed verticalGap=1.94 to 2.97 against a 1.70 m gate for a contact whose
                    // root was 0.05 m from the soldier's own feet, and nothing on the line said
                    // that the gap belonged to a body part authored two metres off the pivot. The
                    // name and the span are what make the next log prove the pick rather than
                    // invite another session of guessing at it.
                    + $", profile={_batonContactProfile}, collider={BatonContactBodyDetails}"
                    + $", reach={BlacksiteEngagementGeometry.MinReach(_batonContactProfile):0.00}-{BlacksiteEngagementGeometry.MaxReach(_batonContactProfile):0.00}, stop={navigator.EffectiveStoppingDistance:0.00}, stopOwner={navigator.StoppingDistanceOwner}"
                    + $", goal={(_hasBatonApproachGoal ? _batonApproachGoal.ToString("0.00") : "none")}, goalAge={navigator.CombatDestinationAge:0.00}s"
                    + $", pathPending={(bound && _agent.pathPending)}, pathStatus={(bound ? _agent.pathStatus.ToString() : "unbound")}"
                    + $", remaining={navigator.RemainingDistance:0.00}, velocity={(bound ? _agent.velocity.magnitude : 0f):0.00}"
                    + $", movementOwner={navigator.MovementOwner}, movedMetresLast5s={_movedMetresLast5s:0.00}"
                    + $", blocked={_batonBlocked}, behaviour={SelectedBehaviour}, branch={_intent.Reason ?? BlacksiteDiagnosticFormat.None}"
                    // #874 S10.1, appended at the end of the line: the range the commit was
                    // actually taken on, the rate that range is changing (positive opens) and the
                    // lead it was projected over.
                    // Both [UAT][Melee] and [UAT][CombatDecision] weapon=Baton carry them, because
                    // both lines end with this one detail string.
                    + $", predictedRange={_batonPredictedRange:0.00}, radialSpeed={_batonRadialSpeed:0.00}"
                    + $", lead={_batonLead:0.00}";
            }
        }

        private static readonly System.Collections.Generic.List<Light> CensusLights = new System.Collections.Generic.List<Light>();
        private static readonly System.Collections.Generic.List<SkinnedMeshRenderer> CensusSkins = new System.Collections.Generic.List<SkinnedMeshRenderer>();

        /// <summary>
        /// #1305: this member's share of the [UAT][Cost] census: animator layers, shadow-casting
        /// lights and skinned renderers that are on, and whether its agent is enabled.
        /// </summary>
        internal void AddToCostCensus(BlacksiteHitchLedger ledger)
        {
            int layers = _animator != null && _animator.runtimeAnimatorController != null ? _animator.layerCount : 0;
            int shadowLights = 0;
            GetComponentsInChildren(false, CensusLights);
            for (int i = 0; i < CensusLights.Count; i++)
                if (CensusLights[i].enabled && CensusLights[i].shadows != LightShadows.None) shadowLights++;
            int skins = 0;
            GetComponentsInChildren(false, CensusSkins);
            for (int i = 0; i < CensusSkins.Count; i++)
                if (CensusSkins[i].enabled) skins++;
            CensusLights.Clear();
            CensusSkins.Clear();
            var agent = _agent;
            ledger.AddSoldierCensus(layers, shadowLights, agent != null && agent.enabled, skins);
        }
    }
}
