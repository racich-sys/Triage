using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.IO.Compression;

namespace VestigantTriage;

internal static class VestigantEntry
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Any(a => a.Equals("--headless-triage", StringComparison.OrdinalIgnoreCase) || a.Equals("headless-triage", StringComparison.OrdinalIgnoreCase)))
            return HeadlessTriageRunner.Run(args);

        if (args.Any(a => a.Equals("--headless-google", StringComparison.OrdinalIgnoreCase) || a.Equals("headless-google", StringComparison.OrdinalIgnoreCase)))
            return HeadlessTriageRunner.RunGoogleSourceTriage(args);

        if (args.Any(a => a.Equals("--export-validation-bundle", StringComparison.OrdinalIgnoreCase) || a.Equals("export-validation-bundle", StringComparison.OrdinalIgnoreCase)))
            return HeadlessTriageRunner.ExportValidationBundle(args);

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }
}

internal sealed class MainForm : Form
{
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };

    // 1. Case Tab Controls
    private readonly TextBox _casePath = new() { Dock = DockStyle.Fill, ReadOnly = true };
    private readonly TextBox _caseName = new() { Dock = DockStyle.Fill };
    private readonly TextBox _caseNumber = new() { Dock = DockStyle.Fill };
    private readonly TextBox _caseSubject = new() { Dock = DockStyle.Fill };
    private readonly TextBox _caseCompany = new() { Dock = DockStyle.Fill };
    private readonly TextBox _caseInvestigator = new() { Dock = DockStyle.Fill };
    private readonly Button _caseCreate = new() { Text = "Create Case", AutoSize = true };
    private readonly Button _caseSave = new() { Text = "Save Case", AutoSize = true };
    private readonly Button _caseOpen = new() { Text = "Open Case", AutoSize = true };
    private readonly TextBox _caseLog = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both, ReadOnly = true, Font = new Font("Consolas", 9), BackColor = Color.Black, ForeColor = Color.Lime };

    // Tag Management Controls
    private readonly TextBox _tagNameInput = new() { Width = 180 };
    private readonly TextBox _tagDescriptionInput = new() { Width = 360 };
    private readonly Button _tagAddOrUpdate = new() { Text = "Add / Update Tag", AutoSize = true };
    private readonly Button _tagDelete = new() { Text = "Delete Selected Tag", AutoSize = true };
    private readonly Button _tagRefresh = new() { Text = "Refresh Tags", AutoSize = true };
    private readonly DataGridView _tagGrid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false };

    // 2. Evidence Tab Controls
    private readonly Button _evidenceAddSources = new() { Text = "Add Evidence", AutoSize = true };
    private readonly DataGridView _evidenceGrid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
    private readonly Button _evidenceRunIngest = new() { Text = "Ingest Pending Evidence", AutoSize = true, Height = 34 };
    private readonly ProgressBar _evidenceProgress = new() { Width = 200, Visible = false };
    private readonly BindingSource _caseSourceBinding = new();

    // 3. Timeline / Metadata Tab Controls (RESTORED & UPGRADED)
    private readonly DateTimePicker _dateFrom = new() { Format = DateTimePickerFormat.Short, Width = 120 };
    private readonly DateTimePicker _dateTo = new() { Format = DateTimePickerFormat.Short, Width = 120 };
    private readonly TextBox _globalFilter = new() { Width = 200 };
    private readonly ComboBox _timelineTimestampMode = new() { Width = 185, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button _searchRun = new() { Text = "Apply Timeline Filter", AutoSize = true };
    private readonly DataGridView _searchGrid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, BackgroundColor = Color.White };
    private readonly DataGridView _searchDetailsGrid = new() { Dock = DockStyle.Fill, ReadOnly = true, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
    private readonly ComboBox _timelineFieldFilter = new() { Width = 170, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _timelineFieldValue = new() { Width = 180 };
    private readonly Button _timelineAddFilter = new() { Text = "Add Field Filter", AutoSize = true };
    private readonly Button _timelineClearFilters = new() { Text = "Clear Filters", AutoSize = true };
    private readonly ListBox _timelineActiveFilters = new() { Dock = DockStyle.Fill, Height = 70 };
    private readonly ComboBox _timelineSort1 = new() { Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _timelineSort2 = new() { Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _timelineSort3 = new() { Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _timelineSortDir1 = new() { Width = 70, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _timelineSortDir2 = new() { Width = 70, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _timelineSortDir3 = new() { Width = 70, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button _timelineApplyView = new() { Text = "Apply View", AutoSize = true };
    private readonly ComboBox _timelineTagCombo = new() { Width = 180, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button _timelineTagSelected = new() { Text = "Tag Selected Timeline Rows", AutoSize = true };
    private readonly Button _timelineUntagSelected = new() { Text = "Remove Tag From Selected", AutoSize = true };
    private readonly Button _timelineExportAllMetadataCsv = new() { Text = "Export All Master Metadata CSV", AutoSize = true };
    private readonly ComboBox _timelinePageSize = new() { Width = 80, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button _timelineFirstPage = new() { Text = "First", AutoSize = true };
    private readonly Button _timelinePrevPage = new() { Text = "Prev", AutoSize = true };
    private readonly Button _timelineNextPage = new() { Text = "Next", AutoSize = true };
    private readonly Label _timelinePageLabel = new() { Text = "Page 1", AutoSize = true, Margin = new Padding(8, 6, 0, 0) };

    // 4. Risk Tab Controls
    private readonly Button _dbRunRisk = new() { Text = "Run Risk Engine", AutoSize = true, BackColor = Color.DarkRed, ForeColor = Color.White };
    private readonly Button _riskRefresh = new() { Text = "Refresh Risk View", AutoSize = true };
    private readonly Button _riskClearFilters = new() { Text = "Clear Risk Filters", AutoSize = true };
    private readonly Button _riskExportCsv = new() { Text = "Export Risk CSV", AutoSize = true };
    private readonly ComboBox _riskLevelFilter = new() { Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _riskDomainFilter = new() { Width = 180, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _riskUserFilter = new() { Width = 150 };
    private readonly TextBox _riskTargetFilter = new() { Width = 220 };
    private readonly TextBox _riskRuleFilter = new() { Width = 150 };
    private readonly NumericUpDown _riskMinScore = new() { Width = 70, Minimum = 0, Maximum = 1000, Increment = 5, Value = 0 };
    private readonly DataGridView _riskGrid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, BackgroundColor = Color.White, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = true };
    private readonly DataGridView _riskDetailsGrid = new() { Dock = DockStyle.Fill, ReadOnly = true, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
    private readonly TextBox _riskSummaryBox = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9), BackColor = Color.White };
    private readonly Label _dbSummary = new() { AutoSize = true, Text = "Risk hits will appear here." };
    private readonly ComboBox _riskTagCombo = new() { Width = 180, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button _riskTagSelected = new() { Text = "Tag Selected Risk Events", AutoSize = true };
    private readonly Button _riskUntagSelected = new() { Text = "Remove Tag From Selected", AutoSize = true };
    private readonly ComboBox _riskPageSize = new() { Width = 80, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button _riskFirstPage = new() { Text = "First", AutoSize = true };
    private readonly Button _riskPrevPage = new() { Text = "Prev", AutoSize = true };
    private readonly Button _riskNextPage = new() { Text = "Next", AutoSize = true };
    private readonly Label _riskPageLabel = new() { Text = "Page 1", AutoSize = true, Margin = new Padding(8, 6, 0, 0) };

    // Tagged Review Tab Controls
    private readonly Button _taggedRefresh = new() { Text = "Refresh Tagged Data", AutoSize = true };
    private readonly Button _taggedExportCsv = new() { Text = "Export Tagged CSV", AutoSize = true };
    private readonly Button _taggedRemoveTag = new() { Text = "Remove Selected Tag Link", AutoSize = true };
    private readonly ComboBox _taggedTagFilter = new() { Width = 180, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _taggedGlobalFilter = new() { Width = 240 };
    private readonly DataGridView _taggedGrid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, BackgroundColor = Color.White, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = true };
    private readonly DataGridView _taggedDetailsGrid = new() { Dock = DockStyle.Fill, ReadOnly = true, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
    private DataTable? _taggedBaseTable;
    private readonly ComboBox _taggedPageSize = new() { Width = 80, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button _taggedFirstPage = new() { Text = "First", AutoSize = true };
    private readonly Button _taggedPrevPage = new() { Text = "Prev", AutoSize = true };
    private readonly Button _taggedNextPage = new() { Text = "Next", AutoSize = true };
    private readonly Label _taggedPageLabel = new() { Text = "Page 1", AutoSize = true, Margin = new Padding(8, 7, 0, 0) };

    // Parser Coverage / Validation Tab Controls
    private readonly Button _coverageRefresh = new() { Text = "Refresh Coverage", AutoSize = true };
    private readonly Button _coverageExport = new() { Text = "Export Current Coverage Grid", AutoSize = true };
    private readonly Button _coverageValidateFolder = new() { Text = "Validate Fixture Folder", AutoSize = true };
    private readonly Button _coverageExportValidation = new() { Text = "Export Validation CSV", AutoSize = true };
    private readonly Button _coverageExportBundle = new() { Text = "Export Validation Bundle", AutoSize = true };
    private readonly DataGridView _coverageGrid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, BackgroundColor = Color.White, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = true };
    private readonly DataGridView _coverageSourcesGrid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, BackgroundColor = Color.White, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = true };
    private readonly DataGridView _coverageErrorsGrid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, BackgroundColor = Color.White, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = true };
    private readonly DataGridView _validationGrid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, BackgroundColor = Color.White, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = true };
    private readonly DataGridView _maintenanceGrid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, BackgroundColor = Color.White, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
    private readonly Button _maintenanceRefresh = new() { Text = "Refresh Diagnostics", AutoSize = true };
    private readonly Button _maintenanceOptimize = new() { Text = "Rebuild Indexes / Optimize", AutoSize = true };
    private readonly Button _maintenanceVacuum = new() { Text = "VACUUM Database", AutoSize = true };
    private readonly TabControl _coverageInnerTabs = new() { Dock = DockStyle.Fill };
    private GridHeaderFilterManager? _coverageHeaderFilter;
    private GridHeaderFilterManager? _coverageSourcesHeaderFilter;
    private GridHeaderFilterManager? _coverageErrorsHeaderFilter;
    private GridHeaderFilterManager? _validationHeaderFilter;

    private readonly ToolStripStatusLabel _status = new() { Text = "Ready" };
    private CaseFile? _currentCase;
    private string _currentCasePath = "";
    private DataTable? _timelineBaseTable;
    private DataTable? _riskBaseTable;
    private readonly List<GridFilterCondition> _timelineFilters = new();
    private int _timelinePageIndex;
    private int _riskPageIndex;
    private int _taggedPageIndex;
    private const int DefaultPageSize = 1000;
    private bool _timelineLoaded;
    private bool _riskLoaded;
    private bool _taggedLoaded;
    private bool _coverageLoaded;
    private bool _maintenanceLoaded;
    private volatile bool _ingestRunning;
    private volatile bool _riskRunning;
    private bool _suppressRiskFilterEvents;
    private bool _suppressTagFilterEvents;
    private int _timelineLoadVersion;
    private int _riskLoadVersion;
    private int _taggedLoadVersion;
    private int _coverageLoadVersion;
    private int _maintenanceLoadVersion;
    private GridHeaderFilterManager? _timelineHeaderFilter;
    private GridHeaderFilterManager? _riskHeaderFilter;
    private GridHeaderFilterManager? _taggedHeaderFilter;

    public MainForm()
    {
        Text = AppInfo.DisplayName;
        Width = 1600; Height = 1000;
        StartPosition = FormStartPosition.CenterScreen;

        // Initialize date ranges to a wide default
        _dateFrom.Value = DateTime.Now.AddYears(-2);
        _dateTo.Value = DateTime.Now.AddDays(1);

        ConfigureGrids();
        InitializeLayout();
        Shown += async (s, e) => await TryAutoOpenLastCaseAsync();
    }

    private void ConfigureGrids()
    {
        _evidenceGrid.AutoGenerateColumns = false;
        _evidenceGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Status", HeaderText = "Status", Width = 80 });
        _evidenceGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "SourceType", HeaderText = "Classification", Width = 180 });
        _evidenceGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "FileName", HeaderText = "Staged Name", Width = 200 });
        _evidenceGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "OriginalSourcePath", HeaderText = "Internal Source Path", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _evidenceGrid.Columns.Add(new DataGridViewCheckBoxColumn { DataPropertyName = "ImportedToDb", HeaderText = "Ingested", Width = 70 });
        _evidenceGrid.DataSource = _caseSourceBinding;

        _timelineTimestampMode.Items.AddRange(new object[] { "Behavioral dates only", "All dated events", "Metadata/fallback only", "Undated/uncertain" });
        _timelineTimestampMode.SelectedIndex = 0;
        _timelineTimestampMode.SelectedIndexChanged += (s, e) => { _timelinePageIndex = 0; RunMasterSearch(); };
        ConfigurePageSizeCombo(_timelinePageSize, DefaultPageSize);
        ConfigurePageSizeCombo(_riskPageSize, DefaultPageSize);
        ConfigurePageSizeCombo(_taggedPageSize, DefaultPageSize);

        _timelineHeaderFilter = new GridHeaderFilterManager(_searchGrid, msg => _status.Text = msg);
        _riskHeaderFilter = new GridHeaderFilterManager(_riskGrid, msg => _status.Text = msg);
        _taggedHeaderFilter = new GridHeaderFilterManager(_taggedGrid, msg => _status.Text = msg);
        _coverageHeaderFilter = new GridHeaderFilterManager(_coverageGrid, msg => _status.Text = msg);
        _coverageSourcesHeaderFilter = new GridHeaderFilterManager(_coverageSourcesGrid, msg => _status.Text = msg);
        _coverageErrorsHeaderFilter = new GridHeaderFilterManager(_coverageErrorsGrid, msg => _status.Text = msg);
        _validationHeaderFilter = new GridHeaderFilterManager(_validationGrid, msg => _status.Text = msg);

        foreach (var grid in EnumerateReviewGrids())
            ConfigureSafeGridDisplay(grid);
    }

    private IEnumerable<DataGridView> EnumerateReviewGrids()
    {
        yield return _tagGrid;
        yield return _evidenceGrid;
        yield return _searchGrid;
        yield return _searchDetailsGrid;
        yield return _riskGrid;
        yield return _riskDetailsGrid;
        yield return _taggedGrid;
        yield return _taggedDetailsGrid;
        yield return _coverageGrid;
        yield return _coverageSourcesGrid;
        yield return _coverageErrorsGrid;
        yield return _validationGrid;
        yield return _maintenanceGrid;
    }

    private void ConfigureSafeGridDisplay(DataGridView grid)
    {
        grid.DataError -= SafeGrid_DataError;
        grid.DataError += SafeGrid_DataError;
        grid.DataBindingComplete -= SafeGrid_DataBindingComplete;
        grid.DataBindingComplete += SafeGrid_DataBindingComplete;
    }

    private void SafeGrid_DataBindingComplete(object? sender, DataGridViewBindingCompleteEventArgs e)
    {
        if (sender is not DataGridView grid)
            return;

        ConvertAutoGeneratedImageColumnsToText(grid);
    }

    private void SafeGrid_DataError(object? sender, DataGridViewDataErrorEventArgs e)
    {
        e.ThrowException = false;
        if (sender is DataGridView grid && e.ColumnIndex >= 0 && e.ColumnIndex < grid.Columns.Count)
        {
            var columnName = grid.Columns[e.ColumnIndex].HeaderText;
            _status.Text = $"Suppressed grid display conversion warning in column '{columnName}'.";
        }
        else
        {
            _status.Text = "Suppressed grid display conversion warning.";
        }
    }

    private static void ConvertAutoGeneratedImageColumnsToText(DataGridView grid)
    {
        for (var i = 0; i < grid.Columns.Count; i++)
        {
            if (grid.Columns[i] is not DataGridViewImageColumn imageColumn)
                continue;

            var replacement = new DataGridViewTextBoxColumn
            {
                Name = imageColumn.Name,
                HeaderText = imageColumn.HeaderText,
                DataPropertyName = imageColumn.DataPropertyName,
                Width = Math.Max(80, imageColumn.Width),
                MinimumWidth = Math.Max(5, imageColumn.MinimumWidth),
                Visible = imageColumn.Visible,
                ReadOnly = imageColumn.ReadOnly,
                Frozen = imageColumn.Frozen,
                SortMode = DataGridViewColumnSortMode.Automatic,
                AutoSizeMode = imageColumn.AutoSizeMode,
                DefaultCellStyle = imageColumn.DefaultCellStyle,
                ToolTipText = imageColumn.ToolTipText
            };

            grid.Columns.RemoveAt(i);
            grid.Columns.Insert(i, replacement);
        }
    }

    private void InitializeLayout()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 80, BackColor = Color.White };
        var picLogo = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, Width = 100, Dock = DockStyle.Left, Padding = new Padding(10) };
        if (File.Exists("Logo-green-Animated.gif")) picLogo.ImageLocation = "Logo-green-Animated.gif";
        var lblTitle = new Label { Text = AppInfo.DisplayName, Font = new Font("Segoe UI Semibold", 24F), ForeColor = Color.FromArgb(60, 110, 80), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        header.Controls.Add(lblTitle); header.Controls.Add(picLogo);

        var ss = new StatusStrip(); ss.Items.Add(_status);

        _tabs.TabPages.Add(BuildCaseTab());
        _tabs.TabPages.Add(BuildEvidenceTab());
        _tabs.TabPages.Add(BuildParserCoverageTab());
        _tabs.TabPages.Add(BuildSearchTab());
        _tabs.TabPages.Add(BuildRiskTab());
        _tabs.TabPages.Add(BuildTaggedTab());
        _tabs.TabPages.Add(BuildDatabaseMaintenanceTab());
        _tabs.SelectedIndexChanged += (s, e) => LoadSelectedTabOnDemand();

        Controls.Add(_tabs);
        Controls.Add(header);
        Controls.Add(ss);
        _tabs.BringToFront();
    }

    private static void ConfigurePageSizeCombo(ComboBox combo, int defaultValue)
    {
        combo.Items.Clear();
        combo.Items.AddRange(new object[] { "1000", "2500", "5000", "10000" });
        combo.SelectedItem = defaultValue.ToString();
    }

    private static int SelectedPageSize(ComboBox combo)
    {
        if (int.TryParse(combo.SelectedItem?.ToString(), out var value) && value > 0)
            return value;
        return DefaultPageSize;
    }

    private void LoadSelectedTabOnDemand()
    {
        _ = LoadSelectedTabOnDemandAsync();
    }

    private async Task LoadSelectedTabOnDemandAsync()
    {
        if (_currentCase == null)
            return;
        try
        {
            var title = _tabs.SelectedTab?.Text ?? string.Empty;
            if (title.StartsWith("Master Timeline", StringComparison.OrdinalIgnoreCase) && !_timelineLoaded)
            {
                await RunMasterSearchAsync();
                _timelineLoaded = true;
            }
            else if (title.StartsWith("Risk", StringComparison.OrdinalIgnoreCase) && !_riskLoaded)
            {
                await LoadRiskDataAsync();
                _riskLoaded = true;
            }
            else if (title.StartsWith("Tagged", StringComparison.OrdinalIgnoreCase) && !_taggedLoaded)
            {
                await LoadTaggedDataAsync();
                _taggedLoaded = true;
            }
            else if (title.StartsWith("Parser Coverage", StringComparison.OrdinalIgnoreCase) && !_coverageLoaded)
            {
                await LoadParserCoverageDataAsync();
                _coverageLoaded = true;
            }
            else if (title.StartsWith("Database Maintenance", StringComparison.OrdinalIgnoreCase) && !_maintenanceLoaded)
            {
                await RefreshDatabaseDiagnosticsAsync();
                _maintenanceLoaded = true;
            }
        }
        catch (Exception ex)
        {
            LogCase($"Deferred tab load failed: {ex.Message}");
            _status.Text = "Deferred tab load failed. See audit log.";
        }
    }

    private void MarkViewsStale()
    {
        _timelineLoaded = false;
        _riskLoaded = false;
        _taggedLoaded = false;
        _coverageLoaded = false;
        _maintenanceLoaded = false;
    }

    private TabPage BuildCaseTab()
    {
        var page = new TabPage("Case Management") { BackColor = Color.White };
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 210));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var detailsGroup = new GroupBox { Text = "Case Details", Dock = DockStyle.Fill, Margin = new Padding(10) };
        var meta = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 4, Padding = new Padding(5) };
        meta.Controls.Add(new Label { Text = "Case File" }, 0, 0);
        meta.Controls.Add(_casePath, 1, 0); meta.SetColumnSpan(_casePath, 3);
        meta.Controls.Add(new Label { Text = "Case Name" }, 0, 1); meta.Controls.Add(_caseName, 1, 1);
        meta.Controls.Add(new Label { Text = "Case Number" }, 2, 1); meta.Controls.Add(_caseNumber, 3, 1);
        meta.Controls.Add(new Label { Text = "Subject Name" }, 0, 2); meta.Controls.Add(_caseSubject, 1, 2);
        meta.Controls.Add(new Label { Text = "Company" }, 2, 2); meta.Controls.Add(_caseCompany, 3, 2);
        meta.Controls.Add(new Label { Text = "Investigator" }, 0, 3); meta.Controls.Add(_caseInvestigator, 1, 3); meta.SetColumnSpan(_caseInvestigator, 3);
        detailsGroup.Controls.Add(meta);

        var btnRow = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10, 0, 0, 0) };
        _caseCreate.Click += (s, e) => CreateNewCase();
        _caseSave.Click += (s, e) => SaveCase(true);
        _caseOpen.Click += async (s, e) => await OpenCaseAsync();
        btnRow.Controls.AddRange(new Control[] { _caseCreate, _caseSave, _caseOpen });

        var tagGroup = BuildCaseTagManager();

        root.Controls.Add(detailsGroup, 0, 0);
        root.Controls.Add(btnRow, 0, 1);
        root.Controls.Add(tagGroup, 0, 2);
        root.Controls.Add(new GroupBox { Text = "Audit Log", Dock = DockStyle.Fill, Controls = { _caseLog } }, 0, 3);
        page.Controls.Add(root);
        return page;
    }

    private GroupBox BuildCaseTagManager()
    {
        var group = new GroupBox { Text = "Case Tags", Dock = DockStyle.Fill, Padding = new Padding(8), Margin = new Padding(10, 0, 10, 6) };
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = false };
        _tagAddOrUpdate.Click += (s, e) => AddOrUpdateTagFromCaseTab();
        _tagDelete.Click += (s, e) => DeleteSelectedTagFromCaseTab();
        _tagRefresh.Click += (s, e) => RefreshTagControls();
        _tagGrid.SelectionChanged += (s, e) => LoadSelectedTagIntoEditor();
        _tagNameInput.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { AddOrUpdateTagFromCaseTab(); e.SuppressKeyPress = true; } };
        _tagDescriptionInput.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { AddOrUpdateTagFromCaseTab(); e.SuppressKeyPress = true; } };
        actions.Controls.AddRange(new Control[] {
            new Label { Text = "Tag:", AutoSize = true, Margin = new Padding(0, 7, 0, 0) }, _tagNameInput,
            new Label { Text = "Description:", AutoSize = true, Margin = new Padding(8, 7, 0, 0) }, _tagDescriptionInput,
            _tagAddOrUpdate, _tagDelete, _tagRefresh
        });

        root.Controls.Add(actions, 0, 0);
        root.Controls.Add(_tagGrid, 0, 1);
        group.Controls.Add(root);
        return group;
    }

    private TabPage BuildEvidenceTab()
    {
        var page = new TabPage("Evidence Processing");
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(5) };
        _evidenceAddSources.Click += async (s, e) => await AddEvidenceAsync();
        _evidenceRunIngest.Click += async (s, e) => await ProcessEvidenceAsync();
        actions.Controls.AddRange(new Control[] { _evidenceAddSources, _evidenceRunIngest, _evidenceProgress });
        root.Controls.Add(actions, 0, 0);
        root.Controls.Add(_evidenceGrid, 0, 1);
        page.Controls.Add(root);
        return page;
    }

    private TabPage BuildParserCoverageTab()
    {
        var page = new TabPage("Parser Coverage & Validation") { BackColor = Color.White };
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), AutoScroll = true, WrapContents = true, BackColor = Color.FromArgb(240, 240, 240) };
        _coverageRefresh.Click += (s, e) => LoadParserCoverageData();
        _coverageExport.Click += (s, e) => ExportCoverageCurrentGrid();
        _coverageValidateFolder.Click += (s, e) => ValidateParserFixtureFolder();
        _coverageExportValidation.Click += (s, e) => ExportGridToCsv(_validationGrid, "parser_validation_results.csv");
        _coverageExportBundle.Click += (s, e) => ExportValidationBundle();
        actions.Controls.AddRange(new Control[] {
            _coverageRefresh,
            _coverageExport,
            _coverageValidateFolder,
            _coverageExportValidation,
            _coverageExportBundle,
            new Label { Text = "Right-click column headers for Excel-style filter/sort.", AutoSize = true, Margin = new Padding(14, 8, 0, 0) }
        });

        _coverageInnerTabs.TabPages.Add(BuildGridPage("Parser Coverage", _coverageGrid));
        _coverageInnerTabs.TabPages.Add(BuildGridPage("Source Coverage", _coverageSourcesGrid));
        _coverageInnerTabs.TabPages.Add(BuildGridPage("Parser Errors", _coverageErrorsGrid));
        _coverageInnerTabs.TabPages.Add(BuildGridPage("Fixture Validation", _validationGrid));

        _coverageGrid.DataBindingComplete += (s, e) => ConfigureCoverageGrid(_coverageGrid);
        _coverageSourcesGrid.DataBindingComplete += (s, e) => ConfigureCoverageGrid(_coverageSourcesGrid);
        _coverageErrorsGrid.DataBindingComplete += (s, e) => ConfigureCoverageGrid(_coverageErrorsGrid);
        _validationGrid.DataBindingComplete += (s, e) => ConfigureCoverageGrid(_validationGrid);

        root.Controls.Add(actions, 0, 0);
        root.Controls.Add(_coverageInnerTabs, 0, 1);
        page.Controls.Add(root);
        return page;
    }

    private static TabPage BuildGridPage(string title, DataGridView grid)
    {
        var page = new TabPage(title) { BackColor = Color.White };
        page.Controls.Add(grid);
        return page;
    }

    private TabPage BuildSearchTab()
    {
        var page = new TabPage("Master Timeline & Metadata");
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var topFilters = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), AutoScroll = true, BackColor = Color.FromArgb(240, 240, 240), WrapContents = true };
        _searchRun.Click += (s, e) => RunMasterSearch();
        _timelineAddFilter.Click += (s, e) => AddTimelineFieldFilter();
        _timelineClearFilters.Click += (s, e) => ClearTimelineFilters();
        _timelineApplyView.Click += (s, e) => { _timelinePageIndex = 0; RunMasterSearch(); };
        _timelineFirstPage.Click += (s, e) => { _timelinePageIndex = 0; RunMasterSearch(); };
        _timelinePrevPage.Click += (s, e) => { if (_timelinePageIndex > 0) _timelinePageIndex--; RunMasterSearch(); };
        _timelineNextPage.Click += (s, e) => { _timelinePageIndex++; RunMasterSearch(); };
        _timelinePageSize.SelectedIndexChanged += (s, e) => { _timelinePageIndex = 0; RunMasterSearch(); };
        _timelineTagSelected.Click += (s, e) => TagSelectedRows(_searchGrid, "ID", _timelineTagCombo, "Timeline");
        _timelineUntagSelected.Click += (s, e) => UntagSelectedRows(_searchGrid, "ID", _timelineTagCombo);
        _timelineExportAllMetadataCsv.Click += async (s, e) => await ExportAllMasterMetadataCsvAsync();
        _globalFilter.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { ApplyTimelineView(); e.SuppressKeyPress = true; } };
        _timelineFieldValue.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { AddTimelineFieldFilter(); e.SuppressKeyPress = true; } };

        topFilters.Controls.AddRange(new Control[] {
            new Label { Text = "From:", AutoSize = true, Margin = new Padding(0, 6, 0, 0) }, _dateFrom,
            new Label { Text = "To:", AutoSize = true, Margin = new Padding(10, 6, 0, 0) }, _dateTo,
            new Label { Text = "Global contains:", AutoSize = true, Margin = new Padding(10, 6, 0, 0) }, _globalFilter,
            new Label { Text = "Time mode:", AutoSize = true, Margin = new Padding(10, 6, 0, 0) }, _timelineTimestampMode,
            _searchRun,
            new Label { Text = "Field:", AutoSize = true, Margin = new Padding(14, 6, 0, 0) }, _timelineFieldFilter,
            new Label { Text = "Contains:", AutoSize = true, Margin = new Padding(8, 6, 0, 0) }, _timelineFieldValue,
            _timelineAddFilter,
            _timelineClearFilters,
            new Label { Text = "Page size:", AutoSize = true, Margin = new Padding(14, 6, 0, 0) }, _timelinePageSize,
            _timelineFirstPage, _timelinePrevPage, _timelineNextPage, _timelinePageLabel
        });

        var sortFilters = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(10, 0, 10, 6), BackColor = Color.FromArgb(248, 248, 248) };
        sortFilters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        sortFilters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));

        var sortPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true };
        ConfigureDirectionCombo(_timelineSortDir1, "DESC");
        ConfigureDirectionCombo(_timelineSortDir2, "ASC");
        ConfigureDirectionCombo(_timelineSortDir3, "ASC");
        sortPanel.Controls.AddRange(new Control[] {
            new Label { Text = "Sort 1:", AutoSize = true, Margin = new Padding(0, 6, 0, 0) }, _timelineSort1, _timelineSortDir1,
            new Label { Text = "Sort 2:", AutoSize = true, Margin = new Padding(8, 6, 0, 0) }, _timelineSort2, _timelineSortDir2,
            new Label { Text = "Sort 3:", AutoSize = true, Margin = new Padding(8, 6, 0, 0) }, _timelineSort3, _timelineSortDir3,
            _timelineApplyView,
            new Label { Text = "Tag:", AutoSize = true, Margin = new Padding(14, 6, 0, 0) }, _timelineTagCombo,
            _timelineTagSelected, _timelineUntagSelected, _timelineExportAllMetadataCsv
        });

        var activeFilterGroup = new GroupBox { Text = "Active Timeline Field Filters", Dock = DockStyle.Fill, Padding = new Padding(6) };
        activeFilterGroup.Controls.Add(_timelineActiveFilters);
        sortFilters.Controls.Add(sortPanel, 0, 0);
        sortFilters.Controls.Add(activeFilterGroup, 1, 0);

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 620 };
        split.Panel1.Controls.Add(_searchGrid);
        split.Panel2.Controls.Add(_searchDetailsGrid);
        _searchGrid.SelectionChanged += SearchGrid_SelectionChanged;
        _searchGrid.DataBindingComplete += (s, e) => ConfigureGridPresentation(_searchGrid);

        root.Controls.Add(topFilters, 0, 0);
        root.Controls.Add(sortFilters, 0, 1);
        root.Controls.Add(split, 0, 2);
        page.Controls.Add(root);
        return page;
    }

    private TabPage BuildRiskTab()
    {
        var page = new TabPage("Risk Engine");
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), AutoScroll = true, WrapContents = true, BackColor = Color.FromArgb(240, 240, 240) };
        _dbRunRisk.Click += async (s, e) => await RunRiskEngineAsync();
        _riskRefresh.Click += (s, e) => { _riskPageIndex = 0; LoadRiskData(); };
        _riskClearFilters.Click += (s, e) => ClearRiskFilters();
        _riskFirstPage.Click += (s, e) => { _riskPageIndex = 0; LoadRiskData(); };
        _riskPrevPage.Click += (s, e) => { if (_riskPageIndex > 0) _riskPageIndex--; LoadRiskData(); };
        _riskNextPage.Click += (s, e) => { _riskPageIndex++; LoadRiskData(); };
        _riskPageSize.SelectedIndexChanged += (s, e) => { _riskPageIndex = 0; LoadRiskData(); };
        _riskExportCsv.Click += (s, e) => ExportGridToCsv(_riskGrid, "risk_hits.csv");
        _riskTagSelected.Click += (s, e) => TagSelectedRows(_riskGrid, "Event_ID", _riskTagCombo, "Risk");
        _riskUntagSelected.Click += (s, e) => UntagSelectedRows(_riskGrid, "Event_ID", _riskTagCombo);
        _riskLevelFilter.SelectedIndexChanged += (s, e) => { if (_suppressRiskFilterEvents) return; _riskPageIndex = 0; LoadRiskData(); };
        _riskDomainFilter.SelectedIndexChanged += (s, e) => { if (_suppressRiskFilterEvents) return; _riskPageIndex = 0; LoadRiskData(); };
        _riskUserFilter.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { _riskPageIndex = 0; LoadRiskData(); e.SuppressKeyPress = true; } };
        _riskTargetFilter.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { _riskPageIndex = 0; LoadRiskData(); e.SuppressKeyPress = true; } };
        _riskRuleFilter.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { _riskPageIndex = 0; LoadRiskData(); e.SuppressKeyPress = true; } };
        _riskMinScore.ValueChanged += (s, e) => { _riskPageIndex = 0; LoadRiskData(); };

        actions.Controls.AddRange(new Control[] {
            _dbRunRisk,
            _riskRefresh,
            _riskExportCsv,
            _riskClearFilters,
            new Label { Text = "Level:", AutoSize = true, Margin = new Padding(14, 6, 0, 0) }, _riskLevelFilter,
            new Label { Text = "Domain:", AutoSize = true, Margin = new Padding(8, 6, 0, 0) }, _riskDomainFilter,
            new Label { Text = "Min score:", AutoSize = true, Margin = new Padding(8, 6, 0, 0) }, _riskMinScore,
            new Label { Text = "User contains:", AutoSize = true, Margin = new Padding(8, 6, 0, 0) }, _riskUserFilter,
            new Label { Text = "Target contains:", AutoSize = true, Margin = new Padding(8, 6, 0, 0) }, _riskTargetFilter,
            new Label { Text = "Rule contains:", AutoSize = true, Margin = new Padding(8, 6, 0, 0) }, _riskRuleFilter,
            new Label { Text = "Tag:", AutoSize = true, Margin = new Padding(14, 6, 0, 0) }, _riskTagCombo,
            _riskTagSelected, _riskUntagSelected,
            new Label { Text = "Page size:", AutoSize = true, Margin = new Padding(14, 6, 0, 0) }, _riskPageSize,
            _riskFirstPage, _riskPrevPage, _riskNextPage, _riskPageLabel
        });

        var summaryGroup = new GroupBox { Text = "Risk Summary", Dock = DockStyle.Fill, Padding = new Padding(8) };
        summaryGroup.Controls.Add(_riskSummaryBox);

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 610 };
        split.Panel1.Controls.Add(_riskGrid);
        split.Panel2.Controls.Add(_riskDetailsGrid);
        _riskGrid.SelectionChanged += RiskGrid_SelectionChanged;
        _riskGrid.DataBindingComplete += (s, e) => FormatRiskGrid();

        root.Controls.Add(actions, 0, 0);
        root.Controls.Add(summaryGroup, 0, 1);
        root.Controls.Add(split, 0, 2);
        page.Controls.Add(root);
        return page;
    }

    private TabPage BuildTaggedTab()
    {
        var page = new TabPage("Tagged Review");
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), AutoScroll = true, WrapContents = true, BackColor = Color.FromArgb(240, 240, 240) };
        _taggedRefresh.Click += (s, e) => { _taggedPageIndex = 0; LoadTaggedData(); };
        _taggedExportCsv.Click += (s, e) => ExportGridToCsv(_taggedGrid, "tagged_events.csv");
        _taggedRemoveTag.Click += (s, e) => RemoveSelectedTaggedLinks();
        _taggedTagFilter.SelectedIndexChanged += (s, e) => { if (_suppressTagFilterEvents) return; _taggedPageIndex = 0; LoadTaggedData(); };
        _taggedGlobalFilter.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { _taggedPageIndex = 0; LoadTaggedData(); e.SuppressKeyPress = true; } };
        _taggedFirstPage.Click += (s, e) => { _taggedPageIndex = 0; LoadTaggedData(); };
        _taggedPrevPage.Click += (s, e) => { if (_taggedPageIndex > 0) _taggedPageIndex--; LoadTaggedData(); };
        _taggedNextPage.Click += (s, e) => { _taggedPageIndex++; LoadTaggedData(); };
        _taggedPageSize.SelectedIndexChanged += (s, e) => { _taggedPageIndex = 0; LoadTaggedData(); };
        actions.Controls.AddRange(new Control[] {
            _taggedRefresh, _taggedExportCsv, _taggedRemoveTag,
            new Label { Text = "Tag:", AutoSize = true, Margin = new Padding(14, 7, 0, 0) }, _taggedTagFilter,
            new Label { Text = "Global contains:", AutoSize = true, Margin = new Padding(8, 7, 0, 0) }, _taggedGlobalFilter,
            new Label { Text = "Page size:", AutoSize = true, Margin = new Padding(14, 7, 0, 0) }, _taggedPageSize,
            _taggedFirstPage, _taggedPrevPage, _taggedNextPage, _taggedPageLabel
        });

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 620 };
        split.Panel1.Controls.Add(_taggedGrid);
        split.Panel2.Controls.Add(_taggedDetailsGrid);
        _taggedGrid.SelectionChanged += TaggedGrid_SelectionChanged;
        _taggedGrid.DataBindingComplete += (s, e) => ConfigureGridPresentation(_taggedGrid);

        root.Controls.Add(actions, 0, 0);
        root.Controls.Add(split, 0, 1);
        page.Controls.Add(root);
        return page;
    }

    private TabPage BuildDatabaseMaintenanceTab()
    {
        var page = new TabPage("Database Maintenance") { BackColor = Color.White };
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), AutoScroll = true, WrapContents = true, BackColor = Color.FromArgb(240, 240, 240) };
        _maintenanceRefresh.Click += (s, e) => RefreshDatabaseDiagnostics();
        _maintenanceOptimize.Click += async (s, e) => await OptimizeDatabaseAsync();
        _maintenanceVacuum.Click += async (s, e) => await VacuumDatabaseAsync();
        actions.Controls.AddRange(new Control[]
        {
            _maintenanceRefresh,
            _maintenanceOptimize,
            _maintenanceVacuum,
            new Label { Text = "Use Optimize regularly. VACUUM can take time on large cases.", AutoSize = true, Margin = new Padding(14, 8, 0, 0) }
        });
        _maintenanceGrid.DataBindingComplete += (s, e) => ConfigureGridPresentation(_maintenanceGrid);
        root.Controls.Add(actions, 0, 0);
        root.Controls.Add(_maintenanceGrid, 0, 1);
        page.Controls.Add(root);
        return page;
    }

    // --- LOGIC ---

    private string GetCurrentCaseFolder() => string.IsNullOrWhiteSpace(_currentCasePath) ? "" : Path.GetDirectoryName(Path.GetFullPath(_currentCasePath)) ?? "";
    private string GetCaseDbPath() => string.IsNullOrWhiteSpace(GetCurrentCaseFolder()) ? "" : Path.Combine(GetCurrentCaseFolder(), "case.db");

    private static bool IsEwfImage(string filePath)
    {
        var ext = Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant();
        return ext == "EX01" || (ext.Length >= 2 && (ext[0] == 'E' || ext[0] == 'S') && ext.Skip(1).All(char.IsDigit));
    }

    private void CreateNewCase()
    {
        using var sfd = new SaveFileDialog { Filter = "JSON|*.json", FileName = "case.json" };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;
        _currentCase = new CaseFile();
        _currentCasePath = sfd.FileName;
        _casePath.Text = _currentCasePath;
        SaveCase(false);
        DatabaseCore.InitializeDatabase(GetCaseDbPath());
        RefreshTagControls();
    }

    private async Task OpenCaseAsync()
    {
        using var ofd = new OpenFileDialog { Filter = "JSON|*.json" };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;
        await OpenCaseFileAsync(ofd.FileName, quiet: false);
    }

    private async Task<bool> OpenCaseFileAsync(string caseFilePath, bool quiet)
    {
        if (string.IsNullOrWhiteSpace(caseFilePath) || !File.Exists(caseFilePath))
            return false;

        try
        {
            _currentCasePath = caseFilePath;
            _currentCase = await Task.Run(() => CaseManager.Load(_currentCasePath));
            ApplyCurrentCaseToUi();
            RefreshCaseBinding();
            RefreshTagControls();
            DatabaseCore.InitializeDatabase(GetCaseDbPath());
            RememberLastCasePath(_currentCasePath);
            MarkViewsStale();
            _searchGrid.DataSource = null;
            _riskGrid.DataSource = null;
            _taggedGrid.DataSource = null;
            _coverageGrid.DataSource = null;
            _status.Text = "Case opened. Heavy views load on demand when selected or refreshed.";
            LogCase($"Case opened automatically: {_currentCasePath}");
            LoadSelectedTabOnDemand();
            return true;
        }
        catch (Exception ex)
        {
            if (!quiet)
                MessageBox.Show(this, ex.Message, "Open Case Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            LogCase($"Case auto-open failed: {ex.Message}");
            return false;
        }
    }

    private async Task TryAutoOpenLastCaseAsync()
    {
        var lastPath = GetLastCasePath();
        if (!string.IsNullOrWhiteSpace(lastPath) && File.Exists(lastPath))
        {
            await OpenCaseFileAsync(lastPath, quiet: true);
            return;
        }

        _status.Text = "No case open. Add Evidence will auto-create a case folder.";
    }

    private void ApplyCurrentCaseToUi()
    {
        _casePath.Text = _currentCasePath;
        _caseName.Text = _currentCase?.CaseName ?? string.Empty;
        _caseNumber.Text = _currentCase?.CaseNumber ?? string.Empty;
        _caseSubject.Text = _currentCase?.SubjectName ?? string.Empty;
        _caseCompany.Text = _currentCase?.Company ?? string.Empty;
        _caseInvestigator.Text = _currentCase?.Investigator ?? string.Empty;
    }

    private bool EnsureCaseReady(string reason)
    {
        if (_currentCase != null && !string.IsNullOrWhiteSpace(_currentCasePath))
            return true;

        try
        {
            var root = GetDefaultAutoCaseRoot();
            Directory.CreateDirectory(root);
            var caseName = $"AutoCase_{DateTime.Now:yyyyMMdd_HHmmss}";
            var caseFolder = Path.Combine(root, caseName);
            Directory.CreateDirectory(caseFolder);

            _currentCasePath = Path.Combine(caseFolder, "case.json");
            _currentCase = new CaseFile
            {
                CaseName = caseName,
                Description = $"Automatically created before {reason}."
            };
            _caseName.Text = caseName;
            _caseNumber.Text = string.Empty;
            _caseSubject.Text = string.Empty;
            _caseCompany.Text = string.Empty;
            _caseInvestigator.Text = string.Empty;
            _casePath.Text = _currentCasePath;

            SaveCase(prompt: false);
            DatabaseCore.InitializeDatabase(GetCaseDbPath());
            RememberLastCasePath(_currentCasePath);
            RefreshCaseBinding();
            RefreshTagControls();
            LogCase($"Auto-created case for {reason}: {_currentCasePath}");
            _status.Text = "Case auto-created.";
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Auto Case Creation Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private static string GetDefaultAutoCaseRoot()
    {
        if (Directory.Exists(@"Q:\"))
            return @"Q:\TriageCase";

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents))
            documents = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(documents, "VestigantTriageCases");
    }

    private static string GetSettingsDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
            appData = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(appData, "VestigantTriage");
    }

    private static string GetLastCasePathFile() => Path.Combine(GetSettingsDirectory(), "last_case_path.txt");

    private static string GetLastCasePath()
    {
        try
        {
            var path = GetLastCasePathFile();
            return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void RememberLastCasePath(string caseFilePath)
    {
        try
        {
            Directory.CreateDirectory(GetSettingsDirectory());
            File.WriteAllText(GetLastCasePathFile(), caseFilePath ?? string.Empty);
        }
        catch
        {
            // Last-case convenience must not interrupt forensic processing.
        }
    }

    private void SaveCase(bool prompt)
    {
        if (_currentCase == null)
            _currentCase = new CaseFile();

        if (string.IsNullOrWhiteSpace(_currentCasePath))
        {
            using var sfd = new SaveFileDialog { Filter = "JSON|*.json", FileName = "case.json" };
            if (sfd.ShowDialog(this) != DialogResult.OK)
                return;

            _currentCasePath = sfd.FileName;
            _casePath.Text = _currentCasePath;
        }

        _currentCase.CaseName = _caseName.Text;
        _currentCase.CaseNumber = _caseNumber.Text;
        _currentCase.SubjectName = _caseSubject.Text;
        _currentCase.Company = _caseCompany.Text;
        _currentCase.Investigator = _caseInvestigator.Text;
        _currentCase.DatabasePath = CaseManager.StorePath(GetCurrentCaseFolder(), GetCaseDbPath());
        CaseManager.Save(_currentCasePath, _currentCase);
        RememberLastCasePath(_currentCasePath);
        _status.Text = "Case Saved.";
    }

    private async Task AddEvidenceAsync()
    {
        if (!EnsureCaseReady("adding evidence")) return;
        var sourceTypeChoices = new[]
        {
            "Raw Disk Image (.dd, .img, .e01)",
            "O365 UAL CSV",
            "Google Workspace Audit / Investigation CSV or ZIP",
            "Google Takeout Archive / Export Files",
            "Gemini Session Archive",
            "Windows Event Log (EVTX)",
            "Registry Hive",
            "Browser History",
            "Print Spool / Print Evidence",
            "Shortcut / Jump List / Prefetch / Recycle Bin"
        };
        var selectedType = Prompt.ShowChoice(this, "Evidence Source", "Select Source Type:", sourceTypeChoices);
        if (string.IsNullOrWhiteSpace(selectedType)) return;

        using var ofd = new OpenFileDialog { Multiselect = true };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;

        var rawDiskMode = ImageArtifactScanMode.Triage;
        if (selectedType.Contains("Disk Image"))
        {
            var scanModeChoice = Prompt.ShowChoice(this, "Disk Image Scan Mode", "Select artifact discovery mode:", new[]
            {
                "Fast Triage - high-value artifact locations only (recommended first pass)",
                "Full Discovery - exhaustive recursive scan (slower; run after triage if needed)"
            });
            if (string.IsNullOrWhiteSpace(scanModeChoice)) return;
            rawDiskMode = scanModeChoice.StartsWith("Full", StringComparison.OrdinalIgnoreCase)
                ? ImageArtifactScanMode.Full
                : ImageArtifactScanMode.Triage;
        }

        var workingDir = Path.Combine(GetCurrentCaseFolder(), "WorkingEvidence");
        Directory.CreateDirectory(workingDir);
        _evidenceProgress.Visible = true;

        await Task.Run(() => {
            void AddExtractedSource(string extractedPath, string internalPath, string sourceType, DateTime? createdUtc = null, DateTime? accessedUtc = null, DateTime? modifiedUtc = null)
            {
                var source = CaseManager.CreateSourceRecord(GetCurrentCaseFolder(), extractedPath, sourceType);
                source.OriginalSourcePath = internalPath;
                source.OriginalCreatedUtc = createdUtc.HasValue ? createdUtc.Value.ToUniversalTime().ToString("O") : "";
                source.OriginalAccessedUtc = accessedUtc.HasValue ? accessedUtc.Value.ToUniversalTime().ToString("O") : "";
                source.OriginalModifiedUtc = modifiedUtc.HasValue ? modifiedUtc.Value.ToUniversalTime().ToString("O") : "";
                Invoke(new Action(() => { if (!_currentCase!.Sources.Any(s => s.HashValue == source.HashValue)) _currentCase.Sources.Add(source); }));
            }

            if (selectedType.Contains("Disk Image"))
            {
                var ewfImages = ofd.FileNames.Where(IsEwfImage).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
                var rawImages = ofd.FileNames.Where(x => !IsEwfImage(x)).ToArray();

                if (rawImages.Length > 0)
                {
                    var extracted = ImageTriageCore.ExtractTargetedArtifacts(rawImages, workingDir, m => Invoke(new Action(() => LogCase(m))), rawDiskMode);
                    var sourceType = rawDiskMode == ImageArtifactScanMode.Triage ? "Auto-Triage Raw Image (Fast Triage)" : "Auto-Triage Raw Image (Full Discovery)";
                    foreach (var item in extracted)
                        AddExtractedSource(item.ExtractedPath, item.InternalPath, sourceType, item.CreatedUtc, item.AccessedUtc, item.ModifiedUtc);
                }

                if (ewfImages.Length > 0)
                {
                    try
                    {
                        var extracted = TskTriageCore.ExtractFromEwf(ewfImages, workingDir, m => Invoke(new Action(() => LogCase(m))));
                        foreach (var item in extracted)
                            AddExtractedSource(item.ExtractedPath, item.InternalPath, "Auto-Triage EWF/TSK");
                    }
                    catch (Exception ex)
                    {
                        Invoke(new Action(() => LogCase($"E01/EWF extraction skipped or failed: {ex.Message}")));
                    }
                }
            }
            else
            {
                foreach (var file in ofd.FileNames)
                {
                    var dest = Path.Combine(workingDir, Path.GetFileName(file));
                    if (!File.Exists(dest))
                        File.Copy(file, dest, overwrite: false);

                    AddExtractedSource(dest, file, selectedType);
                }
            }
        });
        RefreshCaseBinding(); _evidenceProgress.Visible = false;
    }

    private async Task ProcessEvidenceAsync()
    {
        if (!EnsureCaseReady("processing evidence")) return;
        if (_currentCase == null || _currentCase.Sources.Count == 0)
        {
            MessageBox.Show(this, "No evidence sources are staged for ingest.", "Ingest Evidence", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_riskRunning)
        {
            MessageBox.Show(this, "Risk analysis is running. Wait for it to finish before starting ingest.", "Ingest Evidence", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_ingestRunning)
            return;

        try
        {
            _ingestRunning = true;
            _evidenceRunIngest.Enabled = false;
            _evidenceAddSources.Enabled = false;
            _dbRunRisk.Enabled = false;
            DatabaseCore.InitializeDatabase(GetCaseDbPath());
            _status.Text = "Ingesting data...";
            _evidenceProgress.Visible = true;
            SqliteConnection.ClearAllPools();
            await Task.Run(() => {
                var dbPath = GetCaseDbPath();
                var folder = GetCurrentCaseFolder();
                foreach (var s in _currentCase!.Sources) if (!Path.IsPathRooted(s.LocalPath)) s.LocalPath = Path.Combine(folder, s.LocalPath);
                IngestEngine.ProcessEvidence(dbPath, _currentCase.Sources, TimeZoneInfo.Local.Id, LogCase);
                foreach (var s in _currentCase!.Sources) s.LocalPath = CaseManager.StorePath(folder, s.LocalPath);
            });
            SqliteConnection.ClearAllPools();
            SaveCase(prompt: false);
            RefreshCaseBinding();
            MarkViewsStale();
            ExportAutomaticValidationBundle();
            _status.Text = "Ingest complete. Validation bundle generated; heavy views marked stale and load on demand.";
        }
        catch (Exception ex)
        {
            LogCase($"Ingest failed: {ex.Message}");
            MessageBox.Show(this, ex.ToString(), "Ingest Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _status.Text = "Ingest failed.";
        }
        finally
        {
            _ingestRunning = false;
            _evidenceProgress.Visible = false;
            _evidenceRunIngest.Enabled = true;
            _evidenceAddSources.Enabled = true;
            _dbRunRisk.Enabled = true;
        }
    }

    private void ExportAutomaticValidationBundle()
    {
        if (_currentCase == null || string.IsNullOrWhiteSpace(_currentCasePath))
            return;

        try
        {
            var uploadDir = Path.Combine(GetCurrentCaseFolder(), "Upload");
            Directory.CreateDirectory(uploadDir);
            var safeName = SanitizeFileNameForExport(string.IsNullOrWhiteSpace(_currentCase.CaseName) ? "VestigantCase" : _currentCase.CaseName);
            var validationZip = Path.Combine(uploadDir, safeName + "_validation_bundle.zip");
            var result = ValidationBundleService.ExportValidationBundle(validationZip, _currentCase, GetCurrentCaseFolder(), GetCaseDbPath(), LogCase);
            LogCase($"Automatic validation bundle exported: {result.ZipPath} ({result.ZipBytes:N0} bytes).");
        }
        catch (Exception ex)
        {
            LogCase($"Automatic validation bundle export failed: {ex.Message}");
        }
    }

private void RunMasterSearch()
{
    _ = RunMasterSearchAsync();
}

private async Task RunMasterSearchAsync()
{
    var dbPath = GetCaseDbPath();
    if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
        return;

    try
    {
        var loadVersion = ++_timelineLoadVersion;
        var pageSize = SelectedPageSize(_timelinePageSize);
        var offset = Math.Max(0, _timelinePageIndex) * pageSize;
        var where = BuildTimelineWhereSql();
        var order = BuildTimelineSortSql();
        var sql = $@"
            SELECT *
            FROM v_master_timeline
            {where}
            ORDER BY {order}
            LIMIT {pageSize} OFFSET {offset};";

        _status.Text = $"Loading timeline page {_timelinePageIndex + 1:N0} in background...";
        var table = await Task.Run(() => DatabaseCore.QueryToDataTable(dbPath, sql));
        if (loadVersion != _timelineLoadVersion || IsDisposed)
            return;

        _timelineBaseTable = table;
        PopulateTimelineColumnControls(_timelineBaseTable);
        _searchGrid.DataSource = _timelineBaseTable;
        _timelineHeaderFilter?.CaptureCurrentView("Timeline");
        _timelinePageLabel.Text = $"Page {_timelinePageIndex + 1} | Rows {_timelineBaseTable.Rows.Count:N0}";
        _timelinePrevPage.Enabled = _timelinePageIndex > 0;
        _timelineNextPage.Enabled = _timelineBaseTable.Rows.Count >= pageSize;
        _status.Text = $"Timeline page {_timelinePageIndex + 1}: {_timelineBaseTable.Rows.Count:N0} rows loaded. Header filters apply to the loaded page.";
    }
    catch (Exception ex)
    {
        MessageBox.Show(this, ex.Message, "Timeline Load Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        _status.Text = "Timeline load failed.";
    }
}

private string BuildTimelineWhereSql()
{
    var filters = new List<string>();
    var from = _dateFrom.Value.Date.ToString("yyyy-MM-dd 00:00:00");
    var to = _dateTo.Value.Date.ToString("yyyy-MM-dd 23:59:59");
    var mode = _timelineTimestampMode.SelectedItem?.ToString() ?? "Behavioral dates only";

    if (mode == "Behavioral dates only")
    {
        filters.Add("Behavioral_Timestamp = 'Yes'");
        filters.Add($"Date_Time >= '{EscapeSqlLiteral(from)}' AND Date_Time <= '{EscapeSqlLiteral(to)}'");
    }
    else if (mode == "All dated events")
    {
        filters.Add($"Date_Time >= '{EscapeSqlLiteral(from)}' AND Date_Time <= '{EscapeSqlLiteral(to)}'");
    }
    else if (mode == "Metadata/fallback only")
    {
        filters.Add("Behavioral_Timestamp = 'No'");
    }
    else if (mode == "Undated/uncertain")
    {
        filters.Add("(IFNULL(Date_Time,'') = '' OR Time_Confidence IN ('Unknown','MetadataOnly'))");
    }

    var global = _globalFilter.Text.Trim();
    if (!string.IsNullOrWhiteSpace(global))
    {
        var searchable = new[]
        {
            "Date_Time", "Local_Date_Time", "Source", "Operation", "User_Account", "Target_Object",
            "Client_IP", "File_Name", "Process_App", "Action_Result", "To_Recipient", "From_Recipient",
            "Subject", "Drive_Type", "Forensic_Status", "Artifact_Type", "Parser_Confidence", "Source_File",
            "Url_Category", "Url_Host"
        };
        filters.Add("(" + string.Join(" OR ", searchable.Select(c => $"IFNULL({QuoteIdentifier(c)},'') LIKE '%{EscapeSqlLikeLiteral(global)}%'").ToArray()) + ")");
    }

    foreach (var f in _timelineFilters)
    {
        if (!string.IsNullOrWhiteSpace(f.ColumnName) && !string.IsNullOrWhiteSpace(f.ContainsText))
            filters.Add($"IFNULL({QuoteIdentifier(f.ColumnName)},'') LIKE '%{EscapeSqlLikeLiteral(f.ContainsText)}%'");
    }

    return filters.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", filters);
}

private string BuildTimelineSortSql()
{
    var parts = new List<string>();
    AddSortPartSql(parts, _timelineSort1, _timelineSortDir1);
    AddSortPartSql(parts, _timelineSort2, _timelineSortDir2);
    AddSortPartSql(parts, _timelineSort3, _timelineSortDir3);
    return parts.Count == 0 ? "Date_Time DESC" : string.Join(", ", parts);
}

private static void AddSortPartSql(List<string> parts, ComboBox columnCombo, ComboBox dirCombo)
{
    var column = columnCombo.SelectedItem?.ToString() ?? string.Empty;
    if (string.IsNullOrWhiteSpace(column) || column == "(none)")
        return;
    if (parts.Any(p => p.StartsWith(QuoteIdentifier(column), StringComparison.OrdinalIgnoreCase)))
        return;
    var direction = string.Equals(dirCombo.SelectedItem?.ToString(), "ASC", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";
    parts.Add($"{QuoteIdentifier(column)} {direction}");
}

private void AddTimelineFieldFilter()
{
    if (_timelineFieldFilter.SelectedItem == null)
        return;

    var column = _timelineFieldFilter.SelectedItem.ToString() ?? "";
    var value = _timelineFieldValue.Text.Trim();
    if (string.IsNullOrWhiteSpace(column) || string.IsNullOrWhiteSpace(value))
        return;

    _timelineFilters.Add(new GridFilterCondition(column, value));
    _timelineActiveFilters.Items.Add(_timelineFilters[^1]);
    _timelineFieldValue.Clear();
    _timelinePageIndex = 0;
    RunMasterSearch();
}

private void ClearTimelineFilters()
{
    _timelineFilters.Clear();
    _timelineActiveFilters.Items.Clear();
    _globalFilter.Clear();
    _timelinePageIndex = 0;
    RunMasterSearch();
}

private void ApplyTimelineView()
{
    _timelinePageIndex = 0;
    RunMasterSearch();
}

private void AddTimelineTimestampFilters(List<string> filters)
{
    // Retained for compatibility with earlier builds. Phase 8 applies timestamp filters in SQL.
}

private string BuildTimelineSortExpression(DataTable table)
{
    var parts = new List<string>();
    AddSortPart(parts, table, _timelineSort1, _timelineSortDir1);
    AddSortPart(parts, table, _timelineSort2, _timelineSortDir2);
    AddSortPart(parts, table, _timelineSort3, _timelineSortDir3);
    if (parts.Count == 0 && table.Columns.Contains("Date_Time")) parts.Add("[Date_Time] DESC");
    return string.Join(", ", parts);
}

private static void AddSortPart(List<string> parts, DataTable table, ComboBox columnCombo, ComboBox dirCombo)
{
    var column = columnCombo.SelectedItem?.ToString() ?? "";
    if (string.IsNullOrWhiteSpace(column) || column == "(none)" || !table.Columns.Contains(column)) return;
    if (parts.Any(p => p.StartsWith($"[{column.Replace("]", "]]" )}]", StringComparison.OrdinalIgnoreCase))) return;
    var direction = string.Equals(dirCombo.SelectedItem?.ToString(), "ASC", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";
    parts.Add($"[{column.Replace("]", "]]" )}] {direction}");
}

private void PopulateTimelineColumnControls(DataTable table)
{
    var columns = table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray();
    PreserveComboItems(_timelineFieldFilter, columns, "Target_Object");
    PreserveComboItems(_timelineSort1, columns.Prepend("(none)").ToArray(), table.Columns.Contains("Date_Time") ? "Date_Time" : "(none)");
    PreserveComboItems(_timelineSort2, columns.Prepend("(none)").ToArray(), table.Columns.Contains("User_Account") ? "User_Account" : "(none)");
    PreserveComboItems(_timelineSort3, columns.Prepend("(none)").ToArray(), table.Columns.Contains("Operation") ? "Operation" : "(none)");
}

private static void PreserveComboItems(ComboBox combo, IEnumerable<string> values, string preferred)
{
    var previous = combo.SelectedItem?.ToString();
    combo.BeginUpdate();
    combo.Items.Clear();
    var mergedValues = values.ToList();
    if (!string.IsNullOrWhiteSpace(previous)) mergedValues.Add(previous);
    if (!string.IsNullOrWhiteSpace(preferred)) mergedValues.Add(preferred);
    foreach (var v in mergedValues.Distinct(StringComparer.OrdinalIgnoreCase)) combo.Items.Add(v);
    var selected = !string.IsNullOrWhiteSpace(previous) && combo.Items.Contains(previous) ? previous : preferred;
    if (!string.IsNullOrWhiteSpace(selected) && combo.Items.Contains(selected)) combo.SelectedItem = selected;
    else if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    combo.EndUpdate();
}

private static void ConfigureDirectionCombo(ComboBox combo, string selected)
{
    combo.Items.Clear();
    combo.Items.Add("ASC");
    combo.Items.Add("DESC");
    combo.SelectedItem = string.Equals(selected, "ASC", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";
}

private static string ColumnExpr(string columnName) => $"Convert([{columnName.Replace("]", "]]" )}], 'System.String')";

private static string EscapeLikeValue(string value)
{
    return (value ?? "").Replace("'", "''").Replace("[", "[[]").Replace("%", "[%]").Replace("*", "[*]");
}

private static string EscapeSqlLiteral(string value) => (value ?? string.Empty).Replace("'", "''");
private static string EscapeSqlLikeLiteral(string value) => EscapeSqlLiteral(value).Replace("%", "[%]").Replace("_", "[_]");
private static string QuoteIdentifier(string value) => "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";

private void SearchGrid_SelectionChanged(object? sender, EventArgs e)
    {
        if (_searchGrid.SelectedRows.Count == 0) return;
        if (!_searchGrid.Columns.Contains("ID"))
            return;

        var rawId = _searchGrid.SelectedRows[0].Cells["ID"].Value?.ToString();
        if (!long.TryParse(rawId, out var eventId))
            return;

        var data = DatabaseCore.GetReconstructedEventRow(GetCaseDbPath(), eventId);
        var dt = new DataTable(); dt.Columns.Add("Property"); dt.Columns.Add("Value");
        foreach (var kvp in data.OrderBy(x => x.Key)) dt.Rows.Add(kvp.Key, kvp.Value);
        _searchDetailsGrid.DataSource = dt;
    }

    private async Task RunRiskEngineAsync()
    {
        var dbPath = GetCaseDbPath();
        if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
        {
            MessageBox.Show("No case database exists yet. Ingest evidence first.");
            return;
        }
        if (_ingestRunning)
        {
            MessageBox.Show(this, "Ingest is still running. Risk analysis is disabled until ingest and SQLite writes complete.", "Risk Engine", MessageBoxButtons.OK, MessageBoxIcon.Information);
            LogCase("Risk engine request ignored because ingest is still running.");
            return;
        }
        if (_riskRunning)
            return;

        try
        {
            _riskRunning = true;
            _dbRunRisk.Enabled = false;
            _evidenceRunIngest.Enabled = false;
            _dbSummary.Text = "Risk analysis running...";
            _riskGrid.DataSource = null;
            _status.Text = "Analyzing Risk...";
            SqliteConnection.ClearAllPools();
            DatabaseCore.InitializeDatabase(dbPath);

            var result = await Task.Run(() => RiskEngine.Run(dbPath, null, LogCase));

            SqliteConnection.ClearAllPools();
            LoadRiskData();
            _dbSummary.Text = $"Total Risk Hits: {result.TotalHits:N0} | Critical Events: {result.CriticalEvents:N0} | High Events: {result.HighEvents:N0} | Medium Events: {result.MediumEvents:N0}";
        }
        catch (Exception ex)
        {
            _dbSummary.Text = "Risk analysis failed.";
            MessageBox.Show(this, ex.ToString(), "Risk Engine Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            LogCase($"Risk engine failed: {ex.Message}");
        }
        finally
        {
            _riskRunning = false;
            _dbRunRisk.Enabled = true;
            _evidenceRunIngest.Enabled = true;
            _status.Text = "Ready";
        }
    }

    private void LoadRiskData()
    {
        _ = LoadRiskDataAsync();
    }

    private async Task LoadRiskDataAsync()
    {
        var dbPath = GetCaseDbPath();
        if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
            return;

        try
        {
            var loadVersion = ++_riskLoadVersion;
            var pageSize = SelectedPageSize(_riskPageSize);
            var offset = Math.Max(0, _riskPageIndex) * pageSize;
            var where = BuildRiskWhereSql();
            var sql = $@"
                WITH risk_view AS (
                    SELECT
                        rh.id AS Hit_ID,
                        rh.event_id AS Event_ID,
                        e.creation_date_utc AS Timestamp,
                        e.user_id AS User,
                        e.operation AS Operation,
                        e.object_id AS Target,
                        rh.rule_code AS Rule_Code,
                        rh.rule_name AS Rule_Name,
                        rh.risk_domain AS Domain,
                        rh.risk_score AS Score,
                        rh.risk_level AS Level,
                        IFNULL((SELECT group_concat(t.tag_name, '; ') FROM event_tags et JOIN tags t ON t.tag_id = et.tag_id WHERE et.event_id = e.event_id), '') AS Tags,
                        rh.reason AS Reason,
                        CASE
                            WHEN rh.supporting_value LIKE '%WorkingEvidence%' THEN COALESCE(
                                NULLIF((SELECT ef.field_value FROM event_fields ef WHERE ef.event_id = e.event_id AND ef.field_name IN ('DisplayTarget','TargetPath','OriginalSourcePath','Original Path') AND IFNULL(ef.field_value,'') NOT LIKE '%WorkingEvidence%' LIMIT 1), ''),
                                NULLIF(CASE WHEN e.object_id LIKE '%WorkingEvidence%' THEN '' ELSE e.object_id END, ''),
                                NULLIF(e.source_relative_url, ''),
                                NULLIF(e.file_name, ''),
                                e.object_id
                            )
                            ELSE rh.supporting_value
                        END AS Supporting_Value,
                        e.forensic_status AS Forensic_Status,
                        IFNULL((SELECT ef.field_value FROM event_fields ef WHERE ef.event_id = e.event_id AND ef.field_name = 'ArtifactType' LIMIT 1), '') AS Artifact_Type,
                        IFNULL((SELECT ef.field_value FROM event_fields ef WHERE ef.event_id = e.event_id AND ef.field_name = 'ParserConfidence' LIMIT 1), '') AS Parser_Confidence,
                        e.source_file AS Source_File
                    FROM risk_hits rh
                    LEFT JOIN events e ON e.event_id = rh.event_id
                )
                SELECT * FROM risk_view
                {where}
                ORDER BY Score DESC, Timestamp DESC
                LIMIT {pageSize} OFFSET {offset};";

            _status.Text = $"Loading risk page {_riskPageIndex + 1:N0} in background...";
            var table = await Task.Run(() => DatabaseCore.QueryToDataTable(dbPath, sql));
            if (loadVersion != _riskLoadVersion || IsDisposed)
                return;

            _riskBaseTable = table;
            PopulateRiskFilterControls(_riskBaseTable);
            _riskGrid.DataSource = _riskBaseTable;
            _riskHeaderFilter?.CaptureCurrentView("Risk");
            UpdateRiskSummary(_riskBaseTable);
            _riskPageLabel.Text = $"Page {_riskPageIndex + 1} | Rows {_riskBaseTable.Rows.Count:N0}";
            _riskPrevPage.Enabled = _riskPageIndex > 0;
            _riskNextPage.Enabled = _riskBaseTable.Rows.Count >= pageSize;
            _dbSummary.Text = _riskBaseTable.Rows.Count == 0
                ? "No risk hits are visible for the current page/filter."
                : $"Displayed Risk Hits: {_riskBaseTable.Rows.Count:N0} on page {_riskPageIndex + 1}.";
            _status.Text = $"Risk page {_riskPageIndex + 1}: {_riskBaseTable.Rows.Count:N0} rows loaded.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Risk Load Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _status.Text = "Risk load failed.";
        }
    }

    private string BuildRiskWhereSql()
    {
        var filters = new List<string>();
        var level = _riskLevelFilter.SelectedItem?.ToString() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(level) && level != "(all)") filters.Add($"Level = '{EscapeSqlLiteral(level)}'");
        var domain = _riskDomainFilter.SelectedItem?.ToString() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(domain) && domain != "(all)") filters.Add($"Domain = '{EscapeSqlLiteral(domain)}'");
        if (_riskMinScore.Value > 0) filters.Add($"Score >= {(int)_riskMinScore.Value}");
        if (!string.IsNullOrWhiteSpace(_riskUserFilter.Text)) filters.Add($"IFNULL(User,'') LIKE '%{EscapeSqlLikeLiteral(_riskUserFilter.Text.Trim())}%'");
        if (!string.IsNullOrWhiteSpace(_riskTargetFilter.Text))
        {
            var v = EscapeSqlLikeLiteral(_riskTargetFilter.Text.Trim());
            filters.Add($"(IFNULL(Target,'') LIKE '%{v}%' OR IFNULL(Supporting_Value,'') LIKE '%{v}%' OR IFNULL(Source_File,'') LIKE '%{v}%')");
        }
        if (!string.IsNullOrWhiteSpace(_riskRuleFilter.Text))
        {
            var v = EscapeSqlLikeLiteral(_riskRuleFilter.Text.Trim());
            filters.Add($"(IFNULL(Rule,'') LIKE '%{v}%' OR IFNULL(Rule_Name,'') LIKE '%{v}%' OR IFNULL(Reason,'') LIKE '%{v}%' OR IFNULL(Domain,'') LIKE '%{v}%')");
        }
        return filters.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", filters);
    }

    private void PopulateRiskFilterControls(DataTable table)
    {
        _suppressRiskFilterEvents = true;
        try
        {
            PreserveComboItems(_riskLevelFilter, new[] { "(all)" }.Concat(GetDistinctColumnValues(table, "Level")), "(all)");
            PreserveComboItems(_riskDomainFilter, new[] { "(all)" }.Concat(GetDistinctColumnValues(table, "Domain")), "(all)");
        }
        finally
        {
            _suppressRiskFilterEvents = false;
        }
    }

    private static IEnumerable<string> GetDistinctColumnValues(DataTable table, string columnName)
    {
        if (!table.Columns.Contains(columnName)) yield break;
        var values = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DataRow row in table.Rows)
        {
            var value = row[columnName]?.ToString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(value)) values.Add(value);
        }
        foreach (var value in values) yield return value;
    }

    private void ApplyRiskView()
    {
        _riskPageIndex = 0;
        LoadRiskData();
    }

    private void ClearRiskFilters()
    {
        if (_riskLevelFilter.Items.Count > 0) _riskLevelFilter.SelectedIndex = 0;
        if (_riskDomainFilter.Items.Count > 0) _riskDomainFilter.SelectedIndex = 0;
        _riskUserFilter.Clear();
        _riskTargetFilter.Clear();
        _riskRuleFilter.Clear();
        _riskMinScore.Value = 0;
        _riskPageIndex = 0;
        LoadRiskData();
    }

    private void UpdateRiskSummary(DataTable table)
    {
        if (table.Rows.Count == 0)
        {
            _riskSummaryBox.Text = "No risk hits are visible with the current filters/page.";
            return;
        }
        string CountBy(string columnName, int take = 0)
        {
            if (!table.Columns.Contains(columnName)) return string.Empty;
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (DataRow row in table.Rows)
            {
                var key = row[columnName]?.ToString() ?? "(blank)";
                if (string.IsNullOrWhiteSpace(key)) key = "(blank)";
                counts[key] = counts.TryGetValue(key, out var count) ? count + 1 : 1;
            }
            IEnumerable<KeyValuePair<string, int>> ordered = counts.OrderByDescending(kvp => kvp.Value).ThenBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase);
            if (take > 0) ordered = ordered.Take(take);
            return string.Join(Environment.NewLine, ordered.Select(kvp => $"  {kvp.Key}: {kvp.Value:N0}"));
        }
        _riskSummaryBox.Text =
            $"Visible rows on page: {table.Rows.Count:N0}{Environment.NewLine}" +
            $"{Environment.NewLine}By level:{Environment.NewLine}{CountBy("Level")}{Environment.NewLine}" +
            $"{Environment.NewLine}By domain:{Environment.NewLine}{CountBy("Domain")}{Environment.NewLine}" +
            $"{Environment.NewLine}Top users:{Environment.NewLine}{CountBy("User", 8)}";
    }

    private void RiskGrid_SelectionChanged(object? sender, EventArgs e)
    {
        if (_riskGrid.SelectedRows.Count == 0)
            return;

        var dt = new DataTable();
        dt.Columns.Add("Property");
        dt.Columns.Add("Value");

        var row = _riskGrid.SelectedRows[0];
        foreach (DataGridViewCell cell in row.Cells)
        {
            var columnName = _riskGrid.Columns[cell.ColumnIndex].HeaderText;
            dt.Rows.Add(columnName, cell.Value?.ToString() ?? "");
        }

        if (_riskGrid.Columns.Contains("Event_ID"))
        {
            var rawId = row.Cells["Event_ID"].Value?.ToString();
            if (long.TryParse(rawId, out var eventId))
            {
                var eventData = DatabaseCore.GetReconstructedEventRow(GetCaseDbPath(), eventId);
                foreach (var kvp in eventData.OrderBy(x => x.Key))
                    dt.Rows.Add("[Event] " + kvp.Key, kvp.Value);
            }
        }

        _riskDetailsGrid.DataSource = dt;
    }

    private void FormatRiskGrid()
    {
        ConfigureGridPresentation(_riskGrid);

        if (_riskGrid.Columns.Contains("Level"))
        {
            foreach (DataGridViewRow row in _riskGrid.Rows)
            {
                var level = row.Cells["Level"]?.Value?.ToString() ?? "";
                if (level.Equals("Critical", StringComparison.OrdinalIgnoreCase))
            {
                row.DefaultCellStyle.BackColor = Color.MistyRose;
                row.DefaultCellStyle.SelectionBackColor = Color.Firebrick;
            }
            else if (level.Equals("High", StringComparison.OrdinalIgnoreCase))
            {
                row.DefaultCellStyle.BackColor = Color.LemonChiffon;
                row.DefaultCellStyle.SelectionBackColor = Color.DarkGoldenrod;
            }
                else if (level.Equals("Medium", StringComparison.OrdinalIgnoreCase))
                {
                    row.DefaultCellStyle.BackColor = Color.AliceBlue;
                }
            }
        }

        SetColumnWidth(_riskGrid, "Reason", 360);
        SetColumnWidth(_riskGrid, "Target", 320);
        SetColumnWidth(_riskGrid, "Supporting_Value", 320);
        SetColumnWidth(_riskGrid, "Rule_Name", 220);
        SetColumnWidth(_riskGrid, "Source_File", 220);
    }

    private static void ConfigureGridPresentation(DataGridView grid)
    {
        EnableDoubleBuffered(grid);
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.ReadOnly = true;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        grid.RowHeadersVisible = false;
        grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
        grid.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
        grid.ColumnHeadersDefaultCellStyle.Font = new Font(grid.Font, FontStyle.Bold);
        foreach (DataGridViewColumn col in grid.Columns)
        {
            if (col.Width < 80) col.Width = 100;
            if (col.Width > 420) col.Width = 420;
        }
    }

    private static void EnableDoubleBuffered(DataGridView grid)
    {
        try
        {
            var prop = typeof(DataGridView).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            prop?.SetValue(grid, true, null);
        }
        catch
        {
            // Cosmetic performance optimization only.
        }
    }

    private static void SetColumnWidth(DataGridView grid, string columnName, int width)
    {
        if (grid.Columns.Contains(columnName))
            grid.Columns[columnName].Width = width;
    }

    private async Task ExportAllMasterMetadataCsvAsync()
    {
        var dbPath = GetCaseDbPath();
        if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
        {
            MessageBox.Show(this, "Create or open a case with an initialized database first.", "Export Master Metadata CSV", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var safeCaseName = SanitizeFileNameForExport(string.IsNullOrWhiteSpace(_currentCase?.CaseName) ? "VestigantCase" : _currentCase.CaseName);
        using var sfd = new SaveFileDialog
        {
            Filter = "CSV|*.csv",
            FileName = safeCaseName + "_master_all_records_all_metadata.csv"
        };
        if (sfd.ShowDialog(this) != DialogResult.OK)
            return;

        _timelineExportAllMetadataCsv.Enabled = false;
        _status.Text = "Exporting all master records and metadata to CSV...";
        try
        {
            var outputPath = sfd.FileName;
            var exportedRows = await Task.Run(() => DatabaseCore.ExportAllMasterMetadataCsv(dbPath, outputPath, SetStatusThreadSafe));
            _status.Text = $"Exported {exportedRows:N0} master records with all metadata.";
            MessageBox.Show(this, $"Exported {exportedRows:N0} master records with all metadata.\r\n\r\n{outputPath}", "Export Master Metadata CSV", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _status.Text = "Master metadata CSV export failed.";
            MessageBox.Show(this, ex.Message, "Export Master Metadata CSV", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _timelineExportAllMetadataCsv.Enabled = true;
        }
    }

    private void SetStatusThreadSafe(string message)
    {
        if (IsDisposed)
            return;

        try
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(SetStatusThreadSafe), message);
                return;
            }
            _status.Text = message;
        }
        catch
        {
            // Status updates are informational only and must not interrupt exports.
        }
    }


    private void ExportGridToCsv(DataGridView grid, string defaultName)
    {
        if (grid.DataSource is not DataTable table || table.Rows.Count == 0)
        {
            MessageBox.Show(this, "No rows are available to export.", "Export CSV", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var sfd = new SaveFileDialog { Filter = "CSV|*.csv", FileName = defaultName };
        if (sfd.ShowDialog(this) != DialogResult.OK)
            return;

        using var writer = new StreamWriter(sfd.FileName, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine(string.Join(",", table.Columns.Cast<DataColumn>().Select(c => CsvEscape(c.ColumnName))));
        foreach (DataRow row in table.Rows)
            writer.WriteLine(string.Join(",", table.Columns.Cast<DataColumn>().Select(c => CsvEscape(row[c]?.ToString() ?? ""))));

        MessageBox.Show(this, $"Exported {table.Rows.Count:N0} rows.", "Export CSV", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static string CsvEscape(string value)
    {
        value ??= "";
        if (value.Contains('"') || value.Contains(',') || value.Contains('\r') || value.Contains('\n'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    private void AddOrUpdateTagFromCaseTab()
    {
        var dbPath = GetCaseDbPath();
        if (string.IsNullOrWhiteSpace(dbPath))
        {
            MessageBox.Show(this, "Create or open a case first.", "Tags", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var name = _tagNameInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "Enter a tag name.", "Tags", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        DatabaseCore.CreateOrUpdateTag(dbPath, name, _tagDescriptionInput.Text.Trim());
        _tagNameInput.Clear();
        _tagDescriptionInput.Clear();
        RefreshTagControls();
    }

    private void DeleteSelectedTagFromCaseTab()
    {
        var dbPath = GetCaseDbPath();
        if (string.IsNullOrWhiteSpace(dbPath) || _tagGrid.SelectedRows.Count == 0)
            return;

        if (!TryGetLongCell(_tagGrid.SelectedRows[0], "Tag_ID", out var tagId))
            return;

        var tagName = _tagGrid.Columns.Contains("Tag") ? (_tagGrid.SelectedRows[0].Cells["Tag"]?.Value?.ToString() ?? "selected tag") : "selected tag";
        if (MessageBox.Show(this, $"Delete tag '{tagName}' and remove it from all events?", "Delete Tag", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        DatabaseCore.DeleteTag(dbPath, tagId);
        RefreshTagControls();
        RunMasterSearch();
        LoadRiskData();
        LoadTaggedData();
    }

    private void LoadSelectedTagIntoEditor()
    {
        if (_tagGrid.SelectedRows.Count == 0)
            return;

        var row = _tagGrid.SelectedRows[0];
        if (!_tagGrid.Columns.Contains("Tag") || !_tagGrid.Columns.Contains("Description"))
            return;
        _tagNameInput.Text = row.Cells["Tag"]?.Value?.ToString() ?? "";
        _tagDescriptionInput.Text = row.Cells["Description"]?.Value?.ToString() ?? "";
    }

    private void RefreshTagControls()
    {
        var dbPath = GetCaseDbPath();
        if (string.IsNullOrWhiteSpace(dbPath))
            return;

        DatabaseCore.InitializeDatabase(dbPath);
        var tags = DatabaseCore.GetTags(dbPath);
        _tagGrid.DataSource = tags;
        ConfigureGridPresentation(_tagGrid);
        SetColumnWidth(_tagGrid, "Tag", 180);
        SetColumnWidth(_tagGrid, "Description", 420);

        var items = tags.Rows.Cast<DataRow>()
            .Select(r => new TagComboItem(Convert.ToInt64(r["Tag_ID"]), r["Tag"]?.ToString() ?? ""))
            .Where(t => !string.IsNullOrWhiteSpace(t.Name))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _suppressTagFilterEvents = true;
        try
        {
            PopulateTagCombo(_timelineTagCombo, items, includeAll: false);
            PopulateTagCombo(_riskTagCombo, items, includeAll: false);
            PopulateTagCombo(_taggedTagFilter, items, includeAll: true);
        }
        finally
        {
            _suppressTagFilterEvents = false;
        }
    }

    private static void PopulateTagCombo(ComboBox combo, List<TagComboItem> items, bool includeAll)
    {
        var previousId = (combo.SelectedItem as TagComboItem)?.Id;
        combo.BeginUpdate();
        combo.Items.Clear();
        if (includeAll)
            combo.Items.Add(new TagComboItem(0, "(all)"));
        foreach (var item in items)
            combo.Items.Add(item);

        var selected = combo.Items.Cast<object>().OfType<TagComboItem>().FirstOrDefault(i => previousId.HasValue && i.Id == previousId.Value);
        if (selected != null)
            combo.SelectedItem = selected;
        else if (combo.Items.Count > 0)
            combo.SelectedIndex = 0;
        combo.EndUpdate();
    }

    private void TagSelectedRows(DataGridView grid, string eventIdColumn, ComboBox tagCombo, string context)
    {
        var dbPath = GetCaseDbPath();
        if (string.IsNullOrWhiteSpace(dbPath) || tagCombo.SelectedItem is not TagComboItem tag || tag.Id <= 0)
        {
            MessageBox.Show(this, "Select a valid tag first.", "Tag Selected Rows", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var ids = GetSelectedEventIds(grid, eventIdColumn).ToArray();
        if (ids.Length == 0)
        {
            MessageBox.Show(this, "Select one or more rows first.", "Tag Selected Rows", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        DatabaseCore.AddTagToEvents(dbPath, ids, tag.Id, context);
        RefreshAfterTagChange();
        _status.Text = $"Applied tag '{tag.Name}' to {ids.Length:N0} event(s).";
    }

    private void UntagSelectedRows(DataGridView grid, string eventIdColumn, ComboBox tagCombo)
    {
        var dbPath = GetCaseDbPath();
        if (string.IsNullOrWhiteSpace(dbPath) || tagCombo.SelectedItem is not TagComboItem tag || tag.Id <= 0)
        {
            MessageBox.Show(this, "Select a valid tag first.", "Remove Tag", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var ids = GetSelectedEventIds(grid, eventIdColumn).ToArray();
        if (ids.Length == 0)
        {
            MessageBox.Show(this, "Select one or more rows first.", "Remove Tag", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        DatabaseCore.RemoveTagFromEvents(dbPath, ids, tag.Id);
        RefreshAfterTagChange();
        _status.Text = $"Removed tag '{tag.Name}' from {ids.Length:N0} event(s).";
    }

    private IEnumerable<long> GetSelectedEventIds(DataGridView grid, string eventIdColumn)
    {
        var seen = new HashSet<long>();
        foreach (DataGridViewRow row in grid.SelectedRows)
        {
            if (row.IsNewRow)
                continue;

            if (TryGetLongCell(row, eventIdColumn, out var eventId) && seen.Add(eventId))
                yield return eventId;
        }
    }

    private static bool TryGetLongCell(DataGridViewRow row, string columnName, out long value)
    {
        value = 0;
        if (row.DataGridView == null || !row.DataGridView.Columns.Contains(columnName))
            return false;

        return long.TryParse(row.Cells[columnName]?.Value?.ToString(), out value);
    }

    private void RefreshAfterTagChange()
    {
        RefreshTagControls();
        RunMasterSearch();
        LoadRiskData();
        LoadTaggedData();
    }

    private void LoadTaggedData()
    {
        _ = LoadTaggedDataAsync();
    }

    private async Task LoadTaggedDataAsync()
    {
        var dbPath = GetCaseDbPath();
        if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
            return;

        try
        {
            var loadVersion = ++_taggedLoadVersion;
            var pageSize = SelectedPageSize(_taggedPageSize);
            var offset = Math.Max(0, _taggedPageIndex) * pageSize;
            var filters = new List<string>();
            if (_taggedTagFilter.SelectedItem is TagComboItem tag && tag.Id > 0)
                filters.Add($"t.tag_id = {tag.Id}");
            var global = _taggedGlobalFilter.Text.Trim();
            if (!string.IsNullOrWhiteSpace(global))
            {
                var v = EscapeSqlLikeLiteral(global);
                filters.Add($"(IFNULL(t.tag_name,'') LIKE '%{v}%' OR IFNULL(mt.User_Account,'') LIKE '%{v}%' OR IFNULL(mt.Target_Object,'') LIKE '%{v}%' OR IFNULL(mt.Operation,'') LIKE '%{v}%' OR IFNULL(mt.Source,'') LIKE '%{v}%' OR IFNULL(mt.Source_File,'') LIKE '%{v}%')");
            }
            var where = filters.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", filters);
            var sql = $@"
                SELECT
                    et.id AS Tag_Link_ID,
                    t.tag_id AS Tag_ID,
                    t.tag_name AS Tag,
                    t.description AS Tag_Description,
                    et.source_context AS Tag_Context,
                    et.notes AS Tag_Notes,
                    et.created_utc AS Tagged_UTC,
                    mt.*
                FROM event_tags et
                JOIN tags t ON t.tag_id = et.tag_id
                JOIN v_master_timeline mt ON mt.event_id = et.event_id
                {where}
                ORDER BY et.created_utc DESC, mt.Date_Time DESC
                LIMIT {pageSize} OFFSET {offset};";

            _status.Text = $"Loading tagged page {_taggedPageIndex + 1:N0} in background...";
            var table = await Task.Run(() => DatabaseCore.QueryToDataTable(dbPath, sql));
            if (loadVersion != _taggedLoadVersion || IsDisposed)
                return;

            _taggedBaseTable = table;
            RefreshTagControls();
            _taggedGrid.DataSource = _taggedBaseTable;
            _taggedHeaderFilter?.CaptureCurrentView("Tagged Data");
            _taggedPageLabel.Text = $"Page {_taggedPageIndex + 1} | Rows {_taggedBaseTable.Rows.Count:N0}";
            _taggedPrevPage.Enabled = _taggedPageIndex > 0;
            _taggedNextPage.Enabled = _taggedBaseTable.Rows.Count >= pageSize;
            _status.Text = $"Tagged page {_taggedPageIndex + 1}: {_taggedBaseTable.Rows.Count:N0} rows loaded.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Tagged Data Load Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _status.Text = "Tagged data load failed.";
        }
    }

    private void ApplyTaggedView()
    {
        _taggedPageIndex = 0;
        LoadTaggedData();
    }

    private void TaggedGrid_SelectionChanged(object? sender, EventArgs e)
    {
        if (_taggedGrid.SelectedRows.Count == 0)
            return;

        var dt = new DataTable();
        dt.Columns.Add("Property");
        dt.Columns.Add("Value");
        var row = _taggedGrid.SelectedRows[0];
        foreach (DataGridViewCell cell in row.Cells)
        {
            var columnName = _taggedGrid.Columns[cell.ColumnIndex].HeaderText;
            dt.Rows.Add(columnName, cell.Value?.ToString() ?? "");
        }
        _taggedDetailsGrid.DataSource = dt;
    }

    private void RemoveSelectedTaggedLinks()
    {
        var dbPath = GetCaseDbPath();
        if (string.IsNullOrWhiteSpace(dbPath) || _taggedGrid.SelectedRows.Count == 0)
            return;

        using var conn = DatabaseCore.Open(dbPath);
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM event_tags WHERE id = $id;";
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        var removed = 0;
        foreach (DataGridViewRow row in _taggedGrid.SelectedRows)
        {
            if (row.IsNewRow || !TryGetLongCell(row, "Tag_Link_ID", out var linkId))
                continue;
            pId.Value = linkId;
            removed += cmd.ExecuteNonQuery();
        }
        tx.Commit();

        RefreshAfterTagChange();
        _status.Text = $"Removed {removed:N0} tag link(s).";
    }

    private void LoadParserCoverageData()
    {
        _ = LoadParserCoverageDataAsync();
    }

    private async Task LoadParserCoverageDataAsync()
    {
        var dbPath = GetCaseDbPath();
        var sources = (_currentCase?.Sources ?? new List<SourceFileRecord>()).ToList();
        var caseFolder = GetCurrentCaseFolder();
        try
        {
            var loadVersion = ++_coverageLoadVersion;
            _status.Text = "Loading parser/source coverage in background...";
            var result = await Task.Run(() =>
            {
                var parserTable = ParserCoverageService.BuildParserCoverageTable(dbPath, sources, caseFolder);
                var sourceTable = ParserCoverageService.BuildSourceCoverageTable(sources, caseFolder);
                var errorTable = ParserCoverageService.BuildParserErrorTable(dbPath);
                return (parserTable, sourceTable, errorTable);
            });
            if (loadVersion != _coverageLoadVersion || IsDisposed)
                return;

            _coverageGrid.DataSource = result.parserTable;
            _coverageSourcesGrid.DataSource = result.sourceTable;
            _coverageErrorsGrid.DataSource = result.errorTable;
            _coverageHeaderFilter?.CaptureCurrentView("Parser Coverage");
            _coverageSourcesHeaderFilter?.CaptureCurrentView("Source Coverage");
            _coverageErrorsHeaderFilter?.CaptureCurrentView("Parser Errors");
            _status.Text = "Parser coverage refreshed.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Parser Coverage Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _status.Text = "Parser coverage refresh failed.";
        }
    }

    private void ValidateParserFixtureFolder()
    {
        using var fbd = new FolderBrowserDialog { Description = "Select a folder containing known-good parser fixture artifacts." };
        if (fbd.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            _status.Text = "Validating parser fixture folder...";
            var table = ParserCoverageService.ValidateFixtureFolder(fbd.SelectedPath, TimeZoneInfo.Local.Id, LogCase);
            _validationGrid.DataSource = table;
            _validationHeaderFilter?.CaptureCurrentView("Parser Validation");
            _coverageInnerTabs.SelectedTab = _coverageInnerTabs.TabPages.Cast<TabPage>().FirstOrDefault(t => t.Text == "Fixture Validation") ?? _coverageInnerTabs.SelectedTab;
            _status.Text = $"Parser fixture validation complete: {table.Rows.Count:N0} parser/file result rows.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.ToString(), "Parser Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _status.Text = "Parser validation failed.";
        }
    }

    private void ExportValidationBundle()
    {
        if (_currentCase == null)
        {
            MessageBox.Show(this, "Create or open a case first.", "Validation Bundle", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var defaultName = SanitizeFileNameForExport(string.IsNullOrWhiteSpace(_currentCase.CaseName) ? "VestigantCase" : _currentCase.CaseName) + "_validation_bundle.zip";
        using var sfd = new SaveFileDialog { Filter = "ZIP archive|*.zip", FileName = defaultName };
        if (sfd.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            _status.Text = "Building validation bundle...";
            LoadParserCoverageData();
            var result = ValidationBundleService.ExportValidationBundle(sfd.FileName, _currentCase, GetCurrentCaseFolder(), GetCaseDbPath(), LogCase);
            _status.Text = $"Validation bundle exported: {Path.GetFileName(result.ZipPath)} ({result.ZipBytes:N0} bytes).";
            MessageBox.Show(this,
                $"Validation bundle exported.\n\n" +
                $"Source coverage rows: {result.SourceCoverageRows:N0}\n" +
                $"Parser coverage rows: {result.ParserCoverageRows:N0}\n" +
                $"Parser error rows: {result.ParserErrorRows:N0}\n" +
                $"Event summary rows: {result.EventSummaryRows:N0}\n" +
                $"Fallback/source quality rows: {result.MetadataFallbackRows:N0}\n\n" +
                result.ZipPath,
                "Validation Bundle", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.ToString(), "Validation Bundle Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _status.Text = "Validation bundle export failed.";
        }
    }

    private static string SanitizeFileNameForExport(string value)
    {
        var name = string.IsNullOrWhiteSpace(value) ? "VestigantCase" : value.Trim();
        foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
        return string.IsNullOrWhiteSpace(name) ? "VestigantCase" : name;
    }

    private void ExportCoverageCurrentGrid()
    {
        var grid = _coverageInnerTabs.SelectedTab?.Controls.OfType<DataGridView>().FirstOrDefault() ?? _coverageGrid;
        var name = _coverageInnerTabs.SelectedTab?.Text.Replace(' ', '_').ToLowerInvariant() ?? "parser_coverage";
        ExportGridToCsv(grid, $"{name}.csv");
    }

    private static void ConfigureCoverageGrid(DataGridView grid)
    {
        ConfigureGridPresentation(grid);
        SetColumnWidth(grid, "Parser", 240);
        SetColumnWidth(grid, "Coverage_Status", 170);
        SetColumnWidth(grid, "Candidate_Parsers", 340);
        SetColumnWidth(grid, "Original_Source_Path", 420);
        SetColumnWidth(grid, "Normalized_Original_Source_Path", 420);
        SetColumnWidth(grid, "Source_Path_Key", 420);
        SetColumnWidth(grid, "Local_Path", 320);
        SetColumnWidth(grid, "Normalized_Local_Path", 420);
        SetColumnWidth(grid, "Source_File", 260);
        SetColumnWidth(grid, "Error", 420);
        SetColumnWidth(grid, "Notes", 420);
        SetColumnWidth(grid, "Fields_Observed", 420);
    }

    private void RefreshDatabaseDiagnostics()
    {
        _ = RefreshDatabaseDiagnosticsAsync();
    }

    private async Task RefreshDatabaseDiagnosticsAsync()
    {
        var dbPath = GetCaseDbPath();
        if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
            return;
        try
        {
            var loadVersion = ++_maintenanceLoadVersion;
            _status.Text = "Loading database diagnostics in background...";
            var table = await Task.Run(() => DatabaseCore.GetDatabaseDiagnostics(dbPath));
            if (loadVersion != _maintenanceLoadVersion || IsDisposed)
                return;

            _maintenanceGrid.DataSource = table;
            _status.Text = "Database diagnostics refreshed.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Database Diagnostics Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task OptimizeDatabaseAsync()
    {
        var dbPath = GetCaseDbPath();
        if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath)) return;
        try
        {
            _maintenanceOptimize.Enabled = false;
            _status.Text = "Optimizing database indexes/statistics...";
            await Task.Run(() => DatabaseCore.OptimizeDatabase(dbPath));
            RefreshDatabaseDiagnostics();
            _status.Text = "Database optimization complete.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Database Optimization Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _maintenanceOptimize.Enabled = true;
        }
    }

    private async Task VacuumDatabaseAsync()
    {
        var dbPath = GetCaseDbPath();
        if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath)) return;
        if (MessageBox.Show(this, "VACUUM can take time on large databases and temporarily uses extra disk space. Continue?", "VACUUM Database", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        try
        {
            _maintenanceVacuum.Enabled = false;
            _status.Text = "VACUUM running...";
            await Task.Run(() => DatabaseCore.VacuumDatabase(dbPath));
            RefreshDatabaseDiagnostics();
            _status.Text = "VACUUM complete.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Database VACUUM Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _maintenanceVacuum.Enabled = true;
        }
    }

    private void LogCase(string msg)
    {
        if (IsDisposed)
            return;

        if (InvokeRequired)
        {
            try { BeginInvoke(new Action(() => LogCase(msg))); }
            catch { }
            return;
        }

        var clean = SanitizeLogMessage(msg);
        _caseLog.AppendText($"{DateTime.Now:HH:mm:ss} - {clean}{Environment.NewLine}");
    }

    private static string SanitizeLogMessage(string? msg)
    {
        if (string.IsNullOrWhiteSpace(msg))
            return string.Empty;

        var chars = msg.Select(ch => char.IsControl(ch) ? ' ' : ch).ToArray();
        var cleaned = new string(chars);
        while (cleaned.Contains("  ", StringComparison.Ordinal))
            cleaned = cleaned.Replace("  ", " ", StringComparison.Ordinal);
        return cleaned.Trim();
    }

    private void RefreshCaseBinding() => _caseSourceBinding.DataSource = new BindingList<SourceFileRecord>(_currentCase?.Sources ?? new());
}

internal sealed class GridFilterCondition
{
    public string ColumnName { get; }
    public string ContainsText { get; }

    public GridFilterCondition(string columnName, string containsText)
    {
        ColumnName = columnName ?? "";
        ContainsText = containsText ?? "";
    }

    public override string ToString() => $"{ColumnName} contains \"{ContainsText}\"";
}


internal sealed class TagComboItem
{
    public long Id { get; }
    public string Name { get; }

    public TagComboItem(long id, string name)
    {
        Id = id;
        Name = name ?? string.Empty;
    }

    public override string ToString() => Name;
}
