using System;
using System.Linq;
using System.Collections.Generic;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts.Defuse;
using Y4NGZCompany.Contracts._Shared;
using Y4NGZCompany.Contracts.Payload;
using Y4NGZCompany.Escalations._Shared;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal enum BlacksiteShipPhase : byte { Inbound, Landed, Boarding, Departing, Departed, LeavingRendezvous, Offsite, Returning }
    internal sealed partial class BlacksiteShipController : NetworkBehaviour, IMagnetHost
    {
        private readonly NetworkVariable<byte> _phase = new NetworkVariable<byte>();
        private readonly NetworkVariable<bool> _doorOpen = new NetworkVariable<bool>();
        private readonly NetworkVariable<byte> _lockerDoors = new NetworkVariable<byte>();
        private readonly NetworkVariable<float> _thrust = new NetworkVariable<float>(1f);
        // #873: the landing gear result, ship-local, replicated so every peer shares the passage
        // envelope: the measured deck height, the drop landing beyond the lip and the validated
        // ladder (index 255 = none). The pure struct is assembled from them on demand.
        private readonly NetworkVariable<bool> _hoverDeployment = new NetworkVariable<bool>();
        private readonly NetworkVariable<float> _footToDeck = new NetworkVariable<float>(BlacksiteLandingGearPolicy.DefaultFootToDeck);
        private readonly NetworkVariable<Vector3> _dropLandingLocal = new NetworkVariable<Vector3>(new Vector3(0, -BlacksiteLandingGearPolicy.DefaultFootToDeck, BlacksiteLandingGearPolicy.DropZ));
        private readonly NetworkVariable<Vector3> _ladderMountLocal = new NetworkVariable<Vector3>();
        private readonly NetworkVariable<Vector3> _ladderTopLocal = new NetworkVariable<Vector3>();
        private readonly NetworkVariable<byte> _ladderIndex = new NetworkVariable<byte>(255);
        // #1305 H1: the hull's own magnet lever (R20), read by polling (LeverOn); the hull is spawned
        // per round, so a round starts with the lever off.
        private readonly NetworkVariable<bool> _magnetLever = new NetworkVariable<bool>();
        internal BlacksiteLandingAccess Access => new BlacksiteLandingAccess
        {
            GroundRoute = _groundRoute, Hover = _hoverDeployment.Value, DropLandingLocal = _dropLandingLocal.Value, LadderMountLocal = _ladderMountLocal.Value, LadderTopLocal = _ladderTopLocal.Value,
            LadderIndex = _ladderIndex.Value == 255 ? -1 : _ladderIndex.Value, FootToDeck = _footToDeck.Value,
            DropHeight = BlacksiteBoardingGeometry.Lip.y - _dropLandingLocal.Value.y, Altitude = _landingAltitude,
        };
        internal float FootToDeck => _footToDeck.Value;
        internal bool HasLadder => Access.HasLadder;
        internal bool IsHovering => _hoverDeployment.Value;
        // Deployment and the cached facility route are separate facts.
        internal bool SquadOnMoon => PassageProven;
        internal Vector3 DropLandingPosition => transform.TransformPoint(_dropLandingLocal.Value);
        internal Vector3 LadderMountPosition => transform.TransformPoint(_ladderMountLocal.Value);
        internal Vector3 LadderTopPosition => transform.TransformPoint(_ladderTopLocal.Value);
        // #874 Q4: the rope foot at the pose the hull landed on and returns to (server-only, like
        // LandingPosition): a returning member walks to it here and never under the hull flying
        // its corridor; once the hull is Landed the two poses are the same.
        internal Vector3 LandedDropLandingPosition
            => Matrix4x4.TRS(_landingPosition, _landingRotation, transform.lossyScale).MultiplyPoint3x4(_dropLandingLocal.Value);
        /// <summary>
        /// #1219 C2: where a board crossing starts - the Board order's goal. Grounded: the ladder
        /// mount (a ground hull always has a ladder; a hover hull's mount IS the rope foot). While
        /// a hover hull is away, the rope foot at its landing pose.
        /// </summary>
        internal Vector3 BoardingLinkStart => IsGrounded ? LadderMountPosition : LandedDropLandingPosition;
        /// <summary>
        /// #1219 C2: the board crossing's two ends without a NavMesh link: the ladder mount and the
        /// ladder top sampled onto the deck exactly as the ladder link's end is
        /// (<see cref="BlacksiteShipNavigation.TrySampleLadderTop"/>), else the raw top. Refused
        /// while the hull is not grounded or has no ladder.
        /// </summary>
        internal bool TryBoardingLinkEnds(out Vector3 start, out Vector3 end)
        {
            start = end = default;
            if (!IsGrounded || !HasLadder) return false;
            start = LadderMountPosition;
            if (!BlacksiteShipNavigation.TrySampleLadderTop(transform, _ladderTopLocal.Value, out end)) end = LadderTopPosition;
            return true;
        }
        // Payload carts stage on the ground beyond the drop; they cannot climb and must not ram the hull.
        internal Vector3 CartStagingPosition
        {
            get
            {
                Vector3 desired = DropLandingPosition + transform.forward * 4f;
                int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
                return BlacksiteLandingGeometry.TryFloorNav(desired, mask, out var floor, .6f) ? floor : desired;
            }
        }
        private void ApplyBoarding() => BlacksiteBoardingGeometry.Apply(transform);
        private void WriteAccessServer(in BlacksiteLandingAccess access, float footToDeck)
        {
            if (access.GroundRoute != null) _groundRoute = access.GroundRoute;
            _hoverDeployment.Value = access.Hover; _footToDeck.Value = footToDeck; _dropLandingLocal.Value = access.DropLandingLocal;
            _ladderMountLocal.Value = access.LadderMountLocal; _ladderTopLocal.Value = access.LadderTopLocal;
            _ladderIndex.Value = access.LadderIndex >= 0 ? (byte)access.LadderIndex : (byte)255;
        }
        private Vector3 _previousShipPosition;
        private Vector3 _landingPosition;
        private float _landingAltitude;
        private Quaternion _previousShipRotation;
        private Quaternion _landingRotation;
        // #874 (2026-09-23): the pose the hull landed on and returns to. Server-only values (the
        // fields are written in InitializeServer). A hover ship leaves station during the raid, so
        // a footprint test against the live transform would measure the flying hull while it is away.
        // #1233 S7: every flight is BlacksiteFlightCorridor over this pose at this proven altitude, so
        // these three ARE the flight plan; the inbound start, the departure end and the offsite hold are all its Entry.
        internal Vector3 LandingPosition => _landingPosition;
        internal Quaternion LandingRotation => _landingRotation;
        private float _began, _duration, _lastDoorUse, _boardAt;
        private Animator _door;
        private bool _visualDoorOpen, _deploymentNotified;
        private float _landedAt;
        private float _doorChangedAt;
        private NavMeshObstacle _doorObstacle;
        private readonly List<NavMeshObstacle> _hullObstacles=new List<NavMeshObstacle>();
        private Animator[] _lockers;
        private Renderer[] _flames;
        private Light[] _thrusterLights;
        private AudioSource[] _thrusterAudio;
        private AudioSource _flightAudio, _doorAudio;
        private BlacksiteShipPhase _audiblePhase;
        private byte _audibleLockers;
        private bool _audioInitialized;
        private AudioClip _arriveClip, _departClip, _landClip, _doorOpenClip, _doorCloseClip;
        // #943 S11 U5: one source per locker, parented to that locker, with the crew ship's own
        // closet clips. The hangar-door pair above stays with the actual ship door.
        private AudioSource[] _lockerAudio;
        private readonly List<ContractAudioPropagation> _lockerPropagation = new List<ContractAudioPropagation>();
        private AudioClip[] _lockerOpenClips, _lockerCloseClips;

        private BoxCollider _cargoBounds;
        private BlacksiteRetrievalMissionController _mission;
        private NavMeshDataInstance _navigation;
        private NavMeshData _navigationData;
        // #874 Q11: the deck bake in flight. The hull is not Landed while it is Baking.
        private AsyncOperation _navigationBuild;
        private BlacksiteDeckBake _deckBake;
        private float _bakeBegan;
        private readonly List<NavMeshLinkInstance> _links = new List<NavMeshLinkInstance>();
        private bool _ladderLinked;
        private readonly InteractTrigger[] _playerLadders = new InteractTrigger[2];
        // #987: no longer readonly - a saturated occupancy query grows it and asks again.
        private Collider[] _doorOccupants = new Collider[32];
        internal BlacksiteShipPhase Phase => (BlacksiteShipPhase)_phase.Value;
        internal bool DoorsOpen => _doorOpen.Value;
        internal bool IsGrounded => Phase == BlacksiteShipPhase.Landed || Phase == BlacksiteShipPhase.Boarding;
        // #874 (2026-09-20): the hull has committed to leaving and is no longer a place anything
        // can board or be taken out of. The manifest's Loaded records read it as their cutoff, so
        // an 18 s climb can never be mistaken for cargo falling out of the hold.
        internal bool IsDeparting => Phase == BlacksiteShipPhase.Departing || Phase == BlacksiteShipPhase.Departed;
        internal Vector3 BoardingPosition => transform.Find("BoardingPoint").position;
        internal Vector3 ApproachPosition => transform.Find("DoorApproach").position;
        internal Transform CargoAnchor(int slot, BlacksiteObjectiveKind kind)
        {
            // #874 Q4: an item on the cable hook is anchored to the hook for the whole trip, so
            // every peer draws it on the rope from the replicated hook state and its own record.
            if (HookCarries(slot, kind)) return CableHookAnchor;
            string name = kind == BlacksiteObjectiveKind.Payload ? "CartStorage" : kind == BlacksiteObjectiveKind.Survey ? "DroneSlot" + slot % 4
                : kind == BlacksiteObjectiveKind.Apparatus ? "ApparatusSlot" : "CargoSlot" + slot % 8;
            return transform.Find(name);
        }
        // #943 S11 U5: where the recovered cache stands once it is winched aboard. It used to be
        // CargoAnchor(0, Loot) = "CargoSlot0", the BOTTOM SHELF of the storage rack and the same
        // shelf SpawnStorageLoot has already filled with loot item 0, so a 1.20 x 0.79 m crate was
        // pinned every frame half outside a 1.1 m cabinet, on top of a piece of scrap.
        //
        // The point below is MEASURED, not chosen: tests/BlacksiteLandingCombatChecks/CargoDeckChecks.cs
        // sweeps the deck of the shipped ship prefab in 25 mm steps and keeps every standing point
        // that (a) has the ship's own floor under it, (b) holds the whole crate box clear of every
        // ship collider, (c) keeps its footprint inside the CargoBounds trigger the aboard test
        // uses, and (d) clears every reserved cell. Measured off the shipped bundle (evidence in
        // artifacts/s11u5-evidence/failing-reproduction.txt):
        //   crate footprint         1.200 x 0.566 x 0.788 m, half (0.600, 0.283, 0.394)
        //   deck plate              ship-local y = -0.037 across the hold
        //   CargoBounds interior    centre (0.000, 1.500, 2.500), size (5.400, 3.400, 12.000)
        //   StorageCloset (rack)    x 1.005..2.021, z 4.782..7.004, CargoSlot0..7 at x 1.570
        //   CartStorage             (-0.400, 0.120, 2.800), DroneSlot0..3 x -2.200..-0.900, z 2.900..4.200
        //   ApparatusSlot           (1.500, 0.300, 3.900), door plane z 8.900
        //   deployment column       |x| = 0.650, z 5.300 aft in 1.300 m steps
        // The sweep leaves four free rectangles: a port and a starboard strip along the after hold,
        // a pocket forward of the rack and a narrow one to port of the door. The point is picked
        // out of them by ONE stated rule, computed rather than eyeballed: maximise
        // min(clearance from the nearest reserved cell, distance to the edge of its own free
        // region). Maximising the pair refuses both a roomy spot one centimetre from a wall and a
        // well-centred spot pressed against the deployment column. The winner scores 0.200 m on
        // both halves, the best pair anywhere on the deck, and it sits in the port strip of the
        // after hold. Its measured margins there:
        //   0.200 m clear of the port deployment column, its tightest neighbour (a clearance
        //     already counted with a soldier's own 0.45 m agent radius), and 0.200 m of free
        //     standing room in every direction by the erosion the rule uses; walking the lanes
        //     gives 3.425 m forward, 0.200 m inboard and 0.175 m aft and outboard, and those two
        //     stop on the CargoBounds boundary itself rather than on any geometry
        //   4.44 m from CartStorage, 4.81 m from the nearest DroneSlot, 7.30 m from the
        //     aisle-to-door passage, 7.65 m from the StorageCloset, 9.05 m from CargoSlot0 and
        //     10.74 m from the door plane; footprint x -2.500..-1.300, z -3.300..-2.512, which is
        //     0.200 m inside the CargoBounds trigger on both axes, so the cache is aboard
        //   standing on the measured deck plate (ship-local -0.037, taken to the centimetre)
        // Radii are the production layout's own clearances (BlacksiteCargoLayout.AgentRadius .45,
        // LaneClearance .6, CartClearance .95), not new numbers. The point the plan guessed,
        // (-0.4, 0.12, 2.8), is CartStorage itself, and (0, deckY, 1.0) is inside the deployment
        // column; neither is free.
        // x and z are the sweep's own grid sample, not a hand-rounded value; y is the measured deck
        // plate to the centimetre. Re-run the check to re-derive them if the ship prefab changes.
        internal const string CargoDeckName = "BlacksiteCargoDeck";
        internal static readonly Vector3 CargoDeckLocal = new Vector3(-1.9f, -.04f, -2.906f);
        // Created on every peer beside the prefab's authored anchors, so the winch target is a
        // plain ship-local transform lookup and stays deterministic without replicating a point.
        internal static Transform EnsureCargoDeck(Transform ship)
        {
            if (ship == null) return null;
            Transform anchor = ship.Find(CargoDeckName);
            if (anchor != null) return anchor;
            var go = new GameObject(CargoDeckName);
            go.transform.SetParent(ship, false);
            go.transform.localPosition = CargoDeckLocal;
            return go.transform;
        }
        internal Transform CargoDeckAnchor => EnsureCargoDeck(transform);
        internal bool ContainsCargo(Vector3 point) => _cargoBounds != null && new Bounds(_cargoBounds.center, _cargoBounds.size).Contains(_cargoBounds.transform.InverseTransformPoint(point));
        private readonly List<Vector3> _boardedPositions = new List<Vector3>();
        private readonly Dictionary<EnemyAI, Vector3> _reservedBoardingSeats = new Dictionary<EnemyAI, Vector3>();
        // Prisoners and soldiers share one reservation map, so the escort cannot take his captive's seat.
        internal Vector3 BoardingSlot(EnemyAI member)
        {
            foreach (var gone in _reservedBoardingSeats.Keys.Where(s => s == null || s.isEnemyDead).ToArray()) _reservedBoardingSeats.Remove(gone);
            if (_reservedBoardingSeats.TryGetValue(member, out var reserved)) return transform.TransformPoint(reserved);
            _boardedPositions.Clear();
            foreach (var pair in _reservedBoardingSeats) if (pair.Key != member) _boardedPositions.Add(transform.TransformPoint(pair.Value));
            var enemies = RoundManager.Instance != null ? RoundManager.Instance.SpawnedEnemies : null;
            // #1283 S4: a member still crossing (walking his deck leg to his seat) is not standing where he is;
            // his reservation above stands for him, or every walker in the aisle pushes the next seat shallower.
            if (enemies != null) foreach (var enemy in enemies)
                if (enemy is BlacksiteSoldierAI other && other != member && !other.isEnemyDead
                    && (other.HasBoardedShip || (InBoardingAreaOf(other) && !other.Navigator.IsCrossingShipLink)))
                    _boardedPositions.Add(other.transform.position);
            int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
            if (!BlacksiteShipNavigation.TryBoardingSlot(transform, member.transform, ApproachPosition, _boardedPositions, mask, out var seat)) return BoardingPosition;
            _reservedBoardingSeats[member] = transform.InverseTransformPoint(seat);
            return seat;
        }
        private bool InBoardingAreaOf(BlacksiteSoldierAI other) => BlacksiteShipNavigation.InBoardingArea(transform, other.transform.position);

        public override void OnNetworkSpawn()
        {
            _mission = GetComponent<BlacksiteRetrievalMissionController>();
            _previousShipPosition = transform.position; _previousShipRotation = transform.rotation;
            _door = GetComponentsInChildren<Animator>(true).FirstOrDefault(a => a.name == "AnimatedShipDoor");
            var closet = GetComponentsInChildren<Transform>(true).First(t => t.name == "StorageCloset");
            _lockers = closet.GetComponentsInChildren<Animator>(true);
            if(_door!=null)_door.cullingMode=AnimatorCullingMode.AlwaysAnimate;
            foreach(var locker in _lockers)locker.cullingMode=AnimatorCullingMode.AlwaysAnimate;
            _cargoBounds = transform.Find("CargoBounds").GetComponent<BoxCollider>();
            // #943 S11 U5: the deck stand for the winched cache, beside the authored anchors.
            EnsureCargoDeck(transform);
            BlacksiteRappelDavit.Create(transform);
            _flames = GetComponentsInChildren<Renderer>(true).Where(r => r.name == "ThrusterFlame").ToArray();
            _thrusterLights = GetComponentsInChildren<Light>(true).Where(l => l.name == "ThrusterLight").ToArray();
            _thrusterAudio = GetComponentsInChildren<AudioSource>(true).Where(a => a.name.Contains("Thruster")).ToArray();
            InitializeAudio();
            BindButton("StartButton", 1); BindButton("StopButton", 0);
            // #1305 H1: the hull's magnet and its lever, a static mesh (the builder strips its
            // animator) bound like the door buttons.
            var magnet = GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "GiantCylinderMagnet");
            _magnetPoint = magnet != null ? magnet.Find("MagnetPoint") : null;
            _magnetLeverTransform = GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "MagnetLever");
            if (_magnetLeverTransform != null)
                (_magnetLeverTrigger = BindControl(_magnetLeverTransform, "Magnet lever (off) : [E]")).onInteract.AddListener(player => { if (player != null && player.IsOwner) UseMagnetLeverServerRpc(); });
            for (int i = 0; i < BlacksiteLandingGearPolicy.Ladders.Length; i++) BindLadder(i);
            for (int i = 0; i < _lockers.Length; i++) BindInteraction(_lockers[i].transform.Find("Cube") ?? _lockers[i].transform, i + 2, "Open / close storage : [E]", Vector3.one);
            var blocker = new GameObject("BlacksiteDoorNavigation"); blocker.transform.SetParent(transform, false);
            blocker.transform.localPosition = new Vector3(0, 2f, 8.9f);
            _doorObstacle = blocker.AddComponent<NavMeshObstacle>(); _doorObstacle.shape = NavMeshObstacleShape.Box;
            _doorObstacle.size = new Vector3(4.5f, 4.1f, .45f); _doorObstacle.carving = true;
            // #872: fixed walls, the porch sides and one box per railing collider (the front railing
            // remnants), so moon navmesh under the deck plate cannot route agents into the rails.
            var blockerNotes = BlacksiteDiagnostics.Enabled ? new List<string>() : null;
            foreach (var spec in BlacksiteHullBlockers.Specs(transform, closet, blockerNotes))
            {
                AddHullBlocker(spec.Label, spec.Center, spec.Size);
                if (!spec.Fixed) BlacksiteDiagnostics.Log("ShipAccess", $"hull blocker {spec.Label}: localCenter={spec.Center}, size={spec.Size}.");
            }
            if (blockerNotes != null) foreach (var note in blockerNotes) BlacksiteDiagnostics.Log("ShipAccess", note);
            ApplyBoarding(); ApplyDoors();
        }
        private readonly Dictionary<NavMeshObstacle, BlacksiteHullBlockerSpec> _groundCarves = new Dictionary<NavMeshObstacle, BlacksiteHullBlockerSpec>();
        private void AddHullBlocker(string label,Vector3 center,Vector3 size)
        {
            // Static moon navmesh may remain beneath a spawned prefab. Carve its solid walls,
            // rather than allowing the overlapping ground mesh to bypass the authored doorway.
            var go=new GameObject("BlacksiteHullNavigation_"+label);go.transform.SetParent(transform,false);go.transform.localPosition=center;
            var obstacle=go.AddComponent<NavMeshObstacle>();obstacle.shape=NavMeshObstacleShape.Box;obstacle.size=size;
            obstacle.carving=true;obstacle.carveOnlyStationary=false;obstacle.enabled=false;_hullObstacles.Add(obstacle);_groundCarves[obstacle]=new BlacksiteHullBlockerSpec(label,center,size);
        }
        private void BindButton(string name, int command)
        {
            var t = GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == name);
            if (t != null) BindInteraction(t, command, command == 1 ? "Open Blacksite ship : [E]" : "Close Blacksite ship : [E]");
        }
        private void BindInteraction(Transform target, int command, string label, Vector3? size = null)
            => BindControl(target, label, size).onInteract.AddListener(player => { if (player != null && player.IsOwner) UseDoorServerRpc(command); });
        private static InteractTrigger BindControl(Transform target, string label, Vector3? size = null)
        {
            var go = BlacksiteShipPresentation.CreateControlVolume(target, size ?? Vector3.one * .4f).gameObject;
            var trigger = go.AddComponent<InteractTrigger>(); DefuseInteractor.ConfigureCommonTrigger(trigger, false, .1f);
            trigger.hoverTip = label;
            // #1066 U1: the control volume is authored interactable today, so the null disabled tip
            // was latent rather than live - state it anyway, because a future gate on this button
            // would reproduce the ladder's LateUpdate NRE.
            trigger.disabledHoverTip = "[ Control locked ]";
            return trigger;
        }
        // #873: players climb the hull's short ladders through the vanilla ladder trigger when the
        // bundle carries the anchors the rebuild writes. The shipped bundle predates them: then the
        // deck sits exactly as high as the crew ship's and players jump aboard the catwalk.
        private void BindLadder(int index)
        {
            Transform Find(string name) => GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == name + index);
            Transform bottom = Find("LadderBottom"), top = Find("LadderTop"), node = Find("LadderNode"), horizontal = Find("LadderHorizontal"), volume = Find("LadderTrigger");
            if (bottom == null || top == null || node == null || horizontal == null || volume == null)
                throw new System.InvalidOperationException("BlacksiteGeometryV2 ladder anchors missing");
            int layer = LayerMask.NameToLayer("InteractableObject"); volume.gameObject.layer = layer < 0 ? 9 : layer; volume.gameObject.tag = "InteractTrigger";
            var trigger = volume.GetComponent<InteractTrigger>() ?? volume.gameObject.AddComponent<InteractTrigger>();
            DefuseInteractor.ConfigureCommonTrigger(trigger, false, .1f);
            trigger.isLadder = true; trigger.interactable = false; _playerLadders[index] = trigger; trigger.hoverTip = "Use ladder : [LMB]";
            // #1066 U1: ApplyPlayerLadders holds interactable = false for most of a deployment (always
            // in mode=hover), and vanilla's hover path reads disabledHoverTip and then calls .Replace
            // on it, so a null here is an NRE out of PlayerControllerB.LateUpdate on every frame the
            // player looks at a stowed ladder (136x in the 2026-09-15 session).
            trigger.disabledHoverTip = "[ Ladder stowed ]";
            trigger.bottomOfLadderPosition = bottom; trigger.topOfLadderPosition = top; trigger.ladderPlayerPositionNode = node; trigger.ladderHorizontalPosition = horizontal;
            BlacksiteDiagnostics.Log("ShipAccess", $"ladder {BlacksiteLandingGearPolicy.Ladders[index].Side} bound for players: bottom={bottom.localPosition}, top={top.localPosition}.");
        }
        private void ApplyPlayerLadders()
        {
            for (int i = 0; i < _playerLadders.Length; i++)
            {
                var ladder = _playerLadders[i]; if (ladder == null) continue;
                ladder.interactable = IsGrounded && !IsHovering && HasLadder && Access.LadderIndex == i;
                if (!ladder.interactable) continue;
                // Existing replicated access determines the real ground mount on every peer.
                ladder.bottomOfLadderPosition.localPosition = Access.LadderMountLocal;
            }
        }
        [ServerRpc(RequireOwnership = false)]
        private void UseDoorServerRpc(int command, ServerRpcParams rpc = default)
        {
            var player = StartOfRound.Instance?.allPlayerScripts.FirstOrDefault(p => p != null && p.actualClientId == rpc.Receive.SenderClientId);
            if (player == null || player.isPlayerDead || !IsGrounded || command < 0 || command > 3) return;
            var target = command < 2 ? _door.transform.Find("HangarDoorButtonPanel") : _lockers.ElementAtOrDefault(command - 2)?.transform;
            if (target == null || Vector3.Distance(player.transform.position, target.position) > 4f) return;
            if (Time.time - _lastDoorUse < .35f) return; _lastDoorUse = Time.time;
            if (command < 2) { if (command == 0 && DoorwayOccupied()) return; _doorOpen.Value = command == 1; }
            else _lockerDoors.Value = (byte)(_lockerDoors.Value ^ (1 << (command - 2)));
        }
        internal void InitializeServer(Vector3 landing, Quaternion rotation, float seconds, in BlacksiteLandingPlan plan, float footToDeck)
        {
            if (!IsServer) return;
            _plan = plan;
            WriteAccessServer(plan.Access, footToDeck); ApplyBoarding(); _landingPosition = landing; _landingRotation = rotation; _landingAltitude = plan.Access.Altitude; _duration = Mathf.Max(3, seconds); _began = Time.time;
            _extractionStepAt = Time.time;
            _phase.Value = (byte)BlacksiteShipPhase.Inbound; _doorOpen.Value = false; _thrust.Value = 1; FlyCorridorServer(0f);
        }
        internal void BeginBoardingServer() { if (IsServer && Phase == BlacksiteShipPhase.Landed) { _phase.Value = (byte)BlacksiteShipPhase.Boarding; _boardAt = Time.time; } }
        internal void EnsureAccessServer() { if (IsServer && IsGrounded && Time.time - _lastDoorUse > 4f) _doorOpen.Value = true; }
        /// <summary>
        /// #1219 A1: the lift-off, executed for the departure verdict. False when not the server,
        /// not grounded (Departing included, which is the latch) or boarding is not open. #1293 R2: the
        /// physical veto over the doorway box and the ladder column (<see cref="LiftOffVetoed"/>) is a
        /// fold with no clock: it holds only on terms that end on their own event - a living, controlled
        /// player in a box or on the rope, the FIFO holder, cargo on the cable, and a cart someone is
        /// attached to - and the refusal line names the terms that held it. No member holds it: the
        /// verdict is reached only once every unboarded member is stranded. True on the tick the phase
        /// flips to Departing.
        /// </summary>
        internal bool TryBeginDepartureServer(float seconds)
        {
            if (!IsServer || !IsGrounded || !CargoReadyForDeparture) return false; // not-a-wait: its one caller runs only on a PersonnelClimb departure verdict, where boarding is open; not grounded is the Departing latch
            float now = Time.time;
            if (LiftOffVetoed(out BlacksiteLiftOffHold doorway, out BlacksiteLiftOffHold ladder)) // not-a-wait: every veto term ends on its own event (a player steps out, a crossing or the crate's or hook's trip ends or the ledger's stall answer releases the FIFO holder, an attached cart is let go)
            {
                if (now >= _nextDepartureRefusedLog)
                {
                    _nextDepartureRefusedLog = now + BlacksiteObjectivePolicy.SquadMoveSampleSeconds;
                    BlacksiteDiagnostics.Log("Extraction", $"departure refused; doorway={BlacksiteLiftOffHolds.Label(doorway)}, "
                        + $"ladder={BlacksiteLiftOffHolds.Label(ladder)}");
                }
                return false;
            }
            _phase.Value = (byte)BlacksiteShipPhase.Departing; _doorOpen.Value = false;
            _duration = Mathf.Max(8, seconds); _began = now; ReleaseNavigation();
            // #1271 S8: the one-shot edge to Departing (not grounded is the latch above).
            if (_mission != null) _mission.SquadDirector?.ReportDeparture();
            return true;
        }
        // #1233 S7: the one flight. Inbound and the recall fly the proven corridor forward (the leg
        // at its altitude, then straight down the column); the departure and the hover station leave
        // fly it in reverse (straight up the column first, then the leg), so the hull only ever
        // crosses air the landing search swept. Callers pass the inbound share: 1 - t when leaving.
        private void FlyCorridorServer(float inboundShare)
            => transform.SetPositionAndRotation(BlacksiteFlightCorridor.Point(_landingPosition, _landingRotation, _landingAltitude, inboundShare), _landingRotation);
        private void Update()
        {
            if (!IsSpawned) return;
            using var hitch = BlacksiteHitchTracer.Scope(BlacksiteHitchScope.ShipUpdate);
            ApplyBoarding(); ApplyAudio(); ApplyDoors(); ApplyMagnetLever(); ApplyThrusters(); ApplyPlayerLadders(); ApplyCableHook();
            if (!IsServer) return;
            TickRendezvousServer();
            if (Phase == BlacksiteShipPhase.Inbound || Phase == BlacksiteShipPhase.Returning)
            {
                // #874 Q4: only a hover ship is ever recalled (a ground ship stays landed), and it
                // stops fourteen metres over the rope foot, so the recall lands on nothing and the
                // 23 September touchdown hold for a returning ground hull is gone with it.
                float t = Mathf.Clamp01((Time.time - _began) / _duration);
                FlyCorridorServer(t);
                // #874 (2026-09-23): the recalled hover ship picks up the crate it parked on the way
                // out, in the same statement that makes it Landed, so no member can be admitted to
                // the rope between the two and climb ahead of the lift.
                // #874 Q11 (F11): arrival starts the deck bake and holds this pose, still Inbound or
                // Returning, until the bake completes; only then is the hull Landed. Deployment,
                // the access seal, passage and ladder admission, the rehook and the hull carving
                // all key on IsGrounded, so none of them ever sees a hull without its deck.
                if (_deckBake == BlacksiteDeckBake.Baking && (_navigationBuild == null || _navigationBuild.isDone))
                    _deckBake = BlacksiteDeckBake.Ready;
                var touchdown = BlacksiteTouchdownPolicy.Arrival(t >= 1, _deckBake);
                if (touchdown == BlacksiteTouchdownStep.BeginBake) BeginNavigationBuild();
                else if (BlacksiteTouchdownPolicy.Grounds(touchdown))
                {
                    // #1219 C3: a recalled hull proves its passage again; the first Inbound landing
                    // publishes the sealed plan (SealAccessServer) before the escalation releases the squad.
                    bool relanding = Phase == BlacksiteShipPhase.Returning;
                    _phase.Value = (byte)BlacksiteShipPhase.Landed; _thrust.Value = IsHovering ? .35f : 0f; _doorOpen.Value = true; FinishNavigation(); _landedAt = Time.time;
                    if (relanding) ProveRelandingServer();
                    RehookParkedCargoServer();
                }
            }
            else if (Phase == BlacksiteShipPhase.Landed && !_deploymentNotified && Time.time - _landedAt >= 1.5f)
            {
                _deploymentNotified = true; MoonEscalationState.NotifyBlacksiteShipLanded(this, _landingPosition, _landingRotation);
            }
            else if (Phase == BlacksiteShipPhase.Departing)
            {
                float t = Mathf.Clamp01((Time.time - _began) / _duration); _thrust.Value = Mathf.Clamp01(t * 5);
                if (t > .2f) FlyCorridorServer(1f - (t - .2f) / .8f);
                if (t >= 1) { EnterExtractionStepServer(BlacksiteExtractionStep.Departed); _phase.Value = (byte)BlacksiteShipPhase.Departed; MoonEscalationState.NotifyBlacksiteShipDeparted(this); }
            }
        }
        /// <summary>
        /// #1219 A1: the ShipReady wait's fallback on the hull. A recalled ship whose return flight
        /// is over but whose deck bake has not reported done is still holding in the air at the
        /// landing bound; the bake is declared Ready so it grounds on the next frame. Every other
        /// state is left alone (the sequence simply goes on).
        /// </summary>
        private void ForceArrivalServer()
        {
            if (Phase != BlacksiteShipPhase.Returning || _deckBake != BlacksiteDeckBake.Baking) return;
            if (Time.time - _began < _duration) return;
            _deckBake = BlacksiteDeckBake.Ready;
        }
        private void LateUpdate()
        {
            if (!IsSpawned) return;
            var player = GameNetworkManager.Instance?.localPlayerController;
            Vector3 delta = transform.position - _previousShipPosition;
            if (player != null && !player.isPlayerDead && !player.isInsideFactory && _cargoBounds != null && delta.sqrMagnitude > 0 && delta.sqrMagnitude < 400f)
            {
                Vector3 relative = Quaternion.Inverse(_previousShipRotation) * (player.transform.position - _previousShipPosition);
                var cabin = new Bounds(_cargoBounds.transform.localPosition + _cargoBounds.center, _cargoBounds.size);
                if (cabin.Contains(relative)) player.transform.position = transform.TransformPoint(relative);
            }
            // #874 (2026-09-20): the passengers ride the hull in the same place and for the same
            // reason the local player does - one frame-accurate write after the hull has moved -
            // and on EVERY peer, because a damped chase of a departing hull is what leaves a body
            // trailing under the deck. See BlacksiteShipController.Passengers.cs.
            _previousShipPosition = transform.position; _previousShipRotation = transform.rotation;
            ApplyRendezvousReplica();
            ApplyRappel();
            ApplyPassengers();
            ApplyHoverVisual();
        }
        // #1219 A1: the doorway box, shared by the door button (which respects every body) and the
        // lift-off veto (#1293 R2: which counts only the bodies whose hold ends on its own event).
        private Vector3 DoorwayProbeCenter => transform.TransformPoint(new Vector3(0, 1.6f, 8.5f));
        private static readonly Vector3 DoorwayProbeHalf = new Vector3(2f, 1.6f, .65f);
        private bool DoorwayOccupied() => Occupied(DoorwayProbeCenter, DoorwayProbeHalf);

        // #1293 R2: the lift-off veto's one fold, over the doorway box and the ladder column.
        private bool LiftOffVetoed(out BlacksiteLiftOffHold doorway, out BlacksiteLiftOffHold ladder)
        {
            doorway = LiftOffBodies(DoorwayProbeCenter, DoorwayProbeHalf);
            ladder = LadderHold();
            return (doorway | ladder) != BlacksiteLiftOffHold.None;
        }

        // #873: nobody on the ladder column when the ship lifts off.
        // #987: the hover column is BOUNDED. Before this it was one box
        // |top.y - mount.y| * .5 + 1 half-height tall - a formula written for a six-metre ground
        // ladder. On the 2026-09-12 fourteen-metre rappel that made a 1.6 x 15.97 x 1.6 m shaft
        // that ran from a metre over the moon floor up THROUGH the ship's own bow and three metres
        // out of the top of it, and the 32-slot query inside it saturated on the ship's own deck
        // plate, lip, hull and rails, which the old Occupied() read as "occupied". The rope itself
        // is already owned by the two authoritative tests above (the ship FIFO for soldiers,
        // RappelUsedByPlayer for a climbing player), so the box only has to cover the two places a
        // body can stand that neither of those sees: the rope foot and the deck lip. Each is one
        // swept body column tall (BlacksiteWorldLadderColumn.BodyColumnMetres, 1.96 m), which is
        // the same height every other Blacksite occupancy probe uses for a standing body.
        //
        // #874 (2026-09-23): the probe answers WHICH terms held, not whether any did, so the
        // refusal line names them without probing twice. The cache's term is the rope only: a
        // crate standing on the moon, hooked or parked, is not a rope occupant.
        private BlacksiteLiftOffHold LadderHold()
        {
            if (!HasLadder) return BlacksiteLiftOffHold.None;
            Vector3 mount = LadderMountPosition, top = LadderTopPosition;
            if (IsHovering)
            {
                // #874 Q4: the cable hook carrying cargo is the rope's too.
                var hold = BlacksiteLiftOffHolds.Rope(Ladder.HasOccupant(Time.time), RappelUsedByPlayer, CargoUsesCable || HookBusy);
                float body = BlacksiteWorldLadderColumn.BodyColumnMetres;
                var column = new Vector3(.8f, body * .5f, .8f);
                hold |= LiftOffBodies(mount + Vector3.up * (body * .5f), column);
                hold |= LiftOffBodies(top + Vector3.up * (body * .5f), column);
                return hold;
            }
            return LiftOffBodies((mount + top) * .5f + Vector3.up, new Vector3(.8f, Mathf.Abs(top.y - mount.y) * .5f + 1f, .8f));
        }

        // #1293 R2 (A1d): the lift-off's body probe. Each body in the box is classified and folded by
        // BlacksiteLiftOffHolds.Of: a living, controlled player and a cart someone is attached to hold
        // it; a dead or uncontrolled player, an unattached cart, an enemy and any Blacksite member do
        // not, because nothing of ours would ever end them (a member is stranded by the time the
        // departure verdict asks, and a crossing member is the FIFO's holder, not a body here).
        private BlacksiteLiftOffHold LiftOffBodies(Vector3 center, Vector3 half)
        {
            var hold = BlacksiteLiftOffHold.None;
            int n = ProbeBox(center, half);
            for (int i = 0; i < n; i++)
            {
                var c = _doorOccupants[i];
                if (!ForeignBody(c)) continue;
                // A Blacksite soldier is an EnemyAI too, so it is classified first.
                if (c.GetComponentInParent<BlacksiteSoldierAI>() != null)
                    hold |= BlacksiteLiftOffHolds.Of(BlacksiteLiftOffBody.Member, active: false);
                else if (c.GetComponentInParent<PlayerControllerB>() is var player && player != null)
                    hold |= BlacksiteLiftOffHolds.Of(BlacksiteLiftOffBody.Player, player.isPlayerControlled && !player.isPlayerDead);
                else if (c.GetComponentInParent<Y4NGZCompany.Contracts.Payload.PayloadCartController>() is var cart && cart != null)
                    hold |= BlacksiteLiftOffHolds.Of(BlacksiteLiftOffBody.Cart, cart.IsAttached || cart.IsNpcAttached);
                else if (c.GetComponentInParent<EnemyAI>() != null)
                    hold |= BlacksiteLiftOffHolds.Of(BlacksiteLiftOffBody.Enemy, active: false);
                // Loose loot is cargo, not a reason to strand an already-boarded squad.
            }
            return hold;
        }

        // #987: the buffer cap is a query limit, never a verdict. The probe boxes sit ON the ship,
        // so the ship's own colliders are most of what any of them can see, and answering
        // "occupied" on a full buffer made the departure decision a function of the hull's collider
        // count. Own children and body capsules are filtered as they always were; a saturated query
        // grows the shared buffer and asks again, up to MaxOccupantProbe slots, so the answer is
        // decided by what is actually in the box. If even the grown buffer saturates - 256 colliders
        // inside a box that is at most 4 x 3.2 x 1.3 m - the filtered scan's verdict stands, because
        // an occupant that the scan did not already find in 256 hits is not what is holding the ship.
        private const int MaxOccupantProbe = 256;
        private int ProbeBox(Vector3 center, Vector3 half)
        {
            while (true)
            {
                int n = Physics.OverlapBoxNonAlloc(center, half, _doorOccupants, transform.rotation, ~0, QueryTriggerInteraction.Ignore);
                if (n < _doorOccupants.Length || _doorOccupants.Length >= MaxOccupantProbe) return n;
                _doorOccupants = new Collider[Mathf.Min(MaxOccupantProbe, _doorOccupants.Length * 2)];
            }
        }

        // #874 S2.2: the body capsule is player separation, not an occupant; the ship's own children
        // are the hull; registered passengers are aboard, not crossing the boarding lane.
        private bool ForeignBody(Collider c)
            => c != null && !c.transform.IsChildOf(transform) && !BlacksiteBodyCollision.IsBody(c) && !IsPassengerCollider(c);

        // The door button's probe: every body counts, Blacksite soldiers included. Loose loot is
        // cargo; a person crossing the lane (including one holding an item) still holds the door.
        private bool Occupied(Vector3 center, Vector3 half)
        {
            int n = ProbeBox(center, half);
            for (int i = 0; i < n; i++)
            {
                var c = _doorOccupants[i];
                if (!ForeignBody(c)) continue;
                if (c.GetComponentInParent<PlayerControllerB>() != null || c.GetComponentInParent<EnemyAI>() != null
                    || c.GetComponentInParent<Y4NGZCompany.Contracts.Payload.PayloadCartController>() != null) return true;
            }
            return false;
        }
        // #987 / #1219 A1: a live player standing ON the ship is the hover station rule's
        // PlayerAboard veto, the one risk of leaving station that the rope vetoes do not cover (the
        // doorway and ladder probes belong to the lift-off, TryBeginDepartureServer). The volume is
        // every place a body can stand on the hull, not just the cabin:
        // LateUpdate carries a player with the ship only inside the CargoBounds trigger, so a player
        // who climbs the rope, steps off the lip onto the deck and stops climbing is standing on
        // hull that nothing carries, and the lift-off would leave them fourteen metres up. Both
        // halves are the canonical tests other callers already use - ContainsCargo for the cabin and
        // BlacksiteBoardingGeometry.OnDeck (|x| < 2.8, z in (-8, 12), |y| < 0.65) for the walkable
        // deck strip, which covers the aisle, the door, the porch and the whole lip. WithinPassage is
        // deliberately NOT used: its drop and ladder zones are on the moon floor under a hovering
        // ship, and a player standing there is not aboard anything.
        internal bool PlayerAboardShip
        {
            get
            {
                if (StartOfRound.Instance == null) return false;
                foreach (var player in StartOfRound.Instance.allPlayerScripts)
                {
                    if (player == null || !player.isPlayerControlled || player.isPlayerDead || player.isInsideFactory) continue;
                    if (Aboard(player.transform.position)) return true;
                }
                return false;
            }
        }
        private void ApplyDoors()
        {
            if(_visualDoorOpen!=DoorsOpen){_visualDoorOpen=DoorsOpen;_doorChangedAt=Time.time;}
            foreach(var hull in _hullObstacles)if(hull!=null)
            {
                bool active=IsGrounded && !IsHovering;
                if(active && !hull.enabled && _groundCarves.TryGetValue(hull,out var spec)) BlacksiteHullBlockers.ConfigureGroundObstacle(transform,hull,spec);
                hull.enabled=active;
            }
            if (_doorObstacle != null) _doorObstacle.enabled = IsGrounded && (!DoorsOpen || Time.time-_doorChangedAt<1f);
            if (_door != null) { _door.SetBool("OpenInOrbit", false); _door.SetBool("Closed", !_doorOpen.Value); }
            if (_lockers == null) return;
            for (int i = 0; i < _lockers.Length; i++) foreach (var p in _lockers[i].parameters)
                if (p.type == AnimatorControllerParameterType.Bool && p.name == "Open") _lockers[i].SetBool(p.nameHash, (_lockerDoors.Value & (1 << i)) != 0);
        }
        private void ApplyThrusters()
        {
            if (_flames == null) return; bool active = _thrust.Value > .01f;
            foreach (var flame in _flames)
            {
                for (var parent = flame.transform.parent; parent != null && parent != transform; parent = parent.parent) parent.gameObject.SetActive(true); flame.gameObject.SetActive(active); if (active && GameNetworkManager.Instance?.localPlayerController?.gameplayCamera != null)
                    {
                    BlacksiteShipPresentation.FaceFlame(flame.transform, GameNetworkManager.Instance.localPlayerController.gameplayCamera.transform.position, -transform.up);
                }
            }
            foreach (var l in _thrusterLights) l.enabled = active;
            foreach (var a in _thrusterAudio) { a.enabled = true; a.loop = true; a.volume = _thrust.Value * .65f; if (active && !a.isPlaying) a.Play(); else if (!active && a.isPlaying) a.Stop(); }
        }
        private void InitializeAudio()
        {
            // Sources cloned from the scene may be disabled or depend on the crew-ship scripts.
            // This ship owns its sources; it only borrows immutable vanilla clips.
            _flightAudio = gameObject.AddComponent<AudioSource>();
            _flightAudio.playOnAwake = false; _flightAudio.spatialBlend = 1f;
            _flightAudio.minDistance = 15f; _flightAudio.maxDistance = 180f;
            _flightAudio.rolloffMode = AudioRolloffMode.Linear;
            _doorAudio = gameObject.AddComponent<AudioSource>();
            _doorAudio.playOnAwake = false; _doorAudio.spatialBlend = 1f;
            _doorAudio.minDistance = 3f; _doorAudio.maxDistance = 35f;
            var round = StartOfRound.Instance;
            if (round != null)
            {
                _arriveClip = round.shipArriveSFX; _departClip = round.shipDepartSFX;
                _doorOpenClip = round.openingHangarDoorAudio;
            }
            var sources = GetComponentsInChildren<AudioSource>(true);
            _landClip = sources.FirstOrDefault(a => a.name.Contains("LandingTurbulence") && a.clip != null)?.clip;
            // #943 S11 U5: the Resources.FindObjectsOfTypeAll<AudioClip>() name scan is gone. It
            // swept every loaded asset in the process, so on a modded profile the ship door's clip
            // was whichever bundle happened to load first. Vanilla itself has exactly one hangar
            // door clip and plays it in both directions (StartOfRound.PlayerLoadedShipDoorAnimation
            // -> shipDoorAudioSource.PlayOneShot(openingHangarDoorAudio, 1f)), so the serialized
            // field is the whole answer for the ship door, and the lockers no longer borrow it.
            _doorCloseClip = _doorOpenClip;
            TryResolveLockerClips(out _lockerOpenClips, out _lockerCloseClips);
            _lockerPropagation.Clear();
            _lockerAudio = ConfigureLockerAudio(_lockers, _lockerPropagation);
            BlacksiteDiagnostics.Log("Audio", $"event=lockerSources, lockers={(_lockerAudio == null ? 0 : _lockerAudio.Length)}, "
                + $"min={LockerAudioMinDistance:0.00}, max={LockerAudioMaxDistance:0.00}, volume={LockerCueVolume:0.00}, "
                + $"openClips={(_lockerOpenClips == null ? 0 : _lockerOpenClips.Length)}, closeClips={(_lockerCloseClips == null ? 0 : _lockerCloseClips.Length)}");
            foreach (var a in _thrusterAudio)
            {
                for (Transform t = a.transform; t != null && t != transform; t = t.parent) t.gameObject.SetActive(true);
                a.enabled = true; a.spatialize = false; a.spatialBlend = 1f;
                a.minDistance = a.name.Contains("Ambient") ? 18f : 5f;
                a.maxDistance = a.name.Contains("Ambient") ? 140f : 35f;
                a.rolloffMode = AudioRolloffMode.Linear; a.playOnAwake = false;
            }
        }
        // #943 S11 U5: the storage lockers are cabinets, not the hangar door.
        //
        // minDistance 1.00 m: the closet's own carve box is 1.1 m wide (BlacksiteHullBlockers
        // "Closet"), so 0.55 m is its half width; plus a soldier/player agent radius (0.45 m, the
        // layout's own BlacksiteCargoLayout.AgentRadius) that is exactly how close a listener's
        // head can get to the cabinet face. Nothing is nearer, so nothing needs a nearer near field.
        // maxDistance 12.00 m: the ship interior's own length. The CargoBounds trigger the aboard
        // test uses is authored 5.4 x 3.4 x 12.0 m (asset-pipeline/BlacksiteShipBuild.cs:348), so a
        // cabinet is audible from anywhere aboard and no further; the hangar door's 35 m is the
        // range of a four-metre powered door, not of a latch.
        internal const float LockerAudioMinDistance = 1f, LockerAudioMaxDistance = 12f;
        // The gain: equal presentation against vanilla's own cue for these exact clips. The crew
        // ship's AnimatedObjectTrigger plays boolTrue/boolFalseAudios at PlayOneShot(clip, 1f) on a
        // Linear-rolloff source; ours runs the shared facility carry curve instead, so the gain
        // that presents the same level at the shared 2 m reference is 1 * linear(2 m) / carry(2 m),
        // clamped at the engine's 1.0 ceiling (which is what binds: the carry curve is well below
        // Linear at that reference, so the equal-presentation value asks for more than unity).
        private static readonly float LockerCueVolumeValue = Mathf.Min(1f,
            ((LockerAudioMaxDistance - 2f) / (LockerAudioMaxDistance - LockerAudioMinDistance))
            / Mathf.Max(1e-4f, ContractSpatialAudio.BuildCarryFalloffCurve().Evaluate(2f / LockerAudioMaxDistance)));
        internal static float LockerCueVolume => LockerCueVolumeValue;
        /// <summary>
        /// One AudioSource per locker, parented to that locker and routed through the shared
        /// facility carry profile. Returns the sources in locker order; entries are null only where
        /// the animator itself is.
        /// </summary>
        internal static AudioSource[] ConfigureLockerAudio(Animator[] lockers, List<ContractAudioPropagation> propagation)
        {
            if (lockers == null) return new AudioSource[0];
            var sources = new AudioSource[lockers.Length];
            for (int i = 0; i < lockers.Length; i++)
            {
                if (lockers[i] == null) continue;
                var host = new GameObject("BlacksiteLockerAudio" + i);
                host.transform.SetParent(lockers[i].transform, false);
                var source = host.AddComponent<AudioSource>();
                source.playOnAwake = false; source.loop = false;
                var driver = ContractSpatialAudio.ApplyFacilityCarryProfile(source, LockerAudioMaxDistance, LockerAudioMinDistance);
                if (driver != null) propagation?.Add(driver);
                sources[i] = source;
            }
            return sources;
        }
        /// <summary>
        /// The crew ship's own StorageCloset cue, found by walking StartOfRound's elevatorTransform
        /// rather than by a global name scan. Falls back to silence, never to the hangar door.
        /// </summary>
        internal static bool TryResolveLockerClips(out AudioClip[] open, out AudioClip[] close)
        {
            open = null; close = null;
            Transform crew = StartOfRound.Instance != null ? StartOfRound.Instance.elevatorTransform : null;
            if (crew == null) return false;
            AnimatedObjectTrigger trigger = null;
            foreach (var candidate in crew.GetComponentsInChildren<AnimatedObjectTrigger>(true))
                if (candidate != null && candidate.name.IndexOf("StorageCloset", StringComparison.OrdinalIgnoreCase) >= 0) { trigger = candidate; break; }
            if (trigger == null) return false;
            AudioClip[] Live(AudioClip[] clips) => clips == null ? null : clips.Where(c => c != null).ToArray();
            open = Live(trigger.boolTrueAudios); close = Live(trigger.boolFalseAudios);
            // A closet serialized with only a loop clip still gives both directions something.
            if ((open == null || open.Length == 0) && trigger.thisAudioSource != null && trigger.thisAudioSource.clip != null)
                open = new[] { trigger.thisAudioSource.clip };
            if (close == null || close.Length == 0) close = open;
            return open != null && open.Length > 0;
        }
        private void ApplyAudio()
        {
            for (int i = 0; i < _lockerPropagation.Count; i++) _lockerPropagation[i]?.Tick();
            if (_flightAudio == null) return;
            if (!_audioInitialized || _audiblePhase != Phase)
            {
                // Late join should hear the current loop, not replay the landing ceremony.
                AudioClip clip = Phase == BlacksiteShipPhase.Inbound ? _arriveClip
                    : Phase == BlacksiteShipPhase.Departing ? _departClip
                    : Phase == BlacksiteShipPhase.Landed && _audioInitialized && !IsHovering ? _landClip : null;
                if (clip != null) _flightAudio.PlayOneShot(clip, .8f);
                _audiblePhase = Phase;
            }
            if (_audioInitialized && _visualDoorOpen != DoorsOpen)
            {
                var clip = DoorsOpen ? _doorOpenClip : _doorCloseClip;
                if (clip != null) _doorAudio.PlayOneShot(clip, .7f);
            }
            // #943 S11 U5: one cue per changed locker, at that locker, open and close told apart.
            if (_audioInitialized && _audibleLockers != _lockerDoors.Value && _lockerAudio != null)
            {
                int changed = _audibleLockers ^ _lockerDoors.Value;
                for (int i = 0; i < _lockerAudio.Length; i++)
                {
                    if ((changed & (1 << i)) == 0) continue;
                    bool opening = (_lockerDoors.Value & (1 << i)) != 0;
                    var clips = opening ? _lockerOpenClips : _lockerCloseClips;
                    var source = _lockerAudio[i];
                    if (source == null || clips == null || clips.Length == 0) continue;
                    // Vanilla picks a random clip out of the same array rather than pitching one.
                    var clip = clips[UnityEngine.Random.Range(0, clips.Length)];
                    if (clip != null) source.PlayOneShot(clip, LockerCueVolume);
                }
            }
            _audibleLockers = _lockerDoors.Value; _audioInitialized = true;
        }
        private void BeginNavigationBuild()
        {
            using var hitch = BlacksiteHitchTracer.Scope(BlacksiteHitchScope.TouchdownNavigation);
            // Add a local walkable ship surface; do not rebuild or remove the moon's navmesh.
            _navigationBuild = BlacksiteShipNavigation.BeginBuild(transform, _door != null ? _door.transform : null, out _navigationData);
            _deckBake = BlacksiteDeckBake.Baking; _bakeBegan = Time.time;
        }
        private void FinishNavigation()
        {
            using var hitch = BlacksiteHitchTracer.Scope(BlacksiteHitchScope.TouchdownNavigation);
            _navigationBuild = null;
            _navigation = NavMesh.AddNavMeshData(_navigationData);
            ConnectAccessLinks(HasLadder);
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Blacksite] Independent ship navigation added after a {Time.time - _bakeBegan:0.00}s bake; moon access links={_links.Count} (drop={(_links.Count > 0 ? 1 : 0)}, ladders={(_ladderLinked ? 1 : 0)}), boarding={BoardingPosition}; crew navmesh retained.");
        }
        // The drop link and, while the ladder is trusted, the ladder link. Re-run when the touchdown
        // seal or a re-landing proof writes the access, which may pick the other ladder.
        private void ConnectAccessLinks(bool ladder)
        {
            ReleaseLinks();
            var links = BlacksiteShipNavigation.ConnectAccess(transform, DropLandingPosition, ladder && HasLadder ? LadderMountPosition : (Vector3?)null, ladder && HasLadder ? _ladderTopLocal.Value : (Vector3?)null);
            _links.AddRange(links); _ladderLinked = ladder && HasLadder && links.Count > 1;
        }
        private void ReleaseLinks() { foreach (var link in _links) if (link.valid) link.Remove(); _links.Clear(); _ladderLinked = false; }
        private void ReleaseNavigation()
        {
            ReleaseLinks();
            // #874 Q11: a hull despawned mid-bake (round end, abort) cancels the bake before its data goes.
            if (_navigationBuild != null && !_navigationBuild.isDone && _navigationData != null) NavMeshBuilder.Cancel(_navigationData);
            _navigationBuild = null; _deckBake = BlacksiteDeckBake.None;
            if (_navigation.valid) _navigation.Remove(); if (_navigationData != null) UnityEngine.Object.Destroy(_navigationData); _navigationData = null;
        }
        public override void OnNetworkDespawn()
        {
            if (IsServer && _cargoMotion.Value == 3 && CargoContainer != null && CargoContainer.NetworkObject.IsSpawned) CargoContainer.NetworkObject.Despawn(true);
            // #874 (2026-09-20): the hull is going away, so nothing may still be riding it. A
            // round that ends mid-escort, an abort and an ordinary post-departure despawn all
            // arrive here, and each of them has to leave a surviving witness standing in the
            // world with his own agent rather than frozen where the ship used to be.
            if (IsServer) _mission?.Objectives?.ReleaseWitnessHoldsServer();
            ReleaseNavigation(); ReleaseRappel(); base.OnNetworkDespawn();
        }
    }
}
