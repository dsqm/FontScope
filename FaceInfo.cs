using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using FontFamily = System.Windows.Media.FontFamily;
using SkiaSharp;

namespace FontScope;

public enum FontSource { System, User, Custom }

// 一个物理字体 face（ttc 已展开），持有 cmap 索引与懒构造的 GlyphTypeface
public sealed class FaceInfo
{
    public string FilePath = "";
    public int FaceIndex;
    public bool IsCollection;
    public string FamilyEn = "";
    public string FamilyEnLegacy = "";
    public string FamilyZh = "";
    public string SubFamily = "Regular";
    public string FullNameEn = "";
    public string FullNameZh = "";
    public ushort WeightClass = 400;
    public bool IsItalic;
    public bool IsColorFont;       // 含 COLR/CBDT/sbix/SVG 任一彩色表（信息性；渲染统一走 Skia）
    public HashSet<uint> CodePoints = new();
    public FontSource Source;

    // ---------- 解析层填充的扩展信息（SfntParser） ----------

    public string Outline = "";       // 轮廓类型：TrueType('glyf') / CFF / CFF2
    public ushort UnitsPerEm = 1000;  // head.unitsPerEm
    public short TypoAscender;        // OS/2.sTypoAscender，缺省取 hhea.ascender
    public short TypoDescender;       // 同上
    public ushort WidthClass = 5;     // OS/2.usWidthClass
    public ushort FsType;             // OS/2.fsType 嵌入许可位

    // 标准名称表：nameID -> (英, 中)；显示时中文优先
    public readonly Dictionary<ushort, (string En, string Zh)> NameTable = new();

    public string Name(ushort id)
        => NameTable.TryGetValue(id, out var t) ? (t.Zh.Length > 0 ? t.Zh : t.En) : "";

    // 格式显示：容器 + 轮廓，如「TrueType」「OpenType CFF」「TTC · TrueType」
    public string FormatDisplay
    {
        get
        {
            var core = Outline switch
            {
                "CFF" => "OpenType CFF",
                "CFF2" => "OpenType CFF2",
                _ => "TrueType"
            };
            return IsCollection ? "TTC · " + core : core;
        }
    }

    // OS/2.fsType 位含义（0 位域为「可安装嵌入」）
    public string FsTypeDisplay => FsType switch
    {
        0 => "可安装嵌入",
        _ => (FsType & 0x08) != 0 ? "可编辑嵌入"
           : (FsType & 0x04) != 0 ? "预览打印嵌入"
           : (FsType & 0x02) != 0 ? "受限嵌入"
           : "0x" + FsType.ToString("X")
    };

    // 显示名：zh 优先
    public string DisplayName => FamilyZh.Length > 0 ? FamilyZh : FamilyEn;
    public string FileName => Path.GetFileName(FilePath);
    public string FullDisplayName => FullNameZh.Length > 0 ? FullNameZh : FullNameEn;
    public string StyleDisplay => (IsItalic, SubFamily) switch
    {
        (true, "Regular") or (true, "") => "Italic",
        (true, var s) => s + " Italic",
        (false, "") => "Regular",
        (false, var s) => s
    };
    public string SourceDisplay => Source switch { FontSource.System => "系统", FontSource.User => "用户", _ => "自定义" };

    GlyphTypeface? _gt;
    bool _gtTried;

    // 懒构造渲染用 GlyphTypeface（仅 SelfTest 等诊断用；主渲染路径已统一走 Skia）
    public GlyphTypeface? GetGlyphTypeface()
    {
        if (_gt != null || _gtTried) return _gt;
        _gtTried = true;
        try
        {
            if (!IsCollection)
            {
                _gt = new GlyphTypeface(new Uri(FilePath));
            }
            else
            {
                // ttc：用 #family 语法命中指定 face
                var dir = Path.GetDirectoryName(FilePath) + Path.DirectorySeparatorChar;
                var file = "./" + Path.GetFileName(FilePath);
                foreach (var fam in new[] { FamilyEn, FamilyEnLegacy })
                {
                    if (fam.Length == 0) continue;
                    var ff = new FontFamily(new Uri(dir), file + "#" + fam);
                    foreach (var tf in ff.GetTypefaces())
                        if (tf.TryGetGlyphTypeface(out var g)) { _gt = g; break; }
                    if (_gt != null) break;
                }
            }
        }
        catch { }
        return _gt;
    }

    public bool Covers(IReadOnlyList<uint> codepoints) => codepoints.All(CodePoints.Contains);

    // ---------- 占坑字体检测（有码位但字形空白，画出来纯透明） ----------

    // 按码点缓存（ConcurrentDictionary：大结果集时探测在并行线程中执行）
    readonly ConcurrentDictionary<string, bool> _inkCache = new();

    // 彩色字体直接视为可见：COLRv1/SVG 在 Skia 下本就画不出墨迹，不能据此判空（系统 D2D 路径可显示）
    public bool RendersInk(string text)
    {
        if (IsColorFont || string.IsNullOrWhiteSpace(text)) return true;
        bool any = false;
        for (int i = 0; i < text.Length; )
        {
            int len = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            var unit = text.Substring(i, len);
            if (_inkCache.GetOrAdd(unit, ProbeUnit)) any = true;
            i += len;
        }
        return any;
    }

    // 零光栅化预判：字形轮廓的墨迹包围盒（MeasureText+bounds）为空即占坑，
    // 不再绘制位图扫像素——上万像素级查询时这是性能关键。
    // 共享表缺陷集合（face>0）优先走 TtcOutlineProbe 直读 cmap+loca 判空：
    // 若在探测路径做整文件抽取，大结果集并行时会产生 GB 级拷贝导致假死。
    bool ProbeUnit(string s)
    {
        try
        {
            if (IsCollection && FaceIndex > 0 && TtcFaceExtractor.IsCollection(FilePath))
            {
                uint cp = s.Length == 2 && char.IsHighSurrogate(s[0]) && char.IsLowSurrogate(s[1])
                    ? (uint)char.ConvertToUtf32(s[0], s[1])
                    : s[0];
                var r = TtcOutlineProbe.HasOutline(FilePath, FaceIndex, cp);
                if (r.HasValue) return r.Value;
            }
            var tf = GetSkTypeface();
            if (tf == null) return true; // 打不开的交给预览层处理，不在此降权
            using var font = new SKFont(tf, 48f);
            font.MeasureText(s, out var b);
            return b.Width > 0 && b.Height > 0;
        }
        catch { return true; } // 探测失败按可见处理，宁可排序靠前也不误伤
    }

    SKTypeface? _sk;
    bool _skTried;

    // 懒构造渲染用 Skia typeface（线程：UI）；固定该物理 face，绘制时不回退到其他字体。
    // 个别共享表 TTC 按索引 FromFile 光栅化为空（仅 face0 可用），
    // face>0 先经 TtcFaceStore 抽取为独立 sfnt 字节流再加载，绕开该缺陷。
    public SKTypeface? GetSkTypeface()
    {
        if (_sk != null || _skTried) return _sk;
        _skTried = true;
        try
        {
            if (IsCollection && FaceIndex > 0)
            {
                var bytes = TtcFaceStore.GetBytes(FilePath, FaceIndex);
                if (bytes != null)
                    // MemoryStream 由 SKManagedStream 持有，字节缓存保证其长期存活
                    _sk = SKTypeface.FromStream(new MemoryStream(bytes));
            }
            if (_sk == null)
                _sk = SKTypeface.FromFile(FilePath, FaceIndex);
        }
        catch { }
        return _sk;
    }
}
