using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using Vortice;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using FontStyle = Vortice.DirectWrite.FontStyle;
using FontStretch = Vortice.DirectWrite.FontStretch;
using IDWriteFactory = Vortice.DirectWrite.IDWriteFactory;
using ID2D1Factory = Vortice.Direct2D1.ID2D1Factory;
using FontWeight = Vortice.DirectWrite.FontWeight;

namespace FontScope;

// weasel 同款彩色字形渲染器（--d2dcheck 已验证出彩色）：
//   CreateDCRenderTarget + DrawTextLayout(ENABLE_COLOR_FONT)，
//   画到 GDI 内存 DIB，再整块拷入 WPF WriteableBitmap（BGRA premul 布局一致）。
// 用途：Skia 画不出的彩色字体（COLRv1/SVG 等）；未安装的字体文件先经
//   AddFontResourceEx 注册为本进程私有。族名在 DWrite 系统集合中找不到时返回 null，
//   由调用方回退到原 Skia 路径。
internal static class D2dTextRenderer
{
    const int FR_PRIVATE = 0x10;

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    static extern int AddFontResourceExW(string name, int flags, IntPtr res);

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight;
        public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; }

    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")]
    static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);

    static readonly object _gate = new();
    static ID2D1Factory? _d2dFactory;
    static IDWriteFactory? _dwFactory;

    static ID2D1Factory D2D => _d2dFactory ??= D2D1.D2D1CreateFactory<ID2D1Factory>(Vortice.Direct2D1.FactoryType.MultiThreaded);

    internal static IDWriteFactory DW => _dwFactory ??= Vortice.DirectWrite.DWrite.DWriteCreateFactory<IDWriteFactory>(
        Vortice.DirectWrite.FactoryType.Shared);

    /// <summary>渲染字形。失败（族名不可解析/尺寸异常）返回 null，调用方走 Skia 回退。</summary>
    /// <param name="emSizePx">像素级 em 大小（含超采样倍率）。</param>
    /// <param name="foreground">单色字体的文字颜色；彩色字形由字体自身调色板决定。</param>
    public static WriteableBitmap? Render(FaceInfo face, string text, double emSizePx,
        System.Windows.Media.Color? foreground = null)
    {
        lock (_gate)
        {
            try
            {
                // 未安装的字体注册为本进程私有；已安装则此调用幂等无害
                if (File.Exists(face.FilePath))
                    AddFontResourceExW(face.FilePath, FR_PRIVATE, IntPtr.Zero);

                // 族名解析：必须能在 DWrite 系统集合中命中，否则会静默回退成别的字体
                var sysColl = DW.GetSystemFontCollection(true);
                string? family = null;
                foreach (var cand in new[] { face.FamilyEn, face.FamilyEnLegacy, face.FamilyZh, face.DisplayName })
                {
                    if (string.IsNullOrEmpty(cand)) continue;
                    if (sysColl.FindFamilyName(cand, out _)) { family = cand; break; }
                }
                if (family == null) return null;

                return DrawLayout(sysColl, family, face, text, (float)emSizePx,
                    foreground ?? System.Windows.Media.Colors.Black);
            }
            catch
            {
                return null;
            }
        }
    }

    // 公共绘制：给定字体集合与族名，DCRenderTarget + 按"字体是否覆盖"切段渲染：
    //   - 覆盖段走 DrawTextLayout(EnableColorFont)——保留彩色字形，段宽 = layout Metrics.Width；
    //   - 不覆盖段画空心方框（豆腐块），段宽 ≈ emSize*0.6*字符数（贴近中文字宽的均值）。
    // 这样 DirectWrite 的系统字体回退不再被触发，所见即该物理 face 的真实字形——
    // emoji 字体不覆盖的中文显示为豆腐块，而非被系统字体替代。
    internal static WriteableBitmap? DrawLayout(Vortice.DirectWrite.IDWriteFontCollection coll, string family,
        FaceInfo face, string text, float emSize, System.Windows.Media.Color fc)
    {
        // 找族内 weight 对应的 font → 拿到 IDWriteFontFace 用于"是否覆盖"判断
        // （DrawTextLayout 内部也会按 weight 挑同一 font，覆盖与渲染保持一致）
        if (!coll.FindFamilyName(family, out int famIdx)) return null;
        var fam = coll.GetFontFamily(famIdx);
        var dwFont = MatchFontByWeight(fam, face.WeightClass, face.IsItalic);
        if (dwFont == null) return null;
        using var fontFace = dwFont.CreateFontFace();

        // 切段：按码点连续覆盖与否合并
        var runs = BuildRuns(text, fontFace);

        // 覆盖段建 TextFormat + TextLayout（每段独立 shape，禁回退自然成立）；累计总宽与行高
        float pad = MathF.Max(emSize * 0.15f, 4f);
        float totalW = 0, maxH = 0;
        var segLayouts = new List<(float w, Vortice.DirectWrite.IDWriteTextLayout layout)>();
        foreach (var (seg, covered) in runs)
        {
            if (covered)
            {
                using var fmt = DW.CreateTextFormat(family, coll,
                    (FontWeight)face.WeightClass,
                    face.IsItalic ? FontStyle.Italic : FontStyle.Normal,
                    FontStretch.Normal, emSize, "");
                var layout = DW.CreateTextLayout(seg, fmt, 4096f, 4096f);
                var m = layout.Metrics;
                if (m.Height > maxH) maxH = m.Height;
                segLayouts.Add((m.Width, layout));
                totalW += m.Width;
            }
            else
            {
                segLayouts.Add((0, null!));
                totalW += emSize * 0.6f * GlyphPreviewHelper.CodePointsOf(seg).Count();
            }
        }
        if (maxH <= 0) maxH = emSize * 1.4f; // 空文本/全缺字兜底

        // 缺字 .notdef 的 baseline：取首个覆盖段的真实 baseline（相对 linebox 顶）；
        // 全缺字时用 emSize*0.8 估
        float baseline = pad + emSize * 0.8f;
        foreach (var (_, layout) in segLayouts)
        {
            if (layout != null)
            {
                var lm = layout.LineMetrics;
                if (lm.Length > 0) { baseline = pad + lm[0].Baseline; break; }
                break;
            }
        }

        int w = (int)MathF.Ceiling(totalW + pad * 2);
        int h = (int)MathF.Ceiling(maxH + pad * 2);
        if (w <= 0 || h <= 0 || w > 8192 || h > 8192) return null;

        IntPtr hdc = IntPtr.Zero, dib = IntPtr.Zero;
        try
        {
            hdc = CreateCompatibleDC(IntPtr.Zero);
            var bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
            bmi.bmiHeader.biWidth = w;
            bmi.bmiHeader.biHeight = -h;          // top-down
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            dib = CreateDIBSection(hdc, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero) return null;
            SelectObject(hdc, dib);

            var rtProps = new RenderTargetProperties(RenderTargetType.Default,
                new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                96f, 96f, RenderTargetUsage.None, default);
            using var rt = D2D.CreateDCRenderTarget(rtProps);
            using var rtBase = rt.QueryInterface<ID2D1RenderTarget>();   // 该绑定中 DC RT 为平铺接口
            rt.BindDC(hdc, new RawRect(0, 0, w, h));

            using var brush = rtBase.CreateSolidColorBrush(
                new Color4(fc.R / 255f, fc.G / 255f, fc.B / 255f, 1f), null);
            rtBase.BeginDraw();
            rtBase.Clear(new Color4(0f, 0f, 0f, 0f));

            // 顺序按段绘制：覆盖段 DrawTextLayout；缺字段画字体自带 .notdef 字形（豆腐块）
            float x = pad;
            for (int i = 0; i < runs.Count; i++)
            {
                var (seg, covered) = runs[i];
                if (covered && segLayouts[i].layout != null)
                {
                    var (segW, layout) = segLayouts[i];
                    var m = layout.Metrics;
                    rtBase.DrawTextLayout(
                        new Vector2(x - m.Left, pad - m.Top),
                        layout, brush, DrawTextOptions.EnableColorFont);
                    x += segW;
                }
                else
                {
                    float tw = emSize * 0.6f * GlyphPreviewHelper.CodePointsOf(seg).Count();
                    DrawTofu(rtBase, fontFace, brush, x, baseline, emSize);
                    x += tw;
                }
            }
            var end = rtBase.EndDraw(out _, out _);
            if (end.Failure) return null;

            var buf = new byte[w * h * 4];
            Marshal.Copy(bits, buf, 0, buf.Length);

            var wb = new WriteableBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32, null);
            wb.WritePixels(new Int32Rect(0, 0, w, h), buf, w * 4, 0);
            wb.Freeze();
            return wb;
        }
        finally
        {
            if (dib != IntPtr.Zero) DeleteObject(dib);
            if (hdc != IntPtr.Zero) DeleteDC(hdc);
        }
    }

    // 切段：按码点连续覆盖与否合并。覆盖 = face.GetGlyphIndices(cp)[0] != 0
    static List<(string seg, bool covered)> BuildRuns(string text, Vortice.DirectWrite.IDWriteFontFace fontFace)
    {
        var runs = new List<(string seg, bool covered)>();
        for (int i = 0; i < text.Length; )
        {
            int cp = char.ConvertToUtf32(text, i);
            int clen = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            bool cov = fontFace.GetGlyphIndices(new[] { cp })[0] != 0;
            string seg = text.Substring(i, clen);
            if (runs.Count > 0 && runs[^1].covered == cov)
                runs[^1] = (runs[^1].seg + seg, cov);
            else runs.Add((seg, cov));
            i += clen;
        }
        return runs;
    }

    // 按字重（必要时按斜体）在族内找最接近的 font；找不到取第一个
    static Vortice.DirectWrite.IDWriteFont? MatchFontByWeight(
        Vortice.DirectWrite.IDWriteFontFamily fam, ushort wantWeight, bool wantItalic)
    {
        int n = fam.FontCount;
        if (n <= 0) return null;
        Vortice.DirectWrite.IDWriteFont? bestExact = null, bestClose = null, first = null;
        int bestDelta = int.MaxValue;
        for (int i = 0; i < n; i++)
        {
            var f = fam.GetFont(i);
            first ??= f;
            bool italic = f.Style == Vortice.DirectWrite.FontStyle.Italic;
            if (italic != wantItalic) continue;
            int delta = Math.Abs((int)f.Weight - (int)wantWeight);
            if (delta == 0) { bestExact = f; break; }
            if (delta < bestDelta) { bestDelta = delta; bestClose = f; }
        }
        return bestExact ?? bestClose ?? first;
    }

    // 缺字段占位：画该字体自己的 .notdef 字形（glyph 0，即标准"豆腐块"）——
    // 形状、大小都是字体自带的，比手画矩形自然；宽 = 字体真实 .notdef advance
    static void DrawTofu(Vortice.Direct2D1.ID2D1RenderTarget rt,
        Vortice.DirectWrite.IDWriteFontFace fontFace, Vortice.Direct2D1.ID2D1SolidColorBrush brush,
        float left, float baseline, float emSize)
    {
        float adv = GetNotdefAdvance(fontFace, emSize);
        var run = new Vortice.DirectWrite.GlyphRun
        {
            FontFace = fontFace,
            FontEmSize = emSize,
            Indices = new ushort[] { 0 },
            Advances = new float[] { adv },
            Offsets = new Vortice.DirectWrite.GlyphOffset[] { default },
            IsSideways = false,
            BidiLevel = 0,
        };
        rt.DrawGlyphRun(new Vector2(left, baseline), run, brush);
    }

    // 字体 glyph 0 的 advance（像素），下限 emSize*0.5 防字体度量异常时挤成一团
    static float GetNotdefAdvance(Vortice.DirectWrite.IDWriteFontFace fontFace, float emSize)
    {
        float adv;
        try
        {
            var metrics = fontFace.GetDesignGlyphMetrics(new ushort[] { 0 }, false);
            float upem = fontFace.Metrics.DesignUnitsPerEm;
            adv = upem > 0 ? metrics[0].AdvanceWidth / upem * emSize : emSize * 0.6f;
        }
        catch { adv = emSize * 0.6f; }
        return MathF.Max(adv, emSize * 0.5f);
    }
}
