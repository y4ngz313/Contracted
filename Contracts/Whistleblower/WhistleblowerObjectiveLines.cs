using System.Collections.Generic;

using Y4NGZCompany.Contracts._Shared;

namespace Y4NGZCompany.Contracts.Whistleblower
{
    /// <summary>
    /// #966 S14 U4. The Whistleblower contract card, as a pure function of five booleans.
    ///
    /// This file has no UnityEngine and no Unity.Netcode using of its own: every input is a
    /// primitive the caller has already measured, and the output is the same
    /// <see cref="MoonContractObjective"/> list the card consumes, so the headless
    /// <c>tests/ContractHudPresentationRegression</c> drives the real rows through
    /// <c>Select</c>, <c>WithStatusContext</c>, <c>PinCompletedActions</c> and
    /// <c>IsCurrentContractFailed</c>'s scan without a game running. This is the
    /// <c>ContractHudObjectivePresentation</c> precedent: the row policy is worth pinning, the
    /// state that feeds it is not.
    ///
    /// The shape is <c>PayloadContract.GetObjectives</c>'s exactly: a failed status row first when
    /// the contract has voided, then the action rows, which also carry the failure so a voided
    /// contract can never read as a step the crew may still take.
    /// </summary>
    internal static class WhistleblowerObjectiveLines
    {
        /// <summary>The action row's key. It is the same action either way the text reads, so the
        /// key does not move when "Locate" becomes "Neutralize"; the wording change is still a
        /// semantic change the card animates, because the text is what changed.</summary>
        internal const string LocateKey = "whistleblower.locate";

        /// <summary>The failed row's key.</summary>
        internal const string ExtractedKey = "whistleblower.extracted";

        internal const string LocateText = "Locate the witness";
        internal const string NeutralizeText = "Neutralize the witness";

        // #1076: the failed row says what happened to him rather than naming a state, and renders
        // dim rather than danger, because a man who has left the moon is not a thing to act on.
        internal const string ExtractedText = "BLACKSITE TEAM LEFT WITH THE WITNESS";

        /// <summary>
        /// The rows for the current state of the contract.
        /// </summary>
        /// <param name="located">A player has had him in sight recently.</param>
        /// <param name="inCustody">A Blacksite escort holds him.</param>
        /// <param name="beingExtracted">He is in custody and no longer inside the facility.</param>
        /// <param name="neutralized">He is dead.</param>
        /// <param name="extracted">Their ship departed with him aboard: the contract has voided.</param>
        /// <param name="aboard">
        /// #1076: he is stowed aboard their ship and has not left yet. Measured from the replicated
        /// manifest snapshot rather than from custody, because custody says who holds him and this
        /// says where he is. Since the card no longer names either reading, this and
        /// <paramref name="beingExtracted"/> simply both feed the lethal term.
        /// </param>
        internal static List<MoonContractObjective> Build(
            bool located,
            bool inCustody,
            bool beingExtracted,
            bool neutralized,
            bool extracted,
            bool aboard = false)
        {
            var objectives = new List<MoonContractObjective>();
            // #1076, amended by the 2026-09-16 playtest: the custody status rows are gone, because
            // the belt already says WITNESS IN CUSTODY / EXTRACTING WITNESS / WITNESS ABOARD. The
            // card is the action row alone, and its lethal flag is the only card-side danger tint
            // the two late custody readings still get.
            bool lethal = !neutralized && !extracted && (aboard || beingExtracted);

            // The catalog has promised "or the contract voids" since #694 and nothing ever emitted
            // it. This is that row, and it is the whole failure mechanism: there is no NotifyFailed
            // on this contract, MoonContractState.IsCurrentContractFailed scans the rows for
            // IsFailed, and PublishContractResolved carries the answer to the performance report.
            if (extracted)
            {
                objectives.Add(new MoonContractObjective(
                    ExtractedText, 0, 1, true, MoonContractObjectiveKind.Status, key: ExtractedKey, lost: true));
            }

            // A man in an escort's hands has been found, whether or not anybody is looking at him
            // this second, so custody reads as "Neutralize" the same way line of sight does.
            bool killObjective = neutralized || located || inCustody || aboard;
            objectives.Add(new MoonContractObjective(
                killObjective ? NeutralizeText : LocateText,
                neutralized ? 1 : 0,
                1,
                extracted,
                key: LocateKey,
                lethal: lethal));

            return objectives;
        }
    }
}
