namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    public sealed partial class BlacksiteSoldierAI
    {
        internal sealed partial class BlacksiteNavigator
        {
            // #874 S5.2 (#873): what the world ladder already knows, exposed so the server pose
            // write can put a direction on the replicated traversal kind. HandleWorldLadder sets
            // _worldLadderUp from the authored column (points[3].y > points[0].y) at the instant it
            // builds the motor, and ReleaseWorldLadder drops the motor, so the pair is only read
            // while a climb is live. It lives in its own partial file rather than in
            // BlacksiteNavigator.WorldLadder.cs, which the S5.1 column unit owns.
            internal bool IsWorldClimbingUp => _worldClimb != null
                && (_worldClimb.Returning ? !_worldLadderUp : _worldLadderUp);
        }
    }
}
