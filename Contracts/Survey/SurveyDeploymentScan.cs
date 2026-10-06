using System.Collections.Generic;
using GameNetcodeStuff;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace Y4NGZCompany.Contracts.Survey
{
    /// <summary>A single local presentation pulse, driven by the replicated deployment sequence.</summary>
    internal sealed class SurveyDeploymentScan : MonoBehaviour
    {
        internal const float Radius = 15f;
        internal const float SweepSeconds = 1.5f;
        internal const float MarkSeconds = 4f;
        private const int MaxMarks = 12;
        private const int Segments = 96;
        private readonly List<ScanNodeProperties> _targets = new List<ScanNodeProperties>();
        private readonly List<GameObject> _marks = new List<GameObject>();
        private readonly Vector3[] _corners = new Vector3[4];
        private readonly Vector3[] _circle = new Vector3[Segments];
        private Vector3 _origin;
        private float _started;
        private LineRenderer _ring;
        private Material _material;

        internal static void Play(Transform beacon)
        {
            if (beacon == null) return;
            var go = new GameObject("Survey deployment scan");
            go.transform.position = beacon.position;
            go.AddComponent<SurveyDeploymentScan>().Begin(beacon.position);
        }

        private void Begin(Vector3 origin)
        {
            _origin = origin;
            _started = Time.time;
            CreateRing();
            var player = GameNetworkManager.Instance?.localPlayerController;
            var hud = HUDManager.Instance;
            if (player == null || player.isPlayerDead || hud == null ||
                Vector3.Distance(player.transform.position, origin) > Radius) return;

            // Discover once, in a small sphere about the beacon, rather than scanning the level
            // every frame or borrowing the player's normal scanner timers and scrap totals.
            var seen = new HashSet<ScanNodeProperties>();
            foreach (var hit in Physics.OverlapSphere(origin, Radius, 1 << 22, QueryTriggerInteraction.Collide))
            {
                var node = hit.GetComponent<ScanNodeProperties>();
                if (node == null || !seen.Add(node) || !Eligible(node, origin)) continue;
                if (node.requiresLineOfSight && Physics.Linecast(origin + Vector3.up * .35f,
                    node.transform.position, 134217984, QueryTriggerInteraction.Ignore)) continue;
                _targets.Add(node);
            }
            _targets.Sort((a, b) => (a.transform.position - origin).sqrMagnitude.CompareTo((b.transform.position - origin).sqrMagnitude));
            if (_targets.Count > MaxMarks) _targets.RemoveRange(MaxMarks, _targets.Count - MaxMarks);
            if (hud.scanElements == null || hud.scanElements.Length == 0 || hud.scanElements[0] == null)
            { _targets.Clear(); return; }
            foreach (var node in _targets)
            {
                var template = hud.scanElements[0];
                var mark = Instantiate(template.gameObject, template.parent, false);
                mark.name = "Survey scan: " + node.headerText;
                var group = mark.GetComponent<CanvasGroup>() ?? mark.AddComponent<CanvasGroup>();
                group.blocksRaycasts = false; group.interactable = false;
                foreach (var graphic in mark.GetComponentsInChildren<Graphic>(true)) graphic.raycastTarget = false;
                var labels = mark.GetComponentsInChildren<TextMeshProUGUI>(true);
                if (labels.Length > 0) labels[0].text = node.headerText;
                if (labels.Length > 1) labels[1].text = node.subText;
                mark.SetActive(false);
                _marks.Add(mark);
            }
            if (hud.scanSFX != null)
                AudioSource.PlayClipAtPoint(hud.scanSFX, origin, .4f);
        }

        internal static bool Eligible(ScanNodeProperties node, Vector3 origin)
        {
            if (node == null || !node.gameObject.activeInHierarchy ||
                (node.nodeType != 1 && node.nodeType != 2) ||
                (node.transform.position - origin).sqrMagnitude > Radius * Radius) return false;
            var item = node.GetComponentInParent<GrabbableObject>();
            if (item != null && (item.isHeld || item.isPocketed)) return false;
            var enemy = node.GetComponentInParent<EnemyAI>();
            return enemy == null || !enemy.isEnemyDead;
        }

        private void Update()
        {
            float elapsed = Time.time - _started;
            if (elapsed >= SweepSeconds + MarkSeconds) { Destroy(gameObject); return; }
            float radius = Radius * Mathf.Clamp01(elapsed / SweepSeconds);
            if (_ring != null)
            {
                _ring.enabled = elapsed < SweepSeconds;
                for (int i = 0; i < Segments; i++)
                {
                    float angle = i * (2f * Mathf.PI / Segments);
                    _circle[i] = _origin + new Vector3(Mathf.Cos(angle) * radius, -.25f, Mathf.Sin(angle) * radius);
                }
                _ring.SetPositions(_circle);
                _ring.widthMultiplier = Mathf.Lerp(.065f, .015f, Mathf.Clamp01(elapsed / SweepSeconds));
            }
            var player = GameNetworkManager.Instance?.localPlayerController;
            var hud = HUDManager.Instance;
            bool canSee = player != null && !player.isPlayerDead && !player.inTerminalMenu &&
                !player.inSpecialInteractAnimation && player.gameplayCamera != null && hud != null &&
                hud.playerScreenRectTransform != null && Vector3.Distance(player.transform.position, _origin) <= Radius;
            if (canSee) hud.playerScreenRectTransform.GetWorldCorners(_corners);
            for (int i = 0; i < _marks.Count; i++)
            {
                var mark = _marks[i]; var node = _targets[i];
                if (mark == null) continue;
                bool visible = canSee && Eligible(node, _origin) && Vector3.Distance(node.transform.position, _origin) <= radius;
                Vector3 viewport = visible ? player.gameplayCamera.WorldToViewportPoint(node.transform.position) : Vector3.zero;
                visible &= viewport.z > 0 && viewport.x >= 0 && viewport.x <= 1 && viewport.y >= 0 && viewport.y <= 1;
                if (mark.activeSelf != visible)
                {
                    mark.SetActive(visible);
                    if (visible) mark.GetComponent<Animator>()?.SetInteger("colorNumber", node.nodeType);
                }
                if (!visible) continue;
                float x = IngamePlayerSettings.Instance != null && IngamePlayerSettings.Instance.flipCamera ? 1 - viewport.x : viewport.x;
                mark.transform.position = Vector3.Lerp(Vector3.Lerp(_corners[0], _corners[3], x),
                    Vector3.Lerp(_corners[1], _corners[2], x), viewport.y);
                mark.GetComponent<CanvasGroup>().alpha = Mathf.Clamp01((SweepSeconds + MarkSeconds - elapsed) / .75f);
            }
        }

        private void CreateRing()
        {
            Shader shader = Shader.Find("HDRP/Unlit");
            if (shader == null) return;
            _material = new Material(shader);
            _material.SetColor("_UnlitColor", new Color(.12f, .7f, .3f, .6f));
            _material.SetColor("_EmissiveColor", new Color(.12f, .7f, .3f));
            _material.SetFloat("_SurfaceType", 1); _material.SetFloat("_BlendMode", 1);
            _material.SetFloat("_ZWrite", 0);
            _material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            _material.SetFloat("_DstBlend", (float)BlendMode.One);
            _material.SetFloat("_AlphaSrcBlend", (float)BlendMode.SrcAlpha);
            _material.SetFloat("_AlphaDstBlend", (float)BlendMode.One);
            _material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); _material.EnableKeyword("_BLENDMODE_ADD");
            _material.renderQueue = (int)RenderQueue.Transparent;
            _ring = gameObject.AddComponent<LineRenderer>();
            _ring.sharedMaterial = _material; _ring.useWorldSpace = true; _ring.loop = true;
            _ring.positionCount = Segments; _ring.shadowCastingMode = ShadowCastingMode.Off;
            _ring.receiveShadows = false; _ring.widthMultiplier = .065f;
        }

        private void OnDestroy()
        {
            foreach (var mark in _marks) if (mark != null) Destroy(mark);
            if (_material != null) Destroy(_material);
        }
    }
}
