using GameNetcodeStuff;
using Unity.Netcode;
using Y4NGZCompany.Contracts.Payload;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #1219 B1: the one world function. Every <c>Outside(...)</c> answers true when the entity is
    /// outside the facility and false when it is inside; this is the plan's
    /// <c>BlacksiteWorld.Of(entity)</c>, named for the bool it returns.
    ///
    /// An entity's own vanilla world field is the fact: a soldier's, a monster's and the witness's
    /// <see cref="EnemyAI.isOutside"/>, a prop's <see cref="GrabbableObject.isInFactory"/>, a
    /// player's <see cref="PlayerControllerB.isInsideFactory"/> and the cart's own replicated side
    /// <see cref="PayloadCartController.IsOutside"/>. Every place that DERIVES, caches or guesses a
    /// world goes through here instead of re-reading or re-inventing one of those fields; plain
    /// reads of <c>soldier.isOutside</c> elsewhere may stay. A remembered world (a sighting's, a
    /// report's) is a memo whose writer calls this and which is never read as the entity's
    /// current world.
    ///
    /// Host truth: <see cref="EnemyAI.isOutside"/> is host-local for Blacksite soldiers (not
    /// replicated), so client code reads a record's replicated <c>Outside</c>, never this.
    /// </summary>
    internal static class BlacksiteWorld
    {
        /// <summary>A soldier, a monster or the witness: its own <see cref="EnemyAI.isOutside"/>.
        /// The witness's facility flag (<c>IsInsideFacility</c>) is defined as <c>!isOutside</c>, so
        /// he needs no overload of his own.</summary>
        internal static bool Outside(EnemyAI enemy) => enemy.isOutside;

        /// <summary>A prop: <c>!isInFactory</c>.</summary>
        internal static bool Outside(GrabbableObject prop) => !prop.isInFactory;

        /// <summary>A player: <c>!isInsideFactory</c>.</summary>
        internal static bool Outside(PlayerControllerB player) => !player.isInsideFactory;

        /// <summary>The payload cart: its own side, which only its portal crossing and the round reset write.</summary>
        internal static bool Outside(PayloadCartController cart) => cart.IsOutside;

        /// <summary>
        /// A manifest record's object: the cart, then an agent (the witness), then a prop - the same
        /// order the manifest resolves them in. <paramref name="unknown"/> answers for an object that
        /// is none of the three, or is gone.
        /// </summary>
        internal static bool Outside(NetworkObject obj, bool unknown)
        {
            if (obj == null) return unknown;
            var cart = obj.GetComponent<PayloadCartController>();
            if (cart != null) return Outside(cart);
            var agent = obj.GetComponent<EnemyAI>();
            if (agent != null) return Outside(agent);
            var prop = obj.GetComponent<GrabbableObject>();
            return prop != null ? Outside(prop) : unknown;
        }

        /// <summary>
        /// The ONE rule for which world a paired doorway leads to: using an entrance that is not the
        /// way into the building puts the user outside.
        /// </summary>
        internal static bool AfterUsing(EntranceTeleport entrance) => !entrance.isEntranceToBuilding;

        /// <summary>Two agents in the same world.</summary>
        internal static bool Same(EnemyAI a, EnemyAI b) => Outside(a) == Outside(b);

        /// <summary>A player and an agent in the same world.</summary>
        internal static bool Same(PlayerControllerB player, EnemyAI enemy) => Outside(player) == Outside(enemy);
    }
}
