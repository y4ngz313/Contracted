using System;
using System.Collections;
using System.Collections.Generic;
using GameNetcodeStuff;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts._Shared;
namespace Y4NGZCompany.Contracts.Defuse
{
    public enum DefuseKeypadDisplayState
    {
        Normal,
        Warning,
        Success,
        Failed
    }

    public class DefuseInteractor : MonoBehaviour
    {
        private const int InteractLayer = 9;
        private const float WireHoldSeconds = 0.85f;
        private const float RefreshInterval = 0.2f;
        private const float ButtonPressDistance = 0.0025f;
        private const float ButtonPressDuration = 0.1f;
        private const float ButtonReturnDuration = 0.1f;
        internal const int KeypadClearButton = -1;
        internal const int KeypadEnterButton = -2;

        public DefuseBombController Bomb;
        public float InteractRadius = 2.8f;

        private readonly List<DefuseWireInteractable> _wireInteractions = new List<DefuseWireInteractable>();
        private readonly List<DefuseKeypadButtonInteractable> _keypadInteractions = new List<DefuseKeypadButtonInteractable>();
        private readonly Dictionary<Transform, Coroutine> _buttonAnimations = new Dictionary<Transform, Coroutine>();
        private bool _built;
        private Transform[] _cachedTransforms;
        private float _nextRefreshTime;
        private TMP_Text _displayText;
        private Renderer _displayRenderer;
        private AudioSource _audioSource;
        private AudioClip _clickClip;

        private static readonly Color DisplayNormalColor = new Color(0.28f, 1f, 0.32f, 1f);
        private static readonly Color DisplayWarningColor = new Color(1f, 0.82f, 0.18f, 1f);
        private static readonly Color DisplaySuccessColor = new Color(0.1f, 0.95f, 0.36f, 1f);
        private static readonly Color DisplayFailedColor = new Color(1f, 0.05f, 0.04f, 1f);

        protected virtual void Awake()
        {
            ResolveBomb();
            EnsureBuilt();
        }

        protected virtual void Start()
        {
            ResolveBomb();
            EnsureBuilt();
            RefreshInteractionState(force: true);
        }

        protected virtual void OnEnable()
        {
            RefreshInteractionState(force: true);
        }

        protected virtual void Update()
        {
            if (Time.time < _nextRefreshTime)
                return;

            _nextRefreshTime = Time.time + RefreshInterval;
            RefreshInteractionState(force: false);
        }

        internal void EnsureBuilt()
        {
            if (_built)
                return;

            ResolveBomb();
            if (Bomb == null)
                return;

            BuildWireInteractions();
            DisableLegacyKeypadInteractions();
            _built = true;
        }

        internal void RefreshInteractionState(bool force)
        {
            ResolveBomb();
            if (Bomb == null)
                return;

            EnsureBuilt();

            for (int i = 0; i < _wireInteractions.Count; i++)
                _wireInteractions[i]?.RefreshState();
        }

        internal void RequestWireCut(int wireSlot, PlayerControllerB player)
        {
            ResolveBomb();
            if (Bomb == null || !Bomb.IsWireSlotInteractable(wireSlot))
                return;

            Bomb.RequestCutWireServerRpc(wireSlot);
        }

        internal void RequestKeypadButton(int button, PlayerControllerB player, Transform visual)
        {
            ResolveBomb();
            if (Bomb == null)
                return;

            PlayClick();
            AnimateButton(visual);
            Bomb.PressKeypadButtonLocally(button);
        }

        private void ResolveBomb()
        {
            if (Bomb == null)
                Bomb = GetComponent<DefuseBombController>() ?? GetComponentInParent<DefuseBombController>();

            InteractRadius = ContractsBootstrap.DefuseBalance?.GetEffective().InteractDistanceMeters ?? InteractRadius;
        }

        private void BuildWireInteractions()
        {
            _wireInteractions.Clear();
            for (int slot = 0; slot < DefuseRules.MaxWireSlots; slot++)
            {
                GameObject triggerObject = FindOrCreateChild($"DD_WireInteract_{slot + 1:00}");
                if (TryCalculateWireSegmentLocal(slot + 1, out Vector3 segmentCenter, out Quaternion segmentRotation, out Vector3 segmentSize))
                {
                    ConfigureBoxTrigger(triggerObject, segmentCenter, segmentRotation, segmentSize);
                }
                else
                {
                    Bounds bounds = TryCalculateNamedBoundsLocal($"DD_WireSlot_{slot + 1:00}_Intact", out Bounds slotBounds)
                        ? slotBounds
                        : new Bounds(new Vector3(-0.12f + slot * 0.06f, 0.04f, 0f), new Vector3(0.05f, 0.05f, 0.12f));

                    ConfigureBoxTrigger(triggerObject, bounds.center, ExpandSize(bounds.size, new Vector3(0.02f, 0.02f, 0.02f), new Vector3(0.035f, 0.035f, 0.06f)));
                }

                DefuseWireInteractable interactable = triggerObject.GetComponent<DefuseWireInteractable>();
                if (interactable == null)
                    interactable = triggerObject.AddComponent<DefuseWireInteractable>();
                interactable.Owner = this;
                interactable.WireSlot = slot;
                interactable.ConfigureTrigger();
                _wireInteractions.Add(interactable);
            }
        }

