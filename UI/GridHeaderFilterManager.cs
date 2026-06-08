using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace VestigantTriage;

internal sealed class GridHeaderFilterManager
{
    private readonly DataGridView _grid;
    private readonly Action<string> _status;
    private DataTable? _baseTable;
    private string _label = "Grid";
    private readonly Dictionary<string, string> _containsFilters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _valueFilters = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Column, string Direction)> _sorts = new();

    public GridHeaderFilterManager(DataGridView grid, Action<string> status)
    {
        _grid = grid ?? throw new ArgumentNullException(nameof(grid));
        _status = status ?? (_ => { });
        _grid.ColumnHeaderMouseClick += Grid_ColumnHeaderMouseClick;
    }

    public void CaptureCurrentView(string label)
    {
        _label = string.IsNullOrWhiteSpace(label) ? "Grid" : label;
        _baseTable = ToTable(_grid.DataSource);
        PruneStateForCurrentColumns();
    }

    public void ClearAll()
    {
        _containsFilters.Clear();
        _valueFilters.Clear();
        _sorts.Clear();
        Apply();
    }

    private void Grid_ColumnHeaderMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right || e.ColumnIndex < 0 || e.ColumnIndex >= _grid.Columns.Count)
            return;

        if (_baseTable == null)
            CaptureCurrentView(_label);

        var columnName = BoundColumnName(_grid.Columns[e.ColumnIndex]);
        if (string.IsNullOrWhiteSpace(columnName) || _baseTable == null || !_baseTable.Columns.Contains(columnName))
            return;

        ShowColumnMenu(columnName, _grid.PointToClient(Cursor.Position));
    }

    private void ShowColumnMenu(string columnName, Point location)
    {
        // ContextMenuStrip.AutoClose must remain true so the popup can be dismissed by clicking
        // outside it. Earlier versions used AutoClose=false and called Close() from menu item
        // handlers, which could race with ToolStrip disposal and raise ObjectDisposedException.
        var menu = new ContextMenuStrip { AutoClose = true };
        var baseFont = SystemFonts.MenuFont ?? Control.DefaultFont;
        using var boldFont = new Font(baseFont, FontStyle.Bold);
        var title = new ToolStripLabel($"Filter/sort: {columnName}") { Font = (Font)boldFont.Clone() };
        menu.Items.Add(title);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(MenuItem("Sort Ascending", () => SetSort(columnName, "ASC", false)));
        menu.Items.Add(MenuItem("Sort Descending", () => SetSort(columnName, "DESC", false)));
        menu.Items.Add(MenuItem("Add to Multi-Sort Asc", () => SetSort(columnName, "ASC", true)));
        menu.Items.Add(MenuItem("Add to Multi-Sort Desc", () => SetSort(columnName, "DESC", true)));
        menu.Items.Add(MenuItem("Clear Sort for This Column", () => { _sorts.RemoveAll(s => s.Column.Equals(columnName, StringComparison.OrdinalIgnoreCase)); Apply(); }));
        menu.Items.Add(MenuItem("Clear All Sorts", () => { _sorts.Clear(); Apply(); }));
        menu.Items.Add(new ToolStripSeparator());

        var values = DistinctValues(columnName, 500).ToList();
        var activeValues = _valueFilters.TryGetValue(columnName, out var set) ? set : null;

        var panel = new Panel
        {
            Width = 390,
            Height = Math.Min(430, Math.Max(235, values.Count * 18 + 175)),
            Margin = Padding.Empty,
            Padding = new Padding(8)
        };

        var containsLabel = new Label
        {
            Text = "Column text contains:",
            AutoSize = true,
            Left = 8,
            Top = 8
        };
        panel.Controls.Add(containsLabel);

        var containsBox = new TextBox
        {
            Text = _containsFilters.TryGetValue(columnName, out var c) ? c : string.Empty,
            Left = 8,
            Top = containsLabel.Bottom + 4,
            Width = panel.Width - 18,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        panel.Controls.Add(containsBox);

        var valueLabel = new Label
        {
            Text = values.Count >= 500 ? "Distinct values (first 500; use text contains for more):" : "Distinct values:",
            AutoSize = true,
            Left = 8,
            Top = containsBox.Bottom + 8
        };
        panel.Controls.Add(valueLabel);

        var checkedList = new CheckedListBox
        {
            CheckOnClick = true,
            Left = 8,
            Top = valueLabel.Bottom + 4,
            Width = panel.Width - 18,
            Height = Math.Min(245, Math.Max(90, values.Count * 18 + 8)),
            IntegralHeight = false,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        foreach (var value in values)
        {
            var display = string.IsNullOrEmpty(value) ? "(blank)" : value;
            var idx = checkedList.Items.Add(display);
            checkedList.SetItemChecked(idx, activeValues == null || activeValues.Contains(value));
        }
        panel.Controls.Add(checkedList);

        var selectAll = new Button
        {
            Text = "Select All",
            Left = 8,
            Top = checkedList.Bottom + 8,
            Width = 88,
            Height = 26
        };
        selectAll.Click += (s, e) =>
        {
            for (int i = 0; i < checkedList.Items.Count; i++)
                checkedList.SetItemChecked(i, true);
        };
        panel.Controls.Add(selectAll);

        var clearAllValues = new Button
        {
            Text = "Clear All",
            Left = selectAll.Right + 8,
            Top = checkedList.Bottom + 8,
            Width = 88,
            Height = 26
        };
        clearAllValues.Click += (s, e) =>
        {
            for (int i = 0; i < checkedList.Items.Count; i++)
                checkedList.SetItemChecked(i, false);
        };
        panel.Controls.Add(clearAllValues);

        var apply = new Button
        {
            Text = "Apply Filter",
            Left = 8,
            Top = selectAll.Bottom + 8,
            Width = 112,
            Height = 28
        };
        apply.Click += (s, e) =>
        {
            ApplyFilterFromPopup(columnName, containsBox.Text, values, checkedList);
            SafeClose(menu);
        };
        panel.Controls.Add(apply);

        var clearColumn = new Button
        {
            Text = "Clear Column Filter",
            Left = apply.Right + 8,
            Top = selectAll.Bottom + 8,
            Width = 132,
            Height = 28
        };
        clearColumn.Click += (s, e) =>
        {
            _containsFilters.Remove(columnName);
            _valueFilters.Remove(columnName);
            Apply();
            SafeClose(menu);
        };
        panel.Controls.Add(clearColumn);

        var clearAllFilters = new Button
        {
            Text = "Clear All Header Filters",
            Left = 8,
            Top = apply.Bottom + 8,
            Width = 180,
            Height = 28
        };
        clearAllFilters.Click += (s, e) =>
        {
            _containsFilters.Clear();
            _valueFilters.Clear();
            Apply();
            SafeClose(menu);
        };
        panel.Controls.Add(clearAllFilters);

        var host = new ToolStripControlHost(panel)
        {
            AutoSize = false,
            Width = panel.Width,
            Height = panel.Height,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        menu.Items.Add(host);

        menu.Closed += (s, e) =>
        {
            try { menu.Dispose(); } catch { /* no-op: menu is already closing/disposed */ }
        };

        menu.Show(_grid, location);
        containsBox.Focus();
    }

    private void ApplyFilterFromPopup(string columnName, string containsText, IReadOnlyList<string> values, CheckedListBox checkedList)
    {
        var contains = containsText.Trim();
        if (string.IsNullOrWhiteSpace(contains))
            _containsFilters.Remove(columnName);
        else
            _containsFilters[columnName] = contains;

        if (values.Count > 0 && checkedList.CheckedItems.Count < checkedList.Items.Count)
        {
            var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var checkedItem in checkedList.CheckedItems)
            {
                var itemText = checkedItem?.ToString() ?? string.Empty;
                selected.Add(itemText == "(blank)" ? string.Empty : itemText);
            }
            _valueFilters[columnName] = selected;
        }
        else
        {
            _valueFilters.Remove(columnName);
        }

        Apply();
    }

    private static void SafeClose(ContextMenuStrip menu)
    {
        if (menu.IsDisposed)
            return;

        try
        {
            if (menu.Visible)
                menu.Close(ToolStripDropDownCloseReason.ItemClicked);
        }
        catch (ObjectDisposedException)
        {
            // The menu may already be closing as part of WinForms normal drop-down handling.
        }
    }

    private ToolStripMenuItem MenuItem(string text, Action action)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (s, e) => action();
        return item;
    }

    private void SetSort(string columnName, string direction, bool append)
    {
        if (!append) _sorts.Clear();
        _sorts.RemoveAll(s => s.Column.Equals(columnName, StringComparison.OrdinalIgnoreCase));
        _sorts.Add((columnName, direction.Equals("ASC", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC"));
        Apply();
    }

    private void Apply()
    {
        if (_baseTable == null)
            return;

        try
        {
            var dv = new DataView(_baseTable);
            var filters = new List<string>();
            foreach (var kvp in _containsFilters)
            {
                if (_baseTable.Columns.Contains(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value))
                    filters.Add($"Convert([{EscapeColumn(kvp.Key)}], 'System.String') LIKE '%{EscapeFilterValue(kvp.Value)}%'");
            }
            foreach (var kvp in _valueFilters)
            {
                if (!_baseTable.Columns.Contains(kvp.Key)) continue;
                if (kvp.Value.Count == 0)
                {
                    filters.Add("1 = 0");
                    continue;
                }
                var parts = kvp.Value.Select(v => $"Convert([{EscapeColumn(kvp.Key)}], 'System.String') = '{EscapeFilterValue(v)}'");
                filters.Add("(" + string.Join(" OR ", parts) + ")");
            }

            dv.RowFilter = string.Join(" AND ", filters);
            dv.Sort = string.Join(", ", _sorts.Where(s => _baseTable.Columns.Contains(s.Column)).Select(s => $"[{EscapeColumn(s.Column)}] {s.Direction}"));
            var filtered = dv.ToTable();
            _grid.DataSource = filtered;
            _status($"{_label}: {filtered.Rows.Count:N0} visible. Header filters: {_containsFilters.Count + _valueFilters.Count}; Sort: {DescribeSorts()}".Trim());
        }
        catch (Exception ex)
        {
            MessageBox.Show(_grid.FindForm(), ex.Message, "Column Filter Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private IEnumerable<string> DistinctValues(string columnName, int max)
    {
        if (_baseTable == null || !_baseTable.Columns.Contains(columnName)) yield break;
        var values = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DataRow row in _baseTable.Rows)
        {
            values.Add(row[columnName]?.ToString() ?? string.Empty);
            if (values.Count >= max) break;
        }
        foreach (var value in values) yield return value;
    }

    private void PruneStateForCurrentColumns()
    {
        if (_baseTable == null) return;
        foreach (var key in _containsFilters.Keys.ToList()) if (!_baseTable.Columns.Contains(key)) _containsFilters.Remove(key);
        foreach (var key in _valueFilters.Keys.ToList()) if (!_baseTable.Columns.Contains(key)) _valueFilters.Remove(key);
        _sorts.RemoveAll(s => !_baseTable.Columns.Contains(s.Column));
    }

    private string DescribeSorts() => _sorts.Count == 0 ? "none" : string.Join(" -> ", _sorts.Select(s => $"{s.Column} {s.Direction}"));
    private static string EscapeColumn(string columnName) => columnName.Replace("]", "]]", StringComparison.Ordinal);
    private static string EscapeFilterValue(string value) => (value ?? string.Empty).Replace("'", "''", StringComparison.Ordinal).Replace("[", "[[]", StringComparison.Ordinal).Replace("%", "[%]", StringComparison.Ordinal).Replace("*", "[*]", StringComparison.Ordinal);

    private static string BoundColumnName(DataGridViewColumn col)
    {
        if (!string.IsNullOrWhiteSpace(col.DataPropertyName)) return col.DataPropertyName;
        if (!string.IsNullOrWhiteSpace(col.Name)) return col.Name;
        return col.HeaderText ?? string.Empty;
    }

    private static DataTable? ToTable(object? source)
    {
        if (source is DataTable dt) return dt.Copy();
        if (source is DataView dv) return dv.ToTable();
        if (source is BindingSource bs) return ToTable(bs.DataSource);
        return null;
    }
}
