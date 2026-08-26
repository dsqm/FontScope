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

    // 公共绘制：给定字体集合与族名，DCRenderTarget + DrawTextLayout(EnableColorFont)
    internal static WriteableBitmap? DrawLayout(Vortice.DirectWrite.IDWriteFontCollection coll, string family,
        FaceInfo face, string text, float emSize, System.Windows.Media.Color fc)
    {
        IntPtr hdc = IntPtr.Zero, dib = IntPtr.Zero;
        try
        {
            using var fmt = DW.CreateTextFormat(family, coll,
                (FontWeight)face.WeightClass,
                face.IsItalic ? FontStyle.Italic : FontStyle.Normal,
                FontStretch.Normal, emSize, "");
            using var layout = DW.CreateTextLayout(text, fmt, 4096f, 4096f);
            var m = layout.Metrics;

            float pad = MathF.Max(emSize * 0.15f, 4f);
            int w = (int)MathF.Ceiling(m.Width + pad * 2);
            int h = (int)MathF.Ceiling(m.Height + pad * 2);
            if (w <= 0 || h <= 0 || w > 8192 || h > 8192) return null;

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
            // 平移使墨迹区精确落在 (pad, pad)：ink 占据 linebox 内的 [Left,Left+Width]×[Top,Top+Height]
            rtBase.DrawTextLayout(
                new Vector2(pad - m.Left, pad - m.Top),
                layout, brush, DrawTextOptions.EnableColorFont);
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
}
