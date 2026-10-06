using System.Collections.Generic;

namespace Y4NGZCompany.Contracts._Shared
{
    // A single active step at rest. Identity never includes a changing counter/timer.
    internal static class ContractHudObjectivePresentation
    {
        internal static MoonContractObjective? Select(IReadOnlyList<MoonContractObjective> objectives)
        {
            MoonContractObjective? action = null, status = null;
            if (objectives == null) return null;
            foreach (var objective in objectives)
            {
                if (objective.IsFailed) return objective;
                if (objective.IsComplete) continue;
                if (objective.Kind == MoonContractObjectiveKind.Action && !action.HasValue) action = objective;
                if (objective.Kind == MoonContractObjectiveKind.Status && !status.HasValue) status = objective;
            }
            return action ?? status;
        }

        // Blackout is the one concurrent checklist: after discovery starts, players can
        // find further boxes and reset discovered ones in either order.
        internal static MoonContractObjective[] BlackoutPair(IReadOnlyList<MoonContractObjective> objectives)
        {
            MoonContractObjective? locate = null, reset = null;
            if (objectives != null)
                foreach (var objective in objectives)
                {
                    if (objective.SignatureKey == "blackout.locate") locate = objective;
                    if (objective.SignatureKey == "blackout.breakers") reset = objective;
                }
            if (!locate.HasValue || !reset.HasValue || locate.Value.Total <= 1
                || locate.Value.Complete <= 0 || reset.Value.IsComplete
                || locate.Value.IsFailed || reset.Value.IsFailed) return null;
            return new[] { locate.Value, reset.Value };
        }

        internal static MoonContractObjective? CompletedPrevious(
            IReadOnlyList<MoonContractObjective> objectives, MoonContractObjective? previous,
            MoonContractObjective? next)
        {
            if (!previous.HasValue || (next.HasValue && previous.Value.SignatureKey == next.Value.SignatureKey))
                return null;
            if (objectives != null)
                foreach (var objective in objectives)
                    if (objective.SignatureKey == previous.Value.SignatureKey && objective.IsComplete)
                        return objective;
            return null;
        }

        internal static MoonContractObjective WithStatusContext(MoonContractObjective selected, IReadOnlyList<MoonContractObjective> objectives)
        {
            if (selected.Kind != MoonContractObjectiveKind.Action || selected.IsFailed) return selected;
            string text = selected.Text;
            // A countdown/serial is context for the active step, not a second checklist item.
            // Keep these safety-critical details while hiding future action objectives.
            if (objectives != null)
                foreach (var status in objectives)
                    if (status.Kind == MoonContractObjectiveKind.Status && !status.IsComplete && !status.IsFailed)
                        text += " \u00B7 " + status.Text;
            return new MoonContractObjective(text, selected.Complete, selected.Total, selected.Failed,
                selected.Kind, selected.SignatureKey, selected.Lethal);
        }

        /// <summary>
        /// #880. Providers must be monotonic: a step that has ticked stays ticked until it fails or
        /// the round resets. This is the card's guard for the ones that are not. A boolean action
        /// row (Total of one) seen complete has its key added to <paramref name="pinnedKeys"/>; a
        /// later read of the same key as incomplete and not failed is rewritten complete in place
        /// and the key is appended to <paramref name="regressedKeys"/> so the caller can log the
        /// provider bug once. Counted rows and status rows are left alone: their counters are
        /// allowed to move, and pinning them would hide a genuinely reopened step.
        /// </summary>
        internal static void PinCompletedActions(
            IList<MoonContractObjective> objectives, ISet<string> pinnedKeys, IList<string> regressedKeys)
        {
            if (objectives == null || pinnedKeys == null) return;
            for (int i = 0; i < objectives.Count; i++)
            {
                var objective = objectives[i];
                if (objective.Kind != MoonContractObjectiveKind.Action || objective.Total != 1 || objective.IsFailed) continue;
                if (objective.IsComplete) { pinnedKeys.Add(objective.SignatureKey); continue; }
                if (!pinnedKeys.Contains(objective.SignatureKey)) continue;
                objectives[i] = new MoonContractObjective(objective.Text, objective.Total, objective.Total, false,
                    objective.Kind, objective.Key, objective.Lethal);
                regressedKeys?.Add(objective.SignatureKey);
            }
        }

        /// <summary>
        /// #1233 D6. The Shadow Raid drive has left the moon: MoonContractState's failed, lost
        /// <c>shadow.blacksite</c> status row is selected. It is the one terminal card that closes
        /// on its own row - the title is struck and dimmed and the row is the whole body.
        /// </summary>
        internal static bool IsDriveDeparted(MoonContractObjective? selected) =>
            selected.HasValue && selected.Value.IsFailed && selected.Value.Lost
            && selected.Value.SignatureKey == "shadow.blacksite";

        /// <summary>
        /// #1076, narrowed by #1233 D6: <c>Contract voided.</c> goes under every failed row except
        /// the departed drive, whose struck title already says the contract is over.
        /// </summary>
        internal static bool AppendsVoidedLine(MoonContractObjective selected) =>
            selected.IsFailed && !IsDriveDeparted(selected);

        /// <summary>
        /// #1233 D6. The live card title. The title renders TMP rich text, so the name is escaped
        /// like row text and only this renderer adds the strike; the dim tint is the theme's.
        /// </summary>
        internal static string Title(string name, bool struck)
        {
            string title = Escape(name);
            return struck ? "<s>" + title + "</s>" : title;
        }

        internal static string Escape(string text) => (text ?? string.Empty).Replace("<", "\u2039").Replace(">", "\u203a");
    }
}
