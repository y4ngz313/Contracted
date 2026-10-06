using System;

namespace Y4NGZCompany.Contracts._Shared
{
    // Unity-free layout and flight policy. The live card supplies TMP's measured first objective height.
    internal static class ContractHudArrival
    {
        internal const float IntroWidth = 600f;
        internal const float IntroHeight = 190f;
        internal const float IntroScale = 1.35f;
        internal const float WipeSeconds = .30f;
        internal const float HoldSeconds = .65f;
        internal const float FlightSeconds = 1.2f;
        // Visibility follows the real docked state, not elapsed animation time. Callers
        // supply their own section availability; measurement never depends on this gate.
        internal static bool ContentVisible(bool isSettled, bool available = true)
            => isSettled && available;

        internal static float DockedHeight(float bodyHeight, float minimum, float topPadding,
            float titleHeight, float titleGap, float bottomPadding, float powerFaultHeight, float sideHeight)
            => ArrivalDestinationHeight(Math.Max(minimum,
                topPadding + titleHeight + titleGap + bodyHeight + bottomPadding), powerFaultHeight, sideHeight);

        // Keep the measured first objective while sections appear or clear during arrival.
        internal static float ArrivalDestinationHeight(float measuredBaseHeight, float powerFaultHeight, float sideHeight)
            => measuredBaseHeight + powerFaultHeight + sideHeight;

        internal static ContractHudArrivalGeometry Flight(in ContractHudArrivalGeometry start,
            in ContractHudArrivalGeometry end, float elapsed)
        {
            float normalized = Math.Max(0f, Math.Min(1f, elapsed / FlightSeconds));
            float t = normalized * normalized * (3f - 2f * normalized);
            return new ContractHudArrivalGeometry(
                Lerp(start.X, end.X, t), Lerp(start.Y, end.Y, t),
                Lerp(start.Scale, end.Scale, t),
                Lerp(start.Width, end.Width, t), Lerp(start.Height, end.Height, t),
                Lerp(start.HeaderPose, end.HeaderPose, t));
        }

        private static float Lerp(float start, float end, float t) => start + (end - start) * t;
    }

    // The live card stores this value, so re-showing and docking use the same
    // Unity-free transitions exercised by the arrival regression rows.
    internal readonly struct ContractHudArrivalState
    {
        internal bool IsSettled { get; }

        private ContractHudArrivalState(bool isSettled) => IsSettled = isSettled;

        internal ContractHudArrivalState Begin() => new ContractHudArrivalState(false);
        internal ContractHudArrivalState Dock() => new ContractHudArrivalState(true);
    }

    internal readonly struct ContractHudArrivalGeometry
    {
        internal readonly float X;
        internal readonly float Y;
        internal readonly float Scale;
        internal readonly float Width;
        internal readonly float Height;
        internal readonly float HeaderPose;

        internal ContractHudArrivalGeometry(float x, float y, float scale, float width, float height, float headerPose)
        {
            X = x;
            Y = y;
            Scale = scale;
            Width = width;
            Height = height;
            HeaderPose = headerPose;
        }
    }
}
