using System;
using UnityEngine;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Core.Compatibility;
namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>
    /// #1195. The side-objective tick, one step of the <c>MoonContractPatches</c> RoundManager.Update
    /// chain.
    ///
    /// Host: reads the sources at most 4 times a second and publishes one of them (the Wesley's
    /// Interiors secondary when it has one, else the Wesley's Moons trial, #1198). Every peer: while
    /// the published objective is unrevealed, runs the local sight check at most 5 times a second and
    /// reports a sighting (the host reveals it for the crew). Client: asks the host for the current
    /// snapshot until one lands this round. Without Wesley's tools installed the step is a handler
    /// check and two cached bools.
    /// </summary>
    internal static class SideObjectiveRuntime
    {
        private static float _nextHostPollAt;
        private static float _nextSightCheckAt;
        private static bool _trialPollFailureLogged;

        internal static void ResetRunState()
        {
            _nextHostPollAt = 0f;
            _nextSightCheckAt = 0f;
            SideObjectiveWesleyInteriorSource.ResetRunState();
            SideObjectiveWesleyTrialSource.ResetRunState();
            SideObjectiveSight.Reset();
        }

        internal static void UpdateRuntime()
        {
            SideObjectiveNetwork.EnsureNetworkHandlers();
            bool interior = WesleysInteriorToolsCompat.IsUsable;
            bool trials = WesleysTrialsCompat.IsUsable;
            if (!interior && !trials)
            {
                // An adapter that failed mid-round takes its objective off every card rather than
                // freezing it at the last reading.
                if (SideObjectiveNetwork.Current.HasObjective && SideObjectiveNetwork.IsServer)
                    SideObjectiveNetwork.PublishHost(SideObjectiveSnapshot.None);
                return;
            }

            if (!IsRoundLive())
                return;

            float now = Time.time;
            if (SideObjectiveNetwork.IsServer)
            {
                if (now >= _nextHostPollAt)
                {
                    _nextHostPollAt = now + SideObjectivePolicy.HostPollIntervalSeconds;
                    SideObjectiveSnapshot interiorReading = interior
                        ? SideObjectiveWesleyInteriorSource.PollHost(now)
                        : SideObjectiveSnapshot.None;
                    SideObjectiveSnapshot trialReading = trials ? PollTrialHost(now) : SideObjectiveSnapshot.None;
                    SideObjectiveNetwork.PublishHost(SideObjectiveTrialRules.SelectReading(interiorReading, trialReading));
                }
            }
            else
            {
                SideObjectiveNetwork.TickClientReplay(now);
            }

            SideObjectiveSnapshot current = SideObjectiveNetwork.Current;
            if (!SideObjectivePolicy.NeedsLocalSighting(current) || now < _nextSightCheckAt)
                return;

            _nextSightCheckAt = now + SideObjectivePolicy.SightCheckIntervalSeconds;
            SideObjectiveWesleyInteriorSource.RefreshSightTargets(current, now);
            SideObjectiveWesleyTrialSource.RefreshSightTargets(current, now);
            if (SideObjectiveSight.Check(now))
                SideObjectiveNetwork.ReportLocalSighting(now);
        }

        /// <summary>
        /// Whether a client applies an incoming state message: not in orbit and not once the ship has
        /// started to leave, so a message still in flight at the round's end cannot bring back a
        /// finished objective after <see cref="SideObjectiveState.ResetRunState"/>.
        /// </summary>
        internal static bool AcceptsState()
        {
            StartOfRound start = StartOfRound.Instance;
            return start != null && !start.inShipPhase && !start.shipIsLeaving;
        }

        /// <summary>
        /// The trial poll, fenced: a throw in it (#1198) costs that tick's trial reading only and can
        /// never stop the interior reading from being published. Logged once per session.
        /// </summary>
        private static SideObjectiveSnapshot PollTrialHost(float now)
        {
            try
            {
                return SideObjectiveWesleyTrialSource.PollHost(now);
            }
            catch (Exception e)
            {
                if (!_trialPollFailureLogged)
                {
                    _trialPollFailureLogged = true;
                    ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Trials] Trial poll failed; the trial is skipped for this tick: {e.GetType().Name}: {e.Message}");
                }

                return SideObjectiveSnapshot.None;
            }
        }

        private static bool IsRoundLive()
        {
            // shipHasLanded is deliberately not required, unlike the contract gates: Wesley picks the
            // entry about 2.5 s after the controller wakes, inside the landing sequence, and the host
            // poll has to be running by then.
            StartOfRound start = StartOfRound.Instance;
            return start != null
                && !start.inShipPhase
                && !start.shipIsLeaving
                && !start.travellingToNewLevel;
        }
    }
}
