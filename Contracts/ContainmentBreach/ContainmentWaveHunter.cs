using System.Collections.Generic;
using UnityEngine;
using Y4NGZCompany.Facility.Security;

namespace Y4NGZCompany.Contracts.ContainmentBreach
{
    internal static class ContainmentWaveHunter
    {
        private sealed class TrackedEnemy
        {
            internal readonly EnemyAI Enemy;
            internal float NextPulseAt;

            internal TrackedEnemy(EnemyAI enemy, float nextPulseAt)
            {
                Enemy = enemy;
                NextPulseAt = nextPulseAt;
            }
        }

        private static readonly Dictionary<int, TrackedEnemy> BreachEnemies = new Dictionary<int, TrackedEnemy>();
        private static readonly List<EnemyAI> PulseBuffer = new List<EnemyAI>(16);
        private static readonly List<int> RemovalBuffer = new List<int>(16);

        internal static void ResetRunState()
        {
            ClearTrackedEnemies();
            EnemyLocationPulseService.ResetRunState();
        }

        internal static void ClearTrackedEnemies()
        {
            BreachEnemies.Clear();
            PulseBuffer.Clear();
            RemovalBuffer.Clear();
        }

        internal static void RegisterSpawnedEnemies(IReadOnlyList<EnemyAI> enemies)
        {
            if (enemies == null || enemies.Count == 0)
                return;

            float now = Time.unscaledTime;
            PulseBuffer.Clear();
            for (int i = 0; i < enemies.Count; i++)
            {
                EnemyAI enemy = enemies[i];
                if (!EnemyLocationPulseService.IsValidInteriorEnemy(enemy))
                    continue;

                int enemyId = enemy.GetInstanceID();
                BreachEnemies[enemyId] = new TrackedEnemy(
                    enemy,
                    now + EnemyLocationPulseService.PulseIntervalSeconds);
                PulseBuffer.Add(enemy);
            }

            // New wave enemies receive their first frozen snapshot immediately.
            EnemyLocationPulseService.PulseEnemies(PulseBuffer, now);
        }

        internal static void Tick()
        {
            if (BreachEnemies.Count == 0)
                return;

            float now = Time.unscaledTime;
            PulseBuffer.Clear();
            RemovalBuffer.Clear();
            foreach (KeyValuePair<int, TrackedEnemy> pair in BreachEnemies)
            {
                TrackedEnemy tracked = pair.Value;
                if (!EnemyLocationPulseService.IsValidInteriorEnemy(tracked.Enemy))
                {
                    RemovalBuffer.Add(pair.Key);
                    continue;
                }

                if (now + 0.001f < tracked.NextPulseAt)
                    continue;

                tracked.NextPulseAt = now + EnemyLocationPulseService.PulseIntervalSeconds;
                PulseBuffer.Add(tracked.Enemy);
            }

            for (int i = 0; i < RemovalBuffer.Count; i++)
                BreachEnemies.Remove(RemovalBuffer[i]);

            EnemyLocationPulseService.PulseEnemies(PulseBuffer, now);
        }
    }
}
