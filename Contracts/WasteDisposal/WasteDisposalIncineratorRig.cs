using System;
using GameNetcodeStuff;
using UnityEngine;

namespace Y4NGZCompany.Contracts.WasteDisposal
{
    internal readonly struct WasteDisposalIncineratorMotionProfile
    {
        internal readonly Vector3 FeedDoorOpenEuler;
        internal readonly float FeedDoorTravelSeconds;
        internal readonly Vector3 ButtonPressedLocalOffset;
        internal readonly Vector3 BurnLeverActivatedEuler;
        internal readonly Vector3 GaugeColdEuler;
        internal readonly Vector3 GaugeHotEuler;
        internal readonly Vector3 StackDamperOpenEuler;
        internal readonly Vector3 AshDoorCompleteEuler;

        internal WasteDisposalIncineratorMotionProfile(
            Vector3 feedDoorOpenEuler,
            float feedDoorTravelSeconds,
            Vector3 buttonPressedLocalOffset,
            Vector3 burnLeverActivatedEuler,
            Vector3 gaugeColdEuler,
            Vector3 gaugeHotEuler,
            Vector3 stackDamperOpenEuler,
            Vector3 ashDoorCompleteEuler)
        {
            FeedDoorOpenEuler = feedDoorOpenEuler;
            FeedDoorTravelSeconds = Mathf.Clamp(feedDoorTravelSeconds, 0.1f, 3f);
            ButtonPressedLocalOffset = buttonPressedLocalOffset;
            BurnLeverActivatedEuler = burnLeverActivatedEuler;
            GaugeColdEuler = gaugeColdEuler;
            GaugeHotEuler = gaugeHotEuler;
            StackDamperOpenEuler = stackDamperOpenEuler;
            AshDoorCompleteEuler = ashDoorCompleteEuler;
        }

        internal static WasteDisposalIncineratorMotionProfile Default =>
            new WasteDisposalIncineratorMotionProfile(
                new Vector3(0f, 68f, 0f),
                0.65f,
                new Vector3(0f, 0f, -0.006f),
                new Vector3(0f, 0f, -58f),
                new Vector3(0f, 0f, -55f),
                new Vector3(0f, 0f, 55f),
                new Vector3(0f, 70f, 0f),
                new Vector3(-8f, 0f, 0f));
    }

    internal sealed class WasteDisposalIncineratorRig
    {
        private const float VibrationPositionAmplitude = 0.018f;
        private const float VibrationRotationAmplitude = 0.55f;

        /// <summary>
        /// #716 B9: the screen text and its manual camera render used to be rebuilt from every
        /// frame's raw floats. Semantic changes still apply immediately; the running clock and
        /// progress bar settle to 4 Hz.
        /// </summary>
        private const float ScreenUpdateIntervalSeconds = 0.25f;
        internal readonly Transform FeedDoor;
        internal readonly Transform CctvMarker;
        internal readonly InteractTrigger StartTrigger;
        internal readonly InteractTrigger StopTrigger;
        internal readonly InteractTrigger DoorTrigger;
        internal readonly InteractTrigger RepairTrigger;
        internal readonly InteractTrigger PlaceTrigger;

