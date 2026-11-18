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
        SourceInitialized += (_, _) =>
        {
            ApplyTitleBarTheme();
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WidgetHook);
        };
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.TitleBarColor)) ApplyTitleBarTheme();
            if (e.PropertyName == nameof(MainViewModel.IsPomodoroOpen)) UpdatePomodoroWindow();
        };
        vm.Pomodoro.CloseRequested += () => vm.IsPomodoroOpen = false;
        BoardColumn.SizeChanged += (_, _) => FitWidgetBoard();

        // Depois que a janela aparece, oferece restaurar quadros de uma sessão que caiu.
        Loaded += (_, _) => Dispatcher.InvokeAsync(vm.OfferRecovery, System.Windows.Threading.DispatcherPriority.Background);
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    // ---- Modo widget: o quadro vira uma janelinha sem moldura no canto da tela ----

    private bool _isWidget;
    private Rect _normalBounds;
    private WindowState _normalState;

    public bool IsWidget => _isWidget;

    private void OnWidgetClick(object sender, RoutedEventArgs e) => ToggleWidget();

    private void OnWidgetTopmostClick(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        WidgetTopmostLabel.Text = Topmost ? "Soltar do topo" : "Fixar no topo";
    }

    /// <summary>Print só do quadro: sem a janela, a barra do widget ou o que estiver atrás.</summary>
    private void OnWidgetCopyClick(object sender, RoutedEventArgs e)
    {
        // Opaco: a maioria dos programas cola o fundo transparente da área de transferência como preto.
        Clipboard.SetImage(RenderBoard(includeFrame: true, scale: 2, opaque: true));
        WidgetCopiedHint();
    }

    private async void WidgetCopiedHint()
    {
        WidgetCopyLabel.Text = "Copiado!";
        await Task.Delay(1500);
        WidgetCopyLabel.Text = "Copiar imagem";
    }

    private void OnWidgetBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            FitWidgetToCurrentScreen();     // duplo clique: tamanho padrão do monitor onde está
            return;
        }
        try { DragMove(); } catch (InvalidOperationException) { /* soltou o botão antes de começar */ }
    }

    public void ToggleWidget()
    {
        if (_isFullScreen) ToggleFullScreen();
        _isWidget = !_isWidget;
        var full = _isWidget ? Visibility.Collapsed : Visibility.Visible;
        TopBar.Visibility = SidePanel.Visibility = PenTray.Visibility = full;
        WidgetBar.Visibility = _isWidget ? Visibility.Visible : Visibility.Collapsed;

        if (_isWidget)
        {
            _normalState = WindowState;
            _normalBounds = RestoreBounds;
            var screen = System.Windows.Forms.Screen.FromHandle(Hwnd);   // o monitor onde a janela está agora
            // O quadro guarda o tamanho atual e só é desenhado menor: nada é cortado nem muda de lugar.
            double w = BoardFrameRoot.ActualWidth, h = BoardFrameRoot.ActualHeight;
            BoardFrameRoot.Width = w;
            BoardFrameRoot.Height = h;

            // A janela é só o quadro: sem margem, e o fundo vira a cor da moldura (cobre os cantos arredondados).
            RootGrid.Margin = new Thickness(0);
            SetCurrentValue(BackgroundProperty, BoardFrameBorder.Background);

            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            ShowInTaskbar = false;
            // Sem isso a borda de redimensionar ocupa ~7px de cada lado, com fundo aparecendo.
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 0,
                GlassFrameThickness = new Thickness(0),
                ResizeBorderThickness = new Thickness(6),
                UseAeroCaptionButtons = false
            });
            MinWidth = 300;
            MinHeight = 300 * h / w;
            PlaceWidgetOn(screen);
        }
        else
        {
            RootGrid.Margin = new Thickness(12);
            GetBindingExpression(BackgroundProperty)?.UpdateTarget();
            BoardFrameRoot.LayoutTransform = Transform.Identity;
            BoardFrameRoot.ClearValue(WidthProperty);
            BoardFrameRoot.ClearValue(HeightProperty);
            Topmost = false;
            WidgetTopmostLabel.Text = "Fixar no topo";
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, null);

            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            ShowInTaskbar = true;
            MinWidth = 1100;
            MinHeight = 700;
            Left = _normalBounds.Left;
            Top = _normalBounds.Top;
            Width = _normalBounds.Width;
            Height = _normalBounds.Height;
            WindowState = _normalState;
        }
        FitWidgetBoard();
    }

    private void FitWidgetBoard()
    {
        if (!_isWidget || BoardColumn.ActualWidth <= 0 || BoardColumn.ActualHeight <= 0) return;
        double s = Math.Min(BoardColumn.ActualWidth / BoardFrameRoot.Width, BoardColumn.ActualHeight / BoardFrameRoot.Height);
        BoardFrameRoot.LayoutTransform = new ScaleTransform(s, s);
    }

    private const int WmSizing = 0x0214;
    private const int WmExitSizeMove = 0x0232;
    private const uint SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    private IntPtr Hwnd => new WindowInteropHelper(this).Handle;
    private string? _widgetScreen;

    internal NativeRect WindowRectPx => GetWindowRect(Hwnd, out var r) ? r : default;

    /// <summary>
    /// Tamanho padrão do widget no monitor dado: ~80% da largura (até 90% da altura), na proporção do quadro.
    /// Fica centrado em <paramref name="center"/> (ou no monitor), sem sair da área útil.
    /// Em pixels físicos, então vale para monitores com escalas (DPI) diferentes.
    /// </summary>
    internal void PlaceWidgetOn(System.Windows.Forms.Screen screen, System.Drawing.Point? center = null)
    {
        var wa = screen.WorkingArea;
        double ratio = BoardFrameRoot.Width / BoardFrameRoot.Height;
        int w = (int)(wa.Width * 0.8), h = (int)Math.Round(w / ratio);
        if (h > wa.Height * 0.9) { h = (int)(wa.Height * 0.9); w = (int)Math.Round(h * ratio); }
        var c = center ?? new System.Drawing.Point(wa.Left + wa.Width / 2, wa.Top + wa.Height / 2);
        int x = Math.Clamp(c.X - w / 2, wa.Left, wa.Right - w);
        int y = Math.Clamp(c.Y - h / 2, wa.Top, wa.Bottom - h);
        SetWindowPos(Hwnd, IntPtr.Zero, x, y, w, h, SwpNoZOrder | SwpNoActivate);
        _widgetScreen = screen.DeviceName;
    }

    /// <summary>Soltou o widget em outro monitor: ajusta ao tamanho padrão desse monitor, onde foi solto.</summary>
    internal void OnWidgetMoveEnded()
    {
        if (!_isWidget) return;
        var screen = System.Windows.Forms.Screen.FromHandle(Hwnd);
        if (screen.DeviceName == _widgetScreen) return;     // mesmo monitor: respeita o tamanho que o usuário deu
        FitWidgetToCurrentScreen();
    }

    /// <summary>Ajusta ao tamanho padrão do monitor em que o widget está, mantendo o centro onde está.</summary>
    internal void FitWidgetToCurrentScreen()
    {
        if (!_isWidget) return;
        var r = WindowRectPx;
        PlaceWidgetOn(System.Windows.Forms.Screen.FromHandle(Hwnd), new System.Drawing.Point((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2));
    }

    private IntPtr WidgetHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmExitSizeMove) OnWidgetMoveEnded();
        return msg == WmSizing ? KeepWidgetRatio(wParam, lParam, ref handled) : IntPtr.Zero;
    }

    /// <summary>No widget, redimensionar mantém a proporção do quadro, para nunca sobrar fundo em volta.</summary>
    private IntPtr KeepWidgetRatio(IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!_isWidget) return IntPtr.Zero;
        var r = Marshal.PtrToStructure<NativeRect>(lParam);
        double ratio = BoardFrameRoot.Width / BoardFrameRoot.Height;
        int edge = wParam.ToInt32();    // WMSZ_: 1 esq, 2 dir, 3 topo, 4 topo-esq, 5 topo-dir, 6 base, 7 base-esq, 8 base-dir
        if (edge is 3 or 6)
            r.Right = r.Left + (int)Math.Round((r.Bottom - r.Top) * ratio);
        else
        {
            int height = (int)Math.Round((r.Right - r.Left) / ratio);
            if (edge is 4 or 5) r.Top = r.Bottom - height; else r.Bottom = r.Top + height;
        }
        Marshal.StructureToPtr(r, lParam, false);
        handled = true;
        return (IntPtr)1;
    }

    // ---- Janela flutuante do Pomodoro (sempre no topo; não depende da janela principal estar visível) ----

    private PomodoroWindow? _pomodoroWindow;

    private void UpdatePomodoroWindow()
    {
        if (ViewModel.IsPomodoroOpen)
        {
            _pomodoroWindow ??= new PomodoroWindow(ViewModel.Pomodoro);
            _pomodoroWindow.Show();
        }
        else
        {
            _pomodoroWindow?.Hide();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _pomodoroWindow?.Close();
        base.OnClosed(e);
    }

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
        if (e.Key == Key.F11 && !_isWidget)
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

        if (TryHandleUndoKey(e.Key, mods, typing))
        {
            e.Handled = true;
            return;
        }

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

    /// <summary>
    /// Ctrl+Z desfaz e Ctrl+Y / Ctrl+Shift+Z refaz a última ação do quadro, não importa onde esteja o foco.
    /// Só o texto sendo digitado no quadro usa o desfazer próprio (enquanto tiver algo a desfazer/refazer);
    /// campos de texto da barra também seguem o desfazer deles.
    /// </summary>
    private bool TryHandleUndoKey(Key key, ModifierKeys mods, bool typing)
    {
        bool undo = key == Key.Z && mods == ModifierKeys.Control;
        bool redo = (key == Key.Y && mods == ModifierKeys.Control) || (key == Key.Z && mods == (ModifierKeys.Control | ModifierKeys.Shift));
        if (!undo && !redo) return false;

        if (typing)
        {
            if (Keyboard.FocusedElement is not System.Windows.Controls.RichTextBox editor || BoardArea.ActiveEditor != editor)
                return false;                                   // campo de texto comum: desfaz o próprio texto
            if (undo ? editor.CanUndo : editor.CanRedo)
                return false;                                   // ainda há o que desfazer na digitação
            BoardArea.CommitPendingEdit();                       // acabou a digitação: o próximo passo é do quadro
        }

        var command = undo ? ViewModel.UndoCommand : ViewModel.RedoCommand;
        if (command.CanExecute(null)) command.Execute(null);
        return true;
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
