using System;
using Unity.Netcode;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal enum BlacksiteWeaponState : byte
    {
        Ready = 0,
        FiringBurst = 1,
        Recovering = 2,
        Reloading = 3,
        GrenadeCommitted = 4,
        Disabled = 5
    }

    internal struct BlacksiteWeaponSnapshot : INetworkSerializable, IEquatable<BlacksiteWeaponSnapshot>
    {
        internal BlacksiteWeaponState State;
        internal byte Magazine;
        internal byte Grenades;
        internal ushort Sequence;

        internal static BlacksiteWeaponSnapshot CreateReady(int magazine, int grenades)
        {
            return new BlacksiteWeaponSnapshot
            {
                State = BlacksiteWeaponState.Ready,
                Magazine = (byte)Math.Max(0, Math.Min(byte.MaxValue, magazine)),
                Grenades = (byte)Math.Max(0, Math.Min(byte.MaxValue, grenades)),
                Sequence = 1
            };
        }

        public void NetworkSerialize<T>(BufferSerializer<T> serializer)
            where T : IReaderWriter
        {
            byte state = (byte)State;
            serializer.SerializeValue(ref state);
            serializer.SerializeValue(ref Magazine);
            serializer.SerializeValue(ref Grenades);
            serializer.SerializeValue(ref Sequence);
            if (serializer.IsReader)
                State = (BlacksiteWeaponState)state;
        }

        public bool Equals(BlacksiteWeaponSnapshot other)
        {
            return State == other.State
                && Magazine == other.Magazine
                && Grenades == other.Grenades
                && Sequence == other.Sequence;
        }

        public override bool Equals(object obj)
        {
            return obj is BlacksiteWeaponSnapshot other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = (int)State;
                hash = hash * 397 ^ Magazine;
                hash = hash * 397 ^ Grenades;
                hash = hash * 397 ^ Sequence;
                return hash;
            }
        }
    }
}
