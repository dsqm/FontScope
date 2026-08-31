using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
// 项目同时启用 WinForms，Brush/Brushes/Color/Path 与 System.Drawing、System.IO 同名冲突。
// using 别名优先级高于命名空间导入，故下面三个别名可精确收敛到 WPF 版本；文件 IO 另取 IoPath。
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using IoPath = System.IO.Path;

namespace FontScope;

// 单个目录的统计。递归扫描时 Dir 就是字体文件实际所在的那个子目录（而非扫描根）。
public sealed record FolderStat(string Dir, int FaceCount, int FileCount, int BlankCount)
{
    // 文件数与 face 数相同（无 TTC 展开）时省略，避免冗余
    public string FileText => FileCount == FaceCount
        ? "" : string.Format(CultureInfo.CurrentCulture, "（{0:N0} 个文件）", FileCount);
    public string BlankText => BlankCount == 0
        ? "" : string.Format(CultureInfo.CurrentCulture, "（占坑 {0:N0}）", BlankCount);
    public string CountNote => FileText + BlankText;
}

public sealed record SourceStat(string Name, int FaceCount, List<FolderStat> Folders)
{
    public Brush BarBrush { get; init; } = Brushes.Gray;
    public double Percent { get; init; }
    public string PercentText => Percent.ToString("0.0", CultureInfo.InvariantCulture) + "%";
}

public sealed record FormatStat(string Name, int Count);

// 统计快照：查询完成瞬间算好，之后不再变化（窗口是模态的，期间不可能改查询）
public sealed class StatsModel
{
    public string QueryText { get; init; } = "";
    public int TotalFaces { get; init; }
    public int TotalFiles { get; init; }
    public int BlankCount { get; init; }
    public List<SourceStat> Sources { get; init; } = new();
    public List<FormatStat> Formats { get; init; } = new();

    public string QueryTextDisplay => "查询字符：" + QueryText;
    public string TotalFilesText => string.Format(CultureInfo.CurrentCulture, "（{0:N0} 个文件）", TotalFiles);

    /// <summary>按「来源 → 目录」两级聚合；组内目录按 face 数降序，同数按路径排。</summary>
    public static StatsModel Build(IReadOnlyList<ResultRow> rows, string queryText)
    {
        int total = rows.Count;
        var sources = new List<SourceStat>();

        // 固定顺序：系统 → 用户 → 自定义（与扫描优先级一致）
        var specs = new (FontSource Src, string Name, Brush Brush)[]
        {
            (FontSource.System, "系统", new SolidColorBrush(Color.FromRgb(0x37, 0x8A, 0xDD))),
            (FontSource.User, "用户", new SolidColorBrush(Color.FromRgb(0x63, 0x99, 0x22))),
            (FontSource.Custom, "自定义", new SolidColorBrush(Color.FromRgb(0x7F, 0x77, 0xDD))),
        };

        foreach (var (src, name, brush) in specs)
        {
            var group = rows.Where(r => r.Face.Source == src).ToList();
            if (group.Count == 0) continue;

            var folders = group
                .GroupBy(r => IoPath.GetDirectoryName(r.Face.FilePath) ?? "", StringComparer.OrdinalIgnoreCase)
                .Select(g => new FolderStat(
                    g.Key,
                    g.Count(),                              // face 数（TTC 按面展开）
                    g.Select(x => x.Face.FilePath)          // 文件数（同文件多面只算一次）
                     .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    g.Count(x => x.IsBlank)))
                .OrderByDescending(f => f.FaceCount)
                .ThenBy(f => f.Dir, StringComparer.OrdinalIgnoreCase)
                .ToList();

            sources.Add(new SourceStat(name, group.Count, folders)
            {
                BarBrush = brush,
                Percent = total == 0 ? 0 : (double)group.Count / total * 100,
            });
        }

        var formats = rows.GroupBy(r => r.Face.FormatDisplay)
            .Select(g => new FormatStat(g.Key, g.Count()))
            .OrderByDescending(f => f.Count)
            .ToList();

        return new StatsModel
        {
            QueryText = queryText,
            TotalFaces = total,
            TotalFiles = rows.Select(r => r.Face.FilePath)
                             .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            BlankCount = rows.Count(r => r.IsBlank),
            Sources = sources,
            Formats = formats,
        };
    }

    /// <summary>纯文本摘要，供「复制统计摘要」使用。</summary>
    public string ToPlainText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("FontScope 查询结果统计");
        sb.AppendLine(QueryTextDisplay);
        sb.AppendLine(string.Format(CultureInfo.CurrentCulture,
            "命中：{0:N0} 个 face（{1:N0} 个文件）", TotalFaces, TotalFiles));
        sb.AppendLine("来源分布："
            + string.Join(" · ", Sources.Select(s =>
                string.Format(CultureInfo.CurrentCulture, "{0} {1:N0}（{2}）", s.Name, s.FaceCount, s.PercentText))));
        sb.AppendLine();
        sb.AppendLine("格式分布：");
        foreach (var f in Formats)
            sb.AppendLine(string.Format(CultureInfo.CurrentCulture, "  {0}\t{1:N0}", f.Name, f.Count));
        sb.AppendLine();
        sb.AppendLine(string.Format(CultureInfo.CurrentCulture, "占坑字体：{0:N0}", BlankCount));
        sb.AppendLine("（占坑 = 有码位但字形为空；彩色字体与解析失败者不计入，可能漏计，以预览为准。）");
        sb.AppendLine();
        foreach (var s in Sources)
        {
            sb.AppendLine(string.Format(CultureInfo.CurrentCulture, "{0}（{1:N0}）：", s.Name, s.FaceCount));
            foreach (var f in s.Folders)
                sb.AppendLine(string.Format(CultureInfo.CurrentCulture,
                    "  {0}\t{1:N0}{2}", f.Dir, f.FaceCount, f.CountNote));
        }
        return sb.ToString();
    }
}

public partial class StatsWindow : Window
{
    readonly StatsModel _model;

    public StatsWindow(StatsModel model)
    {
        InitializeComponent();
        _model = model;
        DataContext = model;
        Loaded += (_, _) => BuildBar();
    }

    // 来源分布条：按各来源占比动态分配 Star 列宽（等比填充，不依赖像素换算）
    void BuildBar()
    {
        BarGrid.ColumnDefinitions.Clear();
        BarGrid.Children.Clear();
        foreach (var s in _model.Sources)
        {
            var share = _model.TotalFaces == 0 ? 0 : (double)s.FaceCount / _model.TotalFaces;
            BarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(share, GridUnitType.Star) });
            var seg = new Border { Background = s.BarBrush }; // 用 Border 代替 Rectangle，避开 System.Drawing 冲突
            Grid.SetColumn(seg, BarGrid.Children.Count);
            BarGrid.Children.Add(seg);
        }
    }

    // 双击目录行 → 资源管理器打开（与主列表双击行为一致：仅双击响应）
    void FolderRow_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (sender is FrameworkElement { DataContext: FolderStat fs })
            MainWindow.OpenInShell(fs.Dir); // 目录而非文件，走 OpenInShell（SelectInExplorer 只认文件）
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { System.Windows.Clipboard.SetText(_model.ToPlainText()); }
        catch (Exception ex) { App.Log(ex); } // 剪贴板被占用时记录后忽略
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
