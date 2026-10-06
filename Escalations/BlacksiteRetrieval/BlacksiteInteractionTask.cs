using UnityEngine;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal enum BlacksiteInteractionKind
    {
        None = 0,
        Door = 1,
        Entrance = 2,
        Elevator = 4,
        ContainerOverride = 5,
        CacheOverride = 6
    }

    internal enum BlacksiteInteractionPhase
    {
        None = 0,
        Reserved = 1,
        Queued = 2,
        Approach = 3,
        Align = 4,
        Perform = 5,
        Confirm = 6,
        Complete = 8,
        Failed = 9
    }

    internal enum BlacksiteInteractionTickResult
    {
        Inactive = 0,
        Running = 1,
        Complete = 2,
        Failed = 4
    }

    internal sealed class BlacksiteInteractionTask
    {
        internal BlacksiteInteractionKind Kind { get; }
        internal object Target { get; }
        internal object Context { get; set; }
        internal Vector3 ApproachPosition { get; set; }
        internal Vector3 AlignPosition { get; set; }
        internal Vector3 ConfirmPosition { get; set; }
        internal Vector3 FinalDestination { get; set; }
        internal BlacksiteInteractionPhase Phase { get; private set; }
        internal float CreatedAt { get; }
        internal float PhaseStartedAt { get; private set; }
        internal float Deadline { get; set; }
        internal float ProgressSeconds { get; set; }
        internal float RequiredSeconds { get; set; }
        internal bool GesturePlayed { get; set; }
        internal bool OperationIssued { get; set; }
        internal string FailureReason { get; set; }
        internal string Label { get; }

        internal bool IsTerminal => Phase == BlacksiteInteractionPhase.Complete
            || Phase == BlacksiteInteractionPhase.Failed;

        internal BlacksiteInteractionTask(
            BlacksiteInteractionKind kind,
            object target,
            Vector3 approachPosition,
            Vector3 finalDestination,
            string label)
        {
            Kind = kind;
            Target = target;
            ApproachPosition = approachPosition;
            AlignPosition = approachPosition;
            ConfirmPosition = approachPosition;
            FinalDestination = finalDestination;
            Label = string.IsNullOrWhiteSpace(label) ? kind.ToString() : label;
            CreatedAt = Time.time;
            PhaseStartedAt = Time.time;
            Phase = BlacksiteInteractionPhase.Reserved;
        }

        internal void TransitionTo(BlacksiteInteractionPhase phase)
        {
            if (Phase == phase)
                return;

            Phase = phase;
            PhaseStartedAt = Time.time;
        }
    }
}
