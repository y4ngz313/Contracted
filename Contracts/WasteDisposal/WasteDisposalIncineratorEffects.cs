using System;
using UnityEngine;
using UnityEngine.Rendering;
using Y4NGZCompany.Facility.Shared;

namespace Y4NGZCompany.Contracts.WasteDisposal
{
    /// <summary>
    /// Visual-only fire, smoke, lights, and ember emission. It clones vanilla particle visuals
    /// when available and never invokes Landmine explosion/damage code.
    /// </summary>
    internal sealed class WasteDisposalIncineratorEffects
    {
        private static readonly int EmissiveColorId = Shader.PropertyToID("_EmissiveColor");
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
        private static readonly int EmissiveColorLdrId = Shader.PropertyToID("_EmissiveColorLDR");
        private static readonly int EmissiveIntensityId = Shader.PropertyToID("_EmissiveIntensity");
        private static readonly Color EmberColor = new Color(1f, 0.13f, 0.015f, 1f);
        private static readonly Color EmberColorLdr = new Color(1f, 0.13f, 0.015f);

        private static ParticleSystem _cachedLandmineParticle;
        private static ParticleSystem _cachedThrusterParticle;

        private readonly ParticleSystem _flame;
        private readonly ParticleSystem _burst;
        private readonly ParticleSystem _smoke;
        private readonly Light _passiveLight;
        private readonly Light _burnLight;
        private readonly Renderer _emberRenderer;
        private readonly Material _flameMaterial;
        private readonly Material _burstMaterial;
        private readonly Material _smokeMaterial;
        private Material _emberMaterial;
        private bool _emberHasEmissiveColor;
        private bool _emberHasEmissionColor;
        private bool _emberHasEmissiveColorLdr;
        private bool _emberHasEmissiveIntensity;
        private float _emberIntensity = float.NaN;
        private bool _burning = true;
        private float _burstUntil;

        internal WasteDisposalIncineratorEffects(
            Transform fireSocket,
            Transform smokeSocket,
            Transform passiveLightSocket,
            Transform burnLightSocket,
            Renderer emberRenderer)
        {
            ParticleSystem thrusterSource = FindVanillaThrusterParticle();
            ParticleSystem landmineSource = FindLandmineParticle();
            _flameMaterial = CreateSafeParticleMaterial(
                "Y4NGZ_IncineratorFlame",
                new Color(1f, 0.18f, 0.025f, 0.92f),
                additive: true,
                thrusterSource ?? landmineSource);
            _burstMaterial = CreateSafeParticleMaterial(
                "Y4NGZ_IncineratorBurst",
                new Color(1f, 0.48f, 0.045f, 0.95f),
                additive: true,
                landmineSource ?? thrusterSource);
            _smokeMaterial = CreateSafeParticleMaterial(
                "Y4NGZ_IncineratorSmoke",
                new Color(0.34f, 0.34f, 0.36f, 0.42f),
                additive: false,
                thrusterSource ?? landmineSource);
            _flame = CloneParticle(
                         thrusterSource,
                         fireSocket,
                         "IncineratorVanillaThrusterFlame",
                         loop: true,
                         Vector3.one * 0.52f,
                         _flameMaterial)
                     ?? CreateFallbackFlame(fireSocket, _flameMaterial);
            _burst = CloneParticle(
                         landmineSource,
                         fireSocket,
                         "IncineratorVanillaLandmineBurst",
                         loop: false,
                         Vector3.one * 0.34f,
                         _burstMaterial)
                     ?? CreateFallbackBurst(fireSocket, _burstMaterial);
            _smoke = CreateSmoke(smokeSocket, _smokeMaterial);
            _passiveLight = CreateLight(passiveLightSocket, "IncineratorPassiveLight", new Color(1f, 0.22f, 0.035f), 0.42f, 3.3f);
            _burnLight = CreateLight(burnLightSocket, "IncineratorBurnLight", new Color(1f, 0.29f, 0.045f), 2.35f, 6.5f);
            _emberRenderer = emberRenderer;
            BindEmberMaterial();
            SetBurning(false);
        }

        internal void SetBurning(bool burning)
        {
            if (_burning == burning)
                return;
            _burning = burning;
            SetLooping(_flame, burning);
            SetLooping(_smoke, burning);
            if (_burnLight != null) _burnLight.enabled = burning;
            ApplyEmberEmission(burning ? 2.6f : 0.32f);
        }

