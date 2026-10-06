using System;
using System.Collections.Generic;

namespace Y4NGZCompany.Core
{
    /// <summary>
    /// Reflection-compatibility facade for the route API. <b>Not</b> the implementation — that
    /// moved to Y4NGZShipSystems.dll with the custom terminal in #613 task 2.1, under this same
    /// full type name.
    ///
    /// <para><b>Why this file still exists.</b> Y4NGZUpgrades' Lucky-8 does not resolve this API
    /// by type name alone. It scans <c>AppDomain.CurrentDomain.GetAssemblies()</c> for the one
    /// whose simple name is exactly <c>Y4NGZCompany</c> and then calls
    /// <c>company.GetType("Y4NGZCompany.Core.RouteFeatureApi", false)</c> on that assembly object
    /// (<c>Y4NGZUpgrades/src/Y4NGZUpgrades/Lucky8/Lucky8CompanyBridge.cs:112-126</c>), binding
    /// <c>GetRouteCatalog</c>, <c>RegisterMarkerProvider</c> and <c>UnregisterMarkerProvider</c>
    /// as public statics. Nothing about that probe can find a type in a different assembly, so
    /// removing this type would take Lucky-8's route markers down with no error and no log line —
    /// the whole bridge simply no-ops when the type is null. The plan calls for the facade to
    /// stay resolvable here until Lucky-8 is updated in its own repository.</para>
    ///
    /// <para><b>This type name exists exactly once in the whole solution.</b> An earlier draft
    /// of the split gave the Ship Systems implementation this same full name and relied on the
    /// two assemblies never referencing each other. That is what the split plan forbids, and
    /// rightly: a consumer that referenced both would get CS0433 with no way to disambiguate,
    /// and an assembly-agnostic reflection scan — the pattern Y4NGZDiagnostics uses everywhere —
    /// would get an arbitrary one of the two. The canonical implementation is therefore named
    /// differently: <c>Y4NGZCompany.ShipSystems.ShipTerminal.RouteCatalogPublisher</c> in
    /// Y4NGZShipSystems.dll, which is the plan's sanctioned strategy (an old-name facade
    /// delegating to a differently named canonical API).</para>
    ///
    /// <para><b>How the two halves meet.</b> Not by referencing each other — neither assembly
    /// does — but through shared state in Y4NGZCore: <see cref="RouteMarkers"/> for markers and
    /// <see cref="RouteCatalog"/> for the catalogue, the latter published by the terminal as a
    /// capability. That is what makes this the same API rather than one that merely looks alike:
    /// a marker Lucky-8 registers here is the marker the terminal reads over there.</para>
    ///
    /// <para><b>With Ship Systems not installed</b> the catalogue is empty and markers still
    /// register — correct, because a marker with no terminal to draw it is simply never asked
    /// about, and the registration must survive in case Ship Systems is present next launch.</para>
    /// </summary>
    public static class RouteFeatureApi
    {
        /// <summary>
        /// API shape version for companion mods, unchanged since before the split.
        ///
        /// <para>There is nothing to keep it in step with: this type is the only declaration of
        /// the route API in the solution, and <c>RouteCatalogPublisher</c> — the canonical
        /// implementation in Y4NGZShipSystems.dll — has no version member of its own. Bump this
        /// only for a breaking change to the three methods below.</para>
        /// </summary>
        public const int ProtocolVersion = 1;

        /// <summary>
        /// Every route the custom terminal currently offers. Empty when Ship Systems is not
        /// installed, which is the truth about that profile: there is no custom catalogue.
        /// </summary>
        public static IReadOnlyList<RouteFeatureInfo> GetRouteCatalog() => RouteCatalog.Get();

        /// <summary>
        /// Registers a predicate that says which routes carry <paramref name="markerId"/>.
        /// Replaces any predicate already registered under that ID.
        /// </summary>
        public static bool RegisterMarkerProvider(string markerId, Func<int, bool> provider) =>
            RouteMarkers.Register(markerId, provider);

        /// <summary>Withdraws <paramref name="markerId"/>. False when none was registered.</summary>
        public static bool UnregisterMarkerProvider(string markerId) =>
            RouteMarkers.Unregister(markerId);
    }
}
