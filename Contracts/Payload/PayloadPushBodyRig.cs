using System.Collections.Generic;
using System.Text;
using GameNetcodeStuff;
using UnityEngine;
using Y4NGZCompany.Bootstrap;
namespace Y4NGZCompany.Contracts.Payload
{
    /// <summary>
    /// The two vanilla locomotion bools on a third-person body animator (#1071).
    ///
    /// Vanilla derives Walking/Sprinting from <c>PlayerControllerB.moveInputVector</c> inside
    /// PlayerControllerB.Update, and the payload move clamp zeroes that field after every write, so a
    /// pusher's body stayed in Idle while the cart motor translated it: writing only
    /// <c>Animator.speed</c> does nothing to an idle pose. Vanilla does not fight this write - once
    /// <c>isWalking</c> has gone false it stops touching either bool - so the cart's LateUpdate pass is
    /// late enough to own them. Hashes are resolved once, the pattern LethalCCTV's OperatorAnimSession
    /// uses for the same two parameters.
    /// </summary>
    internal static class PayloadPushLocomotion
    {
        private static readonly int WalkingHash = Animator.StringToHash("Walking");
        private static readonly int SprintingHash = Animator.StringToHash("Sprinting");
        private static readonly int AnimationSpeedHash = Animator.StringToHash("animationSpeed");
        // #1071 follow-up: the four bools this pass does not DRIVE but has to OWN for the duration of a
        // grab. Vanilla writes them from live input/state, and the payload move clamp stops that input
        // reaching them, so whatever they happened to hold at the moment the handle was taken latches
        // for the whole push: a handle grabbed mid-strafe keeps Sideways, one grabbed crouched keeps
        // crouching, and a handle grabbed off a ledge keeps FallNoJump. LethalCCTV's OperatorAnimSession
        // pins the same set false for the same reason while it owns a stationary body.
        private static readonly int[] SecondaryHashes =
        {
            Animator.StringToHash("Sideways"),
            Animator.StringToHash("crouching"),
            Animator.StringToHash("Jumping"),
            Animator.StringToHash("FallNoJump"),
        };

        /// <summary>
        /// One attach's worth of ownership over <see cref="SecondaryHashes"/>: which of them the
        /// attached controller actually declares (a Set* against an undeclared parameter warns on every
        /// call, and a REMOTE body runs a different controller asset), and what they held when the
        /// handle was taken. The caller owns one instance per animator it drives and hands the same one
        /// to <see cref="Apply"/> and <see cref="Clear"/>, which is how the capture survives the frames
        /// between them.
        /// </summary>
        internal sealed class SecondaryFlags
        {
            internal Animator Animator;
            internal bool[] Declared;
            internal bool[] Saved;
            internal bool HasSaved;
        }

        /// <summary>
        /// True when the animator's controller actually declares all three parameters this pass drives.
        /// A REMOTE body runs <c>playersManager.otherClientsAnimatorController</c>, a different
        /// controller asset, and a Set* against a parameter a controller does not declare logs a warning
        /// on every call. Callers resolve this once per attach and cache it rather than scanning per frame.
        /// </summary>
        internal static bool HasLocomotionParameters(Animator animator)
        {
            if (animator == null || animator.runtimeAnimatorController == null || !animator.isInitialized)
                return false;

            bool walking = false;
            bool sprinting = false;
            bool animationSpeed = false;
            for (int i = 0; i < animator.parameterCount; i++)
            {
                AnimatorControllerParameter parameter = animator.GetParameter(i);
                if (parameter == null)
                    continue;
                if (parameter.type == AnimatorControllerParameterType.Bool)
                {
                    if (parameter.nameHash == WalkingHash)
                        walking = true;
                    else if (parameter.nameHash == SprintingHash)
                        sprinting = true;
                }
                else if (parameter.type == AnimatorControllerParameterType.Float && parameter.nameHash == AnimationSpeedHash)
                {
                    animationSpeed = true;
                }
            }

            return walking && sprinting && animationSpeed;
        }

