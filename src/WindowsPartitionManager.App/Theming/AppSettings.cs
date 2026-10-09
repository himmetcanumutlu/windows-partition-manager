using System.IO;
using System.Text.Json;

namespace WindowsPartitionManager.App.Theming;

/// <summary>The persisted appearance choices: a colour theme and a UI font family.</summary>
public sealed record AppSettings(string Theme, string Font);

/// <summary>
/// Loads and saves the appearance settings to %LOCALAPPDATA%\WindowsPartitionManager\settings.json.
/// A missing or malformed file falls back to the defaults; writes are atomic (temp file, then replace).
/// </summary>
public static class SettingsStore
{
    public const string DefaultTheme = "White";
    public const string DefaultFont = "Segoe UI";

    public static readonly IReadOnlyList<string> AvailableThemes = ["White", "Dark", "Night"];
    public static readonly IReadOnlyList<string> AvailableFonts = ["Segoe UI", "Arial", "Calibri", "Consolas", "Times New Roman"];

    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WindowsPartitionManager",
        "settings.json");

    public static AppSettings Load()
    {
        var theme = DefaultTheme;
        var font = DefaultFont;

        try
        {
            if (File.Exists(Path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(Path));
                var root = document.RootElement;

                if (root.TryGetProperty("theme", out var themeElement)
                    && themeElement.ValueKind == JsonValueKind.String
                    && themeElement.GetString() is { } candidateTheme
                    && AvailableThemes.Contains(candidateTheme))
                {
                    theme = candidateTheme;
                }

                if (root.TryGetProperty("font", out var fontElement)
                    && fontElement.ValueKind == JsonValueKind.String
                    && fontElement.GetString() is { } candidateFont
                    && !string.IsNullOrWhiteSpace(candidateFont))
                {
                    font = candidateFont;
                }
            }
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return new AppSettings(theme, font);
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var temporary = Path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { theme = settings.Theme, font = settings.Font }));
            File.Move(temporary, Path, overwrite: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
