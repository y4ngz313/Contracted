using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Y4NGZCompany.Escalations._Shared;
using Y4NGZCompany.Experience.UITheme;
namespace Y4NGZCompany.Contracts._Shared
{
    internal partial class ContractHUDManager
    {
        private RectTransform _powerFaultRow;
        private TextMeshProUGUI _powerFaultText;
        private Image _powerFaultDivider;
        // #1195: _sectionsOnly is a card opened by its in-card sections (this row, the SIDE
        // section) with no moon contract; it closes once neither section needs it.
        private bool _powerFaultVisible, _sectionsOnly;
        private float _nextPowerFaultPoll;
        private float PowerFaultRowHeight => _powerFaultVisible ? 68f : 0f;

        private void BuildPowerFaultRow(Transform parent)
        {
            _powerFaultRow = new GameObject("BatteryFailureRow",typeof(RectTransform)).GetComponent<RectTransform>();
            _powerFaultRow.SetParent(parent,false);
            _powerFaultText = new GameObject("BatteryFailureObjective",typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
            _powerFaultText.transform.SetParent(_powerFaultRow,false);
            _powerFaultText.fontSize=20f;_powerFaultText.alignment=TextAlignmentOptions.TopLeft;
            _powerFaultText.enableWordWrapping=true;_powerFaultText.raycastTarget=false;
            _powerFaultText.text="BATTERY FAILURE\n<size=90%>Use the apparatus to restore power</size>";
            _powerFaultText.color=ContractUiTheme.Accent;AssignVanillaFont(_powerFaultText);
            var rect=_powerFaultText.rectTransform;rect.anchorMin=Vector2.zero;rect.anchorMax=Vector2.one;
            rect.offsetMin=Vector2.zero;rect.offsetMax=new Vector2(0,-10);
            _powerFaultDivider=new GameObject("ObjectiveDivider",typeof(RectTransform)).AddComponent<Image>();
            _powerFaultDivider.transform.SetParent(_powerFaultRow,false);_powerFaultDivider.raycastTarget=false;
            _powerFaultDivider.color=ContractUiTheme.Accent;
            rect=_powerFaultDivider.rectTransform;rect.anchorMin=new Vector2(0,1);rect.anchorMax=Vector2.one;
            rect.offsetMin=new Vector2(0,-1);rect.offsetMax=Vector2.zero;
        }
        private void LayoutPowerFaultRow()
        {
            if(_powerFaultRow==null)return;
            _powerFaultRow.gameObject.SetActive(ContractHudArrival.ContentVisible(_isSettled, _powerFaultVisible));
            _powerFaultRow.anchorMin=Vector2.zero;_powerFaultRow.anchorMax=new Vector2(1,0);_powerFaultRow.pivot=new Vector2(.5f,0);
            _powerFaultRow.offsetMin=new Vector2(LiveCardInnerSidePadding,LiveCardBottomPadding);
            _powerFaultRow.offsetMax=new Vector2(-LiveCardInnerSidePadding,LiveCardBottomPadding+PowerFaultRowHeight);
            _powerFaultDivider.enabled=ContractHudSideSectionLayout.PowerFaultDivider(!_sectionsOnly,SideSectionShown);
            _titleText.enabled=!_sectionsOnly;
            _objectiveText.enabled=ContractHudArrival.ContentVisible(_isSettled, !_sectionsOnly);
            if(_contractIcon!=null)_contractIcon.gameObject.SetActive(!_sectionsOnly&&!_showingResult);
            if(_objectiveMarks!=null)_objectiveMarks.gameObject.SetActive(ContractHudArrival.ContentVisible(_isSettled, !_sectionsOnly));
        }
        private void TickPowerFaultRow(bool force = false)
        {
            if(!force && Time.unscaledTime<_nextPowerFaultPoll)return;
            _nextPowerFaultPoll=Time.unscaledTime+.1f;
            bool active=ShipPowerEscalationObjectiveState.IsActive;
            bool changed=active!=_powerFaultVisible;
            _powerFaultVisible=active;
            // Same Contracted card and visibility lifecycle, including when no moon contract exists.
            if(active&&!_isShowing && GameNetworkManager.Instance?.localPlayerController != null)
            {
                if(!OpenSectionsOnlyCard())return;
                changed=true;
            }
            // #1195: one close rule for a card with no contract, shared with the SIDE tick.
            if(_sectionsOnly&&CloseSectionsOnlyCardIfUnneeded())return;
            if(!_isShowing||_cardRect==null)return;
            if(changed)ResizeLiveCard(_objectiveText.text,animate:false);
            LayoutPowerFaultRow();
            if(_sectionsOnly)
            {
                ApplyUnitAdapter(false);ApplyTheme();
                ApplyPresentationVisibility(ShouldPresent());
            }
        }
    }
}
