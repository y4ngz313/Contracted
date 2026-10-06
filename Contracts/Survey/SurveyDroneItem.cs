using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using UnityEngine;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts._Shared;
namespace Y4NGZCompany.Contracts.Survey
{
    /// <summary>
    /// Per-item guidance and deploy behaviour for a survey beacon. Two visual modes share the
    /// same logic (#760):
    /// <list type="bullet">
    /// <item>Authored: the bundled beacon exposes named pivots (<c>Screen_Arrow</c>,
    /// <c>Screen_Ring</c>, <c>Antenna</c>, <c>Leg_1..3</c>). The arrow yaws to the target and
    /// carries the distance colour, the ring carries the floor state, and a beacon dropped in
    /// its target settles upright, folds out its legs, and raises its antenna.</item>
    /// <item>Legacy: no pivots (older bundle, Radar Booster fallback). A procedural cube pointer
    /// and point light do what they did before the beacon existed.</item>
    /// </list>
    /// The per-site placed flag is the only networked state. Beacons are interchangeable (#774):
    /// a beacon at rest in any open site plants that site and claims it locally, so a second
    /// beacon dropped in the same room stays a grabbable spare. A peer that meets a placed site
    /// with an unclaimed beacon resting in it snaps that beacon to the deployed pose.
    /// </summary>
    internal class SurveyDroneItem : MonoBehaviour
    {
        private static AudioClip _beepClip;
        private static AudioClip _confirmClip;
        private static AudioClip _legServoClip;
        private static AudioClip _mastServoClip;

        // The V4 beacon keeps the 126-degree leg sweep and floor datum. Four nested mast
        // stages extend 0.24 m each; older two-stage bundles keep their original strokes.
        // Lawson 2026-09-05: the sequence reads but was too quick; each phase beeps and the
        // motors are audible while the legs swing and the mast rises.
        private const float UprightSeconds = 0.4f;
        private const float LegSeconds = 0.9f;
        private const float MastSeconds = 1.6f;
        private const float LegDeployDegrees = 126f;
        private const float LegLiftMeters = 0.152f;
        private const float Mast1DeployMeters = 0.410f;
        private const float Mast2DeployMeters = 0.400f;
        private const float TipBlinkPeriodSeconds = 2.0f;
        private const float TipBlinkOnSeconds = 0.35f;
        private const float StandbyBreathSeconds = 2.2f;

        private static readonly Color DistanceFar = new Color(1f, 0.15f, 0.05f, 1f);
        private static readonly Color DistanceMid = new Color(1f, 0.75f, 0.1f, 1f);
        private static readonly Color DistanceNear = new Color(0.1f, 1f, 0.25f, 1f);
        private static readonly Color PlacedGreen = new Color(0.15f, 1f, 0.45f, 1f);
        private static readonly Color FloorSame = new Color(0.85f, 0.9f, 1f, 1f);
        private static readonly Color FloorAbove = new Color(0.15f, 0.55f, 1f, 1f);
        private static readonly Color FloorBelow = new Color(0.85f, 0.25f, 1f, 1f);

        // Which local beacon holds which site, so a spare dropped into a planted room is not
        // locked, and so a peer whose own detection lost the race still adopts the right beacon.
        private static readonly Dictionary<int, SurveyDroneItem> Claims = new Dictionary<int, SurveyDroneItem>();

        private int _index;
        private int _targetIndex = -1;
        private bool _initialized;
        private bool _placed;
        private float _nextBeepTime;
        private ulong _lastHolderClientId = ulong.MaxValue;
        private GrabbableObject _grabbable;
        private AudioSource _audio;
        private Light _signalLight;
        private ScanNodeProperties _scanNode;

        // Legacy procedural pointer.
        private GameObject _pointer;
        private Renderer _pointerRenderer;
        private Material _signalMaterial;
        private bool _hasBaseColor;
        private bool _hasEmissiveColor;
        private bool _hasColor;
        private bool _signalColorSet;
        private Color _signalColor;

        // Authored pivots.
        private bool _authored;
        private Transform _arrow;
        private Transform _ring;
        private Transform _deployedScreen;
        private Transform[] _masts;
        private Vector3[] _mastRestLocal;
        private Vector3[] _mastAxisLocal;
        private float[] _mastStrokes;
        private Transform _tip;
        private Transform[] _legs;
        private float[] _legSigns;
        private Quaternion _arrowRestLocal;
        private Quaternion[] _legRestLocal;
        private EmissiveSlot _arrowEmissive;
        private EmissiveSlot _ringEmissive;
        private EmissiveSlot _tipEmissive;
        private EmissiveSlot _deployedEmissive;
        private bool _tipLit;
        private int _heldTuningVersion = -1;
        private readonly SurveyBeaconCarryPose _carryPose = new SurveyBeaconCarryPose();

