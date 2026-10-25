using System.IO;

namespace WhiteboardSimulator.Helpers;

/// <summary>
/// Pasta onde o app guarda preferências e cópias de recuperação (%AppData%\WhiteboardSimulator).
/// A variável de ambiente WBS_DATA_DIR redireciona para outra pasta (usada nos testes automáticos,
/// para nunca mexer nos dados reais do usuário).
/// </summary>
public static class AppPaths
{
    public static string DataDir
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("WBS_DATA_DIR");
            return !string.IsNullOrWhiteSpace(overridden)
                ? overridden
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WhiteboardSimulator");
        }
    }
}
