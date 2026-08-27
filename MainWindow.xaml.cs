using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FontScope;

// 注意：FaceInfo 里 FilePath/SubFamily 等是 public 字段，WPF 绑定不支持字段，
// 列表列一律经由本 record 的属性中转
public record ResultRow(FaceInfo Face, string QueryText)
{
    // 占坑字体（有码位但字形空白），查询时探测一次，排序沉底
    public bool IsBlank { get; init; }
    // 家族名 + 样式（Regular 时省略样式后缀）：如「思源黑体 CN Bold」/「微软雅黑」
    public string NameDisplay => Face.StyleDisplay is "Regular" or ""
        ? Face.DisplayName
        : Face.DisplayName + " " + Face.StyleDisplay;
    public string SubFamilyDisplay => Face.SubFamily;
    public string FilePathDisplay => Face.FilePath;
    public string FormatDisplay => Face.FormatDisplay;

    // 可选列的单元格数据
    public string StyleOnly => Face.StyleDisplay;
    public string WeightDisplay => Face.WeightClass.ToString();
    public string SourceOnly => Face.SourceDisplay;
    public string PsNameDisplay => Face.Name(6);

    // 二次过滤匹配串：家族中英文名、全名、文件名（大小写不敏感包含）
    public string SearchKey => string.Join("\n", new[]
    {
        Face.FamilyZh, Face.FamilyEn, Face.FamilyEnLegacy,
        Face.FullNameZh, Face.FullNameEn, Face.FileName
    });

    static string Line(string k, string v) => k + "：" + (v.Length > 0 ? v : "—");

    // 右侧信息区分组内容（空组自动省略）
    public IReadOnlyList<InfoSection> InfoSections
    {
        get
        {
            var f = Face;
            var basic = new List<string>
            {
                Line("家族名", f.DisplayName),
                Line("样式", f.StyleDisplay + "（weight " + f.WeightClass + "）"),
                Line("全名", f.FullNameZh.Length > 0 ? f.FullNameZh : f.FullNameEn),
                Line("PostScript 名", f.Name(6)),
                Line("版本", f.Name(5)),
                Line("唯一 ID", f.Name(3)),
            };
            var fmt = new List<string>
            {
                Line("格式", f.FormatDisplay),
                Line("轮廓表", f.Outline),
                Line("unitsPerEm", f.UnitsPerEm.ToString()),
                Line("字重类", f.WeightClass.ToString()),
                Line("宽度类", f.WidthClass.ToString()),
                Line("嵌入许可", f.FsTypeDisplay),
                Line("彩色字形", f.IsColorFont ? "是（COLR/CBDT/sbix/SVG 任一）" : "否"),
            };
            var legal = new List<string>();
            foreach (var (label, id) in new (string Label, ushort Id)[]
                { ("版权", 0), ("商标", 7), ("厂商", 8), ("设计师", 9),
                  ("描述", 10), ("厂商网址", 11), ("设计师网址", 12), ("许可证", 13), ("许可证网址", 14) })
            {
                var v = f.Name(id);
                if (v.Length > 0) legal.Add(Line(label, v));
            }
            var file = new List<string>
            {
                Line("文件名", f.FileName),
                Line("路径", f.FilePath),
                Line("TTC 索引", f.IsCollection ? f.FaceIndex.ToString() : "—（单字体文件）"),
                Line("覆盖码点数", f.CodePoints.Count.ToString()),
                Line("来源", f.SourceDisplay),
            };

            var secs = new List<InfoSection>();
            void Add(string title, List<string> lines)
            {
                if (lines.Count > 0) secs.Add(new InfoSection(title, lines));
            }
            Add("基本", basic);
            Add("格式与度量", fmt);
            Add("版权与许可", legal);
            Add("文件", file);
            return secs;
        }
    }
}

// 信息区分组标题 + 行文本集合
public record InfoSection(string Title, IReadOnlyList<string> Lines);

public partial class MainWindow : Window
{
    readonly FontScanner _scanner = new();
    // 扫描范围面板的行：系统/用户固定项 + 自定义文件夹（Enabled 与勾选框双向绑定）
    readonly ObservableCollection<SourceItem> _scopeItems = new();
    SourceItem? _sysItem, _usrItem;
    bool _scanning;
    bool _querying;
    List<ResultRow> _allRows = new();   // 当前查询的完整结果；过滤栏只做二次筛选
    bool _paneVisible = true;

    // 列头点击排序状态：null = 默认序（墨迹分组 → 名称 → 字重）
    string? _sortHeader;
    bool _sortDesc;

