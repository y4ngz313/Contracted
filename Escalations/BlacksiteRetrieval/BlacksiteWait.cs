using System;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #1219 A5: every wait that can hold a Blacksite round, by name. A wait is an owner holding
    /// something until a predicate becomes true. Each one has a finite deadline and a named
    /// fallback verdict that runs when the deadline passes, so no single predicate that cannot
    /// become true can freeze the mission (2026-09-26 playtest, rounds 2 and 3).
    /// A hold that is not listed here is a defect: tests/BlacksiteLivenessChecks scans the owners
    /// for unregistered holds. The value is printed, never serialised. #1233 S4: the hover station
    /// and boarding holds are not waits; they end on live presence and the ledger's verdict.
    /// #1293 R2: nor are the lift-off veto and the ship FIFO's holder and waiters - each ends on
    /// its own event or on the progress ledger's answer to a member who stopped progressing - so
    /// the ship's own landing bound is the one wait left.
    /// </summary>
    internal enum BlacksiteWaitKind : byte
    {
        /// <summary>The pickup request waits for the ship to be on station with its rope offered.</summary>
        ShipReady,
    }

    /// <summary>
    /// #1219 A5: one open wait - which wait, when it opened and when it expires. Unity-free: the
    /// owner supplies the clock and prints the two lines through
    /// <c>BlacksiteDiagnostics.Log("Wait", ...)</c>. <c>default</c> is "no wait open".
    /// </summary>
    internal readonly struct BlacksiteWait
    {
        internal readonly BlacksiteWaitKind Kind;
        internal readonly float StartedAt;
        internal readonly float Deadline;
        private readonly bool _open;

        private BlacksiteWait(BlacksiteWaitKind kind, float startedAt, float deadline)
        {
            Kind = kind; StartedAt = startedAt; Deadline = deadline; _open = true;
        }

        internal bool IsOpen => _open;
        internal float Seconds => Deadline - StartedAt;
        internal bool Expired(float now) => _open && now >= Deadline;
        internal float Remaining(float now) => _open ? Math.Max(0f, Deadline - now) : 0f;

        /// <summary>
        /// Opens <paramref name="kind"/> for <paramref name="seconds"/>. A bound that is not a
        /// finite positive number is refused: an unbounded wait is exactly what this record exists
        /// to forbid, so it fails where it is opened rather than in a playtest.
        /// </summary>
        internal static BlacksiteWait Open(BlacksiteWaitKind kind, float now, float seconds)
        {
            if (float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds <= 0f)
                throw new ArgumentOutOfRangeException(nameof(seconds), seconds,
                    "a Blacksite wait needs a finite positive deadline: " + BlacksiteWaits.Token(kind));
            return new BlacksiteWait(kind, now, now + seconds);
        }

        /// <summary>The <c>[UAT][Wait]</c> open line. <paramref name="detail"/> is appended as-is.</summary>
        internal string OpenLine(string detail = null)
            => $"event=open, owner={BlacksiteWaits.Owner(Kind)}, wait={BlacksiteWaits.Token(Kind)}, "
               + $"predicate={BlacksiteWaits.Predicate(Kind)}, seconds={Seconds:0.0}, deadline={Deadline:0.00}, "
               + $"fallback={BlacksiteWaits.Fallback(Kind)}" + Suffix(detail);

        /// <summary>The <c>[UAT][Wait]</c> expiry line, written once, when the fallback runs.</summary>
        internal string ExpiredLine(float now, string detail = null)
            => $"event=expired, owner={BlacksiteWaits.Owner(Kind)}, wait={BlacksiteWaits.Token(Kind)}, "
               + $"predicate={BlacksiteWaits.Predicate(Kind)}, waited={now - StartedAt:0.0}, "
               + $"fallback={BlacksiteWaits.Fallback(Kind)}" + Suffix(detail);

        private static string Suffix(string detail) => string.IsNullOrEmpty(detail) ? string.Empty : ", " + detail;
    }

    /// <summary>
    /// #1219 A5: the catalogue of waits - who owns each, what it waits for and what its expiry
    /// does. The printed tokens are the <c>[UAT][Wait]</c> line contract.
    /// </summary>
    internal static class BlacksiteWaits
    {
        internal static readonly BlacksiteWaitKind[] All =
            (BlacksiteWaitKind[])Enum.GetValues(typeof(BlacksiteWaitKind));

        internal static string Token(BlacksiteWaitKind kind)
        {
            switch (kind)
            {
                case BlacksiteWaitKind.ShipReady: return "shipReady";
                default: return "unknown";
            }
        }

        internal static string Owner(BlacksiteWaitKind kind)
        {
            switch (kind)
            {
                case BlacksiteWaitKind.ShipReady: return "extractionSequence";
                default: return "unknown";
            }
        }

        internal static string Predicate(BlacksiteWaitKind kind)
        {
            switch (kind)
            {
                case BlacksiteWaitKind.ShipReady: return "onStationWithRope";
                default: return "unknown";
            }
        }

        internal static string Fallback(BlacksiteWaitKind kind)
        {
            switch (kind)
            {
                case BlacksiteWaitKind.ShipReady: return "forceShipReady";
                default: return "unknown";
            }
        }

        /// <summary>
        /// #1219 A5: the bound each wait is opened with, read from its owner's constant.
        /// <c>0</c> (never a valid bound) for an unknown kind.
        /// </summary>
        internal static float MaxSeconds(BlacksiteWaitKind kind)
        {
            switch (kind)
            {
                case BlacksiteWaitKind.ShipReady: return BlacksiteExtractionSequence.ShipLandingTimeoutSeconds;
                default: return 0f;
            }
        }
    }
}
