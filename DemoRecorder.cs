#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WhiteboardSimulator.Models;
using WhiteboardSimulator.ViewModels;

namespace WhiteboardSimulator;

/// <summary>
/// Gravador de demonstração (só na versão de depuração, "--demo pasta"): conduz o app por cenas roteirizadas
/// com eventos sintéticos (não usa o mouse real) e grava os quadros em PNG para virarem GIFs do README.
/// </summary>
internal static class DemoRecorder
{
    private const int OutWidth = 1000;

    public static async Task Run(MainWindow window, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var vm = (MainViewModel)window.DataContext;
        var canvas = window.BoardArea;
        var board = vm.ActiveBoard;
        var tool = vm.ToolState;
        var pomo = vm.Pomodoro;

        async Task Pump() => await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        string sceneDir = outDir;
        int n = 0;
        PomodoroWindow? pw = null;

        BitmapSource Render()
        {
            window.UpdateLayout();
            pw?.UpdateLayout();
            var root = (FrameworkElement)window.Content;
            int w = (int)root.ActualWidth, h = (int)root.ActualHeight;
            var main = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            main.Render(root);

            double s = (double)OutWidth / w;
            var dv = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
            using (var dc = dv.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(s, s));
                dc.DrawImage(main, new Rect(0, 0, w, h));
                if (pw is { IsVisible: true })
                {
                    int pwW = (int)Math.Ceiling(pw.ActualWidth), pwH = (int)Math.Ceiling(pw.ActualHeight);
                    var pb = new RenderTargetBitmap(pwW, pwH, 96, 96, PixelFormats.Pbgra32);
                    pb.Render(pw);
                    var origin = window.PointFromScreen(new Point(pw.Left, pw.Top));
                    dc.DrawImage(pb, new Rect(origin.X, origin.Y, pwW, pwH));
                }
            }
            var rt = new RenderTargetBitmap(OutWidth, (int)Math.Round(h * s), 96, 96, PixelFormats.Pbgra32);
            rt.Render(dv);
            rt.Freeze();
            return rt;
        }

