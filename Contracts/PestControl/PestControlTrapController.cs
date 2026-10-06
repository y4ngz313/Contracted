using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts._Shared;

namespace Y4NGZCompany.Contracts.PestControl
{
    /// <summary>
    /// The networked Pest Control trap. The host spawns one at a resolved
    /// <see cref="PestControlTrapPlacement"/>; every peer builds its OWN copy of the geometry
    /// from the replicated footprint plus the NetworkObject's own spawn transform, so no mesh,
    /// collider, or rope ever crosses the wire.
    ///
    /// What IS replicated: the footprint (a NetworkVariable, so a late joiner gets it in the
    /// spawn payload), the motion state (server-authoritative, drives the local animation on
    /// every peer), and the capture tally. The lever routes through a ServerRpc; the server is
    /// the only thing that decides whether a pull drops, winches, or is ignored.
    ///
    /// The production contract lifecycle owns spawning and cleanup; the debug API uses the same
    /// controller and registry so diagnostics continue to exercise the real network path.
    /// </summary>
    internal sealed class PestControlTrapController : NetworkBehaviour
    {
        internal enum TrapState : byte
        {
            Armed = 0,
            Dropping = 1,
            Dropped = 2,
            Raising = 3,
            Rearming = 4
        }

        /// <summary>Cooldown after a winch completes before the lever can drop the corral again.</summary>
        internal const float RearmSeconds = 10f;

        /// <summary>Beat between the cage landing and the capture being called in.</summary>
        private const float CaptureBeatSeconds = 1.5f;

        /// <summary>Generosity on the capture box: a monster clipping the edge still counts.</summary>
        private const float CaptureMarginMeters = 0.35f;

        /// <summary>How far inside the fence a contained specimen is held, so it never sits in a wall.</summary>
        private const float ContainmentInsetMeters = 0.35f;

        private const float CaptureHeightBelow = 1.5f;
        private const float CaptureHeightAbove = 3.5f;
        private const float SpecimenCheckIntervalSeconds = 0.15f;
        private const float SlamNoiseRange = 22f;
        private const float SlamNoiseLoudness = 0.95f;
        private const float AudioCarryMeters = 45f;

        private static readonly List<PestControlTrapController> Active = new List<PestControlTrapController>();

        private readonly NetworkVariable<float> _footprint = new NetworkVariable<float>(0f);
        private readonly NetworkVariable<float> _ceilingClearance = new NetworkVariable<float>(PestControlTrapPlacer.MinCeilingMeters);
        private readonly NetworkVariable<byte> _state = new NetworkVariable<byte>((byte)TrapState.Armed);
        private readonly NetworkVariable<int> _capturedTotal = new NetworkVariable<int>(0);
        private readonly NetworkVariable<bool> _specimenLured = new NetworkVariable<bool>(false);

        private PestControlTrapPreview _preview;
        private AudioSource _audio;
        private ContractAudioPropagation _propagation;
        private Coroutine _rearmRoutine;
        private Coroutine _captureRoutine;

        /// <summary>
        /// #756. How often a caged specimen is handed a fresh point inside the fence to walk to,
        /// and how far that point may be nudged to land on the NavMesh island under the cage.
        /// </summary>
        private const float PaceIntervalMinSeconds = 1.2f;
        private const float PaceIntervalMaxSeconds = 2.8f;
        private const float PaceSampleRadiusMeters = 1.0f;

        /// <summary>Server-only. One live specimen the cage is holding, plus its pacing clock.</summary>
        private sealed class ContainedSpecimen
        {
            public EnemyAI Enemy;
            public float NextPaceAt;
        }

        /// <summary>Server-only. Live specimens the cage is currently holding.</summary>
        private readonly List<ContainedSpecimen> _contained = new List<ContainedSpecimen>(4);
        private string _lastCaptureSummary = "none";
        private float _nextSpecimenCheckAt;

        /// <summary>
        /// #406. Server-only: who pulled the lever that dropped the cage currently in the air.
        /// The capture that follows is credited to them. <c>ulong.MaxValue</c> means the drop came
        /// from the debug API (or a pull whose sender was lost), and the credit fans out crew-wide.
        ///
        /// The id outlives the pull by the cage drop, so its owner may leave before the capture
        /// resolves. It is deliberately NOT re-validated here: the roster check lives at the
        /// single choke point in <c>ContractProgressionEvents.ResolveCredited</c>, which reroutes
        /// a departed puller's credit to the crew. A puller who merely died still keeps it.
        /// </summary>
        private ulong _dropInitiatorClientId = ContractProgressionEventIds.UnknownPlayerClientId;

