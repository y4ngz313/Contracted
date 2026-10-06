using UnityEngine;

namespace Y4NGZCompany.Contracts._Shared
{
    internal enum MoonContractObjectiveKind
    {
        Action,
        Status
    }

    internal readonly struct MoonContractObjective
    {
        internal readonly string Text;
        internal readonly int Complete;
        internal readonly int Total;
        internal readonly bool Failed;
        internal readonly MoonContractObjectiveKind Kind;

        /// <summary>
        /// Stable identity for HUD change detection, independent of the display text.
        /// Null falls back to <see cref="Text"/>, which is correct for objectives whose
        /// wording only changes when their meaning changes. Objectives whose text embeds
        /// a volatile payload (countdown timers, running totals) must pass an explicit
        /// key so the HUD does not treat every tick as a new objective (#520).
        /// </summary>
        internal readonly string Key;

        /// <summary>
        /// Marks an escalation condition that is immediately fatal rather than merely
        /// hazardous; the HUD renders lethal sections in the danger tier (#520).
        /// </summary>
        internal readonly bool Lethal;

        /// <summary>
        /// #1076: the failure was the objective LEAVING the moon in someone else's hands, not
        /// something the crew can still act on. Only meaningful beside <see cref="Failed"/>:
        /// ContractHUDManager.FormatObjectiveLine renders a failed-and-lost row in the dim tier
        /// instead of the danger tier, because a danger tint reads as "do something about this"
        /// and there is nothing left to do. The argument is last and defaults to false, so every
        /// existing caller, pin and reconstruction is unchanged.
        /// </summary>
        internal readonly bool Lost;

        internal MoonContractObjective(
            string text,
            int complete,
            int total,
            bool failed = false,
            MoonContractObjectiveKind kind = MoonContractObjectiveKind.Action,
            string key = null,
            bool lethal = false,
            bool lost = false)
        {
            Text = text;
            Complete = Mathf.Clamp(complete, 0, Mathf.Max(total, 0));
            Total = Mathf.Max(total, 0);
            Failed = failed;
            Kind = kind;
            Key = key;
            Lethal = lethal;
            Lost = lost;
        }

        internal bool IsComplete => !Failed && (Total <= 0 || Complete >= Total);
        internal bool IsFailed => Failed;

        /// <summary>The identity the semantic signature is computed from.</summary>
        internal string SignatureKey => Key ?? Text ?? string.Empty;
    }
}