    // 程序产生的所有内容（settings/error.log）都放在 exe 所在目录
    static string SettingsDir => AppDomain.CurrentDomain.BaseDirectory;
    static string SettingsJsonFile => Path.Combine(SettingsDir, "settings.json");
    static string LegacyFoldersFile => Path.Combine(SettingsDir, "folders.txt"); // 旧版独立配置，仅做一次性迁移
    AppSettings _settings = new();

    public MainWindow()
    {
        InitializeComponent();
        _inputBaseFont = InputBox.FontFamily.Source; // 回退链的基底，之后只在此基础上追加
        LoadSettings();
        SampleGlyph.Text = SampleBox.Text; // 示例文本预览初始同步（此后回车才刷新）
        ApplyPane();
        RebuildColumns();
        BuildColumnMenu();
        AttachRowMenu();
        BuildScopeItems();
        QueryButton.IsEnabled = false;
        Closing += (_, _) => SaveSettings();
        // 列头点击排序（列是动态组装的，统一走路由事件）
        ResultList.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(Header_Click));
        Loaded += (_, _) => StartScan();
    }

    // ---------- 列配置：可隐藏、可增列，右键列表切换 ----------

    // 全部可用列（Header 同时是 settings.json 列宽/隐藏状态的键）
    static readonly (string Header, double Width, string TemplateKey)[] AllColumns =
    {
        ("预览", 170, "TplPreview"),
        ("字体", 190, "TplFont"),
        ("样式", 90, "TplStyle"),
        ("字重", 60, "TplWeight"),
        ("格式", 120, "TplFormat"),
        ("PostScript 名", 150, "TplPsName"),
        ("来源", 52, "TplSource"),
        ("文件", 420, "TplFile"),
    };

    // 按设置组装可见列；列宽记忆按列头名匹配
    void RebuildColumns()
    {
        var gv = ResultGrid;
        gv.Columns.Clear();
        foreach (var (header, width, key) in AllColumns)
        {
            if (_settings.HiddenColumns.Contains(header)) continue;
            var col = new GridViewColumn
            {
                Header = header,
                CellTemplate = (DataTemplate)FindResource(key)
            };
            col.Width = _settings.ColWidths.TryGetValue(header, out var w) && w > 8 ? w : width;
            gv.Columns.Add(col);
        }
        UpdateHeaderTemplates();
    }

    // 列头排序指示：Header 本体仍是纯文本键（列宽持久化依赖），箭头画在 HeaderTemplate 里
    void UpdateHeaderTemplates()
    {
        foreach (var col in ResultGrid.Columns)
        {
            var h = (string)col.Header;
            var text = _sortHeader == h ? h + (_sortDesc ? " ▼" : " ▲") : h;
            var tb = new FrameworkElementFactory(typeof(TextBlock));
            tb.SetValue(TextBlock.TextProperty, text);
            tb.SetValue(TextBlock.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Left);
            col.HeaderTemplate = new DataTemplate { VisualTree = tb };
        }
    }

    void BuildColumnMenu()
    {
        var menu = new ContextMenu();
        foreach (var spec in AllColumns)
        {
            var mi = new MenuItem { Header = spec.Header, IsCheckable = true };
            mi.IsChecked = !_settings.HiddenColumns.Contains(spec.Header);
            mi.Click += ColumnToggle_Click;
            menu.Items.Add(mi);
        }
        ResultList.ContextMenu = menu;
    }

    void ColumnToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Header: string header }) return;
        if (!_settings.HiddenColumns.Remove(header))
            _settings.HiddenColumns.Add(header);
        RebuildColumns();
        BuildColumnMenu(); // 刷新勾选态
        SaveSettings();
    }

    // ---------- 条目右键菜单：复制名称 / 复制路径 / 系统属性页 ----------

    // 菜单挂在每个 ListViewItem 上：WPF ContextMenu 内层优先命中，
    // 条目上右键弹本菜单；空白区与列头仍回落到 ListView 的列开关菜单。
    // 全部条目共用同一实例，打开时 PlacementTarget 自动指向被点的行。
    void AttachRowMenu()
    {
        var menu = new ContextMenu();
        foreach (var header in new[] { "复制名称", "复制路径", "打开属性页面" })
        {
            var mi = new MenuItem { Header = header };
            mi.Click += RowMenu_Click;
            menu.Items.Add(mi);
        }
        ResultList.ItemContainerStyle.Setters.Add(
            new Setter(FrameworkElement.ContextMenuProperty, menu));

        // 右键未选中的条目时先选中它，保证右侧详情与菜单操作指向同一行
        ResultList.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d
                && ItemsControl.ContainerFromElement(ResultList, d) is System.Windows.Controls.ListViewItem lvi)
            {
                lvi.IsSelected = true;
                lvi.Focus();
            }
        };
    }

    void RowMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Parent: ContextMenu { PlacementTarget: System.Windows.Controls.ListViewItem lvi } }
            || lvi.DataContext is not ResultRow row) return;
        switch ((string)((MenuItem)sender).Header)
        {
            case "复制名称": TrySetClipboard(row.NameDisplay); break;
            case "复制路径": TrySetClipboard(row.Face.FilePath); break;
            case "打开属性页面": OpenProperties(row.Face.FilePath, row.Face.DisplayName); break;
        }
    }

    static void TrySetClipboard(string text)
    {
        try { System.Windows.Clipboard.SetText(text); }
        catch (Exception ex) { App.Log(ex); } // 剪贴板被其他进程占用时记录后忽略
    }

    // ---------- 条目右键：属性页隔离调用 ----------

    // shell 互操作已迁至 ShellProps（见 ShellProps.cs）。
    // 上下文菜单处理器会加载进调用进程内执行，本机 fontext 已损坏会连带主程序崩溃（coreclr AV），
    // 因此隔离到 --showprops 子进程：崩也只崩辅助进程。
    // 异步等待辅助进程（同步 WaitForExit 会阻塞 UI 线程直到辅助进程退出）
    async void OpenProperties(string filePath, string displayNameHint = "")
    {
        if (!File.Exists(filePath)) return;
        try
        {
            // 快路径：shell 属性 API（不加载菜单处理器，失败也只返回 false）
            if (ShellProps.TryObjectProperties(filePath)) return;

            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) throw new InvalidOperationException("无法定位自身可执行文件");
            var psi = new ProcessStartInfo(exe)
            {
                Arguments = "--showprops \"" + filePath + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            // 不 using：属性对话框显示成功后辅助进程会存活（兜底定时器），提前 Dispose 会打断下面的异步等待
            var p = Process.Start(psi);
            if (p == null) throw new InvalidOperationException("辅助进程启动失败");
            StatusText.Text = "正在打开属性页…";
            // 复位不能依赖辅助进程退出（成功后它仍存活一段时间）。
            // 与其生命周期解耦：短暂等待，仍在运行即视为属性页已打开，复位状态栏；
            // 仅当辅助进程提前以非零码退出（RunStandalone 失败路径 Shutdown(1)）才报失败。
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            Task wait = p.WaitForExitAsync(cts.Token);
            if (await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(4))) == wait
                && p.HasExited && p.ExitCode != 0)
            {
                StatusText.Text = "无法打开属性页（shell 未提供该条目的属性扩展）";
                return;
            }
            if (StatusText.Text == "正在打开属性页…") StatusText.Text = "就绪";
        }
        catch (Exception ex)
        {
            App.Log(ex);
            StatusText.Text = "打开属性页失败：" + ex.Message;
        }
    }

    // ---------- 关于 ----------
    // 版本号单一来源：csproj 的 <Version>（编译进程序集），这里只读不写。
    // 自绘小窗口而非 MessageBox：需要可点击的蓝色「赞助」链接（MessageBox 不支持超链接）。
    void About_Click(object sender, RoutedEventArgs e)
    {
        var v = GetType().Assembly.GetName().Version;
        var win = new Window
        {
            Title = "关于 FontScope",
            Width = 480,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            Background = System.Windows.Media.Brushes.White,
        };
        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };

        panel.Children.Add(new TextBlock
        {
            Text = "FontScope",
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 4),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "版本 v" + FormatVersion(v ?? new Version(0, 0)),
            FontSize = 12,
            Foreground = System.Windows.Media.Brushes.Gray,
            Margin = new Thickness(0, 0, 0, 10),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "【功能】输入字符反查本机字体：扫描系统、用户及自定义目录中的全部字体，"
                 + "找出覆盖目标字符的字体并提供字形预览，自动识别画出空白的占坑字体；"
                 + "支持 TTC 集合解析与多维度排序，"
                 + "可复制字体名称与文件路径、查看字体属性页、在资源管理器中定位文件。",
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            Margin = new Thickness(0, 0, 0, 12),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "作者：mono、铁圈",
            Margin = new Thickness(0, 0, 0, 8),
        });

        // 蓝色超链接行：系统浏览器打开目标网址
        static TextBlock LinkLine(string label, string text, string url, Thickness margin)
        {
            var line = new TextBlock { Margin = margin };
            if (label.Length > 0)
                line.Inlines.Add(new System.Windows.Documents.Run(label));
            var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(text))
            {
                Foreground = System.Windows.Media.Brushes.DodgerBlue,
                NavigateUri = new Uri(url),
            };
            link.RequestNavigate += (_, ev) =>
            {
                try { Process.Start(new ProcessStartInfo(ev.Uri.AbsoluteUri) { UseShellExecute = true }); }
                catch (Exception ex) { App.Log(ex); }
            };
            line.Inlines.Add(link);
            return line;
        }

        panel.Children.Add(LinkLine("开源地址：", "https://github.com/dsqm/FontScope",
            "https://github.com/dsqm/FontScope", new Thickness(0, 0, 0, 8)));
        var sponsor = LinkLine("", "赞助", "https://docs.qq.com/aio/DRWtMY3FQS0ZHRGRG", new Thickness(0, 4, 0, 0));
        sponsor.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        sponsor.FontSize = 16;
        panel.Children.Add(sponsor);

        win.Content = panel;
        win.ShowDialog();
    }

    // 0.1.0.0 → "0.1"；带补丁/修订号才追加显示（0.1.2 → "0.1.2"，0.1.2.3 → "0.1.2.3"）
    static string FormatVersion(Version v) =>
        v.Revision == 0 && v.Build == 0 ? $"{v.Major}.{v.Minor}"
        : v.Revision == 0 ? $"{v.Major}.{v.Minor}.{v.Build}"
        : v.ToString();

    // ---------- 扫描 ----------

    async void StartScan(bool force = false)
    {
        if (_scanning) return;
        _scanning = true;
        QueryButton.IsEnabled = false;
        RescanButton.IsEnabled = false;
        ScopeToggle.IsEnabled = false; // 扫描期间禁用范围下拉，避免并发重扫
        StatusText.Text = "正在扫描字体…";

        var progress = new Progress<(int done, int total)>(p =>
            StatusText.Text = $"正在扫描字体… {p.done}/{p.total}");

        try
        {
            await _scanner.ScanAsync(EnabledSources(), force, progress);
        }
        catch (Exception ex)
        {
            StatusText.Text = "扫描失败：" + ex.Message;
            App.Log(ex);
        }

        _scanning = false;
        ScopeToggle.IsEnabled = true;
        RescanButton.IsEnabled = true;
        AfterFacesChanged();
    }

    // Faces 集合变化后的共同收尾：状态栏计数、空态处理、按当前查询词刷新结果。
    // 增量缓存使 FaceInfo 实例跨切换存活（占坑探测缓存延续），重查几乎瞬时。
    void AfterFacesChanged()
    {
        if (_scanner.Faces.Count == 0)
        {
            _allRows = new();
            ApplyFilter();
            QueryButton.IsEnabled = false;
            StatusText.Text = "未启用任何扫描来源——请在「扫描范围」中勾选";
            return;
        }

        var sys = _scanner.Faces.Count(f => f.Source == FontSource.System);
        var usr = _scanner.Faces.Count(f => f.Source == FontSource.User);
        var cus = _scanner.Faces.Count(f => f.Source == FontSource.Custom);
        var fail = _scanner.FailedFiles > 0 ? $"（{_scanner.FailedFiles} 个文件无法解析）" : "";
        var excluded = _scopeItems.Count - EnabledItemCount();
        var excl = excluded > 0 ? $"｜已排除 {excluded} 个来源" : "";
        StatusText.Text = $"就绪 · 共 {_scanner.Faces.Count} 个字体 face（系统 {sys} · 用户 {usr} · 自定义 {cus}）{fail}{excl}";

        QueryButton.IsEnabled = InputBox.Text.Length > 0 && !_querying;
        if (InputBox.Text.Length > 0) DoQuery();
        else { _allRows = new(); ApplyFilter(); } // 无查询词时清掉旧结果
    }

    // 当前启用的扫描来源（顺序即优先级：系统 → 用户 → 自定义）
    IEnumerable<(string Dir, FontSource Src)> EnabledSources()
    {
        if (_sysItem?.Enabled == true) yield return (FontScanner.SystemFontDir, FontSource.System);
        if (_usrItem?.Enabled == true) yield return (FontScanner.UserFontDir, FontSource.User);
        foreach (var it in _scopeItems)
            if (it.FolderPath != null && it.Enabled) yield return (it.FolderPath, FontSource.Custom);
    }

    int EnabledItemCount() => _scopeItems.Count(i => i.Enabled);

    void UpdateScopeHeader() =>
        ScopeToggleText.Text = $"扫描范围：{EnabledItemCount()} / {_scopeItems.Count} 个来源";

    void Rescan_Click(object sender, RoutedEventArgs e) => StartScan();

    // ---------- 查询 ----------

    void Query_Click(object sender, RoutedEventArgs e) => DoQuery();

    void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_scanning) QueryButton.IsEnabled = InputBox.Text.Length > 0;
        ApplyInputFontFallback();
    }

    // 示例文本：回车才刷新下方预览（避免每击键都重渲染字形）
    void SampleBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        SampleGlyph.Text = SampleBox.Text;
    }

    // ---------- 输入框系统字体回退：WPF 缺字只查复合字体表，不扫系统字体，
    // 生僻码位（如「𰻝」U+30EDD）会显示方框。对画不了的码点找到能画的
    // 已安装族，追加到本框的 FontFamily 回退链上。只作用于输入框。 ----------
    string _inputBaseFont = "";
    string? _appliedFontChain;

    void ApplyInputFontFallback()
    {
        try
        {
            var chain = SystemFontFallback.BuildChain(_inputBaseFont, InputBox.Text);
            if (chain == _appliedFontChain) return;
            _appliedFontChain = chain;
            InputBox.FontFamily = new System.Windows.Media.FontFamily(chain);
        }
        catch (Exception ex) { App.Log(ex); }
    }

    internal async void DoQuery()
    {
        if (_scanning || _querying || InputBox.Text.Length == 0) return;
        var cps = GlyphPreviewHelper.CodePointsOf(InputBox.Text).ToList();
        var text = InputBox.Text;

        var matched = _scanner.Faces.Where(f => f.Covers(cps)).ToList();

        // 占坑探测：字形度量很快，但上万字体时加载字体文件本身耗时，大结果集并行跑
        if (matched.Count > 500)
        {
            _querying = true;
            QueryButton.IsEnabled = false;
            StatusText.Text = $"命中 {matched.Count} 个 face，正在分析字形…";
            try
            {
                await Task.Run(() => Parallel.ForEach(matched, f => f.RendersInk(text)));
            }
            catch (Exception ex) { App.Log(ex); } // 探测失败按现状继续，_querying 必须复位
            finally
            {
                _querying = false;
                QueryButton.IsEnabled = InputBox.Text.Length > 0;
            }
        }
        else
        {
            foreach (var f in matched) f.RendersInk(text); // 小结果集直接填缓存
        }

        // 默认排序逻辑在 ApplyFilter 统一执行
        _allRows = matched.Select(f => new ResultRow(f, text)
        {
            IsBlank = !f.RendersInk(text)
        }).ToList();
        ApplyFilter();
    }

    // ---------- 二次过滤 ----------

    void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    // 按家族中英文名/全名/文件名对当前查询结果做包含匹配（大小写不敏感）+ 统一排序
    void ApplyFilter()
    {
        if (ResultList == null) return;
        var kw = FilterBox.Text.Trim();
        var rows = kw.Length == 0 ? _allRows
            : _allRows.Where(r => r.SearchKey.Contains(kw, StringComparison.OrdinalIgnoreCase)).ToList();

        // 一级分组：占坑字体沉底；二级：列头点击的排序（默认名称+字重）
        var ordered = rows.OrderBy(r => r.IsBlank ? 1 : 0);
        if (_sortHeader is string h && h != "预览")
        {
            ordered = _sortDesc ? ThenByKey(ordered, h, desc: true) : ThenByKey(ordered, h, desc: false);
        }
        else
        {
            ordered = _sortDesc
                ? ordered.ThenByDescending(r => r.NameDisplay, StringComparer.CurrentCulture)
                         .ThenByDescending(r => r.Face.WeightClass)
                : ordered.ThenBy(r => r.NameDisplay, StringComparer.CurrentCulture)
                         .ThenBy(r => r.Face.WeightClass);
        }

        ResultList.ItemsSource = ordered.ToList();
        EmptyHint.Text = _allRows.Count == 0 ? "没有字体支持这些字符" : "没有匹配过滤条件的字体";
        EmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = $"命中 {_allRows.Count} / {_scanner.Faces.Count} 个 face"
            + (kw.Length > 0 ? $"（过滤后 {rows.Count}）" : "")
            + (_sortHeader is string sh && sh != "预览" ? $"（按「{sh}」{(_sortDesc ? "降" : "升")}序）" : "");

        if (rows.Count > 0) ResultList.SelectedIndex = 0;
    }

    // 各排序键：字重按数值，其余按字符串
    static IOrderedEnumerable<ResultRow> ThenByKey(IOrderedEnumerable<ResultRow> src, string header, bool desc) =>
        header switch
        {
            "字体" => desc ? src.ThenByDescending(r => r.NameDisplay, StringComparer.CurrentCulture)
                           : src.ThenBy(r => r.NameDisplay, StringComparer.CurrentCulture),
            "样式" => desc ? src.ThenByDescending(r => r.StyleOnly) : src.ThenBy(r => r.StyleOnly),
            "字重" => desc ? src.ThenByDescending(r => r.Face.WeightClass) : src.ThenBy(r => r.Face.WeightClass),
            "格式" => desc ? src.ThenByDescending(r => r.FormatDisplay) : src.ThenBy(r => r.FormatDisplay),
            "PostScript 名" => desc ? src.ThenByDescending(r => r.PsNameDisplay) : src.ThenBy(r => r.PsNameDisplay),
            "来源" => desc ? src.ThenByDescending(r => r.SourceOnly) : src.ThenBy(r => r.SourceOnly),
            "文件" => desc ? src.ThenByDescending(r => r.FilePathDisplay) : src.ThenBy(r => r.FilePathDisplay),
            _ => desc ? src.ThenByDescending(r => r.Face.WeightClass) : src.ThenBy(r => r.Face.WeightClass),
        };

    // 列头点击循环：升序 → 降序 → 恢复默认；点「预览」直接回默认序
    void Header_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: { } col }) return;
        var h = col.Header as string;
        if (string.IsNullOrEmpty(h) || h == "预览")
        {
            _sortHeader = null;
            _sortDesc = false;
        }
        else if (_sortHeader == h && _sortDesc)
        {
            _sortHeader = null; // 第三次点击回到默认
            _sortDesc = false;
        }
        else if (_sortHeader == h)
        {
            _sortDesc = true;
        }
        else
        {
            _sortHeader = h;
            _sortDesc = false;
        }
        UpdateHeaderTemplates();
        ApplyFilter();
    }

    // ---------- 预览窗格开关 ----------

    // WPF 原生不响应 Shift+滚轮手势，手动翻译为横向滚动（上滚=向左）。
    // 预览区：ScrollViewer 本体即事件源
    void PreviewPane_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0) return;
        var sv = (ScrollViewer)sender;
        sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    // 主列表：横向 ScrollViewer 在 ListView 内部可视树，缓存后直接驱动
    ScrollViewer? _listScroll;

    void ResultList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0) return;
        if (_listScroll == null)
        {
            _listScroll = FindDescendant<ScrollViewer>(ResultList);
            if (_listScroll == null) return;
        }
        _listScroll.ScrollToHorizontalOffset(_listScroll.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T hit) return hit;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var r = FindDescendant<T>(VisualTreeHelper.GetChild(root, i));
            if (r != null) return r;
        }
        return null;
    }

    void TogglePane_Click(object sender, RoutedEventArgs e)
    {
        _paneVisible = !_paneVisible;
        ApplyPane();
        SaveSettings(); // 关键变更即存
    }

    void ApplyPane()
    {
        RightColumn.MinWidth = _paneVisible ? 340 : 0;
        RightColumn.Width = _paneVisible ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
        PaneSplitter.Visibility = PreviewPane.Visibility =
            _paneVisible ? Visibility.Visible : Visibility.Collapsed;
        TogglePaneButton.Content = _paneVisible ? "隐藏预览" : "显示预览";
    }

    // ---------- 记忆配置（exe 目录 settings.json，不写 C 盘用户目录） ----------

    void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsJsonFile)) return;
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsJsonFile));
            if (s == null) return;
            _settings = s;
            _paneVisible = s.PaneVisible;

            // 还原窗口尺寸与位置；位置需落在虚拟屏幕内，否则保持居中
            if (!double.IsNaN(s.WinLeft) && !double.IsNaN(s.WinTop) && s.WinW >= 400 && s.WinH >= 300)
            {
                double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
                double vr = vl + SystemParameters.VirtualScreenWidth, vb = vt + SystemParameters.VirtualScreenHeight;
                if (s.WinLeft >= vl && s.WinTop >= vt && s.WinLeft < vr - 120 && s.WinTop < vb - 80)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Left = s.WinLeft; Top = s.WinTop; Width = s.WinW; Height = s.WinH;
                }
            }
            if (s.Maximized) WindowState = WindowState.Maximized;
            if (s.SampleText.Length > 0) SampleBox.Text = s.SampleText;
        }
        catch { } // 配置损坏时按默认值运行

        // 旧版 folders.txt 一次性迁移：settings.json 没有文件夹记录时读入
        if (_settings.CustomFolders.Count == 0)
        {
            try
            {
                if (File.Exists(LegacyFoldersFile))
                    foreach (var line in File.ReadAllLines(LegacyFoldersFile))
                        if (line.Length > 0 && _settings.CustomFolders.All(f => f.Path != line))
                            _settings.CustomFolders.Add(new AppSettings.FolderSetting { Path = line });
            }
            catch { }
        }
    }

    // 退出时 + 预览窗格切换时调用
    void SaveSettings()
    {
        try
        {
            var s = _settings;
            s.PaneVisible = _paneVisible;
            s.Maximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal)
            {
                s.WinLeft = Left; s.WinTop = Top;
                s.WinW = ActualWidth; s.WinH = ActualHeight;
            }
            s.SampleText = SampleBox.Text;
            s.SystemFontsEnabled = _sysItem?.Enabled ?? true;
            s.UserFontsEnabled = _usrItem?.Enabled ?? true;
            s.CustomFolders = _scopeItems.Where(x => x.FolderPath != null)
                .Select(x => new AppSettings.FolderSetting { Path = x.FolderPath!, Enabled = x.Enabled })
                .ToList();
            s.ColWidths.Clear();
            foreach (var c in ResultGrid.Columns)
                s.ColWidths[(string)c.Header] = c.Width;

            File.WriteAllText(SettingsJsonFile,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { App.Log(ex); }
    }

    // ---------- 详情区交互 ----------

    // 双击列表文件路径：打开所在目录并选中该字体文件（与详情区路径行为一致）
    void FilePathCell_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (((FrameworkElement)sender).DataContext is ResultRow row)
            SelectInExplorer(row.Face.FilePath);
    }

    // 点击路径：打开所在目录并选中该字体文件
    void PathLabel_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ResultRow row })
            SelectInExplorer(row.Face.FilePath);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr ILCreateFromPath(string pszPath);

    [DllImport("shell32.dll")]
    static extern void ILFree(IntPtr pidl);

    [DllImport("shell32.dll")]
    static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, IntPtr apidl, uint dwFlags);

    // shell 官方 API：打开父目录并选中文件（资源管理器内部同款，非硬编码 explorer）
    static void SelectInExplorer(string filePath)
    {
        if (!File.Exists(filePath)) return;
        try
        {
            var pidl = ILCreateFromPath(filePath);
            if (pidl != IntPtr.Zero)
            {
                SHOpenFolderAndSelectItems(pidl, 0, IntPtr.Zero, 0);
                ILFree(pidl);
                return;
            }
        }
        catch (Exception ex) { App.Log(ex); }
        // 兜底：API 不可用时退回仅打开目录
        OpenInShell(Path.GetDirectoryName(filePath));
    }

    static void OpenInShell(string? path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
        try
        {
            var p = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "open" });
            p?.Dispose();
        }
        catch (Exception ex) { App.Log(ex); }
    }

    // ---------- 自定义文件夹 ----------

    void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择要扫描的字体文件夹",
            UseDescriptionForTitle = true
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        var path = dlg.SelectedPath;
        if (path.Length > 0 && _scopeItems.All(x => x.FolderPath != path))
        {
            _scopeItems.Add(new SourceItem(path, "", enabled: true, removable: true, folder: path));
            UpdateScopeHeader();
            SaveSettings(); // 关键变更即存
            StartScan();    // 解析新目录（其余来源走缓存，瞬时）
        }
    }

    // ---------- 扫描范围下拉面板 ----------

    // 由配置构建范围面板行：系统、用户两行固定项 + 自定义文件夹
    void BuildScopeItems()
    {
        _sysItem = new SourceItem("系统字体", FontScanner.SystemFontDir, _settings.SystemFontsEnabled, removable: false);
        _usrItem = new SourceItem("用户字体", FontScanner.UserFontDir, _settings.UserFontsEnabled, removable: false);
        _scopeItems.Add(_sysItem);
        _scopeItems.Add(_usrItem);
        foreach (var f in _settings.CustomFolders)
            if (f.Path.Length > 0 && _scopeItems.All(x => x.FolderPath != f.Path))
                _scopeItems.Add(new SourceItem(f.Path, "", f.Enabled, removable: true, folder: f.Path));
        ScopeList.ItemsSource = _scopeItems;
        UpdateScopeHeader();
    }

    void ScopeToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_scanning) { ScopeToggle.IsChecked = false; return; }
        ScopePopup.IsOpen = !ScopePopup.IsOpen;
    }

    void ScopePopup_Closed(object sender, EventArgs e) => ScopeToggle.IsChecked = false;

    // 勾选切换：启用可能需解析新来源 → 异步扫描（已缓存则毫秒级）；
    // 停用只需重新拼装。两种都立即写盘。
    void ScopeItem_Changed(object sender, RoutedEventArgs e)
    {
        if (_scanning || ((FrameworkElement)sender).DataContext is not SourceItem item) return;
        UpdateScopeHeader();
        SaveSettings();
        if (item.Enabled) StartScan();
        else
        {
            _scanner.Reassemble(EnabledSources());
            AfterFacesChanged();
        }
    }

    void RemoveRow_Click(object sender, RoutedEventArgs e)
    {
        if (_scanning || ((FrameworkElement)sender).DataContext is not SourceItem item
            || item.FolderPath == null) return;
        _scopeItems.Remove(item);
        UpdateScopeHeader();
        SaveSettings();
        _scanner.Reassemble(EnabledSources()); // 缓存仍在，仅拼装剔除该目录
        AfterFacesChanged();
    }
}

