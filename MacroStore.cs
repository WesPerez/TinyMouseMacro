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

            foreach (var profile in profiles)
            {
                if (string.IsNullOrWhiteSpace(profile.Id))
                {
                    profile.Id = Guid.NewGuid().ToString("N");
                }
            }

            return profiles;
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

        File.WriteAllText(FilePath, JsonSerializer.Serialize(profiles, JsonOptions));
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
