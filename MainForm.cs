namespace TinyMouseMacro;

public sealed class MainForm : Form
{
    private readonly MacroStore _store;
    private readonly MacroExecutor _executor = new();
    private readonly BindingSource _profilesSource = new();
    private readonly BindingSource _stepsSource = new();
    private readonly Dictionary<string, DateTime> _lastMacroTriggers = [];
    private readonly System.Windows.Forms.Timer _positionTimer = new() { Interval = 100 };
    private CancellationTokenSource? _executionCts;

    private HotkeyService? _hotkeys;
    private List<MacroProfile> _profiles = [];
    private bool _isBinding;
    private bool _dirty;

    private readonly ListBox _profileList = new();
    private readonly TextBox _nameBox = new();
    private readonly TextBox _hotkeyBox = new();
    private readonly ListBox _stepList = new();
    private readonly Label _livePositionLabel = new();
    private readonly Label _statusLabel = new();
    private readonly TextBox _screenInfoBox = new();
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

        _store = new MacroStore(Path.Combine(AppContext.BaseDirectory, "macros.json"));
        _positionTimer.Tick += (_, _) => UpdateLivePosition();

        var showItem = new ToolStripMenuItem(UiText.TrayShow);
        showItem.Click += (_, _) => ShowFromTray();

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
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WmHotkey && _hotkeys?.TryGetProfile(m.WParam.ToInt32(), out var profile) == true && profile is not null)
        {
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
        _profileList.DisplayMember = nameof(MacroProfile.Name);
        _profileList.SelectedIndex = _profiles.Count > 0 ? 0 : -1;

        RefreshScreenInfo();
        BindSelectedProfile();
        RebuildHotkeys();
        _positionTimer.Start();
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
        _positionTimer.Dispose();
        _hotkeys?.Dispose();
        _trayIcon.Dispose();
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
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 210));
        Theme.StylePanel(right);
        root.Controls.Add(right, 1, 0);

        right.Controls.Add(BuildProfileEditor(), 0, 0);
        right.Controls.Add(BuildStepList(), 0, 1);
        right.Controls.Add(BuildCaptureButtons(), 0, 2);
        right.Controls.Add(BuildLivePositionPanel(), 0, 3);
        right.Controls.Add(BuildScreenInfo(), 0, 4);

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
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));
        Theme.StylePanel(panel);

        var header = new Label { Text = UiText.Profiles, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        Theme.StyleSectionHeader(header);
        panel.Controls.Add(header, 0, 0);

        _profileList.Dock = DockStyle.Fill;
        _profileList.IntegralHeight = false;
        _profileList.SelectedIndexChanged += (_, _) => BindSelectedProfile();
        Theme.StyleListBox(_profileList);
        panel.Controls.Add(_profileList, 0, 1);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
        Theme.StylePanel(buttons);
        buttons.Controls.Add(CreateButton(UiText.AddProfile, AddProfile, 118));
        buttons.Controls.Add(CreateButton(UiText.DuplicateProfile, DuplicateProfile, 118));
        buttons.Controls.Add(CreateButton(UiText.DeleteProfile, DeleteProfile, 118));
        buttons.Controls.Add(CreateAccentButton(UiText.SaveEnable, SaveAndReload, 118));
        panel.Controls.Add(buttons, 0, 2);
        return panel;
    }

    private Control BuildProfileEditor()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 2 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        Theme.StylePanel(panel);

        var nameLabel = new Label { Text = UiText.Name, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        var hotkeyLabel = new Label { Text = UiText.Hotkey, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        Theme.StyleLabel(nameLabel);
        Theme.StyleLabel(hotkeyLabel);
        panel.Controls.Add(nameLabel, 0, 0);
        panel.Controls.Add(hotkeyLabel, 2, 0);

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

        panel.Controls.Add(_nameBox, 1, 1);
        panel.Controls.Add(_hotkeyBox, 3, 1);
        return panel;
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
        panel.Controls.Add(CreateButton(UiText.AddDelay, AddDelayStep, 140));
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

    private void BindSelectedProfile()
    {
        _isBinding = true;
        try
        {
            var profile = CurrentProfile;
            _nameBox.Text = profile?.Name ?? string.Empty;
            _hotkeyBox.Text = profile?.Hotkey ?? string.Empty;
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
            RefreshSteps();
            _stepList.SelectedItem = step;
            MarkDirty();
            SetStatus(UiText.EditedStep(step.Display));
        }
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

    private async Task CapturePointAfterDelayAsync(bool addClickStep)
    {
        for (var i = 3; i >= 1; i--)
        {
            SetStatus(UiText.CountdownCapture(i));
            await Task.Delay(1000);
        }

        var point = Cursor.Position;

        if (addClickStep)
        {
            AddStep(new MacroStep { Type = MacroStepType.LeftClick, X = point.X, Y = point.Y });
            SetStatus(UiText.RecordedClick(point.X, point.Y));
        }
        else
        {
            AddStep(new MacroStep { Type = MacroStepType.Move, X = point.X, Y = point.Y });
            SetStatus(UiText.CapturedPoint(point.X, point.Y));
        }
    }

    private void AddDelayStep()
    {
        AddStep(new MacroStep { Type = MacroStepType.Delay, DelayMs = 500 });
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
        if (_hotkeys is not null)
        {
            _hotkeys.HotkeyPressed -= Hotkeys_HotkeyPressed;
        }

        _hotkeys ??= new HotkeyService(Handle);
        _hotkeys.HotkeyPressed += Hotkeys_HotkeyPressed;
        var errors = _hotkeys.RegisterAll(_profiles);
        SetStatus(errors.Count == 0 ? UiText.Ready(_hotkeys.RegisteredCount, _store.FilePath) : string.Join(" | ", errors));
    }

    private void Hotkeys_HotkeyPressed(object? sender, MacroProfile profile)
    {
        if (!IsDisposed)
        {
            BeginInvoke(() => RunMacro(profile));
        }
    }

    private void RunMacro(MacroProfile profile)
    {
        if (IsDuplicateTrigger(profile)) return;

        if (_executor.IsRunning)
        {
            SetStatus(UiText.AlreadyRunning);
            return;
        }

        SetStatus(UiText.Running(profile.Name));
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

    private bool IsDuplicateTrigger(MacroProfile profile)
    {
        var key = profile.Id;
        var now = DateTime.UtcNow;
        if (_lastMacroTriggers.TryGetValue(key, out var last) && now - last < TimeSpan.FromMilliseconds(250))
        {
            return true;
        }

        _lastMacroTriggers[key] = now;
        return false;
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

    private static MacroStep CloneStep(MacroStep step)
    {
        return new MacroStep { Type = step.Type, X = step.X, Y = step.Y, DelayMs = step.DelayMs };
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

    public MacroStepType StepType => _typeBox.SelectedIndex switch
    {
        0 => MacroStepType.Move,
        1 => MacroStepType.LeftClick,
        2 => MacroStepType.Delay,
        _ => MacroStepType.LeftClick
    };

    public int X => (int)_xBox.Value;
    public int Y => (int)_yBox.Value;
    public int DelayMs => (int)_delayBox.Value;

    public StepEditDialog(MacroStep step)
    {
        Text = UiText.EditStepTitle;
        Size = new Size(360, 240);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        KeyPreview = true;

        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, RowCount = 5 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(panel);

        _typeBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _typeBox.Items.AddRange([UiText.TypeMove, UiText.TypeClick, UiText.TypeDelay]);
        _typeBox.SelectedIndex = step.Type switch
        {
            MacroStepType.Move => 0,
            MacroStepType.LeftClick => 1,
            MacroStepType.Delay => 2,
            _ => 1
        };
        _typeBox.Dock = DockStyle.Fill;
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

        var typeLabel = new Label { Text = UiText.StepType, TextAlign = ContentAlignment.MiddleLeft };
        var xLabel = new Label { Text = UiText.X, TextAlign = ContentAlignment.MiddleLeft };
        var yLabel = new Label { Text = UiText.Y, TextAlign = ContentAlignment.MiddleLeft };
        var delayLabel = new Label { Text = UiText.DelayMs, TextAlign = ContentAlignment.MiddleLeft };
        Theme.StyleLabel(typeLabel);
        Theme.StyleLabel(xLabel);
        Theme.StyleLabel(yLabel);
        Theme.StyleLabel(delayLabel);

        panel.Controls.Add(typeLabel, 0, 0);
        panel.Controls.Add(_typeBox, 1, 0);
        panel.Controls.Add(xLabel, 0, 1);
        panel.Controls.Add(_xBox, 1, 1);
        panel.Controls.Add(yLabel, 0, 2);
        panel.Controls.Add(_yBox, 1, 2);
        panel.Controls.Add(delayLabel, 0, 3);
        panel.Controls.Add(_delayBox, 1, 3);

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
        panel.Controls.Add(buttonPanel, 1, 4);

        AcceptButton = okBtn;
        CancelButton = cancelBtn;
    }
}
