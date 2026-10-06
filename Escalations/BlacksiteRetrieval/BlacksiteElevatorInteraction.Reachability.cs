using UnityEngine;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // #1305 G1 (R15): what the route service reads of the round's car. Read-only: no lane or car state.
    internal sealed partial class BlacksiteElevatorInteraction
    {
        /// <summary>M5: whether a point lies in the live car's column (<see cref="TryGetCarColumn"/>); false with no car.</summary>
        internal static bool InCarColumn(Vector3 point) => TryGetCarColumn(out Bounds column) && column.Contains(point);

        /// <summary>
        /// The live car's column: the shaft <see cref="OwnsOffMeshLink"/> reads, expanded the same two steps up and
        /// down so an endpoint laid on the car floor is inside it. False with no car.
        /// </summary>
        internal static bool TryGetCarColumn(out Bounds column)
        {
            column = default;
            MineshaftElevatorController car = RoundManager.Instance != null
                ? RoundManager.Instance.currentMineshaftElevator
                : null;
            if (car == null || car.elevatorBounds == null) return false;
            column = car.elevatorBounds.bounds;
            column.Expand(new Vector3(0f, BlacksitePhysicalStep.StepHeight * 2f, 0f));
            return true;
        }

        /// <summary>
        /// #874 N6: the round's car, measured as the link measures it: its two landings (the car is not a NavMesh
        /// link, so the route service crosses it between them) and its controller, the ElevatorCar leg's handle.
        /// </summary>
        internal static bool TryGetCar(out Vector3 top, out Vector3 bottom, out object car)
        {
            top = bottom = default;
            car = null;
            NativeMineshaftAdapter adapter = CurrentCar();
            if (adapter == null) return false;
            top = adapter.LandingOf(false);
            bottom = adapter.LandingOf(true);
            car = adapter.Controller;
            return true;
        }
    }
}
