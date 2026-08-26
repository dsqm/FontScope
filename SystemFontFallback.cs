using System.Windows.Media;

namespace FontScope;

// 输入框的系统字体回退：WPF 文本控件的缺字回退只查自带复合字体表
// （GlobalUserInterface 等），不会像其它软件那样扫描全部已安装字体，
// 生僻码位（如 U+30EDD「𰻝」）会显示成方框。
//
// 这里对基础字体画不了的码点，逐个在系统已安装字体中找到覆盖它的族，
// 生成一条可直接用于 FontFamily 的逗号回退链：WPF 会按链逐族尝试每个
// 字符，命中即用——效果等价于系统级回退，但作用范围仅限调用方控件。
internal static class SystemFontFallback
{
    // 族名 -> 默认物理字面；码点 -> 能画它的族名（null = 基础族可画，或全系统都画不了）
    static readonly Dictionary<string, GlyphTypeface?> _gtCache = new();
    static readonly Dictionary<int, string?> _cpCache = new();

    /// <summary>返回应设置到控件的 FontFamily 字符串；无缺口时原样返回 baseSource。</summary>
    public static string BuildChain(string baseSource, string text)
    {
        var extra = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var cp in CodePoints(text))
        {
            if (!_cpCache.TryGetValue(cp, out var fam))
            {
                fam = Covers(GetGt(baseSource), cp) ? null : FindCoveringFamily(cp);
                _cpCache[cp] = fam;
            }
            if (fam != null) extra.Add(fam);
        }
        return extra.Count == 0 ? baseSource : baseSource + "," + string.Join(",", extra);
    }

    static IEnumerable<int> CodePoints(string s)
    {
        for (int i = 0; i < s.Length; )
        {
            int cp = char.ConvertToUtf32(s, i);
            yield return cp;
            i += i + 1 < s.Length && char.IsHighSurrogate(s[i]) && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
        }
    }

    static bool Covers(GlyphTypeface? gt, int cp) =>
        gt != null && gt.CharacterToGlyphMap.TryGetValue(cp, out var gid) && gid != 0;

    static GlyphTypeface? GetGt(string familySource)
    {
        if (!_gtCache.TryGetValue(familySource, out var gt))
        {
            try { gt = new Typeface(familySource).TryGetGlyphTypeface(out var g) ? g : null; }
            catch { gt = null; }
            _gtCache[familySource] = gt;
        }
        return gt;
    }

    static string? FindCoveringFamily(int cp)
    {
        foreach (var f in Fonts.SystemFontFamilies)
        {
            if (Covers(GetGt(f.Source), cp)) return f.Source;
        }
        return null;
    }
}
