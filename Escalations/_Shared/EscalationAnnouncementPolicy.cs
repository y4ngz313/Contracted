using System;

namespace Y4NGZCompany.Escalations._Shared
{
    // #1343 S2/S7. The escalation toast's pure core: kinds, copy, the rising-edge rule, the toast's
    // timeline, its card-side geometry and its decode scramble. Deliberately free of UnityEngine (no
    // Mathf, no Time) so the headless ContractHudPresentationRegression fixture compiles this file
    // directly; the Unity presenter lives in EscalationAnnouncementBand.cs.

    internal enum EscalationAnnouncementKind
    {
        None = 0,
        CongregationGathering,
        RitualFireLit,
        CeremonyComplete,
        CeremonyRuined,
        StrangerHunting,
        BlacksiteAborted
    }

    internal enum EscalationAnnouncementTone
    {
        Warning,
        Lethal,
        Calm
    }

    internal readonly struct EscalationAnnouncementCopy
    {
        internal readonly string Header;
        internal readonly string Body;
        internal readonly EscalationAnnouncementTone Tone;

        internal EscalationAnnouncementCopy(string header, string body, EscalationAnnouncementTone tone)
        {
            Header = header ?? string.Empty;
            Body = body ?? string.Empty;
            Tone = tone;
        }
    }

    /// <summary>Every escalation announcement's copy. Bodies are sentence case, headers and the
    /// strip are uppercase telemetry with a middle dot, and nothing carries an exclamation mark.</summary>
    internal static class EscalationAnnouncementCopyTable
    {
        internal const string EscalationHeader = "ESCALATION";
        internal const string BlacksiteAbortedHeader = "BLACKSITE ABORTED";
        /// <summary>The Blacksite pre-flight skip's reason: no landing plan was sealed.</summary>
        internal const string BlacksiteNoRouteBody = "No safe ship landing and access route found.";

        /// <summary>The player-facing row for <paramref name="kind"/>. Abort diagnostics stay
        /// in the log, never in the announcement body.</summary>
        internal static EscalationAnnouncementCopy For(EscalationAnnouncementKind kind)
        {
            switch (kind)
            {
                case EscalationAnnouncementKind.CongregationGathering:
                    return new EscalationAnnouncementCopy(EscalationHeader,
                        "The Congregation is gathering outside. Kill the congregants before the ritual completes.",
                        EscalationAnnouncementTone.Warning);
                case EscalationAnnouncementKind.RitualFireLit:
                    return new EscalationAnnouncementCopy(EscalationHeader,
                        "The ritual fire is lit. Kill the congregants to disrupt the ceremony.",
                        EscalationAnnouncementTone.Warning);
                case EscalationAnnouncementKind.CeremonyComplete:
                    return new EscalationAnnouncementCopy(EscalationHeader,
                        "The ceremony is complete.",
                        EscalationAnnouncementTone.Lethal);
                case EscalationAnnouncementKind.CeremonyRuined:
                    return new EscalationAnnouncementCopy(EscalationHeader,
                        "The ceremony is ruined. The congregants are dead.",
                        EscalationAnnouncementTone.Calm);
                case EscalationAnnouncementKind.StrangerHunting:
                    return new EscalationAnnouncementCopy(EscalationHeader,
                        "The Stranger is hunting you. It cannot be stopped. Survive.",
                        EscalationAnnouncementTone.Warning);
                case EscalationAnnouncementKind.BlacksiteAborted:
                    return new EscalationAnnouncementCopy(BlacksiteAbortedHeader,
                        "The Blacksite team turned back.",
                        EscalationAnnouncementTone.Warning);
                default:
                    return new EscalationAnnouncementCopy(string.Empty, string.Empty, EscalationAnnouncementTone.Warning);
            }
        }

        /// <summary>The Congregation's alert-strip line. The ceremony key stays stable so each new
        /// percentage updates the strip in place.</summary>
        internal static string CongregationStrip(bool ceremonyStarted, int progressPercent)
        {
            return ceremonyStarted
                ? "KILL THE CONGREGANTS · RITUAL " + progressPercent + "%"
                : "FIGURES GATHERING OUTSIDE";
        }
    }

