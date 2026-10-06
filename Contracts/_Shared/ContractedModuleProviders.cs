// ContractedModuleProviders.cs — #611 task 1.3.
//
// The Contracted feature's single registration into the Y4NGZCore module contract layer.
//
// Before this file, the ship monitor row read the contract and escalation state directly:
// OrbitFeed and LandedFeed took `using Y4NGZCompany.Contracts._Shared`, MonitorRowDirector took
// that plus `Escalations._Shared` and `Escalations.Congregation`, and each of the three carried
// its own copy of the precedence rule "escalation objectives first, contract objectives
// otherwise". After the DLL split none of those usings can exist, and three copies of one rule
// in two DLLs would drift the first time either shipped alone.
//
// Everything the row needs is now answered through Y4NGZCore: an objective directory, an
// escalation directory, and the two neutral round metrics. The precedence lives here, once,
// inside the single adapter registered under the single-slot y4ngz.objectives capability —
// which is the arrangement ModuleCapabilities.Objectives documents and the only one that works,
// because a second registration would replace this one rather than joining it.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Escalations._Shared;
using Y4NGZCompany.Escalations.Congregation;
using Y4NGZCore.Modules;
using Y4NGZCore.Modules.Objectives;
using Y4NGZCore.Modules.Quota;

namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>
    /// Registers Contracted's objective, escalation and metrics providers with
    /// <see cref="ModuleCapabilityRegistry"/>. Called once from
    /// <c>ContractsBootstrap.Initialize</c>.
    /// </summary>
    internal static class ContractedModuleProviders
    {
        private static ObjectiveDirectoryAdapter _objectives;
        private static EscalationDirectoryAdapter _escalations;
        private static ObjectiveMetricsAdapter _metrics;

        /// <summary>Idempotent: a second call replaces this module's own registrations.</summary>
        internal static void EnsureRegistered()
        {
            if (_objectives != null)
                return;

            _objectives = new ObjectiveDirectoryAdapter();
            _escalations = new EscalationDirectoryAdapter();
            _metrics = new ObjectiveMetricsAdapter();

            ModuleCapabilityRegistry.Register(ModuleCapabilities.Objectives, _objectives);
            ModuleCapabilityRegistry.Register(ModuleCapabilities.Escalations, _escalations);
            ModuleCapabilityRegistry.Register(ModuleCapabilities.ObjectiveMetrics, _metrics);

            // The world-noise listener registers with the escalations because it *is* an
            // escalation's perception: the publishers (ship battery, CCTV station, the vanilla
            // patch sites) know only the stable kind IDs, and this is the only thing in the
            // profile that acts on them.
            Escalations.Bundy.BundyEnvironmentalNoiseAdapter.EnsureRegistered();

            // The quota milestone owner has to enumerate every contract type to answer "is this
            // one gated?", and MoonContractType is ours. It reads this catalogue instead of the
            // enum, so it never names a Contracted type.
            _contractCatalog = BuildContractCatalog;
            QuotaUnlockCatalogs.Register(QuotaUnlockCategories.Assignment, _contractCatalog);

            // #611 task 1.3 (review H2): the terminal used to reach MoonContractApi by scanning
            // AppDomain for an assembly literally named "Y4NGZCompany". Post-split that scan
            // finds nothing and every moon quietly loses its assignment text, with no exception
            // to notice. Both surfaces are capabilities now; the terminal keeps a reflection
            // fallback only for the foreign LGUContractHUD mod, which really is a separate DLL.
            _assignments = new MoonAssignmentDirectoryAdapter();
            _fieldSupport = new FieldSupportDeskAdapter();
            ModuleCapabilityRegistry.Register(ModuleCapabilities.MoonAssignments, _assignments);
            ModuleCapabilityRegistry.Register(ModuleCapabilities.FieldSupport, _fieldSupport);
        }

        private static MoonAssignmentDirectoryAdapter _assignments;
        private static FieldSupportDeskAdapter _fieldSupport;

        /// <summary>
        /// The assignment table, projected onto the neutral DTO.
        ///
        /// <para>Each lookup gathers the six strings in one pass instead of the twelve separate
        /// reflected calls the terminal used to make per moon.
        /// <see cref="MoonAssignmentPresentation.HasContent"/> decides "has an assignment", so a
        /// moon the producer knows nothing about reports false rather than a struct full of
        /// empty strings the consumer would have to test field by field.</para>
        /// </summary>
        private sealed class MoonAssignmentDirectoryAdapter : IMoonAssignmentDirectory
        {
            public void RefreshAssignments(string reason) =>
                MoonContractApi.RefreshAssignments(reason);

            public bool TryGetByLevelIndex(int levelIndex, out MoonAssignmentPresentation presentation)
            {
                presentation = new MoonAssignmentPresentation(
                    MoonContractApi.GetDisplayNameForLevel(levelIndex),
                    MoonContractApi.GetShortNameForLevel(levelIndex),
                    MoonContractApi.GetTitleForLevel(levelIndex),
                    MoonContractApi.GetBriefingForLevel(levelIndex),
                    MoonContractApi.GetGameplayDescriptionForLevel(levelIndex),
                    MoonContractApi.GetArtForLevel(levelIndex));
                return presentation.HasContent;
            }

            public bool TryGetByLevelId(int levelId, out MoonAssignmentPresentation presentation)
            {
                presentation = new MoonAssignmentPresentation(
                    MoonContractApi.GetDisplayNameForLevelId(levelId),
                    MoonContractApi.GetShortNameForLevelId(levelId),
                    MoonContractApi.GetTitleForLevelId(levelId),
                    MoonContractApi.GetBriefingForLevelId(levelId),
                    MoonContractApi.GetGameplayDescriptionForLevelId(levelId),
                    MoonContractApi.GetArtForLevelId(levelId));
                return presentation.HasContent;
            }

            public bool LockByLevelIndex(int levelIndex, string reason) =>
                MoonContractApi.LockContractForLevel(levelIndex, reason);

            public bool LockByLevelId(int levelId, string reason) =>
                MoonContractApi.LockContractForLevelId(levelId, reason);
        }

        /// <summary>The defuse support desk, behind the neutral contract.</summary>
        private sealed class FieldSupportDeskAdapter : IFieldSupportDesk
        {
            public string StatusText => MoonContractApi.GetFieldSupportStatusText();
            public string ManualText => MoonContractApi.GetDefuseManualText();
            public string DiagnosticInfoText => MoonContractApi.GetDefuseDiagnosticInfoText();

            public string SubmitDiagnostic(WireDiagnosticRequest request) =>
                MoonContractApi.SubmitDefuseDiagnostic(
                    request.FamilyName,
                    request.DisplayName,
                    request.RedCount,
                    request.BlueCount,
                    request.YellowCount,
                    request.GreenCount,
                    request.WhiteCount,
                    request.BlackCount,
                    request.TotalWires,
                    request.WireOrderCsv);
        }

        private static Func<IReadOnlyList<string>> _contractCatalog;

        /// <summary>
        /// Every contract type that exists, as the stable strings the milestone configuration
        /// uses — the enum member names, which is what players type into
        /// <c>UnlockContracts</c>. <see cref="MoonContractType.None"/> is excluded: it is the
        /// absence of a contract and was never gateable.
        /// </summary>
        private static IReadOnlyList<string> BuildContractCatalog() =>
            Enum.GetValues(typeof(MoonContractType))
                .Cast<MoonContractType>()
                .Where(type => type != MoonContractType.None)
                .Select(type => type.ToString())
                .ToArray();

        // ── Objectives ────────────────────────────────────────────────────────

        /// <summary>
        /// The whole objective boundary: precedence, projection onto the neutral kind IDs, and
        /// the change token.
        ///
        /// <para><b>Precedence.</b> An active escalation's objectives replace the contract's
        /// entirely — not merge with them. That is the rule the three consumers each carried
        /// (<c>MoonEscalationState.GetActiveObjectives()</c>, falling through to
        /// <c>MoonContractState.GetCurrentObjectives()</c> only when it came back empty), and it
        /// is preserved exactly: an escalation that is running but has produced no lines yet
        /// still falls through, because "no lines yet" and "no escalation" were never
        /// distinguished and distinguishing them now would blank the row mid-round.</para>
        ///
        /// <para><b>Caching.</b> Both the projected list and the revision are rebuilt at most
        /// once per rendered frame. The row asks for the revision every presentation frame and
        /// for the list only when the revision moved, so without the frame latch the "cheap
        /// change token" would cost exactly what the full-list hash it replaces cost. The latch
        /// is per <see cref="Time.frameCount"/> rather than a timer because a consumer that
        /// reads the revision and then the list within one frame must see the same
        /// snapshot.</para>
        /// </summary>
        private sealed class ObjectiveDirectoryAdapter : IObjectiveDirectory
        {
            private static readonly ObjectiveDescriptor[] Empty = new ObjectiveDescriptor[0];

            private IReadOnlyList<ObjectiveDescriptor> _cached = Empty;
            private int _cachedRevision;
            private int _cachedFrame = -1;

            public IReadOnlyList<ObjectiveDescriptor> GetActiveObjectives()
            {
                Refresh();
                return _cached;
            }

            public int ObjectiveRevision
            {
                get
                {
                    Refresh();
                    return _cachedRevision;
                }
            }

            private void Refresh()
            {
                int frame = Time.frameCount;
                if (frame == _cachedFrame)
                    return;
                _cachedFrame = frame;

                List<MoonContractObjective> source;
                try
                {
                    source = MoonEscalationState.GetActiveObjectives();
                    if (source == null || source.Count == 0)
                        source = MoonContractState.GetCurrentObjectives();
                }
                catch (Exception ex)
                {
                    // A throwing producer must not take the monitor row down with it, and it
                    // must not look like "the objectives finished" either — hold the last good
                    // snapshot and its revision so nothing on screen changes.
                    ContractsBootstrap.Log?.LogWarning(
                        $"[MoonContracts.Modules] Objective read failed: {ex.Message}");
                    return;
                }

                if (source == null || source.Count == 0)
                {
                    _cached = Empty;
                    _cachedRevision = 0;
                    return;
                }

                var projected = new ObjectiveDescriptor[source.Count];
                unchecked
                {
                    int revision = 19;
                    for (int i = 0; i < source.Count; i++)
                    {
                        MoonContractObjective objective = source[i];
                        projected[i] = new ObjectiveDescriptor(
                            objective.SignatureKey,
                            objective.Text,
                            objective.Complete,
                            objective.Total,
                            objective.Failed,
                            ProjectKind(objective),
                            objective.Lethal);

                        // The revision covers exactly what a consumer can see change: identity,
                        // wording, progress and failure. Kind and lethality are derived from
                        // those, so folding them in again would only cost cycles.
                        revision = revision * 31 + (objective.SignatureKey?.GetHashCode() ?? 0);
                        revision = revision * 31 + (objective.Text?.GetHashCode() ?? 0);
                        revision = revision * 31 + objective.Complete;
                        revision = revision * 31 + objective.Total;
                        revision = revision * 31 + (objective.Failed ? 1 : 0);
                    }

                    _cachedRevision = revision;
                }

                _cached = projected;
            }

            /// <summary>
            /// The explicit <c>MoonContractObjectiveKind</c> → <see cref="ObjectiveKinds"/>
            /// projection the contract layer asks for. The rule itself is
            /// <see cref="ObjectiveKinds.Project"/> in Y4NGZCore so a second adapter cannot
            /// disagree with this one; all this does is decide which of the two producer kinds
            /// it is looking at.
            /// </summary>
            private static string ProjectKind(MoonContractObjective objective) =>
                ObjectiveKinds.Project(
                    objective.Kind == MoonContractObjectiveKind.Status,
                    objective.Total);
        }

        // ── Escalations ───────────────────────────────────────────────────────

        /// <summary>
        /// The escalation boundary. The neutral ID is derived from the producer's own display
        /// title rather than from its internal flags, because the title is the one place the
        /// three escalations are already distinguished in a fixed precedence
        /// (<c>MoonEscalationState.ActiveTitle</c>), and re-deriving that precedence from the
        /// flags is how the ID and the title end up naming different escalations.
        /// </summary>
        private sealed class EscalationDirectoryAdapter : IEscalationDirectory
        {
            /// <summary>Display title → stable escalation ID. Both halves are wire-stable.</summary>
            private const string StrangerTitle = "THE STRANGER";
            private const string BlacksiteTitle = "BLACKSITE RETRIEVAL";
            private const string CongregationTitle = "THE CONGREGATION";

            public EscalationDescriptor GetActiveEscalation()
            {
                try
                {
                    if (!MoonEscalationState.HasActiveEscalation)
                        return EscalationDescriptor.None;

                    string title = MoonEscalationState.ActiveTitle ?? string.Empty;
                    return new EscalationDescriptor(EscalationIdFor(title), title, true);
                }
                catch (Exception ex)
                {
                    ContractsBootstrap.Log?.LogWarning(
                        $"[MoonContracts.Modules] Escalation read failed: {ex.Message}");
                    return EscalationDescriptor.None;
                }
            }

            public bool HasSignal(string signalId)
            {
                if (!string.Equals(signalId, EscalationSignals.ThreatManifested, StringComparison.Ordinal))
                    return false;

                try
                {
                    return CongregationEscalationState.HasSpawnedSeraphim;
                }
                catch (Exception ex)
                {
                    ContractsBootstrap.Log?.LogWarning(
                        $"[MoonContracts.Modules] Escalation signal read failed: {ex.Message}");
                    return false;
                }
            }

            private static string EscalationIdFor(string title)
            {
                switch (title)
                {
                    case StrangerTitle: return "stranger";
                    case BlacksiteTitle: return "blacksite_retrieval";
                    case CongregationTitle: return "congregation";
                    // An escalation added later without an ID here is still reported as active
                    // with its title; a blank ID costs a consumer only the ability to tell two
                    // identically-titled escalations apart, which is strictly better than
                    // hiding it.
                    default: return string.Empty;
                }
            }
        }

        // ── Metrics ───────────────────────────────────────────────────────────

        /// <summary>
        /// The two neutral round numbers the monitor row reads beside the objective list.
        ///
        /// <para><see cref="ShipScrapValue"/> is cached per frame for the same reason the
        /// objective list is: the underlying read is a <c>FindObjectsOfType&lt;GrabbableObject&gt;</c>
        /// walk of every item in the level, and the row asks for it from a page builder and
        /// again from its content hash within the same frame.</para>
        /// </summary>
        private sealed class ObjectiveMetricsAdapter : IObjectiveMetricsProvider
        {
            private int _cachedScrap;
            private int _cachedScrapFrame = -1;

            public int ShipScrapValue
            {
                get
                {
                    int frame = Time.frameCount;
                    if (frame == _cachedScrapFrame)
                        return _cachedScrap;

                    try
                    {
                        _cachedScrap = Mathf.Max(0, MoonContractState.GetShipScrapValue());
                        _cachedScrapFrame = frame;
                    }
                    catch (Exception ex)
                    {
                        ContractsBootstrap.Log?.LogWarning(
                            $"[MoonContracts.Modules] Ship scrap read failed: {ex.Message}");
                    }

                    return _cachedScrap;
                }
            }

            public int AssignmentRevision
            {
                get
                {
                    try
                    {
                        return MoonContractState.GetAssignmentStateToken();
                    }
                    catch (Exception ex)
                    {
                        ContractsBootstrap.Log?.LogWarning(
                            $"[MoonContracts.Modules] Assignment token read failed: {ex.Message}");
                        return 0;
                    }
                }
            }
        }
    }
}
