using System;
using UnityEngine;

using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Escalations.Bundy;

/// <summary>
/// Cosmetic engine vibration for the chainsaw prop. Client-local, no netcode, no gameplay effect.
///
/// WHY A RUNTIME OFFSET AND NOT BAKED INTO THE CLIPS
/// -------------------------------------------------
/// The locomotion clips are 30 fps. Nyquist is 15 Hz and the usable band is ~7.5 Hz, while a chainsaw
/// idles around 45 Hz — a baked vibration can only ever be a slow shudder. It would also have to be
/// baked into all 14 movement clips plus the attacks, turns and idles, and rebaked whenever any of
/// them changed. The saw is a single bone
/// (`Armature/root/pelvis/…/hand_l/BundyProp_Chainsaw`), so one LateUpdate offset covers every state.
///
/// WHY THE HANDS COME ALONG FOR FREE
/// ---------------------------------
/// The left hand is the saw's PARENT, so the rear grip is fixed by construction. The right hand is
/// solved by `BundyChainsawHandIK` onto `BundyChainsaw_RightHandIKTarget`, which is a CHILD of the
/// saw, so the front grip tracks the saw rather than fighting it.
///
/// EXECUTION ORDER
/// ---------------
/// `BundyChainsawHandIK.LateUpdate()` carries no `[DefaultExecutionOrder]`, so it sits at 0 and the
/// relative order would otherwise be undefined. The positive order below makes this component
/// provably last, and <see cref="LogOrderingAssertionOnce"/> asserts it from the actual attribute
/// values on the first tick instead of assuming it.
///
/// The trade-off that choice makes: running last means the right hand was solved against the
/// PRE-rumble saw pose, so the front grip lags the vibration by one frame. At the amplitudes this
/// ships with (&lt;=1 degree) that is a sub-centimetre slip at the grip radius and reads as the saw
/// buzzing in a firm hold. Running BEFORE the solver instead (a negative order) would make the right
/// hand track the vibration exactly, at the cost of shaking the whole arm. If the grip ever looks
/// detached, that is the knob — flip the sign, do not add damping here.
///
/// `BundyChainsawPropSpin` is not a conflict — it drives the Animator on the
/// `BundyCustomChainsaw_Animated` CHILD (the blade-chain loop), and never writes this transform.
/// </summary>
[DefaultExecutionOrder(200)]
public sealed class BundyChainsawRumble : MonoBehaviour
{
    // Measured 2026-08-03 from the prop's own meshes, in BundyProp_Chainsaw local space: the `Blades`
    // mesh's longest chord runs (0.6995, -0.0228, -0.7142) and the `Chainsaw` body's runs the same
    // line reversed (|dot| = 0.994). So the bar lies on the XZ diagonal with essentially no Y.
    //
    // Rotating ABOUT this axis rolls the saw around its own length, which is the engine's torque
    // reaction and reads as a rumble. Rotating across it reads as a wobble and looks broken.
    public static readonly Vector3 DefaultBladeAxis = new Vector3(0.6995f, -0.0228f, -0.7142f);

    // The loop AudioSource BundyAIBase.ResolveMovementLoopAudioSource() parents to this very prop.
    private const string LoopAudioObjectName = "BundyMovementLoopAudio";

    public Transform prop;
    public Vector3 bladeAxis = DefaultBladeAxis;

    private AudioSource _loop;
    private bool _loopResolved;
    private float _phase;
    private float _revEnvelope;
    private bool _loggedOrdering;
    private bool _loggedFirstTick;

    // Baseline tracking. See ResolveAuthoredRotation for why this is not a plain `localRotation *=`.
    private Quaternion _authoredLocalRotation = Quaternion.identity;
    private Quaternion _lastWritten = Quaternion.identity;
    private bool _hasBaseline;
    private bool _wroteOffsetLastFrame;
    private bool _warnedNoAnimatorWrites;

    private static bool Enabled => BundyPlugin.ModConfig?.ChainsawRumbleEnabled?.Value ?? true;
    private static float FrequencyHz =>
        Mathf.Clamp(BundyPlugin.ModConfig?.ChainsawRumbleFrequencyHz?.Value ?? 11f, 0.1f, 60f);
    private static float AmplitudeDegrees =>
        Mathf.Clamp(BundyPlugin.ModConfig?.ChainsawRumbleAmplitudeDegrees?.Value ?? 0.7f, 0f, 15f);
    private static float RevGain =>
        Mathf.Clamp(BundyPlugin.ModConfig?.ChainsawRumbleRevGain?.Value ?? 1.6f, 0f, 8f);
    private static float LoopBaseVolume =>
        Mathf.Clamp01(BundyPlugin.ModConfig?.ChainsawLoopBaseVolume?.Value ?? 0.65f);