        internal void Burst(float intensity)
        {
            if (!_burning)
                return;
            intensity = Mathf.Clamp(intensity, 0.6f, 1.6f);
            _burstUntil = Mathf.Max(_burstUntil, Time.time + 0.55f * intensity);
            if (_burst != null)
            {
                _burst.gameObject.SetActive(true);
                _burst.Emit(Mathf.RoundToInt(9f * intensity));
            }
            if (_flame != null) _flame.Emit(Mathf.RoundToInt(7f * intensity));
            if (_smoke != null) _smoke.Emit(Mathf.RoundToInt(5f * intensity));
        }

        internal void Tick()
        {
            float burst = Time.time < _burstUntil ? 1f : 0f;
            if (_passiveLight != null)
                _passiveLight.intensity = 0.36f + Mathf.Sin(Time.time * 4.2f) * 0.06f + burst * 0.32f;
            if (_burnLight != null && _burning)
                _burnLight.intensity = 2.15f + Mathf.Sin(Time.time * 13f) * 0.32f + burst * 1.45f;
            ApplyEmberEmission(_burning ? 2.35f + Mathf.Sin(Time.time * 8f) * 0.35f + burst * 1.25f : 0.32f);
        }

        internal void Dispose()
        {
            if (_emberMaterial != null) UnityEngine.Object.Destroy(_emberMaterial);
            if (_flameMaterial != null) UnityEngine.Object.Destroy(_flameMaterial);
            if (_burstMaterial != null) UnityEngine.Object.Destroy(_burstMaterial);
            if (_smokeMaterial != null) UnityEngine.Object.Destroy(_smokeMaterial);
        }