        private void BuildKeypadInteractions()
        {
            _keypadInteractions.Clear();
            int[] buttons =
            {
                1, 2, 3,
                4, 5, 6,
                7, 8, 9,
                KeypadClearButton, 0, KeypadEnterButton
            };

            for (int i = 0; i < buttons.Length; i++)
            {
                int button = buttons[i];
                string suffix = button >= 0 ? button.ToString() : button == KeypadClearButton ? "Clear" : "Enter";
                Transform visual = FindButtonVisual(button);
                bool fallback = visual == null;
                if (fallback)
                    visual = CreateFallbackButtonAnchor(button);

                GameObject triggerObject = fallback
                    ? FindOrCreateChild($"DD_KeypadButtonInteract_{suffix}")
                    : FindOrCreateTriggerChild(visual, $"DD_KeypadButtonInteract_{suffix}");

                if (fallback)
                {
                    Vector3 position = visual != null ? visual.localPosition : EstimateKeypadButtonPositionLocal(button);
                    ConfigureBoxTrigger(triggerObject, position, EstimateFallbackButtonSize(button));
                }
                else
                {
                    ConfigureButtonTriggerCollider(triggerObject, visual, button);
                }

                DefuseKeypadButtonInteractable interactable = triggerObject.GetComponent<DefuseKeypadButtonInteractable>();
                if (interactable == null)
                    interactable = triggerObject.AddComponent<DefuseKeypadButtonInteractable>();
                interactable.Owner = this;
                interactable.Button = button;
                interactable.Visual = fallback ? null : visual;
                interactable.ConfigureTrigger();
                _keypadInteractions.Add(interactable);
            }
        }

        private void DisableLegacyKeypadInteractions()
        {
            _keypadInteractions.Clear();

            DefuseKeypadButtonInteractable[] legacyButtons = GetComponentsInChildren<DefuseKeypadButtonInteractable>(true);
            for (int i = 0; i < legacyButtons.Length; i++)
            {
                DefuseKeypadButtonInteractable button = legacyButtons[i];
                if (button == null)
                    continue;

                DisableLegacyInteractTrigger(button.gameObject);
                button.enabled = false;
            }

            Transform[] transforms = GetCachedTransforms();
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform child = transforms[i];
                if (child == null)
                    continue;

                string name = child.name ?? string.Empty;
                if (name.StartsWith("DD_KeypadButtonInteract_", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("DD_KeypadFallbackButton_", StringComparison.OrdinalIgnoreCase))
                {
                    DisableLegacyInteractTrigger(child.gameObject);
                    child.gameObject.SetActive(false);
                }
            }
        }

        private static void DisableLegacyInteractTrigger(GameObject target)
        {
            if (target == null)
                return;

            InteractTrigger trigger = target.GetComponent<InteractTrigger>();
            if (trigger != null)
            {
                trigger.interactable = false;
                trigger.enabled = false;
            }

            Collider collider = target.GetComponent<Collider>();
            if (collider != null)
                collider.enabled = false;
        }

        private bool TryCalculateWireSegmentLocal(int slotNumber, out Vector3 center, out Quaternion rotation, out Vector3 size)
        {
            center = Vector3.zero;
            rotation = Quaternion.identity;
            size = Vector3.zero;

            string slot = slotNumber.ToString("00");
            Transform start = FindVisualTransform($"DD_WireSlot_{slot}_Anchor_Start")
                              ?? FindVisualTransform($"DD_KeypadTerminalBlock_Socket_{slot}");
            Transform end = FindVisualTransform($"DD_WireSlot_{slot}_Anchor_End")
                            ?? FindVisualTransform($"DD_DetonatorBus_Socket_{slot}");
            if (start == null || end == null)
                return false;

            Vector3 startLocal = transform.InverseTransformPoint(start.position);
            Vector3 endLocal = transform.InverseTransformPoint(end.position);
            Vector3 delta = endLocal - startLocal;
            float length = delta.magnitude;
            if (length < 0.025f)
                return false;

            Vector3 direction = delta / length;
            Vector3 up = Mathf.Abs(Vector3.Dot(direction, Vector3.up)) > 0.94f ? Vector3.forward : Vector3.up;
            center = (startLocal + endLocal) * 0.5f;
            rotation = Quaternion.LookRotation(direction, up);
            size = new Vector3(0.045f, 0.055f, length + 0.06f);
            return true;
        }

        private Vector3 EstimateKeypadCenterLocal()
        {
            Transform display = FindVisualTransform("DD_DisplayOverlay_Glass")
                                ?? FindVisualTransform("DD_KeypadOutputPort")
                                ?? FindVisualTransform("DD_KeypadTerminalBlock");
            if (display != null)
            {
                Vector3 displayLocal = transform.InverseTransformPoint(display.position);
                return displayLocal + new Vector3(-0.07f, 0.025f, -0.09f);
            }

            if (TryCalculateNamedBoundsLocal("Clean ASGS Briefcase Base", out Bounds baseBounds))
            {
                Vector3 min = baseBounds.min;
                Vector3 size = baseBounds.size;
                return new Vector3(min.x + size.x * 0.43f, min.y + size.y * 0.18f, min.z + size.z * 0.34f);
            }

            return new Vector3(-0.12f, 0.05f, -0.12f);
        }

