namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>#874 Q11: where the ship's deck navigation is in its bake.</summary>
    internal enum BlacksiteDeckBake : byte { None, Baking, Ready }

    /// <summary>#874 Q11: what the arrival frame of an inbound or recall flight does next.</summary>
    internal enum BlacksiteTouchdownStep : byte { Fly, BeginBake, HoldForBake, Land }

    /// <summary>
    /// #874 Q11 (F11): the touchdown no longer bakes the deck in the frame it lands. The 23
    /// September log measured <c>BlacksiteShipController.Update</c> at 113 ms on that frame, the
    /// synchronous <c>NavMeshBuilder.BuildNavMeshData</c> among it. The hull now reaches its pose,
    /// starts an asynchronous bake of the same deck (<c>BlacksiteShipNavigation.BeginBuild</c>)
    /// and holds the pose, still Inbound or Returning, until the bake completes. Only then is it
    /// Landed. Every consumer of the deck (the deployment announcement and its access seal,
    /// the passage and ladder admissions, the recall's cargo rehook, the hull carving, the
    /// departure) keys on <c>IsGrounded</c>, so none of them can see a hull without a deck.
    /// Unity-free so the regression suite pins it.
    /// </summary>
    internal static class BlacksiteTouchdownPolicy
    {
        internal static BlacksiteTouchdownStep Arrival(bool arrived, BlacksiteDeckBake bake)
        {
            if (!arrived) return BlacksiteTouchdownStep.Fly;
            switch (bake)
            {
                case BlacksiteDeckBake.None: return BlacksiteTouchdownStep.BeginBake;
                case BlacksiteDeckBake.Baking: return BlacksiteTouchdownStep.HoldForBake;
                default: return BlacksiteTouchdownStep.Land;
            }
        }

        /// <summary>The ship is grounded (walkable, admitting, announcing) only on the Land step.</summary>
        internal static bool Grounds(BlacksiteTouchdownStep step) => step == BlacksiteTouchdownStep.Land;
    }

    /// <summary>#1293 P2: what one frame of the soldier deployment drain left behind.</summary>
    internal enum BlacksiteDeployResult : byte { Pending, Failed, Complete }

    /// <summary>
    /// #874 Q11 (F11), #1293 P2: the soldier deployment drain's frame rule; <c>MoonEscalationState</c>
    /// supplies the Unity hooks. One member per frame, stow and release alike (the old two-member,
    /// 8 ms rule read its budget only after a member, so a second, unbounded release shared the
    /// pass 7 frame that cost 62.49 ms). Never in the frame that queued the phase: the landed
    /// notification (ship Update) can run before the escalation tick in the same frame. The route
    /// configuration owns its frame; Infiltrating is published after the first release, because the
    /// manifest reads an Infiltrating mission with an empty roster as a squad that is gone.
    /// </summary>
    internal abstract class BlacksiteDeploySlice
    {
        /// <summary>
        /// #1293 R3: members a host frame takes through a frame-bounded squad step - one deployment
        /// release or stow here, one brain decision in <c>BlacksiteDecisionQueue</c>.
        /// </summary>
        internal const int MembersPerFrame = 1;
        internal int SoldierCount;
        internal int Cursor;
        internal int EnqueuedFrame;
        // #874 Q11: the squad route is configured (the release's first frame).
        internal bool Initialized;
        // #874 Q11: the mission has been marked Infiltrating (first releasing frame).
        internal bool Infiltrating;

        protected abstract void ConfigureSquad();
        protected abstract bool Stow(int member);
        protected abstract bool Release(int member);
        protected abstract void PublishInfiltrating();

        /// <summary>One frame: stows while inbound; once landed, the route, then releases.</summary>
        internal BlacksiteDeployResult Drain(int frame, bool releasing)
        {
            if (frame <= EnqueuedFrame)
                return BlacksiteDeployResult.Pending;
            if (releasing && !Initialized)
            {
                ConfigureSquad();
                Initialized = true;
                return BlacksiteDeployResult.Pending;
            }

            for (int taken = 0; taken < MembersPerFrame && Cursor < SoldierCount; taken++)
            {
                int member = Cursor++;
                if (!(releasing ? Release(member) : Stow(member)))
                    return BlacksiteDeployResult.Failed;
                if (releasing && !Infiltrating)
                {
                    Infiltrating = true;
                    PublishInfiltrating();
                }
            }

            // The stowed squad rides aboard until the landed notification turns it to the release.
            return releasing && Cursor >= SoldierCount ? BlacksiteDeployResult.Complete : BlacksiteDeployResult.Pending;
        }
    }
}
