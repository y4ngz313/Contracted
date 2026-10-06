using System;
using System.Collections.Generic;
using System.Linq;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using Y4NGZCompany.Contracts._Shared;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal struct BlacksiteBatonSnapshot : INetworkSerializable, IEquatable<BlacksiteBatonSnapshot>
    {
        internal BlacksiteBatonPhase Phase; internal byte Swing; internal uint Sequence; internal double Started;
        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter { serializer.SerializeValue(ref Phase); serializer.SerializeValue(ref Swing); serializer.SerializeValue(ref Sequence); serializer.SerializeValue(ref Started); }
        public bool Equals(BlacksiteBatonSnapshot b) => Phase == b.Phase && Swing == b.Swing && Sequence == b.Sequence && Started == b.Started;
    }
    public partial class BlacksiteSoldierAI
    {
        private Transform _nativeCarryHolder;
        internal Transform ItemCarryHolder
        {
            get
            {
                if (_nativeCarryHolder == null && _rigBinding?.RightHand != null)
                {
                    var root = new GameObject("BlacksiteNativeItemHolder");
                    _nativeCarryHolder = root.transform;
                    var hand = _rigBinding.RightHand;
                    if (hand.name == BlacksiteSoldierRigBinding.RightHandAnchorName && hand.parent != null) hand = hand.parent;
                    _nativeCarryHolder.SetParent(hand, false);
                    // Measured ServerItemHolder local pose on the native Player.prefab.
                    _nativeCarryHolder.localPosition = new Vector3(.002f, .0563f, -.0456f);
                    _nativeCarryHolder.localRotation = Quaternion.identity;
                }
                return _nativeCarryHolder != null ? _nativeCarryHolder : DriveCarryAnchor;
            }
        }

        internal void AnnouncePickup() => RequestSquadVoice(BlacksiteVoiceEvent.Pickup, volume: .7f);
        private readonly NetworkVariable<byte> _ropeDirection = new NetworkVariable<byte>();
        // #874 S5.2 (#873): the traversal kind, beside _ropeDirection rather than widening it.
        // _ropeDirection is not free to redefine: AlignRopeHands gates on it and the S4.0
        // [UAT][WorldLadder] begin/end lines already print `ropeDirection=` for the score script to
        // parse. A second replicated byte adds a fact instead of changing the meaning of one.
        private readonly NetworkVariable<byte> _traversalKind = new NetworkVariable<byte>();
        internal BlacksiteTraversalKind TraversalKind => (BlacksiteTraversalKind)_traversalKind.Value;
        private Avatar _equipmentAvatar, _originalAvatar;
        private RuntimeAnimatorController _guardOriginalController;
        private bool _guardTraversalActive;
        private int _guardTraversalStateHash;
        // Definitive missing states are remembered per kind; an unbound Animator is retried.
        private int _guardTraversalMissingKinds;
        // #874 S5: the host's clip rate for the crossing (jump and rope), one value for both loadouts and peers.
        private readonly NetworkVariable<float> _crossingPlaybackSpeed = new NetworkVariable<float>(1f);
        private Transform _ropeLeftHand, _ropeRightHand;
        private Vector3 _ropeVisualOffset;
        private readonly NetworkVariable<BlacksiteBatonSnapshot> _baton = new NetworkVariable<BlacksiteBatonSnapshot>();
        private readonly Dictionary<Renderer, bool> _rifleRenderers = new Dictionary<Renderer, bool>();
        private GameObject _batonModel, _batonHolster; private Transform _batonHand, _batonHip; private BlacksiteBatonEffects _batonEffects;
        // #1283 S3b D1: the rifle pair a baton member keeps for the ladder (EnsureBatonEquipment).
        private RuntimeAnimatorController _rifleAnimator, _batonAnimator; private Avatar _rifleAvatar; private string _batonAnimation;
        private uint _presentedBatonSequence; private float _batonLastContactAt, _batonLastElapsed; private int _nextSwing;
        private Vector3 _previousBatonHead, _previousBatonGrip; private bool _hasBatonHead; private bool _batonMissingReported;
        private readonly HashSet<int> _batonHitTargets = new HashSet<int>(); private readonly Collider[] _batonContacts = new Collider[48];
        private readonly RaycastHit[] _batonBlocks = new RaycastHit[32]; private readonly NavMeshPath _batonPath = new NavMeshPath();
        internal bool UsesBaton => _spawnState.Value.IsConfigured && _spawnState.Value.Equipment == 1;
        internal bool BatonCommitted => UsesBaton && (_baton.Value.Phase == BlacksiteBatonPhase.Swing || _baton.Value.Phase == BlacksiteBatonPhase.Drawing);
        private float _cargoThreatUntil, _batonHitUntil;
        private uint _batonHitSequence, _presentedBatonHitSequence;
        internal bool HasImmediateCargoThreat => Time.time < _cargoThreatUntil;
        /// <summary>
        /// #988 round 2, BLOCKING 1: the cargo hand-over latch, and why it exists.
        ///
        /// This is a POLL. The duty board calls it on every pickup dwell and only acts when it
        /// returns true, which it does exactly once - on the frame the stow completes. #988 B2 gave
        /// <see cref="TickBatonServer"/> an unconditional alert re-draw, and with a monster inside
        /// ThreatRadius or an 18 m squad order live that re-draw runs every server frame: Ready
        /// becomes Stowing, Stowing completes to Stowed and is flipped back to Drawing inside the
        /// same call, so the poll could never observe Stowed and the pickup was refused forever.
        /// B3 made it worse by construction - the member the board picks is the one that is NOT in
        /// combat, which is precisely the member that got an order about a monster it never saw.
        /// <c>HandsUnavailable</c> does not protect it: that is only true once the item is already
        /// in hand.
        ///
        /// So a hand-over in progress suppresses the alert re-draw. The latch is refreshed by every
        /// poll, cleared the moment the hands are actually taken (<see cref="TickBatonServer"/>'s
        /// own <c>HandsUnavailable</c> arm), and bounded by <see cref="CargoHandoverSeconds"/> so a
        /// job that ends, a prop that vanishes or a board that simply stops asking can never leave
        /// a raider unable to draw. It suppresses the ALERT draw only: a member already holding a
        /// live close-in keeps its weapon through the ordinary combat path, and the four-second
        /// idle auto-stow and DamageRequiresStow are untouched. (#874 WP F removed the Coil-Head
        /// observation draw this used to leave alone: watching a weapons-excluded class is not a
        /// reason to hold a weapon.)
        /// </summary>
        internal bool PrepareHandsForCargo()
        {
            if (!UsesBaton) { _cargoHandoverUntil = 0f; return true; }
            _cargoHandoverUntil = Time.time + CargoHandoverSeconds;
            if (_baton.Value.Phase == BlacksiteBatonPhase.Stowed) return true;
            if (_baton.Value.Phase == BlacksiteBatonPhase.Ready || _baton.Value.Phase == BlacksiteBatonPhase.Drawing) SetBatonPhase(BlacksiteBatonPhase.Stowing);
            return false;
        }
        /// <summary>
        /// One whole stow plus one more. <see cref="BlacksiteBatonPolicy.StowSeconds"/> is 1.7 s, and
        /// a poll that arrives while the weapon is mid-DRAW restarts the stow from there, so two
        /// stow durations is the longest a single hand-over can legitimately take. It is a bound on
        /// a latch every poll refreshes, not a duration anything waits out.
        /// </summary>
        internal const float CargoHandoverSeconds = BlacksiteBatonPolicy.StowSeconds * 2f;
        private float _cargoHandoverUntil;
        internal bool IsCargoHandoverPending => Time.time < _cargoHandoverUntil;
        // #874 S3.3. Both numbers are read off the assets, not chosen. asset-pipeline/
        // BlacksiteCarryLayerBuild.cs authors the Carry layer's leave transition as a fixed .12 s
        // crossfade with no exit time, and BlacksiteRiflePose.cs:45 blends the support hand over
        // .16 s (delta / .16f), which the aim helper drives on every ownership change. The carry
        // clip therefore still owns the arm for .12 s after `Carrying` drops, and the grip only
        // finishes arriving .16 s after that. S3.1 measured what happens when the weapon is
        // re-shown before both have run: the arm is in the carry pose with the rifle in it.
        internal const float CarryLayerLeaveSeconds = .12f;
        internal const float SupportBlendSeconds = BlacksiteAimPosePolicy.SupportBlendSeconds;
        // #874 S3.2b: the leave transition alone. The Carry layer's weight is now blended down from
        // the same predicate that drops the `Carrying` bool, over the same .12 s the authored leave
        // transition takes, so both finish together and ownership can return the moment they do.
        // The extra .16 s this used to add bought nothing: the support blend cannot start before
        // ownership returns - `handsOnWeapon` gates it - so all the tail did was hold an
        // empty-handed soldier in a rifle idle for five more frames. The support hand blends in
        // after the rifle is back, visibly, exactly as it already does out of a reload.
        internal const float CarryHandbackSeconds = CarryLayerLeaveSeconds;
        // A controller swap rebinds the animator, so no carry state survives it; only the support
        // grip has to arrive.
        internal const float TraversalHandbackSeconds = SupportBlendSeconds;
        // _carryReleaseUntil is the whole Releasing window - the item's own release plus the
        // handback tail. _carryHoldUntil is the shorter part of it during which the carry clip
        // still holds the item on the hand pose.
        private float _carryReleaseUntil, _carryHoldUntil;
        private BlacksiteHandOwner _lastHandOwner = BlacksiteHandOwner.Weapon;
        private bool _lastCarryTwoHanded;
        // #874 S2.4: dead, captured, boarded and consumed all resolve to the same answer, so the
        // equipment writers below stop re-showing a weapon on an actor that is not in the world.
        // #1064 WP3, #1271: Traversal is a crossing that takes both hands - the replicated climb, or a jump
        // whose published rate plays its clip - with Navigator.RequiresBothHands as the host's own immediate
        // half, never HasCommittedMotion (an elevator ride). A hop too short for the clip keeps whatever held
        // the hands, the rifle or the cargo. #874 S8: the cargo arm is HasOccupiedCargoHands (is something
        // physically in the hands), never the custody predicate IsObjectiveCarrier, which a Witness escort
        // satisfies with empty hands; custody, duty-board and protected-carrier decisions keep that one.
        internal BlacksiteHandOwner CurrentHandOwner => ReplicatedStunned || !BlacksiteLifecycle.HoldsWeapon(EffectiveLifecycle) ? BlacksiteHandOwner.Incapacitated
            : _climbingPose || (_jumpingPose && BlacksiteTraversalPresentation.ShowsJumpClip(_crossingPlaybackSpeed.Value))
                || Navigator.RequiresBothHands ? BlacksiteHandOwner.Traversal
            : HasOccupiedCargoHands ? BlacksiteHandOwner.Cargo : Time.time < _carryReleaseUntil ? BlacksiteHandOwner.Releasing : BlacksiteHandOwner.Weapon;
        internal bool TryCarryWrists(out Vector3 left, out Vector3 right)
        {
            left=right=default;
            if (_rigBinding?.LeftHand==null || _rigBinding.RightHand==null) return false;
            left=_rigBinding.LeftHand.parent.position;right=_rigBinding.RightHand.parent.position;return true;
        }
        // The item's own release, then the handback the assets owe it. Both clocks only ever move
        // forward, so a re-arm from AdvanceRelease cannot shorten a window already granted.
        internal void BeginCarryRelease(float seconds)
        {
            _carryHoldUntil = Mathf.Max(_carryHoldUntil, Time.time + seconds);
            _carryReleaseUntil = Mathf.Max(_carryReleaseUntil, _carryHoldUntil + CarryHandbackSeconds);
        }
        // Traversal never played a carry clip, so it owes only the support blend.
        private void BeginTraversalHandback() { _carryReleaseUntil = Mathf.Max(_carryReleaseUntil, Time.time + TraversalHandbackSeconds); }
        // True while the Carry override still poses the arms: its current state is a carry state
        // for the whole authored leave transition, and its next state is one for the whole enter.
        // A controller without the layer answers false, so the timed window remains the guarantee.
        private int _carryLayer = -1;
        private RuntimeAnimatorController _carryLayerController;
        // The two states BlacksiteCarryLayerBuild authors. Measured on the shipped controller:
        // CarryOneHanded is short-name hash -947199821, which is the state S3.1 caught still
        // playing on the frame the rifle came back.
        private static readonly int[] CarryStateHashes =
            { Animator.StringToHash("CarryOneHanded"), Animator.StringToHash("CarryTwoHanded") };
        private static bool IsCarryState(AnimatorStateInfo info)
        {
            for (int i = 0; i < CarryStateHashes.Length; i++) if (info.shortNameHash == CarryStateHashes[i]) return true;
            return false;
        }
        // #874 S3.2b: the one predicate behind both the animator's `Carrying` bool and the Carry
        // layer's weight, so the clip that layer plays and the weight it plays at can never
        // disagree. It is a property rather than a field recorded by UpdateCarryAnimation because
        // UpdateAnimation - and with it DriveAimPose - runs before UpdateEquipmentPresentation
        // every frame, so a recorded value would reach the aim helper one frame stale and would
        // disagree with the pose fixture's own drive order.
        internal bool CarryClipHeld
        {
            get
            {
                var owner = CurrentHandOwner;
                return owner == BlacksiteHandOwner.Cargo || (owner == BlacksiteHandOwner.Releasing && Time.time < _carryHoldUntil);
            }
        }

        // Latched once the Carry override has actually posed the arms. Before that the layer has
        // produced nothing - its default state is empty - so its weight says nothing about the
        // arms, and a soldier that has never carried must not be held rifleless waiting for it.
        private bool _carryLayerRaised;

        internal bool CarryLayerHoldsHands
        {
            get
            {
                if (_animator == null || _animator.runtimeAnimatorController == null) return false;
                if (!ReferenceEquals(_carryLayerController, _animator.runtimeAnimatorController))
                { _carryLayerController = _animator.runtimeAnimatorController; _carryLayer = _animator.GetLayerIndex("Carry"); }
                if (_carryLayer < 0 || _carryLayer >= _animator.layerCount) return false;
                return IsCarryState(_animator.GetCurrentAnimatorStateInfo(_carryLayer))
                    || (_animator.IsInTransition(_carryLayer) && IsCarryState(_animator.GetNextAnimatorStateInfo(_carryLayer)));
            }
        }
        private void UpdateCarryAnimation()
        {
            var owner = CurrentHandOwner;
            // Every exit from cargo owes the handback, including the paths that never reach
            // BlacksiteCarryPresentation.BeginRelease - a carrier with no recorded hand pose, or an
            // objective record cleared without a release. Without this the carry clip would still
            // be playing on the frame the rifle came back.
            if (owner == BlacksiteHandOwner.Weapon && _lastHandOwner == BlacksiteHandOwner.Cargo)
            { BeginCarryRelease(0f); owner = BlacksiteHandOwner.Releasing; }
            _lastHandOwner = owner;
            // Only the item's own release keeps the arm on the carry clip; the tail after it is
            // what lets the authored leave transition actually run.
            bool carrying = CarryClipHeld;
            // Only a real prop reports its own grip. BlacksiteHandOwner.Cargo now excludes the walking
            // witness (OccupiesHands), so a null item here is the payload cart, which is pushed
            // with both hands - the default the cart has always been presented with.
            if (owner == BlacksiteHandOwner.Cargo && TryGetMission(out var mission))
                _lastCarryTwoHanded = mission.Objectives.CarriedItem(NetworkObjectId)?.itemProperties?.twoHandedAnimation ?? true;
            TrySetBool(Animator.StringToHash("Carrying"), carrying);
            TrySetBool(Animator.StringToHash("CarryTwoHanded"), _lastCarryTwoHanded);
            // A gesture accepted a frame before the hands were taken must not survive in the
            // animator and fire on the frame they come back.
            if (owner != BlacksiteHandOwner.Weapon) { TryResetTrigger(SignalHash); TryResetTrigger(RadioHash); }
        }
        // "Can the hands act" (only the baton asks): any owner but the rifle, and never on a jump (#1271: a clipless hop
        // keeps its owner, but the host's jump tick disables combat on every one). Not the animator's slung stance (#874 Q7).
        private bool HandsUnavailable => CurrentHandOwner != BlacksiteHandOwner.Weapon || _jumpingPose || Navigator.IsJumping;
        private void SetBatonPhase(BlacksiteBatonPhase phase) => SetBatonPhase(phase, 0f);
        /// <summary>
        /// #988 F06: <paramref name="alreadyElapsed"/> back-dates the replicated start instant so a
        /// phase can begin PART WAY THROUGH. Only <see cref="ReadyBaton"/> uses it, and only to
        /// reverse a stow that is still in progress. <c>Started</c> stays a server-clock instant, so
        /// every peer - including one that joins during the phase - derives the same elapsed time,
        /// the same clip offset and the same cue decision from it.
        /// </summary>
        private void SetBatonPhase(BlacksiteBatonPhase phase, float alreadyElapsed)
        {
            if (!IsServerAuthority() || _baton.Value.Phase == phase) return;
            var value = _baton.Value; value.Phase = phase; value.Started = GetServerTime() - alreadyElapsed; value.Sequence++;
            if (phase == BlacksiteBatonPhase.Swing) { value.Swing = (byte)(_nextSwing++ % 3); _batonHitTargets.Clear(); _hasBatonHead = false; _batonLastElapsed = 0; }
            // #988 B2: the close-quarters flag belongs to ONE swing. Every phase change out of
            // Swing clears it, so an ordinary commit on the next think keeps both abort walls.
            else _batonCloseQuarters = false;
            _baton.Value = value;
        }
        /// <summary>
        /// #988 F06: <paramref name="handsUnavailable"/> is the hands, and nothing else. It used to
        /// be called <c>disabled</c> and carry "this member may not attack now" as well, which is
        /// how one hazard frame - the evade arm's <c>TickServer(disabled: true)</c> - emptied the
        /// hand of a member whose hands were free and made it pay the draw again. That fact now
        /// reaches the weapon through <see cref="BlacksiteCombatController.HoldFireWithFreeHands"/>,
        /// which runs the ordinary tick; the only thing that empties a hand here is a hand that is
        /// genuinely taken.
        /// </summary>
        internal void TickBatonServer(bool handsUnavailable)
        {
            if (!UsesBaton) return;
            // #988 round 2: hands actually taken ends the hand-over, so the latch never outlives the
            // pickup it was opened for.
            if (handsUnavailable || HandsUnavailable) { _cargoHandoverUntil = 0f; SetBatonPhase(BlacksiteBatonPhase.Stowed); return; }
            var state = _baton.Value; float elapsed = (float)(GetServerTime() - state.Started);
            // #874 S2.3: the authored recovery frame, not the clip end. The presentation crossfades
            // out on this same replicated instant because the phase change is what it reads.
            if (state.Phase == BlacksiteBatonPhase.Swing && elapsed >= BlacksiteBatonPolicy.ReadyAt(state.Swing)) SetBatonPhase(BlacksiteBatonPhase.Ready);
            else if (state.Phase == BlacksiteBatonPhase.Drawing && elapsed >= BlacksiteBatonPolicy.DrawSeconds) SetBatonPhase(BlacksiteBatonPhase.Ready);
            else if (state.Phase == BlacksiteBatonPhase.Stowing && elapsed >= BlacksiteBatonPolicy.StowSeconds) SetBatonPhase(BlacksiteBatonPhase.Stowed);
            else if (state.Phase == BlacksiteBatonPhase.Ready && !BatonAlertIsLive && Time.time - _batonLastContactAt > 4) SetBatonPhase(BlacksiteBatonPhase.Stowing);
            if (BatonAlertIsLive) ReadyBaton();
        }
        /// <summary>
        /// #988 B2: the draw begins on the ALERT, not on the reach annulus. Every melee member in
        /// the 2026-09-12 run entered its fight with `baton=Stowed, batonElapsed=395.52s` - the
        /// batons had been stowed since tâ‰ˆ2 - and then spent the whole 1.233 s
        /// <see cref="BlacksiteBatonPolicy.DrawSeconds"/> under a bite cadence of 0.77 s. That is
        /// 1.6 bites, about a third of a soldier's life expectancy, paid before the first swing can
        /// be committed.
        ///
        /// The trigger is a live, ATTACKABLE monster this member holds by identity - its squad
        /// order, or the contact it is fighting or remembers - inside the existing alert radius,
        /// and nothing else: a player contact, a noise or an alert with no monster behind it draws
        /// nothing, so the #314 locality model and the existing player rules are untouched.
        /// <see cref="TickBatonServer"/> has already returned on unavailable hands, so cargo,
        /// traversal, every jump and capture still prevent drawing. The damage reaction stows only when
        /// a hard stun or unavailable hands require it, not when hazard avoidance clears a target.
        ///
        /// #874 WP F: by IDENTITY, and attackable. This used to be the order OR any exact monster
        /// anchor inside ThreatRadius, and an anchor is a position with no contact behind it, so an
        /// Old Bird or a Coil-Head sighting drew the baton as readily as a crawler did - and the
        /// Coil-Head observer's own watch was a second, unconditional draw. Neither class can be
        /// hit (BlacksiteThreatAdapterRegistry.IsAttackable), so neither draws; a genuinely
        /// attackable contact beside them still does, because it is asked about on its own. A squad
        /// sighting still pre-draws, through its row's identity (#1293 R1).
        /// </summary>
        private bool BatonAlertIsLive
        {
            get
            {
                if (!UsesBaton || !IsServerAuthority() || isEnemyDead) return false;
                // #988 round 2, BLOCKING 1: a hand-over in progress owns the weapon state until the
                // stow completes and the pickup resolves. Without this the re-draw below cancels the
                // stow PrepareHandsForCargo asked for, on the same frame, forever.
                if (IsCargoHandoverPending) return false;
                // The squad order keeps its own reach: it is raised only on members inside
                // MonsterAlertOrderRadius and lapses on its own, exactly as before. ReceiveMonsterAlertOrder
                // refuses an unattackable contact, and this re-asks because the give-up memo can
                // change the answer while an order is held.
                if (HasMonsterAlertOrder && BlacksiteThreatAdapterRegistry.IsAttackableBy(_monsterOrderEnemy, this)) return true;
                var contact = CombatEnemyTarget;
                if (BlacksiteThreatAdapterRegistry.IsAttackableBy(contact, this)
                    && Vector3.Distance(transform.position, contact.transform.position) <= BlacksiteCombatPolicy.ThreatRadius)
                    return true;
                // The #988 B2 pre-draw on squad knowledge: a reporter 18 m from a crawler raises no
                // order, and a teammate 8 m from it must not pay the whole draw when it walks into
                // view. #1293 R1: a Monster row the squad's eyes wrote whose contact this member's
                // weapon can stop, inside the radius by the row and by the body - so an Old Bird,
                // Coil-Head or keeper row, or a stale row beside a crawler elsewhere, draws nothing.
                if (!TryGetWorld(out BlacksiteWorldModel world)) return false;
                var rows = world.Rows;
                for (int i = 0; i < rows.Count; i++)
                {
                    BlacksiteWorldRow row = rows[i];
                    if (row.Class == BlacksiteWorldClass.Monster && row.Source == BlacksiteWorldSource.Saw
                        && row.Subject is EnemyAI tracked && tracked != null
                        && Vector3.Distance(transform.position, row.Position) <= BlacksiteCombatPolicy.ThreatRadius
                        && Vector3.Distance(transform.position, tracked.transform.position) <= BlacksiteCombatPolicy.ThreatRadius
                        && BlacksiteThreatAdapterRegistry.IsAttackableBy(tracked, this))
                        return true;
                }
                return false;
            }
        }
        /// <summary>
        /// #988 F06: a draw that interrupts a stow RESUMES the weapon; it does not start again from
        /// the hip.
        ///
        /// Stowing is 1.7 s and the weapon is still in the hand for the first 0.6 s of it
        /// (<see cref="BlacksiteBatonPolicy.InHand"/>), so an alert that returns early used to
        /// replay a whole 1.233 s draw - and, because the replicated sequence advanced, a second
        /// #929 draw cue on every peer - for a baton that had barely moved. The stow is
        /// <c>elapsed / StowSeconds</c> of the way to the hip, so the draw that reverses it begins
        /// at the mirror of that point and finishes in the time the weapon actually still owes.
        /// That also decides the cue honestly: the presentation plays the draw cue only inside the first
        /// 0.3 s of a draw, which a barely-begun stow now starts well past and a nearly-finished
        /// one still starts inside.
        /// </summary>
        private void ReadyBaton()
        {
            _batonLastContactAt = Time.time;
            var state = _baton.Value;
            if (state.Phase == BlacksiteBatonPhase.Stowed) { SetBatonPhase(BlacksiteBatonPhase.Drawing); return; }
            if (state.Phase != BlacksiteBatonPhase.Stowing) return;
            float stowed = Mathf.Clamp01((float)(GetServerTime() - state.Started) / BlacksiteBatonPolicy.StowSeconds);
            SetBatonPhase(BlacksiteBatonPhase.Drawing, BlacksiteBatonPolicy.DrawSeconds * (1f - stowed));
        }
        internal BlacksiteFireReadiness LastBatonReadiness { get; set; }
        // #874 S2.1: the close-in's own evidence. These are the numbers the melee trace reports,
        // recorded where they are decided rather than reconstructed from rifle state afterwards.
        // #1063 WP2: _batonVertical is SIGNED - positive for a contact above this member, negative
        // for one below - because the vertical gate is asymmetric
        // (BlacksiteEngagementGeometry.WithinVerticalReach). Its "nothing measured on this think"
        // sentinel is therefore NaN and NOT the -1 that _batonRange and _batonAngle carry: -1 is a
        // perfectly ordinary gap now (a contact a metre below), so the two would be
        // indistinguishable on the melee line, and collider= cannot disambiguate them either
        // because it is also `none` for a contact measured at its root. NaN fails both walls of
        // the gate, so an unmeasured gap is refused rather than admitted.
        private float _batonRange = -1f, _batonAngle = -1f, _batonVertical = float.NaN;
        private string _batonBlocked = "none";
        // #1063 WP2: the contact's measured body profile, and the collider the measurement was
        // taken against. The profile is what the close-in's ring, the reach gate and the hit band
        // are all read from - the executor used to pass the Humanoid row for every contact - and
        // the collider is reported on the [UAT][Melee] line so the next log proves the pick.
        private BlacksiteBodyProfile _batonContactProfile = BlacksiteBodyProfile.Humanoid;
        private Collider _batonContactBody;
        // #1010 A2: the plane every melee lane is sampled on. The ring used to be built at the
        // CONTACT's own Y, which is a plane nobody can stand on the moment the contact is not
        // standing on the same floor as the soldier - a flea riding a squadmate's head at 1.65 to
        // 1.9 m refused all six lanes for want of ground and held ApproachBlocked for 49 s. It is
        // the acting soldier's own feet by default, the carrier's feet when the contact is riding a
        // soldier, and the ground under the contact when the contact is overhead.
        private float _batonLaneFloor;
        // #1010 A2: the contact's own body, resolved once per contact rather than per think. The
        // melee geometry is measured against the collider the weapon has to physically meet, not
        // against a pivot the body may not be anywhere near. #1219 F1.1: that collider is the
        // vanilla-hittable one (BlacksiteHitResolver.ResolveBody).
        private object _batonBoundsContact; private Collider _batonBoundsCollider;
        // #1010 A2: what one committed sweep actually overlapped. Reset when the hit window opens
        // and accumulated across every capsule the sweep lays, so a swing that met no eligible
        // contact at all can say so instead of whiffing silently.
        // #1009 A2 (verifier F5): the verdict belongs to the SWING, not to each frame of its hit
        // window. The counters were zeroed at the top of EVERY window frame, so a swing that
        // connected on its first frame and overlapped nothing on its second still printed whiff.
        // They are now zeroed ONCE as the window opens, accumulated across all of its frames, and
        // read once as it closes. `contacts=` reports DISTINCT colliders rather than the
        // per-capsule sum, which counted one body once for every one of the up to seventeen
        // capsules it happened to lie in.
        private int _batonSweepCandidates;
        private readonly HashSet<int> _batonSweepColliders = new HashSet<int>();
        private bool _batonSweepWindowOpen; private byte _batonSweepWindowSwing;
        private Vector3 _batonApproachGoal; private bool _hasBatonApproachGoal;
        // #874 S7.2: how many whole lane steps the next sweep starts past this member's own
        // preferred bearing. Advanced by one per blocked engagement, wrapped over the lane count, and
        // cleared the moment a lane is taken, so it is a retry order and never a permanent offset.
        private int _batonLaneRotation;
        // #874 S10.1, #1293 R3: the contact's motion, read from its squad table row at every
        // engagement pass, so the commit and the windup lane are judged where the contact will be.
        private Vector3 _batonContactVelocity;
        private object _batonVelocityContact;
        // #1293 R3: the contact's relative root and the baton phase sequence the last engagement saw.
        private Vector3 _batonEngagedOffset;
        private uint _batonEngagedSequence;
        // The last measured profile belongs to the tracked contact, not the current engagement's
        // diagnostics. LostSight can reset those while that contact's swing is still in flight.
        private BlacksiteBodyProfile _batonTrackedProfile = BlacksiteBodyProfile.Humanoid;
        private float _batonPredictedRange = -1f, _batonRadialSpeed, _batonLead;
        // #988 B2: this swing was committed from inside the minimum reach, or on a contact closing
        // through it. Cleared by every phase change out of Swing (SetBatonPhase), so it describes
        // one swing and never a member.
        private bool _batonCloseQuarters;
        private static readonly List<BlacksiteSoldierAI> BatonPeerScratch = new List<BlacksiteSoldierAI>();
        // #1010 A2: its own list rather than BatonPeerScratch, because the carrier lookup runs at
        // the top of a close-in whose lane sweep is still going to use that one.
        private static readonly List<BlacksiteSoldierAI> BatonCarrierScratch = new List<BlacksiteSoldierAI>();
        // Live melee lane claims. The squad director is the authority when a mission owns these
        // members, but a claim is also visible without one, so two attackers never converge on the
        // same point through a code path the director cannot see.
        private static readonly Dictionary<BlacksiteSoldierAI, Vector3> BatonLaneClaims = new Dictionary<BlacksiteSoldierAI, Vector3>();
        private static readonly List<BlacksiteSoldierAI> BatonClaimScratch = new List<BlacksiteSoldierAI>();
        // Preferred lane first, then the rest of the circle. 60 degrees keeps two attackers on
        // adjacent lanes 1.72 m apart, clear of the .95 m lane clearance they must both respect.
        private static readonly int[] BatonLaneSteps = { 0, 1, -1, 2, -2, 3 };
        internal bool HasBatonApproachGoal => _hasBatonApproachGoal;
        internal Vector3 BatonApproachGoal => _batonApproachGoal;
        internal string BatonBlockedReason => _batonBlocked;
        // #874 S10.1: the swing lock reads these from the combat controller's target selection.
        internal bool BatonSwingInFlight => UsesBaton && _baton.Value.Phase == BlacksiteBatonPhase.Swing;
        internal uint BatonSwingSequence => _baton.Value.Sequence;
        // #874 S10.4: the mission layer's cover ring and this ring are one placement around one
        // contact, so the ring consults the same live claims the lane sweep does.
        internal bool IsMeleeLaneTaken(Vector3 point) => IsBatonLaneTaken(point);
        private int PhysicalOccupancyMask => StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;

        internal void ReleaseBatonApproach()
        {
            _hasBatonApproachGoal = false;
            BatonLaneClaims.Remove(this);
            ReleaseCombatApproach();
        }

        internal BlacksiteFireReadiness EngageBaton(PlayerControllerB player, EnemyAI enemy)
        {
            var readiness = ResolveBatonEngagement(player, enemy);
            // #874 S10.1: the close-in is released by the outcomes that END it, not by every outcome
            // that is not a walk. Releasing on `Firing` is what dropped the stop distance, the lane
            // claim and the combat approach profile for the whole of a committed swing, and
            // releasing on one `LostSight` frame is P3 in findings-combat-perception. The retained
            // set is Firing, Turning, Acquiring and HoldingLane - a member mid-attack, mid-turn,
            // mid-draw or holding a blocked ring is still closing on this contact. The release set
            // is exactly NoTarget, HandsBusy, PoseMissing, LostSight, Ineligible and ApproachBlocked.
            //
            // #1019 S5: the six named values become their outcomes, and the sets are IDENTICAL by
            // enumeration over the values ResolveBatonEngagement can actually return:
            //   RELEASED - LostSight (NoLine; a VALUE test because the other NoLine readiness
            //              values are retained);
            //              NoTarget and Ineligible (NoContact); HandsBusy and PoseMissing
            //              (NotArmed); ApproachBlocked (OutOfBand). Six, exactly as before.
            //   RETAINED - Firing (Firing), Turning and Acquiring (Aiming), Repositioning
            //              (Closing), HoldingLane (Holding). Four, exactly as before.
            // The NotArmed arm widens on paper to the nine other pose, grip and animator values,
            // and OutOfBand to OutOfRange - none of which this method can return
            // (MeleeApproachChecks.cs:764 pins the produced set) - so no reachable answer moves.
            switch (BlacksiteCombatPolicy.Outcome(readiness))
            {
                case BlacksiteEngagementOutcome.NoLine when readiness == BlacksiteFireReadiness.LostSight:
                case BlacksiteEngagementOutcome.NoContact:
                case BlacksiteEngagementOutcome.NotArmed:
                case BlacksiteEngagementOutcome.OutOfBand:
                    ReleaseBatonApproach();
                    break;
            }
            LastBatonReadiness = readiness;
            return readiness;
        }

        private BlacksiteFireReadiness ResolveBatonEngagement(PlayerControllerB player, EnemyAI enemy)
        {
            _batonBlocked = "none"; _batonRange = _batonAngle = -1f; _batonVertical = float.NaN;
            _batonPredictedRange = -1f; _batonRadialSpeed = 0f; _batonLead = 0f;
            _batonContactBody = null; _batonContactProfile = BlacksiteBodyProfile.Humanoid;
            if (player != null) _batonEngagedOffset = player.transform.position - transform.position;
            else if (enemy != null) _batonEngagedOffset = enemy.transform.position - transform.position;
            _batonEngagedSequence = _baton.Value.Sequence;
            if (HandsUnavailable) { _batonBlocked = "hands owned by " + CurrentHandOwner; return BlacksiteFireReadiness.HandsBusy; }
            // #874 WP F / #1262 P4: the contact THIS baton can stop, not the exclusion alone, so an Old
            // Bird the squad can see, or a keeper, is never closed on or swung at.
            if (player == null && !BlacksiteThreatAdapterRegistry.IsAttackableBy(enemy, this))
            { TrackBatonContact(null); return BlacksiteFireReadiness.NoTarget; }
            // #1219 F1.1 (D6): soldiers only hit what vanilla calls hittable. An enemy none of whose
            // colliders the shovel contract resolves to it is not closed on or swung at - every
            // swing would pass through it - and its type is named once per round.
            if (player == null && BatonContactBody(enemy) == null)
            {
                BlacksiteHitResolver.ReportNotHittable(enemy, MemberIndex);
                _batonBlocked = "not hittable"; TrackBatonContact(null);
                return BlacksiteFireReadiness.NoTarget;
            }
            if (_batonModel == null) { _batonBlocked = "baton visual missing"; return BlacksiteFireReadiness.PoseMissing; } // A missing required asset must never become an invisible attack.
            Vector3 aim = player != null ? GetCombatAimPoint(player) : enemy.eye != null ? enemy.eye.position : enemy.transform.position + Vector3.up * 1.1f;
            if (!HasCombatLineOfSight(aim, player, enemy)) { _batonBlocked = "no line of sight to the contact"; return BlacksiteFireReadiness.LostSight; }
            ReadyBaton();
            // #1010 A2: the range gate and the vertical gate are two questions and were one number.
            // `_batonRange` was the 3D distance between transform roots while `_batonAngle` was
            // already 2D, so every metre of height the contact had was spent out of the horizontal
            // reach the close-in actually walks: a contact 1.65 m up could only pass the 2.17 m
            // gate from 1.41 m away horizontally, and the only destination the close-in ever issues
            // is the 1.72 m stand-off circle - 0.31 m further out than the furthest point where the
            // gate can open. The gate is now horizontal and `_batonVertical` carries the height on
            // its own, exactly as BatonInReach's two parameters always said it should.
            Vector3 root = player != null ? player.transform.position : enemy.transform.position;
            // #1009 A2 (F6): `??` on a UnityEngine.Object bypasses the lifetime check, so a
            // destroyed-but-not-null player would be preferred over a live enemy. The contact is
            // chosen by the same `player != null` test the owner argument beside it uses.
            Vector3 target = MeasureBatonContact(player != null ? (Component)player : enemy, root);
            _batonLaneFloor = ResolveBatonLaneFloor(player, enemy, target);
            // #1063 WP2: the profile this contact's ring, gate and hit band are all read from. It
            // is the threat table's own measurement, behind the same per-contact description cache
            // ResolveThreatIntent uses and keyed on the same collider, so it costs a dictionary
            // probe and a state recompute per think rather than a hierarchy walk or a ray. A player
            // contact has no adapter and keeps the Humanoid row, which is the row the whole melee
            // executor used to pass for every contact of every class.
            _batonContactProfile = ResolveBatonContactProfile(player, enemy);
            Vector3 flat = Vector3.ProjectOnPlane(target - transform.position, Vector3.up);
            _batonRange = flat.magnitude;
            // #1063 WP2: SIGNED, positive for a contact above this member. The gate is one-sided -
            // see BlacksiteEngagementGeometry.WithinVerticalReach - and Mathf.Abs here is what made
            // a contact on the floor and a contact on a catwalk one number.
            _batonVertical = target.y - transform.position.y;
            _batonAngle = flat.sqrMagnitude > .0001f ? Vector3.Angle(transform.forward, flat) : 0f;
            // #874 S10.1: the swing is decided on the range it will be TAKEN at, leading by what is
            // left of the hit window's opening frame. #874 WP F: a captor RIDING a squadmate is posed
            // on the victim, not walking, so it is read as standing still.
            bool riding = player == null && IsAllyAttachedContact(enemy)
                && BlacksiteVanillaMonsters.TryGetCaptureAnchor(enemy, out _);
            TrackBatonContact(player != null ? (object)player : enemy,
                player != null ? player.NetworkObjectId : enemy.NetworkObjectId, riding);
            var committedState = _baton.Value;
            bool inFlight = committedState.Phase == BlacksiteBatonPhase.Swing;
            // #988 B2: the close-quarters flag describes ONE swing in flight. SetBatonPhase clears
            // it on every phase change out of Swing, but the replicated snapshot can also be
            // written directly (a late join, a state restore, a fixture driving the phase), so the
            // authority on "is there a close-quarters swing in flight" is the phase itself.
            if (!inFlight) _batonCloseQuarters = false;
            int swing = inFlight ? committedState.Swing : _nextSwing % 3;
            float committedElapsed = inFlight ? (float)(GetServerTime() - committedState.Started) : 0f;
            _batonLead = Mathf.Max(0f, BlacksiteCombatPolicy.ImpactLeadSeconds(swing) - committedElapsed);
            Vector3 toward = target - transform.position;
            Vector3 self = _agent != null && _agent.enabled ? _agent.velocity : Vector3.zero;
            _batonRadialSpeed = BlacksiteContactVelocity.RadialSpeed(
                _batonContactVelocity.x, _batonContactVelocity.y, _batonContactVelocity.z,
                self.x, self.y, self.z, toward.x, toward.y, toward.z);
            _batonPredictedRange = BlacksiteContactVelocity.PredictRange(_batonRange, _batonRadialSpeed, _batonLead);
            if (inFlight)
            {
                var committed = TickCommittedSwing(target, swing, committedElapsed, out bool aborted);
                // An aborted swing is back on Ready and decides again from the gates below.
                if (!aborted) return committed;
            }
            // #988 B2: it is on top of me, so hit it. The reach gate [1.62, 2.17] is narrower than
            // the weapon - swings 1 and 2 connect from a measured 0.73 m - and until now the only
            // answer to a contact inside that inner wall was to back out to the 1.72 m stand-off
            // (S2.1b below), which put the member back in the gate to print
            // `holding the gate: predicted=0.00` and back out again. Against a 4.8 m/s Thumper that
            // is a free bite every 0.77 s while the actor refuses a strike the weapon can
            // physically make. The commit is taken here, before the back-out, and only for the
            // shape the gate refuses and the measured band accepts; everything outside it still
            // falls through to the unchanged S2.1b/S10.1 path below.
            //
            // MONSTER CONTACTS ONLY, and that is a deliberate narrowing rather than an oversight.
            // The measured failure is a monster: a 4.8 m/s Thumper walks onto a soldier and stays
            // there, and the reach gate refuses a strike the weapon can make while it bites every
            // 0.77 s. A player is the opposite case - 6.6 m/s, free to kite - and
            // BlacksiteMeleeApproachChecks pins exactly that: a player sprinting out of the band
            // must break a committed swing (its 6.6 m/s case measured the break moving from 3.34 m
            // to 2.41 m when this branch was allowed to take player contacts, which is the homing
            // attack S10.1 exists to refuse). Against a player the unchanged S2.1b back-out stands.
            if (enemy != null && player == null
                && _baton.Value.Phase == BlacksiteBatonPhase.Ready
                && BlacksiteCombatPolicy.BatonCloseQuartersSwingFor(_batonContactProfile, _nextSwing,
                    _batonRange, _batonVertical, _batonAngle, out int closeSwing))
            {
                CommitCloseQuartersSwing(closeSwing);
                return TickCommittedSwing(target, _baton.Value.Swing, 0f, out _);
            }
            // #874 S2.1b: too close is a concrete reason, not a whiff. The measured arc passes over
            // a contact inside the minimum reach, so this steps back out to the stand-off under a
            // named reason instead of reporting Firing at a swing that cannot connect. Measured on
            // the instantaneous range: stepping back out of a contact that is beside the actor NOW
            // is right whatever it is about to do.
            if (BlacksiteEngagementGeometry.InsideMinimumReach(_batonContactProfile, _batonRange, _batonVertical))
                return CloseOnBatonTarget(target, flat, "inside minimum reach: range=" + _batonRange.ToString("0.00"));
            // Two ranges, two different questions. The PREDICTED range decides whether a swing is
            // worth taking, because that is the geometry the head will meet. The INSTANTANEOUS range
            // decides whether this member is standing in the gate right now, because a lane issued
            // on the circle the agent is already standing on is a destination it considers reached -
            // the v11 stall - however the prediction reads.
            bool predictedInReach = BlacksiteEngagementGeometry.InReach(_batonContactProfile, _batonPredictedRange, _batonVertical);
            bool standingInReach = BlacksiteEngagementGeometry.InReach(_batonContactProfile, _batonRange, _batonVertical);
            if (predictedInReach || standingInReach)
            {
                // In reach, off the cone: the controller's per-tick FaceCombatPoint finishes the
                // turn. Re-pathing here re-issued a goal the agent already considered reached.
                if (_batonAngle >= BlacksiteCombatPolicy.BatonFacingDegrees)
                { HoldForCombatAction(.15f); _batonBlocked = "turning onto the contact"; return BlacksiteFireReadiness.Turning; }
                if (predictedInReach && _baton.Value.Phase == BlacksiteBatonPhase.Ready
                    && BlacksiteEngagementGeometry.CanAttack(_batonContactProfile, _batonPredictedRange, _batonVertical, _batonAngle)
                    && BatonCommitInsideTheBand(swing))
                {
                    SetBatonPhase(BlacksiteBatonPhase.Swing);
                    return TickCommittedSwing(target, _baton.Value.Swing, 0f, out _);
                }
                if (standingInReach)
                {
                    // Standing in the gate with a strike that is not worth taking yet: the actor is
                    // where it should be and the geometry is coming to it, so it holds and faces
                    // rather than re-issuing its own stand-off. The band is bounded by the contact's
                    // own motion - it leaves through the gate walls, never through the actor's.
                    HoldForCombatAction(.15f);
                    if (!predictedInReach)
                        _batonBlocked = "holding the gate: predicted=" + _batonPredictedRange.ToString("0.00");
                    return _baton.Value.Phase == BlacksiteBatonPhase.Swing
                        ? BlacksiteFireReadiness.Firing : BlacksiteFireReadiness.Acquiring;
                }
                // Predicted into the gate but not standing in it yet: keep closing, which is what
                // puts the actor there in time for the strike it has not committed to.
            }
            return CloseOnBatonTarget(target, flat);
        }

        /// <summary>
        /// #874 S12 U4: the COMMIT is what a prediction the weapon cannot reach calls off, not the
        /// swing. Run 2 committed eight swings whose INSTANTANEOUS range was 4.27 to 8.37 m and
        /// aborted every one of them for `prediction miss` at the hit window (53651, 55438, 55472,
        /// 55658, 55687, 65660). Each was a legitimate S10.1 commit - the sapsucker was closing fast
        /// enough that the predicted range at impact landed in the gate - and each cost a whole
        /// 0.80 to 0.97 s windup to cancel.
        ///
        /// So the commit now needs the contact to be inside the band the weapon actually connects
        /// over as well as predicted inside the gate at impact: the prediction refines a strike the
        /// weapon can nearly take rather than authorising one from three reaches away. The test is
        /// the abort's own - BatonConnectInner/Outer plus SwingAbortMargin, the same tolerance every
        /// melee wall is measured with - so no envelope constant moves and nothing widens; only the
        /// far side of the commit narrows.
        ///
        /// Two things this deliberately does NOT do. It does not touch the S10.1 abort, which still
        /// owns every swing already in flight, because the prediction is only reliable near impact
        /// and calling a swing off early on a noisy estimate loses connects the S10.1 sweep proves
        /// (measured: cancelling during the windup drops swing 2 of the walking-contact scan to
        /// rangeAtImpact 2.90 and no contact). And it does not re-time anything: a member standing
        /// in its own gate is inside the band by construction (2.17 < 2.63 + 0.10), so the ordinary
        /// close-in commits exactly as it does today. What it costs is the far predictive commit on
        /// a contact closing faster than about 4.5 m/s net, which now waits until that contact is
        /// within a weapon's length - the v14 live-only note about committing from six metres is
        /// what run 2 spent eight swings on.
        /// </summary>
        // #1063 WP2: the band is the contact's own profile's, for the same reason the gate is. The
        // two are coupled by construction - MaxReach(profile) is SharedConnectOuter(profile) minus
        // the margin, exactly, for all six rows - so moving one without the other would open a gate
        // no commit could ever pass through.
        private bool BatonCommitInsideTheBand(int swing)
            => _batonRange >= BlacksiteEngagementGeometry.ConnectInner(_batonContactProfile, swing) - BlacksiteCombatPolicy.SwingAbortMargin
               && _batonRange <= BlacksiteEngagementGeometry.ConnectOuter(_batonContactProfile, swing) + BlacksiteCombatPolicy.SwingAbortMargin;

        // #988 B2: take the close-quarters swing the band chose. SetBatonPhase draws the swing off
        // the member's own 0-1-2 cycle, so the cycle is advanced onto the chosen one rather than
        // the phase being given a swing of its own: the cadence, the clip set and the recovery
        // frame are all still the cycle's, and swing 0 is simply stepped over because its measured
        // band starts at 0.98 m instead of 0.73 m.
        private void CommitCloseQuartersSwing(int swing)
        {
            for (int guard = 0; guard < 3 && _nextSwing % 3 != swing; guard++) _nextSwing++;
            SetBatonPhase(BlacksiteBatonPhase.Swing);
            _batonCloseQuarters = true;
            _batonBlocked = "close-quarters swing " + swing + ": range=" + _batonRange.ToString("0.00")
                + ", predicted=" + _batonPredictedRange.ToString("0.00");
        }

        /// <summary>
        /// #988 B3: the approach a squad ORDER produces, for a member that has been told about a
        /// monster it cannot yet see. It is deliberately not <see cref="ResolveBatonEngagement"/>:
        /// that path requires a line of sight and releases the close-in the frame it loses one,
        /// which is exactly why members 2 and 3 of the 2026-09-12 run answered a t=393.6 alert with
        /// `respondedAt=none` and engaged only when the crawler walked up to them 15 s later.
        ///
        /// What it reuses is everything that matters: the same lane sweep, the same
        /// <see cref="BlacksiteCombatPolicy.BatonLaneSpacingDegrees"/> 60-degree spacing, the same
        /// live claims, the same navigation and occupancy proofs and the same combat approach
        /// profile, so three members ordered onto one contact arrive on three different lanes and
        /// never on one point. It draws on the way in (B2) and it issues no damage decision of any
        /// kind - the moment the contact is visible the ordinary engagement takes the member back.
        ///
        /// #874 WP F: it is handed the CONTACT, not its position, so it can refuse one the squad
        /// cannot hurt: a remembered Old Bird, or an ordered contact the give-up memo has since
        /// written off, is not walked to with a drawn baton. The point is still its live root.
        /// </summary>
        internal BlacksiteFireReadiness ConvergeOnOrderedContact(EnemyAI enemy)
        {
            if (!UsesBaton || !BlacksiteThreatAdapterRegistry.IsAttackableBy(enemy, this)) return BlacksiteFireReadiness.NoTarget;
            if (HandsUnavailable) { _batonBlocked = "hands owned by " + CurrentHandOwner; return BlacksiteFireReadiness.HandsBusy; }
            if (_batonModel == null) { _batonBlocked = "baton visual missing"; return BlacksiteFireReadiness.PoseMissing; }
            ReadyBaton();
            Vector3 contact = enemy.transform.position;
            Vector3 flat = Vector3.ProjectOnPlane(contact - transform.position, Vector3.up);
            // #1010 A2: the same two gates as the engagement path. This one is given a remembered
            // point rather than a live contact, so there is no collider to measure and no carrier
            // to read: the lane ring is built on this member's own floor.
            _batonLaneFloor = transform.position.y;
            _batonRange = flat.magnitude;
            // #1063 WP2: signed, exactly as the engagement path measures it.
            _batonVertical = contact.y - transform.position.y;
            _batonAngle = flat.sqrMagnitude > .0001f ? Vector3.Angle(transform.forward, flat) : 0f;
            _batonPredictedRange = -1f; _batonRadialSpeed = 0f; _batonLead = 0f;
            // #1063 WP2: a remembered point has no body to measure, so the ring is the Humanoid
            // row - the row this whole path has always used. The moment the contact is visible the
            // ordinary engagement takes the member back and measures it properly.
            _batonContactBody = null; _batonContactProfile = BlacksiteBodyProfile.Humanoid;
            TrackBatonContact(null);
            return CloseOnBatonTarget(contact, flat, "converging on the squad's monster alert");
        }

        // #874 S10.1, #1293 R3: the engaged contact, when measured, and its locomotion-bounded row velocity.
        private void TrackBatonContact(object contact, ulong key = 0, bool riding = false)
        {
            _batonVelocityContact = contact;
            _batonTrackedProfile = contact == null ? BlacksiteBodyProfile.Humanoid : _batonContactProfile;
            _batonContactVelocity = Vector3.zero;
            if (contact == null) return;
            _batonContactTrackedAt = Time.time;
            if (riding) return;
            Vector3 velocity = ContactRowVelocity(key);
            if (BlacksiteContactVelocity.IsLocomotion(velocity.x, velocity.y, velocity.z))
                _batonContactVelocity = velocity;
        }

        // #1293 R3: the contact's row velocity - the seam a world-less fixture supplies motion through.
        private Vector3 ContactRowVelocity(ulong key)
            => TryGetWorld(out BlacksiteWorldModel world) && world.TryGet(key, out BlacksiteWorldRow row)
                ? row.Velocity
                : Vector3.zero;
        private float _batonContactTrackedAt = float.NegativeInfinity;
        // #1219 F1.2: a melee engagement is live while its contact was measured inside this window.
        private const float MeleeContactFreshSeconds = 1f;

        /// <summary>#1293 R3: the relative contact move that re-engages the baton (the combat re-issue distance).</summary>
        private const float BatonReissueMetres = .2f;

        /// <summary>
        /// #1293 R3: the baton's per-frame half while Combat is committed; it never acquires. It re-runs
        /// the engagement every frame of a committed swing, on a baton phase change, and when the
        /// contact moved past <see cref="BatonReissueMetres"/> relative to this member.
        /// </summary>
        internal void ContinueBatonCombat()
        {
            if (_combatController == null) return;
            Component contact = _combatController.CurrentContact;
            if (contact == null) return;
            Vector3 offset = contact.transform.position - transform.position;
            if (_baton.Value.Phase != BlacksiteBatonPhase.Swing
                && _baton.Value.Sequence == _batonEngagedSequence
                && (offset - _batonEngagedOffset).sqrMagnitude <= BatonReissueMetres * BatonReissueMetres)
                return;
            _combatController.ContinueBatonEngagement();
        }

        /// <summary>
        /// #1219 F1.2: this baton-equipped member is in melee on <paramref name="enemy"/> - its
        /// contact is that enemy and was measured within the last second, or he is standing inside
        /// the baton's own reach envelope of it (BatonAttackRange flat, BatonAttackVerticalRange up
        /// or down). The Coil-Head observer set leaves him out, but a nearby rifleman remains a
        /// watcher: only a baton-equipped man trading blows with a coil is excluded for melee.
        /// </summary>
        internal bool IsInMeleeOn(EnemyAI enemy)
        {
            if (!UsesBaton || enemy == null) return false;
            if (ReferenceEquals(_batonVelocityContact, enemy)
                && Time.time - _batonContactTrackedAt <= MeleeContactFreshSeconds) return true;
            Vector3 delta = enemy.transform.position - transform.position;
            float vertical = delta.y; delta.y = 0f;
            return delta.sqrMagnitude <= BlacksiteCombatPolicy.BatonAttackRange * BlacksiteCombatPolicy.BatonAttackRange
                && Mathf.Abs(vertical) <= BlacksiteCombatPolicy.BatonAttackVerticalRange;
        }

        /// <summary>
        /// #874 Plan F / #1233 S6 (rule 1): this rescuer's baton found no route to
        /// <paramref name="captor"/> on its last decision, so the rescue is handed on. The verdict
        /// is the melee executor's own <see cref="BlacksiteFireReadiness.ApproachBlocked"/> on THIS
        /// captor inside the freshness window <see cref="IsInMeleeOn"/> reads, re-asked on every
        /// election; the incumbent rule keeps whoever holds the rescue while it stays eligible.
        /// </summary>
        internal bool RescueApproachBlocked(EnemyAI captor)
            => LastBatonReadiness == BlacksiteFireReadiness.ApproachBlocked
               && ReferenceEquals(_batonVelocityContact, captor)
               && Time.time - _batonContactTrackedAt <= MeleeContactFreshSeconds;

        /// <summary>
        /// #1010 A2: where on the contact the melee geometry is measured, taken from the contact's
        /// own collider bounds rather than from its transform root.
        ///
        /// The height is the closest point on those bounds to this soldier, clamped into their own
        /// Y span - extended downward to the contact's root since #1063 WP2, see the body: a
        /// contact whose body spans the soldier's own height reads a zero gap however far up its
        /// pivot happens to be authored, and a contact riding a squadmate's head reads the gap to
        /// its UNDERSIDE rather than to its centre. That is the whole of the clung-flea failure:
        /// <c>BlacksiteVanillaMonsters.PoseCapture</c> parks the flea's transform on
        /// <c>GetThreatLookTransform()</c>, so the root reported 1.91 m of vertical gap against a
        /// 1.70 m gate for a body whose lowest point was inside it.
        ///
        /// The HORIZONTAL is deliberately the bounds CENTRE and not their near face. Every number
        /// the close-in is built from - BatonConnectInner/Outer, BatonStandOff, BatonAttackMinRange
        /// and BatonAttackRange - is a root-to-root measurement taken by BlacksiteMeleeReachChecks
        /// against a .4 x 2.5 m capsule, so measuring the horizontal to the near face would move
        /// every one of them inward by the contact's own half-width without re-measuring any of
        /// them: the shipped [1.62, 2.17] gate would then describe root distances of [2.02, 2.57]
        /// against a measured per-swing outer wall of 2.27, and the stand-off the lane ring issues
        /// would sit inside the minimum reach the gate refuses - the v11 stall shape, verbatim.
        /// The centre is the root for every contact whose collider is authored on its pivot and
        /// differs only for one whose collider is offset from it, which is the case this is for.
        /// </summary>
        private Vector3 MeasureBatonContact(Component owner, Vector3 root)
        {
            _batonContactBody = null;
            var body = BatonContactBody(owner);
            if (body == null) return root;
            var bounds = body.bounds;
            if (bounds.size.sqrMagnitude <= 1e-6f) return root;
            _batonContactBody = body;
            // #1063 WP2: the span the soldier's own height is clamped into always CONTAINS the
            // contact's own root, and the root is the only height in the whole measurement that
            // every contact is guaranteed to actually be standing at.
            //
            // The clamp used to be into the collider's bounds alone, which is right for a body
            // authored on its pivot and wrong - by the whole offset - for one that is not. The
            // 2026-09-15 session measured a MouthDog whose largest non-trigger collider sits about
            // two metres above its root: the dog's contact point was reported at y = -1.96 and the
            // soldiers' feet at y = -1.91, five centimetres apart, while this clamp returned the
            // collider's underside and the gate read a 1.94 to 2.97 m vertical gap against a 1.70 m
            // tolerance. Zero swings in three deployments, 17 of 95 decisions spent walking a
            // Repositioning ring inside the dog's grab.
            //
            // Only the FLOOR of the span moves, never its ceiling: extending it downward is what
            // lets a member standing on the same ground as the contact read the zero gap he is
            // actually at, while an elevated contact keeps the whole of its 1.70 m refusal - which
            // is the one case the tolerance was authored for and the one the owner ruling keeps.
            float low = Mathf.Min(bounds.min.y, root.y);
            float high = Mathf.Max(bounds.max.y, low);
            return new Vector3(bounds.center.x, Mathf.Clamp(transform.position.y, low, high), bounds.center.z);
        }

        /// <summary>
        /// #1219 F1.1: the contact's body is the collider vanilla's shovel would hit on it -
        /// <see cref="BlacksiteHitResolver.ResolveBody"/> - and nothing else. The size-and-solidity
        /// ranking that stood here (#1010 A2, #1063 WP2, #874 WP F) chose whichever solid collider
        /// was largest around the root, and the 2026-09-26 log shows what that admits: a player's
        /// <c>LineOfSightCube</c> (10.8 x 5.5 x 11.7 m) read as his body, so a member stood at a
        /// measured 1.09 m from a man 5.64 m away, and a HoarderBug's <c>ScanNode</c> read as its
        /// shell. Both volumes sit on layers the shovel mask excludes. The captor special case that
        /// ranking needed is gone with it: the damageable collider it preferred is now the only
        /// kind there is.
        ///
        /// Resolved once per contact while it HAS a body. A changed or destroyed contact
        /// invalidates the entry; a cached collider that is destroyed or switched off is re-resolved.
        /// #1233 S6 (rule 1): a null result is not cached. "This contact has no body" is a live
        /// geometric fact that changes under the member's feet - a camouflaged Dreadweaver switches
        /// its colliders back on when it drops the camouflage - and the engagement gate asks it on
        /// every think, so a cached null would hold a member off a creature that is now hitting it
        /// with a body its baton could land on. The walk is one scratch-list pass over the
        /// contact's colliders, paid only while the contact stays bodiless.
        /// </summary>
        private Collider BatonContactBody(Component owner)
        {
            if (owner == null)
            {
                _batonBoundsContact = null;
                _batonBoundsCollider = null;
                return null;
            }
            // Unity's null: a real null, a destroyed collider and a destroyed contact all re-resolve.
            if (!ReferenceEquals(owner, _batonBoundsContact)
                || _batonBoundsCollider == null || !_batonBoundsCollider.enabled || !_batonBoundsCollider.gameObject.activeInHierarchy)
            {
                _batonBoundsContact = owner;
                _batonBoundsCollider = BlacksiteHitResolver.ResolveBody(owner);
            }
            return _batonBoundsCollider;
        }

        /// <summary>
        /// #1019 S5: the ONE way the threat table measures a contact's body. It is the cache above,
        /// not a second scan: <see cref="BatonContactBody"/> resolves the contact's hittable collider
        /// once per contact, and the identity it is keyed on is the one
        /// <see cref="ResolveBatonEngagement"/> passes - the <c>EnemyAI</c> itself for an enemy
        /// contact - so the table and the melee path share one entry (design note G10.1).
        ///
        /// Null for a contact with no hittable collider at all, which the melee path refuses as not
        /// hittable; the adapter keeps its profile's declared defaults there.
        /// </summary>
        private Collider ThreatContactCollider(EnemyAI enemy)
            => enemy == null ? null : BatonContactBody(enemy);

        /// <summary>
        /// #1063 WP2: which of the six geometry rows this melee contact is engaged on.
        ///
        /// Until now the executor read the HUMANOID row for every contact of every class - the
        /// scope note the table shipped with - so the ring the close-in walked to, the gate it
        /// tested a swing against and the band it committed inside were a 0.40 m body's, whatever
        /// it was actually standing in front of. This is the table's own answer, taken through the
        /// SAME per-contact description cache <see cref="ResolveThreatIntent"/> uses and keyed on
        /// the same collider, so it costs one capture probe and one state recompute per think and
        /// no hierarchy walk, no bounds re-read and no ray.
        ///
        /// A PLAYER contact keeps the Humanoid row: there is no adapter for one, and the row is
        /// literally the measured player capsule the whole envelope was swept against.
        ///
        /// A HAZARD-profile contact also keeps it, deliberately. The Hazard row has no gate, no
        /// ring and no band at all, which is right for the hazard POLL that walks the squad around
        /// a slime and would be a soft brick here - the melee path would hold a contact it can
        /// neither reach nor release. A hazard is never acquired as a melee contact today (row 6 of
        /// the table refuses every weapons-excluded class before Engage), so this arm is a floor
        /// rather than a behaviour, and it keeps exactly today's numbers if that ever changes.
        /// </summary>
        private BlacksiteBodyProfile ResolveBatonContactProfile(PlayerControllerB player, EnemyAI enemy)
        {
            if (player != null || enemy == null) return BlacksiteBodyProfile.Humanoid;
            if (!BlacksiteThreatAdapterRegistry.Cached(ref _threatDescriptionCache, enemy, this,
                    ThreatContactCollider(enemy), transform.position.y, out var threatClass, out _))
                return BlacksiteBodyProfile.Humanoid;
            return threatClass.Profile == BlacksiteBodyProfile.Hazard
                ? BlacksiteBodyProfile.Humanoid
                : threatClass.Profile;
        }

        /// <summary>
        /// #1010 A2: the plane the lane ring is sampled on. It used to be the contact's own Y, and
        /// <see cref="NavMesh.SamplePosition"/> has nothing to find at a height nobody is standing
        /// at: member 1 of the 2026-09-12 run refused all six lanes of a flea riding member 3's
        /// head and reported `ApproachBlocked rng=3.20 vgap=1.91` for 49 s, because the ring was
        /// built 1.9 m in the air.
        ///
        /// Three cases, in the order they are decided. A contact RIDING a soldier stands on that
        /// carrier's floor - its transform is either parented under the carrier or, as
        /// <c>BlacksiteVanillaMonsters.PoseCapture</c> does for a clung flea, parked on the
        /// carrier's own head socket every frame - so the ring goes on the carrier's feet, which is
        /// the ground a member walking up to strike it will be standing on. A contact whose measured
        /// point is this soldier's own feet height is standing on this soldier's own floor - the
        /// clamp in <see cref="MeasureBatonContact"/> returns his own height whenever his feet are
        /// inside the contact's own Y span - and so is one whose ground is within an agent radius of
        /// them; the ring is his own plane, which is what it was always meant to be. Anything else
        /// is a body a storey above or below him: a ledge, a catwalk, a pit, an unbaked landing. The
        /// ring goes on the ground under it, sampled or not - #874 S9.4 is that a contact further
        /// from the mesh than the agent's own radius is refused with the blocked-approach stance,
        /// not approached across a plane it is not standing on.
        /// </summary>
        private float ResolveBatonLaneFloor(PlayerControllerB player, EnemyAI enemy, Vector3 contact)
        {
            float own = transform.position.y;
            Transform body = player != null ? player.transform : enemy != null ? enemy.transform : null;
            // #1011 A3: the capture registry is asked first. A3's own acceptance owes a carrier for
            // every CAPTURED contact, and the geometric fallback below cannot promise one: it finds
            // a posed contact by standing on the victim's head socket within one body radius
            // (.38 m), while the point it is handed is the contact's bounds CENTRE clamped into its
            // own Y span, so a collider authored off its pivot can sit outside that radius and the
            // lane ring would go back into the air. A live capture names its victim exactly and
            // costs one dictionary probe. The A2 lookup is unchanged and still answers for every
            // contact riding a soldier that no capture covers - a parented prop, a native cling.
            var carrier = BlacksiteVanillaMonsters.CaptureVictim(enemy);
            if (ReferenceEquals(carrier, this)) carrier = null;
            if (carrier == null) carrier = FindBatonContactCarrier(body, contact);
            if (carrier != null) return carrier.transform.position.y;
            // #1010 A2 (RunNative 2026-09-13): a measured point ON this soldier's own plane means his
            // feet are inside the contact's own Y span - MeasureBatonContact clamps into it - which
            // is the definition of standing on the same floor, and it is the overwhelmingly common
            // case, answered without touching physics. A contact with no non-trigger collider at all
            // is measured at its transform root instead (MeasureBatonContact's two early returns),
            // whose Y is almost never exactly his, so that one does reach the ray below: one short
            // downward cast on a path that already lays up to seventeen sweep capsules a frame, and
            // it is correct for it, because the root of a collider-less contact is the only height
            // anything knows about it.
            if (Mathf.Approximately(contact.y, own)) return own;
            // Otherwise the contact's whole body is a storey above or below him, and the ground under
            // it is a question only the geometry can answer. The BatonOverheadContactHeight early-out
            // that stood here compared the CONTACT's height to this soldier's FEET, which is not the
            // question the ring asks - a contact standing on a 2.5 m ledge stopped being "overhead"
            // the moment the soldier was 1.57 m up the ramp to it, so the ring snapped back onto the
            // soldier's own plane, 0.93 m under the surface the lanes have to be sampled on, all six
            // lanes reported no supporting ground and the close-in held ApproachBlocked on the ramp
            // for the whole engagement (BlacksiteMeleeApproachChecks "a contact 2.5 m up a ledge is
            // reached by its ramp").
            //
            // The cast length covers a contact BELOW this soldier as well as one above. That branch
            // is not new behaviour: before #1010 the ring was built at the contact's own Y, and the
            // ground under a contact standing in a pit is that same Y, so the plane this returns for
            // it is the plane the lanes were always sampled at. What f924401c did - return `own` for
            // everything within a metre, above or below - is the part that was new, and it is what
            // is being undone here.
            int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
            BlacksiteHitchTracer.CountPhysicsQuery();
            if (!Physics.Raycast(contact + Vector3.up * .1f, Vector3.down, out var floor,
                    Mathf.Max(4f, contact.y - own + 4f), mask, QueryTriggerInteraction.Ignore))
                return own;
            // Ground within an agent radius of this soldier's own feet IS his floor: that is the
            // tolerance the lane gate itself samples at (:873), so nothing inside it could change a
            // lane's verdict, and answering `own` keeps a step, a kerb or a shallow ramp lip on the
            // one plane the member is standing on.
            if (Mathf.Abs(floor.point.y - own) <= BlacksiteArrivalPolicy.AgentRadius) return own;
            // And past that tolerance the contact's own ground is the answer whether or not anything
            // can be sampled on it. Clamping it to `own` when it carries no navmesh was considered
            // and is wrong: it is exactly the #874 S9.4 stair. A contact on an unbaked 0.75 m
            // landing, or on a crate, a table or a railing, is further from the mesh than the agent's
            // own radius, and the rule that run bought with `stopOwner=combat, stop=0.15,
            // remaining=Infinity` is that the close-in refuses such a contact - takes the
            // blocked-approach stance and releases the melee stop - rather than closing on ground it
            // cannot path to. Putting the ring on this soldier's plane instead would find a lane
            // there and walk to it, which is the behaviour S9.4 exists to prevent
            // (BlacksiteCombatVetoChecks S94_COMBAT_POINT_IN_BLOCKED_VOLUME).
            return floor.point.y;
        }

        // #1010 A2: the soldier this contact is riding, if any. Parentage answers it directly for a
        // contact that was attached to the rig; a contact that is merely POSED onto a head socket
        // every frame - the clung flea - is found by standing on that socket, within one body
        // radius of it. Both are "anchored to a BlacksiteSoldierAI" and both want the same floor.
        private BlacksiteSoldierAI FindBatonContactCarrier(Transform body, Vector3 contact)
        {
            for (Transform node = body; node != null; node = node.parent)
            {
                var attached = node.GetComponent<BlacksiteSoldierAI>();
                if (attached != null && !ReferenceEquals(attached, this)) return attached;
            }
            if (!TryGetSquadDirector(out var director)) return null;
            director.CollectLivingMembers(BatonCarrierScratch);
            for (int i = 0; i < BatonCarrierScratch.Count; i++)
            {
                var peer = BatonCarrierScratch[i];
                if (peer == null || ReferenceEquals(peer, this)) continue;
                var head = peer.GetThreatLookTransform();
                if (head != null && Vector3.Distance(head.position, contact) <= BlacksiteBodyCollision.Radius)
                    return peer;
            }
            return null;
        }

        // #874 S10.1: a swing in flight. The actor keeps its approach and walks a lane measured from
        // where the contact will be when the head arrives, instead of standing still for the whole
        // 0.80 to 0.97 s windup; and a swing that will now land outside the band the weapon actually
        // connects over is called off rather than played into empty air. TickBatonSweep is untouched
        // and still decides every hit, so a mispredicted swing whiffs exactly as it does today.
        private BlacksiteFireReadiness TickCommittedSwing(Vector3 target, int swing, float elapsed, out bool aborted)
        {
            aborted = BlacksiteCombatPolicy.ShouldAbortSwingFor(_batonContactProfile, swing, elapsed, _batonPredictedRange);
            if (aborted)
            {
                _batonBlocked = "swing aborted: prediction miss at " + _batonPredictedRange.ToString("0.00");
                BlacksiteDiagnostics.Log("Baton", "member=" + MemberIndex + ", swing aborted, swing=" + swing
                    + ", reason=prediction miss, range=" + _batonRange.ToString("0.00")
                    + ", profile=" + _batonContactProfile
                    + ", band=[" + BlacksiteEngagementGeometry.ConnectInner(_batonContactProfile, swing).ToString("0.00") + ", "
                    + BlacksiteEngagementGeometry.ConnectOuter(_batonContactProfile, swing).ToString("0.00") + "]"
                    + ", predictedRange=" + _batonPredictedRange.ToString("0.00")
                    + ", radialSpeed=" + _batonRadialSpeed.ToString("0.00")
                    + ", lead=" + _batonLead.ToString("0.00"));
                // Host authority, and the same replicated phase change the S2.3 recovery frame
                // makes: the presentation crossfades out of the attack state on this instant.
                SetBatonPhase(BlacksiteBatonPhase.Ready);
                return BlacksiteFireReadiness.Firing;
            }
            // #1293 R3: where the contact will be when the head arrives, at its row velocity.
            Vector3 predicted = _batonLead > 0f ? target + _batonContactVelocity * _batonLead : target;
            Vector3 predictedFlat = Vector3.ProjectOnPlane(predicted - transform.position, Vector3.up);
            // #988 B2: a swing taken at close quarters does not walk a predictive lane. The S10.1
            // lane exists so an actor mid-windup keeps closing on a contact it has not reached yet;
            // this actor has already been reached, and the lane measured from a lead-predicted point
            // that has crossed through it lands inside its own body (measured in
            // BlacksiteMeleeApproachChecks' walk-through case: 0.29 m from the actor, inside the
            // 0.38 m body radius, which is the v11 stall shape). It holds and faces instead - the
            // controller's per-tick FaceCombatPoint owns the turn and TickBatonSweep owns the hit.
            if (_batonCloseQuarters)
            {
                // Same shape as the S12 U4 HoldingLane stance: the reach contract is kept because
                // this member is still on this contact, but it holds NO lane, so the goal and the
                // claim go rather than being left published for an approach nobody is walking.
                _hasBatonApproachGoal = false;
                BatonLaneClaims.Remove(this);
                HoldForCombatAction(.15f);
                return BlacksiteFireReadiness.Firing;
            }
            // The close-in's own writer, under the same combat approach profile: no second
            // stoppingDistance writer, no new destination issuer, no widened anything.
            CloseOnBatonTarget(predicted, predictedFlat, "committed swing " + swing);
            return BlacksiteFireReadiness.Firing;
        }

        // Separate approach lanes, recomputed from visible evidence; no chasing a hidden transform.
        private BlacksiteFireReadiness CloseOnBatonTarget(Vector3 target, Vector3 flat, string cause = null)
        {
            // Measured from the target, so a contact that walks through the actor puts the goal
            // back between the two rather than behind the actor. Directly overhead has no bearing
            // to preserve, so the actor keeps its own and walks forward instead of spinning.
            Vector3 away = flat.sqrMagnitude > .0025f ? -flat.normalized : transform.forward;
            // #1010 A2: the ring is a place to STAND, so it is built on the floor plane the close-in
            // will be standing on and not at the contact's own height. Everything else about the
            // sweep below - the six lanes, the 60-degree spacing, the order they are tried in, every
            // gate and every diagnostics string - is unchanged; only the plane the candidates are
            // laid on, and therefore the plane NavMesh.SamplePosition is asked about, moves.
            Vector3 ring = new Vector3(target.x, _batonLaneFloor, target.z);
            // Horizontal for the same reason the gate is: the lane radius is a distance the agent
            // walks along the floor, and a contact overhead used to inflate it out of the gate.
            // #1063 WP2: the ring is the THREAT TABLE's ring for this contact's class, not the
            // Humanoid row for everything. BlacksiteEngagementGeometry.LaneRadius also floors it at
            // MinimumLaneRadius(profile), so the close-in can never issue a ring whose own inward
            // settle lands inside that class's minimum reach - the capture ring the 2026-09-15
            // session walked 17 of 95 decisions into at stop=0.15.
            float radius = BlacksiteEngagementGeometry.LaneRadius(_batonContactProfile,
                Vector3.ProjectOnPlane(target - transform.position, Vector3.up).magnitude);
            // #874 S7.2: the member's own bearing, plus one whole lane step per blocked engagement,
            // so an occupied ring is retried from a different first lane instead of the same one.
            float preferred = (MemberIndex % 3 - 1 + _batonLaneRotation)
                * BlacksiteCombatPolicy.BatonLaneSpacingDegrees;
            int mask = PhysicalOccupancyMask;
            // #874 S9.4 combat clause: a lane refused because there is no ground under it is a
            // different fact from a lane refused because someone is standing on it this instant.
            int groundRefusals = 0, transientRefusals = 0;
            // #874 S12 U4: the bearing of the first lane refused for a reason that can clear on its
            // own - a squadmate standing on it, a body in the way, a path that is not complete yet.
            // That is the lane this member is queueing for, and the bearing it closes along.
            float queuedBearing = float.NaN;
            for (int lane = 0; lane < BatonLaneSteps.Length; lane++)
            {
                float bearing = preferred + BatonLaneSteps[lane] * BlacksiteCombatPolicy.BatonLaneSpacingDegrees;
                Vector3 candidate = ring + Quaternion.Euler(0, bearing, 0) * away * radius;
                if (!NavMesh.SamplePosition(candidate, out var hit, 1, NavMesh.AllAreas)) { _batonBlocked = "no navigation under lane " + bearing.ToString("0"); groundRefusals++; continue; }
                // #874 S9.4 combat clause. The sample above may snap up to a metre to find the mesh,
                // which is right for a lane point a few centimetres off an edge and wrong for a
                // contact standing where there is no mesh at all: on the 2026-09-09 run's stair it
                // snapped the goal onto the floor below and Combat then closed to 0.15 m of ground
                // the agent could not path to (stopOwner=combat, stop=0.15, remaining=Infinity).
                // A combat approach point has to be standing ground within the agent's own radius.
                if (!NavMesh.SamplePosition(candidate, out _, BlacksiteArrivalPolicy.AgentRadius, NavMesh.AllAreas))
                { _batonBlocked = "lane " + bearing.ToString("0") + " is off supporting ground"; groundRefusals++; continue; }
                if (!BlacksitePhysicalStep.CanOccupy(transform, hit.position, mask, out var blocker))
                { _batonBlocked = "lane " + bearing.ToString("0") + " blocked by " + (blocker != null ? blocker.name : "unnamed collider"); transientRefusals++; if (float.IsNaN(queuedBearing)) queuedBearing = bearing; continue; }
                if (IsBatonLaneTaken(hit.position)) { _batonBlocked = "lane " + bearing.ToString("0") + " held by a squadmate"; transientRefusals++; if (float.IsNaN(queuedBearing)) queuedBearing = bearing; continue; }
                if (!BlacksiteReachability.Walk(BlacksiteTraveller.Soldier(this), transform.position, hit.position, _batonPath).Reachable)
                { _batonBlocked = "no complete path to lane " + bearing.ToString("0"); transientRefusals++; if (float.IsNaN(queuedBearing)) queuedBearing = bearing; continue; }
                _batonBlocked = cause ?? "none";
                _batonLaneRotation = 0;
                _batonApproachGoal = hit.position; _hasBatonApproachGoal = true; BatonLaneClaims[this] = hit.position;
                // Combat owns the reach contract for as long as it owns this close-in. The stop
                // is what makes the settle land inside the gate; the reach is the gate itself.
                // #1063 WP2: the reach half of the contract is this contact's own outer wall. The
                // stop is unchanged and still BatonApproachStop - BlacksiteMotionOwner remains the
                // sole stoppingDistance writer and no second one is introduced here.
                Navigator.ApplyCombatApproachProfile(new BlacksiteApproachProfile(
                    BlacksiteCombatPolicy.BatonApproachStop,
                    BlacksiteEngagementGeometry.MaxReach(_batonContactProfile),
                    target, requiresFacing: true, operatorLane: false));
                Navigator.SetMeleeApproachDestination(hit.position);
                return BlacksiteFireReadiness.Repositioning;
            }
            // #874 S7.2, M3: a failed lane sweep is not automatically a lost contact. Two different
            // situations produced the same reason in the 2026-09-09 run and they need different
            // answers. Member 2's three ejections were a ring that was occupied for a moment - "no
            // complete path to lane 240", "lane 240 blocked by CaveIntersectTileMeshLOD1" - while
            // the contact itself was 3.8 to 6.8 m away on the same storey and reachable; the member
            // walked off on its order and came back 0.2 s later. A contact on a severed floor or
            // behind a sealed doorway is the other situation, and there no rotation of the ring
            // helps, so preserving threat knowledge while the planner picks another approach stays
            // exactly right. The discriminator is therefore the contact's own route, measured once
            // and only on the failure path.
            // #874 S9.4 combat clause: unless the whole ring was refused for want of ground, which
            // is not a crowd this member can wait out. That is the blocked-approach stance, and it
            // releases the melee reach contract rather than leaving a 0.15 m stop on the agent.
            if (groundRefusals > 0 && transientRefusals == 0)
            {
                _batonBlocked = "no supporting ground on the contact's circle: " + _batonBlocked;
                return BlacksiteFireReadiness.ApproachBlocked;
            }
            // #1010 A2: the discriminator is asked about the floor under the contact, for the same
            // reason the lanes are: a contact riding a head has no mesh at its own Y, and reporting
            // "no route to the contact at all" for that is what pinned member 1 for 49 s.
            bool contactReachable = NavMesh.SamplePosition(ring, out var contactHit, 1.5f, NavMesh.AllAreas)
                && BlacksiteReachability.Walk(BlacksiteTraveller.Soldier(this), transform.position, contactHit.position, _batonPath).Reachable;
            if (!contactReachable) return BlacksiteFireReadiness.ApproachBlocked;
            // #874 S12 U4: a member this far out does not hold a lane, it closes on one. Run 2 held
            // member 1 at range=10.66 with reach=1.62-2.17, moveGoal=(0,0,0) and `blocked=lane 180
            // held by a squadmate` (56399), 48 times across the run, because a crowded ring produced
            // the same stance at ten metres as at two. The stance is right beside the ring and wrong
            // away from it, so the answer is the distance: inside MeleeLaneHoldRange the member is
            // standing among the lanes and one step from the one it is queueing for, outside it the
            // member is nowhere near the fight and walks the bearing of that lane until it is.
            if (!float.IsNaN(queuedBearing)
                && Vector3.ProjectOnPlane(target - transform.position, Vector3.up).magnitude > MeleeLaneHoldRange
                && TryApproachOnLaneBearing(ring, target, away, queuedBearing, mask)) return BlacksiteFireReadiness.Repositioning;
            // The stance. Hold where the member stands rather than walking off - the controller's
            // per-tick FaceCombatPoint keeps it on the contact for as long as it holds combat
            // intent - and start the next sweep one lane further round. The hold is a combat dwell
            // ([UAT][Motion] stallOwner=combat) of the same .15 s the other baton stances hold; its
            // end is the decision that sweeps again. Nothing here touches the lane radius, the lane
            // count, the spacing or the reach gate.
            _batonLaneRotation = (_batonLaneRotation + 1) % BatonLaneSteps.Length;
            // #874 S10.1: the stance keeps the reach contract - the member is still closing on this
            // contact and will be under the melee stop the moment a lane frees - but it has no lane,
            // so it holds no lane: the goal and the claim go, the combat approach profile stays.
            // A claim left on a point this member could not reach would block a squadmate from it.
            _hasBatonApproachGoal = false;
            BatonLaneClaims.Remove(this);
            HoldForCombatAction(.15f);
            return BlacksiteFireReadiness.HoldingLane;
        }

        /// <summary>
        /// #874 S12 U4: the radius inside which holding for a lane is waiting and outside which it
        /// is standing still. Derived from the lane geometry itself, not from the reach: the lanes
        /// sit on the stand-off circle (<c>BatonStandOff</c> 1.72 m) at
        /// <c>BatonLaneSpacingDegrees</c> 60 degrees, so two adjacent lanes are
        /// <c>LaneSeparation(60) = 2 * 1.72 * sin(30) = 1.72</c> m apart. A member within one lane
        /// separation of the circle is already among the lanes - when the one it is queueing for
        /// frees, one lane-separation step puts it there - so waiting is the right answer. Beyond
        /// 1.72 + 1.72 = 3.44 m it is not queueing, it is parked, and the fix is to close.
        ///
        /// The brief's own figure was <c>BatonAttackRange * 2</c> = 4.34 m. That is a doubling of a
        /// measured reach wall with nothing behind it, and this repository does not ship unexplained
        /// factors, so it is replaced by the derived 3.44 m. The replacement is strictly tighter
        /// (it closes from 3.44 m rather than 4.34 m) and still clears the whole gate: it is above
        /// <c>BatonAttackRange</c> 2.17 and above the settle band's outer edge
        /// <c>BatonSettledRange(BatonStandOff, BatonApproachStop)</c> 2.07, so a member standing in
        /// its own gate, or settled on its own lane, is never called parked.
        /// </summary>
        internal static readonly float MeleeLaneHoldRange = BlacksiteCombatPolicy.BatonStandOff
            + BlacksiteCombatPolicy.LaneSeparation(BlacksiteCombatPolicy.BatonLaneSpacingDegrees);

        /// <summary>
        /// #874 S12 U4: the approach leg. The member walks to the hold-off point on the bearing of
        /// the lane it is queueing for and claims THAT point, so the claim moves with the member and
        /// a squadmate can see where this one is going rather than only where it is standing. Every
        /// gate the lane sweep applies is applied here too - supporting ground, the abandonment
        /// record, occupancy, another member's claim and a complete path - because an approach point
        /// nobody can stand on is the same defect one lane further out.
        /// </summary>
        /// <remarks>
        /// #1010 A2: <paramref name="ring"/> is the contact's own circle projected onto the floor
        /// plane the close-in will walk, and <paramref name="contact"/> is the body itself, which is
        /// what the approach profile still faces. The hold-off point is a place to stand, so it is
        /// measured on the ring for exactly the same reason the six lanes above are.
        /// </remarks>
        private bool TryApproachOnLaneBearing(Vector3 ring, Vector3 contact, Vector3 away, float bearing, int mask)
        {
            Vector3 candidate = ring + Quaternion.Euler(0, bearing, 0) * away * MeleeLaneHoldRange;
            if (!NavMesh.SamplePosition(candidate, out var hit, 1, NavMesh.AllAreas)
                || !NavMesh.SamplePosition(candidate, out _, BlacksiteArrivalPolicy.AgentRadius, NavMesh.AllAreas))
            { _batonBlocked = "no supporting ground on the approach to lane " + bearing.ToString("0"); return false; }
            if (!BlacksitePhysicalStep.CanOccupy(transform, hit.position, mask)
                || IsBatonLaneTaken(hit.position)
                || !BlacksiteReachability.Walk(BlacksiteTraveller.Soldier(this), transform.position, hit.position, _batonPath).Reachable)
            { _batonBlocked = "no approach to lane " + bearing.ToString("0"); return false; }
            // Closing, not shuffling: the hold-off point has to be nearer the contact than the
            // member is now, or this is the stance under a different name.
            if (Vector3.Distance(hit.position, ring) >= Vector3.Distance(transform.position, ring))
            { _batonBlocked = "approach to lane " + bearing.ToString("0") + " is no closer"; return false; }
            _batonBlocked = "closing on lane " + bearing.ToString("0") + " at " + MeleeLaneHoldRange.ToString("0.00");
            _batonApproachGoal = hit.position; _hasBatonApproachGoal = true; BatonLaneClaims[this] = hit.position;
            Navigator.ApplyCombatApproachProfile(new BlacksiteApproachProfile(
                BlacksiteCombatPolicy.BatonApproachStop, BlacksiteCombatPolicy.BatonAttackRange,
                contact, requiresFacing: true, operatorLane: false));
            Navigator.SetMeleeApproachDestination(hit.position);
            return true;
        }

        // The S1.4 occupancy rule applied to melee lanes: a point another living member already
        // stands on, or has already claimed for its own close-in, is not available to this one.
        private bool IsBatonLaneTaken(Vector3 point)
        {
            float radius = BlacksiteCombatPolicy.BatonBodySeparation;
            bool taken = false;
            BatonClaimScratch.Clear();
            foreach (var claim in BatonLaneClaims)
            {
                var peer = claim.Key;
                if (peer == null || peer.isEnemyDead) { BatonClaimScratch.Add(peer); continue; }
                if (ReferenceEquals(peer, this)) continue;
                if (Vector3.Distance(claim.Value, point) < radius
                    || Vector3.Distance(peer.transform.position, point) < radius) taken = true;
            }
            for (int i = 0; i < BatonClaimScratch.Count; i++) BatonLaneClaims.Remove(BatonClaimScratch[i]);
            if (taken || !TryGetSquadDirector(out var director)) return taken;
            director.CollectLivingMembers(BatonPeerScratch);
            for (int i = 0; i < BatonPeerScratch.Count; i++)
            {
                var peer = BatonPeerScratch[i];
                if (peer == null || ReferenceEquals(peer, this)) continue;
                if (Vector3.Distance(peer.transform.position, point) < radius) return true;
            }
            return false;
        }
        /// <summary>
        /// #1233 S8: the one-time equipment bind, run by ApplySpawnState the moment the replicated
        /// equipment is known. A rifle guard offers the hosted MX16A4 (TryAttachHostedWeapon keeps
        /// its own bridge throttle); a baton raider builds its sockets, visual, holster and effects
        /// and switches the animator to the baton controller. UpdateEquipmentPresentation still asks
        /// EnsureBatonEquipment every frame, which is the retry for a rig that bound late and one
        /// reference compare once bound.
        /// </summary>
        private void BindEquipment()
        {
            if (UsesBaton) EnsureBatonEquipment();
            else TryAttachHostedWeapon();
        }
        /// <summary>#1283 S3b D1: a living baton member on a ladder itself (Mounting to Dismounting) of any kind that is
        /// not a rope or a jump wears the rifle pair. Replicated inputs only - the pose flags (_climbingPose), the
        /// traversal kind, the ladder phase and the death state - so every peer swaps on the same frames.</summary>
        private bool BatonOnRifleLadder => !isEnemyDead && !ReplicatedDead && _climbingPose
            && !BlacksiteTraversalPresentation.IsRope(TraversalKind) && !BlacksiteTraversalPresentation.IsJump(TraversalKind)
            && BlacksiteTraversalPresentation.OnLadder(LadderPhase);
        /// <summary>The baton sockets, visual, holster, effects and controller pair (the rifle's on a ladder); false until
        /// the rig, the animator and the bundle can supply them.</summary>
        private bool EnsureBatonEquipment()
        {
            if (!_initialized || _rigBinding == null || _animator == null) return false;
            if (_batonAnimator == null)
            {
                _batonAnimator = BlacksiteEquipmentAssets.Load<RuntimeAnimatorController>("BlacksiteBatonRuntime");
                _equipmentAvatar = BlacksiteEquipmentAssets.Load<Avatar>("BlacksiteBatonAvatar");
                var visual = BlacksiteEquipmentAssets.Load<GameObject>("BlacksiteBaton_Visual");
                var holster = BlacksiteEquipmentAssets.Load<GameObject>("BlacksiteBaton_Holster");
                if (_batonAnimator == null || _equipmentAvatar == null || visual == null || holster == null) { if (!_batonMissingReported) { _batonMissingReported = true; BlacksiteDiagnostics.Log("Equipment", "Required baton assets missing; member=" + MemberIndex); } return false; }
                _rifleAnimator = _animator.runtimeAnimatorController; _rifleAvatar = _animator.avatar;
                var bones = _animator.GetComponentsInChildren<Transform>(true);
                var hand = bones.FirstOrDefault(t => t.name == "hand_R"); var hip = bones.FirstOrDefault(t => t.name == "spine_001");
                if (hand == null || hip == null) return false;
                _batonHand = Socket(hand, "BatonHandSocket", new Vector3(0, .08800016f, -.02899902f), new Quaternion(0, 0, -.70710683f, .70710683f));
                _batonHip = Socket(hip, "BatonHipSocket", new Vector3(.1810019f, -.0622911f, -.2323463f), new Quaternion(-.1511807f, -.11140054f, .23799461f, .9529391f));
                _batonModel = Instantiate(visual, _batonHip, false); _batonModel.transform.localPosition = Vector3.zero; _batonModel.transform.localRotation = Quaternion.identity; _batonModel.transform.localScale = Vector3.one;
                _batonEffects = _batonModel.AddComponent<BlacksiteBatonEffects>();
                _batonHolster = Instantiate(holster, _batonHip, false); _batonHolster.transform.localPosition = Vector3.zero; _batonHolster.transform.localRotation = Quaternion.identity; _batonHolster.transform.localScale = Vector3.one;
                foreach (var c in _batonModel.GetComponentsInChildren<Collider>(true)) c.enabled = false;
                foreach (var c in _batonHolster.GetComponentsInChildren<Collider>(true)) c.enabled = false;
                _weaponVisual?.Destroy();
                if (_rigBinding.WeaponRoot != null) foreach (var renderer in _rigBinding.WeaponRoot.GetComponentsInChildren<Renderer>(true)) renderer.enabled = false;
            }
            // #1283 S3b D1: the sidecar authors no ladder, so on the ladder the member wears the rifle pair and plays the
            // rifle's own ladder states. The Rebind restores the authored layer weights (Carry 1); the hands are on the rungs.
            bool ladder = BatonOnRifleLadder;
            var controller = ladder ? _rifleAnimator : _batonAnimator;
            if (_animator.runtimeAnimatorController != controller)
            {
                SwitchEquipmentController(ladder ? _rifleAvatar : _equipmentAvatar, controller); _batonAnimation = null;
                if (ladder && _aimPose != null) _aimPose.ReleaseLayers();
            }
            return true;
        }
        private void UpdateEquipmentPresentation()
        {
            if (!_initialized || _rigBinding == null || _animator == null) return;
            if (!UsesBaton)
            {
                ApplyGuardTraversalAnimation();
                UpdateCarryAnimation();
                // #874 S3.3: the hands are back only when ownership says so and the Carry override
                // has actually left its carry state. The window is the guarantee; the animator read
                // is the direct evidence, and it also covers a cargo exit no window ever armed.
                // While the shared traversal controller is bound there is no rifle pose.
                // #874 S3.2b belt: the layer can leave its carry state while its weight is still
                // fading, and with Write Defaults off a fading layer keeps writing the last carry
                // pose over the arms. Wait for that weight too, but only once the layer has
                // actually posed the arms at least once, and treat an actor with no aim helper yet
                // as settled because nothing has raised the layer for it either.
                bool carryPose = CarryLayerHoldsHands;
                _carryLayerRaised |= carryPose;
                // #1283 S3a: the rifle's own Jump (no controller swap) poses RifleIdle arms, so the weapon stays in them
                // unless the member is incapacitated or holds cargo; only the swapped rope pair has no rifle pose.
                bool ownJump = GuardTraversalActiveUnswapped && !ReplicatedStunned
                    && BlacksiteLifecycle.HoldsWeapon(EffectiveLifecycle) && !HasOccupiedCargoHands;
                bool visible = (CurrentHandOwner == BlacksiteHandOwner.Weapon && !_guardTraversalActive || ownJump) && !carryPose
                    && (!_carryLayerRaised || _aimPose == null || _aimPose.CarryLayerWeight <= .001f);
                if (_rigBinding.WeaponRoot != null) foreach (var renderer in _rigBinding.WeaponRoot.GetComponentsInChildren<Renderer>(true))
                { if (!_rifleRenderers.ContainsKey(renderer)) _rifleRenderers.Add(renderer, renderer.enabled); renderer.enabled = visible && _rifleRenderers[renderer]; }
                if (_weaponVisual?.Model != null) _weaponVisual.Model.SetActive(visible);
                return;
            }
            if (!EnsureBatonEquipment()) return;
            UpdateCarryAnimation();
            _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            if (IsServerAuthority() && HandsUnavailable) SetBatonPhase(BlacksiteBatonPhase.Stowed);
            var state = _baton.Value; float elapsed = (float)(GetServerTime() - state.Started);
            bool handPose = !HandsUnavailable && BlacksiteBatonPolicy.InHand(state.Phase, elapsed);
            Transform parent = handPose ? _batonHand : _batonHip;
            if (_batonModel.transform.parent != parent) { _batonModel.transform.SetParent(parent, false); _batonModel.transform.localPosition = Vector3.zero; _batonModel.transform.localRotation = Quaternion.identity; }
            _batonModel.SetActive(!isEnemyDead && !ReplicatedDead && !BlacksiteLifecycle.Hides(EffectiveLifecycle));
            _batonEffects.Power(!HandsUnavailable && BlacksiteBatonPolicy.Powered(state.Phase, elapsed), elapsed < 1.8f);
            bool changed = state.Sequence != _presentedBatonSequence;
            if (changed && state.Phase == BlacksiteBatonPhase.Swing && elapsed < .3f) _batonEffects.Cue("Swing");
            // #929/#1262 P6: draw/stow play the embedded Rust & Blood item_equip_ufx_4/_2 once per replicated sequence on every peer, no cue
            // RPC; at elapsed 0 the 0.857 s draw cue ends 0.043 s before PowerOn (0.9 s), a late one (elapsed < 0.3 s) overlaps it by that lateness, accepted.
            if (changed && state.Phase == BlacksiteBatonPhase.Drawing && elapsed < .3f) _batonEffects.Cue(ContractAssetLoader.BlacksiteBatonEquipClip);
            if (changed && state.Phase == BlacksiteBatonPhase.Stowing && elapsed < .3f) _batonEffects.Cue(ContractAssetLoader.BlacksiteBatonStowClip);
            _presentedBatonSequence = state.Sequence;
            // #1283 S3b D1: on the ladder the rifle pair is bound and the rifle ladder writer plays it; a controller without
            // the ladder states keeps plain StandingPatrol. Both play at rate 1 (LadderPlaybackSpeed). The selector below,
            // BatonHit with it, resumes once the baton pair is back.
            if (ReferenceEquals(_animator.runtimeAnimatorController, _rifleAnimator))
            {
                WriteLadderClimbRate(); // UpdateAnimation wrote it on the pair bound before this frame's swap.
                DriveRifleLadderState();
                _animator.speed = BlacksiteTraversalPresentation.LadderPlaybackSpeed;
                return;
            }
            // #874 S5.2 (#873): an explicit switch on the replicated traversal kind, not a two-way
            // ternary on _ropeDirection, which gave the rope clip to _ropeDirection == 0 - every
            // member that is not on the cable at all: the world ladder and the ship ladder both played it.
            // #1283 S3a A7: a hit mid-climb swapped to BatonHit while the body kept climbing; the traversal owns the body until it ends,
            // but only a real traversal clip: a Fallback (short hop, a ladder's walked leg, missing state) still yields to BatonHit.
            bool dead = isEnemyDead || ReplicatedDead;
            string traversal = !dead && (_climbingPose || _jumpingPose) ? ResolveBatonTraversalState() : null;
            string animation = dead ? "BatonDead" : traversal != null && traversal != BlacksiteTraversalPresentation.Fallback ? traversal
                : (_monsterCaptured || _spawnState.Value.IsStunned || (Time.time < _batonHitUntil && !BatonCommitted)) ? "BatonHit" : traversal != null ? traversal : HandsUnavailable ? "StandingPatrol"
                : state.Phase == BlacksiteBatonPhase.Swing ? "BatonAttack" + state.Swing : state.Phase == BlacksiteBatonPhase.Drawing ? "BatonDraw"
                : state.Phase == BlacksiteBatonPhase.Stowing ? "BatonStow" : state.Phase == BlacksiteBatonPhase.Ready ? "BatonReady" : "StandingPatrol";
            _animator.speed = ResolveBatonAnimatorSpeed(animation);
            if (_batonAnimation != animation || (changed && state.Phase == BlacksiteBatonPhase.Swing) || (animation == "BatonHit" && _batonHitSequence != _presentedBatonHitSequence))
            {
                _batonAnimation = animation; _presentedBatonHitSequence = _batonHitSequence;
                // #1219 F2: the offset is CLIP seconds. A swing's replicated clock runs at
                // BatonAnimatorSpeed against its clip, so a late joiner or a restarted state lands on
                // the clip frame the host's hit window is reading; draw and stow keep the authored 1:1.
                bool swing = state.Phase == BlacksiteBatonPhase.Swing;
                float clip = swing ? BlacksiteBatonPolicy.ClipSeconds(state.Swing) : state.Phase == BlacksiteBatonPhase.Drawing ? BlacksiteBatonPolicy.DrawSeconds : BlacksiteBatonPolicy.StowSeconds;
                float clipElapsed = swing ? elapsed * BlacksiteBatonPolicy.BatonAnimatorSpeed : elapsed;
                float start = animation == "BatonDraw" || animation == "BatonStow" || animation.StartsWith("BatonAttack", StringComparison.Ordinal) ? Mathf.Clamp01(clipElapsed / clip) : 0;
                if (animation == BlacksiteTraversalPresentation.BatonJump || animation == BlacksiteTraversalPresentation.BatonJumpRunning)
                    _animator.Play(animation, 0, BlacksiteLandingGearPolicy.JumpTakeoffNormalizedTime);
                else _animator.CrossFadeInFixedTime(animation, .1f, 0, start * clip);
            }
        }
        // #874 S5.2 (#873): the baton traversal switch. Exactly two kinds name the rope state; every
        // other climbing kind - a climbing pose whose kind has not replicated yet included, which the
        // old ternary silently sent to BatonRope - is a ladder, whose rungs the rifle pair presents.
        private string ResolveBatonTraversalState()
        {
            switch (TraversalKind)
            {
                case BlacksiteTraversalKind.RopeDescend:
                case BlacksiteTraversalKind.RopeAscend:
                    return BlacksiteTraversalPresentation.BatonRope;
                // #991: the two jump kinds are their own states, resolved by the same capability
                // probe; a stale sidecar or a crossing too short to show airtime (#1271) keeps locomotion.
                case BlacksiteTraversalKind.Jump:
                case BlacksiteTraversalKind.JumpRunning:
                    return BlacksiteTraversalPresentation.ShowsJumpClip(_crossingPlaybackSpeed.Value) ? ResolveJumpState(
                        BlacksiteTraversalPresentation.BatonJump, BlacksiteTraversalPresentation.BatonJumpRunning) : BlacksiteTraversalPresentation.Fallback;
                default:
                    // #1283 S3b D1: off the rungs (BatonOnRifleLadder) a ladder's walked leg keeps locomotion.
                    return BlacksiteTraversalPresentation.Fallback;
            }
        }

        private float ResolveBatonAnimatorSpeed(string animation)
        {
            // The host's crossing duration sets one replicated clip rate for both loadouts and peers.
            if (animation == BlacksiteTraversalPresentation.BatonJump
                || animation == BlacksiteTraversalPresentation.BatonJumpRunning
                || animation == BlacksiteTraversalPresentation.BatonRope)
                return _crossingPlaybackSpeed.Value;
            // #1219 F2: an attack plays at the one factor its hit window and recovery frame are
            // timed with (BlacksiteBatonPolicy.BatonAnimatorSpeed), never at the stride clock.
            if (animation.StartsWith("BatonAttack", StringComparison.Ordinal))
                return BlacksiteBatonPolicy.BatonAnimatorSpeed;
            return animation == "StandingPatrol" || animation == "BatonReady"
                ? BlacksiteLocomotionSpeedTable.BatonAnimatorSpeed(AnimationRoleMoveSpeed) : 1f;
        }

        private void SwitchEquipmentController(Avatar avatar, RuntimeAnimatorController controller)
        {
            if (_animator == null || controller == null || (_animator.avatar == avatar && _animator.runtimeAnimatorController == controller)) return;
            _aimPose?.ResetController();
            _animator.avatar = avatar; _animator.runtimeAnimatorController = controller; _animator.Rebind();
            InvalidateAnimatorParameterCache(); EnsureAimPoseHelper(); EnsureAnimationEventFootsteps();
        }

        // #1283 S3a: only the rope swaps controller and avatar, to the maintained Generic baton pair (BatonRope), hiding the
        // weapon and restoring the rifle pair when traversal ends. A rifle jump plays the rifle controller's own layer-0
        // Jump (BlacksiteRifleJump, the same takeoff) with no swap. #1271: no clip at all for a jump too short to show.
        private void ApplyGuardTraversalAnimation()
        {
            bool alive = !isEnemyDead && !ReplicatedDead;
            bool rope = alive && _ropeDirection.Value != 0;
            bool jump = alive && _jumpingPose && BlacksiteTraversalPresentation.IsJump(TraversalKind) && BlacksiteTraversalPresentation.ShowsJumpClip(_crossingPlaybackSpeed.Value);
            if (!rope && !jump)
            {
                RestoreGuardTraversalController();
                return;
            }
            if (_animator == null || !_animator.isInitialized) return;
            // #1283 S3a: a latch held for the other kind restores first, so a jump never looks Jump up on the baton
            // controller (a permanent missing bit) and the rope never plays on the rifle controller.
            if (_guardTraversalActive && rope == GuardTraversalActiveUnswapped) RestoreGuardTraversalController();
            // #874 S5: both rope directions play the one BatonRope state, so its absence marks both kinds.
            int missingBit = rope ? RopeKindBits : 1 << (int)TraversalKind;
            if ((_guardTraversalMissingKinds & missingBit) != 0) return;
            string state = rope ? BlacksiteTraversalPresentation.BatonRope : RifleJumpState;
            if (!_guardTraversalActive)
            {
                // Saved for both kinds, so the restore after a jump is a no-op and never a stale rope pair.
                _guardOriginalController = _animator.runtimeAnimatorController;
                _originalAvatar = _animator.avatar;
                if (rope)
                {
                    var controller = BlacksiteEquipmentAssets.Load<RuntimeAnimatorController>("BlacksiteBatonRuntime");
                    var avatar = BlacksiteEquipmentAssets.Load<Avatar>("BlacksiteBatonAvatar");
                    if (controller == null || avatar == null) return;
                    SwitchEquipmentController(avatar, controller);
                }
                // #1283 S3a: the rope's Rebind drops triggers; a jump keeps the rifle controller, so a Hit set on the takeoff frame, before this latch, would cut the Jump.
                else TryResetTrigger(HitHash);
                _guardTraversalActive = true;
            }
            if (!_animator.isInitialized) return;
            int stateHash = Animator.StringToHash(state);
            if (_guardTraversalStateHash != stateHash)
            {
                bool available;
                try { available = _animator.HasState(0, stateHash); }
                catch { return; } // A binding failure is not proof that the asset lacks the state.
                if (!available)
                {
                    _guardTraversalMissingKinds |= missingBit;
                    ReportGuardTraversalUnavailable(state);
                    RestoreGuardTraversalController();
                    return;
                }
                _guardTraversalStateHash = stateHash;
                _animator.Play(stateHash, 0, rope ? 0f : BlacksiteLandingGearPolicy.JumpTakeoffNormalizedTime);
            }
            _animator.speed = _crossingPlaybackSpeed.Value;
        }

        private const int RopeKindBits = (1 << (int)BlacksiteTraversalKind.RopeDescend) | (1 << (int)BlacksiteTraversalKind.RopeAscend);
        private const string RifleJumpState = "Jump"; // #1283 S3a: the rifle controller's own layer-0 jump, reachable by name only.
        private bool RopeStateMissing => (_guardTraversalMissingKinds & RopeKindBits) != 0; // #874 S5 (F07): the sidecar has no BatonRope.
        // #1283 S3a: the latch holds the rifle's own controller, i.e. a rifle jump rather than the swapped rope pair.
        private bool GuardTraversalActiveUnswapped => _guardTraversalActive && _animator != null
            && ReferenceEquals(_animator.runtimeAnimatorController, _guardOriginalController);

        // Death and interrupted traversal also call this owner directly; no later animation tick
        // is required to return the rifle's controller/avatar and hand ownership. #1283 S3a: only the
        // rope swapped them; after the rifle's own Jump the switch back is a no-op.
        internal void RestoreGuardTraversalController()
        {
            if (!_guardTraversalActive) return;
            bool swapped = !GuardTraversalActiveUnswapped;
            _guardTraversalActive = false;
            _guardTraversalStateHash = 0;
            SwitchEquipmentController(_originalAvatar, _guardOriginalController);
            if (_animator != null) _animator.Play(BlacksiteTraversalPresentation.Fallback, 0, 0);
            // The rebind drops the rifle solver's socket cache, so the first frames back run
            // with no grip at all. The weapon stays hidden until the support hand has arrived.
            // #1283 S3a: a jump never rebound, and a handback would hide the rifle it kept visible.
            if (swapped) BeginTraversalHandback();
        }

        private void ReportGuardTraversalUnavailable(string state)
        {
            string controller = _animator.runtimeAnimatorController != null
                ? _animator.runtimeAnimatorController.name : BlacksiteDiagnosticFormat.None;
            Y4NGZCompany.Bootstrap.ContractsBootstrap.Log?.LogWarning(
                $"[MoonContracts.Blacksite] Soldier controller '{controller}' has no '{state}' state (member {MemberIndex});"
                + " the rifle controller stays bound for this traversal kind. The maintained traversal clips are the equipment"
                + " sidecar's BatonRope and the contracts bundle's rifle Jump (#1283 S3a).");
        }
        private void AlignRopeHands()
        {
            if (_animator == null) return;
            _animator.transform.localPosition -= _ropeVisualOffset; _ropeVisualOffset = Vector3.zero;
            if (_ropeDirection.Value == 0 || isEnemyDead || !TryGetMission(out var mission)) return;
            var ship = mission.Objectives.Ship; if (ship == null || !ship.IsHovering) return;
            if (_ropeLeftHand == null || _ropeRightHand == null)
            {
                var bones = _animator.GetComponentsInChildren<Transform>(true);
                _ropeLeftHand = bones.FirstOrDefault(t => t.name == "hand_L"); _ropeRightHand = bones.FirstOrDefault(t => t.name == "hand_R");
            }
            if (_ropeLeftHand == null || _ropeRightHand == null) return;
            Vector3 rope = ship.transform.TransformPoint(BlacksiteRappelGeometry.CableTopLocal);
            Vector3 hands = (_ropeLeftHand.position + _ropeRightHand.position) * .5f;
            Vector3 offset = Vector3.ProjectOnPlane(rope - hands, Vector3.up);
            // Only presentation follows the authored hand cycle; the swept traversal body is steady.
            offset = Vector3.ClampMagnitude(offset, 1.2f);
            _ropeVisualOffset = _animator.transform.parent != null ? _animator.transform.parent.InverseTransformVector(offset) : offset;
            _animator.transform.localPosition += _ropeVisualOffset;
        }
        private static Transform Socket(Transform parent, string name, Vector3 position, Quaternion rotation)
        { var go = new GameObject(name); go.transform.SetParent(parent, false); go.transform.localPosition = position; go.transform.localRotation = rotation; go.transform.localScale = Vector3.one; return go.transform; }
        private void TickBatonSweep()
        {
            // #1009 A2 (F5): a swing that is cut short - hands taken, model gone, phase left - closes
            // its hit window here, so an interrupted swing still reports its one verdict.
            if (!IsServerAuthority() || !UsesBaton || _batonModel == null || HandsUnavailable || _baton.Value.Phase != BlacksiteBatonPhase.Swing) { _hasBatonHead = false; CloseBatonSweepWindow(); return; }
            var state = _baton.Value; float elapsed = (float)(GetServerTime() - state.Started);
            // The weapon is a segment, not a point: its grip is the model's own origin and its head
            // sits HeadOffset up the model's +Y axis. Both ends are rigid, so a point a fraction t
            // along the shaft is exactly Lerp(grip, head, t) at every instant - no approximation.
            Vector3 grip = _batonModel.transform.position;
            Vector3 head = _batonModel.transform.TransformPoint(new Vector3(0, BlacksiteBatonPolicy.HeadOffset, 0));
            if (_hasBatonHead && BlacksiteBatonPolicy.CrossesHitWindow(state.Swing, _batonLastElapsed, elapsed))
            {
                float span = Mathf.Max(.0001f, elapsed - _batonLastElapsed);
                float opens = Mathf.Clamp01((BlacksiteBatonPolicy.ActiveFrom(state.Swing) - _batonLastElapsed) / span);
                float closes = Mathf.Clamp01((BlacksiteBatonPolicy.ActiveUntil(state.Swing) - _batonLastElapsed) / span);
                var from = Vector3.Lerp(_previousBatonHead, head, opens);
                var to = Vector3.Lerp(_previousBatonHead, head, closes);
                var gripFrom = Vector3.Lerp(_previousBatonGrip, grip, opens);
                var gripTo = Vector3.Lerp(_previousBatonGrip, grip, closes);
                // #1219 F1.1: the grip's travel over this frame's band, which every confirmed hit
                // laid below is bounded against (SweepBatonSegment's reach and line invariants).
                _sweepGripFrom = gripFrom; _sweepGripTo = gripTo; _sweepFloorPlane = transform.position.y;
                // HeadOffset is model-local; inherited rig scale belongs in this world-space bound.
                // Every interpolated shaft is no longer than the longer clipped endpoint shaft.
                _sweepGripReach = Mathf.Sqrt(Mathf.Max((from - gripFrom).sqrMagnitude, (to - gripTo).sqrMagnitude))
                    + BlacksiteBatonPolicy.HeadRadius + .05f;
                // The head, swept between the two poses, unchanged. Its travel is the swing's, and
                // it stays the hit direction for every capsule: the shaft's own endpoints run along
                // the weapon, not along the strike.
                Vector3 travel = to - from;
                // #1010 A2: one committed swing, one verdict. The counters are zeroed as the hit
                // window opens and every capsule below adds to them, so a swing that met no
                // eligible contact at all says so once instead of playing into empty air in
                // silence - which is what member 0 of the 2026-09-12 run did for 23 s against a
                // grounded snare flea the arc passed straight over.
                // #1009 A2 (F5): "as the window opens" is this transition, and only this one. The
                // window spans as many frames as the swing's active band crosses, and every one of
                // them lays its capsules into the SAME counters.
                if (!_batonSweepWindowOpen)
                {
                    _batonSweepWindowOpen = true; _batonSweepWindowSwing = state.Swing;
                    _batonSweepCandidates = 0; _batonSweepColliders.Clear();
                }
                SweepBatonSegment(state.Swing, from, to, BlacksiteBatonPolicy.HeadRadius, travel);
                // #874 S2.1r: and the shaft under it, which is what physically meets a crouched
                // contact or a contact off the side of the arc that the head passes over. One
                // capsule lies along the whole weapon at each interpolated pose, so the shaft is
                // covered along its length by the capsule itself and along its travel by the poses.
                int steps = BlacksiteBatonPolicy.ShaftSweepSteps(Vector3.Distance(from, to));
                for (int step = 0; step <= steps; step++)
                {
                    float pose = (float)step / steps;
                    SweepBatonSegment(state.Swing, Vector3.Lerp(gripFrom, gripTo, pose), Vector3.Lerp(from, to, pose), BlacksiteBatonPolicy.ShaftRadius, travel);
                }
                // #1010 A2: the low strike. Everything above is laid along the animated weapon,
                // which is held in hand_R and therefore sweeps between waist and shoulder height -
                // S21B_SHAFT measures exactly that, and it is why a 0.3 m body passes the reach
                // gate at vgap=0, range=1.72, commits a swing, and is passed clean over in silence.
                // The strike frames therefore lay two more capsules per pose at the head's own
                // radius - it is the head's radius and not a new number because that is the end of
                // the weapon a downward strike lands with:
                //   * one straight down from the weapon head to the floor plane the soldier stands
                //     on, which is the strike itself; and
                //   * one lying ON that plane under the whole weapon, from the grip's shadow to the
                //     head's, which is the ground the strike sweeps.
                // The second is not decoration. The first alone is the head's own arc and nothing
                // else, and the head does not pass over the near half of the member's own settle
                // band: S21B_LOWSETTLE measured swings 0 and 1 reaching a grounded body only from
                // 1.75 and 1.85 m against a 1.72 m stand-off, and swing 2 losing it again at 1.95 m
                // against a 2.07 m worst settle, so no swing covered the band a member standing in
                // its own stance may be anywhere in. The weapon's shadow is what the whole arc
                // actually passes over, and it closes that gap from the inside.
                //
                // Only a contact whose whole collider is below BatonLowContactHeight is eligible
                // for either (SweepBatonSegment's lowStrike gate), so this cannot reach anything the
                // measured envelope already covers: a standing contact is struck by the capsules
                // above or not at all, exactly as today, and the STANDING reach envelope is
                // untouched.
                //
                // The grounded envelope is not. The shadow capsule ends at the grip's projection, so
                // it lays a HeadRadius disc on the floor around the point under the member's own
                // hand, and a 0.3 m body beside - or slightly behind - that hand is now inside it
                // where before nothing but the head's arc could reach. That is deliberate: the grip
                // travels with the swing and the weapon passes over that ground too. But it widens
                // where a grounded contact may be struck from, which is a judgement about how the
                // melee should feel and not something a fixture can settle, so it is measured rather
                // than assumed - BlacksiteMeleeReachChecks scans a grounded body at the grip's own
                // bearing and distance - and flagged for review (#1010).
                //
                // Its own step count, because the head capsule is six times the shaft's radius and
                // needs correspondingly fewer poses to cover the same travel.
                float floorPlane = transform.position.y;
                int lowSteps = Mathf.Clamp(
                    Mathf.CeilToInt(Vector3.Distance(from, to) / (2f * BlacksiteBatonPolicy.HeadRadius)),
                    1, BlacksiteBatonPolicy.MaxShaftSweepSteps);
                for (int step = 0; step <= lowSteps; step++)
                {
                    float pose = (float)step / lowSteps;
                    Vector3 lowHead = Vector3.Lerp(from, to, pose);
                    Vector3 headShadow = new Vector3(lowHead.x, floorPlane, lowHead.z);
                    if (lowHead.y - floorPlane > BlacksiteBatonPolicy.HeadRadius)
                        SweepBatonSegment(state.Swing, lowHead, headShadow,
                            BlacksiteBatonPolicy.HeadRadius, travel, lowStrike: true);
                    Vector3 lowGrip = Vector3.Lerp(gripFrom, gripTo, pose);
                    SweepBatonSegment(state.Swing, new Vector3(lowGrip.x, floorPlane, lowGrip.z), headShadow,
                        BlacksiteBatonPolicy.HeadRadius, travel, lowStrike: true);
                }
            }
            // #1009 A2 (F5): the window has run out - this frame no longer crosses the swing's
            // active band - so the verdict for the whole swing is taken here, once.
            else CloseBatonSweepWindow();
            _previousBatonHead = head; _previousBatonGrip = grip; _batonLastElapsed = elapsed; _hasBatonHead = true;
        }
        // #1010 A2: the missing diagnostic. A committed swing that overlapped no eligible candidate
        // at all is a whiff, and until now the only [Baton] lines a whiff produced were the #988 B1
        // ineligible-player skips - which is why the 2026-09-12 analysis could only infer the miss
        // from the absence of damage.
        //
        // #1009 A2 (F5): it is emitted when the hit window CLOSES, against what the whole window
        // accumulated. A swing is a whiff only if none of its frames overlapped an eligible
        // candidate; contacts= is the number of distinct colliders the window's capsules saw. The
        // one-per-second-per-member rate limit is unchanged.
        private void CloseBatonSweepWindow()
        {
            if (!_batonSweepWindowOpen) return;
            _batonSweepWindowOpen = false;
            if (_batonSweepCandidates == 0 && BlacksiteDiagnostics.Enabled)
                BlacksiteDiagnostics.LogRateLimited("baton.whiff." + MemberIndex, 1f, "Baton",
                    "member=" + MemberIndex + ", swing=" + _batonSweepWindowSwing
                    + ", verdict=whiff, contacts=" + _batonSweepColliders.Count);
        }
        // One capsule of the swept weapon. Every gate is the one the head-only sweep applied, and
        // the occlusion cast uses this capsule's own endpoints, so a railing between the hand and
        // whatever the shaft would have reached blocks that hit exactly as it blocks a head hit.
        // #988 round 2, NIT 6: one resolve per collider. The B1 ordering rule needs the candidates
        // twice, and running the loop body twice ran GetComponentInParent<EnemyAI>() and
        // GetComponentInParent<PlayerControllerB>() twice per collider on a per-frame path that
        // already lays up to 17 capsules per sweep. The components are resolved once into a
        // preallocated scratch array and only the ORDER is decided twice.
        private struct BatonCandidate { internal Collider Collider; internal EnemyAI Victim; internal PlayerControllerB Player; internal bool Preferred; internal float Distance; }
        private readonly BatonCandidate[] _batonCandidates = new BatonCandidate[48];

        /// <param name="lowStrike">
        /// #1010 A2: this capsule is one of the two TickBatonSweep lays for a grounded body - the
        /// drop from the weapon head to the floor plane, or the weapon's own shadow lying on that
        /// plane - and <paramref name="to"/> is therefore ON that plane in both. It exists for
        /// bodies the authored arc passes over, so it accepts only a contact whose whole collider
        /// sits below <see cref="BlacksiteCombatPolicy.BatonLowContactHeight"/> above it - anything
        /// taller is decided by the weapon capsules exactly as it is today - and its occlusion test
        /// ignores level geometry below the contact's own centre, because a strike that ends at the
        /// floor is otherwise blocked by the floor it ends at.
        /// </param>
        private void SweepBatonSegment(byte swing, Vector3 from, Vector3 to, float radius, Vector3 travel, bool lowStrike = false)
        {
            // #1219 F1.1: vanilla's shovel mask, not ~0. LineOfSight and ScanNode volumes are not in
            // it, so the capsule never even reports them.
            BlacksiteHitchTracer.CountPhysicsQuery();
            int count = Physics.OverlapCapsuleNonAlloc(from, to, radius, _batonContacts, BlacksiteHitResolver.ShovelMask, QueryTriggerInteraction.Collide);
            // #1010 A2: a saturated buffer used to drop the WHOLE swing, silently. That is the one
            // outcome a crowded room must not produce: the fuller the capsule, the more certain it
            // is that something in it was worth hitting. The overlap is kept and the candidate list
            // holds the nearest of them, which is the contact a swing at that instant is for.
            bool saturated = count >= _batonContacts.Length;
            var preferred = CombatEnemyTarget;
            int candidates = 0, farthest = -1;
            for (int i = 0; i < count; i++)
            {
                var collider = _batonContacts[i]; if (collider == null || collider.transform.IsChildOf(transform)) continue;
                // #1009 A2 (F5): what the window SAW, counted once per collider. The old running sum
                // added every capsule's overlap count, so one body lying along the shaft was
                // reported as many times as there were capsules over it.
                _batonSweepColliders.Add(collider.GetInstanceID());
                // #1219 F1.1 (D6): the shovel's own contract - the collider's hit object carries the
                // victim component itself - rather than "any collider with an EnemyAI or a
                // PlayerControllerB somewhere above it", which is what let a player's 11 m
                // LineOfSightCube and a bug's ScanNode stand in for their bodies.
                if (!BlacksiteHitResolver.TryResolve(collider, out var enemy, out var contact)) continue;
                // #874 WP F / #1262 P4: the per-member gate, so an incidental swing through an Old Bird's
                // leg or a keeper's shin lands no hit and teaches the memo nothing.
                if (enemy is BlacksiteSoldierAI || (enemy != null && !BlacksiteThreatAdapterRegistry.IsAttackableBy(enemy, this))) continue;
                if (lowStrike && collider.bounds.max.y > to.y + BlacksiteCombatPolicy.BatonLowContactHeight) continue;
                var entry = new BatonCandidate
                {
                    Collider = collider, Victim = enemy, Player = contact,
                    Preferred = enemy != null && preferred != null && ReferenceEquals(enemy, preferred),
                    Distance = Vector3.Distance(transform.position, collider.bounds.center)
                };
                if (candidates < _batonCandidates.Length) { _batonCandidates[candidates++] = entry; farthest = -1; continue; }
                if (farthest < 0) farthest = FarthestBatonCandidate(candidates);
                if (entry.Distance >= _batonCandidates[farthest].Distance) continue;
                _batonCandidates[farthest] = entry; farthest = -1;
            }
            _batonSweepCandidates += candidates;
            // Rate-limited for the same reason the #988 B1 skip line is: one sweep lays up to
            // seventeen weapon capsules plus the low-strike ones, and a crowded room saturates
            // every one of them.
            if (saturated && BlacksiteDiagnostics.Enabled)
                BlacksiteDiagnostics.LogRateLimited("baton.saturated." + MemberIndex, 1f, "Baton",
                    "member=" + MemberIndex + ", swing=" + swing + ", contacts=" + count + ", saturated=True");
            // #988 B1: two passes over the SAME resolved candidates, and the only thing the order
            // decides is which one resolves first. The contact this member is actually fighting goes
            // first, so a bystander sharing the capsule can never take the per-swing slot for a
            // swing that was aimed at the monster. Nothing else about the loop changes: a swing that
            // physically covers two live contacts still reaches both, exactly as it does today.
            for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i < candidates; i++)
            {
                var candidate = _batonCandidates[i];
                if (candidate.Preferred != (pass == 0)) continue;
                var col = candidate.Collider; var victim = candidate.Victim; var player = candidate.Player;
                // #988 B1: a player the mission does not let this member fight is not a victim at
                // all. The 2026-09-12 run spent six of its seven landed sweeps on one - the local
                // player, ineligible in WitnessEscort - because the sweep registered it, took the
                // dedup slot for that swing and then applied nothing (L11769, L11792, L11851,
                // L11944, L11970, L11992, none followed by a `Local client hit enemy` line). It is
                // skipped here, BEFORE the slot and before any hit line or impact cue, so the swing
                // stays available for the contact behind it.
                if (victim == null && !IsPlayerCombatEligible(player))
                {
                    if (BlacksiteDiagnostics.Enabled)
                        BlacksiteDiagnostics.LogRateLimited("baton.ineligible." + MemberIndex, 1f, "Baton",
                            "member=" + MemberIndex + ", swing=" + swing + ", passed over victim=player:" + player.GetInstanceID()
                            + ", " + BatonEligibilityVerdict(null, player) + ", slotConsumed=False");
                    continue;
                }
                int id = victim != null ? victim.GetInstanceID() : player.GetInstanceID();
                if (_batonHitTargets.Contains(id) || !HasCombatLineOfSight(col.bounds.center, player, victim)) continue;
                Transform victimRoot = victim != null ? victim.transform : player.transform;
                Vector3 contactPoint = BatonContactPoint(col, from, to, out Vector3 contactNormal);
                if (contactNormal.sqrMagnitude < 1e-6f) contactNormal = -travel;
                // #1219 F1.1: the three invariants every confirmed hit is held to. The capsule met
                // the collider; these say the WEAPON could have: the contact is within the weapon's
                // own length of the grip, the victim is within the reach envelope of this member,
                // and nothing solid stands on the line from the grip to the contact.
                Vector3 gripPoint = ClosestOnSegment(_sweepGripFrom, _sweepGripTo, contactPoint);
                float gripReach = _sweepGripReach + (lowStrike ? Mathf.Max(0f, gripPoint.y - _sweepFloorPlane) : 0f);
                Vector3 rootDelta = victimRoot.position - transform.position;
                float rootDistance = rootDelta.magnitude;
                rootDelta.y = 0f;
                float rootReach = BlacksiteEngagementGeometry.MaxReach(BatonVictimProfile(victim, player)) + BlacksiteHitResolver.RootReachSlack;
                string refused = (contactPoint - gripPoint).sqrMagnitude > gripReach * gripReach ? "grip-reach"
                    : rootDelta.sqrMagnitude > rootReach * rootReach ? "root-reach"
                    : !BatonContactClear(gripPoint, from, contactPoint, victimRoot,
                        lowStrike ? col.bounds.center.y : float.NegativeInfinity) ? "grip-line"
                    : null;
                if (refused != null)
                {
                    if (BlacksiteDiagnostics.Enabled)
                        BlacksiteDiagnostics.LogRateLimited("baton.refused." + MemberIndex, 1f, "Baton",
                            "member=" + MemberIndex + ", swing=" + swing + ", refused victim=" + (victim != null ? "enemy:" : "player:") + id
                            + ", invariant=" + refused + ", dist=" + rootDistance.ToString("0.00")
                            + ", grip=" + Vector3.Distance(contactPoint, gripPoint).ToString("0.00") + "/" + gripReach.ToString("0.00")
                            + ", rootReach=" + rootReach.ToString("0.00") + ", collider=" + col.name + ", slotConsumed=False");
                    continue;
                }
                _batonHitTargets.Add(id);
                // #1219 F1.3: the impact is captured and sent BEFORE any damage below, which can
                // kill and despawn the victim: its network identity, the contact in world space and
                // in the victim's own frame, all read while the victim is still whole.
                SendBatonImpact(victim != null ? (Component)victim : player, contactPoint, contactNormal);
                string colliderName = BlacksiteDiagnostics.Enabled ? col.name : null;
                // #988 round 2: the stun RPC is sent only when the host actually stunned. It used to
                // go out on every enemy contact, so an unstunnable monster or a hit inside the
                // 2.5 s re-arm mirrored a no-op and, since B4, played a victim-side arc for it.
                // #1083 (WP I): the two reads the rifle path already had. This is the baton's ONE
                // confirmed hit - the candidate is live and not weapons-excluded (the loop above),
                // the swing was not blocked and the per-swing slot was taken - so the give-up clock
                // counts exactly the hits the squad actually landed. TickBatonSweep is host-only
                // (IsServerAuthority at :1461), so the memo stays host state.
                // #1271 S8: a baton kill is a kill: the enemy's reads inline as the rifle's does
                // (CombatController ApplyShotDamage); the player's lands by ClientRpc, so it arms
                // the combat controller's existing confirm window off the pre-hit health.
                if (victim != null)
                {
                    int health = victim.enemyHP;
                    victim.HitEnemyOnLocalClient(2, travel.normalized, null, true);
                    BlacksiteThreatAdapterRegistry.ObserveDamageAttempt(victim, health, victim.enemyHP, "baton");
                    if (victim.isEnemyDead) ReportKillClearToSquad();
                    if (BlacksiteBatonStun.TryApply(victim, null)) BatonStunClientRpc(victim.NetworkObjectId);
                    BlacksiteMonsterTargeting.ForceAggro(victim, this, 7.5f);
                }
                else
                {
                    int health = player.health;
                    ApplyAuthoritativePlayerDamage(player, 20, CauseOfDeath.Bludgeoning);
                    _combatController?.WatchPlayerKill(player, health);
                }
                // #988 B1 / open question 1: the victim is TYPED. Run 2026-09-12 could only argue
                // from the sign of an instance id that `victim=178706` was the local player; the
                // tag and the verdict settle it at the source. #1219 F1.1: dist= is root to root and
                // collider= is the hittable collider the hit was accepted on.
                if (BlacksiteDiagnostics.Enabled)
                    BlacksiteDiagnostics.Log("Baton", "member=" + MemberIndex + ", swing=" + swing
                        + ", victim=" + (victim != null ? "enemy:" : "player:") + id
                        + ", " + BatonEligibilityVerdict(victim, player)
                        + ", target=" + (candidate.Preferred ? "combat" : "incidental")
                        + ", dist=" + rootDistance.ToString("0.00") + ", collider=" + colliderName);
            }
        }
        // #988 B1: the eligibility verdict the [UAT][Baton] line carries. An enemy is a combatant by
        // construction - the ExcludesWeapons and isEnemyDead gates above are the whole test - and a
        // player is whatever IsPlayerCombatEligible says, named with the reason when it says no.
        private string BatonEligibilityVerdict(EnemyAI victim, PlayerControllerB player)
        {
            if (victim != null) return "eligible=True, verdict=enemy-combatant";
            if (IsPlayerCombatEligible(player)) return "eligible=True, verdict=player-combatant";
            return "eligible=False, verdict=player-" + (PlayerKnowledgeIneligibility(player) ?? "outside this mission's engagement rules");
        }
        // #1010 A2: the index of the candidate held furthest from this member, used only when the
        // overlap buffer saturates and the list has to choose which contacts to keep.
        private int FarthestBatonCandidate(int candidates)
        {
            int index = 0;
            for (int i = 1; i < candidates; i++)
                if (_batonCandidates[i].Distance > _batonCandidates[index].Distance) index = i;
            return index;
        }
        // A visible face does not make a swing through a railing valid. #1219 F1.1: and the grip
        // itself must see the contact - the direct line from the hand to the point struck is the
        // third leg, so a capsule that reached around a corner is not a hit.
        private bool BatonContactClear(Vector3 grip, Vector3 from, Vector3 contact, Transform victim,
            float ignoreLevelBelowY = float.NegativeInfinity)
        {
            return BatonSegmentClear(grip, contact, victim, ignoreLevelBelowY)
                && BatonSegmentClear(grip, from, victim, ignoreLevelBelowY)
                && BatonSegmentClear(from, contact, victim, ignoreLevelBelowY);
        }

        // #1219 F1.1: the grip's travel over the current band and the floor plane the low strike
        // lands on, recorded by TickBatonSweep before it lays this frame's capsules.
        private Vector3 _sweepGripFrom, _sweepGripTo; private float _sweepFloorPlane;
        // #1219 F1.1: how far from the grip a weapon capsule can put a contact - the longest clipped
        // world-space shaft plus the head's own radius, and 5 cm for the contact point's own estimate
        // (BatonContactPoint's two projections and a ray-measured body). A low-strike capsule adds
        // the drop from the grip to the floor plane it ends on, which is the whole of what that
        // capsule is for.
        private float _sweepGripReach;

        // #1219 F1.1: the root-to-root wall a confirmed hit is held to. A player is the measured
        // Humanoid row; the tracked contact keeps its last measured row even across an early
        // engagement return; any other body the swing happens to cross is held to the widest row,
        // because its row was never measured and an incidental hit must not be refused for a guess.
        private BlacksiteBodyProfile BatonVictimProfile(EnemyAI victim, PlayerControllerB player)
        {
            if (victim == null) return BlacksiteBodyProfile.Humanoid;
            return ReferenceEquals(victim, _batonVelocityContact) ? _batonTrackedProfile : BlacksiteBodyProfile.Giant;
        }

        private static Vector3 ClosestOnSegment(Vector3 a, Vector3 b, Vector3 point)
        {
            Vector3 ab = b - a; float length = ab.sqrMagnitude;
            return length < 1e-8f ? a : a + ab * Mathf.Clamp01(Vector3.Dot(point - a, ab) / length);
        }

        // #1219 F1.1: where the capsule met the collider, and the surface's outward direction there: the
        // segment and the collider alternate twice, landing on the nearest pair for every convex shape.
        // #1271 S6: a non-convex mesh or CharacterController, where Physics.ClosestPoint is undefined, takes
        // BlacksiteBatonVictimFlash.Surface, never a bounds point inside the body; an inward normal turns out.
        private static Vector3 BatonContactPoint(Collider collider, Vector3 from, Vector3 to, out Vector3 normal)
        {
            Vector3 segment = ClosestOnSegment(from, to, collider.bounds.center);
            Vector3 contact = ColliderClosestPoint(collider, segment);
            segment = ClosestOnSegment(from, to, contact);
            contact = ColliderClosestPoint(collider, segment);
            normal = segment - contact;
            if (Vector3.Dot(normal, contact - collider.bounds.center) <= 0f) normal = contact - collider.bounds.center;
            normal = normal.sqrMagnitude < 1e-6f ? Vector3.zero : normal.normalized;
            return contact;
        }
        private static Vector3 ColliderClosestPoint(Collider collider, Vector3 point)
            => collider is BoxCollider || collider is SphereCollider || collider is CapsuleCollider || (collider is MeshCollider mesh && mesh.convex)
                ? collider.ClosestPoint(point)
                : BlacksiteBatonVictimFlash.Surface(collider, point, out Vector3 surface, out _) ? surface : collider.bounds.ClosestPoint(point);

        // #1219 F1.3: the confirmed hit's typed impact. The identity is the victim's network object
        // (0 for one that has none, whose flash then plays where it was struck), and the point and
        // outward normal are sent both in world space and in that object's own frame, so a client
        // that receives this after the victim has moved still puts the flash on the body.
        private void SendBatonImpact(Component victim, Vector3 point, Vector3 normal)
        {
            var network = victim.GetComponentInParent<NetworkObject>();
            bool networked = network != null && network.IsSpawned;
            Transform root = networked ? network.transform : victim.transform;
            BatonImpactClientRpc(networked ? network.NetworkObjectId : 0UL, point,
                root.InverseTransformPoint(point), Quaternion.Inverse(root.rotation) * normal);
        }
        // #988 B1: world geometry and the victim occlude a swing. Bodies do not.
        //
        // The rule used to fail the strike on ANY EnemyAI or PlayerControllerB collider between the
        // grip and the contact point, which made a squadmate standing on the far side of the monster
        // - or the player the squad is escorting - a perfect shield for it. Four raiders converging
        // on 60-degree lanes are, by construction, standing in each other's swing lines, so the
        // convergence B3 orders would have vetoed itself. A BlacksiteSoldierAI is friendly and a
        // non-victim player is a bystander; neither is cover, and the swing passes through both.
        // Everything else is unchanged: a solid non-trigger collider still blocks, and a THIRD-PARTY
        // monster between the hand and the victim still blocks, because that body is a real
        // obstacle the weapon would meet first.
        /// <param name="ignoreLevelBelowY">
        /// #1010 A2: the height below which STATIC LEVEL GEOMETRY is not cover for this test. It is
        /// <see cref="float.NegativeInfinity"/> for every capsule laid along the weapon, so the rule
        /// above is exactly what it has always been. The downward low-strike capsule passes it the
        /// contact's own bounds centre, because that capsule ends on the floor plane and the floor
        /// plane is a solid non-trigger collider: without this a strike aimed at a 0.3 m body is
        /// refused by the very ground the body is standing on. Nothing above the contact's centre
        /// is exempted, so a railing, a crate or a step between the hand and that body still blocks
        /// the strike, and neither bodies nor monsters are affected at all.
        /// </param>
        private bool BatonSegmentClear(Vector3 from, Vector3 to, Transform victim,
            float ignoreLevelBelowY = float.NegativeInfinity)
        {
            Vector3 delta = to - from; float length = delta.magnitude;
            if (length < .001f) return true;
            BlacksiteHitchTracer.CountPhysicsQuery();
            int count = Physics.SphereCastNonAlloc(from, .07f, delta / length, _batonBlocks, length, ~0, QueryTriggerInteraction.Collide);
            if (count == _batonBlocks.Length) return false;
            for (int i = 0; i < count; i++)
            {
                var c = _batonBlocks[i].collider;
                if (c == null || c.transform.IsChildOf(transform) || c.transform.IsChildOf(victim)) continue;
                var enemy = c.GetComponentInParent<EnemyAI>();
                if (enemy is BlacksiteSoldierAI) continue;
                if (enemy == null && c.GetComponentInParent<PlayerControllerB>() != null) continue;
                if (c.isTrigger && enemy == null) continue;
                if (enemy == null && !float.IsNegativeInfinity(ignoreLevelBelowY)
                    && BatonBlockPoint(_batonBlocks[i], c, to).y < ignoreLevelBelowY) continue;
                return false;
            }
            return true;
        }
        // #1010 A2: where the blocker actually met the cast. A sphere cast that starts already
        // overlapping reports distance 0 and a meaningless point, so the fallback is the blocker's
        // own closest point to the far end of the segment - by bounds for a non-convex mesh, for
        // which Physics.ClosestPoint is undefined.
        private static Vector3 BatonBlockPoint(RaycastHit hit, Collider blocker, Vector3 to)
            => hit.distance > 0f ? hit.point
                : blocker is MeshCollider mesh && !mesh.convex ? blocker.bounds.ClosestPoint(to) : blocker.ClosestPoint(to);
        // #988 round 2: this RPC means "the host stunned this victim"; the client's own TryApply is
        // the state mirror it always was - its verdict is the client's re-arm table, not the host's
        // decision. The victim-side presentation is BatonImpactClientRpc's (BlacksiteBatonVictimFlash).
        [ClientRpc] private void BatonStunClientRpc(ulong enemyId)
        {
            if (NetworkManager == null || !NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(enemyId, out var obj)) return;
            var enemy = obj.GetComponent<EnemyAI>();
            BlacksiteBatonStun.TryApply(enemy, null);
        }
        /// <summary>
        /// #1219 F1.3: one confirmed baton hit on every peer including the host; presentation only (the host
        /// applied the damage). The weapon plays its flash and <c>Impact</c> cue once; the victim gets one short
        /// flash, where it was struck when this peer cannot resolve it.
        /// </summary>
        [ClientRpc]
        private void BatonImpactClientRpc(ulong victimId, Vector3 worldPoint, Vector3 localPoint, Vector3 localNormal)
        {
            _batonEffects?.Impact();
            Transform root = null;
            if (victimId != 0UL && NetworkManager != null && NetworkManager.SpawnManager != null
                && NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(victimId, out var obj) && obj != null)
                root = obj.transform;
            if (root != null) BlacksiteBatonVictimFlash.Play(root, root.TransformPoint(localPoint), root.rotation * localNormal, victimId);
            else BlacksiteBatonVictimFlash.Play(null, worldPoint, Vector3.up, victimId);
        }
    }
}
