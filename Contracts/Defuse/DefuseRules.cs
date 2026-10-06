using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts._Shared;
namespace Y4NGZCompany.Contracts.Defuse
{
    internal enum DefuseBombFamily
    {
        Kilo,
        Manta,
        Rust,
        Omega,
        Type7
    }

    internal enum DefuseDisplayState
    {
        Armed,
        Error,
        Locked
    }

    internal enum DefuseWireColor
    {
        Red,
        Blue,
        Yellow,
        Green,
        Black,
        White
    }

    internal enum DefuseCutMode
    {
        Ordered,
        AnyOrder
    }

    internal sealed class DefuseArchetype
    {
        internal readonly DefuseBombFamily Family;
        internal readonly DefuseDisplayState DisplayState;
        internal readonly DefuseWireColor[] Wires;
        internal readonly int[] CutSlots;
        internal readonly DefuseCutMode CutMode;
        internal readonly string Procedure;

        internal DefuseArchetype(
            DefuseBombFamily family,
            DefuseDisplayState displayState,
            DefuseWireColor[] wires,
            int[] cutSlots,
            DefuseCutMode cutMode,
            string procedure)
        {
            Family = family;
            DisplayState = displayState;
            Wires = wires ?? Array.Empty<DefuseWireColor>();
            CutSlots = cutSlots ?? Array.Empty<int>();
            CutMode = cutMode;
            Procedure = procedure ?? string.Empty;
        }

        internal int WireCount => Mathf.Clamp(Wires.Length, 0, DefuseRules.MaxWireSlots);
    }

    internal readonly struct DefuseProfile
    {
        internal readonly int ArchetypeIndex;
        internal readonly int Code;

        internal DefuseProfile(int archetypeIndex, int code)
        {
            ArchetypeIndex = archetypeIndex;
            Code = Mathf.Clamp(code, 0, 9999);
        }
    }

    internal static class DefuseRules
    {
        internal const int MaxWireSlots = 5;
        internal const int CodeDigits = 4;

        internal static readonly DefuseArchetype[] Archetypes =
        {
            new DefuseArchetype(
                DefuseBombFamily.Kilo,
                DefuseDisplayState.Armed,
                new[] { DefuseWireColor.Red, DefuseWireColor.Blue, DefuseWireColor.Yellow },
                new[] { 1 },
                DefuseCutMode.AnyOrder,
                "Cut the blue wire only."),

            new DefuseArchetype(
                DefuseBombFamily.Kilo,
                DefuseDisplayState.Error,
                new[] { DefuseWireColor.Green, DefuseWireColor.Red, DefuseWireColor.Black, DefuseWireColor.Yellow },
                new[] { 0, 1, 2, 3 },
                DefuseCutMode.Ordered,
                "Cut every wire from left to right."),

            new DefuseArchetype(
                DefuseBombFamily.Manta,
                DefuseDisplayState.Locked,
                new[] { DefuseWireColor.Blue, DefuseWireColor.Yellow, DefuseWireColor.Green },
                new[] { 1, 0, 2 },
                DefuseCutMode.Ordered,
                "Cut yellow, then blue, then green."),

            new DefuseArchetype(
                DefuseBombFamily.Manta,
                DefuseDisplayState.Armed,
                new[] { DefuseWireColor.Red, DefuseWireColor.White, DefuseWireColor.Blue, DefuseWireColor.Black },
                new[] { 0, 1, 2 },
                DefuseCutMode.AnyOrder,
                "Cut every non-black wire in any order."),

            new DefuseArchetype(
                DefuseBombFamily.Rust,
                DefuseDisplayState.Error,
                new[] { DefuseWireColor.Red, DefuseWireColor.Yellow, DefuseWireColor.Black },
                new[] { 2 },
                DefuseCutMode.AnyOrder,
                "Cut the black wire only."),

            new DefuseArchetype(
                DefuseBombFamily.Rust,
                DefuseDisplayState.Locked,
                new[] { DefuseWireColor.Green, DefuseWireColor.Red, DefuseWireColor.Blue, DefuseWireColor.Yellow, DefuseWireColor.Black },
                new[] { 1, 2, 3, 4, 0 },
                DefuseCutMode.Ordered,
                "Cut red, blue, yellow, black, then green last."),

            new DefuseArchetype(
                DefuseBombFamily.Omega,
                DefuseDisplayState.Armed,
                new[] { DefuseWireColor.White, DefuseWireColor.Red, DefuseWireColor.Green },
                new[] { 1 },
                DefuseCutMode.AnyOrder,
                "Cut the center wire only."),

            new DefuseArchetype(
                DefuseBombFamily.Omega,
                DefuseDisplayState.Error,
                new[] { DefuseWireColor.Blue, DefuseWireColor.Black, DefuseWireColor.Yellow, DefuseWireColor.Green },
                new[] { 0, 3, 2, 1 },
                DefuseCutMode.Ordered,
                "Cut blue, green, yellow, then black."),

            new DefuseArchetype(
                DefuseBombFamily.Type7,
                DefuseDisplayState.Locked,
                new[] { DefuseWireColor.Yellow, DefuseWireColor.Red, DefuseWireColor.White, DefuseWireColor.Blue, DefuseWireColor.Green },
                new[] { 1, 3 },
                DefuseCutMode.Ordered,
                "Cut red, then blue. Leave the others intact."),

            new DefuseArchetype(
                DefuseBombFamily.Type7,
                DefuseDisplayState.Error,
                new[] { DefuseWireColor.Black, DefuseWireColor.Green, DefuseWireColor.Red },
                new[] { 2, 1, 0 },
                DefuseCutMode.Ordered,
                "Cut every wire from right to left."),
        };

