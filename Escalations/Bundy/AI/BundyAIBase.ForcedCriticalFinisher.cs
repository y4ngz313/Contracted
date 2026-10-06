using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;

namespace Y4NGZCompany.Escalations.Bundy.AI;

public abstract partial class BundyAIBase
{
    private const float ForcedCriticalFinisherDurationSeconds = 6f;
    private const float ForcedCriticalFinisherPositionTolerance = 0.75f;
    private const float ForcedCriticalFinisherYawToleranceDegrees = 40f;
    private const float ForcedCriticalFinisherFrontArcMinDot = 0.25f;
    private const float ForcedCriticalApproachLogIntervalSeconds = 0.5f;

    // #716 G6: vanilla's player "Limp" bool was reasserted by string name on every frame of the
    // six-second window, hashing the name each time. Pre-hash it and write only on change.
    private static readonly int VanillaLimpHash = Animator.StringToHash("Limp");

    private int _forcedCriticalPlayerIndex = -1;
    private int _forcedCriticalGeneration;
    private double _forcedCriticalExpiresAt = double.NegativeInfinity;
    private int _bundyBasicDamageSerial;

    private int _localForcedCriticalPlayerIndex = -1;
    private int _localForcedCriticalGeneration;
    private double _localForcedCriticalExpiresAt = double.NegativeInfinity;
    private int _lastBundyBasicDamageSerial;
    private bool _hasForcedCriticalApproachForward;
    private Vector3 _forcedCriticalApproachForward;
    private float _nextForcedCriticalApproachLogAt;

    // #374 heartbeat backstop: the window tick only runs from this Bundy instance's Update, so
    // a Bundy that dies/despawns mid-window (or whose Update stalls) leaves the local player
    // force-critical with nothing left to clear it. Y4NGZCompanyHost.LateUpdate polls this
    // static deadline (window duration + grace) and force-expires through the normal end path.
    // One static slot is enough: only one forced-critical window exists at a time, and a newer
    // Begin overwriting an older registration still clears everything this class asserts. The
    // reference is held as a plain C# reference on purpose — a Destroyed Bundy is exactly the
    // case the backstop exists for, and its managed object stays callable after Destroy.
    private const float ForcedCriticalBackstopGraceSeconds = 2f;
    private static BundyAIBase _localForcedCriticalBackstopInstance;
    private static double _localForcedCriticalBackstopDeadline = double.NegativeInfinity;

    private void ApplyBundyBasicAttackDamage(PlayerControllerB victim, int damage)
    {
        if (!IsLivingPlayer(victim) || damage <= 0)
            return;

        // EnemyAI is server-owned in the live game. DamagePlayer itself is victim-owner-only, so the
        // server tells every peer about the hit and only the victim owner actually invokes vanilla
        // damage. This also makes the critical-injury start occur after DamagePlayer's crit-save logic.
        // A non-server peer reaching this means the server-ownership invariant broke — drop the hit
        // rather than apply unreplicated local damage (issue #275).
        if (!IsServer)
        {
            LogError($"ApplyBundyBasicAttackDamage ran on a non-server peer; dropping hit on {PlayerLabel(victim)}.");
            return;
        }

        int playerIndex = GetPlayerIndex(victim);
        if (playerIndex < 0)
            return;

        int preHitHealth = victim.health;
        bool wouldSurvive = preHitHealth > damage || (damage < 50 && !victim.criticallyInjured);
        bool forceCritical = UsesForcedCriticalFinisherGate &&
            preHitHealth < 100 &&
            wouldSurvive;
        int generation = 0;
        float durationSeconds = 0f;

        if (forceCritical)
        {
            if (_forcedCriticalPlayerIndex >= 0 && _forcedCriticalPlayerIndex != playerIndex)
                ReleaseForcedCriticalFinisherWindow("replaced by a newer victim");

            generation = ++_forcedCriticalGeneration;
            durationSeconds = ForcedCriticalFinisherDurationSeconds;
            _forcedCriticalPlayerIndex = playerIndex;
            // #374: expiry is duration-based on the wire. Every peer (this server included)
            // anchors its own deadline to its local monotonic Time.timeAsDouble at receive
            // time, so no server-clock value crosses the wire and a peer whose
            // NetworkManager clock disagrees (or is not listening) cannot mis-time the window.
            _forcedCriticalExpiresAt = Time.timeAsDouble + durationSeconds;
            WriteInfoLine(
                $"BundyForcedCritical begin: victim={PlayerLabel(victim)}, generation={generation}, " +
                $"preHitHealth={preHitHealth}, damage={damage}, duration={ForcedCriticalFinisherDurationSeconds:F1}s.");
        }

        int serial = ++_bundyBasicDamageSerial;
        ApplyBundyBasicAttackDamageClientRpc(
            playerIndex,
            damage,
            forceCritical,
            generation,
            durationSeconds,
            serial);
    }

