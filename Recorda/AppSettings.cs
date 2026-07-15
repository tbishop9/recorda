using System.IO;
using System.Text.Json;

namespace Recorda;

internal sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Recorda");
    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    public string? OutputFolder { get; set; }
    public string? MicrophoneDeviceId { get; set; }
    public string? OutputDeviceId { get; set; }
    public double MicrophoneGain { get; set; } = 0.72;
    public double SystemGain { get; set; } = 0.72;
    public double WindowWidth { get; set; } = 820;
    public double WindowHeight { get; set; } = 780;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new();
        }
        catch
        {
            // A corrupt settings file should never prevent recording.
        }

        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsDirectory);
        string temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temporaryPath, SettingsPath, true);
    }
}