        /// <summary>
        /// Drive the push locomotion state. Sprinting is always false: the cart motor has one speed band
        /// and a sprint pose would not match the hands pinned to the handle.
        ///
        /// <paramref name="animationDirection"/> is the walk cycle's direction float, and the caller has
        /// to supply it because vanilla stops updating that float the moment its own isWalking goes
        /// false under the move clamp: a player who strafed or backed into the handle would otherwise
        /// keep the stale sign, and S PULLS the cart, so a pinned +1 would walk a reversing body
        /// forwards. Vanilla owns the float again, and rewrites it every frame, as soon as the player
        /// walks under their own power.
        /// </summary>
        internal static void Apply(Animator animator, bool hasParameters, bool walking, float animationDirection, SecondaryFlags secondary = null)
        {
            if (animator == null)
                return;

            // The secondary set is owned whether or not this controller declares the three the push
            // pass drives: a latched Sideways or crouching is wrong on any body holding the handle.
            PinSecondary(animator, secondary);
            if (!hasParameters)
                return;

            animator.SetBool(WalkingHash, walking);
            animator.SetBool(SprintingHash, false);
            if (walking)
                animator.SetFloat(AnimationSpeedHash, animationDirection);
        }

        /// <summary>
        /// Hand the body back to vanilla with neither driven flag left set, and with the owned
        /// secondary bools put back the way the handle found them.
        /// </summary>
        internal static void Clear(Animator animator, bool hasParameters, SecondaryFlags secondary = null)
        {
            if (animator != null && hasParameters)
            {
                animator.SetBool(WalkingHash, false);
                animator.SetBool(SprintingHash, false);
            }

            RestoreSecondary(animator, secondary);
        }

        private static void PinSecondary(Animator animator, SecondaryFlags state)
        {
            if (state == null)
                return;

            if (state.Animator != animator || state.Declared == null)
            {
                state.Animator = animator;
                state.Declared = ResolveDeclaredSecondary(animator);
                state.HasSaved = false;
            }

            if (!state.HasSaved)
            {
                state.Saved = new bool[SecondaryHashes.Length];
                for (int i = 0; i < SecondaryHashes.Length; i++)
                    state.Saved[i] = state.Declared[i] && animator.GetBool(SecondaryHashes[i]);
                state.HasSaved = true;
            }

            for (int i = 0; i < SecondaryHashes.Length; i++)
            {
                if (state.Declared[i])
                    animator.SetBool(SecondaryHashes[i], false);
            }
        }

        private static void RestoreSecondary(Animator animator, SecondaryFlags state)
        {
            if (state == null)
                return;

            if (state.HasSaved && state.Declared != null && animator != null && animator == state.Animator)
            {
                for (int i = 0; i < SecondaryHashes.Length; i++)
                {
                    if (state.Declared[i])
                        animator.SetBool(SecondaryHashes[i], state.Saved[i]);
                }
            }

            state.Animator = null;
            state.Declared = null;
            state.Saved = null;
            state.HasSaved = false;
        }

        private static bool[] ResolveDeclaredSecondary(Animator animator)
        {
            var declared = new bool[SecondaryHashes.Length];
            if (animator == null || animator.runtimeAnimatorController == null || !animator.isInitialized)
                return declared;

            for (int i = 0; i < animator.parameterCount; i++)
            {
                AnimatorControllerParameter parameter = animator.GetParameter(i);
                if (parameter == null || parameter.type != AnimatorControllerParameterType.Bool)
                    continue;
                for (int s = 0; s < SecondaryHashes.Length; s++)
                {
                    if (parameter.nameHash == SecondaryHashes[s])
                        declared[s] = true;
                }
            }

            return declared;
        }
    }

    internal sealed class PayloadPushBodyRig
    {
        private const float PushLeanDegrees = 8f;
        private const float MaxReachShift = .40f; // the reach closure's cap: pelvis drop or view-arms shift
        private readonly PlayerControllerB _player;
        private readonly ArmRig _leftArm;
        private readonly ArmRig _rightArm;
        private readonly Transform[] _spineBones;
        private readonly float _leanDegrees;
        private readonly Transform[] _legs; // thigh, shin, foot per side, left first; null on the view arms
        private bool _solveLogged;
        private Vector3 _lastActorPosition = Vector3.positiveInfinity;
        private string _rigName = "body";
        // #1071 follow-up: the reach-root shift is a RELATIVE write, the one piece of this rig that can
        // leave residue; see ReleaseReachShift.
        private Transform _reachRoot;
        private Vector3 _reachRootLocalWritten, _reachRootLocalOriginal;
        private bool _reachShiftActive;
        private float _reachShift;

