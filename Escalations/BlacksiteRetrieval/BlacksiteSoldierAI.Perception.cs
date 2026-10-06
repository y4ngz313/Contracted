using System.Collections.Generic;
using GameNetcodeStuff;
using UnityEngine;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    public sealed partial class BlacksiteSoldierAI
    {
        /// <summary>
        /// #1293 R1: this member's bit in the squad table's per-row sight and announce masks, 0..31.
        /// The squad director assigns a stable slot when the member registers and frees it with him;
        /// -1 is none. Host only.
        /// </summary>
        internal int PerceptionSlot { get; set; } = -1;

        // #1293 R1: the director that assigned PerceptionSlot - the squad whose table the slot
        // indexes. A member who has lost his transport keeps it: he reads that table through it,
        // and once the ship has taken the mission away he drives its pass (Survivor.cs).
        private BlacksiteSquadDirector _perceptionDirector;

        // The director's one write of both: its slot on register, (null, -1) on unregister and reset.
        internal void JoinPerception(BlacksiteSquadDirector director, int slot)
        {
            _perceptionDirector = director;
            PerceptionSlot = slot;
        }

        /// <summary>
        /// #1293 R1: the squad table. Host only: a client has no director and so no table. A
        /// stranded member - his transport lost, his mission resolved or gone with the ship - still
        /// sees, selects and fires through the table of the squad that registered him.
        /// </summary>
        internal bool TryGetWorld(out BlacksiteWorldModel world)
        {
            world = null;
            if (!IsServerAuthority()) return false;
            if (TryGetSquadDirector(out BlacksiteSquadDirector director)) world = director.World;
            else if (_transportLost && _perceptionDirector != null) world = _perceptionDirector.World;
            return world != null;
        }

        /// <summary>
        /// #1293 R1: the squad table holds a threat. What one member knows the squad knows; roles
        /// differ only in what they do with it.
        /// </summary>
        internal bool IsAware => TryGetWorld(out BlacksiteWorldModel world) && world.HasThreat;

        /// <summary>
        /// #1293 R1: this member's ONE look at the world per squad perception pass. The director
        /// calls it on every living member every <see cref="BlacksiteSightPolicy.VisualTickSeconds"/>.
        /// Every candidate - the players and the spawned enemies (the witness's Live row among them)
        /// - is tested once, and the answer is that
        /// candidate's row: Saw sets this member's sight bit, a player heard and not seen is Heard,
        /// and a row whose bit this pass did not refresh goes through Unseen, which keeps it
        /// for <see cref="BlacksiteSightPolicy.VisualGraceSeconds"/>. Nothing is remembered here: the
        /// table is the memory. No allocation outside diagnostics. #1293 R3: the pass ends with the
        /// member's combat acquisition, whose answer changing is its World event.
        /// </summary>
        internal void PerceiveServer(BlacksiteWorldModel world, float now)
        {
            int slot = PerceptionSlot;
            if (world == null || slot < 0) return;
            // A member that cannot look - not deployed, boarded, captured, consumed - sees nothing,
            // and its bits lapse through the same grace as any lost sighting.
            bool looking = _initialized && _spawnState.Value.IsConfigured && !HasBoardedShip && AllowsCombatLifecycle;
            if (looking)
            {
                // #314: the wide cone while the squad table holds a threat or this member is fighting.
                float threshold = world.HasThreat || (_combatController != null && _combatController.HasCombatIntent)
                    ? AlertedVisionDotThreshold : VisionDotThreshold;
                PerceivePlayers(world, slot, threshold, now);
                PerceiveEnemies(world, slot, threshold, now);
            }
            ReleaseUnseen(world, slot, now);
            if (!looking) return;
            ObserveHazardRowsServer(world, now);
            NoteCombatTargetServer();
        }

        // #1293 R3: whether this member's combat acquisition answered a target at the last pass; it
        // can change with no table change (a lease, reach, a capture), and either edge is World.
        private bool _passCombatTarget;

        private void NoteCombatTargetServer()
        {
            bool target = TryFindCombatTarget(out _, out _);
            if (target == _passCombatTarget) return;
            _passCombatTarget = target;
            RequestDecision(BlacksiteDecisionCause.World);
        }

        // The eligibility and world gate (CanConsiderPlayer: the 12 m post is no part of it),
        // MaxAggroRange, then sight; a player not in sight may be heard.
        private void PerceivePlayers(BlacksiteWorldModel world, int slot, float threshold, float now)
        {
            PlayerControllerB[] players = StartOfRound.Instance != null ? StartOfRound.Instance.allPlayerScripts : null;
            if (players == null) return;
            Vector3 here = transform.position;
            for (int i = 0; i < players.Length; i++)
            {
                PlayerControllerB player = players[i];
                if (!CanConsiderPlayer(player)) continue;
                float distance = Vector3.Distance(here, player.transform.position);
                if (distance > MaxAggroRange) continue;
                ulong key = player.NetworkObjectId;
                bool tracked = world.TryGet(key, out BlacksiteWorldRow row) && row.SeenByMember(slot);
                bool seen = EvaluatePlayerSight(player, distance, tracked, threshold, out float dot, out string rays);
                // #874 S10.2 P3: the consumer grace is the table's now; it is reported, not decided, here.
                bool grace = !seen && tracked && BlacksiteSightPolicy.HeldByGrace(false, row.MemberSawAt(slot), now);
                string target = null;
                if (BlacksiteDiagnostics.Enabled)
                {
                    target = BlacksiteDiagnosticFormat.Target(player);
                    if (float.IsNaN(dot)) dot = MeasureViewDot(GetPlayerAimPoint(player));
                    BlacksitePerceptionDiagnostics.NoteSighting(MemberIndex, NetworkObjectId, target, seen, distance, dot,
                        BlacksitePerceptionDiagnostics.SourceVisual, rays, grace);
                }
                if (seen)
                {
                    BlacksiteWorldChange change = world.Saw(key, player, BlacksiteWorldClass.Player,
                        player.transform.position, !player.isInsideFactory, NetworkObjectId, slot, now);
                    NoteWorldChange(world, key, change);
                    NoteOwnSight(key, tracked, change);
                    if (target != null)
                        BlacksitePerceptionDiagnostics.NoteContact(MemberIndex, NetworkObjectId, target,
                            BlacksitePerceptionDiagnostics.SourceVisual, distance, exact: true);
                }
                else if (!grace && now - _lastStimulusTime >= 0.8f && CanHearPlayer(player, distance))
                {
                    // #1293 R1: hearing rides the noise reaction's own clock. The row is written only
                    // when the reaction is taken, so an audible player is not tracked through walls
                    // at the pass rate.
                    float heardBefore = _lastStimulusTime;
                    ReactToStimulusServer(BlacksiteSoldierReactionKind.Noise, player.transform.position,
                        ulong.MaxValue, broadcastToSquad: true);
                    if (_lastStimulusTime != heardBefore)
                        NoteWorldChange(world, key, world.Heard(key, player, BlacksiteWorldClass.Player,
                            player.transform.position, !player.isInsideFactory, NetworkObjectId, now));
                }
            }
        }

        // The SIGHT gate (CanSightEnemy: the escort objective is seen though no weapon may touch
        // him), EnemyVisionRange, the view cone and the single root ray. A Live row keeps the class
        // its publisher gave it (the witness, a hazard); every other sighting is a Monster.
        private void PerceiveEnemies(BlacksiteWorldModel world, int slot, float threshold, float now)
        {
            List<EnemyAI> enemies = RoundManager.Instance != null ? RoundManager.Instance.SpawnedEnemies : null;
            if (enemies == null) return;
            Vector3 here = transform.position;
            Vector3 eye = GetEyePosition();
            for (int i = 0; i < enemies.Count; i++)
            {
                EnemyAI enemy = enemies[i];
                if (!CanSightEnemy(enemy)) continue;
                float distance = Vector3.Distance(here, enemy.transform.position);
                if (distance > EnemyVisionRange) continue;
                ulong key = enemy.NetworkObjectId;
                bool known = world.TryGet(key, out BlacksiteWorldRow row);
                BlacksiteWorldClass cls = known && row.Source == BlacksiteWorldSource.Live ? row.Class : BlacksiteWorldClass.Monster;
                // The escort objective is the manifest's Witness row; with none published (aboard,
                // delivered) he is nobody's contact.
                if (cls == BlacksiteWorldClass.Monster && BlacksiteThreatAdapterRegistry.IsEscortObjective(enemy)) continue;
                Vector3 direction = enemy.transform.position + Vector3.up - eye;
                if (direction.sqrMagnitude <= .01f) continue;
                float dot = Vector3.Dot(transform.forward, direction.normalized);
                bool seen = dot >= threshold && HasLineOfSight(enemy);
                string target = BlacksiteDiagnostics.Enabled ? BlacksiteDiagnosticFormat.Target(enemy) : null;
                if (target != null)
                    BlacksitePerceptionDiagnostics.NoteSighting(MemberIndex, NetworkObjectId, target, seen, distance, dot,
                        BlacksitePerceptionDiagnostics.SourceVisual);
                if (!seen) continue;
                bool hadSight = known && row.SeenByMember(slot);
                BlacksiteWorldChange change = world.Saw(key, enemy, cls, enemy.transform.position, enemy.isOutside,
                    NetworkObjectId, slot, now);
                NoteWorldChange(world, key, change);
                NoteOwnSight(key, hadSight, change);
                // #990: ONE call-out per witness contact, by the first member to see him; the manifest
                // re-arms it only by forgetting the row.
                if (cls == BlacksiteWorldClass.Witness && row.AnnouncedMask == 0)
                {
                    row.Announce(slot);
                    AnnounceWitnessSighted();
                }
                else if (cls == BlacksiteWorldClass.Monster && target != null)
                    BlacksitePerceptionDiagnostics.NoteContact(MemberIndex, NetworkObjectId, target,
                        BlacksitePerceptionDiagnostics.SourceVisual, distance, exact: true);
            }
        }

        // Every row this member's eyes held that this pass did not refresh. Unseen keeps the bit for
        // BlacksiteSightPolicy.VisualGraceSeconds, so one blocked ray is not a lost contact.
        private void ReleaseUnseen(BlacksiteWorldModel world, int slot, float now)
        {
            var rows = world.Rows;
            for (int i = 0; i < rows.Count; i++)
            {
                BlacksiteWorldRow row = rows[i];
                if (row.SeenByMember(slot) && row.MemberSawAt(slot) < now)
                    world.Unseen(row.Key, slot, now);
            }
        }

        // A first-hand sense that is not sight - a hit - writes the contact's row as Heard where it
        // stands: it never makes anybody fire-eligible, and the pass's eyes make it Saw.
        private void NoteHeardServer(ulong key, object subject, BlacksiteWorldClass cls, Vector3 position, bool outside)
        {
            if (TryGetWorld(out BlacksiteWorldModel world))
                NoteWorldChange(world, key, world.Heard(key, subject, cls, position, outside, NetworkObjectId, Time.time));
        }

        // What THIS member does with a transition its own senses or a report wrote: the [UAT][World]
        // line for a contact (player, monster, witness; hazard rows churn with no
        // reader of the line) and, for a sighting of a player or a monster, the squad's contact
        // bark (the director keeps its per-class cooldown). The squad-wide re-decision is the
        // director's, on BlacksiteWorldModel.ChangeCount.
        private void NoteWorldChange(BlacksiteWorldModel world, ulong key, BlacksiteWorldChange change)
        {
            if ((change != BlacksiteWorldChange.New && change != BlacksiteWorldChange.Reacquired)
                || !world.TryGet(key, out BlacksiteWorldRow row))
                return;
            bool contact = row.Class == BlacksiteWorldClass.Player || row.Class == BlacksiteWorldClass.Monster;
            if (BlacksiteDiagnostics.Enabled && (contact || row.Class == BlacksiteWorldClass.Witness))
                BlacksiteDiagnostics.Log("World",
                    $"{(change == BlacksiteWorldChange.New ? "new" : "reacquired")} entity={key} class={row.Class} source={row.Source} by={MemberIndex}");
            if (contact && row.Source == BlacksiteWorldSource.Saw && TryGetSquadDirector(out BlacksiteSquadDirector director))
                director.AnnounceContactServer(this, row);
        }

        // #1293 R3: own eyes retaking the engaged contact a squadmate still held is only Refreshed
        // (no fan-out), so it is this member's own World event.
        private void NoteOwnSight(ulong key, bool hadSight, BlacksiteWorldChange change)
        {
            if (!hadSight && change == BlacksiteWorldChange.Refreshed
                && _combatController != null && _combatController.IsEngagedRow(key))
                RequestDecision(BlacksiteDecisionCause.World);
        }
    }
}