        // Deploy sequence. Phase 0 idle, 1 turning upright, 2 legs out and body lifting,
        // 3 mast rising, 4 deployed.
        private int _deployPhase;
        private float _deployStart;
        private Quaternion _settleFromRotation;
        private Vector3 _settleFromPosition;
        private Quaternion _settleToRotation;
        private Vector3 _settleToPosition;

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int EmissiveColorId = Shader.PropertyToID("_EmissiveColor");
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        private bool _scanNodeWritten;
        private bool _scanNodePlaced;
        private string _scanNodeLabel;

        /// <summary>
        /// One emissive material instance and the colour last written to it, so a static
        /// colour costs nothing per frame (#716/B11 pattern).
        /// </summary>
        private struct EmissiveSlot
        {
            public Material Material;
            public bool HasBaseColor;
            public bool HasEmissive;
            public bool HasEmission;
            public bool HasColor;
            public bool Set;
            public Color Color;
            public float Intensity;

            public static EmissiveSlot From(Renderer renderer, bool tintBase = true)
            {
                var slot = new EmissiveSlot();
                if (renderer == null)
                    return slot;

                // renderer.material instantiates once; the instance is destroyed in OnDestroy.
                slot.Material = renderer.material;
                if (slot.Material == null)
                    return slot;

                slot.HasBaseColor = tintBase && slot.Material.HasProperty(BaseColorId);
                slot.HasEmissive = slot.Material.HasProperty(EmissiveColorId);
                slot.HasEmission = slot.Material.HasProperty(EmissionColorId);
                slot.HasColor = tintBase && slot.Material.HasProperty(ColorId);
                return slot;
            }

            public void Apply(Color color, float intensity)
            {
                if (Material == null)
                    return;
                if (Set && Color == color && Mathf.Approximately(Intensity, intensity))
                    return;

                Set = true;
                Color = color;
                Intensity = intensity;
                Color dim = color * Mathf.Clamp01(intensity * 0.5f);
                if (HasBaseColor)
                    Material.SetColor(BaseColorId, dim);
                if (HasColor)
                    Material.SetColor(ColorId, dim);
                if (HasEmissive)
                    Material.SetColor(EmissiveColorId, color * intensity);
                if (HasEmission)
                    Material.SetColor(EmissionColorId, color * intensity);
            }

            public void Release()
            {
                if (Material != null)
                    Destroy(Material);
                Material = null;
            }
        }

        internal void Initialize(int index)
        {
            _index = index;
            _initialized = true;
            _scanNodeWritten = false;
            _grabbable = GetComponent<GrabbableObject>();
            _scanNode = GetComponentInChildren<ScanNodeProperties>();
            EnsureVisuals();
            UpdateScanNode();

            // A peer that meets an already-planted site with this beacon resting in it (late
            // join, reconnect) goes straight to the deployed pose. Nobody should watch it deploy
            // twice.
            if (!_placed
                && _grabbable != null
                && !_grabbable.isHeldByEnemy && _grabbable.parentObject == null
                && !_grabbable.isHeld
                && MoonContractState.TryFindSurveyTargetAt(transform.position, placed: true, out int placedIndex)
                && !IsClaimedByOther(placedIndex))
            {
                ClaimAndPlace(placedIndex, animate: false);
            }
        }

        internal void ReleaseForBlacksiteCarry()
        {
            if (_targetIndex >= 0 && Claims.TryGetValue(_targetIndex, out var owner) && owner == this) Claims.Remove(_targetIndex);
            _placed = false; _targetIndex = -1; _deployPhase = 0; _scanNodeWritten = false;
            if (_authored) { ApplyLegPose(0f); ApplyMastPose(0f); }
            if (_grabbable != null) _grabbable.grabbable = true;
        }

        private void Start()
        {
            if (!_initialized)
                Initialize(_index);
        }

