using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace WindowsBaselineTestSimulator
{
    /// <summary>
    /// 本地命名管道：模拟“应用查询系统基线状态”的那条通道。
    /// 协议：单行请求 → 单行响应（query 返回单行 JSON）。
    /// </summary>
    public static class Ipc
    {
        public const string PipeName = "WBTS.v1";

        static Thread _thread;
        static volatile bool _running;

        public static bool ServerRunning { get { return _running; } }

        public static void StartServer()
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "WindowsBaselineTestSimulator.Pipe";
            _thread.Start();
            Store.Log("pipe server started");
        }

        public static void StopServer()
        {
            if (!_running) return;
            _running = false;
            try
            {
                using (var wake = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut))
                    wake.Connect(250);
            }
            catch { }
            Store.Log("pipe server stop requested");
        }

        static void Loop()
        {
            while (_running)
            {
                try
                {
                    using (var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.None))
                    {
                        server.WaitForConnection();

                        string request;
                        using (var reader = new StreamReader(server, Encoding.UTF8, false, 1024, true))
                            request = reader.ReadLine();

                        string response = Dispatch(request ?? "");

                        using (var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, true))
                        {
                            writer.WriteLine(response);
                            writer.Flush();
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (_running)
                    {
                        Store.Log("pipe loop error: " + ex.Message);
                        Thread.Sleep(200);
                    }
                }
            }
        }

        /// <summary>命令分发：管道与本地降级共用同一份实现，保证行为一致。</summary>
        public static string Dispatch(string request)
        {
            string text = (request ?? "").Trim();
            if (text.Length == 0) return "err empty";

            string[] parts = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string command = parts[0].ToLowerInvariant();

            try
            {
                switch (command)
                {
                    case "ping":
                        return "pong";

                    case "query":
                    case "info":
                        return Baseline.Json("service");

                    case "enable":
                        return Store.SetDetectionEnabled(true) ? "ok detection=on" : "err write-failed";

                    case "disable":
                        return Store.SetDetectionEnabled(false) ? "ok detection=off" : "err write-failed";

                    case "toggle":
                        {
                            bool target = !Store.IsDetectionEnabled();
                            if (!Store.SetDetectionEnabled(target)) return "err write-failed";
                            return "ok detection=" + (target ? "on" : "off");
                        }

                    case "detection":
                        {
                            string mode = parts.Length > 1 ? parts[1].ToLowerInvariant() : "toggle";
                            if (mode == "toggle") goto case "toggle";
                            bool enabled;
                            if (mode == "on" || mode == "enable") enabled = true;
                            else if (mode == "off" || mode == "disable") enabled = false;
                            else return "err usage: detection on|off|toggle";
                            return Store.SetDetectionEnabled(enabled) ? ("ok detection=" + (enabled ? "on" : "off")) : "err write-failed";
                        }

                    case "apply":
                        return "ok boot=" + Baseline.SnapshotBoot();

                    case "reset":
                        return Store.SaveOverrides(new Dictionary<string, CState>()) ? "ok" : "err write-failed";

                    case "set":
                        {
                            if (parts.Length < 3) return "err usage: set <id> on|off|unknown|reset";
                            CriterionDef def = Baseline.Find(parts[1]);
                            if (def == null) return "err unknown-id";

                            var map = Store.LoadOverrides();
                            string value = parts[2].ToLowerInvariant();
                            if (value == "reset" || value == "real" || value == "clear")
                            {
                                map.Remove(def.Id);
                            }
                            else
                            {
                                CState state;
                                if (!Store.TryParseState(value, out state)) return "err bad-state";
                                map[def.Id] = state;
                            }
                            return Store.SaveOverrides(map) ? "ok" : "err write-failed";
                        }

                    case "help":
                        return "commands: ping | query | set <id> <on|off|unknown|reset> | reset | apply | help";

                    default:
                        return "err unknown-command";
                }
            }
            catch (Exception ex)
            {
                return "err " + ex.GetType().Name + ": " + ex.Message;
            }
        }

        /// <summary>客户端调用；服务未运行返回 null，调用方降级为本地执行。</summary>
        public static string Send(string request, int timeoutMs = 400)
        {
            try
            {
                using (var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.None))
                {
                    client.Connect(timeoutMs);

                    using (var writer = new StreamWriter(client, new UTF8Encoding(false), 1024, true))
                    {
                        writer.WriteLine(request);
                        writer.Flush();
                    }

                    using (var reader = new StreamReader(client, Encoding.UTF8, false, 1024, true))
                        return reader.ReadLine();
                }
            }
            catch { return null; }
        }

        public static bool IsServiceAvailable()
        {
            string pong = Send("ping", 250);
            return pong != null && pong.StartsWith("pong", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>优先走服务；服务不在则本地执行同一条命令。</summary>
        public static string SendOrLocal(string request)
        {
            string response = Send(request);
            if (response != null) return response;
            return Dispatch(request);
        }
    }
}
