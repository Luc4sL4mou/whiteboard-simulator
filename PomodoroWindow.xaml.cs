using System.Windows;
using System.Windows.Input;
using WhiteboardSimulator.Helpers;
using WhiteboardSimulator.ViewModels;

namespace WhiteboardSimulator;

/// <summary>
/// Janela flutuante do Pomodoro: fica sempre no topo (acima dos outros programas), pode ser arrastada
/// para qualquer lugar da tela e travada. A posição é lembrada entre as execuções.
/// </summary>
public partial class PomodoroWindow : Window
{
    private readonly PomodoroViewModel _vm;

    public PomodoroWindow(PomodoroViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        WindowStartupLocation = WindowStartupLocation.Manual;

        Loaded += (_, _) => PlaceInitially();
        SizeChanged += (_, _) => KeepOnScreen();
        IsVisibleChanged += (_, _) => { if (!IsVisible) SavePosition(); };
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm.IsLocked || e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); } catch (InvalidOperationException) { /* soltou o botão antes de começar */ }
        KeepOnScreen();
        SavePosition();
    }

    /// <summary>Usa a posição guardada; sem ela (ou se a tela mudou), canto inferior esquerdo da área útil.</summary>
    private void PlaceInitially()
    {
        var settings = AppSettings.Load();

        if (settings.PomodoroLeft is { } left && settings.PomodoroTop is { } top && IsInsideScreen(left, top))
        {
            Left = left;
            Top = top;
        }
        else
        {
            var work = SystemParameters.WorkArea;
            Left = work.Left + 90;
            Top = work.Bottom - ActualHeight - 20;
        }
        KeepOnScreen();
    }

    private static bool IsInsideScreen(double left, double top) =>
        left >= SystemParameters.VirtualScreenLeft - 100 && top >= SystemParameters.VirtualScreenTop - 20 &&
        left <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
        top <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100;

    /// <summary>Impede que a janela fique inteira fora da tela (ex.: ao abrir os ajustes perto da borda de baixo).</summary>
    private void KeepOnScreen()
    {
        if (!IsLoaded) return;
        var work = SystemParameters.WorkArea;
        if (Top + ActualHeight > work.Bottom) Top = Math.Max(work.Top, work.Bottom - ActualHeight);
        if (Top < work.Top) Top = work.Top;
        if (Left + ActualWidth > work.Right + 16) Left = work.Right + 16 - ActualWidth;
        if (Left < work.Left - 16) Left = work.Left - 16;
    }

    private void SavePosition()
    {
        if (!IsLoaded) return;
        var settings = AppSettings.Load();
        settings.PomodoroLeft = Left;
        settings.PomodoroTop = Top;
        settings.Save();
    }
}