        /// <summary>Server-only. Running capture number this round; 1 is the completion beat.</summary>
        private int _captureSequence;

        /// <summary>Most recently spawned live trap. Production owns at most one per round.</summary>
        internal static PestControlTrapController Instance
        {
            get
            {
                for (int i = Active.Count - 1; i >= 0; i--)
                {
                    if (Active[i] != null)
                        return Active[i];
                }

                return null;
            }
        }

        internal TrapState State => (TrapState)_state.Value;

        internal float FootprintMeters => _footprint.Value;

        internal int CapturedTotal => _capturedTotal.Value;

        internal bool SpecimenLured => _specimenLured.Value;

        internal Vector3 MarkerPosition => transform.position + Vector3.up * 0.15f;

        internal string LastCaptureSummary => _lastCaptureSummary;

        internal PestControlTrapPreview Preview => _preview;

        /// <summary>Host-only. Call immediately after <c>NetworkObject.Spawn</c>.</summary>
        internal void ServerInitialize(float footprintMeters, float ceilingClearanceMeters)
        {
            if (!IsServer)
                return;

            // Ceiling first: the footprint write is what wakes the clients' lazy assemble, so the
            // cage would otherwise be built from the default clearance.
            _ceilingClearance.Value = Mathf.Max(PestControlTrapPlacer.MinCeilingMeters, ceilingClearanceMeters);
            _footprint.Value = Mathf.Clamp(
                footprintMeters,
                PestControlTrapPlacer.MinFootprintMeters * 0.5f,
                PestControlTrapPlacer.MaxFootprintMeters);
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            if (!Active.Contains(this))
                Active.Add(this);
            PestControlContract.RegisterTrap(this);

            _footprint.OnValueChanged += OnFootprintChanged;
            _state.OnValueChanged += OnStateChanged;

            // The server sets the footprint the frame AFTER Spawn (a NetworkVariable cannot be
            // written before the behaviour has a NetworkManager), so the spawn payload may still
            // carry 0. Build lazily instead of assuming an order: whichever of these two paths
            // sees a usable value first wins, and the other is a no-op.
            if (_footprint.Value > 0f)
                EnsureAssembled();
        }

        public override void OnNetworkDespawn()
        {
            PestControlContract.UnregisterTrap(this);
            // The specimens outlive the trap: the cage going away has to stop pinning them, or the
            // clamp would keep dragging them back to a trap that no longer exists.
            _contained.Clear();
            _footprint.OnValueChanged -= OnFootprintChanged;
            _state.OnValueChanged -= OnStateChanged;
            base.OnNetworkDespawn();
        }

        public override void OnDestroy()
        {
            PestControlContract.UnregisterTrap(this);
            Active.Remove(this);
            base.OnDestroy();
        }

        private void OnFootprintChanged(float previous, float current)
        {
            EnsureAssembled();
        }

        private void EnsureAssembled()
        {
            if (_preview != null || _footprint.Value <= 0f)
                return;

            _preview = PestControlTrapAssembler.Build(
                transform,
                PestControlTrapAssembler.NetworkedRootName,
                transform.position,
                transform.rotation.eulerAngles.y,
                _footprint.Value,
                _ceilingClearance.Value,
                interactive: true);
            if (_preview == null)
                return;

            _preview.LeverPulled = OnLocalLeverPulled;
            _preview.FenceLanded = OnLocalFenceLanded;
            _preview.FenceRaised = OnLocalFenceRaised;
            _preview.FenceRaiseBegan = PlayWinchAudio;

            BuildAudio();
            _preview.SnapTo(StateToMotion((TrapState)_state.Value));
            ApplyLeverTip((TrapState)_state.Value);
        }

        private void BuildAudio()
        {
            if (_audio != null)
                return;

            _audio = gameObject.AddComponent<AudioSource>();
            _audio.playOnAwake = false;
            _audio.loop = false;
            _propagation = ContractSpatialAudio.ApplyFacilityCarryProfile(_audio, AudioCarryMeters);
            PestControlTrapAudio.EnsureLoaded(this);
        }

        private void Update()
        {
            _propagation?.Tick();
            if (!IsServer)
                return;

            ServerObserveSpecimenOnPlatform(force: false);
            ServerTickContainment();
        }