        private void BindEmberMaterial()
        {
            if (_emberRenderer == null)
                return;
            // #716 A4: read the shared array so only the single assignment below instantiates.
            Material[] materials;
            try
            {
                Material[] shared = _emberRenderer.sharedMaterials;
                materials = new Material[shared.Length];
                Array.Copy(shared, materials, shared.Length);
            }
            catch { return; }
            int index = -1;
            for (int i = 0; i < materials.Length; i++)
            {
                if (materials[i] != null && materials[i].name.IndexOf("ember", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    index = i;
                    break;
                }
            }
            if (index < 0 && materials.Length > 0) index = 0;
            if (index < 0 || materials[index] == null) return;
            _emberMaterial = new Material(materials[index]) { name = materials[index].name + "_LGURuntime" };
            materials[index] = _emberMaterial;
            try { _emberRenderer.materials = materials; } catch { }
            _emberMaterial.EnableKeyword("_EMISSION");
            _emberMaterial.EnableKeyword("_MATERIAL_FEATURE_EMISSION");
            _emberHasEmissiveColor = _emberMaterial.HasProperty(EmissiveColorId);
            _emberHasEmissionColor = _emberMaterial.HasProperty(EmissionColorId);
            _emberHasEmissiveColorLdr = _emberMaterial.HasProperty(EmissiveColorLdrId);
            _emberHasEmissiveIntensity = _emberMaterial.HasProperty(EmissiveIntensityId);
        }

        /// <summary>
        /// #716 B9: called from Tick, so the four name-keyed HasProperty lookups and their
        /// setters ran every frame on every peer. Property presence and ids resolve once at
        /// bind time and an unchanged intensity writes nothing.
        /// </summary>
        private void ApplyEmberEmission(float intensity)
        {
            if (_emberMaterial == null)
                return;
            intensity = Mathf.Max(0f, intensity);
            if (!float.IsNaN(_emberIntensity) && Mathf.Abs(_emberIntensity - intensity) < 0.01f)
                return;
            _emberIntensity = intensity;
            Color color = EmberColor * intensity;
            if (_emberHasEmissiveColor) _emberMaterial.SetColor(EmissiveColorId, color);
            if (_emberHasEmissionColor) _emberMaterial.SetColor(EmissionColorId, color);
            if (_emberHasEmissiveColorLdr) _emberMaterial.SetColor(EmissiveColorLdrId, EmberColorLdr);
            if (_emberHasEmissiveIntensity) _emberMaterial.SetFloat(EmissiveIntensityId, intensity);
        }

        /// <summary>
        /// #716 A4: both prototype searches are whole-registry scans that ran on the spawn
        /// frame. They are cached for as long as the found object lives; Unity's null overload
        /// reports a destroyed reference as null, so a scene unload re-runs the search by
        /// itself and no explicit scene-change hook is needed.
        /// </summary>
        private static ParticleSystem FindLandmineParticle()
        {
            if (_cachedLandmineParticle != null)
                return _cachedLandmineParticle;

            Landmine[] mines = Resources.FindObjectsOfTypeAll<Landmine>();
            for (int i = 0; i < mines.Length; i++)
            {
                if (mines[i] != null && mines[i].explosionParticle != null)
                {
                    _cachedLandmineParticle = mines[i].explosionParticle;
                    return _cachedLandmineParticle;
                }
            }
            return null;
        }

        private static ParticleSystem FindVanillaThrusterParticle()
        {
            if (_cachedThrusterParticle != null)
                return _cachedThrusterParticle;

            ParticleSystem best = null;
            int bestScore = int.MinValue;
            ParticleSystem[] candidates = StartOfRound.Instance != null
                ? StartOfRound.Instance.GetComponentsInChildren<ParticleSystem>(true)
                : Resources.FindObjectsOfTypeAll<ParticleSystem>();
            for (int i = 0; i < candidates.Length; i++)
            {
                ParticleSystem candidate = candidates[i];
                if (candidate == null || candidate.name.StartsWith("Incinerator", StringComparison.OrdinalIgnoreCase))
                    continue;
                int score = ScoreHierarchy(candidate.transform);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }
            _cachedThrusterParticle = bestScore >= 24 ? best : null;
            return _cachedThrusterParticle;
        }

        /// <summary>
        /// #716 A4: scores the candidate's ancestry in one walk. This used to concatenate the
        /// whole hierarchy path and lowercase it for every candidate in the ship, which is two
        /// string allocations per particle system on the spawn frame.
        /// </summary>
        private static int ScoreHierarchy(Transform value)
        {
            bool thruster = false, ship = false, engine = false, flame = false, smoke = false;
            for (Transform current = value; current != null; current = current.parent)
            {
                string name = current.name;
                if (!thruster && name.IndexOf("thruster", StringComparison.OrdinalIgnoreCase) >= 0) thruster = true;
                if (!ship && name.IndexOf("ship", StringComparison.OrdinalIgnoreCase) >= 0) ship = true;
                if (!engine && name.IndexOf("engine", StringComparison.OrdinalIgnoreCase) >= 0) engine = true;
                if (!flame && (name.IndexOf("flame", StringComparison.OrdinalIgnoreCase) >= 0
                               || name.IndexOf("fire", StringComparison.OrdinalIgnoreCase) >= 0)) flame = true;
                if (!smoke && name.IndexOf("smoke", StringComparison.OrdinalIgnoreCase) >= 0) smoke = true;
            }
            int score = 0;
            if (thruster) score += 100;
            if (ship) score += 35;
            if (engine) score += 30;
            if (flame) score += 24;
            if (smoke) score -= 15;
            return score;
        }

        private static ParticleSystem CloneParticle(
            ParticleSystem source,
            Transform parent,
            string name,
            bool loop,
            Vector3 scale,
            Material safeMaterial)
        {
            if (source == null || parent == null)
                return null;
            GameObject clone;
            try { clone = UnityEngine.Object.Instantiate(source.gameObject, parent, false); }
            catch { return null; }
            clone.name = name;
            clone.transform.localPosition = Vector3.zero;
            clone.transform.localRotation = Quaternion.identity;
            clone.transform.localScale = scale;
            MonoBehaviour[] scripts = clone.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < scripts.Length; i++)
                if (scripts[i] != null) scripts[i].enabled = false;
            Collider[] colliders = clone.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
                if (colliders[i] != null) colliders[i].enabled = false;
            AudioSource[] audio = clone.GetComponentsInChildren<AudioSource>(true);
            for (int i = 0; i < audio.Length; i++)
                if (audio[i] != null) audio[i].enabled = false;
            ParticleSystem[] particles = clone.GetComponentsInChildren<ParticleSystem>(true);
            ParticleSystem particle = clone.GetComponent<ParticleSystem>() ?? clone.GetComponentInChildren<ParticleSystem>(true);
            if (particle == null)
            {
                UnityEngine.Object.Destroy(clone);
                return null;
            }
            for (int i = 0; i < particles.Length; i++)
            {
                ParticleSystem.MainModule childMain = particles[i].main;
                childMain.playOnAwake = false;
                particles[i].Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
            ParticleSystem.MainModule main = particle.main;
            main.loop = loop;
            ParticleSystemRenderer[] renderers = clone.GetComponentsInChildren<ParticleSystemRenderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                renderers[i].sharedMaterial = safeMaterial;
                renderers[i].trailMaterial = safeMaterial;
                renderers[i].shadowCastingMode = ShadowCastingMode.Off;
                renderers[i].receiveShadows = false;
            }
            // Keep the cloned hierarchy active so a nested ParticleSystem can be toggled.
            // Deactivating the clone root would leave the child inactive even after enabling it.
            clone.SetActive(true);
            particle.gameObject.SetActive(false);
            return particle;
        }