        private readonly Transform _visualRoot;
        private readonly Transform _burnLever;
        private readonly Transform _gaugeNeedle;
        private readonly Transform _fanRotor;
        private readonly Transform _stackDamper;
        private readonly Transform _ashDoor;
        private readonly Quaternion _feedDoorRest;
        private readonly Vector3 _visualRootRestPosition;
        private readonly Quaternion _visualRootRestRotation;
        private readonly Quaternion _burnLeverRest;
        private readonly Quaternion _gaugeNeedleRest;
        private readonly Quaternion _stackDamperRest;
        private readonly Quaternion _ashDoorRest;
        private readonly WasteDisposalIncineratorMotionProfile _motion;
        private readonly ButtonVisual _startButton;
        private readonly ButtonVisual _stopButton;
        private readonly WasteDisposalIncineratorEffects _effects;
        private readonly WasteDisposalIncineratorAudio _audio;
        private readonly WasteDisposalIncineratorScreenDisplay _screen;
        private readonly float _feedDoorTravelSeconds;
        private bool _feedDoorInitialized;
        private bool _feedDoorTargetOpen;
        private float _feedDoorProgress;
        private bool _tipsInitialized;
        private WasteDisposalIncineratorController.IncineratorState _tipState;
        private bool _tipDoorOpen;
        private bool _tipDoorVisuallyClosed;
        private int _tipStagedValue = int.MinValue;
        private int _tipRequiredValue = int.MinValue;
        private float _tipRepairHoldSeconds = float.NaN;
        private bool _screenStateInitialized;
        private WasteDisposalIncineratorController.IncineratorState _screenState;
        private bool _screenDoorOpen;
        private int _screenStagedValue = int.MinValue;
        private int _screenRequiredValue = int.MinValue;
        private float _nextScreenUpdateTime;

        internal WasteDisposalIncineratorRig(
            Transform visualRoot,
            Transform feedDoor,
            Transform burnLever,
            Transform startButton,
            Transform stopButton,
            Transform gaugeNeedle,
            Transform fanRotor,
            Transform stackDamper,
            Transform ashDoor,
            Transform cctvMarker,
            InteractTrigger startTrigger,
            InteractTrigger stopTrigger,
            InteractTrigger doorTrigger,
            InteractTrigger repairTrigger,
            InteractTrigger placeTrigger,
            Renderer screenRenderer,
            Renderer emberRenderer,
            WasteDisposalIncineratorEffects effects,
            WasteDisposalIncineratorAudio audio,
            WasteDisposalIncineratorMotionProfile motion)
        {
            _visualRoot = visualRoot;
            FeedDoor = feedDoor;
            CctvMarker = cctvMarker;
            StartTrigger = startTrigger;
            StopTrigger = stopTrigger;
            DoorTrigger = doorTrigger;
            RepairTrigger = repairTrigger;
            PlaceTrigger = placeTrigger;
            _burnLever = burnLever;
            _gaugeNeedle = gaugeNeedle;
            _fanRotor = fanRotor;
            _stackDamper = stackDamper;
            _ashDoor = ashDoor;
            _motion = motion;
            _visualRootRestPosition = visualRoot != null ? visualRoot.localPosition : Vector3.zero;
            _visualRootRestRotation = RestRotation(visualRoot);
            _feedDoorRest = RestRotation(feedDoor);
            _burnLeverRest = RestRotation(burnLever);
            _gaugeNeedleRest = RestRotation(gaugeNeedle);
            _stackDamperRest = RestRotation(stackDamper);
            _ashDoorRest = RestRotation(ashDoor);
            _feedDoorTravelSeconds = motion.FeedDoorTravelSeconds;
            _startButton = new ButtonVisual(startButton, new Color(0.12f, 1f, 0.22f), motion.ButtonPressedLocalOffset);
            _stopButton = new ButtonVisual(stopButton, new Color(1f, 0.08f, 0.035f), motion.ButtonPressedLocalOffset);
            _effects = effects;
            _audio = audio;
            _screen = screenRenderer != null ? new WasteDisposalIncineratorScreenDisplay(screenRenderer) : null;
        }

        internal event Action StartRequested;
        internal event Action StopRequested;
        internal event Action DoorRequested;
        internal event Action RepairRequested;
        internal event Action<PlayerControllerB> PlaceRequested;

        internal void WireInteraction()
        {
            StartTrigger?.onInteract.AddListener(OnStartInteract);
            StopTrigger?.onInteract.AddListener(OnStopInteract);
            DoorTrigger?.onInteract.AddListener(OnDoorInteract);
            RepairTrigger?.onInteract.AddListener(OnRepairInteract);
            PlaceTrigger?.onInteract.AddListener(OnPlaceInteract);
        }

