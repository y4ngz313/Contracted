using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using UnityEngine;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts._Shared;

namespace Y4NGZCompany.Contracts.ObjectiveHints
{
    /// <summary>
    /// #1197. The objective pity timer's Unity side, ticked last in the contract runtime's
    /// <c>RoundManager.Update</c> chain on every peer. Everything here is local to this client and
    /// nothing is sent: it reads the local player, samples the current contract's locate row four
    /// times a second through <see cref="MoonContractState.CollectLocateTargets"/>, feeds
    /// <see cref="ContractPityTimer"/> a <see cref="ContractPityReading"/>, and while a blink window
    /// is open holds the card word's <see cref="ContractHudWordPulse"/> request and blinks the
    /// outline on the same clock.
    ///
    /// <para>Idle cost is a config read, a few player-field reads and two reference compares per
    /// frame; the row is only sampled while the local player is alive inside the facility. The
    /// window closes, and the outline is torn down, when it runs out, when the objective is found,
    /// finished or changes, when the player dies, starts spectating or leaves the facility, when the
    /// local player or its gameplay camera is replaced or destroyed, when the hint is disabled, and
    /// on every run reset.</para>
    /// </summary>
    internal static class ObjectiveHintRuntime
    {
        private const float SampleIntervalSeconds = 0.25f;

        private static readonly ContractPityTimer Timer = new ContractPityTimer();
        private static readonly List<Component> Targets = new List<Component>(8);
        private static readonly List<Component> PreviousLocateTargets = new List<Component>(8);
        private static readonly List<Component> LocatedTargets = new List<Component>(8);
        private static readonly List<Component> WindowTargets = new List<Component>(8);

        private static float _nextSampleAt;
        /// <summary>Last sample's row key and found progress, found or not, to see progress.</summary>
        private static string _trackedRowKey;
        private static int _trackedFoundCount;
        private static bool _trackedRowUnfound;
        private static bool _glintFailureLogged;

        /// <summary>The local player the timer is counting for; another player starts over.</summary>
        private static PlayerControllerB _timerPlayer;

        private static bool _windowOpen;
        private static int _windowSerial;
        private static string _pulseKey;
        private static float _pulseUntil;
        /// <summary>The gameplay camera the open window's outline is on; null for a word-only window.</summary>
        private static Camera _windowCamera;
        /// <summary>A word-only pity window still owns the targets it opened for.</summary>
        internal static bool IsPityBlinkActiveForTarget(Component target)
        {
            if (!_windowOpen || target == null)
                return false;
            Transform root = target.transform;
            for (int i = 0; i < WindowTargets.Count; i++)
            {
                Component windowTarget = WindowTargets[i];
                if (windowTarget != null && (ReferenceEquals(target, windowTarget)
                    || root.IsChildOf(windowTarget.transform)
                    || windowTarget.transform.IsChildOf(root)))
                    return true;
            }

            return false;
        }

