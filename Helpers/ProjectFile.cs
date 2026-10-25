using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using WhiteboardSimulator.Models;

namespace WhiteboardSimulator.Helpers;

/// <summary>Leitura e gravação de arquivos de quadro (.wboard, JSON).</summary>
public static class ProjectFile
{
    public const string Extension = ".wboard";
    public const string DialogFilter = "Quadro do Whiteboard Simulator (*.wboard)|*.wboard";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void Save(string path, BoardDocument document) =>
        File.WriteAllText(path, JsonSerializer.Serialize(document, Options));

    public static BoardDocument Load(string path) =>
        JsonSerializer.Deserialize<BoardDocument>(File.ReadAllText(path), Options)
        ?? throw new InvalidDataException("Arquivo de quadro vazio.");
}
