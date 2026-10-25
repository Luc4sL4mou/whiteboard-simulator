using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Input;
using System.Windows.Threading;
using WhiteboardSimulator.Helpers;

namespace WhiteboardSimulator;

/// <summary>Aviso "indo para a bandeja" em formato de post-it, no canto da tela.</summary>
public partial class TrayNoteWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(8) };
    private bool _closing;

    public TrayNoteWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 8;
            Top = area.Bottom - ActualHeight - 8;
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
            _timer.Start();
        };

        // Fica na tela enquanto o mouse estiver em cima (ex.: marcando "não avisar de novo").
        _timer.Tick += (_, _) =>
        {
            if (IsMouseOver) return;
            Dismiss();
        };
    }

    private void OnNoteClicked(object sender, MouseButtonEventArgs e) => Dismiss();

    /// <summary>Grava a escolha na hora (antes, só ao fechar o aviso: sair do app antes disso perdia a escolha).</summary>
    private void OnDontShowAgainChanged(object sender, RoutedEventArgs e)
    {
        var settings = AppSettings.Load();
        settings.HideTrayNotice = DontShowAgain.IsChecked == true;
        settings.Save();
    }

    // Clicar no checkbox não deve fechar o post-it.
    private void OnCheckBoxClicked(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        _timer.Stop();

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }
}