        // ---------------------------------------------------------------- lever

        private void OnLocalLeverPulled(PlayerControllerB player)
        {
            TrapState state = (TrapState)_state.Value;
            bool pushingDown = state == TrapState.Armed;
            if (state == TrapState.Armed || state == TrapState.Dropped)
            {
                _preview?.BeginLeverVisual(pushingDown, 0f);
                PestControlLeverPlayerAnimation.Play(player, pushingDown, 0f, _preview);
                // The puller hears the throw on the frame they press it; the ClientRpc below skips
                // them so the same pull is never heard twice on their machine.
                PlayLeverAudio();
            }

            PullLeverServerRpc();
        }

        [ServerRpc(RequireOwnership = false)]
        internal void PullLeverServerRpc(ServerRpcParams rpcParams = default)
        {
            TrapState state = (TrapState)_state.Value;
            bool pushingDown = state == TrapState.Armed;
            // #406: the sender used to be read for the animation and then discarded. It is the
            // only identification of the person whose pull causes the capture below.
            if (!ServerHandlePull(rpcParams.Receive.SenderClientId))
                return;

            double startedAt = NetworkManager != null ? NetworkManager.ServerTime.Time : 0d;
            PlayLeverAnimationClientRpc(rpcParams.Receive.SenderClientId, pushingDown, startedAt);
        }

        [ClientRpc]
        private void PlayLeverAnimationClientRpc(ulong playerClientId, bool pushingDown, double serverStartedAt)
        {
            EnsureAssembled();
            double serverNow = NetworkManager != null ? NetworkManager.ServerTime.Time : serverStartedAt;
            float elapsed = Mathf.Max(0f, (float)(serverNow - serverStartedAt));
            _preview?.BeginLeverVisual(pushingDown, elapsed);
            PestControlLeverPlayerAnimation.Play(playerClientId, pushingDown, elapsed, _preview);
            if (NetworkManager == null || playerClientId != NetworkManager.LocalClientId)
                PlayLeverAudio();
        }

        /// <summary>
        /// Server-side decision point shared by the lever and the debug API.
        /// <paramref name="initiatorClientId"/> is the player whose pull this is, or
        /// <see cref="ContractProgressionEventIds.UnknownPlayerClientId"/> when there is no
        /// identifiable actor - the resulting capture credit then fans out crew-wide.
        /// </summary>
        internal bool ServerHandlePull(ulong initiatorClientId)
        {
            if (!IsServer)
                return false;

            switch ((TrapState)_state.Value)
            {
                case TrapState.Armed:
                    ServerObserveSpecimenOnPlatform(force: true);
                    _dropInitiatorClientId = initiatorClientId;
                    _state.Value = (byte)TrapState.Dropping;
                    return true;
                case TrapState.Dropped:
                    ServerReleaseContained("lever winch");
                    _state.Value = (byte)TrapState.Raising;
                    return true;
                default:
                    return false;
            }
        }

        internal bool ServerRequestDrop()
        {
            if (!IsServer || (TrapState)_state.Value != TrapState.Armed)
                return false;

            ServerObserveSpecimenOnPlatform(force: true);
            // Debug drop: nobody pulled a lever, so the resulting capture pays crew-wide.
            _dropInitiatorClientId = ContractProgressionEventIds.UnknownPlayerClientId;
            _state.Value = (byte)TrapState.Dropping;
            return true;
        }

        internal bool ServerRequestRaise()
        {
            if (!IsServer || (TrapState)_state.Value != TrapState.Dropped)
                return false;

            ServerReleaseContained("debug raise");
            _state.Value = (byte)TrapState.Raising;
            return true;
        }

        // ---------------------------------------------------------------- state

        private void OnStateChanged(byte previous, byte current)
        {
            var state = (TrapState)current;
            EnsureAssembled();
            ApplyLeverTip(state);
            if (_preview == null)
                return;

            switch (state)
            {
                case TrapState.Dropping:
                    _preview.TryDrop(out _);
                    break;
                case TrapState.Raising:
                    // The winch cue rides FenceRaiseBegan so it lands when the drum actually turns,
                    // rather than on the frame the state replicated.
                    _preview.TryRaise(out _);
                    break;
                case TrapState.Dropped:
                    // Only reached without the animation when a peer's state jumped (late RPC
                    // ordering); the snap keeps geometry and replicated state consistent.
                    if (_preview.State != PestControlTrapPreview.TrapMotionState.Dropped)
                        _preview.SnapTo(PestControlTrapPreview.TrapMotionState.Dropped);
                    break;
                case TrapState.Armed:
                case TrapState.Rearming:
                    if (_preview.State != PestControlTrapPreview.TrapMotionState.Rest
                        && _preview.State != PestControlTrapPreview.TrapMotionState.Raising)
                        _preview.SnapTo(PestControlTrapPreview.TrapMotionState.Rest);
                    break;
            }
        }

