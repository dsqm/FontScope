using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace FontScope;

public partial class App : System.Windows.Application
{
    static string? _lastShownError;

    public static string ErrorLog => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error.log");

    public static void Log(Exception ex)
    {
        try
        {
            File.AppendAllText(ErrorLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}]\n{ex}\n\n");
        }
        catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // 未处理异常写日志（渲染线程的 fatal error 无法捕获，仍会直接退出）
        DispatcherUnhandledException += (s, args) =>
        {
            Log(args.Exception);
            var msg = exToString(args.Exception);
            // 列表多行同时失败会连续抛同一异常，去重只弹一次
            if (msg != _lastShownError)
            {
                _lastShownError = msg;
                System.Windows.MessageBox.Show(msg, "FontScope 错误");
            }
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            Log(args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString() ?? "?"));
        TaskScheduler.UnobservedTaskException += (s, args) => Log(args.Exception);

        base.OnStartup(e);

        // --showprops <字体文件>：属性页隔离辅助进程。
        // shell 上下文菜单处理器在本进程内执行，损坏时会 AV 连带宿主崩溃，
        // 因此主程序把属性页调用放到这个独立进程，崩了也不影响主界面。
        if (e.Args.Length > 0 && e.Args[0] == "--showprops")
        {
            bool ok = false;
            try { ok = ShellProps.RunStandalone(e.Args.Length > 1 ? e.Args[1] : ""); }
            catch (Exception ex) { Log(ex); }
            if (!ok)
            {
                Shutdown(1);
                return;
            }
            // 属性对话框由 shell 在本进程内异步显示：必须保持消息泵，
            // 否则进程立即退出会把对话框一并带走（表现为毫无反应）。
            // 不能死等固定时长——属性页一关就变成无窗口隐形进程，任务管理器里看着像没退出。
            // 改为轮询本进程可见顶层窗口：消失即退出；5 秒宽限等对话框弹出，
            // 连续两次空轮询防误判；10 分钟硬上限兜底。
            var uptime = Stopwatch.StartNew();
            int emptyPolls = 0;
            var poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            var kill = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
            kill.Tick += (s, _) => { poll.Stop(); kill.Stop(); Shutdown(0); };
            poll.Tick += (s, _) =>
            {
                if (HasVisibleWindow()) { emptyPolls = 0; return; }
                emptyPolls++;
                if (uptime.Elapsed.TotalSeconds > 5 && emptyPolls >= 2)
                {
                    poll.Stop(); kill.Stop(); Shutdown(0);
                }
            };
            poll.Start();
            kill.Start();
            return;
        }
        new MainWindow().Show();
    }

    // 本进程是否存在可见顶层窗口（属性对话框属于本进程，关闭后即为 false）
    static bool HasVisibleWindow()
    {
        bool found = false;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint wpid);
            if (wpid == _currentPid && IsWindowVisible(h)) { found = true; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    static readonly uint _currentPid = GetCurrentProcessId();

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    static string exToString(Exception ex) =>
        ex.InnerException != null ? ex.Message + "\n\n" + ex.InnerException.Message : ex.Message;
}
