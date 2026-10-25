using System.Media;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WhiteboardSimulator.Helpers;

namespace WhiteboardSimulator.ViewModels;

public enum PomodoroMode { Focus, ShortBreak, LongBreak }

/// <summary>
/// Pomodoro simples: foco, pausa curta e pausa longa (depois de N focos). O tempo é medido pelo relógio
/// (hora de término), então não atrasa mesmo com a janela ocupada. As durações ficam em settings.json.
/// </summary>
public sealed class PomodoroViewModel : ViewModelBase
{
    // O anel é uma elipse de RingDiameter com traço RingThickness; o tracejado é medido em "espessuras de traço".
    public const double RingDiameter = 176, RingThickness = 8;
    private const double RingLength = Math.PI * (RingDiameter - RingThickness) / RingThickness;

    public const int MinMinutes = 1, MaxMinutes = 120, MinCycles = 2, MaxCycles = 8;

    private static readonly Brush FocusBrush = Frozen("#E5533D");
    private static readonly Brush ShortBrush = Frozen("#2FA36B");
    private static readonly Brush LongBrush = Frozen("#4F6BED");

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private PomodoroMode _mode = PomodoroMode.Focus;
    private bool _isRunning;
    private DateTime _endsAt;
    private TimeSpan _remaining;
    private int _completedFocus;
    private bool _isSettingsOpen;
    private bool _isLocked;
    private double _backgroundOpacity;
    private readonly DispatcherTimer _persistTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    private int _focusMinutes, _shortMinutes, _longMinutes, _cycles;

    public PomodoroViewModel()
    {
        var saved = AppSettings.Load();
        _focusMinutes = Clamp(saved.PomodoroFocusMinutes, MinMinutes, MaxMinutes);
        _shortMinutes = Clamp(saved.PomodoroShortBreakMinutes, MinMinutes, MaxMinutes);
        _longMinutes = Clamp(saved.PomodoroLongBreakMinutes, MinMinutes, MaxMinutes);
        _cycles = Clamp(saved.PomodoroCycles, MinCycles, MaxCycles);
        _isLocked = saved.PomodoroLocked;
        _backgroundOpacity = Clamp(saved.PomodoroBackgroundOpacity, MinOpacity, 1.0);
        _remaining = FullDuration(_mode);

        _persistTimer.Tick += (_, _) => { _persistTimer.Stop(); Persist(); };
        _timer.Tick += (_, _) => Tick(DateTime.UtcNow);

        StartPauseCommand = new RelayCommand(_ => ToggleRunning());
        ResetCommand = new RelayCommand(_ => Reset());
        SkipCommand = new RelayCommand(_ => Skip());
        SelectModeCommand = new RelayCommand(p => SelectMode(ParseMode(p)));
        AdjustCommand = new RelayCommand(p => Adjust(p as string));
        ToggleSettingsCommand = new RelayCommand(_ => IsSettingsOpen = !IsSettingsOpen);
        ToggleLockCommand = new RelayCommand(_ => IsLocked = !IsLocked);
        CloseCommand = new RelayCommand(_ => CloseRequested?.Invoke());
    }

    /// <summary>Mensagem para a barra de status quando uma sessão termina.</summary>
    public event Action<string>? Notice;

    /// <summary>O usuário fechou a janela do Pomodoro (o relógio continua correndo).</summary>
    public event Action? CloseRequested;

    public ICommand StartPauseCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand SkipCommand { get; }
    public ICommand SelectModeCommand { get; }
    public ICommand AdjustCommand { get; }
    public ICommand ToggleSettingsCommand { get; }
    public ICommand ToggleLockCommand { get; }
    public ICommand CloseCommand { get; }

    // ---- Janela: trava e transparência do fundo ----

    public const double MinOpacity = 0.3;

    /// <summary>Travada: não dá para mover nem mexer nos controles (evita cliques sem querer enquanto roda).</summary>
    public bool IsLocked
    {
        get => _isLocked;
        set
        {
            if (!Set(ref _isLocked, value)) return;
            Notify(nameof(IsUnlocked));
            Notify(nameof(LockIcon));
            Notify(nameof(LockToolTip));
            Notify(nameof(HeaderCursor));
            SchedulePersist();
        }
    }

    public bool IsUnlocked => !_isLocked;

    public Geometry LockIcon => Geometry.Parse(_isLocked
        ? "M7,11 H17 V20 H7 Z M9,11 V8 A3,3 0 0 1 15,8 V11"
        : "M7,11 H17 V20 H7 Z M9,11 V8 A3,3 0 0 1 14.5,6.5");