        private void ApplyLeverTip(TrapState state)
        {
            if (_preview == null)
                return;

            switch (state)
            {
                case TrapState.Armed:
                    _preview.SetLeverHoverTip("Drop cage : [LMB]", true);
                    break;
                case TrapState.Dropped:
                    _preview.SetLeverHoverTip("Raise cage : [LMB]", true);
                    break;
                default:
                    _preview.SetLeverHoverTip("[ Resetting winch... ]", false);
                    break;
            }
        }

        private static PestControlTrapPreview.TrapMotionState StateToMotion(TrapState state)
        {
            return state == TrapState.Dropped || state == TrapState.Dropping
                ? PestControlTrapPreview.TrapMotionState.Dropped
                : PestControlTrapPreview.TrapMotionState.Rest;
        }

        // ------------------------------------------------------------- landing

        private void OnLocalFenceLanded()
        {
            PlaySlamAudio();

            if (!IsServer)
                return;

            RoundManager.Instance?.PlayAudibleNoise(transform.position, SlamNoiseRange, SlamNoiseLoudness);
            _state.Value = (byte)TrapState.Dropped;

            if (_captureRoutine != null)
                StopCoroutine(_captureRoutine);
            _captureRoutine = StartCoroutine(ServerResolveCapture());
        }

        private void OnLocalFenceRaised()
        {
            if (!IsServer)
                return;

            _state.Value = (byte)TrapState.Rearming;
            if (_rearmRoutine != null)
                StopCoroutine(_rearmRoutine);
            _rearmRoutine = StartCoroutine(ServerRearm());
        }

        private IEnumerator ServerRearm()
        {
            yield return new WaitForSeconds(RearmSeconds);
            if (IsServer && (TrapState)_state.Value == TrapState.Rearming)
                _state.Value = (byte)TrapState.Armed;

            _rearmRoutine = null;
        }

        // ------------------------------------------------------------- capture

        private IEnumerator ServerResolveCapture()
        {
            var captured = new List<EnemyAI>(4);
            CollectCapturedEnemies(captured);
            if (captured.Count == 0)
            {
                _captureRoutine = null;
                yield break;
            }

            // Custody starts on the landing frame, not after the beat. The specimen is a live AI
            // that was already trying to leave, and anything between the cage sealing and the clamp
            // engaging is a window it can walk out of.
            for (int i = 0; i < captured.Count; i++)
            {
                Contain(captured[i]);
                PlayEnemyThrash(captured[i]);
            }

            // Thrash beat: let the monster be heard reacting to the cage before the contract calls
            // it contained, so the capture lands as an event rather than a silent counter tick.
            yield return new WaitForSeconds(CaptureBeatSeconds);

            var names = new List<string>(captured.Count);
            for (int i = 0; i < captured.Count; i++)
            {
                EnemyAI enemy = captured[i];
                if (enemy == null || enemy.isEnemyDead)
                    continue;

                names.Add(ResolveEnemyName(enemy));
            }

            if (names.Count == 0)
            {
                _captureRoutine = null;
                yield break;
            }

            bool firstCapture = _capturedTotal.Value == 0;
            _capturedTotal.Value += names.Count;
            _captureSequence++;
            string summary = BuildCaptureSummary(names);
            // One capture satisfies the contract, so only the first one is the completion beat.
            // #406: every capture is now credited - the first as the completion, the rest as
            // repeatable capture credit - and to the player whose pull dropped this cage.
            if (firstCapture)
            {
                ContractProgressionEvents.PublishPestControlCompleted(
                    _capturedTotal.Value, "containment cage", _dropInitiatorClientId);
            }
            else
            {
                ContractProgressionEvents.PublishPestControlCapture(
                    _captureSequence, _capturedTotal.Value, "containment cage", _dropInitiatorClientId);
            }
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.PestControl] PEST_CAPTURE count={names.Count} {summary} (contained, alive)");
            AnnounceCaptureClientRpc(summary);
            _captureRoutine = null;
        }

