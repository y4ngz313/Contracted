using GameNetcodeStuff;
using UnityEngine;
using Y4NGZCompany.Escalations.Bundy.AI;

namespace Y4NGZCompany.Escalations.Bundy
{
    internal sealed class BundyFinisherBloodVfx : MonoBehaviour
    {
        private const float DefaultFollowSeconds = 3.15f;
        private const float DefaultDestroySeconds = 6.4f;

        private BundyAIBase _bundy;
        private PlayerControllerB _victim;
        private ParticleSystem[] _particles;
        private Material _runtimeMaterial;
        private float _followUntil;
        private float _destroyAt;
        private bool _stoppedEmitting;

        internal static BundyFinisherBloodVfx Spawn(BundyAIBase bundy, PlayerControllerB victim)
        {
            if (bundy == null || victim == null)
                return null;

            // #276: start each finisher with a fresh victim renderer scan; the set is then cached
            // for the follow window (see BundyAIBase.GetPlayerBloodRenderers).
            BundyAIBase.InvalidatePlayerBloodRendererCache();
            Vector3 contact = bundy.ResolveFinisherBloodContactPoint(victim);
            Quaternion rotation = bundy.ResolveFinisherBloodRotation(contact, victim);
            GameObject instance = new GameObject("Bundy_Finisher_DirectionalBlood");
            instance.transform.SetPositionAndRotation(contact, rotation);
            BundyFinisherBloodVfx effect = instance.AddComponent<BundyFinisherBloodVfx>();
            effect.Initialize(bundy, victim);
            return effect;
        }

        internal void Initialize(BundyAIBase bundy, PlayerControllerB victim)
        {
            _bundy = bundy;
            _victim = victim;
            _followUntil = Time.time + DefaultFollowSeconds;
            _destroyAt = Time.time + DefaultDestroySeconds;

            if (GetComponent<ParticleSystem>() == null)
                CreateRuntimeSpray(victim);

            _particles = GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < _particles.Length; i++)
            {
                ParticleSystem particle = _particles[i];
                if (particle == null)
                    continue;

                ParticleSystem.MainModule main = particle.main;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                particle.Play(withChildren: true);
            }

            UpdateContactPose();
        }

        private void CreateRuntimeSpray(PlayerControllerB victim)
        {
            _runtimeMaterial = CreateBloodMaterial(victim);

            ParticleSystem spray = gameObject.AddComponent<ParticleSystem>();
            ConfigureSpray(
                spray,
                rate: 210f,
                minSpeed: 6.5f,
                maxSpeed: 13.5f,
                minSize: 0.045f,
                maxSize: 0.15f,
                gravity: 1.05f,
                minLifetime: 0.24f,
                maxLifetime: 0.92f,
                coneAngle: 9f,
                radius: 0.07f,
                length: 0.3f,
                maxParticles: 760,
                bursts: new[]
                {
                    new ParticleSystem.Burst(0f, 70, 105),
                    new ParticleSystem.Burst(0.16f, 38, 62),
                    new ParticleSystem.Burst(0.42f, 26, 48),
                    new ParticleSystem.Burst(0.78f, 20, 36),
                });
            ConfigureBloodRenderer(spray, 22);

            GameObject jetObject = new GameObject("Bundy_Finisher_BloodJet");
            jetObject.transform.SetParent(transform, false);
            ParticleSystem jet = jetObject.AddComponent<ParticleSystem>();
            ConfigureSpray(
                jet,
                rate: 145f,
                minSpeed: 10f,
                maxSpeed: 18f,
                minSize: 0.035f,
                maxSize: 0.105f,
                gravity: 0.85f,
                minLifetime: 0.18f,
                maxLifetime: 0.58f,
                coneAngle: 5f,
                radius: 0.035f,
                length: 0.38f,
                maxParticles: 520,
                bursts: new[]
                {
                    new ParticleSystem.Burst(0f, 55, 85),
                    new ParticleSystem.Burst(0.24f, 28, 52),
                    new ParticleSystem.Burst(0.55f, 22, 42),
                });
            ConfigureBloodRenderer(jet, 23);

            GameObject mistObject = new GameObject("Bundy_Finisher_BloodMist");
            mistObject.transform.SetParent(transform, false);
            ParticleSystem mist = mistObject.AddComponent<ParticleSystem>();
            ConfigureSpray(
                mist,
                rate: 95f,
                minSpeed: 1.8f,
                maxSpeed: 5.2f,
                minSize: 0.12f,
                maxSize: 0.32f,
                gravity: 0.28f,
                minLifetime: 0.38f,
                maxLifetime: 1.15f,
                coneAngle: 22f,
                radius: 0.12f,
                length: 0.16f,
                maxParticles: 430,
                bursts: new[]
                {
                    new ParticleSystem.Burst(0f, 28, 48),
                    new ParticleSystem.Burst(0.3f, 18, 34),
                    new ParticleSystem.Burst(0.7f, 14, 28),
                });
            ConfigureBloodRenderer(mist, 20);
        }

