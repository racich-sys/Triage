using System;
using System.Collections.Generic;
using System.Data;

namespace VestigantTriage;

public class GridViewState
{
    public string Name { get; }
    public DataTable? BaseTable { get; set; }
    public DataTable? CurrentTable { get; set; }
    public string SortColumn { get; set; } = "";
    public bool SortDescending { get; set; } = false;
    public HashSet<string> HiddenColumns { get; set; } = new();
    public Dictionary<string, HashSet<string>> ValueFilters { get; set; } = new();

    public GridViewState(string name) => Name = name;

    public void ApplyFilter(string filterText, string userFilter, string opFilter)
    {
        if (BaseTable == null) return;

        var dv = new DataView(BaseTable);
        var sb = new List<string>();

        if (!string.IsNullOrWhiteSpace(filterText))
            sb.Add($"(Source LIKE '%{filterText}%' OR TargetObject LIKE '%{filterText}%')");

        if (!string.IsNullOrWhiteSpace(userFilter))
            sb.Add($"User LIKE '%{userFilter}%'");

        if (!string.IsNullOrWhiteSpace(opFilter))
            sb.Add($"Operation LIKE '%{opFilter}%'");

        dv.RowFilter = string.Join(" AND ", sb);
        CurrentTable = dv.ToTable();
    }
}