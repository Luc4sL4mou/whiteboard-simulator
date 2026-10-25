using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WhiteboardSimulator.Helpers;
using WhiteboardSimulator.Models;
using WhiteboardSimulator.ViewModels;

namespace WhiteboardSimulator;

public partial class MainWindow : Window, IBoardView
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmBorderColor = 34;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private bool _isFullScreen;

    public MainWindow()
    {
        InitializeComponent();

        var vm = ViewModel;
        vm.BoardView = this;
        RegisterEditCommands();
        BoardArea.EditorFormatChanged += (_, state) => vm.OnEditorFormatChanged(state);

        // A barra de título acompanha o tema: refaz as cores quando o tema muda.
        SourceInitialized += (_, _) => ApplyTitleBarTheme();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.TitleBarColor)) ApplyTitleBarTheme();
        };

        // Depois que a janela aparece, oferece restaurar quadros de uma sessão que caiu.
        Loaded += (_, _) => Dispatcher.InvokeAsync(vm.OfferRecovery, System.Windows.Threading.DispatcherPriority.Background);
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    // ---- Barra de título nativa acompanhando o tema ----

    private void ApplyTitleBarTheme()
    {
        var vm = ViewModel;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        SetAttribute(hwnd, DwmUseImmersiveDarkMode, vm.IsDarkTheme ? 1 : 0);
        SetAttribute(hwnd, DwmCaptionColor, ToColorRef(vm.TitleBarColor));
        SetAttribute(hwnd, DwmBorderColor, ToColorRef(vm.TitleBarColor));
        SetAttribute(hwnd, DwmTextColor, ToColorRef(vm.TitleTextColor));
    }

    private static void SetAttribute(IntPtr hwnd, int attribute, int value) =>
        DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));

    // COLORREF = 0x00BBGGRR
    private static int ToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    // ---- Menus suspensos da barra de projetos ----

    private static void Toggle(Popup popup) => popup.IsOpen = !popup.IsOpen;

    private void OnTextColorClick(object sender, RoutedEventArgs e) => Toggle(TextColorPopup);
    private void OnRecentClick(object sender, RoutedEventArgs e) => Toggle(RecentPopup);
    private void OnExportClick(object sender, RoutedEventArgs e) => Toggle(ExportPopup);
    private void OnMoreClick(object sender, RoutedEventArgs e) => Toggle(MorePopup);

    /// <summary>Qualquer botão dentro de um menu fecha o menu depois de executar o comando.</summary>
    private void OnPopupButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Parent: Popup popup })
            Dispatcher.BeginInvoke(() => popup.IsOpen = false);
    }

    // ---- Editar: copiar, recortar, colar, selecionar tudo, excluir (comandos padrão do Windows) ----

    private void RegisterEditCommands()
    {
        void Bind(RoutedUICommand command, Action action, Func<bool>? canExecute = null) =>
            CommandBindings.Add(new CommandBinding(command,
                (_, _) => action(),
                (_, e) => { e.CanExecute = canExecute?.Invoke() ?? true; e.Handled = true; }));

        // Dentro de uma caixa de texto em edição, a própria caixa trata Ctrl+C/X/V/A (fica mais perto no caminho do evento).
        Bind(ApplicationCommands.Copy, BoardArea.Copy, () => BoardArea.HasSelection);
        Bind(ApplicationCommands.Cut, BoardArea.Cut, () => BoardArea.HasSelection);
        Bind(ApplicationCommands.Paste, BoardArea.Paste);
        Bind(ApplicationCommands.SelectAll, BoardArea.SelectAll);
        Bind(ApplicationCommands.Delete, BoardArea.DeleteSelection, () => BoardArea.HasSelection);
    }

    // ---- Teclado: ferramentas, setas e tela cheia ----

    private static readonly Dictionary<Key, ToolType> ToolKeys = new()
    {
        [Key.V] = ToolType.Select,
        [Key.P] = ToolType.Pen,
        [Key.E] = ToolType.Eraser,
        [Key.T] = ToolType.Text,
        [Key.L] = ToolType.Line,
        [Key.R] = ToolType.Rectangle,
        [Key.O] = ToolType.Ellipse,
        [Key.G] = ToolType.Ruler,
        [Key.N] = ToolType.StickyNote,
        [Key.A] = ToolType.Pin,
        [Key.C] = ToolType.Connector,
        [Key.I] = ToolType.Image,
    };

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            ToggleFullScreen();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && _isFullScreen)
        {
            ToggleFullScreen();
            e.Handled = true;
            return;
        }

        // Os atalhos abaixo não valem enquanto o usuário digita (texto do quadro).
        bool typing = Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase;
        var mods = Keyboard.Modifiers;

        if (!typing && mods == ModifierKeys.Control && e.Key == Key.D)
        {
            BoardArea.DuplicateSelection();      // Ctrl+D: duplica o que está selecionado
            e.Handled = true;
            return;
        }

        if (!typing && (TryHandleEraserKey(e.Key, mods) || TryHandleStrokeKey(e.Key, mods)))
        {
            e.Handled = true;
            return;
        }

        if (!typing && mods == ModifierKeys.None && ToolKeys.TryGetValue(e.Key, out var tool))
        {
            ViewModel.ToolState.Tool = tool;
            e.Handled = true;
            return;
        }

        if (!typing && (mods == ModifierKeys.None || mods == ModifierKeys.Shift) && BoardArea.HasSelection)
        {
            double step = mods == ModifierKeys.Shift ? 10 : 1;
            (double dx, double dy)? nudge = e.Key switch
            {
                Key.Left => (-step, 0),
                Key.Right => (step, 0),
                Key.Up => (0, -step),
                Key.Down => (0, step),
                _ => null
            };
            if (nudge is { } n)
            {
                BoardArea.Nudge(n.dx, n.dy);
                e.Handled = true;
                return;
            }
        }

        base.OnPreviewKeyDown(e);
    }

    /// <summary>Com a borracha ativa, + aumenta e - diminui o tamanho (teclado principal ou numérico; no ABNT2 o + é Shift+=).</summary>
    internal bool TryHandleEraserKey(Key key, ModifierKeys mods)
    {
        if (ViewModel.ToolState.Tool != ToolType.Eraser) return false;
        if (SizeDirection(key, mods) is not { } bigger) return false;

        ViewModel.ChangeEraserSize(bigger);
        return true;
    }

    /// <summary>Com a caneta (ou linha/quadrado/círculo) ativa, + engrossa e - afina o traço.</summary>
    internal bool TryHandleStrokeKey(Key key, ModifierKeys mods)
    {
        if (!ViewModel.IsStrokeTool) return false;
        if (SizeDirection(key, mods) is not { } thicker) return false;

        ViewModel.ChangeThickness(thicker);
        return true;
    }

    private static bool? SizeDirection(Key key, ModifierKeys mods)
    {
        if (mods != ModifierKeys.None && mods != ModifierKeys.Shift) return null;
        return key switch
        {
            Key.Add or Key.OemPlus => true,
            Key.Subtract or Key.OemMinus => false,
            _ => null
        };
    }

    private void ToggleFullScreen()
    {
        _isFullScreen = !_isFullScreen;
        // Passar por Normal faz o Windows recalcular a área e cobrir também a barra de tarefas.
        WindowState = WindowState.Normal;
        WindowStyle = _isFullScreen ? WindowStyle.None : WindowStyle.SingleBorderWindow;
        WindowState = WindowState.Maximized;
    }

    // ---- Edição de texto (a barra de opções fala com o editor por aqui) ----

    public bool IsEditingText => BoardArea.IsEditing;
    public void FormatEditor(TextFormatChange change) => BoardArea.FormatEditor(change);
    public void CommitPendingEdit() => BoardArea.CommitPendingEdit();

    // ---- Exportar / imprimir: desenha o quadro como imagem ----

    public BitmapSource RenderBoard(bool includeFrame, double scale, bool opaque)
    {
        FrameworkElement target = includeFrame ? BoardFrameRoot : BoardSurfaceBorder;

        // Esconde o que é interface (dica, pílula de status, opções, seleção) e a sombra externa, que sairia cortada;
        // o quadro é desenhado em 100%, sem zoom nem deslocamento.
        var hidden = new UIElement[] { HintPanel, StatusPill, ToolOptionsBar };
        // SetCurrentValue/InvalidateProperty preservam as ligações de dados (atribuir Visibility as removeria).
        foreach (var el in hidden) el.SetCurrentValue(VisibilityProperty, Visibility.Hidden);
        var shadow = BoardFrameBorder.Effect;
        BoardFrameBorder.Effect = null;
        BoardArea.BeginExport();
        UpdateLayout();

        try
        {
            double w = target.ActualWidth, h = target.ActualHeight;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                if (opaque) dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
                dc.DrawRectangle(new VisualBrush(target), null, new Rect(0, 0, w, h));
            }

            var bitmap = new RenderTargetBitmap(
                (int)Math.Ceiling(w * scale), (int)Math.Ceiling(h * scale),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            BoardArea.EndExport();
            foreach (var el in hidden) el.InvalidateProperty(VisibilityProperty);
            BoardFrameBorder.Effect = shadow;
        }
    }

    /// <summary>O X não encerra o app: vai para a bandeja do Windows.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        var app = (App)Application.Current;
        if (!app.IsExiting)
        {
            e.Cancel = true;
            app.HideToTray();
        }
        base.OnClosing(e);
    }
}
