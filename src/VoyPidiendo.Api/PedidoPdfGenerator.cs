using System.Globalization;
using System.IO;
using System.Text;

namespace VoyPidiendo.Api;

public static class PedidoPdfGenerator
{
    private const double PageWidth = 595.28;
    private const double PageHeight = 841.89;
    private static readonly CultureInfo Ar = CultureInfo.GetCultureInfo("es-AR");

    public static byte[] Generate(PedidoPdfData pedido, bool mostrarSubtotales = true)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var pages = BuildPages(pedido, mostrarSubtotales);
        return BuildDocument(pages);
    }

    private static List<string> BuildPages(PedidoPdfData pedido, bool mostrarSubtotales)
    {
        var pages = new List<string>();
        var index = 0;
        do
        {
            var s = new StringBuilder();
            Header(s, pedido, pages.Count + 1);
            var y = pages.Count == 0 ? 218d : 172d;
            TableHeader(s, y);
            y += 25;
            while (index < pedido.Lineas.Count)
            {
                var line = pedido.Lineas[index];
                var description = Wrap(line.Descripcion, 43);
                var rowHeight = description.Count > 1 ? 29d : 21d;
                if (y + rowHeight > 730) break;
                Row(s, y, rowHeight, line, description);
                y += rowHeight;
                index++;
            }
            if (index >= pedido.Lineas.Count)
            {
                Summary(s, pedido, y + 11, mostrarSubtotales);
            }
            else Text(s, "Continúa en la página siguiente", 395, 790, 8, false, 0, 0, 0);
            TextRight(s, $"Página {pages.Count + 1}", 573, 815, 8, false, 0, 0, 0);
            pages.Add(s.ToString());
        } while (index < pedido.Lineas.Count);
        return pages;
    }

    private static void Header(StringBuilder s, PedidoPdfData p, int page)
    {
        Line(s, 22, 34, 350, 34, 0, 0, 0, 2);
        Text(s, p.Empresa, 22, 61, 18, true, 0, 0, 0);
        if (!string.IsNullOrWhiteSpace(p.EmpresaDireccion)) Text(s, p.EmpresaDireccion, 22, 81, 9, false, 0, 0, 0);
        if (!string.IsNullOrWhiteSpace(p.EmpresaCuit)) Text(s, $"CUIT: {p.EmpresaCuit}", 22, 98, 9, false, 0, 0, 0);
        Text(s, "Nº PEDIDO", 380, 58, 16, true, 0, 0, 0);
        TextRight(s, OrderNumber(p), 573, 58, 12, true);
        Text(s, "FECHA", 408, 84, 16, true, 0, 0, 0);
        TextRight(s, FormatDateTime(p.Fecha, p.Hora), 573, 84, 10, false);
        Line(s, 380, 102, 573, 102, 0, 0, 0);
        Line(s, 22, 124, 573, 124, 0, 0, 0, 2);
        if (page == 1)
        {
            LabelValue(s, "CLIENTE", p.Cliente, 145);
            LabelValue(s, "CUIT", p.ClienteCuit, 145, 400);
            LabelValue(s, "DOMICILIO", p.Domicilio, 165);
            LabelValue(s, "TIPO IVA", p.ClienteTipoIva, 165, 400);
            LabelValue(s, "LOCALIDAD", p.Localidad, 185);
            LabelValue(s, "VENDEDOR", p.Vendedor, 185, 400);
        }
        else Text(s, $"Pedido {OrderNumber(p)} - continuación", 22, 155, 9, true, 0, 0, 0);
    }

    private static void Summary(StringBuilder s, PedidoPdfData p, double y, bool mostrarSubtotales)
    {
        Line(s, 22, y, 573, y, 0, 0, 0);
        if (mostrarSubtotales)
        {
            Text(s, "SUBTOTAL", 22, y + 23, 7, true, 0, 0, 0);
            TextRight(s, Money(p.SubTotal), 145, y + 23, 9, true);
            Text(s, "IVA", 165, y + 23, 7, true, 0, 0, 0);
            TextRight(s, Money(p.Iva), 255, y + 23, 9, true);
            Text(s, $"PERCEP. IIBB ({Number(p.PercepcionAlicuota)}%)", 275, y + 23, 7, true, 0, 0, 0);
            TextRight(s, Money(p.PercepcionIibb), 430, y + 23, 9, true);
        }
        Text(s, "TOTAL", 448, y + 23, 8, true, 0, 0, 0);
        TextRight(s, Money(p.Total), 573, y + 23, 11, true);
        if (!string.IsNullOrWhiteSpace(p.Comentario)) TextCenter(s, p.Comentario, PageWidth / 2, y + 47, 8, true);
    }

    private static void LabelValue(StringBuilder s, string label, string value, double y, double x = 22)
    {
        Text(s, label, x, y, 9, true, 0, 0, 0);
        Text(s, value, x + (x > 300 ? 66 : 68), y, 9, false, 0, 0, 0);
    }

    private static void TableHeader(StringBuilder s, double y)
    {
        FillRect(s, 22, y, 551, 24, .94, .94, .94);
        TextRight(s, "CANTIDAD", 80, y + 16, 8, true, 0, 0, 0);
        Text(s, "CÓDIGO", 100, y + 16, 8, true, 0, 0, 0);
        Text(s, "ARTÍCULO", 175, y + 16, 8, true, 0, 0, 0);
        TextRight(s, "PRECIO UNITARIO", 478, y + 16, 8, true, 0, 0, 0);
        TextRight(s, "PRECIO TOTAL", 568, y + 16, 8, true, 0, 0, 0);
    }

    private static void Row(StringBuilder s, double y, double height, PedidoPdfLinea line, List<string> description)
    {
        Line(s, 22, y + height, 573, y + height, .84, .87, .89);
        TextRight(s, Number(line.Cantidad), 80, y + 14, 8, false);
        Text(s, line.Codigo.ToString("00000", Ar), 100, y + 14, 8, false);
        for (var i = 0; i < description.Count; i++) Text(s, description[i], 175, y + 14 + i * 11, 8, false);
        TextRight(s, Money(line.PrecioUnitario), 478, y + 14, 8, false);
        TextRight(s, Money(line.Importe), 568, y + 14, 8, true);
    }

    private static List<string> Wrap(string value, int max)
    {
        value = string.IsNullOrWhiteSpace(value) ? "Sin descripción" : value.Trim();
        if (value.Length <= max) return [value];
        var cut = value.LastIndexOf(' ', Math.Min(max, value.Length - 1));
        if (cut < max / 2) cut = max;
        var first = value[..cut].Trim();
        var second = value[cut..].Trim();
        if (second.Length > max) second = second[..(max - 1)] + "…";
        return [first, second];
    }

    private static string FormatDateTime(int date, int time)
    {
        var d = date.ToString("00000000");
        var result = date > 0 ? $"{d[6..8]}/{d[4..6]}/{d[..4]}" : string.Empty;
        if (time > 0)
        {
            var h = time.ToString("0000");
            result += $" {h[..2]}:{h[2..4]}";
        }
        return result;
    }

    private static string Money(decimal value) => "$ " + value.ToString("N2", Ar);
    private static string Number(decimal value) => value.ToString(value == decimal.Truncate(value) ? "N0" : "N2", Ar);
    private static string OrderNumber(PedidoPdfData value) => $"{value.Prefijo:0000}-{value.Remito:00000000}";
    private static double PdfY(double top) => PageHeight - top;
    private static string E(string value) => value.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)").Replace("\r", " ").Replace("\n", " ");
    private static double Width(string value, double size) => value.Length * size * .49;

    private static void Text(StringBuilder s, string value, double x, double top, double size, bool bold, double r = 0, double g = 0, double b = 0) =>
        s.AppendFormat(CultureInfo.InvariantCulture, "BT {0:0.###} {1:0.###} {2:0.###} rg /{3} {4:0.##} Tf {5:0.##} {6:0.##} Td ({7}) Tj ET\n", r, g, b, bold ? "F2" : "F1", size, x, PdfY(top), E(value));
    private static void TextRight(StringBuilder s, string value, double right, double top, double size, bool bold, double r = 0, double g = 0, double b = 0) => Text(s, value, right - Width(value, size), top, size, bold, r, g, b);
    private static void TextCenter(StringBuilder s, string value, double center, double top, double size, bool bold) => Text(s, value, center - Width(value, size) / 2, top, size, bold, 0, 0, 0);
    private static void Line(StringBuilder s, double x1, double y1, double x2, double y2, double r, double g, double b, double width = 1) =>
        s.AppendFormat(CultureInfo.InvariantCulture, "{0:0.###} {1:0.###} {2:0.###} RG {3:0.##} w {4:0.##} {5:0.##} m {6:0.##} {7:0.##} l S\n", r, g, b, width, x1, PdfY(y1), x2, PdfY(y2));
    private static void FillRect(StringBuilder s, double x, double top, double width, double height, double r, double g, double b) =>
        s.AppendFormat(CultureInfo.InvariantCulture, "{0:0.###} {1:0.###} {2:0.###} rg {3:0.##} {4:0.##} {5:0.##} {6:0.##} re f\n", r, g, b, x, PdfY(top + height), width, height);

    private static byte[] BuildDocument(List<string> pages)
    {
        var enc = Encoding.GetEncoding(1252);
        var objects = new Dictionary<int, byte[]>();
        objects[1] = enc.GetBytes("<< /Type /Catalog /Pages 2 0 R >>");
        objects[3] = enc.GetBytes("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        objects[4] = enc.GetBytes("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        var kids = new List<string>();
        for (var i = 0; i < pages.Count; i++)
        {
            var contentId = 5 + i * 2;
            var pageId = contentId + 1;
            var content = enc.GetBytes(pages[i]);
            var head = enc.GetBytes($"<< /Length {content.Length} >>\nstream\n");
            var tail = enc.GetBytes("\nendstream");
            objects[contentId] = head.Concat(content).Concat(tail).ToArray();
            objects[pageId] = enc.GetBytes($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageWidth.ToString(CultureInfo.InvariantCulture)} {PageHeight.ToString(CultureInfo.InvariantCulture)}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {contentId} 0 R >>");
            kids.Add($"{pageId} 0 R");
        }
        objects[2] = enc.GetBytes($"<< /Type /Pages /Kids [{string.Join(' ', kids)}] /Count {pages.Count} >>");
        using var output = new MemoryStream();
        void Write(string value) { var bytes = enc.GetBytes(value); output.Write(bytes); }
        Write("%PDF-1.4\n%âãÏÓ\n");
        var offsets = new long[objects.Count + 1];
        for (var id = 1; id <= objects.Count; id++)
        {
            offsets[id] = output.Position;
            Write($"{id} 0 obj\n"); output.Write(objects[id]); Write("\nendobj\n");
        }
        var xref = output.Position;
        Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        for (var id = 1; id <= objects.Count; id++) Write($"{offsets[id]:0000000000} 00000 n \n");
        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");
        return output.ToArray();
    }
}