    /// <summary>The host-observed Congregation flags one state message carries.</summary>
    internal readonly struct CongregationAnnouncementSnapshot
    {
        internal readonly bool Active;
        internal readonly bool CeremonyStarted;
        internal readonly bool Complete;
        internal readonly bool Ruined;

        internal CongregationAnnouncementSnapshot(bool active, bool ceremonyStarted, bool complete, bool ruined)
        {
            Active = active;
            CeremonyStarted = ceremonyStarted;
            Complete = complete;
            Ruined = ruined;
        }
    }

    /// <summary>
    /// When a client announces a replicated escalation state. An announcement fires on the
    /// rising edge of its flag only, so a duplicate broadcast or a late-join resend never
    /// re-announces. A late joiner's first message is a rising edge for whatever is already set:
    /// it gets the current ongoing state once (the newest only), and a terminal event only while it
    /// is still fresh, never history.
    /// </summary>
    internal static class EscalationAnnouncementEdges
    {
        /// <summary>A terminal event older than this on arrival is history, not news.</summary>
        internal const double TerminalFreshSeconds = 5.0;

        internal static bool IsTerminal(EscalationAnnouncementKind kind)
        {
            return kind == EscalationAnnouncementKind.CeremonyComplete
                || kind == EscalationAnnouncementKind.CeremonyRuined
                || kind == EscalationAnnouncementKind.BlacksiteAborted;
        }

        /// <summary>Seconds since the host stamped the event. An unstamped event (0 or less, an
        /// older host or no event) is infinitely old.</summary>
        internal static double EventAge(double eventServerTime, double nowServerTime)
        {
            return eventServerTime <= 0d ? double.PositiveInfinity : nowServerTime - eventServerTime;
        }

        /// <summary>True on the rising edge of <paramref name="isSet"/>; a terminal kind also needs a
        /// fresh event. A negative age is clock skew and counts as fresh.</summary>
        internal static bool ShouldAnnounce(EscalationAnnouncementKind kind, bool wasSet, bool isSet, double eventAgeSeconds)
        {
            if (!isSet || wasSet)
                return false;
            if (!IsTerminal(kind))
                return true;
            return eventAgeSeconds <= TerminalFreshSeconds;
        }

        /// <summary>The one Congregation announcement a state message raises, newest first:
        /// ruined, complete, fire lit, gathering. None when nothing rose.</summary>
        internal static EscalationAnnouncementKind SelectCongregation(
            in CongregationAnnouncementSnapshot previous,
            in CongregationAnnouncementSnapshot current,
            double endEventAgeSeconds)
        {
            if (ShouldAnnounce(EscalationAnnouncementKind.CeremonyRuined, previous.Ruined, current.Ruined, endEventAgeSeconds))
                return EscalationAnnouncementKind.CeremonyRuined;
            if (ShouldAnnounce(EscalationAnnouncementKind.CeremonyComplete, previous.Complete, current.Complete, endEventAgeSeconds))
                return EscalationAnnouncementKind.CeremonyComplete;
            if (current.Active
                && ShouldAnnounce(EscalationAnnouncementKind.RitualFireLit, previous.CeremonyStarted, current.CeremonyStarted, 0d))
                return EscalationAnnouncementKind.RitualFireLit;
            if (ShouldAnnounce(EscalationAnnouncementKind.CongregationGathering, previous.Active, current.Active, 0d))
                return EscalationAnnouncementKind.CongregationGathering;
            return EscalationAnnouncementKind.None;
        }
    }

    internal enum EscalationBandPhase
    {
        SlideIn,
        Resolve,
        Pulse,
        Hold,
        Collapse,
        Sweep,
        Idle
    }

    /// <summary>
    /// The toast's timeline (#1343 S7: the contract card's timeline). <c>t</c> is unscaled seconds
    /// since Show. Every sampler clamps, and once the toast is idle every sampler is at rest: no
    /// offset, full decode, no vignette, unit brightness, fully collapsed, nothing visible.
    /// </summary>
    internal static class EscalationBandTimeline
    {
        internal const float SlideSeconds = 0.25f;
        internal const float DecodeStart = 0.05f;
        internal const float DecodeSeconds = 0.4f;
        // #1343 S7 D7: the danger vignette stays, weaker and thinner (S2 had 0.22 alpha and 18%).
        internal const float VignetteSeconds = 0.6f;
        internal const float VignettePeakAlpha = 0.12f;
        internal const float VignetteInsetOfShorterSide = 0.1f;
        internal const float PulseStart = 0.45f;
        internal const float PulseSeconds = 0.5f;
        internal const float PulseGain = 0.3f;
        internal const float HoldStart = 1.45f;
        internal const float CollapseStart = 3f;
        internal const float CollapseSeconds = 0.5f;
        internal const float SweepStart = 3.5f;
        internal const float SweepSeconds = 0.45f;
        internal const float TotalSeconds = 3.95f;

