namespace Y4NGZCompany.Contracts.Defuse
{
    internal static class DefusePlacementPolicy
    {
        internal const float PreferredSeparation = 25f;
        internal static bool IsSeparated(float nearest) =>
            !float.IsNaN(nearest) && nearest >= PreferredSeparation;
    }
}
