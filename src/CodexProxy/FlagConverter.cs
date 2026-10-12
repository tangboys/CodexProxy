using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace CodexProxy;

// Small vector flags render consistently on Windows, including systems without flag emoji fonts.
public sealed class FlagConverter : IValueConverter
{
    private readonly Dictionary<string, DrawingImage> images = [];
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var code = value as string ?? "";
        if (images.TryGetValue(code, out var cached)) return cached;
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            Brush Color(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;
            void Rect(string color, double x, double y, double w, double h) => dc.DrawRectangle(Color(color), null, new Rect(x, y, w, h));
            void Circle(string color, double x, double y, double r) => dc.DrawEllipse(Color(color), null, new Point(x, y), r, r);
            void Polygon(string color, params Point[] points)
            {
                var geometry = new StreamGeometry();
                using (var path = geometry.Open()) { path.BeginFigure(points[0], true, true); path.PolyLineTo(points.Skip(1).ToArray(), true, false); }
                dc.DrawGeometry(Color(color), null, geometry);
            }
            void Star(string color, double x, double y, double radius)
            {
                var points = Enumerable.Range(0, 10).Select(i => { var a = i * Math.PI / 5 - Math.PI / 2; var r = i % 2 == 0 ? radius : radius * .4; return new Point(x + Math.Cos(a) * r, y + Math.Sin(a) * r); }).ToArray();
                Polygon(color, points);
            }
            void Stripes(params string[] colors)
            { for (var i = 0; i < colors.Length; i++) Rect(colors[i], 0, 20d * i / colors.Length, 30, 20d / colors.Length); }
            void UnionJack(double scale = 1)
            {
                dc.PushTransform(new ScaleTransform(scale, scale));
                Rect("#173A78", 0, 0, 30, 20);
                foreach (var (color, width) in new[] { ("White", 5d), ("#CE243B", 2d) })
                { dc.DrawLine(new Pen(Color(color), width), new Point(0, 0), new Point(30, 20)); dc.DrawLine(new Pen(Color(color), width), new Point(30, 0), new Point(0, 20)); }
                Rect("White", 0, 7, 30, 6); Rect("White", 12, 0, 6, 20);
                Rect("#CE243B", 0, 8.5, 30, 3); Rect("#CE243B", 13.5, 0, 3, 20);
                dc.Pop();
            }
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, 30, 20)));
            Rect("White", 0, 0, 30, 20);
            switch (code)
            {
                case "JP": Circle("#BC002D", 15, 10, 5.5); break;
                case "US":
                    for (var i = 0; i < 13; i++) Rect(i % 2 == 0 ? "#B22234" : "White", 0, i * 20d / 13, 30, 20d / 13);
                    Rect("#3C3B6E", 0, 0, 13, 10.8);
                    for (var row = 0; row < 9; row++) for (var col = 0; col < (row % 2 == 0 ? 6 : 5); col++) Star("White", 1.1 + col * 2.15 + (row % 2) * 1.08, 1 + row * 1.1, .65);
                    break;
                case "HK":
                    Rect("#DE2910", 0, 0, 30, 20);
                    for (var i = 0; i < 5; i++) { dc.PushTransform(new RotateTransform(i * 72, 15, 10)); dc.DrawEllipse(Brushes.White, null, new Point(15, 6.5), 1.7, 3); Star("#DE2910", 15, 6, .75); dc.Pop(); } break;
                case "TW":
                    Rect("#FE0000", 0, 0, 30, 20); Rect("#000095", 0, 0, 15, 10);
                    for (var i = 0; i < 12; i++) { var a = i * Math.PI / 6; Polygon("White", new(7.5 + Math.Cos(a) * 4, 5 + Math.Sin(a) * 4), new(7.5 + Math.Cos(a + .15) * 2, 5 + Math.Sin(a + .15) * 2), new(7.5 + Math.Cos(a - .15) * 2, 5 + Math.Sin(a - .15) * 2)); }
                    Circle("#000095", 7.5, 5, 2.3); Circle("White", 7.5, 5, 1.8); break;
                case "SG": Stripes("#EF3340", "White"); Circle("White", 6, 5, 3.5); Circle("#EF3340", 7.5, 4.7, 3);
                    for (var i = 0; i < 5; i++) { var a = i * Math.PI * 2 / 5 - Math.PI / 2; Star("White", 11 + Math.Cos(a) * 2.5, 5 + Math.Sin(a) * 2.5, .9); } break;
                case "GB": UnionJack(); break;
                case "DE": Stripes("#171717", "#DD0000", "#FFCE00"); break;
                case "RU": Stripes("White", "#0039A6", "#D52B1E"); break;
                case "NL": Stripes("#AE1C28", "White", "#21468B"); break;
                case "FR": Rect("#002395", 0, 0, 10, 20); Rect("#ED2939", 20, 0, 10, 20); break;
                case "CA": Rect("#D80621", 0, 0, 7.5, 20); Rect("#D80621", 22.5, 0, 7.5, 20);
                    Polygon("#D80621", new(15, 3), new(17, 7), new(19, 6), new(18, 10), new(21, 9), new(20, 13), new(16, 14), new(15.5, 17), new(14.5, 17), new(14, 14), new(10, 13), new(9, 9), new(12, 10), new(11, 6), new(13, 7)); break;
                case "AU": Rect("#012169", 0, 0, 30, 20); UnionJack(.5); Star("White", 7.5, 15, 2.5); Star("White", 23, 5, 1.7); Star("White", 19, 11, 1.7); Star("White", 27, 10, 1.7); Star("White", 23, 17, 1.7); break;
                case "IN": Stripes("#FF9933", "White", "#138808"); Circle("#000080", 15, 10, 2.5); Circle("White", 15, 10, 1.8); break;
                case "CN": Rect("#DE2910", 0, 0, 30, 20); Star("#FFDE00", 5, 5, 3); Star("#FFDE00", 10, 2, 1); Star("#FFDE00", 12, 4.5, 1); Star("#FFDE00", 12, 7.5, 1); Star("#FFDE00", 10, 10, 1); break;
                case "TR": Rect("#E30A17", 0, 0, 30, 20); Circle("White", 12, 10, 5); Circle("#E30A17", 13.5, 10, 4); Star("White", 20, 10, 2.5); break;
                case "BR": Rect("#009B3A", 0, 0, 30, 20); Polygon("#FFDF00", new(15, 2), new(28, 10), new(15, 18), new(2, 10)); Circle("#002776", 15, 10, 5); dc.DrawLine(new Pen(Brushes.White, 1), new(11, 8), new(19, 11)); break;
                case "KR":
                    Circle("#CD2E3A", 15, 10, 4.5);
                    var half = Geometry.Parse("M 10.5,10 A 4.5,4.5 0 0 0 19.5,10 A 2.25,2.25 0 0 0 15,10 A 2.25,2.25 0 0 1 10.5,10 Z"); dc.DrawGeometry(Color("#0047A0"), null, half);
                    foreach (var x in new[] { 4d, 23d }) foreach (var y in new[] { 3d, 14d }) for (var i = 0; i < 3; i++) Rect("#171717", x, y + i * 1.2, 3, .7); break;
                default:
                    Rect("#E9EEF3", 0, 0, 30, 20); Circle("#82909D", 15, 10, 6); Circle("#E9EEF3", 15, 10, 4.5); dc.DrawLine(new Pen(Brushes.White, 1), new(9, 10), new(21, 10)); break;
            }
            dc.Pop();
            dc.DrawRoundedRectangle(null, new Pen(Color("#D7DEE5"), .5), new Rect(0, 0, 30, 20), 1, 1);
        }
        var image = new DrawingImage(drawing); image.Freeze(); images[code] = image; return image;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
