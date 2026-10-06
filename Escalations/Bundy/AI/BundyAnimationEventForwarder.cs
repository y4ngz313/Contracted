using System;
using System.Text;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

namespace Y4NGZCompany.Escalations.Bundy.AI;

public class BundyAnimationEventForwarder : MonoBehaviour
{
    private BundyAIBase _ai;
    private int _visualProbeCount;
    private float _nextVisualProbeAt;
    private bool _loggedVisualProbeOnce;

    private const int VisualProbeMax = 40;
    private const float VisualProbeIntervalSeconds = 0.5f;

    private BundyAIBase AI
    {
        get
        {
            if (_ai == null)
                _ai = GetComponentInParent<BundyAIBase>();

            return _ai;
        }
    }

    private void Awake()
    {
        LogVisualFacingProbe("awake");
    }

    public void OnAttackImpact()
    {
        AI?.OnAttackImpact();
    }

    public void OnAttackRecover()
    {
        AI?.OnAttackRecover();
    }

    public void OnFinisherGrab()
    {
        AI?.OnFinisherGrab();
    }

    public void OnFinisherImpact()
    {
        AI?.OnFinisherImpact();
    }

    public void OnFinisherEnd()
    {
        AI?.OnFinisherEnd();
    }

    public void OnFootstep()
    {
        LogVisualFacingProbe("footstep");
        AI?.OnFootstep();
    }

    public void OnEmoteBeat()
    {
        AI?.OnEmoteBeat();
    }

    public void OnContextHit()
    {
        AI?.OnContextHit();
    }

    public void OnPullStartDone()
    {
        AI?.OnPullStartDone();
    }

    public void OnStaggerEnd()
    {
        AI?.OnStaggerEnd();
    }