// 记忆配置：窗口尺寸位置、列宽、预览窗格开关、示例文本
public sealed class AppSettings
{
    public double WinW { get; set; } = 1280;
    public double WinH { get; set; } = 800;
    public double WinLeft { get; set; } = double.NaN; // NaN = 首次启动保持居中
    public double WinTop { get; set; } = double.NaN;
    public bool Maximized { get; set; }
    public bool PaneVisible { get; set; } = true;
    public string SampleText { get; set; } = "";

    // 扫描范围：系统/用户来源开关 + 自定义文件夹（含各自的启用位）
    public bool SystemFontsEnabled { get; set; } = true;
    public bool UserFontsEnabled { get; set; } = true;

    [JsonConverter(typeof(FolderListConverter))]
    public List<FolderSetting> CustomFolders { get; set; } = new(); // 兼容旧版纯字符串数组

    public Dictionary<string, double> ColWidths { get; set; } = new(); // 列头名 -> 宽度

    // 隐藏的列（列头名）。默认隐藏三个附加列，主列表保持简洁
    public List<string> HiddenColumns { get; set; } = new() { "样式", "字重", "PostScript 名" };

    public sealed class FolderSetting
    {
        public string Path { get; set; } = "";
        public bool Enabled { get; set; } = true;
    }

