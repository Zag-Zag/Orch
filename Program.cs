using Orch.Models;
using Orch.Services;
using System.Collections.ObjectModel;
using System.Text;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Orch;

class Program
{
    private static readonly object _crashLock = new();
    private static StorageService _storage = new();
    private static OrchestratorService? _orch;
    private static string _workDir = "";
    private static FactoryConfig _factory = new();
    private static Plan? _plan;
    private static List<AgentConfig> _agents = new();
    private static List<IterationRecord> _history = new();
    private static List<string> _logLines = new();

    private static ObservableCollection<string> _agentItems = new();
    private static ObservableCollection<string> _planItems = new();
    private static ObservableCollection<string> _historyItems = new();

    // Stream buffer + role filter
    private static StringBuilder _streamBuffer = new();
    private static AgentRole? _historyFilterRole;
    private static readonly List<AgentRole> _allRoles = new() { AgentRole.SuperManager, AgentRole.Executor, AgentRole.Judge };

    private static Window? _win;
    private static Tabs? _leftTabs;
    private static ListView? _agentList;
    private static ListView? _planList;
    private static ListView? _historyList;
    private static Label? _statusLabel;
    private static Label? _agentDetail;
    private static Label? _logLabel;
    private static TextField? _workDirField;
    private static TextField? _tzField;
    private static FrameView? _rightFrame;
    private static TextView? _historyDetail;
    private static Label? _streamLabel;
    private static Label? _filterLabel;
    private static string? _activeAgentId;
    private static string? _activePlanItemId;

    // Orch — Terminal.Gui application for orchestrating AI agents with workflows, plans, and history tracking.
    static void Main(string[] args)
    {
        if (args.Length > 0) { _workDir = args[0]; LoadFromWorkDir(); }
        else
        {
            var lastWd = LoadLastWorkDir();
            if (!string.IsNullOrEmpty(lastWd)) { _workDir = lastWd; LoadFromWorkDir(); }
        }

        // --- Global crash handlers: write unhandled exceptions to .crash.log ---
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                LogCrash(ex, source: "UnhandledException (AppDomain)");
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash(args.Exception, source: "UnobservedTaskException");
            args.SetObserved(); // prevent process kill
        };

        Application.Init();
        _win = new Window
        {
            Title = " Orch - Cline Agent Orchestrator v2 ",
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };

