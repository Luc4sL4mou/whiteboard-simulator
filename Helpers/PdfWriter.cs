using System.IO;
using System.Text;

namespace WhiteboardSimulator.Helpers;

/// <summary>Gera um PDF de uma página com uma imagem JPEG, ajustada à página (A4) com margem.</summary>
public static class PdfWriter
{
    public static void WriteImagePage(string path, byte[] jpeg, int imageWidth, int imageHeight)
    {
        // A4 em pontos; paisagem quando a imagem é mais larga que alta.
        bool landscape = imageWidth >= imageHeight;
        double pageW = landscape ? 841.89 : 595.28;
        double pageH = landscape ? 595.28 : 841.89;
        const double margin = 28;

        double scale = Math.Min((pageW - 2 * margin) / imageWidth, (pageH - 2 * margin) / imageHeight);
        double drawW = imageWidth * scale, drawH = imageHeight * scale;
        double x = (pageW - drawW) / 2, y = (pageH - drawH) / 2;

        string F(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        string content = $"q {F(drawW)} 0 0 {F(drawH)} {F(x)} {F(y)} cm /Im0 Do Q";

        using var ms = new MemoryStream();
        var offsets = new List<long>();

        void Write(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }
        void BeginObject(int n) { offsets.Add(ms.Position); Write($"{n} 0 obj\n"); }

        Write("%PDF-1.4\n");
        BeginObject(1); Write("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        BeginObject(2); Write("<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        BeginObject(3);
        Write($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {F(pageW)} {F(pageH)}] " +
              "/Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        BeginObject(4);
        Write($"<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        BeginObject(5);
        Write($"<< /Type /XObject /Subtype /Image /Width {imageWidth} /Height {imageHeight} " +
              $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n");
        ms.Write(jpeg, 0, jpeg.Length);
        Write("\nendstream\nendobj\n");

        long xref = ms.Position;
        Write($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) Write($"{o:D10} 00000 n \n");
        Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        File.WriteAllBytes(path, ms.ToArray());
    }
}
