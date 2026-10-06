using UnityEngine;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // #873: what the landing search proved about a site's ground access, ship-local so the ship can
    // replicate it as plain values (the controller holds the NetworkVariables; this file stays free
    // of netcode so the Unity fixture can copy it). The drop landing is the moon ground beyond the
    // lip; the ladder mount is the ground beside the validated ladder and the top its catwalk exit.
    internal struct BlacksiteLandingAccess
    {
        // Server-only retained physical corridor; existing network fields still own deck access.
        internal Vector3[] GroundRoute;
        internal bool Hover;
        internal Vector3 DropLandingLocal, LadderMountLocal, LadderTopLocal;
        // Index into BlacksiteLandingGearPolicy.Ladders; -1 when no ladder foot validated.
        internal int LadderIndex;
        internal float DropHeight, FootToDeck;
        // #1233 S7: the transit altitude BlacksiteFlightCorridor proved over this pose; every flight
        // to and from it climbs to exactly this. Server-only, like GroundRoute: clients see the hull move.
        internal float Altitude;
        internal bool HasLadder => Hover || (LadderIndex >= 0 && LadderIndex < BlacksiteLandingGearPolicy.Ladders.Length);
        internal string LadderSide => Hover ? "rappel" : HasLadder ? BlacksiteLandingGearPolicy.Ladders[LadderIndex].Side : "none";
        internal float LadderOutward => !Hover && HasLadder ? BlacksiteLandingGearPolicy.Ladders[LadderIndex].Outward : 1f;
    }

    // #1305: the sealed preflight landing plan. Made once by BlacksiteLandingSearch, carried by the
    // mission and the ship, published at touchdown without re-judging.
    internal readonly struct BlacksiteLandingPlan
    {
        // The hull origin: the ground pose, or the hover air pose.
        internal readonly Vector3 Origin;
        internal readonly Quaternion Rotation;
        // Hover is the mode; GroundRoute is the proven two-way route; Altitude the corridor altitude.
        internal readonly BlacksiteLandingAccess Access;
        // The proven staging point next to the foot: the ground exit or the hover step-off.
        internal readonly Vector3 Exit;
        // Exactly `members` slots: ground's proven TryEgressSlots; hover's best effort, padded with Exit.
        internal readonly Vector3[] Egress;
        // False only for a hover whose air pose had no clear corridor rung (it flies the best-effort one).
        internal readonly bool CorridorClear;
        internal BlacksiteLandingPlan(Vector3 origin, Quaternion rotation, in BlacksiteLandingAccess access, Vector3 exit, Vector3[] egress, bool corridorClear)
        { Origin = origin; Rotation = rotation; Access = access; Exit = exit; Egress = egress; CorridorClear = corridorClear; }
        // The world deployment foot: the ground drop landing or the hover rappel foot.
        internal Vector3 Foot => Origin + Rotation * Access.DropLandingLocal;
        internal bool Hover => Access.Hover;
    }
}
