using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using WhiteboardSimulator.Models;

namespace WhiteboardSimulator.Helpers;

public sealed class RecentFile
{
    public string Path { get; set; } = "";
    public bool Pinned { get; set; }
}

/// <summary>Preferências do app, guardadas em %AppData%\WhiteboardSimulator\settings.json.</summary>
public sealed class AppSettings
{
    private static string FilePath => Path.Combine(AppPaths.DataDir, "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Quando true, o aviso em post-it ao ir para a bandeja não aparece mais.</summary>
    public bool HideTrayNotice { get; set; }

    // Aparência (padrão de fábrica: tema escuro, quadro branco com moldura de madeira).
    public AppTheme Theme { get; set; } = AppTheme.Dark;
    public BoardFrame Frame { get; set; } = BoardFrame.Wood;
    public BoardSurface Surface { get; set; } = BoardSurface.Whiteboard;
    public string Tint { get; set; } = "Branco suave";

    // Pomodoro (minutos; ciclos = focos até a pausa longa).
    public int PomodoroFocusMinutes { get; set; } = 25;
    public int PomodoroShortBreakMinutes { get; set; } = 5;
    public int PomodoroLongBreakMinutes { get; set; } = 15;
    public int PomodoroCycles { get; set; } = 4;
    public bool PomodoroLocked { get; set; }
    public double PomodoroBackgroundOpacity { get; set; } = 0.8;
    public double? PomodoroLeft { get; set; }
    public double? PomodoroTop { get; set; }

    /// <summary>Quadros abertos/salvos recentemente (favoritos primeiro).</summary>
    public List<RecentFile> Recent { get; set; } = new();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
        }
        catch { /* arquivo corrompido: volta ao padrão */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch { /* sem permissão de escrita: ignora */ }
    }
}
