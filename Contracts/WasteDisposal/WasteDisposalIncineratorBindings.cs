using System;
using UnityEngine;

namespace Y4NGZCompany.Contracts.WasteDisposal
{
    [Serializable]
    public struct WasteDisposalCollisionBoxBinding
    {
        public string Name;
        public Vector3 Center;
        public Vector3 Size;
    }

    /// <summary>
    /// Optional semantic bindings for an authored incinerator visual. Gameplay never reads mesh
    /// names or renderer bounds; a replacement prefab only needs to bind the roles it supports.
    /// Missing roles are supplied by <see cref="WasteDisposalIncineratorVisualAdapter"/>.
    /// </summary>
    public sealed class WasteDisposalIncineratorBindings : MonoBehaviour
    {
        public Transform FeedDoor;
        public Transform BurnLever;
        public Transform StartButton;
        public Transform StopButton;
        public Transform GaugeNeedle;
        public Transform FanRotor;
        public Transform StackDamper;
        public Transform AshDoor;
        public Transform FireFxSocket;
        public Transform SmokeFxSocket;
        public Transform PassiveLightSocket;
        public Transform BurnLightSocket;
        public Transform AudioSocket;
        public Transform CctvMarkerSocket;
        public Renderer ScreenRenderer;
        public Renderer EmberRenderer;
        public WasteDisposalCollisionBoxBinding[] CollisionBoxes;

        [Header("Semantic motion offsets")]
        public Vector3 FeedDoorOpenEuler = new Vector3(0f, 68f, 0f);
        [Min(0.1f)] public float FeedDoorTravelSeconds = 0.65f;
        public Vector3 ButtonPressedLocalOffset = new Vector3(0f, 0f, -0.006f);
        public Vector3 BurnLeverActivatedEuler = new Vector3(0f, 0f, -58f);
        public Vector3 GaugeColdEuler = new Vector3(0f, 0f, -55f);
        public Vector3 GaugeHotEuler = new Vector3(0f, 0f, 55f);
        public Vector3 StackDamperOpenEuler = new Vector3(0f, 70f, 0f);
        public Vector3 AshDoorCompleteEuler = new Vector3(-8f, 0f, 0f);
    }
}
