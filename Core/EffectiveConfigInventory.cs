using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace Y4NGZCompany.Core
{
    internal static class EffectiveConfigInventory
    {
        internal static string ReportPath => Y4NGZCompanyPaths.LocalDataFile("effective-config.tsv");

        internal static void Write(ConfigFile config, ManualLogSource log)
        {
            if (config == null)
                return;

            try
            {
                var rows = new List<ConfigEntryBase>();
                foreach (KeyValuePair<ConfigDefinition, ConfigEntryBase> pair in config)
                    rows.Add(pair.Value);
                rows.Sort((left, right) =>
                {
                    int section = string.Compare(left.Definition.Section, right.Definition.Section, StringComparison.OrdinalIgnoreCase);
                    return section != 0 ? section : string.Compare(left.Definition.Key, right.Definition.Key, StringComparison.OrdinalIgnoreCase);
                });

                var output = new StringBuilder();
                output.AppendLine("section\tkey\ttype\tdefault\teffective\tdescription");
                for (int i = 0; i < rows.Count; i++)
                {
                    ConfigEntryBase entry = rows[i];
                    output.Append(Escape(entry.Definition.Section)).Append('\t')
                        .Append(Escape(entry.Definition.Key)).Append('\t')
                        .Append(Escape(entry.SettingType?.FullName)).Append('\t')
                        .Append(Escape(Format(entry.DefaultValue))).Append('\t')
                        .Append(Escape(Format(entry.BoxedValue))).Append('\t')
                        .Append(Escape(entry.Description?.Description))
                        .AppendLine();
                }

                Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
                File.WriteAllText(ReportPath, output.ToString());
                log?.LogInfo($"Effective config inventory written to '{ReportPath}' ({rows.Count} definitions).");
            }
            catch (Exception ex)
            {
                log?.LogWarning($"Failed writing effective config inventory: {ex.Message}");
            }
        }

        private static string Format(object value)
        {
            if (value == null)
                return string.Empty;
            return value is IFormattable formattable
                ? formattable.ToString(null, CultureInfo.InvariantCulture)
                : value.ToString();
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty).Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
        }
    }
}
