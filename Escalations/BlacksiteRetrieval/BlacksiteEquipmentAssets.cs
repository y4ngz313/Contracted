using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts._Shared;
using Y4NGZCompany.Core.Compat;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal static class BlacksiteEquipmentAssets
    {
        internal const string FileName = "blacksite-equipment.bundle";
        private static AssetBundle _bundle;
        private static bool _attempted;
        private static readonly Dictionary<string, UnityEngine.Object> Assets = new Dictionary<string, UnityEngine.Object>(StringComparer.OrdinalIgnoreCase);
        internal static GameObject DroppedBaton { get; private set; }
        internal static T Load<T>(string name) where T : UnityEngine.Object
        {
            if (!_attempted)
            {
                _attempted = true;
                string path = Path.Combine(Path.GetDirectoryName(typeof(BlacksiteEquipmentAssets).Assembly.Location), FileName);
                if (File.Exists(path)) _bundle = AssetBundle.LoadFromFile(path);
                if (_bundle == null) ContractsBootstrap.Log?.LogError("[Blacksite.Equipment] Required equipment bundle missing: " + FileName);
            }
            if (Assets.TryGetValue(name, out var cached)) return cached as T;
            var value = _bundle != null ? _bundle.LoadAllAssets<T>().FirstOrDefault(a => string.Equals(a.name, name, StringComparison.OrdinalIgnoreCase)) : null;
            // #929: a miss is cached below and is therefore PERMANENT for the process, and every
            // consumer of a null is silent by design - BlacksiteBatonEffects.Cue simply does not
            // play, Shovel.hitSFX simply holds a null entry. A deployed bundle that predates a clip
            // was indistinguishable from a working one; the 2026-09-15 host log carries no line at
            // all for the missing "Impact" cue. Warned once per NAME, not per call, because Cue is
            // reached on every contact: the miss is a property of the bundle, and repeating it per
            // swing would bury the log the same diagnosis needs.
            if (value == null && Missing.Add(name))
                ContractsBootstrap.Log?.LogWarning(
                    "[Blacksite.Equipment] " + FileName + " has no " + typeof(T).Name + " named '" + name
                    + "'; every use of it is silently skipped until the bundle is rebuilt.");
            Assets[name] = value; return value;
        }

        // Names already reported. Parallel to Assets, which caches the null itself, so the warning
        // has exactly the lifetime the miss it describes has.
        private static readonly HashSet<string> Missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // #874 S5: the BS_RopeClimb clip's length on the shipped baton controller, the numerator of the
        // rope crossing's clip rate; 0 when the controller or clip is absent (BatonRope is absent too, so
        // the walking-legs fallback plays and the rate is never used). Cached like Load's own misses.
        private static float? _ropeClipSeconds;
        internal static float RopeClipSeconds => _ropeClipSeconds ??= MeasureRopeClip();
        private static float MeasureRopeClip()
        {
            var controller = Load<RuntimeAnimatorController>("BlacksiteBatonRuntime");
            var clip = controller == null ? null : controller.animationClips.FirstOrDefault(c => c != null && c.name == "BS_RopeClimb");
            return clip == null ? 0f : clip.length;
        }
        internal static void Register()
        {
            if (DroppedBaton != null) return;
            var visual = Load<GameObject>("BlacksiteBaton_Visual");
            if (visual == null) return;
            var root = ContractAssetLoader.CreateHiddenPrefabRoot("BlacksiteStunBaton");
            root.layer = 6; root.tag = "PhysicsProp";
            var network = root.AddComponent<NetworkObject>();
            ContractAssetLoader.AssignStableNetworkHash(network, "Blacksite", "StunBaton");
            var body = root.AddComponent<Rigidbody>(); body.isKinematic = true; body.mass = 1.2f;
            var audio = root.AddComponent<AudioSource>(); audio.playOnAwake = false; audio.spatialBlend = 1; audio.maxDistance = 18;
            var prop = root.AddComponent<Shovel>();
            var native = Resources.FindObjectsOfTypeAll<Item>().FirstOrDefault(i => i.spawnPrefab != null && i.spawnPrefab.GetComponent<Shovel>() != null);
            var item = native != null ? UnityEngine.Object.Instantiate(native) : ScriptableObject.CreateInstance<Item>();
            item.name = "Blacksite Stun Baton Item"; item.itemName = "Blacksite stun baton";
            item.spawnPrefab = root; item.spawnPositionTypes = new List<ItemGroup>();
            item.twoHanded = false; item.twoHandedAnimation = true; item.holdButtonUse = true;
            item.grabAnim = "HoldLung"; item.isDefensiveWeapon = true;
            item.weight = 1.04f; item.isScrap = false; item.creditsWorth = 0; item.requiresBattery = false;
            item.canBeGrabbedBeforeGameStart = true; item.itemSpawnsOnGround = true; item.isConductiveMetal = true;
            item.syncGrabFunction = true; item.syncUseFunction = true; item.syncDiscardFunction = true;
            item.toolTips = new[] { "Swing : [LMB]" }; item.grabAnimationTime = .3f;
            // Native shovel holder rotation; palm centre measured from the supplied native-hand fixture.
            // First/third-person camera framing remains a gameplay acceptance check.
            item.positionOffset = Quaternion.Euler(0, 200, 0) * new Vector3(.035f, -.15f, -.59f); item.rotationOffset = new Vector3(0, 200, 0);
            item.restingRotation = new Vector3(0, 0, 90); item.verticalOffset = .045f;
            prop.itemProperties = item; prop.grabbable = true; prop.grabbableToEnemies = false;
            prop.propBody = body; prop.originalScale = Vector3.one;
            prop.shovelHitForce = 2; prop.shovelAudio = audio;
            prop.reelUp = Load<AudioClip>("PowerOn"); prop.swing = Load<AudioClip>("Swing"); prop.hitSFX = new[] { Load<AudioClip>("Impact") };
            var model = UnityEngine.Object.Instantiate(visual, root.transform, false);
            model.name = "BatonModel"; model.transform.localScale = Vector3.one * 1.693f;
            foreach (var collider in model.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.DestroyImmediate(collider);
            var pickup = root.AddComponent<BoxCollider>(); pickup.center = new Vector3(0, .52f, 0); pickup.size = new Vector3(.12f, 1.22f, .12f);
            var scan = new GameObject("ScanNode"); scan.transform.SetParent(root.transform, false); scan.transform.localPosition = pickup.center; scan.layer = 22;
            var scanCollider = scan.AddComponent<BoxCollider>(); scanCollider.isTrigger = true; scanCollider.size = pickup.size;
            var node = scan.AddComponent<ScanNodeProperties>(); node.headerText = "Blacksite stun baton"; node.subText = ""; node.nodeType = 2; node.minRange = 1; node.maxRange = 13; node.requiresLineOfSight = true;
            model.AddComponent<BlacksiteBatonEffects>();
            DawnLibCompat.FixMixerGroups(root); DawnLibCompat.RegisterNetworkPrefab(root); DawnLibCompat.RegisterItem("blacksite_stun_baton", item);
            DroppedBaton = root;
        }
        internal static void DropServer(BlacksiteSoldierAI soldier)
        {
            if (DroppedBaton == null) return;
            var point = BlacksiteDriveCarrier.ResolveReachableDropPoint(soldier.transform.position);
            var go = UnityEngine.Object.Instantiate(DroppedBaton, point, Quaternion.Euler(0, soldier.transform.eulerAngles.y, 0), StartOfRound.Instance?.propsContainer);
            go.SetActive(true); var item = go.GetComponent<Shovel>(); item.isInFactory = !soldier.isOutside;
            item.startFallingPosition = go.transform.localPosition; item.targetFloorPosition = item.startFallingPosition; item.fallTime = 0;
            go.GetComponent<NetworkObject>().Spawn();
        }
    }
}
