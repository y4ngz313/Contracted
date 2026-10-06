using System;
using Unity.Netcode;
using UnityEngine;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal enum BlacksiteSoldierReactionKind : byte
    {
        None,
        Noise,
        LoudNoise,
        Explosion,
        DirectDamage,
        Stun,
        EnemyCollision,
    }

    internal struct BlacksiteSoldierSpawnState : INetworkSerializable, IEquatable<BlacksiteSoldierSpawnState>
    {
        internal bool IsConfigured;
        internal ulong SquadId;
        internal int MemberIndex;
        internal int FormationSlot;
        internal Vector3 FormationOffset;
        internal int AnimationSeed;
        internal NetworkObjectReference MissionReference;
        internal byte RoleValue;
        internal byte TacticalRoleValue;
        internal byte Equipment; // 0 legacy/fixture, 1 baton raider, 2 rifle guard; stable for the entire life.
        internal int Health;
        internal bool IsDead;
        internal bool IsStunned;
        internal double StunEndServerTime;
        internal byte ReactionValue;
        internal ushort ReactionSequence;
        internal Vector3 StimulusPosition;
        internal ulong LastAttackerClientId;

        internal BlacksiteSoldierRole Role => (BlacksiteSoldierRole)RoleValue;
        internal BlacksiteSoldierTacticalRole TacticalRole => (BlacksiteSoldierTacticalRole)TacticalRoleValue;
        internal BlacksiteSoldierReactionKind Reaction => (BlacksiteSoldierReactionKind)ReactionValue;

        internal static BlacksiteSoldierSpawnState Create(
            ulong squadId,
            int memberIndex,
            BlacksiteSoldierRole role,
            BlacksiteSoldierTacticalRole tacticalRole,
            int formationSlot,
            Vector3 formationOffset,
            int animationSeed,
            NetworkObjectReference missionReference,
            int health, byte equipment = 0)
        {
            return new BlacksiteSoldierSpawnState
            {
                IsConfigured = true,
                SquadId = squadId,
                MemberIndex = memberIndex,
                FormationSlot = formationSlot,
                FormationOffset = formationOffset,
                AnimationSeed = animationSeed,
                MissionReference = missionReference,
                RoleValue = (byte)role,
                TacticalRoleValue = (byte)tacticalRole,
                Equipment = equipment,
                Health = Mathf.Max(0, health),
                IsDead = false,
                IsStunned = false,
                StunEndServerTime = 0d,
                ReactionValue = (byte)BlacksiteSoldierReactionKind.None,
                ReactionSequence = 0,
                StimulusPosition = Vector3.zero,
                LastAttackerClientId = ulong.MaxValue,
            };
        }

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref IsConfigured);
            serializer.SerializeValue(ref SquadId);
            serializer.SerializeValue(ref MemberIndex);
            serializer.SerializeValue(ref FormationSlot);
            serializer.SerializeValue(ref FormationOffset);
            serializer.SerializeValue(ref AnimationSeed);
            serializer.SerializeValue(ref MissionReference);
            serializer.SerializeValue(ref RoleValue);
            serializer.SerializeValue(ref TacticalRoleValue);
            serializer.SerializeValue(ref Equipment);
            serializer.SerializeValue(ref Health);
            serializer.SerializeValue(ref IsDead);
            serializer.SerializeValue(ref IsStunned);
            serializer.SerializeValue(ref StunEndServerTime);
            serializer.SerializeValue(ref ReactionValue);
            serializer.SerializeValue(ref ReactionSequence);
            serializer.SerializeValue(ref StimulusPosition);
            serializer.SerializeValue(ref LastAttackerClientId);
        }

        public bool Equals(BlacksiteSoldierSpawnState other)
        {
            return IsConfigured == other.IsConfigured
                && SquadId == other.SquadId
                && MemberIndex == other.MemberIndex
                && FormationSlot == other.FormationSlot
                && FormationOffset.Equals(other.FormationOffset)
                && AnimationSeed == other.AnimationSeed
                && MissionReference.Equals(other.MissionReference)
                && RoleValue == other.RoleValue
                && TacticalRoleValue == other.TacticalRoleValue
                && Equipment == other.Equipment
                && Health == other.Health
                && IsDead == other.IsDead
                && IsStunned == other.IsStunned
                && StunEndServerTime.Equals(other.StunEndServerTime)
                && ReactionValue == other.ReactionValue
                && ReactionSequence == other.ReactionSequence
                && StimulusPosition.Equals(other.StimulusPosition)
                && LastAttackerClientId == other.LastAttackerClientId;
        }

        public override bool Equals(object obj)
        {
            return obj is BlacksiteSoldierSpawnState other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = IsConfigured ? 1 : 0;
                hash = (hash * 397) ^ SquadId.GetHashCode();
                hash = (hash * 397) ^ MemberIndex;
                hash = (hash * 397) ^ FormationSlot;
                hash = (hash * 397) ^ AnimationSeed;
                hash = (hash * 397) ^ Health;
                hash = (hash * 397) ^ ReactionSequence;
                return hash;
            }
        }
    }
}
