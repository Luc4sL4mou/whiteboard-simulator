using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using WhiteboardSimulator.Models;

namespace WhiteboardSimulator.Helpers;

/// <summary>Cópia de segurança de um quadro com alterações não salvas.</summary>
public sealed class RecoveryEntry
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? OriginalPath { get; set; }
    public BoardDocument Document { get; set; } = new();
}

/// <summary>
/// Salvamento automático: guarda cópias dos quadros alterados em %AppData%\WhiteboardSimulator\recovery.
/// As cópias somem ao salvar/fechar o quadro; se o app cair, sobram para serem restauradas na próxima abertura.
/// O arquivo original do usuário nunca é tocado.
/// </summary>
public static class RecoveryService
{
    private static string Folder => Path.Combine(AppPaths.DataDir, "recovery");

    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static string PathFor(Guid id) => Path.Combine(Folder, id + ".json");

    public static void Write(RecoveryEntry entry)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(PathFor(entry.Id), JsonSerializer.Serialize(entry, Options));
        }
        catch { /* sem espaço/permissão: o salvamento automático é só uma rede de segurança */ }
    }

    public static void Delete(Guid id)
    {
        try { File.Delete(PathFor(id)); } catch { }
    }

    public static List<RecoveryEntry> LoadAll()
    {
        var list = new List<RecoveryEntry>();
        try
        {
            if (!Directory.Exists(Folder)) return list;
            foreach (var file in Directory.GetFiles(Folder, "*.json"))
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<RecoveryEntry>(File.ReadAllText(file), Options);
                    if (entry != null) list.Add(entry);
                    else File.Delete(file);
                }
                catch { File.Delete(file); }
            }
        }
        catch { }
        return list;
    }
}