    // How fast the rev swell follows the audio. Attack is quick so a throttle blip lands with the
    // sound; release is slower so the saw settles rather than snapping back to idle.
    private const float RevAttackSeconds = 0.06f;
    private const float RevReleaseSeconds = 0.35f;

    private void Awake()
    {
        if (prop == null)
            prop = transform;
        if (bladeAxis.sqrMagnitude < 0.0001f)
            bladeAxis = DefaultBladeAxis;
        bladeAxis = bladeAxis.normalized;
    }

    private void LateUpdate()
    {
        if (prop == null)
            prop = transform;

        LogOrderingAssertionOnce();

        float dt = Mathf.Max(0f, Time.deltaTime);
        float target = ResolveTargetAmplitudeDegrees();

        // Envelope first, so the vibration decays smoothly to rest instead of cutting out mid-cycle.
        float tau = target > _revEnvelope ? RevAttackSeconds : RevReleaseSeconds;
        _revEnvelope = Mathf.Lerp(_revEnvelope, target, DampingAlpha(dt, tau));
        if (_revEnvelope < 0.0005f)
            _revEnvelope = 0f;

        // Advance phase off the CURRENT frequency rather than off absolute time, so a live config
        // change does not jump the waveform.
        _phase += 2f * Mathf.PI * FrequencyHz * dt;
        if (_phase > 2f * Mathf.PI)
            _phase -= 2f * Mathf.PI * Mathf.Floor(_phase / (2f * Mathf.PI));

        Quaternion authored = ResolveAuthoredRotation();

        if (_revEnvelope <= 0f)
        {
            // Fully at rest. Restore exactly once — on the frame the vibration finished decaying —
            // then stop writing entirely, so an off/idle saw is bit-identical to the no-component case
            // and nothing here can contend with a future writer of this transform.
            if (_hasBaseline && _wroteOffsetLastFrame)
            {
                prop.localRotation = authored;
                _lastWritten = authored;
                _wroteOffsetLastFrame = false;
            }
            return;
        }

        float angle = _revEnvelope * Mathf.Sin(_phase);
        Quaternion written = authored * Quaternion.AngleAxis(angle, bladeAxis);
        prop.localRotation = written;
        _lastWritten = written;
        _wroteOffsetLastFrame = true;

        if (!_loggedFirstTick && BundyLogGate.Allows(BundyLogCategory.Anim, 2))
        {
            _loggedFirstTick = true;
            LogRumbleInfo(
                $"[BundyChainsawRumble] first vibration tick: freq={FrequencyHz:F1}Hz, " +
                $"amplitude={AmplitudeDegrees:F2}deg, envelope={_revEnvelope:F3}deg, " +
                $"axis={bladeAxis}, prop='{prop.name}', loopSource={(_loop != null)}.");
        }
    }

    /// <summary>
    /// The prop's authored pose, tracked defensively.
    ///
    /// A plain `localRotation *= offset` is WRONG here and would be spectacular in play. The Animator
    /// rewrites this transform every frame in `Chainsaw_WalkForward` / `RunForward` / `Idle` (measured
    /// 2026-08-03: they each carry a constant identity `m_LocalRotation` binding for
    /// `BundyProp_Chainsaw`), which would make the naive form correct — but
    /// `Chainsaw_Sprint_Overhead_Loop` carries **no prop bindings at all**, so during a sprint nothing
    /// resets the transform and the offset would compound every frame until the saw span away.
    ///
    /// So: if what we find differs from what we wrote last frame, something upstream (the Animator)
    /// authored it and that becomes the new baseline. If it matches, nothing rewrote it and the old
    /// baseline stands. Correct under both clip shapes, and self-correcting if a re-export changes
    /// which clips animate the prop.
    /// </summary>
    private Quaternion ResolveAuthoredRotation()
    {
        Quaternion current = prop.localRotation;

        if (!_hasBaseline)
        {
            _authoredLocalRotation = current;
            _hasBaseline = true;
            return _authoredLocalRotation;
        }

        bool upstreamWrote = Quaternion.Angle(current, _lastWritten) > 0.0005f;
        if (upstreamWrote)
        {
            _authoredLocalRotation = current;
        }
        else if (!_warnedNoAnimatorWrites && _revEnvelope > 0f && BundyLogGate.Allows(BundyLogCategory.Anim, 2))
        {
            // Not an error — the sprint clips legitimately do not bind the prop. Logged once so the
            // "which clips animate the saw" question is answerable from a log rather than a re-probe.
            _warnedNoAnimatorWrites = true;
            LogRumbleInfo(
                "[BundyChainsawRumble] no upstream writer for the prop's localRotation this frame " +
                "(expected during sprint, whose clips carry no BundyProp_Chainsaw bindings); " +
                "holding the cached authored pose so the offset cannot compound.");
        }

        return _authoredLocalRotation;
    }

