using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using FontStyle = System.Drawing.FontStyle;

namespace FontScope;

// GDI+ 回退渲染器（第三级）：Skia、D2D 都画不出时使用。
// PrivateFontCollection 直接从文件加载字体，不经系统集合与 GDI 字体表，
// 对部分系统栈不认的「假 TTF」CFF 字体（如方正卡通）仍可出字。
internal static class GdiPlusTextRenderer
{
    static readonly object _gate = new();
    static readonly Dictionary<string, PrivateFontCollection> _collections = new();

    /// <summary>失败返回 null。</param>
    public static WriteableBitmap? Render(FaceInfo face, string text, double emSizePx,
        System.Windows.Media.Color? foreground = null)
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(face.FilePath)) return null;
                if (!_collections.TryGetValue(face.FilePath, out var pfc))
                {
                    pfc = new PrivateFontCollection();
                    pfc.AddFontFile(face.FilePath);
                    _collections[face.FilePath] = pfc;
                }

                // TTC 按 faceIndex 抽取为独立 sfnt 再加载：个别共享表集合按索引
                // 访问时 GDI+ 同样光栅化为空（仅 face0 可用），抽取绕开该缺陷。
                // 缓存与固定句柄由 TtcFaceStore 统一管理（与 Skia 路径共享字节）。
                if (face.IsCollection && face.FaceIndex > 0)
                {
                    var memPfc = TtcFaceStore.GetPfc(face.FilePath, face.FaceIndex);
                    if (memPfc != null) pfc = memPfc;
                }

                // TTC 多 face 时按族名匹配；匹配不到退第一个
                var fam = pfc.Families.FirstOrDefault(f =>
                    f.Name == face.FamilyEn || f.Name == face.FamilyZh || f.Name == face.DisplayName)
                    ?? pfc.Families[0];

                var style = face.IsItalic ? FontStyle.Italic : FontStyle.Regular;
                using var font = new Font(fam, (float)emSizePx, style, GraphicsUnit.Pixel);

                int pad = (int)MathF.Max((float)emSizePx * 0.15f, 4f);
                // MeasureString 含悬挂余量，直接按其结果加边距排版
                using var bmp = new Bitmap((int)(emSizePx * text.Length * 1.5) + 64, (int)(emSizePx * 2.2));
                using var g = Graphics.FromImage(bmp);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.Clear(System.Drawing.Color.Transparent);
                var fc = foreground ?? System.Windows.Media.Colors.Black;
                using var brush = new SolidBrush(System.Drawing.Color.FromArgb(fc.A, fc.R, fc.G, fc.B));
                g.DrawString(text, font, brush, pad, pad);
                g.Flush();

                // 裁掉右侧多余空白：找最大非透明列
                var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
                var bd = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                int stride = Math.Abs(bd.Stride);
                var buf = new byte[stride * bmp.Height];
                Marshal.Copy(bd.Scan0, buf, 0, buf.Length);
                bmp.UnlockBits(bd);

                // 全量扫描墨迹包围盒（不能逐行提前 break：最左墨迹不代表行内最右）
                int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
                for (int y = 0; y < bmp.Height; y++)
                {
                    int rowOff = y * stride;
                    for (int x = 0; x < bmp.Width; x++)
                        if (buf[rowOff + x * 4 + 3] != 0)
                        {
                            if (x < minX) minX = x;
                            if (x > maxX) maxX = x;
                            if (y < minY) minY = y;
                            maxY = y;
                        }
                }
                if (maxX < 0 || maxY < 0) return null; // 纯透明

                int w = Math.Min(maxX + pad, bmp.Width);
                int h = Math.Min(maxY + pad, bmp.Height);
                if (w <= 0 || h <= 0 || w > 8192 || h > 8192) return null;

                var wb = new WriteableBitmap(w, h, 96, 96,
                    System.Windows.Media.PixelFormats.Pbgra32, null);
                wb.WritePixels(new Int32Rect(0, 0, w, h), buf, stride, 0);
                wb.Freeze();
                return wb;
            }
            catch
            {
                return null;
            }
        }
    }
}
