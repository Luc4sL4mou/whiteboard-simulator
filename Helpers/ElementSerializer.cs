using System.Text.Json;
using System.Text.Json.Serialization;
using WhiteboardSimulator.Models;

namespace WhiteboardSimulator.Helpers;

/// <summary>Serialização, clonagem e área de transferência de elementos do quadro.</summary>
public static class ElementSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize(IEnumerable<BoardElement> elements) =>
        JsonSerializer.Serialize(elements.ToList(), Options);

    public static List<BoardElement> Deserialize(string json) =>
        JsonSerializer.Deserialize<List<BoardElement>>(json, Options) ?? new List<BoardElement>();

    /// <summary>
    /// Copia profunda com ids novos. Referências entre os elementos copiados (ligações, alfinete preso)
    /// são refeitas para apontar para as cópias; ligações sem os dois alfinetes no conjunto são descartadas.
    /// </summary>
    public static List<BoardElement> CloneSet(IEnumerable<BoardElement> source)
    {
        var originals = source.ToList();
        var copies = Deserialize(Serialize(originals));
        var map = new Dictionary<Guid, Guid>();

        for (int i = 0; i < copies.Count; i++)
        {
            map[originals[i].Id] = Guid.NewGuid();
            copies[i].Id = map[originals[i].Id];
        }

        var result = new List<BoardElement>();
        foreach (var copy in copies)
        {
            switch (copy)
            {
                case PinElement pin:
                    pin.AttachedToId = pin.AttachedToId is { } a && map.TryGetValue(a, out var na) ? na : null;
                    break;
                case ConnectorElement c:
                    if (!map.TryGetValue(c.FromId, out var f) || !map.TryGetValue(c.ToId, out var t)) continue;
                    c.FromId = f;
                    c.ToId = t;
                    break;
            }
            result.Add(copy);
        }
        return result;
    }
}
