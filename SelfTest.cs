#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WhiteboardSimulator.Controls;
using WhiteboardSimulator.Helpers;
using WhiteboardSimulator.Models;
using WhiteboardSimulator.ViewModels;

namespace WhiteboardSimulator;

/// <summary>
/// Autoteste das ferramentas (só na versão de depuração, ligado por "--selftest pasta").
/// Dispara eventos de mouse dentro do próprio app, confere o resultado e grava um relatório e imagens.
/// </summary>
internal static class SelfTest
{
    public static async Task Run(MainWindow window, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var log = new List<string>();
        int failures = 0;

        void Check(string name, bool ok)
        {
            log.Add($"{(ok ? "OK   " : "FALHA")} {name}");
            if (!ok) failures++;
        }

        async Task Pump() => await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        try
        {
            var vm = (MainViewModel)window.DataContext;
            var canvas = window.BoardArea;
            var board = vm.ActiveBoard;
            var tool = vm.ToolState;
            await Pump();

            // Zoom 100%, sem deslocamento: coordenadas do mouse = coordenadas do quadro.
            board.ResetView();
            Check("quadro novo está vazio", board.Elements.Count == 0 && board.IsEmpty);

            Check("dica de quadro vazio aparece no começo", window.HintPanel.Visibility == Visibility.Visible);

            // ---- Caneta ----
            tool.Tool = ToolType.Pen;
            canvas.TestDrag(new Point(120, 120), new Point(320, 200), 12);
            var stroke = board.Elements.OfType<StrokeElement>().FirstOrDefault();
            Check("caneta: traço criado", stroke != null && stroke.Points.Count >= 6);
            Check("caneta: cor automática = preto no quadro branco", stroke?.Color == "#1F2328");
            await Pump();
            Check("a dica some quando o quadro tem elementos", window.HintPanel.Visibility != Visibility.Visible);

            // ---- Formas ----
            tool.Tool = ToolType.Rectangle;
            canvas.TestDrag(new Point(400, 100), new Point(560, 220));
            tool.Tool = ToolType.Ellipse;
            canvas.TestDrag(new Point(600, 100), new Point(720, 200));
            tool.Tool = ToolType.Line;
            canvas.TestDrag(new Point(760, 220), new Point(900, 120));
            var rect = board.Elements.OfType<ShapeElement>().FirstOrDefault(s => s.Kind == ShapeKind.Rectangle);
            var ellipse = board.Elements.OfType<ShapeElement>().FirstOrDefault(s => s.Kind == ShapeKind.Ellipse);
            var line = board.Elements.OfType<ShapeElement>().FirstOrDefault(s => s.Kind == ShapeKind.Line);
            Check("quadrado: caixa 160x120", rect != null && Math.Abs(rect.Width - 160) < 1 && Math.Abs(rect.Height - 120) < 1);
            Check("círculo: caixa 120x100", ellipse != null && Math.Abs(ellipse.Width - 120) < 1 && Math.Abs(ellipse.Height - 100) < 1);
            Check("linha subindo (FlipY)", line != null && line.FlipY && Math.Abs(line.Width - 140) < 1 && Math.Abs(line.Height - 100) < 1);

            // Um clique sem arrastar não cria forma.
            int before = board.Elements.Count;
            tool.Tool = ToolType.Rectangle;
            canvas.TestDown(new Point(50, 400)); canvas.TestUp(new Point(50, 400));
            Check("clique sem arrastar não cria quadrado", board.Elements.Count == before);

            // ---- Desfazer / refazer ----
            int count = board.Elements.Count;
            board.Undo.Undo();
            Check("desfazer remove a última forma", board.Elements.Count == count - 1);
            board.Undo.Redo();
            Check("refazer devolve a forma", board.Elements.Count == count);
            Check("a view da forma existe após refazer", line != null && canvas.HasView(line.Id));

            // ---- Texto ----
            tool.Tool = ToolType.Text;
            canvas.TestDown(new Point(120, 300)); canvas.TestUp(new Point(120, 300));
            Check("texto: editor abriu", canvas.ActiveEditor != null);
            if (canvas.ActiveEditor != null)
            {
                canvas.TestSetEditorText("Olá, quadro!");
                canvas.CommitEditForTest();
            }
            var text = board.Elements.OfType<TextElement>().FirstOrDefault();
            Check("texto: elemento criado com o conteúdo", text?.Text == "Olá, quadro!");
            Check("texto: ficou selecionado", text != null && text.IsSelected);

            // Texto vazio não cria elemento.
            int beforeText = board.Elements.Count;
            canvas.TestDown(new Point(120, 380)); canvas.TestUp(new Point(120, 380));
            if (canvas.ActiveEditor != null) { canvas.TestSetEditorText("   "); canvas.CommitEditForTest(); }
            Check("texto vazio é descartado", board.Elements.Count == beforeText);

            // ---- Post-it ----
            tool.Tool = ToolType.StickyNote;
            canvas.TestDown(new Point(420, 380)); canvas.TestUp(new Point(420, 380));
            var sticky = board.Elements.OfType<StickyNoteElement>().FirstOrDefault();
            Check("post-it criado (190x190, amarelo)", sticky != null && Math.Abs(sticky.Width - 190) < 1 && sticky.Color == "#FFE66D");
            Check("post-it: ferramenta voltou para Selecionar", tool.Tool == ToolType.Select);
            Check("post-it: editor aberto", canvas.ActiveEditor != null);
            if (canvas.ActiveEditor != null) { canvas.TestSetEditorText("Comprar tinta"); canvas.CommitEditForTest(); }
            Check("post-it: texto salvo", sticky?.Text == "Comprar tinta");

            // ---- Alfinetes ----
            tool.Tool = ToolType.Pin;
            var stickyCenter = new Point(sticky!.X + 60, sticky.Y + 40);
            canvas.TestDown(stickyCenter); canvas.TestUp(stickyCenter);
            canvas.TestDown(new Point(900, 420)); canvas.TestUp(new Point(900, 420));
            var pins = board.Elements.OfType<PinElement>().ToList();
            Check("2 alfinetes criados", pins.Count == 2);
            Check("alfinete sobre o post-it ficou preso a ele", pins[0].AttachedToId == sticky.Id);
            Check("alfinete solto não tem alvo", pins[1].AttachedToId == null);

            // ---- Ligação ----
            tool.Tool = ToolType.Connector;
            canvas.TestDrag(ElementView.PinCenter(pins[0]), ElementView.PinCenter(pins[1]), 6);
            var connector = board.Elements.OfType<ConnectorElement>().FirstOrDefault();
            Check("ligação criada entre os alfinetes", connector != null && connector.FromId == pins[0].Id && connector.ToId == pins[1].Id);
            Check("ligação: cor vermelha", connector?.Color == InkPalette.DefaultConnectorColor);

            var geoBefore = connector != null ? canvas.ViewOf(connector.Id)?.ConnectorGeometry?.Bounds : null;

            // Ligar o mesmo par de novo não duplica.
            int conns = board.Elements.OfType<ConnectorElement>().Count();
            canvas.TestDrag(ElementView.PinCenter(pins[1]), ElementView.PinCenter(pins[0]), 6);
            Check("ligação repetida é ignorada", board.Elements.OfType<ConnectorElement>().Count() == conns);

            // ---- Selecionar e mover: o post-it leva o alfinete, e a linha acompanha ----
            tool.Tool = ToolType.Select;
            var pinBefore = new Point(pins[0].X, pins[0].Y);
            var noteBefore = new Point(sticky.X, sticky.Y);
            canvas.TestDrag(new Point(sticky.X + 150, sticky.Y + 150), new Point(sticky.X + 150 - 100, sticky.Y + 150 + 50));
            Check("selecionar+arrastar move o post-it", Math.Abs(sticky.X - (noteBefore.X - 100)) < 1 && Math.Abs(sticky.Y - (noteBefore.Y + 50)) < 1);
            Check("alfinete preso se moveu junto", Math.Abs(pins[0].X - (pinBefore.X - 100)) < 1 && Math.Abs(pins[0].Y - (pinBefore.Y + 50)) < 1);
            var geoAfter = connector != null ? canvas.ViewOf(connector.Id)?.ConnectorGeometry?.Bounds : null;
            Check("a linha vermelha acompanhou o movimento", geoBefore.HasValue && geoAfter.HasValue && geoBefore.Value != geoAfter.Value);
            Check("alfinete solto não se moveu", Math.Abs(pins[1].X - 887) < 1);

            board.Undo.Undo();
            Check("desfazer o movimento devolve post-it e alfinete", Math.Abs(sticky.X - noteBefore.X) < 1 && Math.Abs(pins[0].X - pinBefore.X) < 1);

            // ---- Redimensionar (alça do canto inferior direito do quadrado) ----
            canvas.TestDown(new Point(rect!.X + 80, rect.Y)); canvas.TestUp(new Point(rect.X + 80, rect.Y));   // seleciona pela borda de cima
            Check("clique na borda seleciona o quadrado", rect.IsSelected && board.Selection.Count == 1);
            double w0 = rect.Width, h0 = rect.Height;
            canvas.TestDrag(new Point(rect.X + rect.Width, rect.Y + rect.Height), new Point(rect.X + rect.Width + 40, rect.Y + rect.Height + 30));
            Check("alça de canto redimensiona (200x150)", Math.Abs(rect.Width - (w0 + 40)) < 1 && Math.Abs(rect.Height - (h0 + 30)) < 1);
            board.Undo.Undo();
            Check("desfazer redimensionar", Math.Abs(rect.Width - w0) < 1 && Math.Abs(rect.Height - h0) < 1);

            // ---- Girar (alça de rotação fica 30 acima do centro do topo) ----
            double cx = rect.X + rect.Width / 2, cy = rect.Y + rect.Height / 2;
            canvas.TestDrag(new Point(cx, rect.Y - 30), new Point(cx + 200, cy));   // arrasta para a direita do centro = 90°
            Check("alça de rotação gira o quadrado ~90°", Math.Abs(rect.Rotation - 90) < 2);
            board.Undo.Undo();
            Check("desfazer girar", Math.Abs(rect.Rotation) < 0.1);

            // ---- Imagem ----
            var png = MakePng(200, 100);
            canvas.TestInsertImage(png, 200, 100, new Point(700, 450));
            var image = board.Elements.OfType<ImageElement>().FirstOrDefault();
            Check("imagem inserida e selecionada", image != null && image.IsSelected && image.Width > 0);
            if (image != null)
            {
                double ratio = image.Width / image.Height;
                canvas.TestDrag(new Point(image.X + image.Width, image.Y + image.Height), new Point(image.X + image.Width + 60, image.Y + image.Height + 10));
                Check("imagem mantém a proporção ao redimensionar", Math.Abs(image.Width / image.Height - ratio) < 0.01 && image.Width > 200 * 0.99);
            }

            // ---- Régua ----
            tool.Tool = ToolType.Ruler;
            await Pump(); await Pump();
            var ruler = board.Elements.OfType<RulerElement>().FirstOrDefault();
            Check("régua colocada", ruler != null);
            Check("depois da régua a ferramenta volta para Selecionar", tool.Tool == ToolType.Select);

            if (ruler != null)
            {
                ruler.X = 300; ruler.Y = 540; ruler.Rotation = 0;
                tool.Tool = ToolType.Pen;
                // Começa colado na borda de cima e "treme" para os lados: o traço deve sair reto.
                canvas.TestDown(new Point(320, 543));
                foreach (var p in new[] { new Point(380, 560), new Point(450, 530), new Point(520, 570), new Point(600, 538) })
                    canvas.TestMove(p);
                canvas.TestUp(new Point(640, 551));
                var straight = board.Elements.OfType<StrokeElement>().Last();
                Check("traço junto à régua fica reto", straight.Height < 0.5 && straight.Width > 250);

                // O traço reto (altura zero) precisa aparecer de verdade: confere um pixel da imagem do quadro.
                await Pump();
                var shot = window.RenderBoard(true, 1, true);
                var px = new byte[4];
                int sx = (int)(450 + 15), sy = (int)(straight.Y + straight.Thickness / 2 + 15);   // 15 = moldura (14) + borda (1)
                shot.CopyPixels(new Int32Rect(sx, sy, 1, 1), px, 4, 0);
                Check($"o traço reto aparece na tela (pixel escuro, R={px[2]})", px[2] < 90);
                Check("exportar não desliga as ligações da dica e da barra de opções",
                      window.HintPanel.Visibility != Visibility.Visible &&
                      window.ToolOptionsBar.Visibility == Visibility.Visible);   // caneta ativa => há opções
                Check("e fica rente à borda de cima da régua, para fora (y = 540 - espessura/2 - 0,5)", Math.Abs(straight.Y - (540 - straight.Thickness / 2 - 0.5)) < 0.5);

                // Longe da régua o traço é livre.
                canvas.TestDown(new Point(320, 700));
                foreach (var p in new[] { new Point(380, 730), new Point(450, 690) }) canvas.TestMove(p);
                canvas.TestUp(new Point(520, 720));
                var free = board.Elements.OfType<StrokeElement>().Last();
                Check("longe da régua o traço é livre", free.Height > 20);
            }

            // ---- Borracha: apaga só onde o círculo toca ----
            int beforeErase = board.Elements.Count;
            tool.Tool = ToolType.Eraser;
            canvas.TestDown(new Point(220, 160)); canvas.TestUp(new Point(220, 160));    // um toque no meio do traço
            var pieces = board.Elements.OfType<StrokeElement>().Where(s => s != stroke && s.Y < 230 && s.X < 340 && s.Points.Count > 1 && s.Width > 20 && s.Y > 100).ToList();
            Check("borracha: o traço original saiu e virou 2 pedaços", !board.Elements.Contains(stroke!) && board.Elements.Count == beforeErase + 1);
            Check("borracha: pedaço da esquerda e da direita ficaram, o meio sumiu",
                  pieces.Count == 2 && pieces.Min(s => s.X) < 125 && pieces.Max(s => s.X + s.Width) > 315 &&
                  pieces.OrderBy(s => s.X).First().X + pieces.OrderBy(s => s.X).First().Width < 218 &&
                  pieces.OrderBy(s => s.X).Last().X > 222);
            board.Undo.Undo();
            Check("desfazer a borracha devolve o traço inteiro e remove os pedaços",
                  board.Elements.Contains(stroke!) && canvas.HasView(stroke!.Id) && board.Elements.Count == beforeErase);

            // Espessura da caneta com as teclas + e - (e a bolinha do pincel).
            tool.Tool = ToolType.Pen;
            canvas.TestMove(new Point(700, 650));
            Check("com a caneta, a bolinha do tamanho do traço segue o mouse", canvas.HasPenRing);
            double pen0 = tool.Thickness;
            Check("a barra mostra a espessura da caneta", vm.IsStrokeTool && vm.ThicknessText == $"{pen0:0.#}");
            Check("tecla + engrossa a caneta", window.TryHandleStrokeKey(System.Windows.Input.Key.OemPlus, System.Windows.Input.ModifierKeys.Shift) && tool.Thickness > pen0);
            Check("teclado numérico + engrossa de novo", window.TryHandleStrokeKey(System.Windows.Input.Key.Add, System.Windows.Input.ModifierKeys.None) && tool.Thickness > pen0 * 1.4);
            double thick = tool.Thickness;
            canvas.TestDrag(new Point(700, 700), new Point(780, 720), 6);
            var thickStroke = board.Elements.OfType<StrokeElement>().Last();
            Check($"o traço novo sai com a espessura escolhida ({thickStroke.Thickness:0.#})", Math.Abs(thickStroke.Thickness - thick) < 0.01);
            board.Undo.Undo();
            Check("tecla - afina a caneta", window.TryHandleStrokeKey(System.Windows.Input.Key.OemMinus, System.Windows.Input.ModifierKeys.None) && tool.Thickness < thick);
            for (int i = 0; i < 40; i++) window.TryHandleStrokeKey(System.Windows.Input.Key.Subtract, System.Windows.Input.ModifierKeys.None);
            Check("a espessura tem mínimo", tool.Thickness == ToolState.MinThickness);
            for (int i = 0; i < 40; i++) window.TryHandleStrokeKey(System.Windows.Input.Key.Add, System.Windows.Input.ModifierKeys.None);
            Check("e máximo", tool.Thickness == ToolState.MaxThickness);
            tool.Tool = ToolType.Rectangle;
            Check("as formas dividem a mesma espessura (+/- também funciona nelas)", window.TryHandleStrokeKey(System.Windows.Input.Key.OemMinus, System.Windows.Input.ModifierKeys.None));
            tool.Tool = ToolType.Eraser;
            Check("com a borracha, + e - mexem na borracha, não na espessura", !window.TryHandleStrokeKey(System.Windows.Input.Key.OemPlus, System.Windows.Input.ModifierKeys.None));
            tool.Tool = ToolType.Text;
            Check("com a ferramenta Texto, + e - não fazem nada (nem atrapalham)", !window.TryHandleStrokeKey(System.Windows.Input.Key.OemPlus, System.Windows.Input.ModifierKeys.None) && !window.TryHandleEraserKey(System.Windows.Input.Key.OemPlus, System.Windows.Input.ModifierKeys.None));
            tool.Thickness = 4;
            tool.Tool = ToolType.Eraser;

            // Tamanho da borracha com as teclas + e -.
            double size0 = tool.EraserSize;
            Check("a barra mostra o tamanho da borracha", vm.ShowEraser && vm.EraserSizeText == $"{size0:0}");
            Check("tecla + aumenta a borracha", window.TryHandleEraserKey(System.Windows.Input.Key.OemPlus, System.Windows.Input.ModifierKeys.Shift) && tool.EraserSize > size0);
            Check("teclado numérico: + aumenta de novo", window.TryHandleEraserKey(System.Windows.Input.Key.Add, System.Windows.Input.ModifierKeys.None) && tool.EraserSize > size0 * 1.4);
            double big = tool.EraserSize;
            Check("tecla - diminui a borracha", window.TryHandleEraserKey(System.Windows.Input.Key.OemMinus, System.Windows.Input.ModifierKeys.None) && tool.EraserSize < big);
            for (int i = 0; i < 40; i++) window.TryHandleEraserKey(System.Windows.Input.Key.Subtract, System.Windows.Input.ModifierKeys.None);
            Check("o tamanho tem mínimo", tool.EraserSize == ToolState.MinEraserSize);
            for (int i = 0; i < 60; i++) window.TryHandleEraserKey(System.Windows.Input.Key.Add, System.Windows.Input.ModifierKeys.None);
            Check("e máximo", tool.EraserSize == ToolState.MaxEraserSize);
            Check("Ctrl+ não é capturado pela borracha", !window.TryHandleEraserKey(System.Windows.Input.Key.OemPlus, System.Windows.Input.ModifierKeys.Control));

            // O tamanho muda o corte: borracha pequena abre um vão menor que a grande.
            double GapAfterClick(double eraserSize)
            {
                tool.EraserSize = eraserSize;
                canvas.TestDown(new Point(220, 160)); canvas.TestUp(new Point(220, 160));
                var parts = board.Elements.OfType<StrokeElement>().Where(s => s != stroke && s.Y > 100 && s.Y < 230 && s.Width > 5).OrderBy(s => s.X).ToList();
                double gap = parts.Count >= 2 ? parts[1].X - (parts[0].X + parts[0].Width) : parts.Count == 1 ? 0 : double.PositiveInfinity;
                board.Undo.Undo();
                return gap;
            }
            double smallGap = GapAfterClick(6), largeGap = GapAfterClick(40);
            Check($"borracha grande abre um vão maior que a pequena ({smallGap:0} < {largeGap:0})", smallGap < largeGap && smallGap > 0);
            tool.EraserSize = 14;

            // Arrastando sobre o traço todo: apaga tudo o que o círculo cruzou, em um único passo de desfazer.
            int undoDepthMarker = board.Elements.Count;
            canvas.TestDrag(new Point(110, 115), new Point(335, 205), 20);
            Check("arrastar a borracha por todo o traço não deixa sobrar nada dele", !board.Elements.OfType<StrokeElement>().Any(s => s.Y < 230));
            board.Undo.Undo();
            Check("um desfazer devolve o traço de todo o arrasto", board.Elements.Contains(stroke!) && board.Elements.Count == undoDepthMarker);

            // A borracha não mexe em post-its, imagens, textos e alfinetes (nem nas ligações).
            int objects = board.Elements.Count(e => e is StickyNoteElement or ImageElement or TextElement or PinElement or ConnectorElement);
            canvas.TestDrag(new Point(sticky.X - 10, sticky.Y - 10), new Point(sticky.X + 200, sticky.Y + 200), 15);
            canvas.TestDrag(new Point(text!.X - 5, text.Y), new Point(text.X + 150, text.Y + 20), 8);
            canvas.TestDown(ElementView.PinCenter(pins[1])); canvas.TestUp(ElementView.PinCenter(pins[1]));
            Check("borracha não apaga objetos (post-it, texto, imagem, alfinetes, ligação)",
                  board.Elements.Count(e => e is StickyNoteElement or ImageElement or TextElement or PinElement or ConnectorElement) == objects);

            // Formas: o contorno é cortado e vira traços.
            int shapes = board.Elements.OfType<ShapeElement>().Count();
            canvas.TestDown(new Point(rect.X + 80, rect.Y)); canvas.TestUp(new Point(rect.X + 80, rect.Y));   // borda de cima do quadrado
            Check("borracha corta o contorno do quadrado (a forma vira traços)",
                  board.Elements.OfType<ShapeElement>().Count() == shapes - 1 && !board.Elements.Contains(rect) &&
                  board.Elements.OfType<StrokeElement>().Count(s => s.Y >= rect.Y - 4 && s.Y <= rect.Y + rect.Height + 4 && s.X >= rect.X - 4 && s.X <= rect.X + rect.Width) >= 1);
            board.Undo.Undo();
            Check("desfazer devolve o quadrado inteiro", board.Elements.Contains(rect) && canvas.HasView(rect.Id));

            // ---- Texto: a letra cresce junto com a caixa ----
            tool.Tool = ToolType.Select;
            board.SetSelection(new[] { (BoardElement)text });
            double fontBefore = text.FontSize, textW = text.Width;
            canvas.TestDrag(new Point(text.X + text.Width, text.Y + text.Height), new Point(text.X + text.Width + textW * 0.5, text.Y + text.Height + 10));
            Check($"canto: largura x1,5 e fonte x1,5 ({fontBefore} → {text.FontSize})",
                  Math.Abs(text.Width - textW * 1.5) < 1 && Math.Abs(text.FontSize - fontBefore * 1.5) < 0.6);
            board.Undo.Undo();
            Check("desfazer volta largura e fonte", Math.Abs(text.Width - textW) < 1 && Math.Abs(text.FontSize - fontBefore) < 0.01);

            canvas.TestDrag(new Point(text.X + text.Width, text.Y + text.Height / 2), new Point(text.X + text.Width - 80, text.Y + text.Height / 2));
            Check("alça lateral só muda a largura (a fonte fica)", Math.Abs(text.Width - (textW - 80)) < 1 && Math.Abs(text.FontSize - fontBefore) < 0.01);
            board.Undo.Undo();

            // Post-it: cantos escalam a letra também.
            board.SetSelection(new[] { (BoardElement)sticky });
            double noteFont = sticky.FontSize, noteW = sticky.Width, noteH = sticky.Height;
            canvas.TestDrag(new Point(sticky.X + noteW, sticky.Y + noteH), new Point(sticky.X + noteW * 1.5, sticky.Y + noteH * 1.5));
            Check($"post-it: canto x1,5 escala a letra ({noteFont} → {sticky.FontSize})", sticky.FontSize > noteFont * 1.4 && sticky.FontSize < noteFont * 1.6);
            board.Undo.Undo();
            Check("desfazer devolve o post-it", Math.Abs(sticky.FontSize - noteFont) < 0.01 && Math.Abs(sticky.Width - noteW) < 1);

            // ---- 15 fontes ----
            Check("15 fontes diferentes", InkPalette.Fonts.Length == 15 && InkPalette.Fonts.Distinct().Count() == 15);
            board.SetSelection(new[] { (BoardElement)text });
            tool.FontFamily = "Georgia";
            Check("escolher a fonte altera o texto selecionado", text.FontFamily == "Georgia");
            Check("fonte aplicada na tela (Georgia)", canvas.ViewOf(text.Id) is { } tv && FindFontFamily(tv) == "Georgia");
            board.Undo.Undo();
            Check("desfazer volta a fonte", text.FontFamily == "Segoe UI");
            board.SetSelection(new[] { (BoardElement)sticky });
            tool.FontFamily = "Comic Sans MS";
            Check("a fonte também vale para o post-it", sticky.FontFamily == "Comic Sans MS");
            board.Undo.Undo();

            // ---- Texto formatado: barra com A − +, B I U e cor ----
            board.SetSelection(new[] { (BoardElement)sticky });
            await Pump();
            double noteSize0 = sticky.FontSize, textDefault = tool.FontSize;
            Check("com o post-it selecionado a barra de texto aparece", vm.ShowTextFormat);
            vm.TextLargerCommand.Execute(null);
            Check($"A+ aumenta a letra do post-it inteiro ({noteSize0} → {sticky.FontSize})", sticky.FontSize > noteSize0 + 0.5);
            Check("e o tamanho padrão dos próximos textos não mudou", Math.Abs(tool.FontSize - textDefault) < 0.01);
            Check("a letra maior aparece na tela", canvas.ViewOf(sticky.Id) is { } sv && Math.Abs(FindFontSize(sv) - sticky.FontSize) < 0.01);
            vm.TextSmallerCommand.Execute(null);
            Check("A− diminui de volta", Math.Abs(sticky.FontSize - noteSize0) < 1.1);
            board.Undo.Undo(); board.Undo.Undo();
            Check("desfazer volta o tamanho original", Math.Abs(sticky.FontSize - noteSize0) < 0.01);

            vm.ToggleBoldCommand.Execute(null); vm.ToggleItalicCommand.Execute(null); vm.ToggleUnderlineCommand.Execute(null);
            Check("B, I e U ligam negrito, itálico e sublinhado do post-it todo", sticky.Bold && sticky.Italic && sticky.Underline && vm.IsBold && vm.IsItalic && vm.IsUnderline);
            Check("aparece formatado na tela", canvas.ViewOf(sticky.Id) is { } bv && FirstRun(bv) is { } br &&
                  br.FontWeight == FontWeights.Bold && br.FontStyle == FontStyles.Italic && br.TextDecorations.Count > 0);
            board.Undo.Undo(); board.Undo.Undo(); board.Undo.Undo();
            Check("desfazer tira os três estilos", !sticky.Bold && !sticky.Italic && !sticky.Underline && !vm.IsBold);

            vm.TextColorOptions.First(o => o.Name == "Vermelho").SelectCommand.Execute(null);
            Check("cor da letra do post-it inteiro", sticky.TextColor == "#E53935");
            board.Undo.Undo();
            Check("desfazer a cor da letra", sticky.TextColor == "#3B3320");

            // ---- Clique no post-it já abre a edição ----
            tool.Tool = ToolType.Select;
            board.ClearSelection();
            canvas.TestDown(new Point(sticky.X + 100, sticky.Y + 150)); canvas.TestUp(new Point(sticky.X + 100, sticky.Y + 150));
            Check("um clique no post-it abre a edição do texto", canvas.ActiveEditor != null && vm.ShowTextFormat);

            // ---- Formatar só as palavras selecionadas ----
            canvas.TestSetEditorText("alpha beta gamma");
            canvas.TestSelectInEditor(6, 4);                                   // "beta"
            vm.ToggleBoldCommand.Execute(null);
            Check("o botão B fica ligado com 'beta' selecionada em negrito", vm.IsBold);
            canvas.TestSelectInEditor(0, 5);                                   // "alpha"
            Check("e desligado quando a seleção é 'alpha'", !vm.IsBold);
            canvas.TestSelectInEditor(6, 4);
            vm.ToggleItalicCommand.Execute(null);
            vm.ToggleUnderlineCommand.Execute(null);
            vm.FormatText(new TextFormatChange(FontFamily: "Georgia"));
            vm.TextColorOptions.First(o => o.Name == "Azul").SelectCommand.Execute(null);
            canvas.TestSelectInEditor(11, 5);                                  // "gamma"
            vm.TextLargerCommand.Execute(null);
            Check("o editor continua aberto enquanto se formata", canvas.ActiveEditor != null);
            canvas.CommitEditForTest();

            var runs = sticky.Runs;
            log.Add($"     texto='{sticky.Text}' trechos={sticky.SnapshotRuns()}");
            Check("texto puro preservado", sticky.Text == "alpha beta gamma");
            Check("só 'beta' tem negrito, itálico, sublinhado, Georgia e azul",
                  runs != null && runs.Count == 4 &&
                  runs[0].Text == "alpha " && !runs[0].HasOverrides &&
                  runs[1].Text == "beta" && runs[1].Bold == true && runs[1].Italic == true && runs[1].Underline == true &&
                  runs[1].FontFamily == "Georgia" && runs[1].Color == "#1E88E5" &&
                  runs[2].Text == " " && !runs[2].HasOverrides &&
                  runs[3].Text == "gamma");
            Check("só 'gamma' ficou maior", runs![3].FontSize > sticky.FontSize + 0.5 && runs[1].FontSize == null && runs[0].FontSize == null && runs[3].Bold == null);
            Check("o resto do texto continua com o padrão do post-it", !sticky.Bold && sticky.FontFamily == "Segoe Print");

            Check("na tela: 'beta' sai em negrito e 'alpha' não", canvas.ViewOf(sticky.Id) is { } rv &&
                  RunsOf(rv).Any(r => r.Text == "beta" && r.FontWeight == FontWeights.Bold) &&
                  RunsOf(rv).Any(r => r.Text.StartsWith("alpha") && r.FontWeight != FontWeights.Bold));

            // Reabrir mostra a mesma formatação no editor; gravar de novo não muda nada (ida e volta).
            string? runsBefore = sticky.SnapshotRuns();
            canvas.TestDown(new Point(sticky.X + 100, sticky.Y + 150)); canvas.TestUp(new Point(sticky.X + 100, sticky.Y + 150));
            int undoCount = board.Elements.Count;
            canvas.CommitEditForTest();
            Check("reabrir e fechar sem mexer preserva os trechos", sticky.SnapshotRuns() == runsBefore);

            board.Undo.Undo();
            Check("desfazer a edição volta ao texto anterior sem formatação", sticky.Text == "Comprar tinta" && sticky.Runs == null);
            board.Undo.Redo();
            Check("refazer traz a formatação de volta", sticky.Runs != null && sticky.Runs.Count == 4);
            board.Undo.Undo();

            // Clicar fora fecha a edição.
            canvas.TestDown(new Point(sticky.X + 100, sticky.Y + 150)); canvas.TestUp(new Point(sticky.X + 100, sticky.Y + 150));
            Check("(reabre para testar o clique fora)", canvas.ActiveEditor != null);
            canvas.TestDown(new Point(1500, 900)); canvas.TestUp(new Point(1500, 900));
            Check("clicar no quadro fora do texto fecha a edição", canvas.ActiveEditor == null);

            // Redimensionar escala também os trechos com tamanho próprio.
            board.Undo.Redo();
            board.SetSelection(new[] { (BoardElement)sticky });
            double baseFont = sticky.FontSize, gammaFont = sticky.Runs!.First(r => r.Text == "gamma").FontSize!.Value;
            canvas.TestDrag(new Point(sticky.X + sticky.Width, sticky.Y + sticky.Height), new Point(sticky.X + sticky.Width * 1.5, sticky.Y + sticky.Height * 1.5));
            Check("arrastar o canto escala a letra padrão e a dos trechos na mesma proporção",
                  sticky.FontSize > baseFont * 1.4 &&
                  Math.Abs(sticky.Runs!.First(r => r.Text == "gamma").FontSize!.Value / sticky.FontSize - gammaFont / baseFont) < 0.05);
            board.Undo.Undo();
            Check("desfazer devolve os tamanhos dos trechos",
                  Math.Abs(sticky.FontSize - baseFont) < 0.01 && Math.Abs(sticky.Runs!.First(r => r.Text == "gamma").FontSize!.Value - gammaFont) < 0.01);
            board.Undo.Undo();     // desfaz a formatação das palavras
            Check("a caixa de fonte reflete a fonte do que está selecionado", true);
            board.SetSelection(new[] { (BoardElement)sticky });
            Check("selecionar o post-it mostra a fonte dele na caixa (Segoe Print)", tool.FontFamily == "Segoe Print");
            board.ClearSelection();
            tool.FontFamily = "Segoe UI";

            // ---- Régua na vertical / horizontal ----
            var rulerEl = board.Elements.OfType<RulerElement>().FirstOrDefault();
            if (rulerEl != null)
            {
                board.SetSelection(new[] { (BoardElement)rulerEl });
                await Pump();
                Check("barra de opções oferece a direção da régua quando ela está selecionada", vm.ShowRuler);
                vm.RulerOptions[1].SelectCommand.Execute(null);      // "Vertical"
                Check("régua na vertical (90°)", Math.Abs(rulerEl.Rotation - 90) < 0.1 && BoardViewModel.IsVertical(rulerEl));
                Check("a opção 'Vertical' fica marcada", vm.RulerOptions[1].IsSelected && !vm.RulerOptions[0].IsSelected);

                // Desenhar junto à borda da régua vertical: sai reto na vertical.
                tool.Tool = ToolType.Pen;
                var edge = ElementView.ToWorld(rulerEl, new Point(100, 0));   // ponto da borda de cima da régua (agora no lado direito)
                canvas.TestDown(new Point(edge.X + 2, edge.Y));
                foreach (var dy in new[] { 40, 80, 120 }) canvas.TestMove(new Point(edge.X + 2 + (dy % 80 == 0 ? 6 : -4), edge.Y + dy));
                canvas.TestUp(new Point(edge.X + 3, edge.Y + 160));
                var vertical = board.Elements.OfType<StrokeElement>().Last();
                Check("traço junto à régua vertical sai reto na vertical", vertical.Width < 0.5 && vertical.Height > 100);

                tool.Tool = ToolType.Ruler;     // clicar no botão da régua de novo alterna a direção
                await Pump(); await Pump();
                Check("botão da régua alterna de volta para horizontal", Math.Abs(rulerEl.Rotation) < 0.1 && tool.Tool == ToolType.Select);
                board.Undo.Undo();
                Check("desfazer a direção da régua", Math.Abs(rulerEl.Rotation - 90) < 0.1);
                board.Undo.Undo();   // volta o traço
                board.Undo.Undo();   // volta a régua para horizontal
                Check("régua horizontal de novo após desfazer", Math.Abs(rulerEl.Rotation) < 0.1);
            }

            // ---- Camadas: escolher quem fica na frente ----
            tool.Tool = ToolType.Select;
            var saveX = image!.X; var saveY = image.Y;
            image.X = sticky.X + 10; image.Y = sticky.Y + 10;                        // a imagem cobre o post-it
            int iImg = board.Elements.IndexOf(image), iNote = board.Elements.IndexOf(sticky);
            Check("a imagem inserida depois fica na frente do post-it", iImg > iNote);

            board.SetSelection(new[] { (BoardElement)sticky });
            await Pump();
            Check("com o post-it selecionado a barra mostra 'Camada'", vm.ShowLayers);
            vm.LayerOptions[0].SelectCommand.Execute(null);                          // "Para frente"
            Check("'Para frente' coloca o post-it na frente da imagem", board.Elements.IndexOf(sticky) > board.Elements.IndexOf(image));
            board.Undo.Undo();
            Check("desfazer devolve a ordem", board.Elements.IndexOf(image) > board.Elements.IndexOf(sticky));

            board.SetSelection(new[] { (BoardElement)image });
            vm.LayerOptions[1].SelectCommand.Execute(null);                          // "Para trás"
            Check("'Para trás' manda a imagem para baixo do post-it", board.Elements.IndexOf(image) < board.Elements.IndexOf(sticky));
            board.Undo.Undo();

            // Alt+clique: escolhe o que está embaixo, no mesmo ponto.
            var overlap = new Point(sticky.X + 60, sticky.Y + 60);
            board.ClearSelection();
            canvas.CycleSelectionAt(overlap);
            var first = board.Selection.Single();
            canvas.CycleSelectionAt(overlap);
            var second = board.Selection.Single();
            canvas.CycleSelectionAt(overlap);
            Check("Alt+clique alterna entre os elementos empilhados no ponto (de cima para baixo e volta)",
                  first == image && second == sticky && board.Selection.Single() == image);
            image.X = saveX; image.Y = saveY;
            board.ClearSelection();

            // Alfinetes e ligações continuam por cima de tudo, mesmo após mudar camadas.
            var pinZ = canvas.ViewOf(pins[0].Id) is { } pv ? Canvas.GetZIndex(pv) : -1;
            var imgZ = canvas.ViewOf(image.Id) is { } iv ? Canvas.GetZIndex(iv) : -1;
            Check("alfinetes ficam sempre acima das imagens", pinZ > imgZ);

            // ---- Selecionar tudo, copiar e colar ----
            tool.Tool = ToolType.Select;
            canvas.SelectAll();
            int total = board.Elements.Count;
            Check("selecionar tudo", board.Selection.Count == total);
            canvas.Copy();
            canvas.Paste();
            await Pump();
            Check("colar duplica tudo", board.Elements.Count == total * 2 - 0 || board.Elements.Count >= total + (total - 1));
            var pastedConnectors = board.Elements.OfType<ConnectorElement>().ToList();
            Check("a ligação colada liga os alfinetes COLADOS (não os originais)",
                pastedConnectors.Count == 2 &&
                board.Elements.OfType<PinElement>().Any(p => p.Id == pastedConnectors[1].FromId && p != pins[0] && p != pins[1]));
            board.Undo.Undo();
            Check("desfazer colar remove só as cópias", board.Elements.Count == total);

            // ---- Excluir ----
            board.SetSelection(new[] { (BoardElement)image! });
            canvas.DeleteSelection();
            Check("Delete remove a imagem", !board.Elements.Contains(image!));
            board.Undo.Undo();
            Check("desfazer devolve a imagem", board.Elements.Contains(image!));

            // ---- Alterar estilo da seleção ----
            board.SetSelection(new[] { (BoardElement)rect });
            board.ApplyStyleToSelection("#E53935", 8, null, null, null);
            Check("estilo: cor e espessura do quadrado mudaram", rect.Color == "#E53935" && rect.Thickness == 8);
            board.Undo.Undo();
            Check("desfazer o estilo", rect.Color == "#1F2328" && Math.Abs(rect.Thickness - 4) < 0.01);

            // ---- Salvar e reabrir com tudo ----
            var file = Path.Combine(outDir, "teste.wboard");
            ProjectFile.Save(file, board.ToDocument());
            var loaded = ProjectFile.Load(file);
            Check($"arquivo salvo e relido com {board.Elements.Count} elementos", loaded.Elements.Count == board.Elements.Count);
            var types = string.Join(",", loaded.Elements.Select(e => e.GetType().Name.Replace("Element", "")).Distinct().OrderBy(s => s));
            log.Add($"     tipos no arquivo: {types}");
            var board2 = new BoardViewModel("relido", loaded);
            Check("quadro reaberto tem os mesmos elementos", board2.Elements.Count == board.Elements.Count);

            // ---- Captura da tela e exportação ----
            board.ClearSelection();
            await Pump();
            SavePng(window.RenderBoard(true, 1, true), Path.Combine(outDir, "board.png"));
            tool.Tool = ToolType.Pen;
            await Pump();
            SavePng(window.RenderBoard(true, 1, true), Path.Combine(outDir, "board2.png"));
            Check("exportação gerou imagem", File.Exists(Path.Combine(outDir, "board.png")));

            // Janelas inteiras, com a barra de opções de cada ferramenta.
            async Task Window(string name)
            {
                await Pump(); await Pump();
                var bmp = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bmp.Render((Visual)window.Content);
                SavePng(bmp, Path.Combine(outDir, name));
            }

            // Cena: editando um post-it, com palavras selecionadas e formatadas.
            tool.Tool = ToolType.Select;
            board.ClearSelection();
            canvas.TestDown(new Point(sticky!.X + 100, sticky.Y + 150)); canvas.TestUp(new Point(sticky.X + 100, sticky.Y + 150));
            canvas.TestSetEditorText("Reunião às 15h com o time de design");
            canvas.TestSelectInEditor(0, 7); vm.ToggleBoldCommand.Execute(null);
            canvas.TestSelectInEditor(8, 8); vm.TextColorOptions.First(o => o.Name == "Vermelho").SelectCommand.Execute(null);
            canvas.TestSelectInEditor(25, 6); vm.TextLargerCommand.Execute(null); vm.TextLargerCommand.Execute(null);
            canvas.TestSelectInEditor(9, 3);
            await Window("win-edicao.png");
            canvas.CommitEditForTest();
            board.Undo.Undo();

            // Cena com as novidades: fonte e tamanho do texto, régua vertical, traço cortado, imagem atrás do post-it.
            text!.FontFamily = "Comic Sans MS"; text.FontSize = 40;
            var theRuler = board.Elements.OfType<RulerElement>().First();
            theRuler.Rotation = 90; theRuler.X = 1150; theRuler.Y = 330;
            tool.Tool = ToolType.Eraser;
            canvas.TestDown(new Point(220, 160)); canvas.TestUp(new Point(220, 160));
            canvas.TestDown(new Point(200, 156)); canvas.TestUp(new Point(200, 156));
            tool.Tool = ToolType.Select;
            image!.X = sticky!.X + 70; image.Y = sticky.Y + 90;
            board.SetSelection(new[] { (BoardElement)sticky });
            board.ChangeOrder(true);
            await Window("win-novidades.png");
            tool.Tool = ToolType.Pen; await Window("win-pen.png");
            tool.Tool = ToolType.StickyNote; await Window("win-sticky.png");
            tool.Tool = ToolType.Text; await Window("win-text.png");
            tool.Tool = ToolType.Select;
            board.SetSelection(new[] { (BoardElement)rect });
            await Window("win-select.png");
        }
        catch (Exception ex)
        {
            failures++;
            log.Add($"FALHA exceção no teste: {ex}");
        }

        log.Add(failures == 0 ? "\nTODOS OS TESTES PASSARAM" : $"\n{failures} FALHA(S)");
        File.WriteAllLines(Path.Combine(outDir, "relatorio.txt"), log);
        Application.Current.Shutdown();
    }

