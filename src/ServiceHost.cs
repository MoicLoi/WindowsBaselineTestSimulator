using System;
using System.ServiceProcess;

namespace WindowsBaselineTestSimulator
{
    /// <summary>
    /// Windows 服务宿主：服务启动即视为一次“开机”，冻结一份启动测量快照，然后对外提供管道查询。
    /// </summary>
    public class BaselineSimService : ServiceBase
    {
        public BaselineSimService()
        {
            ServiceName = ServiceNames.Name;
            CanStop = true;
            CanShutdown = true;
            CanPauseAndContinue = false;
            AutoLog = true;
        }

        protected override void OnStart(string[] args)
        {
            Store.Log("service OnStart");
            // 先让管道可用，再做可能耗时数秒的 WMI 检测，避免 SCM 启动超时
            Ipc.StartServer();
            Baseline.SnapshotBoot();
        }

        protected override void OnStop()
        {
            Store.Log("service OnStop");
            Ipc.StopServer();
        }

        protected override void OnShutdown()
        {
            Ipc.StopServer();
            base.OnShutdown();
        }
    }

    public static class ServiceNames
    {
        public const string Name = "WBTS";
        public const string Display = "WindowsBaselineTestSimulator（基线保护模拟器）";
        public const string Description = "模拟 Windows 11 基线保护的 8 项开机检测：启动时冻结快照，并通过本地命名管道对外提供查询。仅供测试，不修改任何系统安全设置。";
    }

    public static class ServiceHost
    {
        public static void Run()
        {
            ServiceBase.Run(new BaselineSimService());
        }
    }
}
