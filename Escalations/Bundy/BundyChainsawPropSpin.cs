using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Escalations.Bundy;
using Y4NGZCompany.Escalations.Bundy.AI;
using UnityEngine;

namespace Y4NGZCompany.Escalations.Bundy;

public sealed class BundyChainsawPropSpin : MonoBehaviour
{
    public Animator propAnimator;
    public string spinStateName = "Anim_Chainsaw_On";
    public bool startStopped = true;
    public float spinSpeed = 1f;

    private bool _started;
    private bool _warnedMissingAnimator;

    private void Awake()
    {
        ResolveAnimator();

        if (startStopped && propAnimator != null)
            propAnimator.speed = 0f;
    }

    public void SetSpinning(bool spinning, string reason = null)
    {
        ResolveAnimator();
        if (propAnimator == null)
        {
            if (!_warnedMissingAnimator)
            {
                _warnedMissingAnimator = true;
                Debug.LogWarning("[Bundy] Chainsaw prop spin skipped: missing prop Animator.");
            }

            return;
        }

        if (spinning)
        {
            propAnimator.enabled = true;
            propAnimator.speed = Mathf.Max(0.01f, spinSpeed);
            if (!_started && !string.IsNullOrEmpty(spinStateName))
            {
                propAnimator.Play(spinStateName, 0, 0f);
                _started = true;
            }
        }
        else
        {
            propAnimator.speed = 0f;
        }

        if (BundyLogGate.Allows(BundyLogCategory.Anim, 2))
            Debug.Log($"[Bundy] Chainsaw prop spin {(spinning ? "started" : "stopped")}; reason={reason ?? "<none>"}.");
    }

    private void ResolveAnimator()
    {
        if (propAnimator == null)
            propAnimator = GetComponentInChildren<Animator>(true);
    }
}