        internal void SetKeypadDisplayText(string text, DefuseKeypadDisplayState state)
        {
            ResolveDisplay();

            if (_displayText != null)
                _displayText.text = text ?? string.Empty;

            Color color = GetDisplayColor(state);
            if (_displayText != null)
                _displayText.color = color;

            if (_displayRenderer == null)
                return;

            Material material = _displayRenderer.material;
            if (material == null)
                return;

            Color emission = color * 2.25f;
            if (material.HasProperty("_EmissionColor"))
                material.SetColor("_EmissionColor", emission);
            if (material.HasProperty("_EmissiveColor"))
                material.SetColor("_EmissiveColor", emission);
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color"))
                material.SetColor("_Color", color);
        }

        private void ResolveDisplay()
        {
            if (_displayText == null)
                _displayText = FindDisplayText();

            if (_displayRenderer == null)
                _displayRenderer = FindDisplayRenderer();
        }

        private TMP_Text FindDisplayText()
        {
            TMP_Text[] texts = GetComponentsInChildren<TMP_Text>(true);
            for (int i = 0; i < texts.Length; i++)
            {
                TMP_Text text = texts[i];
                if (text == null)
                    continue;

                string path = GetTransformPath(text.transform);
                if (path.IndexOf("DD_KeypadDisplayText", StringComparison.OrdinalIgnoreCase) >= 0)
                    return text;
            }

            for (int i = 0; i < texts.Length; i++)
            {
                TMP_Text text = texts[i];
                if (text == null)
                    continue;

                string path = GetTransformPath(text.transform);
                bool displayPath = path.IndexOf("display", StringComparison.OrdinalIgnoreCase) >= 0
                                   || path.IndexOf("screen", StringComparison.OrdinalIgnoreCase) >= 0;
                bool stickerPath = path.IndexOf("DD_Marking", StringComparison.OrdinalIgnoreCase) >= 0
                                   || path.IndexOf("Sticker", StringComparison.OrdinalIgnoreCase) >= 0
                                   || path.IndexOf("Marking", StringComparison.OrdinalIgnoreCase) >= 0;
                if (displayPath && !stickerPath)
                    return text;
            }

            return null;
        }

