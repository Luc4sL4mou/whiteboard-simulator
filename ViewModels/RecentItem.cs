namespace WhiteboardSimulator.ViewModels;

/// <summary>Linha do menu "Abrir recente".</summary>
public sealed class RecentItem
{
    public RecentItem(string path, bool pinned)
    {
        Path = path;
        Pinned = pinned;
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        Label = pinned ? $"★  {name}" : name;
    }

    public string Path { get; }
    public bool Pinned { get; }
    public string Label { get; }
    public string Tooltip => Path;
}
