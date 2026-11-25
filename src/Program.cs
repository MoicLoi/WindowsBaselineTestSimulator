using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WindowsBaselineTestSimulator
{
    static class Program
    {
#if CONSOLE_BUILD
        static readonly bool IsConsoleBuild = true;
#else
        static readonly bool IsConsoleBuild = false;
#endif

        const int AttachParentProcess = -1;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AttachConsole(int processId);

        // Windows 10 1703+：显式声明 per-monitor v2，避免被系统按 96 DPI 位图拉伸（125%/150% 下窗口会溢出屏幕）
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        static void EnsureDpiAwareness()
        {
            try { SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); }
            catch { }
        }

        [STAThread]
        static int Main(string[] args)
        {
            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "tray";

            // 服务模式：由 SCM 启动；或非交互会话下自兜底，避免无桌面环境直接崩
            if (mode == "service" || !Environment.UserInteractive)
            {
                ServiceHost.Run();
                return 0;
            }

            AttachConsole(AttachParentProcess);
            Cli.SetupConsole();
            EnsureDpiAwareness();

            try
            {
                switch (mode)
                {
                    case "tray": return TrayApp.Run(false);
                    case "panel": return TrayApp.Run(true);
                    case "query": return Cli.Query(args);
                    case "status": return Cli.Status();
                    case "set": return Cli.Set(args);
                    case "whql": return Cli.Whql(args);
                    case "toggle": return RunToggle(null, args);
                    case "enable": return RunToggle(true, args);
                    case "disable": return RunToggle(false, args);
                    case "svc": return Cli.Svc(args);
                    case "reset": return Cli.Reset();
                    case "apply":
                    case "boot": return Cli.Apply();
                    case "run": return Cli.RunForeground();
                    case "help":
                    case "--help":
                    case "-h":
                    case "/?": return Cli.Help();
                    default:
                        Console.Error.WriteLine("未知模式：" + mode);
                        return Cli.Help();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
                return 1;
            }
        }

        static int RunToggle(bool? force, string[] args)
        {
            int code = Cli.Toggle(force);

            if (!IsConsoleBuild)
            {
                bool enabled = Store.IsDetectionEnabled();
                Toast.Show(enabled
                    ? "检测已开启：会重新评估 8 项"
                    : "检测已关闭：不做任何评估（再次按 Ctrl+Alt+D 或双击快捷方式可恢复）", enabled);
            }
            return code;
        }
    }
}
