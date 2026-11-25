using System;
using System.Text;
using System.Threading;

namespace WindowsBaselineTestSimulator
{
    public static class Cli
    {
        public static void SetupConsole()
        {
            try { Console.OutputEncoding = new UTF8Encoding(false); }
            catch { }
        }

        public static int Query(string[] args)
        {
            if (HasFlag(args, "--human") || HasFlag(args, "-h"))
            {
                Console.WriteLine(Baseline.HumanReport(Ipc.IsServiceAvailable() ? "service" : "local"));
                return 0;
            }

            string response = Ipc.Send("query");
            if (response != null && response.StartsWith("{")) Console.WriteLine(response);
            else Console.WriteLine(Baseline.Json("local"));
            return 0;
        }

        public static int Status()
        {
            Console.WriteLine(Baseline.HumanReport(Ipc.IsServiceAvailable() ? "service（管道在线）" : "local（服务未运行，按当前进程权限实时评估）"));
            return 0;
        }

        public static int Set(string[] args)
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("用法：WindowsBaselineTestSimulator.exe set <id> <on|off|unknown|reset>");
                Console.Error.WriteLine("可用 id：" + Ids());
                return 2;
            }

            CriterionDef def = Baseline.Find(args[1]);
            if (def == null)
            {
                Console.Error.WriteLine("未知 id：" + args[1] + "；可用 id：" + Ids());
                return 2;
            }

