using WhiteboardSimulator.ViewModels;

namespace WhiteboardSimulator.Helpers;

/// <summary>Uma ação que pode ser desfeita e refeita (mudança de moldura, desenho, objeto movido...).</summary>
public sealed class UndoableAction
{
    private readonly Action _do;
    private readonly Action _undo;

    public UndoableAction(string description, Action @do, Action undo)
    {
        Description = description;
        _do = @do;
        _undo = undo;
    }

    public string Description { get; }
    public void Do() => _do();
    public void Undo() => _undo();
}

/// <summary>
/// Histórico de desfazer/refazer de um quadro. Lembra em que ponto o quadro foi salvo, então
/// desfazer até esse ponto tira a marca de "alterações não salvas".
/// </summary>
public sealed class UndoManager : ViewModelBase
{
    private const int MaxHistory = 200;

    private readonly List<UndoableAction> _actions = new();
    private int _position;       // quantas ações estão aplicadas
    private int _savedPosition;  // posição do último salvamento (-1 = inalcançável)

    public event EventHandler? Changed;

    public bool CanUndo => _position > 0;
    public bool CanRedo => _position < _actions.Count;
    public bool IsAtSavedState => _position == _savedPosition;
    public string? UndoDescription => CanUndo ? _actions[_position - 1].Description : null;
    public string? RedoDescription => CanRedo ? _actions[_position].Description : null;

    /// <summary>Executa a ação e a registra; descarta o que poderia ser refeito.</summary>
    public void Execute(UndoableAction action)
    {
        action.Do();

        if (_position < _actions.Count)
        {
            _actions.RemoveRange(_position, _actions.Count - _position);
            if (_savedPosition > _position) _savedPosition = -1;
        }

        _actions.Add(action);
        _position++;

        if (_actions.Count > MaxHistory)
        {
            _actions.RemoveAt(0);
            _position--;
            _savedPosition = _savedPosition > 0 ? _savedPosition - 1 : -1;
        }

        RaiseChanged();
    }

    public void Undo()
    {
        if (!CanUndo) return;
        _actions[--_position].Undo();
        RaiseChanged();
    }

    public void Redo()
    {
        if (!CanRedo) return;
        _actions[_position++].Do();
        RaiseChanged();
    }

    public void MarkSaved()
    {
        _savedPosition = _position;
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        Notify(nameof(CanUndo));
        Notify(nameof(CanRedo));
        Notify(nameof(UndoDescription));
        Notify(nameof(RedoDescription));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