    [ClientRpc]
    public void ApplyBundyBasicAttackDamageClientRpc(
        int playerIndex,
        int damage,
        bool forceCritical,
        int generation,
        float durationSeconds,
        int serial)
    {
        if (serial <= _lastBundyBasicDamageSerial)
            return;
        _lastBundyBasicDamageSerial = serial;

        PlayerControllerB victim = ResolvePlayerByIndex(playerIndex);
        if (victim == null || victim.isPlayerDead || !victim.isPlayerControlled)
            return;

        if (victim.IsOwner)
            victim.DamagePlayer(damage, true, true, CauseOfDeath.Bludgeoning);

        if (!forceCritical || victim.isPlayerDead)
            return;

        // MakeCriticallyInjured is the real vanilla state: Limp animator, heavy bleeding, sprint
        // suppression and limp movement. Only the player owner calls it because its ServerRpc requires
        // ownership; every peer also records and reasserts the visible fields for the full six seconds.
        if (victim.IsOwner)
        {
            // Exact-20 dead zone (#374): vanilla's only in-round crit clear is the owner-side
            // LateUpdate regen loop, which runs solely while health < 20 (1 HP/s up to 20,
            // repainting the vignette each tick, then MakeCriticallyInjured(false)). A survivor
            // parked at 20+ — chainsaw's 40+40 lands exactly there — would keep the forced limp
            // with no vanilla path out. Mirror DamagePlayer's own crit-save value and drop to 5
            // through a second DamagePlayer call: it repaints the vignette and visor cracks
            // itself and its DamagePlayerServerRpc replicates the new health to every peer, the
            // same sync path the hit above already used. Recovery then belongs to vanilla.
            if (victim.health >= 20)
                victim.DamagePlayer(victim.health - 5, false, true, CauseOfDeath.Bludgeoning);
            if (!victim.criticallyInjured)
                victim.MakeCriticallyInjured(true);
        }
        BeginLocalForcedCriticalFinisherWindow(victim, playerIndex, generation, durationSeconds);
    }

    private void BeginLocalForcedCriticalFinisherWindow(
        PlayerControllerB victim,
        int playerIndex,
        int generation,
        float durationSeconds)
    {
        _localForcedCriticalPlayerIndex = playerIndex;
        _localForcedCriticalGeneration = generation;
        _localForcedCriticalExpiresAt = Time.timeAsDouble + durationSeconds;
        _localForcedCriticalBackstopInstance = this;
        _localForcedCriticalBackstopDeadline =
            Time.timeAsDouble + durationSeconds + ForcedCriticalBackstopGraceSeconds;
        ReassertVanillaCriticalInjury(victim);
    }

    /// <summary>
    /// #374 backstop, ticked from <c>Y4NGZCompanyHost.LateUpdate</c>: the plugin host outlives
    /// every Bundy instance, so this fires even after the Bundy that opened the window has been
    /// destroyed. Routes through the normal end path so the vanilla clear and repaint apply.
    /// </summary>
    internal static void TickForcedCriticalFinisherBackstop()
    {
        BundyAIBase instance = _localForcedCriticalBackstopInstance;
        if (instance is null || Time.timeAsDouble < _localForcedCriticalBackstopDeadline)
            return;

        _localForcedCriticalBackstopInstance = null;
        _localForcedCriticalBackstopDeadline = double.NegativeInfinity;
        instance.EndLocalForcedCriticalFinisherWindow(
            instance._localForcedCriticalPlayerIndex,
            instance._localForcedCriticalGeneration,
            notifyVanilla: true);
    }

    private void UpdateForcedCriticalFinisherState()
    {
        // #374: all deadlines are anchored to this machine's monotonic clock at receive time
        // (duration-based protocol), so ticking compares against the same local clock.
        double now = Time.timeAsDouble;

        if (_localForcedCriticalPlayerIndex >= 0)
        {
            PlayerControllerB localVictim = ResolvePlayerByIndex(_localForcedCriticalPlayerIndex);
            if (localVictim == null || localVictim.isPlayerDead || now >= _localForcedCriticalExpiresAt)
                EndLocalForcedCriticalFinisherWindow(_localForcedCriticalPlayerIndex, _localForcedCriticalGeneration, notifyVanilla: true);
            else
                ReassertVanillaCriticalInjury(localVictim);
        }

        if (!IsServer || _forcedCriticalPlayerIndex < 0)
            return;

        PlayerControllerB serverVictim = ResolvePlayerByIndex(_forcedCriticalPlayerIndex);
        if (serverVictim == null || serverVictim.isPlayerDead)
            ReleaseForcedCriticalFinisherWindow("victim unavailable");
        else if (now >= _forcedCriticalExpiresAt)
            ReleaseForcedCriticalFinisherWindow("six-second window expired");
    }

    private static void ReassertVanillaCriticalInjury(PlayerControllerB victim)
    {
        if (victim == null || victim.isPlayerDead)
            return;

        bool vanillaClearedEarly = !victim.criticallyInjured;
        if (vanillaClearedEarly && victim.IsOwner)
            victim.MakeCriticallyInjured(true);

        victim.criticallyInjured = true;
        victim.bleedingHeavily = true;
        Animator victimAnimator = victim.playerBodyAnimator;
        if (victimAnimator != null && !victimAnimator.GetBool(VanillaLimpHash))
            victimAnimator.SetBool(VanillaLimpHash, true);
    }