    /// <summary>
    /// Amplitude in degrees, driven off the saw's OWN audio so the vibration and the sound agree by
    /// construction rather than by two independent timers.
    ///
    /// `BundyAIBase.ResolveMovementLoopAudioSource()` parents the engine loop to this prop as a child
    /// named `BundyMovementLoopAudio`, so it is a local lookup. Its volume rests at
    /// `ChainsawLoopBaseVolume` (0.65) and swells toward 1.0 on revs, swings and contact via
    /// `UpdateMovementLoopAudioModulation` — that excursion is the rev signal.
    /// </summary>
    private float ResolveTargetAmplitudeDegrees()
    {
        if (!Enabled)
            return 0f;

        ResolveLoopSource();
        if (_loop == null || !_loop.isPlaying)
            return 0f; // Saw off: no engine, no vibration.

        float baseVolume = LoopBaseVolume;
        float headroom = Mathf.Max(0.01f, 1f - baseVolume);
        float rev = Mathf.Clamp01((_loop.volume - baseVolume) / headroom);

        return AmplitudeDegrees * (1f + RevGain * rev);
    }

    private void ResolveLoopSource()
    {
        // The loop source is created lazily by the AI the first time the saw sounds, so a single
        // failed lookup is normal — keep retrying until it exists, then cache.
        if (_loop != null)
            return;

        Transform t = prop != null ? prop.Find(LoopAudioObjectName) : null;
        if (t != null)
            _loop = t.GetComponent<AudioSource>();

        if (_loop != null && !_loopResolved)
        {
            _loopResolved = true;
            if (BundyLogGate.Allows(BundyLogCategory.Anim, 2))
                LogRumbleInfo($"[BundyChainsawRumble] bound to engine loop source '{LoopAudioObjectName}'.");
        }
    }

    /// <summary>
    /// Asserts the ordering from the real attribute values rather than trusting that the positive
    /// number above did what it says. Logged once, as a warning when it is WRONG so it cannot be lost
    /// in verbose output.
    /// </summary>
    private void LogOrderingAssertionOnce()
    {
        if (_loggedOrdering)
            return;

        _loggedOrdering = true;

        int mine = ReadExecutionOrder(typeof(BundyChainsawRumble));
        int solver = ReadExecutionOrder(typeof(BundyChainsawHandIK));
        bool ok = mine > solver;

        string message =
            $"[BundyChainsawRumble] execution order: rumble={mine}, BundyChainsawHandIK={solver}, " +
            $"runsAfterSolver={ok}.";

        if (!ok)
            LogRumbleWarning(message + " EXPECTED the rumble to run AFTER the hand solver; the front " +
                             "grip will be solved against the vibrated pose and the arm will shake.");
        else if (BundyLogGate.Allows(BundyLogCategory.Anim, 2))
            LogRumbleInfo(message);
    }

    private static int ReadExecutionOrder(Type type)
    {
        var attribute = (DefaultExecutionOrder)Attribute.GetCustomAttribute(type, typeof(DefaultExecutionOrder));
        return attribute != null ? attribute.order : 0;
    }

    private static float DampingAlpha(float deltaTime, float timeConstant)
    {
        if (deltaTime <= 0f)
            return 0f;
        return 1f - Mathf.Exp(-deltaTime / Mathf.Max(0.0001f, timeConstant));
    }

    // #716 F3: these lines used to go straight to Unity's Debug, so they bypassed BepInEx's Bundy
    // logger (and its source name) even though every call site already consults BundyLogGate. The
    // "[BundyChainsawRumble]" prefix inside the messages is left alone; a later pass unifies prefixes.
    private static void LogRumbleInfo(string message)
    {
        if (BundyPlugin.Log != null)
            BundyPlugin.Log.LogInfo(message);
        else
            Debug.Log(message);
    }

    private static void LogRumbleWarning(string message)
    {
        if (BundyPlugin.Log != null)
            BundyPlugin.Log.LogWarning(message);
        else
            Debug.LogWarning(message);
    }

    private void OnDisable()
    {
        // Leave the prop exactly as authored rather than frozen mid-cycle.
        if (_hasBaseline && _wroteOffsetLastFrame && prop != null)
        {
            prop.localRotation = _authoredLocalRotation;
            _lastWritten = _authoredLocalRotation;
        }
        _wroteOffsetLastFrame = false;
        _revEnvelope = 0f;
    }
}