        // ----------------------------------------------------------- containment

        /// <summary>
        /// Takes a live specimen into custody. It stays spawned, alive, and audible for the rest of
        /// the round — the cage holds it rather than the capture deleting it.
        ///
        /// The previous implementation despawned the enemy through <c>DespawnEnemyOnServer</c> and
        /// covered the gap with a smoke burst, whose only job was to hide the fact that the monster
        /// blinked out of existence. Both are gone.
        /// </summary>
        private void Contain(EnemyAI enemy)
        {
            if (enemy == null || IndexOfContained(enemy) >= 0)
                return;

            _contained.Add(new ContainedSpecimen { Enemy = enemy, NextPaceAt = 0f });
        }

        private int IndexOfContained(EnemyAI enemy)
        {
            for (int i = 0; i < _contained.Count; i++)
            {
                if (_contained[i].Enemy == enemy)
                    return i;
            }

            return -1;
        }

        /// <summary>
        /// Keeps every contained specimen inside the cage.
        ///
        /// The seal colliders and the carving NavMeshObstacles already go up at the landing, so a
        /// specimen pathing normally cannot walk out. That is not the whole population: plenty of
        /// interior enemies warp, phase, or are shoved by their own AI, and any of those would pop
        /// through a collider the AI never consults. So containment does not trust the barrier —
        /// each tick, anything that has left the volume is put back into it. That is deliberately
        /// the only restraint: the specimen keeps its AI, so it still thrashes, animates and
        /// vocalises inside the cage instead of freezing into a prop.
        ///
        /// #756. Keeping its AI is not the same as keeping it moving. Once the four carving
        /// obstacles go up, the island of NavMesh under the specimen reaches none of the AI nodes
        /// its search routine roams between, so every candidate is rejected as unreachable and the
        /// routine never hands the agent a destination again: the specimen stands where the cage
        /// caught it until a player steps in and gives it something to chase. So the cage paces it
        /// itself: while nothing is being chased, it is handed a fresh point inside the fence every
        /// couple of seconds. A live chase is left alone, which is what keeps a caged teammate in
        /// danger.
        /// </summary>
        private void ServerTickContainment()
        {
            if (_contained.Count == 0)
                return;

            float fenceSide = Mathf.Max(0f, _footprint.Value - PestControlTrapAssembler.FenceInsetMeters);
            float half = Mathf.Max(0.1f, fenceSide * 0.5f - ContainmentInsetMeters);

            for (int i = _contained.Count - 1; i >= 0; i--)
            {
                ContainedSpecimen entry = _contained[i];
                EnemyAI enemy = entry.Enemy;
                if (enemy == null || enemy.isEnemyDead || enemy.NetworkObject == null || !enemy.NetworkObject.IsSpawned)
                {
                    _contained.RemoveAt(i);
                    continue;
                }

                Vector3 local = transform.InverseTransformPoint(enemy.transform.position);
                float clampedX = Mathf.Clamp(local.x, -half, half);
                float clampedZ = Mathf.Clamp(local.z, -half, half);
                float clampedY = Mathf.Clamp(local.y, 0f, CaptureHeightAbove);
                if (!Mathf.Approximately(clampedX, local.x)
                    || !Mathf.Approximately(clampedZ, local.z)
                    || !Mathf.Approximately(clampedY, local.y))
                {
                    MoveEnemyTo(enemy, transform.TransformPoint(new Vector3(clampedX, clampedY, clampedZ)));
                }

                ServerPaceSpecimen(entry, half);
            }
        }

