using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>
    /// Separate native-font event strip. Arrive, hold for reading, then scroll; semantic
    /// changes cross-fade at the start, while live counters and scrambling keep moving. The
    /// belt is its own nested canvas so its per-frame motion does not re-batch the vanilla HUD.
    /// </summary>
    internal sealed class ContractHudAlertBelt
    {
        internal const float Height = 38f;
        private const float LabelFontSize = 22f;
        private const float LoopGap = 64f;
        private RectTransform _root;
        private CanvasGroup _group, _textGroup;
        private ContractHudLineArt _warningIcon;
        private readonly List<Image> _frame = new List<Image>(4);
        private RectTransform _labelRect;
        private TextMeshProUGUI _label;
        private readonly ContractHudAlertContent _content = new ContractHudAlertContent();
        private readonly ContractHudAlertMotion _motion = new ContractHudAlertMotion();
        private float _passWidth, _stripWidth;
        private bool _active;
        private float _lastAppliedAlpha = -1f, _lastTextAlpha = -1f;
        private float _lastAppliedX = float.NaN;
        private Color _lastFrameTint, _lastIconTint;

        internal bool IsActive => _active;
        internal RectTransform Root => _root;
        internal float ReservedHeight => _active ? Height : 0f;

        internal void Build(Transform parent, Action<TextMeshProUGUI> assignFont)
        {
            _content.Clear(); _motion.Clear(); _frame.Clear();
            _active = false;
            _passWidth = _stripWidth = 0f;
            _lastAppliedAlpha = _lastTextAlpha = -1f;
            _lastAppliedX = float.NaN;
            _lastFrameTint = _lastIconTint = Color.clear;
            var rootGo = new GameObject("AlertBelt");
            rootGo.transform.SetParent(parent, false);
            _root = rootGo.AddComponent<RectTransform>();
            _root.anchorMin = new Vector2(0f, 1f);
            _root.anchorMax = Vector2.one;
            _root.pivot = new Vector2(.5f, 1f);
            // The belt animates every frame; a nested canvas keeps that re-batch out of the vanilla HUD canvas.
            rootGo.AddComponent<Canvas>();
            _group = rootGo.AddComponent<CanvasGroup>();
            _group.interactable = false; _group.blocksRaycasts = false; _group.alpha = 0f;

            // Authored card units: at the resting 0.75 scale these are 1.5px / 3px at 1080p.
            var backing = CreateImage(rootGo.transform, "BeltBacking", Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            backing.color = new Color(0f, 0f, 0f, .12f);
            _frame.Add(CreateImage(rootGo.transform, "BeltLeft", Vector2.zero, Vector2.up, Vector2.zero, new Vector2(4f, 0f)));
            _frame.Add(CreateImage(rootGo.transform, "BeltTop", Vector2.up, Vector2.one, new Vector2(0f, -2f), Vector2.zero));
            _frame.Add(CreateImage(rootGo.transform, "BeltRight", Vector2.right, Vector2.one, new Vector2(-2f, 0f), Vector2.zero));
            _frame.Add(CreateImage(rootGo.transform, "BeltBottom", Vector2.zero, Vector2.right, Vector2.zero, new Vector2(0f, 2f)));

            var viewportGo = new GameObject("TickerViewport", typeof(RectTransform), typeof(RectMask2D));
            viewportGo.transform.SetParent(rootGo.transform, false);
            var viewport = viewportGo.GetComponent<RectTransform>();
            viewport.anchorMin = Vector2.zero; viewport.anchorMax = Vector2.one;
            viewport.offsetMin = new Vector2(40f, 3f); viewport.offsetMax = new Vector2(-8f, -3f);
            _textGroup = viewportGo.AddComponent<CanvasGroup>();
            _textGroup.interactable = false; _textGroup.blocksRaycasts = false;

            var warningGo = new GameObject("WarningMarker", typeof(RectTransform));
            warningGo.transform.SetParent(rootGo.transform, false);
            _warningIcon = warningGo.AddComponent<ContractHudLineArt>();
            _warningIcon.Icon = ContractHudLineArt.Shape.Warning;
            _warningIcon.raycastTarget = false;
            var warningRect = _warningIcon.rectTransform;
            warningRect.anchorMin = warningRect.anchorMax = new Vector2(0f, .5f);
            warningRect.pivot = new Vector2(0f, .5f);
            warningRect.anchoredPosition = new Vector2(9f, 0f);
            warningRect.sizeDelta = new Vector2(23f, 23f);

            var labelGo = new GameObject("BeltLabel");
            labelGo.transform.SetParent(viewport, false);
            _label = labelGo.AddComponent<TextMeshProUGUI>();
            _label.fontSize = LabelFontSize;
            _label.fontStyle = FontStyles.Normal;
            _label.characterSpacing = 0f;
            _label.alignment = TextAlignmentOptions.MidlineLeft;
            _label.enableWordWrapping = false;
            _label.overflowMode = TextOverflowModes.Overflow;
            _label.richText = true;
            _label.raycastTarget = false;
            _labelRect = _label.rectTransform;
            _labelRect.anchorMin = Vector2.zero;
            _labelRect.anchorMax = Vector2.up;
            _labelRect.pivot = new Vector2(0f, .5f);
            _labelRect.anchoredPosition = Vector2.zero;
            assignFont?.Invoke(_label);
            rootGo.SetActive(false);
            ApplyTheme();
        }

        private static Image CreateImage(Transform parent, string name, Vector2 min, Vector2 max, Vector2 insetMin, Vector2 insetMax)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<Image>();
            image.raycastTarget = false;
            var rect = image.rectTransform;
            rect.anchorMin = min; rect.anchorMax = max;
            rect.offsetMin = insetMin; rect.offsetMax = insetMax;
            return image;
        }

        internal void AssignFont(Action<TextMeshProUGUI> assignFont)
        {
            if (_label != null) assignFont?.Invoke(_label);
            if (_active && !_motion.WaitingToSwap) RebuildLabel(force: true);
        }

        internal bool SetSegments(List<ContractHudAlertSegment> segments)
        {
            if (!_content.Set(segments, ContractUiTheme.HudAccentHex)) return false;
            if (!_content.HasContent) { Hide(); return true; }

            if (!_active)
            {
                _active = true;
                _motion.Show();
                RebuildLabel(force: true);
            }
            else if (_content.MessageChanged)
                _motion.ChangeMessage();
            else if (!_motion.WaitingToSwap)
                RebuildLabel(force: true);

            // Apply zero arrival alpha BEFORE activation so the entire strip really fades in.
            ApplyMotion();
            if (_root != null && !_root.gameObject.activeSelf) _root.gameObject.SetActive(true);
            return true;
        }

        internal void Layout(float top, float sidePadding)
        {
            if (_root == null) return;
            _root.offsetMin = new Vector2(sidePadding, -(top + Height));
            _root.offsetMax = new Vector2(-sidePadding, -top);
            float width = Mathf.Max(0f, _root.rect.width - 48f);
            if (width <= 0f && _root.parent is RectTransform parent)
                width = Mathf.Max(0f, parent.rect.width - sidePadding * 2f - 48f);
            if (Mathf.Abs(width - _stripWidth) <= .5f) return;
            _stripWidth = width;
            if (_active && !_motion.WaitingToSwap) RebuildLabel(force: false);
        }

        internal void Tick(float unscaledDeltaTime)
        {
            if (!_active || _labelRect == null || _passWidth <= 0f) return;
            if (_motion.Tick(unscaledDeltaTime)) RebuildLabel(force: true);
            ApplyMotion();
        }

        internal void ApplyTheme()
        {
            if (_label != null) _label.color = ContractUiTheme.HudAccent;
            if (_content.Recolor(ContractUiTheme.HudAccentHex) && _active && !_motion.WaitingToSwap)
                RebuildLabel(force: true);
            ApplyMotion();
        }

        internal void Hide()
        {
            _active = false; _content.Clear(); _motion.Clear(); _passWidth = 0f;
            ApplyMotion();
            if (_root != null) _root.gameObject.SetActive(false);
        }

        private void RebuildLabel(bool force)
        {
            if (_label == null || !_content.HasContent) return;
            float textWidth = _label.GetPreferredValues(_content.Pass, 4000f, Height).x;
            if (textWidth <= 0f) return;
            // A short headline has blank space after it, not a second copy beside it during the hold.
            float width = Mathf.Max(textWidth + LoopGap, _stripWidth + LoopGap);
            if (!force && Mathf.Abs(width - _passWidth) <= .5f) return;
            _passWidth = width;
            string gap = "<space=" + (width - textWidth).ToString("0.###", CultureInfo.InvariantCulture) + ">";
            _label.text = _content.Pass + gap + _content.Pass + gap;
            _labelRect.sizeDelta = new Vector2(width * 2f, 0f);
            _lastAppliedX = float.NaN;
        }

        private void ApplyMotion()
        {
            float alpha = _motion.Alpha;
            if (_group != null && Mathf.Abs(alpha - _lastAppliedAlpha) > .0001f)
            {
                _group.alpha = alpha; _lastAppliedAlpha = alpha;
            }
            float textAlpha = _motion.TextAlpha;
            if (_textGroup != null && Mathf.Abs(textAlpha - _lastTextAlpha) > .0001f)
            {
                _textGroup.alpha = textAlpha; _lastTextAlpha = textAlpha;
            }
            float x = _passWidth > 0f ? -(float)(_motion.ScrollDistance % _passWidth) : 0f;
            if (_labelRect != null && (float.IsNaN(_lastAppliedX) || Mathf.Abs(x - _lastAppliedX) > .01f))
            {
                _labelRect.anchoredPosition = new Vector2(x, 0f); _lastAppliedX = x;
            }

            float gain = _motion.PulseGain;
            Color frameTint = Brighten(ContractUiTheme.HudFrame, gain);
            Color iconTint = Brighten(ContractUiTheme.HudAccent, gain);
            if (frameTint != _lastFrameTint)
            {
                foreach (var edge in _frame) if (edge != null) edge.color = frameTint;
                _lastFrameTint = frameTint;
            }
            if (_warningIcon != null && iconTint != _lastIconTint)
            {
                _warningIcon.color = iconTint; _lastIconTint = iconTint;
            }
        }

        private static Color Brighten(Color tint, float gain)
            => new Color(Mathf.Min(1f, tint.r * gain), Mathf.Min(1f, tint.g * gain), Mathf.Min(1f, tint.b * gain), tint.a);
    }

    /// <summary>
    /// #724/#727. Transient belt segments posted by systems outside the escalation
    /// providers: a weather change, or anything else that wants one pass on the strip.
    /// Entries expire on their own; the card collects live ones on its poll.
    /// </summary>
    internal static class ContractHudAlerts
    {
        private struct Entry
        {
            internal string Text;
            internal bool Lethal;
            internal float ExpiresAt;
        }

        private static readonly List<Entry> _entries = new List<Entry>();

        /// <summary>Posts or refreshes a segment. Posting the same text again extends it
        /// rather than duplicating it.</summary>
        internal static void Post(string text, float seconds, bool lethal = false)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            float expiresAt = Time.unscaledTime + Mathf.Max(1f, seconds);
            for (int i = 0; i < _entries.Count; i++)
            {
                if (string.Equals(_entries[i].Text, text, StringComparison.Ordinal))
                {
                    _entries[i] = new Entry { Text = text, Lethal = lethal, ExpiresAt = expiresAt };
                    return;
                }
            }

            _entries.Add(new Entry { Text = text, Lethal = lethal, ExpiresAt = expiresAt });
        }

        internal static void Clear()
        {
            _entries.Clear();
        }

        /// <summary>Appends unexpired entries to <paramref name="segments"/> and prunes the
        /// rest. Returns the number appended.</summary>
        internal static int Collect(List<ContractHudAlertSegment> segments)
        {
            int appended = 0;
            float now = Time.unscaledTime;
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (_entries[i].ExpiresAt <= now)
                {
                    _entries.RemoveAt(i);
                    continue;
                }
            }

            for (int i = 0; i < _entries.Count; i++)
            {
                segments.Add(new ContractHudAlertSegment { Text = _entries[i].Text, Lethal = _entries[i].Lethal });
                appended++;
            }

            return appended;
        }

        /// <summary>Signature fragment so the card's change detection sees a posted alert.</summary>
        internal static void AppendSignature(StringBuilder sb)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                sb.Append("|A:").Append(_entries[i].Text);
            }
        }
    }
}
