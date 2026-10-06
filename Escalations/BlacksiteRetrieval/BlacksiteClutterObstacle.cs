using UnityEngine;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // #1293 R4: the observed-clutter carve went with the elevator boarding retry that was its one
    // caller; what stays is the static-geometry predicate the step arbiter reads.
    internal static class BlacksiteClutterObstacle
    {
        // #1170: the step arbiter's gate. BlacksitePhysicalStep.Steppable may only forgive a
        // contact the navmesh bake itself saw, and that is what this predicate answers: a prop, a
        // cart or an animated cabin lip arrived after the bake and stays a motor's step to refuse,
        // while a tile's own floor, kerb and stair flight are the bake's geometry.
        internal static bool IsStaticLevelGeometry(Collider source)
            => source.attachedRigidbody == null && source.GetComponentInParent<GrabbableObject>() == null
                && source.GetComponentInParent<Animator>() == null
                && (source.gameObject.isStatic || source.GetComponentInParent<DunGen.Tile>() != null);
    }
}
