using System.Collections;
using System.Reflection;
using GameNetcodeStuff;
using TMPro;
using UnityEngine;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts._Shared;
namespace Y4NGZCompany.Contracts.BlackoutAudit
{
    /// <summary>
    /// UNSPAWNED DEAD CODE as of #406: <see cref="Spawn"/> has no callers and
    /// <c>MoonContractState._blackoutLever</c> is never assigned, so the live Blackout Audit is
    /// resolved entirely through breaker boxes. It is also un-networked - a plain MonoBehaviour
    /// with a local <c>InteractTrigger</c> - so reviving it requires networking the pull to the
    /// host before either the completion or its per-player attribution can work in multiplayer.
    /// </summary>
    internal class BlackoutAuditLever : MonoBehaviour
    {
        private const int InteractLayer = 9;
        private const int ScanNodeLayer = 22;

        private static readonly Color PanelColor = new Color(0.08f, 0.09f, 0.08f, 1f);
        private static readonly Color PlateColor = new Color(0.20f, 0.22f, 0.18f, 1f);
        private static readonly Color HandleColor = new Color(0.12f, 0.12f, 0.11f, 1f);
        private static readonly Color SafetyStripeColor = new Color(0.85f, 0.55f, 0.08f, 1f);
        private static readonly Color KnobReadyColor = new Color(0.74f, 0.08f, 0.05f, 1f);
        private static readonly Color KnobCompleteColor = new Color(0.0f, 0.85f, 0.35f, 1f);

        private Transform _handlePivot;
        private Renderer _knobRenderer;
        private Light _indicatorLight;
        private AudioSource _audioSource;
        private InteractTrigger _trigger;
        private Coroutine _animationCoroutine;

        internal bool Pulled { get; private set; }

        internal static BlackoutAuditLever Spawn(Vector3 position, Quaternion rotation)
        {
            var root = new GameObject("LGU_BlackoutAuditLever");
            root.transform.SetPositionAndRotation(position, rotation);

            var lever = root.AddComponent<BlackoutAuditLever>();
            lever.Build();
            return lever;
        }

        private void Build()
        {
            BuildVisuals();
            BuildInteraction();
            BuildScanNode();

            _audioSource = gameObject.AddComponent<AudioSource>();
            _audioSource.spatialBlend = 1f;
            _audioSource.minDistance = 2f;
            _audioSource.maxDistance = 18f;
            _audioSource.volume = 0.8f;
        }

        private void BuildVisuals()
        {
            var basePlate = CreatePrimitive("BasePlate", PrimitiveType.Cube, transform);
            basePlate.transform.localPosition = new Vector3(0f, 0.08f, 0f);
            basePlate.transform.localScale = new Vector3(1.08f, 0.14f, 0.78f);
            Paint(basePlate, PlateColor);

            var backPlate = CreatePrimitive("BackPlate", PrimitiveType.Cube, transform);
            backPlate.transform.localPosition = new Vector3(0f, 0.72f, -0.31f);
            backPlate.transform.localScale = new Vector3(1.08f, 1.18f, 0.1f);
            Paint(backPlate, PanelColor);

            var labelPlate = CreatePrimitive("LabelPlate", PrimitiveType.Cube, transform);
            labelPlate.transform.localPosition = new Vector3(0f, 1.24f, -0.235f);
            labelPlate.transform.localScale = new Vector3(0.82f, 0.13f, 0.04f);
            Paint(labelPlate, new Color(0.02f, 0.16f, 0.12f, 1f));
            CreateLabelText("AUDIT POWER", new Vector3(0f, 1.245f, -0.18f), 1.1f, Color.green);

            CreateTrim("TrimTop", new Vector3(0f, 1.35f, -0.18f), new Vector3(1.2f, 0.05f, 0.08f), SafetyStripeColor);
            CreateTrim("TrimBottom", new Vector3(0f, 0.12f, -0.18f), new Vector3(1.2f, 0.05f, 0.08f), SafetyStripeColor);
            CreateTrim("TrimLeft", new Vector3(-0.58f, 0.72f, -0.18f), new Vector3(0.05f, 1.15f, 0.08f), SafetyStripeColor);
            CreateTrim("TrimRight", new Vector3(0.58f, 0.72f, -0.18f), new Vector3(0.05f, 1.15f, 0.08f), SafetyStripeColor);

            _handlePivot = new GameObject("HandlePivot").transform;
            _handlePivot.SetParent(transform, false);
            _handlePivot.localPosition = new Vector3(0f, 0.64f, -0.1f);
            _handlePivot.localRotation = Quaternion.Euler(-34f, 0f, 0f);

            var handle = CreatePrimitive("Handle", PrimitiveType.Cylinder, _handlePivot);
            handle.transform.localPosition = Vector3.up * 0.42f;
            handle.transform.localScale = new Vector3(0.055f, 0.46f, 0.055f);
            Paint(handle, HandleColor);

            var knob = CreatePrimitive("Knob", PrimitiveType.Sphere, _handlePivot);
            knob.transform.localPosition = Vector3.up * 0.94f;
            knob.transform.localScale = new Vector3(0.22f, 0.22f, 0.22f);
            _knobRenderer = knob.GetComponent<Renderer>();
            Paint(knob, KnobReadyColor);

            var lightGo = new GameObject("IndicatorLight");
            lightGo.transform.SetParent(transform, false);
            lightGo.transform.localPosition = new Vector3(0.25f, 0.96f, -0.13f);
            _indicatorLight = lightGo.AddComponent<Light>();
            _indicatorLight.type = LightType.Point;
            _indicatorLight.range = 5f;
            _indicatorLight.intensity = 3.2f;
            _indicatorLight.color = KnobReadyColor;

            var beacon = CreatePrimitive("WarningBeacon", PrimitiveType.Sphere, transform);
            beacon.transform.localPosition = new Vector3(0.43f, 1.24f, -0.14f);
            beacon.transform.localScale = new Vector3(0.16f, 0.16f, 0.16f);
            Paint(beacon, KnobReadyColor);
        }

