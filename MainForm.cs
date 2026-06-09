namespace TinyMouseMacro;

public sealed class MainForm : Form
{
    private readonly MacroStore _store;
    private readonly MacroExecutor _executor = new();
    private readonly BindingSource _profilesSource = new();
    private readonly BindingSource _stepsSource = new();
    private readonly Dictionary<string, DateTime> _lastMacroTriggers = [];
    private readonly System.Windows.Forms.Timer _positionTimer = new() { Interval = 100 };

    private HotkeyService? _hotkeys;
    private List<MacroProfile> _profiles = [];
    private bool _isBinding;

    private readonly ListBox _profileList = new();
    private readonly TextBox _nameBox = new();
    private readonly TextBox _hotkeyBox = new();
    private readonly ListBox _stepList = new();
    private readonly ComboBox _stepTypeBox = new();
    private readonly NumericUpDown _xBox = new();
    private readonly NumericUpDown _yBox = new();
    private readonly NumericUpDown _delayBox = new();
    private readonly Label _livePositionLabel = new();
    private readonly Label _statusLabel = new();
    private readonly TextBox _screenInfoBox = new();

    public MainForm()
    {
        Text = UiText.AppTitle;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(980, 720);
        Size = new Size(1100, 780);
        Font = new Font("Microsoft YaHei UI", 9F);
        KeyPreview = true;

        _store = new MacroStore(Path.Combine(AppContext.BaseDirectory, "macros.json"));
        _positionTimer.Tick += (_, _) => UpdateLivePosition();

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

        _stepTypeBox.Items.AddRange([UiText.TypeMove, UiText.TypeClick, UiText.TypeDelay]);
        _stepTypeBox.SelectedIndex = 1;

        RefreshScreenInfo();
        BindSelectedProfile();
        RebuildHotkeys();
        _positionTimer.Start();
    }

    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        SaveAll();
        _positionTimer.Dispose();
        _hotkeys?.Dispose();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(10)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 270));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        Controls.Add(root);

        root.Controls.Add(BuildProfilePanel(), 0, 0);

        var right = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(10, 0, 0, 0)
        };
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 132));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 172));
        root.Controls.Add(right, 1, 0);

        right.Controls.Add(BuildProfileEditor(), 0, 0);
        right.Controls.Add(BuildStepList(), 0, 1);
        right.Controls.Add(BuildStepEditor(), 0, 2);
        right.Controls.Add(BuildStepButtons(), 0, 3);
        right.Controls.Add(BuildLivePositionPanel(), 0, 4);
        right.Controls.Add(BuildScreenInfo(), 0, 5);

        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.AutoEllipsis = true;
        root.SetColumnSpan(_statusLabel, 2);
        root.Controls.Add(_statusLabel, 0, 1);
    }

    private Control BuildProfilePanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));

        panel.Controls.Add(new Label { Text = UiText.Profiles, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);

        _profileList.Dock = DockStyle.Fill;
        _profileList.IntegralHeight = false;
        _profileList.SelectedIndexChanged += (_, _) => BindSelectedProfile();
        panel.Controls.Add(_profileList, 0, 1);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
        buttons.Controls.Add(CreateButton(UiText.AddProfile, AddProfile, 118));
        buttons.Controls.Add(CreateButton(UiText.DuplicateProfile, DuplicateProfile, 118));
        buttons.Controls.Add(CreateButton(UiText.DeleteProfile, DeleteProfile, 118));
        buttons.Controls.Add(CreateButton(UiText.SaveEnable, SaveAndReload, 118));
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

        panel.Controls.Add(new Label { Text = UiText.Name, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        panel.Controls.Add(new Label { Text = UiText.Hotkey, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 2, 0);

        _nameBox.Dock = DockStyle.Fill;
        _nameBox.TextChanged += (_, _) =>
        {
            if (_isBinding || CurrentProfile is null) return;
            CurrentProfile.Name = string.IsNullOrWhiteSpace(_nameBox.Text) ? UiText.Untitled : _nameBox.Text.Trim();
            _profilesSource.ResetBindings(false);
        };

        _hotkeyBox.Dock = DockStyle.Fill;
        _hotkeyBox.ReadOnly = true;
        _hotkeyBox.PlaceholderText = UiText.HotkeyHint;
        _hotkeyBox.KeyDown += HotkeyBox_KeyDown;
        _hotkeyBox.PreviewKeyDown += (_, e) => e.IsInputKey = true;

        panel.Controls.Add(_nameBox, 1, 1);
        panel.Controls.Add(_hotkeyBox, 3, 1);
        return panel;
    }

    private Control BuildStepList()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label { Text = UiText.Steps, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);

        _stepList.Dock = DockStyle.Fill;
        _stepList.IntegralHeight = false;
        _stepList.DataSource = _stepsSource;
        _stepList.SelectedIndexChanged += (_, _) => BindSelectedStep();
        panel.Controls.Add(_stepList, 0, 1);
        return panel;
    }

    private Control BuildStepEditor()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 3 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));

        panel.Controls.Add(new Label { Text = UiText.StepType, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        _stepTypeBox.Dock = DockStyle.Fill;
        _stepTypeBox.DropDownStyle = ComboBoxStyle.DropDownList;
        panel.Controls.Add(_stepTypeBox, 1, 0);

        panel.Controls.Add(new Label { Text = UiText.X, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 2, 0);
        panel.Controls.Add(_xBox, 3, 0);
        panel.Controls.Add(new Label { Text = UiText.Y, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 4, 0);
        panel.Controls.Add(_yBox, 5, 0);

        panel.Controls.Add(new Label { Text = UiText.DelayMs, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
        panel.Controls.Add(_delayBox, 1, 1);

        ConfigureCoordinateBox(_xBox);
        ConfigureCoordinateBox(_yBox);
        _delayBox.Minimum = 0;
        _delayBox.Maximum = 600000;
        _delayBox.Increment = 50;
        _delayBox.Dock = DockStyle.Fill;

        panel.Controls.Add(CreateButton(UiText.ApplyEdit, ApplyStepEdit, 132), 2, 1);
        panel.SetColumnSpan(panel.GetControlFromPosition(2, 1)!, 2);
        panel.Controls.Add(CreateButton(UiText.CapturePointDelay, async () => await CapturePointAfterDelayAsync(false), 190), 4, 1);
        panel.SetColumnSpan(panel.GetControlFromPosition(4, 1)!, 2);
        panel.Controls.Add(CreateButton(UiText.RecordNextClick, async () => await CapturePointAfterDelayAsync(true), 190), 0, 2);
        panel.SetColumnSpan(panel.GetControlFromPosition(0, 2)!, 2);

        return panel;
    }

    private Control BuildStepButtons()
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
        panel.Controls.Add(CreateButton(UiText.AddMove, AddMoveStep, 140));
        panel.Controls.Add(CreateButton(UiText.AddClick, AddClickStep, 140));
        panel.Controls.Add(CreateButton(UiText.AddDelay, AddDelayStep, 140));
        panel.Controls.Add(CreateButton(UiText.MoveUp, MoveStepUp, 104));
        panel.Controls.Add(CreateButton(UiText.MoveDown, MoveStepDown, 104));
        panel.Controls.Add(CreateButton(UiText.DeleteStep, DeleteStep, 104));
        panel.Controls.Add(CreateButton(UiText.TestRun, TestRun, 104));
        return panel;
    }

    private Control BuildLivePositionPanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label { Text = UiText.LivePosition, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        _livePositionLabel.Dock = DockStyle.Fill;
        _livePositionLabel.TextAlign = ContentAlignment.MiddleLeft;
        _livePositionLabel.Font = new Font(Font.FontFamily, 11F, FontStyle.Bold);
        panel.Controls.Add(_livePositionLabel, 1, 0);
        return panel;
    }

    private Control BuildScreenInfo()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 2 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label { Text = UiText.ScreenInfo, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        panel.Controls.Add(CreateButton(UiText.RefreshCoordinates, RefreshScreenInfo, 158), 1, 0);

        _screenInfoBox.Dock = DockStyle.Fill;
        _screenInfoBox.Multiline = true;
        _screenInfoBox.ReadOnly = true;
        _screenInfoBox.ScrollBars = ScrollBars.Vertical;
        panel.SetColumnSpan(_screenInfoBox, 2);
        panel.Controls.Add(_screenInfoBox, 0, 1);
        return panel;
    }

    private Button CreateButton(string text, Action action, int width)
    {
        var button = new Button { Text = text, Width = width, Height = 32, Margin = new Padding(4) };
        button.Click += (_, _) => action();
        return button;
    }

    private void ConfigureCoordinateBox(NumericUpDown box)
    {
        box.Minimum = -100000;
        box.Maximum = 100000;
        box.Dock = DockStyle.Fill;
    }

    private MacroProfile? CurrentProfile => _profileList.SelectedItem as MacroProfile;
    private MacroStep? CurrentStep => _stepList.SelectedItem as MacroStep;

    private void HotkeyBox_KeyDown(object? sender, KeyEventArgs e)
    {
        e.SuppressKeyPress = true;
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

        BindSelectedStep();
    }

    private void BindSelectedStep()
    {
        if (_isBinding) return;

        var step = CurrentStep;
        if (step is null)
        {
            return;
        }

        _stepTypeBox.SelectedIndex = step.Type switch
        {
            MacroStepType.Move => 0,
            MacroStepType.LeftClick => 1,
            MacroStepType.Delay => 2,
            _ => 1
        };
        _xBox.Value = ClampToNumeric(_xBox, step.X);
        _yBox.Value = ClampToNumeric(_yBox, step.Y);
        _delayBox.Value = ClampToNumeric(_delayBox, step.DelayMs);
    }

    private void AddProfile()
    {
        var profile = new MacroProfile { Name = $"{UiText.Macro} {_profiles.Count + 1}", Hotkey = GetNextDefaultHotkey(), Steps = [] };
        _profiles.Add(profile);
        _profilesSource.ResetBindings(false);
        _profileList.SelectedItem = profile;
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
        _xBox.Value = ClampToNumeric(_xBox, point.X);
        _yBox.Value = ClampToNumeric(_yBox, point.Y);

        if (addClickStep)
        {
            AddStep(new MacroStep { Type = MacroStepType.LeftClick, X = point.X, Y = point.Y });
            SetStatus(UiText.RecordedClick(point.X, point.Y));
        }
        else
        {
            SetStatus(UiText.CapturedPoint(point.X, point.Y));
        }
    }

    private void AddMoveStep()
    {
        AddStep(new MacroStep { Type = MacroStepType.Move, X = (int)_xBox.Value, Y = (int)_yBox.Value });
    }

    private void AddClickStep()
    {
        AddStep(new MacroStep { Type = MacroStepType.LeftClick, X = (int)_xBox.Value, Y = (int)_yBox.Value });
    }

    private void AddDelayStep()
    {
        AddStep(new MacroStep { Type = MacroStepType.Delay, DelayMs = (int)_delayBox.Value });
    }

    private void AddStep(MacroStep step)
    {
        var profile = CurrentProfile;
        if (profile is null) return;

        profile.Steps.Add(step);
        RefreshSteps();
        _stepList.SelectedItem = step;
        SaveAll();
        SetStatus(UiText.AddedStep(step.Display));
    }

    private void ApplyStepEdit()
    {
        var step = CurrentStep;
        if (step is null)
        {
            SetStatus(UiText.SelectStepFirst);
            return;
        }

        step.Type = _stepTypeBox.SelectedIndex switch
        {
            0 => MacroStepType.Move,
            1 => MacroStepType.LeftClick,
            2 => MacroStepType.Delay,
            _ => MacroStepType.LeftClick
        };
        step.X = (int)_xBox.Value;
        step.Y = (int)_yBox.Value;
        step.DelayMs = (int)_delayBox.Value;

        RefreshSteps();
        _stepList.SelectedItem = step;
        SaveAll();
        SetStatus(UiText.EditedStep(step.Display));
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
        SaveAll();
    }

    private void DeleteStep()
    {
        var profile = CurrentProfile;
        var step = CurrentStep;
        if (profile is null || step is null) return;

        profile.Steps.Remove(step);
        RefreshSteps();
        SaveAll();
        SetStatus(UiText.DeletedStep);
    }

    private void TestRun()
    {
        var profile = CurrentProfile;
        if (profile is null) return;

        SetStatus(UiText.Testing(profile.Name));
        _ = _executor.RunAsync(profile);
    }

    private void SaveAndReload()
    {
        SaveAll();
        RebuildHotkeys();
    }

    private void SaveAll()
    {
        _store.Save(_profiles);
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

        SetStatus(UiText.Running(profile.Name));
        _ = _executor.RunAsync(profile).ContinueWith(task =>
        {
            if (IsDisposed) return;
            BeginInvoke(() =>
            {
                SetStatus(task.Exception is null ? UiText.Finished(profile.Name) : task.Exception.GetBaseException().Message);
            });
        });
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

    private static decimal ClampToNumeric(NumericUpDown box, int value)
    {
        return Math.Min(box.Maximum, Math.Max(box.Minimum, value));
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
