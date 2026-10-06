using System;
using System.Collections.Generic;

using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>
    /// #716/G5: log-on-catch throttling for repeating paths. A catch inside a per-frame or
    /// per-hit callback either shouts every occurrence (drowning the first, which is the one
    /// that still carries the state that caused it) or whispers at Debug forever. This says it
    /// once, loudly, with the exception type and message, and keeps the rest at Debug.
    /// </summary>
    internal static class MoonContractCatchReporter
    {
        private static readonly HashSet<string> ReportedSites = new HashSet<string>(StringComparer.Ordinal);

        internal static void Report(string site, string message, Exception exception, bool includeStackOnFirst = false)
        {
            string detail = exception != null
                ? $"{exception.GetType().Name}: {exception.Message}"
                : "<no exception>";

            if (ReportedSites.Add(site))
            {
                ContractsBootstrap.Log?.LogWarning(
                    includeStackOnFirst && exception != null
                        ? $"{message}: {detail}{Environment.NewLine}{exception}"
                        : $"{message}: {detail}");
                return;
            }

            ContractsBootstrap.Log?.LogDebug($"{message}: {detail}");
        }
    }

    /// <summary>
    /// #716/G5: the authority test copy-pasted across contract runtimes. Seven files spell
    /// <c>start != null &amp;&amp; (start.IsServer || start.IsHost)</c> out by hand, and one of
    /// them (LethalCCTV's CctvSecurityDirector) inverts the null case, which is how a null
    /// StartOfRound ends up reading as "we are the server". One name for the rule makes the
    /// null answer a decision instead of an accident.
    /// </summary>
    internal static class NetRole
    {
        /// <summary>True when this peer owns authoritative state. A missing
        /// <see cref="StartOfRound"/> is not authority: there is no session to be host of.</summary>
        internal static bool IsServerOrHost(StartOfRound start)
        {
            return start != null && (start.IsServer || start.IsHost);
        }

        /// <summary>Convenience overload for the common
        /// <c>NetRole.IsServerOrHost()</c> call against the live instance.</summary>
        internal static bool IsServerOrHost()
        {
            return IsServerOrHost(StartOfRound.Instance);
        }
    }
}