        internal static DefuseArchetype GetArchetype(int index)
        {
            if (Archetypes.Length == 0)
                return null;

            return Archetypes[Mathf.Clamp(index, 0, Archetypes.Length - 1)];
        }

        /// <summary>
        /// True once this round's map seed exists, i.e. once a profile rolled now will be
        /// the profile the round actually generates against.
        /// </summary>
        /// <remarks>
        /// #217: <c>randomMapSeed</c> is assigned when the level generates, so it is zero (or
        /// still the previous round's) while the ship is in orbit. Rolling and caching a
        /// profile in that window is what could pin an entire round's bomb to a degenerate
        /// seed.
        /// </remarks>
        internal static bool IsRoundSeedAvailable()
        {
            var start = StartOfRound.Instance;
            return start != null && start.randomMapSeed != 0 && start.currentLevel != null;
        }

        internal static DefuseProfile BuildProfileForCurrentRound()
        {
            int seed = 7307;
            var start = StartOfRound.Instance;
            if (start != null)
            {
                seed ^= start.randomMapSeed;
                if (start.currentLevel != null)
                {
                    seed ^= start.currentLevel.levelID * 73856093;
                    seed ^= StableHash(start.currentLevel.PlanetName);
                }
            }

            var random = new System.Random(seed);
            DefuseEffectiveSettings settings = ContractsBootstrap.DefuseBalance?.GetEffective()
                                               ?? new DefuseEffectiveSettings(
                                                   ContractRiskLevel.C,
                                                   9f / 16f,
                                                   8f,
                                                   1,
                                                   MaxWireSlots,
                                                   2.8f,
                                                   false);
            int[] eligibleArchetypes = Archetypes
                .Select((candidate, index) => new { candidate, index })
                .Where(entry => entry.candidate != null
                                && entry.candidate.CutSlots.Length >= settings.MinCorrectWireInputs
                                && entry.candidate.CutSlots.Length <= settings.MaxCorrectWireInputs)
                .Select(entry => entry.index)
                .ToArray();
            int archetype = eligibleArchetypes.Length > 0
                ? eligibleArchetypes[random.Next(0, eligibleArchetypes.Length)]
                : random.Next(0, Mathf.Max(1, Archetypes.Length));
            int code = random.Next(0, 10000);
            return new DefuseProfile(archetype, code);
        }

        internal static bool IsWireCutAccepted(DefuseArchetype archetype, int cutMask, int wireSlot, out bool complete, out string reason)
        {
            complete = false;
            reason = string.Empty;
            if (archetype == null)
            {
                reason = "missing archetype";
                return false;
            }

            if (wireSlot < 0 || wireSlot >= archetype.WireCount)
            {
                reason = "invalid slot";
                return false;
            }

            int bit = 1 << wireSlot;
            if ((cutMask & bit) != 0)
            {
                reason = "already cut";
                return true;
            }

            int targetMask = BuildTargetMask(archetype);
            if (archetype.CutMode == DefuseCutMode.AnyOrder)
            {
                if ((targetMask & bit) == 0)
                {
                    reason = "wire not in any-order target set";
                    return false;
                }

                int nextMask = cutMask | bit;
                complete = (nextMask & targetMask) == targetMask;
                reason = complete ? "wire phase complete" : "accepted";
                return true;
            }

            int expectedStep = CountCutTargetWires(archetype, cutMask);
            if (expectedStep < 0 || expectedStep >= archetype.CutSlots.Length)
            {
                reason = "no more wires expected";
                return false;
            }

            int expectedSlot = archetype.CutSlots[expectedStep];
            if (wireSlot != expectedSlot)
            {
                reason = $"expected slot {expectedSlot + 1}";
                return false;
            }

            complete = expectedStep + 1 >= archetype.CutSlots.Length;
            reason = complete ? "wire phase complete" : "accepted";
            return true;
        }