        private static void ConfigureSpray(
            ParticleSystem particle,
            float rate,
            float minSpeed,
            float maxSpeed,
            float minSize,
            float maxSize,
            float gravity,
            float minLifetime,
            float maxLifetime,
            float coneAngle,
            float radius,
            float length,
            int maxParticles,
            ParticleSystem.Burst[] bursts)
        {
            ParticleSystem.MainModule main = particle.main;
            main.duration = DefaultFollowSeconds;
            main.loop = true;
            main.maxParticles = maxParticles;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = new ParticleSystem.MinMaxCurve(minLifetime, maxLifetime);
            main.startSpeed = new ParticleSystem.MinMaxCurve(minSpeed, maxSpeed);
            main.startSize = new ParticleSystem.MinMaxCurve(minSize, maxSize);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(0.55f, 0f, 0f, 0.95f),
                new Color(0.16f, 0f, 0f, 0.7f));
            main.gravityModifier = gravity;

            ParticleSystem.EmissionModule emission = particle.emission;
            emission.enabled = true;
            emission.rateOverTime = rate;
            emission.SetBursts(bursts ?? new ParticleSystem.Burst[0]);

            ParticleSystem.ShapeModule shape = particle.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = coneAngle;
            shape.radius = radius;
            shape.length = length;

            ParticleSystem.ColorOverLifetimeModule color = particle.colorOverLifetime;
            color.enabled = true;
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(0.55f, 0f, 0f), 0f),
                    new GradientColorKey(new Color(0.3f, 0f, 0f), 0.6f),
                    new GradientColorKey(new Color(0.08f, 0f, 0f), 1f),
                },
                new[]
                {
                    new GradientAlphaKey(0.95f, 0f),
                    new GradientAlphaKey(0.75f, 0.55f),
                    new GradientAlphaKey(0f, 1f),
                });
            color.color = gradient;

            ParticleSystem.SizeOverLifetimeModule size = particle.sizeOverLifetime;
            size.enabled = true;
            AnimationCurve sizeCurve = new AnimationCurve(
                new Keyframe(0f, 1f),
                new Keyframe(0.65f, 0.72f),
                new Keyframe(1f, 0.18f));
            size.size = new ParticleSystem.MinMaxCurve(1f, sizeCurve);
        }

        private void ConfigureBloodRenderer(ParticleSystem particle, int sortingOrder)
        {
            ParticleSystemRenderer renderer = particle.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.sortingOrder = sortingOrder;
            renderer.sharedMaterial = _runtimeMaterial;
        }

        private static Material CreateBloodMaterial(PlayerControllerB victim)
        {
            Material source = null;
            try
            {
                ParticleSystemRenderer sourceRenderer = victim != null && victim.bloodParticle != null
                    ? victim.bloodParticle.GetComponent<ParticleSystemRenderer>()
                    : null;
                source = sourceRenderer != null ? sourceRenderer.sharedMaterial : null;
            }
            catch { }

            Material material = source != null
                ? new Material(source)
                : new Material(Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Hidden/Internal-Colored"));
            material.name = "Bundy_Finisher_Blood_Material";
            material.color = new Color(0.45f, 0f, 0f, 0.85f);
            return material;
        }

        private void LateUpdate()
        {
            if (_bundy == null || _victim == null)
            {
                StopAndDestroy();
                return;
            }

            float now = Time.time;
            if (now <= _followUntil)
                UpdateContactPose();
            else if (!_stoppedEmitting)
                StopEmitting();

            if (now >= _destroyAt)
                Destroy(gameObject);
        }

        private void UpdateContactPose()
        {
            Vector3 contact = _bundy.ResolveFinisherBloodContactPoint(_victim);
            Quaternion rotation = _bundy.ResolveFinisherBloodRotation(contact, _victim);
            if (IsFinite(contact))
            {
                transform.SetPositionAndRotation(contact, rotation);
            }
        }

        private void StopAndDestroy()
        {
            StopEmitting();
            Destroy(gameObject, 1.5f);
            enabled = false;
        }

        private void StopEmitting()
        {
            _stoppedEmitting = true;
            if (_particles == null)
                return;

            for (int i = 0; i < _particles.Length; i++)
            {
                ParticleSystem particle = _particles[i];
                if (particle != null)
                    particle.Stop(withChildren: true, ParticleSystemStopBehavior.StopEmitting);
            }
        }

        private void OnDestroy()
        {
            if (_runtimeMaterial != null)
                Destroy(_runtimeMaterial);
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