    public string LockToolTip => _isLocked ? "Destravar a janela" : "Travar a janela (bloqueia movimento e cliques)";

    public System.Windows.Input.Cursor HeaderCursor => _isLocked ? System.Windows.Input.Cursors.Arrow : System.Windows.Input.Cursors.SizeAll;

    /// <summary>Opacidade do fundo da janela (1 = sólido). O conteúdo (texto, anel) continua opaco.</summary>
    public double BackgroundOpacity
    {
        get => _backgroundOpacity;
        set
        {
            if (!Set(ref _backgroundOpacity, Clamp(value, MinOpacity, 1.0))) return;
            Notify(nameof(Transparency));
            Notify(nameof(OpacityText));
            Notify(nameof(PanelBackground));
            SchedulePersist();
        }
    }

    /// <summary>O inverso da opacidade: é o que o controle deslizante mostra (0% = sólido).</summary>
    public double Transparency
    {
        get => 1 - _backgroundOpacity;
        set => BackgroundOpacity = 1 - value;
    }

    public string OpacityText => $"{Math.Round(Transparency * 100)}%";

    public Brush PanelBackground => new SolidColorBrush(Colors.White) { Opacity = _backgroundOpacity };

    public bool IsRunning => _isRunning;
    public bool IsFocus => _mode == PomodoroMode.Focus;
    public bool IsShortBreak => _mode == PomodoroMode.ShortBreak;
    public bool IsLongBreak => _mode == PomodoroMode.LongBreak;

    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        set => Set(ref _isSettingsOpen, value);
    }

    public int FocusMinutes => _focusMinutes;
    public int ShortBreakMinutes => _shortMinutes;
    public int LongBreakMinutes => _longMinutes;
    public int Cycles => _cycles;
    public int CompletedFocus => _completedFocus;

    public string TimeText
    {
        get
        {
            // Arredonda para cima: 00:00 só aparece quando de fato acabou.
            int seconds = (int)Math.Ceiling(Math.Max(0, _remaining.TotalSeconds));
            return $"{seconds / 60:00}:{seconds % 60:00}";
        }
    }

    public string SessionLabel => _mode switch
    {
        PomodoroMode.Focus => "Foco",
        PomodoroMode.ShortBreak => "Pausa curta",
        _ => "Pausa longa"
    };

    public string StartPauseText => _isRunning ? "Pausar"
        : _remaining < FullDuration(_mode) ? "Continuar" : "Iniciar";

    /// <summary>Em que foco do ciclo estamos, até a pausa longa (ex.: "Foco 2 de 4").</summary>
    public string CycleText
    {
        get
        {
            int current = IsFocus ? _completedFocus % _cycles + 1 : Math.Max(1, (_completedFocus - 1) % _cycles + 1);
            return $"Foco {current} de {_cycles}";
        }
    }

    public string ToolTipText => _isRunning || _remaining < FullDuration(_mode)
        ? $"Pomodoro — {SessionLabel} {TimeText}" : "Pomodoro";

    public Brush RingBrush => _mode switch
    {
        PomodoroMode.Focus => FocusBrush,
        PomodoroMode.ShortBreak => ShortBrush,
        _ => LongBrush
    };

    /// <summary>Quanto do anel está preenchido (1 = sessão inteira pela frente, 0 = acabou).</summary>
    public double Progress
    {
        get
        {
            double total = FullDuration(_mode).TotalSeconds;
            return total <= 0 ? 0 : Math.Max(0, Math.Min(1, _remaining.TotalSeconds / total));
        }
    }

    public DoubleCollection RingDash => new() { Progress * RingLength, RingLength + 10 };

    // ---- Controle ----

    private void ToggleRunning()
    {
        if (_isRunning) Pause();
        else Start();
    }

    private void Start()
    {
        if (_remaining <= TimeSpan.Zero) _remaining = FullDuration(_mode);
        _endsAt = DateTime.UtcNow + _remaining;
        _isRunning = true;
        _timer.Start();
        NotifyAll();
    }

    private void Pause()
    {
        Tick(DateTime.UtcNow);
        if (!_isRunning) return;     // terminou nesse instante
        _isRunning = false;
        _timer.Stop();
        NotifyAll();
    }

    public void Reset()
    {
        Stop();
        _remaining = FullDuration(_mode);
        NotifyAll();
    }

    /// <summary>Pula para a próxima sessão sem contar a atual como concluída.</summary>
    private void Skip()
    {
        Stop();
        GoTo(NextMode());
    }

    private void SelectMode(PomodoroMode mode)
    {
        Stop();
        GoTo(mode);
    }

    private void Stop()
    {
        _isRunning = false;
        _timer.Stop();
    }

    private void GoTo(PomodoroMode mode)
    {
        _mode = mode;
        _remaining = FullDuration(mode);
        NotifyAll();
    }

    private PomodoroMode NextMode() => _mode == PomodoroMode.Focus ? PomodoroMode.ShortBreak : PomodoroMode.Focus;

    internal void Tick(DateTime nowUtc)
    {
        if (!_isRunning) return;
        _remaining = _endsAt - nowUtc;
        if (_remaining <= TimeSpan.Zero) Complete();
        else NotifyTime();
    }

    private void Complete()
    {
        Stop();
        bool wasFocus = _mode == PomodoroMode.Focus;
        if (wasFocus) _completedFocus++;
        var finished = SessionLabel;

        // Depois do foco vem a pausa (longa a cada N focos); depois da pausa, o foco. Não inicia sozinho.
        var next = _mode == PomodoroMode.Focus
            ? (_completedFocus % _cycles == 0 ? PomodoroMode.LongBreak : PomodoroMode.ShortBreak)
            : PomodoroMode.Focus;
        GoTo(next);

        try { SystemSounds.Asterisk.Play(); } catch { /* sem áudio: segue sem som */ }
        Notice?.Invoke($"Pomodoro: {finished} terminou. Próximo: {SessionLabel}");
    }

    // ---- Configurações ----

    private void Adjust(string? code)
    {
        if (string.IsNullOrEmpty(code) || code.Length < 2) return;
        int delta = code[1] == '+' ? 1 : -1;
        bool untouched = !_isRunning && _remaining == FullDuration(_mode);

        switch (code[0])
        {
            case 'F': _focusMinutes = Clamp(_focusMinutes + delta, MinMinutes, MaxMinutes); break;
            case 'S': _shortMinutes = Clamp(_shortMinutes + delta, MinMinutes, MaxMinutes); break;
            case 'L': _longMinutes = Clamp(_longMinutes + delta, MinMinutes, MaxMinutes); break;
            case 'C': _cycles = Clamp(_cycles + delta, MinCycles, MaxCycles); break;
            default: return;
        }

        if (untouched) _remaining = FullDuration(_mode);     // sessão ainda não começou: já mostra o novo tempo
        Persist();
        NotifyAll();
    }

    private void SchedulePersist()
    {
        _persistTimer.Stop();
        _persistTimer.Start();     // junta vários ajustes seguidos (ex.: arrastar o controle deslizante) numa só gravação
    }

    private void Persist()
    {
        var settings = AppSettings.Load();
        settings.PomodoroFocusMinutes = _focusMinutes;
        settings.PomodoroShortBreakMinutes = _shortMinutes;
        settings.PomodoroLongBreakMinutes = _longMinutes;
        settings.PomodoroCycles = _cycles;
        settings.PomodoroLocked = _isLocked;
        settings.PomodoroBackgroundOpacity = _backgroundOpacity;
        settings.Save();
    }

    // ---- Apoio ----

    private TimeSpan FullDuration(PomodoroMode mode) => TimeSpan.FromMinutes(mode switch
    {
        PomodoroMode.Focus => _focusMinutes,
        PomodoroMode.ShortBreak => _shortMinutes,
        _ => _longMinutes
    });

    private static PomodoroMode ParseMode(object? p) => (p as string) switch
    {
        "Short" => PomodoroMode.ShortBreak,
        "Long" => PomodoroMode.LongBreak,
        _ => PomodoroMode.Focus
    };

    private static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(max, value));

    private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));

    private static Brush Frozen(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }

    private void NotifyTime()
    {
        Notify(nameof(TimeText));
        Notify(nameof(Progress));
        Notify(nameof(RingDash));
        Notify(nameof(ToolTipText));
    }

    private void NotifyAll()
    {
        NotifyTime();
        Notify(nameof(IsRunning));
        Notify(nameof(IsFocus));
        Notify(nameof(IsShortBreak));
        Notify(nameof(IsLongBreak));
        Notify(nameof(SessionLabel));
        Notify(nameof(StartPauseText));
        Notify(nameof(CycleText));
        Notify(nameof(RingBrush));
        Notify(nameof(FocusMinutes));
        Notify(nameof(ShortBreakMinutes));
        Notify(nameof(LongBreakMinutes));
        Notify(nameof(Cycles));
        Notify(nameof(CompletedFocus));
    }
}