        private const float DecodeEnd = 0.45f;
        private const int PulseCount = 2;
        // Text is gone in the first 40% of the move (3.2 s), the accent arrives in the last 40%
        // (3.3-3.5 s), and the box itself fades over the last 20% (3.4-3.5 s).
        private const float TextFadeOfCollapse = 0.4f;
        private const float AccentBlendFromCollapse = 0.6f;
        private const float BandFadeFromCollapse = 0.8f;

        internal static EscalationBandPhase Phase(float t)
        {
            if (t < SlideSeconds) return EscalationBandPhase.SlideIn;
            if (t < PulseStart) return EscalationBandPhase.Resolve;
            if (t < HoldStart) return EscalationBandPhase.Pulse;
            if (t < CollapseStart) return EscalationBandPhase.Hold;
            if (t < SweepStart) return EscalationBandPhase.Collapse;
            if (t < TotalSeconds) return EscalationBandPhase.Sweep;
            return EscalationBandPhase.Idle;
        }

        /// <summary>Fraction of the toast's width it sits to the right while sliding in.</summary>
        internal static float SlideOffset(float t)
        {
            if (t >= SlideSeconds) return 0f;
            float remaining = 1f - Clamp01(t / SlideSeconds);
            // 1 - easeOutCubic(x) is (1 - x)^3.
            return remaining * remaining * remaining;
        }

        /// <summary>True on the first rendered frame at or after the band lands, exactly once at
        /// any frame rate. The band starts with <paramref name="previousT"/> = -1.</summary>
        internal static bool FlashFrame(float previousT, float t)
        {
            return previousT < SlideSeconds && t >= SlideSeconds;
        }

        internal static float DecodeProgress(float t)
        {
            if (t <= DecodeStart) return 0f;
            if (t >= DecodeEnd) return 1f;
            return Clamp01((t - DecodeStart) / DecodeSeconds);
        }

        internal static float VignetteAlpha(float t, EscalationAnnouncementTone tone)
        {
            if (tone == EscalationAnnouncementTone.Calm || t < 0f || t >= VignetteSeconds)
                return 0f;
            return VignettePeakAlpha * (float)Math.Sin(Math.PI * t / VignetteSeconds);
        }

        /// <summary>The vignette's inner inset: <see cref="VignetteInsetOfShorterSide"/> of the
        /// shorter side of the rect it frames.</summary>
        internal static float VignetteInset(float width, float height)
        {
            float shorter = width < height ? width : height;
            return shorter > 0f ? shorter * VignetteInsetOfShorterSide : 0f;
        }

        /// <summary>Two sine pulses on the rules after the decode resolves; 1 otherwise.</summary>
        internal static float RuleBrightness(float t)
        {
            for (int i = 0; i < PulseCount; i++)
            {
                float start = PulseStart + i * PulseSeconds;
                if (t >= start && t < start + PulseSeconds)
                    return 1f + PulseGain * (float)Math.Sin(Math.PI * (t - start) / PulseSeconds);
            }
            return 1f;
        }

        internal static float CollapseLinear(float t)
        {
            if (t <= CollapseStart) return 0f;
            if (t >= SweepStart) return 1f;
            return Clamp01((t - CollapseStart) / CollapseSeconds);
        }

        internal static float CollapseProgress(float t)
        {
            float x = CollapseLinear(t);
            return x * x * (3f - 2f * x);
        }

        internal static float TextAlpha(float t)
        {
            if (t < CollapseStart) return 1f;
            return 1f - Clamp01(CollapseLinear(t) / TextFadeOfCollapse);
        }

