using System.Collections.Generic;

namespace VestigantTriage;

internal enum ExportCostClass
{
    CheapSummary,
    ModerateDetail,
    ExpensiveJoinedDump
}

internal sealed record ExportSafeguardRule(string ExportName, ExportCostClass CostClass, bool EnabledByDefault, string Safeguard, string Rationale);

internal static class ExportSafeguards
{
    public const int DefaultBoundedExportRowLimit = 50000;
    public const int DefaultExpensiveExportRowLimit = 5000;
    public const int DefaultExportTimeoutSeconds = 300;

    public static IReadOnlyList<ExportSafeguardRule> DefaultRules { get; } = new List<ExportSafeguardRule>
    {
        new("validation_summary_csvs", ExportCostClass.CheapSummary, true, "bounded exports", "Compact validation summaries remain enabled by default."),
        new("investigator_detail_csvs", ExportCostClass.ModerateDetail, true, "bounded exports and timeout/cancel behavior", "Detail exports must be row-bounded unless the investigator explicitly opts into full output."),
        new("expensive_joined_csv_dumps", ExportCostClass.ExpensiveJoinedDump, false, "opt-in only; avoid expensive joined CSV dumps by default", "Large joined exports are roadmap-blocking on full investigations unless explicitly requested."),
    };
}
