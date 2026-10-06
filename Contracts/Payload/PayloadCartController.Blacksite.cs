using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;
using UnityEngine.AI;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Escalations.BlacksiteRetrieval;

namespace Y4NGZCompany.Contracts.Payload
{
    public partial class PayloadCartController
    {
        private readonly NetworkVariable<ulong> _npcPusher = new NetworkVariable<ulong>(ulong.MaxValue);
        private PayloadPushBodyRig _npcRig;
        private BlacksiteSoldierAI _npcPoseActor;
        private int _npcPusherMember = -1;
        // #874 (2026-09-20): the coupling's whole state, and why the fifteen fields it replaces are
        // gone. The retired haul had the pusher walking his own route while the cart chased a
        // follow point thrown off his yaw, so it needed a field per symptom of the gap between
        // them: two halves of a path gate, the alignment error, the pusher's measured speed and the
        // sample it was differenced from, the follow yaw, an overrun clock, a refusal clock, a
        // pivot veto, a jam latch with a rung counter, a progress stamp, a stamped heading, three
        // more clocks and a brake dwell. On the 2026-09-20 host run all of it stood a cart whose
        // handle moved 0.09 m while its operator walked 7 m, and then let go as jammed.
        //
        // The pair now moves on ONE step - the cart's - so the only facts left are who holds the
        // handle and whether the coupling owns his body. The coupling owns that body while it holds
        // the navigator's payload hold: it is the cart that moves him, and the release restores his
        // agent and his goal (BlacksiteNavigator.EndPayloadCoupling). #1293 R2 / #1305 R23: whether a
        // pair with a plan is getting anywhere is the ledger's (NoteNpcProgressServer). Host-only state; the pusher id
        // is the one replicated fact, exactly as before, and the cart transform replicates as it
        // always has.
        private bool _npcCoupled;
        private BlacksiteSoldierAI _npcCoupledActor;
        // #1305 C3: which predicate refused the operator's last step (None: it proved out), read on the
        // NEXT step so a cart he cannot follow does not advance again. P1 the swept step (its failure
        // kept), P2 no NavMesh within tolerance, P3 the NavMesh height gap, P4 the navigator's carry.
        private enum NpcOperatorRefusal : byte { None, P1, P2, P3, P4 }
        private NpcOperatorRefusal _npcOperatorRefusal;
        private BlacksiteStepFailure _npcOperatorStep;
        // #874 (2026-09-22): where the executor last left the cart, so something ELSE moving it -
        // a player pushing it, a magnet, a warp - is seen as the plan invalidation it is rather
        // than as the pair travelling.
        private Vector3 _npcExecutedPose;
        private bool _npcHasExecutedPose;
        // #1305 C3: where the handle stood when the pair last reported progress. The handle, not
        // the centre: coupled turns pivot about it, so a swing on the spot never reads as travel.
        private Vector3 _npcProgressHandle;
        // How long the pair has stood with nowhere to go, or with the body held by an owner that
        // outranks the coupling. A wait, not a block: a stop, a turn, a door queue or a combat
        // stance may last as long as it lasts and can never release the handle.
        private float _npcWaitingFor;
        // #874 (2026-09-22): what the coupling is doing about the world right now. ONE outcome,
        // owned by the plan: it is either following poses it has proved, computing a fresh set, or
        // reporting that it has none. The retired trio - a quarter-metre reversal counter, a
        // corner-refusal memory and the route owner's corridor latch - each answered a third of
        // this question and could disagree with the other two.
        private enum NpcHaulState : byte { Idle, Searching, Following, Blocked }
        private NpcHaulState _npcHaulState;
        // The pusher's own physical grace, and now the only separation test the coupling keeps.
        // Nothing the coupling does can charge it: one step moves both bodies.
        private float _npcLostHandleFor;
        // 2026-09-16 (WP2 step 4): which of CartPoseClear's four tests refused. Held as one of four
        // interned literals so a refused sub-step allocates nothing; the collider that did it is
        // _motionBlocker, whose name is resolved only when a diagnostic line is actually built.
        private string _npcPoseRefusal;
        // #1219 D3: the cart's one "stuck" verdict (PayloadCartStuckVerdict). Two `blocked`
        // releases from the same pose, half a chassis at most apart, with no cart route proved
        // from that pose (PayloadCartRoute.Prove) latch it for the cart's life. It is scoped to
        // the CART rather than to the member because a fresh operator walked onto the same handle
        // repeats the same refused step exactly - the cycle the 2026-09-16 run repeated seventeen
        // times and 2026-09-26 round 1 ran 19 attempts of. It replaces the 20 s released-pose
        // back-off, which only paced that cycle: two tries, then the mission answers
        // (BlacksiteObjectivePolicy.CartStuckAnswer). Host-only; the record phase the manifest
        // writes from it is what replicates.
        private readonly PayloadCartStuckVerdict _npcStuckVerdict = new PayloadCartStuckVerdict();
        // The route query's caller-owned scratch for that verdict. Its own, not the planner's: a
        // blocked release and a plan search can run on the same fixed step.
        private readonly PayloadCartRouteScratch _npcStuckRouteScratch = new PayloadCartRouteScratch();
        /// <summary>#1219 D3: the cart's stuck verdict is latched; see <see cref="PayloadCartStuckVerdict"/>.</summary>
        internal bool HasNpcStuckVerdict => _npcStuckVerdict.Latched;
        /// <summary>#1305 R23: why the cart has no plan while it reports none (Blocked); null while it has or seeks one.</summary>
        internal string NpcPlanRefusal => _npcHaulState == NpcHaulState.Blocked ? _npcPlanRefusal ?? "none" : null;
        // #874 (2026-09-22): THE PLAN. A retained sequence of cart poses the pair has proved it
        // can reach, each one a handle-anchored swing onto a yaw followed by a signed straight run
        // at that yaw (PayloadCartMotionMath.ResolveCoupledManoeuvre). It is the only thing that
        // steers: the navigator supplies the destination and the world route, and nothing else
        // sends the cart a heading.
        //
        // The retired design took the first corner of the OPERATOR's NavMesh plan the pair could
        // start down and proved half a chassis of advance along it. That can never state that the
        // pair will get THROUGH an opening or AROUND a corner, which is the 2026-09-22 jam: at
        // t=1549.28 the cart was square on its leg (alignment=0.0), its operator was on the handle
        // with a complete route, and the swept advance one half-length ahead overlapped
        // RegularRoomStorageTile. There was no manoeuvre in the vocabulary that could have shifted
        // the chassis sideways through the opening, so the pair reversed 0.25 m twice and let go
        // after 4.02 s. A pose search has that manoeuvre by construction.
        // Sized past the longest chain one search can build - every expansion adds at most one
        // depth level, so a plan is never longer than NpcSearchExpansions - and the truncation
        // below is the defensive case rather than the ordinary one.
        private const int NpcPlanCapacity = 16;
        private readonly Vector3[] _npcPlanPositions = new Vector3[NpcPlanCapacity];
        private readonly float[] _npcPlanYaws = new float[NpcPlanCapacity];
        private readonly float[] _npcPlanRuns = new float[NpcPlanCapacity];
        private int _npcPlanCount, _npcPlanAt;
        private bool _npcHasPlan;
        // The operator goal this plan serves, the point it actually drives to (a horizon point on
        // the world route, or an alternative corridor's waypoint), and which of the two that is.
        private Vector3 _npcPlanGoal, _npcPlanTarget;
        private bool _npcPlanCorridor;
        // Whether the world route this plan was cut from reached the goal, and why the last search
        // failed where it did. Both are diagnostics and release-line facts, not steering.
        private bool _npcRouteComplete;
        private string _npcPlanRefusal;
        // How long the current node has been asked for without the pose measurably closing on it.
        private float _npcNodeStalledFor;
        // THE SEARCH, budgeted across fixed steps. A* over cart poses with the heuristic measured
        // at the HANDLE - it is the operator who has to arrive - and a bounded node/expansion
        // count, so a hopeless opening costs one search rather than one search per frame.
        private const int NpcSearchCapacity = 48;
        private struct NpcPoseNode
        {
            internal Vector3 Position;
            internal float Yaw, Run, Cost, Estimate;
            internal short Parent;
            internal bool Open;
        }
        private readonly NpcPoseNode[] _npcSearchNodes = new NpcPoseNode[NpcSearchCapacity];
        private int _npcSearchCount, _npcSearchExpanded, _npcSearchBest;
        private bool _npcSearching, _npcSearchCorridor;
        private Vector3 _npcSearchTarget, _npcSearchGoal;
        private float _npcSearchRootEstimate, _npcPlanSearchedAt = -10f;
        // The probe the search hands PayloadCartMotionMath, cached so a search allocates no
        // delegate, plus the scratch it needs: how many pose tests are left this fixed step, the
        // expansion's base pose and its wheel mask, and the last sub-pose a probe accepted. The two
        // cached masks are what keep a chained manoeuvre to ONE ground sample per sub-step instead
        // of two.
        private PayloadCartMotionMath.CoupledPoseProbe _npcPlanProbe;
        private PayloadCartMotionMath.CoupledPoseProbe NpcPlanProbe
            => _npcPlanProbe ?? (_npcPlanProbe = NpcPlanProbeServer);
        private int _npcSearchBudget;
        private Vector3 _npcProbeBase, _npcProbeChain;
        private float _npcProbeBaseYaw, _npcProbeChainYaw;
        private int _npcProbeBaseMask, _npcProbeChainMask;
        private bool _npcProbeHasChain;
        // The wheel mask and the seated height the last CartPoseClear resolved for the pose it
        // accepted. Read by the next chained planner probe, so a manoeuvre walking up a ramp asks
        // about each sub-pose from the height the one before it actually settled at.
        private int _npcPoseResultMask = PayloadCartMotionMath.AllWheelsMask;
        private float _npcPoseResultY;
        // The pose the next CartPoseClear sweeps FROM, and the wheel mask its ledge test compares
        // against. Unset while the motor asks about its own next step - the live pose - and set by
        // the planner while it asks about a step from a pose the cart has not reached yet. Fields
        // rather than parameters for the same reason _npcPoseRefusal and _motionBlocker are: the
        // signature is reflected on by BlacksiteLandingCombatChecks and by the Unity physics
        // fixture, and every probe has to go through the one production predicate.
        private bool _npcHasPoseOrigin;
        private Vector3 _npcPoseOrigin;
        private int _npcPoseOriginMask;
        // #1219 D1: the planner's own route-proof memory - the cart's route, not the pusher's.
        // Its own instance, never the stuck verdict's: both may ask in one frame.
        private readonly PayloadCartRouteScratch _npcCartRouteScratch = new PayloadCartRouteScratch();
        // #874/#1054, migrated here from the navigator: the ground the PAIR proved it cannot take,
        // the side of it this episode is working along, and the alternative waypoints it has
        // already failed to reach. It lives on the CART because a fresh operator walking onto the
        // same handle meets the same geometry - resetting it at the attach is exactly how the
        // 2026-09-16 run repeated one refused step seventeen times - and it is cleared by the
        // things that make it untrue: its own expiry, a portal crossing, an elevator carry and the
        // pair travelling out of it.
        private Vector3 _npcRefusedGround;
        private float _npcRefusedRadius, _npcRefusedUntil = float.NegativeInfinity;
        private float _npcCorridorSide, _npcCorridorSearchedAt = -10f;
        private const int NpcRefusedWaypointSlots = 3;
        private readonly Vector3[] _npcRefusedWaypoints = new Vector3[NpcRefusedWaypointSlots];
        private int _npcRefusedWaypointCount, _npcRefusedWaypointNext;
        // #874: the operator's swept step ignores squad bodies - exactly as every other hand-walked
        // Blacksite body does (BlacksitePhysicalStep.IgnoreSquadBodies) - and the cart he is
        // holding. The authored handle overhangs the motion box the cart's own sweep uses and
        // stands inside his capsule, so a cart-aware step would refuse every step of the haul; the
        // cart's half of the coupled step is what keeps the CART legal. Cached, so the per-step
        // sweep allocates no delegate.
        private Predicate<Collider> _npcOperatorIgnore;
        private Predicate<Collider> NpcOperatorIgnore
            => _npcOperatorIgnore ?? (_npcOperatorIgnore = IgnoreNpcOperatorCollider);
        private bool IgnoreNpcOperatorCollider(Collider collider)
            => BlacksitePhysicalStep.IsSquadBody(collider)
                || (collider != null && (collider.transform == transform || collider.transform.IsChildOf(transform)));
        // The rate-limit key for the blocked line, built once rather than concatenated per blocked
        // step: the line itself is rate limited, the key it is limited by was not.
        private string _npcBlockedLogKey;
        private string NpcBlockedLogKey
            => _npcBlockedLogKey ?? (_npcBlockedLogKey = "cart-coupled-" + NetworkObjectId);
        // #874 (2026-09-22): how close the pair has ever come to the node it is executing, in
        // metres and in degrees. Monotone best-so-far, reset when the node changes, so a pair
        // oscillating about that pose cannot keep setting new minima. It feeds the plan's stall
        // replan only; the ledger hears the handle travel (#1305 C3).
        private float _npcNodeBestDistance = float.MaxValue;
        private float _npcNodeBestYawError = float.MaxValue;
        internal float NpcSlopeSpeedScale { get; private set; } = 1f;
        private int _npcElevatorFrame = -10;
        internal bool IsNpcAttached => _npcPusher.Value != ulong.MaxValue;
        internal static float NpcPushSpeed => 4.6f * RiskSettings.PushSpeedMultiplier.Value;
        // #874 (2026-09-22): the haul run backwards, on the SAME authored share a player's reverse
        // input gets (PayloadCartController.Motion.cs). A planned reverse is a manoeuvre the pose
        // search proved, not a recovery nudge, so it travels at the cart's own reverse speed rather
        // than at a constant chosen for backing out of a jam.
        internal static float NpcReverseSpeed => NpcPushSpeed * Mathf.Max(.1f, RiskSettings.ReverseInputMultiplier.Value);
        internal Vector3 NpcHandlePosition => transform.position - Quaternion.Euler(0, transform.eulerAngles.y - FollowYawOffset, 0) * Vector3.forward * FollowDistance - Vector3.up * FollowHeight;
        internal bool IsPushedBy(BlacksiteSoldierAI actor) => actor != null && _npcPusher.Value == actor.NetworkObjectId;
        // #874 S1.2: the attach gate is one production value that the approach planner budgets
        // against and the fixture exercises directly, instead of a literal repeated per call site.
        internal const float NpcAttachDistance = .85f;
        // #940 S11 U1 / #874: the physical leash, kept at the reviewed 2.5 m and now measuring one
        // thing only. The coupling moves both bodies on one step, so it cannot produce a gap: a
        // separation this wide means something OUTSIDE it has moved the body - a monster carrying
        // him off, a warp that did not release the cart, a world transfer. The derivation is
        // unchanged: the .85 m attach reach, plus .90 m of lateral body displacement (two .45 m
        // agent radii), plus the .69 m of chase lag the retired follow-point drive could hold.
        internal const float NpcLeashBreakMetres = 2.5f;
        // #940 S11 U1: the cart's authored maximum yaw rate. The follow point that used to slew at
        // half of it is gone with the follow-point drive; the coupling's speed law reads this rate
        // directly (PayloadCartMotionMath.CoupledHaulSpeed), so the advance it projects is the one
        // the motor can actually turn onto within the same step.
        private const float CartTurnRateDegreesPerSecond = 55f;
        // The motor's yaw sub-step for an NPC-pushed cart, and the coarser one the attach pre-aim
        // swings through. Both were unnamed literals at their call sites.
        private const float NpcPivotStepDegrees = 2f;
        private const float NpcPreAimStepDegrees = 10f;
        // #940 S11 U1: the pusher's lost-handle grace. Mirrors the player driver's grace
        // (PayloadCartController.Motion.cs, "_driverLostHandleAt"): one bad frame - a jamb, a
        // squadmate's avoidance push, a frame spike - may not break the link. Accumulated in fixed
        // steps rather than stored as a timestamp because the cart integrates in FixedUpdate: a
        // starved fixed step then buys the link more wall-clock grace, not less.
        internal const float LostHandleGraceSeconds = 1.25f;
        // #874: the speed below which the pair is not travelling. It is the plan's node-stall
        // test, and it is measured on the motion the motor integrated rather than on the speed it
        // was commanded, so an accelerating, slope-capped or wall-grazing pair is never read as a
        // blocked one. 5 cm/s is a fortieth of the haul speed and a millimetre per fixed step.
        private const float NpcCoupledCreepSpeed = .05f;
        // #874: and the rate below which the pair is not CLOSING its alignment either, for the same
        // reason - a swing onto the route closes on the node, a swing the sweep refuses does not. Two
        // degrees a second is a twenty-seventh of the cart's authored turn rate, so even a swing
        // the sweep is accepting a fraction of at a time still reads as closing, while a cart
        // holding or oscillating about its error does not.
        private const float NpcCoupledCreepDegreesPerSecond = 2f;
        // #874: how long the world has to refuse the pair's step before the coupling replans.
        // #1305 C3: a replan is recovery, not progress; letting go is the ledger's (#1293 R2).
        //
        // #874 (2026-09-22): the replan rather than a blind reverse. A pair that
        // has to back out gets a reverse because the pose search proposed one and proved it, not
        // because a counter fired; the search's own reverse primitive is what the retired
        // NpcRecoverReverseMetres ladder was guessing at.
        private const float NpcPlanStallSeconds = 1f;
        // #1219 D3: how close two blocked releases have to be to be "the same pose" for the stuck
        // verdict. Half a chassis length: a cart that moved farther than that - a player pushing
        // it, a haul, a magnet - is standing somewhere its step has not been refused yet.
        private static float NpcStuckSamePoseMetres => MotionHalfExtents.z;
        // #874: how far the operator may be from the handle and still be holding it - the attach
        // reach, reused. A pair whose operator has fallen outside the gate that let him take the
        // handle does not advance until he is back on it, which bounds any divergence the world can
        // produce at the same distance the attach was granted inside.
        private const float NpcCoupleReachMetres = NpcAttachDistance;
        // #874: the operator is on the handle for practical purposes, so no step is taken at all.
        // Two centimetres is inside the 5 cm floor his own swept step would move him anyway.
        private const float NpcOperatorSettledSqrMetres = .0004f;
        // #874: how far the navmesh may be from the ground the operator's step found. A pose with
        // floor under it but no navigation on it is not a place to leave a member: his route, the
        // coupling's own heading query and the walk he resumes on release all come from an agent
        // that has to stay bound. Six centimetres short of a metre is the same tolerance the
        // recovery ladder's rebind probes use.
        private const float NpcOperatorNavTolerance = .6f;
        // #874 (2026-09-22): the pose plan's own measures.
        //
        // The horizon is how far down the world route one plan reaches. Six metres is two rooms'
        // worth of doorway for a 1.4 m chassis-plus-handle and about three seconds of haul, so a
        // clear corridor costs one search per three seconds instead of a NavMesh query per quarter
        // second plus a fresh acceptance every fixed step.
        private const float NpcPlanHorizonMetres = 6f;
        // How close the pair's HANDLE has to come to the point it is steering at for the search to
        // call it reached, and for a goal to be an arrival rather than a heading. Measured at the
        // handle because it is the OPERATOR who has to get there; the chassis leads him by
        // FollowDistance and would otherwise report an arrival 1.4 m early.
        private const float NpcPlanArriveMetres = .5f;
        // One short manoeuvre's run: the cart's own half length, which states "the cart can put
        // its front half down here" rather than choosing a distance. The long run is what a clear
        // leg takes in one node, capped so a single node's sweep still sub-steps at the short run.
        private static float NpcPlanRunMetres => MotionHalfExtents.z;
        private const float NpcPlanLongRunMetres = 4f;
        // The swing one manoeuvre may ask for. Seventy degrees is a doorway turn in two nodes and
        // a full reversal in three; a swing the sweep refuses part way is KEPT at whatever it
        // reached, so the fan does not need a rung per angle.
        private const float NpcPlanTurnDegrees = 70f;
        // How square the cart has to be on the node it is executing before the coupling asks for
        // travel at all. This is "align before you enter, go straight while the opening has you":
        // below it the speed law transmits better than 99.7 per cent of the haul, and above it the
        // pair turns about the handle, which moves neither body through anything.
        private const float NpcPlanAlignDegrees = 4f;
        // How close along its own run axis the cart has to come for a node to be done, and how far
        // something else has to move the cart for the plan to stop describing it.
        private const float NpcPlanNodeReachedMetres = .12f;
        private static float NpcPlanDisplacedMetres => MotionHalfExtents.z;
        // One handle-progress measure, used twice and coupled on purpose (#1305 C3): a partial plan is only
        // installed if it buys this much toward its target (below it the search says it found nothing), so
        // an executed plan always moves the handle far enough for the ledger to hear it. Retune them together.
        private const float NpcPlanProgressMetres = .35f;
        // The search's bounds. Expansions and nodes are the hard stop; the per-step probe budget
        // is what spreads one search over fixed steps instead of spiking a frame. A clear leg is
        // answered by the FIRST candidate of the first expansion, so the quiet path costs about
        // ten pose tests and one fixed step.
        private const int NpcSearchExpansions = 12;
        private const int NpcSearchProbesPerStep = 48;
        // Two nodes this close in position and heading are one node for steering purposes.
        private const float NpcSearchDuplicateMetres = .35f;
        private const float NpcSearchDuplicateDegrees = 20f;
        // What a turn and a reverse cost the search, as metres of equivalent travel. A degree of
        // swing is about six millimetres, so a 70-degree turn costs 0.42 m - real, and far less
        // than the detour that avoids it - and a reverse costs two and a half times its distance,
        // which keeps backing out a last resort without making it unreachable.
        private const float NpcPlanTurnCostMetresPerDegree = .006f;
        private const float NpcPlanReverseCostShare = 2.5f;
        // How far down a leg the refused GROUND is recorded - the cart's own half length ahead of
        // the chassis, which is what the swept probe tests and what the motor's ledge and support
        // tests refuse at.
        private static float NpcRouteProbeMetres => MotionHalfExtents.z;
        // #874/#1054: how much ground ONE refusal puts out of use for the corridor search. The
        // pair's own footprint and no more: the chassis' half diagonal, which is the largest
        // turning circle any pose of it sweeps, plus the operator's body radius, because the man
        // on the handle is half of what was refused.
        private static float NpcRefusedGroundRadius =>
            Mathf.Sqrt(MotionHalfExtents.x * MotionHalfExtents.x + MotionHalfExtents.z * MotionHalfExtents.z)
            + BlacksiteArrivalPolicy.AgentRadius;
        // How long ground the pair refused stays refused for route selection. Twelve seconds for
        // its own reason: it has to outlive the ledger's progress window (#1293 R2), so one blocked
        // episode is planned around rather than re-measured mid-turn, and it has to expire well
        // inside a round, so a door that opens or a body that moves is answered by the world and
        // not by memory.
        private const float NpcRefusedGroundSeconds = 12f;
        // The floor between two corridor fans. One second, a fraction of the ledger's window.
        private const float NpcCorridorSearchSeconds = 1f;
        // How long a pair that has proved it has nowhere to go waits before proving it again: a
        // blocked pair costs two searches a second rather than fifty until the manifest lets it go
        // (#1305 R23). Only a BLOCKED outcome waits: a replan after travel, a new goal or a spent
        // plan starts on the step that asked for it.
        private const float NpcPlanRetrySeconds = .5f;
        // Bearings from the refused ground, laterals first: walking along a face is what gets past
        // it, and the diagonals only exist for a mouth that is not square to it. No backward
        // bearing - a coupled pair backing away from its goal is a reverse manoeuvre inside the
        // pose search, not a corridor.
        private static readonly float[] NpcCorridorBearings = { 90f, -90f, 45f, -45f };
        // How far to one side the goal has to lie before it is evidence about which way to go
        // round. Five centimetres of lateral offset over the whole distance to the goal is
        // arithmetic noise, and a fan that reads a side out of it flips with the rounding.
        private const float NpcCorridorSideBias = .05f;
        /// <summary>
        /// #874/#1054: the cart's read-only admission - "would this cart refuse this member the
        /// handle for something it has already MEASURED?".
        ///
        /// The board asks it before it spends an operator on the walk over (SelectOperator), and
        /// <see cref="TryAttachNpcServer"/> asks it again at the handle, so the two can never
        /// disagree. Deliberately NOT a full attach gate: distance, motion state, delivery and the
        /// magnet are all the acquisition's business and several of them cost physics, while this
        /// is asked once per candidate per board tick and is answered out of fields. It is
        /// cart-scoped rather than member-scoped, because a fresh operator walking onto the same
        /// handle repeats the same refused step exactly.
        ///
        /// #1219 D3: the one refusal is the latched stuck verdict. The 20 s released-pose back-off
        /// it replaces only paced the reacquisition cycle; the verdict ends it after two tries.
        /// Soldiers only: a player's push never asks this.
        /// </summary>
        internal bool CanReserveNpc(BlacksiteSoldierAI actor)
        {
            if (actor == null || actor.isEnemyDead || actor.IsShipGuard) return false;
            if (IsPushedBy(actor)) return true;
            return !_npcStuckVerdict.Latched;
        }
        internal bool IsWithinNpcAttachDistance(Vector3 position) => Vector3.Distance(position, NpcHandlePosition) <= NpcAttachDistance;
        internal bool TryAttachNpcServer(BlacksiteSoldierAI actor)
        {
            if (!IsServer || actor == null || actor.isEnemyDead || actor.IsShipGuard || IsAttached || IsNpcAttached || _deliveryStarted || IsDelivered || IsFailed || PayloadContract.IsFailed) return false;
            // #874 / #1305 H1: a cart a magnet or the cable owns is not pushed. FixedUpdate hands its
            // pose to the magnet or the cable before the haul runs, so an attach granted here would
            // leave the record Carried with nothing moving either body.
            if (_blacksiteMagnetOwner.Value != ulong.MaxValue || _magnetAttachHost != null) return false;
            if (!IsWithinNpcAttachDistance(actor.transform.position) || !CanGrabMotionServer()) return false;
            // #874/#1054: the cart's own admission, RE-CHECKED here rather than re-stated. The
            // board asks the same question before it spends an operator on the walk over
            // (BlacksiteObjectiveManifestController.Jobs.cs, SelectOperator); this is the same
            // predicate at the moment of acquisition, so the two can never drift apart and no
            // second clock exists to disagree with.
            if (!CanReserveNpc(actor)) return false;
            _npcPusher.Value = actor.NetworkObjectId;
            _npcPusherMember = actor.MemberIndex;
            ResetNpcCouplingState();
            // #874 (2026-09-22): and NOT the geometry record. A corridor and the ground it was
            // taken for were measured against the WORLD, not against the man, so a fresh operator
            // walking onto the same handle meets the same opening: resetting it here is how the
            // 2026-09-16 run walked seventeen operators into one refused step. It is cleared by
            // the things that make it untrue - its own expiry, a portal, an elevator carry and the
            // pair travelling out of it - and by nothing else.
            // 2026-09-16 (E5): pre-aim the cart onto the route he is about to walk before the first
            // step, about the handle so the pre-aim cannot itself move the handle out of his hands.
            // Refused poses simply keep what the turn could take; the motor finishes the rest at
            // its own rate. The attach gate requires him to face the cart within 45 degrees, so his
            // body yaw is by construction the yaw of the PARKED cart, and the manifest re-targets
            // him on the same tick - on the 2026-09-16 map that was 143 degrees away.
            PreAimNpcCartServer(ResolveNpcAttachYaw(actor));
            return true;
        }
        /// <summary>
        /// 2026-09-16 (E5): swing the parked cart onto the pusher's route heading in one step, about
        /// the handle he is standing at. Every intermediate pose is run through the cart's own pose
        /// sweep, so the swing can be partial or refused entirely; nothing here may teleport the
        /// cart through geometry, and nothing here moves the pusher.
        /// </summary>
        private void PreAimNpcCartServer(float routeYaw)
        {
            float target = routeYaw + FollowYawOffset;
            if (Mathf.Abs(Mathf.DeltaAngle(transform.eulerAngles.y, target)) < .5f) return;
            Vector3 position = transform.position;
            // Conform the pre-aim to the floor it is standing on, exactly as StepCartMotion and
            // UpdateNpcHaul do. A bare yaw quaternion strips the ground pitch and roll, so on a
            // graded floor the attach frame would publish an unconformed pose and the motor would
            // have to put it back on the next fixed step.
            Quaternion wanted = Quaternion.Euler(0f, target, 0f);
            if (PayloadCartGroundSampler.TrySampleCartGround(transform, _groundSampleState, out var ground)
                && ground.HasStableSupport)
                wanted = GroundRotation(wanted, ground);
            // A coarser step than the motor's 2 degrees: this is one swing of up to 180 degrees
            // rather than a fixed step's 1.1, and every sub-step is swept from the cart's real pose
            // by CartPoseClear rather than from the sub-step before it.
            Quaternion rotation = ResolveNpcPivotRotation(ref position, transform.rotation, wanted, NpcPreAimStepDegrees);
            if (Quaternion.Angle(rotation, transform.rotation) < .5f) return;
            transform.SetPositionAndRotation(position, rotation);
            ResetCartMotion();
        }
        // #940 S11 U1: the mission's own release entry, kept for the call sites that take the cart
        // away rather than lose it (Drop, the contract failure, the delivery, the staging hand-off,
        // the despawn, and the motor's support and runaway exits). It cannot be told a cause it did
        // not measure, so it reads one off the pusher it is about to let go of.
        internal void ReleaseNpcPusherServer() => ReleaseNpcPusherServer(ResolveNpcReleaseReason());
        private string ResolveNpcReleaseReason()
        {
            var actor = IsNpcAttached ? BlacksiteObjectiveManifestController.Resolve(_npcPusher.Value)?.GetComponent<BlacksiteSoldierAI>() : null;
            if (actor == null || actor.isEnemyDead) return "dead";
            if (actor.ReplicatedStunned) return "stunned";
            if (actor.HasImmediateCargoThreat) return "cargoThreat";
            // Every remaining caller is the cart being taken away from a pusher who was holding it
            // correctly. `blocked` is the ledger's or the plan verdict's answer and is never inferred here.
            return "unsupported";
        }
        /// <summary>
        /// #940 S11 U1 / #874: the one release. Every exit that lets go of the cart states why, in
        /// the closed vocabulary dead|stunned|cargoThreat|separated|blocked|portal|unsupported.
        ///
        /// #874 retires poseBlocked, misaligned and jammed with the machinery that produced them:
        /// there is no alignment gate to brake on, no per-step pose gate that releases on its own
        /// refusal and no unstick ladder to exhaust. #1293 R2 / #1305 R23: a pair with no plan (the
        /// manifest) or whose plan does not progress (the ledger's AnswerStalledRecord) is let go as
        /// `blocked`, once, and #1219 D3 counts that release into the cart's stuck verdict
        /// (<see cref="NoteBlockedReleaseServer"/>). #1233 S1: a paired
        /// entrance that refuses the arrived pair says `portal`, with the verdict already latched
        /// (<see cref="TryCrossPortalServer"/>). The release also hands
        /// the pusher's BODY back with the handle - his agent follows his transform again and his
        /// goal is re-requested (BlacksiteNavigator.EndPayloadCoupling) - so there is no state in
        /// which the coupling has let go of the cart and kept the man.
        /// </summary>
        internal void ReleaseNpcPusherServer(string reason)
        {
            if (!IsServer) return;
            if (IsNpcAttached)
            {
                var actor = BlacksiteObjectiveManifestController.Resolve(_npcPusher.Value)?.GetComponent<BlacksiteSoldierAI>();
                float leash = actor != null ? Vector3.Distance(actor.transform.position, NpcHandlePosition) : -1f;
                float reach = actor != null ? NpcOperatorReach(actor) : -1f;
                // #1219 D3: the goal the haul was serving, read before the coupling hands the
                // body back and re-requests it. No goal has no route to prove (#1305 R2).
                Vector3? exit = reason == "blocked" && actor != null && actor.Navigator != null ? actor.Navigator.IntendedGoal : null;
                EndNpcCouplingServer(actor);
                string cartRoute = exit.HasValue ? NoteBlockedReleaseServer(exit.Value) : "none";
                BlacksiteDiagnostics.Log("Cargo", $"member={_npcPusherMember}, cart released, reason={reason}"
                    + $", leash={leash:0.00}, reach={reach:0.00}, grace={_npcLostHandleFor:0.00}"
                    + $", waitedFor={_npcWaitingFor:0.00}"
                    + $", state={_npcHaulState}, operatorRefused={_npcOperatorRefusal != NpcOperatorRefusal.None}"
                    + $", routeComplete={_npcRouteComplete}, planNodes={(_npcHasPlan ? _npcPlanCount : 0)}"
                    + $", planAt={_npcPlanAt}, planRefusal={_npcPlanRefusal ?? "none"}"
                    + $", corridor={(_npcPlanCorridor && _npcHasPlan ? _npcPlanTarget.ToString() : "none")}"
                    + $", refusedGround={(Time.time < _npcRefusedUntil ? _npcRefusedGround.ToString() : "none")}"
                    + $", refusedBy={NpcPoseRefusalDetail}, cartRoute={cartRoute}, stuckVerdict={_npcStuckVerdict.Token}");
            }
            _npcPusher.Value = ulong.MaxValue;
            _npcPusherMember = -1;
            ResetNpcCouplingState();
        }
        /// <summary>
        /// #1219 D3: count one `blocked` release into the stuck verdict. The route is the CART's
        /// (<see cref="PayloadCartRoute.Prove"/>: link-free, cart-width, step and ledge rules) from
        /// the pose it was let go in to the goal the haul was serving - the inside main entrance,
        /// or the delivery point outside - not the pusher's walking route. One query per blocked
        /// release. #1305 G4: also the manifest's lease end, from the
        /// same pose toward the same goal, for a holder who made no progress to the handle. Returns the
        /// release line's cartRoute= value: <c>proven</c>, or the refusal as <see cref="PayloadCartRoute.Describe"/> words it.
        /// </summary>
        internal string NoteBlockedReleaseServer(Vector3 exit)
        {
            if (_npcStuckVerdict.Latched) return "none";
            Vector3 pose = transform.position;
            bool proven = PayloadCartRoute.Prove(pose, transform.rotation, exit, NavMesh.AllAreas,
                _npcStuckRouteScratch, transform, out string refusal);
            _npcStuckVerdict.NoteBlockedRelease(pose.x, pose.y, pose.z, NpcStuckSamePoseMetres, proven);
            return proven ? "proven" : PayloadCartRoute.Describe(refusal, _npcStuckRouteScratch);
        }
        // 2026-09-16 (WP2 step 4) / #874: the refusal, composed only where a diagnostic line is
        // built. _npcPoseRefusal is an interned literal written by the cart's own pose sweep and
        // _motionBlocker is a Collider reference the motor's sweep leaves behind, so a refused step
        // costs neither a string nor a native name marshal. A translation the motor's own sweep
        // stopped names the collider alone: no pose test refused it, so there is none to name.
        private string NpcPoseRefusalDetail => _npcPoseRefusal != null
            ? (_motionBlocker == null ? _npcPoseRefusal : _npcPoseRefusal + ":" + _motionBlocker.name)
            : _motionBlocker != null ? "sweep:" + _motionBlocker.name : "none";
        private void ResetNpcCouplingState()
        {
            _npcOperatorRefusal = NpcOperatorRefusal.None; _npcHasExecutedPose = false;
            _npcWaitingFor = 0f; _npcLostHandleFor = 0f;
            _npcPoseRefusal = null;
            _npcRouteComplete = false; _npcPlanRefusal = null;
            _npcProgressHandle = NpcHandlePosition;
            DropNpcPlan(null);
            _npcHaulState = NpcHaulState.Idle;
        }
        /// <summary>
        /// #874 (2026-09-22): drop the retained plan and any search running for it, and say why in
        /// the next diagnostic line. The ONE place the coupling stops steering by what it proved:
        /// arrival, a changed goal or world, a displaced cart, a stalled node, a released handle.
        /// It deliberately does NOT touch the refused-ground record, which is a fact about the
        /// world rather than about this plan.
        /// </summary>
        private void DropNpcPlan(string why)
        {
            _npcHasPlan = false; _npcPlanCount = 0; _npcPlanAt = 0; _npcPlanCorridor = false;
            _npcSearching = false; _npcSearchCount = 0; _npcSearchExpanded = 0; _npcSearchBest = 0;
            _npcNodeStalledFor = 0f;
            _npcNodeBestDistance = float.MaxValue; _npcNodeBestYawError = float.MaxValue;
            if (why != null) _npcPlanRefusal = why;
            if (_npcHaulState != NpcHaulState.Blocked) _npcHaulState = NpcHaulState.Idle;
        }
        /// <summary>
        /// #874 (2026-09-22): drop the ground the pair proved it cannot take, the side it was
        /// working along and the alternatives it failed to reach. Called where that record has
        /// stopped describing the world the pair is in: a portal crossing, an elevator carry, and
        /// the pair actually travelling clear of the disc.
        /// </summary>
        private void ForgetNpcCoupledGeometry()
        {
            _npcRefusedUntil = float.NegativeInfinity; _npcRefusedRadius = 0f;
            _npcRefusedWaypointCount = 0; _npcRefusedWaypointNext = 0;
            _npcCorridorSide = 0f; _npcCorridorSearchedAt = -10f;
        }
        // How far the operator is from the handle he is holding, in the plane. The handle sits
        // FollowHeight below the cart's own origin, and that vertical offset is not a separation the
        // reach gate has any business paying for.
        private float NpcOperatorReach(BlacksiteSoldierAI actor)
            => Vector3.ProjectOnPlane(actor.transform.position - NpcHandlePosition, Vector3.up).magnitude;
        /// <summary>
        /// #874: "this member is hauling this cart". True for every state the coupling holds the
        /// handle in, INCLUDING a legitimate stop - no route, a door queue, a combat stance, a
        /// turn on the spot, a blocked step inside the coupling's own bounded recovery - and false
        /// only once the cart has actually let go.
        ///
        /// It used to answer "is the cart following", with the pose gate and the jam latch folded
        /// into it, and the board read that as "this carry is not working": a braked frame fed a
        /// manifest watchdog that dropped the record and cycled a fresh operator into unchanged
        /// geometry. The cart owns physical block, recovery and release now, so what the board
        /// needs from the cart is the fact of the attachment.
        /// </summary>
        internal bool TryFollowNpcServer(BlacksiteSoldierAI actor) => IsPushedBy(actor);
        /// <summary>
        /// #1293 R2: the cart's ONE report about its pusher - its handle travelled (#1305 C3), the pair
        /// crossed or rode, or it waits. The ledger (<see cref="BlacksiteMotionMonitor.NoteProgress"/>)
        /// stamps his Push commitment; no report within its window is his miss, answered by
        /// AnswerStalledRecord with a `blocked` release. Host only: a member with no squad director
        /// (a client, a lost transport, a terminal mission) reports to nobody.
        /// </summary>
        private static void NoteNpcProgressServer(BlacksiteSoldierAI actor)
        {
            if (actor != null && actor.TryGetSquadDirector(out BlacksiteSquadDirector director)) director.Ledger.NoteProgress(actor);
        }
        /// <summary>
        /// #874 (2026-09-22): one coupled hauling action, steered by ONE retained plan of cart
        /// poses the pair has proved it can reach.
        ///
        /// The navigator supplies the destination and the world route to it, and nothing else. The
        /// cart cuts a short horizon off that route and searches its own poses to it
        /// (<see cref="TickNpcPlanSearchServer"/>): handle-anchored swings, straight runs and the
        /// reverse manoeuvres a tight opening needs, every primitive proved against the SAME
        /// <see cref="CartPoseClear"/> and <see cref="IsPusherStandClear"/> the step executes
        /// with. The plan is then followed until it is spent or something makes it untrue.
        ///
        /// What this replaces, and why one owner rather than three: the retired coupling took the
        /// first corner of the operator's NavMesh plan the pair could START down and proved half a
        /// chassis of advance along it. That says nothing about getting THROUGH an opening. On
        /// 2026-09-22 at t=1549.28 the cart was square on its leg (alignment 0.0), the operator was
        /// on the handle with a complete route, and the swept advance overlapped
        /// RegularRoomStorageTile; the vocabulary had no manoeuvre that could shift the chassis
        /// sideways through the opening, so a blind 0.25 m reverse ran twice, a corner-refusal
        /// memory and the route owner's corridor latch disagreed about what had failed, and the
        /// handle went back after 4.02 s. The same episode repeats at 73314-74038.
        ///
        /// The motor is untouched: the cart still integrates the command through the same
        /// collision sweep, wheel-support, ledge, slope and runaway machinery a player-pushed cart
        /// uses, and <see cref="StepNpcOperatorServer"/> carries the operator by the step the cart
        /// ACTUALLY took, so the two can never diverge.
        ///
        /// Host only, like every other write to this transform; the pusher id is replicated and the
        /// cart is a NetworkTransform, so clients render the pair as they render any pushed cart.
        /// </summary>
        private void UpdateNpcHaul()
        {
            if (!IsServer || !IsNpcAttached) return;
            var actor = BlacksiteObjectiveManifestController.Resolve(_npcPusher.Value)?.GetComponent<BlacksiteSoldierAI>();
            // #940 S11 U1: one exit per cause, so the release line names the cause instead of a
            // single silent guard covering four different ones.
            if (actor == null || actor.isEnemyDead) { ReleaseNpcPusherServer("dead"); return; }
            if (actor.ReplicatedStunned) { ReleaseNpcPusherServer("stunned"); return; }
            if (actor.HasImmediateCargoThreat) { ReleaseNpcPusherServer("cargoThreat"); return; }
            if (Time.frameCount - _npcElevatorFrame <= 1) return;
            // #940 S11 U1: the leash is physical, measured from his body to the cart's own handle
            // point, which is the distance a player watching the two bodies would call a
            // separation. Since #874 the coupling cannot produce one, so what survives the grace is
            // always something else that moved him.
            if (Vector3.Distance(actor.transform.position, NpcHandlePosition) > NpcLeashBreakMetres)
            {
                _npcLostHandleFor += Time.fixedDeltaTime;
                if (_npcLostHandleFor > LostHandleGraceSeconds) { ReleaseNpcPusherServer("separated"); return; }
            }
            else _npcLostHandleFor = 0f;
            var navigator = actor.Navigator;
            // Something that outranks the coupling is moving this body - a ride, a hand-walk, a
            // capture, a door pause, a combat stance. The cart holds where it stands and KEEPS the
            // handle: letting go for a stop, a turn or a queue is exactly the detach this replaces.
            // The wait is not a block and releases nothing (#1293 R2: it is reported as progress).
            if (navigator == null || !navigator.TryHoldForPayloadCoupling())
            {
                EndNpcCouplingServer(actor);
                _npcWaitingFor += Time.fixedDeltaTime;
                NoteNpcProgressServer(actor);
                return;
            }
            _npcCoupledActor = actor; _npcCoupled = true;
            Vector3 cart = transform.position;
            float cartYaw = transform.eulerAngles.y;
            // A cart something ELSE moved is not standing where the plan's poses were proved from:
            // a player pushing it, a magnet, a warp, a squadmate's shove. Half a chassis is the
            // same measure the stuck verdict's same-pose test uses.
            if (_npcHasExecutedPose
                && Vector3.ProjectOnPlane(cart - _npcExecutedPose, Vector3.up).magnitude > NpcPlanDisplacedMetres)
                DropNpcPlan("displaced");
            Vector3? goal = navigator.IntendedGoal;
            // The goal is the OPERATOR's, always, and nothing here ever changes it. None is no plan (#1305 R2).
            bool arrived = goal.HasValue && Vector3.ProjectOnPlane(goal.Value - actor.transform.position, Vector3.up).magnitude
                < NpcPlanArriveMetres;
            if ((_npcHasPlan || _npcSearching) && (!goal.HasValue
                || Vector3.ProjectOnPlane(goal.Value - _npcPlanGoal, Vector3.up).magnitude
                    > BlacksiteArrivalPolicy.MinDestinationChange))
                DropNpcPlan("goal");
            if (arrived)
            {
                // An arrived pair is waiting for whatever comes next, not blocked by anything.
                DropNpcPlan(null);
                _npcHaulState = NpcHaulState.Idle;
            }
            bool closing = TickNpcPlanServer(cart, cartYaw, out float commandYaw, out float commandSpeed);
            if (!arrived && !_npcHasPlan) TickNpcPlanSearchServer(actor, navigator, goal);
            // The pair travelling clear of the ground it once refused is that record ceasing to
            // describe anything. Nothing else expires it early, and its own clock bounds it.
            if (Time.time < _npcRefusedUntil
                && Vector3.ProjectOnPlane(cart - _npcRefusedGround, Vector3.up).magnitude
                    > _npcRefusedRadius + NpcPlanHorizonMetres)
                ForgetNpcCoupledGeometry();
            // The pair is asking to get somewhere when it is following a plan, computing one, or
            // reporting that it has none; a pair with no goal is refused (noGoal) and the manifest
            // lets its holder go (#1305 R23). A pair that has ARRIVED at a paired entrance waits: the
            // portal transaction answers that doorway, once (TryCrossPortalServer), never the ledger (#1233 S1).
            bool seeking = _npcHaulState != NpcHaulState.Idle;
            // #1293 R2 / #1305 C3: the ledger hears the pair advance when its handle has travelled
            // NpcPlanProgressMetres since the last report, and a waiting pair by declaration. A pair
            // with a plan that cannot move is the ledger's; one with no plan the manifest's (#1305 R23).
            Vector3 handle = NpcHandlePosition;
            if (!seeking || Vector3.ProjectOnPlane(handle - _npcProgressHandle, Vector3.up).magnitude >= NpcPlanProgressMetres)
            { _npcProgressHandle = handle; NoteNpcProgressServer(actor); }
            _npcWaitingFor = seeking ? 0f : _npcWaitingFor + Time.fixedDeltaTime;
            // The operator has to be on the handle, and his own last step has to have proved out,
            // for the pair to move at all. This is the mutual half of the step, and the reach gate
            // bounds any divergence the world can produce at the distance the attach was granted
            // inside - he closes it at BlacksitePhysicalStep.WalkSpeed against a cart that can only
            // leave at NpcPushSpeed, so it closes by construction whenever his step is legal.
            bool onHandle = _npcOperatorRefusal == NpcOperatorRefusal.None && NpcOperatorReach(actor) <= NpcCoupleReachMetres;
            _driveRequested = true;
            _driveTurnRate = CartTurnRateDegreesPerSecond;
            _driveTargetYaw = commandYaw;
            _driveTargetSpeed = onHandle ? commandSpeed : 0f;
            // The direction the pair is being asked to travel. The cart travels along -(its own
            // forward) and a reverse travels the other way; the grade is read along the same axis,
            // because that is where the cart is going rather than where a residual happens to point.
            Vector3 travel = -(Quaternion.Euler(0f, _driveTargetYaw, 0f) * Vector3.forward);
            if (_driveTargetSpeed < 0f) travel = -travel;
            NpcSlopeSpeedScale = 1f;
            if (PayloadCartGroundSampler.TrySampleCartGround(transform, _groundSampleState, out var ground)
                && ground.HasStableSupport)
            {
                float grade = ground.VerticalGradeAlong(travel);
                if (grade > .02f)
                    NpcSlopeSpeedScale = Mathf.Lerp(1f, RiskSettings.UphillMinimumSpeedMultiplier.Value, Mathf.Clamp01(grade / .7f));
            }
            if (BlacksiteDiagnostics.Enabled && seeking && !closing)
                BlacksiteDiagnostics.LogRateLimited(NpcBlockedLogKey, 1f, "Cargo",
                    $"member={_npcPusherMember}, cart blocked"
                    + $", state={_npcHaulState}, onHandle={onHandle}, operatorRefused={_npcOperatorRefusal != NpcOperatorRefusal.None}"
                    + $", reach={NpcOperatorReach(actor):0.00}"
                    + $", routeComplete={_npcRouteComplete}, planNodes={_npcPlanCount}, planAt={_npcPlanAt}"
                    + $", planRefusal={_npcPlanRefusal ?? "none"}"
                    // Whether this blocked pair is walking an alternative corridor, and where. A
                    // blocked line that says corridor= with a waypoint is a pair that HAS somewhere
                    // else to go; corridor=none on a Blocked line is the honest end of the episode.
                    + $", corridor={(_npcPlanCorridor ? _npcPlanTarget.ToString() : "none")}"
                    + $", refusedGround={(Time.time < _npcRefusedUntil ? _npcRefusedGround.ToString() : "none")}"
                    + $", nodeStalled={_npcNodeStalledFor:0.00}, speed={_driveTargetSpeed:0.00}, refusedBy="
                    // #1305 C3: the operator's predicate is named on this line only.
                    + (_npcOperatorRefusal == NpcOperatorRefusal.P1 ? "P1:" + _npcOperatorStep.Reason + ":" + (_npcOperatorStep.Blocker != null ? _npcOperatorStep.Blocker.name : "none")
                        : _npcOperatorRefusal != NpcOperatorRefusal.None ? _npcOperatorRefusal.ToString() : NpcPoseRefusalDetail) + ".");
        }
        /// <summary>
        /// #874 (2026-09-22): follow the retained plan - measure the pair against the node it is
        /// executing, retire the nodes it has reached, and hand back the one command the motor
        /// gets this step.
        ///
        /// Closing is a monotone best-so-far against a FIXED node, on its pose or on its yaw, so a
        /// pair turning about the handle onto a leg is closing and one oscillating about it is not.
        /// A node the world stops for <see cref="NpcPlanStallSeconds"/> was proved and has stopped
        /// being reachable (a door shut, a body moved in, a carve expired): that records the ground
        /// and replans, the coupling's ONLY recovery. The return (closing, or the plan spent) gates
        /// only the blocked diagnostic; the ledger hears the handle travel (#1305 C3).
        /// </summary>
        private bool TickNpcPlanServer(Vector3 cart, float cartYaw, out float commandYaw, out float commandSpeed)
        {
            commandYaw = cartYaw; commandSpeed = 0f;
            if (!_npcHasPlan) return false;
            while (_npcPlanAt < _npcPlanCount)
            {
                Vector3 node = _npcPlanPositions[_npcPlanAt];
                float yaw = _npcPlanYaws[_npcPlanAt];
                float run = _npcPlanRuns[_npcPlanAt];
                float yawError = Mathf.Abs(Mathf.DeltaAngle(cartYaw, yaw));
                bool aligned = yawError <= NpcPlanAlignDegrees;
                Vector3 offset = Vector3.ProjectOnPlane(node - cart, Vector3.up);
                bool travels = Mathf.Abs(run) > .0001f;
                // How much of this node's own run axis is left. Measured along the axis rather than
                // as a distance so a cart the sweep has slid a few centimetres sideways is level
                // with its node rather than permanently short of it.
                Vector3 axis = -(Quaternion.Euler(0f, yaw, 0f) * Vector3.forward) * (run < 0f ? -1f : 1f);
                float remaining = travels ? Vector3.Dot(offset, axis) : 0f;
                if (aligned && remaining <= NpcPlanNodeReachedMetres)
                {
                    _npcPlanAt++;
                    _npcNodeBestDistance = float.MaxValue; _npcNodeBestYawError = float.MaxValue;
                    _npcNodeStalledFor = 0f;
                    continue;
                }
                bool closing = false;
                float distance = offset.magnitude;
                if (distance < _npcNodeBestDistance - NpcCoupledCreepSpeed * Time.fixedDeltaTime)
                { _npcNodeBestDistance = distance; closing = true; }
                if (yawError < _npcNodeBestYawError - NpcCoupledCreepDegreesPerSecond * Time.fixedDeltaTime)
                { _npcNodeBestYawError = yawError; closing = true; }
                _npcNodeStalledFor = closing ? 0f : _npcNodeStalledFor + Time.fixedDeltaTime;
                if (_npcNodeStalledFor > NpcPlanStallSeconds)
                {
                    NoteNpcRefusedGround(cart, node);
                    DropNpcPlan("stall");
                    return false;
                }
                _npcHaulState = NpcHaulState.Following;
                commandYaw = yaw;
                // Align, THEN travel. Below the alignment band the speed law transmits better than
                // 99.7 per cent of the haul, so a plan of short legs is driven at full speed; above
                // it the pair turns about the handle, which moves neither body through anything.
                // This is what lets a doorway be entered square and left square.
                commandSpeed = !aligned || !travels ? 0f
                    : (run < 0f ? -1f : 1f) * PayloadCartMotionMath.CoupledHaulSpeed(cartYaw, yaw,
                        run < 0f ? NpcReverseSpeed : NpcPushSpeed, CartTurnRateDegreesPerSecond, Time.fixedDeltaTime);
                return closing;
            }
            // Every node reached: the plan is spent, not stalled. The next step cuts the next horizon.
            DropNpcPlan(null);
            return true;
        }
        /// <summary>
        /// #874 (2026-09-22): the cart-owned pose search, budgeted across fixed steps.
        ///
        /// A* over cart poses, with the heuristic measured at the HANDLE - it is the operator who
        /// has to arrive, and the chassis leads him by <see cref="FollowDistance"/>. Bounded by
        /// construction: <see cref="NpcSearchExpansions"/> expansions, <see cref="NpcSearchCapacity"/>
        /// nodes and <see cref="NpcSearchProbesPerStep"/> pose tests a fixed step, so a clear leg
        /// is answered by the first candidate of the first expansion and a hopeless opening costs
        /// one search rather than one search per frame.
        /// </summary>
        private void TickNpcPlanSearchServer(BlacksiteSoldierAI actor,
            BlacksiteSoldierAI.BlacksiteNavigator navigator, Vector3? goal)
        {
            if (!_npcSearching)
            {
                // A pair that has just proved it has nowhere to go does not re-prove it fifty
                // times a second. A replan after real travel, a fresh goal or a spent plan starts
                // immediately; only a BLOCKED outcome waits (#1305 R23: the manifest lets it go).
                if (_npcHaulState == NpcHaulState.Blocked
                    && Time.time - _npcPlanSearchedAt < NpcPlanRetrySeconds) return;
                if (!BeginNpcPlanSearchServer(navigator, goal)) return;
            }
            _npcSearchBudget = NpcSearchProbesPerStep;
            while (_npcSearchBudget > 0)
            {
                if (_npcSearchNodes[_npcSearchBest].Estimate <= NpcPlanArriveMetres) break;
                if (_npcSearchExpanded >= NpcSearchExpansions) break;
                int index = TakeNpcSearchNode();
                if (index < 0) break;
                _npcSearchExpanded++;
                ExpandNpcSearchNode(index);
            }
            bool reached = _npcSearchNodes[_npcSearchBest].Estimate <= NpcPlanArriveMetres;
            bool exhausted = _npcSearchExpanded >= NpcSearchExpansions
                || _npcSearchCount >= NpcSearchCapacity || !HasOpenNpcSearchNode();
            if (!reached && !exhausted) return;
            FinishNpcSearchServer(actor, navigator);
        }
        /// <summary>
        /// Cut a horizon off the world route and start a search to it. The navigator supplies its
        /// half of this: its agent's area mask and its goal, no second destination.
        /// </summary>
        /// <remarks>
        /// #1219 D1: the world route is the CART's, proved by <see cref="PayloadCartRoute"/> from the
        /// cart's origin on the operator's areas - never the operator agent's own path, which
        /// crosses off-mesh links a cart cannot. A full cart-route refusal blocks; corridor
        /// recovery is available only after that same proof succeeds from the actual cart pose.
        /// </remarks>
        private bool BeginNpcPlanSearchServer(BlacksiteSoldierAI.BlacksiteNavigator navigator, Vector3? goal)
        {
            var agent = navigator.Agent;
            if (!goal.HasValue || agent == null || !agent.enabled || !agent.isOnNavMesh)
            { _npcHaulState = NpcHaulState.Blocked; _npcPlanRefusal = goal.HasValue ? "noAgent" : "noGoal"; return false; }
            _npcPlanGoal = goal.Value;
            _npcPlanSearchedAt = Time.time;
            if (!PayloadCartRoute.Prove(transform.position, transform.rotation, goal.Value, agent.areaMask,
                    _npcCartRouteScratch, transform, out string refusal))
            {
                _npcRouteComplete = false;
                _npcHaulState = NpcHaulState.Blocked;
                _npcPlanRefusal = refusal;
                return false;
            }
            _npcRouteComplete = true;
            int corners = _npcCartRouteScratch.CornerCount;
            if (corners < 2)
            { _npcHaulState = NpcHaulState.Blocked; _npcPlanRefusal = "noCorners"; return false; }
            StartNpcSearchServer(ResolveNpcRouteHorizon(_npcCartRouteScratch.Corners, corners), goal.Value, false);
            return true;
        }
        /// <summary>
        /// The point <see cref="NpcPlanHorizonMetres"/> along the proven cart route, or its end if
        /// the route is shorter. One plan reaches this far; the next one cuts the following horizon.
        /// </summary>
        private static Vector3 ResolveNpcRouteHorizon(Vector3[] route, int corners)
        {
            float walked = 0f;
            for (int i = 1; i < corners; i++)
            {
                Vector3 from = route[i - 1], to = route[i];
                float span = Vector3.ProjectOnPlane(to - from, Vector3.up).magnitude;
                if (walked + span >= NpcPlanHorizonMetres)
                    return Vector3.Lerp(from, to, span < .0001f ? 1f
                        : Mathf.Clamp01((NpcPlanHorizonMetres - walked) / span));
                walked += span;
            }
            return route[corners - 1];
        }
        private void StartNpcSearchServer(Vector3 target, Vector3 goal, bool corridor)
        {
            _npcSearchTarget = target; _npcSearchGoal = goal; _npcSearchCorridor = corridor;
            _npcSearchExpanded = 0; _npcSearchBest = 0; _npcSearchCount = 1; _npcSearching = true;
            Vector3 cart = transform.position;
            float yaw = transform.eulerAngles.y;
            _npcSearchNodes[0].Position = cart;
            _npcSearchNodes[0].Yaw = yaw;
            _npcSearchNodes[0].Run = 0f;
            _npcSearchNodes[0].Cost = 0f;
            _npcSearchNodes[0].Estimate = NpcHandleEstimate(cart, yaw, target);
            _npcSearchNodes[0].Parent = -1;
            _npcSearchNodes[0].Open = true;
            _npcSearchRootEstimate = _npcSearchNodes[0].Estimate;
            _npcPlanSearchedAt = Time.time;
            _npcHaulState = NpcHaulState.Searching;
        }
        /// <summary>How far the pair's HANDLE would still be from the point it is steering at.</summary>
        private float NpcHandleEstimate(Vector3 position, float yaw, Vector3 target)
        {
            PayloadCartMotionMath.CartHandlePoint(position.x, position.z, yaw, FollowDistance,
                out float handleX, out float handleZ);
            float dx = target.x - handleX, dz = target.z - handleZ;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }
        private bool HasOpenNpcSearchNode()
        {
            for (int i = 0; i < _npcSearchCount; i++) if (_npcSearchNodes[i].Open) return true;
            return false;
        }
        /// <summary>The cheapest open node by cost plus estimate, closed as it is taken.</summary>
        private int TakeNpcSearchNode()
        {
            int best = -1; float bestScore = float.MaxValue;
            for (int i = 0; i < _npcSearchCount; i++)
            {
                if (!_npcSearchNodes[i].Open) continue;
                float score = _npcSearchNodes[i].Cost + _npcSearchNodes[i].Estimate;
                if (score >= bestScore) continue;
                bestScore = score; best = i;
            }
            if (best >= 0) _npcSearchNodes[best].Open = false;
            return best;
        }
        /// <summary>
        /// #874 (2026-09-22): the four manoeuvres one pose may be left by. Aim at the point the
        /// pair is steering at and run as far as the world allows; swing hard either way and take
        /// one chassis length; or back out one chassis length.
        ///
        /// A swing the sweep refuses part way is KEPT at whatever it reached, so the fan needs no
        /// rung per angle: the geometry itself supplies the intermediate headings. Every primitive
        /// goes through <see cref="PayloadCartMotionMath.ResolveCoupledManoeuvre"/> against the
        /// production pose sweep, and the resulting pose has to hold the OPERATOR as well - a pose
        /// the cart fits in with nowhere for the man on the handle to stand is not a pose for this
        /// pair.
        /// </summary>
        private void ExpandNpcSearchNode(int index)
        {
            Vector3 basePosition = _npcSearchNodes[index].Position;
            float baseYaw = _npcSearchNodes[index].Yaw;
            float baseCost = _npcSearchNodes[index].Cost;
            _npcProbeBase = basePosition; _npcProbeBaseYaw = baseYaw;
            _npcProbeBaseMask = SampleNpcWheelMask(basePosition, baseYaw);
            PayloadCartMotionMath.CartHandlePoint(basePosition.x, basePosition.z, baseYaw, FollowDistance,
                out float handleX, out float handleZ);
            Vector3 lead = Vector3.ProjectOnPlane(
                _npcSearchTarget - new Vector3(handleX, basePosition.y, handleZ), Vector3.up);
            // The yaw that puts the chassis square on the line from the handle to the target: the
            // handle trails the cart along its own travel axis, so this is the heading that carries
            // the OPERATOR there rather than the chassis.
            float aimYaw = lead.sqrMagnitude > .0001f
                ? Quaternion.LookRotation(lead, Vector3.up).eulerAngles.y + FollowYawOffset
                : baseYaw;
            float aim = Mathf.Clamp(PayloadCartMotionMath.DeltaDegrees(baseYaw, aimYaw),
                -NpcPlanTurnDegrees, NpcPlanTurnDegrees);
            float aimRun = Mathf.Clamp(lead.magnitude, NpcPlanRunMetres, NpcPlanLongRunMetres);
            // The aim candidate FIRST, and the rest only when the world has given a reason to
            // look for one. Two reasons end an expansion here:
            //
            //   - it already arrived, and nothing a turn could find beats having got there;
            //   - the direct run was admitted WHOLE, which is the world saying this node stands on
            //     open ground. Lateral manoeuvres exist for openings and corners, and a run
            //     nothing cut short is neither.
            //
            // That is what keeps the search off the quiet path: a clear corridor costs six pose
            // tests for four metres of run, where the two hard turns cost thirty-five sub-steps
            // each. The moment a run IS cut short - a doorway, a corner, a body in the way - the
            // full fan opens, which is exactly the frame the 2026-09-22 jam needed it in.
            bool admitted = TryAddNpcSearchNode(index, basePosition, baseYaw, baseCost, baseYaw + aim, aimRun);
            if (_npcSearchNodes[_npcSearchBest].Estimate <= NpcPlanArriveMetres) return;
            if (admitted) return;
            TryAddNpcSearchNode(index, basePosition, baseYaw, baseCost, baseYaw + NpcPlanTurnDegrees, NpcPlanRunMetres);
            TryAddNpcSearchNode(index, basePosition, baseYaw, baseCost, baseYaw - NpcPlanTurnDegrees, NpcPlanRunMetres);
            TryAddNpcSearchNode(index, basePosition, baseYaw, baseCost, baseYaw, -NpcPlanRunMetres);
        }
        /// <summary>
        /// Resolve one manoeuvre from a node and keep the pose it reached as a new node.
        ///
        /// Returns whether the manoeuvre was admitted WHOLE and the pose it ends in holds the
        /// operator - which is the caller's "this node stands on open ground" test, and is
        /// deliberately not "a node was added": a pose already in the frontier is a pose the
        /// search has, so a duplicate is open ground too.
        /// </summary>
        private bool TryAddNpcSearchNode(int parent, Vector3 from, float fromYaw, float fromCost,
            float targetYaw, float run)
        {
            // The budget is checked between EXPANSIONS, never inside one: half a fan is not a
            // cheaper fan, it is a node whose alternatives were silently dropped after it was
            // closed. One expansion may therefore overshoot the step's budget, which bounds a
            // fixed step at the budget plus one fan rather than at the budget exactly.
            if (_npcSearchCount >= NpcSearchCapacity) return false;
            _npcProbeHasChain = false;
            bool admitted = PayloadCartMotionMath.ResolveCoupledManoeuvre(from.x, from.z, fromYaw,
                FollowDistance, targetYaw, NpcPivotStepDegrees, run, NpcPlanRunMetres, NpcPlanProbe,
                out float endX, out float endZ, out float endYaw, out float taken);
            float turned = Mathf.Abs(PayloadCartMotionMath.DeltaDegrees(fromYaw, endYaw));
            // Neither swung nor travelled: the world refused this manoeuvre outright.
            if (turned < NpcPivotStepDegrees && Mathf.Abs(taken) < .01f) return false;
            // The height the last accepted sub-pose was seated at, so a node at the top of a ramp
            // is sampled from the ramp rather than from the floor the manoeuvre started on.
            Vector3 position = new Vector3(endX, _npcProbeHasChain ? _npcProbeChain.y : from.y, endZ);
            Quaternion rotation = Quaternion.Euler(0f, endYaw, 0f);
            // Seat the node on the floor it would stand on, so the operator's standing room and the
            // next expansion's wheel mask are both measured at the height the cart would be at.
            if (PayloadCartGroundSampler.TrySampleCartGround(transform, position, rotation, _groundSampleState, out var ground)
                && ground.HasStableSupport)
                position.y = ground.SupportY + FollowHeight;
            if (!IsPusherStandClear(position, rotation, out _, transform)) return false;
            for (int i = 0; i < _npcSearchCount; i++)
            {
                if (Vector3.ProjectOnPlane(position - _npcSearchNodes[i].Position, Vector3.up).magnitude
                        > NpcSearchDuplicateMetres) continue;
                if (Mathf.Abs(Mathf.DeltaAngle(endYaw, _npcSearchNodes[i].Yaw)) > NpcSearchDuplicateDegrees) continue;
                return admitted;
            }
            int index = _npcSearchCount++;
            _npcSearchNodes[index].Position = position;
            _npcSearchNodes[index].Yaw = endYaw;
            _npcSearchNodes[index].Run = taken;
            _npcSearchNodes[index].Cost = fromCost + Mathf.Abs(taken) * (taken < 0f ? NpcPlanReverseCostShare : 1f)
                + turned * NpcPlanTurnCostMetresPerDegree;
            _npcSearchNodes[index].Estimate = NpcHandleEstimate(position, endYaw, _npcSearchTarget);
            _npcSearchNodes[index].Parent = (short)parent;
            _npcSearchNodes[index].Open = true;
            if (_npcSearchNodes[index].Estimate < _npcSearchNodes[_npcSearchBest].Estimate) _npcSearchBest = index;
            return admitted;
        }
        /// <summary>
        /// The one probe every planned primitive goes through: <see cref="CartPoseClear"/> - ground
        /// sample, ledge, overlap and swept box - swept from the pose the manoeuvre is LEAVING
        /// rather than from the cart's live pose, plus the budget that spreads a search over fixed
        /// steps. There is no second, gentler fit: the planner asks exactly what the motor asks.
        /// </summary>
        private bool NpcPlanProbeServer(float fromX, float fromZ, float fromYaw,
            float toX, float toZ, float toYaw)
        {
            _npcSearchBudget--;
            // The height a planned sub-pose is asked about: the pose the last one was seated at
            // while the manoeuvre is chaining, and the expansion's own base otherwise. Without it
            // a long run across a grade keeps asking about the height it started at, and
            // CartPoseClear's own grounding tolerance refuses the far end of a ramp the cart could
            // actually roll up.
            float reference = _npcProbeHasChain ? _npcProbeChain.y : _npcProbeBase.y;
            Vector3 origin = new Vector3(fromX, reference, fromZ);
            int mask = _npcProbeHasChain && Vector3.ProjectOnPlane(origin - _npcProbeChain, Vector3.up).sqrMagnitude < .0001f
                    && Mathf.Abs(Mathf.DeltaAngle(fromYaw, _npcProbeChainYaw)) < .01f
                ? _npcProbeChainMask
                : Vector3.ProjectOnPlane(origin - _npcProbeBase, Vector3.up).sqrMagnitude < .0001f
                    && Mathf.Abs(Mathf.DeltaAngle(fromYaw, _npcProbeBaseYaw)) < .01f
                    ? _npcProbeBaseMask
                    : SampleNpcWheelMask(origin, fromYaw);
            Vector3 candidate = new Vector3(toX, reference, toZ);
            _npcHasPoseOrigin = true; _npcPoseOrigin = origin; _npcPoseOriginMask = mask;
            bool clear;
            try { clear = CartPoseClear(candidate, Quaternion.Euler(0f, toYaw, 0f), _npcCoupledActor); }
            finally { _npcHasPoseOrigin = false; }
            if (clear)
            {
                _npcProbeHasChain = true; _npcProbeChainYaw = toYaw; _npcProbeChainMask = _npcPoseResultMask;
                _npcProbeChain = new Vector3(toX, _npcPoseResultY, toZ);
            }
            else _npcProbeHasChain = false;
            return clear;
        }
        private int SampleNpcWheelMask(Vector3 position, float yaw)
            => PayloadCartGroundSampler.TrySampleCartGround(transform, position,
                Quaternion.Euler(0f, yaw, 0f), _groundSampleState, out var ground)
                ? ground.FreshMask : PayloadCartMotionMath.AllWheelsMask;
        /// <summary>
        /// #874 (2026-09-22): the search is over. Either it reached what it was steering at, or it
        /// bought enough of the way there to be worth walking, or the pair has nowhere to go and
        /// says so - after the alternative corridor has been asked for exactly once.
        /// </summary>
        private void FinishNpcSearchServer(BlacksiteSoldierAI actor,
            BlacksiteSoldierAI.BlacksiteNavigator navigator)
        {
            _npcSearching = false;
            int terminal = _npcSearchBest;
            bool useful = _npcSearchNodes[terminal].Estimate <= NpcPlanArriveMetres
                || _npcSearchRootEstimate - _npcSearchNodes[terminal].Estimate >= NpcPlanProgressMetres;
            if (useful && terminal > 0)
            {
                InstallNpcPlan(terminal, navigator.Agent.areaMask);
                return;
            }
            // Nothing the pair can walk toward this target. Record the ground it could not get past
            // - the cart's own half length toward it, which is the pose the probe was refused AT,
            // not the target thirty metres away - and ask for a way round it ONCE.
            NoteNpcRefusedGround(transform.position, _npcSearchTarget);
            if (_npcSearchCorridor)
            {
                RememberNpcRefusedWaypoint(_npcSearchTarget);
                _npcHaulState = NpcHaulState.Blocked; _npcPlanRefusal = "corridorRefused";
                return;
            }
            BeginNpcCorridorSearchServer(actor, navigator, _npcSearchGoal, "noPose");
        }
        private void InstallNpcPlan(int terminal, int areaMask)
        {
            int length = 0;
            for (int walk = terminal; walk > 0; walk = _npcSearchNodes[walk].Parent) length++;
            // A chain longer than the buffer keeps the nodes NEAREST THE CART: those are the ones
            // the pair executes next, and the horizon is re-cut from wherever it gets to.
            for (int drop = length - NpcPlanCapacity; drop > 0; drop--)
            { terminal = _npcSearchNodes[terminal].Parent; length--; }
            // Prove onward from the actual retained cart pose, never the operator waypoint.
            // The local pose search may take a different heading or offset from the world route.
            if (!PayloadCartRoute.Prove(_npcSearchNodes[terminal].Position,
                    Quaternion.Euler(0f, _npcSearchNodes[terminal].Yaw, 0f), _npcSearchGoal,
                    areaMask, _npcCartRouteScratch, transform, out string refusal))
            {
                _npcRouteComplete = false;
                _npcHaulState = NpcHaulState.Blocked;
                _npcPlanRefusal = refusal;
                return;
            }
            int slot = length;
            for (int walk = terminal; walk > 0 && slot > 0; walk = _npcSearchNodes[walk].Parent)
            {
                slot--;
                _npcPlanPositions[slot] = _npcSearchNodes[walk].Position;
                _npcPlanYaws[slot] = _npcSearchNodes[walk].Yaw;
                _npcPlanRuns[slot] = _npcSearchNodes[walk].Run;
            }
            _npcPlanCount = length; _npcPlanAt = 0; _npcHasPlan = true;
            _npcPlanTarget = _npcSearchTarget; _npcPlanGoal = _npcSearchGoal;
            _npcPlanCorridor = _npcSearchCorridor; _npcPlanRefusal = null;
            _npcNodeBestDistance = float.MaxValue; _npcNodeBestYawError = float.MaxValue;
            _npcNodeStalledFor = 0f;
            _npcHaulState = NpcHaulState.Following;
            _npcSearchCount = 0; _npcSearchExpanded = 0; _npcSearchBest = 0;
        }
        /// <summary>
        /// #874/#1054, migrated onto the cart: the alternative corridor. The pair has proved it
        /// cannot take the ground in front of it, so ONE intermediate waypoint is chosen as a leg
        /// of the SAME goal and the pose search is re-run to that instead.
        ///
        /// It lives here rather than in the navigator because the thing that decides whether a
        /// corridor is an alternative is the pair's own geometry, and two owners steering on
        /// separate memories is the defect this package removes: the navigator's latch and the
        /// cart's corner memory disagreed about what had failed and re-picked a waypoint per plan.
        /// The navigator still supplies the connectivity - its area mask, its abandoned-destination
        /// record - and never a heading.
        /// </summary>
        private bool BeginNpcCorridorSearchServer(BlacksiteSoldierAI actor,
            BlacksiteSoldierAI.BlacksiteNavigator navigator, Vector3 goal, string refusal)
        {
            _npcPlanRefusal = refusal;
            _npcHaulState = NpcHaulState.Blocked;
            if (Time.time >= _npcRefusedUntil) return false;
            if (Time.time - _npcCorridorSearchedAt < NpcCorridorSearchSeconds) return false;
            _npcCorridorSearchedAt = Time.time;
            var agent = navigator.Agent;
            if (agent == null || !agent.enabled || !agent.isOnNavMesh) return false;
            if (!PayloadCartRoute.Prove(transform.position, transform.rotation, goal, agent.areaMask,
                    _npcCartRouteScratch, transform, out string routeRefusal))
            {
                _npcRouteComplete = false;
                _npcPlanRefusal = routeRefusal;
                return false;
            }
            if (!TryFindNpcCoupledCorridor(actor, navigator, goal, out Vector3 waypoint)) return false;
            BlacksiteDiagnostics.Log("Cargo",
                $"member={_npcPusherMember}, coupled corridor around refused ground at {_npcRefusedGround}"
                + $", waypoint={waypoint}, goal={goal} kept, radius={_npcRefusedRadius:0.00}.");
            StartNpcSearchServer(waypoint, goal, true);
            return true;
        }
        /// <summary>
        /// The candidate fan, bounded by construction - two radii by four bearings, each candidate
        /// sampled once, cheapest tests first: a sample, the storey, the record, real separation,
        /// progress toward the goal, standing room, then the two routes.
        ///
        /// Candidates ring the MEMBER, with only their bearing taken from the refused ground.
        /// Ringing the refused ground instead was tried and reverted on native evidence: it puts
        /// the waypoint level with the thing that stopped the pair, and the cart leads the man it
        /// is steered from by FollowDistance, so the leg to such a waypoint aims the chassis
        /// diagonally INTO that thing. A waypoint abreast of the member is reached by travelling
        /// along the face, which is the way past the end of one.
        ///
        /// The second route is what makes this a different CORRIDOR rather than a farther corner of
        /// the same one: a waypoint whose own way to the goal goes back through the ground the pair
        /// just refused is that corridor reached sideways, and it is rejected with no lead
        /// exemption at all, because it starts outside the record by construction.
        /// </summary>
        private bool TryFindNpcCoupledCorridor(BlacksiteSoldierAI actor,
            BlacksiteSoldierAI.BlacksiteNavigator navigator, Vector3 goal, out Vector3 waypoint)
        {
            waypoint = Vector3.zero;
            Vector3 from = actor.transform.position;
            float goalDistance = Vector3.ProjectOnPlane(goal - from, Vector3.up).magnitude;
            if (goalDistance < BlacksiteRecoveryLadder.MinimumGoalDistance) return false;
            Vector3 away = Vector3.ProjectOnPlane(from - _npcRefusedGround, Vector3.up);
            if (away.sqrMagnitude < .01f) away = Vector3.ProjectOnPlane(from - goal, Vector3.up);
            away = away.sqrMagnitude < .01f ? -actor.transform.forward : away.normalized;
            // Which lateral side to work along. The goal decides whenever it actually says
            // anything, and when it does not - a goal straight beyond the refused ground puts this
            // dot at zero - the side this episode has already been working along is inherited
            // rather than re-rolled. A person working along a face they cannot cross picks an end
            // and keeps to it; re-deciding per search reversed the legs of one way round and
            // travelled thirty-one metres of a twelve-metre journey on the 2026-09-22 run.
            Vector3 lateral = Quaternion.AngleAxis(90f, Vector3.up) * away;
            float bias = Vector3.Dot(lateral, Vector3.ProjectOnPlane(goal - _npcRefusedGround, Vector3.up));
            float side = Mathf.Abs(bias) >= NpcCorridorSideBias ? Mathf.Sign(bias)
                : _npcCorridorSide != 0f ? _npcCorridorSide
                : 1f;
            _npcCorridorSide = side;
            int mask = navigator.CoupledAreaMask;
            float near = _npcRefusedRadius + BlacksiteSoldierAI.BlacksiteNavigator.MinObstacleRadius;
            for (int step = 0; step < 2; step++)
            {
                // The FARTHER ring first. A longer leg is more of the way past the thing per plan,
                // and the near ring stays the fallback for a far candidate with no standing room,
                // no route or no pose the pair can reach.
                float radius = step == 0
                    ? near + BlacksiteSoldierAI.BlacksiteNavigator.RouteLeadMetres
                    : near;
                for (int bearing = 0; bearing < NpcCorridorBearings.Length; bearing++)
                {
                    Vector3 candidate = from
                        + Quaternion.AngleAxis(NpcCorridorBearings[bearing] * side, Vector3.up) * away * radius;
                    if (!NavMesh.SamplePosition(candidate, out var nav, BlacksiteArrivalPolicy.AgentRadius, mask)) continue;
                    if (Mathf.Abs(nav.position.y - from.y) >= BlacksiteSoldierAI.BlacksiteNavigator.VerticalReboundMetres) continue;
                    if (IsNpcGroundRefused(nav.position)) continue;
                    if (Vector3.ProjectOnPlane(nav.position - from, Vector3.up).magnitude
                        < BlacksiteArrivalPolicy.MinDestinationChange) continue;
                    // A waypoint the PAIR has already failed to reach is not offered twice.
                    if (IsNpcWaypointRefused(nav.position)) continue;
                    // A waypoint further from the goal than the member is now, by more than the
                    // step it takes to reach it, is a retreat rather than a way around.
                    if (Vector3.ProjectOnPlane(goal - nav.position, Vector3.up).magnitude > goalDistance + radius) continue;
                    if (!BlacksitePhysicalStep.CanOccupy(actor.transform, nav.position, MotionMask,
                            BlacksitePhysicalStep.IgnoreSquadBodies)) continue;
                    // The way there - exempting the lead the pair is standing in, which is next to
                    // the ground that refused it by definition - and then the way ON.
                    if (!NpcCorridorLegProves(navigator, from, nav.position,
                            BlacksiteSoldierAI.BlacksiteNavigator.RouteLeadMetres)) continue;
                    if (!NpcCorridorLegProves(navigator, nav.position, goal, 0f)) continue;
                    // The fan's operator legs are only connectivity filters. Admission still
                    // requires the same full cart proof, starting at its real pose and yaw.
                    if (!PayloadCartRoute.Prove(transform.position, transform.rotation, nav.position,
                            navigator.Agent.areaMask, _npcCartRouteScratch, transform, out _)) continue;
                    waypoint = nav.position;
                    return true;
                }
            }
            return false;
        }
        /// <summary>
        /// Connectivity filter between operator waypoints: no off-mesh link or recorded refused
        /// ground. This is not cart-route authority; corridor admission separately runs the full
        /// <see cref="PayloadCartRoute"/> proof from an actual cart pose.
        /// </summary>
        private bool NpcCorridorLegProves(BlacksiteSoldierAI.BlacksiteNavigator navigator,
            Vector3 from, Vector3 to, float leadExempt)
        {
            var agent = navigator.Agent;
            int areas = agent != null ? agent.areaMask : NavMesh.AllAreas;
            if (!PayloadCartRoute.ProveLinkFree(from, to, areas, _npcCartRouteScratch, out _)) return false;
            Vector3[] route = _npcCartRouteScratch.Corners;
            int corners = _npcCartRouteScratch.CornerCount;
            if (corners < 2 || Time.time >= _npcRefusedUntil) return true;
            float lead = 0f;
            for (int i = 1; i < corners; i++)
            {
                Vector3 begin = route[i - 1], end = route[i];
                float span = Vector3.ProjectOnPlane(end - begin, Vector3.up).magnitude;
                if (lead + span > leadExempt)
                {
                    Vector3 start = lead >= leadExempt
                        ? begin
                        : Vector3.Lerp(begin, end, (leadExempt - lead) / Mathf.Max(.0001f, span));
                    if (PayloadCartMotionMath.SegmentEntersDisc(_npcRefusedGround.x, _npcRefusedGround.z,
                            _npcRefusedRadius, start.x, start.z, end.x, end.z)) return false;
                }
                lead += span;
            }
            return true;
        }
        /// <summary>
        /// One disc, replaced rather than accumulated. A refusal measured somewhere else is a
        /// different piece of ground; a refusal measured inside the record it already has is the
        /// same ground restated and only renews it. Both are bounded by the ledger's window: a pair
        /// that keeps finding new refused ground is a pair that is not travelling.
        /// </summary>
        private void NoteNpcRefusedGround(Vector3 from, Vector3 toward)
        {
            Vector3 lead = Vector3.ProjectOnPlane(toward - from, Vector3.up);
            Vector3 point = lead.sqrMagnitude < .0001f
                ? from
                : from + lead.normalized * Mathf.Min(lead.magnitude, NpcRouteProbeMetres);
            float radius = Mathf.Max(NpcRefusedGroundRadius, BlacksiteSoldierAI.BlacksiteNavigator.MinObstacleRadius);
            if (Time.time >= _npcRefusedUntil
                || Vector3.ProjectOnPlane(point - _npcRefusedGround, Vector3.up).magnitude > _npcRefusedRadius)
            { _npcRefusedGround = point; _npcRefusedRadius = radius; }
            else _npcRefusedRadius = Mathf.Max(_npcRefusedRadius, radius);
            _npcRefusedUntil = Time.time + NpcRefusedGroundSeconds;
        }
        private bool IsNpcGroundRefused(Vector3 point)
            => Time.time < _npcRefusedUntil
                && Vector3.ProjectOnPlane(point - _npcRefusedGround, Vector3.up).sqrMagnitude
                    < _npcRefusedRadius * _npcRefusedRadius;
        private bool IsNpcWaypointRefused(Vector3 point)
        {
            for (int i = 0; i < _npcRefusedWaypointCount; i++)
                if (Vector3.ProjectOnPlane(point - _npcRefusedWaypoints[i], Vector3.up).sqrMagnitude
                    <= BlacksiteArrivalPolicy.MinDestinationChange * BlacksiteArrivalPolicy.MinDestinationChange)
                    return true;
            return false;
        }
        private void RememberNpcRefusedWaypoint(Vector3 point)
        {
            _npcRefusedWaypoints[_npcRefusedWaypointNext] = point;
            _npcRefusedWaypointNext = (_npcRefusedWaypointNext + 1) % NpcRefusedWaypointSlots;
            if (_npcRefusedWaypointCount < NpcRefusedWaypointSlots) _npcRefusedWaypointCount++;
        }
        /// <summary>
        /// #874: the coupling's second half - the operator is carried by the step the cart actually
        /// took, on the pose the motor resolved rather than the one it was commanded.
        ///
        /// Called from FixedUpdate immediately after <c>StepCartMotion</c>, so the handle this
        /// reads is the one the cart's collision sweep, wheel-support and ledge tests left it at.
        /// His step is swept by the same <see cref="BlacksitePhysicalStep"/> every hand-walked
        /// Blacksite body uses - supporting ground under the foot and a clear swept capsule along
        /// the way - and it is REFUSED rather than forced: a refusal stops the cart on the next
        /// step, which is how the pair stays together instead of the cart walking out of his hands.
        ///
        /// He is walked toward the handle at <see cref="BlacksitePhysicalStep.WalkSpeed"/> rather
        /// than at the cart's own speed, so one call both tracks a moving handle and settles the
        /// reach the attach was granted inside. It is the NPC half of what
        /// <c>FollowCartHandleLocal</c> does for a player driver, through the motion owner that
        /// owns his agent instead of through a CharacterController.
        /// </summary>
        private void StepNpcOperatorServer()
        {
            if (!IsServer || !_npcCoupled) return;
            var actor = _npcCoupledActor;
            var navigator = actor != null ? actor.Navigator : null;
            if (navigator == null) return;
            // The body faces the CART, which with the handle square behind the chassis is the
            // cart's own forward travel axis and the facing the push rig poses onto. It is
            // deliberately independent of the run's sign: on a reverse leg the cart comes toward
            // him, he backpedals with his hands still on the handle, and his own signed local
            // velocity is what gives him the reverse walk cycle.
            navigator.FaceCoupledHaul(-(Quaternion.Euler(0f, transform.eulerAngles.y, 0f) * Vector3.forward));
            // Where the coupling left the cart this step. Read on the NEXT step to tell the pair
            // travelling from something else having moved the cart under it.
            _npcExecutedPose = transform.position; _npcHasExecutedPose = true;
            Vector3 from = actor.transform.position;
            Vector3 handle = NpcHandlePosition;
            Vector3 target = new Vector3(handle.x, from.y, handle.z);
            if (Vector3.ProjectOnPlane(target - from, Vector3.up).sqrMagnitude <= NpcOperatorSettledSqrMetres)
            { _npcOperatorRefusal = NpcOperatorRefusal.None; return; }
            _npcOperatorRefusal = !BlacksitePhysicalStep.TryStep(actor.transform, target, BlacksitePhysicalStep.WalkSpeed * Time.fixedDeltaTime,
                    MotionMask, NpcOperatorIgnore, out Vector3 next, out _npcOperatorStep) ? NpcOperatorRefusal.P1
                : !NavMesh.SamplePosition(next, out var nav, NpcOperatorNavTolerance, NavMesh.AllAreas) ? NpcOperatorRefusal.P2
                : Mathf.Abs(nav.position.y - next.y) > NpcOperatorNavTolerance ? NpcOperatorRefusal.P3
                : !navigator.TryCarryCoupledBody(next) ? NpcOperatorRefusal.P4 : NpcOperatorRefusal.None;
        }
        /// <summary>
        /// #874: give the pusher's body back. Called on every release and on every step the
        /// coupling does not own the body, so the agent is never left suspended by an owner that
        /// has stopped moving it.
        /// </summary>
        private void EndNpcCouplingServer(BlacksiteSoldierAI actor)
        {
            // The actor the caller resolved, or the one the coupling was holding: a release whose
            // own lookup came back empty must still hand the body back to the member it took it
            // from, or his agent is left suspended by an owner that no longer exists.
            var coupled = actor != null ? actor : _npcCoupledActor;
            _npcCoupledActor = null;
            if (!_npcCoupled) return;
            _npcCoupled = false; _npcOperatorRefusal = NpcOperatorRefusal.None; _npcHasExecutedPose = false;
            var navigator = coupled != null ? coupled.Navigator : null;
            if (navigator != null) navigator.EndPayloadCoupling();
        }
        /// <summary>
        /// 2026-09-16 (E1): the handle-anchored turn, resolved against the cart's own pose sweep.
        ///
        /// The cart used to rotate about its centre while an NPC pushed it, which threw the handle
        /// around an arc of 2 * FollowDistance * sin(turn / 2) - 2.66 m for the 143-degree re-target
        /// the 2026-09-16 host run measured, 2.80 m for a reversal. That crosses
        /// <see cref="NpcLeashBreakMetres"/> at 126.5 degrees, about 0.47 s at the agent's
        /// angularSpeed 270, and after the 1.25 s grace the pusher was released as
        /// <c>separated</c>: nine attempts, nine drops, no deliveries. Pivoting about the handle
        /// instead removes the arc at its source - the handle stays under his hands and the body
        /// swings round him, which is what a person turning a cart actually does - and it needs no
        /// write to the soldier, whose agent has exactly one writer (BlacksiteMotionOwner).
        ///
        /// Every sub-step is a full <see cref="CartPoseClear"/>: the body may not swing into a wall,
        /// off a ledge or through a door, and the sweep inside that test runs from the cart's real
        /// pose to the candidate, so the chord is checked and not just the endpoint. A turn refused
        /// at its FIRST sub-step is held rather than degraded to a centre pivot.
        ///
        /// 2026-09-16 (WP2 step 1): a refusal at the coarse first sub-step is retried once at a
        /// quarter of the swing, so "accepted 0 of 1.1 degrees" becomes a partial turn wherever one
        /// exists. The retry runs ONLY when a quarter of the swing is strictly finer than the
        /// caller's own sub-step (v2). Otherwise it would be COARSER, and a coarser ladder is not a
        /// refinement: the 180-degree attach pre-aim passes 10 degrees, and a quarter of that swing
        /// is 45 - four single 45-degree jumps checked as an endpoint overlap plus a straight chord,
        /// which can accept a swing the 10-degree ladder correctly refused. Where the quarter step
        /// is not finer than the caller's own, the coarse refusal stands.
        ///
        /// The pivot translates the body by at most 2 * FollowDistance * sin(turn / 2) per call: at
        /// the motor's 55 deg/s over a 0.02 s step that is 2.7 cm, which is why the frame's ground
        /// sample stays good and the translation is kept out of <c>_motionVelocity</c> by the
        /// caller. The plane arithmetic itself is PayloadCartMotionMath.PivotAboutHandle, so the
        /// headless fixture integrates the same geometry the motor runs.
        /// </summary>
        private Quaternion ResolveNpcPivotRotation(ref Vector3 position, Quaternion from, Quaternion to, float stepDegrees)
        {
            float sweep = Quaternion.Angle(from, to);
            if (sweep < .0001f) return from;
            // The handle as it stands NOW, before any of the sub-steps: it is the fixed point of the
            // whole turn, so it must not be re-read from a yaw the loop is changing.
            Vector3 handle = NpcHandlePosition;
            float fromYaw = from.eulerAngles.y;
            Quaternion accepted = from;
            Vector3 acceptedPosition = position;
            // 2026-09-16 (WP2 step 1): two passes, coarse then quarter-step, and the second runs ONLY
            // when the first was refused at its own first sub-step. The motor asks for 1.1 degrees
            // against a 2-degree sub-step, so every refusal was an all-or-nothing veto of the whole
            // turn and "accepted 0 of 1.1 degrees" became a permanent one; a quarter of it is a swing
            // the cart can usually take and is enough to walk the body out of what refused it. The
            // retry is safe to restart from `from` because neither `position` nor `handle` is
            // mutated by the loop. A clear swing still costs exactly ONE CartPoseClear, which is what
            // keeps this off the quiet path: the second pass is reachable only from a blocked step.
            float coarse = Mathf.Max(.5f, stepDegrees);
            float refined = Mathf.Max(.05f, sweep * .25f);
            // v2: a quarter of the swing is a REFINEMENT only while it is finer than what was already
            // tried. For the attach pre-aim (10 degrees, up to 180 of sweep) and for the ladder's own
            // yaw rungs it is coarser, and a coarser retry could accept a pose the fine ladder
            // refused. Where it is not finer the coarse refusal stands and the veto is final.
            int passes = refined < coarse ? 2 : 1;
            for (int pass = 0; pass < passes; pass++)
            {
                float granularity = pass == 0 ? coarse : refined;
                int steps = Mathf.Max(1, Mathf.CeilToInt(sweep / granularity));
                bool refusedOutright = false;
                for (int i = 1; i <= steps; i++)
                {
                    Quaternion candidate = Quaternion.Slerp(from, to, i / (float)steps);
                    PayloadCartMotionMath.PivotAboutHandle(position.x, position.z, handle.x, handle.z,
                        Mathf.DeltaAngle(fromYaw, candidate.eulerAngles.y), out float pivotedX, out float pivotedZ);
                    Vector3 candidatePosition = new Vector3(pivotedX, position.y, pivotedZ);
                    if (!CartPoseClear(candidatePosition, candidate, null)) { refusedOutright = i == 1; break; }
                    accepted = candidate; acceptedPosition = candidatePosition;
                }
                if (!refusedOutright) break;
            }
            position = acceptedPosition;
            return accepted;
        }
        /// <summary>
        /// #874 (2026-09-22): the yaw the parked cart is pre-aimed onto at the attach - square on
        /// the line from the handle the operator has just taken to his own goal.
        ///
        /// Deliberately not a plan: the plan is searched on the first fixed step of the haul, from
        /// whatever pose this swing could actually reach, and every intermediate pose of the swing
        /// is run through the cart's own sweep. This only saves the first plan from starting a
        /// third of a turn out.
        /// </summary>
        private float ResolveNpcAttachYaw(BlacksiteSoldierAI actor)
        {
            if (!(actor.Navigator?.IntendedGoal is Vector3 goal)) return transform.eulerAngles.y;
            Vector3 lead = Vector3.ProjectOnPlane(goal - NpcHandlePosition, Vector3.up);
            return lead.sqrMagnitude > .0001f
                ? Quaternion.LookRotation(lead, Vector3.up).eulerAngles.y + FollowYawOffset
                : transform.eulerAngles.y;
        }
        // #940 S11 U1: SnapToNavMesh and the three recovery rungs teleport the member. A teleport
        // while pushing used to break the leash silently on the next FixedUpdate; the release is now
        // deliberate and logged. Releasing rather than refusing the warp: the recovery ladder only
        // runs when navigation is already broken, and refusing there leaves the member off the mesh
        // AND holding a cart, with no clock to escape either - while a logged release lets the
        // manifest drop the record immediately (it is "no longer pushed", not "path blocked") and
        // the member re-acquire the handle once he is back on the mesh. #1233 S1: a paired entrance
        // never comes here - the coupling carries its own body across (TryCrossPortalServer).
        //
        // The guard is the cart's own IsPushedBy rather than BlacksiteSoldierAI.IsPushingPayload,
        // which reports the manifest RECORD's phase. The two disagree in exactly the state this unit
        // repairs - a released cart whose record is still Carried, or a record dropped while the
        // handle is still held - and only one of them owns the handle.
        internal static void ReleaseForWarpServer(BlacksiteSoldierAI actor)
        {
            var cart = PayloadContract.ActiveCart;
            if (cart != null && cart.IsPushedBy(actor)) cart.ReleaseNpcPusherServer("unsupported");
        }
        // Full authored cart dimensions, with only floor-contact clearance removed. Check intermediate
        // poses as well as the endpoint so a low frame rate cannot skip a thin wall or closed door.
        // 2026-09-16 (WP2 step 4): each refusal names the TEST that refused and, where the test has
        // one, the collider. Recorded in a field rather than returned: the signature is reflected on
        // by BlacksiteLandingCombatChecks (AccessTools.Method + a Harmony prefix) and read by the
        // Unity physics fixture, and a diagnostic is not worth breaking either. The quiet path never
        // touches a collider name - every write below is on a branch that returns false.
        private bool CartPoseClear(Vector3 position, Quaternion rotation, BlacksiteSoldierAI actor)
        {
            // #874 (2026-09-22): the pose this step is swept FROM, and the wheels it has there.
            // The motor asks about its own next step, so both come from the live pose; the planner
            // asks about a step from a pose the cart has not reached yet, so both come from the
            // manoeuvre it is resolving. One predicate either way - a planner with a gentler fit
            // than the motor is a planner that plans steps the motor refuses.
            Vector3 start = _npcHasPoseOrigin ? _npcPoseOrigin : transform.position;
            bool sampled = PayloadCartGroundSampler.TrySampleCartGround(transform, position, rotation, _groundSampleState, out var ground);
            // #1003: floor before clearance. A clear motion box over a pit is still a pit, and an
            // operator walking past one used to push the cart straight off the walkway. The gate is
            // the wheels the cart has now, so a cart already parked at a lip can still be walked
            // back from it; an unreadable current pose demands all four, which is the strict case.
            int currentMask = _npcHasPoseOrigin ? _npcPoseOriginMask
                : PayloadCartGroundSampler.TrySampleCartGround(transform, _groundSampleState, out var current)
                    ? current.FreshMask : PayloadCartMotionMath.AllWheelsMask;
            // v2: the tokens are interned literals and the collider stays a reference, so a refused
            // sub-step allocates nothing and Collider.name is marshalled only by a diagnostic line.
            // _motionBlocker is stale for the two ground tests, which name no collider, so it is
            // cleared for them rather than left to attribute a wall to a ledge.
            if (!sampled) { _npcPoseRefusal = "unsampled"; _motionBlocker = null; return false; }
            if (PayloadCartMotionMath.IsLedgeAhead(currentMask, sampled, ground.FreshMask))
            { _npcPoseRefusal = "ledge"; _motionBlocker = null; return false; }
            if (PayloadCartMotionMath.CanGround(ground.FreshMask, position.y, ground.SupportY + FollowHeight, true))
            {
                position.y = ground.SupportY + FollowHeight;
                rotation = GroundRotation(Quaternion.Euler(0f, rotation.eulerAngles.y, 0f), ground);
                if (position.y > start.y + MotionSkin)
                    start = SweepMotion(start, Vector3.up * (position.y - start.y), rotation);
            }
            if (!MotionPoseClear(position, rotation)) { _npcPoseRefusal = "overlap"; return false; }
            if (Vector3.Distance(SweepMotion(start, position - start, rotation), position) >= 0.03f)
            { _npcPoseRefusal = "sweep"; return false; }
            // A pose that passes clears the record, so no later line can name a collider that
            // refused an earlier candidate. Every reader of _npcPoseRefusal is downstream of this.
            // The wheel mask it resolved is kept so a chained planner probe can use it as the next
            // sub-step's "wheels I have now" rather than sampling the same pose twice.
            _npcPoseResultMask = ground.FreshMask; _npcPoseResultY = position.y;
            _npcPoseRefusal = null; return true;
        }
        /// <summary>
        /// #1233 S1 / #1305 C2: the coupled pair crosses a paired EntranceTeleport as ONE
        /// transaction. The cart half is <see cref="CommitPortalCrossingServer"/>, which a player's
        /// crossing uses too; this wrapper adds the soldier's body. It is taken for the coupling - a
        /// Crossing, its agent position suspended - and carried onto the far navmesh at the handle of
        /// the pose the cart will take; only then does the cart move, the handle held throughout.
        ///
        /// Two kinds of false. A PRECONDITION refusal - this member does not hold the handle, is out
        /// of its reach, or another owner has his body - is a pair still approaching, and nothing is
        /// judged. A far endpoint that cannot bind his agent is answered once: the stuck verdict
        /// latches and the pusher is released `portal`, so the manifest's Drop applies the D3 answer
        /// on this first refusal. The cart's own geometry never refuses (#1305 C2).
        /// </summary>
        internal bool TryCrossPortalServer(BlacksiteSoldierAI actor, int entranceId, bool outsideAfter,
            Vector3 exitActor, Quaternion facing, out string refusal)
        {
            var navigator = actor != null ? actor.Navigator : null;
            if (!IsServer || navigator == null || !IsPushedBy(actor)) { refusal = "the cart is not held by this member"; return false; }
            Vector3 from = actor.transform.position;
            if (Vector3.Distance(from, NpcHandlePosition) > 2f) { refusal = "the pair is still approaching the cart's handle"; return false; }
            float advance = ChoosePortalPose(exitActor, facing, out Vector3 exitPose, out Quaternion exitRotation);
            // The body stands on the handle of the pose the cart takes, so the pair stays coupled. Past
            // the nominal pose that is the chosen pose's stand point behind the handle, snapped to the
            // navmesh, rather than the endpoint's height carried along the lane.
            Vector3 exitBody = exitActor;
            if (advance > 0f)
            {
                exitBody = exitPose + Quaternion.Euler(0f, exitRotation.eulerAngles.y, 0f) * Vector3.forward * FollowDistance;
                if (NavMesh.SamplePosition(exitBody, out NavMeshHit stand, 1f, NavMesh.AllAreas)) exitBody = stand.position;
            }
            if (!navigator.TryHoldForPayloadCoupling(atPortal: true)) { refusal = "another owner holds the operator's body"; return false; }
            _npcCoupledActor = actor; _npcCoupled = true;
            if (!navigator.TryCarryCoupledBody(exitBody, throughPortal: true))
            {
                _npcPoseRefusal = "navmesh"; _motionBlocker = null;
                refusal = $"the operator cannot bind at the exit endpoint {exitBody:F2} ({NpcPoseRefusalDetail})";
                _npcStuckVerdict.NotePortalRefusal();
                ReleaseNpcPusherServer("portal");
                return false;
            }
            // #1054 / #874: both bodies cross a world boundary. Drop the coupling's retained
            // plan and refused ground, all measured in the region they left, and the presentation
            // sample, which differenced across the jump reads as hundreds of metres per second.
            // #1293 R2: the crossing is the pair's progress.
            _npcHasExecutedPose = false;
            NoteNpcProgressServer(actor);
            DropNpcPlan("portal");
            ForgetNpcCoupledGeometry();
            ResetNpcPoseWalking();
            CommitPortalCrossingServer(exitPose, exitRotation, entranceId, outsideAfter, "member=" + _npcPusherMember, from, exitBody);
            refusal = null;
            return true;
        }
        /// <summary>
        /// #1305 C2: vanilla has just moved <paramref name="player"/> through
        /// <paramref name="entrance"/>; if he is pushing this cart, it crosses with him and he keeps
        /// the handle. Host only, from both <see cref="PayloadCartEntranceTeleportPatch"/> postfixes.
        /// They also fire for a teleport a lockdown prefix skipped and, on the host, for the
        /// ClientRpc's send phase: a player still on the near side, or a cart already on the far
        /// one, is a no-op.
        /// </summary>
        internal void CrossPortalWithPlayerServer(PlayerControllerB player, EntranceTeleport entrance)
        {
            if (!IsServer || !IsSpawned || player == null || entrance == null
                || player.actualClientId != _attachedClientId.Value
                || player.isInsideFactory != entrance.isEntranceToBuilding
                || IsOutside == BlacksiteWorld.AfterUsing(entrance))
                return;
            // The door vanilla put him through; the lane is the same as the soldiers'.
            EntranceTeleport exit = entrance.exitScript;
            if (exit == null) return;
            Quaternion facing = Quaternion.LookRotation(
                Vector3.ProjectOnPlane(BlacksiteEntranceInteraction.ExitForward(exit), Vector3.up).normalized);
            // Vanilla has already moved him, so the line's from= is the cart's own near-side pose.
            Vector3 from = transform.position, to = player.transform.position;
            ChoosePortalPose(to, facing, out Vector3 pose, out Quaternion rotation);
            CommitPortalCrossingServer(pose, rotation, entrance.entranceId, BlacksiteWorld.AfterUsing(entrance),
                "player=" + player.actualClientId, from, to);
        }
        // #1305 C2: how far past the nominal far-side pose the cart may stand, and in what steps.
        private const float PortalLaneStepMetres = .25f;
        private const float PortalLaneReachMetres = 1.5f;
        /// <summary>
        /// #1305 C2: the cart's far-side pose for a pusher at <paramref name="exitPusher"/> facing
        /// along the exit lane: its handle at his feet, else the nearest clear pose further along
        /// the lane, else the nominal pose anyway - the cart is never left behind for geometry.
        /// Returns how far past nominal the pose stands. A pure query of the far side only (ground
        /// sample, snap, overlap): nothing is swept from the side the cart is leaving.
        /// </summary>
        private float ChoosePortalPose(Vector3 exitPusher, Quaternion facing, out Vector3 pose, out Quaternion rotation)
        {
            Vector3 lane = facing * Vector3.forward;
            Quaternion yaw = facing * Quaternion.Euler(0f, FollowYawOffset, 0f);
            pose = exitPusher + lane * FollowDistance + Vector3.up * FollowHeight;
            rotation = yaw;
            float chosen = 0f;
            int steps = Mathf.RoundToInt(PortalLaneReachMetres / PortalLaneStepMetres);
            for (int step = 0; step <= steps; step++)
            {
                float advance = step * PortalLaneStepMetres;
                Vector3 candidate = exitPusher + lane * (FollowDistance + advance) + Vector3.up * FollowHeight;
                Quaternion candidateRotation = yaw;
                if (PortalPoseClear(ref candidate, ref candidateRotation))
                { pose = candidate; rotation = candidateRotation; chosen = advance; break; }
            }
            // A refused candidate's collider is not what any later release line should blame.
            _motionBlocker = null;
            return chosen;
        }
        // Clear means the wheels have stable floor within the sampler's reach, the pose is seated on
        // it (a door's exit point need not sit on the floor), and the motion box overlaps nothing there.
        private bool PortalPoseClear(ref Vector3 position, ref Quaternion rotation)
        {
            if (!PayloadCartGroundSampler.TrySampleCartGround(transform, position, rotation, _groundSampleState, out var ground)
                || !ground.HasStableSupport)
                return false;
            position.y = ground.SupportY + FollowHeight;
            rotation = GroundRotation(rotation, ground);
            return MotionPoseClear(position, rotation);
        }
        /// <summary>
        /// #1305 C2: the commit every pusher shares. One server write of the pose - a NetworkTransform
        /// teleport where this peer commits the transform, so clients snap instead of interpolating
        /// across the map - then the cart's own replicated side, which nothing but this and the round
        /// reset (<see cref="ConfigureServer"/>) writes.
        /// </summary>
        private void CommitPortalCrossingServer(Vector3 pose, Quaternion rotation, int entranceId, bool outsideAfter,
            string pusher, Vector3 from, Vector3 to)
        {
            var networkTransform = GetComponent<NetworkTransform>();
            if (networkTransform != null && networkTransform.CanCommitToTransform)
            {
                // Teleport takes the pose in the space the component synchronises.
                Transform space = networkTransform.InLocalSpace ? transform.parent : null;
                networkTransform.Teleport(space != null ? space.InverseTransformPoint(pose) : pose,
                    space != null ? Quaternion.Inverse(space.rotation) * rotation : rotation, transform.localScale);
            }
            else
                transform.SetPositionAndRotation(pose, rotation);
            ResetCartMotion();
            bool armed = outsideAfter && !_outside.Value;
            _outside.Value = outsideAfter;
            if (armed) ContractsBootstrap.Log?.LogInfo("[MoonContracts.Payload] Cart hauled outside; delivery armed.");
            BlacksiteDiagnostics.Log("Cargo", $"{pusher}, pair crossed, entrance={entranceId}, outside={outsideAfter}"
                + $", from={BlacksiteDiagnosticFormat.V(from)}, to={BlacksiteDiagnosticFormat.V(to)}");
        }
        internal bool FitsNpcElevator(System.Func<Vector3, bool> inside)
        {
            foreach (float x in new[] { -.43f, .43f })
                foreach (float z in new[] { -.85f, .85f })
                    if (!inside(transform.position + transform.rotation * new Vector3(x, .3f, z))) return false;
            return true;
        }
        internal void CarryNpcWithElevator(Vector3 delta)
        {
            if (!IsServer || !IsNpcAttached || delta.sqrMagnitude >= 100f) return;
            transform.position += delta; _npcElevatorFrame = Time.frameCount;
            // #1054 rework / #874: the carry moves the cart under both bodies, so the coupling's
            // executed pose is dropped HERE. It cannot be dropped in UpdateNpcHaul's own elevator
            // guard, because FixedUpdate (PayloadCartController.Motion.cs) already returns on the
            // same condition before UpdateNpcHaul is called - that branch was unreachable.
            // #1293 R2: and the carry is the pair's progress, reported for the same reason.
            _npcHasExecutedPose = false;
            var rider = BlacksiteObjectiveManifestController.Resolve(_npcPusher.Value)?.GetComponent<BlacksiteSoldierAI>();
            if (rider != null) NoteNpcProgressServer(rider);
            // The carry moved the cart, so the poses the plan proved are poses the pair no longer
            // stands where it planned from, and an elevator carry is the floor itself moving, so
            // the refused ground is on a storey the pair has left.
            DropNpcPlan("elevator");
            ForgetNpcCoupledGeometry();
            ResetNpcPoseWalking();
        }
        private void ApplyNpcPushLate()
        {
            var actor = IsNpcAttached ? BlacksiteObjectiveManifestController.Resolve(_npcPusher.Value)?.GetComponent<BlacksiteSoldierAI>() : null;
            if (actor == null) { _npcRig = null; _npcPoseActor = null; ResetNpcPoseWalking(); return; }
            EnsureGripAnchors();
            if (_npcPoseActor != actor) { _npcPoseActor = actor; _npcRig = PayloadPushBodyRig.ResolveActor(actor.transform); ResetNpcPoseWalking(); }
            _npcRig?.ApplyActor(actor.transform, _leftGripAnchor, _rightGripAnchor, UpdateNpcPoseWalking(actor));
        }
        // 2026-09-16 (H, #1071 follow-up): the carrier's crouch-to-reach is standing-only for the same
        // reason the player's is - see PayloadPushBodyRig.FitBodyToHandles - and a hauling soldier has
        // been walking under it since #850. The speed is measured HERE, from the actor's own
        // transform, rather than taken from anything the coupling records: the coupling is a
        // host-side action behind an IsServer gate, while this pass is presentation and runs on
        // every peer, so a client would read a permanent zero and keep the broken solve - which is
        // exactly the observer-side half of this defect on the player path.
        // Smoothed on the same constant as the player path, and hysteresed
        // because unlike the player's Walking bool nothing else is holding this edge steady.
        private const float NpcPoseWalkEnterSpeed = 0.12f;
        private const float NpcPoseWalkExitSpeed = 0.05f;
        private const float NpcPoseTeleportStep = 2f;
        private Vector3 _npcPosePosition;
        private bool _npcPoseHasPosition;
        private float _npcPoseSpeed;
        private bool _npcPoseWalking;
        private void ResetNpcPoseWalking() { _npcPoseHasPosition = false; _npcPoseSpeed = 0f; _npcPoseWalking = false; }
        private bool UpdateNpcPoseWalking(BlacksiteSoldierAI actor)
        {
            float dt = Mathf.Max(Time.deltaTime, 0.0001f);
            Vector3 position = actor.transform.position;
            float step = _npcPoseHasPosition ? Vector3.ProjectOnPlane(position - _npcPosePosition, Vector3.up).magnitude : 0f;
            // A single frame's planar step past NpcPoseTeleportStep is not locomotion, it is a world
            // boundary or an elevator carry. The two server seams drop the sample explicitly; this
            // covers the same two on a CLIENT, where those methods return on their IsServer gate.
            if (step > NpcPoseTeleportStep) { _npcPosePosition = position; return _npcPoseWalking; }
            float instant = _npcPoseHasPosition ? step / dt : 0f;
            _npcPosePosition = position; _npcPoseHasPosition = true;
            _npcPoseSpeed = Mathf.Lerp(_npcPoseSpeed, instant, 1f - Mathf.Exp(-12f * dt));
            if (_npcPoseWalking) { if (_npcPoseSpeed < NpcPoseWalkExitSpeed) _npcPoseWalking = false; }
            else if (_npcPoseSpeed > NpcPoseWalkEnterSpeed) _npcPoseWalking = true;
            return _npcPoseWalking;
        }
        // #1305 H1: the Blacksite hull's claim on the cart - its magnet (attaching or docked) or its
        // cable - replicated so every peer's NPC, player and manifest guard reads the same owner.
        private readonly NetworkVariable<ulong> _blacksiteMagnetOwner = new NetworkVariable<ulong>(ulong.MaxValue);
        // #1305 H1 D25: the hover cable set the cart down on the deck; every peer hides it.
        private readonly NetworkVariable<bool> _stowedOnBlacksiteDeck = new NetworkVariable<bool>();
        private Vector3 _blacksiteMagnetLocalPosition;
        private Quaternion _blacksiteMagnetLocalRotation;
        private bool _blacksiteMagnetSeated;
        internal bool IsUsingBlacksiteMagnet(ulong ship) => _blacksiteMagnetOwner.Value == ship;
        internal void CancelBlacksiteMagnetServer() => ReleaseBlacksiteMagnetServer();
        internal bool IsOnBlacksiteMagnet(ulong ship) => _blacksiteMagnetOwner.Value == ship && _blacksiteMagnetSeated;
        /// <summary>#1305 H1 D25: replicated; true once the hover cable has set the cart down on the deck.</summary>
        internal bool IsStowedOnBlacksiteDeck => _stowedOnBlacksiteDeck.Value;
        /// <summary>
        /// #874 Q4 (Lawson's decision 2, 2026-09-24): a HOVERING Blacksite ship winches the cart up
        /// its cable; its magnet works only on the ground (#1305 H1). The cable is the same claim the
        /// magnet is - the replicated <c>_blacksiteMagnetOwner</c> - so every existing reader keeps
        /// its answer: no NPC or player pusher may take the handle, and the manifest's staged-record
        /// test does not read the lift as the cart being moved away. While held the cart's own motion
        /// yields entirely (<see cref="TickBlacksiteMagnet"/>) and the ship writes its pose through
        /// <see cref="SetBlacksiteCablePoseServer"/>; clients and a late joiner see that pose through
        /// the cart's NetworkTransform, as they see a pushed cart.
        /// </summary>
        internal bool HoldOnBlacksiteCableServer(BlacksiteShipController ship)
        {
            if (!IsServer || ship == null || IsAttached || _deliveryStarted || _magnetAttachHost != null || IsDelivered || IsFailed) return false;
            if (_blacksiteMagnetOwner.Value != ulong.MaxValue && _blacksiteMagnetOwner.Value != ship.NetworkObjectId) return false;
            if (_blacksiteCableHeld) return true;
            if (IsNpcAttached) ReleaseNpcPusherServer("blacksiteCable");
            _blacksiteMagnetOwner.Value = ship.NetworkObjectId;
            _blacksiteMagnetSeated = false; _blacksiteCableHeld = true; ResetCartMotion();
            return true;
        }
        /// <summary>#874 Q4: the ship's cable trip moves the held cart; nothing else may.</summary>
        internal void SetBlacksiteCablePoseServer(Vector3 position, Quaternion rotation)
        {
            if (IsServer && _blacksiteCableHeld) transform.SetPositionAndRotation(position, rotation);
        }
        /// <summary>
        /// #874 Q4: the cable trip is over and the cart stands on the deck. From here it is exactly a
        /// cart docked on the magnet: pinned in the ship's frame, carried by the hull, and read as
        /// aboard by <see cref="IsOnBlacksiteMagnet"/>. #1305 H1 D25: and stowed, hidden on every peer.
        /// </summary>
        internal bool SeatOnBlacksiteDeckServer(BlacksiteShipController ship)
        {
            if (!IsServer || ship == null || !_blacksiteCableHeld || _blacksiteMagnetOwner.Value != ship.NetworkObjectId) return false;
            _blacksiteCableHeld = false; SeatOnBlacksiteShipServer(ship); _stowedOnBlacksiteDeck.Value = true;
            PlayPayloadMagnetClangClientRpc(transform.position, 1f, .78f);
            return true;
        }
        private void SeatOnBlacksiteShipServer(BlacksiteShipController ship)
        {
            _blacksiteMagnetSeated = true;
            _blacksiteMagnetLocalPosition = ship.transform.InverseTransformPoint(transform.position);
            _blacksiteMagnetLocalRotation = Quaternion.Inverse(ship.transform.rotation) * transform.rotation;
        }
        // #1305 H1 D25: one-way; the cart is despawned at extraction or round end.
        private void OnStowedOnBlacksiteDeckChanged(bool previous, bool current)
        {
            if (!current) return;
            foreach (Renderer part in GetComponentsInChildren<Renderer>(true)) part.enabled = false;
            foreach (Collider part in GetComponentsInChildren<Collider>(true)) part.enabled = false;
            foreach (Light part in GetComponentsInChildren<Light>(true)) part.enabled = false;
        }
        // #874 Q4: server-only. The replicated half of the claim is _blacksiteMagnetOwner.
        private bool _blacksiteCableHeld;
        internal bool IsOnBlacksiteCable => _blacksiteCableHeld;
        /// <summary>
        /// The Blacksite claim's step, ahead of the haul: true while the cable or the docked magnet
        /// owns the cart's pose. A cart rising onto the magnet is moved by RiseOntoMagnetServer. The
        /// claim is let go when the hull is gone, a player takes the handle, or the cart is resolved.
        /// </summary>
        private bool TickBlacksiteMagnet()
        {
            if (_blacksiteMagnetOwner.Value == ulong.MaxValue) return false;
            var ship = BlacksiteObjectiveManifestController.Resolve(_blacksiteMagnetOwner.Value)?.GetComponent<BlacksiteShipController>();
            // #874 Q4: on the cable the ship owns the pose; the cart's own motion yields entirely.
            if (_blacksiteCableHeld && ship != null && !IsAttached && !IsDelivered && !IsFailed) return true;
            if (ship == null || IsAttached || IsDelivered || IsFailed)
            { ReleaseBlacksiteMagnetServer(); return false; }
            if (!_blacksiteMagnetSeated) return false;
            transform.SetPositionAndRotation(ship.transform.TransformPoint(_blacksiteMagnetLocalPosition), ship.transform.rotation * _blacksiteMagnetLocalRotation);
            return true;
        }
        private void ReleaseBlacksiteMagnetServer()
        {
            if (!IsServer) return;
            _blacksiteMagnetOwner.Value = ulong.MaxValue; _blacksiteMagnetSeated = false; _blacksiteCableHeld = false;
            _magnetAttachHost = null; ResetCartMotion();
        }
        internal bool MarkBlacksiteExtractedServer()
        {
            if (!IsServer || IsAttached || IsNpcAttached || _deliveryStarted || IsDelivered || IsFailed) return false;
            _deliveryStarted = true;
            PayloadContract.NotifyFailed(this, "Blacksite extracted the payload aboard its ship");
            _failed.Value = true;
            ShowPayloadTipClientRpc("Payload extracted", "Blacksite departed with the cart.", true);
            return true;
        }
    }
}