        internal void ApplyState(
            WasteDisposalIncineratorController.IncineratorState state,
            bool doorOpen,
            int stagedValue,
            int requiredValue,
            float cycleDuration,
            float remainingSeconds,
            float repairHoldSeconds)
        {
            bool loading = state == WasteDisposalIncineratorController.IncineratorState.Loading;
            bool burning = state == WasteDisposalIncineratorController.IncineratorState.Burning;
            bool paused = state == WasteDisposalIncineratorController.IncineratorState.Paused;
            bool malfunctioned = state == WasteDisposalIncineratorController.IncineratorState.Malfunctioned;
            bool finished = state == WasteDisposalIncineratorController.IncineratorState.Complete;
            float loadProgress = requiredValue > 0 ? Mathf.Clamp01(stagedValue / (float)requiredValue) : 0f;
            float cycleProgress = cycleDuration > 0.01f ? 1f - Mathf.Clamp01(remainingSeconds / cycleDuration) : 0f;
            float gaugeProgress = burning || paused || malfunctioned || finished ? (finished ? 1f : cycleProgress) : loadProgress;

            SetFeedDoorTarget(doorOpen);
            SetRelativeRotation(_burnLever, _burnLeverRest, Quaternion.Euler(loading ? Vector3.zero : _motion.BurnLeverActivatedEuler));
            SetRelativeRotation(_gaugeNeedle, _gaugeNeedleRest, Quaternion.Euler(Vector3.Lerp(_motion.GaugeColdEuler, _motion.GaugeHotEuler, gaugeProgress)));
            SetRelativeRotation(_stackDamper, _stackDamperRest, Quaternion.Euler(burning ? _motion.StackDamperOpenEuler : Vector3.zero));
            SetRelativeRotation(_ashDoor, _ashDoorRest, Quaternion.Euler(finished ? _motion.AshDoorCompleteEuler : Vector3.zero));
            _effects?.SetBurning(burning);
            _audio?.SetDoorOpen(doorOpen);
            _audio?.SetState(state);

            UpdateControlLights(state, doorOpen, stagedValue, requiredValue);
            ConfigureTriggers(state, doorOpen, stagedValue, requiredValue, repairHoldSeconds);
        }

        internal void Tick(
            WasteDisposalIncineratorController.IncineratorState state,
            bool doorOpen,
            int stagedValue,
            int requiredValue,
            float targetPercent,
            float cycleDuration,
            float remainingSeconds,
            float repairHoldSeconds)
        {
            TickFeedDoor();
            bool burning = state == WasteDisposalIncineratorController.IncineratorState.Burning;
            TickVibration(burning);
            if (burning && _fanRotor != null)
                _fanRotor.Rotate(0f, 0f, 420f * Time.deltaTime, Space.Self);
            _effects?.Tick();
            UpdateControlLights(state, doorOpen, stagedValue, requiredValue);
            _startButton.Tick();
            _stopButton.Tick();
            ConfigureTriggers(state, doorOpen, stagedValue, requiredValue, repairHoldSeconds);
            ConfigureLocalPlaceTrigger(state, doorOpen);
            UpdateScreen(state, doorOpen, stagedValue, requiredValue, targetPercent, cycleDuration, remainingSeconds);
        }

        internal void PulseStartButton()
        {
            _startButton.Pulse();
            _audio?.PlayStartButton();
        }

        internal void PulseStopButton()
        {
            _stopButton.Pulse();
            _audio?.PlayStopButton();
        }

        internal void PlayItemPlaced(int index)
        {
            _audio?.PlayItemPlaced(index);
        }

        internal void PlayEmberBurst(int index, float intensity)
        {
            _audio?.PlayEmber(index);
            _effects?.Burst(intensity);
        }

        internal void Dispose()
        {
            ResetVibration();
            _screen?.Dispose();
            _effects?.Dispose();
            _startButton.Dispose();
            _stopButton.Dispose();
        }

