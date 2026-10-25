using System.Threading;
using System.Windows;
using WhiteboardSimulator.Helpers;
using WhiteboardSimulator.ViewModels;

namespace WhiteboardSimulator;

public partial class App : Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private TrayService? _tray;

    /// <summary>True quando o usuário escolheu sair de verdade (menu da bandeja ou logoff).</summary>
    public bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Instância única: abrir de novo (atalho) só traz a janela existente de volta.
        _mutex = new Mutex(true, @"Local\WhiteboardSimulator.SingleInstance", out bool isFirst);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\WhiteboardSimulator.Show");
        if (!isFirst)
        {
            _showEvent.Set();
            Shutdown();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

#if DEBUG
        // Autoteste das ferramentas (só em depuração): --selftest <pasta de saída>
        var args = Environment.GetCommandLineArgs();
        int at = Array.IndexOf(args, "--selftest");
        if (at >= 0 && at + 1 < args.Length)
        {
            var outDir = args[at + 1];
            _ = Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(2500);
                await SelfTest.Run(window, outDir);
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
#endif

        _tray = new TrayService(ShowMainWindow, ExitApp);

        new Thread(() =>
        {
            while (_showEvent.WaitOne())
            {
                if (IsExiting) break;
                Dispatcher.BeginInvoke(ShowMainWindow);
            }
        }) { IsBackground = true }.Start();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        IsExiting = true;
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }

    public void ShowMainWindow()
    {
        var w = MainWindow;
        if (w == null) return;
        w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
    }

    /// <summary>Esconde a janela (botão X) e, se não desativado, mostra o aviso em post-it.</summary>
    public void HideToTray()
    {
        MainWindow?.Hide();
        if (!AppSettings.Load().HideTrayNotice)
            new TrayNoteWindow().Show();
    }

    public void ExitApp()
    {
        // Alterações não salvas: traz a janela para a frente e pergunta, quadro a quadro, antes de encerrar.
        if (MainWindow?.DataContext is MainViewModel vm)
        {
            if (vm.HasUnsavedChanges)
            {
                ShowMainWindow();
                if (!vm.ConfirmDiscardAll()) return;
            }
            vm.DiscardRecovery();   // saída normal: não sobram cópias para "restaurar"
        }

        IsExiting = true;
        _showEvent?.Set();
        Shutdown();
    }
}
