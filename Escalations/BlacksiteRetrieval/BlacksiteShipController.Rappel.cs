using Unity.Netcode;
using UnityEngine;
using Y4NGZCompany.Contracts.Defuse;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal sealed partial class BlacksiteShipController
    {
        private readonly NetworkVariable<bool> _cableReserved = new NetworkVariable<bool>();
        private GameObject _rappelRoot;
        private InteractTrigger _rappelTrigger;
        private Transform _rappelBottom, _rappelTop, _rappelPlane, _rappelHorizontal;
        private BlacksiteRopeVisual _rappelCable;
        private float _cableExtension;
        // #874 F04: the cache owns the ROPE only while it is actually on it. Its ground stop is a
        // separate fact - see BlacksiteCablePhase - read by CargoBlocksPersonnelCable below.
        internal bool CargoUsesCable => IsHovering && HasCargoContainer && BlacksiteCablePhase.OnCable(_cargoMotion.Value);
        // Whether a PLAYER can use this rope at all (the interact trigger and the rope's end pose):
        // the crate is on it, or standing hooked on the touchdown its bottom end rests on. A crate
        // PARKED while the ship was away is neither.
        internal bool CargoBlocksPersonnelCable => IsHovering && HasCargoContainer && BlacksiteCablePhase.BlocksPersonnel(_cargoMotion.Value);
        private bool HasCargoContainer => _cargoContainerId.Value != ulong.MaxValue;
        internal bool RappelUsedByPlayer
        {
            get
            {
                // Nobody can be on a cable that is not drawn: a ground hull has none.
                if (!IsHovering || StartOfRound.Instance == null) return false;
                foreach (var player in StartOfRound.Instance.allPlayerScripts)
                {
                    if (player == null || player.isPlayerDead || !player.isClimbingLadder) continue;
                    Vector3 local = transform.InverseTransformPoint(player.transform.position);
                    if (Mathf.Abs(local.x) < 1.1f && Mathf.Abs(local.z - BlacksiteRappelGeometry.CableTopLocal.z) < 1.1f
                        && local.y >= Access.DropLandingLocal.y - 1f && local.y <= 3f) return true;
                }
                return false;
            }
        }
        private void ApplyRappel()
        {
            if (!IsHovering) { if (_rappelRoot != null) _rappelRoot.SetActive(false); return; }
            if (_rappelRoot == null) CreateRappel();
            // One rope serves people and the cache. A cargo trip reserves the existing player
            // ladder rather than drawing another cable through the same boarding column.
            bool deployed = IsGrounded;
            _cableExtension = Mathf.MoveTowards(_cableExtension, deployed ? 1 : 0, Time.deltaTime * 2.5f);
            _rappelRoot.SetActive(deployed || _cableExtension > 0);
            _rappelTrigger.gameObject.SetActive(deployed);
            if (_cableExtension <= 0) return;
            // The rope is reserved against a player whenever a body cannot share it: the crate on
            // it or on its foot, a FIFO holder (#874 S3), or another player.
            if (IsServer) _cableReserved.Value = CargoBlocksPersonnelCable || Ladder.HasOccupant(Time.time) || RappelUsedByPlayer;
            var access = Access;
            _rappelBottom.localPosition = access.DropLandingLocal;
            _rappelTop.localPosition = access.LadderTopLocal;
            _rappelHorizontal.localPosition = BlacksiteRappelGeometry.CableTopLocal;
            // Vanilla owns this node while the local player is climbing.
            if (!_rappelTrigger.usingLadder) _rappelPlane.localPosition = access.DropLandingLocal + Vector3.up * .3f;
            Vector3 cableEnd = access.DropLandingLocal + Vector3.up * .15f;
            if (CargoContainer != null && CargoBlocksPersonnelCable)
                cableEnd = transform.InverseTransformPoint(CargoContainer.transform.TransformPoint(_cargoHookLocal));
            _rappelCable.Pose(BlacksiteRappelDavit.CableAnchor, cableEnd, (float)CargoNow, _cableExtension, _cableReserved.Value);
            var box = _rappelTrigger.GetComponent<BoxCollider>();
            box.center = new Vector3(0, access.DropLandingLocal.y * .5f + 1f, BlacksiteRappelGeometry.CableTopLocal.z);
            box.size = new Vector3(.65f, -access.DropLandingLocal.y + 2f, .65f);
            _rappelTrigger.interactable = !_cableReserved.Value || _rappelTrigger.usingLadder;
        }
        private void CreateRappel()
        {
            _rappelRoot = new GameObject("BlacksiteRappel"); _rappelRoot.transform.SetParent(transform, false);
            Transform Anchor(string name)
            {
                var anchor = new GameObject(name).transform; anchor.SetParent(_rappelRoot.transform, false);
                anchor.localRotation = Quaternion.Euler(0, 180, 0); return anchor;
            }
            _rappelBottom = Anchor("CableBottom"); _rappelTop = Anchor("CableTop");
            _rappelPlane = Anchor("CablePlayer"); _rappelHorizontal = Anchor("CableColumn");
            var cable = new GameObject("Cable"); cable.transform.SetParent(_rappelRoot.transform, false);
            _rappelCable = cable.AddComponent<BlacksiteRopeVisual>(); _rappelCable.Initialize();
            var volume = Anchor("CableInteraction"); volume.localRotation = Quaternion.identity;
            int layer = LayerMask.NameToLayer("InteractableObject"); volume.gameObject.layer = layer < 0 ? 9 : layer;
            volume.gameObject.tag = "InteractTrigger"; volume.gameObject.AddComponent<BoxCollider>().isTrigger = true;
            _rappelTrigger = volume.gameObject.AddComponent<InteractTrigger>();
            DefuseInteractor.ConfigureCommonTrigger(_rappelTrigger, false, .1f);
            _rappelTrigger.isLadder = true; _rappelTrigger.hoverTip = "Climb cable : [LMB]";
            _rappelTrigger.disabledHoverTip = "[ Cable in use ]";
            _rappelTrigger.bottomOfLadderPosition = _rappelBottom; _rappelTrigger.topOfLadderPosition = _rappelTop;
            _rappelTrigger.ladderPlayerPositionNode = _rappelPlane; _rappelTrigger.ladderHorizontalPosition = _rappelHorizontal;
        }
        private void ReleaseRappel()
        {
            // #874 F04: the cache's own cable lane goes with the rope it was drawn on, so a ship
            // that despawns mid-trip leaves no lease behind for the next round to inherit.
            ReleaseCargoCableLane();
            if (_rappelRoot != null) Destroy(_rappelRoot);
        }
    }
}
