using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using BepInEx.Configuration;
using DunGen;
using DunGen.Graph;
using GameNetcodeStuff;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;

using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Facility.Interior
{
    /// <summary>
    /// Fills the logging blackout between "Created New Day History Log" and
    /// <c>RoundManager.OnFinishedGeneratingDungeon</c> (y4ngz313/Y4NGZCompany#446).
    ///
    /// Three separate jobs, all driven from one <c>RoundManager.Update</c> postfix so
    /// nothing depends on a coroutine host surviving a scene load:
    ///
    /// 1. A generation heartbeat that runs on every peer. DunGen generates asynchronously
    ///    (<c>GenerateAsynchronously = true</c>, <c>MaxAsyncFrameMilliseconds = 1</c>), so the
    ///    frame loop keeps ticking while it works and an Update-driven beat still reports even
    ///    when individual frames stretch into whole seconds - which is exactly the shape of the
    ///    Session 3 client hang.
    /// 2. A host-only watchdog over <c>RoundManager.playersFinishedGeneratingFloor</c>. Vanilla
    ///    <c>LoadNewLevelWait</c> parks on
    ///    <c>WaitUntil(playersFinishedGeneratingFloor.Count >= GameNetworkManager.Instance.connectedPlayers)</c>
    ///    with no timeout and no diagnostics; this names the clients that have not acked.
    /// 3. The optional DungeonSize soft cap. The "DungeonSize Is: x | Leaving DungeonSize As: y"
    ///    line in the logs is LethalLevelLoader's, not ours, and it writes the scalar from inside
    ///    <c>RoundManager.GenerateNewFloor</c> (its <c>GenerateNewFloor_Transpiler</c> injects
    ///    <c>InjectHostDungeonSizeSelection</c>), so a prefix on that method would be overwritten.
    ///    Clamping in <c>DungeonGenerator.Generate</c> is the last point before generation runs
    ///    and therefore sees whatever value actually took effect.
    /// </summary>
    internal static class DungeonGenerationWatchdog
    {
        /// <summary>Heartbeat cadence. ~250 ms over a healthy 2-5 s generation is 10-20 lines.</summary>
        private const float HeartbeatIntervalSeconds = 0.25f;

        /// <summary>Ack watchdog cadence. Only ticks while acks are actually outstanding.</summary>
        private const float AckWatchdogIntervalSeconds = 5f;

        private const string Tag = "[MoonContracts.GenWatch]";

        // Heartbeat state.
        private static DungeonGenerator _generator;
        private static readonly Stopwatch GenerationTimer = new Stopwatch();
        private static float _nextBeatAt;
        private static int _beatCount;
        private static GenerationStatus _lastLoggedStatus;
        private static string _role = "unknown";
        private static bool _summaryLogged;

        // Ack watchdog state (host only).
        private static bool _ackWatchActive;
        private static readonly Stopwatch AckTimer = new Stopwatch();
        private static float _nextAckLogAt;
        private static readonly List<ulong> OutstandingClientIds = new List<ulong>(4);
        private static readonly StringBuilder OutstandingBuilder = new StringBuilder(128);

        // Cached so subscribing does not allocate a delegate per generation.
        private static readonly GenerationStatusDelegate StatusHandler = OnGenerationStatusChanged;

        internal static void ResetRunState(string reason)
        {
            if (_generator != null || _ackWatchActive)
            {
                ContractsBootstrap.Log?.LogInfo(
                    $"{Tag} Generation diagnostics reset ({reason}).");
            }

            Detach();
            GenerationTimer.Reset();
            AckTimer.Reset();
            _ackWatchActive = false;
            _beatCount = 0;
            _summaryLogged = false;
            OutstandingClientIds.Clear();
        }

        /// <summary>
        /// Called from the <see cref="DungeonGenerator.Generate"/> prefix, on every peer, for the
        /// round's own generator only.
        /// </summary>
        internal static void BeginGeneration(DungeonGenerator generator)
        {
            if (generator == null)
                return;

            Detach();

            _generator = generator;
            _role = ResolveRole();
            _beatCount = 0;
            _summaryLogged = false;
            // OuterGenerate assigns Status = NotStarted directly rather than through ChangeStatus,
            // so no event announces it; seed the phase tracker with it instead of last round's
            // leftover Complete, which would otherwise print a nonsense "Complete -> TileInjection".
            _lastLoggedStatus = GenerationStatus.NotStarted;
            GenerationTimer.Reset();
            GenerationTimer.Start();
            _nextBeatAt = Time.realtimeSinceStartup + HeartbeatIntervalSeconds;
            generator.OnGenerationStatusChanged += StatusHandler;

            LogResolvedParameters(generator);

            // The host is the only peer that waits on acks, and it starts waiting the moment
            // its own generation finishes. Arm here so the wait is already instrumented.
            if (IsServerPeer())
            {
                _ackWatchActive = true;
                AckTimer.Reset();
                _nextAckLogAt = 0f;
            }
        }

        /// <summary>
        /// A disconnect moves <c>GameNetworkManager.connectedPlayers</c> under the vanilla wait, so
        /// the next watchdog tick must re-evaluate rather than keep counting against the old target.
        /// Reporting immediately is also the single most useful line in this failure mode.
        /// </summary>
        internal static void NotifyPlayerDisconnected()
        {
            if (!_ackWatchActive || !AckTimer.IsRunning)
                return;

            _nextAckLogAt = 0f;
            ContractsBootstrap.Log?.LogWarning(
                $"{Tag} A player disconnected while the host was waiting on floor-generation acks " +
                $"(waited {AckTimer.Elapsed.TotalSeconds:0.0}s so far).");
        }

        /// <summary>Driven from a <c>RoundManager.Update</c> postfix. Quiet path allocates nothing.</summary>
        internal static void UpdateRuntime()
        {
            if (_generator != null)
                UpdateHeartbeat();

            if (_ackWatchActive)
                UpdateAckWatchdog();
        }

        private static void UpdateHeartbeat()
        {
            DungeonGenerator generator = _generator;
            if (generator == null)
                return;

            // Completion and failure both detach from OnGenerationStatusChanged in the same frame
            // the status changes, so reaching Update with IsGenerating false means generation
            // stopped without that event: Cancel(), a destroyed generator, or a Generate() call
            // that no-opped. Terminate with a summary rather than beat forever.
            if (!generator.IsGenerating)
            {
                LogSummary(generator, "stopped");
                Detach();
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (now < _nextBeatAt)
                return;

            _nextBeatAt = now + HeartbeatIntervalSeconds;
            _beatCount++;

            GenerationStats stats = generator.GenerationStats;
            int attempt = stats != null ? stats.TotalRetries + 1 : 1;
            // proxyDungeon is protected in stock DunGen; the build publicizes the game libraries
            // (LethalCompany.GameLibs.Steam, Publicize="true"), so this is a direct field read and
            // not per-beat reflection. It is the only live tile count during MainPath/Branching:
            // Dungeon.AllTiles stays empty until the InstantiatingTiles phase fills it from the proxy.
            int proxyTiles = generator.proxyDungeon != null && generator.proxyDungeon.AllTiles != null
                ? generator.proxyDungeon.AllTiles.Count
                : -1;
            int builtTiles = generator.CurrentDungeon != null && generator.CurrentDungeon.AllTiles != null
                ? generator.CurrentDungeon.AllTiles.Count
                : -1;

            ContractsBootstrap.Log?.LogInfo(
                $"{Tag} beat #{_beatCount}: role={_role}, status={generator.Status}, " +
                $"attempt={attempt}/{generator.MaxAttemptCount}, proxyTiles={proxyTiles}, builtTiles={builtTiles}, " +
                $"elapsed={GenerationTimer.ElapsedMilliseconds}ms.");
            _lastLoggedStatus = generator.Status;
        }

        private static void OnGenerationStatusChanged(DungeonGenerator generator, GenerationStatus status)
        {
            if (generator == null || !ReferenceEquals(generator, _generator))
                return;

            if (status != GenerationStatus.Complete && status != GenerationStatus.Failed)
            {
                // Phase transitions are the cheapest possible progress signal, and unlike the
                // beat they cannot be missed by a frame that ran long.
                if (status != _lastLoggedStatus)
                {
                    ContractsBootstrap.Log?.LogInfo(
                        $"{Tag} phase: role={_role}, {_lastLoggedStatus} -> {status}, " +
                        $"elapsed={GenerationTimer.ElapsedMilliseconds}ms.");
                    _lastLoggedStatus = status;
                }

                return;
            }

            LogSummary(generator, status == GenerationStatus.Complete ? "complete" : "failed");
            Detach();
        }

        private static void LogResolvedParameters(DungeonGenerator generator)
        {
            try
            {
                DungeonFlow flow = generator.DungeonFlow;
                RoundManager roundManager = RoundManager.Instance;
                string flowName = flow != null ? flow.name : "<none>";
                string flowLength = flow != null && flow.Length != null
                    ? $"{flow.Length.Min}-{flow.Length.Max}"
                    : "<none>";
                string branchCount = flow != null && flow.BranchCount != null
                    ? $"{flow.BranchCount.Min}-{flow.BranchCount.Max}"
                    : "<none>";
                int tileSets = 0;
                if (flow != null)
                {
                    TileSet[] usedTileSets = flow.GetUsedTileSets();
                    tileSets = usedTileSets != null ? usedTileSets.Length : 0;
                }

                float factorySize = roundManager != null && roundManager.currentLevel != null
                    ? roundManager.currentLevel.factorySizeMultiplier
                    : -1f;
                float mapSize = roundManager != null ? roundManager.mapSizeMultiplier : -1f;

                ContractsBootstrap.Log?.LogInfo(
                    $"{Tag} Floor generation parameters resolved: role={_role}, flow='{flowName}', " +
                    $"dungeonSize={generator.LengthMultiplier:0.###} (factorySizeMultiplier={factorySize:0.###}, " +
                    $"mapSizeMultiplier={mapSize:0.###}), maxAttempts={generator.MaxAttemptCount}, " +
                    $"seed={generator.Seed}, flowLength={flowLength}, branchCount={branchCount}, " +
                    $"tileSets={tileSets}, nodes={(flow != null && flow.Nodes != null ? flow.Nodes.Count : -1)}, " +
                    $"lines={(flow != null && flow.Lines != null ? flow.Lines.Count : -1)}.");
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogWarning($"{Tag} Could not describe generation parameters: {e.Message}");
            }
        }

        private static void LogSummary(DungeonGenerator generator, string outcome)
        {
            if (_summaryLogged)
                return;

            _summaryLogged = true;
            GenerationTimer.Stop();

            try
            {
                GenerationStats stats = generator != null ? generator.GenerationStats : null;
                int attempts = stats != null ? stats.TotalRetries + 1 : -1;
                int tiles = generator != null && generator.CurrentDungeon != null && generator.CurrentDungeon.AllTiles != null
                    ? generator.CurrentDungeon.AllTiles.Count
                    : -1;
                string rooms = stats != null
                    ? $"{stats.TotalRoomCount} (main={stats.MainPathRoomCount}, branch={stats.BranchPathRoomCount}, pruned={stats.PrunedBranchTileCount})"
                    : "<none>";
                string phases = stats != null
                    ? $"preProcess={stats.GetGenerationStepTime(GenerationStatus.PreProcessing):0}ms, " +
                      $"mainPath={stats.GetGenerationStepTime(GenerationStatus.MainPath):0}ms, " +
                      $"branching={stats.GetGenerationStepTime(GenerationStatus.Branching):0}ms, " +
                      $"instantiate={stats.GetGenerationStepTime(GenerationStatus.InstantiatingTiles):0}ms, " +
                      $"postProcess={stats.GetGenerationStepTime(GenerationStatus.PostProcessing):0}ms"
                    : "<none>";

                string message =
                    $"{Tag} Floor generation {outcome}: role={_role}, status={(generator != null ? generator.Status.ToString() : "<destroyed>")}, " +
                    $"attempts={attempts}/{(generator != null ? generator.MaxAttemptCount : -1)}, " +
                    $"totalTime={GenerationTimer.ElapsedMilliseconds}ms, beats={_beatCount}, " +
                    $"tiles={tiles}, rooms={rooms}, phases=[{phases}].";

                if (outcome == "complete")
                    ContractsBootstrap.Log?.LogInfo(message);
                else
                    ContractsBootstrap.Log?.LogWarning(message);
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogWarning($"{Tag} Could not summarise generation: {e.Message}");
            }
        }

        private static void Detach()
        {
            if (_generator != null)
            {
                try
                {
                    _generator.OnGenerationStatusChanged -= StatusHandler;
                }
                catch (Exception)
                {
                    // A destroyed generator can throw here; the reference is dropped either way.
                }
            }

            _generator = null;
        }

        private static void UpdateAckWatchdog()
        {
            RoundManager roundManager = RoundManager.Instance;
            if (roundManager == null || !IsServerPeer())
            {
                StopAckWatch("no host round manager");
                return;
            }

            // Vanilla only waits on acks when the level spawns enemies and scrap; on every other
            // level LoadNewLevelWait skips the WaitUntil entirely and there is nothing to watch.
            if (roundManager.currentLevel != null && !roundManager.currentLevel.spawnEnemiesAndScrap)
            {
                StopAckWatch("level does not wait on acks");
                return;
            }

            if (roundManager.dungeonFinishedGeneratingForAllPlayers)
            {
                StopAckWatch("all players finished generating");
                return;
            }

            // The host only starts waiting once its own generation has landed. Until then there is
            // nothing outstanding and the timer must not run.
            if (!roundManager.dungeonCompletedGenerating)
                return;

            int expected = GameNetworkManager.Instance != null ? GameNetworkManager.Instance.connectedPlayers : 0;
            int acked = roundManager.playersFinishedGeneratingFloor != null
                ? roundManager.playersFinishedGeneratingFloor.Count
                : 0;
            if (acked >= expected)
            {
                if (AckTimer.IsRunning)
                    StopAckWatch($"all {acked}/{expected} acks received after {AckTimer.ElapsedMilliseconds}ms");
                else
                    StopAckWatch("acks already complete");
                return;
            }

            if (!AckTimer.IsRunning)
            {
                AckTimer.Reset();
                AckTimer.Start();
                _nextAckLogAt = Time.realtimeSinceStartup + AckWatchdogIntervalSeconds;
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (now < _nextAckLogAt)
                return;

            _nextAckLogAt = now + AckWatchdogIntervalSeconds;
            ContractsBootstrap.Log?.LogWarning(
                $"{Tag} Host still waiting on floor-generation acks after {AckTimer.Elapsed.TotalSeconds:0.0}s: " +
                $"{acked}/{expected} acked, outstanding={DescribeOutstandingPlayers(roundManager)}.");
        }

        private static void StopAckWatch(string reason)
        {
            if (AckTimer.IsRunning)
            {
                AckTimer.Stop();
                ContractsBootstrap.Log?.LogInfo($"{Tag} Ack watchdog stopped: {reason}.");
            }

            _ackWatchActive = false;
        }

        private static string DescribeOutstandingPlayers(RoundManager roundManager)
        {
            OutstandingClientIds.Clear();
            OutstandingBuilder.Length = 0;

            NetworkManager networkManager = NetworkManager.Singleton;
            List<ulong> acked = roundManager.playersFinishedGeneratingFloor;
            if (networkManager != null && networkManager.IsServer)
            {
                IReadOnlyList<ulong> connected = networkManager.ConnectedClientsIds;
                for (int i = 0; i < connected.Count; i++)
                {
                    ulong clientId = connected[i];
                    if (acked == null || !acked.Contains(clientId))
                        OutstandingClientIds.Add(clientId);
                }
            }

            if (OutstandingClientIds.Count == 0)
                return "<none named; ack count is short but every connected client is listed as acked>";

            OutstandingBuilder.Append('[');
            for (int i = 0; i < OutstandingClientIds.Count; i++)
            {
                if (i > 0)
                    OutstandingBuilder.Append(", ");
                ulong clientId = OutstandingClientIds[i];
                OutstandingBuilder.Append(ResolvePlayerName(clientId)).Append(" (clientId=").Append(clientId).Append(')');
            }

            OutstandingBuilder.Append(']');
            return OutstandingBuilder.ToString();
        }

        private static string ResolvePlayerName(ulong clientId)
        {
            StartOfRound startOfRound = StartOfRound.Instance;
            if (startOfRound == null || startOfRound.allPlayerScripts == null)
                return "UNKNOWN";

            for (int i = 0; i < startOfRound.allPlayerScripts.Length; i++)
            {
                PlayerControllerB player = startOfRound.allPlayerScripts[i];
                if (player == null || player.actualClientId != clientId)
                    continue;
                if (!string.IsNullOrWhiteSpace(player.playerUsername))
                    return player.playerUsername.Trim();
                return $"P{player.playerClientId + 1}";
            }

            return "UNKNOWN";
        }

        private static bool IsServerPeer()
        {
            NetworkManager networkManager = NetworkManager.Singleton;
            return networkManager != null && networkManager.IsListening && networkManager.IsServer;
        }

        private static string ResolveRole()
        {
            NetworkManager networkManager = NetworkManager.Singleton;
            if (networkManager == null || !networkManager.IsListening)
                return "offline";
            if (networkManager.IsHost)
                return $"host(clientId={networkManager.LocalClientId})";
            if (networkManager.IsServer)
                return $"server(clientId={networkManager.LocalClientId})";
            return $"client(clientId={networkManager.LocalClientId})";
        }

        /// <summary>
        /// Optional mitigation for #446: clamp the effective DungeonSize scalar so an oversized
        /// interior cannot push a slower client into a hard hang. Runs immediately before
        /// generation, after LethalLevelLoader has finished writing the value.
        /// </summary>
        internal static void ApplyDungeonSizeSoftCap(DungeonGenerator generator)
        {
            ConfigEntry<float> cap = ContractsBootstrap.DungeonSizeSoftCap;
            if (generator == null || cap == null)
                return;

            float capValue = cap.Value;
            if (capValue <= 0f || generator.LengthMultiplier <= capValue)
                return;

            float original = generator.LengthMultiplier;
            generator.LengthMultiplier = capValue;
            ContractsBootstrap.Log?.LogWarning(
                $"{Tag} DungeonSize soft cap engaged: clamped LengthMultiplier {original:0.###} -> {capValue:0.###} " +
                $"for flow '{(generator.DungeonFlow != null ? generator.DungeonFlow.name : "<none>")}'. " +
                "The uncapped value is set by LethalLevelLoader, not by this mod; clear DungeonSizeSoftCap to restore it.");
        }
    }

    [HarmonyPatch(typeof(DungeonGenerator), "Generate")]
    internal static class DungeonGenerationWatchdogGeneratePatch
    {
        [HarmonyPrefix]
        private static void Prefix(DungeonGenerator __instance)
        {
            try
            {
                // Only the round's own generator. DunGen is also driven by other tooling and by
                // analysis passes, and instrumenting those would be noise at best.
                RoundManager roundManager = RoundManager.Instance;
                if (roundManager == null
                    || roundManager.dungeonGenerator == null
                    || !ReferenceEquals(roundManager.dungeonGenerator.Generator, __instance))
                {
                    return;
                }

                DungeonGenerationWatchdog.ApplyDungeonSizeSoftCap(__instance);
                DungeonGenerationWatchdog.BeginGeneration(__instance);
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogError($"[MoonContracts.GenWatch] Generation instrumentation failed to start: {e}");
            }
        }
    }

    [HarmonyPatch(typeof(RoundManager), "Update")]
    internal static class DungeonGenerationWatchdogRuntimePatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            try
            {
                DungeonGenerationWatchdog.UpdateRuntime();
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogError($"[MoonContracts.GenWatch] Generation diagnostics runtime failed: {e}");
                DungeonGenerationWatchdog.ResetRunState("runtime exception");
            }
        }
    }

    [HarmonyPatch(typeof(StartOfRound))]
    internal static class DungeonGenerationWatchdogLifecyclePatches
    {
        [HarmonyPatch("ShipLeave")]
        [HarmonyPostfix]
        private static void ShipLeave_Postfix()
        {
            DungeonGenerationWatchdog.ResetRunState("ship leave");
        }

        [HarmonyPatch("OnPlayerDC")]
        [HarmonyPostfix]
        private static void OnPlayerDC_Postfix()
        {
            // A disconnect changes GameNetworkManager.connectedPlayers under the wait, so the
            // outstanding list must be recomputed rather than kept from the previous tick.
            DungeonGenerationWatchdog.NotifyPlayerDisconnected();
        }
    }

    [HarmonyPatch(typeof(GameNetworkManager), "Disconnect")]
    internal static class DungeonGenerationWatchdogDisconnectPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            DungeonGenerationWatchdog.ResetRunState("disconnect");
        }
    }
}
