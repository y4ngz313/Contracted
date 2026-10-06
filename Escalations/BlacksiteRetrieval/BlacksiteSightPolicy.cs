namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // #874 S10.2: the pure half of the player sight test. Unity-free on purpose, so every number
    // below is pinned directly by BlacksiteReactiveRegression rather than only by the native
    // fixture. Nothing here widens a detection radius: VisionRange is the shipped 15 m, the keep
    // range is a RELEASE hysteresis that is never spent on an unacquired contact, and the grace is
    // a consumer smoothing of a sample the diagnostic still reports raw.
    internal static class BlacksiteSightPolicy
    {
        // The cadence of the squad's ONE perception pass (#1293 R1: BlacksiteSquadDirector's
        // UpdatePerceptionServer, which replaced every member's own scan clock). Unchanged value
        // (0.10 s); it lives here because the keep-range hysteresis is derived from it and the two
        // must not be able to drift apart.
        internal const float VisualTickSeconds = .10f;

        // A walking Lethal Company player. Not chosen: run 3's six pure boundary flips sit at
        // 15.02 to 15.60 m with dot 1.00, which is one visual tick of movement either side of the
        // 15 m cliff, and 4.6 m/s is the walk speed that produces exactly that spread.
        internal const float PlayerWalkSpeed = 4.6f;

        // Acquire range. Unchanged at 15 m since the soldier's own scan read it.
        internal const float VisionRange = 15f;

        // Release hysteresis. One visual tick of a walk is PlayerWalkSpeed * VisualTickSeconds =
        // 0.46 m, so the tick pair that produced run 3's flip/unflip covers 0.92 m; 1 m is the
        // smallest whole metre above that, which is what makes a straddled boundary unable to
        // produce a transition within one pair of ticks. Release only: see InRange.
        internal const float KeepRangeHysteresis = 1f;
        internal const float VisionKeepRange = VisionRange + KeepRangeHysteresis;

        // Consumer grace: three and a half visual ticks, so one blocked tick never reads as a lost
        // contact, and no more, so it can never mask a genuine sight loss from the brain.
        internal const float VisualGraceSeconds = .35f;

        // Eye height. BlacksiteBodyCollision is a Unity type, so the soldier's own body geometry
        // arrives as arguments (CenterY, Height) and only the head's half height is owned here.
        // 0.15 m under the capsule top puts the origin at eye level INSIDE the soldier's own body,
        // where the old 2.18 m origin sat 0.28 m above it at lintel and ceiling-beam height.
        internal const float HeadHalfHeight = .15f;
        internal static float EyeHeight(float bodyCenterY, float bodyHeight)
            => bodyCenterY + bodyHeight / 2f - HeadHalfHeight;

        // The three body samples. The camera is the shipped aim point and is unchanged.
        //
        // ChestSampleHeight 1.20 m is the height the cover search already probes a contact at
        // (BlacksiteObjectiveManifestController.Players.cs, `center + Vector3.up * 1.2f`), so a
        // cover point scored as having sight of the contact and the sight test itself now agree on
        // the same point of the same body.
        //
        // ShinSampleHeight 0.35 m is one LinecastTolerance above a 0.30 m stair riser, so a floor
        // lip on the far side cannot eat the low ray before it reaches the body.
        //
        // The 0.85 m that separates them is the consequence, and it is the number that carries the
        // guarantee: the Lethal Company player capsule is 0.4 m in radius, so 0.80 m wide, and
        // 0.80 + 0.05 m of linecast tolerance is 0.85. No horizontal band of geometry thinner than
        // a body can therefore hide both. (0.80 m spacing would have made the two samples exactly
        // one body apart with no tolerance for a ray that grazes the capsule wall; 0.85 is chosen
        // over it for that reason, and because 1.20 is the height another subsystem already uses.)
        internal const float PlayerCapsuleDiameter = .8f;
        internal const float LinecastTolerance = .05f;
        internal const float StairRiserHeight = .3f;
        internal const float ShinSampleHeight = StairRiserHeight + LinecastTolerance;
        internal const float ChestSampleHeight = 1.2f;
        internal const float BodySampleSpacing = ChestSampleHeight - ShinSampleHeight;

        // Closed vocabulary for the `rays=` key on [UAT][Sighting]: the first sample that cleared.
        internal const string RayCamera = "camera";
        internal const string RayChest = "chest";
        internal const string RayShins = "shins";
        internal const string RayNone = "none";

        // How far a last-known point may be extrapolated. 1.0 s is 4.6 m at a walk, about one
        // Vow corridor width; past that the member should be searching, not chasing a guess.
        internal const float PredictionHorizonSeconds = 1f;

        // Release hysteresis, not acquisition: `wasVisible` is false for every contact the member
        // is not already tracking, so nothing is ever acquired past VisionRange.
        internal static bool InRange(float distance, bool wasVisible)
            => distance <= (wasVisible ? VisionKeepRange : VisionRange);

        // #1126: weapon exclusion is not blindness.
        //
        // BlacksiteThreatAdapterRegistry.WeaponsExcludedFor answers one question - "never shoot at,
        // swing at or alert on this contact". #1091 added the witness to it so the squad would stop
        // batoning the man it is paid to escort out, and the squad went BLIND to him instead:
        // BlacksiteSoldierAI.CanConsiderEnemy is both the weapons gate and the first gate of
        // EvaluateEnemySight, so CanSeeEnemyForSquad went false, and the #990 knowledge model - which
        // only ever learns through that one test - recorded no sighting for the whole 2026-09-18
        // March round. No [Custody] line, no pursuit floor, a 6.6 m/s man walking away.
        //
        // So the two gates are asked separately, and this is the only term that separates them: an
        // excluded contact is unseen UNLESS it is the contract's escort objective
        // (BlacksiteThreatAdapterRegistry.IsEscortObjective). Every other excluded contact - a cold
        // sapsucker, a swarm, a slime, a Puffer, an instance the give-up memo has written off - stays
        // exactly as unseen as it is today, so nothing moves in how the squad perceives, reports or
        // retreats from any of them. Seeing him buys him nothing on the weapons side: the exclusion
        // itself is untouched, and every weapons consumer still reads it.
        internal static bool BlindedByWeaponExclusion(bool weaponsExcluded, bool isEscortObjective)
            => weaponsExcluded && !isEscortObjective;

        // The consumer answer. `rawSample` is what the diagnostic records and what the score
        // script's flip count measures; this is what melee and rifle gates read.
        internal static bool Visible(bool rawSample, float lastRawTrueAt, float now)
            => rawSample || HeldByGrace(rawSample, lastRawTrueAt, now);

        // True exactly when the consumer answer above is being carried by the grace rather than by
        // a live sample. This is the `grace=` key on the sighting line.
        internal static bool HeldByGrace(bool rawSample, float lastRawTrueAt, float now)
        {
            if (rawSample || float.IsNegativeInfinity(lastRawTrueAt))
                return false;
            float elapsed = now - lastRawTrueAt;
            return elapsed >= 0f && elapsed <= VisualGraceSeconds;
        }

        // The clamped age a last-known point may be extrapolated over.
        internal static float PredictionElapsed(float now, float lastVisualAt)
        {
            float elapsed = now - lastVisualAt;
            if (!(elapsed > 0f))
                return 0f;
            return elapsed < PredictionHorizonSeconds ? elapsed : PredictionHorizonSeconds;
        }
    }
}