        private void ConfigureTriggers(
            WasteDisposalIncineratorController.IncineratorState state,
            bool doorOpen,
            int stagedValue,
            int requiredValue,
            float repairHoldSeconds)
        {
            bool loading = state == WasteDisposalIncineratorController.IncineratorState.Loading;
            bool burning = state == WasteDisposalIncineratorController.IncineratorState.Burning;
            bool paused = state == WasteDisposalIncineratorController.IncineratorState.Paused;
            bool malfunctioned = state == WasteDisposalIncineratorController.IncineratorState.Malfunctioned;
            bool doorVisuallyClosed = !_feedDoorTargetOpen && _feedDoorProgress <= 0.01f;
            bool ready = loading && !doorOpen && doorVisuallyClosed && requiredValue > 0 && stagedValue >= requiredValue;

            // #716 B9: this ran from Tick every frame and interpolated a fresh load-progress
            // string each time. Nothing here depends on time, so it only needs to re-run when
            // one of its inputs actually moves.
            if (_tipsInitialized
                && state == _tipState
                && doorOpen == _tipDoorOpen
                && doorVisuallyClosed == _tipDoorVisuallyClosed
                && stagedValue == _tipStagedValue
                && requiredValue == _tipRequiredValue
                && repairHoldSeconds.Equals(_tipRepairHoldSeconds))
                return;

            _tipsInitialized = true;
            _tipState = state;
            _tipDoorOpen = doorOpen;
            _tipDoorVisuallyClosed = doorVisuallyClosed;
            _tipStagedValue = stagedValue;
            _tipRequiredValue = requiredValue;
            _tipRepairHoldSeconds = repairHoldSeconds;

            if (StartTrigger != null)
            {
                StartTrigger.interactable = ready || paused;
                StartTrigger.hoverTip = paused ? "Resume incinerator" : "Start incinerator";
                StartTrigger.disabledHoverTip = malfunctioned ? "[ Repair incinerator first ]"
                    : !loading ? "[ Incinerator unavailable ]"
                    : doorOpen ? "[ Close door before starting ]"
                    : !doorVisuallyClosed ? "[ Feed door closing ]"
                    : stagedValue < requiredValue ? $"[ Load more scrap (${stagedValue}/${requiredValue}) ]"
                    : "[ Incinerator unavailable ]";
            }
            if (StopTrigger != null)
            {
                StopTrigger.interactable = burning;
                StopTrigger.hoverTip = "Pause incinerator";
                StopTrigger.disabledHoverTip = malfunctioned ? "[ Repair incinerator first ]"
                    : paused ? "[ Incinerator paused ]" : "[ Stop unavailable ]";
            }
            if (DoorTrigger != null)
            {
                DoorTrigger.interactable = loading;
                DoorTrigger.hoverTip = doorOpen ? "Close feed door" : "Open feed door";
                DoorTrigger.disabledHoverTip = "[ Feed door locked ]";
            }
            if (RepairTrigger != null)
            {
                RepairTrigger.gameObject.SetActive(malfunctioned);
                RepairTrigger.interactable = malfunctioned;
                RepairTrigger.holdInteraction = true;
                RepairTrigger.timeToHold = Mathf.Max(0.5f, repairHoldSeconds);
                RepairTrigger.timeToHoldSpeedMultiplier = 1f;
                // #716 E7: [LMB] is vanilla's interact-key token; PlayerControllerB rewrites it
                // to the player's bound key, so a hard-coded [E] lies on a rebound control.
                RepairTrigger.hoverTip = "Repair incinerator : Hold [LMB]";
                RepairTrigger.holdTip = "Repairing incinerator";
                RepairTrigger.disabledHoverTip = "[ Incinerator operational ]";
            }
            if (PlaceTrigger != null && (!loading || !doorOpen))
            {
                PlaceTrigger.interactable = false;
                PlaceTrigger.disabledHoverTip = loading ? "[ Open feed door to place scrap ]" : "[ Chamber locked ]";
            }
        }