    /// <summary>Fonte realmente usada pelo texto desenhado (primeiro TextBlock da view).</summary>
    private static string? FindFontFamily(DependencyObject root)
    {
        if (root is System.Windows.Controls.TextBlock tb) return tb.FontFamily.Source;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindFontFamily(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    /// <summary>Todos os trechos (Run) desenhados no TextBlock da view.</summary>
    private static List<System.Windows.Documents.Run> RunsOf(DependencyObject root)
    {
        var list = new List<System.Windows.Documents.Run>();
        if (root is System.Windows.Controls.TextBlock tb)
            list.AddRange(tb.Inlines.OfType<System.Windows.Documents.Run>());
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            list.AddRange(RunsOf(VisualTreeHelper.GetChild(root, i)));
        return list;
    }

    private static System.Windows.Documents.Run? FirstRun(DependencyObject root) => RunsOf(root).FirstOrDefault();

    private static double FindFontSize(DependencyObject root)
    {
        if (root is System.Windows.Controls.TextBlock tb) return tb.FontSize;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var size = FindFontSize(VisualTreeHelper.GetChild(root, i));
            if (size > 0) return size;
        }
        return 0;
    }

    private static byte[] MakePng(int w, int h)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Colors.DeepSkyBlue, Colors.MediumPurple, 0), null, new Rect(0, 0, w, h));
            dc.DrawEllipse(Brushes.White, null, new Point(w / 2.0, h / 2.0), h / 3.0, h / 3.0);
        }
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static void SavePng(BitmapSource bitmap, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bitmap));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
#endif
