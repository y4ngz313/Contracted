using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.AI;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Contracts.Whistleblower
{
    public sealed partial class WhistleblowerEnemyAI
    {
        private const int AssessingState = 6; // Append: existing network state IDs stay stable.
        private float _assessmentUntil;
        private float _nextFugitiveCheckAt;
        private float _nextFugitiveSafetyAt;
        private int _fugitiveNodeCursor;
        private float _fugitiveProgressAt;
        private Vector3 _fugitiveProgressPosition;
        private bool _fugitiveRetreat;
        private readonly NavMeshPath _fugitivePath = new NavMeshPath();

        private void UpdateAssessmentServer()
        {
            StopAgent();
            if (agent != null)
                agent.updateRotation = false;
            if (IsLivingInteriorPlayer(_combatTarget))
                FaceTarget(_combatTarget);
            if (Time.time < _assessmentUntil)
                return;
            SwitchToBehaviourState(CorneredState);
            _nextFugitiveCheckAt = 0f;
            UpdateDefensiveServer();
        }

        private void UpdateDefensiveServer()
        {
            if (agent != null)
                agent.updateRotation = false;
            StopAgent();
            // Hold the strike's facing and target through recovery. Re-selecting the
            // nearest player every frame twists a punch between two crew members.
            if (Time.time < _movementLockedUntil)
                return;
            _combatTarget = FindClosestLivingInteriorPlayer(transform.position, out float distance);
            if (_combatTarget != null)
                FaceTarget(_combatTarget);

            if (Time.time >= _nextFugitiveCheckAt)
            {
                _nextFugitiveCheckAt = Time.time + WhistleblowerFugitivePolicy.RouteRetrySeconds;
                if (TryFindFugitiveExit(out Vector3 exit))
                {
                    BeginQuietRetreatServer(_combatTarget);
                    // The long, sliced search may take several frames. Start on the
                    // checked local exit instead of its former blind 32 m fallback.
                    _escapeDestination = exit;
                    _fugitiveRetreat = true;
                    _fugitiveProgressPosition = transform.position;
                    _fugitiveProgressAt = Time.time;
                    CommitFugitiveDestination();
                    return;
                }
            }

            if (_combatTarget == null)
                return;
            if (!_corneredTipShown)
            {
                _corneredTipShown = true;
                ShowTipClientRpc(TipTitle, "His escape is blocked. Keep your distance.", true);
            }
            if (distance <= PunchRange && Time.time >= _nextPunchAt && HasClearPunchLine(_combatTarget))
            {
                BeginPunchServer(_combatTarget);
                // Reassess after the entire strike/recovery rather than immediately
                // replacing a punch with locomotion or a second action.
                _nextFugitiveCheckAt = _movementLockedUntil;
                _nextPunchAt = Mathf.Max(_nextPunchAt, _movementLockedUntil + 0.65f);
            }
        }

        private bool HasClearPunchLine(PlayerControllerB player)
        {
            if (!IsLivingInteriorPlayer(player) || StartOfRound.Instance == null)
                return false;
            return !Physics.Linecast(transform.position + Vector3.up,
                player.transform.position + Vector3.up,
                StartOfRound.Instance.collidersAndRoomMask, QueryTriggerInteraction.Ignore);
        }

        private bool TryFindFugitiveExit(out Vector3 exit)
        {
            exit = transform.position;
            if (agent == null || !agent.enabled || !agent.isOnNavMesh)
                return false;
            Vector3 away = _combatTarget != null
                ? transform.position - _combatTarget.transform.position : transform.forward;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f)
                away = transform.forward;
            away.Normalize();
            float best = float.MinValue;
            // Eight local directions plus up to four nearby AI nodes. The node
            // fallback finds bends that no radial sample can land inside. Scan and
            // path budgets are bounded, and subsequent retries rotate the node scan.
            for (int i = 0; i < 8; i++)
                ConsiderFugitiveExit(transform.position + Quaternion.Euler(0f, i * 45f, 0f) * away * 6f,
                    ref best, ref exit);
            GameObject[] nodes = RoundManager.Instance?.insideAINodes;
            int checkedNodes = 0;
            for (int scanned = 0; nodes != null && scanned < Mathf.Min(nodes.Length, 48) && checkedNodes < 4; scanned++)
            {
                GameObject node = nodes[_fugitiveNodeCursor++ % nodes.Length];
                if (node == null)
                    continue;
                float distance = Vector3.Distance(transform.position, node.transform.position);
                if (distance < 4f || distance > 12f)
                    continue;
                checkedNodes++;
                ConsiderFugitiveExit(node.transform.position, ref best, ref exit);
            }
            return best > float.MinValue;
        }

        private void ConsiderFugitiveExit(Vector3 candidate, ref float best, ref Vector3 exit)
        {
            if (!NavMesh.SamplePosition(candidate, out NavMeshHit sampled, 0.75f, agent.areaMask)
                || Mathf.Abs(sampled.position.y - transform.position.y) > 1.75f
                || !agent.CalculatePath(sampled.position, _fugitivePath)
                || _fugitivePath.status != NavMeshPathStatus.PathComplete)
                return;
            float length = PathLength(_fugitivePath);
            if (length < 4f || length > 12f || !FugitivePathClearsCrew(_fugitivePath))
                return;
            float gain = _combatTarget != null
                ? Vector3.Distance(sampled.position, _combatTarget.transform.position)
                    - Vector3.Distance(transform.position, _combatTarget.transform.position)
                : 6f;
            if (gain < 2f)
                return;
            float score = gain * 2f - length * 0.2f;
            if (score <= best)
                return;
            best = score;
            exit = sampled.position;
        }

        private bool FugitivePathClearsCrew(NavMeshPath path)
        {
            Vector3[] corners = path.corners;
            if (corners.Length < 2)
                return false;
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null)
                return true;
            foreach (PlayerControllerB player in players)
            {
                if (!IsLivingInteriorPlayer(player))
                    continue;
                Vector3 p = player.transform.position;
                for (int i = 1; i < corners.Length; i++)
                {
                    Vector3 a = corners[i - 1], b = corners[i];
                    if (!WhistleblowerFugitivePolicy.SegmentClearsPlayer(
                        a.x, a.y, a.z, b.x, b.y, b.z, p.x, p.y, p.z))
                        return false;
                }
            }
            return true;
        }

        private void CommitFugitiveDestination()
        {
            SetAgentSpeed(EscapeSpeed, stopped: false, stoppingDistance: 0.75f);
            if (agent != null)
                agent.updateRotation = true;
            SetDestinationToPosition(_escapeDestination, checkForPath: false);
            CommitDestination();
        }

        // Called only for an ordinary flee-first retreat. Flashbang and Blacksite
        // custody escapes retain their existing traversal and outcome semantics.
        private bool UpdateFugitiveRetreatServer()
        {
            if (!_fugitiveRetreat)
                return false;
            if (Vector3.Distance(transform.position, _fugitiveProgressPosition) >= 0.35f)
            {
                _fugitiveProgressPosition = transform.position;
                _fugitiveProgressAt = Time.time;
            }
            if (Time.time < _nextFugitiveSafetyAt)
                return false;
            _nextFugitiveSafetyAt = Time.time + 0.25f;
            bool stuck = Time.time - _fugitiveProgressAt >= WhistleblowerFugitivePolicy.StuckSeconds;
            bool safe = agent != null && agent.enabled && agent.isOnNavMesh
                && agent.CalculatePath(_escapeDestination, _fugitivePath)
                && _fugitivePath.status == NavMeshPathStatus.PathComplete
                && FugitivePathClearsCrew(_fugitivePath);
            if (safe && !stuck)
                return false;

            // Stop first. The defensive state's slower, bounded route scan chooses
            // another exit, so an intercepted runner does not plough into the crew
            // for the whole route-retry interval.
            _fugitiveRetreat = false;
            _quietRetreat = false;
            CancelEscapeSearch();
            ResetBurstState();
            StopAgent();
            SwitchToBehaviourState(CorneredState);
            ContractsBootstrap.Log?.LogInfo("[MoonContracts] Whistleblower escape blocked; holding defensively.");
            return true;
        }
    }
}