        private PayloadPushBodyRig(PlayerControllerB player, ArmRig leftArm, ArmRig rightArm, Transform[] spineBones, float leanDegrees, Transform[] legs)
        {
            _player = player;
            _leftArm = leftArm;
            _rightArm = rightArm;
            _spineBones = spineBones ?? new Transform[0];
            _leanDegrees = leanDegrees;
            _legs = legs;
            _reachRoot = legs != null ? legs[0].parent : null;
        }

        internal bool IsFor(PlayerControllerB player)
        {
            return player != null && player == _player;
        }

        /// <summary>At least one arm chain resolved, so the IK solve can actually reach a grip.</summary>
        internal static PayloadPushBodyRig ResolveActor(Transform actor)
        {
            Transform[] bones = actor.GetComponentsInChildren<Transform>(true);
            return new PayloadPushBodyRig(null, ArmRig.Resolve(bones, ArmSide.Left), ArmRig.Resolve(bones, ArmSide.Right), ResolveSpineBones(bones), PushLeanDegrees, ResolveLegs(bones));
        }

        /// <summary>
        /// First-person arms (#1305 E5): the same IK on <c>thisPlayerModelArms.bones</c> (skinned bones
        /// only, so no RigArms constraint or body bone can match), no lean, no legs, and
        /// <c>localArmsTransform</c>, the arms root vanilla's glue rewrites every frame, as reach root.
        /// </summary>
        internal static PayloadPushBodyRig ResolveViewArms(PlayerControllerB player)
        {
            Transform[] bones = player != null && player.thisPlayerModelArms != null ? player.thisPlayerModelArms.bones : new Transform[0];
            return new PayloadPushBodyRig(player, ArmRig.Resolve(bones, ArmSide.Left), ArmRig.Resolve(bones, ArmSide.Right), null, 0f, null)
            { _reachRoot = player != null ? player.localArmsTransform : null, _rigName = "view" };
        }
        /// <summary>
        /// How far the worse hand finished from its grip after the solve (WP2 step 4). It rides each
        /// path's one-shot <c>[MoonContracts.Payload.IK]</c> solve line rather than a line of its own.
        /// </summary>
        internal float MaximumGripError { get; private set; }
        private void RecordGripError(Transform left, Transform right)
            => MaximumGripError = _leftArm.IsUsable && _rightArm.IsUsable && left != null && right != null
                ? Mathf.Max(Vector3.Distance(_leftArm.Hand.position, left.position), Vector3.Distance(_rightArm.Hand.position, right.position))
                : float.PositiveInfinity;
        internal void ApplyActor(Transform actor, Transform left, Transform right, bool walking)
        {
            Pose(actor, left, right, walking, out bool leftOk, out bool rightOk);
            // v2: the soldier path runs on EVERY peer for every attach, and this line is a UAT
            // diagnostic rather than the player path's one-shot resolve record, so it is gated on
            // BlacksiteDiagnostics.Enabled like every other Blacksite diagnostic. The flag is checked
            // before the string is built, so a normal lobby pays one bool read per attach.
            if (_solveLogged || !Y4NGZCompany.Escalations.BlacksiteRetrieval.BlacksiteDiagnostics.Enabled) return;
            _solveLogged = true;
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Payload.IK] First LateUpdate solve: carrier=soldier, leftOk={leftOk}, rightOk={rightOk}, "
                + $"walking={walking}, gripError={MaximumGripError:0.000}.");
        }

        internal bool HasUsableArms => _leftArm.IsUsable || _rightArm.IsUsable;

        /// <summary>
        /// Resolve the two-arm IK chains + spine lean bones for ANY player's third-person body.
        /// <paramref name="verboseLog"/> gates the one-shot hierarchy/bone dumps: the local attach keeps
        /// them, the observer-side remote presentation (#441) only logs the summary line so a lobby of
        /// observers does not each dump 512 transforms per grab.
        /// </summary>
        internal static PayloadPushBodyRig Resolve(PlayerControllerB player, float leanDegrees, bool verboseLog = true)
        {
            Transform bodyRoot = FindThirdPersonBodyRoot(player);
            if (bodyRoot == null)
            {
                ContractsBootstrap.Log?.LogWarning("[MoonContracts.Payload.IK] Could not find third-person ScavengerModel body root; push IK disabled for this attach.");
                return new PayloadPushBodyRig(player, ArmRig.Empty, ArmRig.Empty, new Transform[0], leanDegrees, null);
            }

            Transform rigRoot = FindMetarig(bodyRoot) ?? bodyRoot;
            if (verboseLog)
                LogHierarchy(bodyRoot);

            Transform[] bones = rigRoot.GetComponentsInChildren<Transform>(includeInactive: true);
            ArmRig left = ArmRig.Resolve(bones, ArmSide.Left);
            ArmRig right = ArmRig.Resolve(bones, ArmSide.Right);
            Transform[] spine = ResolveSpineBones(bones);
            Transform[] legs = ResolveLegs(bones);

            if (verboseLog)
            {
                LogArm("left", left);
                LogArm("right", right);
                ContractsBootstrap.Log?.LogInfo(
                    "[MoonContracts.Payload.IK] Spine lean bones: " +
                    (spine.Length > 0 ? string.Join(", ", TransformNames(spine)) : "none matched"));
            }
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Payload.IK] IK resolve summary: playerClientId={(player != null ? player.actualClientId.ToString() : "none")}, " +
                $"root='{bodyRoot.name}', metarig='{rigRoot.name}', " +
                $"leftUsable={left.IsUsable}, rightUsable={right.IsUsable}, pelvis='{(legs != null ? legs[0].parent.name : "none")}', " +
                $"leanDegrees={leanDegrees:0.##}, verbose={verboseLog}.");

            return new PayloadPushBodyRig(player, left, right, spine, leanDegrees, legs);
        }

        /// <summary>
        /// <paramref name="walking"/> is "a walk cycle is playing on this body right now", which every
        /// caller already knows before it calls (#1071 follow-up). See
        /// <see cref="FitBodyToHandles"/> for what it gates and why.
        /// </summary>
        internal void ApplyLate(PlayerControllerB player, Transform leftGrip, Transform rightGrip, bool walking)
        {
            if (player == null || player != _player)
                return;

            Pose(player.transform, leftGrip, rightGrip, walking, out bool leftOk, out bool rightOk);
            // One line per rig (#1305 E5) once the follow stops dragging the pusher onto the stand point
            // (moved <= 0.01 m since the previous solve) or both hands are on the grips.
            bool still = (player.transform.position - _lastActorPosition).sqrMagnitude <= .0001f;
            _lastActorPosition = player.transform.position;
            if (_solveLogged || !(still || (leftOk && rightOk))) return;
            _solveLogged = true;
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Payload.IK] Settled grip solve: rig={_rigName}, leftOk={leftOk}, rightOk={rightOk}, " +
                $"walking={walking}, rootShift={_reachShift:0.000}, gripError={MaximumGripError:0.000}.");
        }

        private void Pose(Transform actor, Transform left, Transform right, bool walking, out bool leftOk, out bool rightOk)
        {
            ResolveGripSides(actor, ref left, ref right);
            ApplyLean(actor);
            FitBodyToHandles(actor, left, right, walking);
            leftOk = SolveArm(_leftArm, left, actor, -1f);
            rightOk = SolveArm(_rightArm, right, actor, 1f);
            RecordGripError(left, right);
        }

        // The root move that brings this arm's grip within reach (0.015 m short of full extension): for a
        // body straight down by exactly the closing distance (to grip height past horizontal reach), else
        // along the shoulder-to-grip line; zero when the grip is already in reach.
        private static Vector3 ReachShift(ArmRig arm, Transform grip, Vector3 shifted, bool vertical)
        {
            if (!arm.IsUsable || grip == null) return Vector3.zero;
            float reach = Vector3.Distance(arm.UpperArm.position, arm.Forearm.position) + Vector3.Distance(arm.Forearm.position, arm.Hand.position) - .015f;
            Vector3 toGrip = grip.position - arm.UpperArm.position - shifted;
            float deficit = toGrip.magnitude - reach, horizontal = new Vector2(toGrip.x, toGrip.z).magnitude;
            if (deficit < .01f) return Vector3.zero;
            return vertical ? Vector3.down * Mathf.Max(0f, -toGrip.y - Mathf.Sqrt(Mathf.Max(0f, reach * reach - horizontal * horizontal))) : toGrip.normalized * deficit;
        }
        /// <summary>
        /// Reach closure for both rigs (#1305 E5), capped at <see cref="MaxReachShift"/>. A body (legs
        /// matched by name, so the Generic player rig gets it like the humanoid soldier) drops its pelvis
        /// straight down by exactly what closes the worse arm and two-bone-IKs both legs to pin the feet
        /// where the clip left them. The first-person arms shift <c>localArmsTransform</c> toward the
        /// worse arm's grip, then once more for whichever arm is still short. The leg pin is skipped on
        /// a walk (#1080): the swinging foot runs the knee hint (<c>BlacksiteRiflePose.SolveArm</c>
        /// projects <c>actor.forward</c> onto hip-&gt;foot) through zero and a sign flip every stride,
        /// while the pelvis still drops so the hands reach the bar (WP2 step 5). Presentation only, on
        /// every peer: bone transforms, the resolved chains and <paramref name="walking"/>, no server state.
        /// </summary>
        private void FitBodyToHandles(Transform actor, Transform left, Transform right, bool walking)
        {
            ReleaseReachShift();
            if (_reachRoot == null) return;
            bool body = _legs != null;
            Vector3 shift = Vector3.zero;
            for (int pass = body ? 1 : 0; pass < 2; pass++)
            {
                Vector3 l = ReachShift(_leftArm, left, shift, body), r = ReachShift(_rightArm, right, shift, body);
                Vector3 step = l.sqrMagnitude >= r.sqrMagnitude ? l : r;
                if (step == Vector3.zero) break;
                shift = Vector3.ClampMagnitude(shift + step, MaxReachShift);
            }
            _reachShift = shift.magnitude;
            if (shift == Vector3.zero) return;
            bool pinFeet = body && !walking;
            Vector3 lp = pinFeet ? _legs[2].position : default, rp = pinFeet ? _legs[5].position : default;
            Quaternion lr = pinFeet ? _legs[2].rotation : default, rr = pinFeet ? _legs[5].rotation : default;
            _reachRootLocalOriginal = _reachRoot.localPosition;
            _reachRoot.position += shift;
            _reachRootLocalWritten = _reachRoot.localPosition; _reachShiftActive = true;
            if (!pinFeet) return;
            Y4NGZCompany.Escalations.BlacksiteRetrieval.BlacksiteRiflePose.SolveArm(_legs[0], _legs[1], _legs[2], lp, actor.forward);
            Y4NGZCompany.Escalations.BlacksiteRetrieval.BlacksiteRiflePose.SolveArm(_legs[3], _legs[4], _legs[5], rp, actor.forward);
            _legs[2].rotation = lr; _legs[5].rotation = rr;
        }

        /// <summary>
        /// Undo the previous pass's root shift, ONLY if nothing rewrote the root since: a LOCAL position
        /// (what the Animator and vanilla's arms glue own; the actor's travel does not touch it) still
        /// equal to what this rig wrote means a culled body or a skipped glue frame, so the shift comes
        /// back off and the next solve re-applies it from the same original instead of accumulating. A
        /// rewritten root differs by at least the 0.01 m smallest shift and is left alone. Arm and leg
        /// writes are rotations recomputed every pass and need no equivalent.
        /// </summary>
        internal void ReleaseReachShift()
        {
            if (!_reachShiftActive) return;
            _reachShiftActive = false;
            if (_reachRoot == null) return;
            if ((_reachRoot.localPosition - _reachRootLocalWritten).sqrMagnitude > 1e-8f) return;
            _reachRoot.localPosition = _reachRootLocalOriginal;
        }

        private static void ResolveGripSides(Transform actor, ref Transform left, ref Transform right)
        {
            if (actor != null && left != null && right != null && Vector3.Dot(right.position - left.position, actor.right) < 0f)
            { Transform swap = left; left = right; right = swap; }
        }

        private void ApplyLean(Transform actor)
        {
            if (_spineBones.Length == 0 || Mathf.Abs(_leanDegrees) < 0.01f)
                return;

            Vector3 axis = actor.right;
            if (axis.sqrMagnitude < 0.0001f)
                axis = Vector3.right;

            Quaternion lean = Quaternion.AngleAxis(_leanDegrees / _spineBones.Length, axis.normalized);
            for (int i = 0; i < _spineBones.Length; i++)
            {
                Transform spine = _spineBones[i];
                if (spine != null)
                    spine.rotation = lean * spine.rotation;
            }
        }

        private static bool SolveArm(ArmRig arm, Transform grip, Transform actor, float sideSign)
        {
            if (arm == null || !arm.IsUsable || grip == null)
                return false;

            Transform upper = arm.UpperArm;
            Transform forearm = arm.Forearm;
            Transform hand = arm.Hand;

            Vector3 shoulderPos = upper.position;
            Vector3 elbowPos = forearm.position;
            Vector3 handPos = hand.position;
            float upperLen = Vector3.Distance(shoulderPos, elbowPos);
            float foreLen = Vector3.Distance(elbowPos, handPos);
            if (upperLen < 0.01f || foreLen < 0.01f)
                return false;

            Vector3 targetPos = grip.position;
            Vector3 toTarget = targetPos - shoulderPos;
            float targetDist = toTarget.magnitude;
            if (targetDist < 0.001f)
                return false;

            Vector3 targetDir = toTarget / targetDist;
            float maxReach = Mathf.Max(0.01f, upperLen + foreLen - 0.001f);
            float minReach = Mathf.Max(0.001f, Mathf.Abs(upperLen - foreLen) + 0.001f);
            float solveDist = Mathf.Clamp(targetDist, minReach, maxReach);
            Vector3 solveTarget = shoulderPos + targetDir * solveDist;

            Vector3 pole = Vector3.ProjectOnPlane(elbowPos - shoulderPos, targetDir);
            if (pole.sqrMagnitude < 0.0001f)
            {
                Vector3 playerSide = actor != null ? actor.right * sideSign : Vector3.right * sideSign;
                pole = Vector3.ProjectOnPlane(playerSide, targetDir);
            }
            if (pole.sqrMagnitude < 0.0001f)
                pole = Vector3.Cross(targetDir, Vector3.up);
            if (pole.sqrMagnitude < 0.0001f)
                pole = Vector3.Cross(targetDir, Vector3.forward);
            pole.Normalize();

            float along = (upperLen * upperLen - foreLen * foreLen + solveDist * solveDist) / (2f * solveDist);
            float heightSq = Mathf.Max(0f, upperLen * upperLen - along * along);
            Vector3 desiredElbow = shoulderPos + targetDir * along + pole * Mathf.Sqrt(heightSq);

            Vector3 currentUpperDir = elbowPos - shoulderPos;
            Vector3 desiredUpperDir = desiredElbow - shoulderPos;
            if (currentUpperDir.sqrMagnitude > 0.0001f && desiredUpperDir.sqrMagnitude > 0.0001f)
                upper.rotation = Quaternion.FromToRotation(currentUpperDir, desiredUpperDir) * upper.rotation;

            Vector3 currentForeDir = hand.position - forearm.position;
            Vector3 desiredForeDir = solveTarget - forearm.position;
            if (currentForeDir.sqrMagnitude > 0.0001f && desiredForeDir.sqrMagnitude > 0.0001f)
                forearm.rotation = Quaternion.FromToRotation(currentForeDir, desiredForeDir) * forearm.rotation;

            hand.rotation = grip.rotation;
            return Vector3.Distance(hand.position, targetPos) <= 0.12f;
        }

        private static Transform FindThirdPersonBodyRoot(PlayerControllerB player)
        {
            if (player == null)
                return null;

            Transform root = FindDeep(player.transform, t =>
                string.Equals(t.name, "ScavengerModel", System.StringComparison.OrdinalIgnoreCase)
                && !IsFirstPersonArmsName(t.name));
            if (root != null)
                return root;

            Transform animatorRoot = player.playerBodyAnimator != null ? player.playerBodyAnimator.transform : null;
            if (animatorRoot != null && !IsFirstPersonArmsName(animatorRoot.name))
                return animatorRoot;

            return null;
        }

        private static Transform FindMetarig(Transform bodyRoot)
        {
            return FindDeep(bodyRoot, t =>
                t != null
                && t.name != null
                && t.name.IndexOf("metarig", System.StringComparison.OrdinalIgnoreCase) >= 0
                && !IsFirstPersonArmsName(t.name));
        }

        private static Transform FindDeep(Transform root, System.Func<Transform, bool> predicate)
        {
            if (root == null || predicate == null)
                return null;
            if (predicate(root))
                return root;

            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindDeep(root.GetChild(i), predicate);
                if (found != null)
                    return found;
            }

            return null;
        }

        private static void LogHierarchy(Transform root)
        {
            var sb = new StringBuilder(4096);
            sb.AppendLine($"[MoonContracts.Payload.IK] Third-person body hierarchy under '{root.name}':");
            AppendHierarchy(root, sb, 0, 0, 512);
            ContractsBootstrap.Log?.LogInfo(sb.ToString());
        }

        private static int AppendHierarchy(Transform t, StringBuilder sb, int depth, int count, int limit)
        {
            if (t == null || count >= limit)
                return count;

            sb.Append(' ', depth * 2);
            sb.AppendLine(t.name);
            count++;
            for (int i = 0; i < t.childCount && count < limit; i++)
                count = AppendHierarchy(t.GetChild(i), sb, depth + 1, count, limit);
            if (depth == 0 && count >= limit)
                sb.AppendLine("... hierarchy dump truncated at 512 transforms ...");
            return count;
        }

        private static Transform[] ResolveSpineBones(Transform[] bones)
        {
            var matches = new List<Transform>();
            for (int i = 0; i < bones.Length; i++)
            {
                Transform t = bones[i];
                if (t == null || string.IsNullOrEmpty(t.name))
                    continue;

                string name = Normalize(t.name);
                if (name.Contains("spine") || name.Contains("chest") || name.Contains("torso"))
                {
                    if (!IsFirstPersonArmsName(t.name))
                        matches.Add(t);
                }
            }

            if (matches.Count > 3)
                matches.RemoveRange(3, matches.Count - 3);
            return matches.ToArray();
        }

        // Thigh, shin and foot per side by name, like the arms (dotted thigh.L on the player, underscored
        // thigh_L on the soldier), each below the one before; null unless all six share one pelvis.
        private static Transform[] ResolveLegs(Transform[] bones)
        {
            var legs = new Transform[6];
            for (int i = 0; i < legs.Length; i++)
            {
                string role = i % 3 == 0 ? "thigh" : i % 3 == 1 ? "shin" : "foot";
                foreach (Transform t in bones)
                    if (legs[i] == null && t != null && Normalize(t.name).Contains(role) && TryMatchSide(t.name, i < 3 ? ArmSide.Left : ArmSide.Right, out _)
                        && (i % 3 == 0 || IsDescendantOf(t, legs[i - 1])))
                        legs[i] = t;
                if (legs[i] == null) return null;
            }
            return legs[0].parent != null && legs[0].parent == legs[3].parent ? legs : null;
        }

        private static void LogArm(string label, ArmRig arm)
        {
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Payload.IK] {label} arm matches: " +
                $"shoulder={arm.ShoulderMatch}, upper={arm.UpperArmMatch}, forearm={arm.ForearmMatch}, hand={arm.HandMatch}.");
        }

        private static string[] TransformNames(Transform[] transforms)
        {
            string[] names = new string[transforms.Length];
            for (int i = 0; i < transforms.Length; i++)
                names[i] = transforms[i] != null ? transforms[i].name : "null";
            return names;
        }

        private static bool IsFirstPersonArmsName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            string lower = name.ToLowerInvariant();
            return lower.Contains("armsonly")
                || lower.Contains("player-model-arms")
                || lower.Contains("playermodelarms")
                || lower.Contains("armsmetarig")
                || lower.Contains("arms_only");
        }

        private static string Normalize(string name)
        {
            return (name ?? string.Empty)
                .Replace(" ", string.Empty)
                .Replace("-", string.Empty)
                .ToLowerInvariant();
        }

        private enum ArmSide
        {
            Left,
            Right
        }

        private enum BoneRole
        {
            Shoulder,
            UpperArm,
            Forearm,
            Hand
        }

        private sealed class ArmRig
        {
            internal static readonly ArmRig Empty = new ArmRig();

            internal Transform Shoulder;
            internal Transform UpperArm;
            internal Transform Forearm;
            internal Transform Hand;
            internal string ShoulderMatch = "none";
            internal string UpperArmMatch = "none";
            internal string ForearmMatch = "none";
            internal string HandMatch = "none";

            internal bool IsUsable => UpperArm != null && Forearm != null && Hand != null;

            internal static ArmRig Resolve(Transform[] bones, ArmSide side)
            {
                var rig = new ArmRig();
                BoneMatch shoulder = FindBestBone(bones, side, BoneRole.Shoulder, null);
                BoneMatch upper = FindBestBone(bones, side, BoneRole.UpperArm, shoulder.Transform);
                BoneMatch forearm = FindBestBone(bones, side, BoneRole.Forearm, upper.Transform);
                BoneMatch hand = FindBestBone(bones, side, BoneRole.Hand, forearm.Transform);

                rig.Shoulder = shoulder.Transform;
                rig.UpperArm = upper.Transform;
                rig.Forearm = forearm.Transform;
                rig.Hand = hand.Transform;
                rig.ShoulderMatch = shoulder.Description;
                rig.UpperArmMatch = upper.Description;
                rig.ForearmMatch = forearm.Description;
                rig.HandMatch = hand.Description;
                return rig;
            }
        }

        private struct BoneMatch
        {
            internal Transform Transform;
            internal string Description;
            internal int Score;
        }

        private static BoneMatch FindBestBone(Transform[] bones, ArmSide side, BoneRole role, Transform preferredAncestor)
        {
            BoneMatch best = new BoneMatch { Description = "none", Score = int.MinValue };
            for (int i = 0; i < bones.Length; i++)
            {
                Transform t = bones[i];
                if (t == null || string.IsNullOrEmpty(t.name) || IsFirstPersonArmsName(t.name))
                    continue;

                if (!TryMatchSide(t.name, side, out string sidePattern))
                    continue;
                if (!TryMatchRole(t.name, role, out string rolePattern, out int roleScore))
                    continue;

                int score = roleScore;
                if (preferredAncestor != null && IsDescendantOf(t, preferredAncestor))
                    score += 100;
                if (t.name.IndexOf(".L", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || t.name.IndexOf(".R", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    score += 8;
                }

                if (score > best.Score)
                {
                    best.Transform = t;
                    best.Score = score;
                    best.Description = $"{t.name} ({rolePattern} + {sidePattern})";
                }
            }

            return best;
        }

        private static bool TryMatchRole(string rawName, BoneRole role, out string pattern, out int score)
        {
            string name = Normalize(rawName);
            pattern = null;
            score = 0;

            switch (role)
            {
                case BoneRole.Shoulder:
                    if (name.Contains("shoulder"))
                    {
                        pattern = "name contains shoulder";
                        score = 60;
                        return true;
                    }
                    if (name.Contains("clavicle"))
                    {
                        pattern = "name contains clavicle";
                        score = 55;
                        return true;
                    }
                    break;

                case BoneRole.UpperArm:
                    if ((name.Contains("upper") && name.Contains("arm")) || name.Contains("uparm"))
                    {
                        pattern = "upper arm";
                        score = 70;
                        return true;
                    }
                    if (name.Contains("arm") && !name.Contains("fore") && !name.Contains("lower") && !name.Contains("hand"))
                    {
                        pattern = "arm without fore/lower/hand";
                        score = 45;
                        return true;
                    }
                    break;

                case BoneRole.Forearm:
                    if (name.Contains("forearm"))
                    {
                        pattern = "name contains forearm";
                        score = 70;
                        return true;
                    }
                    if (name.Contains("lower") && name.Contains("arm"))
                    {
                        pattern = "lower arm";
                        score = 65;
                        return true;
                    }
                    break;

                case BoneRole.Hand:
                    if (name.Contains("hand"))
                    {
                        pattern = "name contains hand";
                        score = 70;
                        return true;
                    }
                    if (name.Contains("wrist"))
                    {
                        pattern = "name contains wrist";
                        score = 55;
                        return true;
                    }
                    break;
            }

            return false;
        }

        private static bool TryMatchSide(string rawName, ArmSide side, out string pattern)
        {
            string name = Normalize(rawName);
            string leftRight = side == ArmSide.Left ? "left" : "right";
            string dot = side == ArmSide.Left ? ".l" : ".r";
            string underscore = side == ArmSide.Left ? "_l" : "_r";
            string suffix = side == ArmSide.Left ? "l" : "r";

            if (name.Contains(leftRight))
            {
                pattern = $"contains {leftRight}";
                return true;
            }
            string lower = rawName.ToLowerInvariant();
            if (lower.Contains(dot))
            {
                pattern = $"contains {dot}";
                return true;
            }
            if (lower.Contains(underscore))
            {
                pattern = $"contains {underscore}";
                return true;
            }
            if (name.EndsWith(suffix, System.StringComparison.Ordinal)
                && (rawName.EndsWith("." + suffix, System.StringComparison.OrdinalIgnoreCase)
                    || rawName.EndsWith("_" + suffix, System.StringComparison.OrdinalIgnoreCase)
                    || rawName.EndsWith("-" + suffix, System.StringComparison.OrdinalIgnoreCase)))
            {
                pattern = $"ends with side suffix {suffix}";
                return true;
            }

            pattern = null;
            return false;
        }

        private static bool IsDescendantOf(Transform child, Transform ancestor)
        {
            Transform current = child;
            while (current != null)
            {
                if (current == ancestor)
                    return true;
                current = current.parent;
            }
            return false;
        }
    }

}
