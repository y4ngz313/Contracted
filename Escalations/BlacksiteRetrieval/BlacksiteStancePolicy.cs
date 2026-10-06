using System;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// Who owns the soldier's hands this frame. Resolved on every peer from replicated state by
    /// <c>BlacksiteSoldierAI.CurrentHandOwner</c>; Weapon is the only owner that may aim or gesture.
    /// </summary>
    internal enum BlacksiteHandOwner { Weapon, Cargo, Releasing, Traversal, Incapacitated }

    /// <summary>Why the stance owner chose the stance it did; printed by <c>[UAT][Stance]</c>.</summary>
    internal enum BlacksiteStanceReason
    {
        // The rifle is in the hands and nothing is being engaged: the lowered rest stance.
        Rest,
        // A target the member intends to engage: raised.
        Engaging,
        // A fire, reload or grenade action still running after the intent lapsed: raised.
        WeaponAction,
        // Something is carried. The legs stay on the locomotion tree and the Carry layer owns the
        // arms; RestToSlung is a full-body held frame and would freeze a walking carrier.
        Cargo,
        // The carry handback window: legs on the tree while the Carry layer blends out.
        Releasing,
        // An authored rung, rope or jump state owns the whole body; the rifle is slung.
        TraversalState,
        // Walking to or away from a ladder (or a climb the controller cannot present): the legs are
        // on the locomotion substitute, so nothing is slung and nothing is raised.
        TraversalWalk,
        // Stunned: slung. Boarded, captured or dead: not slung; a seated guard stands in StandingPatrol (RifleIdle).
        Incapacitated,
    }

    /// <summary>The two stance parameters, and the reason for them.</summary>
    internal readonly struct BlacksiteStance
    {
        internal readonly bool Aiming;
        internal readonly bool HandsUnavailable;
        internal readonly BlacksiteStanceReason Reason;

        internal BlacksiteStance(bool aiming, bool handsUnavailable, BlacksiteStanceReason reason)
        { Aiming = aiming; HandsUnavailable = handsUnavailable; Reason = reason; }
    }

    /// <summary>#874 (2026-09-25): what a live non-combat facing request does this frame.</summary>
    internal enum BlacksiteIdleFacingMode { Settle, TurnNow }

    /// <summary>
    /// #874 Q7 (E2): the soldier's stance - rest, aimed or slung - from retained intent, one rule for
    /// every peer. <c>BlacksiteSoldierAI.ApplyStance</c> is its only caller and the only writer of the
    /// animator's <c>Aiming</c> and <c>HandsUnavailable</c>; the controller's RestToAim, AimToRest,
    /// RestToSlung and SlungToRest states present the change. Unity-free so the headless suite pins
    /// every (intent, hand owner, action) combination.
    /// </summary>
    internal static class BlacksiteStancePolicy
    {
        /// <summary>
        /// The candidate controller's rest-stance states (builder <c>BuildRestStance</c>). A bound
        /// controller without them is the pre-Q7 bundle: the parameters are still written and it
        /// simply ignores <c>HandsUnavailable</c>.
        /// </summary>
        internal static readonly string[] RestStanceStateNames =
            { "RestToAim", "AimToRest", "RestToSlung", "SlungToRest", "RestTurn90", "RestTurn180", "RestIdleVariant" };

        /// <summary>
        /// Every base-layer state whose rest/aimed presentation belongs to the stance owner: the two
        /// standing trees and the rest-stance states. Nothing else may cut into or out of these for a
        /// stance change.
        /// </summary>
        internal static readonly string[] StanceOwnedStateNames =
            { "StandingPatrol", "StandingAimed", "RestToAim", "AimToRest", "RestToSlung", "SlungToRest", "RestTurn90", "RestTurn180", "RestIdleVariant" };

        /// <param name="targetIntent">The host's replicated aiming bit: a target the member intends to engage.</param>
        /// <param name="weaponAction">A fire, recovery, reload or grenade action is running.</param>
        /// <param name="owner">The resolved hand owner.</param>
        /// <param name="traversalOnLocomotion">For a Traversal owner: the legs are on the locomotion
        /// tree (approach, dismount, an unauthored climb) rather than an authored traversal state.</param>
        /// <param name="stunned">For an Incapacitated owner: stunned is the one case that slings.</param>
        internal static BlacksiteStance Resolve(bool targetIntent, bool weaponAction, BlacksiteHandOwner owner, bool traversalOnLocomotion, bool stunned)
        {
            switch (owner)
            {
                case BlacksiteHandOwner.Weapon:
                    if (targetIntent) return new BlacksiteStance(true, false, BlacksiteStanceReason.Engaging);
                    if (weaponAction) return new BlacksiteStance(true, false, BlacksiteStanceReason.WeaponAction);
                    return new BlacksiteStance(false, false, BlacksiteStanceReason.Rest);
                case BlacksiteHandOwner.Cargo:
                    return new BlacksiteStance(false, false, BlacksiteStanceReason.Cargo);
                case BlacksiteHandOwner.Releasing:
                    return new BlacksiteStance(false, false, BlacksiteStanceReason.Releasing);
                case BlacksiteHandOwner.Traversal:
                    return traversalOnLocomotion
                        ? new BlacksiteStance(false, false, BlacksiteStanceReason.TraversalWalk)
                        : new BlacksiteStance(false, true, BlacksiteStanceReason.TraversalState);
                default:
                    return new BlacksiteStance(false, stunned, BlacksiteStanceReason.Incapacitated);
            }
        }

        /// <summary>
        /// The body is inside the yaw error an in-place turn needs; below it the member is facing and
        /// <c>TurnAngle</c> is cleared (below every exit threshold, the smallest being 20/180).
        /// </summary>
        internal const float TurnDeadZoneDegrees = 10f;

        /// <summary>
        /// The controller's rest-turn thresholds on the normalised <c>TurnAngle</c> (builder
        /// <c>BuildRestStance</c>): RestTurn90 above the first, RestTurn180 above the second.
        /// </summary>
        internal const float RestTurn90Threshold = .44f;
        internal const float RestTurn180Threshold = .78f;

        /// <summary>
        /// The aimed and crouched turn thresholds: the builder's degree thresholds (80 in, 20 out,
        /// 140 for CrouchTurn180) divided by 180 by <c>BlacksiteTierOneBuild</c>, which fails the build
        /// if one survives in degrees. Turn45Left and CrouchTurnLeft enter below the negative one.
        /// </summary>
        internal const float AimedTurnThreshold = 80f / 180f;
        internal const float AimedTurnExitThreshold = 20f / 180f;

        /// <summary>
        /// The <c>TurnAngle</c> for this frame: the yaw error of an in-place turn over 180 degrees,
        /// for every stance whose base state has turn clips - the rest turns out of StandingPatrol
        /// and the aimed/crouched turns out of StandingAimed and CrouchedLocomotion - and on every
        /// controller, since the parameter is a plain float on both bundles.
        ///
        /// The sign is the one each clip set is keyed on, and the two sets disagree. The aimed and
        /// crouched sets use Unity's sign, positive = clockwise from above (a right turn): Turn45Left
        /// enters below -80/180, as the old yaw-rate writer fed it. The rest set's supplied clips
        /// turn LEFT (the "Rifle Turn 90" hips rotate 132.6 degrees counter-clockwise,
        /// clip-inspection 2026-09-24) yet enter on a POSITIVE value. The two sets are read only from
        /// their own base states, and the rest transitions require !Aiming, so the value is mirrored
        /// exactly while the lowered, standing set is the one that can read it (<paramref name="restClipSet"/>:
        /// only on a controller that authors the rest states; the aimed Turn45Left/Right read Unity's sign).
        /// Controller follow-up: key RestTurn90/180 on the negative side like Turn45Left, then drop the mirror.
        /// <paramref name="yawErrorDegrees"/> is Unity's signed angle from the body's forward to the
        /// facing target, positive clockwise.
        ///
        /// The value is the SIZE OF THE TURN, latched from the frame it starts: the body turns at
        /// <see cref="IdleTurnRate"/> (68 deg/s on the rest set, 240 otherwise), so the remaining
        /// error falls through the thresholds during the turn and a remaining-error signal would
        /// select a 90-degree clip for only part of it, or never. It is held until the body is
        /// inside <see cref="TurnDeadZoneDegrees"/>, then cleared. A slung member (incapacitated or
        /// on a traversal state) and a moving one publish 0: every turn transition is authored on
        /// <c>Speed &lt; .05</c>.
        /// </summary>
        internal static float TurnAngle(float latched, float yawErrorDegrees, bool facedInPlace, bool restClipSet,
            bool slung, float normalizedSpeed)
        {
            if (!facedInPlace || slung || float.IsNaN(yawErrorDegrees)
                || normalizedSpeed >= BlacksiteLocomotionSpeedTable.StationaryBlend
                || Math.Abs(yawErrorDegrees) <= TurnDeadZoneDegrees)
                return 0f;
            float value = Math.Max(-1f, Math.Min(1f, yawErrorDegrees / 180f));
            if (restClipSet)
                value = -value;
            bool sameTurn = latched != 0f && Math.Sign(latched) == Math.Sign(value);
            return sameTurn && Math.Abs(latched) >= Math.Abs(value) ? latched : value;
        }

        /// <summary>The lowered, standing clip set - the only one the rest-turn transitions leave.</summary>
        internal static bool IsRestClipSet(in BlacksiteStance stance, bool crouched) => !stance.Aiming && !crouched;

        /// <summary>
        /// The idle facing rate before #874 (2026-09-25), and still the rate of every stance that is
        /// not presented by a rest-turn clip. <c>BlacksiteSoldierAI.FacingTurnDegreesPerSecond</c> is it.
        /// </summary>
        internal const float FacingTurnDegreesPerSecond = 240f;

        /// <summary>
        /// #874 (2026-09-25): the supplied "Rifle Turn 90" clip rotates the hips 133 degrees in
        /// 1.97 s (clip-inspection 2026-09-24), about 68 deg/s. An idle turn the rest set presents
        /// turns the body at this rate, so the feet and the transform agree.
        /// </summary>
        internal const float RestTurnDegreesPerSecond = 68f;

        /// <summary>
        /// #874 (2026-09-25): the idle facing rate. A lowered, standing rifle with the rest states
        /// bound turns at the rest clip's own rate; every other stance keeps the old fast turn,
        /// which its aimed/crouched turn clips or the plain tree were tuned against.
        /// </summary>
        internal static float IdleTurnRate(bool restClipSet)
            => restClipSet ? RestTurnDegreesPerSecond : FacingTurnDegreesPerSecond;

        /// <summary>The animator's float damping. <c>BlacksiteSoldierAI.AnimationFloatDampTime</c> is it.</summary>
        internal const float AnimationFloatDampSeconds = .12f;

        /// <summary>
        /// #874 (2026-09-25): how long a STANDING member's planar speed stays under StationaryBlend
        /// before an idle turn starts: twice the float damping, so the damped Speed every rest-turn
        /// transition is authored on (<c>Speed &lt; .05</c>) is down too when TurnAngle latches.
        /// </summary>
        internal const float IdleTurnSettleSeconds = 2f * AnimationFloatDampSeconds;

        /// <summary>
        /// #874 (2026-09-25): the idle facing rule. A MOVING member (planar speed at or above
        /// StationaryBlend) with a live target turns at once at <see cref="FacingTurnDegreesPerSecond"/>,
        /// exactly as before the settle existed: the settle is only there so a standing body's damped
        /// Speed is down when TurnAngle latches, and a moving body publishes no rest turn anyway. The
        /// callers that reach this while moving are bodies whose agent is stopped while a crossing
        /// motor carries the transform (#874 S4 deleted the entrance hand-walk closer). A STANDING member waits out
        /// <see cref="IdleTurnSettleSeconds"/> of stillness (<paramref name="stillSeconds"/>) and then
        /// turns at <see cref="IdleTurnRate"/>. <paramref name="degreesPerSecond"/> is the rate the turn
        /// runs (or will run, once settled) at.
        /// </summary>
        internal static BlacksiteIdleFacingMode IdleFacingMode(bool moving, float stillSeconds, bool restClipSet,
            out float degreesPerSecond)
        {
            degreesPerSecond = moving ? FacingTurnDegreesPerSecond : IdleTurnRate(restClipSet);
            return moving || stillSeconds >= IdleTurnSettleSeconds ? BlacksiteIdleFacingMode.TurnNow : BlacksiteIdleFacingMode.Settle;
        }

        /// <summary>The slack a facing hold keeps past the turn it was extended for.</summary>
        internal const float FacingHoldMarginSeconds = .1f;

        /// <summary>
        /// #874 (2026-09-25): how long a facing request must still live so a settling or rest-rate
        /// turn completes: the settle still owed, the turn itself at <paramref name="degreesPerSecond"/>,
        /// and <see cref="FacingHoldMarginSeconds"/>. A one-shot request (the 0.5 s stimulus look)
        /// otherwise expires 18 degrees into a lowered rifle's turn.
        /// </summary>
        internal static float FacingHoldFor(float remainingSettleSeconds, float absYawDegrees, float degreesPerSecond)
            => Math.Max(0f, remainingSettleSeconds) + Math.Abs(absYawDegrees) / Math.Max(1f, degreesPerSecond)
               + FacingHoldMarginSeconds;

        /// <summary>
        /// The smallest look-around a patrolling guard takes: 5 degrees past the RestTurn90 entry
        /// (<see cref="RestTurn90Threshold"/> x 180 = 79.2), so the latched <see cref="TurnAngle"/>
        /// clears the threshold with margin for the few degrees the body settles after arriving.
        /// </summary>
        internal const float PatrolLookMinimumDegrees = RestTurn90Threshold * 180f + 5f;

        /// <summary>
        /// #874 (2026-09-25): the patrol look-around is ONE deliberate turn. <paramref name="outwardYaw"/>
        /// is Unity's signed yaw from the body's arrival heading to the outward direction (negative =
        /// left); the answer is the signed yaw the guard turns to. The loop runs clockwise from
        /// above, so the outward is normally 45-68 degrees to the LEFT on arrival: the guard turns
        /// further left, to <see cref="PatrolLookMinimumDegrees"/>, looking outward-and-back along
        /// the loop, and the turn is large enough to enter RestTurn90. A left outward already past
        /// that is taken as it is. An outward on the right (one end of a two-point loop) is taken
        /// as it is too: the rest set has no right-turn clip, so that turn is presented by the tree.
        /// Controller follow-up: a mirrored RestTurn90 keyed on the negative side.
        /// </summary>
        internal static float PatrolLookYaw(float outwardYaw)
        {
            if (float.IsNaN(outwardYaw)) return 0f;
            if (outwardYaw >= 0f) return outwardYaw;
            return outwardYaw > -PatrolLookMinimumDegrees ? -PatrolLookMinimumDegrees : outwardYaw;
        }

        /// <summary>
        /// #874 (2026-09-25): how long a guard stands still in StandingPatrol before the
        /// RestIdleVariant clip plays: 4 s plus 0-3 s seeded by the member index, so two guards
        /// standing together do not play it in step.
        /// </summary>
        internal static float IdleVariantAfterSeconds(int seed) => 4f + (uint)seed % 4u;

        /// <summary>
        /// #874 (2026-09-25): the controller authors no entry into RestIdleVariant, so the runtime
        /// cross-fades into it. Only out of StandingPatrol itself, only on the lowered rest set of a
        /// controller that carries the rest states (<paramref name="rest"/>), never slung, and only
        /// once the member has stood still there for <see cref="IdleVariantAfterSeconds"/>.
        /// <see cref="IdleVariantCancel"/> takes it back out.
        /// </summary>
        internal static bool IdleVariantDue(float stillSeconds, int seed, bool inPatrolState, bool rest, bool handsUnavailable)
            => inPatrolState && rest && !handsUnavailable && stillSeconds >= IdleVariantAfterSeconds(seed);

        /// <summary>
        /// #874 (2026-09-25): the controller's only exits from RestIdleVariant are Aiming,
        /// HandsUnavailable and the timed return at the 5.93 s clip's end, so a walk or a turn begun
        /// during the clip would move the body with the legs frozen in it. The runtime cross-fades
        /// back to StandingPatrol when the member is in the variant (<paramref name="inVariantState"/>)
        /// and moving (damped Speed at or above StationaryBlend), turning (TurnAngle live) or off the
        /// standing, alive rest set (<paramref name="rest"/>). An authored exit
        /// (<paramref name="authoredExit"/>: Aiming or HandsUnavailable) is the controller's own and wins.
        /// </summary>
        internal static bool IdleVariantCancel(bool inVariantState, bool moving, bool turning, bool rest, bool authoredExit)
            => inVariantState && !authoredExit && (moving || turning || !rest);

        /// <summary>
        /// #874 Q7 (E1): Signal and Radio are full-body states on the base layer (builder
        /// AddAnyActionTransition), not a masked upper-body layer, so a gesture fired on a moving
        /// member would replace the MoveX/MoveZ tree and stop its legs. A gesture is therefore only
        /// accepted while the member stands, with the rifle in its hands, and a Signal also yields to
        /// an engaged weapon. The locomotion blend never reads a gesture.
        /// </summary>
        internal static bool AllowsGesture(bool signal, bool usesBaton, BlacksiteHandOwner owner, bool engaged, float normalizedSpeed)
            => !usesBaton && owner == BlacksiteHandOwner.Weapon
               && normalizedSpeed < BlacksiteLocomotionSpeedTable.StationaryBlend
               && (!signal || !engaged);
    }
}
