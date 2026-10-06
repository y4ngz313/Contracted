using System;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #1019 S5: the SHAPE the engagement geometry is measured against. Six values, closed.
    ///
    /// Every profile is the measured humanoid envelope TRANSLATED by its own body radius (design
    /// note C.2). Since #1010 A2 the horizontal is measured to the contact's collider bounds
    /// CENTRE (BlacksiteSoldierAI.Equipment.cs:657-665, and :646-655 says why), so a body of
    /// radius r has its near face at centre - r and every wall moves by exactly (r - .40), the
    /// player capsule radius the S21B_REFINED sweep was taken against. That is what makes the
    /// build assertion below hold for all six by construction rather than by six lucky numbers.
    /// </summary>
    internal enum BlacksiteBodyProfile : byte
    {
        Humanoid = 0, GroundCrawler = 1, Flyer = 2, Ceiling = 3, Giant = 4, Hazard = 5
    }

    /// <summary>
    /// #1019 S5: where the approach ring's PLANE comes from. The Y itself stays Unity's to compute
    /// (<c>BlacksiteSoldierAI.ResolveBatonLaneFloor</c>, Equipment.cs:712-774); this says WHICH of
    /// its three cases applies, so the decision is in the table and the geometry query stays where
    /// the colliders are.
    /// </summary>
    internal enum BlacksiteRingPlane : byte
    {
        /// <summary>The ground under the contact (Humanoid, GroundCrawler, Flyer, Giant).</summary>
        ContactGround = 0,
        /// <summary>The soldier the contact is riding (Ceiling, and any live capture).</summary>
        CarrierFeet = 1,
        /// <summary>No ring at all: the intent is Evade, Withdraw, Watch or Ignore.</summary>
        None = 2
    }

    /// <summary>
    /// #1019 S5: ONE type owning reach, stand-off, arrival tolerance and the hit test's band, for
    /// every body profile.
    ///
    /// STRICTLY Unity-free, and that is a constraint rather than a preference: BlacksiteTierOne-
    /// Regression, BlacksiteReactiveRegression and BlacksiteBehaviourRegression carry no
    /// UnityEngineStubs.cs and all four headless suites that link BlacksiteCombatPolicy.cs link
    /// this file beside it. One Vector3 or one Mathf here breaks four suites at compile time.
    /// System.Math only.
    ///
    /// SCOPE, as built in S5: the melee consumers - <c>ResolveBatonEngagement</c> and
    /// <c>CloseOnBatonTarget</c> - read the HUMANOID row, through <c>BlacksiteCombatPolicy</c>'s
    /// forwarders, and wiring the executor to take its ring and its gate per profile was filed as
    /// follow-up work.
    ///
    /// #1063 WP2 is that follow-up, for the melee executor. <c>ResolveBatonEngagement</c> resolves
    /// the contact's profile once per think from the threat table's own description cache and
    /// carries it into every geometry read it takes: the reach gate, the inside-minimum-reach
    /// back-out, the commit's band, the abort's band, the close-quarters branch, the close-in's
    /// lane radius and the approach profile's reach. The row for a PLAYER contact, and for a
    /// contact with no usable body, is still Humanoid, which is the measured player capsule the
    /// whole envelope was swept against - so nothing about a player engagement moves.
    /// <c>BlacksiteThreatResponse.ApproachRing</c>/<c>ReachGate</c>/<c>AimAnchor</c> remain
    /// reported rather than executed; the executor reads the same rows this type owns.
    ///
    /// The numbers below are LITERALS with their derivation written beside them, not references to
    /// BlacksiteCombatPolicy's constants, because the end state of this stage is the opposite
    /// direction: block 5 turns BlacksiteCombatPolicy's melee constants into forwarders to this
    /// type's Humanoid row (design note D, F.2), and `const float` cannot point both ways - a pair
    /// of constants that reference each other is CS0110. Each row therefore cites the
    /// BlacksiteCombatPolicy line it was taken from, and block 5 is what makes the two one value.
    /// </summary>
    internal static class BlacksiteEngagementGeometry
    {
        // ------------------------------------------------------------------------------------
        // #1219: measured with the shipped 1.25 attack clock and confirmed-hit guards, across
        // 30/60/120 Hz and eight/four/four swing-start phases. The conservative per-swing bands
        // are [.99, 2.22], [.74, 2.22] and [.73, 2.14] m against a .4 x 2.5 m player capsule.
        // Faster playback changes frame chords; a single 60 Hz phase is not the reach authority.
        // ------------------------------------------------------------------------------------
        internal const float MeasuredConnectInner = .99f;
        internal const float MeasuredConnectOuter = 2.14f;
        /// <summary>The player capsule radius every measured number above was taken against.</summary>
        internal const float MeasuredBodyRadius = .40f;

        /// <summary>The margin each wall is measured with (BlacksiteCombatPolicy.cs:277).</summary>
        internal const float ReachMargin = .10f;
        /// <summary>The agent stop under the combat approach profile (BlacksiteCombatPolicy.cs:299).</summary>
        internal const float ApproachStop = .15f;
        /// <summary>
        /// The slack a NavMeshAgent with autoBraking settles inside, shared with
        /// BlacksiteArrivalPolicy.SettleSlack (:116) and BlacksiteCombatPolicy.BatonSettleSlack
        /// (:301), which the native fixture already asserts agree.
        /// </summary>
        internal const float SettleSlack = .20f;

        /// <summary>
        /// #1019 S5: the worst distance a SETTLED body can be from the point it was issued, either
        /// way. It is the agent's own settle, so it is profile-invariant - the contact's body
        /// moves the walls, not the tolerance - which is why the band width is .90 m for every
        /// profile and the assertion below holds for all six.
        ///
        /// It is NOT BlacksiteArrivalPolicy.MinDestinationChange (1.45, design note A.2): that is
        /// the smallest goal move a PLANNER may hand out (BlacksiteArrivalPolicy.cs:161-169), the
        /// melee ring does not go through a planner, and BlacksiteMotionOwner.cs:705-714's use of
        /// it refuses nothing.
        /// </summary>
        internal const float ArrivalTolerance = ApproachStop + SettleSlack;   // .35

        /// <summary>
        /// The vertical gate, separate from the horizontal since #1010 A2
        /// (BlacksiteCombatPolicy.cs:280). 1.7 m for EVERY profile and deliberately not a
        /// per-profile number: a flyer more than 1.7 m up is genuinely out of a baton's reach and
        /// the right answer there is an OutOfBand readiness that yields the think, not a widened gate.
        ///
        /// #1063 WP2: it is ASYMMETRIC, and the gap it is measured against is SIGNED - positive for
        /// a contact ABOVE the asking member, negative for one below
        /// (<c>BlacksiteSoldierAI.ResolveBatonEngagement</c> writes <c>target.y - transform.position.y</c>,
        /// no longer its absolute value). This is the ABOVE wall; the below wall is
        /// <see cref="VerticalDepthTolerance"/>, and it is a different number because the two
        /// directions are limited by different things. See <see cref="WithinVerticalReach"/>.
        /// </summary>
        internal const float VerticalTolerance = 1.7f;

        /// <summary>
        /// #1063 WP2: the deepest a strike capsule reaches BELOW the member's own floor plane, and
        /// therefore the deepest a contact can be and still be hittable at all.
        ///
        /// It is the sweep's own geometry, not a chosen number. <c>TickBatonSweep</c> lays the low
        /// strike as two capsules per pose (BlacksiteSoldierAI.Equipment.cs, the <c>lowStrike</c>
        /// pair): one dropping from the weapon head to <c>floorPlane = transform.position.y</c> and
        /// one lying ON that plane from the grip's shadow to the head's. Both TERMINATE on the
        /// plane, and both are laid at <c>BlacksiteBatonPolicy.HeadRadius</c> .18 m, so the lowest
        /// point any capsule of any swing occupies is exactly .18 m under the member's feet.
        /// Nothing in the weapon capsules above goes lower - they run along the hand, between waist
        /// and shoulder height.
        /// </summary>
        internal const float StrikeFloorDepth = .18f;   // BlacksiteBatonPolicy.HeadRadius

        /// <summary>
        /// #1063 WP2: the BELOW wall of the vertical gate - the strike's own depth plus the margin
        /// every other wall in this type is measured with.
        ///
        /// The measured gap for a contact entirely under the member is the gap to the TOP of its
        /// body (<c>MeasureBatonContact</c> clamps his height into the contact's span, so a span
        /// wholly below him returns that span's top), which is the part of it a downward strike
        /// meets first. A body whose top is further under his feet than this is not something the
        /// weapon can reach from where he is standing: it is something he has to walk down to, and
        /// the close-in already knows how - <c>ResolveBatonLaneFloor</c>'s downward cast puts the
        /// ring on the ground under a contact a storey BELOW him exactly as it does for one above.
        ///
        /// Without this wall the owner ruling "too low is fine as long as it is within horizontal
        /// reach" has no floor at all, and a member on a landing with a contact two to four metres
        /// down at 1.3 to 2.2 m horizontal holds, commits, whiffs, re-arms and recommits forever:
        /// Firing and Acquiring are RETAINED outcomes, so nothing releases the approach and the
        /// blocked-think counter never moves. The ruling is preserved exactly over the band the
        /// weapon actually covers and no further.
        ///
        /// The margin is <see cref="ReachMargin"/> .10 and deliberately not something larger such
        /// as <c>BatonLowContactHeight</c> .70: this wall exists to stop a whiff loop, so the one
        /// thing it must not do is re-admit a 0.70 m band of contacts no capsule can touch.
        /// </summary>
        internal const float VerticalDepthTolerance = StrikeFloorDepth + ReachMargin;   // .28
        /// <summary>The cone, unchanged at 35 degrees (BlacksiteCombatPolicy.cs:285).</summary>
        internal const float FacingDegrees = 35f;
        /// <summary>
        /// The extra step BatonLaneRadius pushes a lane out by for a contact inside the minimum
        /// reach, so the back-out is longer than the agent's own arrival tolerance
        /// (BlacksiteCombatPolicy.cs:360-364).
        /// </summary>
        internal const float LaneRadiusStep = .05f;

        // ------------------------------------------------------------------------------------
        // Every row translates the measured humanoid row by (body radius - .40).
        // MaxReach = MeasuredConnectOuter - ReachMargin = 2.04.
        // MinReach = MaxReach - 2 * (ArrivalTolerance + ReachMargin) = 1.14.
        // StandOff is the midpoint, 1.59; its full +/- .35 settle stays .10 inside both walls.
        // The inner wall still clears body separation .95 and measured inner reach .99 + .10.
        // Literals retain independent per-profile checks rather than restating their derivation.
        // ------------------------------------------------------------------------------------
        // The Humanoid row is internal, and only this row is: BlacksiteCombatPolicy's melee
        // constants forward to it (BatonAttackMinRange = HumanoidMinReach, and so on), which is
        // what makes the two one value rather than two literals that agree today. The other five
        // rows have no forwarder and stay private.
        internal const float HumanoidRadius = .40f;
        internal const float HumanoidMinReach = 1.14f;   // 2.04 - 2 * (.35 + .10)
        internal const float HumanoidMaxReach = 2.04f;   // 2.14 - .10
        internal const float HumanoidStandOff = 1.59f;   // (1.14 + 2.04) / 2

        /// <summary>
        /// Lane clearance, not the physical minimum, and profile-invariant because it is about two
        /// BODIES rather than about a reach: the player CharacterController radius .4 plus the
        /// solid soldier body capsule radius .38 (BlacksiteBodyCollision) is .78, and this is what
        /// the lane spacing must clear (BlacksiteCombatPolicy.cs:386, which forwards here).
        /// </summary>
        internal const float BodySeparation = .95f;

        // A flea, a crawler or a spider: a .30 m body, so every wall moves in by .10.
        private const float GroundCrawlerRadius = .30f;
        private const float GroundCrawlerMinReach = 1.04f;
        private const float GroundCrawlerMaxReach = 1.94f;
        private const float GroundCrawlerStandOff = 1.49f;

        // A flying body is still a humanoid-sized one; what makes it a Flyer is the measured
        // ground gap, not a different envelope.
        private const float FlyerRadius = HumanoidRadius;
        private const float FlyerMinReach = HumanoidMinReach;
        private const float FlyerMaxReach = HumanoidMaxReach;
        private const float FlyerStandOff = HumanoidStandOff;

        // A contact riding a squadmate is a GroundCrawler-sized body whose RING is the carrier's
        // feet rather than the ground under itself - the whole of the clung-flea failure
        // (BlacksiteSoldierAI.Equipment.cs:712-727).
        private const float CeilingRadius = GroundCrawlerRadius;
        private const float CeilingMinReach = GroundCrawlerMinReach;
        private const float CeilingMaxReach = GroundCrawlerMaxReach;
        private const float CeilingStandOff = GroundCrawlerStandOff;

        // A Forest Keeper or a Rad Mech: a 1.20 m body, so every wall moves out by .80.
        // Its outer wall is 2.14 + (1.20 - .40) - .10 = 2.84.
        // Two bodies still fit: soldier radius .38 + giant 1.20 = 1.58 <= 1.94.
        private const float GiantRadius = 1.20f;
        private const float GiantMinReach = 1.94f;
        private const float GiantMaxReach = 2.84f;
        private const float GiantStandOff = 2.39f;

        // A hazard is not reached, approached or struck: it is walked around. It has NO gate, and
        // the assertion below asserts the absence rather than a band.
        private const float HazardRadius = 0f;
        private const float HazardMinReach = 0f;
        private const float HazardMaxReach = 0f;
        private const float HazardStandOff = 0f;

        // ------------------------------------------------------------------------------------
        // Per-swing intersection of the measured frame grids. The first two outer walls are
        // clipped by the confirmed-hit root guard: HumanoidMaxReach 2.04 + RootReachSlack .18.
        // The gate decides commits; these wider bands decide whether an in-flight swing can land.
        // ------------------------------------------------------------------------------------
        private const float ConnectInnerSwingZero = .99f;
        private const float ConnectInnerSwingOne = .74f;
        private const float ConnectInnerSwingTwo = .73f;
        private const float ConnectOuterSwingZero = 2.22f;
        private const float ConnectOuterSwingOne = 2.22f;
        private const float ConnectOuterSwingTwo = 2.14f;

        // ------------------------------------------------------------------------------------
        // #1019 S5: the acceptance criterion, as a BUILD failure.
        //
        // A constant expression is evaluated at compile time, the conditional operator over
        // constant operands is one, and INTEGER DIVISION BY A CONSTANT ZERO inside one is
        // error CS0020 "Division by constant zero" - a compiler error, on the line of the
        // assertion, naming the profile. A violated inequality therefore fails `dotnet build`.
        //
        // Deliberately NOT: a zero-length array (allocates fine at run time), [Conditional] (it
        // elides a CALL and evaluates nothing), Debug.Assert or UnityEngine.Assertions (run time,
        // and Unity-bound), or a throwing static constructor - that last one is a
        // TypeInitializationException at first touch, which Unity catches and logs inside Update
        // and which leaves the soldier inert with no build signal at all. That is the exact class
        // of silent failure this stage exists to remove.
        // ------------------------------------------------------------------------------------
        private const int HumanoidFits =
            1 / ((ArrivalTolerance <= (HumanoidMaxReach - HumanoidMinReach) * .5f) ? 1 : 0);
        private const int GroundCrawlerFits =
            1 / ((ArrivalTolerance <= (GroundCrawlerMaxReach - GroundCrawlerMinReach) * .5f) ? 1 : 0);
        private const int FlyerFits =
            1 / ((ArrivalTolerance <= (FlyerMaxReach - FlyerMinReach) * .5f) ? 1 : 0);
        private const int CeilingFits =
            1 / ((ArrivalTolerance <= (CeilingMaxReach - CeilingMinReach) * .5f) ? 1 : 0);
        private const int GiantFits =
            1 / ((ArrivalTolerance <= (GiantMaxReach - GiantMinReach) * .5f) ? 1 : 0);
        private const int HazardHasNoGate =
            1 / ((HazardMaxReach == 0f && HazardMinReach == 0f) ? 1 : 0);

        /// <summary>
        /// #1019 S5: the sum of the six per-profile proofs, so none of them is unused and the
        /// whole set has one name a regression can read back. Its VALUE is meaningless (it is 6);
        /// its existence is the acceptance criterion.
        /// </summary>
        internal const int ArrivalToleranceFitsEveryProfile =
            HumanoidFits + GroundCrawlerFits + FlyerFits + CeilingFits + GiantFits + HazardHasNoGate;

        /// <summary>The contact body radius this profile is measured against.</summary>
        internal static float RadiusOf(BlacksiteBodyProfile profile)
        {
            switch (profile)
            {
                case BlacksiteBodyProfile.GroundCrawler: return GroundCrawlerRadius;
                case BlacksiteBodyProfile.Flyer: return FlyerRadius;
                case BlacksiteBodyProfile.Ceiling: return CeilingRadius;
                case BlacksiteBodyProfile.Giant: return GiantRadius;
                case BlacksiteBodyProfile.Hazard: return HazardRadius;
                default: return HumanoidRadius;
            }
        }

        /// <summary>
        /// How far this profile's walls sit from the humanoid row: (r - MeasuredBodyRadius). Zero
        /// for Hazard, which has no walls to move.
        /// </summary>
        internal static float Translation(BlacksiteBodyProfile profile)
            => profile == BlacksiteBodyProfile.Hazard ? 0f : RadiusOf(profile) - MeasuredBodyRadius;

        /// <summary>The gate's inner wall. Zero for Hazard, which has no gate.</summary>
        internal static float MinReach(BlacksiteBodyProfile profile)
        {
            switch (profile)
            {
                case BlacksiteBodyProfile.GroundCrawler: return GroundCrawlerMinReach;
                case BlacksiteBodyProfile.Flyer: return FlyerMinReach;
                case BlacksiteBodyProfile.Ceiling: return CeilingMinReach;
                case BlacksiteBodyProfile.Giant: return GiantMinReach;
                case BlacksiteBodyProfile.Hazard: return HazardMinReach;
                default: return HumanoidMinReach;
            }
        }

        /// <summary>The gate's outer wall. Zero for Hazard, which has no gate.</summary>
        internal static float MaxReach(BlacksiteBodyProfile profile)
        {
            switch (profile)
            {
                case BlacksiteBodyProfile.GroundCrawler: return GroundCrawlerMaxReach;
                case BlacksiteBodyProfile.Flyer: return FlyerMaxReach;
                case BlacksiteBodyProfile.Ceiling: return CeilingMaxReach;
                case BlacksiteBodyProfile.Giant: return GiantMaxReach;
                case BlacksiteBodyProfile.Hazard: return HazardMaxReach;
                default: return HumanoidMaxReach;
            }
        }

        /// <summary>The nav ring radius: the midpoint of the gate, which is where a settle of
        /// +/- ArrivalTolerance still lands inside both walls with one ReachMargin to spare.</summary>
        internal static float StandOff(BlacksiteBodyProfile profile)
        {
            switch (profile)
            {
                case BlacksiteBodyProfile.GroundCrawler: return GroundCrawlerStandOff;
                case BlacksiteBodyProfile.Flyer: return FlyerStandOff;
                case BlacksiteBodyProfile.Ceiling: return CeilingStandOff;
                case BlacksiteBodyProfile.Giant: return GiantStandOff;
                case BlacksiteBodyProfile.Hazard: return HazardStandOff;
                default: return HumanoidStandOff;
            }
        }

        /// <summary>
        /// The far edge of the settle band around the issued ring point: the worst range a member
        /// that arrived correctly can be at. BlacksiteCombatPolicy.BatonSettledRange (:340) with
        /// this profile's own stand-off.
        /// </summary>
        internal static float SettledRange(BlacksiteBodyProfile profile)
            => profile == BlacksiteBodyProfile.Hazard
                ? 0f
                : StandOff(profile) + ApproachStop + SettleSlack;

        /// <summary>
        /// Where on the contact's own circle a close-in lane is placed. Normally the stand-off;
        /// for a contact that has walked inside the minimum reach the lane is pushed out far
        /// enough to be a step the agent can execute, capped at the far edge of the settle band so
        /// it stays inside the gate. BlacksiteCombatPolicy.BatonLaneRadius (:360-364), per profile.
        /// </summary>
        internal static float LaneRadius(BlacksiteBodyProfile profile, float range)
        {
            if (profile == BlacksiteBodyProfile.Hazard) return 0f;
            float standOff = StandOff(profile);
            float lane = range >= MinReach(profile)
                ? standOff
                : Math.Min(SettledRange(profile),
                    Math.Max(standOff, range + ApproachStop + SettleSlack + LaneRadiusStep));
            // #1063 WP2: the close-in may never issue a ring whose own settle lands inside the
            // profile's capture ring. See MinimumLaneRadius.
            return Math.Max(lane, MinimumLaneRadius(profile));
        }

        /// <summary>
        /// #1063 WP2: the smallest ring the close-in is allowed to walk to for this profile - the
        /// inner wall plus the whole of the agent's own inward settle, so a member that arrives
        /// SHORT by the worst amount its agent can is still outside the gate's minimum reach.
        ///
        /// MinReach is the profile's capture ring in the only sense the table expresses one: it is
        /// the contact's own measured body radius translated out of the humanoid row, and the
        /// per-profile assertion "a soldier body and a &lt;profile&gt; body both fit inside its
        /// minimum reach" is what makes it that. The 2026-09-15 session walked members to
        /// <c>stop=0.15</c> on a ring that had no such floor at all, inside the grab of the thing
        /// they were closing on, because the close-in never asked the table which class it was
        /// approaching - <see cref="LaneRadius"/>'s caller passed the Humanoid row for every
        /// contact.
        ///
        /// It BINDS on no shipped row: <c>StandOff - ArrivalTolerance &gt;= MinReach</c> is exactly
        /// the build assertion <c>ArrivalToleranceFitsEveryProfile</c> already fails the compile
        /// over, and every branch above returns at least the stand-off. It is the guarantee written
        /// down where the ring is produced rather than left as a property of six literals.
        /// </summary>
        internal static float MinimumLaneRadius(BlacksiteBodyProfile profile)
            => profile == BlacksiteBodyProfile.Hazard
                ? 0f
                : MinReach(profile) + ApproachStop + SettleSlack;

        /// <summary>
        /// #1063 WP2: the vertical half of the gate, ASYMMETRIC, and it is the whole of the
        /// "raiders never swing" failure.
        ///
        /// The gap is SIGNED: positive means the measured contact point is ABOVE the asking member,
        /// negative means below. The two walls are different numbers because the two directions are
        /// limited by different things:
        /// <list type="bullet">
        /// <item>ABOVE, <see cref="VerticalTolerance"/> 1.70 m: a contact further up than this is
        /// genuinely out of a baton's reach and the right answer is an OutOfBand readiness rather
        /// than a widened gate. This is the flyer case the tolerance was authored for, unchanged.</item>
        /// <item>BELOW, <see cref="VerticalDepthTolerance"/> 0.28 m: the depth the strike capsules
        /// physically occupy under the member's floor plane, plus the usual margin. Inside it a
        /// downward swing is goofy to look at and it connects, which is the owner ruling for this
        /// issue. Outside it nothing the weapon lays can touch the contact, so the member walks
        /// down to it instead - the close-in's ring goes on the ground under a contact below him
        /// exactly as it does for one above.</item>
        /// </list>
        ///
        /// It used to be tested against <c>Mathf.Abs</c>, which made the two directions ONE number
        /// and that number the ABOVE one: the 2026-09-15 session measured a MouthDog contact at a
        /// 1.94 to 2.97 m gap while the dog's ROOT was 0.05 m from the soldier's own feet - the gap
        /// was the height of a collider authored two metres above the body, not the height of the
        /// body - and every baton path in three deployments refused every swing on it. Three things
        /// fix that together: the collider pick and the span extension in
        /// <c>BlacksiteSoldierAI.BatonContactBody</c>/<c>MeasureBatonContact</c>, this sign, and
        /// the depth wall that keeps the sign from becoming an unbounded licence to swing at
        /// nothing.
        ///
        /// NaN - the "nothing measured on this think" sentinel - fails BOTH comparisons and is
        /// therefore refused, which is the safe answer for an unmeasured gap.
        /// </summary>
        internal static bool WithinVerticalReach(float verticalGap)
            => verticalGap < VerticalTolerance && verticalGap > -VerticalDepthTolerance;

        /// <summary>
        /// The gate test, with the same two parameters and the same inclusive walls as
        /// BlacksiteCombatPolicy.BatonInReach (:345-346). A Hazard has no gate and is never in
        /// reach of anything: it is walked around, not struck.
        /// </summary>
        internal static bool InReach(BlacksiteBodyProfile profile, float horizontalRange, float verticalGap)
            => profile != BlacksiteBodyProfile.Hazard
               && horizontalRange >= MinReach(profile)
               && horizontalRange <= MaxReach(profile)
               && WithinVerticalReach(verticalGap);

        /// <summary>
        /// Inside the arc rather than out of it: a spacing rule, not a geometry one. A reason to
        /// step back out to the stand-off and strike from where the weapon reads, never a reason
        /// to swing from contact range. BlacksiteCombatPolicy.BatonInsideMinimumReach (:351-352).
        /// </summary>
        internal static bool InsideMinimumReach(BlacksiteBodyProfile profile, float range, float verticalGap)
            => profile != BlacksiteBodyProfile.Hazard
               && range < MinReach(profile)
               && WithinVerticalReach(verticalGap);

        /// <summary>Gate plus cone. BlacksiteCombatPolicy.BatonCanAttack (:353-354).</summary>
        internal static bool CanAttack(BlacksiteBodyProfile profile, float range, float verticalGap, float facingAngle)
            => InReach(profile, range, verticalGap) && facingAngle < FacingDegrees;

        /// <summary>
        /// The HIT TEST's own inner wall for this profile and swing - what the weapon physically
        /// connects over, which is what ShouldAbortSwing and BatonCloseQuartersSwing measure
        /// against. The measured humanoid band translated by (r - .40), exactly as the gate is.
        /// </summary>
        internal static float ConnectInner(BlacksiteBodyProfile profile, int swing)
            => profile == BlacksiteBodyProfile.Hazard
                ? 0f
                : (swing == 0 ? ConnectInnerSwingZero : swing == 1 ? ConnectInnerSwingOne : ConnectInnerSwingTwo)
                  + Translation(profile);

        /// <summary>The hit test's own outer wall for this profile and swing.</summary>
        internal static float ConnectOuter(BlacksiteBodyProfile profile, int swing)
            => profile == BlacksiteBodyProfile.Hazard
                ? 0f
                : (swing == 0 ? ConnectOuterSwingZero : swing == 1 ? ConnectOuterSwingOne : ConnectOuterSwingTwo)
                  + Translation(profile);

        /// <summary>
        /// The band all three swings share across measured frame grids, translated by body radius.
        /// The gate is a deliberate narrowing of it and the inner end is what has the room.
        /// </summary>
        internal static float SharedConnectInner(BlacksiteBodyProfile profile)
            => profile == BlacksiteBodyProfile.Hazard ? 0f : MeasuredConnectInner + Translation(profile);

        /// <summary>The outer end of the shared band. This is the one with no room: MaxReach is
        /// exactly this minus ReachMargin for every profile.</summary>
        internal static float SharedConnectOuter(BlacksiteBodyProfile profile)
            => profile == BlacksiteBodyProfile.Hazard ? 0f : MeasuredConnectOuter + Translation(profile);

        /// <summary>
        /// Which of ResolveBatonLaneFloor's three cases this profile's ring is sampled on. A
        /// Ceiling contact is riding a squadmate, so its ring goes on that carrier's feet; a
        /// Hazard has no ring at all.
        /// </summary>
        internal static BlacksiteRingPlane PlaneFor(BlacksiteBodyProfile profile)
        {
            switch (profile)
            {
                case BlacksiteBodyProfile.Ceiling: return BlacksiteRingPlane.CarrierFeet;
                case BlacksiteBodyProfile.Hazard: return BlacksiteRingPlane.None;
                default: return BlacksiteRingPlane.ContactGround;
            }
        }
    }
}
