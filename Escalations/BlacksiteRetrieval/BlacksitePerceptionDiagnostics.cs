using System;
using System.Collections.Generic;
using UnityEngine;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // #874 S4.0b: the perception and reaction half of the S4.0 diagnostic contract.
    // Pure observation. Nothing here influences a decision, a threshold, a cooldown or an order;
    // every entry point is a no-op unless BlacksiteDiagnostics.Enabled, and all state below exists
    // only so a transition can be told from a repeat.
    internal static class BlacksitePerceptionDiagnostics
    {
        // sourceKind vocabulary (Stage 4 common brief).
        internal const string SourceVisual = "visual";
        internal const string SourceAdapter = "adapter";
        internal const string SourceDamage = "damage";
        internal const string SourceAudio = "audio";
        internal const string SourceMemory = "memory";

        // A contact-response pair that never resolves prints one response=none line after this
        // long and then resets. Fixed by the Stage 4 brief ("5 s pass without a response").
        private const float ResponseWindowSeconds = 5f;
        // A live contact that stops being re-reported for this long is considered lost, so the
        // next report of the same target is a NEW registration. Held equal to the response window
        // on purpose: anything shorter could arm a second latency pair for a contact whose first
        // pair is still open, and anything longer would hide a genuine lose/reacquire cycle.
        private const float ContactIdleSeconds = ResponseWindowSeconds;

        private readonly struct Key : IEquatable<Key>
        {
            internal readonly ulong Member;
            internal readonly string Target;
            internal Key(ulong member, string target) { Member = member; Target = target ?? "none"; }
            public bool Equals(Key other) => Member == other.Member && string.Equals(Target, other.Target, StringComparison.Ordinal);
            public override bool Equals(object obj) => obj is Key key && Equals(key);
            public override int GetHashCode() => unchecked((Member.GetHashCode() * 397) ^ Target.GetHashCode());
        }

        private struct Contact
        {
            internal float ContactAt;
            internal float LastReportedAt;
            internal bool AwaitingResponse;
            internal string SourceKind;
            // Recorded at registration so a timeout line names the member that held the contact,
            // not whichever member happened to run the once-per-frame sweep.
            internal int MemberIndex;
        }

        private static readonly Dictionary<Key, bool> Visibility = new Dictionary<Key, bool>();
        private static readonly Dictionary<Key, Contact> Contacts = new Dictionary<Key, Contact>();
        private static readonly List<Key> Scratch = new List<Key>();
        private static int _lastTickFrame = -1;

        internal static void Reset()
        {
            Visibility.Clear();
            Contacts.Clear();
            Scratch.Clear();
            // #1017 S3: [UAT][Brain]'s two gates are static and keyed by NetworkObjectId, which the
            // next deployment reuses, so they are cleared here with every other Blacksite gate.
            BlacksiteSoldierAI.BlacksiteSoldierBrain.ResetGates();
            _lastTickFrame = -1;
        }

        // One line on every transition of the visible flag for a (member, target) pair.
        // #874 S10.2: `rays` and `grace` are appended at the END of the line, per the Stage 10
        // contract, and only for the player sight test, which is the only caller that has them.
        // The enemy sight test passes neither and its line keeps exactly the shape it had.
        // `visible=` remains the RAW per-tick sample: the consumer grace is reported by
        // `grace=True` on a line whose `visible=False`, so the score script's flip count still
        // measures raw flicker and not a smoothed one.
        internal static void NoteSighting(
            int memberIndex,
            ulong memberToken,
            string target,
            bool visible,
            float distance,
            float dot,
            string sourceKind,
            string rays = null,
            bool? grace = null)
        {
            if (!BlacksiteDiagnostics.Enabled)
                return;
            var key = new Key(memberToken, target);
            if (Visibility.TryGetValue(key, out bool previous) && previous == visible)
                return;
            Visibility[key] = visible;
            BlacksiteDiagnostics.Log(
                "Sighting",
                $"member={memberIndex}, target={target}, visible={visible}, distance={distance:0.00}, "
                + $"dot={dot:0.00}, sourceKind={sourceKind}"
                + (rays != null ? $", rays={rays}, grace={grace ?? false}" : string.Empty));
        }

        // One line per contact REGISTRATION. A target already live for this member is refreshed
        // silently; only a first contact, or one re-acquired after ContactIdleSeconds of silence,
        // is a registration, and only a registration arms a reaction-latency pair.
        internal static void NoteContact(
            int memberIndex,
            ulong memberToken,
            string target,
            string sourceKind,
            float distance,
            bool exact)
        {
            if (!BlacksiteDiagnostics.Enabled)
                return;
            float now = Time.time;
            var key = new Key(memberToken, target);
            if (Contacts.TryGetValue(key, out Contact existing)
                && now - existing.LastReportedAt <= ContactIdleSeconds)
            {
                existing.LastReportedAt = now;
                Contacts[key] = existing;
                return;
            }
            Contacts[key] = new Contact
            {
                ContactAt = now,
                LastReportedAt = now,
                AwaitingResponse = true,
                SourceKind = sourceKind,
                MemberIndex = memberIndex
            };
            BlacksiteDiagnostics.Log(
                "Contact",
                $"member={memberIndex}, target={target}, sourceKind={sourceKind}, "
                + $"distance={distance:0.00}, exact={exact}");
        }

        // A response behaviour answers every contact this member is still holding open.
        internal static void NoteResponse(int memberIndex, ulong memberToken, BlacksiteBehaviour response)
        {
            if (!BlacksiteDiagnostics.Enabled || !IsResponseBehaviour(response))
                return;
            float now = Time.time;
            Scratch.Clear();
            foreach (var pair in Contacts)
                if (pair.Key.Member == memberToken && pair.Value.AwaitingResponse)
                    Scratch.Add(pair.Key);
            for (int i = 0; i < Scratch.Count; i++)
            {
                Key key = Scratch[i];
                Contact contact = Contacts[key];
                contact.AwaitingResponse = false;
                Contacts[key] = contact;
                BlacksiteDiagnostics.Log(
                    "ReactionLatency",
                    $"member={memberIndex}, target={key.Target}, contactAt={contact.ContactAt:0.00}, "
                    + $"respondedAt={now:0.00}, latency={now - contact.ContactAt:0.00}, response={response}");
            }
            Scratch.Clear();
        }

        // Combat is the response the brief names; Evade and Withdrawal are the other two
        // behaviours a contact can legitimately produce, so a member that backs off is not
        // scored as having failed to react.
        private static bool IsResponseBehaviour(BlacksiteBehaviour behaviour) =>
            behaviour == BlacksiteBehaviour.Combat
            || behaviour == BlacksiteBehaviour.Evade
            || behaviour == BlacksiteBehaviour.Withdrawal;

        // Prints the single response=none line for a pair that went unanswered, and drops
        // long-dead entries. Safe to call from every member every frame: it runs once a frame.
        internal static void Tick()
        {
            if (!BlacksiteDiagnostics.Enabled || _lastTickFrame == Time.frameCount)
                return;
            _lastTickFrame = Time.frameCount;
            float now = Time.time;
            Scratch.Clear();
            foreach (var pair in Contacts)
                if (pair.Value.AwaitingResponse && now - pair.Value.ContactAt >= ResponseWindowSeconds)
                    Scratch.Add(pair.Key);
            for (int i = 0; i < Scratch.Count; i++)
            {
                Key key = Scratch[i];
                Contact contact = Contacts[key];
                contact.AwaitingResponse = false;
                Contacts[key] = contact;
                BlacksiteDiagnostics.Log(
                    "ReactionLatency",
                    $"member={contact.MemberIndex}, target={key.Target}, contactAt={contact.ContactAt:0.00}, "
                    + $"respondedAt=none, latency=none, response=none");
            }
            Scratch.Clear();
        }
    }
}