        private void BuildInteraction()
        {
            var triggerObject = new GameObject("InteractionTrigger");
            triggerObject.transform.SetParent(transform, false);
            triggerObject.transform.localPosition = new Vector3(0f, 0.72f, 0.05f);
            triggerObject.layer = InteractLayer;
            SetTagSafe(triggerObject, "InteractTrigger");

            var collider = triggerObject.AddComponent<BoxCollider>();
            collider.size = new Vector3(1.25f, 1.45f, 0.95f);
            collider.isTrigger = true;

            _trigger = triggerObject.AddComponent<InteractTrigger>();
            _trigger.interactable = true;
            _trigger.oneHandedItemAllowed = true;
            _trigger.twoHandedItemAllowed = false;
            _trigger.holdInteraction = false;
            _trigger.interactCooldown = true;
            _trigger.cooldownTime = 0.4f;
            _trigger.hoverTip = "Restore power : [LMB]";
            _trigger.disabledHoverTip = "[ Directive complete ]";
            _trigger.disableTriggerMesh = true;
            _trigger.onInteract = new InteractEvent();
            _trigger.onInteractEarly = new InteractEvent();
            _trigger.onInteractEarlyOtherClients = new InteractEvent();
            _trigger.onStopInteract = new InteractEvent();
            _trigger.onCancelAnimation = new InteractEvent();
            _trigger.holdingInteractEvent = new InteractEventFloat();
            _trigger.onInteract.AddListener(OnInteract);
        }

        private void BuildScanNode()
        {
            var scanObject = new GameObject("AuditLeverScanNode");
            scanObject.transform.SetParent(transform, false);
            scanObject.transform.localPosition = new Vector3(0f, 1.1f, 0f);
            scanObject.layer = ScanNodeLayer;

            var collider = scanObject.AddComponent<SphereCollider>();
            collider.radius = 0.65f;
            collider.isTrigger = true;

            var scanNode = scanObject.AddComponent<ScanNodeProperties>();
            scanNode.headerText = "AUDIT LEVER";
            scanNode.subText = "restore systems";
            scanNode.maxRange = 24;
            scanNode.minRange = 1;
            scanNode.requiresLineOfSight = false;
            scanNode.nodeType = 0;
        }

