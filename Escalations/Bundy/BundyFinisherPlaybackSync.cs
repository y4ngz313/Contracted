using System;
using UnityEngine;

namespace Y4NGZCompany.Escalations.Bundy;

// Company owns the choreography; Interactions still owns the player controller and its restore.
// No Animator speed, root-motion flag, controller, layer weight, or playable graph is replaced.
internal sealed class BundyFinisherPlaybackSync
{
    private readonly Track _attacker;
    private readonly Track _victim;
    private readonly double _start;

    private BundyFinisherPlaybackSync(Track attacker, Track victim, double start)
    {
        _attacker = attacker;
        _victim = victim;
        _start = start;
    }

    internal static bool TryCreate(Animator attacker, Animator victim, int bodyIndex, double start,
        out BundyFinisherPlaybackSync sync, out string reason)
    {
        sync = null;
        string variant = bodyIndex == 0 ? "NeckSlash" : bodyIndex == 1 ? "SawMiddle" : "StabLift";
        if (!Track.TryCreate(attacker, 0, "Fin_" + variant,
                "Paired_Chainsaw_" + variant + "_Att", "DoFinisher", out Track attack, out reason))
            return false;
        int victimLayer = victim != null ? victim.GetLayerIndex("BundyVictimFullBody") : -1;
        if (!Track.TryCreate(victim, victimLayer, variant, "BundyVictim_" + variant,
                "BundyVictim_" + variant, out Track receive, out reason))
            return false;
        sync = new BundyFinisherPlaybackSync(attack, receive, start);
        reason = $"variant={variant}, attackerDuration={attack.Duration:F3}s, victimDuration={receive.Duration:F3}s";
        return true;
    }

    internal bool Tick(double now)
    {
        // A controller replacement is an ownership change, including Interactions restoration.
        // Stand down before writing either animator when either side no longer belongs to us.
        if (!_attacker.IsValid || !_victim.IsValid)
            return false;
        _attacker.Sample(now, _start);
        _victim.Sample(now, _start);
        return true;
    }

    private sealed class Track
    {
        private Animator _animator;
        private RuntimeAnimatorController _controller;
        private int _layer;
        private int _state;
        private string _trigger;
        internal float Duration;

        internal bool IsValid => _animator != null && _animator.isActiveAndEnabled &&
            _animator.runtimeAnimatorController == _controller;

        internal static bool TryCreate(Animator animator, int layer, string state, string clipName,
            string trigger, out Track track, out string reason)
        {
            track = null;
            reason = $"animator/state unavailable: {state}";
            if (animator == null || !animator.isActiveAndEnabled || layer < 0 ||
                layer >= animator.layerCount || animator.runtimeAnimatorController == null)
                return false;
            int hash = Animator.StringToHash(animator.GetLayerName(layer) + "." + state);
            if (!animator.HasState(layer, hash))
                return false;
            AnimationClip clip = null;
            foreach (AnimationClip candidate in animator.runtimeAnimatorController.animationClips)
            {
                if (candidate != null && (candidate.name == clipName ||
                    candidate.name.EndsWith("_" + clipName, StringComparison.Ordinal)))
                {
                    clip = candidate;
                    break;
                }
            }
            if (clip == null || clip.length <= 0f)
            {
                reason = $"clip unavailable: {clipName}";
                return false;
            }
            track = new Track { _animator = animator, _controller = animator.runtimeAnimatorController,
                _layer = layer, _state = hash, _trigger = trigger, Duration = clip.length };
            return true;
        }

        internal void Sample(double now, double start)
        {
            // Discard a delayed replicated entry trigger; it must not restart this local timeline.
            _animator.ResetTrigger(_trigger);
            float phase = BundyFinisherClock.NormalizedSample(now, start, Duration);
            _animator.Play(_state, _layer, phase);
        }
    }
}