        /// <summary>
        /// Hands a caged specimen a random point inside the fence to walk to, on the cadence in
        /// <see cref="PaceIntervalMinSeconds"/>..<see cref="PaceIntervalMaxSeconds"/>. Writes the
        /// same fields the AI's own search routine would (<c>destination</c> plus the two
        /// steering flags) so <c>EnemyAI.DoAIInterval</c> keeps steering there, and pushes the
        /// agent directly so it starts this frame rather than on the next interval.
        /// </summary>
        private void ServerPaceSpecimen(ContainedSpecimen entry, float half)
        {
            EnemyAI enemy = entry.Enemy;
            if (Time.time < entry.NextPaceAt)
                return;

            entry.NextPaceAt = Time.time + Random.Range(PaceIntervalMinSeconds, PaceIntervalMaxSeconds);

            // A live chase is the AI doing exactly what a caged monster should: leave it alone.
            if (enemy.inSpecialAnimation || enemy.stunNormalizedTimer > 0f)
                return;
            if (enemy.movingTowardsTargetPlayer && enemy.targetPlayer != null && !enemy.targetPlayer.isPlayerDead)
                return;

            NavMeshAgent agent = enemy.agent;
            if (agent == null || !agent.enabled || !agent.isOnNavMesh)
                return;

            Vector3 target = transform.TransformPoint(new Vector3(
                Random.Range(-half, half), 0f, Random.Range(-half, half)));
            if (NavMesh.SamplePosition(target, out NavMeshHit hit, PaceSampleRadiusMeters, NavMesh.AllAreas))
                target = hit.position;

            enemy.movingTowardsTargetPlayer = false;
            enemy.moveTowardsDestination = true;
            enemy.destination = target;
            agent.SetDestination(target);
        }

        /// <summary>
        /// Repositions an enemy without desyncing its agent. Warping through the agent is what keeps
        /// the NavMesh path valid; writing the transform under a live agent leaves the two disagreeing
        /// and the enemy slides back to where the agent still thinks it is.
        /// </summary>
        private static void MoveEnemyTo(EnemyAI enemy, Vector3 position)
        {
            NavMeshAgent agent = enemy.agent;
            if (agent != null && agent.enabled && agent.isOnNavMesh && agent.Warp(position))
                return;

            enemy.transform.position = position;
        }

