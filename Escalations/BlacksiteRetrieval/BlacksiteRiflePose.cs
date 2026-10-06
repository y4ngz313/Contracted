using UnityEngine;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // Solved after the torso pose, from the evaluated skeleton. The dominant palm owns the rifle and the support
    // arm reaches it, rather than the weapon being stretched to the clip. #874 S3.2: the support hand is oriented
    // as well as placed, its wrist built from the weapon's own geometry inside anatomical bounds, and every write
    // is scaled by the support weight so no ownership change can teleport the hand.
    internal sealed class BlacksiteRiflePose
    {
        internal static readonly Vector3 DominantPalm = new Vector3(0f, -.095f, -.125f);
        internal static readonly Vector3 SupportPalm = new Vector3(-.032779f, -.042416f, .202373f);

        // The support grip frame, in the weapon model's own axes, measured off the real MX16A4
        // world model rather than adopted from either hand (S3.1 found production and the authored
        // clip 178.68 deg apart, so neither was a grip). 1871 of the mesh's 27676 vertices lie in a
        // 40 mm slab centred on SupportPalm.z = 0.2024; that handguard section is 61.6 x 62.1 mm
        // and its box centre is (0, -0.0152, 0.2030). SupportPalm sits below and left of that
        // centre, so the outward contact direction is the radial (-0.76919, -0.63902, 0) - 39.7 deg
        // round from the -X face toward the underside, which is where a support hand cups a
        // handguard. The mesh's own averaged vertex normals over the 40 mm around the contact read
        // (-0.782, -0.607, 0.142), 8.4 deg from that radial, so the section geometry and the
        // surface agree. The palm normal is the radial negated (into the weapon); the thumb axis is
        // the barrel direction +Z projected off it, which leaves +Z exactly; and the finger long
        // axis is cross(palmNormal, thumbAxis), the left-hand triad, because a left hand pointing
        // its fingers f with palm normal p carries its thumb along cross(f, p). The result is the
        // modern support hold: thumb down the handguard at the muzzle, fingers wrapping under and
        // across, palm rolled up into the weapon.
        internal static readonly Vector3 GripPalmNormal = new Vector3(.76919f, .63902f, 0f);
        internal static readonly Vector3 GripThumbAxis = new Vector3(0f, 0f, 1f);
        internal static readonly Vector3 GripFingerAxis = new Vector3(.63902f, -.76919f, 0f);

        private readonly Transform _root, _socket, _model, _right, _rightFinger, _left, _leftFinger, _upper, _forearm;
        private readonly Vector3 _socketPosition, _modelPosition;
        private readonly Quaternion _socketRotation, _modelRotation;
        private readonly Transform _rightUpper, _rightForearm;

        // The wrist frame, all constant for the life of the actor. _palmOffset, _palmLocal and
        // _fingerLocal are hand-local and pose-invariant because the fingers are children of the
        // hand; _bindRelative is the hand-in-forearm rotation of the avatar's bind pose, read from
        // the skinned mesh's bindposes so the bounds below are measured from the same zero the
        // fixture derives after Animator.Rebind.
        private readonly Quaternion _bindRelative, _handFrame;
        private readonly Vector3 _palmOffset, _palmLocal, _fingerLocal;
        private readonly Vector3 _twistAxis, _deviationAxis, _flexionAxis;
        // _grip: the hand's own palm frame resolved, so the support hand can be oriented at all.
        // _anatomy: the wrist frame resolved too, so the bounds and the twist absorption can run.
        private readonly bool _grip, _anatomy;

        private float _supportWeight;
        internal float SupportError { get; private set; }
        internal float DominantError { get; private set; }
        // The residuals of the last support solve, for the pose fixture and for logs.
        internal float SupportFlexDegrees { get; private set; }
        internal float SupportDeviationDegrees { get; private set; }
        internal float SupportTwistDegrees { get; private set; }
        internal float SupportWrapDegrees { get; private set; }
        internal float SupportSwivelDegrees { get; private set; }
        // How much pronation the forearm was asked to take off the wrist on the last solve.
        internal float SupportAbsorbedDegrees { get; private set; }

        internal BlacksiteRiflePose(Animator animator, Transform root, Transform socket, Transform model)
        {
            _root = root; _socket = socket; _model = model;
            _socketPosition = socket.localPosition; _socketRotation = socket.localRotation;
            _modelPosition = model.localPosition; _modelRotation = model.localRotation;
            _rightUpper = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
            _rightForearm = animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
            _right = animator.GetBoneTransform(HumanBodyBones.RightHand);
            _rightFinger = animator.GetBoneTransform(HumanBodyBones.RightMiddleProximal);
            _left = animator.GetBoneTransform(HumanBodyBones.LeftHand);
            _leftFinger = animator.GetBoneTransform(HumanBodyBones.LeftMiddleProximal);
            _upper = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            _forearm = animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
            _handFrame = Quaternion.identity;
            _bindRelative = Quaternion.identity;
            if (_left == null || _forearm == null || _leftFinger == null)
                return;

            _palmLocal = _left.InverseTransformDirection(SupportPalmNormal(animator)).normalized;
            _fingerLocal = _left.InverseTransformDirection(_leftFinger.position - _left.position).normalized;
            _palmOffset = Quaternion.Inverse(_left.rotation) * (Palm(_left, _leftFinger) - _left.position);
            float between = Vector3.Angle(_palmLocal, _fingerLocal);
            if (_palmLocal.sqrMagnitude < .5f || _fingerLocal.sqrMagnitude < .5f || between < 20f || between > 160f)
                return;
            _handFrame = Quaternion.Inverse(Quaternion.LookRotation(_fingerLocal, _palmLocal));
            _grip = true;

            Vector3 chain = _left.position - _forearm.position;
            if (chain.sqrMagnitude < 1e-8f)
                return;
            _twistAxis = _forearm.InverseTransformDirection(chain.normalized);
            // The bind pose is the wrist's anatomical zero. Without it the bounds would be measured
            // from whatever pose the soldier happened to stand in when the weapon was first bound,
            // so the live pose is only the fallback.
            _bindRelative = TryBindRelative(animator, out Quaternion bind)
                ? bind
                : Quaternion.Inverse(_forearm.rotation) * _left.rotation;
            _deviationAxis = Vector3.ProjectOnPlane(_bindRelative * _palmLocal, _twistAxis).normalized;
            if (_deviationAxis.sqrMagnitude < .25f)
                return;
            _flexionAxis = Vector3.Cross(_twistAxis, _deviationAxis).normalized;
            _anatomy = _flexionAxis.sqrMagnitude > .25f;
        }

        // The outward normal of the support palm, from the hand's own bones: across the index and
        // little knuckles, which is the same construction the pose fixture measures with.
        private Vector3 SupportPalmNormal(Animator animator)
        {
            Transform index = animator.GetBoneTransform(HumanBodyBones.LeftIndexProximal)
                ?? animator.GetBoneTransform(HumanBodyBones.LeftMiddleProximal);
            Transform little = animator.GetBoneTransform(HumanBodyBones.LeftLittleProximal)
                ?? animator.GetBoneTransform(HumanBodyBones.LeftThumbProximal);
            if (index == null || little == null)
                return _left.up;
            Vector3 normal = Vector3.Cross((index.position - _left.position).normalized,
                (little.position - _left.position).normalized);
            return normal.sqrMagnitude < 1e-6f ? _left.up : normal.normalized;
        }

        // The bind pose lives in the skinned mesh's bindposes. Only the forearm-to-hand relation is
        // wanted, and the renderer transform cancels out of that, so no world matrix is needed.
        private bool TryBindRelative(Animator animator, out Quaternion relative)
        {
            relative = Quaternion.identity;
            if (animator == null)
                return false;
            SkinnedMeshRenderer[] renderers = animator.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                SkinnedMeshRenderer renderer = renderers[i];
                if (renderer == null || renderer.sharedMesh == null || renderer.bones == null)
                    continue;
                Transform[] bones = renderer.bones;
                int forearm = System.Array.IndexOf(bones, _forearm), hand = System.Array.IndexOf(bones, _left);
                if (forearm < 0 || hand < 0)
                    continue;
                Matrix4x4[] poses;
                try { poses = renderer.sharedMesh.bindposes; }
                catch { continue; }
                if (poses == null || forearm >= poses.Length || hand >= poses.Length)
                    continue;
                relative = Quaternion.Inverse(poses[forearm].inverse.rotation) * poses[hand].inverse.rotation;
                return true;
            }
            return false;
        }

        internal void RestoreSocket()
        {
            if (_socket == null) return;
            _socket.localPosition = _socketPosition;
            _socket.localRotation = _socketRotation;
            if (_model != null) { _model.localPosition = _modelPosition; _model.localRotation = _modelRotation; }
        }

        /// <summary>
        /// One solve per evaluated frame. <paramref name="handsOnWeapon"/> is false whenever hand ownership
        /// has left the weapon (cargo, releasing, climbing, incapacitated): the dominant seating is then
        /// skipped and the support weight blends down, so the carry layer or the base clip owns the arms and
        /// the return is a blend, not a teleport. <paramref name="releaseSupport"/> is the narrower case of
        /// hands on the weapon but busy - reload, grenade - where the rifle keeps the clip's own hold.
        /// </summary>
        internal void Apply(bool aim, Vector3 point, bool handsOnWeapon, bool releaseSupport, float delta, float recoil)
        {
            if (_right == null || _left == null || _upper == null || _forearm == null || _model == null) return;
            _supportWeight = Mathf.MoveTowards(_supportWeight, handsOnWeapon && !releaseSupport ? 1f : 0f,
                delta / BlacksiteAimPosePolicy.SupportBlendSeconds);
            // Both elbow hints are the clip's own bend direction, read before anything is written.
            Vector3 supportHint = ClipHint(_upper, _forearm, _left,
                Vector3.Lerp(_root.right * -.3f - _root.up, _forearm.position - _upper.position, .35f));
            Vector3 dominantHint = _rightUpper == null || _rightForearm == null ? Vector3.zero
                : ClipHint(_rightUpper, _rightForearm, _right, _root.right * .3f - _root.up);

            Quaternion before = _model.rotation, rotation = before, correction = Quaternion.identity;
            if (handsOnWeapon)
            {
                // #1271: one rule for both holds, a rifle axis laid on a line from the dominant palm. Aimed, the
                // bore on the target, rolled level (the body turns first rather than wrench the wrist toward a
                // target behind it); otherwise the grip axis on the clip's own support palm, with the clip's roll.
                if (!releaseSupport)
                {
                    Vector3 line = (aim ? point : Palm(_left, _leftFinger)) - Palm(_right, _rightFinger);
                    if (aim) line = Vector3.RotateTowards(_root.forward, line.normalized, 65f * Mathf.Deg2Rad, 0f);
                    rotation = Quaternion.LookRotation(line, aim ? _root.up : before * Vector3.up) * Quaternion.AngleAxis(-recoil, Vector3.right)
                        * Quaternion.Inverse(Quaternion.LookRotation(aim ? Vector3.forward : SupportPalm - DominantPalm));
                }
                correction = rotation * Quaternion.Inverse(before);
                _right.rotation = correction * _right.rotation;
                Seat(rotation);
                DominantError = Vector3.Distance(Palm(_right, _rightFinger), _model.TransformPoint(DominantPalm));
            }
            if (_supportWeight <= 0f) return;

            Vector3 origin = _left.position;
            // Without a resolvable hand frame there is no grip to solve for, and the pre-S3.2
            // behaviour - the clip's own hand rotation carried by the weapon's turn - is the
            // fallback rather than an arbitrary orientation.
            Quaternion grip = _grip ? GripRotation(0f) : correction * _left.rotation;
            Vector3 target = _model.TransformPoint(SupportPalm) - grip * _palmOffset;
            // Blended crouch poses can put the dominant wrist beyond the support arm's
            // reach. Bring the rifle inward with the dominant arm instead of leaving a gap.
            if (handsOnWeapon && _rightUpper != null && _rightForearm != null)
            {
                float reach = Vector3.Distance(_upper.position, _forearm.position)
                    + Vector3.Distance(_forearm.position, _left.position) - .015f;
                Quaternion wristRotation = _right.rotation;
                for (int i = 0; i < 3; i++)
                {
                    Vector3 offset = target - _upper.position;
                    if (offset.magnitude <= reach) break;
                    Vector3 shift = offset.normalized * (reach - offset.magnitude) * _supportWeight;
                    SolveArm(_rightUpper, _rightForearm, _right, _right.position + shift, dominantHint);
                    _right.rotation = wristRotation;
                    Seat(rotation);
                    if (_grip) grip = GripRotation(0f);
                    target = _model.TransformPoint(SupportPalm) - grip * _palmOffset;
                }
                DominantError = Vector3.Distance(Palm(_right, _rightFinger), _model.TransformPoint(DominantPalm));
            }

            Vector3 blended = Vector3.Lerp(origin, target, _supportWeight);
            SolveArm(_upper, _forearm, _left, blended, supportHint);
            float wrap = 0f, swivel = 0f;
            if (_anatomy && _grip)
            {
                // The elbow swivel: the same hand in the same place with the elbow rolled around
                // the shoulder-to-wrist chord. It is the only motion that changes the forearm's own
                // roll under a fixed hand, so it is what the wrist bounds are actually bought with.
                swivel = ChooseSwivel(supportHint, blended, grip);
                if (Mathf.Abs(swivel) > .01f)
                    SolveArm(_upper, _forearm, _left, blended, Swivelled(supportHint, blended, swivel));
                // The freedom a palm flat on the handguard still keeps: spin about the contact
                // normal. On this rig that normal is close to the forearm's long axis, so most of it
                // is pronation the forearm absorbs below; it is kept because what is left of it is
                // free, and it is only taken when it buys more than it costs.
                wrap = ChooseWrap();
                if (Mathf.Abs(wrap) > .01f)
                {
                    grip = GripRotation(wrap);
                    target = _model.TransformPoint(SupportPalm) - grip * _palmOffset;
                    blended = Vector3.Lerp(origin, target, _supportWeight);
                    SolveArm(_upper, _forearm, _left, blended, Swivelled(supportHint, blended, swivel));
                }
            }
            _left.rotation = Quaternion.Slerp(_left.rotation, grip, _supportWeight);
            SupportWrapDegrees = wrap; SupportSwivelDegrees = swivel;
            if (_anatomy)
            {
                SupportAbsorbedDegrees = 0f;
                AbsorbTwist(_supportWeight);
                Wrist(out float flex, out float deviation, out float twist);
                SupportFlexDegrees = flex; SupportDeviationDegrees = deviation; SupportTwistDegrees = twist;
            }
            SupportError = Vector3.Distance(Palm(_left, _leftFinger), _model.TransformPoint(SupportPalm));
        }

        private void Seat(Quaternion rotation)
        {
            _socket.rotation = rotation * Quaternion.Inverse(_model.localRotation);
            _socket.position += Palm(_right, _rightFinger) - _model.TransformPoint(DominantPalm);
        }

        // The world rotation that puts this hand's own palm normal on the grip frame's normal and
        // its fingers along the grip frame's long axis, optionally spun about the contact normal.
        private Quaternion GripRotation(float wrapDegrees)
        {
            Quaternion model = _model.rotation;
            Vector3 normal = model * GripPalmNormal;
            Vector3 fingers = model * GripFingerAxis;
            if (Mathf.Abs(wrapDegrees) > .01f) fingers = Quaternion.AngleAxis(wrapDegrees, normal) * fingers;
            return Quaternion.LookRotation(fingers, normal) * _handFrame;
        }

        // The clip's own elbow direction: the elbow's offset from the shoulder-to-wrist chord. A
        // near-straight arm has no bend plane to speak of, and only then does the body-relative
        // hint take over.
        private static Vector3 ClipHint(Transform upper, Transform forearm, Transform hand, Vector3 fallback)
        {
            Vector3 chord = hand.position - upper.position;
            if (chord.sqrMagnitude < 1e-8f) return fallback;
            Vector3 offset = Vector3.ProjectOnPlane(forearm.position - upper.position, chord.normalized);
            return offset.magnitude < .01f ? fallback : offset.normalized;
        }

        // The clip's bend plane rotated about the shoulder-to-wrist chord: the elbow moves, the
        // shoulder and the hand do not.
        private Vector3 Swivelled(Vector3 hint, Vector3 target, float degrees)
        {
            Vector3 chord = target - _upper.position;
            return chord.sqrMagnitude < 1e-8f ? hint : Quaternion.AngleAxis(degrees, chord.normalized) * hint;
        }

        // Each candidate is solved for real and then rolled back, because the forearm's roll - the
        // whole point of the search - is inherited from the pose the solve starts in and cannot be
        // predicted from the elbow position alone.
        private float ChooseSwivel(Vector3 hint, Vector3 target, Quaternion grip)
        {
            float limit = BlacksiteAimPosePolicy.SupportSwivelLimitDegrees;
            Quaternion upper = _upper.rotation, forearm = _forearm.rotation;
            float best = 0f, bestCost = float.MaxValue;
            for (int i = -3; i <= 3; i++)
            {
                float swivel = i * (limit / 3f);
                SolveArm(_upper, _forearm, _left, target, Swivelled(hint, target, swivel));
                Measure(Quaternion.Inverse(_forearm.rotation) * grip, out float flex, out float deviation, out float _);
                float cost = Strain(flex, deviation) + .1f * Mathf.Abs(swivel) / limit;
                _upper.rotation = upper; _forearm.rotation = forearm;
                if (cost >= bestCost) continue;
                bestCost = cost; best = swivel;
            }
            return best;
        }

        // How hard a wrist is working, as a fraction of its own bounds, with being outside one
        // costing four times as much again.
        private static float Strain(float flex, float deviation)
        {
            float flexLimit = BlacksiteAimPosePolicy.SupportFlexLimitDegrees;
            float deviationLimit = BlacksiteAimPosePolicy.SupportDeviationLimitDegrees;
            return Mathf.Abs(flex) / flexLimit + Mathf.Abs(deviation) / deviationLimit
                + 4f * (Excess(flex, flexLimit) / flexLimit + Excess(deviation, deviationLimit) / deviationLimit);
        }

        private float ChooseWrap()
        {
            float limit = BlacksiteAimPosePolicy.SupportWrapLimitDegrees;
            float best = 0f, bestCost = float.MaxValue;
            Quaternion forearm = Quaternion.Inverse(_forearm.rotation);
            for (int i = -6; i <= 6; i++)
            {
                float wrap = i * (limit / 6f);
                Measure(forearm * GripRotation(wrap), out float flex, out float deviation, out float _);
                float cost = Strain(flex, deviation) + .1f * Mathf.Abs(wrap) / limit;
                if (cost >= bestCost) continue;
                bestCost = cost; best = wrap;
            }
            return best;
        }

        private static float Excess(float value, float limit) => Mathf.Max(0f, Mathf.Abs(value) - limit);

        // Pronation and supination belong to the forearm, not to the wrist. Rotating the forearm
        // about the axis through its own elbow and wrist leaves both joint positions and the whole
        // grip exactly where they are, so the twist the grip demands is taken off the wrist and
        // given to the bone that owns it. Two passes because the swing and the twist do not commute.
        private void AbsorbTwist(float weight)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                Wrist(out float _, out float __, out float twist);
                // Half a degree inside the limit, so the two-pass residue cannot land on the wrong
                // side of it.
                float limit = BlacksiteAimPosePolicy.SupportTwistLimitDegrees - .5f;
                float excess = twist - Mathf.Clamp(twist, -limit, limit);
                if (Mathf.Abs(excess) < .05f) return;
                Vector3 axis = _left.position - _forearm.position;
                if (axis.sqrMagnitude < 1e-8f) return;
                Quaternion hand = _left.rotation;
                _forearm.rotation = Quaternion.AngleAxis(excess * weight, axis.normalized) * _forearm.rotation;
                _left.rotation = hand;
                SupportAbsorbedDegrees += excess * weight;
            }
        }

        private void Wrist(out float flex, out float deviation, out float twist)
            => Measure(Quaternion.Inverse(_forearm.rotation) * _left.rotation, out flex, out deviation, out twist);

        // Flexion, deviation and twist of a hand-in-forearm rotation, in degrees from the bind pose,
        // split swing/twist about the forearm's own long axis.
        private void Measure(Quaternion relative, out float flex, out float deviation, out float twist)
        {
            Quaternion delta = relative * Quaternion.Inverse(_bindRelative);
            Vector3 projected = Vector3.Project(new Vector3(delta.x, delta.y, delta.z), _twistAxis);
            var spin = new Quaternion(projected.x, projected.y, projected.z, delta.w);
            float norm = Mathf.Sqrt(spin.x * spin.x + spin.y * spin.y + spin.z * spin.z + spin.w * spin.w);
            spin = norm < 1e-6f ? Quaternion.identity
                : new Quaternion(spin.x / norm, spin.y / norm, spin.z / norm, spin.w / norm);
            twist = Signed(spin, _twistAxis);
            (delta * Quaternion.Inverse(spin)).ToAngleAxis(out float angle, out Vector3 axis);
            if (angle > 180f) angle -= 360f;
            Vector3 swing = axis.normalized * angle;
            flex = Vector3.Dot(swing, _flexionAxis);
            deviation = Vector3.Dot(swing, _deviationAxis);
        }

        private static float Signed(Quaternion rotation, Vector3 axis)
        {
            rotation.ToAngleAxis(out float angle, out Vector3 about);
            if (angle > 180f) angle -= 360f;
            return Vector3.Dot(about.normalized, axis) < 0f ? -angle : angle;
        }

        private static Vector3 Palm(Transform hand, Transform finger) => finger == null ? hand.position : Vector3.Lerp(hand.position, finger.position, .5f);

        internal static void SolveArm(Transform upper, Transform forearm, Transform hand, Vector3 target, Vector3 hint)
        {
            Vector3 origin = upper.position;
            float a = Vector3.Distance(origin, forearm.position), b = Vector3.Distance(forearm.position, hand.position);
            Vector3 delta = target - origin;
            float distance = Mathf.Clamp(delta.magnitude, Mathf.Abs(a - b) + .001f, a + b - .001f);
            if (a < .001f || b < .001f || delta.sqrMagnitude < .000001f) return;
            Vector3 direction = delta.normalized;
            Vector3 bend = Vector3.ProjectOnPlane(hint, direction).normalized;
            if (bend.sqrMagnitude < .01f) bend = Vector3.Cross(direction, Vector3.forward).normalized;
            float along = (a * a + distance * distance - b * b) / (2f * distance);
            Vector3 elbow = origin + direction * along + bend * Mathf.Sqrt(Mathf.Max(0f, a * a - along * along));
            upper.rotation = Quaternion.FromToRotation(forearm.position - origin, elbow - origin) * upper.rotation;
            forearm.rotation = Quaternion.FromToRotation(hand.position - forearm.position, target - forearm.position) * forearm.rotation;
        }
    }
}