        private void Update()
        {
            if (!_initialized)
                Initialize(_index);

            if (_grabbable != null && _grabbable.isHeld && _grabbable.playerHeldBy != null)
            {
                _lastHolderClientId = _grabbable.playerHeldBy.actualClientId;
                MoonContractState.NoteSurveyBeaconGrabbed();
            }

            bool atRest = _grabbable != null
                && !_grabbable.isHeldByEnemy && _grabbable.parentObject == null
                && !_grabbable.isHeld
                && (_grabbable.reachedFloorTarget || _grabbable.propBody == null);
            if (!_placed && atRest)
            {
                // #774: any open site this beacon rests in becomes its site. If another peer
                // flagged that site placed before this peer's own detection ran, this beacon
                // still takes it unless another local beacon already holds it.
                if (MoonContractState.TryFindSurveyTargetAt(transform.position, placed: false, out int openIndex))
                {
                    MoonContractState.MarkSurveyDronePlaced(openIndex, _lastHolderClientId);
                    ClaimAndPlace(openIndex, animate: true);
                }
                else if (MoonContractState.TryFindSurveyTargetAt(transform.position, placed: true, out int placedIndex)
                         && !IsClaimedByOther(placedIndex))
                {
                    ClaimAndPlace(placedIndex, animate: true);
                }
            }

            if (_placed)
                UpdateDeploySequence();

            // #807 temporary held-pose tuning: re-apply when the config strings change.
            if (_heldTuningVersion != SurveyHeldPoseTuning.Version && _grabbable != null)
            {
                _heldTuningVersion = SurveyHeldPoseTuning.Version;
                SurveyHeldPoseTuning.Apply(_grabbable.itemProperties);
            }

            // Guidance: the site this beacon holds, else the nearest open one, else nothing.
            int guideIndex = _placed ? _targetIndex : -1;
            if (guideIndex < 0 && !MoonContractState.TryGetNearestOpenSurveyTarget(transform.position, out guideIndex))
                guideIndex = -1;

            if (guideIndex >= 0
                && MoonContractState.TryGetSurveyTarget(guideIndex, out Vector3 target, out float radius, out float floorY, out string label, out _))
            {
                UpdateSignal(target, radius, floorY, hasTarget: true);
                UpdateScanNode(label);
            }
            else
            {
                UpdateSignal(transform.position, 0f, transform.position.y, hasTarget: false);
                UpdateScanNode(null);
            }
        }

        internal void ApplyHeldPoseAfterVanilla(GrabbableObject item)
        {
            // Called by the GrabbableObject.LateUpdate postfix, after the hand socket write.
            if (_authored && isActiveAndEnabled) _carryPose.Apply(item);
        }

        private bool IsClaimedByOther(int siteIndex)
        {
            return Claims.TryGetValue(siteIndex, out SurveyDroneItem owner) && owner != null && owner != this;
        }

        private void ClaimAndPlace(int siteIndex, bool animate)
        {
            _targetIndex = siteIndex;
            Claims[siteIndex] = this;
            SetPlacedLocal(animate);
        }

        private void EnsureVisuals()
        {
            if (_audio == null)
            {
                _audio = gameObject.GetComponent<AudioSource>();
                if (_audio == null)
                    _audio = gameObject.AddComponent<AudioSource>();
                _audio.spatialBlend = 1f;
                _audio.volume = 0.35f;
                _audio.maxDistance = 14f;
                _audio.playOnAwake = false;
            }

            if (_signalLight == null)
            {
                var lightObject = new GameObject("SurveySignalLight");
                lightObject.transform.SetParent(transform, false);
                lightObject.transform.localPosition = new Vector3(0f, 0.18f, 0f);
                _signalLight = lightObject.AddComponent<Light>();
                _signalLight.type = LightType.Point;
                _signalLight.range = 1.35f;
                _signalLight.intensity = 1.1f;
            }

            if (_authored || _pointer != null)
                return;

            if (TryBindAuthoredPivots())
            {
                _authored = true;
                return;
            }

            _pointer = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _pointer.name = "SurveyDirectionPointer";
            _pointer.transform.SetParent(transform, worldPositionStays: true);
            _pointer.transform.localScale = new Vector3(0.06f, 0.06f, 0.55f);

            var collider = _pointer.GetComponent<Collider>();
            if (collider != null)
                Destroy(collider);

            _pointerRenderer = _pointer.GetComponent<Renderer>();
            Shader shader = Shader.Find("HDRP/Lit") ?? Shader.Find("Standard");
            _signalMaterial = shader != null ? new Material(shader) : null;
            if (_signalMaterial != null && _pointerRenderer != null)
                _pointerRenderer.material = _signalMaterial;

            if (_signalMaterial != null)
            {
                _hasBaseColor = _signalMaterial.HasProperty(BaseColorId);
                _hasEmissiveColor = _signalMaterial.HasProperty(EmissiveColorId);
                _hasColor = _signalMaterial.HasProperty(ColorId);
            }
            _signalColorSet = false;
        }