        /// <summary>
        /// Lets every contained specimen go. The winch is the documented rescue mechanism for a
        /// caged teammate, and it cannot free a player without also freeing whatever else is in
        /// there. The capture is already credited by this point and stays credited — raising the
        /// cage releases a monster into the facility, it does not undo the contract objective.
        /// </summary>
        private void ServerReleaseContained(string reason)
        {
            if (_contained.Count == 0)
                return;

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.PestControl] PEST_RELEASE count={_contained.Count} reason={reason}");
            _contained.Clear();
        }

        private void CollectCapturedEnemies(List<EnemyAI> results)
        {
            List<EnemyAI> spawned = RoundManager.Instance?.SpawnedEnemies;
            if (spawned == null)
                return;

            for (int i = 0; i < spawned.Count; i++)
            {
                EnemyAI enemy = spawned[i];
                if (enemy == null || enemy.isEnemyDead)
                    continue;

                if (IsEnemyInsideCaptureVolume(enemy))
                    results.Add(enemy);
            }

            if (results.Count > 0)
                _specimenLured.Value = true;
        }

        private void ServerObserveSpecimenOnPlatform(bool force)
        {
            if (!IsServer || _specimenLured.Value || _footprint.Value <= 0f)
                return;
            if (!force && Time.unscaledTime < _nextSpecimenCheckAt)
                return;

            _nextSpecimenCheckAt = Time.unscaledTime + SpecimenCheckIntervalSeconds;
            List<EnemyAI> spawned = RoundManager.Instance?.SpawnedEnemies;
            if (spawned == null)
                return;

            for (int i = 0; i < spawned.Count; i++)
            {
                EnemyAI enemy = spawned[i];
                if (enemy == null || enemy.isEnemyDead || !IsEnemyInsideCaptureVolume(enemy))
                    continue;

                _specimenLured.Value = true;
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.PestControl] Specimen reached trap platform ({ResolveEnemyName(enemy)}).");
                return;
            }
        }

        private bool IsEnemyInsideCaptureVolume(EnemyAI enemy)
        {
            if (enemy == null)
                return false;

            float fenceSide = Mathf.Max(0f, _footprint.Value - PestControlTrapAssembler.FenceInsetMeters);
            float half = fenceSide * 0.5f + CaptureMarginMeters;
            // Centre-point test in the trap's own frame. Deliberately generous and loose on Y:
            // standing, jumping, or clipping slightly into the platform all count.
            Vector3 local = transform.InverseTransformPoint(enemy.transform.position);
            return Mathf.Abs(local.x) <= half
                   && Mathf.Abs(local.z) <= half
                   && local.y >= -CaptureHeightBelow
                   && local.y <= CaptureHeightAbove;
        }

        private static void PlayEnemyThrash(EnemyAI enemy)
        {
            if (enemy == null)
                return;

            AudioSource voice = enemy.creatureVoice != null ? enemy.creatureVoice : enemy.creatureSFX;
            if (voice == null || enemy.enemyType == null)
                return;

            AudioClip[] clips = enemy.enemyType.audioClips;
            if (clips == null || clips.Length == 0)
                return;

            AudioClip clip = clips[Random.Range(0, clips.Length)];
            if (clip != null)
                voice.PlayOneShot(clip, 1f);
        }

        private static string ResolveEnemyName(EnemyAI enemy)
        {
            if (enemy == null)
                return "unknown";

            if (enemy.enemyType != null && !string.IsNullOrWhiteSpace(enemy.enemyType.enemyName))
                return enemy.enemyType.enemyName;

            return enemy.gameObject.name;
        }

        private static string BuildCaptureSummary(List<string> names)
        {
            var counts = new Dictionary<string, int>(names.Count);
            for (int i = 0; i < names.Count; i++)
            {
                counts.TryGetValue(names[i], out int existing);
                counts[names[i]] = existing + 1;
            }

            var builder = new StringBuilder();
            foreach (KeyValuePair<string, int> pair in counts)
            {
                if (builder.Length > 0)
                    builder.Append(", ");
                builder.Append(pair.Key).Append(" x").Append(pair.Value);
            }

            return builder.ToString();
        }

        /// <summary>
        /// Carries the capture summary to every peer for the status readout. It used to carry the
        /// captured positions too, so each client could spawn a smoke burst over them; the smoke
        /// only ever existed to mask the despawn and went with it.
        /// </summary>
        [ClientRpc]
        private void AnnounceCaptureClientRpc(string summary)
        {
            _lastCaptureSummary = string.IsNullOrEmpty(summary) ? "none" : summary;
        }

        // --------------------------------------------------------------- audio
        // #756. The authored cues live in ContractAudio\PestControl and load through
        // PestControlTrapAudio. Each slot keeps the vanilla clip it used before as a fallback for a
        // missing file or a pull in the first second of a round: the slam falls back to the metal
        // door slam (the same clip the Containment lockdown gate uses) and the winch to
        // StartOfRound's start-game whir. The lever has no vanilla stand-in and stays silent
        // without its files.

        private void PlayLeverAudio()
        {
            AudioClip clip = PestControlTrapAudio.PickLeverClip();
            if (_audio == null || clip == null)
                return;

            _audio.pitch = Random.Range(0.95f, 1.05f);
            _audio.PlayOneShot(clip, 0.9f);
        }

        private void PlaySlamAudio()
        {
            AudioClip clip = PestControlTrapAudio.DropClip;
            if (clip != null)
            {
                if (_audio == null)
                    return;

                _audio.pitch = Random.Range(0.97f, 1.03f);
                _audio.PlayOneShot(clip, 1f);
                return;
            }

            clip = ResolveSlamClip();
            if (_audio == null || clip == null)
                return;

            _audio.pitch = Random.Range(0.86f, 0.96f);
            _audio.PlayOneShot(clip, 0.95f);
        }

        private void PlayWinchAudio()
        {
            AudioClip clip = PestControlTrapAudio.RaiseClip;
            if (clip != null)
            {
                if (_audio == null)
                    return;

                _audio.pitch = Random.Range(0.97f, 1.03f);
                _audio.PlayOneShot(clip, 0.9f);
                return;
            }

            clip = GetStartOfRoundAudioClip("startGameWhir");
            if (_audio == null || clip == null)
                return;

            _audio.pitch = Random.Range(0.7f, 0.8f);
            _audio.PlayOneShot(clip, 0.45f);
        }

        private static AudioClip ResolveSlamClip()
        {
            AudioClip[] clips = StartOfRound.Instance?.shutDoorMetal;
            if (clips == null)
                return null;

            for (int i = 0; i < clips.Length; i++)
            {
                if (clips[i] != null)
                    return clips[i];
            }

            return null;
        }

        private static AudioClip GetStartOfRoundAudioClip(string fieldName)
        {
            StartOfRound start = StartOfRound.Instance;
            if (start == null)
                return null;

            FieldInfo field = typeof(StartOfRound).GetField(
                fieldName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var source = field?.GetValue(start) as AudioSource;
            return source != null ? source.clip : null;
        }

        internal string BuildStatusSummary()
        {
            return $"state={State} footprint={_footprint.Value:0.00}m " +
                   $"captured={_capturedTotal.Value} ({_lastCaptureSummary})";
        }
    }
}
