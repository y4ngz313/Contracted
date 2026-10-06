using System;
using System.Globalization;

namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>
    /// The words the end-of-round performance report puts on a contract act line, plus the count
    /// formatting its other note lines share.
    ///
    /// #1202 (Lawson, 2026-09-22): an act line reads as what the player did - verb first, past
    /// tense, no trailing period - and a repeat count sits inside the sentence rather than in a
    /// trailing "xN", except on the two kinds Lawson kept on a trailing count.
    ///
    /// Engine-free on purpose. <see cref="MoonContractRoundStats"/> needs UnityEngine and cannot
    /// be compiled outside the game, so the wording lives here where tests/PerformanceReportRegression
    /// runs it instead of reading it back as source text.
    /// </summary>
    internal static class PerformanceReportWording
    {
        /// <summary>
        /// The act line for one credit, worded for <paramref name="count"/>. Contract-level
        /// outcomes return empty: the DIRECTIVE box already states success or failure, so a note
        /// would only repeat it, and <c>RecordProgressionEvent</c> drops the credit on the empty
        /// string. A merged credit is re-worded whenever its count rises, so this is called at
        /// render time and not once at the first act.
        /// </summary>
        internal static string DescribeAct(ContractProgressionEventKind kind, bool auditLever, int count)
        {
            count = Math.Max(1, count);
            switch (kind)
            {
                case ContractProgressionEventKind.DefuseCorrectWire:
                    return count == 1 ? "Cut the correct wire" : $"Cut {FormatCount(count, "correct wire")}";
                case ContractProgressionEventKind.DefuseCorrectCode:
                    return "Entered the defuse code";
                case ContractProgressionEventKind.WhistleblowerSighted:
                    return "Spotted the Whistleblower";
                case ContractProgressionEventKind.WhistleblowerNeutralized:
                    return "Killed the Whistleblower";
                case ContractProgressionEventKind.SurveyDronePlaced:
                    return count == 1 ? "Planted a survey beacon" : $"Planted {FormatCount(count, "survey beacon")}";
                case ContractProgressionEventKind.BlackoutBreakerRestored:
                    return BreakerRestoredLabel(count);
                case ContractProgressionEventKind.BlackoutAuditRestored:
                    // #794: the full restore is the last breaker's consequence, so it shares the
                    // breaker line (and merges into it without raising the count). Only the audit
                    // lever, which is not a breaker, keeps a line of its own.
                    return auditLever ? "Pulled the audit lever" : BreakerRestoredLabel(count);
                case ContractProgressionEventKind.ShadowRaidDrillAttached:
                    return "Mounted the breach drill";
                case ContractProgressionEventKind.ShadowRaidDrillCompleted:
                    return "Cracked the container";
                case ContractProgressionEventKind.PestControlCompleted:
                case ContractProgressionEventKind.PestControlCapture:
                    // One wording for both capture kinds on purpose: they share a merge key and
                    // become "Trapped 2 specimens" rather than two near-duplicate lines.
                    return count == 1 ? "Trapped a specimen" : $"Trapped {FormatCount(count, "specimen")}";
                case ContractProgressionEventKind.WasteDisposalCompleted:
                    return "Fired the incinerator";
                // #406 kinds. Without these the four attributed sub-objectives were published and
                // rewarded but never reached the report at all.
                case ContractProgressionEventKind.ContainmentBreachWaveSurvived:
                    // Count is the wave number, not a repetition, so the sentence carries it and
                    // the render adds no "xN".
                    return $"Survived wave {count.ToString(CultureInfo.InvariantCulture)}";
                case ContractProgressionEventKind.ShadowRaidHardDriveRecovered:
                    return "Recovered the hard drive";
                case ContractProgressionEventKind.WasteDisposalRepaired:
                    // Lawson kept this one on a trailing xN: a second repair is the same act again.
                    return "Fixed the incinerator";
                // #805 kinds. Lategame Upgrades' own ClientRpcs name the object and not the hand,
                // so nine of these ten are crew-wide payouts and say what the crew got done; the
                // cull is the one that can name a killer.
                case ContractProgressionEventKind.LguLaptopLogin:
                    return "Crew logged into laptop";
                case ContractProgressionEventKind.LguDataRecovered:
                    return "Crew pulled survey data";
                case ContractProgressionEventKind.LguHoardingBugExterminated:
                    return $"Culled {FormatCount(count, "hoarding bug")}";
                case ContractProgressionEventKind.LguNestDestroyed:
                    return "Crew destroyed bug nest";
                case ContractProgressionEventKind.LguScavengerTreated:
                    return "Crew treated scavenger";
                case ContractProgressionEventKind.LguScavengerRecovered:
                    return "Crew carried scavenger";
                case ContractProgressionEventKind.LguRitualItemPlaced:
                    return $"Crew placed {FormatCount(count, "ritual item")}";
                case ContractProgressionEventKind.LguDemonIdentified:
                    return "Crew identified demon";
                case ContractProgressionEventKind.LguWireCut:
                    return $"Crew cut {FormatCount(count, "device wire")}";
                case ContractProgressionEventKind.LguDeviceDefused:
                    return "Crew defused the device";
                default:
                    return string.Empty;
            }
        }

        /// <summary>
        /// The count-independent key credits merge on: the line as it reads for a single act. Two
        /// kinds that answer the same key share one line (the two Pest Control captures, the
        /// breaker and its full restore); the audit lever answers a different key and keeps its
        /// own line.
        /// </summary>
        internal static string MergeKey(ContractProgressionEventKind kind, bool auditLever)
        {
            return DescribeAct(kind, auditLever, 1);
        }

        /// <summary>
        /// Kinds whose repeat count reads inside the sentence ("Restored 2 breaker boxes"), so the
        /// render must not append " xN" on top of it. The Whistleblower sighting and the
        /// incinerator repair are absent on purpose - Lawson kept those on a trailing count - and
        /// wave survival carries its milestone on its own high-water path.
        /// </summary>
        internal static bool CountIsInsideLabel(ContractProgressionEventKind kind)
        {
            switch (kind)
            {
                case ContractProgressionEventKind.BlackoutBreakerRestored:
                case ContractProgressionEventKind.BlackoutAuditRestored:
                case ContractProgressionEventKind.SurveyDronePlaced:
                case ContractProgressionEventKind.DefuseCorrectWire:
                case ContractProgressionEventKind.PestControlCompleted:
                case ContractProgressionEventKind.PestControlCapture:
                case ContractProgressionEventKind.LguHoardingBugExterminated:
                case ContractProgressionEventKind.LguRitualItemPlaced:
                case ContractProgressionEventKind.LguWireCut:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// #794: the full blackout restore that follows the last breaker joins that line for its
        /// XP but is not another act of it, so it must not raise the line's count.
        /// </summary>
        internal static bool IsCompletionMarker(ContractProgressionEventKind kind)
        {
            return kind == ContractProgressionEventKind.BlackoutAuditRestored;
        }

        /// <summary>#794: the audit lever is not a breaker, so its restore reads as its own act.</summary>
        internal static bool IsAuditLeverSource(string source)
        {
            return !string.IsNullOrWhiteSpace(source)
                && source.IndexOf("lever", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// One rendered act line. No trailing period (#1202); the trailing repeat count is added
        /// only for the kinds that do not carry it inside the sentence. An unnamed kind renders
        /// nothing, so the caller writes no line at all.
        /// </summary>
        internal static string BuildActLine(
            string bullet, ContractProgressionEventKind labelKind, bool auditLever, int count, bool countIsInLabel)
        {
            string label = DescribeAct(labelKind, auditLever, count);
            if (string.IsNullOrWhiteSpace(label))
                return string.Empty;

            return !countIsInLabel && count > 1
                ? $"{bullet} {label} x{Math.Max(1, count).ToString(CultureInfo.InvariantCulture)}"
                : $"{bullet} {label}";
        }

        /// <summary>The Payload pilot line, which counts seconds rather than acts.</summary>
        internal static string BuildPayloadLine(string bullet, string duration)
        {
            return $"{bullet} Piloted the cart {duration}";
        }

        internal static string FormatCount(int count, string noun)
        {
            count = Math.Max(0, count);
            return $"{count.ToString(CultureInfo.InvariantCulture)} {noun}{Plural(count)}";
        }

        // "box" pluralises with -es, so the breaker line cannot go through FormatCount.
        private static string BreakerRestoredLabel(int count)
        {
            return count <= 1
                ? "Restored the breaker box"
                : $"Restored {Math.Max(1, count).ToString(CultureInfo.InvariantCulture)} breaker boxes";
        }

        private static string Plural(int count)
        {
            return Math.Abs(count) == 1 ? string.Empty : "s";
        }
    }
}
