using System;
using System.Collections;
using UnityEngine;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts._Shared;

namespace Y4NGZCompany.Contracts.Defuse
{
    // #219: the Defuse case used to detonate as a single Landmine.SpawnExplosion. One blast
    // reads as a landmine, not as the briefcase IED the contract has been building toward,
    // and the balance config clamps ExplosionRadiusMeters at 30 m so the radius knob alone
    // cannot get there — nor should it, since scaling one VFX asset just yields a stretched
    // sprite. Instead a cluster of explosion instances is spawned around the case and
    // staggered over half a second, so the detonation reads as one large sustained event
    // built from real assets.
    //
    // Exactly ONE instance carries damage. The rest pass killRange/damageRange = 0, which
    // makes vanilla's Physics.OverlapSphere return nothing, and physicsForce = 0, which its
    // `physicsForce > 0f` guard skips outright — so a player at the edge of the blast takes
    // the same damage the single-blast tuning was built around instead of ten overlapping
    // hits. Camera shake is deliberately left on every instance: SpawnExplosion's shake is a
    // state set, not an accumulator, so the stagger sustains the shake for the whole sequence.
    internal static class DefuseDetonation
    {
        internal static void Play(Vector3 origin, float lethalRadius, int seed)
        {
            try
            {
                // The runner cannot live on the bomb: FailServer despawns the case 0.25s
                // later and a destroyed GameObject stops the coroutine mid-cluster.
                var runnerObject = new GameObject("Y4NGZ_DefuseDetonation");
                runnerObject.transform.position = origin;
                runnerObject.AddComponent<DefuseDetonationRunner>().Begin(origin, lethalRadius, seed);
            }
            catch (Exception ex)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Defuse] Detonation effect failed to start: {ex.Message}");
            }
        }
    }

    internal sealed class DefuseDetonationRunner : MonoBehaviour
    {
        // Ten instances is the "10x as large" ask taken literally: one damaging blast plus
        // nine presentation blasts.
        private const int ExplosionCount = 10;

        // Secondaries reach out to 2.2x the lethal radius, so the visible footprint covers
        // roughly an order of magnitude more volume than the single blast did while the
        // lethal envelope stays exactly where the config puts it.
        private const float ClusterRadiusScale = 2.2f;
        private const float ClusterSpreadSeconds = 0.55f;

        // The detonation is the loudest thing this contract produces and the one cue that
        // should reach the whole facility, so it carries further than the beep's 75 m.
        private const float BoomMaxDistanceMeters = 140f;
        private const float BoomVolume = 1f;

        // Vanilla's occlusion/room mask, matching OccludeAudio and Landmine's own linecasts.
        private const int RoomMask = 256;

        private AudioSource _boomSource;
        private ContractAudioPropagation _boomPropagation;

        internal void Begin(Vector3 origin, float lethalRadius, int seed)
        {
            StartCoroutine(Run(origin, lethalRadius, seed));
        }

        private void Update()
        {
            _boomPropagation?.Tick();
        }

        private IEnumerator Run(Vector3 origin, float lethalRadius, int seed)
        {
            PlayLongRangeBoom(origin);

            // The one blast that damages. Values are unchanged from the pre-#219 single
            // explosion so the failure tuning is untouched.
            SpawnExplosionSafely(origin + Vector3.up, lethalRadius, lethalRadius + 1.5f, 80, 70f);

            // Seeded from the bomb's network id so every client renders the same cluster.
            // Nothing here is netcoded — this is local presentation on each machine.
            var random = new System.Random(seed);
            float spread = Mathf.Max(2f, lethalRadius * ClusterRadiusScale);
            float step = ClusterSpreadSeconds / Mathf.Max(1, ExplosionCount - 1);

            // Golden angle, so successive instances land on opposite sides of the case
            // instead of walking around it in one direction.
            const float GoldenAngleRadians = 2.399963f;

            for (int i = 1; i < ExplosionCount; i++)
            {
                yield return new WaitForSeconds(step);

                float angle = i * GoldenAngleRadians + (float)random.NextDouble();
                float radius = spread * Mathf.Lerp(0.35f, 1f, (float)random.NextDouble());
                float height = Mathf.Lerp(-0.3f, 2.6f, (float)random.NextDouble());
                Vector3 target = origin + new Vector3(Mathf.Cos(angle) * radius, height, Mathf.Sin(angle) * radius);

                SpawnExplosionSafely(ClampInsideRoom(origin + Vector3.up, target), 0f, 0f, 0, 0f);
            }

            // Outlive the boom clip so the audio is not cut off with the object.
            float wait = _boomSource != null && _boomSource.clip != null ? _boomSource.clip.length + 0.5f : 1f;
            yield return new WaitForSeconds(wait);
            Destroy(gameObject);
        }

        // Facility interiors are cramped: a secondary placed 17 m out is usually inside
        // geometry, where the effect is invisible and the flash lights the wrong room. Pull
        // each one back to just short of the first wall between it and the case.
        private static Vector3 ClampInsideRoom(Vector3 from, Vector3 target)
        {
            Vector3 delta = target - from;
            float length = delta.magnitude;
            if (length <= 0.05f)
                return target;

            Vector3 direction = delta / length;
            if (Physics.Raycast(from, direction, out RaycastHit hit, length, RoomMask, QueryTriggerInteraction.Ignore))
                return hit.point - direction * 0.4f;

            return target;
        }

        private static void SpawnExplosionSafely(Vector3 position, float killRange, float damageRange, int nonLethalDamage, float physicsForce)
        {
            // SpawnExplosion dereferences localPlayerController, HUDManager and
            // mapPropsContainer without guarding any of them. A throw here would abort the
            // rest of the cluster, so each instance is isolated.
            try
            {
                Landmine.SpawnExplosion(position, true, killRange, damageRange, nonLethalDamage, physicsForce);
            }
            catch (Exception ex)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Defuse] Detonation instance failed: {ex.Message}");
            }
        }

        // #218: vanilla's explosion prefab brings its own AudioSource, but on the prefab's
        // own short range. This is a second emitter using the same clip, routed through the
        // diagetic mixer with OccludeAudio and the carry curve, so the detonation is heard
        // (muffled, reverbed) from anywhere in the facility rather than only in the room.
        private void PlayLongRangeBoom(Vector3 origin)
        {
            try
            {
                AudioClip clip = ResolveExplosionClip();
                if (clip == null)
                    return;

                _boomSource = gameObject.AddComponent<AudioSource>();
                _boomSource.playOnAwake = false;
                _boomSource.loop = false;
                _boomSource.clip = clip;
                _boomPropagation = ContractSpatialAudio.ApplyFacilityCarryProfile(
                    _boomSource,
                    BoomMaxDistanceMeters,
                    minDistance: 4f,
                    spread: 20f,
                    priority: 90);

                transform.position = origin;
                _boomSource.volume = BoomVolume;
                _boomSource.Play();
            }
            catch (Exception ex)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Defuse] Detonation boom failed: {ex.Message}");
            }
        }

        private static AudioClip ResolveExplosionClip()
        {
            GameObject prefab = StartOfRound.Instance?.explosionPrefab;
            if (prefab == null)
                return null;

            AudioSource source = prefab.GetComponentInChildren<AudioSource>(includeInactive: true);
            return source != null ? source.clip : null;
        }
    }
}