        internal static void UpdateRuntime()
        {
            bool hintsEnabled = ContractsBootstrap.ObjectiveHintsEnabled != null
                && ContractsBootstrap.ObjectiveHintsEnabled.Value;
            if (!hintsEnabled)
            {
                TickGlint(null, false);
                if (_windowOpen || Timer.Phase != ContractPityTimerPhase.Idle)
                    ResetHints();
                return;
            }

            GameNetworkManager network = GameNetworkManager.Instance;
            // A destroyed player reads as none (Unity's == null), so its replacement or its loss is a change.
            PlayerControllerB local = network != null && network.localPlayerController != null ? network.localPlayerController : null;
            if (!ReferenceEquals(local, _timerPlayer))
            {
                // A different local player, or ours is gone: nothing counted or shown for the old
                // one carries over, but a sighting already consumed this round remains consumed.
                ResetHints();
                _timerPlayer = local;
            }

            bool alive = local != null && local.isPlayerControlled && !local.isPlayerDead;
            bool inside = alive && local.isInsideFactory;
            Camera camera = alive ? local.gameplayCamera : null;
            TickGlint(camera, ContractsBootstrap.ObjectiveFirstSightGlintEnabled != null
                && ContractsBootstrap.ObjectiveFirstSightGlintEnabled.Value && alive);
            float now = Time.unscaledTime;

            if (_windowOpen && !(_windowCamera is null)
                && (_windowCamera == null || local == null || local.gameplayCamera != _windowCamera))
            {
                // The camera the outline is on was replaced or destroyed: close, and let a fresh count decide.
                CloseWindow();
                Timer.Reset();
            }

            ContractPityReading reading = ContractPityReading.NotSampled;
            string key = null;
            bool progressed = false;
            if (inside && now >= _nextSampleAt)
            {
                _nextSampleAt = now + SampleIntervalSeconds;
                reading = Sample(out key, out progressed, camera);
            }

            var input = new ContractPityTimerInput
            {
                DeltaSeconds = Time.unscaledDeltaTime,
                Enabled = true,
                LocalPlayerAlive = alive,
                LocalPlayerInside = inside,
                Reading = reading,
                ObjectiveKey = key,
                ObjectiveFound = progressed,
            };
            var settings = new ContractPityTimerSettings(
                ContractsBootstrap.ObjectiveHintDelaySeconds.Value,
                ContractsBootstrap.ObjectiveHintBlinkSeconds.Value,
                ContractsBootstrap.ObjectiveHintRepeatSeconds.Value);

            if (Timer.Tick(input, settings) != ContractPityTimerPhase.Blinking)
            {
                if (_windowOpen)
                    CloseWindow();
                return;
            }

            if (!_windowOpen || Timer.BlinkSerial != _windowSerial)
                OpenWindow(local, now);
            else if (reading != ContractPityReading.NotSampled)
                // The card clears every pulse request when it hides; re-assert it on each sample.
                ContractHudWordPulse.Begin(_pulseKey, _pulseUntil);

            ObjectiveHintOutlines.SetLit(ContractHudWordPulse.IsLit(now));
        }

        /// <summary>Round reset, disconnect and lobby change: close presentation and forget round sightings.</summary>
        internal static void ResetRunState()
        {
            ResetHints();
            ObjectiveGlintRuntime.ResetRunState();
        }

        private static void ResetHints()
        {
            CloseWindow();
            Timer.Reset();
            Targets.Clear();
            PreviousLocateTargets.Clear();
            LocatedTargets.Clear();
            _nextSampleAt = 0f;
            _trackedRowKey = null;
            _trackedFoundCount = 0;
            _trackedRowUnfound = false;
            _timerPlayer = null;
        }

        /// <summary>
        /// One read of the locate row. No row, or a found, captured, finished or failed one, is
        /// <see cref="ContractPityReading.NoObjective"/>, which resets the timer; an unfound row reads
        /// inside or outside by its targets.
        /// </summary>
        private static ContractPityReading Sample(out string key, out bool progressed, Camera camera)
        {
            key = null;
            progressed = false;
            // A dungeon-reference compare on every sample, so a new dungeon is picked up even while
            // the player never leaves the facility. The only allocation is a buffer growth for a
            // dungeon with more than 256 tiles (or more than any before it this session), once per
            // such dungeon.
            MoonContractState.PrepareLocateTileBounds();
            if (!MoonContractState.CollectLocateTargets(Targets, out string rowKey, out bool rowFound, out int foundCount))
            {
                _trackedRowKey = null;
                _trackedFoundCount = 0;
                _trackedRowUnfound = false;
                PreviousLocateTargets.Clear();
                LocatedTargets.Clear();
                return ContractPityReading.NoObjective;
            }

            bool sameRow = _trackedRowKey != null
                && string.Equals(rowKey, _trackedRowKey, StringComparison.Ordinal);
            if (!sameRow || !_trackedRowUnfound)
                LocatedTargets.Clear();

            // Progress while the row remains unfound restarts pity, but is not a completion cue.
            progressed = sameRow && foundCount > _trackedFoundCount;
            bool completed = ObjectiveGlintTransition.IsCompleted(_trackedRowKey, _trackedRowUnfound,
                _trackedFoundCount, rowKey, rowFound, foundCount);
            if (completed)
            {
                // The collector returns only unfound entries: retain all previously inside targets
                // that became found, including those from this row's earlier partial progress.
                RememberLocatedTargets(true, foundCount - _trackedFoundCount);
                for (int i = 0; i < LocatedTargets.Count; i++)
                {
                    Component target = LocatedTargets[i];
                    if (target != null && target.gameObject.activeInHierarchy)
                        TryShowGlint(target, camera);
                }
            }
            else if (progressed && !rowFound)
            {
                // A partial breaker count is not itself a cue. Retain only prior same-row
                // targets that stopped being unfound, so they can cue at completion.
                RememberLocatedTargets(false, foundCount - _trackedFoundCount);
            }

            _trackedRowKey = rowKey;
            _trackedFoundCount = foundCount;
            _trackedRowUnfound = !rowFound;
            PreviousLocateTargets.Clear();
            if (rowFound)
            {
                LocatedTargets.Clear();
                return ContractPityReading.NoObjective;
            }

            // Keep references before the next collector call clears Targets, including multiple
            // still-unfound breaker boxes on the same Blackout row.
            PreviousLocateTargets.AddRange(Targets);
            key = rowKey;
            return Targets.Count > 0 ? ContractPityReading.TargetInside : ContractPityReading.TargetOutside;
        }