    // 旧版 settings.json 的 CustomFolders 是纯字符串数组，新版是对象数组——
    // 读取时两种都认（旧条目默认启用），写入一律新格式。
    sealed class FolderListConverter : JsonConverter<List<FolderSetting>>
    {
        public override List<FolderSetting> Read(ref Utf8JsonReader reader, Type typeToConvert,
            JsonSerializerOptions options)
        {
            var list = new List<FolderSetting>();
            if (reader.TokenType != JsonTokenType.StartArray) return list;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.String:
                        var p = reader.GetString();
                        if (!string.IsNullOrEmpty(p)) list.Add(new FolderSetting { Path = p });
                        break;
                    case JsonTokenType.StartObject:
                        string path = "";
                        bool en = true;
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                        {
                            if (reader.TokenType != JsonTokenType.PropertyName) continue;
                            var name = reader.GetString();
                            reader.Read();
                            if (name == nameof(FolderSetting.Path)) path = reader.GetString() ?? "";
                            else if (name == nameof(FolderSetting.Enabled)) en = reader.GetBoolean();
                            // 未知属性：reader 已停在值 token 上，下一轮 Read 自然跳过
                        }
                        if (path.Length > 0) list.Add(new FolderSetting { Path = path, Enabled = en });
                        break;
                }
            }
            return list;
        }

        public override void Write(Utf8JsonWriter writer, List<FolderSetting> value,
            JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, value, new JsonSerializerOptions());
        }
    }
}

// 扫描范围面板的一行：固定来源（系统/用户）或可移除的自定义文件夹
public sealed class SourceItem : System.ComponentModel.INotifyPropertyChanged
{
    bool _enabled;

    public string Display { get; }      // 勾选框旁主文本
    public string Detail { get; }       // 右侧灰色说明（路径）
    public bool Removable { get; }      // 是否显示「移除」按钮
    public string? FolderPath { get; }  // 非空 = 自定义文件夹行

    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Enabled))); }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public SourceItem(string display, string detail, bool enabled, bool removable,
        string? folder = null)
    {
        Display = display; Detail = detail; _enabled = enabled;
        Removable = removable; FolderPath = folder;
    }
}

// 供 code-behind 使用的码点枚举（与渲染组件内部逻辑一致）
static class GlyphPreviewHelper
{
    public static IEnumerable<uint> CodePointsOf(string s)
    {
        for (int i = 0; i < s.Length; )
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                yield return (uint)char.ConvertToUtf32(s[i], s[i + 1]);
                i += 2;
            }
            else { yield return s[i]; i++; }
        }
    }
}
