using System.Collections.Generic;
using UnityEngine;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // #1293 R1: the squad's ONE world model. Every member reads the same rows, written by the
    // director's one perception pass (BlacksiteSquadDirector) and by event writers (hits, stimuli,
    // adapter acquires). Roles differ in what they DO with a row, never in what they KNOW.
    //
    // Plain C#: only Vector3/Mathf from Unity (stubbed in the headless suites), no Time.time (every
    // writer takes `now`) and no logging (callers log the transition a writer returns).
    internal enum BlacksiteWorldSource : byte { Saw = 0, Heard = 1, Told = 2, Live = 3 }
    internal enum BlacksiteWorldClass : byte { Unknown = 0, Player = 1, Monster = 2, Hazard = 3, Witness = 4, Objective = 5 }
    internal enum BlacksiteWorldChange : byte { None = 0, New = 1, Reacquired = 2, Refreshed = 3 }

    internal sealed class BlacksiteWorldRow
    {
        // One bit per perception slot (BlacksiteSoldierAI.PerceptionSlot) in SightMask/AnnouncedMask.
        internal const int SlotCount = 32;

        internal ulong Key;
        internal object Subject;
        internal BlacksiteWorldClass Class;
        internal BlacksiteWorldSource Source;
        internal Vector3 Position;
        internal Vector3 Velocity;
        internal bool Outside;
        internal float SeenAt;
        internal ulong SeenBy;
        internal float SawAt = float.NegativeInfinity;
        internal uint SightMask;
        internal uint AnnouncedMask;
        internal int Revision;

        // Table-owned state, written only through BlacksiteWorldModel.
        private readonly float[] _memberSawAt = new float[SlotCount];
        private Vector3 _sawPosition;
        private bool _velocitySampled;
        private Vector3 _revisionAnchor;

        internal BlacksiteWorldRow()
        {
            for (int i = 0; i < SlotCount; i++) _memberSawAt[i] = float.NegativeInfinity;
        }

        internal bool InSight => SightMask != 0;
        internal bool SeenByMember(int slot) => (uint)slot < SlotCount && (SightMask & (1u << slot)) != 0;
        internal float MemberSawAt(int slot) => (uint)slot < SlotCount ? _memberSawAt[slot] : float.NegativeInfinity;
        internal float Age(float now) => now - SeenAt;

        // THE decay function.
        internal float Confidence(float now)
            => Source == BlacksiteWorldSource.Live ? 1f : Mathf.Clamp01(1f - Age(now) / BlacksiteWorldModel.MemorySeconds);

        // Carried forward from when Position was written (SeenAt; SawAt for every Saw row), clamped
        // by BlacksiteSightPolicy.PredictionHorizonSeconds. Heard/Told zero Velocity, so only two
        // consecutive sightings ever extrapolate.
        internal Vector3 PredictedPosition(float now)
            => Position + Velocity * BlacksiteSightPolicy.PredictionElapsed(now, SeenAt);

        internal bool Announce(int slot)
        {
            if ((uint)slot >= SlotCount) return false;
            uint bit = 1u << slot;
            if ((AnnouncedMask & bit) != 0) return false;
            AnnouncedMask |= bit;
            return true;
        }

        internal void Place(Vector3 position, bool outside, ulong by, float now, bool newTrack)
        {
            if (newTrack || outside != Outside
                || (position - _revisionAnchor).sqrMagnitude
                    > BlacksiteWorldModel.RevisionMoveMetres * BlacksiteWorldModel.RevisionMoveMetres)
            {
                Revision++;
                _revisionAnchor = position;
            }
            Position = position;
            Outside = outside;
            SeenAt = now;
            SeenBy = by;
        }

        // One own-eyes sample. Velocity is a 0.5 exponential average over consecutive samples of one
        // track, seeded with the first measured value; a new track (New/Reacquired) starts over.
        internal void Sample(Vector3 position, int slot, float now, bool newTrack, float smoothing)
        {
            float elapsed = now - SawAt;
            if (newTrack)
            {
                Velocity = Vector3.zero;
                _velocitySampled = false;
            }
            else if (elapsed > 0f)
            {
                Vector3 measured = (position - _sawPosition) / elapsed;
                Velocity = _velocitySampled ? Velocity + (measured - Velocity) * smoothing : measured;
                _velocitySampled = true;
            }
            _sawPosition = position;
            SawAt = now;
            if ((uint)slot >= SlotCount) return;
            SightMask |= 1u << slot;
            _memberSawAt[slot] = now;
        }

        // A point that no sighting stands behind has no motion to carry forward.
        internal void Unsample()
        {
            Velocity = Vector3.zero;
            _velocitySampled = false;
        }

        internal void Hide(int slot) => SightMask &= ~(1u << slot);

        // The slot's eyes are gone. Its announcement goes too, so the next holder starts
        // unannounced - except on a Witness row, whose sighting is announced once for the whole
        // squad until the row is forgotten.
        internal void ReleaseSlot(int slot)
        {
            uint bit = 1u << slot;
            SightMask &= ~bit;
            if (Class != BlacksiteWorldClass.Witness) AnnouncedMask &= ~bit;
            _memberSawAt[slot] = float.NegativeInfinity;
        }
    }

    internal sealed class BlacksiteWorldModel
    {
        // #1293 R1: the ONE decay. It replaces TargetMemorySeconds 14, CombatController
        // SuppressionMemorySeconds 4, CombatPolicy LostSightGraceSeconds 0.8, Awareness 14/8,
        // ThreatMemory 2.5/12/40 (+0.085/s), PlayerKnowledge 2.5/18/60 and witness VisualSeconds 2.5.
        internal const float MemorySeconds = 14f;
        // Kind-less stimuli (explosion, unattributed noise/damage) ride one row, Class Unknown.
        internal const ulong StimulusKey = ulong.MaxValue;
        internal const float RevisionMoveMetres = 4f;
        private const float VelocitySmoothing = .5f;

        private readonly List<BlacksiteWorldRow> _rows = new List<BlacksiteWorldRow>();
        private readonly Dictionary<ulong, BlacksiteWorldRow> _byKey = new Dictionary<ulong, BlacksiteWorldRow>();

        internal static bool Expired(BlacksiteWorldSource source, float seenAt, float now)
            => source != BlacksiteWorldSource.Live && now - seenAt > MemorySeconds;

        internal IReadOnlyList<BlacksiteWorldRow> Rows => _rows;
        // Bumps when a threat row (Player, Monster, Unknown) is New, Reacquired, loses the squad's
        // sight, changes class, expires or is forgotten (Reset included): the squad has news (#1293
        // R3: the director's World fan-out). Refreshes and Hazard/Witness rows never move it.
        internal int ChangeCount { get; private set; }

        internal bool TryGet(ulong key, out BlacksiteWorldRow row) => _byKey.TryGetValue(key, out row);

        internal bool HasThreat
        {
            get
            {
                for (int i = 0; i < _rows.Count; i++)
                    if (IsThreat(_rows[i].Class)) return true;
                return false;
            }
        }

        // What a member is alerted to: the most recently refreshed threat row, while that member's
        // own eyes hold none (a member that sees a threat is in contact, not alerted).
        internal bool TryGetAlert(int slot, out BlacksiteWorldRow alert)
        {
            alert = null;
            for (int i = 0; i < _rows.Count; i++)
            {
                BlacksiteWorldRow row = _rows[i];
                if (!IsThreat(row.Class)) continue;
                if (row.SeenByMember(slot))
                {
                    alert = null;
                    return false;
                }
                if (alert == null || row.SeenAt > alert.SeenAt) alert = row;
            }
            return alert != null;
        }

        private static bool IsThreat(BlacksiteWorldClass cls)
            => cls == BlacksiteWorldClass.Player || cls == BlacksiteWorldClass.Monster || cls == BlacksiteWorldClass.Unknown;

        internal BlacksiteWorldChange Saw(ulong key, object subject, BlacksiteWorldClass cls, Vector3 position,
            bool outside, ulong observer, int observerSlot, float now)
        {
            if (!_byKey.TryGetValue(key, out BlacksiteWorldRow row))
            {
                row = Add(key, subject, cls, BlacksiteWorldSource.Saw, position, outside, observer, now);
                row.Sample(position, observerSlot, now, newTrack: true, VelocitySmoothing);
                return BlacksiteWorldChange.New;
            }
            bool reacquired = !row.InSight && now - row.SawAt > BlacksiteSightPolicy.VisualGraceSeconds;
            Adopt(row, subject, cls);
            // Live is sticky: a sighting of a game fact only adds the sight fields.
            if (row.Source != BlacksiteWorldSource.Live) row.Source = BlacksiteWorldSource.Saw;
            row.Sample(position, observerSlot, now, reacquired, VelocitySmoothing);
            row.Place(position, outside, observer, now, reacquired);
            if (!reacquired) return BlacksiteWorldChange.Refreshed;
            if (IsThreat(row.Class)) ChangeCount++;
            return BlacksiteWorldChange.Reacquired;
        }

        // That member's pass did not see it. The bit outlives the sample by the sight grace
        // (BlacksiteSightPolicy.VisualGraceSeconds), so one blocked pass is not a sight loss. The
        // last bit of a threat row going is the squad's sight loss, news like the reacquisition.
        internal void Unseen(ulong key, int observerSlot, float now)
        {
            if (!_byKey.TryGetValue(key, out BlacksiteWorldRow row) || !row.SeenByMember(observerSlot))
                return;
            if (now - row.MemberSawAt(observerSlot) <= BlacksiteSightPolicy.VisualGraceSeconds)
                return;
            row.Hide(observerSlot);
            if (!row.InSight && IsThreat(row.Class)) ChangeCount++;
        }

        // Hearing and radio never make a member fire-eligible (#314 locality is the Source field), so
        // they never overwrite what an eye or the game holds.
        internal BlacksiteWorldChange Heard(ulong key, object subject, BlacksiteWorldClass cls, Vector3 position,
            bool outside, ulong observer, float now)
            => Report(key, subject, cls, BlacksiteWorldSource.Heard, position, outside, observer, now);

        internal BlacksiteWorldChange Told(ulong key, object subject, BlacksiteWorldClass cls, Vector3 position,
            bool outside, ulong observer, float now)
            => Report(key, subject, cls, BlacksiteWorldSource.Told, position, outside, observer, now);

        // A game fact: never expires, overwrites Class and Source, keeps the sight fields.
        internal void Live(ulong key, object subject, BlacksiteWorldClass cls, Vector3 position, bool outside, float now)
        {
            if (!_byKey.TryGetValue(key, out BlacksiteWorldRow row))
            {
                Add(key, subject, cls, BlacksiteWorldSource.Live, position, outside, 0UL, now);
                return;
            }
            row.Subject = subject;
            Reclass(row, cls);
            row.Source = BlacksiteWorldSource.Live;
            row.Place(position, outside, 0UL, now, newTrack: false);
        }

        internal bool Forget(ulong key)
        {
            if (!_byKey.TryGetValue(key, out BlacksiteWorldRow row))
                return false;
            _byKey.Remove(key);
            _rows.Remove(row);
            if (IsThreat(row.Class)) ChangeCount++;
            return true;
        }

        internal void Reset()
        {
            if (HasThreat) ChangeCount++;
            _rows.Clear();
            _byKey.Clear();
        }

        internal int Expire(float now, List<BlacksiteWorldRow> expired)
        {
            int count = 0;
            for (int i = _rows.Count - 1; i >= 0; i--)
            {
                BlacksiteWorldRow row = _rows[i];
                if (!Expired(row.Source, row.SeenAt, now))
                    continue;
                _rows.RemoveAt(i);
                _byKey.Remove(row.Key);
                expired.Add(row);
                if (IsThreat(row.Class)) ChangeCount++;
                count++;
            }
            return count;
        }

        // A slot whose member is dead or gone: its eyes see nothing from now on and the next holder
        // of the slot starts unannounced.
        internal void ReleaseSlot(int slot)
        {
            if ((uint)slot >= BlacksiteWorldRow.SlotCount)
                return;
            for (int i = 0; i < _rows.Count; i++) _rows[i].ReleaseSlot(slot);
        }

        private BlacksiteWorldChange Report(ulong key, object subject, BlacksiteWorldClass cls,
            BlacksiteWorldSource source, Vector3 position, bool outside, ulong observer, float now)
        {
            if (!_byKey.TryGetValue(key, out BlacksiteWorldRow row))
            {
                Add(key, subject, cls, source, position, outside, observer, now);
                return BlacksiteWorldChange.New;
            }
            if (row.InSight || row.Source == BlacksiteWorldSource.Live)
                return BlacksiteWorldChange.None;
            Adopt(row, subject, cls);
            row.Source = source;
            row.Unsample();
            row.Place(position, outside, observer, now, newTrack: false);
            return BlacksiteWorldChange.Refreshed;
        }

        private BlacksiteWorldRow Add(ulong key, object subject, BlacksiteWorldClass cls, BlacksiteWorldSource source,
            Vector3 position, bool outside, ulong observer, float now)
        {
            var row = new BlacksiteWorldRow { Key = key, Subject = subject, Class = cls, Source = source };
            row.Place(position, outside, observer, now, newTrack: true);
            _rows.Add(row);
            _byKey.Add(key, row);
            if (IsThreat(cls)) ChangeCount++;
            return row;
        }

        // A row keeps the identity it was created with; only an unknown one learns what it is.
        private void Adopt(BlacksiteWorldRow row, object subject, BlacksiteWorldClass cls)
        {
            if (row.Subject == null) row.Subject = subject;
            if (row.Class == BlacksiteWorldClass.Unknown) Reclass(row, cls);
        }

        // A class change into, out of or between threat classes is news; one between game rows is not.
        private void Reclass(BlacksiteWorldRow row, BlacksiteWorldClass cls)
        {
            if (row.Class == cls) return;
            if (IsThreat(row.Class) || IsThreat(cls)) ChangeCount++;
            row.Class = cls;
        }
    }
}