            string response = Ipc.SendOrLocal("set " + def.Id + " " + args[2]);
            Console.WriteLine("set " + def.Id + " -> " + response);
            return response != null && response.StartsWith("ok") ? 0 : 1;
        }

        public static int Reset()
        {
            string response = Ipc.SendOrLocal("reset");
            Console.WriteLine("reset -> " + response);
            return response != null && response.StartsWith("ok") ? 0 : 1;
        }

        public static int Apply()
        {
            string response = Ipc.SendOrLocal("apply");
            Console.WriteLine("apply -> " + response);
            Console.WriteLine("（已把当前状态冻结为新的“本次启动”快照，等价于一次模拟重启）");
            return response != null && response.StartsWith("ok") ? 0 : 1;
        }

        public static int RunForeground()
        {
            Console.WriteLine("前台模拟服务：启动快照 + 管道服务，Ctrl+C 或关闭进程退出。");
            Ipc.StartServer();
            Console.WriteLine("管道名：\\\\.\\pipe\\" + Ipc.PipeName);
            Console.WriteLine("状态文件目录：" + Store.Dir);

            Console.WriteLine("正在做首次启动测量（非提权下 TPM/VBS 的 WMI 查询可能耗时数秒）…");
            Baseline.SnapshotBoot();
            Console.WriteLine("启动测量完成，可以查询了。");

            var stop = new ManualResetEvent(false);
            Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                stop.Set();
            };
            try
            {
                while (!stop.WaitOne(500)) { }
            }
            catch { }
            Ipc.StopServer();
            return 0;
        }

        public static int Toggle(bool? forceOn)
        {
            bool current = Store.IsDetectionEnabled();
            bool target = forceOn.HasValue ? forceOn.Value : !current;

            string response = Ipc.SendOrLocal(target ? "enable" : "disable");
            bool now = Store.IsDetectionEnabled();

            Console.WriteLine("检测开关 -> " + (now ? "开（正在评估）" : "关（不做任何评估）") + "   [" + response + "]");
            if (now == current) Console.WriteLine("注意：状态未变化，可能是状态目录不可写（" + Store.Dir + "）");
            return 0;
        }

        public static int Svc(string[] args)
        {
            string action = args.Length > 1 ? args[1].ToLowerInvariant() : "status";

            try
            {
                var controller = new System.ServiceProcess.ServiceController(ServiceNames.Name);
                controller.Refresh();

                switch (action)
                {
                    case "status":
                        Console.WriteLine("服务 " + ServiceNames.Name + " 状态：" + controller.Status);
                        return 0;

                    case "start":
                        if (controller.Status == System.ServiceProcess.ServiceControllerStatus.Running)
                        {
                            Console.WriteLine("服务已在运行。");
                            return 0;
                        }
                        controller.Start();
                        controller.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
                        Console.WriteLine("服务已启动：" + controller.Status);
                        return 0;

                    case "stop":
                        if (controller.Status == System.ServiceProcess.ServiceControllerStatus.Stopped)
                        {
                            Console.WriteLine("服务已停止。");
                            return 0;
                        }
                        controller.Stop();
                        controller.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
                        Console.WriteLine("服务已停止：" + controller.Status);
                        return 0;

                    case "restart":
                        if (controller.Status != System.ServiceProcess.ServiceControllerStatus.Stopped)
                        {
                            controller.Stop();
                            controller.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
                        }
                        controller.Start();
                        controller.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
                        Console.WriteLine("服务已重启：" + controller.Status);
                        return 0;

                    default:
                        Console.Error.WriteLine("用法：wbts.exe svc <start|stop|restart|status>");
                        return 2;
                }
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine("操作失败：" + ex.Message);
                Console.Error.WriteLine("常见原因：服务未安装（先运行安装程序），或当前进程没有管理员权限。");
                return 1;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                Console.Error.WriteLine("操作失败：" + ex.Message + "（通常是缺少管理员权限）");
                return 1;
            }
        }

        public static int Whql(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("用法：wbts.exe whql <驱动文件路径>");
                return 2;
            }

            string path = args[1];
            if (!System.IO.File.Exists(path))
            {
                Console.Error.WriteLine("文件不存在：" + path);
                return 2;
            }

            WhqlProxy.FileVerdict verdict = WhqlProxy.Describe(path);
            string kind;
            switch (verdict.Kind)
            {
                case "whql": kind = "WHQL / 微软硬件兼容签名（满足）"; break;
                case "microsoft": kind = "Windows 内置驱动签名（满足）"; break;
                case "thirdparty": kind = "仅第三方签名（不满足）"; break;
                case "unsigned": kind = "未签名 / 无目录签名（不满足）"; break;
                default: kind = "无法判定"; break;
            }

            Console.WriteLine("文件      : " + verdict.Path);
            Console.WriteLine("分类      : " + kind + "   [" + verdict.Kind + "]");
            Console.WriteLine("签名者    : " + (verdict.Signer == null ? "(无)" : verdict.Signer));
            Console.WriteLine("验证状态  : 0x" + verdict.Status.ToString("X8"));
            Console.WriteLine("目录签名  : " + (verdict.CatalogPath == null ? "(无)" : verdict.CatalogPath));
            return 0;
        }

        public static int Help()
        {
            Console.WriteLine("WindowsBaselineTestSimulator —— Windows 11 基线保护（8 项检测）模拟器");
            Console.WriteLine();
            Console.WriteLine("  WindowsBaselineTestSimulator.exe tray                     托盘 + 快捷开关（默认，Ctrl+Alt+B 打开面板）");
            Console.WriteLine("  WindowsBaselineTestSimulator.exe query [--human]          查询判定（默认输出单行 JSON）");
            Console.WriteLine("  WindowsBaselineTestSimulator.exe status                   人类可读的状态表");
            Console.WriteLine("  WindowsBaselineTestSimulator.exe set <id> <on|off|unknown|reset>");
            Console.WriteLine("  WindowsBaselineTestSimulator.exe whql <文件>              按 WHQL 口径分类单个驱动文件（第 7 项诊断）");
            Console.WriteLine("  WindowsBaselineTestSimulator.exe reset                    清除全部模拟注入");
            Console.WriteLine("  WindowsBaselineTestSimulator.exe apply                    冻结当前状态为“本次启动”快照（模拟重启）");
            Console.WriteLine("  WindowsBaselineTestSimulator.exe run                       前台跑管道服务（免安装验证用）");
            Console.WriteLine("  WindowsBaselineTestSimulator.exe service                   以 Windows 服务身份运行（由 SCM 调用）");
            Console.WriteLine();
            Console.WriteLine("  id：" + Ids());
            Console.WriteLine("  状态文件目录：" + Store.Dir);
            return 0;
        }

        static string Ids()
        {
            var sb = new StringBuilder();
            foreach (CriterionDef def in Baseline.Criteria)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(def.Id);
            }
            return sb.ToString();
        }

        static bool HasFlag(string[] args, string flag)
        {
            foreach (string a in args)
                if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
