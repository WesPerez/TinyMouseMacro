namespace TinyMouseMacro;

public sealed class MainForm : Form
{
    private readonly MacroStore _store;
    private readonly MacroExecutor _executor = new();
    private readonly BindingSource _profilesSource = new();
    private readonly BindingSource _stepsSource = new();
    private readonly System.Windows.Forms.Timer _positionTimer = new() { Interval = 100 };
    private readonly System.Windows.Forms.Timer _scheduleTimer = new() { Interval = 10000 };
    private CancellationTokenSource? _executionCts;

    private HotkeyService? _hotkeys;
    private RecorderService? _recorder;
    private List<MacroProfile> _profiles = [];
    private bool _isBinding;
    private bool _dirty;
    private bool _globalPaused;
    private int _pauseHotkeyId = 999;
    private int _dragStepIndex = -1;

    private readonly ListBox _profileList = new();
    private readonly TextBox _nameBox = new();
    private readonly TextBox _hotkeyBox = new();
    private readonly ComboBox _triggerTypeBox = new();
    private readonly TextBox _hookKeyBox = new();
    private readonly ComboBox _mouseButtonBox = new();
    private readonly NumericUpDown _repeatCountBox = new() { Minimum = 0, Maximum = 9999, Increment = 1 };
    private readonly NumericUpDown _repeatIntervalBox = new() { Minimum = 0, Maximum = 600000, Increment = 100 };
    private readonly CheckBox _enabledCheckBox = new();
    private readonly TextBox _targetWindowBox = new();
    private readonly NumericUpDown _speedBox = new() { Minimum = 0.1m, Maximum = 10.0m, Increment = 0.1m, DecimalPlaces = 1 };
    private readonly NumericUpDown _scheduleIntervalBox = new() { Minimum = 0, Maximum = 1440, Increment = 1 };
    private readonly ListBox _stepList = new();
    private readonly Label _livePositionLabel = new();
    private readonly Label _statusLabel = new();
    private readonly TextBox _screenInfoBox = new();
    private readonly ListBox _logList = new();
    private readonly TextBox _searchBox = new();
    private readonly Button _cancelButton;
    private readonly NotifyIcon _trayIcon;
    private bool _reallyClosing;

    public MainForm()
    {
        Text = UiText.AppTitle;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(980, 758);
        Size = new Size(1100, 818);
        Font = new Font(Theme.UiFont, 9F);
        KeyPreview = true;
        Theme.ApplyToForm(this);

        var appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TinyMouseMacro");
        Directory.CreateDirectory(appDataDir);
        _store = new MacroStore(Path.Combine(appDataDir, "macros.json"));
        _positionTimer.Tick += (_, _) => UpdateLivePosition();
        _scheduleTimer.Tick += ScheduleTimer_Tick;

        var showItem = new ToolStripMenuItem(UiText.TrayShow);
        showItem.Click += (_, _) => ShowFromTray();

        var autoStartItem = new ToolStripMenuItem(UiText.TrayAutoStart);
        autoStartItem.Checked = AutoStart.IsEnabled();
        autoStartItem.Click += (_, _) =>
        {
            var enable = !autoStartItem.Checked;
            if (AutoStart.SetEnabled(enable))
            {
                autoStartItem.Checked = enable;
            }
        };

        var exitItem = new ToolStripMenuItem(UiText.TrayExit);
        exitItem.Click += (_, _) => { _reallyClosing = true; Close(); };

        _trayIcon = new NotifyIcon
        {
            Icon = TryLoadIcon(),
            Text = UiText.AppTitle,
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip()
        };
        _trayIcon.ContextMenuStrip.Items.Add(showItem);
        _trayIcon.ContextMenuStrip.Items.Add(autoStartItem);
        _trayIcon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        _trayIcon.ContextMenuStrip.Items.Add(exitItem);
        _trayIcon.MouseDoubleClick += (_, _) => ShowFromTray();

        _cancelButton = new Button
        {
            Text = UiText.CancelExecution,
            Width = 110,
            Height = 32,
            Visible = false,
            Margin = new Padding(4)
        };
        Theme.StyleDangerButton(_cancelButton);
        _cancelButton.Click += (_, _) => CancelExecution();

        BuildUi();
        Load += MainForm_Load;
        FormClosing += MainForm_FormClosing;

        _hotkeys = new HotkeyService(Handle);
        _hotkeys.HookTriggered += profile =>
        {
            if (_globalPaused) return;
            if (!IsDisposed && !Disposing)
            {
                BeginInvoke(() => RunMacro(profile));
            }
        };

        _executor.ChainMacroRequested += chainId =>
        {
            if (!IsDisposed && !Disposing)
            {
                BeginInvoke(() =>
                {
                    var target = _profiles.FirstOrDefault(p => p.Id == chainId);
                    if (target is not null && target.Enabled)
                    {
                        SetStatus(UiText.Chaining(target.Name));
                        _ = RunWithCancellationAsync(target);
                    }
                });
            }
        };

        _recorder = new RecorderService();
        _recorder.StepRecorded += step =>
        {
            if (!IsDisposed && !Disposing)
            {
                BeginInvoke(() =>
                {
                    var profile = CurrentProfile;
                    if (profile is null) return;
                    profile.Steps.Add(step);
                    RefreshSteps();
                    _stepList.SelectedItem = step;
                    MarkDirty();
                });
            }
        };

        KeyDown += MainForm_KeyDown;

        NativeMethods.RegisterHotKey(Handle, _pauseHotkeyId, NativeMethods.ModControl | NativeMethods.ModAlt | NativeMethods.ModNoRepeat, (uint)Keys.P);
    }