        private bool TryBindAuthoredPivots()
        {
            _arrow = SurveyBeaconItemRegistry.FindChildByName(transform, SurveyBeaconItemRegistry.ScreenArrowName);
            if (_arrow == null)
                return false;

            _ring = SurveyBeaconItemRegistry.FindChildByName(transform, "Screen_Ring");
            _deployedScreen = SurveyBeaconItemRegistry.FindChildByName(transform, "Screen_Deployed");
            if (_deployedScreen != null)
                _deployedScreen.gameObject.SetActive(false);
            _masts = new Transform[4];
            _mastRestLocal = new Vector3[4];
            _mastAxisLocal = new Vector3[4];
            for (int i = 0; i < _masts.Length; i++)
                _masts[i] = SurveyBeaconItemRegistry.FindChildByName(transform, "Mast_" + (i + 1));
            bool fourStages = _masts[0] != null && _masts[1] != null && _masts[2] != null && _masts[3] != null;
            _mastStrokes = fourStages
                ? new[] { 0.24f, 0.24f, 0.24f, 0.24f }
                : new[] { Mast1DeployMeters, Mast2DeployMeters, 0f, 0f };
            _tip = SurveyBeaconItemRegistry.FindChildByName(transform, "Tip_Light");
            Transform leg1 = SurveyBeaconItemRegistry.FindChildByName(transform, "Leg_1");
            Transform leg2 = SurveyBeaconItemRegistry.FindChildByName(transform, "Leg_2");
            Transform leg3 = SurveyBeaconItemRegistry.FindChildByName(transform, "Leg_3");
            int legCount = (leg1 != null ? 1 : 0) + (leg2 != null ? 1 : 0) + (leg3 != null ? 1 : 0);
            _legs = new Transform[legCount];
            _legSigns = new float[legCount];
            _legRestLocal = new Quaternion[legCount];
            int cursor = 0;
            foreach (Transform leg in new[] { leg1, leg2, leg3 })
            {
                if (leg == null)
                    continue;
                _legs[cursor] = leg;
                // Leg_1/2 are the rear pair and swing -X; Leg_3 is front-centre and swings +X.
                _legSigns[cursor] = leg == leg3 ? 1f : -1f;
                _legRestLocal[cursor] = leg.localRotation;
                cursor++;
            }

            _arrowRestLocal = _arrow.localRotation;
            // Each mast segment extends along the body's up, expressed in its parent's space so
            // the FBX axis convention cannot flip it.
            for (int i = 0; i < _masts.Length; i++)
            {
                Transform mast = _masts[i];
                if (mast == null) continue;
                _mastRestLocal[i] = mast.localPosition;
                _mastAxisLocal[i] = mast.parent != null
                    ? mast.parent.InverseTransformDirection(transform.up).normalized
                    : Vector3.up;
            }

            // Keep V4's dark phosphor substrate; a bright diffuse tint washes out scanlines.
            _arrowEmissive = EmissiveSlot.From(_arrow.GetComponent<Renderer>(), tintBase: _deployedScreen == null);
            _ringEmissive = EmissiveSlot.From(_ring != null ? _ring.GetComponent<Renderer>() : null, tintBase: _deployedScreen == null);
            _tipEmissive = EmissiveSlot.From(_tip != null ? _tip.GetComponent<Renderer>() : null);
            _tipEmissive.Apply(Color.black, 0f);
            _deployedEmissive = EmissiveSlot.From(_deployedScreen != null ? _deployedScreen.GetComponent<Renderer>() : null, tintBase: false);
            _deployedEmissive.Apply(PlacedGreen, 1.15f);

            // The point light rides the mast tip so the deployed blink comes from the top.
            if (_tip != null && _signalLight != null)
            {
                _signalLight.transform.SetParent(_tip, false);
                _signalLight.transform.localPosition = Vector3.zero;
            }

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Survey] {name}: bound authored pivots (legs={legCount}, fourStageMast={fourStages}, deployedScreen={(_deployedScreen != null)}, tip={(_tip != null)}).");
            return true;
        }

        private void UpdateSignal(Vector3 target, float radius, float floorY, bool hasTarget)
        {
            EnsureVisuals();

            Vector3 origin = transform.position + Vector3.up * 0.35f;
            Vector3 toTarget = target - origin;
            float flatDistance = Vector2.Distance(
                new Vector2(transform.position.x, transform.position.z),
                new Vector2(target.x, target.z));
            float verticalDelta = floorY - transform.position.y;
            bool wrongFloor = Mathf.Abs(verticalDelta) > 7f;
            bool localHeld = IsHeldByLocalPlayer();
            bool anyHeld = _grabbable != null && _grabbable.isHeld;
            float closeness = Mathf.Clamp01(1f - flatDistance / 55f);

            Color distanceColor;
            if (flatDistance <= radius)
                distanceColor = DistanceNear;
            else if (closeness > 0.55f)
                distanceColor = Color.Lerp(DistanceMid, DistanceNear, (closeness - 0.55f) / 0.45f);
            else
                distanceColor = Color.Lerp(DistanceFar, DistanceMid, closeness / 0.55f);

            Color floorColor = !wrongFloor ? FloorSame : (verticalDelta > 0f ? FloorAbove : FloorBelow);

            if (_authored)
                UpdateAuthoredSignal(toTarget, flatDistance, radius, localHeld, anyHeld, hasTarget, distanceColor, floorColor);
            else
                UpdateLegacySignal(origin, toTarget, flatDistance, radius, wrongFloor, verticalDelta, localHeld, hasTarget, distanceColor);

            if (hasTarget && localHeld && !_placed && Time.time >= _nextBeepTime)
            {
                float interval = Mathf.Lerp(1.15f, 0.16f, Mathf.Clamp01(closeness + (flatDistance <= radius ? 0.3f : 0f)));
                _audio.pitch = wrongFloor
                    ? (verticalDelta > 0f ? 1.35f : 0.72f)
                    : Mathf.Lerp(0.85f, 1.25f, closeness);
                _audio.PlayOneShot(GetBeepClip(), wrongFloor ? 0.28f : 0.42f);
                _nextBeepTime = Time.time + interval;
            }
        }

