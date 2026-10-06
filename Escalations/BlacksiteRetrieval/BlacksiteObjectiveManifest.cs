using System;
using Unity.Netcode;
using UnityEngine;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // #964 S14 U2: Witness is APPENDED. This enum is serialized as a byte at :20 below, so every
    // value the network already carries keeps its number; the native fixture's BlacksiteDutyChecks
    // pins the list and the wire values by name so a reorder fails there rather than on the wire.
    // A Witness record is an agent, not a prop: it has no GrabbableObject, no cargo slot and no
    // ground drop point, and the manifest reaches it through custody instead of through hands.
    internal enum BlacksiteObjectiveKind : byte { ShadowDrive, Survey, Apparatus, Payload, Loot, Salvage, Witness }
    internal enum BlacksiteObjectivePhase : byte { Available, Reserved, Carried, Loaded, Extracted, Unavailable, Staged }
    internal struct BlacksiteObjectiveSnapshot : INetworkSerializable, IEquatable<BlacksiteObjectiveSnapshot>
    {
        internal ulong ObjectId, CarrierId;
        internal BlacksiteObjectiveKind Kind;
        internal BlacksiteObjectivePhase Phase;
        internal int Slot;
        internal Vector3 Position, CargoLocalPosition;
        internal float CargoLocalYaw;
        internal bool Outside, Acquired, ContainerCargo;
        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref ObjectId); serializer.SerializeValue(ref CarrierId);
            serializer.SerializeValue(ref Kind); serializer.SerializeValue(ref Phase); serializer.SerializeValue(ref Slot);
            serializer.SerializeValue(ref CargoLocalPosition); serializer.SerializeValue(ref CargoLocalYaw); serializer.SerializeValue(ref Position); serializer.SerializeValue(ref Outside); serializer.SerializeValue(ref Acquired); serializer.SerializeValue(ref ContainerCargo);
        }
        public bool Equals(BlacksiteObjectiveSnapshot x) => ObjectId == x.ObjectId && CarrierId == x.CarrierId && Kind == x.Kind && Phase == x.Phase && Slot == x.Slot && Position == x.Position && Acquired == x.Acquired && Outside == x.Outside && CargoLocalPosition == x.CargoLocalPosition && CargoLocalYaw == x.CargoLocalYaw && ContainerCargo == x.ContainerCargo;
    }
}
