using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>
    /// #1196. Draws the row cues on one TMP body text by editing the vertices TMP has just generated:
    /// the accent sweep and the key-word pulse are vertex colours, the counter roll is a vertex offset
    /// plus fade of the counter's digits. No extra GameObjects or geometry.
    ///
    /// <para>The edits are re-applied from <c>OnPreRenderText</c> on every mesh rebuild (typewriter
    /// steps, fades, the checkbox art's ForceMeshUpdate), and <see cref="Tick"/> redraws them on the
    /// frames in between. Every pass restores the generated colours and positions first, so a cue
    /// never accumulates, and the last pass after the cues end leaves the generated mesh untouched.
    /// With no cue and no word-pulse request, both entry points return after
    /// <see cref="ContractHudRowFeedbackTracker.IsAnimating"/>, which touches no mesh.</para>
    ///
    /// <para>Rows are found by matching each line of the text against the tracked rows' own text
    /// (ContractHUDManager.FormatObjectiveLine writes the escaped objective text right after the
    /// invisible checkbox marker), longest match first, so the renderer needs no bookkeeping from
    /// the code that composes the body. A consumer with a text of its own (a card section) builds
    /// its own renderer and feeds it its rows.</para>
    /// </summary>
    internal sealed class ContractHudRowCueRenderer
    {
        private struct RowSpan
        {
            internal int Row;
            internal int Start;
            internal int End;
            internal int WordStart;
            internal int WordEnd;
            internal int NumeratorStart;
            internal int NumeratorEnd;
            internal int DenominatorStart;
            internal int DenominatorEnd;
        }

        private readonly TMP_Text _text;
        private readonly ContractHudRowFeedbackTracker _tracker = new ContractHudRowFeedbackTracker();
        private readonly List<RowSpan> _spans = new List<RowSpan>(4);
        private bool _spansDirty = true;
        private int _spanRevision = -1;
        private bool _applied;

        /// <summary>Card-edge glow for this frame, 0..1, from completion or failure cues on rows in this text.</summary>
        internal float Glow { get; private set; }
        /// <summary>The glow comes from a failed row, so it takes the danger colour.</summary>
        internal bool GlowIsFailure { get; private set; }
        /// <summary>A completion transition was observed on this poll, not a glow frame.</summary>
        internal bool CompletedThisObserve => _tracker.CompletedThisObserve;

        internal ContractHudRowCueRenderer(TMP_Text text)
        {
            _text = text;
            if (_text != null)
                _text.OnPreRenderText += OnPreRenderText;
        }

        /// <summary>Feeds a snapshot of the rows this text can show. See <see cref="ContractHudRowFeedbackTracker.Observe"/>.</summary>
        internal void Observe(IReadOnlyList<MoonContractObjective> rows, bool seedOnly)
        {
            _tracker.Observe(rows, Time.unscaledTime, seedOnly);
        }

        /// <summary>Starts only the side section's first-reveal accent sweep on an already seeded row.</summary>
        internal bool PlayRevealSweep(string key, float now) => _tracker.PlayRevealSweep(key, now);

        /// <summary>Forgets every row and cue; the next generated mesh is the plain one.</summary>
        internal void Clear()
        {
            _tracker.Clear();
            _spans.Clear();
            _spansDirty = true;
            Glow = 0f;
            GlowIsFailure = false;
            if (_applied && _text != null)
                _text.havePropertiesChanged = true;
            _applied = false;
        }

        /// <summary>
        /// Advances the cues one frame. Returns true when <see cref="Glow"/> must be (re)applied, which
        /// includes the one frame after the last cue ends so the edge returns to rest.
        /// </summary>
        internal bool Tick(float now)
        {
            bool animating = _tracker.IsAnimating(now);
            if (!animating && !_applied && Glow <= 0f)
                return false;

            if (_text == null || !_text.isActiveAndEnabled)
            {
                // A hidden text is regenerated when it shows again, and OnPreRenderText redraws any
                // cue still running then; one that ended meanwhile must not survive in the old mesh.
                if (_applied && _text != null)
                    _text.havePropertiesChanged = true;
                _applied = false;
                Glow = 0f;
                GlowIsFailure = false;
                return true;
            }

            TMP_TextInfo info = _text.textInfo;
            // A pending rebuild regenerates the mesh before this frame renders, and OnPreRenderText
            // draws the cues on it; editing the old mesh now would be thrown away.
            if (info == null || info.characterInfo == null || info.meshInfo == null || _text.havePropertiesChanged)
                return true;

            RefreshSpans(info);
            UpdateGlow(now);
            Draw(info, now, restore: true);
            _text.UpdateVertexData(TMP_VertexDataUpdateFlags.Colors32 | TMP_VertexDataUpdateFlags.Vertices);
            _applied = animating;
            return true;
        }

        private void OnPreRenderText(TMP_TextInfo info)
        {
            _spansDirty = true;
            float now = Time.unscaledTime;
            if (!_tracker.IsAnimating(now))
            {
                _applied = false;
                return;
            }

            RefreshSpans(info);
            Draw(info, now, restore: false);
            _applied = true;
        }

        private void RefreshSpans(TMP_TextInfo info)
        {
            if (!_spansDirty && _spanRevision == _tracker.Revision)
                return;

            _spansDirty = false;
            _spanRevision = _tracker.Revision;
            _spans.Clear();
            TMP_CharacterInfo[] chars = info.characterInfo;
            if (_tracker.Count == 0 || chars == null)
                return;

            int count = Mathf.Min(info.characterCount, chars.Length);
            int lineStart = 0;
            for (int i = 0; i <= count; i++)
            {
                if (i < count && chars[i].character != '\n')
                    continue;
                MapLine(chars, lineStart, i);
                lineStart = i + 1;
            }
        }

        private void MapLine(TMP_CharacterInfo[] chars, int start, int end)
        {
            int phrase = start;
            // Same test as ContractHudLineArt: only the card's own invisible marker, never literal brackets.
            if (end - start >= 3 && chars[start].character == '[' && chars[start + 2].character == ']'
                && chars[start].color.a == 0)
                phrase = start + 3;

            int best = -1;
            int bestLength = 0;
            for (int r = 0; r < _tracker.Count; r++)
            {
                string text = _tracker[r].Last.Text;
                if (string.IsNullOrEmpty(text) || text.Length <= bestLength)
                    continue;
                if (StartsWithPhrase(chars, phrase, end, text))
                {
                    best = r;
                    bestLength = text.Length;
                }
            }
            if (best < 0)
                return;

            ContractHudRowState row = _tracker[best];
            var span = new RowSpan
            {
                Row = best,
                Start = phrase,
                End = end,
                WordStart = -1,
                WordEnd = -1,
                NumeratorStart = -1,
                NumeratorEnd = -1,
                DenominatorStart = -1,
                DenominatorEnd = -1
            };
            if (ContractHudWordPulse.TryGetKeyWord(row.Last.Text, out int wordStart, out int wordLength))
            {
                // Escaping swaps characters one for one, so offsets into the objective text hold on the line.
                span.WordStart = phrase + wordStart;
                span.WordEnd = span.WordStart + wordLength;
            }
            if (ContractHudRowFeedback.ShowsCounter(row.Last))
                FindCounter(chars, phrase, end, ref span);
            _spans.Add(span);
        }

        private static bool StartsWithPhrase(TMP_CharacterInfo[] chars, int from, int end, string text)
        {
            int length = text.Length;
            if (from + length > end)
                return false;
            for (int k = 0; k < length; k++)
            {
                if (chars[from + k].character != ContractHudRowFeedback.EscapedChar(text[k]))
                    return false;
            }
            return from + length == end || chars[from + length].character == ' ';
        }

        /// <summary>The trailing " n/m" FormatObjectiveLine appends to a counted action row.</summary>
        private static void FindCounter(TMP_CharacterInfo[] chars, int start, int end, ref RowSpan span)
        {
            int denominator = end;
            while (denominator > start && char.IsDigit(chars[denominator - 1].character))
                denominator--;
            if (denominator == end || denominator <= start || chars[denominator - 1].character != '/')
                return;
            int slash = denominator - 1;
            int numerator = slash;
            while (numerator > start && char.IsDigit(chars[numerator - 1].character))
                numerator--;
            if (numerator == slash || numerator <= start || chars[numerator - 1].character != ' ')
                return;
            span.NumeratorStart = numerator;
            span.NumeratorEnd = slash;
            span.DenominatorStart = denominator;
            span.DenominatorEnd = end;
        }

        private void UpdateGlow(float now)
        {
            float glow = 0f;
            bool failure = false;
            for (int i = 0; i < _spans.Count; i++)
            {
                ContractHudRowState row = _tracker[_spans[i].Row];
                float level = ContractHudRowFeedback.GlowLevel(now - row.GlowAt);
                if (level <= glow)
                    continue;
                glow = level;
                failure = row.Cue == ContractHudRowCue.Fail;
            }
            Glow = glow;
            GlowIsFailure = failure;
        }

        private void Draw(TMP_TextInfo info, float now, bool restore)
        {
            TMP_CharacterInfo[] chars = info.characterInfo;
            TMP_MeshInfo[] meshes = info.meshInfo;
            int count = Mathf.Min(info.characterCount, chars.Length);
            if (restore)
            {
                for (int i = 0; i < count; i++)
                {
                    if (chars[i].isVisible)
                        Restore(chars, meshes, i);
                }
            }

            if (_spans.Count == 0)
                return;

            Color32 white = ContractUiTheme.White;
            for (int s = 0; s < _spans.Count; s++)
            {
                RowSpan span = _spans[s];
                ContractHudRowState row = _tracker[span.Row];
                float sweepAge = now - row.SweepAt;
                bool sweeping = sweepAge >= 0f && sweepAge < ContractHudRowFeedback.SweepSeconds;
                float word = ContractHudRowFeedback.WordModulation(now, row.WordAt, ContractHudWordPulse.IsActive(row.Key, now));
                float roll = ContractHudRowFeedback.RollProgress(now - row.RollAt);
                bool rolling = roll < 1f;
                if (!sweeping && word == 0f && !rolling)
                    continue;

                int end = Mathf.Min(span.End, count);
                float xMin = 0f, width = 1f;
                if (sweeping)
                    MeasureRow(chars, span.Start, end, out xMin, out width);
                float travel = rolling
                    ? (1f - roll) * ContractHudRowFeedback.RollTravel * _text.fontSize * (row.RollDirection > 0 ? -1f : 1f)
                    : 0f;

                for (int i = span.Start; i < end; i++)
                {
                    if (!chars[i].isVisible)
                        continue;
                    bool inWord = word != 0f && i >= span.WordStart && i < span.WordEnd;
                    bool inRoll = rolling
                        && ((row.RollComplete && i >= span.NumeratorStart && i < span.NumeratorEnd)
                            || (row.RollTotal && i >= span.DenominatorStart && i < span.DenominatorEnd));
                    if (!sweeping && !inWord && !inRoll)
                        continue;

                    int vertex = chars[i].vertexIndex;
                    Color32[] colors = meshes[chars[i].materialReferenceIndex].colors32;
                    Vector3[] vertices = meshes[chars[i].materialReferenceIndex].vertices;
                    for (int k = 0; k < 4; k++)
                    {
                        Color32 color = colors[vertex + k];
                        if (inWord)
                            color = word > 0f
                                ? Toward(color, white, word * ContractHudRowFeedback.WordBrighten)
                                : Scale(color, 1f + word * ContractHudRowFeedback.WordDim);
                        if (sweeping)
                        {
                            float weight = ContractHudRowFeedback.SweepWeight(sweepAge, (vertices[vertex + k].x - xMin) / width);
                            if (weight > 0f)
                                color = Toward(color, white, weight * ContractHudRowFeedback.SweepStrength);
                        }
                        if (inRoll)
                        {
                            color.a = (byte)(color.a * roll);
                            vertices[vertex + k].y += travel;
                        }
                        colors[vertex + k] = color;
                    }
                }
            }
        }

        private static void Restore(TMP_CharacterInfo[] chars, TMP_MeshInfo[] meshes, int i)
        {
            int vertex = chars[i].vertexIndex;
            Vector3[] vertices = meshes[chars[i].materialReferenceIndex].vertices;
            Color32[] colors = meshes[chars[i].materialReferenceIndex].colors32;
            // TMP fills each quad BL, TL, TR, BR from these same fields (FillCharacterVertexBuffers).
            vertices[vertex] = chars[i].vertex_BL.position;
            vertices[vertex + 1] = chars[i].vertex_TL.position;
            vertices[vertex + 2] = chars[i].vertex_TR.position;
            vertices[vertex + 3] = chars[i].vertex_BR.position;
            colors[vertex] = chars[i].vertex_BL.color;
            colors[vertex + 1] = chars[i].vertex_TL.color;
            colors[vertex + 2] = chars[i].vertex_TR.color;
            colors[vertex + 3] = chars[i].vertex_BR.color;
        }

        private static void MeasureRow(TMP_CharacterInfo[] chars, int start, int end, out float xMin, out float width)
        {
            float min = float.MaxValue, max = float.MinValue;
            for (int i = start; i < end; i++)
            {
                if (!chars[i].isVisible)
                    continue;
                min = Mathf.Min(min, chars[i].vertex_BL.position.x);
                max = Mathf.Max(max, chars[i].vertex_TR.position.x);
            }
            xMin = min <= max ? min : 0f;
            width = Mathf.Max(1f, max - min);
        }

        private static Color32 Toward(Color32 color, Color32 target, float amount)
        {
            Color32 blended = Color32.Lerp(color, target, amount);
            blended.a = color.a;
            return blended;
        }

        private static Color32 Scale(Color32 color, float factor)
            => new Color32((byte)(color.r * factor), (byte)(color.g * factor), (byte)(color.b * factor), color.a);
    }
}