        /// <summary>Cross-fade of the band's colours toward the strip's accent.</summary>
        internal static float AccentBlend(float t)
        {
            if (t < CollapseStart) return 0f;
            return Clamp01((CollapseLinear(t) - AccentBlendFromCollapse) / (1f - AccentBlendFromCollapse));
        }

        internal static float BandAlpha(float t)
        {
            if (t < CollapseStart) return 1f;
            if (t >= SweepStart) return 0f;
            return 1f - Clamp01((CollapseLinear(t) - BandFadeFromCollapse) / (1f - BandFadeFromCollapse));
        }

        internal static float SweepProgress(float t)
        {
            if (t <= SweepStart) return 0f;
            if (t >= TotalSeconds) return 1f;
            return Clamp01((t - SweepStart) / SweepSeconds);
        }

        internal static bool SweepActive(float t)
        {
            return t >= SweepStart && t < TotalSeconds;
        }

        internal static bool IsIdle(float t)
        {
            return t >= TotalSeconds;
        }

        internal static float Clamp01(float value)
        {
            if (value <= 0f || float.IsNaN(value)) return 0f;
            return value >= 1f ? 1f : value;
        }
    }

    /// <summary>A y-up root-local box: <see cref="X"/> is the left edge and <see cref="Y"/> the
    /// bottom edge, like a Unity Rect's xMin and yMin.</summary>
    internal readonly struct EscalationToastBox
    {
        internal readonly float X;
        internal readonly float Y;
        internal readonly float Width;
        internal readonly float Height;

        internal EscalationToastBox(float x, float y, float width, float height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        internal float XMax => X + Width;
        internal float YMax => Y + Height;
    }

    /// <summary>
    /// #1343 S7 D6. The card-side toast's geometry: a compact box directly below the contract card,
    /// exactly as wide, left edges aligned. Every size here is in card units (the card body's
    /// authored units, 500 across) and scales by cardUnit = toast width / 500, so the toast's type
    /// renders at the card's resting sizes. Never large, never centred: without a card it takes
    /// the alert strip's fallback corner.
    /// </summary>
    internal static class EscalationToastLayout
    {
        // Mirrors of the contract card (origin/main e93042ba).
        internal const float CardBodyWidthUnits = 500f;  // ContractHUDManager.cs:130 LiveCardDefaultWidth
        internal const float BeltHeightUnits = 38f;      // ContractHudAlertBelt.cs:18 Height
        internal const float CardGapUnits = 6f;          // ContractHUDManager.cs:143 LiveCardBeltGap
        internal const float MaxHeightUnits = 3f * BeltHeightUnits;
        internal const float HeaderFontUnits = 32f;      // ContractHUDManager.cs:696 title, resting size
        internal const float BodyFontUnits = 22f;        // ContractHUDManager.cs:714 objective text
        internal const float SidePaddingUnits = 20f;     // ContractHUDManager.cs:144 LiveCardInnerSidePadding
        internal const float RuleThicknessUnits = 2f;    // ContractHudAlertBelt.cs:62-64 frame lines
        // The toast's own spacing.
        internal const float TopPaddingUnits = 10f;
        internal const float HeaderBodyGapUnits = 6f;
        internal const float BottomPaddingUnits = 12f;
        internal const float IconSizeUnits = 30f;
        internal const float IconGapUnits = 8f;
        internal const float HeaderLeftUnits = SidePaddingUnits + IconSizeUnits + IconGapUnits;
        internal const float HeaderWidthUnits = CardBodyWidthUnits - HeaderLeftUnits - SidePaddingUnits;
        internal const float BodyWidthUnits = CardBodyWidthUnits - 2f * SidePaddingUnits;
        internal const int BodyMaxLines = 2;
        // No card on screen: the alert strip fallback's corner numbers, 250 px wide and 16 px in
        // from the safe area's top-right corner at 1280x720.
        internal const float FallbackWidthOfRootWidth = 0.195f;
        internal const float FallbackInsetOfRootHeight = 24f / 1080f;

        /// <summary>Root units per card unit for a toast laid out as <paramref name="toast"/>.</summary>
        internal static float CardUnit(EscalationToastBox toast)
        {
            return toast.Width / CardBodyWidthUnits;
        }

        /// <summary>Top padding, the header line, the gap, the body block and the bottom padding,
        /// in card units, from the measured header line and body block heights.</summary>
        internal static float NaturalHeightUnits(float headerLineUnits, float bodyUnits)
        {
            return TopPaddingUnits + headerLineUnits + HeaderBodyGapUnits + bodyUnits + BottomPaddingUnits;
        }

        /// <summary>
        /// The toast's box in root space. With a card: the card's width and left edge, its top
        /// <see cref="CardGapUnits"/> below the card's bottom. Without one: 0.195 of the root's
        /// width in the safe area's top-right corner, inset 24/1080 of the root's height. The height
        /// is <paramref name="naturalHeightUnits"/> capped at three alert-strip heights, and the box
        /// is shifted (and only narrowed when wider than the area) to stay inside the safe area.
        /// </summary>
        internal static EscalationToastBox ToastRect(bool hasCard, EscalationToastBox card, EscalationToastBox root,
            EscalationToastBox safeArea, float naturalHeightUnits)
        {
            float width = hasCard ? card.Width : root.Width * FallbackWidthOfRootWidth;
            if (width > safeArea.Width)
                width = safeArea.Width;
            if (width < 0f || float.IsNaN(width))
                width = 0f;

            float unit = width / CardBodyWidthUnits;
            float left, top;
            if (hasCard)
            {
                left = card.X;
                top = card.Y - CardGapUnits * unit;
            }
            else
            {
                float inset = root.Height * FallbackInsetOfRootHeight;
                left = safeArea.XMax - inset - width;
                top = safeArea.YMax - inset;
            }

            float natural = naturalHeightUnits > 0f ? naturalHeightUnits : 0f;
            float height = (natural < MaxHeightUnits ? natural : MaxHeightUnits) * unit;
            if (height > safeArea.Height)
                height = safeArea.Height > 0f ? safeArea.Height : 0f;

            if (left + width > safeArea.XMax)
                left = safeArea.XMax - width;
            if (left < safeArea.X)
                left = safeArea.X;
            float bottom = top - height;
            if (bottom + height > safeArea.YMax)
                bottom = safeArea.YMax - height;
            if (bottom < safeArea.Y)
                bottom = safeArea.Y;
            return new EscalationToastBox(left, bottom, width, height);
        }
    }

    /// <summary>
    /// The header and body decode: resolved characters read as the target, the rest scramble
    /// through a fixed glyph set picked by a deterministic hash of (index, frame). No random
    /// stream is touched, so gameplay randomness never shifts.
    /// </summary>
    internal static class EscalationBandDecode
    {
        /// <summary>Scramble frames per second; callers pass <see cref="ScrambleFrame"/>.</summary>
        internal const float ScrambleFramesPerSecond = 30f;
        // Rich text is off on the band, but '<' and '>' stay out anyway; no '!' either.
        private const string Glyphs = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789#$%&*+=?/";

        internal static int ScrambleFrame(float t)
        {
            return (int)(t * ScrambleFramesPerSecond);
        }

        /// <summary>How many leading characters of a <paramref name="length"/>-long target read
        /// as the target at <paramref name="t"/>.</summary>
        internal static int ResolvedCount(int length, float t)
        {
            return (int)Math.Floor(length * EscalationBandTimeline.DecodeProgress(t));
        }

        /// <summary>Writes the decoded <paramref name="target"/> into <paramref name="buffer"/>
        /// (the caller guarantees capacity) and returns its length.</summary>
        internal static int Write(string target, float t, int frame, char[] buffer)
        {
            if (string.IsNullOrEmpty(target))
                return 0;

            int length = target.Length;
            int resolved = ResolvedCount(length, t);
            for (int i = 0; i < length; i++)
            {
                char c = target[i];
                buffer[i] = i < resolved || char.IsWhiteSpace(c) ? c : Glyphs[GlyphIndex(i, frame)];
            }
            return length;
        }

        private static int GlyphIndex(int index, int frame)
        {
            unchecked
            {
                uint h = (uint)index * 0x9E3779B1u ^ ((uint)frame + 0x7F4A7C15u) * 0x85EBCA77u;
                h ^= h >> 15;
                h *= 0x2C1B3C6Du;
                h ^= h >> 12;
                h *= 0x297A2D39u;
                h ^= h >> 15;
                return (int)(h % (uint)Glyphs.Length);
            }
        }
    }
}
