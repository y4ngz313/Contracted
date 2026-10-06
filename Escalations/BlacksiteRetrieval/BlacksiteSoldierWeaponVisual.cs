using Unity.Netcode;
using UnityEngine;

using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// The hosted MX16A4 a blacksite soldier carries when Y4NGZUpgrades is installed (#316).
    ///
    /// <b>Every peer builds this itself.</b> The model, its socket pose, the re-seated muzzle and the
    /// cached fire clips are all derived from local prefab data and the soldier's own rig, so there
    /// is nothing here to replicate: the existing shot ClientRpcs already fan the events out and each
    /// client draws them against the model it built. Owning a soldier that is not the one firing is
    /// therefore free of any new traffic.
    ///
    /// When the bridge is inert this type never attaches anything and the soldier falls back to its
    /// authored muzzle-flash renderer and gunshot cues - today's exact behaviour.
    /// </summary>
    internal sealed class BlacksiteSoldierWeaponVisual
    {
        private readonly BlacksiteSoldierAI _soldier;

        private GameObject _model;
        private AudioClip[] _fireClips;
        private int _fireClipCursor;

        private Transform _muzzleAnchor;
        private Vector3 _originalMuzzleLocalPosition;
        private Quaternion _originalMuzzleLocalRotation;
        private bool _muzzleReseated;
        private bool _attachAttempted;

        internal BlacksiteSoldierWeaponVisual(BlacksiteSoldierAI soldier)
        {
            _soldier = soldier;
        }

        internal bool HasModel => _model != null;

        /// <summary>
        /// True once there is nothing left to try: a model is attached, or the bridge has settled
        /// permanently inert. Until then the owner keeps re-offering the rig, because a miss while
        /// the bridge is still resolving is not an answer.
        /// </summary>
        internal bool IsSettled => _attachAttempted || _model != null;

        /// <summary>True once the weapon's own fire audio is available, which is what lets the
        /// soldier stop routing its networked gunshot cue.</summary>
        internal bool HasFireAudio => _model != null && _fireClips != null && _fireClips.Length > 0;

        internal GameObject Model => _model;

        /// <summary>
        /// Socket the model under the rig's weapon root and move the muzzle anchor onto the model's
        /// real barrel end. Idempotent.
        ///
        /// The attempt only latches on a definitive answer - a model that attached, or a bridge that
        /// has permanently gone inert. A miss while the bridge is still resolving (its lookup is
        /// throttled to one every couple of seconds, and the pack may not have loaded yet) leaves the
        /// latch open so the next rig bind or periodic re-bind tries again; latching there would
        /// disarm a soldier for the whole round over a timing accident. The caller only reaches this
        /// while the soldier is alive, which is what bounds the retries.
        /// </summary>
        internal void TryAttach(BlacksiteSoldierRigBinding rig)
        {
            if (_attachAttempted || _model != null)
                return;
            if (rig == null || !rig.IsValid || rig.WeaponRoot == null)
                return;

            if (!BlacksiteUpgradesWeaponBridge.IsAvailable())
            {
                _attachAttempted = BlacksiteUpgradesWeaponBridge.IsPermanentlyInert;
                return;
            }

            if (!BlacksiteUpgradesWeaponBridge.TryInstantiateWorldModel(rig.WeaponRoot, out GameObject model))
            {
                _attachAttempted = BlacksiteUpgradesWeaponBridge.IsPermanentlyInert;
                return;
            }

            _attachAttempted = true;
            _model = model;
            Transform modelTransform = model.transform;
            modelTransform.localPosition = BlacksiteRetrievalTuning.HostedWeaponLocalPosition;
            modelTransform.localRotation = Quaternion.Euler(BlacksiteRetrievalTuning.HostedWeaponLocalEuler);
            modelTransform.localScale = Vector3.one * BlacksiteRetrievalTuning.HostedWeaponUniformScale;

            ReseatMuzzle(rig, modelTransform);
            BlacksiteUpgradesWeaponBridge.TryGetFireClips(out _fireClips);
            // #929: the hosted pack's own fire clips are what a peer carrying an MX16A4 actually
            // hears, so they get the same measured scaling to the -1 dBFS ceiling the built-in
            // gunshot cues do - once, here, rather than per shot.
            if (_fireClips != null)
                for (int i = 0; i < _fireClips.Length; i++)
                    _fireClips[i] = BlacksiteAudioPolicy.NormalizedShot(_fireClips[i]);

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Blacksite] Soldier {_soldier?.MemberIndex ?? -1} is carrying a hosted MX16A4"
                + (_muzzleReseated ? " with its muzzle seated on the model barrel." : "."));
        }

        /// <summary>
        /// Move <c>BlacksiteMuzzleAnchor</c> onto the model's authored bore. The landmark arrives in
        /// the model root's local frame, so it goes through the model transform (which carries the
        /// socket pose and scale) and back into the anchor's own parent frame. Both live in the same
        /// rigid subtree below the weapon root, so this survives every animation without a per-frame
        /// correction.
        /// </summary>
        private void ReseatMuzzle(BlacksiteSoldierRigBinding rig, Transform modelTransform)
        {
            Transform muzzle = rig.Muzzle;
            if (muzzle == null || muzzle.parent == null)
                return;
            if (!BlacksiteUpgradesWeaponBridge.TryGetMuzzle(out Vector3 localPosition, out Vector3 localForward))
                return;

            _muzzleAnchor = muzzle;
            _originalMuzzleLocalPosition = muzzle.localPosition;
            _originalMuzzleLocalRotation = muzzle.localRotation;

            Vector3 worldPosition = modelTransform.TransformPoint(localPosition);
            Vector3 worldForward = modelTransform.TransformDirection(localForward);
            if (worldForward.sqrMagnitude < 1e-6f)
                worldForward = modelTransform.forward;

            Transform parent = muzzle.parent;
            muzzle.localPosition = parent.InverseTransformPoint(worldPosition);
            muzzle.localRotation = Quaternion.Inverse(parent.rotation)
                * Quaternion.LookRotation(worldForward.normalized, modelTransform.up);
            _muzzleReseated = true;
        }

        /// <summary>Returns whether a flash was actually drawn, so a caller can fall back to its own
        /// stand-in when the bridge call did not land.</summary>
        internal bool PlayMuzzleFlash(Vector3 worldForward)
        {
            if (_model == null)
                return false;
            return BlacksiteUpgradesWeaponBridge.PlayMuzzleFlash(
                _model,
                worldForward,
                BlacksiteRetrievalTuning.HostedWeaponMuzzleFlashScale);
        }

        /// <summary>The next fire variation in cycle order, so a burst does not repeat one sample.</summary>
        internal AudioClip NextFireClip()
        {
            if (!HasFireAudio)
                return null;
            AudioClip clip = _fireClips[_fireClipCursor % _fireClips.Length];
            _fireClipCursor = (_fireClipCursor + 1) % _fireClips.Length;
            return clip;
        }

        /// <summary>
        /// Drop the cosmetic model and put the muzzle anchor back where the rig authored it, so a
        /// soldier that loses its weapon behaves exactly like one that never had one.
        /// </summary>
        internal void Destroy()
        {
            if (_muzzleReseated && _muzzleAnchor != null)
            {
                _muzzleAnchor.localPosition = _originalMuzzleLocalPosition;
                _muzzleAnchor.localRotation = _originalMuzzleLocalRotation;
            }
            _muzzleReseated = false;
            _muzzleAnchor = null;
            _fireClips = null;

            if (_model != null)
            {
                Object.Destroy(_model);
                _model = null;
            }
        }

        /// <summary>
        /// Server-only: put a real, pickable MX16A4 on the floor where the soldier fell, carrying the
        /// magazine he had left rather than the free full one every weapon starts with.
        ///
        /// Deliberately does not touch <c>RoundManager.totalScrapValueInLevel</c> - a dropped rifle is
        /// a weapon, not scrap, and adding it to the quota total would be a balance change nobody
        /// asked for.
        /// </summary>
        internal static bool TrySpawnDeathDropServer(BlacksiteSoldierAI soldier)
        {
            if (soldier == null)
                return false;
            if (!BlacksiteUpgradesWeaponBridge.TryGetItemPrefab(out GameObject prefab))
                return false;

            Transform props = StartOfRound.Instance != null ? StartOfRound.Instance.propsContainer : null;
            Vector3 point = BlacksiteDriveCarrier.ResolveReachableDropPoint(soldier.transform.position);
            Quaternion rotation = Quaternion.Euler(0f, soldier.transform.eulerAngles.y, 0f);

            GameObject spawned = Object.Instantiate(prefab, point, rotation, props);
            if (spawned == null)
                return false;
            if (!spawned.activeSelf)
                spawned.SetActive(true);

            var networkObject = spawned.GetComponent<NetworkObject>();
            if (networkObject == null)
            {
                ContractsBootstrap.Log?.LogWarning(
                    "[MoonContracts.Blacksite] Hosted weapon prefab lacks a NetworkObject; the death drop was cancelled.");
                Object.Destroy(spawned);
                return false;
            }

            var grabbable = spawned.GetComponent<GrabbableObject>();
            if (grabbable != null)
            {
                // The same fields a vanilla scrap spawn seeds, and the same set BlacksiteHardDrive-
                // ObjectiveService seeds for its own drop: they describe the item for the frames
                // before GrabbableObject.Start runs, after which Start's FallToGround owns the fall
                // state outright and rewrites fallTime and the two floor positions itself.
                grabbable.isHeld = false;
                grabbable.isHeldByEnemy = false;
                grabbable.isPocketed = false;
                grabbable.fallTime = 0f;
                grabbable.isInFactory = !soldier.isOutside;
                grabbable.startFallingPosition = props != null
                    ? props.InverseTransformPoint(point)
                    : spawned.transform.localPosition;
                grabbable.targetFloorPosition = grabbable.startFallingPosition;
            }

            // Initialise before spawning: the API supports either order, and doing it first means the
            // magazine is already correct in the spawn-time state publish rather than in a correcting
            // one, so no client ever sees a full magazine flash past.
            int magazine = Mathf.Clamp(soldier.WeaponSnapshot.Magazine, 0, MaximumDroppedMagazine);
            BlacksiteUpgradesWeaponBridge.InitializeDroppedWeapon(spawned, magazine, safetyOn: true);

            networkObject.Spawn();
            if (props != null)
                networkObject.TrySetParent(props, worldPositionStays: true);

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Blacksite] Soldier {soldier.MemberIndex} dropped an MX16A4 with {magazine} rounds.");
            return true;
        }

        /// <summary>
        /// Ceiling on the round count handed to the API, which clamps again to the weapon's real
        /// magazine size.
        ///
        /// It cannot bind today: the snapshot's magazine is a byte the combat controller already
        /// clamps to its own capacity of 18. This is future-proofing against that capacity growing
        /// past what a dropped rifle should ever carry, which is why the clamp stays.
        /// </summary>
        private const int MaximumDroppedMagazine = 30;
    }
}
