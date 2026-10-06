using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>#1293 R5: the squad weapons a monster CLASS takes a hit from.</summary>
    [Flags]
    internal enum BlacksiteWeaponReach : byte { None = 0, Baton = 1, Rifle = 2, Any = Baton | Rifle }

    /// <summary>#1293 R5: where a monster's attack on a soldier comes from.</summary>
    internal enum BlacksiteAttackWindow : byte
    {
        /// <summary>A BlacksiteVanillaMonsters contact row, or the generic default contact for a type no row names.</summary>
        Contact,
        /// <summary>The monster's own native attack: its own collision, its gun or its popped state.</summary>
        NativeState,
        /// <summary>A postfix on the monster's attack animation event (the Old Bird's stomp).</summary>
        AnimationEvent,
        /// <summary>A hook on the monster's native contact method (dog collision, slime ray, barber beat).</summary>
        NativeHook
    }

    /// <summary>
    /// #1293 R5: one vanilla monster class as the squad knows it. Every field has a reader:
    /// <see cref="Support"/> and <see cref="AttackWindow"/> are the MOTOR facts
    /// (<see cref="BlacksiteMonsterCapabilities.GetSupportLevel"/>, <c>BlacksiteVanillaMonsters.UsesGenericContact</c>);
    /// <see cref="LeasedMotor"/>, <see cref="SquadHunter"/>, <see cref="Attaches"/> and
    /// <see cref="LethalOnContactWhenMoving"/> are the lease's (BlacksiteVanillaMonsters and its patches);
    /// <see cref="Footprint"/> is BlacksiteHazardFootprint's; the rest are CLASS facts
    /// BlacksiteThreatAdapterRegistry turns into a BlacksiteThreatClass, and <see cref="Gaze"/> also keeps
    /// the Coil-Head disc (BlacksiteHazardFootprint.Live).
    /// </summary>
    internal sealed class BlacksiteMonsterCapability
    {
        internal Type Type;
        internal string DisplayName;
        internal BlacksiteMonsterSupportLevel Support;
        /// <summary>The motor facts answer for this exact type only; a subclass takes the default row's
        /// (today's exact-type support tests). Every class fact is inherited.</summary>
        internal bool MotorExactType;
        internal BlacksiteBodyProfile Profile = BlacksiteBodyProfile.Humanoid;
        internal BlacksiteWeaponReach HittableBy = BlacksiteWeaponReach.Any;
        internal BlacksiteAttackWindow AttackWindow = BlacksiteAttackWindow.Contact;
        /// <summary>Freezes while watched: a member that sees it watches it rather than evading it.</summary>
        internal bool Gaze;
        internal float ReactionRange = BlacksiteCombatPolicy.MonsterReactionRange;
        internal float ReactionConeDegrees = BlacksiteCombatPolicy.MonsterReactionConeDegrees;
        internal bool EngagementRefused, EscortObjective;
        /// <summary>The BlacksiteVanillaMonsters lease drives its motor: the strategy gate, Acquire/Release,
        /// TickPursuit. Inherited, so a modded subclass of a leased class is leased.</summary>
        internal bool LeasedMotor;
        /// <summary>Squad gunfire leases it to the shooter (SignalSoldierThreat) and, when the member it hunts
        /// falls, its lease passes to the nearest teammate it sees (the lease handoff).</summary>
        internal bool SquadHunter;
        /// <summary>It takes a soldier into its hands (a capture), so its native player collision waits while it holds one.</summary>
        internal bool Attaches;
        /// <summary>A touch while its own native gate lets it move kills (CaptureKillRawDamage).</summary>
        internal bool LethalOnContactWhenMoving;
        /// <summary>The ground a member keeps off around it, and the disc's radius (a slime's band is measured by its caller).</summary>
        internal BlacksiteHazardFootprintKind Footprint;
        internal float FootprintRadius;
        internal string RuntimeTypeName => Type.Name;
    }

    /// <summary>
    /// #1293 R5: the monster capability table - one row per vanilla class the squad names and one
    /// default row for every other type, which stays Unsupported so
    /// BlacksiteThreatResponsePolicy.DecideAcquired still evades an unclassified hunter (pass-4 P4).
    /// A type resolves to its own row, else to its nearest listed ancestor, else to the default row.
    /// Live vanilla state (a hostile butler, a hot sapsucker, a clinging flea, a popped Jester,
    /// EnemyType.canBeStunned) is read beside the row, never cached in it.
    /// </summary>
    internal static class BlacksiteMonsterCapabilities
    {
        // Assembly-CSharp.dll shipped 2026-04-21, SHA-256
        // 5F7DB5538B78DC408845A3002907619785AC9F9C6B6059D13DC9A602D9B65731.
        // All shipped IVisibleThreat consumers were audited before selecting this
        // non-player classification. No adapter may infer a player-shaped target.
        internal const ThreatType SoldierThreatType = ThreatType.BaboonHawk;

        internal static readonly BlacksiteMonsterCapability Default = new BlacksiteMonsterCapability
        { Type = typeof(EnemyAI), DisplayName = "Unknown", Support = BlacksiteMonsterSupportLevel.Unsupported };

        internal static readonly BlacksiteMonsterCapability[] Rows =
        {
            // Native IVisibleThreat consumers: their own scan acquires a soldier.
            new BlacksiteMonsterCapability { Type = typeof(BaboonBirdAI), DisplayName = "Baboon Hawk", MotorExactType = true,
                Support = BlacksiteMonsterSupportLevel.NativeVisibleThreat, AttackWindow = BlacksiteAttackWindow.NativeState },
            // #874 WP F: its HitEnemy is a no-op, so the squad refuses to engage it.
            new BlacksiteMonsterCapability { Type = typeof(RadMechAI), DisplayName = "Old Bird", MotorExactType = true,
                Support = BlacksiteMonsterSupportLevel.NativeVisibleThreat, Profile = BlacksiteBodyProfile.Giant,
                AttackWindow = BlacksiteAttackWindow.AnimationEvent, EngagementRefused = true },
            new BlacksiteMonsterCapability { Type = typeof(BushWolfEnemy), DisplayName = "Bush Wolf",
                Support = BlacksiteMonsterSupportLevel.Unsupported, AttackWindow = BlacksiteAttackWindow.NativeState },
            // #948 S12 U3: the hot bird; a cold one (BlacksiteKiwiThreatState.IsAvoided) is read live below.
            new BlacksiteMonsterCapability { Type = typeof(GiantKiwiAI), DisplayName = "Giant Sapsucker",
                Support = BlacksiteMonsterSupportLevel.NativeVisibleThreat, AttackWindow = BlacksiteAttackWindow.NativeState },
            new BlacksiteMonsterCapability { Type = typeof(MouthDogAI), DisplayName = "Eyeless Dog", MotorExactType = true,
                Support = BlacksiteMonsterSupportLevel.NativeEnemyCollision, AttackWindow = BlacksiteAttackWindow.NativeHook,
                LeasedMotor = true, Attaches = true },
            new BlacksiteMonsterCapability { Type = typeof(BlobAI), DisplayName = "Hygrodere", Support = BlacksiteMonsterSupportLevel.ContactHazard,
                Profile = BlacksiteBodyProfile.Hazard, HittableBy = BlacksiteWeaponReach.None, AttackWindow = BlacksiteAttackWindow.NativeHook,
                Footprint = BlacksiteHazardFootprintKind.Slime },
            // #1293 R5: the lease drives both since the registry adapters folded into it; the Thumper bites
            // through its contact row, the Nutcracker shoots its own native gun.
            new BlacksiteMonsterCapability { Type = typeof(CrawlerAI), DisplayName = "Thumper", MotorExactType = true,
                Support = BlacksiteMonsterSupportLevel.ExplicitStateAdapter, Profile = BlacksiteBodyProfile.GroundCrawler,
                LeasedMotor = true, SquadHunter = true },
            new BlacksiteMonsterCapability { Type = typeof(SandSpiderAI), DisplayName = "Bunker Spider",
                Support = BlacksiteMonsterSupportLevel.ExplicitStateAdapter, Profile = BlacksiteBodyProfile.GroundCrawler, LeasedMotor = true },
            new BlacksiteMonsterCapability { Type = typeof(NutcrackerEnemyAI), DisplayName = "Nutcracker", MotorExactType = true,
                Support = BlacksiteMonsterSupportLevel.ExplicitStateAdapter, AttackWindow = BlacksiteAttackWindow.NativeState,
                LeasedMotor = true, SquadHunter = true },
            new BlacksiteMonsterCapability { Type = typeof(FlowermanAI), DisplayName = "Bracken", Support = BlacksiteMonsterSupportLevel.ExplicitStateAdapter,
                LeasedMotor = true, Attaches = true },
            // #1293 D3: any player or soldier looking at it freezes it (native stop/go plus
            // BlacksiteCoilObservationPatch); while it moves its contact kills (Attacks.cs, SpringManAI row).
            new BlacksiteMonsterCapability { Type = typeof(SpringManAI), DisplayName = "Coil-Head", Support = BlacksiteMonsterSupportLevel.ExplicitStateAdapter,
                HittableBy = BlacksiteWeaponReach.None, Gaze = true, ReactionRange = 22f, ReactionConeDegrees = 68f,
                LeasedMotor = true, LethalOnContactWhenMoving = true,
                Footprint = BlacksiteHazardFootprintKind.Coil, FootprintRadius = BlacksiteHazardPolicy.CoilFootprintRadius },
            new BlacksiteMonsterCapability { Type = typeof(CentipedeAI), DisplayName = "Snare Flea",
                Support = BlacksiteMonsterSupportLevel.ExplicitStateAdapter, Profile = BlacksiteBodyProfile.GroundCrawler,
                LeasedMotor = true, Attaches = true },
            // Hittable only while hostile (behaviour state 2), read live below.
            new BlacksiteMonsterCapability { Type = typeof(ButlerEnemyAI), DisplayName = "Butler", Support = BlacksiteMonsterSupportLevel.ExplicitStateAdapter,
                LeasedMotor = true },
            new BlacksiteMonsterCapability { Type = typeof(ClaySurgeonAI), DisplayName = "Barber", Support = BlacksiteMonsterSupportLevel.ExplicitStateAdapter,
                Profile = BlacksiteBodyProfile.Hazard, HittableBy = BlacksiteWeaponReach.None, AttackWindow = BlacksiteAttackWindow.NativeHook,
                LeasedMotor = true },
            // #1262 P4: EnemyType.canBeStunned is false for it, so a baton never reaches it.
            new BlacksiteMonsterCapability { Type = typeof(ForestGiantAI), DisplayName = "Forest Keeper",
                Support = BlacksiteMonsterSupportLevel.ExplicitStateAdapter, Profile = BlacksiteBodyProfile.Giant,
                LeasedMotor = true, Attaches = true },
            // A contact row only: its own swarm strategy is never leased.
            new BlacksiteMonsterCapability { Type = typeof(ButlerBeesEnemyAI), DisplayName = "Butler Bees", Support = BlacksiteMonsterSupportLevel.ContactHazard,
                Profile = BlacksiteBodyProfile.Hazard, HittableBy = BlacksiteWeaponReach.None },
            // #1020 S6: a hive is not a target, and harmless bees and wildlife cost a magazine for nothing.
            new BlacksiteMonsterCapability { Type = typeof(RedLocustBees), DisplayName = "Red Bees", Support = BlacksiteMonsterSupportLevel.Unsupported,
                Profile = BlacksiteBodyProfile.Hazard, HittableBy = BlacksiteWeaponReach.None, AttackWindow = BlacksiteAttackWindow.NativeState,
                Footprint = BlacksiteHazardFootprintKind.Hive, FootprintRadius = BlacksiteHazardPolicy.HiveFootprintRadius },
            new BlacksiteMonsterCapability { Type = typeof(DocileLocustBeesAI), DisplayName = "Docile Bees", Support = BlacksiteMonsterSupportLevel.Unsupported,
                Profile = BlacksiteBodyProfile.Hazard, HittableBy = BlacksiteWeaponReach.None, AttackWindow = BlacksiteAttackWindow.NativeState },
            new BlacksiteMonsterCapability { Type = typeof(DoublewingAI), DisplayName = "Manticoil", Support = BlacksiteMonsterSupportLevel.Unsupported,
                Profile = BlacksiteBodyProfile.Hazard, HittableBy = BlacksiteWeaponReach.None, AttackWindow = BlacksiteAttackWindow.NativeState },
            // WP B: these three never override EnemyAI.HitEnemy, so no hit from anyone decrements enemyHP.
            new BlacksiteMonsterCapability { Type = typeof(PufferAI), DisplayName = "Spore Lizard", Support = BlacksiteMonsterSupportLevel.Unsupported,
                HittableBy = BlacksiteWeaponReach.None, AttackWindow = BlacksiteAttackWindow.NativeState },
            // #1086: walked away from; it kills only once popped (JesterPopped, the Attacks.cs JesterAI row).
            new BlacksiteMonsterCapability { Type = typeof(JesterAI), DisplayName = "Jester", Support = BlacksiteMonsterSupportLevel.ContactHazard,
                HittableBy = BlacksiteWeaponReach.None, AttackWindow = BlacksiteAttackWindow.NativeState, LeasedMotor = true },
            new BlacksiteMonsterCapability { Type = typeof(DressGirlAI), DisplayName = "Ghost Girl", Support = BlacksiteMonsterSupportLevel.Unsupported,
                HittableBy = BlacksiteWeaponReach.None, AttackWindow = BlacksiteAttackWindow.NativeState },
            // #1097: the growth is a plant with no HitEnemy and no collision; the bloom is the creature.
            new BlacksiteMonsterCapability { Type = typeof(CadaverGrowthAI), DisplayName = "Cadaver Growth", Support = BlacksiteMonsterSupportLevel.Unsupported,
                HittableBy = BlacksiteWeaponReach.None, AttackWindow = BlacksiteAttackWindow.NativeState },
            new BlacksiteMonsterCapability { Type = typeof(CadaverBloomAI), DisplayName = "Cadaver Bloom", Support = BlacksiteMonsterSupportLevel.ExplicitStateAdapter,
                LeasedMotor = true },
            // #963 / #1126: the contract objective. Hittable by class (a player's baton still stuns and
            // kills him on every peer); host weapons exclude him and the sight gate still sees him.
            new BlacksiteMonsterCapability { Type = typeof(Y4NGZCompany.Contracts.Whistleblower.WhistleblowerEnemyAI), DisplayName = "Witness",
                Support = BlacksiteMonsterSupportLevel.Unsupported, AttackWindow = BlacksiteAttackWindow.NativeState, EscortObjective = true },
        };

        private static readonly Dictionary<Type, BlacksiteMonsterCapability> Listed = BuildListed();
        // Per concrete type, so the ancestor walk runs once per type for the process.
        private static readonly Dictionary<Type, BlacksiteMonsterCapability> Resolved = new Dictionary<Type, BlacksiteMonsterCapability>();
        private static readonly HashSet<JesterAI> PoppedJesters = new HashSet<JesterAI>();
        private static bool _logged;

        private static Dictionary<Type, BlacksiteMonsterCapability> BuildListed()
        {
            var listed = new Dictionary<Type, BlacksiteMonsterCapability>();
            foreach (var row in Rows) listed.Add(row.Type, row);
            return listed;
        }

        /// <summary>This contact's row: its own type, else its nearest listed ancestor, else <see cref="Default"/>.</summary>
        internal static BlacksiteMonsterCapability For(EnemyAI enemy)
        {
            if (ReferenceEquals(enemy, null)) return Default;
            var type = enemy.GetType();
            if (Resolved.TryGetValue(type, out var row)) return row;
            for (var probe = type; probe != null && probe != typeof(EnemyAI); probe = probe.BaseType)
                if (Listed.TryGetValue(probe, out row)) break;
            return Resolved[type] = row ?? Default;
        }

        /// <summary>The row whose motor facts apply: <see cref="Default"/> for a subclass of a <see cref="BlacksiteMonsterCapability.MotorExactType"/> row.</summary>
        internal static BlacksiteMonsterCapability MotorFor(EnemyAI enemy)
        {
            var row = For(enemy);
            return !row.MotorExactType || enemy.GetType() == row.Type ? row : Default;
        }

        internal static BlacksiteMonsterSupportLevel GetSupportLevel(EnemyAI enemy)
        {
            // #948 S12 U3: cold is AvoidUnlessHostile, hot is the NativeVisibleThreat it shipped as.
            if (BlacksiteKiwiThreatState.IsAvoided(enemy)) return BlacksiteMonsterSupportLevel.AvoidUnlessHostile;
            if (IsVerifiedVisibleThreatConsumer(enemy)) return BlacksiteMonsterSupportLevel.NativeVisibleThreat;
            var support = MotorFor(enemy).Support;
            // A subclass of a leased type the exact-type rule turned away is still leased (a modded dog).
            return support == BlacksiteMonsterSupportLevel.Unsupported && For(enemy).LeasedMotor
                ? BlacksiteMonsterSupportLevel.ExplicitStateAdapter : support;
        }

        /// <summary>"An adapter may drive it": a bird the squad walks around and a contact hazard are driven by nothing.</summary>
        internal static bool IsSupported(EnemyAI enemy)
        {
            var support = GetSupportLevel(enemy);
            return support != BlacksiteMonsterSupportLevel.Unsupported
                && support != BlacksiteMonsterSupportLevel.ContactHazard
                && support != BlacksiteMonsterSupportLevel.AvoidUnlessHostile;
        }

        /// <summary>The native IVisibleThreat consumers, hot or cold. NoInlining: the native fixtures
        /// replace this call to name their stand-in subclass the shipped type.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool IsVerifiedVisibleThreatConsumer(EnemyAI enemy)
            => enemy != null && MotorFor(enemy).Support == BlacksiteMonsterSupportLevel.NativeVisibleThreat;

        /// <summary>
        /// The row's weapons with the two STATE exclusions applied (a butler outside its hostile
        /// state 2, a cold sapsucker) and the baton dropped for a class vanilla cannot stun. Type
        /// and replicated vanilla state only, so every peer reaches the same answer.
        /// </summary>
        internal static BlacksiteWeaponReach HittableBy(EnemyAI enemy)
        {
            if (ReferenceEquals(enemy, null)) return BlacksiteWeaponReach.Any;
            if ((enemy is ButlerEnemyAI && enemy.currentBehaviourStateIndex != 2) || BlacksiteKiwiThreatState.IsAvoided(enemy))
                return BlacksiteWeaponReach.None;
            var reach = For(enemy).HittableBy;
            return enemy.enemyType == null || enemy.enemyType.canBeStunned ? reach : reach & ~BlacksiteWeaponReach.Baton;
        }

        /// <summary>The row's body profile with the two STATE overrides: a flea on the ceiling, a cold sapsucker.</summary>
        internal static BlacksiteBodyProfile ProfileOf(EnemyAI enemy)
        {
            if (enemy is CentipedeAI flea && flea.clingingToCeiling) return BlacksiteBodyProfile.Ceiling;
            if (BlacksiteKiwiThreatState.IsAvoided(enemy)) return BlacksiteBodyProfile.Hazard;
            return For(enemy).Profile;
        }

        /// <summary>JesterAI behaviour state 2 is the popped, hunting machine. 0 and 1 are the walk and the crank.</summary>
        internal const int JesterPoppedState = 2;

        /// <summary>
        /// #1086: true once this Jester has been observed popped, and for the rest of the round after
        /// that - vanilla drops it back to state 0 whenever it loses a target, and "it is quiet again"
        /// is not a reason to walk back toward it. The observation IS the read: every caller asks from
        /// inside a reaction that has already seen the contact. Host state, cleared by <see cref="Reset"/>.
        /// </summary>
        internal static bool JesterPopped(EnemyAI enemy)
        {
            var jester = enemy as JesterAI;
            if (jester == null) return false;
            if (jester.currentBehaviourStateIndex >= JesterPoppedState && !PoppedJesters.Contains(jester))
            {
                // A destroyed contact is a null key that still hashes; pruned only when one is added.
                PoppedJesters.RemoveWhere(popped => popped == null);
                PoppedJesters.Add(jester);
                if (BlacksiteDiagnostics.Enabled)
                    BlacksiteDiagnostics.Log("JesterThreat",
                        $"jester={jester.GetInstanceID()}, latch=popped, state={jester.currentBehaviourStateIndex}");
            }
            return PoppedJesters.Contains(jester);
        }

        /// <summary>Round reset, from BlacksiteMonsterCombatAdapterRegistry.Reset beside the sapsucker latch.</summary>
        internal static void Reset() => PoppedJesters.Clear();

        /// <summary>The table, once per process, on the debug channel the compatibility matrix used.</summary>
        internal static void LogTableOnce()
        {
            if (_logged) return;
            _logged = true;
            foreach (var row in Rows) LogRow(row);
            LogRow(Default);
        }

        private static void LogRow(BlacksiteMonsterCapability row)
            => ContractsBootstrap.Log?.LogDebug(
                $"[MoonContracts.Blacksite] Monster capability {row.DisplayName}/{row.RuntimeTypeName}: {row.Support}"
                + $"{(row.MotorExactType ? " (exact type)" : "")}; profile={row.Profile}, hittableBy={row.HittableBy}, "
                + $"attack={row.AttackWindow}, gaze={row.Gaze}, reaction={row.ReactionRange:0}m/{row.ReactionConeDegrees:0}deg, "
                + $"engagementRefused={row.EngagementRefused}, escortObjective={row.EscortObjective}, leased={row.LeasedMotor}, "
                + $"squadHunter={row.SquadHunter}, attaches={row.Attaches}, lethalWhenMoving={row.LethalOnContactWhenMoving}, "
                + $"footprint={row.Footprint}/{row.FootprintRadius:0.0}m.");
    }
}
