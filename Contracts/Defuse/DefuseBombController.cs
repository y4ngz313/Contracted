using System;
using System.Collections;
using System.IO;
using BepInEx;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Networking;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Core.Compat;
using Y4NGZCompany.Facility.Stash;
using Y4NGZCompany.Contracts._Shared;
namespace Y4NGZCompany.Contracts.Defuse
{
    public class DefuseBombController : NetworkBehaviour, ICompanyStashKeypadClient
    {
        private const int StateArmed = 0;
        private const int StateWireSolved = 1;
        private const int StateDefused = 2;
        private const int StateFailed = 3;
        private const string BombBeepFileName = "mr_berrydev-single-keypad-beep-433456.mp3";
        private const float KeypadStatusHoldSeconds = 1.15f;

        // #218: the beep is the contract's "where is it / how long left" cue, so it has to
        // carry like a monster does rather than dying at the doorway. 75 m sits between
        // Bundy's voice (40 m) and his saw (60-85 m); the carry curve holds a low tail over
        // the whole band and the air-absorption low-pass keeps distance reading spectrally.
        private const float BeepMaxDistanceMeters = 75f;

        private readonly NetworkVariable<int> _archetypeIndex = new NetworkVariable<int>(
            -1,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<int> _code = new NetworkVariable<int>(
            -1,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<int> _cutMask = new NetworkVariable<int>(
            0,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<int> _state = new NetworkVariable<int>(
            StateArmed,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<bool> _diagnosticIdentified = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        internal bool DiagnosticIdentified => _diagnosticIdentified.Value;

        // A diagnostic is authored at the ship terminal, not next to the bomb. The server
        // checks the submitted profile against this bomb before sharing the HUD milestone.
        [ServerRpc(RequireOwnership = false)]
        public void IdentifyDiagnosticServerRpc(int archetypeIndex, ServerRpcParams rpcParams = default)
        {
            if (!IsServer || !IsLive || archetypeIndex != _archetypeIndex.Value) return;
            PlayerControllerB player = FindPlayer(rpcParams.Receive.SenderClientId);
            if (player == null || player.isPlayerDead || !player.isPlayerControlled) return;
            _diagnosticIdentified.Value = true;
        }

        private int _lastVisualArchetype = int.MinValue;
        private int _lastVisualCutMask = int.MinValue;
        private int _lastVisualState = int.MinValue;
        private int _localKeypadValue;
        private int _localKeypadDigitCount;
        private Transform[] _visualTransforms;
        private DefuseInteractor _interactor;
        private CompanyStashKeypadInteractor _keypad;

        // #716 (WP-B8): set once EnsureInteractor has wired both, so Update stops redoing it.
        private bool _wiredInteractor;
        private AudioSource _audioSource;
        private ContractAudioPropagation _audioPropagation;
        private AudioClip _beepClip;
        private AudioClip _fallbackBeepClip;
        private bool _beepLoadStarted;
        private bool _beepLoadFailed;
        private float _nextBeepAt = -1f;
        private float _keypadStatusUntil;
        private bool _explosionCleanupStarted;

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int UnlitColorId = Shader.PropertyToID("_UnlitColor");

        public int ArchetypeIndex => _archetypeIndex.Value;
        public int AssignedCode => _code.Value;
        public int CutMask => _cutMask.Value;
        public bool WirePhaseComplete => _state.Value >= StateWireSolved && _state.Value != StateFailed;
        public bool IsDefused => _state.Value == StateDefused;
        public bool IsFailed => _state.Value == StateFailed;
        public bool IsLive => _state.Value == StateArmed || _state.Value == StateWireSolved;
        internal bool HasLocalKeypadInput => _localKeypadDigitCount > 0;

        private DefuseArchetype Archetype => DefuseRules.GetArchetype(_archetypeIndex.Value);

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            DefuseContract.RegisterBomb(this);
            _archetypeIndex.OnValueChanged += HandleNetworkVisualChanged;
            _cutMask.OnValueChanged += HandleNetworkVisualChanged;
            _state.OnValueChanged += HandleNetworkVisualChanged;
            RefreshVisuals(force: true);
        }

        public override void OnNetworkDespawn()
        {
            _archetypeIndex.OnValueChanged -= HandleNetworkVisualChanged;
            _cutMask.OnValueChanged -= HandleNetworkVisualChanged;
            _state.OnValueChanged -= HandleNetworkVisualChanged;
            DefuseContract.UnregisterBomb(this);
            base.OnNetworkDespawn();
        }

        private void Update()
        {
            EnsureInteractor();
            UpdateKeypadStatusTimeout();
            UpdateTimerAndBeep();

            // Built up front rather than on the first beep, so the occlusion/air-absorption
            // state is already settled by the time a beep actually plays.
            EnsureBombAudio();
            _audioPropagation?.Tick();
        }

        internal void ConfigureServer(DefuseProfile profile)
        {
            if (!IsServer)
                return;

            _archetypeIndex.Value = Mathf.Clamp(profile.ArchetypeIndex, 0, DefuseRules.Archetypes.Length - 1);
            _code.Value = Mathf.Clamp(profile.Code, 0, 9999);
            _cutMask.Value = 0;
            _state.Value = StateArmed;
            _diagnosticIdentified.Value = false;
            _nextBeepAt = -1f;
            RefreshVisuals(force: true);
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Defuse] Bomb configured as {DefuseRules.FormatArchetypeBrief(Archetype)} code={_code.Value:D4}.");
        }

        internal void ConfigurePlacementPreview(int archetypeIndex)
        {
            DefuseArchetype archetype = DefuseRules.GetArchetype(archetypeIndex);
            ConfigureMarking(archetype);
            ConfigureDisplay(archetype, StateArmed);
            ConfigureWires(archetype, 0);
        }

        public bool TrySubmitCodeLocally(int code, out string statusText)
        {
            if (IsFailed)
            {
                statusText = "DEVICE LOST";
                return false;
            }

            if (IsDefused)
            {
                statusText = "ALREADY SAFE";
                return true;
            }

            if (!WirePhaseComplete)
            {
                statusText = "CUT WIRES FIRST";
                return false;
            }

            if (_code.Value == code)
            {
                statusText = "DEFUSED";
                return true;
            }

            statusText = "DENIED";
            return false;
        }

        [ServerRpc(RequireOwnership = false)]
        public void SubmitCodeServerRpc(int code, ServerRpcParams rpcParams = default)
        {
            if (!IsServer || IsFailed || IsDefused || !WirePhaseComplete)
                return;

            if (!IsPlayerCloseEnough(rpcParams.Receive.SenderClientId))
            {
                ContractsBootstrap.Log?.LogDebug("[MoonContracts.Defuse] Rejected keypad submit from distant sender.");
                return;
            }

            PlayerControllerB player = FindPlayer(rpcParams.Receive.SenderClientId);
            bool acceptedCode = _code.Value == code;
            MoonContractRoundStats.RecordDefuseCodeSubmitted(player, acceptedCode);
            if (!acceptedCode)
            {
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Defuse] Incorrect keypad code submitted ({code:D4}).");
                return;
            }

            PublishCorrectCodeClientRpc(rpcParams.Receive.SenderClientId, NetworkObjectId);

            _state.Value = StateDefused;
            DefuseContract.NotifyDefused(this);
            NotifyStatusClientRpc("DEFUSE COMPLETE");
        }

        [ServerRpc(RequireOwnership = false)]
        public void RequestCutWireServerRpc(int wireSlot, ServerRpcParams rpcParams = default)
        {
            if (!IsServer || _state.Value != StateArmed)
                return;

            if (!IsPlayerCloseEnough(rpcParams.Receive.SenderClientId))
            {
                ContractsBootstrap.Log?.LogDebug("[MoonContracts.Defuse] Rejected wire cut from distant sender.");
                return;
            }

            DefuseArchetype archetype = Archetype;
            PlayerControllerB player = FindPlayer(rpcParams.Receive.SenderClientId);
            bool acceptedWire = DefuseRules.IsWireCutAccepted(archetype, _cutMask.Value, wireSlot, out bool complete, out string reason);
            MoonContractRoundStats.RecordDefuseWireCut(player, acceptedWire, wireSlot);
            if (!acceptedWire)
            {
                FailServer($"wrong wire slot {wireSlot + 1}: {reason}");
                return;
            }

            PublishCorrectWireClientRpc(rpcParams.Receive.SenderClientId, NetworkObjectId, wireSlot);

            _cutMask.Value |= 1 << wireSlot;
            if (complete)
            {
                _state.Value = StateWireSolved;
                NotifyStatusClientRpc("WIRE SEQUENCE ACCEPTED");
            }
            else
            {
                NotifyStatusClientRpc($"WIRE {wireSlot + 1} CUT");
            }
        }

        [ClientRpc]
        private void PublishCorrectWireClientRpc(ulong playerClientId, ulong bombNetworkId, int wireSlot)
        {
            ContractProgressionEvents.PublishDefuseCorrectWire(playerClientId, bombNetworkId, wireSlot);
        }

        [ClientRpc]
        private void PublishCorrectCodeClientRpc(ulong playerClientId, ulong bombNetworkId)
        {
            ContractProgressionEvents.PublishDefuseCorrectCode(playerClientId, bombNetworkId);
        }

        internal bool IsWireSlotInteractable(int wireSlot)
        {
            if (_state.Value != StateArmed || IsFailed || IsDefused)
                return false;

            DefuseArchetype archetype = Archetype;
            if (archetype == null || wireSlot < 0 || wireSlot >= archetype.WireCount)
                return false;

            return (_cutMask.Value & (1 << wireSlot)) == 0;
        }

        // #716 (WP-E7): hover tips use vanilla's [LMB] token, not a literal [E]. These strings
        // reach the player through PlayerControllerB's cursorTip, which rewrites [LMB] to the
        // bound key ([E] on keyboard, [X] on a controller); a hard-coded [E] is only correct for
        // the default binding. This applies to hoverTip/disabledHoverTip only - HUDManager's
        // control-tip lines never substitute [LMB] on keyboard, so those keep [E].
        internal string GetWireInteractionTip(int wireSlot)
        {
            DefuseArchetype archetype = Archetype;
            if (archetype == null || wireSlot < 0 || wireSlot >= archetype.WireCount)
                return "Cut wire : [LMB]";

            DefuseWireColor color = archetype.Wires[wireSlot];
            return $"Cut {DefuseRules.GetWireName(color)} wire : [LMB]";
        }

        internal string GetWireDisabledTip(int wireSlot)
        {
            if (IsFailed)
                return "[ Device failed ]";
            if (IsDefused)
                return "[ Device safe ]";
            if (WirePhaseComplete)
                return "[ Wires accepted ]";

            DefuseArchetype archetype = Archetype;
            if (archetype == null || wireSlot < 0 || wireSlot >= archetype.WireCount)
                return "[ Wire absent ]";

            return (_cutMask.Value & (1 << wireSlot)) != 0 ? "[ Wire cut ]" : "[ Wire unavailable ]";
        }

        // ICompanyStashKeypadClient (#393). The keypad hardware moved to Y4NGZCore with the
        // Company Stash and no longer names either owner; these forward to the members the
        // bomb already had, so behaviour is unchanged. Explicit implementations keep them
        // internal.
        bool ICompanyStashKeypadClient.IsKeypadInteractable() => IsKeypadButtonInteractable();

        bool ICompanyStashKeypadClient.HasLocalKeypadInput => HasLocalKeypadInput;

        string ICompanyStashKeypadClient.GetKeypadButtonTip(int button) => GetKeypadButtonTip(button);

        string ICompanyStashKeypadClient.GetKeypadDisabledTip() => GetKeypadDisabledTip();

        void ICompanyStashKeypadClient.PressKeypadButtonLocally(int button, PlayerControllerB player) =>
            PressSharedKeypadButtonLocally(button, player);

        string ICompanyStashKeypadClient.GetKeypadLockedDisplayText() => GetKeypadLockedDisplayText();

        CompanyStashKeypadDisplayState ICompanyStashKeypadClient.GetKeypadLockedDisplayState() =>
            GetKeypadLockedDisplayState();

        // The bomb authors its own display and its own keypad meshes. A synthesized TMP
        // label or a floating cube grid on a defusable bomb reads as a bug, so both
        // fallbacks are declined and a missing mesh set is reported once instead.
        bool ICompanyStashKeypadClient.AllowsFallbackDisplayText => false;

        bool ICompanyStashKeypadClient.AllowsFallbackButtonVisuals => false;

        string ICompanyStashKeypadClient.MissingButtonVisualsWarning =>
            "[MoonContracts.Defuse] Defuse bomb has no physical keypad button transforms; skipping fallback cube keypad visuals.";

        internal bool IsKeypadButtonInteractable()
        {
            return _state.Value == StateWireSolved && !IsFailed && !IsDefused;
        }

        internal string GetKeypadButtonTip(int button)
        {
            if (button == CompanyStashKeypadInteractor.EnterButton || button == DefuseInteractor.KeypadEnterButton)
                return "Press [Enter]";

            return $"Press [{Mathf.Clamp(button, 0, 9)}]";
        }

        internal string GetKeypadDisabledTip()
        {
            if (IsFailed)
                return "[ Device failed ]";
            if (IsDefused)
                return "[ Device safe ]";
            if (!WirePhaseComplete)
                return "[ Cut wires first ]";
            return "[ Keypad locked ]";
        }

        internal string GetKeypadLockedDisplayText()
        {
            if (IsFailed)
                return "ERROR";
            if (IsDefused)
                return "SAFE";
            if (!WirePhaseComplete)
                return "LOCKED";

            return "LOCKED";
        }

        internal CompanyStashKeypadDisplayState GetKeypadLockedDisplayState()
        {
            if (IsDefused)
                return CompanyStashKeypadDisplayState.Granted;

            return CompanyStashKeypadDisplayState.Denied;
        }

        internal void PressSharedKeypadButtonLocally(int button, PlayerControllerB player)
        {
            PressKeypadButtonLocally(button);
        }

        internal void PressKeypadButtonLocally(int button)
        {
            if (IsFailed)
            {
                ResetLocalKeypadEntry();
                ShowKeypadStatus("ERROR", DefuseKeypadDisplayState.Failed, KeypadStatusHoldSeconds);
                return;
            }

            if (IsDefused)
            {
                ResetLocalKeypadEntry();
                ShowKeypadStatus("SAFE", DefuseKeypadDisplayState.Success, KeypadStatusHoldSeconds);
                return;
            }

            if (!WirePhaseComplete)
            {
                ResetLocalKeypadEntry();
                ShowKeypadStatus("WIRES", DefuseKeypadDisplayState.Warning, KeypadStatusHoldSeconds);
                return;
            }

            if (button == DefuseInteractor.KeypadClearButton)
            {
                ResetLocalKeypadEntry();
                ShowKeypadStatus(FormatLocalKeypadEntry(), DefuseKeypadDisplayState.Normal, 0f);
                return;
            }

            if (button == CompanyStashKeypadInteractor.EnterButton || button == DefuseInteractor.KeypadEnterButton)
            {
                SubmitLocalKeypadEntry();
                return;
            }

            int digit = Mathf.Clamp(button, 0, 9);
            if (_localKeypadDigitCount >= DefuseRules.CodeDigits)
                ResetLocalKeypadEntry();

            _localKeypadValue = _localKeypadValue * 10 + digit;
            _localKeypadDigitCount++;

            if (_localKeypadDigitCount >= DefuseRules.CodeDigits)
            {
                SubmitLocalKeypadEntry();
                return;
            }

            ShowKeypadStatus(FormatLocalKeypadEntry(), DefuseKeypadDisplayState.Normal, 0f);
        }

        internal string GetLocalInteractionHint()
        {
            if (IsFailed)
                return "Device failed";
            if (IsDefused)
                return "Device safe";
            if (!WirePhaseComplete)
                return "Hold [E] on a wire";

            return "Press the keypad buttons";
        }

        private void SubmitLocalKeypadEntry()
        {
            if (_localKeypadDigitCount < DefuseRules.CodeDigits)
            {
                ShowKeypadStatus($"NEED {DefuseRules.CodeDigits - _localKeypadDigitCount}", DefuseKeypadDisplayState.Warning, KeypadStatusHoldSeconds);
                return;
            }

            int code = _localKeypadValue;
            bool accepted = TrySubmitCodeLocally(code, out string statusText);
            SubmitCodeServerRpc(code);
            ShowKeypadStatus(string.IsNullOrWhiteSpace(statusText) ? (accepted ? "DEFUSED" : "DENIED") : statusText,
                accepted ? DefuseKeypadDisplayState.Success : DefuseKeypadDisplayState.Failed,
                KeypadStatusHoldSeconds);
            ResetLocalKeypadEntry();
        }

        private void ResetLocalKeypadEntry()
        {
            _localKeypadValue = 0;
            _localKeypadDigitCount = 0;
        }

        private string FormatLocalKeypadEntry()
        {
            string digits = _localKeypadValue.ToString("D" + Mathf.Max(1, _localKeypadDigitCount));
            if (digits.Length > _localKeypadDigitCount)
                digits = digits.Substring(digits.Length - _localKeypadDigitCount);

            char[] slots = { '_', '_', '_', '_' };
            int count = Mathf.Min(_localKeypadDigitCount, slots.Length);
            for (int i = 0; i < count; i++)
                slots[i] = digits[i];

            return $"{slots[0]} {slots[1]} {slots[2]} {slots[3]}";
        }

        private void ShowKeypadStatus(string message, DefuseKeypadDisplayState displayState, float holdSeconds)
        {
            EnsureInteractor();
            _keypad?.SetDisplayText(message ?? string.Empty, MapKeypadDisplayState(displayState));
            _keypadStatusUntil = holdSeconds > 0f ? Time.time + holdSeconds : 0f;
        }

        private void UpdateKeypadStatusTimeout()
        {
            if (_keypadStatusUntil <= 0f || Time.time < _keypadStatusUntil)
                return;

            _keypadStatusUntil = 0f;
            ApplyGlobalKeypadDisplay();
        }

        /// <summary>
        /// Resolves and wires the interactor and the shared keypad. #716 (WP-B8): this ran in
        /// full every frame from <see cref="Update"/>, which re-resolved the risk config for
        /// <c>InteractRadius</c> and then called <c>EnsureBuilt</c> plus <c>RefreshButtons</c> on
        /// the keypad — the latter rebuilding eleven <c>"Press [n]"</c> hover tips per frame on
        /// top of the keypad's own 0.2s refresh (<c>CompanyStashKeypadInteractor.Update</c> calls
        /// <c>RefreshButtons</c> on a <c>RefreshInterval</c> of 0.2s, and its <c>EnsureBuilt</c>
        /// latches on <c>_built</c>, so both calls here were pure duplication). The work is now
        /// latched and re-runs only if a reference is lost.
        /// </summary>
        private void EnsureInteractor()
        {
            if (_wiredInteractor && _interactor != null && _keypad != null)
                return;

            if (_interactor == null)
                _interactor = GetComponent<DefuseInteractor>() ?? GetComponentInChildren<DefuseInteractor>(true);

            if (_interactor == null)
                return;

            _interactor.Bomb = this;
            _interactor.InteractRadius = ContractsBootstrap.DefuseBalance?.GetEffective().InteractDistanceMeters
                                         ?? _interactor.InteractRadius;
            _interactor.EnsureBuilt();

            if (_keypad == null)
                _keypad = GetComponentInChildren<CompanyStashKeypadInteractor>(true);
            if (_keypad == null)
            {
                Transform keypadHost = FindSharedKeypadHost() ?? transform;
                _keypad = keypadHost.gameObject.AddComponent<CompanyStashKeypadInteractor>();
            }

            // The keypad resolves its owner from the parent chain since #393 (see
            // ICompanyStashKeypadClient); nothing to assign. It builds itself in Awake/Start and
            // refreshes its own buttons every 0.2s, so one EnsureBuilt here covers the case where
            // this method added the component a moment ago.
            _keypad.EnsureBuilt();
            _wiredInteractor = true;
        }

        private void ApplyGlobalKeypadDisplay()
        {
            if (_keypadStatusUntil > Time.time)
                return;

            EnsureInteractor();
            if (_interactor == null)
                return;

            switch (_state.Value)
            {
                case StateFailed:
                    _keypad?.SetDisplayText("ERROR", CompanyStashKeypadDisplayState.Denied);
                    return;
                case StateDefused:
                    _keypad?.SetDisplayText("SAFE", CompanyStashKeypadDisplayState.Granted);
                    return;
                case StateWireSolved:
                    _keypad?.SetDisplayText(FormatLocalKeypadEntry(), CompanyStashKeypadDisplayState.Normal);
                    return;
                default:
                    DefuseArchetype archetype = Archetype;
                    string display = archetype != null ? DefuseRules.GetDisplayName(archetype.DisplayState) : "ARMED";
                    _keypad?.SetDisplayText(display, CompanyStashKeypadDisplayState.Normal);
                    return;
            }
        }

        private void UpdateTimerAndBeep()
        {
            if (!IsLive)
            {
                _nextBeepAt = -1f;
                return;
            }

            int remainingSeconds = DefuseContract.GetDeadlineRemainingSeconds();
            if (IsServer && DefuseContract.IsDeadlineExpired())
            {
                FailServer("deadline expired");
                return;
            }

            float interval = GetBeepInterval(remainingSeconds);
            if (_nextBeepAt < 0f)
            {
                _nextBeepAt = Time.time + interval;
                return;
            }

            if (Time.time < _nextBeepAt)
                return;

            PlayBombBeep(remainingSeconds);
            _nextBeepAt = Time.time + interval;
        }

        private void EnsureBombAudio()
        {
            if (_audioSource == null)
            {
                _audioSource = GetComponentInChildren<AudioSource>(true);
                if (_audioSource == null)
                    _audioSource = gameObject.AddComponent<AudioSource>();
                _audioSource.playOnAwake = false;
                _audioSource.volume = 0.95f;

                // #218: this source used to be a bare AudioSource on Linear rolloff over
                // 34 m with no mixer group — so no occlusion low-pass, no room reverb, no
                // volume slider, and silence one room away. It now uses the same diagetic
                // routing + carry curve + air-absorption model the monsters use.
                _audioPropagation = ContractSpatialAudio.ApplyFacilityCarryProfile(
                    _audioSource,
                    BeepMaxDistanceMeters,
                    minDistance: 2.5f,
                    spread: 20f,
                    priority: 115);
            }

            if (!_beepLoadStarted && !_beepLoadFailed && _beepClip == null)
                StartCoroutine(LoadBombBeepClip());
        }

        private IEnumerator LoadBombBeepClip()
        {
            _beepLoadStarted = true;
            string path = ResolveBombBeepPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                _beepLoadFailed = true;
                yield break;
            }

            using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, AudioType.MPEG))
            {
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    _beepLoadFailed = true;
                    ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Defuse] Failed loading bomb beep audio '{path}': {request.error}");
                    yield break;
                }

                AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                if (clip == null)
                {
                    _beepLoadFailed = true;
                    ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Defuse] Bomb beep audio decoded null: {path}");
                    yield break;
                }

                clip.name = "LGU_DefuseBombBeep";
                _beepClip = clip;
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Defuse] Loaded bomb beep audio from '{path}'.");
            }
        }

        private static string ResolveBombBeepPath()
        {
            string assemblyDir = Path.GetDirectoryName(typeof(ContractsBootstrap).Assembly.Location) ?? string.Empty;
            string[] candidates =
            {
                Path.Combine(assemblyDir, "ContractAudio", "Defuse", BombBeepFileName),
                Path.Combine(Paths.PluginPath, "y4ngz-Y4NGZCompany", "ContractAudio", "Defuse", BombBeepFileName),
                Path.Combine(Paths.PluginPath, "y4ngz-Y4NGZUpgrades", "ContractAudio", "Defuse", BombBeepFileName),
                Path.Combine(Paths.PluginPath, "ContractAudio", "Defuse", BombBeepFileName),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ContractAudio", "Defuse", BombBeepFileName),
            };

            for (int i = 0; i < candidates.Length; i++)
            {
                if (File.Exists(candidates[i]))
                    return candidates[i];
            }

            return null;
        }

        private void PlayBombBeep(int remainingSeconds)
        {
            EnsureBombAudio();
            if (_audioSource == null)
                return;

            AudioClip clip = _beepClip ?? (_beepLoadFailed ? GetFallbackBeepClip() : null);
            if (clip == null)
                clip = GetFallbackBeepClip();

            _audioSource.pitch = GetBeepPitch(remainingSeconds);
            _audioSource.PlayOneShot(clip, 0.9f);
        }

        private AudioClip GetFallbackBeepClip()
        {
            if (_fallbackBeepClip != null)
                return _fallbackBeepClip;

            int sampleRate = 24000;
            int sampleCount = Mathf.RoundToInt(sampleRate * 0.14f);
            float[] samples = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                float t = i / (float)sampleRate;
                float envelope = Mathf.Clamp01(1f - i / (float)sampleCount);
                samples[i] = Mathf.Sin(2f * Mathf.PI * 1160f * t) * envelope * 0.45f;
            }

            _fallbackBeepClip = AudioClip.Create("LGU_DefuseBombBeepFallback", sampleCount, 1, sampleRate, false);
            _fallbackBeepClip.SetData(samples, 0);
            return _fallbackBeepClip;
        }

        private static float GetBeepInterval(int remainingSeconds)
        {
            if (remainingSeconds > 600)
                return 45f;
            if (remainingSeconds > 300)
                return 30f;
            if (remainingSeconds > 120)
                return 15f;
            if (remainingSeconds > 60)
                return 8f;
            if (remainingSeconds > 30)
                return 4f;
            if (remainingSeconds > 10)
                return 2f;
            return 1f;
        }

        private static float GetBeepPitch(int remainingSeconds)
        {
            if (remainingSeconds > 300)
                return 1f;
            if (remainingSeconds > 60)
                return 1.12f;
            if (remainingSeconds > 30)
                return 1.22f;
            if (remainingSeconds > 10)
                return 1.34f;
            return 1.48f;
        }

        private bool IsPlayerCloseEnough(ulong senderClientId)
        {
            PlayerControllerB player = FindPlayer(senderClientId);
            if (player == null)
                return false;

            float maxDistance = (ContractsBootstrap.DefuseBalance?.GetEffective().InteractDistanceMeters ?? 2.8f) + 0.75f;
            return Vector3.Distance(player.transform.position, transform.position) <= maxDistance;
        }

        private static PlayerControllerB FindPlayer(ulong senderClientId)
        {
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null)
                return null;

            for (int i = 0; i < players.Length; i++)
            {
                PlayerControllerB player = players[i];
                if (player != null && player.actualClientId == senderClientId)
                    return player;
            }

            return null;
        }

        private void FailServer(string reason)
        {
            if (!IsServer || _state.Value == StateFailed || _state.Value == StateDefused)
                return;

            _state.Value = StateFailed;
            DefuseContract.NotifyFailed(this, reason);

            // #220: the meltdown is now unconditional, not a config opt-in, and it no longer
            // replaces the blast — the case detonates AND the facility goes into meltdown.
            // Host-only: Meltdown's own StartMeltdownClientRpc fans the sequence out to
            // clients, so calling it per-client would start it several times over.
            bool meltdownTriggered = FacilityMeltdownCompat.TryStartMeltdown();
            if (!meltdownTriggered)
            {
                ContractsBootstrap.Log?.LogInfo(
                    "[MoonContracts.Defuse] FacilityMeltdown unavailable; detonating without the meltdown sequence.");
            }

            NotifyExplosionClientRpc(transform.position);
            StartCoroutine(DespawnAfterExplosionServer());
        }

        [ClientRpc]
        private void NotifyStatusClientRpc(string message)
        {
            RefreshVisuals(force: true);
            if (string.Equals(message, "DEFUSE COMPLETE", StringComparison.OrdinalIgnoreCase))
                ShowKeypadStatus("SAFE", DefuseKeypadDisplayState.Success, KeypadStatusHoldSeconds);
            else if (string.Equals(message, "WIRE SEQUENCE ACCEPTED", StringComparison.OrdinalIgnoreCase))
                ShowKeypadStatus(FormatLocalKeypadEntry(), DefuseKeypadDisplayState.Normal, 0f);
        }

        [ClientRpc]
        private void NotifyExplosionClientRpc(Vector3 position)
        {
            // #219: a cluster of explosion instances rather than one blast. The lethal
            // envelope is still exactly ExplosionRadiusMeters — only one instance in the
            // cluster carries damage. Seeded from the network id so every client renders
            // the same pattern; the effect itself is local presentation on each machine.
            float radius = ContractsBootstrap.DefuseBalance?.GetEffective().ExplosionRadiusMeters ?? 8f;
            DefuseDetonation.Play(position, radius, unchecked((int)NetworkObjectId));

            HideAfterExplosionLocal();
        }

        private void HandleNetworkVisualChanged(int previous, int current)
        {
            RefreshVisuals(force: false);
        }

        private void RefreshVisuals(bool force)
        {
            int archetypeIndex = _archetypeIndex.Value;
            int cutMask = _cutMask.Value;
            int state = _state.Value;
            if (!force
                && archetypeIndex == _lastVisualArchetype
                && cutMask == _lastVisualCutMask
                && state == _lastVisualState)
            {
                return;
            }

            _lastVisualArchetype = archetypeIndex;
            _lastVisualCutMask = cutMask;
            _lastVisualState = state;

            DefuseArchetype archetype = Archetype;
            ConfigureMarking(archetype);
            ConfigureDisplay(archetype, state);
            ConfigureWires(archetype, cutMask);
            ApplyGlobalKeypadDisplay();
        }

        // The authored kit names its stickers DD_Marking_KILO.001 ... DD_Marking_TYPE_7.001
        // (the .001 is Blender's duplicate suffix), and ships with only KILO active — so a
        // swap that silently matches nothing leaves every bomb reading KILO, which is exactly
        // the #217 symptom. The prefix match handles the suffix; the log makes a miss visible
        // instead of leaving it to look like a randomization bug.
        private void ConfigureMarking(DefuseArchetype archetype)
        {
            string activeName = archetype != null
                ? "DD_Marking_" + DefuseRules.GetFamilyName(archetype.Family).Replace("-", "_")
                : "DD_Marking_KILO";

            Transform[] children = GetVisualTransforms();
            int found = 0;
            int activated = 0;
            for (int i = 0; i < children.Length; i++)
            {
                Transform child = children[i];
                if (child == null || !child.name.StartsWith("DD_Marking_", StringComparison.OrdinalIgnoreCase))
                    continue;

                found++;
                bool active = child.name.StartsWith(activeName, StringComparison.OrdinalIgnoreCase);
                if (active)
                    activated++;
                child.gameObject.SetActive(active);
            }

            if (found == 0 || activated == 0)
            {
                ContractsBootstrap.Log?.LogWarning(
                    $"[MoonContracts.Defuse] Marking swap matched nothing: wanted '{activeName}', " +
                    $"found {found} DD_Marking_* child(ren), activated {activated}. The case will keep its authored sticker.");
            }
            else if (ContractsBootstrap.DefuseBalance?.DebugLogging ?? false)
            {
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.Defuse] Marking set to '{activeName}' ({activated}/{found} marking meshes active).");
            }
        }

        private void ConfigureDisplay(DefuseArchetype archetype, int state)
        {
            Transform[] children = GetVisualTransforms();
            for (int i = 0; i < children.Length; i++)
            {
                Transform child = children[i];
                if (child == null)
                    continue;

                string name = child.name ?? string.Empty;
                if (name.StartsWith("DD_DisplayState_", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "DD_DisplayOverlay_Glass", StringComparison.OrdinalIgnoreCase))
                {
                    child.gameObject.SetActive(false);
                }
            }
        }

        private IEnumerator DespawnAfterExplosionServer()
        {
            if (_explosionCleanupStarted)
                yield break;

            _explosionCleanupStarted = true;
            yield return new WaitForSeconds(0.25f);

            NetworkObject networkObject = NetworkObject;
            if (networkObject != null && networkObject.IsSpawned)
            {
                networkObject.Despawn(true);
                yield break;
            }

            Destroy(gameObject);
        }

        private void HideAfterExplosionLocal()
        {
            Renderer[] renderers = GetComponentsInChildren<Renderer>(includeInactive: true);
            for (int i = 0; i < renderers.Length; i++)
                if (renderers[i] != null)
                    renderers[i].enabled = false;

            Collider[] colliders = GetComponentsInChildren<Collider>(includeInactive: true);
            for (int i = 0; i < colliders.Length; i++)
                if (colliders[i] != null)
                    colliders[i].enabled = false;

            InteractTrigger[] triggers = GetComponentsInChildren<InteractTrigger>(includeInactive: true);
            for (int i = 0; i < triggers.Length; i++)
            {
                InteractTrigger trigger = triggers[i];
                if (trigger == null)
                    continue;

                trigger.interactable = false;
                trigger.enabled = false;
            }
        }

        private static CompanyStashKeypadDisplayState MapKeypadDisplayState(DefuseKeypadDisplayState displayState)
        {
            switch (displayState)
            {
                case DefuseKeypadDisplayState.Success:
                    return CompanyStashKeypadDisplayState.Granted;
                case DefuseKeypadDisplayState.Warning:
                case DefuseKeypadDisplayState.Failed:
                    return CompanyStashKeypadDisplayState.Denied;
                default:
                    return CompanyStashKeypadDisplayState.Normal;
            }
        }

        private Transform FindSharedKeypadHost()
        {
            Transform[] transforms = GetComponentsInChildren<Transform>(includeInactive: true);
            Transform enter = null;
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform child = transforms[i];
                if (child == null)
                    continue;

                string name = child.name ?? string.Empty;
                if (string.Equals(name, "Defuse Keypad", StringComparison.OrdinalIgnoreCase))
                    return child;

                if (string.Equals(name, "bttnEnter", StringComparison.OrdinalIgnoreCase))
                    enter = child;
            }

            if (enter == null)
                return null;

            Transform current = enter;
            while (current.parent != null && current.parent != transform)
                current = current.parent;
            return current;
        }

        private void ConfigureWires(DefuseArchetype archetype, int cutMask)
        {
            int wireCount = archetype != null ? archetype.WireCount : DefuseRules.MaxWireSlots;
            for (int slot = 0; slot < DefuseRules.MaxWireSlots; slot++)
            {
                bool slotActive = slot < wireCount;
                bool cut = (cutMask & (1 << slot)) != 0;
                DefuseWireColor wireColor = archetype != null && slot < archetype.Wires.Length
                    ? archetype.Wires[slot]
                    : (DefuseWireColor)Mathf.Clamp(slot, 0, (int)DefuseWireColor.Black);

                ConfigureWireSlot(slot + 1, slotActive, cut, DefuseRules.GetWireColor(wireColor));
            }
        }

        private void ConfigureWireSlot(int slotNumber, bool slotActive, bool cut, Color color)
        {
            string slotPrefix = $"DD_WireSlot_{slotNumber:00}_";
            string socketSuffix = $"_Socket_{slotNumber:00}";
            Transform[] children = GetVisualTransforms();
            for (int i = 0; i < children.Length; i++)
            {
                Transform child = children[i];
                if (child == null)
                    continue;

                string childName = child.name;
                bool isSlotPart = childName.StartsWith(slotPrefix, StringComparison.OrdinalIgnoreCase);
                bool isSocket = childName.EndsWith(socketSuffix, StringComparison.OrdinalIgnoreCase);
                if (!isSlotPart && !isSocket)
                    continue;

                bool active = slotActive;
                if (isSlotPart)
                {
                    if (childName.IndexOf("Intact", StringComparison.OrdinalIgnoreCase) >= 0)
                        active = slotActive && !cut;
                    else if (childName.IndexOf("CutA", StringComparison.OrdinalIgnoreCase) >= 0
                             || childName.IndexOf("CutB", StringComparison.OrdinalIgnoreCase) >= 0)
                        active = slotActive && cut;
                }

                child.gameObject.SetActive(active);
                if (active && isSlotPart)
                    TintRenderers(child.gameObject, color);
            }
        }

        private Transform[] GetVisualTransforms()
        {
            if (_visualTransforms == null || _visualTransforms.Length == 0)
                _visualTransforms = GetComponentsInChildren<Transform>(includeInactive: true);
            return _visualTransforms;
        }

        private static void TintRenderers(GameObject root, Color color)
        {
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(includeInactive: true);
            MaterialPropertyBlock block = new MaterialPropertyBlock();
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                    continue;

                renderer.GetPropertyBlock(block);
                block.SetColor(BaseColorId, color);
                block.SetColor(ColorId, color);
                block.SetColor(UnlitColorId, color);
                renderer.SetPropertyBlock(block);
            }
        }
    }
}
