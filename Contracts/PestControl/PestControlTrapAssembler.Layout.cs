using System.Collections.Generic;
using UnityEngine;
using Y4NGZCompany.Facility.Interior;

namespace Y4NGZCompany.Contracts.PestControl
{
    internal static partial class PestControlTrapAssembler
    {
        /// <summary>
        /// Resolves the lever floor pose without constructing visuals, so placement can reserve
        /// the same interaction envelope the assembler will use.
        /// </summary>
        internal static bool TryResolveLeverPose(
            Vector3 center,
            float yaw,
            float side,
            out Vector3 leverPosition,
            out Quaternion leverRotation)
        {
            List<Vector3> entrances = InteriorAnchorService.GetInteriorEntrancePositions();
            Vector3 entrance = InteriorAnchorService.GetNearestEntrancePoint(entrances, center);
            Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);
            Vector3 right = rotation * Vector3.right;
            Vector3 forward = rotation * Vector3.forward;

            Vector3 toEntrance = entrance - center;
            toEntrance.y = 0f;
            if (toEntrance.sqrMagnitude < 1e-4f)
                toEntrance = forward;

            Vector3 outward = forward;
            Vector3 tangent = right;
            float bestDot = float.MinValue;
            Vector3[] axes = { forward, -forward, right, -right };
            Vector3[] tangents = { right, right, forward, forward };
            for (int i = 0; i < axes.Length; i++)
            {
                float dot = Vector3.Dot(axes[i], toEntrance);
                if (dot <= bestDot)
                    continue;

                bestDot = dot;
                outward = axes[i];
                tangent = tangents[i];
            }

            // The outset used to be a flat LeverOutsetMeters past the cage edge, probed at one
            // distance with a few tangent slides. In a corridor that single ring is inside the
            // wall, the downward probe finds no floor, and the contract was failed over a lever.
            // Walk the outset inward instead and keep the first pose that lands on real floor, so
            // a tight room still gets a reachable lever near the cage.
            float halfSide = side * 0.5f;
            float[] outsets =
            {
                LeverOutsetMeters,
                Mathf.Lerp(LeverMinimumOutsetMeters, LeverOutsetMeters, 0.66f),
                Mathf.Lerp(LeverMinimumOutsetMeters, LeverOutsetMeters, 0.33f),
                LeverMinimumOutsetMeters
            };
            float[] slides = { 0f, 1.0f, -1.0f, 2.0f, -2.0f };
            Vector3 chosen = center + outward * (halfSide + LeverOutsetMeters);
            bool validated = false;
            StartOfRound start = StartOfRound.Instance;
            int mask = start != null ? start.collidersAndRoomMaskAndDefault : ~0;

            for (int o = 0; o < outsets.Length && !validated; o++)
            {
                float outset = halfSide + outsets[o];
                for (int i = 0; i < slides.Length; i++)
                {
                    Vector3 candidate = center + outward * outset + tangent * slides[i];
                    InteriorAnchorService.RecordPhysicsQuery();
                    if (!Physics.Raycast(candidate + Vector3.up * 2.5f, Vector3.down, out RaycastHit hit, 6f, mask, QueryTriggerInteraction.Ignore))
                        continue;

                    chosen = hit.point;
                    validated = true;
                    break;
                }
            }

            if (!validated)
                chosen = new Vector3(chosen.x, center.y, chosen.z);

            leverPosition = chosen;
            // +Z is the OPERATOR side. The authored base models its back (Blender +Y, which the
            // kit's -90 X import maps to local -Z) toward the corral, and the arm throws toward
            // local +Z, so the lever has to face outward or the player ends up behind it and the
            // handle swings away from them.
            leverRotation = Quaternion.LookRotation(outward, Vector3.up);
            return validated;
        }
    }
}
