using UnityEngine;
using UnityEngine.AI;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal static class BlacksiteFormationPlanner
    {
        internal static BlacksiteSoldierTacticalRole ResolveRole(int memberIndex, int memberCount)
        {
            int index = Mathf.Max(0, memberIndex);
            int count = Mathf.Max(1, memberCount);
            if (index == 0)
                return BlacksiteSoldierTacticalRole.Leader;
            if (index == 1)
                return BlacksiteSoldierTacticalRole.Pointman;
            if (count >= 3 && index == count - 1)
                return BlacksiteSoldierTacticalRole.RearGuard;
            if (count >= 4 && index == count - 2)
                return BlacksiteSoldierTacticalRole.Support;
            return BlacksiteSoldierTacticalRole.Rifleman;
        }

        internal static BlacksiteFormationKind SelectMovementFormation(
            bool outside,
            Vector3 anchor)
        {
            if (outside)
                return BlacksiteFormationKind.LooseWedge;

            if (NavMesh.SamplePosition(anchor, out NavMeshHit sampled, 2f, NavMesh.AllAreas)
                && NavMesh.FindClosestEdge(sampled.position, out NavMeshHit edge, NavMesh.AllAreas)
                && Vector3.Distance(sampled.position, edge.position) >= 2.35f)
            {
                return BlacksiteFormationKind.SpreadSecurity;
            }

            return BlacksiteFormationKind.StaggeredColumn;
        }

        internal static BlacksiteMemberOrder BuildMemberOrder(
            BlacksiteSquadOrder order,
            BlacksiteSoldierTacticalRole role,
            int memberIndex,
            int memberCount,
            int animationSeed,
            bool isOperator)
        {
            Vector3 forward = order.Forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.001f)
                forward = Vector3.forward;
            forward.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;

            Vector2 local = GetFormationOffset(
                order.Formation,
                role,
                memberIndex,
                memberCount,
                isOperator);
            float presentationSide = SignedUnit(animationSeed, 101) * 0.22f;
            float presentationBack = SignedUnit(animationSeed, 113) * 0.18f;
            Vector3 candidate = order.Anchor
                + right * (local.x + presentationSide)
                + forward * (local.y + presentationBack);

            bool useAssignedSweepPoint = order.HasAssignedSweepPoint
                && (order.Kind == BlacksiteSquadOrderKind.TrackDrive
                    || order.Kind == BlacksiteSquadOrderKind.SearchLastKnown);

            float sampleRadius = order.Formation == BlacksiteFormationKind.StaggeredColumn
                || order.Formation == BlacksiteFormationKind.BoardingColumn
                    ? 1.25f
                    : 2.5f;
            Vector3 destination;
            if (useAssignedSweepPoint)
            {
                destination = order.AssignedSweepPoint;
            }
            else
            {
                destination = NavMesh.SamplePosition(
                        candidate,
                        out NavMeshHit hit,
                        sampleRadius,
                        NavMesh.AllAreas)
                    ? hit.position
                    : candidate;
            }

            Vector3 preferredFacing;
            if (order.ExactContact
                || order.Kind == BlacksiteSquadOrderKind.SearchLastKnown
                || order.Kind == BlacksiteSquadOrderKind.RecoverDrive)
            {
                preferredFacing = order.Anchor;
            }
            else if (role == BlacksiteSoldierTacticalRole.RearGuard)
            {
                preferredFacing = destination - forward * 4f;
            }
            else if (!isOperator
                && (order.Formation == BlacksiteFormationKind.SpreadSecurity
                    || order.Formation == BlacksiteFormationKind.InteractionStack
                    || order.Formation == BlacksiteFormationKind.EscortDiamond)
                && Mathf.Abs(local.x) > 0.1f)
            {
                preferredFacing = destination
                    + forward * 2.5f
                    + right * Mathf.Sign(local.x) * 3f;
            }
            else
            {
                preferredFacing = destination + forward * 4f;
            }
            float separation = order.Formation == BlacksiteFormationKind.StaggeredColumn
                || order.Formation == BlacksiteFormationKind.BoardingColumn
                    ? 0.75f
                    : 1.15f;
            float tolerance = isOperator
                ? 0.45f
                : order.Formation == BlacksiteFormationKind.LooseWedge ? 1.35f : 0.95f;

            return new BlacksiteMemberOrder(
                order,
                destination,
                preferredFacing,
                separation,
                tolerance,
                isOperator);
        }

        internal static Vector3 BuildUncertainSearchPoint(
            Vector3 center,
            float uncertaintyRadius,
            int memberIndex,
            int animationSeed)
        {
            float radius = Mathf.Max(1.25f, uncertaintyRadius);
            float angle = (memberIndex * 137.50777f + Unit(animationSeed, 149) * 70f) * Mathf.Deg2Rad;
            float distance = radius * Mathf.Lerp(0.42f, 0.92f, Unit(animationSeed, 163));
            Vector3 candidate = center + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * distance;
            return NavMesh.SamplePosition(candidate, out NavMeshHit hit, 4f, NavMesh.AllAreas)
                ? hit.position
                : center;
        }

        private static Vector2 GetFormationOffset(
            BlacksiteFormationKind formation,
            BlacksiteSoldierTacticalRole role,
            int memberIndex,
            int memberCount,
            bool isOperator)
        {
            if (formation == BlacksiteFormationKind.InteractionStack)
            {
                if (isOperator)
                    return Vector2.zero;
                switch (role)
                {
                    case BlacksiteSoldierTacticalRole.Pointman:
                        return new Vector2(-1.8f, -1.2f);
                    case BlacksiteSoldierTacticalRole.Rifleman:
                        return new Vector2((memberIndex & 1) == 0 ? -2.7f : 2.7f, -2.2f);
                    case BlacksiteSoldierTacticalRole.RearGuard:
                        return new Vector2(0f, -5.2f);
                    default:
                        return new Vector2(1.8f, -1.5f);
                }
            }

            if (formation == BlacksiteFormationKind.StaggeredColumn
                || formation == BlacksiteFormationKind.BoardingColumn)
            {
                int position = formation == BlacksiteFormationKind.BoardingColumn
                    ? BoardingPosition(role, memberIndex)
                    : memberIndex;
                float side = position == 0 ? 0f : ((position & 1) == 0 ? -0.42f : 0.42f);
                return new Vector2(side, -position * 1.55f);
            }

            if (formation == BlacksiteFormationKind.SearchSectors)
            {
                float angle = memberIndex * (360f / Mathf.Max(1, memberCount)) * Mathf.Deg2Rad;
                float radius = 3.2f + (memberIndex % 2) * 1.25f;
                return new Vector2(Mathf.Sin(angle) * radius, Mathf.Cos(angle) * radius);
            }

            if (formation == BlacksiteFormationKind.EscortDiamond)
            {
                switch (role)
                {
                    case BlacksiteSoldierTacticalRole.Pointman:
                        return new Vector2(0f, 3f);
                    case BlacksiteSoldierTacticalRole.RearGuard:
                        return new Vector2(0f, -3.4f);
                    case BlacksiteSoldierTacticalRole.Rifleman:
                        return new Vector2((memberIndex & 1) == 0 ? -2.5f : 2.5f, 0f);
                    case BlacksiteSoldierTacticalRole.Leader:
                        return new Vector2(-1.35f, -1.4f);
                    case BlacksiteSoldierTacticalRole.Support:
                        return Vector2.zero;
                }
            }

            if (formation == BlacksiteFormationKind.SpreadSecurity)
            {
                float angle = (-75f + memberIndex * (150f / Mathf.Max(1, memberCount - 1))) * Mathf.Deg2Rad;
                float radius = 3f + (memberIndex % 2) * 0.8f;
                return new Vector2(Mathf.Sin(angle) * radius, Mathf.Cos(angle) * radius - 1f);
            }

            switch (role)
            {
                case BlacksiteSoldierTacticalRole.Pointman:
                    return new Vector2(0f, 3.2f);
                case BlacksiteSoldierTacticalRole.Leader:
                    return new Vector2(0f, 0f);
                case BlacksiteSoldierTacticalRole.Support:
                    return new Vector2(0f, -2.4f);
                case BlacksiteSoldierTacticalRole.RearGuard:
                    return new Vector2(0f, -4.8f);
                default:
                    return new Vector2((memberIndex & 1) == 0 ? -2.7f : 2.7f, -0.8f);
            }
        }

        private static int BoardingPosition(BlacksiteSoldierTacticalRole role, int memberIndex)
        {
            switch (role)
            {
                case BlacksiteSoldierTacticalRole.Support:
                    return 0;
                case BlacksiteSoldierTacticalRole.Pointman:
                    return 1;
                case BlacksiteSoldierTacticalRole.Leader:
                    return 2;
                case BlacksiteSoldierTacticalRole.Rifleman:
                    return 3 + (memberIndex & 1);
                default:
                    return 5;
            }
        }

        private static float SignedUnit(int seed, int salt)
        {
            return Unit(seed, salt) * 2f - 1f;
        }

        private static float Unit(int seed, int salt)
        {
            unchecked
            {
                uint value = (uint)seed ^ ((uint)salt * 0x9E3779B9u);
                value ^= value >> 16;
                value *= 0x7FEB352Du;
                value ^= value >> 15;
                value *= 0x846CA68Bu;
                value ^= value >> 16;
                return (value & 0x00FFFFFFu) / 16777215f;
            }
        }
    }
}
