using Unity.Netcode;
using UnityEngine;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal enum BlacksiteRetrievalMissionState
    {
        Scheduled = 0,
        Inbound = 1,
        Deploying = 2,
        Infiltrating = 3,
        ApproachingExpectedDrive = 4,
        Overriding = 5,
        Inspecting = 6,
        TrackingMissingDrive = 7,
        RecoveringDrive = 8,
        EscortingCarrier = 9,
        Extracting = 10,
        Recovered = 11,
        Denied = 12,
        Aborted = 13
    }

    internal enum BlacksiteHardDriveState
    {
        AtExpectedTarget = 0,
        Loose = 1,
        PlayerHeld = 2,
        SoldierHeld = 3,
        InShip = 4,
        Extracted = 5,
        Missing = 6
    }

    internal enum BlacksiteRetrievalOutcome
    {
        Pending = 0,
        Recovered = 1,
        Denied = 2,
        Aborted = 3
    }

    internal readonly struct BlacksiteRetrievalTargetPose
    {
        internal Vector3 Position { get; }
        internal Quaternion Rotation { get; }
        internal NetworkObject ExpectedTarget { get; }
        internal NetworkObject SecondaryTarget { get; }
        internal NetworkObject Drive { get; }
        internal string Source { get; }

        internal BlacksiteRetrievalTargetPose(
            Vector3 position,
            Quaternion rotation,
            NetworkObject expectedTarget,
            NetworkObject secondaryTarget,
            NetworkObject drive,
            string source)
        {
            Position = position;
            Rotation = rotation;
            ExpectedTarget = expectedTarget;
            SecondaryTarget = secondaryTarget;
            Drive = drive;
            Source = source ?? string.Empty;
        }
    }
}