        private void UpdateControlLights(
            WasteDisposalIncineratorController.IncineratorState state,
            bool doorOpen,
            int stagedValue,
            int requiredValue)
        {
            bool loading = state == WasteDisposalIncineratorController.IncineratorState.Loading;
            bool paused = state == WasteDisposalIncineratorController.IncineratorState.Paused;
            bool burning = state == WasteDisposalIncineratorController.IncineratorState.Burning;
            bool doorVisuallyClosed = !_feedDoorTargetOpen && _feedDoorProgress <= 0.01f;
            bool ready = loading && !doorOpen && doorVisuallyClosed
                         && requiredValue > 0 && stagedValue >= requiredValue;
            _startButton.SetLit(ready || paused);
            _stopButton.SetLit(burning);
        }

        private void ConfigureLocalPlaceTrigger(WasteDisposalIncineratorController.IncineratorState state, bool doorOpen)
        {
            if (PlaceTrigger == null)
                return;
            bool visuallyOpen = _feedDoorTargetOpen && _feedDoorProgress >= 0.99f;
            if (state != WasteDisposalIncineratorController.IncineratorState.Loading || !doorOpen || !visuallyOpen)
            {
                PlaceTrigger.interactable = false;
                PlaceTrigger.disabledHoverTip = state != WasteDisposalIncineratorController.IncineratorState.Loading
                    ? "[ Chamber locked ]"
                    : doorOpen ? "[ Wait for feed door ]" : "[ Open feed door to place scrap ]";
                return;
            }
            PlayerControllerB player = GameNetworkManager.Instance?.localPlayerController;
            GrabbableObject held = player != null ? player.currentlyHeldObjectServer : null;
            bool eligible = WasteDisposalIncineratorController.IsEligibleHeldScrap(held);
            PlaceTrigger.interactable = eligible;
            PlaceTrigger.hoverTip = "Place item";
            PlaceTrigger.disabledHoverTip = "[ Hold scrap to place ]";
        }

        private void TickVibration(bool burning)
        {
            if (_visualRoot == null)
                return;
            if (!burning)
            {
                ResetVibration();
                return;
            }

            float phase = Time.time * 35f;
            float x = Mathf.Sin(phase);
            float y = Mathf.Sin(phase * 1.37f + 1.1f);
            float z = Mathf.Cos(phase * 0.83f + 0.4f);
            _visualRoot.localPosition = _visualRootRestPosition
                                        + new Vector3(x, y * 0.65f, z * 0.8f) * VibrationPositionAmplitude;
            _visualRoot.localRotation = _visualRootRestRotation * Quaternion.Euler(
                y * VibrationRotationAmplitude * 0.45f,
                x * VibrationRotationAmplitude * 0.25f,
                z * VibrationRotationAmplitude);
        }

        private void ResetVibration()
        {
            if (_visualRoot == null)
                return;
            _visualRoot.localPosition = _visualRootRestPosition;
            _visualRoot.localRotation = _visualRootRestRotation;
        }

        private void SetFeedDoorTarget(bool open)
        {
            _feedDoorTargetOpen = open;
            if (_feedDoorInitialized)
                return;
            _feedDoorInitialized = true;
            _feedDoorProgress = open ? 1f : 0f;
            ApplyFeedDoorPose();
        }

        private void TickFeedDoor()
        {
            if (!_feedDoorInitialized || FeedDoor == null)
                return;
            float target = _feedDoorTargetOpen ? 1f : 0f;
            _feedDoorProgress = Mathf.MoveTowards(
                _feedDoorProgress,
                target,
                Time.deltaTime / _feedDoorTravelSeconds);
            ApplyFeedDoorPose();
        }

