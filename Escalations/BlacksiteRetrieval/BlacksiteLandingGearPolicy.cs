namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // #873: the numbers behind the landing gear, the lip drop and the ladder climb. Unity-free so
    // the headless suite can pin them and the Unity fixture can copy the file untouched. Every
    // ship-local figure below is measured from the shipped prefab dump (deck datum y 0): the four
    // thruster pod centres are the gear feet, the two short ladders are the return route.
    internal static class BlacksiteLandingGearPolicy
    {
        // Deck height above the highest gear foot: measured per moon from the crew ship datum (the
        // smallest of its three terrain drops), clamped, defaulting to Dine's measurement.
        internal const float DefaultFootToDeck = 3.875485f, MinFootToDeck = 3.696325f, MaxFootToDeck = 3.875485f;
        internal const float ContactTolerance = .2f, MaximumTilt = 8f, LowestRung = -5.913885f;
        internal static readonly float[] FootDepths = { 3.875485f, 3.875485f, 3.875485f, 3.696325f };
        // Feet must sit within this height band of each other (about 4 degrees across the wheelbase)
        // and on ground shallower than 37 degrees. The catwalk underside is 0.16 m below the deck
        // datum; the belly box stops this far above the foot plane so it never sees the feet's ground.
        internal const float FootSpreadMax = .75f, FootMinNormalY = .8f, CatwalkUnderside = -.23f, BellySkin = .15f;
        // Drop landing 0.9 m beyond the lip; its three columns must agree, and the drop must be a real
        // step down but no more than the ceiling above the measured deck height.
        internal const float DropZ = 12.65f, DropUnevenMax = .35f, DropMinHeight = .15f, DropCeilingAbove = .8f;
        // Ladder mount 1.2 m out from the rungs (preflight rejects mounts inside the planned hull carve), the
        // players' LadderNode column 0.55 m out (#1271 S6), the top 0.55 m inboard, the passage zone radius.
        internal const float LadderMountOut = 1.2f, LadderNodeOut = .55f, LadderTopIn = .55f, LadderZoneRadius = 3.5f;
        internal static readonly (string Name, float X, float Z)[] Feet =
        {
            ("FL", -2.87548f, 3.75275f), ("FR", 3.10958f, 3.48123f), ("BL", -3.67948f, -5.82525f), ("BR", 3.57364f, -5.86816f),
        };
        internal static readonly (string Side, float X, float Z, float Outward)[] Ladders =
        {
            ("starboard", 6f, -4.73f, 1f), ("port", -4.5f, 8.1f, -1f),
        };
        // Catwalk plate renderer bounds (ship-local), confirmed against the bundle by the fixture.
        internal const float CatwalkTop = -.07f, CatwalkHalfWidth = 6.1f, CatwalkHalfLength = 11.72f;
        internal static float ResolveFootToDeck(float a, float b, float c, out string note)
        {
            note = "measured BlacksiteGeometryV2 foot depth; crew datum is diagnostic only";
            return DefaultFootToDeck;
        }
        internal static float DropCeiling(float footToDeck) => footToDeck + DropCeilingAbove;
        internal static float BellyDepth(float footToDeck) => footToDeck + CatwalkUnderside - BellySkin;
        // #874 S3 (rule 2): the ship ladder is climbed at the ladder clip's rate.
        internal const float ClimbSpeed = 1.2f, MountTurnSeconds = .2f;
        // #874 S3 (rule 2): a rope crossing takes 1.3 s per 10 m of drop height, either direction.
        internal const float RopeSecondsPerTenMetres = 1.3f;
        // #874 S3 (rule 2): the lip drop, deck to landing, is one half-second crossing.
        internal const float LipDropSeconds = .5f;
        // #1219 A5: the longest boarding crossing the gear allows, the member unit of the ShipLadder
        // wait's bound: the ladder from the lowest foot TryLadderFoot accepts (LowestRung - .05)
        // to its top, horizontally the mount out plus its .6 m nav snap plus the top in, at
        // ClimbSpeed (5.3 s). The rope at its 18 m rappel ceiling is shorter (2.3 s), the lip drop too.
        internal static readonly float LongestBoardCrossingSeconds = (float)System.Math.Sqrt(
            (LadderMountOut + .6f + LadderTopIn) * (LadderMountOut + .6f + LadderTopIn)
            + (LowestRung - .05f) * (LowestRung - .05f)) / ClimbSpeed;

        // #991: Unarmed Jump (2.3333 s) crosses neutral on its Armature Y up at 0.80 s, down at 1.47 s (jump-wiring-plan.md
        // 2.2). #1271: the clip starts at that takeoff (measured from the clip, pinned by LadderPresentationChecks.RunJumpTiming);
        // a crossing shorter than the minimum plays no clip.
        internal const float JumpStandingAirtime = .67f, JumpTakeoffNormalizedTime = .343f, JumpClipMinimumCrossingSeconds = .6f;
    }
}