        internal static string GetFamilyName(DefuseBombFamily family)
        {
            switch (family)
            {
                case DefuseBombFamily.Kilo:
                    return "KILO";
                case DefuseBombFamily.Manta:
                    return "MANTA";
                case DefuseBombFamily.Rust:
                    return "RUST";
                case DefuseBombFamily.Omega:
                    return "OMEGA";
                case DefuseBombFamily.Type7:
                    return "TYPE-7";
                default:
                    return "UNKNOWN";
            }
        }

        internal static string GetDisplayName(DefuseDisplayState state)
        {
            switch (state)
            {
                case DefuseDisplayState.Error:
                    return "ERROR";
                case DefuseDisplayState.Locked:
                    return "LOCKED";
                default:
                    return "ARMED";
            }
        }

        internal static string GetWireName(DefuseWireColor color)
        {
            switch (color)
            {
                case DefuseWireColor.Blue:
                    return "blue";
                case DefuseWireColor.Yellow:
                    return "yellow";
                case DefuseWireColor.Green:
                    return "green";
                case DefuseWireColor.Black:
                    return "black";
                case DefuseWireColor.White:
                    return "white";
                default:
                    return "red";
            }
        }

        internal static Color GetWireColor(DefuseWireColor color)
        {
            switch (color)
            {
                case DefuseWireColor.Blue:
                    return new Color(0.03f, 0.13f, 0.82f, 1f);
                case DefuseWireColor.Yellow:
                    return new Color(0.95f, 0.72f, 0.03f, 1f);
                case DefuseWireColor.Green:
                    return new Color(0.04f, 0.55f, 0.12f, 1f);
                case DefuseWireColor.Black:
                    return new Color(0.01f, 0.01f, 0.012f, 1f);
                case DefuseWireColor.White:
                    return new Color(0.86f, 0.84f, 0.78f, 1f);
                default:
                    return new Color(0.75f, 0.03f, 0.02f, 1f);
            }
        }

        internal static string FormatWireList(DefuseArchetype archetype)
        {
            if (archetype == null || archetype.WireCount == 0)
                return "none";

            return string.Join(", ", archetype.Wires.Take(archetype.WireCount).Select(GetWireName).ToArray());
        }

        internal static string FormatManual()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Match the on-site marking, display text, and left-to-right wire list.");
            sb.AppendLine("After the wire procedure is complete, enter the case code.");
            sb.AppendLine();
            for (int i = 0; i < Archetypes.Length; i++)
            {
                DefuseArchetype archetype = Archetypes[i];
                sb.Append(i + 1);
                sb.Append(". ");
                sb.Append(GetFamilyName(archetype.Family));
                sb.Append(" / ");
                sb.Append(GetDisplayName(archetype.DisplayState));
                sb.Append(" / ");
                sb.Append(archetype.WireCount);
                sb.Append(" wires [");
                sb.Append(FormatWireList(archetype));
                sb.Append("]: ");
                sb.AppendLine(archetype.Procedure);
            }
            return sb.ToString().TrimEnd();
        }

        internal static string FormatArchetypeBrief(DefuseArchetype archetype)
        {
            if (archetype == null)
                return "No bomb profile has been loaded.";

            return $"{GetFamilyName(archetype.Family)} / {GetDisplayName(archetype.DisplayState)} / {archetype.WireCount} wires [{FormatWireList(archetype)}]";
        }

        private static int BuildTargetMask(DefuseArchetype archetype)
        {
            int mask = 0;
            if (archetype?.CutSlots == null)
                return mask;

            for (int i = 0; i < archetype.CutSlots.Length; i++)
            {
                int slot = archetype.CutSlots[i];
                if (slot >= 0 && slot < MaxWireSlots)
                    mask |= 1 << slot;
            }
            return mask;
        }

        private static int CountCutTargetWires(DefuseArchetype archetype, int cutMask)
        {
            int count = 0;
            for (int i = 0; i < archetype.CutSlots.Length; i++)
            {
                int slot = archetype.CutSlots[i];
                if (slot < 0 || slot >= MaxWireSlots)
                    continue;
                if ((cutMask & (1 << slot)) != 0)
                    count++;
            }
            return count;
        }

        private static int StableHash(string value)
        {
            unchecked
            {
                int hash = 23;
                if (value == null)
                    return hash;

                for (int i = 0; i < value.Length; i++)
                    hash = hash * 31 + value[i];

                return hash;
            }
        }
    }
}