    private void LogVisualFacingProbe(string context)
    {
        if (!Y4NGZCompany.Bootstrap.BundyPlugin.DebugLogsEnabled)
            return;

        if (_visualProbeCount >= VisualProbeMax)
            return;

        if (Time.time < _nextVisualProbeAt && _loggedVisualProbeOnce)
            return;

        _loggedVisualProbeOnce = true;
        _nextVisualProbeAt = Time.time + VisualProbeIntervalSeconds;
        _visualProbeCount++;

        try
        {
            var root = transform.root;
            Transform visual = transform;
            var ai = AI;
            var agent = GetComponentInParent<NavMeshAgent>();
            var netObj = GetComponentInParent<NetworkObject>();
            var animator = GetComponent<Animator>();

            Vector3 rootPos = root.position;
            Vector3 rootFwd = root.forward;
            float rootYaw = root.eulerAngles.y;
            string rootName = root.name;

            string visualPath = GetHierarchyPath(transform, root);
            Vector3 visualPos = visual.position;
            Vector3 visualFwd = visual.forward;
            float visualYaw = visual.eulerAngles.y;
            Vector3 visualLocalPos = visual.localPosition;
            Vector3 visualLocalRot = visual.localEulerAngles;
            float rootToVisualYawDelta = Mathf.DeltaAngle(rootYaw, visualYaw);

            string aiType = ai != null ? ai.GetType().Name : "<none>";
            string aiEnabled = ai != null ? ai.enabled.ToString() : "<n/a>";
            string aiActive = ai != null ? ai.gameObject.activeInHierarchy.ToString() : "<n/a>";

            string netSpawned = netObj != null ? netObj.IsSpawned.ToString() : "<n/a>";
            string netOwner = netObj != null ? netObj.IsOwner.ToString() : "<n/a>";

            string agentExists = agent != null ? "true" : "false";
            string agentEnabled = agent != null ? agent.enabled.ToString() : "<n/a>";
            string agentOnNavMesh = agent != null ? (agent.enabled && agent.isOnNavMesh).ToString() : "<n/a>";
            string agentUpdatePos = agent != null ? agent.updatePosition.ToString() : "<n/a>";
            string agentUpdateRot = agent != null ? agent.updateRotation.ToString() : "<n/a>";
            string agentStopped = agent != null ? agent.isStopped.ToString() : "<n/a>";
            string agentVel = "<n/a>";
            float agentVelMag = -1f;
            float agentVelYaw = 0f;
            string agentDesVel = "<n/a>";
            float agentDesVelMag = -1f;
            float agentDesVelYaw = 0f;
            if (agent != null && agent.enabled)
            {
                agentVel = FormatVector(agent.velocity);
                agentVelMag = agent.velocity.magnitude;
                if (agent.velocity.sqrMagnitude > 0.0001f)
                    agentVelYaw = Quaternion.LookRotation(agent.velocity.normalized, Vector3.up).eulerAngles.y;
                agentDesVel = FormatVector(agent.desiredVelocity);
                agentDesVelMag = agent.desiredVelocity.magnitude;
                if (agent.desiredVelocity.sqrMagnitude > 0.0001f)
                    agentDesVelYaw = Quaternion.LookRotation(agent.desiredVelocity.normalized, Vector3.up).eulerAngles.y;
            }

            string clip = "<n/a>";
            string stateHash = "<n/a>";
            string motionInt = "<n/a>";
            if (animator != null && animator.runtimeAnimatorController != null && animator.layerCount > 0)
            {
                try
                {
                    AnimatorClipInfo[] ci = animator.GetCurrentAnimatorClipInfo(0);
                    if (ci != null && ci.Length > 0 && ci[0].clip != null)
                        clip = ci[0].clip.name;
                    stateHash = animator.GetCurrentAnimatorStateInfo(0).shortNameHash.ToString();
                    motionInt = animator.GetInteger("MotionState").ToString();
                }
                catch { }
            }

            float localDist = -1f;
            float localAngle = 0f;
            try
            {
                var localPlayer = StartOfRound.Instance?.localPlayerController;
                if (localPlayer != null && localPlayer.isPlayerControlled && !localPlayer.isPlayerDead)
                {
                    localDist = Vector3.Distance(rootPos, localPlayer.transform.position);
                    Vector3 dir = (localPlayer.transform.position - rootPos).normalized;
                    localAngle = Vector3.SignedAngle(rootFwd, dir, Vector3.up);
                }
            }
            catch { }

            EmitWarning(
                $"BundyVisualProbe[{context}] #{_visualProbeCount} t={Time.time:F2}: " +
                $"visualPath={visualPath}, visualPos={FormatVector(visualPos)}, " +
                $"visualFwd={FormatVector(visualFwd)}, visualYaw={visualYaw:F1}, " +
                $"visualLocalPos={FormatVector(visualLocalPos)}, visualLocalRot={FormatVector(visualLocalRot)}, " +
                $"root={rootName}, rootPos={FormatVector(rootPos)}, rootFwd={FormatVector(rootFwd)}, rootYaw={rootYaw:F1}, " +
                $"rootVisYawDelta={rootToVisualYawDelta:F1}, " +
                $"aiType={aiType}, aiEnabled={aiEnabled}, aiActive={aiActive}, " +
                $"netSpawned={netSpawned}, netOwner={netOwner}, " +
                $"agentExists={agentExists}, agentEnabled={agentEnabled}, agentOnNavMesh={agentOnNavMesh}, " +
                $"agentUpdatePos={agentUpdatePos}, agentUpdateRot={agentUpdateRot}, agentStopped={agentStopped}, " +
                $"agentVel={agentVel}, agentVelMag={agentVelMag:F2}, agentVelYaw={agentVelYaw:F1}, " +
                $"agentDesVel={agentDesVel}, agentDesVelMag={agentDesVelMag:F2}, agentDesVelYaw={agentDesVelYaw:F1}, " +
                $"clip={clip}, stateHash={stateHash}, motionInt={motionInt}, " +
                $"localPlayerDist={localDist:F2}, localPlayerAngle={localAngle:F1}");
        }
        catch (Exception e)
        {
            EmitWarning($"BundyVisualProbe[{context}] exception: {e.GetType().Name}: {e.Message}");
        }
    }

    private static string GetHierarchyPath(Transform from, Transform root)
    {
        if (from == null) return "<null>";
        if (from == root) return root.name;
        var sb = new StringBuilder(from.name);
        Transform cur = from.parent;
        while (cur != null && cur != root)
        {
            sb.Insert(0, cur.name + "/");
            cur = cur.parent;
        }
        if (cur == root)
            sb.Insert(0, root.name + "/");
        return sb.ToString();
    }

    private static string FormatVector(Vector3 v)
    {
        return $"({v.x:F3}, {v.y:F3}, {v.z:F3})";
    }

    private static void EmitWarning(string message)
    {
        if (Y4NGZCompany.Bootstrap.BundyPlugin.Log != null)
            Y4NGZCompany.Bootstrap.BundyPlugin.Log.LogWarning(message);
        else
            Debug.LogWarning(message);
    }
}
