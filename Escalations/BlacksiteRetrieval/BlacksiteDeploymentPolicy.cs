using UnityEngine;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal enum BlacksiteDeploymentDuty : byte { Raider, ShipGuard }
    internal static class BlacksiteDeploymentPolicy
    {
        // Keep one raider in a tiny configured squad; casualties never change assignments.
        internal static int GuardCount(int members) => members <= 1 ? 0 : members <= 3 ? 1 : 2;
        internal static BlacksiteDeploymentDuty Duty(int index, int members) =>
            index >= members - GuardCount(members) ? BlacksiteDeploymentDuty.ShipGuard : BlacksiteDeploymentDuty.Raider;
        // Spawn-state equipment codes (BlacksiteSoldierSpawnState.Equipment): 1 baton raider, 2 rifle guard.
        internal const byte BatonEquipment = 1;
        internal const byte RifleEquipment = 2;
        // #936: the rifle exists only while the Better Armory / Y4NGZUpgrades pack is loaded. Without
        // it a guard would fight with a weapon the game does not have, so the whole squad carries
        // batons and nobody is a rifle guard (IsRifleGuard reads this code, so the guard duty goes
        // with the rifle). Pure so the tier 2/3 regression pins it.
        internal static byte Equipment(BlacksiteDeploymentDuty duty, bool rifleAvailable) =>
            duty == BlacksiteDeploymentDuty.ShipGuard && rifleAvailable ? RifleEquipment : BatonEquipment;
        internal static bool CanClaimObjective(bool objectiveOutside, bool enteredFacility) => !objectiveOutside || enteredFacility;
        // #874 Q6: the leash's one value lives on the Unity-free patrol policy so the headless patrol
        // suite reads the number the planner proves waypoints against.
        internal const float GuardLeash = BlacksitePatrolPolicy.LeashMetres;
        // #1293 R1: the guard's POST - where a ship guard may walk (LeashedGoal, the planner, player
        // response, combat movement). It is never a perception or target gate: a guard knows and
        // shoots at what the squad's world model holds within his weapon's reach of himself.
        internal static bool CanGuardPursue(bool targetOutside, float distanceFromShip) => targetOutside && distanceFromShip <= GuardLeash;
        // #874 P-S1: the leash is a property of the Guard DUTY, never of the member. A guard given
        // Rally (the Board order), Egress, a response or any other duty walks that duty's goal.
        internal static bool LeashesDuty(BlacksiteJobKind kind) => kind == BlacksiteJobKind.Guard;
        // #874 Q6 (F9): the leash is a ROUTE test, not a snap. ClampGuardGoal pulled any Guard goal
        // beyond 10 m straight back onto the 3.5 m ring - a point nobody had proved standable, on a
        // ring the cargo was staged on. Now a leashed goal whose navmesh route from the rendezvous
        // (routeMetres; negative = no complete route) is outside the leash is refused, and the duty
        // walks the member's own current patrol waypoint instead, which the loop proof already
        // measured on the same route test. Every other duty, and every member that is not an outside
        // ship guard, walks its goal unchanged. Idempotent: a waypoint is inside the leash.
        internal static Vector3 LeashedGoal(BlacksiteJobKind kind, bool outsideShipGuard, float routeMetres,
            Vector3 goal, Vector3 waypoint)
            => outsideShipGuard && LeashesDuty(kind) && !BlacksitePatrolPolicy.InsideLeash(routeMetres) ? waypoint : goal;
    }
}