        private void OnInteract(PlayerControllerB player)
        {
            if (Pulled)
                return;

            Pulled = true;
            if (_trigger != null)
            {
                _trigger.interactable = false;
                _trigger.hoverTip = "[ Directive complete ]";
            }

            if (player != null)
                player.playerBodyAnimator.SetTrigger("SA_PullLever");

            PlayPullAudio();

            if (_animationCoroutine != null)
                StopCoroutine(_animationCoroutine);
            _animationCoroutine = StartCoroutine(AnimatePulled());

            // #406: this pays crew-wide, and cannot do better as written. InteractTrigger.onInteract
            // fires only on the interacting machine, and ContractProgressionNetwork.BroadcastServer
            // is a no-op on a client, so a remote pull is invisible to the host - who is the only
            // machine allowed to author a progression event. Nothing was built to fix that here
            // because this component is dead code: Spawn() has no callers and MoonContractState's
            // _blackoutLever is never assigned, so no lever ever exists in a live round. Reviving
            // it means networking the pull to the host first; per-player credit follows from that,
            // not from this call site.
            MoonContractState.CompleteBlackoutAudit("audit lever");
        }

        private IEnumerator AnimatePulled()
        {
            Quaternion start = _handlePivot.localRotation;
            Quaternion end = Quaternion.Euler(48f, 0f, 0f);
            Color startColor = _indicatorLight != null ? _indicatorLight.color : KnobReadyColor;

            float elapsed = 0f;
            const float duration = 0.42f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                float smooth = t * t * (3f - 2f * t);

                _handlePivot.localRotation = Quaternion.Slerp(start, end, smooth);
                SetKnobColor(Color.Lerp(KnobReadyColor, KnobCompleteColor, smooth));
                if (_indicatorLight != null)
                {
                    _indicatorLight.color = Color.Lerp(startColor, KnobCompleteColor, smooth);
                    _indicatorLight.intensity = Mathf.Lerp(1.4f, 2.4f, smooth);
                }

                yield return null;
            }

            _handlePivot.localRotation = end;
            SetKnobColor(KnobCompleteColor);
            if (_indicatorLight != null)
            {
                _indicatorLight.color = KnobCompleteColor;
                _indicatorLight.intensity = 2.0f;
            }

            _animationCoroutine = null;
        }

        private void PlayPullAudio()
        {
            if (_audioSource == null)
                return;

            AudioClip clip = GetStartOfRoundAudioClip("shipDoorsClosingJingle");

            if (clip != null)
            {
                _audioSource.pitch = Random.Range(0.92f, 1.08f);
                _audioSource.PlayOneShot(clip, 0.55f);
            }

            AudioClip whir = GetStartOfRoundAudioClip("startGameWhir");
            if (whir != null)
                _audioSource.PlayOneShot(whir, 0.22f);
        }

        private static AudioClip GetStartOfRoundAudioClip(string fieldName)
        {
            var start = StartOfRound.Instance;
            if (start == null)
                return null;

            var field = typeof(StartOfRound).GetField(
                fieldName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var source = field?.GetValue(start) as AudioSource;
            return source != null ? source.clip : null;
        }

        private void SetKnobColor(Color color)
        {
            if (_knobRenderer != null)
                _knobRenderer.material.color = color;
        }

        private static GameObject CreatePrimitive(string name, PrimitiveType type, Transform parent)
        {
            var obj = GameObject.CreatePrimitive(type);
            obj.name = name;
            obj.transform.SetParent(parent, false);

            var collider = obj.GetComponent<Collider>();
            if (collider != null)
                Destroy(collider);

            return obj;
        }

        private void CreateTrim(string name, Vector3 localPosition, Vector3 localScale, Color color)
        {
            var trim = CreatePrimitive(name, PrimitiveType.Cube, transform);
            trim.transform.localPosition = localPosition;
            trim.transform.localScale = localScale;
            Paint(trim, color);
        }

        private void CreateLabelText(string text, Vector3 localPosition, float fontSize, Color color)
        {
            var obj = new GameObject("AuditLeverLabelText");
            obj.transform.SetParent(transform, false);
            obj.transform.localPosition = localPosition;
            obj.transform.localRotation = Quaternion.Euler(0f, 0f, 0f);

            var tmp = obj.AddComponent<TextMeshPro>();
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = color;
            tmp.enableWordWrapping = false;
            tmp.rectTransform.sizeDelta = new Vector2(1.1f, 0.24f);
        }

        private static void Paint(GameObject obj, Color color)
        {
            var renderer = obj.GetComponent<Renderer>();
            if (renderer != null)
                renderer.material.color = color;
        }

        private static void SetTagSafe(GameObject obj, string tag)
        {
            try
            {
                obj.tag = tag;
            }
            catch
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts] Tag '{tag}' was not available for audit lever interaction.");
            }
        }
    }
}
