using System;
using System.Collections.Generic;

namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>
    /// Maps the internal <c>EnemyType.enemyName</c> ids the game ships with onto the
    /// names players actually know them by (#716/E5). Death notes used to read
    /// "Died to Flowerman inside." while the note catalogue promised
    /// "Died to Bracken inside."
    /// </summary>
    /// <remarks>
    /// Unknown ids - modded enemies, or vanilla ids that already read correctly - are
    /// returned unchanged, so a mod that names its enemy well needs no entry here.
    /// </remarks>
    internal static class EnemyDisplayNames
    {
        private static readonly Dictionary<string, string> Map =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Flowerman", "Bracken" },
                { "Crawler", "Thumper" },
                { "Centipede", "Snare Flea" },
                { "MouthDog", "Eyeless Dog" },
                { "ForestGiant", "Forest Keeper" },
                { "Puffer", "Spore Lizard" },
                { "SandSpider", "Bunker Spider" },
                { "Blob", "Hygrodere" },
                { "DressGirl", "Ghost Girl" },
                { "Jester", "Jester" },
                { "Nutcracker", "Nutcracker" },
                { "Masked", "Masked" },
                { "Butler", "Butler" },
                { "BushWolf", "Kidnapper Fox" },
                { "RadMech", "Old Bird" },
                { "ClaySurgeon", "Barber" },
                { "FlowerSnake", "Tulip Snake" },
                { "Doublewing", "Manticoil" },
                { "Docile Locust Bees", "Roaming Locusts" },
                { "Red Locust Bees", "Circuit Bees" },
                { "BaboonHawk", "Baboon Hawk" },
                { "SpringMan", "Coil-Head" },
                { "Earth Leviathan", "Earth Leviathan" },
                { "LassoMan", "Lasso Man" },
            };

        /// <summary>
        /// Returns the player-facing name for an internal enemy id, or the trimmed id
        /// itself when nothing is registered for it.
        /// </summary>
        internal static string Resolve(string enemyName)
        {
            if (string.IsNullOrWhiteSpace(enemyName))
                return string.Empty;

            string trimmed = enemyName.Trim();
            return Map.TryGetValue(trimmed, out string display) ? display : trimmed;
        }
    }
}