        private void ApplyFeedDoorPose()
        {
            float eased = Mathf.SmoothStep(0f, 1f, _feedDoorProgress);
            Quaternion offset = Quaternion.SlerpUnclamped(
                Quaternion.identity,
                Quaternion.Euler(_motion.FeedDoorOpenEuler),
                eased);
            SetRelativeRotation(FeedDoor, _feedDoorRest, offset);
        }

        private void UpdateScreen(
            WasteDisposalIncineratorController.IncineratorState state,
            bool doorOpen,
            int stagedValue,
            int requiredValue,
            float targetPercent,
            float cycleDuration,
            float remainingSeconds)
        {
            if (_screen == null)
                return;

            // #716 B9: a semantic move (state, door, staged/required scrap) redraws at once;
            // otherwise the clock and bar refresh on a fixed cadence instead of every frame,
            // which also bounds the display's manual Camera.Render calls.
            bool semanticChange = !_screenStateInitialized
                                  || state != _screenState
                                  || doorOpen != _screenDoorOpen
                                  || stagedValue != _screenStagedValue
                                  || requiredValue != _screenRequiredValue;
            if (!semanticChange && Time.unscaledTime < _nextScreenUpdateTime)
                return;
            _screenStateInitialized = true;
            _screenState = state;
            _screenDoorOpen = doorOpen;
            _screenStagedValue = stagedValue;
            _screenRequiredValue = requiredValue;
            _nextScreenUpdateTime = Time.unscaledTime + ScreenUpdateIntervalSeconds;

            string status;
            string detail;
            float progress;
            switch (state)
            {
                case WasteDisposalIncineratorController.IncineratorState.Burning:
                    status = "BURNING";
                    detail = $"LOCKED  |  {FormatTime(remainingSeconds)}";
                    progress = cycleDuration > 0.01f ? 1f - remainingSeconds / cycleDuration : 0f;
                    break;
                case WasteDisposalIncineratorController.IncineratorState.Paused:
                    status = "PAUSED";
                    detail = $"LOCKED  |  {FormatTime(remainingSeconds)}  |  START TO RESUME";
                    progress = cycleDuration > 0.01f ? 1f - remainingSeconds / cycleDuration : 0f;
                    break;
                case WasteDisposalIncineratorController.IncineratorState.Malfunctioned:
                    status = "MALFUNCTION";
                    // #716 E12: this is drawn into the machine's own screen texture, not a
                    // vanilla tip field, so no [LMB] key substitution runs on it. Keep it
                    // key-neutral rather than naming a key the player may have rebound.
                    detail = $"HOLD INTERACT TO REPAIR  |  {FormatTime(remainingSeconds)}";
                    progress = cycleDuration > 0.01f ? 1f - remainingSeconds / cycleDuration : 0f;
                    break;
                case WasteDisposalIncineratorController.IncineratorState.Complete:
                    status = "COMPLETE";
                    detail = "CYCLE COMPLETE  |  DOOR LOCKED";
                    progress = 1f;
                    break;
                default:
                    bool ready = !doorOpen && requiredValue > 0 && stagedValue >= requiredValue;
                    status = ready ? "READY" : doorOpen ? "LOAD SCRAP" : "OPEN / LOAD MORE";
                    detail = $"TARGET {targetPercent:0.#}%  |  DOOR {(doorOpen ? "OPEN" : "CLOSED")}  |  EST {FormatTime(cycleDuration)}";
                    progress = requiredValue > 0 ? stagedValue / (float)requiredValue : 0f;
                    break;
            }
            _screen.Tick(status, $"${Mathf.Max(0, stagedValue)} / ${Mathf.Max(0, requiredValue)}", detail, Mathf.Clamp01(progress));
        }

        private static string FormatTime(float seconds)
        {
            int total = Mathf.Max(0, Mathf.CeilToInt(seconds));
            return $"{total / 60:00}:{total % 60:00}";
        }

