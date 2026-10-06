using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Contracts.PestControl
{
    /// <summary>
    /// #716 (WP-A A4). The authored rig is roughly 150 <c>Instantiate</c> calls, and every peer
    /// used to pay all of them on the single frame the trap's footprint replicated - measured as
    /// one of the landed-frame contract bursts.
    ///
    /// The split here is between the rig's SHAPE and its MESHES. Every part still gets its host
    /// transform on the build frame, so positions, parenting, the rope endpoints, the fence
    /// hierarchy, the platform collider, the lever trigger and the scan node are all final and
    /// correct the moment <c>Build</c> returns - nothing downstream has to learn about a partially
    /// built rig, and the network spawn never waits on art. Only the template instantiation is
    /// queued, then drained under a per-frame budget.
    ///
    /// The lever is held non-interactive across the drain (see
    /// <c>PestControlTrapPreview.BeginDeferredAssembly</c>) so nobody can pull a handle whose cage
    /// is still assembling, and the per-part collider walk that used to run inside every clone is
    /// replaced by a single sweep once the last part exists.
    /// </summary>
    internal static partial class PestControlTrapAssembler
    {
        /// <summary>Per-frame instantiation budget for the queued art parts.</summary>
        private const double DeferredBuildMillisecondsPerFrame = 3d;

        private readonly struct DeferredArtPart
        {
            internal DeferredArtPart(Transform template, Transform host, string partName, Vector2 tiling)
            {
                Template = template;
                Host = host;
                PartName = partName;
                Tiling = tiling;
            }

            internal Transform Template { get; }

            /// <summary>The empty transform already carrying this part's final pose and scale.</summary>
            internal Transform Host { get; }

            internal string PartName { get; }

            internal Vector2 Tiling { get; }
        }

        /// <summary>Non-null only while a build is collecting parts. Static because the build is.</summary>
        private static List<DeferredArtPart> _deferredParts;

        private static void BeginDeferredArtParts()
        {
            _deferredParts = new List<DeferredArtPart>(192);
        }

        private static void CancelDeferredArtParts()
        {
            _deferredParts = null;
        }

        private static List<DeferredArtPart> TakeDeferredArtParts()
        {
            List<DeferredArtPart> queue = _deferredParts;
            _deferredParts = null;
            return queue;
        }

        /// <summary>
        /// Instantiates the queued templates a slice at a time, then strips the art kit's own
        /// colliders in one sweep and hands the lever back to the player.
        ///
        /// The coroutine runs on the preview itself, so a trap that despawns mid-build takes its
        /// unfinished queue with it and no orphaned part is ever instantiated afterwards.
        /// </summary>
        private static IEnumerator DrainDeferredArtParts(
            List<DeferredArtPart> queue,
            PestControlTrapPreview preview,
            GameObject authoredRoot,
            GameObject externalLights,
            HashSet<Collider> keptColliders)
        {
            // StartCoroutine runs synchronously up to the first yield, so the build frame pays for
            // the host transforms and nothing else; the first instantiation slice lands next frame.
            yield return null;

            int index = 0;
            int frames = 0;
            double cpuMilliseconds = 0d;
            while (queue != null && index < queue.Count)
            {
                long sliceStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                while (index < queue.Count)
                {
                    InstantiateDeferredArtPart(queue[index]);
                    index++;
                    if (ElapsedDeferredMilliseconds(sliceStarted) >= DeferredBuildMillisecondsPerFrame)
                        break;
                }

                cpuMilliseconds += ElapsedDeferredMilliseconds(sliceStarted);
                frames++;
                yield return null;
            }

            // One walk for the whole rig instead of one per part. The code-owned colliders - the
            // standable deck, the four fence seals, the lever's interact box and its scan sphere -
            // are the only ones that survive; everything the art kit brought with it goes.
            StripArtKitColliders(authoredRoot, keptColliders);
            StripArtKitColliders(externalLights, keptColliders);

            preview?.CompleteDeferredAssembly();
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.PestControl] Authored trap art assembled across {frames} sliced frame(s): " +
                $"parts={(queue != null ? queue.Count : 0)}, cpuMs={cpuMilliseconds:F2}.");
        }

        private static void InstantiateDeferredArtPart(DeferredArtPart part)
        {
            // The host is destroyed with the rig, so a despawn between slices simply drops the part.
            if (part.Host == null || part.Template == null)
                return;

            GameObject instance = Object.Instantiate(part.Template.gameObject);
            instance.name = part.PartName + "Mesh";
            instance.transform.SetParent(part.Host, false);
            // The host already composes the template's own rotation and scale, so the mesh sits on
            // it neutrally and the world transform matches the pre-#716 single-object clone exactly.
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;
            ApplyAuthoredTiling(instance, part.Tiling);
        }

        private static void StripArtKitColliders(GameObject root, HashSet<Collider> keptColliders)
        {
            if (root == null)
                return;

            Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider collider = colliders[i];
                if (collider == null || (keptColliders != null && keptColliders.Contains(collider)))
                    continue;

                collider.enabled = false;
                Object.Destroy(collider);
            }
        }

        private static double ElapsedDeferredMilliseconds(long started)
        {
            return (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000d /
                System.Diagnostics.Stopwatch.Frequency;
        }
    }
}