    private void MainForm_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.S)
        {
            e.Handled = true;
            SaveAndReload();
        }
        else if (e.KeyCode == Keys.Delete && _stepList.Focused)
        {
            e.Handled = true;
            DeleteStep();
        }
    }

    protected override void WndProc(ref Message m)
    {
        const int wmShowWindow = NativeMethods.WmUser + 1;

        if (m.Msg == wmShowWindow)
        {
            ShowFromTray();
            return;
        }

        if (m.Msg == NativeMethods.WmHotkey && m.WParam.ToInt32() == _pauseHotkeyId)
        {
            ToggleGlobalPause();
            return;
        }

        if (m.Msg == NativeMethods.WmHotkey && _hotkeys?.TryGetProfile(m.WParam.ToInt32(), out var profile) == true && profile is not null)
        {
            if (_globalPaused) return;
            RunMacro(profile);
            return;
        }

        base.WndProc(ref m);
    }

    private void MainForm_Load(object? sender, EventArgs e)
    {
        _profiles = _store.Load();
        _profilesSource.DataSource = _profiles;
        _profileList.DataSource = _profilesSource;
        _profileList.DisplayMember = nameof(MacroProfile.DisplayText);
        _profileList.SelectedIndex = _profiles.Count > 0 ? 0 : -1;

        RefreshScreenInfo();
        BindSelectedProfile();
        RebuildHotkeys();
        _positionTimer.Start();
        _scheduleTimer.Start();
    }

    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_reallyClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        CancelExecution();
        SaveAll();
        _scheduleTimer.Dispose();
        _positionTimer.Dispose();
        NativeMethods.UnregisterHotKey(Handle, _pauseHotkeyId);
        _hotkeys?.Dispose();
        _recorder?.Dispose();
        _trayIcon.Dispose();
    }

    private void ToggleGlobalPause()
    {
        _globalPaused = !_globalPaused;
        if (_globalPaused)
        {
            CancelExecution();
            SetStatus(UiText.GlobalPaused);
            _trayIcon.Text = UiText.AppTitle + UiText.PausedSuffix;
        }
        else
        {
            SetStatus(UiText.GlobalResumed);
            _trayIcon.Text = UiText.AppTitle;
        }
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(12)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 270));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        Theme.StylePanel(root);
        Controls.Add(root);

        root.Controls.Add(BuildProfilePanel(), 0, 0);

        var right = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(12, 0, 0, 0)
        };
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Theme.StylePanel(right);
        root.Controls.Add(right, 1, 0);

        right.Controls.Add(BuildProfileEditor(), 0, 0);
        right.Controls.Add(BuildStepList(), 0, 1);
        right.Controls.Add(BuildCaptureButtons(), 0, 2);
        right.Controls.Add(BuildLivePositionPanel(), 0, 3);
        right.Controls.Add(BuildBottomPanel(), 0, 4);

        var statusPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
        Theme.StylePanel(statusPanel);
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.AutoEllipsis = true;
        Theme.StyleStatusBar(_statusLabel);
        statusPanel.Controls.Add(_statusLabel, 0, 0);
        statusPanel.Controls.Add(_cancelButton, 1, 0);
        root.SetColumnSpan(statusPanel, 2);
        root.Controls.Add(statusPanel, 0, 1);
    }

    private Control BuildProfilePanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1 };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));
        Theme.StylePanel(panel);

        var header = new Label { Text = UiText.Profiles, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        Theme.StyleSectionHeader(header);
        panel.Controls.Add(header, 0, 0);

        _searchBox.Dock = DockStyle.Fill;
        _searchBox.PlaceholderText = UiText.SearchHint;
        _searchBox.TextChanged += (_, _) => FilterProfiles();
        Theme.StyleTextBox(_searchBox);
        panel.Controls.Add(_searchBox, 0, 1);

        _profileList.Dock = DockStyle.Fill;
        _profileList.IntegralHeight = false;
        _profileList.SelectedIndexChanged += (_, _) => BindSelectedProfile();
        Theme.StyleListBox(_profileList);
        panel.Controls.Add(_profileList, 0, 2);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
        Theme.StylePanel(buttons);
        buttons.Controls.Add(CreateButton(UiText.AddProfile, AddProfile, 118));
        buttons.Controls.Add(CreateButton(UiText.DuplicateProfile, DuplicateProfile, 118));
        buttons.Controls.Add(CreateButton(UiText.DeleteProfile, DeleteProfile, 118));
        buttons.Controls.Add(CreateButton(UiText.ImportProfile, ImportProfile, 118));
        buttons.Controls.Add(CreateButton(UiText.ExportProfile, ExportProfile, 118));
        buttons.Controls.Add(CreateButton(UiText.ToggleTheme, ToggleTheme, 118));
        buttons.Controls.Add(CreateButton(UiText.PickColor, PickPixelColor, 118));
        buttons.Controls.Add(CreateAccentButton(UiText.SaveEnable, SaveAndReload, 118));
        panel.Controls.Add(buttons, 0, 3);
        return panel;
    }

    private Control BuildProfileEditor()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 8, RowCount = 5 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        Theme.StylePanel(panel);

        AddEditorLabel(panel, UiText.Name, 0, 0);
        AddEditorLabel(panel, UiText.TriggerType, 2, 0);
        AddEditorLabel(panel, UiText.Hotkey, 4, 0);
        AddEditorLabel(panel, UiText.RepeatCount, 6, 0);

        _nameBox.Dock = DockStyle.Fill;
        _nameBox.TextChanged += (_, _) =>
        {
            if (_isBinding || CurrentProfile is null) return;
            CurrentProfile.Name = string.IsNullOrWhiteSpace(_nameBox.Text) ? UiText.Untitled : _nameBox.Text.Trim();
            _profilesSource.ResetBindings(false);
            MarkDirty();
        };
        Theme.StyleTextBox(_nameBox);

        _hotkeyBox.Dock = DockStyle.Fill;
        _hotkeyBox.ReadOnly = true;
        _hotkeyBox.PlaceholderText = UiText.HotkeyHint;
        _hotkeyBox.KeyDown += HotkeyBox_KeyDown;
        _hotkeyBox.PreviewKeyDown += (_, e) => e.IsInputKey = true;
        Theme.StyleTextBox(_hotkeyBox);

        _triggerTypeBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _triggerTypeBox.Items.AddRange([UiText.TriggerKeyboardHotkey, UiText.TriggerKeyboardHook, UiText.TriggerMouseButton]);
        _triggerTypeBox.SelectedIndex = 0;
        _triggerTypeBox.Dock = DockStyle.Fill;
        _triggerTypeBox.SelectedIndexChanged += TriggerTypeBox_SelectedIndexChanged;
        Theme.StyleComboBox(_triggerTypeBox);

        _hookKeyBox.Dock = DockStyle.Fill;
        _hookKeyBox.ReadOnly = true;
        _hookKeyBox.PlaceholderText = UiText.HookKeyHint;
        _hookKeyBox.KeyDown += HookKeyBox_KeyDown;
        _hookKeyBox.PreviewKeyDown += (_, e) => e.IsInputKey = true;
        _hookKeyBox.Visible = false;
        Theme.StyleTextBox(_hookKeyBox);

        _mouseButtonBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _mouseButtonBox.Items.AddRange([UiText.MouseX1, UiText.MouseX2, UiText.MouseMiddle]);
        _mouseButtonBox.SelectedIndex = 0;
        _mouseButtonBox.Dock = DockStyle.Fill;
        _mouseButtonBox.Visible = false;
        _mouseButtonBox.SelectedIndexChanged += (_, _) =>
        {
            if (_isBinding || CurrentProfile is null) return;
            CurrentProfile.TriggerMouseButton = _mouseButtonBox.SelectedIndex switch
            {
                0 => 4,
                1 => 5,
                2 => 3,
                _ => 0
            };
            MarkDirty();
        };
        Theme.StyleComboBox(_mouseButtonBox);

        _repeatCountBox.Dock = DockStyle.Fill;
        _repeatCountBox.ValueChanged += (_, _) =>
        {
            if (_isBinding || CurrentProfile is null) return;
            CurrentProfile.RepeatCount = (int)_repeatCountBox.Value;
            MarkDirty();
        };
        Theme.StyleNumericUpDown(_repeatCountBox);

        _repeatIntervalBox.Dock = DockStyle.Fill;
        _repeatIntervalBox.ValueChanged += (_, _) =>
        {
            if (_isBinding || CurrentProfile is null) return;
            CurrentProfile.RepeatIntervalMs = (int)_repeatIntervalBox.Value;
            MarkDirty();
        };
        Theme.StyleNumericUpDown(_repeatIntervalBox);

        panel.Controls.Add(_nameBox, 1, 1);
        panel.Controls.Add(_triggerTypeBox, 3, 1);
        panel.Controls.Add(_hotkeyBox, 5, 1);
        panel.Controls.Add(_repeatCountBox, 7, 1);

        var repeatIntervalLabel = new Label { Text = UiText.RepeatInterval, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        Theme.StyleLabel(repeatIntervalLabel);
        panel.Controls.Add(repeatIntervalLabel, 6, 2);
        panel.Controls.Add(_repeatIntervalBox, 7, 2);

        AddEditorLabel(panel, UiText.ScheduleInterval, 0, 3);
        _scheduleIntervalBox.Dock = DockStyle.Fill;
        _scheduleIntervalBox.ValueChanged += (_, _) =>
        {
            if (_isBinding || CurrentProfile is null) return;
            CurrentProfile.ScheduleIntervalMinutes = (int)_scheduleIntervalBox.Value;
            _profilesSource.ResetBindings(false);
            MarkDirty();
        };
        Theme.StyleNumericUpDown(_scheduleIntervalBox);
        panel.Controls.Add(_scheduleIntervalBox, 1, 3);

        AddEditorLabel(panel, UiText.Enabled, 0, 4);
        AddEditorLabel(panel, UiText.TargetWindow, 2, 4);
        AddEditorLabel(panel, UiText.SpeedMultiplier, 6, 4);

        _enabledCheckBox.Dock = DockStyle.Fill;
        _enabledCheckBox.Checked = true;
        _enabledCheckBox.CheckedChanged += (_, _) =>
        {
            if (_isBinding || CurrentProfile is null) return;
            CurrentProfile.Enabled = _enabledCheckBox.Checked;
            _profilesSource.ResetBindings(false);
            MarkDirty();
        };
        panel.Controls.Add(_enabledCheckBox, 1, 4);

        _targetWindowBox.Dock = DockStyle.Fill;
        _targetWindowBox.PlaceholderText = UiText.TargetWindowHint;
        _targetWindowBox.TextChanged += (_, _) =>
        {
            if (_isBinding || CurrentProfile is null) return;
            CurrentProfile.TargetWindowTitle = _targetWindowBox.Text;
            MarkDirty();
        };
        Theme.StyleTextBox(_targetWindowBox);
        panel.SetColumnSpan(_targetWindowBox, 3);
        panel.Controls.Add(_targetWindowBox, 3, 4);

        _speedBox.Dock = DockStyle.Fill;
        _speedBox.Value = 1.0m;
        _speedBox.ValueChanged += (_, _) =>
        {
            if (_isBinding || CurrentProfile is null) return;
            CurrentProfile.SpeedMultiplier = (double)_speedBox.Value;
            MarkDirty();
        };
        Theme.StyleNumericUpDown(_speedBox);
        panel.Controls.Add(_speedBox, 7, 4);

        panel.Controls.Add(_hookKeyBox, 5, 1);
        panel.Controls.Add(_mouseButtonBox, 5, 1);

        AddEditorLabel(panel, UiText.Enabled, 0, 2);

        return panel;
    }

    private static void AddEditorLabel(TableLayoutPanel panel, string text, int col, int row)
    {
        var label = new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        Theme.StyleLabel(label);
        panel.Controls.Add(label, col, row);
    }

    private Control BuildStepList()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Theme.StylePanel(panel);

        var header = new Label { Text = UiText.Steps, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        Theme.StyleSectionHeader(header);
        panel.Controls.Add(header, 0, 0);

        _stepList.Dock = DockStyle.Fill;
        _stepList.IntegralHeight = false;
        _stepList.DataSource = _stepsSource;
        _stepList.MouseDoubleClick += StepList_MouseDoubleClick;
        _stepList.MouseDown += StepList_MouseDown;
        _stepList.MouseMove += StepList_MouseMove;
        _stepList.DragOver += StepList_DragOver;
        _stepList.DragDrop += StepList_DragDrop;
        _stepList.AllowDrop = true;
        Theme.StyleListBox(_stepList);
        panel.Controls.Add(_stepList, 0, 1);
        return panel;
    }

    private Control BuildCaptureButtons()
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
        Theme.StylePanel(panel);
        panel.Controls.Add(CreateButton(UiText.CapturePointDelay, async () => await CapturePointAfterDelayAsync(false), 190));
        panel.Controls.Add(CreateButton(UiText.RecordNextClick, async () => await CapturePointAfterDelayAsync(true), 190));
        panel.Controls.Add(CreateButton(UiText.RecordRightClick, async () => await CapturePointAfterDelayAsync(false, MacroStepType.RightClick), 160));
        panel.Controls.Add(CreateButton(UiText.RecordDoubleClick, async () => await CapturePointAfterDelayAsync(false, MacroStepType.DoubleClick), 160));
        panel.Controls.Add(CreateButton(UiText.AddDelay, AddDelayStep, 140));
        panel.Controls.Add(CreateButton(UiText.AddRelativeMove, AddRelativeMoveStep, 140));
        panel.Controls.Add(CreateButton(UiText.AddKeyCombo, AddKeyComboStep, 140));
        panel.Controls.Add(CreateButton(UiText.AddTypeText, AddTypeTextStep, 140));
        panel.Controls.Add(CreateButton(UiText.AddWaitPixel, AddWaitPixelStep, 140));
        panel.Controls.Add(CreateButton(UiText.AddFindPixel, AddFindPixelStep, 140));
        panel.Controls.Add(CreateButton(UiText.AddScreenshot, AddScreenshotStep, 140));
        panel.Controls.Add(CreateAccentButton(UiText.StartRecording, ToggleRecording, 120));
        panel.Controls.Add(CreateButton(UiText.DuplicateStep, DuplicateStep, 120));
        panel.Controls.Add(CreateButton(UiText.MoveUp, MoveStepUp, 104));
        panel.Controls.Add(CreateButton(UiText.MoveDown, MoveStepDown, 104));
        panel.Controls.Add(CreateButton(UiText.DeleteStep, DeleteStep, 104));
        panel.Controls.Add(CreateAccentButton(UiText.TestRun, TestRun, 104));
        return panel;
    }

    private Control BuildLivePositionPanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Theme.StylePanel(panel);

        var label = new Label { Text = UiText.LivePosition, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        Theme.StyleLabel(label);
        panel.Controls.Add(label, 0, 0);

        _livePositionLabel.Dock = DockStyle.Fill;
        _livePositionLabel.TextAlign = ContentAlignment.MiddleLeft;
        Theme.StyleMonoLabel(_livePositionLabel);
        panel.Controls.Add(_livePositionLabel, 1, 0);
        return panel;
    }

    private Control BuildScreenInfo()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Theme.StylePanel(panel);

        var header = new Label { Text = UiText.ScreenInfo, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        Theme.StyleSectionHeader(header);
        panel.Controls.Add(header, 0, 0);

        _screenInfoBox.Dock = DockStyle.Fill;
        _screenInfoBox.Multiline = true;
        _screenInfoBox.ReadOnly = true;
        _screenInfoBox.ScrollBars = ScrollBars.None;
        Theme.StyleTextBox(_screenInfoBox);
        _screenInfoBox.Font = new Font(Theme.MonoFont, 8.5F);
        panel.Controls.Add(_screenInfoBox, 0, 1);
        return panel;
    }

    private Control BuildBottomPanel()
    {
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 120,
            Panel1MinSize = 60,
            Panel2MinSize = 60
        };
        Theme.StylePanel(split.Panel1);
        Theme.StylePanel(split.Panel2);
        split.Panel1.Controls.Add(BuildScreenInfo());
        split.Panel2.Controls.Add(BuildLogPanel());
        return split;
    }

    private Control BuildLogPanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Theme.StylePanel(panel);

        var header = new Label { Text = UiText.ExecutionLog, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        Theme.StyleSectionHeader(header);
        panel.Controls.Add(header, 0, 0);

        _logList.Dock = DockStyle.Fill;
        _logList.IntegralHeight = false;
        Theme.StyleListBox(_logList);
        panel.Controls.Add(_logList, 0, 1);
        return panel;
    }

    private Button CreateButton(string text, Action action, int width)
    {
        var button = new Button { Text = text, Width = width, Height = 32, Margin = new Padding(4) };
        Theme.StyleButton(button);
        button.Click += (_, _) => action();
        return button;
    }

    private Button CreateAccentButton(string text, Action action, int width)
    {
        var button = new Button { Text = text, Width = width, Height = 32, Margin = new Padding(4) };
        Theme.StyleAccentButton(button);
        button.Click += (_, _) => action();
        return button;
    }

    private MacroProfile? CurrentProfile => _profileList.SelectedItem as MacroProfile;
    private MacroStep? CurrentStep => _stepList.SelectedItem as MacroStep;

    private void HotkeyBox_KeyDown(object? sender, KeyEventArgs e)
    {
        e.Handled = true;
        var hotkey = HotkeyParser.FromKeyEvent(e);
        if (string.IsNullOrWhiteSpace(hotkey))
        {
            return;
        }

        _hotkeyBox.Text = hotkey;
        if (CurrentProfile is not null)
        {
            if (IsHotkeyUsedByAnotherProfile(CurrentProfile, hotkey))
            {
                SetStatus(UiText.DuplicateHotkey(hotkey));
                return;
            }

            CurrentProfile.Hotkey = hotkey;
            MarkDirty();
        }
    }

    private void TriggerTypeBox_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_isBinding || CurrentProfile is null) return;

        CurrentProfile.TriggerType = _triggerTypeBox.SelectedIndex switch
        {
            0 => MacroTriggerType.KeyboardHotkey,
            1 => MacroTriggerType.KeyboardHook,
            2 => MacroTriggerType.MouseButton,
            _ => MacroTriggerType.KeyboardHotkey
        };

        _hotkeyBox.Visible = CurrentProfile.TriggerType == MacroTriggerType.KeyboardHotkey;
        _hookKeyBox.Visible = CurrentProfile.TriggerType == MacroTriggerType.KeyboardHook;
        _mouseButtonBox.Visible = CurrentProfile.TriggerType == MacroTriggerType.MouseButton;
        MarkDirty();
    }

    private void HookKeyBox_KeyDown(object? sender, KeyEventArgs e)
    {
        e.Handled = true;
        var keyCode = e.KeyCode;
        if (HotkeyParser.IsModifierKey(keyCode)) return;

        _hookKeyBox.Text = keyCode.ToString();
        if (CurrentProfile is not null)
        {
            CurrentProfile.TriggerKey = (int)keyCode;
            MarkDirty();
        }
    }

    private void BindSelectedProfile()
    {
        _isBinding = true;
        try
        {
            var profile = CurrentProfile;
            _nameBox.Text = profile?.Name ?? string.Empty;
            _hotkeyBox.Text = profile?.Hotkey ?? string.Empty;
            _triggerTypeBox.SelectedIndex = profile?.TriggerType switch
            {
                MacroTriggerType.KeyboardHotkey => 0,
                MacroTriggerType.KeyboardHook => 1,
                MacroTriggerType.MouseButton => 2,
                _ => 0
            };
            _hookKeyBox.Text = profile?.TriggerKey > 0 ? ((Keys)profile.TriggerKey).ToString() : string.Empty;
            _mouseButtonBox.SelectedIndex = profile?.TriggerMouseButton switch
            {
                4 => 0,
                5 => 1,
                3 => 2,
                _ => 0
            };
            _repeatCountBox.Value = Math.Min(_repeatCountBox.Maximum, Math.Max(_repeatCountBox.Minimum, profile?.RepeatCount ?? 1));
            _repeatIntervalBox.Value = Math.Min(_repeatIntervalBox.Maximum, Math.Max(_repeatIntervalBox.Minimum, profile?.RepeatIntervalMs ?? 0));
            _enabledCheckBox.Checked = profile?.Enabled ?? true;
            _scheduleIntervalBox.Value = Math.Min(_scheduleIntervalBox.Maximum, Math.Max(_scheduleIntervalBox.Minimum, profile?.ScheduleIntervalMinutes ?? 0));
            _targetWindowBox.Text = profile?.TargetWindowTitle ?? string.Empty;
            _speedBox.Value = Math.Min(_speedBox.Maximum, Math.Max(_speedBox.Minimum, (decimal)(profile?.SpeedMultiplier ?? 1.0)));

            _hotkeyBox.Visible = profile?.TriggerType == MacroTriggerType.KeyboardHotkey;
            _hookKeyBox.Visible = profile?.TriggerType == MacroTriggerType.KeyboardHook;
            _mouseButtonBox.Visible = profile?.TriggerType == MacroTriggerType.MouseButton;

            _stepsSource.DataSource = profile?.Steps ?? [];
            _stepsSource.ResetBindings(false);
        }
        finally
        {
            _isBinding = false;
        }
    }

    private void StepList_MouseDoubleClick(object? sender, MouseEventArgs e)
    {
        var step = CurrentStep;
        if (step is null) return;

        using var dialog = new StepEditDialog(step);
        dialog.StartPosition = FormStartPosition.CenterParent;
        dialog.Font = new Font(Theme.UiFont, 9F);
        Theme.ApplyToForm(dialog);

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            step.Type = dialog.StepType;
            step.X = dialog.X;
            step.Y = dialog.Y;
            step.DelayMs = dialog.DelayMs;
            step.DragEndX = dialog.DragEndX;
            step.DragEndY = dialog.DragEndY;
            step.WheelDelta = dialog.WheelDelta;
            step.KeyCode = step.Type == MacroStepType.KeyCombo ? dialog.KeyComboCode : dialog.KeyCode;
            step.KeyModifiers = dialog.KeyModifiers;
            step.TextToType = dialog.TextToType;
            step.PixelColor = dialog.PixelColor;
            step.PixelTolerance = dialog.PixelTolerance;
            step.PixelTimeoutMs = dialog.PixelTimeoutMs;
            step.SearchWidth = dialog.SearchWidth;
            step.SearchHeight = dialog.SearchHeight;
            step.DelayMsMax = dialog.DelayMsMax;
            step.JumpToStepIndex = dialog.JumpToStepIndex;
            step.RunProgramPath = dialog.RunProgramPath;
            step.RunProgramArgs = dialog.RunProgramArgs;
            step.SoundFilePath = dialog.SoundFilePath;
            step.ChainMacroId = dialog.ChainMacroId;
            RefreshSteps();
            _stepList.SelectedItem = step;
            MarkDirty();
            SetStatus(UiText.EditedStep(step.Display));
        }
    }

    private void StepList_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _dragStepIndex = _stepList.IndexFromPoint(e.Location);
        if (_dragStepIndex < 0) return;
        _stepList.DoDragDrop(_stepList.Items[_dragStepIndex], DragDropEffects.Move);
    }

    private void StepList_MouseMove(object? sender, MouseEventArgs e)
    {
    }

    private void StepList_DragOver(object? sender, DragEventArgs e)
    {
        e.Effect = DragDropEffects.Move;
    }

    private void StepList_DragDrop(object? sender, DragEventArgs e)
    {
        var profile = CurrentProfile;
        if (profile is null || _dragStepIndex < 0) return;

        var point = _stepList.PointToClient(new Point(e.X, e.Y));
        var targetIndex = _stepList.IndexFromPoint(point);
        if (targetIndex < 0 || targetIndex >= profile.Steps.Count || targetIndex == _dragStepIndex) return;

        var step = profile.Steps[_dragStepIndex];
        profile.Steps.RemoveAt(_dragStepIndex);
        profile.Steps.Insert(targetIndex, step);
        RefreshSteps();
        _stepList.SelectedItem = step;
        MarkDirty();
        _dragStepIndex = -1;
    }

    private void AddProfile()
    {
        var profile = new MacroProfile { Name = $"{UiText.Macro} {_profiles.Count + 1}", Hotkey = GetNextDefaultHotkey(), Steps = [] };
        _profiles.Add(profile);
        _profilesSource.ResetBindings(false);
        _profileList.SelectedItem = profile;
        MarkDirty();
        SetStatus(UiText.AddedProfile);
    }

    private void DuplicateProfile()
    {
        var current = CurrentProfile;
        if (current is null) return;

        var copy = new MacroProfile
        {
            Name = current.Name + UiText.CopySuffix,
            Hotkey = GetNextDefaultHotkey(),
            Steps = current.Steps.Select(CloneStep).ToList()
        };

        _profiles.Add(copy);
        _profilesSource.ResetBindings(false);
        _profileList.SelectedItem = copy;
        MarkDirty();
        SetStatus(UiText.DuplicatedProfile);
    }

    private void DeleteProfile()
    {
        var current = CurrentProfile;
        if (current is null) return;

        _profiles.Remove(current);
        if (_profiles.Count == 0)
        {
            _profiles.Add(new MacroProfile { Name = $"{UiText.Macro} 1", Hotkey = GetNextDefaultHotkey() });
        }

        _profilesSource.ResetBindings(false);
        _profileList.SelectedIndex = 0;
        MarkDirty();
        SetStatus(UiText.DeletedProfile);
    }

    private void ImportProfile()
    {
        using var dialog = new OpenFileDialog
        {
            Title = UiText.ImportTitle,
            Filter = "JSON (*.json)|*.json|All (*.*)|*.*",
            DefaultExt = "json"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var json = File.ReadAllText(dialog.FileName);
            var imported = System.Text.Json.JsonSerializer.Deserialize<List<MacroProfile>>(json);
            if (imported is not { Count: > 0 })
            {
                SetStatus(UiText.ImportEmpty);
                return;
            }

            foreach (var profile in imported)
            {
                profile.Id = Guid.NewGuid().ToString("N");
                if (string.IsNullOrWhiteSpace(profile.Hotkey))
                    profile.Hotkey = GetNextDefaultHotkey();
                _profiles.Add(profile);
            }

            _profilesSource.ResetBindings(false);
            _profileList.SelectedIndex = _profiles.Count - 1;
            MarkDirty();
            SetStatus(UiText.Imported(imported.Count));
        }
        catch (Exception ex)
        {
            SetStatus(UiText.ImportFailed(ex.Message));
        }
    }

    private void ExportProfile()
    {
        var current = CurrentProfile;
        if (current is null) return;

        using var dialog = new SaveFileDialog
        {
            Title = UiText.ExportTitle,
            Filter = "JSON (*.json)|*.json",
            DefaultExt = "json",
            FileName = $"{current.Name}.json"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new[] { current }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(dialog.FileName, json);
            SetStatus(UiText.Exported(dialog.FileName));
        }
        catch (Exception ex)
        {
            SetStatus(UiText.ExportFailed(ex.Message));
        }
    }

    private void ToggleTheme()
    {
        Theme.Toggle();
        Theme.ApplyToForm(this);
        foreach (Control control in Controls)
        {
            ReapplyTheme(control);
        }
        _profileList.Invalidate();
        SetStatus(Theme.IsDark ? UiText.DarkTheme : UiText.LightTheme);
    }

    private void ScheduleTimer_Tick(object? sender, EventArgs e)
    {
        if (_globalPaused || _executor.IsRunning) return;
        var now = DateTime.Now;
        foreach (var profile in _profiles)
        {
            if (!profile.Enabled || profile.ScheduleIntervalMinutes <= 0) continue;
            var next = profile.ScheduleNextRun;
            if (next is null || now >= next.Value)
            {
                profile.ScheduleNextRun = now.AddMinutes(profile.ScheduleIntervalMinutes);
                RunMacro(profile);
                break;
            }
        }
    }

    private static void ReapplyTheme(Control control)
    {
        if (control is Button btn)
        {
            Theme.StyleButton(btn);
        }
        else if (control is TextBoxBase tb)
        {
            Theme.StyleTextBox(tb);
        }
        else if (control is ListBox lb)
        {
            Theme.StyleListBox(lb);
        }
        else if (control is ComboBox cb)
        {
            Theme.StyleComboBox(cb);
        }
        else if (control is NumericUpDown nud)
        {
            Theme.StyleNumericUpDown(nud);
        }
        else if (control is Label lbl)
        {
            Theme.StyleLabel(lbl);
        }
        else if (control is TableLayoutPanel or FlowLayoutPanel or Panel)
        {
            Theme.StylePanel(control);
        }

        foreach (Control child in control.Controls)
        {
            ReapplyTheme(child);
        }
    }

    private async void PickPixelColor()
    {
        SetStatus(UiText.PickColorHint);
        await Task.Delay(500);

        var step = CurrentStep;
        if (step is null) return;

        var dc = NativeMethods.GetDC(0);
        try
        {
            var point = Cursor.Position;
            var color = (int)NativeMethods.GetPixel(dc, point.X, point.Y);
            step.PixelColor = color;
            SetStatus(UiText.PickedColor(point.X, point.Y, color));
            MarkDirty();
            RefreshSteps();
        }
        finally
        {
            NativeMethods.ReleaseDC(0, dc);
        }
    }

    private async Task CapturePointAfterDelayAsync(bool addClickStep, MacroStepType stepType = MacroStepType.LeftClick)
    {
        for (var i = 3; i >= 1; i--)
        {
            SetStatus(UiText.CountdownCapture(i));
            await Task.Delay(1000);
        }

        var point = Cursor.Position;

        if (addClickStep)
        {
            AddStep(new MacroStep { Type = stepType, X = point.X, Y = point.Y });
            SetStatus(UiText.RecordedClick(point.X, point.Y));
        }
        else
        {
            AddStep(new MacroStep { Type = stepType == MacroStepType.LeftClick ? MacroStepType.Move : stepType, X = point.X, Y = point.Y });
            SetStatus(UiText.CapturedPoint(point.X, point.Y));
        }
    }

    private void AddDelayStep()
    {
        AddStep(new MacroStep { Type = MacroStepType.Delay, DelayMs = 500 });
    }

    private void AddRelativeMoveStep()
    {
        AddStep(new MacroStep { Type = MacroStepType.MoveRelative, X = 0, Y = 0 });
    }

    private void AddKeyComboStep()
    {
        AddStep(new MacroStep { Type = MacroStepType.KeyCombo, KeyCode = (int)Keys.C, KeyModifiers = NativeMethods.ModControl });
    }

    private void AddTypeTextStep()
    {
        AddStep(new MacroStep { Type = MacroStepType.TypeText, TextToType = "Hello" });
    }

    private void AddWaitPixelStep()
    {
        var point = Cursor.Position;
        AddStep(new MacroStep { Type = MacroStepType.WaitPixel, X = point.X, Y = point.Y, PixelColor = 0xFFFFFF, PixelTolerance = 10, PixelTimeoutMs = 5000 });
    }

    private void AddFindPixelStep()
    {
        var point = Cursor.Position;
        AddStep(new MacroStep { Type = MacroStepType.FindPixel, X = point.X, Y = point.Y, SearchWidth = 100, SearchHeight = 100, PixelColor = 0xFFFFFF, PixelTolerance = 10, PixelTimeoutMs = 5000 });
    }

    private void AddScreenshotStep()
    {
        AddStep(new MacroStep { Type = MacroStepType.Screenshot, X = 0, Y = 0, SearchWidth = 1920, SearchHeight = 1080 });
    }

    private void ToggleRecording()
    {
        if (_recorder is null) return;

        if (_recorder.IsRecording)
        {
            var steps = _recorder.Stop();
            var profile = CurrentProfile;
            if (profile is not null)
            {
                profile.Steps.AddRange(steps);
                RefreshSteps();
                MarkDirty();
            }
            SetStatus(UiText.RecordingStopped(steps.Count));
        }
        else
        {
            _recorder.Start();
            SetStatus(UiText.RecordingStarted);
        }
    }

    private void AddStep(MacroStep step)
    {
        var profile = CurrentProfile;
        if (profile is null) return;

        profile.Steps.Add(step);
        RefreshSteps();
        _stepList.SelectedItem = step;
        MarkDirty();
        SetStatus(UiText.AddedStep(step.Display));
    }

    private void MoveStepUp() => MoveStep(-1);
    private void MoveStepDown() => MoveStep(1);

    private void MoveStep(int direction)
    {
        var profile = CurrentProfile;
        var step = CurrentStep;
        if (profile is null || step is null) return;

        var oldIndex = profile.Steps.IndexOf(step);
        var newIndex = oldIndex + direction;
        if (oldIndex < 0 || newIndex < 0 || newIndex >= profile.Steps.Count) return;

        profile.Steps.RemoveAt(oldIndex);
        profile.Steps.Insert(newIndex, step);
        RefreshSteps();
        _stepList.SelectedItem = step;
        MarkDirty();
    }

    private void DeleteStep()
    {
        var profile = CurrentProfile;
        var step = CurrentStep;
        if (profile is null || step is null) return;

        profile.Steps.Remove(step);
        RefreshSteps();
        MarkDirty();
        SetStatus(UiText.DeletedStep);
    }

    private void DuplicateStep()
    {
        var profile = CurrentProfile;
        var step = CurrentStep;
        if (profile is null || step is null) return;

        var index = profile.Steps.IndexOf(step);
        var copy = CloneStep(step);
        profile.Steps.Insert(index + 1, copy);
        RefreshSteps();
        _stepList.SelectedItem = copy;
        MarkDirty();
        SetStatus(UiText.DuplicatedStep);
    }

    private void FilterProfiles()
    {
        var filter = _searchBox.Text.Trim();
        if (string.IsNullOrEmpty(filter))
        {
            _profilesSource.DataSource = _profiles;
        }
        else
        {
            _profilesSource.DataSource = _profiles
                .Where(p => p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        _profilesSource.ResetBindings(false);
        if (_profileList.Items.Count > 0)
            _profileList.SelectedIndex = 0;
    }

    private void TestRun()
    {
        var profile = CurrentProfile;
        if (profile is null) return;

        if (_executor.IsRunning)
        {
            SetStatus(UiText.AlreadyRunning);
            return;
        }

        SetStatus(UiText.Testing(profile.Name));
        _ = RunWithCancellationAsync(profile);
    }

    private void SaveAndReload()
    {
        SaveAll();
        RebuildHotkeys();
    }

    private void SaveAll()
    {
        if (!_dirty) return;
        _store.Save(_profiles);
        _dirty = false;
    }

    private void MarkDirty()
    {
        _dirty = true;
    }

    private void RebuildHotkeys()
    {
        if (_hotkeys is null) return;
        var errors = _hotkeys.RegisterAll(_profiles);
        SetStatus(errors.Count == 0 ? UiText.Ready(_hotkeys.RegisteredCount, _store.FilePath) : string.Join(" | ", errors));
    }

    private void RunMacro(MacroProfile profile)
    {
        if (_executor.IsRunning)
        {
            SetStatus(UiText.AlreadyRunning);
            return;
        }

        SetStatus(UiText.Running(profile.Name));
        Log(UiText.Running(profile.Name));
        _ = RunWithCancellationAsync(profile);
    }

    private async Task RunWithCancellationAsync(MacroProfile profile)
    {
        CancelExecution();
        _executionCts = new CancellationTokenSource();
        _cancelButton.Visible = true;

        try
        {
            await _executor.RunAsync(profile, _executionCts.Token);
            if (!IsDisposed)
            {
                BeginInvoke(() =>
                {
                    _cancelButton.Visible = false;
                    SetStatus(UiText.Finished(profile.Name));
                    Log(UiText.Finished(profile.Name));
                });
            }
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed)
            {
                BeginInvoke(() =>
                {
                    _cancelButton.Visible = false;
                    SetStatus(UiText.Cancelled(profile.Name));
                    Log(UiText.Cancelled(profile.Name));
                });
            }
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
            {
                BeginInvoke(() =>
                {
                    _cancelButton.Visible = false;
                    SetStatus(ex.GetBaseException().Message);
                    Log($"{UiText.Error}: {ex.GetBaseException().Message}");
                });
            }
        }
        finally
        {
            _executionCts?.Dispose();
            _executionCts = null;
        }
    }

    private void CancelExecution()
    {
        _executionCts?.Cancel();
    }

    private void RefreshSteps()
    {
        var selected = CurrentStep;
        _stepsSource.DataSource = null;
        _stepsSource.DataSource = CurrentProfile?.Steps ?? [];
        _stepsSource.ResetBindings(false);
        if (selected is not null)
        {
            _stepList.SelectedItem = selected;
        }
    }

    private void RefreshScreenInfo()
    {
        var virtualScreen = SystemInformation.VirtualScreen;
        var lines = new List<string>
        {
            $"Virtual: X={virtualScreen.X}, Y={virtualScreen.Y}, W={virtualScreen.Width}, H={virtualScreen.Height}",
            $"Mouse: X={Cursor.Position.X}, Y={Cursor.Position.Y}",
            UiText.AdminBoundary
        };

        for (var i = 0; i < Screen.AllScreens.Length; i++)
        {
            var screen = Screen.AllScreens[i];
            var bounds = screen.Bounds;
            lines.Add($"Screen {i + 1}{(screen.Primary ? " Primary" : "")}: X={bounds.X}, Y={bounds.Y}, W={bounds.Width}, H={bounds.Height}");
        }

        _screenInfoBox.Text = string.Join(Environment.NewLine, lines);
    }

    private void UpdateLivePosition()
    {
        var point = Cursor.Position;
        _livePositionLabel.Text = $"X={point.X}, Y={point.Y}";
    }

    private void SetStatus(string message)
    {
        _statusLabel.Text = message;
    }

    private void Log(string message)
    {
        if (IsDisposed || Disposing) return;
        BeginInvoke(() =>
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            _logList.Items.Insert(0, $"[{timestamp}] {message}");
            while (_logList.Items.Count > 200)
                _logList.Items.RemoveAt(_logList.Items.Count - 1);
        });
    }

    private static MacroStep CloneStep(MacroStep step)
    {
        return new MacroStep
        {
            Type = step.Type,
            X = step.X,
            Y = step.Y,
            DelayMs = step.DelayMs,
            DragEndX = step.DragEndX,
            DragEndY = step.DragEndY,
            WheelDelta = step.WheelDelta,
            KeyCode = step.KeyCode,
            KeyModifiers = step.KeyModifiers,
            TextToType = step.TextToType,
            PixelColor = step.PixelColor,
            PixelTolerance = step.PixelTolerance,
            PixelTimeoutMs = step.PixelTimeoutMs,
            SearchWidth = step.SearchWidth,
            SearchHeight = step.SearchHeight,
            DelayMsMax = step.DelayMsMax,
            JumpToStepIndex = step.JumpToStepIndex,
            RunProgramPath = step.RunProgramPath,
            RunProgramArgs = step.RunProgramArgs,
            SoundFilePath = step.SoundFilePath,
            ChainMacroId = step.ChainMacroId
        };
    }

    private static Icon TryLoadIcon()
    {
        try
        {
            var exePath = Application.ExecutablePath;
            if (File.Exists(exePath) && exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return Icon.ExtractAssociatedIcon(exePath)!;
        }
        catch
        {
        }

        return SystemIcons.Application;
    }

    private string GetNextDefaultHotkey()
    {
        var used = _profiles.Select(static profile => profile.Hotkey.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = new[] { "Alt+Z", "Alt+X", "Alt+C", "Alt+V", "Ctrl+Alt+Z", "Ctrl+Alt+X", "Ctrl+Alt+C", "Ctrl+Alt+V" };
        foreach (var candidate in candidates)
        {
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }

        for (var i = 1; i <= 12; i++)
        {
            var candidate = $"Ctrl+Alt+F{i}";
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    private bool IsHotkeyUsedByAnotherProfile(MacroProfile currentProfile, string hotkey)
    {
        if (!HotkeyParser.TryParse(hotkey, out var modifiers, out var key, out _))
        {
            return false;
        }

        var normalized = HotkeyParser.Normalize(modifiers, key);
        foreach (var profile in _profiles)
        {
            if (profile.Id == currentProfile.Id || !HotkeyParser.TryParse(profile.Hotkey, out var otherModifiers, out var otherKey, out _))
            {
                continue;
            }

            if (HotkeyParser.Normalize(otherModifiers, otherKey).Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

internal sealed class StepEditDialog : Form
{
    private readonly ComboBox _typeBox = new();
    private readonly NumericUpDown _xBox = new() { Minimum = -100000, Maximum = 100000 };
    private readonly NumericUpDown _yBox = new() { Minimum = -100000, Maximum = 100000 };
    private readonly NumericUpDown _delayBox = new() { Minimum = 0, Maximum = 600000, Increment = 50 };
    private readonly NumericUpDown _dragEndXBox = new() { Minimum = -100000, Maximum = 100000 };
    private readonly NumericUpDown _dragEndYBox = new() { Minimum = -100000, Maximum = 100000 };
    private readonly NumericUpDown _wheelDeltaBox = new() { Minimum = -12000, Maximum = 12000, Increment = 120 };
    private readonly ComboBox _keyPressBox = new();
    private readonly ComboBox _keyComboBox = new();
    private readonly CheckedListBox _modifierCheckList = new();
    private readonly TextBox _typeTextBox = new();
    private readonly NumericUpDown _pixelColorBox = new() { Minimum = 0, Maximum = 0xFFFFFF, Hexadecimal = true };
    private readonly NumericUpDown _pixelToleranceBox = new() { Minimum = 0, Maximum = 255, Increment = 5 };
    private readonly NumericUpDown _pixelTimeoutBox = new() { Minimum = 0, Maximum = 60000, Increment = 500 };
    private readonly NumericUpDown _searchWBox = new() { Minimum = 1, Maximum = 3840 };
    private readonly NumericUpDown _searchHBox = new() { Minimum = 1, Maximum = 2160 };
    private readonly NumericUpDown _delayMaxBox = new() { Minimum = 0, Maximum = 600000, Increment = 50 };
    private readonly NumericUpDown _jumpToStepBox = new() { Minimum = 0, Maximum = 9999 };
    private readonly TextBox _runProgramPathBox = new();
    private readonly TextBox _runProgramArgsBox = new();
    private readonly TextBox _soundFilePathBox = new();
    private readonly TextBox _chainMacroIdBox = new();
    private readonly Label _xLabel;
    private readonly Label _yLabel;
    private readonly Label _delayLabel;
    private readonly Label _dragEndXLabel;
    private readonly Label _dragEndYLabel;
    private readonly Label _wheelLabel;
    private readonly Label _keyLabel;
    private readonly Label _modifierLabel;
    private readonly Label _textLabel;
    private readonly Label _pixelColorLabel;
    private readonly Label _pixelToleranceLabel;
    private readonly Label _pixelTimeoutLabel;
    private readonly Label _searchWLabel;
    private readonly Label _searchHLabel;
    private readonly Label _delayMaxLabel;
    private readonly Label _jumpToStepLabel;
    private readonly Label _runProgramPathLabel;
    private readonly Label _runProgramArgsLabel;
    private readonly Label _soundFileLabel;
    private readonly Label _chainMacroIdLabel;

    public MacroStepType StepType => _typeBox.SelectedIndex switch
    {
        0 => MacroStepType.Move,
        1 => MacroStepType.LeftClick,
        2 => MacroStepType.RightClick,
        3 => MacroStepType.DoubleClick,
        4 => MacroStepType.MiddleClick,
        5 => MacroStepType.Delay,
        6 => MacroStepType.KeyPress,
        7 => MacroStepType.KeyCombo,
        8 => MacroStepType.TypeText,
        9 => MacroStepType.MouseWheel,
        10 => MacroStepType.Drag,
        11 => MacroStepType.MoveRelative,
        12 => MacroStepType.WaitPixel,
        13 => MacroStepType.FindPixel,
        14 => MacroStepType.Screenshot,
        15 => MacroStepType.RandomDelay,
        16 => MacroStepType.JumpIfPixel,
        17 => MacroStepType.RunProgram,
        18 => MacroStepType.PlaySound,
        19 => MacroStepType.ChainMacro,
        _ => MacroStepType.Move
    };

    public int X => (int)_xBox.Value;
    public int Y => (int)_yBox.Value;
    public int DelayMs => (int)_delayBox.Value;
    public int DragEndX => (int)_dragEndXBox.Value;
    public int DragEndY => (int)_dragEndYBox.Value;
    public int WheelDelta => (int)_wheelDeltaBox.Value;
    public int KeyCode => _keyPressBox.SelectedIndex >= 0 ? (int)(Keys)_keyPressBox.SelectedItem! : 0;
    public int KeyComboCode => _keyComboBox.SelectedIndex >= 0 ? (int)(Keys)_keyComboBox.SelectedItem! : 0;
    public uint KeyModifiers
    {
        get
        {
            uint mods = 0;
            for (var i = 0; i < _modifierCheckList.Items.Count; i++)
            {
                if (_modifierCheckList.GetItemChecked(i))
                {
                    mods |= i switch
                    {
                        0 => NativeMethods.ModControl,
                        1 => NativeMethods.ModAlt,
                        2 => NativeMethods.ModShift,
                        3 => NativeMethods.ModWin,
                        _ => 0
                    };
                }
            }
            return mods;
        }
    }
    public string TextToType => _typeTextBox.Text;
    public int PixelColor => (int)_pixelColorBox.Value;
    public int PixelTolerance => (int)_pixelToleranceBox.Value;
    public int PixelTimeoutMs => (int)_pixelTimeoutBox.Value;
    public int SearchWidth => (int)_searchWBox.Value;
    public int SearchHeight => (int)_searchHBox.Value;
    public int DelayMsMax => (int)_delayMaxBox.Value;
    public int JumpToStepIndex => (int)_jumpToStepBox.Value;
    public string RunProgramPath => _runProgramPathBox.Text;
    public string RunProgramArgs => _runProgramArgsBox.Text;
    public string SoundFilePath => _soundFilePathBox.Text;
    public string ChainMacroId => _chainMacroIdBox.Text;

    public StepEditDialog(MacroStep step)
    {
        Text = UiText.EditStepTitle;
        Size = new Size(420, 560);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        KeyPreview = true;
        AutoScroll = true;

        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, RowCount = 23 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 22; i++)
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(panel);

        _typeBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _typeBox.Items.AddRange([
            UiText.TypeMove, UiText.TypeLeftClick, UiText.TypeRightClick,
            UiText.TypeDoubleClick, UiText.TypeMiddleClick, UiText.TypeDelay,
            UiText.TypeKeyPress, UiText.TypeKeyCombo, UiText.TypeTypeText,
            UiText.TypeMouseWheel, UiText.TypeDrag, UiText.TypeMoveRelative,
            UiText.TypeWaitPixel, UiText.TypeFindPixel, UiText.TypeScreenshot,
            UiText.TypeRandomDelay, UiText.TypeJumpIfPixel, UiText.TypeRunProgram,
            UiText.TypePlaySound, UiText.TypeChainMacro
        ]);
        _typeBox.SelectedIndex = step.Type switch
        {
            MacroStepType.Move => 0,
            MacroStepType.LeftClick => 1,
            MacroStepType.RightClick => 2,
            MacroStepType.DoubleClick => 3,
            MacroStepType.MiddleClick => 4,
            MacroStepType.Delay => 5,
            MacroStepType.KeyPress => 6,
            MacroStepType.KeyCombo => 7,
            MacroStepType.TypeText => 8,
            MacroStepType.MouseWheel => 9,
            MacroStepType.Drag => 10,
            MacroStepType.MoveRelative => 11,
            MacroStepType.WaitPixel => 12,
            MacroStepType.FindPixel => 13,
            MacroStepType.Screenshot => 14,
            MacroStepType.RandomDelay => 15,
            MacroStepType.JumpIfPixel => 16,
            MacroStepType.RunProgram => 17,
            MacroStepType.PlaySound => 18,
            MacroStepType.ChainMacro => 19,
            _ => 0
        };
        _typeBox.Dock = DockStyle.Fill;
        _typeBox.SelectedIndexChanged += (_, _) => UpdateFieldVisibility();
        Theme.StyleComboBox(_typeBox);

        _xBox.Value = Math.Min(_xBox.Maximum, Math.Max(_xBox.Minimum, step.X));
        _xBox.Dock = DockStyle.Fill;
        Theme.StyleNumericUpDown(_xBox);

        _yBox.Value = Math.Min(_yBox.Maximum, Math.Max(_yBox.Minimum, step.Y));
        _yBox.Dock = DockStyle.Fill;
        Theme.StyleNumericUpDown(_yBox);

        _delayBox.Value = Math.Min(_delayBox.Maximum, Math.Max(_delayBox.Minimum, step.DelayMs));
        _delayBox.Dock = DockStyle.Fill;
        Theme.StyleNumericUpDown(_delayBox);

        _dragEndXBox.Value = Math.Min(_dragEndXBox.Maximum, Math.Max(_dragEndXBox.Minimum, step.DragEndX));
        _dragEndXBox.Dock = DockStyle.Fill;
        Theme.StyleNumericUpDown(_dragEndXBox);

        _dragEndYBox.Value = Math.Min(_dragEndYBox.Maximum, Math.Max(_dragEndYBox.Minimum, step.DragEndY));
        _dragEndYBox.Dock = DockStyle.Fill;
        Theme.StyleNumericUpDown(_dragEndYBox);

        _wheelDeltaBox.Value = Math.Min(_wheelDeltaBox.Maximum, Math.Max(_wheelDeltaBox.Minimum, step.WheelDelta));
        _wheelDeltaBox.Dock = DockStyle.Fill;
        Theme.StyleNumericUpDown(_wheelDeltaBox);

        _keyPressBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _keyPressBox.Dock = DockStyle.Fill;
        var keyNames = new List<Keys>();
        for (var k = (int)Keys.A; k <= (int)Keys.Z; k++) keyNames.Add((Keys)k);
        for (var k = (int)Keys.D0; k <= (int)Keys.D9; k++) keyNames.Add((Keys)k);
        keyNames.AddRange([Keys.F1, Keys.F2, Keys.F3, Keys.F4, Keys.F5, Keys.F6, Keys.F7, Keys.F8, Keys.F9, Keys.F10, Keys.F11, Keys.F12,
            Keys.Escape, Keys.Tab, Keys.Space, Keys.Enter, Keys.Back, Keys.Delete, Keys.Insert,
            Keys.Home, Keys.End, Keys.PageUp, Keys.PageDown, Keys.Up, Keys.Down, Keys.Left, Keys.Right]);
        _keyPressBox.DataSource = keyNames;
        var keyIdx = keyNames.IndexOf((Keys)step.KeyCode);
        _keyPressBox.SelectedIndex = keyIdx >= 0 ? keyIdx : 0;
        Theme.StyleComboBox(_keyPressBox);

        _keyComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _keyComboBox.Dock = DockStyle.Fill;
        _keyComboBox.DataSource = new List<Keys>(keyNames);
        var comboKeyIdx = keyNames.IndexOf((Keys)step.KeyCode);
        _keyComboBox.SelectedIndex = comboKeyIdx >= 0 ? comboKeyIdx : 0;
        Theme.StyleComboBox(_keyComboBox);

        _modifierCheckList.Dock = DockStyle.Fill;
        _modifierCheckList.Items.AddRange(["Ctrl", "Alt", "Shift", "Win"]);
        _modifierCheckList.CheckOnClick = true;
        _modifierCheckList.Height = 36;
        if ((step.KeyModifiers & NativeMethods.ModControl) != 0) _modifierCheckList.SetItemChecked(0, true);
        if ((step.KeyModifiers & NativeMethods.ModAlt) != 0) _modifierCheckList.SetItemChecked(1, true);
        if ((step.KeyModifiers & NativeMethods.ModShift) != 0) _modifierCheckList.SetItemChecked(2, true);
        if ((step.KeyModifiers & NativeMethods.ModWin) != 0) _modifierCheckList.SetItemChecked(3, true);
        Theme.StyleListBox(_modifierCheckList);

        _typeTextBox.Dock = DockStyle.Fill;
        _typeTextBox.Text = step.TextToType;
        Theme.StyleTextBox(_typeTextBox);

        _pixelColorBox.Value = Math.Min(_pixelColorBox.Maximum, Math.Max(_pixelColorBox.Minimum, step.PixelColor));
        _pixelColorBox.Dock = DockStyle.Fill;
        Theme.StyleNumericUpDown(_pixelColorBox);

        _pixelToleranceBox.Value = Math.Min(_pixelToleranceBox.Maximum, Math.Max(_pixelToleranceBox.Minimum, step.PixelTolerance));
        _pixelToleranceBox.Dock = DockStyle.Fill;
        Theme.StyleNumericUpDown(_pixelToleranceBox);

        _pixelTimeoutBox.Value = Math.Min(_pixelTimeoutBox.Maximum, Math.Max(_pixelTimeoutBox.Minimum, step.PixelTimeoutMs));
        _pixelTimeoutBox.Dock = DockStyle.Fill;
        Theme.StyleNumericUpDown(_pixelTimeoutBox);

        _searchWBox.Value = Math.Min(_searchWBox.Maximum, Math.Max(_searchWBox.Minimum, step.SearchWidth));
        _searchWBox.Dock = DockStyle.Fill;
        Theme.StyleNumericUpDown(_searchWBox);

        _searchHBox.Value = Math.Min(_searchHBox.Maximum, Math.Max(_searchHBox.Minimum, step.SearchHeight));
        _searchHBox.Dock = DockStyle.Fill;
        Theme.StyleNumericUpDown(_searchHBox);

        _delayMaxBox.Value = Math.Min(_delayMaxBox.Maximum, Math.Max(_delayMaxBox.Minimum, step.DelayMsMax));
        _delayMaxBox.Dock = DockStyle.Fill;
        Theme.StyleNumericUpDown(_delayMaxBox);

        _jumpToStepBox.Value = Math.Min(_jumpToStepBox.Maximum, Math.Max(_jumpToStepBox.Minimum, Math.Max(0, step.JumpToStepIndex)));
        _jumpToStepBox.Dock = DockStyle.Fill;
        Theme.StyleNumericUpDown(_jumpToStepBox);

        _runProgramPathBox.Dock = DockStyle.Fill;
        _runProgramPathBox.Text = step.RunProgramPath;
        Theme.StyleTextBox(_runProgramPathBox);

        _runProgramArgsBox.Dock = DockStyle.Fill;
        _runProgramArgsBox.Text = step.RunProgramArgs;
        Theme.StyleTextBox(_runProgramArgsBox);

        _soundFilePathBox.Dock = DockStyle.Fill;
        _soundFilePathBox.Text = step.SoundFilePath;
        Theme.StyleTextBox(_soundFilePathBox);

        _chainMacroIdBox.Dock = DockStyle.Fill;
        _chainMacroIdBox.Text = step.ChainMacroId;
        Theme.StyleTextBox(_chainMacroIdBox);

        var typeLabel = CreateDialogLabel(UiText.StepType);
        _xLabel = CreateDialogLabel(UiText.X);
        _yLabel = CreateDialogLabel(UiText.Y);
        _delayLabel = CreateDialogLabel(UiText.DelayMs);
        _dragEndXLabel = CreateDialogLabel(UiText.DragEndX);
        _dragEndYLabel = CreateDialogLabel(UiText.DragEndY);
        _wheelLabel = CreateDialogLabel(UiText.WheelDelta);
        _keyLabel = CreateDialogLabel(UiText.KeyToPress);
        _modifierLabel = CreateDialogLabel(UiText.Modifiers);
        _textLabel = CreateDialogLabel(UiText.TextToType);
        _pixelColorLabel = CreateDialogLabel(UiText.PixelColor);
        _pixelToleranceLabel = CreateDialogLabel(UiText.PixelTolerance);
        _pixelTimeoutLabel = CreateDialogLabel(UiText.PixelTimeout);
        _searchWLabel = CreateDialogLabel(UiText.SearchWidth);
        _searchHLabel = CreateDialogLabel(UiText.SearchHeight);
        _delayMaxLabel = CreateDialogLabel(UiText.DelayMax);
        _jumpToStepLabel = CreateDialogLabel(UiText.JumpToStep);
        _runProgramPathLabel = CreateDialogLabel(UiText.ProgramPath);
        _runProgramArgsLabel = CreateDialogLabel(UiText.ProgramArgs);
        _soundFileLabel = CreateDialogLabel(UiText.SoundFile);
        _chainMacroIdLabel = CreateDialogLabel(UiText.ChainMacroId);

        panel.Controls.Add(typeLabel, 0, 0);
        panel.Controls.Add(_typeBox, 1, 0);
        panel.Controls.Add(_xLabel, 0, 1);
        panel.Controls.Add(_xBox, 1, 1);
        panel.Controls.Add(_yLabel, 0, 2);
        panel.Controls.Add(_yBox, 1, 2);
        panel.Controls.Add(_delayLabel, 0, 3);
        panel.Controls.Add(_delayBox, 1, 3);
        panel.Controls.Add(_dragEndXLabel, 0, 4);
        panel.Controls.Add(_dragEndXBox, 1, 4);
        panel.Controls.Add(_dragEndYLabel, 0, 5);
        panel.Controls.Add(_dragEndYBox, 1, 5);
        panel.Controls.Add(_wheelLabel, 0, 6);
        panel.Controls.Add(_wheelDeltaBox, 1, 6);
        panel.Controls.Add(_keyLabel, 0, 7);
        panel.Controls.Add(_keyPressBox, 1, 7);
        panel.Controls.Add(_modifierLabel, 0, 8);
        panel.Controls.Add(_modifierCheckList, 1, 8);
        panel.Controls.Add(_textLabel, 0, 9);
        panel.Controls.Add(_typeTextBox, 1, 9);
        panel.Controls.Add(_pixelColorLabel, 0, 10);
        panel.Controls.Add(_pixelColorBox, 1, 10);
        panel.Controls.Add(_pixelToleranceLabel, 0, 11);
        panel.Controls.Add(_pixelToleranceBox, 1, 11);
        panel.Controls.Add(_pixelTimeoutLabel, 0, 12);
        panel.Controls.Add(_pixelTimeoutBox, 1, 12);
        panel.Controls.Add(_searchWLabel, 0, 13);
        panel.Controls.Add(_searchWBox, 1, 13);
        panel.Controls.Add(_searchHLabel, 0, 14);
        panel.Controls.Add(_searchHBox, 1, 14);
        panel.Controls.Add(_delayMaxLabel, 0, 15);
        panel.Controls.Add(_delayMaxBox, 1, 15);
        panel.Controls.Add(_jumpToStepLabel, 0, 16);
        panel.Controls.Add(_jumpToStepBox, 1, 16);
        panel.Controls.Add(_runProgramPathLabel, 0, 17);
        panel.Controls.Add(_runProgramPathBox, 1, 17);
        panel.Controls.Add(_runProgramArgsLabel, 0, 18);
        panel.Controls.Add(_runProgramArgsBox, 1, 18);
        panel.Controls.Add(_soundFileLabel, 0, 19);
        panel.Controls.Add(_soundFilePathBox, 1, 19);
        panel.Controls.Add(_chainMacroIdLabel, 0, 20);
        panel.Controls.Add(_chainMacroIdBox, 1, 20);

        var buttonPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 6, 0, 0) };
        Theme.StylePanel(buttonPanel);

        var cancelBtn = new Button { Text = UiText.Cancel, Width = 80, Height = 32 };
        Theme.StyleButton(cancelBtn);
        cancelBtn.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        var okBtn = new Button { Text = UiText.Confirm, Width = 80, Height = 32 };
        Theme.StyleAccentButton(okBtn);
        okBtn.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };

        buttonPanel.Controls.Add(cancelBtn);
        buttonPanel.Controls.Add(okBtn);
        panel.Controls.Add(buttonPanel, 1, 21);

        AcceptButton = okBtn;
        CancelButton = cancelBtn;

        UpdateFieldVisibility();
    }

    private void UpdateFieldVisibility()
    {
        var type = StepType;
        var isClick = type is MacroStepType.LeftClick or MacroStepType.RightClick or MacroStepType.DoubleClick or MacroStepType.MiddleClick;
        var isMove = type is MacroStepType.Move or MacroStepType.MoveRelative;
        var isDrag = type == MacroStepType.Drag;
        var isDelay = type == MacroStepType.Delay;
        var isWheel = type == MacroStepType.MouseWheel;
        var isKey = type is MacroStepType.KeyPress or MacroStepType.KeyCombo;
        var isCombo = type == MacroStepType.KeyCombo;
        var isTypeText = type == MacroStepType.TypeText;
        var isPixel = type is MacroStepType.WaitPixel or MacroStepType.FindPixel;
        var isFindPixel = type == MacroStepType.FindPixel;
        var isScreenshot = type == MacroStepType.Screenshot;
        var isRandomDelay = type == MacroStepType.RandomDelay;
        var isJumpIfPixel = type == MacroStepType.JumpIfPixel;
        var isRunProgram = type == MacroStepType.RunProgram;
        var isPlaySound = type == MacroStepType.PlaySound;
        var isChainMacro = type == MacroStepType.ChainMacro;

        _xLabel.Visible = _xBox.Visible = isClick || isMove || isDrag || isPixel || isScreenshot;
        _yLabel.Visible = _yBox.Visible = isClick || isMove || isDrag || isPixel || isScreenshot || isJumpIfPixel;
        _delayLabel.Visible = _delayBox.Visible = isDelay;
        _dragEndXLabel.Visible = _dragEndXBox.Visible = isDrag;
        _dragEndYLabel.Visible = _dragEndYBox.Visible = isDrag;
        _wheelLabel.Visible = _wheelDeltaBox.Visible = isWheel;
        _keyLabel.Visible = isKey || isCombo;
        _keyPressBox.Visible = isKey && !isCombo;
        _keyComboBox.Visible = isCombo;
        _modifierLabel.Visible = _modifierCheckList.Visible = isCombo;
        _textLabel.Visible = _typeTextBox.Visible = isTypeText;
        _pixelColorLabel.Visible = _pixelColorBox.Visible = isPixel;
        _pixelToleranceLabel.Visible = _pixelToleranceBox.Visible = isPixel;
        _pixelTimeoutLabel.Visible = _pixelTimeoutBox.Visible = isPixel;
        _searchWLabel.Visible = _searchWBox.Visible = isFindPixel || isScreenshot;
        _searchHLabel.Visible = _searchHBox.Visible = isFindPixel || isScreenshot;
        _delayMaxLabel.Visible = _delayMaxBox.Visible = isRandomDelay;
        _jumpToStepLabel.Visible = _jumpToStepBox.Visible = isJumpIfPixel;
        _runProgramPathLabel.Visible = _runProgramPathBox.Visible = isRunProgram;
        _runProgramArgsLabel.Visible = _runProgramArgsBox.Visible = isRunProgram;
        _soundFileLabel.Visible = _soundFilePathBox.Visible = isPlaySound;
        _chainMacroIdLabel.Visible = _chainMacroIdBox.Visible = isChainMacro;
    }

    private static Label CreateDialogLabel(string text)
    {
        var label = new Label { Text = text, TextAlign = ContentAlignment.MiddleLeft };
        Theme.StyleLabel(label);
        return label;
    }
}
