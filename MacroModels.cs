using System.Text.Json.Serialization;

namespace TinyMouseMacro;

public sealed class MacroProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = UiText.Macro;
    public string Hotkey { get; set; } = "Alt+Z";
    public List<MacroStep> Steps { get; set; } = [];

    public MacroTriggerType TriggerType { get; set; } = MacroTriggerType.KeyboardHotkey;
    public int TriggerKey { get; set; }
    public uint TriggerModifiers { get; set; }
    public int TriggerMouseButton { get; set; }

    public int RepeatCount { get; set; } = 1;
    public int RepeatIntervalMs { get; set; } = 0;
    public bool Enabled { get; set; } = true;
    public string TargetWindowTitle { get; set; } = string.Empty;
    public double SpeedMultiplier { get; set; } = 1.0;
    public int ScheduleIntervalMinutes { get; set; }

    [JsonIgnore]
    public DateTime? ScheduleNextRun { get; set; }

    [JsonIgnore]
    public string DisplayText
    {
        get
        {
            var enabled = Enabled ? "" : $"[{UiText.Disabled}] ";
            var hotkey = TriggerType switch
            {
                MacroTriggerType.KeyboardHotkey => Hotkey,
                MacroTriggerType.KeyboardHook => TriggerKey > 0 ? ((Keys)TriggerKey).ToString() : "",
                MacroTriggerType.MouseButton => TriggerMouseButton switch { 4 => "X1", 5 => "X2", 3 => "M", _ => "" },
                _ => ""
            };
            var schedule = ScheduleIntervalMinutes > 0 ? $" \u23f0{ScheduleIntervalMinutes}m" : "";
            return $"{enabled}{Name}  [{hotkey}]{schedule}";
        }
    }
}

public sealed class MacroStep
{
    public MacroStepType Type { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int DelayMs { get; set; }
    public int WheelDelta { get; set; }
    public int KeyCode { get; set; }
    public int DragEndX { get; set; }
    public int DragEndY { get; set; }
    public uint KeyModifiers { get; set; }
    public string TextToType { get; set; } = string.Empty;
    public int PixelColor { get; set; }
    public int PixelTolerance { get; set; } = 10;
    public int PixelTimeoutMs { get; set; } = 5000;
    public int SearchWidth { get; set; } = 100;
    public int SearchHeight { get; set; } = 100;
    public int DelayMsMax { get; set; }
    public int JumpToStepIndex { get; set; } = -1;
    public string RunProgramPath { get; set; } = string.Empty;
    public string RunProgramArgs { get; set; } = string.Empty;
    public string SoundFilePath { get; set; } = string.Empty;
    public string ChainMacroId { get; set; } = string.Empty;

    [JsonIgnore]
    public string Display => Type switch
    {
        MacroStepType.Move => $"{UiText.MoveStepDisplay} ({X}, {Y})",
        MacroStepType.LeftClick => $"{UiText.ClickStepDisplay} ({X}, {Y})",
        MacroStepType.RightClick => $"{UiText.RightClickStepDisplay} ({X}, {Y})",
        MacroStepType.DoubleClick => $"{UiText.DoubleClickStepDisplay} ({X}, {Y})",
        MacroStepType.MiddleClick => $"{UiText.MiddleClickStepDisplay} ({X}, {Y})",
        MacroStepType.Delay => $"{UiText.DelayStepDisplay} {DelayMs} {UiText.Milliseconds}",
        MacroStepType.KeyPress => $"{UiText.KeyPressStepDisplay} {((Keys)KeyCode).ToString()}",
        MacroStepType.KeyCombo => $"{UiText.KeyComboStepDisplay} {HotkeyParser.FormatModifiers(KeyModifiers)}+{((Keys)KeyCode).ToString()}",
        MacroStepType.TypeText => $"{UiText.TypeTextStepDisplay} \"{TextToType}\"",
        MacroStepType.MouseWheel => $"{UiText.WheelStepDisplay} {WheelDelta}",
        MacroStepType.Drag => $"{UiText.DragStepDisplay} ({X}, {Y}) \u2192 ({DragEndX}, {DragEndY})",
        MacroStepType.MoveRelative => $"{UiText.MoveRelativeStepDisplay} (\u0394{X}, \u0394{Y})",
        MacroStepType.WaitPixel => $"{UiText.WaitPixelStepDisplay} ({X},{Y}) 0x{PixelColor:X6}",
        MacroStepType.FindPixel => $"{UiText.FindPixelStepDisplay} 0x{PixelColor:X6} ({X},{Y},{SearchWidth}x{SearchHeight})",
        MacroStepType.Screenshot => $"{UiText.ScreenshotStepDisplay} ({X},{Y},{SearchWidth}x{SearchHeight})",
        MacroStepType.RandomDelay => $"{UiText.RandomDelayStepDisplay} {DelayMs}-{DelayMsMax} {UiText.Milliseconds}",
        MacroStepType.JumpIfPixel => $"{UiText.JumpIfPixelStepDisplay} ({X},{Y}) 0x{PixelColor:X6} \u2192 {UiText.Step} {JumpToStepIndex + 1}",
        MacroStepType.RunProgram => $"{UiText.RunProgramStepDisplay} {Path.GetFileName(RunProgramPath)}",
        MacroStepType.PlaySound => $"{UiText.PlaySoundStepDisplay} {Path.GetFileName(SoundFilePath)}",
        MacroStepType.ChainMacro => $"{UiText.ChainMacroStepDisplay}",
        _ => Type.ToString()
    };

    public override string ToString()
    {
        return Display;
    }
}

public enum MacroStepType
{
    Move,
    LeftClick,
    RightClick,
    DoubleClick,
    MiddleClick,
    Delay,
    KeyPress,
    KeyCombo,
    TypeText,
    MouseWheel,
    Drag,
    MoveRelative,
    WaitPixel,
    FindPixel,
    Screenshot,
    RandomDelay,
    JumpIfPixel,
    RunProgram,
    PlaySound,
    ChainMacro
}

public enum MacroTriggerType
{
    KeyboardHotkey,
    KeyboardHook,
    MouseButton
}