        private static void RememberLocatedTargets(bool allFound, int progressDelta)
        {
            int removed = 0;
            for (int i = 0; i < PreviousLocateTargets.Count; i++)
            {
                Component previous = PreviousLocateTargets[i];
                if (previous != null && !Targets.Contains(previous))
                    removed++;
            }
            // More disappearances than found progress may include a target that simply left.
            if (!allFound && removed > progressDelta)
                return;

            for (int i = 0; i < PreviousLocateTargets.Count; i++)
            {
                Component previous = PreviousLocateTargets[i];
                if (previous != null && previous.gameObject.activeInHierarchy
                    && !Targets.Contains(previous) && !LocatedTargets.Contains(previous))
                    LocatedTargets.Add(previous);
            }
        }

        private static void OpenWindow(PlayerControllerB local, float now)
        {
            if (_windowOpen)
                CloseWindow();
            WindowTargets.Clear();
            WindowTargets.AddRange(Targets);
            // A just-opened pity window owns even a word-only target and takes precedence over
            // any still-running first-sight accent on its current unfound targets.
            try
            {
                ObjectiveGlintRuntime.CancelTargets(WindowTargets);
            }
            catch (Exception e)
            {
                LogGlintFailure(e);
            }

            _windowOpen = true;
            _windowSerial = Timer.BlinkSerial;
            _pulseKey = Timer.Key;
            _pulseUntil = now + Timer.BlinkRemaining;
            ContractHudWordPulse.Begin(_pulseKey, _pulseUntil);
            ContractHudAudio.Play(ContractHudAudioKind.PityBlink);
            // Word only when this returns false (shaders missing, no camera, no eligible renderer).
            Camera camera = local != null ? local.gameplayCamera : null;
            _windowCamera = ObjectiveHintOutlines.Show(camera, Targets) ? camera : null;
        }

        private static void TickGlint(Camera camera, bool enabled)
        {
            try
            {
                ObjectiveGlintRuntime.Tick(camera, enabled);
            }
            catch (Exception e)
            {
                LogGlintFailure(e);
            }
        }

        private static void TryShowGlint(Component target, Camera camera)
        {
            try
            {
                ObjectiveGlintRuntime.TryShow(target, camera, IsPityBlinkActiveForTarget(target));
            }
            catch (Exception e)
            {
                LogGlintFailure(e);
            }
        }

        private static void LogGlintFailure(Exception e)
        {
            if (_glintFailureLogged)
                return;
            _glintFailureLogged = true;
            ContractsBootstrap.Log?.LogWarning($"[MoonContracts] Objective glint failed: {e}");
        }

        private static void CloseWindow()
        {
            if (_pulseKey != null)
                ContractHudWordPulse.End(_pulseKey);
            _pulseKey = null;
            _windowOpen = false;
            _windowCamera = null;
            WindowTargets.Clear();
            ObjectiveHintOutlines.Clear();
        }
    }
}
