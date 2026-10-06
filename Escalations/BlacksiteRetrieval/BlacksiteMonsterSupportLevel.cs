namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // #1019 S5: MOVED here verbatim from BlacksiteThreatCompatibility.cs:6-17, with its values,
    // its ordinals and its comment unchanged. Nothing about the type moved: same name, same
    // namespace, same assembly, same wire format, so every by-name reflection row in the native
    // fixtures and every `BlacksiteMonsterSupportLevel.X` in production resolves exactly as before.
    //
    // Why it had to move: BlacksiteThreatClass carries this enum (design note B.3), and
    // BlacksiteThreatResponse.cs is compiled by four headless suites that have no UnityEngine at
    // all. Its table (BlacksiteMonsterCapabilities.cs since #1293 R5) is Unity-BOUND - it names
    // EnemyAI and two dozen vanilla types - so it can never be linked into them. The enum itself is a
    // byte enum with no dependency of any kind, which is why moving the declaration, rather than
    // duplicating it or weakening the field to a byte, is the answer.
    internal enum BlacksiteMonsterSupportLevel : byte
    {
        NativeVisibleThreat = 0,
        NativeEnemyCollision = 1,
        ExplicitStateAdapter = 2,
        Unsupported = 3,
        ContactHazard = 4,
        // #948 S12 U3: appended, never renumbered - this enum is wire format. A monster the squad
        // walks around while it is indifferent and fights only once it has committed to a soldier.
        // The Giant Sapsucker is the first; BlacksiteKiwiThreatState owns the hot/cold decision.
        AvoidUnlessHostile = 5
    }
}