        private static ParticleSystem CreateFallbackFlame(Transform parent, Material material)
        {
            ParticleSystem system = CreateParticleObject(parent, "IncineratorFallbackFlame", material);
            ParticleSystem.MainModule main = system.main;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.22f, 0.48f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.35f, 0.8f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.18f, 0.42f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.1f, 0.01f, 0.9f), new Color(1f, 0.65f, 0.06f, 0.8f));
            ParticleSystem.EmissionModule emission = system.emission;
            emission.rateOverTime = 28f;
            ParticleSystem.ShapeModule shape = system.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 18f;
            shape.radius = 0.28f;
            return system;
        }

        private static ParticleSystem CreateFallbackBurst(Transform parent, Material material)
        {
            ParticleSystem system = CreateParticleObject(parent, "IncineratorFallbackBurst", material);
            ParticleSystem.MainModule main = system.main;
            main.loop = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.12f, 0.3f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.7f, 1.8f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.12f, 0.32f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.18f, 0.01f), new Color(1f, 0.8f, 0.12f));
            return system;
        }

        private static ParticleSystem CreateSmoke(Transform parent, Material material)
        {
            ParticleSystem system = CreateParticleObject(parent, "IncineratorSmoke", material);
            system.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            ParticleSystem.MainModule main = system.main;
            main.loop = true;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.2f, 2.6f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.25f, 0.65f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.15f, 0.42f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.2f, 0.2f, 0.2f, 0.34f), new Color(0.42f, 0.42f, 0.42f, 0.18f));
            ParticleSystem.EmissionModule emission = system.emission;
            emission.rateOverTime = 7f;
            ParticleSystem.ShapeModule shape = system.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 10f;
            shape.radius = 0.08f;
            return system;
        }

        private static ParticleSystem CreateParticleObject(Transform parent, string name, Material material)
        {
            GameObject obj = new GameObject(name);
            obj.transform.SetParent(parent, false);
            ParticleSystem system = obj.AddComponent<ParticleSystem>();
            ParticleSystem.MainModule main = system.main;
            main.playOnAwake = false;
            main.maxParticles = 180;
            ParticleSystemRenderer renderer = obj.GetComponent<ParticleSystemRenderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = material;
                renderer.trailMaterial = material;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
            system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            obj.SetActive(false);
            return system;
        }

        private static Material CreateSafeParticleMaterial(
            string name,
            Color color,
            bool additive,
            ParticleSystem visualSource)
        {
            Material material = FacilitySmokeEffectBuilder.CreateParticleMaterial(name, color, additive);
            Texture sourceTexture = FindParticleTexture(visualSource);
            if (sourceTexture != null)
            {
                SetTextureIfPresent(material, "_BaseColorMap", sourceTexture);
                SetTextureIfPresent(material, "_MainTex", sourceTexture);
                SetTextureIfPresent(material, "_UnlitColorMap", sourceTexture);
            }
            return material;
        }

        private static Texture FindParticleTexture(ParticleSystem source)
        {
            if (source == null)
                return null;
            ParticleSystemRenderer renderer = source.GetComponent<ParticleSystemRenderer>()
                                              ?? source.GetComponentInChildren<ParticleSystemRenderer>(true);
            Material material = renderer != null ? renderer.sharedMaterial : null;
            if (material == null)
                return null;
            string[] properties = { "_BaseColorMap", "_MainTex", "_UnlitColorMap", "_EmissiveColorMap" };
            for (int i = 0; i < properties.Length; i++)
            {
                if (!material.HasProperty(properties[i]))
                    continue;
                Texture texture = material.GetTexture(properties[i]);
                if (texture != null)
                    return texture;
            }
            return null;
        }

        private static void SetTextureIfPresent(Material material, string property, Texture texture)
        {
            if (material != null && texture != null && material.HasProperty(property))
                material.SetTexture(property, texture);
        }

        private static Light CreateLight(Transform parent, string name, Color color, float intensity, float range)
        {
            if (parent == null)
                return null;
            GameObject obj = new GameObject(name);
            obj.transform.SetParent(parent, false);
            Light light = obj.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = color;
            light.intensity = intensity;
            light.range = range;
            light.shadows = LightShadows.None;
            light.enabled = true;
            return light;
        }

        private static void SetLooping(ParticleSystem system, bool enabled)
        {
            if (system == null)
                return;
            system.gameObject.SetActive(enabled);
            if (enabled) system.Play(true);
            else system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }
    }
}
