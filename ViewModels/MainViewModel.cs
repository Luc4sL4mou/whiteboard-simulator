using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WhiteboardSimulator.Helpers;
using WhiteboardSimulator.Models;

namespace WhiteboardSimulator.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private const int MaxRecent = 10;

    private readonly DialogService _dialogs = new();
    private readonly DispatcherTimer _noticeTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly DispatcherTimer _autosaveTimer = new() { Interval = TimeSpan.FromSeconds(45) };

    private AppTheme _theme;
    private BoardDocument _defaults;   // aparência usada em quadros novos (a última escolhida)
    private BoardViewModel _activeBoard;
    private bool _isPaletteOpen;
    private string? _notice;

    public MainViewModel()
    {
        Tools = new ObservableCollection<ToolItemViewModel>
        {
            new(ToolType.Select,     "Selecionar",       "Icon.Select",    "V"),
            new(ToolType.Pen,        "Caneta",           "Icon.Pen",       "P"),
            new(ToolType.Eraser,     "Borracha",         "Icon.Eraser",    "E"),
            new(ToolType.Text,       "Texto",            "Icon.Text",      "T"),
            new(ToolType.Line,       "Linha",            "Icon.Line",      "L"),
            new(ToolType.Rectangle,  "Quadrado",         "Icon.Rectangle", "R"),
            new(ToolType.Ellipse,    "Círculo",          "Icon.Ellipse",   "O"),
            new(ToolType.Ruler,      "Régua",            "Icon.Ruler",     "G"),
            new(ToolType.StickyNote, "Post-it",          "Icon.StickyNote","N"),
            new(ToolType.Pin,        "Alfinete",         "Icon.Pin",       "A"),
            new(ToolType.Connector,  "Linha de ligação", "Icon.Connector", "C"),
            new(ToolType.Image,      "Imagem",           "Icon.Image",     "I"),
        };

        BuildToolOptions();
        ToolState.PropertyChanged += OnToolStateChanged;
        SyncTools();

        TintColors = new ObservableCollection<ColorOption>(
            BoardPalette.Tints.Select(t => new ColorOption(t.Name, t.Hex)));

        ThemeOptions = new ObservableCollection<ChoiceOption>
        {
            new("Escuro", AppTheme.Dark,  BoardBrushes.Backdrop(AppTheme.Dark),  v => SetTheme((AppTheme)v)),
            new("Claro",  AppTheme.Light, BoardBrushes.Backdrop(AppTheme.Light), v => SetTheme((AppTheme)v)),
        };

        FrameOptions = new ObservableCollection<ChoiceOption>
        {
            new("Metal",   BoardFrame.Metal, BoardBrushes.Frame(BoardFrame.Metal), v => ActiveBoard.ChangeFrame((BoardFrame)v)),
            new("Madeira", BoardFrame.Wood,  BoardBrushes.Frame(BoardFrame.Wood),  v => ActiveBoard.ChangeFrame((BoardFrame)v)),
        };

        var tint = TintColors[0].Brush;
        SurfaceOptions = new ObservableCollection<ChoiceOption>
        {
            new("Quadro branco", BoardSurface.Whiteboard, tint, v => ActiveBoard.ChangeSurface((BoardSurface)v)),
            new("Lousa preta",   BoardSurface.BlackSlate, BoardBrushes.Surface(BoardSurface.BlackSlate, tint), v => ActiveBoard.ChangeSurface((BoardSurface)v)),
            new("Lousa verde",   BoardSurface.GreenSlate, BoardBrushes.Surface(BoardSurface.GreenSlate, tint), v => ActiveBoard.ChangeSurface((BoardSurface)v)),
            new("Veludo verde",  BoardSurface.GreenFelt,  BoardBrushes.Surface(BoardSurface.GreenFelt, tint),  v => ActiveBoard.ChangeSurface((BoardSurface)v)),
        };

        // Restaura as preferências da última sessão.
        var saved = AppSettings.Load();
        _theme = saved.Theme;
        _defaults = new BoardDocument { Frame = saved.Frame, Surface = saved.Surface, Tint = saved.Tint };

        _activeBoard = CreateBoard(BoardViewModel.UntitledName, _defaults);
        Tabs = new ObservableCollection<BoardViewModel> { _activeBoard };
        _activeBoard.IsActive = true;
        _activeBoard.AppearanceChanged += OnActiveAppearanceChanged;
        _activeBoard.SelectionChanged += OnSelectionChanged;

        SelectToolCommand = new RelayCommand(p => { if (p is ToolItemViewModel t) ToolState.Tool = t.Type; });
        SelectTintCommand = new RelayCommand(p => { if (p is ColorOption c) ActiveBoard.ChangeTint(c.Name); });
        TogglePaletteCommand = new RelayCommand(_ => IsPaletteOpen = !IsPaletteOpen);

        CreateTextCommands();
        ThickerCommand = new RelayCommand(_ => ChangeThickness(true));
        ThinnerCommand = new RelayCommand(_ => ChangeThickness(false));
        EraserBiggerCommand = new RelayCommand(_ => ChangeEraserSize(true));
        EraserSmallerCommand = new RelayCommand(_ => ChangeEraserSize(false));

        NewBoardCommand = new RelayCommand(_ => AddBoard(CreateBoard(NextUntitledName(), _defaults)));
        OpenBoardCommand = new RelayCommand(_ => OpenBoard());
        OpenRecentCommand = new RelayCommand(p => { if (p is string path) OpenRecent(path); });
        ClearRecentCommand = new RelayCommand(_ => ClearRecent());
        SaveBoardCommand = new RelayCommand(_ => Save(ActiveBoard));
        SaveBoardAsCommand = new RelayCommand(_ => SaveAs(ActiveBoard));

        ExportPngCommand = new RelayCommand(_ => ExportImage(includeFrame: true));
        ExportSurfacePngCommand = new RelayCommand(_ => ExportImage(includeFrame: false));
        ExportPdfCommand = new RelayCommand(_ => ExportPdf());
        PrintCommand = new RelayCommand(_ => Print());
        ShareCommand = new RelayCommand(_ => Share());

        UndoCommand = new RelayCommand(_ => ActiveBoard.Undo.Undo(), _ => ActiveBoard.Undo.CanUndo);
        RedoCommand = new RelayCommand(_ => ActiveBoard.Undo.Redo(), _ => ActiveBoard.Undo.CanRedo);

        RenameBoardCommand = new RelayCommand(p => Rename(p as BoardViewModel ?? ActiveBoard));
        DuplicateBoardCommand = new RelayCommand(_ => Duplicate(ActiveBoard));
        ToggleFavoriteCommand = new RelayCommand(_ => ToggleFavorite(ActiveBoard));
        ShowInFolderCommand = new RelayCommand(_ => ShowInFolder(ActiveBoard), _ => HasFile(ActiveBoard));
        CloseTabCommand = new RelayCommand(p => CloseTab(p as BoardViewModel ?? ActiveBoard));
        SelectTabCommand = new RelayCommand(p => { if (p is BoardViewModel b) SelectBoard(b); });
        NextTabCommand = new RelayCommand(_ => CycleTab(+1));
        PreviousTabCommand = new RelayCommand(_ => CycleTab(-1));

        ZoomInCommand = new RelayCommand(_ => ZoomBy(1.25));
        ZoomOutCommand = new RelayCommand(_ => ZoomBy(1 / 1.25));
        ZoomResetCommand = new RelayCommand(_ => ActiveBoard.ResetView());

        SyncOptions();
        RefreshRecent();

        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            _notice = null;
            Notify(nameof(StatusText));
        };

        _autosaveTimer.Tick += (_, _) => AutosaveAll();
        _autosaveTimer.Start();
    }

    /// <summary>Preenchido pela janela; permite exportar/imprimir sem o ViewModel conhecer a interface.</summary>
    public IBoardView? BoardView { get; set; }

    public ObservableCollection<ToolItemViewModel> Tools { get; }
    public ObservableCollection<ColorOption> TintColors { get; }
    public ObservableCollection<ChoiceOption> ThemeOptions { get; }
    public ObservableCollection<ChoiceOption> FrameOptions { get; }
    public ObservableCollection<ChoiceOption> SurfaceOptions { get; }
    public ObservableCollection<BoardViewModel> Tabs { get; }
    public ObservableCollection<RecentItem> RecentItems { get; } = new();

    public ICommand SelectToolCommand { get; }
    public ICommand SelectTintCommand { get; }
    public ICommand TogglePaletteCommand { get; }
    public ICommand ThickerCommand { get; }
    public ICommand ThinnerCommand { get; }
    public ICommand EraserBiggerCommand { get; }
    public ICommand EraserSmallerCommand { get; }
    public ICommand NewBoardCommand { get; }
    public ICommand OpenBoardCommand { get; }
    public ICommand OpenRecentCommand { get; }
    public ICommand ClearRecentCommand { get; }
    public ICommand SaveBoardCommand { get; }
    public ICommand SaveBoardAsCommand { get; }
    public ICommand ExportPngCommand { get; }
    public ICommand ExportSurfacePngCommand { get; }
    public ICommand ExportPdfCommand { get; }
    public ICommand PrintCommand { get; }
    public ICommand ShareCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }
    public ICommand RenameBoardCommand { get; }
    public ICommand DuplicateBoardCommand { get; }
    public ICommand ToggleFavoriteCommand { get; }
    public ICommand ShowInFolderCommand { get; }
    public ICommand CloseTabCommand { get; }
    public ICommand SelectTabCommand { get; }
    public ICommand NextTabCommand { get; }
    public ICommand PreviousTabCommand { get; }
    public ICommand ZoomInCommand { get; }
    public ICommand ZoomOutCommand { get; }
    public ICommand ZoomResetCommand { get; }

    // ---- Ferramentas, painel e mensagens ----

    /// <summary>Ferramenta ativa e suas configurações (usadas pelo BoardCanvas).</summary>
    public ToolState ToolState { get; } = new();

    public ToolItemViewModel SelectedTool => Tools.First(t => t.Type == ToolState.Tool);

    public ObservableCollection<ToolOption> InkOptions { get; } = new();
    public ObservableCollection<ToolOption> ThicknessOptions { get; } = new();
    public ObservableCollection<ToolOption> TextColorOptions { get; } = new();
    public ObservableCollection<ToolOption> StickyColorOptions { get; } = new();
    public ObservableCollection<ToolOption> PinColorOptions { get; } = new();
    public ObservableCollection<ToolOption> RulerOptions { get; } = new();
    public ObservableCollection<ToolOption> LayerOptions { get; } = new();

    public string[] FontFamilies => InkPalette.Fonts;

    private void BuildToolOptions()
    {
        RulerOptions.Add(new ToolOption("Horizontal", false, null, 0, v => SetRuler(vertical: false)));
        RulerOptions.Add(new ToolOption("Vertical", true, null, 0, v => SetRuler(vertical: true)));
        LayerOptions.Add(new ToolOption("Para frente", true, null, 0, v => ActiveBoard.ChangeOrder(true)));
        LayerOptions.Add(new ToolOption("Para trás", false, null, 0, v => ActiveBoard.ChangeOrder(false)));

        foreach (var (name, hex) in InkPalette.Inks)
        {
            Brush fill = hex == InkPalette.Auto
                ? new LinearGradientBrush(new GradientStopCollection
                {
                    new GradientStop(Color.FromRgb(0x1F, 0x23, 0x28), 0.5), new GradientStop(Color.FromRgb(0xF5, 0xF5, 0xF0), 0.5)
                }, 45)
                : new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            InkOptions.Add(new ToolOption(name, hex, fill, 0, v => ChooseInk((string)v)));
            TextColorOptions.Add(new ToolOption(name, hex, fill, 0, v => ChooseTextColor((string)v)));
        }

        foreach (var (name, value) in InkPalette.Thicknesses)
            ThicknessOptions.Add(new ToolOption(name, value, null, value, v => ChooseThickness((double)v)));

        foreach (var (name, hex) in InkPalette.StickyColors)
            StickyColorOptions.Add(new ToolOption(name, hex, new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)), 0, v => ChooseSticky((string)v)));

        foreach (var (name, hex) in InkPalette.PinColors)
            PinColorOptions.Add(new ToolOption(name, hex, new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)), 0, v => ChoosePin((string)v)));

        SyncToolOptions();
    }

    private void SyncToolOptions()
    {
        foreach (var o in InkOptions) o.IsSelected = (string)o.Value == ToolState.Color;
        foreach (var o in ThicknessOptions) o.IsSelected = Math.Abs((double)o.Value - ToolState.Thickness) < 0.01;
        foreach (var o in StickyColorOptions) o.IsSelected = (string)o.Value == ToolState.StickyColor;
        foreach (var o in PinColorOptions) o.IsSelected = (string)o.Value == ToolState.PinColor;
    }

    private bool SelectionMode => ToolState.Tool == ToolType.Select;

    // Com a ferramenta Selecionar, mudar uma opção também altera os elementos selecionados.
    private void ChooseInk(string hex)
    {
        ToolState.Color = hex;
        SyncToolOptions();
        if (SelectionMode) ActiveBoard.ApplyStyleToSelection(ToolState.GetColor(ActiveBoard.Surface), null, null, null, null);
    }

    private void ChooseThickness(double value)
    {
        ToolState.Thickness = value;
        SyncToolOptions();
        if (SelectionMode) ActiveBoard.ApplyStyleToSelection(null, value, null, null, null);
    }

    private void ChooseSticky(string hex)
    {
        ToolState.StickyColor = hex;
        SyncToolOptions();
        if (SelectionMode) ActiveBoard.ApplyStyleToSelection(null, null, hex, null, null);
    }

    private void SetRuler(bool vertical)
    {
        var rulers = Selected.OfType<RulerElement>().ToList();
        if (rulers.Count == 0) rulers = ActiveBoard.Elements.OfType<RulerElement>().ToList();
        ActiveBoard.SetRulerOrientation(rulers, vertical);
        SyncRulerOptions();
    }

    private void SyncRulerOptions()
    {
        var ruler = Selected.OfType<RulerElement>().FirstOrDefault() ?? ActiveBoard.Elements.OfType<RulerElement>().FirstOrDefault();
        bool vertical = ruler != null && BoardViewModel.IsVertical(ruler);
        foreach (var o in RulerOptions) o.IsSelected = (bool)o.Value == vertical;
    }

    private void ChoosePin(string hex)
    {
        ToolState.PinColor = hex;
        SyncToolOptions();
        if (SelectionMode) ActiveBoard.ApplyStyleToSelection(null, null, null, hex, null);
    }

    private void OnToolStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ToolState.FontFamily))
        {
            // Escolher a fonte na caixa: vale para o trecho selecionado no editor ou para o texto/post-it selecionado.
            if (!_syncingFont && TextTargetIsDocument) FormatText(new TextFormatChange(FontFamily: ToolState.FontFamily));
            return;
        }
        if (e.PropertyName != nameof(ToolState.Tool)) return;
        SyncTools();
        NotifyToolOptions();
    }

    private void SyncTools()
    {
        foreach (var t in Tools) t.IsSelected = t.Type == ToolState.Tool;
        Notify(nameof(SelectedTool));
        Notify(nameof(StatusText));
    }

    private bool _syncingFont;

    private void OnSelectionChanged(object? sender, EventArgs e) => NotifyToolOptions();

    /// <summary>A caixa de fonte mostra a fonte do texto ou post-it selecionado (sem alterá-lo).</summary>
    private void SyncFontFromSelection()
    {
        if (BoardView?.IsEditingText == true) return;       // durante a edição quem manda é o trecho selecionado

        var family = Selected.OfType<RichTextElement>().Select(r => r.FontFamily).FirstOrDefault();

        if (family != null && family != ToolState.FontFamily)
        {
            _syncingFont = true;
            ToolState.FontFamily = family;
            _syncingFont = false;
        }
    }

    // Quais grupos de opções aparecem na barra flutuante: os da ferramenta ativa ou, com
    // "Selecionar", os que servem para o que está selecionado.
    private IReadOnlyList<BoardElement> Selected =>
        (object?)_activeBoard == null ? Array.Empty<BoardElement>() : _activeBoard.Selection;   // ainda nulo durante a construção

    public bool ShowInk =>
        ToolState.Tool is ToolType.Pen or ToolType.Line or ToolType.Rectangle or ToolType.Ellipse ||
        (SelectionMode && Selected.Any(e => e is StrokeElement or ShapeElement));

    public bool ShowThickness =>
        ToolState.Tool is ToolType.Pen or ToolType.Line or ToolType.Rectangle or ToolType.Ellipse ||
        (SelectionMode && Selected.Any(e => e is StrokeElement or ShapeElement));

    public bool ShowStickyColors =>
        ToolState.Tool == ToolType.StickyNote || (SelectionMode && Selected.Any(e => e is StickyNoteElement));

    public bool ShowPinColors =>
        ToolState.Tool == ToolType.Pin || (SelectionMode && Selected.Any(e => e is PinElement));

    // ---- Texto: fonte, tamanho (A − +), negrito, itálico, sublinhado e cor ----

    /// <summary>A barra de texto aparece com a ferramenta Texto, com texto/post-it selecionado ou durante a edição.</summary>
    public bool ShowTextFormat =>
        ToolState.Tool == ToolType.Text ||
        BoardView?.IsEditingText == true ||
        (SelectionMode && Selected.Any(e => e is RichTextElement));

    // Há um texto "de verdade" para formatar (editor aberto ou texto/post-it selecionado)?
    private bool TextTargetIsDocument =>
        BoardView?.IsEditingText == true || (SelectionMode && Selected.OfType<RichTextElement>().Any());

    private bool _isBold, _isItalic, _isUnderline;
    private string? _currentTextColor;

    public bool IsBold { get => _isBold; private set => Set(ref _isBold, value); }
    public bool IsItalic { get => _isItalic; private set => Set(ref _isItalic, value); }
    public bool IsUnderline { get => _isUnderline; private set => Set(ref _isUnderline, value); }

    /// <summary>Cor da letra no trecho selecionado (mostrada no botão de cor).</summary>
    public Brush TextColorBrush =>
        ElementView_BrushFor(_currentTextColor ?? (_activeBoard == null ? "#1F2328" : ToolState.GetColor(_activeBoard.Surface)));

    private static Brush ElementView_BrushFor(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    public ICommand TextLargerCommand { get; private set; } = null!;
    public ICommand TextSmallerCommand { get; private set; } = null!;
    public ICommand ToggleBoldCommand { get; private set; } = null!;
    public ICommand ToggleItalicCommand { get; private set; } = null!;
    public ICommand ToggleUnderlineCommand { get; private set; } = null!;

    private void CreateTextCommands()
    {
        const double step = 1.15;
        TextLargerCommand = new RelayCommand(_ => FormatText(new TextFormatChange(SizeFactor: step)));
        TextSmallerCommand = new RelayCommand(_ => FormatText(new TextFormatChange(SizeFactor: 1 / step)));
        ToggleBoldCommand = new RelayCommand(_ => FormatText(new TextFormatChange(Bold: !IsBold)));
        ToggleItalicCommand = new RelayCommand(_ => FormatText(new TextFormatChange(Italic: !IsItalic)));
        ToggleUnderlineCommand = new RelayCommand(_ => FormatText(new TextFormatChange(Underline: !IsUnderline)));
    }

    /// <summary>
    /// Aplica a formatação: ao trecho selecionado no editor; fora da edição, ao texto/post-it selecionado inteiro;
    /// com a ferramenta Texto e nada selecionado, vira o padrão do próximo texto.
    /// </summary>
    public void FormatText(TextFormatChange change)
    {
        if (BoardView?.IsEditingText == true)
        {
            BoardView.FormatEditor(change);         // o estado volta por OnEditorFormatChanged
            return;
        }

        if (SelectionMode && Selected.OfType<RichTextElement>().Any())
        {
            ActiveBoard.ApplyTextFormat(change);
            SyncTextFormatFromSelection();
            return;
        }

        // Padrão do próximo texto.
        if (change.FontFamily != null) ToolState.FontFamily = change.FontFamily;
        if (change.SizeFactor is { } f)
        {
            double next = Math.Round(ToolState.FontSize * f);
            if (Math.Abs(next - ToolState.FontSize) < 0.5) next = ToolState.FontSize + (f > 1 ? 1 : -1);
            ToolState.FontSize = Math.Max(6, Math.Min(300, next));
        }
        if (change.Bold is { } b) ToolState.Bold = b;
        if (change.Italic is { } i) ToolState.Italic = i;
        if (change.Underline is { } u) ToolState.Underline = u;
        if (change.Color != null) _currentTextColor = change.Color;
        SyncTextFormatFromSelection();
    }

    private void ChooseTextColor(string hex)
    {
        // "Automático" acompanha a superfície; as outras são cores fixas.
        string resolved = hex == InkPalette.Auto ? ToolState.GetColor(ActiveBoard.Surface) : hex;
        if (!TextTargetIsDocument) ToolState.Color = hex;      // padrão do próximo texto
        FormatText(new TextFormatChange(Color: resolved));
        _currentTextColor = resolved;
        Notify(nameof(TextColorBrush));
    }

    /// <summary>O editor avisa a formatação do trecho selecionado (ou null quando a edição termina).</summary>
    public void OnEditorFormatChanged(TextFormatState? state)
    {
        if (state == null)
        {
            SyncTextFormatFromSelection();
        }
        else
        {
            IsBold = state.Bold;
            IsItalic = state.Italic;
            IsUnderline = state.Underline;
            _currentTextColor = state.Color;
            Notify(nameof(TextColorBrush));
            if (state.FontFamily != null && state.FontFamily != ToolState.FontFamily)
            {
                _syncingFont = true;
                ToolState.FontFamily = state.FontFamily;
                _syncingFont = false;
            }
        }
        Notify(nameof(ShowTextFormat));
        Notify(nameof(HasToolOptions));
    }

    /// <summary>Fora da edição: B / I / U e cor mostram o estado do texto selecionado (ou o padrão do próximo texto).</summary>
    private void SyncTextFormatFromSelection()
    {
        if (BoardView?.IsEditingText == true || (object?)_activeBoard == null) return;

        var rich = Selected.OfType<RichTextElement>().FirstOrDefault();
        if (rich != null && SelectionMode)
        {
            IsBold = rich.Bold;
            IsItalic = rich.Italic;
            IsUnderline = rich.Underline;
            _currentTextColor = rich.DefaultColor;
        }
        else
        {
            IsBold = ToolState.Bold;
            IsItalic = ToolState.Italic;
            IsUnderline = ToolState.Underline;
            _currentTextColor = null;
        }
        Notify(nameof(TextColorBrush));
    }

    public bool ShowRuler => SelectionMode && Selected.Any(e => e is RulerElement);

    // ---- Borracha: tamanho ----

    /// <summary>Ferramentas de traço: caneta e formas dividem a mesma espessura.</summary>
    public bool IsStrokeTool => ToolState.Tool is ToolType.Pen or ToolType.Line or ToolType.Rectangle or ToolType.Ellipse;

    public string ThicknessText => $"{ToolState.Thickness:0.#}";

    /// <summary>Engrossa ou afina o traço em passos de ~25% (mínimo de 1 por passo).</summary>
    public void ChangeThickness(bool thicker)
    {
        double size = ToolState.Thickness;
        double step = Math.Max(1, Math.Round(size * 0.25));
        ToolState.Thickness = thicker ? size + step : size - step;
        SyncToolOptions();
        Notify(nameof(ThicknessText));
        if (SelectionMode) ActiveBoard.ApplyStyleToSelection(null, ToolState.Thickness, null, null, null);
    }

    public bool ShowEraser => ToolState.Tool == ToolType.Eraser;
    public string EraserSizeText => $"{ToolState.EraserSize:0}";

    /// <summary>Aumenta ou diminui a borracha em passos de ~25% (mínimo de 2 px por passo).</summary>
    public void ChangeEraserSize(bool bigger)
    {
        double size = ToolState.EraserSize;
        double step = Math.Max(2, Math.Round(size * 0.25));
        ToolState.EraserSize = bigger ? size + step : size - step;
        Notify(nameof(EraserSizeText));
    }

    // Camadas: para qualquer coisa selecionada que possa mudar de camada (imagem, post-it, texto, desenho...).
    public bool ShowLayers => SelectionMode && Selected.Any(BoardViewModel.HasLayer);

    public bool HasToolOptions =>
        ShowInk || ShowThickness || ShowTextFormat || ShowStickyColors || ShowPinColors || ShowRuler || ShowLayers || ShowEraser;

    private void NotifyToolOptions()
    {
        SyncFontFromSelection();
        SyncToolOptions();
        SyncRulerOptions();
        SyncTextFormatFromSelection();
        Notify(nameof(ShowTextFormat));
        Notify(nameof(ShowEraser));
        Notify(nameof(EraserSizeText));
        Notify(nameof(ThicknessText));
        Notify(nameof(IsStrokeTool));
        Notify(nameof(ShowRuler));
        Notify(nameof(ShowLayers));
        Notify(nameof(ShowInk));
        Notify(nameof(ShowThickness));
        Notify(nameof(ShowStickyColors));
        Notify(nameof(ShowPinColors));
        Notify(nameof(HasToolOptions));
    }

    public bool IsPaletteOpen
    {
        get => _isPaletteOpen;
        set => Set(ref _isPaletteOpen, value);
    }

    /// <summary>Mensagem passageira (ex.: "Salvo em...") ou, sem ela, a ferramenta atual.</summary>
    public string StatusText => _notice ?? $"Ferramenta: {SelectedTool.Name}";
    private void ShowNotice(string message)
    {
        _notice = message;
        Notify(nameof(StatusText));
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    // ---- Tema (preferência do app) ----

    public Brush WindowBackground => BoardBrushes.Backdrop(_theme);
    public bool IsDarkTheme => _theme == AppTheme.Dark;
    public Color TitleBarColor => BoardBrushes.BackdropTop(_theme);
    public Color TitleTextColor => BoardBrushes.TitleText(_theme);

    private void SetTheme(AppTheme theme)
    {
        if (_theme == theme) return;
        _theme = theme;
        SyncOptions();
        Notify(nameof(IsDarkTheme));
        Notify(nameof(TitleBarColor));
        Notify(nameof(TitleTextColor));
        Notify(nameof(WindowBackground));
        SaveSettings();
    }

    private void SaveSettings()
    {
        // Recarrega antes de gravar para não pisar em outras preferências (aviso da bandeja, recentes).
        var settings = AppSettings.Load();
        settings.Theme = _theme;
        settings.Frame = _defaults.Frame;
        settings.Surface = _defaults.Surface;
        settings.Tint = _defaults.Tint;
        settings.Save();
    }

    // ---- Quadros abertos (abas) ----

    public BoardViewModel ActiveBoard
    {
        get => _activeBoard;
        private set
        {
            var old = _activeBoard;
            if (!Set(ref _activeBoard, value)) return;
            old.IsActive = false;
            old.AppearanceChanged -= OnActiveAppearanceChanged;
            old.SelectionChanged -= OnSelectionChanged;
            value.IsActive = true;
            value.AppearanceChanged += OnActiveAppearanceChanged;
            value.SelectionChanged += OnSelectionChanged;
            NotifyToolOptions();
            Notify(nameof(WindowTitle));
            Notify(nameof(FavoriteMenuText));
            SyncOptions();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string WindowTitle => ActiveBoard.WindowTitle;

    private BoardViewModel CreateBoard(string name, BoardDocument appearance, Guid? id = null)
    {
        var board = new BoardViewModel(name, new BoardDocument
        {
            Frame = appearance.Frame,
            Surface = appearance.Surface,
            Tint = appearance.Tint,
            Elements = new List<BoardElement>(appearance.Elements)
        }, id);
        board.Undo.Changed += (_, _) =>
        {
            CommandManager.InvalidateRequerySuggested();
            if (ReferenceEquals(board, _activeBoard)) NotifyToolOptions();   // ex.: desfazer girou a régua
        };
        board.PropertyChanged += (_, e) =>
        {
            if (ReferenceEquals(board, _activeBoard) && e.PropertyName == nameof(BoardViewModel.WindowTitle))
                Notify(nameof(WindowTitle));
        };
        return board;
    }

    private void AddBoard(BoardViewModel board)
    {
        Tabs.Add(board);
        SelectBoard(board);
    }

    private void SelectBoard(BoardViewModel board)
    {
        if (Tabs.Contains(board)) ActiveBoard = board;
    }

    private void CycleTab(int step)
    {
        if (Tabs.Count < 2) return;
        int index = (Tabs.IndexOf(ActiveBoard) + step + Tabs.Count) % Tabs.Count;
        SelectBoard(Tabs[index]);
    }

    private string NextUntitledName()
    {
        for (int n = 1; ; n++)
        {
            var name = n == 1 ? BoardViewModel.UntitledName : $"{BoardViewModel.UntitledName} {n}";
            if (!Tabs.Any(t => t.Name == name)) return name;
        }
    }

    /// <summary>Muda moldura/superfície/cor do quadro ativo: vira o padrão de quadros novos.</summary>
    private void OnActiveAppearanceChanged(object? sender, EventArgs e)
    {
        // Só a aparência vira padrão de quadros novos (nunca os elementos).
        _defaults = new BoardDocument { Frame = ActiveBoard.Frame, Surface = ActiveBoard.Surface, Tint = ActiveBoard.TintName };
        SyncOptions();
        SaveSettings();
    }

    private void SyncOptions()
    {
        foreach (var o in ThemeOptions) o.IsSelected = Equals(o.Value, _theme);
        foreach (var o in FrameOptions) o.IsSelected = Equals(o.Value, ActiveBoard.Frame);
        foreach (var o in SurfaceOptions) o.IsSelected = Equals(o.Value, ActiveBoard.Surface);
        foreach (var t in TintColors) t.IsSelected = t.Name == ActiveBoard.TintName;
    }

    /// <summary>Fecha uma aba (perguntando sobre alterações). Sempre sobra pelo menos um quadro.</summary>
    private void CloseTab(BoardViewModel board)
    {
        if (!ConfirmClose(board)) return;

        RecoveryService.Delete(board.Id);
        int index = Tabs.IndexOf(board);
        Tabs.Remove(board);

        if (Tabs.Count == 0)
            AddBoard(CreateBoard(NextUntitledName(), _defaults));
        else if (ReferenceEquals(board, ActiveBoard))
            SelectBoard(Tabs[Math.Min(index, Tabs.Count - 1)]);
    }

    /// <summary>Pergunta o que fazer com alterações não salvas do quadro. false = cancelou.</summary>
    private bool ConfirmClose(BoardViewModel board)
    {
        CommitPendingEdit();
        if (!board.IsDirty) return true;
        SelectBoard(board);
        return _dialogs.AskSaveChanges(board.Name) switch
        {
            true => Save(board),
            false => true,
            null => false
        };
    }

    /// <summary>Para sair do app: percorre todos os quadros alterados. false = o usuário cancelou.</summary>
    public bool ConfirmDiscardAll()
    {
        foreach (var board in Tabs.Where(b => b.IsDirty).ToList())
            if (!ConfirmClose(board)) return false;
        return true;
    }

    public bool HasUnsavedChanges
    {
        get
        {
            CommitPendingEdit();     // o que está sendo digitado conta como alteração
            return Tabs.Any(b => b.IsDirty);
        }
    }

    /// <summary>Saída confirmada: apaga as cópias de recuperação (a sessão terminou de forma normal).</summary>
    public void DiscardRecovery()
    {
        foreach (var board in Tabs) RecoveryService.Delete(board.Id);
    }

    // ---- Abrir / salvar ----

    private void OpenBoard()
    {
        var path = _dialogs.PickOpenPath();
        if (path != null) OpenPath(path);
    }

    private void OpenRecent(string path)
    {
        if (!File.Exists(path))
        {
            _dialogs.ShowError($"O arquivo não foi encontrado:\n{path}");
            RemoveRecent(path);
            return;
        }
        OpenPath(path);
    }

    private bool OpenPath(string path)
    {
        // Já está aberto em uma aba: só mostra.
        var existing = Tabs.FirstOrDefault(t =>
            t.FilePath != null && string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            SelectBoard(existing);
            return true;
        }

        bool isPack = string.Equals(Path.GetExtension(path), SharePackage.Extension, StringComparison.OrdinalIgnoreCase);
        BoardDocument doc;
        try
        {
            doc = isPack ? SharePackage.Load(path) : ProjectFile.Load(path);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            _dialogs.ShowError($"Não foi possível abrir o quadro.\n\n{ex.Message}");
            return false;
        }

        var board = CreateBoard(Path.GetFileNameWithoutExtension(path), doc);
        if (isPack)
            board.MarkDirty();            // veio de um pacote: precisa ser salvo como .wboard
        else
            board.FilePath = path;

        // Aproveita a aba vazia inicial em vez de empilhar uma aba em branco.
        var current = ActiveBoard;
        if (current.FilePath == null && !current.IsDirty && !current.Undo.CanUndo && Tabs.Count == 1)
        {
            Tabs[0] = board;
            ActiveBoard = board;
        }
        else
        {
            AddBoard(board);
        }

        if (!isPack) AddRecent(path);
        ShowNotice($"Aberto: {board.Name}");
        return true;
    }

    /// <summary>Salva no arquivo do quadro (ou pergunta onde, se for novo). false = cancelado/falhou.</summary>
    public bool Save(BoardViewModel board) => board.FilePath == null ? SaveAs(board) : WriteTo(board, board.FilePath);

    public bool SaveAs(BoardViewModel board)
    {
        var suggested = board.Name.StartsWith(BoardViewModel.UntitledName) ? "Meu quadro" : board.Name;
        var folder = board.FilePath != null ? Path.GetDirectoryName(board.FilePath) : null;
        var path = _dialogs.PickSavePath("Salvar quadro como", ProjectFile.DialogFilter, ProjectFile.Extension, suggested, folder);
        return path != null && WriteTo(board, path);
    }

    /// <summary>Grava no quadro o texto que ainda está sendo digitado (antes de salvar, fechar, duplicar, compartilhar...).</summary>
    public void CommitPendingEdit() => BoardView?.CommitPendingEdit();

    private bool WriteTo(BoardViewModel board, string path)
    {
        CommitPendingEdit();
        try
        {
            ProjectFile.Save(path, board.ToDocument());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError($"Não foi possível salvar o quadro.\n\n{ex.Message}");
            return false;
        }

        board.FilePath = path;
        board.Name = Path.GetFileNameWithoutExtension(path);
        board.MarkSaved();
        RecoveryService.Delete(board.Id);
        AddRecent(path);
        Notify(nameof(FavoriteMenuText));
        ShowNotice($"Salvo: {board.Name}");
        return true;
    }

    // ---- Recentes e favoritos ----

    private void AddRecent(string path)
    {
        var settings = AppSettings.Load();
        var existing = settings.Recent.FirstOrDefault(r => SamePath(r.Path, path));
        bool pinned = existing?.Pinned ?? false;
        if (existing != null) settings.Recent.Remove(existing);
        settings.Recent.Insert(0, new RecentFile { Path = path, Pinned = pinned });

        // Mantém todos os favoritos e no máximo MaxRecent comuns.
        var normal = settings.Recent.Where(r => !r.Pinned).Take(MaxRecent).ToList();
        settings.Recent = settings.Recent.Where(r => r.Pinned).Concat(normal).ToList();
        settings.Save();
        RefreshRecent();
    }

    private void RemoveRecent(string path)
    {
        var settings = AppSettings.Load();
        settings.Recent.RemoveAll(r => SamePath(r.Path, path));
        settings.Save();
        RefreshRecent();
    }

    private void ClearRecent()
    {
        var settings = AppSettings.Load();
        settings.Recent.RemoveAll(r => !r.Pinned);
        settings.Save();
        RefreshRecent();
        ShowNotice("Lista de recentes limpa (favoritos mantidos)");
    }

    private void RefreshRecent()
    {
        var recent = AppSettings.Load().Recent;
        RecentItems.Clear();
        foreach (var r in recent.Where(r => r.Pinned)) RecentItems.Add(new RecentItem(r.Path, true));
        foreach (var r in recent.Where(r => !r.Pinned)) RecentItems.Add(new RecentItem(r.Path, false));
        Notify(nameof(HasRecent));
        Notify(nameof(FavoriteMenuText));
    }

    public bool HasRecent => RecentItems.Count > 0;

    public string FavoriteMenuText =>
        ActiveBoard.FilePath != null && AppSettings.Load().Recent.Any(r => r.Pinned && SamePath(r.Path, ActiveBoard.FilePath))
            ? "Remover dos favoritos"
            : "Fixar nos favoritos";

    private void ToggleFavorite(BoardViewModel board)
    {
        if (board.FilePath == null)
        {
            _dialogs.ShowError("Salve o quadro primeiro para poder fixá-lo nos favoritos.");
            return;
        }

        var settings = AppSettings.Load();
        var entry = settings.Recent.FirstOrDefault(r => SamePath(r.Path, board.FilePath));
        if (entry == null)
        {
            entry = new RecentFile { Path = board.FilePath };
            settings.Recent.Insert(0, entry);
        }
        entry.Pinned = !entry.Pinned;
        settings.Save();
        RefreshRecent();
        ShowNotice(entry.Pinned ? "Fixado nos favoritos" : "Removido dos favoritos");
    }

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // ---- Renomear, duplicar, mostrar na pasta ----

    private void Rename(BoardViewModel board)
    {
        var name = _dialogs.PromptText("Renomear quadro", "Novo nome do quadro:", board.Name);
        if (name == null || name == board.Name) return;

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            _dialogs.ShowError("O nome não pode conter os caracteres \\ / : * ? \" < > |");
            return;
        }

        if (board.FilePath != null)
        {
            var newPath = Path.Combine(Path.GetDirectoryName(board.FilePath)!, name + Path.GetExtension(board.FilePath));
            if (File.Exists(newPath) && !SamePath(newPath, board.FilePath))
            {
                _dialogs.ShowError("Já existe um arquivo com esse nome na mesma pasta.");
                return;
            }

            try
            {
                if (File.Exists(board.FilePath) && !SamePath(newPath, board.FilePath)) File.Move(board.FilePath, newPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _dialogs.ShowError($"Não foi possível renomear o arquivo.\n\n{ex.Message}");
                return;
            }

            var oldPath = board.FilePath;
            board.FilePath = newPath;

            var settings = AppSettings.Load();
            foreach (var r in settings.Recent.Where(r => SamePath(r.Path, oldPath))) r.Path = newPath;
            settings.Save();
            RefreshRecent();
        }

        board.Name = name;
    }

    private void Duplicate(BoardViewModel board)
    {
        CommitPendingEdit();
        var copy = CreateBoard($"{board.Name} (cópia)", board.CloneDocument());
        copy.MarkDirty();
        AddBoard(copy);
        ShowNotice("Quadro duplicado");
    }

    private static bool HasFile(BoardViewModel board) => board.FilePath != null && File.Exists(board.FilePath);

    private static void ShowInFolder(BoardViewModel board)
    {
        if (HasFile(board)) RevealInExplorer(board.FilePath!);
    }

    private static void RevealInExplorer(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });

    // ---- Exportar, imprimir, compartilhar ----

    private void ExportImage(bool includeFrame)
    {
        if (BoardView == null) return;
        var board = ActiveBoard;
        var suffix = includeFrame ? "" : " (superfície)";
        var path = _dialogs.PickSavePath("Exportar como PNG", "Imagem PNG (*.png)|*.png", ".png",
            board.Name + suffix, FolderOf(board));
        if (path == null) return;

        try
        {
            var bitmap = BoardView.RenderBoard(includeFrame, 2, opaque: false);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError($"Não foi possível exportar a imagem.\n\n{ex.Message}");
            return;
        }

        ShowNotice($"Exportado: {Path.GetFileName(path)}");
    }

    private void ExportPdf()
    {
        if (BoardView == null) return;
        var board = ActiveBoard;
        var path = _dialogs.PickSavePath("Exportar como PDF", "Documento PDF (*.pdf)|*.pdf", ".pdf",
            board.Name, FolderOf(board));
        if (path == null) return;

        try
        {
            var bitmap = BoardView.RenderBoard(true, 2, opaque: true);
            var opaque = new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0);
            var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
            encoder.Frames.Add(BitmapFrame.Create(opaque));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            PdfWriter.WriteImagePage(path, ms.ToArray(), bitmap.PixelWidth, bitmap.PixelHeight);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError($"Não foi possível exportar o PDF.\n\n{ex.Message}");
            return;
        }

        ShowNotice($"Exportado: {Path.GetFileName(path)}");
    }

    private void Print()
    {
        if (BoardView == null) return;

        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true) return;

        var bitmap = BoardView.RenderBoard(true, 3, opaque: true);
        double margin = 36;
        double availW = dialog.PrintableAreaWidth - 2 * margin;
        double availH = dialog.PrintableAreaHeight - 2 * margin;
        double scale = Math.Min(availW / bitmap.Width, availH / bitmap.Height);
        double w = bitmap.Width * scale, h = bitmap.Height * scale;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawImage(bitmap, new Rect((dialog.PrintableAreaWidth - w) / 2, (dialog.PrintableAreaHeight - h) / 2, w, h));

        dialog.PrintVisual(visual, $"Whiteboard Simulator - {ActiveBoard.Name}");
        ShowNotice("Enviado para a impressora");
    }

    private void Share()
    {
        CommitPendingEdit();
        var board = ActiveBoard;
        var path = _dialogs.PickSavePath("Compartilhar quadro", "Pacote compartilhável (*.wbpack)|*.wbpack",
            SharePackage.Extension, board.Name, FolderOf(board));
        if (path == null) return;

        try
        {
            SharePackage.Save(path, board.ToDocument());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError($"Não foi possível criar o pacote.\n\n{ex.Message}");
            return;
        }

        ShowNotice($"Pacote criado: {Path.GetFileName(path)}");
        RevealInExplorer(path);
    }

    private static string? FolderOf(BoardViewModel board) =>
        board.FilePath != null ? Path.GetDirectoryName(board.FilePath) : null;

    // ---- Zoom ----

    public void ZoomBy(double factor) => ActiveBoard.Zoom = Math.Round(ActiveBoard.Zoom * factor, 2);

    // ---- Salvamento automático e recuperação ----

    private void AutosaveAll()
    {
        foreach (var board in Tabs)
        {
            if (board.IsDirty)
                RecoveryService.Write(new RecoveryEntry
                {
                    Id = board.Id,
                    Name = board.Name,
                    OriginalPath = board.FilePath,
                    Document = board.ToDocument()
                });
            else
                RecoveryService.Delete(board.Id);
        }
    }

    /// <summary>Na abertura: oferece restaurar quadros que sobraram de uma sessão que terminou de forma inesperada.</summary>
    public void OfferRecovery()
    {
        var entries = RecoveryService.LoadAll().Where(e => Tabs.All(t => t.Id != e.Id)).ToList();
        if (entries.Count == 0) return;

        var plural = entries.Count == 1 ? "1 quadro não salvo" : $"{entries.Count} quadros não salvos";
        bool restore = _dialogs.Confirm(
            $"Encontrei {plural} de uma sessão anterior (o app pode ter sido encerrado de forma inesperada).\n\nDeseja restaurar?");

        if (!restore)
        {
            foreach (var e in entries) RecoveryService.Delete(e.Id);
            return;
        }

        bool replaceBlank = Tabs.Count == 1 && !ActiveBoard.IsDirty && ActiveBoard.FilePath == null && !ActiveBoard.Undo.CanUndo;
        BoardViewModel? last = null;
        foreach (var e in entries)
        {
            var board = CreateBoard(e.Name, e.Document, e.Id);
            if (e.OriginalPath != null && File.Exists(e.OriginalPath)) board.FilePath = e.OriginalPath;
            board.MarkDirty();
            if (replaceBlank)
            {
                Tabs[0] = board;
                ActiveBoard = board;
                replaceBlank = false;
            }
            else
            {
                Tabs.Add(board);
            }
            last = board;
        }

        if (last != null) SelectBoard(last);
        ShowNotice("Quadros restaurados");
    }
}
