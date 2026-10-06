using UnityEngine;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal sealed class BlacksiteAnimationEventForwarder : MonoBehaviour
    {
        private BlacksiteSoldierAI _soldier;

        private void Awake()
        {
            ResolveSoldier();
        }

        private void OnEnable()
        {
            ResolveSoldier();
        }

        internal void Init(BlacksiteSoldierAI soldier)
        {
            _soldier = soldier;
        }

        public void OnBlacksiteFootstep()
        {
            ResolveSoldier();
            _soldier?.OnBlacksiteFootstep();
        }

        public void OnBlacksiteLeftStep()
        {
            ResolveSoldier();
            _soldier?.OnBlacksiteLeftStep();
        }

        public void OnBlacksiteRightStep()
        {
            ResolveSoldier();
            _soldier?.OnBlacksiteRightStep();
        }

        public void OnBlacksiteFire()
        {
            ResolveSoldier();
            _soldier?.OnBlacksiteFireEvent();
        }

        // Old bundles may still emit this event. It deliberately has no gameplay receiver.
        public void OnBlacksiteGrenadeRelease() { }

        public void OnBlacksiteMagazineRelease()
        {
            ResolveSoldier();
            _soldier?.OnBlacksiteMagazineRelease();
        }

        public void OnBlacksiteMagazineInsert()
        {
            ResolveSoldier();
            _soldier?.OnBlacksiteMagazineInsert();
        }

        public void OnBlacksiteActionComplete()
        {
            ResolveSoldier();
            _soldier?.OnBlacksiteActionComplete();
        }

        private void ResolveSoldier()
        {
            if (_soldier == null)
                _soldier = GetComponentInParent<BlacksiteSoldierAI>();
        }
    }
}