        private Renderer FindDisplayRenderer()
        {
            Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
            Renderer fallback = null;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                    continue;

                string path = GetTransformPath(renderer.transform);
                if (path.IndexOf("Clean ASGS Briefcase Base", StringComparison.OrdinalIgnoreCase) >= 0
                    || path.IndexOf("Briefcase Base", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                if (path.IndexOf("DD_DisplayState_", StringComparison.OrdinalIgnoreCase) >= 0
                    || path.IndexOf("DD_Marking", StringComparison.OrdinalIgnoreCase) >= 0
                    || path.IndexOf("Sticker", StringComparison.OrdinalIgnoreCase) >= 0
                    || path.IndexOf("Interact", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                string materialName = GetRendererMaterialNames(renderer);
                bool displayPath = path.IndexOf("display", StringComparison.OrdinalIgnoreCase) >= 0
                                   || path.IndexOf("screen", StringComparison.OrdinalIgnoreCase) >= 0
                                   || path.IndexOf("num_pad", StringComparison.OrdinalIgnoreCase) >= 0
                                   || path.IndexOf("numpad", StringComparison.OrdinalIgnoreCase) >= 0;
                bool displayMaterial = materialName.IndexOf("display", StringComparison.OrdinalIgnoreCase) >= 0
                                       || materialName.IndexOf("screen", StringComparison.OrdinalIgnoreCase) >= 0
                                       || materialName.IndexOf("num_pad", StringComparison.OrdinalIgnoreCase) >= 0;

                if (!displayPath && !displayMaterial)
                    continue;

                if (path.IndexOf("keypad", StringComparison.OrdinalIgnoreCase) >= 0
                    || path.IndexOf("Briefcase_IED_Keypad", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return renderer;
                }

                fallback = fallback ?? renderer;
            }

            return fallback;
        }

        private static string GetRendererMaterialNames(Renderer renderer)
        {
            if (renderer == null)
                return string.Empty;

            Material[] materials = renderer.sharedMaterials;
            if (materials == null || materials.Length == 0)
                return renderer.sharedMaterial != null ? renderer.sharedMaterial.name ?? string.Empty : string.Empty;

            List<string> names = new List<string>(materials.Length);
            for (int i = 0; i < materials.Length; i++)
            {
                Material material = materials[i];
                if (material != null && !string.IsNullOrWhiteSpace(material.name))
                    names.Add(material.name);
            }

            return string.Join(" ", names.ToArray());
        }

        private static TMP_Text CreateDisplayText(Renderer displayRenderer)
        {
            if (displayRenderer == null)
                return null;

            GameObject textObject = new GameObject("DD_KeypadDisplayText");
            textObject.transform.SetParent(displayRenderer.transform, false);

            Bounds bounds = CalculateRendererLocalBounds(displayRenderer.transform, displayRenderer);
            int normalAxis = SmallestAxis(bounds.size);
            Vector3 normal = AxisVector(normalAxis);
            Vector3 extents = bounds.extents;
            float normalExtent = normalAxis == 0 ? extents.x : normalAxis == 1 ? extents.y : extents.z;
            textObject.transform.localPosition = bounds.center + normal * (normalExtent + 0.001f);
            textObject.transform.localRotation = Quaternion.FromToRotation(Vector3.forward, normal);
            textObject.transform.localScale = Vector3.one * 0.018f;

            TextMeshPro text = textObject.AddComponent<TextMeshPro>();
            text.text = "ARMED";
            text.alignment = TextAlignmentOptions.Center;
            text.enableWordWrapping = false;
            text.enableAutoSizing = true;
            text.fontSizeMin = 0.18f;
            text.fontSizeMax = 0.85f;
            text.color = DisplayNormalColor;
            text.rectTransform.sizeDelta = new Vector2(5.4f, 1.3f);
            return text;
        }

        private Transform FindButtonVisual(int button)
        {
            Transform[] transforms = GetCachedTransforms();
            string[] exactNames = GetButtonExactNames(button);

            for (int i = 0; i < transforms.Length; i++)
            {
                Transform candidate = transforms[i];
                if (!IsUsableButtonVisual(candidate))
                    continue;

                string name = candidate.name ?? string.Empty;
                for (int n = 0; n < exactNames.Length; n++)
                {
                    if (string.Equals(name, exactNames[n], StringComparison.OrdinalIgnoreCase))
                        return candidate;
                }
            }

            for (int i = 0; i < transforms.Length; i++)
            {
                Transform candidate = transforms[i];
                if (!IsUsableButtonVisual(candidate))
                    continue;

                string name = candidate.name ?? string.Empty;
                if (button == KeypadClearButton)
                {
                    if (IsClearButtonName(name))
                        return candidate;
                    continue;
                }

                if (button == KeypadEnterButton)
                {
                    if (IsEnterButtonName(name))
                        return candidate;
                    continue;
                }

                if (IsDigitButtonName(name, button))
                    return candidate;
            }

            return null;
        }

        private static bool IsUsableButtonVisual(Transform candidate)
        {
            if (candidate == null)
                return false;

            string path = GetTransformPath(candidate);
            if (path.IndexOf("Interact", StringComparison.OrdinalIgnoreCase) >= 0
                || path.IndexOf("Trigger", StringComparison.OrdinalIgnoreCase) >= 0
                || path.IndexOf("DD_KeypadFallback", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            return candidate.GetComponent<Renderer>() != null
                   || candidate.GetComponentsInChildren<Renderer>(true).Length > 0;
        }

        private static string[] GetButtonExactNames(int button)
        {
            if (button == KeypadClearButton)
            {
                return new[]
                {
                    "bttnClear", "buttonClear", "btnClear", "keyClear", "Clear", "CLR", "C"
                };
            }

            if (button == KeypadEnterButton)
            {
                return new[]
                {
                    "bttnEnter", "buttonEnter", "btnEnter", "keyEnter", "Enter", "Submit", "Confirm", "Return", "OK"
                };
            }

            return new[]
            {
                $"bttn{button}",
                $"button{button}",
                $"btn{button}",
                $"key{button}",
                $"num{button}",
                $"number{button}",
                $"digit{button}",
                button.ToString()
            };
        }

        private static bool IsClearButtonName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            string lower = name.ToLowerInvariant();
            return lower.Contains("clear")
                   || lower.Contains("clr")
                   || (lower.EndsWith("_c") && (lower.Contains("button") || lower.Contains("key") || lower.Contains("btn")));
        }

        private static bool IsEnterButtonName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            string lower = name.ToLowerInvariant();
            return lower.Contains("enter")
                   || lower.Contains("submit")
                   || lower.Contains("confirm")
                   || lower.Contains("return")
                   || lower == "ok"
                   || lower.EndsWith("_ok")
                   || lower.EndsWith("-ok");
        }

        private static bool IsDigitButtonName(string name, int digit)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            string digitText = Mathf.Clamp(digit, 0, 9).ToString();
            string lower = name.ToLowerInvariant();
            return lower.Contains(digitText)
                   && (lower.Contains("bttn")
                       || lower.Contains("button")
                       || lower.Contains("btn")
                       || lower.Contains("key")
                       || lower.Contains("num")
                       || lower.Contains("digit"));
        }

        private Transform CreateFallbackButtonAnchor(int button)
        {
            string suffix = button >= 0 ? button.ToString() : button == KeypadClearButton ? "Clear" : "Enter";
            GameObject anchor = FindOrCreateChild($"DD_KeypadFallbackButton_{suffix}");
            anchor.transform.localPosition = EstimateKeypadButtonPositionLocal(button);
            anchor.transform.localRotation = Quaternion.identity;
            anchor.transform.localScale = Vector3.one;
            return anchor.transform;
        }

        private Vector3 EstimateKeypadButtonPositionLocal(int button)
        {
            if (!TryCalculateKeypadBoundsLocal(out Bounds bounds))
                return EstimateKeypadCenterLocal();

            int row;
            int col;
            if (button == KeypadClearButton)
            {
                row = 0;
                col = -1;
            }
            else if (button == KeypadEnterButton)
            {
                row = 3;
                col = -1;
            }
            else if (button == 0)
            {
                row = 3;
                col = 1;
            }
            else
            {
                int index = Mathf.Clamp(button, 1, 9) - 1;
                row = index / 3;
                col = index % 3;
            }

            float xSpacing = Mathf.Max(0.035f, bounds.size.x * 0.19f);
            float zSpacing = Mathf.Max(0.035f, bounds.size.z * 0.16f);
            float x = bounds.center.x + (col - 1) * xSpacing;
            float z = bounds.center.z + bounds.size.z * 0.12f - row * zSpacing;
            float y = bounds.max.y + 0.006f;
            return new Vector3(x, y, z);
        }

        private Vector3 EstimateFallbackButtonSize(int button)
        {
            Vector3 size = new Vector3(0.052f, 0.025f, 0.048f);
            if (button == KeypadEnterButton || button == KeypadClearButton)
                size.x *= 1.35f;
            return size;
        }

        private bool TryCalculateKeypadBoundsLocal(out Bounds bounds)
        {
            if (TryCalculateNamedBoundsLocal("Briefcase_IED_Keypad", out bounds)
                || TryCalculateNamedBoundsLocal("Brifecase_Keypad", out bounds)
                || TryCalculateNamedBoundsLocal("Num_Pad", out bounds)
                || TryCalculateNamedBoundsLocal("NumPad", out bounds))
            {
                return bounds.size.sqrMagnitude > 0.0001f;
            }

            return false;
        }

        private static GameObject FindOrCreateTriggerChild(Transform visual, string name)
        {
            Transform existing = visual != null ? visual.Find(name) : null;
            GameObject child = existing != null ? existing.gameObject : new GameObject(name);
            if (visual != null)
                child.transform.SetParent(visual, false);
            child.transform.localPosition = Vector3.zero;
            child.transform.localRotation = Quaternion.identity;
            child.transform.localScale = Vector3.one;
            return child;
        }

        private static void ConfigureButtonTriggerCollider(GameObject triggerObject, Transform visual, int button)
        {
            triggerObject.layer = ResolveLayer("InteractableObject", InteractLayer);
            SetTagSafe(triggerObject, "InteractTrigger");
            HideTriggerVisuals(triggerObject);

            BoxCollider collider = triggerObject.GetComponent<BoxCollider>();
            if (collider == null)
                collider = triggerObject.AddComponent<BoxCollider>();
            collider.isTrigger = true;

            Bounds bounds = CalculateButtonColliderBounds(visual);
            if (button == KeypadEnterButton || button == KeypadClearButton)
            {
                bounds.size = new Vector3(
                    ExpandLocalColliderAxis(bounds.size.x, 1.65f, 0.002f),
                    ExpandLocalColliderAxis(bounds.size.y, 1.65f, 0.002f),
                    ExpandLocalColliderAxis(bounds.size.z, 2.1f, 0.002f));
            }

            collider.center = bounds.center;
            collider.size = bounds.size;
        }

        private static Bounds CalculateButtonColliderBounds(Transform visual)
        {
            if (visual == null)
                return new Bounds(Vector3.zero, new Vector3(0.02f, 0.02f, 0.02f));

            Renderer[] renderers = visual.GetComponentsInChildren<Renderer>(true);
            bool initialized = false;
            Bounds bounds = default;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                    continue;

                Mesh mesh = GetRendererMesh(renderer);
                if (mesh == null)
                    continue;

                Bounds meshBounds = mesh.bounds;
                Vector3 min = meshBounds.min;
                Vector3 max = meshBounds.max;
                Vector3[] corners =
                {
                    new Vector3(min.x, min.y, min.z),
                    new Vector3(max.x, min.y, min.z),
                    new Vector3(min.x, max.y, min.z),
                    new Vector3(max.x, max.y, min.z),
                    new Vector3(min.x, min.y, max.z),
                    new Vector3(max.x, min.y, max.z),
                    new Vector3(min.x, max.y, max.z),
                    new Vector3(max.x, max.y, max.z),
                };

                for (int corner = 0; corner < corners.Length; corner++)
                {
                    Vector3 local = visual.InverseTransformPoint(renderer.transform.TransformPoint(corners[corner]));
                    if (!initialized)
                    {
                        bounds = new Bounds(local, Vector3.zero);
                        initialized = true;
                    }
                    else
                    {
                        bounds.Encapsulate(local);
                    }
                }
            }

            if (!initialized)
                return new Bounds(Vector3.zero, new Vector3(0.02f, 0.02f, 0.02f));

            bounds.size = new Vector3(
                ExpandLocalColliderAxis(bounds.size.x, 1.45f, 0.001f),
                ExpandLocalColliderAxis(bounds.size.y, 1.45f, 0.001f),
                ExpandLocalColliderAxis(bounds.size.z, 1.85f, 0.0015f));
            return bounds;
        }

        private static Bounds CalculateRendererLocalBounds(Transform localRoot, Renderer renderer)
        {
            if (localRoot == null || renderer == null)
                return new Bounds(Vector3.zero, Vector3.one * 0.01f);

            Mesh mesh = GetRendererMesh(renderer);
            if (mesh == null)
                return new Bounds(Vector3.zero, Vector3.one * 0.01f);

            Bounds meshBounds = mesh.bounds;
            Vector3 min = meshBounds.min;
            Vector3 max = meshBounds.max;
            Vector3[] corners =
            {
                new Vector3(min.x, min.y, min.z),
                new Vector3(max.x, min.y, min.z),
                new Vector3(min.x, max.y, min.z),
                new Vector3(max.x, max.y, min.z),
                new Vector3(min.x, min.y, max.z),
                new Vector3(max.x, min.y, max.z),
                new Vector3(min.x, max.y, max.z),
                new Vector3(max.x, max.y, max.z),
            };

            Bounds bounds = new Bounds(localRoot.InverseTransformPoint(renderer.transform.TransformPoint(corners[0])), Vector3.zero);
            for (int i = 1; i < corners.Length; i++)
                bounds.Encapsulate(localRoot.InverseTransformPoint(renderer.transform.TransformPoint(corners[i])));
            return bounds;
        }

        private static Mesh GetRendererMesh(Renderer renderer)
        {
            if (renderer == null)
                return null;

            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            if (filter != null && filter.sharedMesh != null)
                return filter.sharedMesh;

            SkinnedMeshRenderer skinned = renderer as SkinnedMeshRenderer;
            return skinned != null ? skinned.sharedMesh : null;
        }

        private static float ExpandLocalColliderAxis(float size, float multiplier, float padding)
        {
            if (size <= 0.00001f)
                return padding;

            return Mathf.Max(size * multiplier, size + padding);
        }

        private void ResolveAudio()
        {
            if (_audioSource == null)
            {
                _audioSource = GetComponentInChildren<AudioSource>(true);
                if (_audioSource == null)
                    _audioSource = gameObject.AddComponent<AudioSource>();
                _audioSource.playOnAwake = false;
                _audioSource.spatialBlend = 1f;
                _audioSource.rolloffMode = AudioRolloffMode.Linear;
                _audioSource.volume = 0.65f;
                _audioSource.minDistance = 1.3f;
                _audioSource.maxDistance = 10f;
            }

            _clickClip = _clickClip ?? ContractAssetLoader.GetBundledAudioClip("sfx_keypadClick", "sfx_keypadClick.wav");
        }

        private void PlayClick()
        {
            ResolveAudio();
            if (_audioSource != null && _clickClip != null)
                _audioSource.PlayOneShot(_clickClip, 0.6f);
        }

        private void AnimateButton(Transform visual)
        {
            if (visual == null)
                return;

            if (_buttonAnimations.TryGetValue(visual, out Coroutine running) && running != null)
                StopCoroutine(running);

            _buttonAnimations[visual] = StartCoroutine(AnimateButtonRoutine(visual));
        }

        private IEnumerator AnimateButtonRoutine(Transform visual)
        {
            Vector3 start = visual.localPosition;
            Vector3 pressed = start + new Vector3(0f, 0f, ButtonPressDistance);
            float elapsed = 0f;

            while (elapsed < ButtonPressDuration)
            {
                elapsed += Time.deltaTime;
                visual.localPosition = Vector3.Lerp(start, pressed, Mathf.Clamp01(elapsed / ButtonPressDuration));
                yield return null;
            }

            visual.localPosition = pressed;
            elapsed = 0f;
            while (elapsed < ButtonReturnDuration)
            {
                elapsed += Time.deltaTime;
                visual.localPosition = Vector3.Lerp(pressed, start, Mathf.Clamp01(elapsed / ButtonReturnDuration));
                yield return null;
            }

            visual.localPosition = start;
            _buttonAnimations.Remove(visual);
        }

        private GameObject FindOrCreateChild(string childName)
        {
            Transform existing = transform.Find(childName);
            if (existing != null)
                return existing.gameObject;

            GameObject child = new GameObject(childName);
            child.transform.SetParent(transform, false);
            // A new child invalidates the shared hierarchy snapshot (#716, WP-A4).
            _cachedTransforms = null;
            return child;
        }

        /// <summary>
        /// One shared <c>GetComponentsInChildren&lt;Transform&gt;</c> snapshot for the build path.
        /// #716 (WP-A4): the wire-segment bounds alone walked the whole bomb rig once per slot,
        /// so a bomb landing cost well over a dozen full hierarchy passes on a single frame.
        /// Taken with <c>includeInactive</c>, so deactivating a child does not invalidate it;
        /// <see cref="FindOrCreateChild"/> drops it when it adds one.
        /// </summary>
        private Transform[] GetCachedTransforms()
        {
            if (_cachedTransforms == null || _cachedTransforms.Length == 0 || _cachedTransforms[0] == null)
                _cachedTransforms = GetComponentsInChildren<Transform>(true);
            return _cachedTransforms;
        }

        private void ConfigureBoxTrigger(GameObject triggerObject, Vector3 localPosition, Vector3 size)
        {
            ConfigureBoxTrigger(triggerObject, localPosition, Quaternion.identity, size);
        }

        private void ConfigureBoxTrigger(GameObject triggerObject, Vector3 localPosition, Quaternion localRotation, Vector3 size)
        {
            triggerObject.transform.localPosition = localPosition;
            triggerObject.transform.localRotation = localRotation;
            triggerObject.transform.localScale = Vector3.one;
            triggerObject.layer = InteractLayer;
            SetTagSafe(triggerObject, "InteractTrigger");
            HideTriggerVisuals(triggerObject);

            BoxCollider collider = triggerObject.GetComponent<BoxCollider>();
            if (collider == null)
                collider = triggerObject.AddComponent<BoxCollider>();
            collider.center = Vector3.zero;
            collider.size = size;
            collider.isTrigger = true;
        }

        private bool TryCalculateNamedBoundsLocal(string nameToken, out Bounds bounds)
        {
            bounds = default;
            bool hasBounds = false;
            Transform[] children = GetCachedTransforms();
            for (int i = 0; i < children.Length; i++)
            {
                Transform child = children[i];
                if (child == null || child == transform)
                    continue;
                if (child.name.IndexOf(nameToken, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                Renderer[] renderers = child.GetComponentsInChildren<Renderer>(true);
                for (int r = 0; r < renderers.Length; r++)
                {
                    Renderer renderer = renderers[r];
                    if (renderer == null)
                        continue;

                    Bounds local = ConvertWorldBoundsToLocal(renderer.bounds);
                    bounds = hasBounds ? Encapsulate(bounds, local) : local;
                    hasBounds = true;
                }
            }

            return hasBounds;
        }

        private Transform FindVisualTransform(string nameToken)
        {
            Transform[] children = GetCachedTransforms();
            for (int i = 0; i < children.Length; i++)
            {
                Transform child = children[i];
                if (child != null && child.name.IndexOf(nameToken, StringComparison.OrdinalIgnoreCase) >= 0)
                    return child;
            }

            return null;
        }

        private Bounds ConvertWorldBoundsToLocal(Bounds worldBounds)
        {
            Vector3 min = worldBounds.min;
            Vector3 max = worldBounds.max;
            Vector3[] corners =
            {
                new Vector3(min.x, min.y, min.z),
                new Vector3(max.x, min.y, min.z),
                new Vector3(min.x, max.y, min.z),
                new Vector3(max.x, max.y, min.z),
                new Vector3(min.x, min.y, max.z),
                new Vector3(max.x, min.y, max.z),
                new Vector3(min.x, max.y, max.z),
                new Vector3(max.x, max.y, max.z),
            };

            Vector3 localMin = transform.InverseTransformPoint(corners[0]);
            Vector3 localMax = localMin;
            for (int i = 1; i < corners.Length; i++)
            {
                Vector3 local = transform.InverseTransformPoint(corners[i]);
                localMin = Vector3.Min(localMin, local);
                localMax = Vector3.Max(localMax, local);
            }

            return new Bounds((localMin + localMax) * 0.5f, localMax - localMin);
        }

        private static Bounds Encapsulate(Bounds a, Bounds b)
        {
            a.Encapsulate(b.min);
            a.Encapsulate(b.max);
            return a;
        }

        private static Vector3 ExpandSize(Vector3 size, Vector3 padding, Vector3 minimum)
        {
            return new Vector3(
                Mathf.Max(minimum.x, size.x + padding.x),
                Mathf.Max(minimum.y, size.y + padding.y),
                Mathf.Max(minimum.z, size.z + padding.z));
        }

        private static Color GetDisplayColor(DefuseKeypadDisplayState state)
        {
            switch (state)
            {
                case DefuseKeypadDisplayState.Warning:
                    return DisplayWarningColor;
                case DefuseKeypadDisplayState.Success:
                    return DisplaySuccessColor;
                case DefuseKeypadDisplayState.Failed:
                    return DisplayFailedColor;
                default:
                    return DisplayNormalColor;
            }
        }

        private static int SmallestAxis(Vector3 size)
        {
            if (size.x <= size.y && size.x <= size.z)
                return 0;
            if (size.y <= size.x && size.y <= size.z)
                return 1;
            return 2;
        }

        private static Vector3 AxisVector(int axis)
        {
            switch (axis)
            {
                case 0:
                    return Vector3.right;
                case 1:
                    return Vector3.up;
                default:
                    return Vector3.forward;
            }
        }

        private static int ResolveLayer(string name, int fallback)
        {
            int layer = LayerMask.NameToLayer(name);
            return layer >= 0 ? layer : fallback;
        }

        private static string GetTransformPath(Transform transform)
        {
            if (transform == null)
                return string.Empty;

            string path = transform.name;
            Transform current = transform.parent;
            while (current != null)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }

            return path;
        }

        private static void SetTagSafe(GameObject obj, string tag)
        {
            try
            {
                obj.tag = tag;
            }
            catch
            {
                obj.tag = "Untagged";
            }
        }

        internal static void EnsureInteractTriggerEvents(InteractTrigger trigger)
        {
            if (trigger.onInteract == null)
                trigger.onInteract = new InteractEvent();
            if (trigger.onInteractEarly == null)
                trigger.onInteractEarly = new InteractEvent();
            if (trigger.onInteractEarlyOtherClients == null)
                trigger.onInteractEarlyOtherClients = new InteractEvent();
            if (trigger.onStopInteract == null)
                trigger.onStopInteract = new InteractEvent();
            if (trigger.onCancelAnimation == null)
                trigger.onCancelAnimation = new InteractEvent();
            if (trigger.holdingInteractEvent == null)
                trigger.holdingInteractEvent = new InteractEventFloat();
        }

        internal static void ConfigureCommonTrigger(InteractTrigger trigger, bool holdInteraction, float holdSeconds)
        {
            trigger.oneHandedItemAllowed = true;
            trigger.twoHandedItemAllowed = true;
            trigger.holdInteraction = holdInteraction;
            trigger.timeToHold = Mathf.Max(0.05f, holdSeconds);
            trigger.timeToHoldSpeedMultiplier = 1f;
            trigger.interactCooldown = true;
            trigger.cooldownTime = 0.18f;
            trigger.disableTriggerMesh = true;
            // #1066 U1: InteractTrigger declares hoverTip/disabledHoverTip as plain strings with no
            // initialiser, so a runtime AddComponent leaves them null. Vanilla
            // PlayerControllerB.SetHoverTipAndCurrentInteractTrigger assigns cursorTip.text from
            // disabledHoverTip whenever the trigger is not interactable and then calls
            // cursorTip.text.Replace("[LMB]", "[E]") unconditionally, so a null tip throws an NRE out
            // of LateUpdate - skipping arm placement, SetWhoToSpectate and every LateUpdate postfix
            // for as long as the player looks at the trigger. Default both here so no consumer of
            // this helper can leave one null; every caller that wants a real tip still overwrites it.
            trigger.hoverTip ??= string.Empty;
            trigger.disabledHoverTip ??= string.Empty;
            HideTriggerVisuals(trigger.gameObject);
            ApplyInteractionIcons(trigger);
            EnsureInteractTriggerEvents(trigger);
        }

        internal static void ConfigureWireTrigger(InteractTrigger trigger)
        {
            ConfigureCommonTrigger(trigger, holdInteraction: true, holdSeconds: WireHoldSeconds);
            trigger.holdTip = "Cutting wire";
            trigger.disabledHoverTip = "[ Wire unavailable ]";
        }

        internal static void ApplyInteractionIcons(InteractTrigger trigger)
        {
            if (trigger == null || trigger.hoverIcon != null)
                return;

            PlayerControllerB player = GameNetworkManager.Instance?.localPlayerController
                                       ?? StartOfRound.Instance?.localPlayerController;
            if (player?.grabItemIcon == null)
                return;

            trigger.hoverIcon = player.grabItemIcon;
            trigger.disabledHoverIcon = player.grabItemIcon;
        }

        private static void HideTriggerVisuals(GameObject triggerObject)
        {
            if (triggerObject == null)
                return;

            Renderer[] renderers = triggerObject.GetComponentsInChildren<Renderer>(includeInactive: true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer != null)
                    renderer.enabled = false;
            }

            MeshFilter[] filters = triggerObject.GetComponentsInChildren<MeshFilter>(includeInactive: true);
            for (int i = 0; i < filters.Length; i++)
            {
                MeshFilter filter = filters[i];
                if (filter != null)
                    filter.sharedMesh = null;
            }

            Image[] images = triggerObject.GetComponentsInChildren<Image>(includeInactive: true);
            for (int i = 0; i < images.Length; i++)
            {
                Image image = images[i];
                if (image != null)
                    image.enabled = false;
            }
        }
    }

    public class DefuseWireInteractable : MonoBehaviour
    {
        public DefuseInteractor Owner;
        public int WireSlot;

        private InteractTrigger _trigger;
        private Collider _collider;

        internal void ConfigureTrigger()
        {
            _collider = GetComponent<Collider>();
            _trigger = GetComponent<InteractTrigger>();
            if (_trigger == null)
                _trigger = gameObject.AddComponent<InteractTrigger>();

            DefuseInteractor.ConfigureWireTrigger(_trigger);
            _trigger.onInteract.RemoveListener(OnInteract);
            _trigger.onInteract.AddListener(OnInteract);
            RefreshState();
        }

        internal void RefreshState()
        {
            if (_trigger == null)
                return;

            DefuseBombController bomb = Owner != null ? Owner.Bomb : null;
            bool available = bomb != null && bomb.IsWireSlotInteractable(WireSlot);
            _trigger.interactable = available;
            _trigger.hoverTip = bomb != null ? bomb.GetWireInteractionTip(WireSlot) : "Cut wire : [LMB]";
            _trigger.disabledHoverTip = bomb != null ? bomb.GetWireDisabledTip(WireSlot) : "[ Wire unavailable ]";
            DefuseInteractor.ApplyInteractionIcons(_trigger);
            if (_collider != null)
                _collider.enabled = available;
        }

        public void OnInteract(PlayerControllerB player)
        {
            Owner?.RequestWireCut(WireSlot, player);
            RefreshState();
        }
    }

    public class DefuseKeypadButtonInteractable : MonoBehaviour
    {
        public DefuseInteractor Owner;
        public int Button;
        public Transform Visual;

        private InteractTrigger _trigger;
        private Collider _collider;

        internal void ConfigureTrigger()
        {
            _collider = GetComponent<Collider>();
            _trigger = GetComponent<InteractTrigger>();
            if (_trigger == null)
                _trigger = gameObject.AddComponent<InteractTrigger>();

            DefuseInteractor.ConfigureCommonTrigger(_trigger, holdInteraction: false, holdSeconds: 0.1f);
            _trigger.onInteract.RemoveListener(OnInteract);
            _trigger.onInteract.AddListener(OnInteract);
            RefreshState();
        }

        internal void RefreshState()
        {
            if (_trigger == null)
                return;

            DefuseBombController bomb = Owner != null ? Owner.Bomb : null;
            bool available = bomb != null && bomb.IsKeypadButtonInteractable();
            _trigger.interactable = available;
            _trigger.hoverTip = bomb != null ? bomb.GetKeypadButtonTip(Button) : "Press keypad : [LMB]";
            _trigger.disabledHoverTip = bomb != null ? bomb.GetKeypadDisabledTip() : "[ Keypad locked ]";
            DefuseInteractor.ApplyInteractionIcons(_trigger);
            if (_collider != null)
                _collider.enabled = available;
        }

        public void OnInteract(PlayerControllerB player)
        {
            Owner?.RequestKeypadButton(Button, player, Visual);
            RefreshState();
        }
    }
}
