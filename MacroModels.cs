using System.Text.Json.Serialization;

namespace TinyMouseMacro;

public sealed class MacroProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = UiText.Macro;
    public string Hotkey { get; set; } = "Alt+Z";
    public List<MacroStep> Steps { get; set; } = [];
}

public sealed class MacroStep
{
    public MacroStepType Type { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int DelayMs { get; set; }

    [JsonIgnore]
    public string Display => Type switch
    {
        MacroStepType.Move => $"{UiText.MoveStepDisplay} ({X}, {Y})",
        MacroStepType.LeftClick => $"{UiText.ClickStepDisplay} ({X}, {Y})",
        MacroStepType.Delay => $"{UiText.DelayStepDisplay} {DelayMs} {UiText.Milliseconds}",
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
    Delay
}
