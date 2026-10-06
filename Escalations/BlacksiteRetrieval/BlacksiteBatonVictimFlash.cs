using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// One authored HDRP impact per confirmed hit, owned by BatonImpactClientRpc: its strands wrap the
    /// victim's collider at authored world widths and follow its pose, one slot per network victim.
    /// </summary>
    internal static class BlacksiteBatonVictimFlash
    {
        internal const string HostName = "BlacksiteBatonVictimFlash";
        internal const int SlotCount = 8;
        internal const float ArcSeconds = .080f, ArcHoldSeconds = .020f;
        internal const float LightSeconds = .065f, LightHoldSeconds = .010f, OffSeconds = .100f;
        // #1271 S6: strand wrap angle, lift off the collider, points per authored segment; the Hit A5 row measures them.
        internal const float WrapRadians = 2.8f, ShellLift = .04f; internal const int Subdivide = 3;
        private const string PrefabPath = "assets/y4ngz/contracts/blacksiteretrieval/generated/s9/blacksitebatonvictimimpact.prefab";

        private static readonly int EmissiveColorId = Shader.PropertyToID("_EmissiveColor");
        private static readonly MaterialPropertyBlock Block = new MaterialPropertyBlock();

        private sealed class Slot
        {
            internal GameObject Root;
            internal LineRenderer[] Core; internal Vector3[][] Shell;
            internal Light Light; internal HDAdditionalLightData HdrpLight;
            internal Color Emission;
            internal Transform Victim;
            internal ulong NetworkId;
            internal bool Active, Following, ArcsLit;
            internal Vector3 LocalPoint, WorldPoint;
            internal Quaternion LocalFrame, WorldFrame;
            internal float Started;
        }

        private static GameObject _host;
        private static Slot[] _slots;
        private static int _active;
        private static Vector3[][] _authored;

        /// <summary>One flash struck at <paramref name="worldPoint"/> facing <paramref name="worldNormal"/>, following
        /// <paramref name="victimRoot"/> when this peer resolved it; the network id keeps the per-victim budget.</summary>
        internal static void Play(Transform victimRoot, Vector3 worldPoint, Vector3 worldNormal, ulong networkId = 0)
        {
            if (!EnsurePool()) return;
            worldNormal.Normalize();
            Quaternion frame = Quaternion.LookRotation(worldNormal,
                Mathf.Abs(Vector3.Dot(worldNormal, Vector3.up)) > .95f ? Vector3.forward : Vector3.up);
            var slot = SlotFor(victimRoot, networkId);
            if (!slot.Active) _active++;
            slot.Active = true;
            slot.Victim = victimRoot;
            slot.NetworkId = networkId;
            slot.Following = victimRoot != null;
            slot.WorldPoint = worldPoint; slot.WorldFrame = frame;
            if (slot.Following)
            {
                slot.LocalPoint = victimRoot.InverseTransformPoint(worldPoint);
                slot.LocalFrame = Quaternion.Inverse(victimRoot.rotation) * frame;
            }
            slot.Started = Time.time;
            // The struck player's own camera sits inside the body the arcs would coat; his view
            // gets the small light only, never a screen-filling arc.
            slot.ArcsLit = !IsLocalPlayer(victimRoot);
            Wrap(slot, victimRoot == null ? null : BlacksiteHitResolver.ResolveBody(victimRoot.TryGetComponent(out EnemyAI enemy)
                ? enemy : (Component)victimRoot.GetComponent<GameNetcodeStuff.PlayerControllerB>()), worldPoint, frame);
            slot.Root.SetActive(true);
            Place(slot);
            Apply(slot, 0f);
        }

        internal static void Tick()
        {
            if (_active == 0 || _slots == null) return;
            float now = Time.time;
            foreach (var slot in _slots)
            {
                if (!slot.Active) continue;
                float t = now - slot.Started;
                if (t >= OffSeconds || (t >= ArcSeconds && t >= LightSeconds)) { Stop(slot); continue; }
                // A victim destroyed mid-flash - killed and despawned by the hit itself - leaves
                // the rest of the flash where it last was rather than taking it down with it.
                if (slot.Following && slot.Victim == null) slot.Following = false;
                Place(slot);
                Apply(slot, t);
            }
        }

        private static void Place(Slot slot)
        {
            if (slot.Following)
            {
                slot.WorldPoint = slot.Victim.TransformPoint(slot.LocalPoint);
                slot.WorldFrame = slot.Victim.rotation * slot.LocalFrame;
                for (int i = 0; i < slot.Core.Length; i++)
                    for (int j = 0; j < slot.Shell[i].Length; j++) slot.Core[i].SetPosition(j, slot.Victim.TransformPoint(slot.Shell[i][j]));
            }
            slot.Root.transform.SetPositionAndRotation(slot.WorldPoint, slot.WorldFrame);
        }

        // #1271 S6: the authored strands wrapped over the struck body's hittable collider, on every peer: strand i
        // leaves the contact on its own evenly spread heading and runs WrapRadians round the body in the bounds'
        // normalised frame, zigzag kept. Only positions follow the body, never widths (#988 B4); off it: the burst.
        private static void Wrap(Slot slot, Collider body, Vector3 contact, Quaternion frame)
        {
            Bounds bounds = body != null ? body.bounds : default;
            Vector3 extents = Vector3.Max(bounds.extents, Vector3.one * .05f);
            Vector3 pole = Vector3.Scale(contact - bounds.center, new Vector3(1f / extents.x, 1f / extents.y, 1f / extents.z)).normalized;
            Vector3 u = Vector3.Cross(pole, Mathf.Abs(pole.y) > .95f ? Vector3.forward : Vector3.up).normalized, v = Vector3.Cross(pole, u);
            for (int i = 0; i < slot.Core.Length; i++)
            {
                Vector3[] strand = _authored[i]; Vector3 tip = strand[strand.Length - 1];
                float spin = i * 2f * Mathf.PI / slot.Core.Length - Mathf.Atan2(tip.y, tip.x), reach = Mathf.Max(.001f, new Vector2(tip.x, tip.y).magnitude);
                for (int j = 0; j < slot.Shell[i].Length; j++)
                {
                    int k = Mathf.Min(j / Subdivide, strand.Length - 2);
                    Vector3 p = Vector3.Lerp(strand[k], strand[k + 1], (j - k * Subdivide) / (float)Subdivide), world = contact + frame * p;
                    float theta = new Vector2(p.x, p.y).magnitude / reach * WrapRadians, phi = Mathf.Atan2(p.y, p.x) + spin;
                    Vector3 toward = bounds.center + Vector3.Scale(pole * Mathf.Cos(theta) + (u * Mathf.Cos(phi) + v * Mathf.Sin(phi)) * Mathf.Sin(theta), extents);
                    if (body != null && Surface(body, toward, out Vector3 point, out Vector3 normal)) world = point + normal * ShellLift;
                    slot.Core[i].SetPosition(j, world);
                    slot.Shell[i][j] = slot.Following ? slot.Victim.InverseTransformPoint(world) : world;
                }
            }
        }

        // #1271 S6: where a ray from outside the bounds toward their centre, through `toward`, meets the
        // collider. That ray misses a CharacterController, so one is its own upright capsule.
        internal static bool Surface(Collider body, Vector3 toward, out Vector3 point, out Vector3 normal)
        {
            if (body is CharacterController controller)
            {
                Vector3 scale = controller.transform.lossyScale, axis = controller.transform.TransformPoint(controller.center);
                float radius = controller.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z)), half = Mathf.Max(0f, controller.height * Mathf.Abs(scale.y) * .5f - radius);
                axis.y += Mathf.Clamp(toward.y - axis.y, -half, half);
                normal = (toward - axis).normalized; point = axis + normal * radius;
                return normal != Vector3.zero;
            }
            Vector3 outside = (toward - body.bounds.center).normalized * body.bounds.extents.magnitude * 2f;
            bool hit = body.Raycast(new Ray(body.bounds.center + outside, -outside), out RaycastHit ray, outside.magnitude);
            point = ray.point; normal = ray.normal; return hit;
        }

        private static void Apply(Slot slot, float t)
        {
            float arcs = !slot.ArcsLit || t >= ArcSeconds ? 0f
                : t < ArcHoldSeconds ? 1f : 1f - (t - ArcHoldSeconds) / (ArcSeconds - ArcHoldSeconds);
            bool arcsOn = arcs > 0f;
            if (arcsOn)
            {
                Block.Clear();
                Block.SetColor(EmissiveColorId, slot.Emission * arcs);
                for (int i = 0; i < slot.Core.Length; i++) slot.Core[i].SetPropertyBlock(Block);
            }
            for (int i = 0; i < slot.Core.Length; i++) slot.Core[i].enabled = arcsOn;
            if (slot.Light == null) return;
            float light = t >= LightSeconds ? 0f
                : t < LightHoldSeconds ? 1f : 1f - Mathf.SmoothStep(0f, 1f, (t - LightHoldSeconds) / (LightSeconds - LightHoldSeconds));
            slot.Light.enabled = light > 0f;
            slot.HdrpLight.lightDimmer = light;
        }

        private static void Stop(Slot slot)
        {
            for (int i = 0; i < slot.Core.Length; i++) slot.Core[i].enabled = false;
            if (slot.Light != null) slot.Light.enabled = false;
            slot.Root.SetActive(false);
            slot.Victim = null; slot.NetworkId = 0; slot.Following = false;
            if (slot.Active) { slot.Active = false; _active--; }
        }

        // The victim's own lit slot when it has one (the per-victim budget), else an idle slot, else the oldest.
        private static Slot SlotFor(Transform victimRoot, ulong networkId)
        {
            Slot idle = null, oldest = null;
            foreach (var slot in _slots)
            {
                if (slot.Active && (networkId != 0 ? slot.NetworkId == networkId
                    : victimRoot != null && slot.Victim == victimRoot)) return slot;
                if (!slot.Active) { if (idle == null) idle = slot; continue; }
                if (oldest == null || slot.Started < oldest.Started) oldest = slot;
            }
            return idle ?? oldest;
        }

        private static bool IsLocalPlayer(Transform victimRoot)
            => victimRoot != null && GameNetworkManager.Instance != null && GameNetworkManager.Instance.localPlayerController != null
                && GameNetworkManager.Instance.localPlayerController.transform == victimRoot;

        private static bool EnsurePool()
        {
            if (_host != null) return true;
            _slots = null; _active = 0;
            return BuildPool();
        }

        private static bool BuildPool()
        {
            var bundle = Contracts._Shared.ContractAssetLoader.Bundle;
            var prefab = bundle != null ? bundle.LoadAsset<GameObject>(PrefabPath) : null;
            if (prefab == null) return false;
            var authored = prefab.GetComponentsInChildren<LineRenderer>(true);
            _authored = new Vector3[authored.Length][];
            for (int i = 0; i < authored.Length; i++) { _authored[i] = new Vector3[authored[i].positionCount]; authored[i].GetPositions(_authored[i]); }
            _host = new GameObject(HostName);
            Object.DontDestroyOnLoad(_host);
            _host.AddComponent<BlacksiteBatonVictimFlashDriver>();
            _host.SetActive(false);
            _slots = new Slot[SlotCount];
            for (int i = 0; i < SlotCount; i++)
            {
                var slot = new Slot { Root = Object.Instantiate(prefab, _host.transform, false) };
                slot.Root.name = "Flash" + i;
                slot.Root.SetActive(false);
                slot.Core = slot.Root.GetComponentsInChildren<LineRenderer>(true);
                slot.Shell = new Vector3[slot.Core.Length][];
                for (int a = 0; a < slot.Core.Length; a++)
                { slot.Shell[a] = new Vector3[(_authored[a].Length - 1) * Subdivide + 1]; slot.Core[a].positionCount = slot.Shell[a].Length; slot.Core[a].useWorldSpace = true; }
                slot.Light = slot.Root.GetComponentInChildren<Light>(true);
                slot.HdrpLight = slot.Root.GetComponentInChildren<HDAdditionalLightData>(true);
                slot.Emission = slot.Core[0].sharedMaterial.GetColor(EmissiveColorId);
                _slots[i] = slot;
            }
            _host.SetActive(true);
            return true;
        }
    }

    /// <summary>#1219 F1.3: drives <see cref="BlacksiteBatonVictimFlash"/> after animation, so a flash follows the pose the victim is drawn in.</summary>
    internal sealed class BlacksiteBatonVictimFlashDriver : MonoBehaviour
    {
        private void LateUpdate() => BlacksiteBatonVictimFlash.Tick();
    }
}
