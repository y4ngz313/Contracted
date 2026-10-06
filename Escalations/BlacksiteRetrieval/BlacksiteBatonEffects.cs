using System.Linq;
using UnityEngine;

using Y4NGZCompany.Contracts._Shared;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // Rebind the approved V06 presentation without a dependency on the authoring assembly.
    public sealed class BlacksiteBatonEffects : MonoBehaviour
    {
        private LineRenderer[] _arcs; private Light _light; private ParticleSystem _sparks;
        private AudioSource _loop, _cues; private bool _powered; private float _nextArc; private float _impactUntil;
        private Shovel _item;
        // #929: one propagation driver per source (occlusion, facility reverb, the volume slider).
        private ContractAudioPropagation _loopPropagation, _cuePropagation;
        private void Awake()
        {
            _arcs = GetComponentsInChildren<LineRenderer>(true); _light = GetComponentInChildren<Light>(true);
            _sparks = GetComponentInChildren<ParticleSystem>(true); _item = GetComponentInParent<Shovel>();
            foreach (var audio in GetComponentsInChildren<AudioSource>(true)) { audio.Stop(); audio.playOnAwake = false; }
            _loop = gameObject.AddComponent<AudioSource>(); _loop.playOnAwake = false; _loop.loop = true;
            _loop.volume = BlacksiteAudioPolicy.BatonLoopVolume;
            _loop.clip = BlacksiteEquipmentAssets.Load<AudioClip>("ElectricLoop");
            _loopPropagation = ContractSpatialAudio.ApplyFacilityCarryProfile(
                _loop, BlacksiteAudioPolicy.BatonLoopMaxDistance, BlacksiteAudioPolicy.BatonLoopMinDistance);
            // The cue has its own child at the parent's origin: occlusion and its filters are per
            // GameObject, so two sources on one object would share one filter chain.
            var cueHost = new GameObject("BlacksiteBatonCues");
            cueHost.transform.SetParent(transform, false);
            _cues = cueHost.AddComponent<AudioSource>(); _cues.playOnAwake = false;
            _cues.volume = BlacksiteAudioPolicy.BatonCueVolume;
            // #929 round 2: the cue is a CONTACT sound, so near-field: Custom rolloff ignores minDistance
            // and the facility shape put it at 0.50 gain by 1.5 m, across the whole melee envelope. The
            // plateau holds full gain to the widest authored reach for every weapon cue in the hand
            // (BlacksiteAudioPolicy records why they are not split); the hum LOOP keeps the facility profile.
            _cuePropagation = ContractSpatialAudio.ApplyNearFieldCarryProfile(
                _cues, BlacksiteAudioPolicy.BatonCueMaxDistance, BlacksiteAudioPolicy.BatonCueMinDistance,
                BlacksiteAudioPolicy.BatonCueNearFieldPlateau);
            Apply(false);
        }
        internal void Power(bool enabled, bool cue = true)
        {
            if (_powered == enabled) return; _powered = enabled; Apply(enabled);
            if (cue) Cue(enabled ? "PowerOn" : "PowerOff");
        }
        private void Apply(bool enabled)
        {
            if (_arcs != null) foreach (var arc in _arcs) if (arc != null) arc.enabled = enabled;
            if (_light != null) _light.enabled = enabled;
            if (_loop != null) { if (enabled && _loop.clip != null) _loop.Play(); else _loop.Stop(); }
            if (!enabled && _sparks != null) _sparks.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }
        internal void Cue(string name) => Cue(BlacksiteEquipmentAssets.Load<AudioClip>(name));
        internal void Cue(AudioClip clip) { if (clip != null && _cues != null) _cues.PlayOneShot(clip); }
        internal void Impact() { _impactUntil = Time.time + .1f; if (_sparks != null) _sparks.Play(); Cue("Impact"); }
        // #988 B4 (reverted 2026-09-16): this weapon's arcs scaled for a victim drew metres-wide
        // polygons; never re-derive them for a victim. The victim flash is BlacksiteBatonVictimFlash.
        private void Update()
        {
            // #929: propagation ticks unpowered too: a cue can fire then and the loop's filter must be current.
            _loopPropagation?.Tick(); _cuePropagation?.Tick();
            if (_item != null) Power(_item.isHeld && !_item.isPocketed, false);
            if (!_powered || Time.time < _nextArc) return; _nextArc = Time.time + 1f/24;
            for (int i = 0; i < _arcs.Length; i++)
            {
                var arc = _arcs[i]; if (arc == null) continue; arc.positionCount = 9; arc.useWorldSpace = false;
                for (int k = 0; k < 9; k++) { float angle = i * Mathf.PI * .5f + Mathf.Sin(k * 2.7f + Time.time * 23) * .35f;
                    arc.SetPosition(k, new Vector3(Mathf.Cos(angle) * .027f, Mathf.Lerp(.531f, .607f, k/8f), Mathf.Sin(angle) * .027f)); }
            }
            if (_light != null) _light.intensity = Time.time < _impactUntil ? 3 : 1.1f;
        }
        private void OnDisable() { _powered = false; Apply(false); }
    }
}
