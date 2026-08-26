using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brush = System.Windows.Media.Brush;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using SkiaSharp;

namespace FontScope;

// 统一渲染（混合路径）：
//   1) 彩色字体且族名可被 DWrite 解析 → D2dTextRenderer（weasel 同款系统 Direct2D，
//      DrawTextLayout+ENABLE_COLOR_FONT，COLRv1/SVG/CBDT 全支持，如新版 Noto Color Emoji）；
//   2) 其余一律走 SkiaSharp（普通字体与部分彩色字体），锁定物理 face 不回退，
//      保证"所见即该物理 face 的真实字形"。
public class GlyphPreview : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(GlyphPreview),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty FaceProperty = DependencyProperty.Register(
        nameof(Face), typeof(FaceInfo), typeof(GlyphPreview),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(
        typeof(GlyphPreview), new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(GlyphPreview), new FrameworkPropertyMetadata(System.Windows.Media.Brushes.Black));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public FaceInfo? Face
    {
        get => (FaceInfo?)GetValue(FaceProperty);
        set => SetValue(FaceProperty, value);
    }

    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    // 2x 超采样：渲染分辨率高于逻辑尺寸，DrawImage 缩放到逻辑尺寸时保持清晰
    const float Supersample = 2f;

    ImageSource? _bitmap;
    Size _logical;
    string? _sig;

    string Sig() => $"{Text}|{Face?.FilePath}|{Face?.FaceIndex}|{FontSize}|{Foreground}";

    protected override Size MeasureOverride(Size availableSize)
    {
        var sig = Sig();
        if (_bitmap != null && sig == _sig) return _logical;

        var text = Text;
        var face = Face;
        if (face == null || string.IsNullOrEmpty(text)) return new Size(0, 0);

        // 彩色字体（COLRv1/SVG 等 Skia 画不出的）先走 weasel 同款系统 D2D 路径；
        // 失败（族名不可解析等）再回退 Skia。普通字体一律 Skia。
        if (face.IsColorFont)
        {
            var d2d = D2dTextRenderer.Render(face, text, FontSize * Supersample, FgColor());
            if (d2d != null)
            {
                _bitmap = d2d;
                _logical = new Size(d2d.PixelWidth / Supersample, d2d.PixelHeight / Supersample);
                _sig = sig;
                return _logical;
            }
        }

        var tf = face.GetSkTypeface();
        if (tf == null) return new Size(0, 0);

        float emPx = (float)(FontSize * Supersample);
        using var font = new SKFont(tf, emPx);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Color = ToSkColor(Foreground, SKColors.Black),
            // 彩色字形由字体自带调色板绘制，忽略此色；此处仅作用于单色字体
        };

        float padX = emPx * 0.12f;
        float padY = emPx * 0.12f;

        // 锁定该物理 face 的字形（无字体回退）；用 SKTextBlob 测墨迹包围盒并据此排版
        using var blob = SKTextBlob.Create(text, font, SKPoint.Empty);
        if (blob == null) return new Size(0, 0);
        var tb = blob.Bounds;
        if (tb.Width <= 0 || tb.Height <= 0) return new Size(0, 0);

        // 以墨迹包围盒定位：保证内容不被裁切；DrawText(blob) 按字体 advance 排版
        float widthPx = tb.Width + 2 * padX;
        float heightPx = tb.Height + 2 * padY;
        float baselineY = -tb.Top + padY;

        int W = (int)Math.Ceiling(widthPx);
        int H = (int)Math.Ceiling(heightPx);
        if (W <= 0 || H <= 0) return new Size(0, 0);

        using var bmp = new SKBitmap(W, H, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var cv = new SKCanvas(bmp))
        {
            cv.Clear(SKColors.Transparent);
            cv.DrawText(blob, padX, baselineY, paint);
        }

        // Skia 光栅化为空（如方正老 CFF「假 TTF」：度量有墨迹但画不出）→ 回退系统 D2D，
        // 再不行走 GDI+（PrivateFontCollection 独立加载）；三路都空才是真占坑字形
        if (!HasInk(bmp))
        {
            var d2d = D2dTextRenderer.Render(face, text, FontSize * Supersample, FgColor());
            if (d2d != null)
            {
                _bitmap = d2d;
                _logical = new Size(d2d.PixelWidth / Supersample, d2d.PixelHeight / Supersample);
                _sig = sig;
                return _logical;
            }
            var gdip = GdiPlusTextRenderer.Render(face, text, FontSize * Supersample, FgColor());
            if (gdip != null)
            {
                _bitmap = gdip;
                _logical = new Size(gdip.PixelWidth / Supersample, gdip.PixelHeight / Supersample);
                _sig = sig;
                return _logical;
            }
        }

        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        var ms = new MemoryStream();
        data.SaveTo(ms);
        ms.Position = 0;
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = ms;
        bi.EndInit();
        bi.Freeze();

        _bitmap = bi;
        _logical = new Size(W / Supersample, H / Supersample);
        _sig = sig;
        return _logical;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_bitmap != null)
            dc.DrawImage(_bitmap, new Rect(0, 0, _logical.Width, _logical.Height));
    }

    static SKColor ToSkColor(Brush b, SKColor fallback)
    {
        if (b is SolidColorBrush sc)
            return new SKColor(sc.Color.R, sc.Color.G, sc.Color.B, sc.Color.A);
        return fallback;
    }

    System.Windows.Media.Color FgColor()
        => Foreground is SolidColorBrush sc ? sc.Color : System.Windows.Media.Colors.Black;

    // 全图 alpha 扫描：判断 Skia 输出是否纯透明
    static bool HasInk(SKBitmap bmp)
    {
        int n = bmp.Width * bmp.Height * 4;
        var buf = new byte[n];
        Marshal.Copy(bmp.GetPixels(), buf, 0, n);
        for (int i = 3; i < n; i += 4)
            if (buf[i] != 0) return true;
        return false;
    }
}