        private void UpdateAuthoredSignal(Vector3 toTarget, float flatDistance, float radius, bool localHeld, bool anyHeld, bool hasTarget, Color distanceColor, Color floorColor)
        {
            // A claimed site is still deploying until phase 4. Peers adopting an already
            // placed beacon snap directly to that phase and get the same confirmation.
            bool showDeployed = _placed && _deployPhase >= 4;
            if (_deployedScreen != null && _deployedScreen.gameObject.activeSelf != showDeployed)
                _deployedScreen.gameObject.SetActive(showDeployed);
            if (_placed)
            {
                // Locked: arrow off, screen ring a static green; once the mast is up the tip
                // light blinks slowly and is the ongoing signal.
                if (_arrow.gameObject.activeSelf)
                    _arrow.gameObject.SetActive(false);
                _ringEmissive.Apply(PlacedGreen, 2.2f);

                bool lit = _deployPhase >= 4
                    && Mathf.Repeat(Time.time, TipBlinkPeriodSeconds) < TipBlinkOnSeconds;
                if (lit != _tipLit)
                {
                    _tipLit = lit;
                    _tipEmissive.Apply(lit ? PlacedGreen : Color.black, lit ? 4f : 0f);
                    if (_signalLight != null)
                    {
                        _signalLight.color = PlacedGreen;
                        _signalLight.intensity = lit ? 2.2f : 0f;
                        _signalLight.range = 4f;
                    }
                }
                return;
            }

            bool showArrow = hasTarget && (anyHeld || flatDistance <= radius * 1.35f);
            if (_arrow.gameObject.activeSelf != showArrow)
                _arrow.gameObject.SetActive(showArrow);

            if (showArrow)
            {
                // Yaw the arrow on the screen plane. Its rest pose points "up" the screen, which
                // is the item's up; the target bearing is measured against the item's forward
                // (the screen normal, so the player looking at the screen sees "away from me" as
                // up) and rolled around the screen normal.
                Vector3 flatTarget = new Vector3(toTarget.x, 0f, toTarget.z);
                if (flatTarget.sqrMagnitude > 0.01f)
                {
                    // The bearing in the horizontal plane, measured from the direction the
                    // screen's back faces (away from the reader) toward the reader's right.
                    Vector3 away = -Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
                    if (away.sqrMagnitude < 0.001f)
                        away = Vector3.ProjectOnPlane(transform.up, Vector3.up).normalized;
                    Vector3 rightFlat = Vector3.Cross(Vector3.up, away);
                    float bearing = Mathf.Atan2(Vector3.Dot(flatTarget, rightFlat), Vector3.Dot(flatTarget, away)) * Mathf.Rad2Deg;
                    // The arrow yaws about its local Z (the handoff's Unity axis). A target to the
                    // reader's right needs a clockwise turn as the reader sees it, which is a
                    // negative angle about the viewer-facing normal; flip if local Z points into
                    // the body instead.
                    float axisSign = Vector3.Dot(_arrow.forward, transform.forward) >= 0f ? -1f : 1f;
                    _arrow.localRotation = _arrowRestLocal * Quaternion.AngleAxis(axisSign * bearing, Vector3.forward);
                }

                _arrowEmissive.Apply(distanceColor, _deployedScreen != null ? 1.15f : 2.6f);
                _ringEmissive.Apply(floorColor, 1.6f);
                if (_signalLight != null)
                {
                    _signalLight.color = distanceColor;
                    _signalLight.intensity = 1.1f;
                }
            }
            else
            {
                // Standby on the floor: no arrow, the ring breathes so it can be found in the dark.
                float breath = 0.5f + 0.5f * Mathf.Sin(Time.time * (2f * Mathf.PI / StandbyBreathSeconds));
                _ringEmissive.Apply(FloorSame, Mathf.Lerp(0.15f, 0.7f, breath));
                if (_signalLight != null)
                {
                    _signalLight.color = FloorSame;
                    _signalLight.intensity = Mathf.Lerp(0.1f, 0.45f, breath);
                }
            }
        }

