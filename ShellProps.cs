using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace FontScope;

// 字体文件系统属性页的隔离调用器。
// 背景：部分机器的字体属性扩展（fontext）注册损坏或被防护软件干扰，
// 上下文菜单处理器加载进调用进程内执行，一旦崩溃会带走宿主。
// 因此主程序以 "--showprops <path>" 子进程模式使用本类：辅助进程崩溃不影响主程序。
public static class ShellProps
{
    // 快路径：仅调 shell 属性 API，不加载上下文菜单处理器（进程内安全）
    public static bool TryObjectProperties(string filePath)
    {
        if (!File.Exists(filePath)) return false;
        try { return SHObjectProperties(IntPtr.Zero, SHOP_FILEPATH, filePath, IntPtr.Zero); }
        catch (Exception ex) { App.Log(ex); return false; }
    }

    // 独立进程入口：依次尝试各条路径，成功返回 true（App 以退出码 0/1 回传）。
    // 注意：调用方在返回 true 后必须保持消息泵一段时间——属性对话框由 shell
    // 在本进程内异步显示，进程立即退出会把它一并带走（表现为「无任何反应」）。
    public static bool RunStandalone(string filePath)
    {
        if (filePath.Length == 0 || !File.Exists(filePath))
        {
            App.Log(new Exception("[props] --showprops 参数无效"));
            return false;
        }
        try
        {
            if (SHObjectProperties(IntPtr.Zero, SHOP_FILEPATH, filePath, IntPtr.Zero)) return true;

            // 关键：properties 动词必须带 SEE_MASK_INVOKEIDLIST 才会走条目的
            // IContextMenu 调用（.NET Process.Start 不加此标志，故报 1155 无关联）
            if (TryVerbViaShellExecuteEx(filePath)) return true;
            return TryContextMenuRoutes(IntPtr.Zero, filePath, "");
        }
        catch (Exception ex)
        {
            App.Log(new Exception("[props] 辅助进程失败：" + ex.Message));
            return false;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern bool SHObjectProperties(IntPtr hwnd, uint dwFlags, string pszName, IntPtr pszParameters);

    const uint SHOP_FILEPATH = 0x00000002; // pszName 为文件系统路径

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr ILCreateFromPath(string pszPath);

    [DllImport("shell32.dll")]
    static extern void ILFree(IntPtr pidl);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr ILClone(IntPtr pidl);

    [DllImport("shell32.dll")]
    static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr ppv, out IntPtr ppidlLast);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHParseDisplayName(string name, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("user32.dll")]
    static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    static extern bool DestroyMenu(IntPtr hMenu);

    // 最小 IShellFolder：只声明到用到的槽位顺序。
    // 字符串参数必须显式 LPWStr：COM 接口默认按 ANSI 封送会把路径变乱码
    // （表现为 ParseDisplayName 报 E_INVALIDARG「值不在预期范围内」）
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("000214E6-0000-0000-C000-000000000046")]
    interface IShellFolder
    {
        void ParseDisplayName(IntPtr hwnd, IntPtr pbc,
            [MarshalAs(UnmanagedType.LPWStr)] string name,
            ref uint eaten, out IntPtr pidl, ref uint attrs);
        [PreserveSig]
        int EnumObjects(IntPtr hwnd, int grfFlags, out IntPtr enumList);
        void BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        void GetAttributesOf(uint cidl, IntPtr apidl, ref uint rgfInOut);
        [PreserveSig]
        int GetUIObjectOf(IntPtr hwndOwner, uint cidl, IntPtr[] apidl, ref Guid riid, IntPtr prgf, out IntPtr ppv);
        [PreserveSig]
        int GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr strret); // strret 指向调用方分配的 STRRET 缓冲
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("000214F2-0000-0000-C000-000000000046")]
    interface IEnumIDList
    {
        [PreserveSig]
        int Next(uint celt, out IntPtr rgelt, out uint pceltFetched);
        [PreserveSig]
        int Skip(uint celt);
        void Reset();
        void Clone(out IEnumIDList ppenum);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("000214E4-0000-0000-C000-000000000046")]
    interface IContextMenu
    {
        void QueryContextMenu(uint hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        void InvokeCommand(IntPtr pici);
        void GetCommandString(uint idCmd, uint uType, IntPtr reserved, IntPtr name, uint cch);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    struct CMINVOKECOMMANDINFO
    {
        public uint cbSize, fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPStr)] public string lpVerb;
        public IntPtr lpParameters, lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
    }

    const uint SHGDN_FORPARSING = 0x8000;

    const int SEE_MASK_INVOKEIDLIST = 0x0000000C; // 经条目 IContextMenu 调动词
    const int SEE_MASK_NOASYNC = 0x00000100;      // 调用方无消息泵时必须同步

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string lpVerb;
        public string lpFile;
        public string lpParameters;
        public string lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIconOrMonitor;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO info);

    // ShellExecuteEx + SEE_MASK_INVOKEIDLIST + 规范动词 "properties"（小写敏感）：
    // shell 会取条目的 IContextMenu 自行调用属性命令，不依赖注册表动词注册。
    // 这是资源管理器右键「属性」的等价入口，对字体命名空间条目同样有效。
    static bool TryVerbViaShellExecuteEx(string filePath)
    {
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = (uint)(SEE_MASK_INVOKEIDLIST | SEE_MASK_NOASYNC),
            lpVerb = "properties",
            lpFile = filePath,
            nShow = 1, // SW_SHOWNORMAL
        };
        if (ShellExecuteEx(ref info)) return true;
        App.Log(new Exception($"[props] ShellExecuteEx(INVOKEIDLIST) 失败 Win32Error={Marshal.GetLastWin32Error()}"));
        return false;
    }

    // 命名空间感知的属性页调用。路径 A：文件系统父文件夹的条目菜单；
    // 路径 B：绑定字体命名空间后按「文件名 / 显示名」解析；
    // 路径 C：解析被拒时枚举字体文件夹子项逐个比对文件名。
    // 每个失败分支都记入 error.log 便于定位卡点。
    public static bool TryContextMenuRoutes(IntPtr hwnd, string filePath, string displayNameHint)
    {
        if (TryViaParentFolder(hwnd, filePath, out var whyA)) return true;

        try
        {
            var fonts = (IShellFolder)Activator.CreateInstance(
                Type.GetTypeFromCLSID(new Guid("BD84B380-8CA2-1069-AB1D-08000948F534"))!)!;
            foreach (var cand in new[] { Path.GetFileName(filePath), displayNameHint })
            {
                if (string.IsNullOrEmpty(cand)) continue;
                uint eaten = 0, attrs = 0;
                try
                {
                    fonts.ParseDisplayName(hwnd, IntPtr.Zero, cand, ref eaten, out var child, ref attrs);
                    if (InvokePropertiesVerb(hwnd, fonts, child, out var whyB)) return true;
                    App.Log(new Exception($"[props] 字体命名空间「{cand}」失败：{whyB}"));
                }
                catch (COMException ex)
                {
                    App.Log(new Exception($"[props] 字体命名空间 ParseDisplayName「{cand}」hr=0x{ex.ErrorCode:X8}"));
                }
            }
        }
        catch (Exception ex)
        {
            App.Log(new Exception("[props] 字体命名空间绑定失败：" + ex.Message));
        }

        // 路径 C：枚举字体文件夹子项，按解析名/显示名匹配目标文件
        try
        {
            var fonts = (IShellFolder)Activator.CreateInstance(
                Type.GetTypeFromCLSID(new Guid("BD84B380-8CA2-1069-AB1D-08000948F534"))!)!;
            if (TryFindByEnumeration(hwnd, fonts, Path.GetFileName(filePath), displayNameHint,
                    out var childPidl, out var whyC))
            {
                bool ok = InvokePropertiesVerb(hwnd, fonts, childPidl, out _);
                ILFree(childPidl);
                if (ok) return true;
            }
            else
            {
                App.Log(new Exception($"[props] 字体命名空间枚举失败：{whyC}"));
            }
        }
        catch (Exception ex)
        {
            App.Log(new Exception("[props] 字体命名空间枚举异常：" + ex.Message));
        }

        App.Log(new Exception($"[props] 父文件夹路径失败：{whyA}"));
        return false;
    }

    static bool TryViaParentFolder(IntPtr hwnd, string filePath, out string why)
    {
        why = "";
        // SHParseDisplayName 与桌面解析器同源，比 ILCreateFromPath 多一层可用性
        int hrP = SHParseDisplayName(filePath, IntPtr.Zero, out var pidlFull, 0, out _);
        if (hrP != 0 || pidlFull == IntPtr.Zero)
        {
            why = $"SHParseDisplayName hr=0x{hrP:X8}";
            return false;
        }
        IntPtr folderPtr = IntPtr.Zero;
        try
        {
            var iidFolder = typeof(IShellFolder).GUID;
            int hr = SHBindToParent(pidlFull, ref iidFolder, out folderPtr, out var child);
            if (hr != 0 || folderPtr == IntPtr.Zero)
            {
                why = $"SHBindToParent hr=0x{hr:X8}";
                return false;
            }
            var folder = (IShellFolder)Marshal.GetTypedObjectForIUnknown(folderPtr, typeof(IShellFolder));
            return InvokePropertiesVerb(hwnd, folder, child, out why);
        }
        finally
        {
            if (folderPtr != IntPtr.Zero) Marshal.Release(folderPtr);
            ILFree(pidlFull);
        }
    }

    // 枚举字体文件夹子项，GetDisplayNameOf(FORPARSING) 比对文件名/显示名。
    static bool TryFindByEnumeration(IntPtr hwnd, IShellFolder folder,
        string fileName, string displayNameHint,
        out IntPtr matchPidl, out string why)
    {
        matchPidl = IntPtr.Zero;
        why = "";
        int hr = folder.EnumObjects(hwnd, 0x30 /*FOLDERS|NONFOLDERS*/, out var enumPtr);
        if (hr != 0 || enumPtr == IntPtr.Zero) { why = $"EnumObjects hr=0x{hr:X8}"; return false; }

        var buf = Marshal.AllocCoTaskMem(512); // STRRET 缓冲（uType + 联合）
        bool found = false;
        try
        {
            var enumIdList = (IEnumIDList)Marshal.GetObjectForIUnknown(enumPtr);
            int n = 0;
            while (enumIdList.Next(1, out var pidl, out _) == 0 && pidl != IntPtr.Zero)
            {
                n++;
                hr = folder.GetDisplayNameOf(pidl, SHGDN_FORPARSING, buf);
                if (hr == 0)
                {
                    uint type = (uint)Marshal.ReadInt32(buf);
                    string name = type switch
                    {
                        0 => Marshal.PtrToStringUni(Marshal.ReadIntPtr(buf, IntPtr.Size)) ?? "", // WSTR
                        1 => Marshal.PtrToStringAnsi(pidl + Marshal.ReadInt32(buf, IntPtr.Size)) ?? "", // PIDL 偏移
                        _ => Marshal.PtrToStringAnsi(buf + IntPtr.Size) ?? "", // cStr
                    };
                    if (!found &&
                        (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase)
                         || (displayNameHint.Length > 0 && name.Equals(displayNameHint, StringComparison.Ordinal))))
                    {
                        matchPidl = ILClone(pidl);
                        found = true;
                    }
                }
                ILFree(pidl);
            }
            if (!found) why = $"枚举 {n} 个子项未命中「{fileName}」";
        }
        finally
        {
            Marshal.FreeCoTaskMem(buf);
            Marshal.Release(enumPtr);
        }
        return found;
    }

    // 对单个 shell 条目调规范动词 properties；同步调用，属性页关闭后才返回
    static bool InvokePropertiesVerb(IntPtr hwnd, IShellFolder folder, IntPtr childPidl, out string why)
    {
        why = "";
        IntPtr menuPtr = IntPtr.Zero, hMenu = IntPtr.Zero, pici = IntPtr.Zero;
        try
        {
            var iidMenu = typeof(IContextMenu).GUID;
            int hr = folder.GetUIObjectOf(hwnd, 1, new[] { childPidl }, ref iidMenu, IntPtr.Zero, out menuPtr);
            if (hr != 0 || menuPtr == IntPtr.Zero)
            {
                why = $"GetUIObjectOf hr=0x{hr:X8}";
                return false;
            }

            var menu = (IContextMenu)Marshal.GetObjectForIUnknown(menuPtr);
            hMenu = CreatePopupMenu();
            menu.QueryContextMenu((uint)hMenu.ToInt64(), 0, 1, 0x7FFF, 0); // 初始化处理器上下文

            var ici = new CMINVOKECOMMANDINFO
            {
                cbSize = (uint)Marshal.SizeOf<CMINVOKECOMMANDINFO>(),
                hwnd = hwnd,
                lpVerb = "properties",
                nShow = 1, // SW_SHOWNORMAL
            };
            pici = Marshal.AllocCoTaskMem(Marshal.SizeOf<CMINVOKECOMMANDINFO>());
            Marshal.StructureToPtr(ici, pici, false);
            menu.InvokeCommand(pici);
            return true;
        }
        catch (COMException ex)
        {
            why = $"菜单调用 COM 异常 0x{ex.ErrorCode:X8}";
            return false;
        }
        finally
        {
            if (pici != IntPtr.Zero) Marshal.FreeCoTaskMem(pici);
            if (hMenu != IntPtr.Zero) DestroyMenu(hMenu);
            if (menuPtr != IntPtr.Zero) Marshal.Release(menuPtr);
        }
    }
}
