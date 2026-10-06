using UnityEngine;
using UnityEngine.AI;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    public sealed partial class BlacksiteSoldierAI
    {
        private bool _raidExtracting;
        private float _nextRaidAssessment, _raidRegroupUntil, _raidReconsiderAt;
        private Vector3 _raidRegroupPoint;
        internal bool IsRaidWithdrawing => !_transportLost && (_raidExtracting || (TryGetMission(out var m) && m.Objectives.WithdrawalCommitted));
        internal void AnnounceRaidAction() => RequestSquadVoice(BlacksiteVoiceEvent.TacticalAction, volume: .65f);
        // #1293 R2: bind the agent where the body stands, or the member is stranded (Navigator.BindInPlace).
        internal void BindNavigationInPlace(string reason) => Navigator.BindInPlace(reason);
        private void LateUpdate()
        {
            using (BlacksiteHitchTracer.Scope(BlacksiteHitchScope.SoldierNavigator))
                _navigator?.LateTick();
            using (BlacksiteHitchTracer.Scope(BlacksiteHitchScope.SoldierLateUpdate))
            {
                AlignRopeHands();
                TickBatonSweep();
            }
        }
        private void UpdateRaidWithdrawal()
        {
            if (_transportLost || IsShipGuard || !_initialized || Time.time < _nextRaidAssessment || IsRaidWithdrawing) return;
            _nextRaidAssessment = Time.time + 1f;
            if (BlacksiteRaidPolicy.WithdrawMember(ReplicatedHealth, _combatController != null && _combatController.AmmunitionExhausted, IsShipGuard))
            {
                _raidExtracting = true;
                BlacksiteDiagnostics.Log("Raid", $"member={MemberIndex} withdrawing: hp={ReplicatedHealth}, ammunitionExhausted={_combatController?.AmmunitionExhausted}.");
                // #1293 R3: the member's own withdrawal begins: its duty and its decision both change.
                RequestDecision(BlacksiteDecisionCause.Outcome);
                AnnounceRaidAction(); return;
            }
            if (Time.time < _raidReconsiderAt || !IsCombatEngaged) return;
            if (!TryGetWorld(out BlacksiteWorldModel world)) return;
            var enemies = RoundManager.Instance?.SpawnedEnemies; if (enemies == null) return;
            int seenEnemies = 0, seenPlayers = 0, allies = 1; Vector3 threat = Vector3.zero;
            foreach (var enemy in enemies)
            {
                if (enemy == null || enemy.isEnemyDead || enemy == this || !BlacksiteWorld.Same(enemy, this)) continue;
                if (enemy is BlacksiteSoldierAI member && member.SquadId == SquadId && !member.IsRaidWithdrawing
                    && Vector3.Distance(transform.position, member.transform.position) < BlacksiteRaidPolicy.RegroupAllyRange)
                    allies++;
            }
            // #1293 R1: the threats are the squad table's rows this member's OWN eyes hold (its sight
            // bit, with the pass's grace) - never an omniscient retreat - same world, inside the 18 m
            // the tally has always used. #1126: a monster counts only while the weapons predicate
            // admits it, because a man the squad may not fight is not a reason to break contact.
            // #874 S11 U2 (I8): a player is preferred as the threat ANCHOR when one is present,
            // because breaking contact from the crew is the movement this rule is for.
            int slot = PerceptionSlot;
            var rows = world.Rows;
            for (int i = 0; i < rows.Count; i++)
            {
                BlacksiteWorldRow row = rows[i];
                if (!row.SeenByMember(slot)) continue;
                if (row.Class == BlacksiteWorldClass.Monster && row.Subject is EnemyAI contact && contact != null
                    && !contact.isEnemyDead && BlacksiteWorld.Same(contact, this)
                    && Vector3.Distance(transform.position, contact.transform.position) < BlacksiteRaidPolicy.RegroupThreatRange
                    && !BlacksiteHazardAwareness.ExcludeFromRifleTargets(contact))
                { seenEnemies++; if (seenPlayers == 0) threat = contact.transform.position; }
                else if (row.Class == BlacksiteWorldClass.Player && row.Subject is GameNetcodeStuff.PlayerControllerB player
                    && CanConsiderPlayer(player)
                    && Vector3.Distance(transform.position, player.transform.position) < BlacksiteRaidPolicy.RegroupThreatRange)
                { seenPlayers++; threat = player.transform.position; }
            }
            int threats = BlacksiteRaidPolicy.RegroupThreats(seenEnemies, seenPlayers);
            if (!BlacksiteRaidPolicy.RegroupOutnumbered(threats, allies, true)) return;
            if (!TryFindRetreatPoint(threat, out var destination)) return;
            _raidRegroupPoint = destination; _raidRegroupUntil = Time.time + 8f; _raidReconsiderAt = Time.time + 24f;
            BlacksiteDiagnostics.Log("Raid", $"member={MemberIndex} breaking contact for regroup: threats={threats}, support={allies}, point={destination}, enemies={seenEnemies}, players={seenPlayers}.");
            // #1293 R3: breaking contact is a withdrawal the brain has to choose; its end
            // (_raidRegroupUntil) is the Withdrawal executor's wake.
            RequestDecision(BlacksiteDecisionCause.Outcome);
            AnnounceRaidAction();
        }
        private bool TryFindRetreatPoint(Vector3 threat, out Vector3 destination)
        {
            destination = transform.position; float best = float.NegativeInfinity;
            Vector3 away = Vector3.ProjectOnPlane(transform.position - threat, Vector3.up).normalized;
            for (int i = -2; i <= 2; i++)
            {
                var point = transform.position + Quaternion.Euler(0, i * 35f, 0) * away * 7f;
                if (!NavMesh.SamplePosition(point, out var nav, 1f, NavMesh.AllAreas)) continue;
                // The member's one scratch path (BlacksiteSoldierAI._navPath), consumed here.
                _navPath.ClearCorners();
                if (!BlacksiteReachability.Walk(BlacksiteTraveller.Soldier(this), transform.position, nav.position, _navPath).Reachable) continue;
                // #1305 H (C3): and its route clear of every live hazard disc, so a retreat is never
                // a goal the admission would only detour or refuse.
                if (!Navigator.HazardRouteClear(nav.position)) continue;
                // #1015 S1: the same null-tolerant mask BlacksiteNavigator.SnapToNavMesh already
                // takes. This is the only unguarded StartOfRound.Instance read on the hold path, and
                // the lease rewrite reaches it in states the old blind stop did not - a native
                // fixture with no StartOfRound at all threw here.
                int coverMask = StartOfRound.Instance != null
                    ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
                bool cover = Physics.Linecast(threat + Vector3.up * 1.5f, nav.position + Vector3.up * 1.2f,
                    coverMask, QueryTriggerInteraction.Ignore);
                float score = Vector3.Distance(threat, nav.position) + (cover ? 12f : 0f);
                if (score > best) { destination = nav.position; best = score; }
            }
            return best > float.NegativeInfinity;
        }
        private bool TryExecuteRaidWithdrawal()
        {
            if (_transportLost) return TickStrandedServer();
            if (!IsRaidWithdrawing && Time.time >= _raidRegroupUntil && _combatController != null) _combatController.DefendingDuringWithdrawal = false;
            if (IsRaidWithdrawing || Time.time < _raidRegroupUntil)
                if (TryDefendDuringWithdrawal()) return true;
            if (IsRaidWithdrawing && TryGetSquadDirector(out var director) && TryGetMission(out var mission))
            {
                if (!mission.Objectives.TryGetOrder(this, out var order)) return false;
                // Retain carried loot. A cart still releases when combat or collision requires it.
                ExecuteSquadOrder(director, order); return true;
            }
            if (Time.time < _raidRegroupUntil)
            {
                SetDestinationThrottled(_raidRegroupPoint, issuer: "order:Regroup");
                return true;
            }
            return false;
        }
    }
}
