namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal enum BlacksiteVoiceEvent
    {
        InitialEntry = 0,
        PlayerContact = 1,
        MonsterContact = 2,
        TacticalAction = 3,
        DriveMissing = 4,
        DriveAcquired = 5,
        CasualtyEscalation = 6,
        KillClear = 7,
        GrenadeWarning = 8, // Reserved for older bundle/network compatibility; no sender.
        BackupRequested = 9,
        BackupAcknowledged = 10,
        Searching = 11,
        Reacquired = 12,
        CargoDeposited = 13,
        Pickup = 14,
        Patrol = 15,
        // #1271 S8: appended, never renumbered - the int crosses PresentSquadVoiceServer's ClientRpc.
        ObjectiveSighted = 16,
        WitnessSighted = 17,
        WitnessTaken = 18,
        ObjectiveTaken = 19,
        ObjectiveLost = 20,
        MemberBoarded = 21,
        ShipDeparting = 22,
        AcknowledgeOrder = 23, // Response only: never requested by a sender.
        AcknowledgeContact = 24, // Response only: never requested by a sender.
    }

    internal static class BlacksiteVoiceEventRules
    {
        internal static bool IsCritical(BlacksiteVoiceEvent voiceEvent)
        {
            return voiceEvent == BlacksiteVoiceEvent.PlayerContact
                || voiceEvent == BlacksiteVoiceEvent.MonsterContact
                || voiceEvent == BlacksiteVoiceEvent.DriveMissing
                || voiceEvent == BlacksiteVoiceEvent.DriveAcquired
                || voiceEvent == BlacksiteVoiceEvent.CasualtyEscalation
                || voiceEvent == BlacksiteVoiceEvent.BackupRequested
                || voiceEvent == BlacksiteVoiceEvent.BackupAcknowledged
                || voiceEvent == BlacksiteVoiceEvent.Reacquired
                // #1271 S8: the boarding order expired queued behind chatter (round 2 of 2026-09-29).
                // It, the witness calls, the loss and the departure are one-shot moments, so they
                // skip the ordinary lock and cap and replace a queued noncritical line.
                || voiceEvent == BlacksiteVoiceEvent.Pickup
                || voiceEvent == BlacksiteVoiceEvent.WitnessSighted
                || voiceEvent == BlacksiteVoiceEvent.WitnessTaken
                || voiceEvent == BlacksiteVoiceEvent.ObjectiveLost
                || voiceEvent == BlacksiteVoiceEvent.ShipDeparting;
        }

        internal static bool IsAmbient(BlacksiteVoiceEvent voiceEvent)
        {
            return voiceEvent == BlacksiteVoiceEvent.TacticalAction;
        }

        /// <summary>
        /// #1271 S8: the answer a different member gives on the shared radio when this call plays.
        /// Contacts are answered with a closing/covering line, reports and orders with a copy. An
        /// ambient TacticalAction is chatter, not a report, so it goes unanswered, and a response
        /// never has a response of its own.
        /// </summary>
        internal static bool TryGetResponse(BlacksiteVoiceEvent call, bool ambient, out BlacksiteVoiceEvent response)
        {
            switch (call)
            {
                case BlacksiteVoiceEvent.PlayerContact:
                case BlacksiteVoiceEvent.MonsterContact:
                case BlacksiteVoiceEvent.Reacquired:
                case BlacksiteVoiceEvent.WitnessSighted:
                    response = BlacksiteVoiceEvent.AcknowledgeContact;
                    return true;
                case BlacksiteVoiceEvent.InitialEntry:
                case BlacksiteVoiceEvent.DriveAcquired:
                case BlacksiteVoiceEvent.KillClear:
                case BlacksiteVoiceEvent.Pickup:
                case BlacksiteVoiceEvent.Patrol:
                case BlacksiteVoiceEvent.ObjectiveSighted:
                case BlacksiteVoiceEvent.WitnessTaken:
                case BlacksiteVoiceEvent.ObjectiveTaken:
                case BlacksiteVoiceEvent.ShipDeparting:
                    response = BlacksiteVoiceEvent.AcknowledgeOrder;
                    return true;
                case BlacksiteVoiceEvent.TacticalAction when !ambient:
                    response = BlacksiteVoiceEvent.AcknowledgeOrder;
                    return true;
                default:
                    response = default;
                    return false;
            }
        }

        /// <summary>#1271 S8: squad-level orders and calls; the director gives them to the Leader.</summary>
        internal static bool IsLeaderCall(BlacksiteVoiceEvent voiceEvent)
        {
            return voiceEvent == BlacksiteVoiceEvent.InitialEntry
                || voiceEvent == BlacksiteVoiceEvent.Pickup
                || voiceEvent == BlacksiteVoiceEvent.ObjectiveSighted
                || voiceEvent == BlacksiteVoiceEvent.ObjectiveLost
                || voiceEvent == BlacksiteVoiceEvent.ShipDeparting
                || voiceEvent == BlacksiteVoiceEvent.Patrol;
        }

        /// <summary>
        /// #1271 S8: lines spoken once members are aboard, so a boarded member stays a speaker for
        /// them (and for their response). Boarded members ride the hull visible with a live voice
        /// source until the ship has departed.
        /// </summary>
        internal static bool IsSpokenAboard(BlacksiteVoiceEvent voiceEvent)
        {
            return voiceEvent == BlacksiteVoiceEvent.MemberBoarded
                || voiceEvent == BlacksiteVoiceEvent.ShipDeparting;
        }
    }
}
