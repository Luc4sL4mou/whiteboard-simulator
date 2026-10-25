using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using WhiteboardSimulator.Models;

namespace WhiteboardSimulator.Helpers;

/// <summary>
/// Pacote de compartilhamento (.wbpack): um ZIP com o quadro (board.json) e, nas próximas fases,
/// as imagens coladas (pasta assets/), para o quadro abrir igual em outro computador.
/// </summary>
public static class SharePackage
{
    public const string Extension = ".wbpack";
    private const string BoardEntry = "board.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void Save(string path, BoardDocument document)
    {
        if (File.Exists(path)) File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = zip.CreateEntry(BoardEntry, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(JsonSerializer.Serialize(document, Options));
    }

    public static BoardDocument Load(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry(BoardEntry) ?? throw new InvalidDataException("Pacote sem board.json.");
        using var reader = new StreamReader(entry.Open());
        return JsonSerializer.Deserialize<BoardDocument>(reader.ReadToEnd(), Options)
               ?? throw new InvalidDataException("Pacote de quadro vazio.");
    }
}
