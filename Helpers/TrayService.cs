using System.Windows;
using Forms = System.Windows.Forms;

namespace WhiteboardSimulator.Helpers;

/// <summary>Ícone do app na bandeja do Windows, com menu Abrir / Sair.</summary>
public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;

    public TrayService(Action onShow, Action onExit)
    {
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico")).Stream;

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Abrir Whiteboard Simulator", null, (_, _) => onShow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => onExit());

        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream),
            Text = "Whiteboard Simulator",
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) onShow();
        };
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
