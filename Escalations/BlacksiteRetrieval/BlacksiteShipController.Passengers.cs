using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>A server-authored hull-local seat. Late peers never derive it from a moving body.</summary>
    internal struct BlacksitePassengerSeat : INetworkSerializable, IEquatable<BlacksitePassengerSeat>
    {
        internal ulong ObjectId;
        internal Vector3 LocalSeat;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref ObjectId);
            serializer.SerializeValue(ref LocalSeat);
        }

        public bool Equals(BlacksitePassengerSeat other)
            => ObjectId == other.ObjectId && LocalSeat == other.LocalSeat;
    }

    // Visible passengers follow the replicated hull without vanilla's independent position
    // interpolation trailing behind it. Boarding registration, not hidden colliders, owns occupancy.
    internal sealed partial class BlacksiteShipController
    {
        // Created in Awake so Netcode discovers the list before spawn.
        private NetworkList<BlacksitePassengerSeat> _passengerSeatList;

        // The applied form of the list, per peer: the seat by id, and the body it belongs to once
        // this peer has resolved it. Neither is authoritative - both are rebuilt from the list -
        // and the body cache exists only so the spawn-manager lookup is not repeated every frame.
        private readonly Dictionary<ulong, Vector3> _passengerSeats = new Dictionary<ulong, Vector3>();
        private readonly Dictionary<ulong, EnemyAI> _passengerBodies = new Dictionary<ulong, EnemyAI>();
        private readonly List<ulong> _passengerSweep = new List<ulong>();
        // Set on construction and by every list change, so a late joiner's initial list state is
        // taken on its first frame whether or not the change event fired for it.
        private bool _passengerSeatsDirty = true;

        private void Awake()
        {
            _passengerSeatList = new NetworkList<BlacksitePassengerSeat>();
            _passengerSeatList.OnListChanged += NotePassengerSeatsChanged;
        }

        private void NotePassengerSeatsChanged(NetworkListEvent<BlacksitePassengerSeat> change)
            => _passengerSeatsDirty = true;

        /// <summary>How many bodies the hull is carrying.</summary>
        internal int PassengerCount => _passengerSeats.Count;

        /// <summary>Whether this network identity is riding the hull.</summary>
        internal bool IsPassenger(ulong id) => _passengerSeats.ContainsKey(id);

        /// <summary>Host-only, idempotent registration of a physically aboard body's validated seat.</summary>
        internal Vector3 SeatPassengerServer(ulong id, EnemyAI body, Vector3 standing)
        {
            if (body != null) _passengerBodies[id] = body;
            if (_passengerSeats.TryGetValue(id, out Vector3 known)) return transform.TransformPoint(known);
            Vector3 seat = transform.InverseTransformPoint(standing);
            _passengerSeats[id] = seat;
            if (IsSpawned && IsServer && _passengerSeatList != null)
                _passengerSeatList.Add(new BlacksitePassengerSeat { ObjectId = id, LocalSeat = seat });
            BlacksiteDiagnostics.Log("Extraction",
                $"event=passenger, object={id}, action=seated, local={BlacksiteDiagnosticFormat.V(seat)}"
                + $", aboard={_passengerSeats.Count}");
            return transform.TransformPoint(seat);
        }

        /// <summary>Host-only release on casualty, unboarding, objective release or teardown.</summary>
        internal void ReleasePassengerServer(ulong id)
        {
            bool seated = _passengerSeats.Remove(id);
            _passengerBodies.Remove(id);
            if (IsSpawned && IsServer && _passengerSeatList != null)
                for (int i = _passengerSeatList.Count - 1; i >= 0; i--)
                    if (_passengerSeatList[i].ObjectId == id) { _passengerSeatList.RemoveAt(i); seated = true; }
            if (!seated) return;
            BlacksiteDiagnostics.Log("Extraction",
                $"event=passenger, object={id}, action=released, aboard={_passengerSeats.Count}");
        }

        /// <summary>
        /// #1283 S4: the deck walk from a ladder <paramref name="top"/> to <paramref name="member"/>'s reserved seat: to
        /// the door (the hold's one way in), down the seat's aisle column, onto the seat - BoardingSlot's own two proven
        /// legs behind the top's. The floor points after the top, the last exactly the seat; false with the failing leg.
        /// </summary>
        internal bool TryDeckRoute(EnemyAI member, Vector3 top, out Vector3[] deck, out string reason)
        {
            deck = null;
            Vector3 seat = BoardingSlot(member), door = ApproachPosition;
            if (!_reservedBoardingSeats.ContainsKey(member)) { reason = "no reserved seat"; return false; }
            Vector3 column = BlacksiteShipNavigation.SeatApproach(transform, seat);
            int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
            var route = new List<Vector3>();
            if (!DeckLeg(member.transform, top, door, mask, route, "top to door", out reason)
                || !DeckLeg(member.transform, door, column, mask, route, "door to aisle", out reason)
                || !DeckLeg(member.transform, column, seat, mask, route, "aisle to seat", out reason)) return false;
            if (route.Count > 0) route.RemoveAt(route.Count - 1);
            route.Add(seat);
            deck = route.ToArray();
            return true;
        }

        // One leg's corners after its first, which the leg before ended on (or the top).
        private static bool DeckLeg(Transform actor, Vector3 from, Vector3 to, int mask, List<Vector3> route, string leg, out string reason)
        {
            if (!BlacksiteLandingGeometry.WalkRoute(actor, from, to, mask, out Vector3[] corners, out reason)) { reason = leg + ": " + reason; return false; }
            for (int i = 1; i < corners.Length; i++) route.Add(corners[i]);
            return true;
        }

        /// <summary>
        /// #1283 S4: give back <paramref name="member"/>'s reserved seat. A board crossing reserves it at its begin
        /// (<see cref="TryDeckRoute"/>), so one ended early - capture, teleport, lost agent, dispose - frees it for the
        /// next boarder; a dead or destroyed member's goes back by BoardingSlot's own purge.
        /// </summary>
        internal void ReleaseBoardingSlot(EnemyAI member) => _reservedBoardingSeats.Remove(member);

        // Called after hull movement on every peer. Do not write serverPosition: vanilla
        // compares it against the transform to decide whether to publish a position update.
        internal void ApplyPassengers()
        {
            RebuildPassengerSeats();
            if (_passengerSeats.Count == 0) return;
            _passengerSweep.Clear();
            foreach (var pair in _passengerSeats)
            {
                EnemyAI body = ResolvePassengerBody(pair.Key);
                // Not spawned on this peer yet - or not any more. A seat with no body is skipped,
                // never dropped: on a late joiner the body arrives a frame or two after the list.
                if (body == null) continue;
                Vector3 seat = transform.TransformPoint(pair.Value);
                // A stationary grounded ship writes nothing; a climbing one writes every frame.
                if ((body.transform.position - seat).sqrMagnitude > 1e-6f) body.transform.position = seat;
            }
            for (int i = 0; i < _passengerSweep.Count; i++) _passengerBodies.Remove(_passengerSweep[i]);
        }

        // Rebuild only on list changes. An unspawned fixture retains its host-side registrations.
        private void RebuildPassengerSeats()
        {
            if (!_passengerSeatsDirty) return;
            _passengerSeatsDirty = false;
            if (!IsSpawned || _passengerSeatList == null) return;
            _passengerSeats.Clear();
            for (int i = 0; i < _passengerSeatList.Count; i++)
            {
                BlacksitePassengerSeat entry = _passengerSeatList[i];
                _passengerSeats[entry.ObjectId] = entry.LocalSeat;
            }
            _passengerSweep.Clear();
            foreach (var pair in _passengerBodies)
                if (!_passengerSeats.ContainsKey(pair.Key)) _passengerSweep.Add(pair.Key);
            for (int i = 0; i < _passengerSweep.Count; i++) _passengerBodies.Remove(_passengerSweep[i]);
        }

        // Late-spawned bodies are resolved lazily; successful lookups are cached.
        private EnemyAI ResolvePassengerBody(ulong id)
        {
            if (_passengerBodies.TryGetValue(id, out EnemyAI cached) && cached != null) return cached;
            EnemyAI resolved = BlacksiteObjectiveManifestController.Resolve(id)?.GetComponent<EnemyAI>();
            if (resolved != null) _passengerBodies[id] = resolved;
            else _passengerSweep.Add(id);
            return resolved;
        }

        // A passenger cannot block takeoff merely because his body remains visible and hittable.
        private bool IsPassengerCollider(Collider collider)
        {
            if (_passengerSeats.Count == 0 || collider == null) return false;
            var enemy = collider.GetComponentInParent<EnemyAI>();
            return enemy != null && IsPassenger(enemy.NetworkObjectId);
        }
    }
}