    private void ReleaseForcedCriticalFinisherWindow(string reason)
    {
        if (_forcedCriticalPlayerIndex < 0)
            return;

        int playerIndex = _forcedCriticalPlayerIndex;
        int generation = _forcedCriticalGeneration;
        _forcedCriticalPlayerIndex = -1;
        _forcedCriticalExpiresAt = double.NegativeInfinity;

        WriteInfoLine(
            $"BundyForcedCritical end: playerIndex={playerIndex}, generation={generation}, reason={reason}.");

        if (IsServer)
            EndForcedCriticalFinisherWindowClientRpc(playerIndex, generation);
    }

    [ClientRpc]
    public void EndForcedCriticalFinisherWindowClientRpc(int playerIndex, int generation)
    {
        EndLocalForcedCriticalFinisherWindow(playerIndex, generation, notifyVanilla: true);
    }

    private void EndLocalForcedCriticalFinisherWindow(int playerIndex, int generation, bool notifyVanilla)
    {
        // #374: index must match exactly, but any generation at or above ours clears. A peer
        // that missed an apply RPC (late join, dropped delivery) still holds an older
        // generation, and demanding an exact match would make it ignore the authoritative end
        // broadcast and reassert the critical state forever.
        if (_localForcedCriticalPlayerIndex != playerIndex ||
            generation < _localForcedCriticalGeneration)
        {
            return;
        }

        PlayerControllerB victim = ResolvePlayerByIndex(playerIndex);
        _localForcedCriticalPlayerIndex = -1;
        _localForcedCriticalGeneration = 0;
        _localForcedCriticalExpiresAt = double.NegativeInfinity;
        if (ReferenceEquals(_localForcedCriticalBackstopInstance, this))
        {
            _localForcedCriticalBackstopInstance = null;
            _localForcedCriticalBackstopDeadline = double.NegativeInfinity;
        }

        if (victim == null)
            return;

        bool ownerCanNotify = victim.IsOwner && !victim.isPlayerDead && victim.criticallyInjured;
        // #374: at 20+ health the owner MUST clear through the vanilla path even on teardown —
        // the regen loop (vanilla's only in-round crit clear) never runs at 20+, so raw field
        // writes would strand the ServerRpc-replicated crit state and the red vignette, which
        // only UpdateHealthUI ever repaints. Below 20, vanilla regen owns recovery as before.
        if (ownerCanNotify && (notifyVanilla || victim.health >= 20))
        {
            victim.MakeCriticallyInjured(false);
            if (victim.health >= 20 && HUDManager.Instance != null)
                HUDManager.Instance.UpdateHealthUI(victim.health, false);
        }
        else
        {
            victim.criticallyInjured = false;
            victim.bleedingHeavily = false;
        }

        if (victim.playerBodyAnimator != null)
            victim.playerBodyAnimator.SetBool(VanillaLimpHash, false);
    }

    private void ClearForcedCriticalFinisherStateOnTeardown(string reason)
    {
        if (IsServer)
            ReleaseForcedCriticalFinisherWindow(reason);

        if (_localForcedCriticalPlayerIndex >= 0)
        {
            EndLocalForcedCriticalFinisherWindow(
                _localForcedCriticalPlayerIndex,
                _localForcedCriticalGeneration,
                notifyVanilla: false);
        }
    }

    private bool TryGetForcedCriticalFinisherTarget(out PlayerControllerB victim)
    {
        victim = null;
        if (!UsesForcedCriticalFinisherGate ||
            _forcedCriticalPlayerIndex < 0 ||
            Time.timeAsDouble >= _forcedCriticalExpiresAt)
        {
            return false;
        }

        victim = ResolvePlayerByIndex(_forcedCriticalPlayerIndex);
        return IsLivingPlayer(victim);
    }

    private float GetForcedCriticalFinisherRemainingSeconds(PlayerControllerB victim)
    {
        if (!TryGetForcedCriticalFinisherTarget(out PlayerControllerB forcedVictim) || forcedVictim != victim)
            return 0f;
        return Mathf.Max(0f, (float)(_forcedCriticalExpiresAt - Time.timeAsDouble));
    }

    private static PlayerControllerB ResolvePlayerByIndex(int playerIndex)
    {
        PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
        if (players == null || playerIndex < 0 || playerIndex >= players.Length)
            return null;
        return players[playerIndex];
    }

    private float GetPairedFinisherRootBackoff()
    {
        return UsesForcedCriticalFinisherGate
            ? Mathf.Max(0.1f, Mathf.Abs(GetChainsawFinisherVictimLocalOffset().z))
            : PairedFinisherRootBackoff;
    }

    private float GetPairedFinisherMaxRootSnapDistance()
    {
        return UsesForcedCriticalFinisherGate
            ? ForcedCriticalFinisherPositionTolerance
            : PairedFinisherMaxRootSnapDistance;
    }
}