        BuildTopBar();
        BuildSplitContent();
        BuildBottomBar();
        ResetCancellation();
        RefreshAll();
        Application.Run(_win);
        Application.Shutdown();
    }

    static void BuildTopBar()
    {
        // Row 0: Working directory
        _workDirField = new TextField { Text = _workDir, X = 1, Y = 0, Width = 35 };
        var btnBrowse = new Button { Text = "[ Browse ]", X = 38, Y = 0 };
        var btnSave = new Button { Text = "[ Save ]", X = 52, Y = 0 };
        _statusLabel = new Label { Text = " Status: IDLE ", X = 63, Y = 0, Width = Dim.Fill() };

        btnBrowse.Accepting += (s, e) => FileBrowserDialog(false);
        btnSave.Accepting += (s, e) => { SaveFactory(); RefreshAll(); };

        // Row 2: TЗ (Technical Specification)
        var lblTz = new Label { Text = " TЗ:", X = 1, Y = 2 };
        _tzField = new TextField { Text = _plan?.OriginalTz ?? "", X = 6, Y = 2, Width = 55 };
        var btnTz = new Button { Text = " ... ", X = 62, Y = 2 };

        btnTz.Accepting += (s, e) => EditTzDialog();

        _win!.Add(_workDirField, btnBrowse, btnSave, _statusLabel, lblTz, _tzField, btnTz);
    }

    static void BuildSplitContent()
    {
        _leftTabs = new Tabs
        {
            X = 0,
            Y = 4,
            Width = Dim.Percent(60),
            Height = Dim.Fill() - 4
        };

        var agentTabView = BuildAgentView();
        agentTabView.Title = " Agents ";
        _leftTabs.InsertTab(0, agentTabView);

        var planTabView = BuildPlanView();
        planTabView.Title = " Plan ";
        _leftTabs.InsertTab(1, planTabView);

        var settingsTabView = BuildSettingsView();
        settingsTabView.Title = " Settings ";
        _leftTabs.InsertTab(2, settingsTabView);

        _rightFrame = new FrameView
        {
            Title = " History / Output ",
            X = Pos.Right(_leftTabs),
            Y = 4,
            Width = Dim.Fill(),
            Height = Dim.Fill() - 4
        };

        // --- History list with role filter buttons ---
        var filterY = 0;

        _filterLabel = new Label { Text = " Filter:", X = 0, Y = filterY };
        _rightFrame.Add(_filterLabel);

        var btnFilterAll = new Button { Text = " All ", X = 9, Y = filterY };

        btnFilterAll.Accepting += (s, e) =>
        {
            if (_historyFilterRole == null) return;
            _historyFilterRole = null; RefreshHistory();
        };

        _rightFrame.Add(btnFilterAll);

        int fx = 18;
        foreach (var role in _allRoles)
        {
            var r = role;
            var btn = new Button { Text = $" {r.ToString()[..2].ToUpper()} ", X = fx, Y = filterY };
            btn.Accepting += (s, e) =>
            {
                _historyFilterRole = _historyFilterRole == r ? null : r;
                RefreshHistory();
            };
            _rightFrame.Add(btn);
            fx += 6;
        }

        _historyList = new ListView { X = 0, Y = 2, Width = Dim.Fill(), Height = 8 };
        _historyList.ValueChanged += (s, e) => ShowHistoryDetail();

        _historyDetail = new TextView
        {
            X = 0,
            Y = Pos.Bottom(_historyList),
            Width = Dim.Fill(),
            Height = Dim.Percent(30),
            ReadOnly = true,
            WordWrap = true
        };

        // Stream output label (below detail, fills remaining)
        _streamLabel = new Label
        {
            X = 0,
            Y = Pos.Bottom(_historyDetail),
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            Text = "Streaming output..."
        };

        _rightFrame.Add(_historyList, _historyDetail, _streamLabel);
        _win!.Add(_leftTabs, _rightFrame);
    }

    static View BuildAgentView()
    {
        var frame = new View { Width = Dim.Fill(), Height = Dim.Fill() };

        var btnAddSM = new Button { Text = " + SM ", X = 0, Y = 0 };
        var btnAddExec = new Button { Text = " + Executor ", X = 9, Y = 0 };
        var btnAddJudge = new Button { Text = " + Judge ", X = 24, Y = 0 };
        var btnEdit = new Button { Text = " Edit ", X = 38, Y = 0 };
        var btnRemove = new Button { Text = " Remove ", X = 48, Y = 0 };

        btnAddSM.Accepting += (s, e) => AddAgent(AgentRole.SuperManager);
        btnAddExec.Accepting += (s, e) => AddAgent(AgentRole.Executor);
        btnAddJudge.Accepting += (s, e) => AddAgent(AgentRole.Judge);
        btnEdit.Accepting += (s, e) => EditAgentDialog();
        btnRemove.Accepting += (s, e) => RemoveAgent();

        _agentList = new ListView { X = 0, Y = 2, Width = Dim.Fill(), Height = Dim.Fill() - 4 };
        _agentList.ValueChanged += (s, e) => UpdateAgentDetail();

        _agentDetail = new Label { X = 0, Y = Pos.Bottom(_agentList), Width = Dim.Fill(), Height = 2 };

        frame.Add(btnAddSM, btnAddExec, btnAddJudge, btnEdit, btnRemove, _agentList, _agentDetail);
        return frame;
    }

    static View BuildPlanView()
    {
        var frame = new View { Width = Dim.Fill(), Height = Dim.Fill() };

        var lblPlan = new Label
        {
            Text = " Plan items:",
            X = 0,
            Y = 0
        };

        var btnAutoPlan = new Button { Text = " Auto-Plan ", X = 0, Y = 1 };
        var btnAddItem = new Button { Text = " + Add ", X = 14, Y = 1 };
        var btnEditItem = new Button { Text = " Edit ", X = 24, Y = 1 };
        var btnDelItem = new Button { Text = " Remove ", X = 33, Y = 1 };
        var btnCheck = new Button { Text = " Check ", X = 44, Y = 1 };

        btnAutoPlan.Accepting += (s, e) => { AutoPlan(); };
        btnAddItem.Accepting += (s, e) => { AddPlanItem(); };
        btnEditItem.Accepting += (s, e) => { EditPlanItem(); };
        btnDelItem.Accepting += (s, e) => { RemovePlanItem(); };
        btnCheck.Accepting += (s, e) => { CheckPlanItem(); };

        _planList = new ListView { X = 0, Y = 3, Width = Dim.Fill(), Height = Dim.Fill() - 3 };
        // Click (Enter/Accept) on a plan item toggles its status
        _planList.Accepting += (s, e) => { TogglePlanItem(); };

        frame.Add(lblPlan, btnAutoPlan, btnAddItem, btnEditItem, btnDelItem, btnCheck, _planList);
        return frame;
    }

    static View BuildSettingsView()
    {
        var frame = new View { Width = Dim.Fill(), Height = Dim.Fill() };

        var btnPrompts = new Button { Text = " Edit System Prompts ", X = 0, Y = 0 };
        var lblInfo = new Label
        {
            X = 0,
            Y = 3,
            Text = "  .fabrik     — Factory configuration (agents/roles)\n" +
                   "  .plane      — Development plan (tasks/statuses)\n" +
                   "  .history    — Iteration records (full chat logs)\n" +
                   "  prompts.json — System prompts (auto-loaded)"
        };

        btnPrompts.Accepting += (s, e) => EditPromptsDialog();
        frame.Add(btnPrompts, lblInfo);
        return frame;
    }

    static void BuildBottomBar()
    {
        var sep = new Line { X = 0, Y = Pos.AnchorEnd(2), Width = Dim.Fill(), Orientation = Orientation.Horizontal };
        _win!.Add(sep);

        var btnStart = new Button { Text = " [ START ] ", X = 1, Y = Pos.AnchorEnd(1) };
        var btnStop = new Button { Text = " [ STOP ] ", X = 14, Y = Pos.AnchorEnd(1) };
        _logLabel = new Label { Text = " Ready.", X = 28, Y = Pos.AnchorEnd(1), Width = Dim.Fill() - 28 };

        btnStart.Accepting += async (s, e) =>
        {
            _streamBuffer.Clear();
            if (_streamLabel != null) _streamLabel.Text = "";
            _ = RunCycleAsync();
        };
        btnStop.Accepting += (s, e) => { _orch?.Stop(); Log("STOPPED"); };

        _win.Add(btnStart, btnStop, _logLabel);
    }

    static async Task CreatePlanAsync()
    {
        if (!ValidateSetup()) return;
        _streamBuffer.Clear();
        if (_streamLabel != null) _streamLabel.Text = "";
        Log("Creating plan...");
        await _orch!.CreatePlanAsync();
        RefreshAll();
        Log($"Plan: {_plan?.Items.Count ?? 0} items.");
    }

    static async Task RunCycleAsync()
    {
        if (!ValidateSetup()) return;
        Log("Production cycle STARTED");
        _statusLabel!.Text = " Status: RUNNING ";
        await _orch!.RunProductionCycleAsync();
        RefreshAll();
        _statusLabel!.Text = $" Status: {_orch.Status.ToString().ToUpper()} ";
        Log("Cycle completed.");
    }

    static void AddAgent(AgentRole role)
    {
        int c = _agents.Count(a => a.Role == role) + 1;
        _agents.Add(new AgentConfig
        {
            Role = role,
            Name = $"{role}_{c}",
            SystemPrompt = Defaults.DefaultSystemPrompts[role],
            PreserveContext = role == AgentRole.SuperManager,
            JudgeSubRoles = role == AgentRole.Judge ? Defaults.DefaultJudgeSubRoles.ToList() : new()
        });
        RefreshAll();
    }

    static void RemoveAgent()
    {
        int idx = _agentList?.SelectedItem ?? -1;
        if (idx < 0 || idx >= _agents.Count)
        {
            Log("Remove: no agent selected.");
            return;
        }
        var a = _agents[idx];
        _agents.RemoveAt(idx);
        RefreshAll();
        Log($"Removed agent: {a.Name}");
    }

    static void EditAgentDialog()
    {
        int idx = _agentList?.SelectedItem ?? -1;
        if (idx < 0 || idx >= _agents.Count) return;
        ShowEditAgentDialog(_agents[idx]);
        RefreshAll();
    }

    static void UpdateAgentDetail()
    {
        int idx = _agentList?.SelectedItem ?? -1;
        if (idx >= 0 && idx < _agents.Count && _agentDetail != null)
        {
            var a = _agents[idx];
            var sr = a.JudgeSubRoles.Any() ? " [" + string.Join(", ", a.JudgeSubRoles) + "]" : "";
            bool hasPlan = _plan != null && _plan.Items.Count > 0;
            string ctxStatus = a.Role == AgentRole.SuperManager && !hasPlan
                ? "CTX: LOCKED-ON"
                : $"Ctx: {(a.PreserveContext ? "ON" : "OFF")}";
            _agentDetail.Text = $"  {a.Role}: {a.Name}{sr}  {ctxStatus}";
        }
    }

    /// <summary>
    /// Auto-generates global business plan from TЗ via Cline.
    /// Requests a high-level plan: DB → Services → API → UI, etc.
    /// If a plan already exists, it REVISES it (does NOT clear until new plan is ready).
    /// Falls back to simulation if Cline not available.
    /// </summary>
    static async void AutoPlan()
    {
        var tz = _plan?.OriginalTz ?? _tzField?.Text.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(tz))
        {
            Log("ERROR: Enter TЗ text first.");
            return;
        }

        _streamBuffer.Clear();
        if (_streamLabel != null) _streamLabel.Text = "";
        var isRevision = _plan != null && _plan.Items.Count > 0;
        Log(isRevision ? "Auto-Plan: REVISING existing plan..." : "Auto-Plan: Requesting global plan from Cline...");
        _statusLabel!.Text = " Status: PLANNING ";

        var loadingDlg = new Dialog
        {
            Title = " Auto-Plan ",
            Width = 50,
            Height = 7,
            X = Pos.Center(),
            Y = Pos.Center()
        };
        loadingDlg.Add(new Label
        {
            Text = "Идёт построение плана...",
            X = 1,
            Y = 1,
            Width = Dim.Fill() - 2
        });
        _win!.Add(loadingDlg);

        try
        {
            var generated = await _orch!.GenerateGlobalPlanAsync(tz);
            if (generated == null)
            {
                _win.Remove(loadingDlg);
                ShowErrorDialog("Auto-Plan Failed", "Cline not available. Cannot generate plan without AI.\n\nInstall Cline or check NPX path.");
                _statusLabel!.Text = " Status: IDLE ";
                RefreshAll();
                return;
            }
            _plan ??= new Plan();
            _plan.Items = generated.Items;
            _plan.OriginalTz = tz;
            Log($"Auto-Plan: {_plan.Items.Count} items generated.");
        }
        catch (Exception ex)
        {
            var fullError = ex.ToString();
            Log($"Auto-Plan failed: {ex.Message}");
            _win.Remove(loadingDlg);
            ShowErrorDialog("Auto-Plan Failed", fullError);
            _statusLabel!.Text = " Status: IDLE ";
            RefreshAll();
            return;
        }
        finally
        {
            _win.Remove(loadingDlg);
            _statusLabel!.Text = " Status: IDLE ";
            RefreshAll();
        }
    }

    static void AddPlanItem() { _plan ??= new Plan(); _plan.Items.Add(new PlanItem { Title = "New task", Description = "..." }); RefreshAll(); }
    static void EditPlanItem() { int idx = _planList?.SelectedItem ?? -1; if (_plan != null && idx >= 0 && idx < _plan.Items.Count) { ShowEditPlanItemDialog(_plan.Items[idx]); RefreshAll(); } }
    static void RemovePlanItem() { int idx = _planList?.SelectedItem ?? -1; if (_plan != null && idx >= 0 && idx < _plan.Items.Count) { _plan.Items.RemoveAt(idx); RefreshAll(); } }

    static void TogglePlanItem()
    {
        int idx = _planList?.SelectedItem ?? -1;
        if (_plan == null || idx < 0 || idx >= _plan.Items.Count) return;
        var item = _plan.Items[idx];
        item.Status = item.Status switch
        {
            PlanItemStatus.Pending => PlanItemStatus.Done,
            PlanItemStatus.Done => PlanItemStatus.Pending,
            PlanItemStatus.InProgress => PlanItemStatus.Pending,
            _ => PlanItemStatus.Pending
        };
        Log($"Toggled '{item.Title}' -> {item.Status}");
        RefreshAll();
    }

    static void CheckPlanItem()
    {
        int idx = _planList?.SelectedItem ?? -1;
        if (_plan == null || idx < 0 || idx >= _plan.Items.Count) return;
        var item = _plan.Items[idx];
        ShowCheckPlanItemDialog(item);
        RefreshAll();
    }

    static void ShowCheckPlanItemDialog(PlanItem item)
    {
        var dlg = new Dialog { Title = $" Check: {item.Title} ", Width = 65, Height = 14 };

        dlg.Add(new Label { Text = $"Item: {item.Title}", X = 1, Y = 1 });

        dlg.Add(new Label { Text = "Remarks (issues found):", X = 1, Y = 3 });
        var tvRemarks = new TextView
        {
            X = 1,
            Y = 4,
            Width = Dim.Fill() - 2,
            Height = 4,
            Text = item.Adjustments ?? ""
        };
        dlg.Add(tvRemarks);

        var btnApprove = new Button { Text = " Approve ", X = 10, Y = 9 };
        var btnReject = new Button { Text = " Reject (add remark) ", X = 23, Y = 9 };
        var btnCancel = new Button { Text = " Cancel ", X = 47, Y = 9 };
        dlg.AddButton(btnApprove); dlg.AddButton(btnReject); dlg.AddButton(btnCancel);

        btnApprove.Accepting += (s, e) =>
        {
            item.Status = PlanItemStatus.Done;
            item.Adjustments = null;
            Log($"Approved: {item.Title}");
            Application.RequestStop();
        };
        btnReject.Accepting += (s, e) =>
        {
            var remark = tvRemarks.Text.ToString()?.Trim();
            if (!string.IsNullOrWhiteSpace(remark))
            {
                item.Adjustments = remark;
                item.Status = PlanItemStatus.Pending;
                Log($"Rejected with remarks: {item.Title}");
            }
            else Log("No remark entered — rejection ignored.");
            Application.RequestStop();
        };
        btnCancel.Accepting += (s, e) => Application.RequestStop();
        Application.Run(dlg);
    }

    static void EditTzDialog()
    {
        var dlg = new Dialog { Title = " Original TЗ (Technical Specification) ", Width = 70, Height = 16 };
        var tv = new TextView
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill() - 2,
            Height = Dim.Fill() - 2,
            Text = _plan?.OriginalTz ?? ""
        };
        dlg.Add(tv);
        var btnOk = new Button { Text = " Save ", X = 24, Y = Pos.Bottom(tv) + 1 };
        var btnCancel = new Button { Text = " Cancel ", X = 35, Y = Pos.Bottom(tv) + 1 };
        dlg.AddButton(btnOk); dlg.AddButton(btnCancel);
        btnOk.Accepting += (s, e) => { _plan ??= new Plan(); _plan.OriginalTz = tv.Text.ToString() ?? ""; _tzField!.Text = _plan.OriginalTz; Application.RequestStop(); };
        btnCancel.Accepting += (s, e) => Application.RequestStop();
        Application.Run(dlg);
    }

    static void ShowHistoryDetail()
    {
        int idx = _historyList?.SelectedItem ?? -1;
        var filtered = GetFilteredHistory();
        if (idx < 0 || idx >= filtered.Count || _historyDetail == null) return;
        var rec = filtered[idx];
        _historyDetail.Text = $"#{rec.IterationNumber} — {rec.AgentName} ({rec.Role})  {rec.Timestamp:yyyy-MM-dd HH:mm:ss}\n\n" +
                              $"=== INPUT ===\n{rec.Input}\n\n=== OUTPUT ===\n{rec.Output}\n\n=== FULL CHAT ===\n{rec.FullChat}";
    }

    static List<IterationRecord> GetFilteredHistory()
    {
        return _historyFilterRole == null
            ? _history.ToList()
            : _history.Where(h => h.Role == _historyFilterRole.Value).ToList();
    }

    // ========== FILE BROWSER ==========
    static void FileBrowserDialog(bool loadMode)
    {
        string title = loadMode ? " Load .fabrik " : " Browse Directory ";
        var browser = new Window
        {
            Title = title,
            Width = 70,
            Height = 18,
            X = 2,
            Y = 2
        };

        var currentPath = string.IsNullOrEmpty(_workDir) ? Environment.CurrentDirectory : _workDir;
        try { if (!Directory.Exists(currentPath)) currentPath = Environment.CurrentDirectory; } catch { currentPath = Environment.CurrentDirectory; }

        // Drive-switching header: clickable drive button + path label
        var driveRoot = Path.GetPathRoot(currentPath) ?? currentPath;
        var driveBtn = new Button { Text = driveRoot, X = 1, Y = 0 };
        var remainingPath = currentPath.Length > driveRoot.Length ? currentPath[driveRoot.Length..] : "";
        var pathLabel = new Label { Text = remainingPath, X = 2 + driveRoot.Length + 1, Y = 0, Width = Dim.Fill() };
        browser.Add(driveBtn, pathLabel);

        var entries = new ObservableCollection<string>();
        var listView = new ListView { X = 1, Y = 2, Width = Dim.Fill() - 2, Height = 12 };
        browser.Add(listView);

        void RefreshDir(string path)
        {
            entries.Clear();
            entries.Add("  [..]");
            try
            {
                foreach (var d in Directory.GetDirectories(path).OrderBy(x => x))
                    entries.Add("  <DIR>  " + Path.GetFileName(d));
                if (loadMode)
                    foreach (var f in Directory.GetFiles(path, "*.fabrik").OrderBy(x => x))
                        entries.Add("  <CFG>  " + Path.GetFileName(f));
                else
                    foreach (var f in Directory.GetFiles(path).OrderBy(x => x))
                        entries.Add("         " + Path.GetFileName(f));
            }
            catch { }
            listView.SetSource(entries);
            currentPath = path;
            var newDriveRoot = Path.GetPathRoot(path) ?? path;
            driveBtn.Text = newDriveRoot;
            var newRemaining = path.Length > newDriveRoot.Length ? path[newDriveRoot.Length..] : "";
            pathLabel.Text = newRemaining;
            pathLabel.X = 2 + newDriveRoot.Length + 1;
        }

        RefreshDir(currentPath);

        listView.Accepting += (s, e) =>
        {
            int sel = listView.SelectedItem ?? -1;
            if (sel < 0 || sel >= entries.Count) return;
            var t = entries[sel];
            if (sel == 0)
            {
                var parent = Directory.GetParent(currentPath);
                if (parent != null) RefreshDir(parent.FullName);
            }
            else if (t.Contains("<DIR>"))
            {
                int si = t.IndexOf("<DIR>") + 6;
                var dn = t[si..].Trim();
                var np = Path.Combine(currentPath, dn);
                if (Directory.Exists(np)) RefreshDir(np);
            }
        };

        // Drive switching: clicking drive button opens drive picker
        driveBtn.Accepting += (s, e) =>
        {
            ShowDrivePickerDialog(browser, currentPath, (newDriveRoot) =>
            {
                if (!string.Equals(newDriveRoot, Path.GetPathRoot(currentPath), StringComparison.OrdinalIgnoreCase))
                    RefreshDir(newDriveRoot);
            });
        };

        var btnSelect = new Button { Text = " Select ", X = 16, Y = 14 };
        var btnCancel = new Button { Text = " Cancel ", X = 28, Y = 14 };
        browser.Add(btnSelect, btnCancel);

        btnSelect.Accepting += (s, e) =>
        {
            if (loadMode)
            {
                int sel = listView.SelectedItem ?? -1;
                if (sel >= 0 && sel < entries.Count && entries[sel].Contains("<CFG>"))
                {
                    var t = entries[sel];
                    int si = t.IndexOf("<CFG>") + 6;
                    var fname = t[si..].Trim();
                    var fpath = Path.Combine(currentPath, fname);
                    var cfg = _storage.LoadFactory(fpath);
                    if (cfg != null)
                    {
                        _factory = cfg; _agents = _factory.Agents.ToList();
                        _workDir = Path.GetDirectoryName(fpath) ?? _workDir;
                        _workDirField!.Text = _workDir;
                        LoadFromWorkDir(); RefreshAll();
                        Log("Loaded: " + fpath);
                    }
                    else Log("ERROR: Failed to load " + fpath);
                }
                else
                {
                    _workDir = currentPath;
                    _workDirField!.Text = _workDir;
                    SaveLastWorkDir(_workDir);
                    LoadFromWorkDir(); RefreshAll();
                    Log("Work dir: " + _workDir);
                }
            }
            else
            {
                _workDir = currentPath;
                _workDirField!.Text = _workDir;
                SaveLastWorkDir(_workDir);
                LoadFromWorkDir(); RefreshAll();
                Log("Work dir: " + _workDir);
            }
            browser.SuperView?.Remove(browser);
        };

        btnCancel.Accepting += (s, e) => browser.SuperView?.Remove(browser);
        _win!.Add(browser);
    }


    static void ShowDrivePickerDialog(View parent, string currentPath, Action<string> onDriveSelected)
    {
        var drives = DriveInfo.GetDrives()
            .Where(d => d.IsReady)
            .Select(d => $"  {d.Name}  [{d.VolumeLabel}]")
            .ToList();

        if (drives.Count == 0) return;

        var dlg = new Dialog
        {
            Title = " Select Drive ",
            Width = 50,
            Height = Math.Min(drives.Count + 6, 14),
            X = Pos.Center(),
            Y = Pos.Center()
        };

        var driveEntries = new ObservableCollection<string>(drives);
        var driveList = new ListView
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill() - 2,
            Height = drives.Count
        };
        driveList.SetSource(driveEntries);
        dlg.Add(driveList);

        var btnOk = new Button { Text = " OK ", X = Pos.Center() - 10, Y = drives.Count + 2 };
        var btnCancel = new Button { Text = " Cancel ", X = Pos.Center() + 4, Y = drives.Count + 2 };
        dlg.AddButton(btnOk);
        dlg.AddButton(btnCancel);

        var readyDrives = DriveInfo.GetDrives().Where(d => d.IsReady).ToList();

        void SelectDrive(int index)
        {
            if (index >= 0 && index < readyDrives.Count)
            {
                var selectedRoot = readyDrives[index].RootDirectory.FullName;
                dlg.SuperView?.Remove(dlg);
                onDriveSelected(selectedRoot);
            }
        }

        btnOk.Accepting += (s, e) => SelectDrive(driveList.SelectedItem ?? -1);
        btnCancel.Accepting += (s, e) => dlg.SuperView?.Remove(dlg);
        driveList.Accepting += (s, e) => SelectDrive(driveList.SelectedItem ?? -1);

        parent.SuperView?.Add(dlg);
    }

    static void ShowEditAgentDialog(AgentConfig agent)
    {
        var dlg = new Dialog { Title = $" Edit Agent: {agent.Name} ", Width = 72, Height = 20 };
        int y = 0;

        dlg.Add(new Label { Text = "Name:", X = 1, Y = y });
        var tfName = new TextField { Text = agent.Name, X = 8, Y = y, Width = 28 };
        dlg.Add(tfName); y += 2;

        dlg.Add(new Label { Text = $"Role: {agent.Role}", X = 1, Y = y }); y += 2;

        bool hasPlan = _plan != null && _plan.Items.Count > 0;

        if (agent.Role == AgentRole.SuperManager && !hasPlan)
        {
            // SM context locked
            dlg.Add(new Label { Text = "Context: LOCKED ON (create plan to unlock)", X = 1, Y = y }); y += 2;
        }
        else
        {
            dlg.Add(new Label { Text = "Preserve Context:", X = 1, Y = y });
            var btnCtx = new Button { Text = agent.PreserveContext ? " [ ON ] " : " [ OFF ] ", X = 20, Y = y };
            btnCtx.Accepting += (s, e) =>
            {
                agent.PreserveContext = !agent.PreserveContext;
                btnCtx.Text = agent.PreserveContext ? " [ ON ] " : " [ OFF ] ";
            };
            dlg.Add(btnCtx); y += 2;
        }

        if (agent.Role == AgentRole.Judge)
        {
            dlg.Add(new Label { Text = "Judge Sub-Roles:", X = 1, Y = y }); y++;
            foreach (var sr in new[] { JudgeSubRole.TzCompliance, JudgeSubRole.CodingStandards, JudgeSubRole.ErrorCheck, JudgeSubRole.CodeQuality })
            {
                bool has = agent.JudgeSubRoles.Contains(sr);
                var btn = new Button { Text = $" {(has ? "[X]" : "[ ]")} {sr} ", X = 3, Y = y };
                var cap = sr;
                btn.Accepting += (s, e) =>
                {
                    if (agent.JudgeSubRoles.Contains(cap)) agent.JudgeSubRoles.Remove(cap);
                    else agent.JudgeSubRoles.Add(cap);
                    btn.Text = $" {(agent.JudgeSubRoles.Contains(cap) ? "[X]" : "[ ]")} {cap} ";
                };
                dlg.Add(btn); y++;
            }
        }

        dlg.Add(new Label { Text = "System Prompt:", X = 1, Y = y }); y++;
        var tvPrompt = new TextView { X = 1, Y = y, Width = Dim.Fill() - 2, Height = 4, Text = agent.SystemPrompt };
        dlg.Add(tvPrompt); y += 5;

        var btnOk = new Button { Text = " OK ", X = 25, Y = y };
        var btnCancel = new Button { Text = " Cancel ", X = 36, Y = y };
        dlg.Add(btnOk); dlg.Add(btnCancel);
        var savedIdx = _agentList?.SelectedItem;
        btnOk.Accepting += (s, e) =>
        {
            agent.Name = tfName.Text.ToString() ?? agent.Name;
            agent.SystemPrompt = tvPrompt.Text.ToString() ?? agent.SystemPrompt;
            _win!.Remove(dlg);
            RefreshAll();
            FocusAgentList(savedIdx);
        };
        btnCancel.Accepting += (s, e) => { _win!.Remove(dlg); RefreshAll(); FocusAgentList(savedIdx); };
        _win!.Add(dlg);
    }

    static void ShowEditPlanItemDialog(PlanItem item)
    {
        var dlg = new Dialog { Title = " Edit Plan Item ", Width = 60, Height = 12 };
        dlg.Add(new Label { Text = "Title:", X = 1, Y = 1 });
        var tf = new TextField { Text = item.Title, X = 8, Y = 1, Width = 45 }; dlg.Add(tf);
        dlg.Add(new Label { Text = "Description:", X = 1, Y = 3 });
        var tv = new TextView { X = 1, Y = 4, Width = Dim.Fill() - 2, Height = 3, Text = item.Description }; dlg.Add(tv);
        var btnOk = new Button { Text = " OK ", X = 16, Y = 8 };
        var btnCancel = new Button { Text = " Cancel ", X = 27, Y = 8 };
        dlg.AddButton(btnOk); dlg.AddButton(btnCancel);
        btnOk.Accepting += (s, e) => { item.Title = tf.Text.ToString() ?? item.Title; item.Description = tv.Text.ToString() ?? item.Description; Application.RequestStop(); };
        btnCancel.Accepting += (s, e) => Application.RequestStop();
        Application.Run(dlg);
    }

    static void EditPromptsDialog()
    {
        var dlg = new Dialog { Title = " System Prompts Editor ", Width = 80, Height = 22 };
        var tabs = new Tabs { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() - 3 };

        var smView = new View { Width = Dim.Fill(), Height = Dim.Fill(), Title = " SuperManager " };
        var smTv = new TextView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), Text = Defaults.DefaultSystemPrompts[AgentRole.SuperManager] };
        smView.Add(smTv); tabs.InsertTab(0, smView);

        var execView = new View { Width = Dim.Fill(), Height = Dim.Fill(), Title = " Executor " };
        var execTv = new TextView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), Text = Defaults.DefaultSystemPrompts[AgentRole.Executor] };
        execView.Add(execTv); tabs.InsertTab(1, execView);

        var judgeView = new View { Width = Dim.Fill(), Height = Dim.Fill(), Title = " Judge " };
        var judgeTv = new TextView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), Text = Defaults.DefaultSystemPrompts[AgentRole.Judge] };
        judgeView.Add(judgeTv); tabs.InsertTab(2, judgeView);

        dlg.Add(tabs);
        var btnSave = new Button { Text = " Save to prompts.json ", X = 28, Y = Pos.Bottom(tabs) + 1 };
        var btnCancel = new Button { Text = " Cancel ", X = 52, Y = Pos.Bottom(tabs) + 1 };
        dlg.AddButton(btnSave); dlg.AddButton(btnCancel);
        btnSave.Accepting += (s, e) =>
        {
            var newPrompts = new Dictionary<AgentRole, string>
            {
                [AgentRole.SuperManager] = smTv.Text.ToString() ?? "",
                [AgentRole.Executor] = execTv.Text.ToString() ?? "",
                [AgentRole.Judge] = judgeTv.Text.ToString() ?? ""
            };
            Defaults.DefaultSystemPrompts = newPrompts;
            Log("System prompts saved to prompts.json");
            Application.RequestStop();
        };
        btnCancel.Accepting += (s, e) => Application.RequestStop();
        Application.Run(dlg);
    }

    static bool ValidateSetup()
    {
        if (string.IsNullOrEmpty(_workDir)) { Log("ERROR: Set working directory (Browse)."); return false; }
        if (!_agents.Any(a => a.Role == AgentRole.SuperManager)) { Log("ERROR: Add a SuperManager agent (+ SM)."); return false; }
        if (!_agents.Any(a => a.Role == AgentRole.Executor)) { Log("ERROR: Add an Executor agent (+ Executor)."); return false; }

        // Update factory and apply context rules
        _factory.Name = "OrchFactory"; _factory.WorkingDirectory = _workDir; _factory.Agents = _agents.ToList();
        _plan ??= new Plan();
        if (string.IsNullOrWhiteSpace(_plan.OriginalTz) && !string.IsNullOrWhiteSpace(_tzField?.Text.ToString()))
            _plan.OriginalTz = _tzField!.Text.ToString() ?? "";

        // SM context rule: if no plan, SM context is always ON
        bool hasPlan = _plan.Items.Count > 0;
        foreach (var a in _agents.Where(a => a.Role == AgentRole.SuperManager))
        {
            if (!hasPlan) a.PreserveContext = true;
        }

        _orch!.SetFactory(_factory); _orch.SetPlan(_plan);
        return true;
    }

    static void ResetCancellation()
    {
        _orch = new OrchestratorService(_storage, _workDir);
        _orch.OnLog += Log;
        _orch.OnIterationAdded += _ =>
        {
            _history = _orch.History;
            Application.Invoke(RefreshHistory);
        };
        _orch.OnStatusChanged += s =>
            Application.Invoke(() => { _statusLabel!.Text = $" Status: {s.ToString().ToUpper()} "; });

        _orch.OnStreamChunk += chunk =>
            Application.Invoke(() =>
            {
                _streamBuffer.Append(chunk);
                if (_streamLabel != null)
                {
                    var txt = _streamBuffer.ToString();
                    if (txt.Length > 8000) txt = txt[^8000..];
                    _streamLabel.Text = txt;
                }
            });

        _orch.OnAgentActive += agent =>
            Application.Invoke(() =>
            {
                _activeAgentId = agent.Id;
                RefreshAgents();
            });

        _orch.OnPlanItemActive += item =>
            Application.Invoke(() =>
            {
                _activePlanItemId = item.Id;
                RefreshPlan();
            });
    }

    static void SaveFactory()
    {
        if (string.IsNullOrEmpty(_workDir)) return;

        // Apply SM context rule before saving
        bool hasPlan = _plan != null && _plan.Items.Count > 0;
        foreach (var a in _agents.Where(a => a.Role == AgentRole.SuperManager))
            if (!hasPlan) a.PreserveContext = true;

        _factory.Name = "OrchFactory"; _factory.WorkingDirectory = _workDir; _factory.Agents = _agents.ToList();
        _storage.SaveFactory(_factory, _storage.GetFactoryPath(_workDir));
        if (_plan != null)
        {
            if (string.IsNullOrWhiteSpace(_plan.OriginalTz) && !string.IsNullOrWhiteSpace(_tzField?.Text.ToString()))
                _plan.OriginalTz = _tzField!.Text.ToString() ?? "";
            _storage.SavePlan(_plan, _storage.GetPlanPath(_workDir));
        }
        Log("Saved to " + _storage.GetFactoryPath(_workDir));
    }

    static void LoadFromWorkDir()
    {
        if (string.IsNullOrEmpty(_workDir)) return;
        var cfg = _storage.LoadFactory(_storage.GetFactoryPath(_workDir));
        if (cfg != null) { _factory = cfg; _agents = _factory.Agents.ToList(); }
        _plan = _storage.LoadPlan(_storage.GetPlanPath(_workDir));
        if (_plan != null && _tzField != null) _tzField.Text = _plan.OriginalTz;
        _history = _storage.LoadHistory(_storage.GetHistoryPath(_workDir));
        ResetCancellation();
    }

    static void RefreshAll() { RefreshAgents(); RefreshPlan(); RefreshHistory(); }

    static void RefreshAgents()
    {
        _agentItems.Clear();
        var savedSel = _agentList?.SelectedItem;
        bool hasPlan = _plan != null && _plan.Items.Count > 0;
        foreach (var a in _agents)
        {
            string ctx;
            if (a.Role == AgentRole.SuperManager && !hasPlan)
                ctx = " [CTX:LOCKED]";
            else
                ctx = a.PreserveContext ? " [CTX]" : "";
            string subs = a.Role == AgentRole.Judge && a.JudgeSubRoles.Any() ? $" ({a.JudgeSubRoles.Count})" : "";
            string activePrefix = a.Id == _activeAgentId ? "► " : "  ";
            _agentItems.Add($"{activePrefix}  [{a.Role.ToString()[..2].ToUpper()}] {a.Name}{ctx}{subs}");
        }
        _agentList?.SetSource(_agentItems);
        if (savedSel.HasValue && savedSel.Value >= 0 && savedSel.Value < _agentItems.Count)
            _agentList!.SelectedItem = savedSel.Value;
        UpdateAgentDetail();
    }

    static void RefreshPlan()
    {
        _planItems.Clear();
        if (_plan != null)
            foreach (var i in _plan.Items)
            {
                string st = i.Status switch
                {
                    PlanItemStatus.Pending => " [ ]",
                    PlanItemStatus.InProgress => " [>]",
                    PlanItemStatus.Done => " [V]",
                    _ => " [?]"
                };
                string remark = !string.IsNullOrWhiteSpace(i.Adjustments) ? " [!]" : "";
                string activePrefix = i.Id == _activePlanItemId ? "► " : "  ";
                _planItems.Add($"{activePrefix}{st} {i.Title}{remark}");
            }
        _planList?.SetSource(_planItems);
    }

    static void RefreshHistory()
    {
        _historyItems.Clear();
        var filtered = GetFilteredHistory();
        foreach (var h in Enumerable.Reverse(filtered))
        {
            var pv = h.Output.Length > 50 ? h.Output[..50] + "..." : h.Output;
            pv = pv.Replace('\n', ' ').Replace('\r', ' ');
            string stream = h.IsStreaming ? " [>>]" : "";
            _historyItems.Add($"  #{h.IterationNumber} [{h.Role.ToString()[..2]}] {h.AgentName}:{stream} {pv}");
        }
        _historyList?.SetSource(_historyItems);
    }

    static void Log(string msg)
    {
        _logLines.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
        if (_logLines.Count > 200) _logLines.RemoveRange(0, _logLines.Count - 200);
        Application.Invoke(() => { if (_logLabel != null) _logLabel.Text = _logLines.LastOrDefault() ?? ""; });
    }

    // ==================== LAST WORKDIR PERSISTENCE ====================

    private static string GetLastWorkDirPath() =>
        Path.Combine(AppContext.BaseDirectory, ".orch_lastwd");

    private static string? LoadLastWorkDir()
    {
        var path = GetLastWorkDirPath();
        try
        {
            if (File.Exists(path)) return File.ReadAllText(path).Trim();
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Writes crash info to .crash.log in _workDir (or BaseDirectory as fallback).
    /// Thread‑safe, catches own exceptions to avoid infinite loops.
    /// </summary>
    static void LogCrash(Exception ex, string source)
    {
        try
        {
            var dir = !string.IsNullOrWhiteSpace(_workDir) ? _workDir : AppContext.BaseDirectory;
            var path = Path.Combine(dir, ".crash.log");

            var sb = new StringBuilder();
            sb.AppendLine(new string('=', 80));
            sb.AppendLine($"CRASH  |  {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            sb.AppendLine($"Source |  {source}");
            sb.AppendLine(new string('-', 80));
            sb.AppendLine(ex.ToString());
            sb.AppendLine();

            lock (_crashLock)
            {
                File.AppendAllText(path, sb.ToString());
            }
        }
        catch
        {
            // MUST NOT throw – crash logger must be silent on failure
        }
    }

    private static void SaveLastWorkDir(string wd)
    {
        try
        {
            File.WriteAllText(GetLastWorkDirPath(), wd);
        }
        catch { }
    }

    /// <summary>
    /// Shows error dialog NON-MODALLY (no nested Application.Run).
    /// Adds dialog to _win, Close button removes it.
    /// </summary>
    static void ShowErrorDialog(string title, string fullErrorText)
    {
        if (_win == null) return;

        var dlg = new Dialog
        {
            Title = $" ERROR: {title} ",
            Width = 75,
            Height = 20,
            X = Pos.Center(),
            Y = Pos.Center()
        };

        dlg.Add(new Label
        {
            Text = "Copy the error text below (Ctrl+C):",
            X = 1,
            Y = 0
        });

        var tv = new TextView
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill() - 2,
            Height = Dim.Fill() - 4,
            ReadOnly = true,
            WordWrap = true,
            Text = fullErrorText
        };
        dlg.Add(tv);

        var btnClose = new Button
        {
            Text = " Close ",
            X = Pos.Center(),
            Y = Pos.AnchorEnd(1)
        };
        btnClose.Accepting += (s, e) =>
        {
            _win.Remove(dlg);
            Application.Invoke(RefreshAll);
        };
        dlg.AddButton(btnClose);

        _win.Add(dlg);
    }

    static void FocusAgentList(int? idx)
    {
        if (_agentList != null && idx.HasValue && idx >= 0 && idx < _agentItems.Count)
            _agentList.SelectedItem = idx.Value;
    }
}