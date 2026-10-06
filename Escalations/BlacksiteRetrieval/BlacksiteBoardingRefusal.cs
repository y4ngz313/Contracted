namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #1219 A3: the terms <c>BlacksiteSquadDirector.NotifyMemberBoarded</c> refuses a
    /// boarding with, in the order it tests them. #1219 C2: boarding is the board crossing, so no
    /// term is about where the member stands; each one is a legitimate wait the boarding hold names.
    /// </summary>
    internal static class BlacksiteBoardingRefusal
    {
        internal const string NotExtracting = "notExtracting", BoardingClosed = "boardingClosed",
            CommittedMotion = "committedMotion";
    }
}
