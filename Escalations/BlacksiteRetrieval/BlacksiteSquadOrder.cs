using UnityEngine;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal enum BlacksiteSoldierTacticalRole : byte
    {
        Leader = 0,
        Pointman = 1,
        Rifleman = 2,
        Support = 3,
        RearGuard = 4
    }

    internal enum BlacksiteSquadOrderKind : byte
    {
        Deploy = 0,
        Infiltrate = 1,
        ApproachObjective = 2,
        OverrideObjective = 3,
        InspectObjective = 4,
        TrackDrive = 5,
        RecoverDrive = 6,
        Contact = 7,
        SearchLastKnown = 8,
        EscortCarrier = 9,
        Extract = 10,
        Board = 11,
        Hold = 12
    }

    internal enum BlacksiteFormationKind : byte
    {
        LooseWedge = 0,
        StaggeredColumn = 1,
        SpreadSecurity = 2,
        InteractionStack = 3,
        SearchSectors = 4,
        EscortDiamond = 5,
        BoardingColumn = 6
    }

    internal readonly struct BlacksiteSquadOrder
    {
        internal int Sequence { get; }
        internal BlacksiteSquadOrderKind Kind { get; }
        internal BlacksiteFormationKind Formation { get; }
        internal Vector3 Anchor { get; }
        internal Vector3 Forward { get; }
        internal float Confidence { get; }
        internal float UncertaintyRadius { get; }
        internal bool ExactContact { get; }
        internal bool HasAssignedSweepPoint { get; }
        internal Vector3 AssignedSweepPoint { get; }

        internal BlacksiteSquadOrder(
            int sequence,
            BlacksiteSquadOrderKind kind,
            BlacksiteFormationKind formation,
            Vector3 anchor,
            Vector3 forward,
            float confidence = 0f,
            float uncertaintyRadius = 0f,
            bool exactContact = false,
            bool hasAssignedSweepPoint = false,
            Vector3 assignedSweepPoint = default)
        {
            Sequence = sequence;
            Kind = kind;
            Formation = formation;
            Anchor = anchor;
            Forward = forward.sqrMagnitude > 0.001f ? forward.normalized : Vector3.forward;
            Confidence = Mathf.Clamp01(confidence);
            UncertaintyRadius = Mathf.Max(0f, uncertaintyRadius);
            ExactContact = exactContact;
            HasAssignedSweepPoint = hasAssignedSweepPoint;
            AssignedSweepPoint = assignedSweepPoint;
        }
    }

    // #874 S1.2: an order that ends in an interaction carries that interaction's reach contract.
    // Generic arrival is not proof an action is executable, so the profile states where movement
    // must stop, the gate the action itself enforces, and whether facing is part of the action.
    internal readonly struct BlacksiteApproachProfile
    {
        // A NavMeshAgent with autoBraking settles anywhere inside stoppingDistance, and the issued
        // destination is itself a sampled navigation point. Both are budgeted before the gate.
        // #874 S4.2: the value lives in the Unity-free BlacksiteArrivalPolicy so the arrival
        // contract and this profile cannot drift apart. The number is unchanged.
        internal const float SettleSlack = BlacksiteArrivalPolicy.SettleSlack;

        internal bool HasValue { get; }
        internal float StopDistance { get; }
        internal float ReachRadius { get; }
        internal Vector3 FacingPoint { get; }
        internal bool RequiresFacing { get; }
        internal bool OperatorLane { get; }

        internal BlacksiteApproachProfile(float stopDistance, float reachRadius, Vector3 facingPoint,
            bool requiresFacing = true, bool operatorLane = true)
        {
            StopDistance = Mathf.Max(.15f, stopDistance);
            ReachRadius = Mathf.Max(.2f, reachRadius);
            FacingPoint = facingPoint;
            RequiresFacing = requiresFacing;
            OperatorLane = operatorLane;
            HasValue = true;
        }

        // Worst-case standing distance from the issued destination once the agent settles.
        internal float SettledDistance => StopDistance + SettleSlack;
        internal bool SettlesInsideReach => HasValue && SettledDistance < ReachRadius;
        // How far the issued destination may be relocated from the action's own anchor and still
        // leave the gate satisfiable from a settled stop.
        internal float RelocationBudget => HasValue ? Mathf.Max(0f, ReachRadius - SettledDistance) : 0f;
    }

    internal readonly struct BlacksiteMemberOrder
    {
        internal BlacksiteSquadOrder SquadOrder { get; }
        internal Vector3 Destination { get; }
        internal Vector3 PreferredFacing { get; }
        internal float SeparationRadius { get; }
        internal float ArrivalTolerance { get; }
        internal bool IsInteractionOperator { get; }
        internal bool IsPlayerResponse { get; }
        internal bool TargetOutside { get; }
        internal bool HasAssignedSweepPoint { get; }
        internal Vector3 AssignedSweepPoint { get; }
        internal BlacksiteApproachProfile Approach { get; }
        // #874 S4.2: which duty issued this order, when the duty board issued it. Without it the
        // [UAT][Motion] issuer field could only ever print order:<squad kind>, and the S4.0
        // contract's duty:<kind> token had no source. Stamped once, where the planner issues it.
        internal bool HasDutyKind { get; }
        internal BlacksiteJobKind DutyKind { get; }
        // #1219 E2: the pace the issuing duty declares (BlacksitePatrolPolicy.ResolvePace applies it,
        // under the named escalations). Normal unless the duty says otherwise.
        internal BlacksiteOrderPace Pace { get; }

        /// <summary>The S4.0 [UAT][Motion] issuer token for every destination this order sets.</summary>
        internal string IssuerTag => HasDutyKind ? "duty:" + DutyKind : "order:" + SquadOrder.Kind;

        internal BlacksiteMemberOrder(BlacksiteSquadOrder squadOrder, Vector3 destination,
            Vector3 preferredFacing, float separationRadius, float arrivalTolerance, bool isInteractionOperator,
            BlacksiteApproachProfile approach = default)
            : this(squadOrder, destination, preferredFacing, separationRadius, arrivalTolerance, isInteractionOperator, false, false, approach) { }

        internal BlacksiteMemberOrder(
            BlacksiteSquadOrder squadOrder,
            Vector3 destination,
            Vector3 preferredFacing,
            float separationRadius,
            float arrivalTolerance,
            bool isInteractionOperator,
            bool isPlayerResponse,
            bool targetOutside,
            BlacksiteApproachProfile approach = default,
            bool hasDutyKind = false,
            BlacksiteJobKind dutyKind = default,
            BlacksiteOrderPace pace = BlacksiteOrderPace.Normal)
        {
            Approach = approach;
            SquadOrder = squadOrder;
            Destination = destination;
            PreferredFacing = preferredFacing;
            SeparationRadius = separationRadius;
            // #1305: an Infiltrate goal is always a door, completed at the hand-off its route ends inside.
            ArrivalTolerance = squadOrder.Kind == BlacksiteSquadOrderKind.Infiltrate ? BlacksiteArrivalPolicy.EntranceHandoffDistance : arrivalTolerance;
            IsInteractionOperator = isInteractionOperator;
            IsPlayerResponse = isPlayerResponse; TargetOutside = targetOutside;
            HasAssignedSweepPoint = squadOrder.HasAssignedSweepPoint;
            AssignedSweepPoint = squadOrder.AssignedSweepPoint;
            HasDutyKind = hasDutyKind; DutyKind = dutyKind; Pace = pace;
        }

        /// <summary>The same order, stamped with the duty that issued it.</summary>
        internal BlacksiteMemberOrder WithDutyKind(BlacksiteJobKind kind)
            => new BlacksiteMemberOrder(SquadOrder, Destination, PreferredFacing, SeparationRadius,
                ArrivalTolerance, IsInteractionOperator, IsPlayerResponse, TargetOutside, Approach, true, kind, Pace);

        /// <summary>#1219 E2: the same order at <paramref name="pace"/>.</summary>
        internal BlacksiteMemberOrder WithPace(BlacksiteOrderPace pace)
            => new BlacksiteMemberOrder(SquadOrder, Destination, PreferredFacing, SeparationRadius,
                ArrivalTolerance, IsInteractionOperator, IsPlayerResponse, TargetOutside, Approach, HasDutyKind, DutyKind, pace);
    }
}
