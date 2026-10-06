using UnityEngine;

namespace Y4NGZCompany.Escalations.Bundy.AI;

/// <summary>
/// Job 3b -- the world-noise bus vocabulary.
///
/// Before this, Bundy's only inputs were line of sight, a 14m <c>HearingRadiusMeters</c> that also
/// demanded <c>noiseLoudness &gt;= 0.55</c>, and the escalation pings. The locked design says "noise
/// feeds him", but in practice nothing in the world reached him: a player could flip a breaker,
/// blow a door, set off a turret and call it in on the walkie from 30m away and Bundy learned
/// exactly nothing. D4 (the ship teleporter) was the single exception, and it was written as a
/// one-off.
///
/// Each entry pairs a *meaning* with the two things the bus needs to reason about it: how far the
/// sound carries, and how much the position can be trusted. Confidence is not loudness -- a walkie
/// transmission is audible everywhere but says almost nothing about where the speaker is standing,
/// while a breaker lever is quiet but pins them to a specific wall.
/// </summary>
internal enum BundyWorldNoise
{
    ShipTeleporter = 0,
    BreakerFlip = 1,
    BigDoorOpen = 2,
    ShipHorn = 3,
    TurretFire = 4,
    TerminalUse = 5,
    ScanPing = 6,
    WalkieTransmit = 7,
    // Ours-systems events (3b follow-up). These need no Harmony patch: the systems below are ours
    // and call the bus directly. Run 9 showed the vanilla hooks alone starve the bus in a real
    // round -- the only event that fired all run was a scan ping.
    CctvStationUse = 8,
    ShipTurretFire = 9,
    BatteryRestore = 10,
    BatteryRepair = 11,
}

internal static class BundyWorldNoiseCatalog
{
    /// <summary>
    /// Number of distinct meanings. Sizes the per-event rate-limit table on the AI, so adding an
    /// event to the enum above must be reflected here or the new event simply goes unthrottled.
    /// </summary>
    internal const int MeaningCount = 12;

    /// <summary>
    /// Metres the event carries. <see cref="float.PositiveInfinity"/> means map-wide -- reserved for
    /// events every player in the game can hear regardless of where they are standing.
    /// </summary>
    internal static float GetAudibleRadius(BundyWorldNoise meaning)
    {
        switch (meaning)
        {
            // The beam is the loudest, least ambiguous event in the game and everyone knows it.
            case BundyWorldNoise.ShipTeleporter: return float.PositiveInfinity;
            // The horn is deliberately map-wide: it exists to be heard from the facility.
            case BundyWorldNoise.ShipHorn: return float.PositiveInfinity;
            // Radio, not sound. It reaches him because he is listening to the channel, not the room.
            case BundyWorldNoise.WalkieTransmit: return float.PositiveInfinity;
            // Sustained automatic fire, echoing down corridors.
            case BundyWorldNoise.TurretFire: return 60f;
            // A heavy industrial lever with a power-cycle thump behind it.
            case BundyWorldNoise.BreakerFlip: return 45f;
            // Hydraulics and a slam.
            case BundyWorldNoise.BigDoorOpen: return 40f;
            // Keyboard and speaker chatter inside the ship.
            case BundyWorldNoise.TerminalUse: return 30f;
            // A short pulse, easy to miss.
            case BundyWorldNoise.ScanPing: return 35f;
            // A single loud report from the ship's roof gun.
            case BundyWorldNoise.ShipTurretFire: return 60f;
            // The whole ship slams back to life -- the restore clip is ship-audible by design.
            case BundyWorldNoise.BatteryRestore: return 50f;
            // The repair clank plays with a 30m max distance; the radius follows the actual clip.
            case BundyWorldNoise.BatteryRepair: return 35f;
            // Chair, buttons and mainframe chatter -- the terminal's neighbour.
            case BundyWorldNoise.CctvStationUse: return 30f;
            default: return 30f;
        }
    }

    /// <summary>
    /// How much the event's position can be trusted as the target's position, 0..1. At or above the
    /// live threshold the contact grants live tracking; below it the contact only buys commitment
    /// (see the D5 contact-quality split), and the position is jittered in proportion to the doubt.
    /// </summary>
    internal static float GetConfidence(BundyWorldNoise meaning)
    {
        switch (meaning)
        {
            // They are standing on the pad. This is as certain as sight.
            case BundyWorldNoise.ShipTeleporter: return 1.0f;
            // Both require standing at a fixed piece of geometry and holding still to use it.
            case BundyWorldNoise.ShipHorn: return 0.95f;
            case BundyWorldNoise.TerminalUse: return 0.9f;
            // Seated at the console -- the terminal argument, if anything stronger. The turret event
            // reports the OPERATOR's chair, not the muzzle: a shot from the roof gun means someone
            // is in that chair at this exact moment.
            case BundyWorldNoise.CctvStationUse: return 0.9f;
            case BundyWorldNoise.ShipTurretFire: return 0.9f;
            // Held still through the repair hold, and about to bend down for the repaired cell.
            case BundyWorldNoise.BatteryRepair: return 0.85f;
            // At the socket for the dock, but it is dock-and-go -- the breaker argument.
            case BundyWorldNoise.BatteryRestore: return 0.8f;
            // At the lever, but it is a flick -- they can already be moving as the power cycles.
            case BundyWorldNoise.BreakerFlip: return 0.75f;
            // They opened it to go through it, so they are near the frame but not stationary.
            case BundyWorldNoise.BigDoorOpen: return 0.65f;
            // The turret's position is known exactly; the player's only roughly, and they are running.
            case BundyWorldNoise.TurretFire: return 0.45f;
            // Fires from wherever the player is, but they are mid-stride and it is over instantly.
            case BundyWorldNoise.ScanPing: return 0.5f;
            // Audible everywhere, informative nowhere. Deliberately the weakest contact on the bus.
            case BundyWorldNoise.WalkieTransmit: return 0.2f;
            default: return 0.5f;
        }
    }

    internal static string GetLabel(BundyWorldNoise meaning)
    {
        switch (meaning)
        {
            case BundyWorldNoise.ShipTeleporter: return "ship teleporter";
            case BundyWorldNoise.BreakerFlip: return "breaker flip";
            case BundyWorldNoise.BigDoorOpen: return "big door";
            case BundyWorldNoise.ShipHorn: return "ship horn";
            case BundyWorldNoise.TurretFire: return "turret fire";
            case BundyWorldNoise.TerminalUse: return "terminal";
            case BundyWorldNoise.ScanPing: return "scan ping";
            case BundyWorldNoise.WalkieTransmit: return "walkie";
            case BundyWorldNoise.CctvStationUse: return "cctv station";
            case BundyWorldNoise.ShipTurretFire: return "ship turret";
            case BundyWorldNoise.BatteryRestore: return "battery restore";
            case BundyWorldNoise.BatteryRepair: return "battery repair";
            default: return "world noise";
        }
    }
}