        private void UpdateLegacySignal(Vector3 origin, Vector3 toTarget, float flatDistance, float radius, bool wrongFloor, float verticalDelta, bool localHeld, bool hasTarget, Color distanceColor)
        {
            Color color;
            if (_placed)
                color = PlacedGreen;
            else if (wrongFloor)
                color = verticalDelta > 0f ? FloorAbove : FloorBelow;
            else
                color = distanceColor;

            ApplySignalColor(color);

            _pointer.SetActive(hasTarget && (localHeld || _placed || flatDistance <= radius * 1.35f));
            if (_pointer.activeSelf)
            {
                _pointer.transform.position = origin;
                if (toTarget.sqrMagnitude > 0.01f && !_placed)
                    _pointer.transform.rotation = Quaternion.LookRotation(toTarget.normalized, Vector3.up);
                else
                    _pointer.transform.rotation = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
            }
        }

        private bool IsHeldByLocalPlayer()
        {
            PlayerControllerB localPlayer = GameNetworkManager.Instance?.localPlayerController;
            return _grabbable != null
                && localPlayer != null
                && _grabbable.isHeld
                && !_grabbable.isPocketed
                && _grabbable.playerHeldBy == localPlayer;
        }

        private void ApplySignalColor(Color color)
        {
            if (_signalColorSet && _signalColor == color)
                return;

            _signalColorSet = true;
            _signalColor = color;

            if (_signalLight != null)
                _signalLight.color = color;

            if (_signalMaterial != null)
            {
                if (_hasBaseColor)
                    _signalMaterial.SetColor(BaseColorId, color);
                if (_hasEmissiveColor)
                    _signalMaterial.SetColor(EmissiveColorId, color * 1.8f);
                if (_hasColor)
                    _signalMaterial.SetColor(ColorId, color);
            }
            else if (_pointerRenderer != null)
            {
                _pointerRenderer.material.color = color;
            }
        }

        private void OnDestroy()
        {
            if (_targetIndex >= 0 && Claims.TryGetValue(_targetIndex, out SurveyDroneItem owner) && owner == this)
                Claims.Remove(_targetIndex);

            if (_signalMaterial != null)
            {
                Destroy(_signalMaterial);
                _signalMaterial = null;
            }
            _arrowEmissive.Release();
            _ringEmissive.Release();
            _tipEmissive.Release();
            _deployedEmissive.Release();
        }

        private void SetPlacedLocal(bool animate)
        {
            if (_placed)
                return;
            _placed = true;

            if (_grabbable != null)
            {
                _grabbable.grabbable = false;
                _grabbable.grabbableToEnemies = false;
                _grabbable.EnablePhysics(enable: false);
                if (_grabbable.propBody != null)
                    _grabbable.propBody.isKinematic = true;
            }

            if (!_authored)
            {
                ApplySignalColor(PlacedGreen);
                if (_audio != null)
                    _audio.PlayOneShot(GetBeepClip(), 0.55f);
                _deployPhase = 4;
                if (animate) SurveyDeploymentScan.Play(transform);
                return;
            }

            ComputeSettleTargets();
            if (animate)
            {
                _deployPhase = 1;
                _deployStart = Time.time;
                LogDeployPhase("upright");
            }
            else
            {
                ApplyLegPose(1f);
                ApplyMastPose(1f);
                transform.rotation = _settleToRotation;
                WriteRestPosition(_settleToPosition);
                _deployPhase = 4;
                LogDeployPhase("snap");
            }
        }

        /// <summary>
        /// Upright with the current yaw, body raised so the deployed legs meet the floor. The
        /// floor is probed straight down from the body centre; a miss keeps the current height.
        /// </summary>
        private void ComputeSettleTargets()
        {
            _settleFromRotation = transform.rotation;
            _settleFromPosition = transform.position;

            Vector3 forwardFlat = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
            if (forwardFlat.sqrMagnitude < 0.001f)
                forwardFlat = Vector3.ProjectOnPlane(-transform.up, Vector3.up);
            if (forwardFlat.sqrMagnitude < 0.001f)
                forwardFlat = Vector3.forward;
            _settleToRotation = Quaternion.LookRotation(forwardFlat.normalized, Vector3.up);

            _settleToPosition = _settleFromPosition;
            int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : Physics.DefaultRaycastLayers;
            if (Physics.Raycast(_settleFromPosition + Vector3.up * 0.05f, Vector3.down, out RaycastHit hit, 1.5f, mask, QueryTriggerInteraction.Ignore))
            {
                float originAboveFloor = -SurveyBeaconItemRegistry.BodyBottomLocalY + LegLiftMeters;
                _settleToPosition = new Vector3(_settleFromPosition.x, hit.point.y + originAboveFloor, _settleFromPosition.z);
            }
        }