        private void OnStartInteract(PlayerControllerB player) => StartRequested?.Invoke();
        private void OnStopInteract(PlayerControllerB player) => StopRequested?.Invoke();
        private void OnDoorInteract(PlayerControllerB player) => DoorRequested?.Invoke();
        private void OnRepairInteract(PlayerControllerB player) => RepairRequested?.Invoke();
        private void OnPlaceInteract(PlayerControllerB player) => PlaceRequested?.Invoke(player);

        private static Quaternion RestRotation(Transform value) => value != null ? value.localRotation : Quaternion.identity;

        private static void SetRelativeRotation(Transform target, Quaternion rest, Quaternion offset)
        {
            if (target != null) target.localRotation = rest * offset;
        }

        private sealed class ButtonVisual
        {
            private readonly Transform _transform;
            private readonly Vector3 _restPosition;
            private readonly Vector3 _pressedOffset;
            private static readonly int EmissiveColorId = Shader.PropertyToID("_EmissiveColor");
            private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
            private static readonly int EmissiveColorLdrId = Shader.PropertyToID("_EmissiveColorLDR");
            private static readonly int EmissiveIntensityId = Shader.PropertyToID("_EmissiveIntensity");

            private readonly Color _color;
            private Material _material;
            private bool _hasEmissiveColor;
            private bool _hasEmissionColor;
            private bool _hasEmissiveColorLdr;
            private bool _hasEmissiveIntensity;
            private float _pressedUntil;
            private bool _lit;

            internal ButtonVisual(Transform transform, Color color, Vector3 pressedOffset)
            {
                _transform = transform;
                _restPosition = transform != null ? transform.localPosition : Vector3.zero;
                _pressedOffset = pressedOffset;
                _color = color;
                Renderer renderer = transform != null ? transform.GetComponent<Renderer>() ?? transform.GetComponentInChildren<Renderer>(true) : null;
                if (renderer == null) return;
                // #716 A4: reading renderer.materials instantiates a private copy of every slot.
                // Only slot 0 is replaced, so read the shared array and let the single assignment
                // below do the instancing.
                Material[] shared;
                try { shared = renderer.sharedMaterials; } catch { return; }
                if (shared.Length == 0 || shared[0] == null) return;
                _material = new Material(shared[0]) { name = shared[0].name + "_LGURuntime" };
                var materials = new Material[shared.Length];
                Array.Copy(shared, materials, shared.Length);
                materials[0] = _material;
                try { renderer.materials = materials; } catch { }
                _material.EnableKeyword("_EMISSION");
                _hasEmissiveColor = _material.HasProperty(EmissiveColorId);
                _hasEmissionColor = _material.HasProperty(EmissionColorId);
                _hasEmissiveColorLdr = _material.HasProperty(EmissiveColorLdrId);
                _hasEmissiveIntensity = _material.HasProperty(EmissiveIntensityId);
                ApplyEmission();
            }

            internal void SetLit(bool lit)
            {
                if (_lit == lit) return;
                _lit = lit;
                ApplyEmission();
            }

            internal void Pulse()
            {
                _pressedUntil = Time.time + 0.13f;
            }

            internal void Tick()
            {
                if (_transform == null) return;
                Vector3 target = _restPosition + (Time.time < _pressedUntil ? _pressedOffset : Vector3.zero);
                _transform.localPosition = Vector3.Lerp(_transform.localPosition, target, Mathf.Clamp01(Time.deltaTime * 28f));
            }

            internal void Dispose()
            {
                if (_material != null) UnityEngine.Object.Destroy(_material);
            }

            private void ApplyEmission()
            {
                if (_material == null) return;
                Color emission = _color * (_lit ? 2.8f : 0.18f);
                if (_hasEmissiveColor) _material.SetColor(EmissiveColorId, emission);
                if (_hasEmissionColor) _material.SetColor(EmissionColorId, emission);
                if (_hasEmissiveColorLdr) _material.SetColor(EmissiveColorLdrId, _color);
                if (_hasEmissiveIntensity) _material.SetFloat(EmissiveIntensityId, _lit ? 2.8f : 0.18f);
            }
        }
    }
}