        void Save(BitmapSource bmp, string path)
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(path);
            enc.Save(fs);
        }

        // Grava o quadro atual; "hold" repete o mesmo quadro (para pausas na animação).
        void Frame(int hold = 1)
        {
            var path = Path.Combine(sceneDir, $"f{++n:0000}.png");
            Save(Render(), path);
            for (int i = 1; i < hold; i++) File.Copy(path, Path.Combine(sceneDir, $"f{++n:0000}.png"), true);
        }

        async Task Step(int hold = 1, int wait = 25)
        {
            await Pump();
            await Task.Delay(wait);
            Frame(hold);
        }

        void NewScene(string name)
        {
            sceneDir = Path.Combine(outDir, name);
            Directory.CreateDirectory(sceneDir);
            n = 0;
        }

        async Task Click(Point p) { canvas.TestDown(p); canvas.TestUp(p); await Pump(); }

        async Task Drag(Point a, Point b, int steps = 10, int everyN = 2)
        {
            canvas.TestDown(a);
            for (int i = 1; i <= steps; i++)
            {
                canvas.TestMove(new Point(a.X + (b.X - a.X) * i / steps, a.Y + (b.Y - a.Y) * i / steps));
                if (i % everyN == 0) await Step();
            }
            canvas.TestUp(b);
            await Step();
        }

        async Task Stroke(IReadOnlyList<Point> pts, int everyN = 4)
        {
            canvas.TestDown(pts[0]);
            for (int i = 1; i < pts.Count; i++)
            {
                canvas.TestMove(pts[i]);
                if (i % everyN == 0) await Step();
            }
            canvas.TestUp(pts[^1]);
            await Step();
        }

        async Task Type(Point at, string text, double size)
        {
            tool.FontSize = size;
            tool.Tool = ToolType.Text;
            await Click(at);
            canvas.TestSetEditorText(text);
            await Step(2);
            canvas.CommitEditForTest();
            await Step(2);
        }

        try
        {
            await Pump();
            await Task.Delay(1500);
            window.Activate();
            board.ResetView();
            double cw = canvas.ActualWidth, ch = canvas.ActualHeight;
            double X(double f) => cw * f;
            double Y(double f) => ch * f;

            // ===================== Cena 1: o quadro =====================
            NewScene("quadro");
            tool.Tool = ToolType.Select;
            await Step(8);

            await Type(new Point(X(.07), Y(.06)), "Planejamento da semana", 40);

            tool.Tool = ToolType.Pen;
            tool.Color = "#1E88E5";
            tool.Thickness = 5;
            var wave = new List<Point>();
            for (int i = 0; i <= 60; i++) wave.Add(new Point(X(.07) + X(.34) * i / 60, Y(.19) + 6 * Math.Sin(i / 3.0)));
            await Stroke(wave, 5);

            tool.Color = "#E53935";
            tool.Thickness = 4;
            double[] boxX = { .08, .38, .68 };
            string[] titles = { "Ideias", "Fazer", "Feito" };
            for (int i = 0; i < 3; i++)
            {
                tool.Tool = ToolType.Rectangle;
                await Drag(new Point(X(boxX[i]), Y(.30)), new Point(X(boxX[i] + .20), Y(.50)), 8, 2);
                await Type(new Point(X(boxX[i]) + 24, Y(.32)), titles[i], 30);
            }

            tool.Tool = ToolType.Line;
            tool.Color = "#43A047";
            await Drag(new Point(X(.29), Y(.40)), new Point(X(.37), Y(.40)), 6, 2);
            await Drag(new Point(X(.59), Y(.40)), new Point(X(.67), Y(.40)), 6, 2);

            tool.Tool = ToolType.StickyNote;
            await Click(new Point(X(.08), Y(.60)));
            canvas.TestSetEditorText("Revisar o código");
            await Step(3);
            canvas.CommitEditForTest();
            await Step(2);

            tool.StickyColor = Helpers.InkPalette.StickyColors[1].Hex;
            tool.Tool = ToolType.StickyNote;
            await Click(new Point(X(.38), Y(.60)));
            canvas.TestSetEditorText("Estudar para a prova");
            await Step(3);
            canvas.CommitEditForTest();
            await Step(2);

            var notes = board.Elements.OfType<StickyNoteElement>().ToList();
            var c1 = new Point(notes[0].X + 60, notes[0].Y + 14);
            var c2 = new Point(notes[1].X + 60, notes[1].Y + 14);
            tool.Tool = ToolType.Pin;
            await Click(c1); await Step();
            await Click(c2); await Step();
            tool.Tool = ToolType.Connector;
            await Click(c1); await Step();
            await Click(c2); await Step(4);

            tool.Tool = ToolType.Pen;
            tool.Color = "#8E24AA";
            tool.Thickness = 5;
            var star = new List<Point>();
            double sx = X(.80), sy = Y(.72), r = Y(.14);
            int[] order = { 0, 2, 4, 1, 3, 0 };
            foreach (var k in order)
            {
                double a = -Math.PI / 2 + k * 2 * Math.PI / 5;
                star.Add(new Point(sx + r * Math.Cos(a), sy + r * Math.Sin(a)));
            }
            var dense = new List<Point>();
            for (int i = 0; i < star.Count - 1; i++)
                for (int t = 0; t < 8; t++)
                    dense.Add(new Point(star[i].X + (star[i + 1].X - star[i].X) * t / 8, star[i].Y + (star[i + 1].Y - star[i].Y) * t / 8));
            dense.Add(star[^1]);
            await Stroke(dense, 5);
            await Step(6);
            Save(Render(), Path.Combine(outDir, "shot-quadro.png"));

            // Desfazer (Ctrl+Z) três vezes e refazer (Ctrl+Y).
            for (int i = 0; i < 3; i++) { board.Undo.Undo(); await Step(4); }
            for (int i = 0; i < 3; i++) { board.Undo.Redo(); await Step(3); }
            await Step(4);

            // Outras superfícies e molduras.
            board.ChangeFrame(BoardFrame.Metal);
            board.ChangeSurface(BoardSurface.GreenSlate);
            await Step(8);
            board.ChangeSurface(BoardSurface.BlackSlate);
            await Step(8);
            board.ChangeSurface(BoardSurface.GreenFelt);
            await Step(8);
            board.ChangeFrame(BoardFrame.Wood);
            board.ChangeSurface(BoardSurface.Whiteboard);
            await Step(8);

            // ===================== Cena 2: Pomodoro =====================
            NewScene("pomodoro");
            pomo.IsLocked = false;
            pomo.Transparency = 0.2;
            vm.IsPomodoroOpen = true;
            await Pump();
            await Task.Delay(500);
            pw = Application.Current.Windows.OfType<PomodoroWindow>().FirstOrDefault();
            if (pw == null) throw new InvalidOperationException("janela do Pomodoro não abriu");
            var work = SystemParameters.WorkArea;
            pw.Left = work.Left + 90;
            pw.Top = work.Bottom - pw.ActualHeight - 20;
            await Step(8);

            pomo.StartPauseCommand.Execute(null);
            var t0 = DateTime.UtcNow;
            int fake = 0;
            // O timer real também corre; por isso o relógio falso é reaplicado logo antes de cada quadro.
            void Shot(int hold = 1) { pomo.Tick(t0.AddSeconds(fake)); Frame(hold); }
            async Task Wait(int ms = 20) { await Pump(); await Task.Delay(ms); }
            for (int i = 1; i <= 26; i++)
            {
                fake = i * 40;      // avança o relógio "de mentira" para mostrar o anel esvaziando
                Shot();
                await Wait();
            }
            Shot(4);

            pomo.IsSettingsOpen = true;
            await Wait(); Shot(3);
            for (int i = 1; i <= 10; i++) { pomo.Transparency = 0.2 + 0.05 * i; await Wait(); Shot(); }
            Shot(6);
            save_shot();
            for (int i = 10; i >= 0; i--) { pomo.Transparency = 0.2 + 0.03 * i; await Wait(); Shot(); }
            pomo.IsSettingsOpen = false;
            await Wait(); Shot(3);

            // Arrastar a janela para o canto superior direito (sempre no topo, em qualquer lugar da tela).
            double fromL = pw.Left, fromT = pw.Top, toL = work.Right - pw.ActualWidth - 40, toT = work.Top + 130;
            for (int i = 1; i <= 24; i++)
            {
                double e = 1 - Math.Pow(1 - i / 24.0, 3);
                pw.Left = fromL + (toL - fromL) * e;
                pw.Top = fromT + (toT - fromT) * e;
                await Wait(15);
                Shot();
            }
            Shot(4);

            pomo.IsLocked = true;
            await Wait(); Shot(10);
            pomo.IsLocked = false;
            await Wait(); Shot(4);

            void save_shot() => Save(Render(), Path.Combine(outDir, "shot-pomodoro.png"));
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(outDir, "erro.txt"), ex.ToString());
        }

        File.WriteAllText(Path.Combine(outDir, "pronto.txt"), "ok");
        Application.Current.Shutdown();
    }
}
#endif
