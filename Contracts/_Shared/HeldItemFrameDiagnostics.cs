using System.Text;
using GameNetcodeStuff;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>
    /// One-shot measurement of the hand frame a held item is posed in (#455, #789). Vanilla
    /// <c>GrabbableObject.LateUpdate</c> places a held item at
    /// <c>parentObject.position + parentObject.rotation * positionOffset</c> with rotation
    /// <c>parentObject.rotation * rotationOffset</c>, where <c>parentObject</c> is the holder's
    /// <c>localItemHolder</c> (first person, under the arms rig) or <c>serverItemHolder</c> (third
    /// person). Those frames are animation-driven and have never been measured for this project;
    /// two blind offset guesses for the drill failed on that alone. This logs the anchor's axes and
    /// position expressed in the camera frame and in the body frame, so a carry offset can be
    /// authored from numbers rather than guessed, and reports the mesh's bounds against the anchor
    /// so "floats above the hands" is a measurable gap.
    /// </summary>
    internal static class HeldItemFrameDiagnostics
    {
        internal static void Log(string prefix, GrabbableObject prop, Transform visual, string context)
        {
            if (prop == null)
                return;

            PlayerControllerB holder = prop.playerHeldBy;
            Transform anchor = prop.parentObject;
            var sb = new StringBuilder(640);
            sb.Append(prefix).Append(" Held frame (").Append(context).Append("): holder=")
              .Append(holder != null ? holder.playerUsername : "null")
              .Append(holder != null && holder.IsOwner ? " (local)" : " (remote)")
              .Append(", anchor=").Append(DescribeAnchor(holder, anchor));

            if (anchor == null)
            {
                ContractsBootstrap.Log?.LogInfo(sb.ToString());
                return;
            }

            sb.Append(", anchorPos=").Append(F(anchor.position))
              .Append(", itemPos=").Append(F(prop.transform.position))
              .Append(", item-anchor(anchorLocal)=").Append(F(anchor.InverseTransformPoint(prop.transform.position)))
              .Append(", positionOffset=").Append(prop.itemProperties != null ? F(prop.itemProperties.positionOffset) : "null")
              .Append(", rotationOffset=").Append(prop.itemProperties != null ? F(prop.itemProperties.rotationOffset) : "null");

            Camera cam = holder != null ? holder.gameplayCamera : null;
            if (cam != null)
            {
                Transform ct = cam.transform;
                sb.Append(" | camera frame: anchorPos=").Append(F(ct.InverseTransformPoint(anchor.position)))
                  .Append(", anchorRight=").Append(F(ct.InverseTransformDirection(anchor.right)))
                  .Append(", anchorUp=").Append(F(ct.InverseTransformDirection(anchor.up)))
                  .Append(", anchorForward=").Append(F(ct.InverseTransformDirection(anchor.forward)))
                  .Append(", itemViewport=").Append(F(cam.WorldToViewportPoint(prop.transform.position)));
            }

            if (holder != null)
            {
                Transform bt = holder.transform;
                sb.Append(" | body frame: anchorPos=").Append(F(bt.InverseTransformPoint(anchor.position)))
                  .Append(", anchorRight=").Append(F(bt.InverseTransformDirection(anchor.right)))
                  .Append(", anchorUp=").Append(F(bt.InverseTransformDirection(anchor.up)))
                  .Append(", anchorForward=").Append(F(bt.InverseTransformDirection(anchor.forward)))
                  .Append(", eyeToAnchor=").Append(holder.playerEye != null ? F(anchor.position - holder.playerEye.position) : "n/a");
            }

            if (visual != null)
            {
                Renderer[] renderers = visual.GetComponentsInChildren<Renderer>(true);
                bool any = false;
                Bounds bounds = default;
                for (int i = 0; i < renderers.Length; i++)
                {
                    Renderer renderer = renderers[i];
                    if (renderer == null || renderer is ParticleSystemRenderer)
                        continue;
                    if (!any)
                    {
                        bounds = renderer.bounds;
                        any = true;
                    }
                    else
                    {
                        bounds.Encapsulate(renderer.bounds);
                    }
                }

                sb.Append(" | mesh: localScale=").Append(F(visual.localScale));
                if (any)
                {
                    sb.Append(", boundsCenter-anchor=").Append(F(bounds.center - anchor.position))
                      .Append(", boundsSize=").Append(F(bounds.size))
                      .Append(", bottomBelowAnchor=").Append((anchor.position.y - bounds.min.y).ToString("0.00"))
                      .Append(", topAboveAnchor=").Append((bounds.max.y - anchor.position.y).ToString("0.00"));
                }
            }

            ContractsBootstrap.Log?.LogInfo(sb.ToString());
        }

        private static string DescribeAnchor(PlayerControllerB holder, Transform anchor)
        {
            if (anchor == null)
                return "none";
            if (holder != null && anchor == holder.localItemHolder)
                return "localItemHolder";
            if (holder != null && anchor == holder.serverItemHolder)
                return "serverItemHolder";
            return anchor.name;
        }

        private static string F(Vector3 value)
        {
            return $"{value.x:0.00},{value.y:0.00},{value.z:0.00}";
        }
    }
}
