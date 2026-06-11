using System.Text.Json;

namespace TinyMouseMacro;

public sealed class MacroStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string FilePath { get; }

    public MacroStore(string filePath)
    {
        FilePath = filePath;
    }

    public List<MacroProfile> Load()
    {
        if (!File.Exists(FilePath))
        {
            return [CreateDefaultProfile()];
        }

        try
        {
            var json = File.ReadAllText(FilePath);
            var profiles = JsonSerializer.Deserialize<List<MacroProfile>>(json, JsonOptions);
            if (profiles is not { Count: > 0 })
            {
                return [CreateDefaultProfile()];
            }

            return NormalizeProfiles(profiles);
        }
        catch
        {
            try
            {
                var backupPath = FilePath + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Copy(FilePath, backupPath, overwrite: true);
            }
            catch
            {
            }

            return [CreateDefaultProfile()];
        }
    }

    public void Save(IReadOnlyCollection<MacroProfile> profiles)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(NormalizeProfiles(profiles), JsonOptions);
        var tempPath = FilePath + ".tmp";
        File.WriteAllText(tempPath, json);

        if (File.Exists(FilePath))
        {
            File.Replace(tempPath, FilePath, null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tempPath, FilePath);
        }
    }

    public static List<MacroProfile> NormalizeProfiles(IEnumerable<MacroProfile?> profiles)
    {
        var normalized = profiles
            .Where(static profile => profile is not null)
            .Select(static profile => NormalizeProfile(profile!))
            .ToList();

        return normalized.Count > 0 ? normalized : [CreateDefaultProfile()];
    }

    public static MacroProfile NormalizeProfile(MacroProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Id))
        {
            profile.Id = Guid.NewGuid().ToString("N");
        }

        profile.Name = string.IsNullOrWhiteSpace(profile.Name) ? UiText.Untitled : profile.Name.Trim();
        profile.Hotkey ??= string.Empty;
        profile.TargetWindowTitle ??= string.Empty;
        profile.TriggerType = Enum.IsDefined(profile.TriggerType) ? profile.TriggerType : MacroTriggerType.KeyboardHotkey;
        profile.TriggerKey = Math.Max(0, profile.TriggerKey);
        profile.TriggerMouseButton = profile.TriggerMouseButton is 3 or 4 or 5 ? profile.TriggerMouseButton : 0;
        profile.RepeatCount = Math.Clamp(profile.RepeatCount, 0, 9999);
        profile.RepeatIntervalMs = Math.Clamp(profile.RepeatIntervalMs, 0, 600000);
        profile.SpeedMultiplier = Math.Clamp(profile.SpeedMultiplier, 0.1, 10.0);
        profile.ScheduleIntervalMinutes = Math.Clamp(profile.ScheduleIntervalMinutes, 0, 1440);
        profile.ScheduleNextRun = null;
        profile.Steps ??= [];

        for (var i = profile.Steps.Count - 1; i >= 0; i--)
        {
            if (profile.Steps[i] is null)
            {
                profile.Steps.RemoveAt(i);
            }
            else
            {
                NormalizeStep(profile.Steps[i]);
            }
        }

        return profile;
    }

    public static MacroStep NormalizeStep(MacroStep step)
    {
        step.Type = Enum.IsDefined(step.Type) ? step.Type : MacroStepType.Move;
        step.DelayMs = Math.Clamp(step.DelayMs, 0, 600000);
        step.DelayMsMax = Math.Clamp(step.DelayMsMax, 0, 600000);
        step.WheelDelta = Math.Clamp(step.WheelDelta, -12000, 12000);
        step.KeyCode = Math.Clamp(step.KeyCode, 0, byte.MaxValue);
        step.KeyModifiers &= NativeMethods.ModControl | NativeMethods.ModAlt | NativeMethods.ModShift | NativeMethods.ModWin;
        step.TextToType ??= string.Empty;
        step.PixelColor = Math.Clamp(step.PixelColor, 0, 0xFFFFFF);
        step.PixelTolerance = Math.Clamp(step.PixelTolerance, 0, 255);
        step.PixelTimeoutMs = Math.Clamp(step.PixelTimeoutMs, 0, 60000);
        step.SearchWidth = Math.Clamp(step.SearchWidth, 1, 3840);
        step.SearchHeight = Math.Clamp(step.SearchHeight, 1, 2160);
        step.JumpToStepIndex = Math.Max(-1, step.JumpToStepIndex);
        step.RunProgramPath ??= string.Empty;
        step.RunProgramArgs ??= string.Empty;
        step.SoundFilePath ??= string.Empty;
        step.ChainMacroId ??= string.Empty;
        return step;
    }

    private static MacroProfile CreateDefaultProfile()
    {
        return new MacroProfile
        {
            Name = $"{UiText.Macro} 1",
            Hotkey = "Alt+Z",
            Steps = []
        };
    }
}
