namespace Y4NGZCompany.Contracts.Payload
{
    /// <summary>
    /// #1219 D3: the cart's one "stuck" verdict. Two <c>blocked</c> releases from the same pose
    /// (within <c>samePoseMetres</c>, half a chassis at the call site), with no cart route proved
    /// from that pose, latch it. Once latched it stays latched for the cart's life, so the cart
    /// offers its handle to no soldier again and the mission answers it once. A player may still
    /// push the cart; the verdict governs only soldier admission.
    ///
/// A blocked release farther than <c>samePoseMetres</c> from the pose remembered from the
    /// last one replaces that pose, so a cart that moved gets two tries at its new pose. Moved
    /// vertically while staying at the same XZ (elevator, portal) counts as a different pose: a
    /// 3 m drop is new ground the step has not been refused on, and combining it with an earlier
    /// floor would wrongly latch. A release with a route proof clears the remembered pose, because
    /// a proven route means the cart is not stuck there. #1233 S1: a paired entrance that refuses
    /// the arrived pair latches it at once (<see cref="NotePortalRefusal"/>): the pair cannot walk
    /// round a portal, and a fresh operator at that doorway asks the same two endpoint poses. Other
    /// releases (dead, stunned, cargo threat, separated, unsupported) do not come here and change
    /// nothing. Six failures without new haul ground (A4) remain the backstop behind it.
    ///
    /// Host only and Unity-free: the cart passes its 3D position, and a fresh cart gets a fresh
    /// verdict, so round reset needs nothing here.
    /// </summary>
    internal sealed class PayloadCartStuckVerdict
    {
        private bool _armed;
        private float _armedX, _armedY, _armedZ;

        /// <summary>The verdict is latched: the cart refuses the soldiers' handle for good.</summary>
        internal bool Latched { get; private set; }

        /// <summary>A blocked release has been counted at a pose and the next one there latches.</summary>
        internal bool Armed => _armed && !Latched;

        /// <summary>
        /// Count one <c>blocked</c> release at the 3D pose (<paramref name="x"/>,
        /// <paramref name="y"/>, <paramref name="z"/>). Returns true only on the release that
        /// latches the verdict.
        /// </summary>
        internal bool NoteBlockedRelease(float x, float y, float z, float samePoseMetres, bool routeProven)
        {
            if (Latched) return false;
            if (routeProven) { _armed = false; return false; }
            if (_armed)
            {
                float dx = x - _armedX, dy = y - _armedY, dz = z - _armedZ;
                if (dx * dx + dy * dy + dz * dz <= samePoseMetres * samePoseMetres)
                {
                    Latched = true;
                    return true;
                }
            }
            _armed = true; _armedX = x; _armedY = y; _armedZ = z;
            return false;
        }

        /// <summary>
        /// #1233 S1: a paired entrance refused the arrived pair at one of its endpoints. That first
        /// refusal is the whole verdict. Returns true only on the call that latches.
        /// </summary>
        internal bool NotePortalRefusal()
        {
            if (Latched) return false;
            Latched = true;
            return true;
        }

        /// <summary>The verdict's release-line token: none, armed or latched.</summary>
        internal string Token => Latched ? "latched" : _armed ? "armed" : "none";
    }
}