        /// <summary>
        /// Moves the root and keeps vanilla in agreement. Once an item has landed,
        /// <c>GrabbableObject.Update</c> writes <c>localPosition = targetFloorPosition</c> every
        /// frame, so a lift written to the transform alone is undone the same frame (#791).
        /// </summary>
        private void WriteRestPosition(Vector3 world)
        {
            transform.position = world;
            if (_grabbable == null)
                return;
            _grabbable.targetFloorPosition = transform.parent != null
                ? transform.parent.InverseTransformPoint(world)
                : world;
            _grabbable.startFallingPosition = _grabbable.targetFloorPosition;
        }

        private void UpdateDeploySequence()
        {
            if (!_authored || _deployPhase == 0 || _deployPhase >= 4)
                return;

            float elapsed = Time.time - _deployStart;
            if (_deployPhase == 1)
            {
                float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / UprightSeconds));
                transform.rotation = Quaternion.Slerp(_settleFromRotation, _settleToRotation, t);
                WriteRestPosition(_settleFromPosition);
                if (elapsed >= UprightSeconds)
                {
                    _deployPhase = 2;
                    _deployStart = Time.time;
                    if (_audio != null)
                    {
                        _audio.pitch = 1f;
                        _audio.PlayOneShot(GetBeepClip(), 0.5f);
                        _audio.PlayOneShot(GetLegServoClip(), 0.55f);
                    }
                    LogDeployPhase("legs");
                }
                return;
            }

            if (_deployPhase == 2)
            {
                float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / LegSeconds));
                ApplyLegPose(t);
                WriteRestPosition(Vector3.Lerp(_settleFromPosition, _settleToPosition, t));
                if (elapsed >= LegSeconds)
                {
                    _deployPhase = 3;
                    _deployStart = Time.time;
                    if (_audio != null)
                    {
                        _audio.pitch = 1f;
                        _audio.PlayOneShot(GetBeepClip(), 0.5f);
                        _audio.PlayOneShot(GetMastServoClip(), 0.6f);
                    }
                    LogDeployPhase("mast");
                }
                return;
            }

            float mast = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / MastSeconds));
            ApplyMastPose(mast);
            WriteRestPosition(_settleToPosition);
            if (elapsed >= MastSeconds)
            {
                _deployPhase = 4;
                if (_audio != null)
                {
                    _audio.pitch = 1f;
                    _audio.PlayOneShot(GetConfirmClip(), 0.6f);
                }
                LogDeployPhase("deployed");
                SurveyDeploymentScan.Play(transform);
            }
        }

        private void ApplyLegPose(float t)
        {
            if (_legs == null)
                return;
            for (int i = 0; i < _legs.Length; i++)
            {
                if (_legs[i] == null)
                    continue;
                _legs[i].localRotation = _legRestLocal[i] * Quaternion.AngleAxis(_legSigns[i] * LegDeployDegrees * t, Vector3.right);
            }
        }

        private void ApplyMastPose(float t)
        {
            if (_masts == null) return;
            for (int i = 0; i < _masts.Length; i++)
                if (_masts[i] != null)
                    _masts[i].localPosition = _mastRestLocal[i] + _mastAxisLocal[i] * (_mastStrokes[i] * t);
        }

        private void LogDeployPhase(string phase)
        {
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Survey] {name} deploy {phase}: pos={transform.position:F3} rot={transform.eulerAngles:F1} " +
                $"targetFloor={(_grabbable != null ? _grabbable.targetFloorPosition.ToString("F3") : "n/a")} " +
                $"reachedFloor={(_grabbable != null && _grabbable.reachedFloorTarget)}");
        }

        private void UpdateScanNode(string label = null)
        {
            if (_scanNode == null)
                return;

            if (_scanNodeWritten
                && _scanNodePlaced == _placed
                && string.Equals(_scanNodeLabel, label, StringComparison.Ordinal))
                return;

            _scanNodeWritten = true;
            _scanNodePlaced = _placed;
            _scanNodeLabel = label;

            _scanNode.headerText = "Survey Beacon";
            _scanNode.subText = _placed
                ? "Survey locked"
                : string.IsNullOrWhiteSpace(label)
                    ? "Company survey equipment"
                    : $"Target: {label}";
            _scanNode.scrapValue = 0;
        }

        private static AudioClip GetBeepClip()
        {
            if (_beepClip != null)
                return _beepClip;

            const int sampleRate = 22050;
            const float duration = 0.08f;
            int samples = Mathf.CeilToInt(sampleRate * duration);
            float[] data = new float[samples];
            for (int i = 0; i < samples; i++)
            {
                float t = i / (float)sampleRate;
                float envelope = Mathf.Clamp01(1f - i / (float)samples);
                data[i] = Mathf.Sin(2f * Mathf.PI * 980f * t) * envelope * 0.45f;
            }

            _beepClip = AudioClip.Create("LGU Survey Beacon Beep", samples, 1, sampleRate, stream: false);
            _beepClip.SetData(data, 0);
            return _beepClip;
        }

        /// <summary>Two rising tones, the "locked" confirm played as the legs deploy.</summary>
        private static AudioClip GetConfirmClip()
        {
            if (_confirmClip != null)
                return _confirmClip;

            const int sampleRate = 22050;
            const float toneSeconds = 0.11f;
            const float gapSeconds = 0.03f;
            int toneSamples = Mathf.CeilToInt(sampleRate * toneSeconds);
            int gapSamples = Mathf.CeilToInt(sampleRate * gapSeconds);
            int samples = toneSamples * 2 + gapSamples;
            float[] data = new float[samples];
            for (int i = 0; i < toneSamples; i++)
            {
                float t = i / (float)sampleRate;
                float envelope = Mathf.Sin(Mathf.PI * i / (float)toneSamples);
                data[i] = Mathf.Sin(2f * Mathf.PI * 740f * t) * envelope * 0.42f;
                data[toneSamples + gapSamples + i] = Mathf.Sin(2f * Mathf.PI * 1108f * t) * envelope * 0.42f;
            }

            _confirmClip = AudioClip.Create("LGU Survey Beacon Confirm", samples, 1, sampleRate, stream: false);
            _confirmClip.SetData(data, 0);
            return _confirmClip;
        }

        /// <summary>Short, low motor whir while the legs swing out.</summary>
        private static AudioClip GetLegServoClip()
        {
            if (_legServoClip == null)
                _legServoClip = SynthesizeServo("LGU Survey Beacon Leg Servo", LegSeconds, 110f, 150f, 0.5f);
            return _legServoClip;
        }

        /// <summary>Longer rising whir while the mast telescopes up, ending in a soft latch.</summary>
        private static AudioClip GetMastServoClip()
        {
            if (_mastServoClip == null)
                _mastServoClip = SynthesizeServo("LGU Survey Beacon Mast Servo", MastSeconds, 160f, 260f, 0.45f);
            return _mastServoClip;
        }

        /// <summary>
        /// A small electric motor: a band-limited sawtooth with a slow pitch ramp, a 30 Hz
        /// amplitude wobble, a short attack, and a click at the end.
        /// </summary>
        private static AudioClip SynthesizeServo(string name, float seconds, float startHz, float endHz, float gain)
        {
            const int sampleRate = 22050;
            int samples = Mathf.CeilToInt(sampleRate * seconds);
            float[] data = new float[samples];
            float phase = 0f;
            for (int i = 0; i < samples; i++)
            {
                float t = i / (float)sampleRate;
                float u = i / (float)samples;
                float hz = Mathf.Lerp(startHz, endHz, Mathf.SmoothStep(0f, 1f, u));
                phase += hz / sampleRate;
                if (phase >= 1f)
                    phase -= 1f;
                // Three harmonics of a sawtooth keep it from buzzing.
                float saw = Mathf.Sin(2f * Mathf.PI * phase)
                            + 0.5f * Mathf.Sin(4f * Mathf.PI * phase)
                            + 0.33f * Mathf.Sin(6f * Mathf.PI * phase);
                float wobble = 0.85f + 0.15f * Mathf.Sin(2f * Mathf.PI * 30f * t);
                float attack = Mathf.Clamp01(t / 0.06f);
                float release = Mathf.Clamp01((seconds - t) / 0.12f);
                data[i] = saw * wobble * attack * release * gain * 0.3f;
            }
            // End click.
            int clickStart = Mathf.Max(0, samples - Mathf.CeilToInt(sampleRate * 0.03f));
            for (int i = clickStart; i < samples; i++)
            {
                float k = (i - clickStart) / (float)(samples - clickStart);
                data[i] += Mathf.Sin(2f * Mathf.PI * 1800f * (i / (float)sampleRate)) * (1f - k) * gain * 0.5f;
            }

            AudioClip clip = AudioClip.Create(name, samples, 1, sampleRate, stream: false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
