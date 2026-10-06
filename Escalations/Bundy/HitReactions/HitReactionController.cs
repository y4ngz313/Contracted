// VENDORED from the Procedural Hit Reactions System (IronSamiStudio), asset path
// Assets/IronSamiStudio/ProceduralHitReactionSystem/Scripts/HitReactionController.cs.
// Kept as close to verbatim as possible so upstream can be diffed against it. Changes:
//   1. namespace (IronSamiStudio.ProceduralHitReactions -> Y4NGZCompany.Escalations.Bundy.HitReactions)
//   2. `reactors` is initialized to an empty list so React() cannot NRE before the runtime
//      adapter (BundyProceduralFlinch) has populated it.
// `reactionLayer` is retained as data only — BodyPartReactor no longer drives an animator
// layer weight from it (see the note in that file).
using UnityEngine;
using System;
using System.Collections.Generic;

namespace Y4NGZCompany.Escalations.Bundy.HitReactions
{
    /// <summary>
    /// Per-character controller that routes a hit to the right BodyPartReactor.
    /// Attach one of these to every character prefab that needs hit reactions.
    /// </summary>
    public class HitReactionController : MonoBehaviour
    {
        [Header("Animator Settings")]
        public Animator animator;          // single Animator for this character
        public int      reactionLayer = 1; // retained for upstream parity; not written to by the mod

        [Header("Body Part Reactors")]
        public List<BodyPartReactor> reactors = new List<BodyPartReactor>();

        void Awake()
        {
            // grab the local Animator if the field wasn’t set in the Inspector
            if (animator == null)
                animator = GetComponent<Animator>();
        }

        /// <param name="partName">exact Transform.name of the bone you hit</param>
        /// <param name="hitDirection">world-space incoming velocity</param>
        /// <param name="hitPoint">world-space contact point</param>
        public void React(string partName, Vector3 hitDirection, Vector3 hitPoint)
        {
            foreach (var r in reactors)
            {
                if (r == null)
                    continue;
                if (r.name.Equals(partName, StringComparison.OrdinalIgnoreCase))
                {
                    r.React(hitDirection, hitPoint);
                    return;
                }
            }
        }
    }
}
