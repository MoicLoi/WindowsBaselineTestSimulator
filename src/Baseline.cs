using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace WindowsBaselineTestSimulator
{
    public enum CState { On, Off, Unknown, NotSupported }

    /// <summary>总判定，对齐微软的 WLDP_BASELINE_SECURITY_MODE_STATE_*（省略前缀）。</summary>
    public enum Verdict { In, NotIn, Unknown }

    public class CriterionDef
    {
        public string Id;          // 短标识，用于 overrides.ini / 管道命令
        public string Criterion;   // 微软文档里的枚举名，JSON 输出时原样带上
        public string Name;        // 中文名
        public string What;        // 说明

        public CriterionDef(string id, string criterion, string name, string what)
        {
            Id = id;
            Criterion = criterion;
            Name = name;
            What = what;
        }
    }

    public class CriterionResult
    {
        public CriterionDef Def;
        public CState BootState = CState.Unknown;
        public CState CurrentState = CState.Unknown;
        public string BootCode = "0x80004005";
        public string CurrentCode = "0x80004005";
        public string BootReason = "";
        public string CurrentReason = "";
        public bool Simulated;

        public CriterionResult(CriterionDef def) { Def = def; }
    }

    /// <summary>落盘位置：优先 ProgramData（机器级，服务与所有用户共享），不可写时退回 LocalAppData。</summary>
    public static class Store
    {
        public static readonly string Dir = ResolveDir();

        public static string BootFile { get { return Path.Combine(Dir, "boot.txt"); } }
        public static string OverrideFile { get { return Path.Combine(Dir, "overrides.ini"); } }
        public static string ConfigFile { get { return Path.Combine(Dir, "config.ini"); } }
        public static string LogFile { get { return Path.Combine(Dir, "sim.log"); } }

        static string ResolveDir()
        {
            string[] candidates = new string[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WindowsBaselineTestSimulator"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsBaselineTestSimulator")
            };

            foreach (string dir in candidates)
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    string probe = Path.Combine(dir, ".writable");
                    File.WriteAllText(probe, "1");
                    File.Delete(probe);
                    return dir;
                }
                catch { }
            }
            return Path.Combine(Path.GetTempPath(), "WindowsBaselineTestSimulator");
        }

        /// <summary>
        /// 让标准用户也能改 config.ini / overrides.ini：
        /// 服务（LocalSystem）创建目录后默认只有 Users 读权限，那样软开关在服务停下后就切不动了。
        /// 这里给 Users 授 Modify（失败不影响主流程）。
        /// </summary>
        static void GrantUsersModify(string dir)
        {
            try
            {
                var info = new DirectoryInfo(dir);
                var security = info.GetAccessControl();
                var users = new System.Security.Principal.SecurityIdentifier(
                    System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null);
                security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    users,
                    System.Security.AccessControl.FileSystemRights.Modify,
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                    System.Security.AccessControl.PropagationFlags.None,
                    System.Security.AccessControl.AccessControlType.Allow));
                info.SetAccessControl(security);
            }
            catch (Exception ex) { Log("GrantUsersModify failed: " + ex.Message); }
        }

        public static void Log(string message)
        {
            try
            {
                File.AppendAllText(LogFile,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + message + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch { }
        }

        public static string GetSetting(string key, string fallback)
        {
            try
            {
                if (!File.Exists(ConfigFile)) return fallback;
                foreach (string line in File.ReadAllLines(ConfigFile))
                {
                    string text = line.Trim();
                    if (text.Length == 0 || text.StartsWith("#") || text.StartsWith(";")) continue;
                    int eq = text.IndexOf('=');
                    if (eq <= 0) continue;
                    if (string.Equals(text.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                        return text.Substring(eq + 1).Trim();
                }
            }
            catch { }
            return fallback;
        }

        public static Dictionary<string, CState> LoadOverrides()
        {
            var map = new Dictionary<string, CState>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(OverrideFile)) return map;
                foreach (string line in File.ReadAllLines(OverrideFile))
                {
                    string text = line.Trim();
                    if (text.Length == 0 || text.StartsWith("#") || text.StartsWith(";")) continue;
                    int eq = text.IndexOf('=');
                    if (eq <= 0) continue;
                    string id = text.Substring(0, eq).Trim();
                    CState state;
                    if (!TryParseState(text.Substring(eq + 1).Trim(), out state)) continue;
                    if (Baseline.Find(id) == null) continue;
                    map[id] = state;
                }
            }
            catch { }
            return map;
        }

        public static bool SaveOverrides(Dictionary<string, CState> map)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# WindowsBaselineTestSimulator 模拟注入：把某项强制为 on / off / unknown（只为演示，不改动真实系统设置）");
                sb.AppendLine("# 删除本文件或执行 reset 即恢复真实检测");
                foreach (var pair in map)
                    sb.AppendLine(pair.Key + "=" + StateText(pair.Value));
                File.WriteAllText(OverrideFile, sb.ToString(), Encoding.UTF8);
                return true;
            }
            catch (Exception ex)
            {
                Log("SaveOverrides failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>就地更新 config.ini 中的一个键，保留其它行与注释。</summary>
        public static bool UpdateSetting(string key, string value)
        {
            try
            {
                var lines = new System.Collections.Generic.List<string>();
                bool replaced = false;

                if (File.Exists(ConfigFile)) lines.AddRange(File.ReadAllLines(ConfigFile));

                for (int i = 0; i < lines.Count; i++)
                {
                    string text = lines[i].Trim();
                    if (text.Length == 0 || text.StartsWith("#") || text.StartsWith(";")) continue;
                    int eq = text.IndexOf('=');
                    if (eq <= 0) continue;
                    if (string.Equals(text.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                    {
                        lines[i] = key + "=" + value;
                        replaced = true;
                    }
                }

                if (!replaced) lines.Add(key + "=" + value);
                File.WriteAllText(ConfigFile, string.Join(Environment.NewLine, lines.ToArray()) + Environment.NewLine, Encoding.UTF8);
                return true;
            }
            catch (Exception ex)
            {
                Log("UpdateSetting failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>检测总开关（软开关）：off 时不做任何评估，也不扫描驱动。</summary>
        public static bool IsDetectionEnabled()
        {
            string raw = GetSetting("detection", "on");
            return !(string.Equals(raw, "off", StringComparison.OrdinalIgnoreCase)
                  || string.Equals(raw, "0", StringComparison.OrdinalIgnoreCase)
                  || string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase)
                  || string.Equals(raw, "no", StringComparison.OrdinalIgnoreCase));
        }

        public static bool SetDetectionEnabled(bool enabled)
        {
            bool ok = UpdateSetting("detection", enabled ? "on" : "off");
            if (ok) InvalidateSettingCache();
            return ok;
        }

        static void InvalidateSettingCache()
        {
            Baseline.InvalidateCache();
        }

        public static bool TryParseState(string text, out CState state)        {
            switch ((text ?? "").Trim().ToLowerInvariant())
            {
                case "on": case "1": case "true": case "yes": state = CState.On; return true;
                case "off": case "0": case "false": case "no": state = CState.Off; return true;
                case "unknown": case "?": state = CState.Unknown; return true;
                case "notsupported": case "unsupported": state = CState.NotSupported; return true;
                default: state = CState.Unknown; return false;
            }
        }

        public static string StateText(CState state)
        {
            switch (state)
            {
                case CState.On: return "on";
                case CState.Off: return "off";
                case CState.NotSupported: return "notsupported";
                default: return "unknown";
            }
        }
    }

    /// <summary>
    /// 8 项基线模型 + 评估器。核心语义与微软一致：
    ///  - BootState 来自“启动测量”（本程序在服务启动 / apply 时冻结一份快照），决定总判定；
    ///  - CurrentState 每次实时评估，只决定 nextBootState；
    ///  - 判定是“全过才算达到”，任何一项不满足即 NotIn。
    /// </summary>
    public static class Baseline
    {
        public const int Version = 1;
        public const string ApiNote = "本地审计模式；不可作为安全边界（真实实现同样如此要求）";

        public static readonly CriterionDef[] Criteria = new CriterionDef[]
        {
            new CriterionDef("TPM", "WLDP_BASELINE_SECURITY_CRITERION_TPM",
                "安全处理器", "TPM 2.0 或 Microsoft Pluton，用于硬件级密钥与测量"),
            new CriterionDef("SECURE_BOOT", "WLDP_BASELINE_SECURITY_CRITERION_SECURE_BOOT",
                "安全启动", "阻止未签名引导组件在启动阶段加载"),
            new CriterionDef("VBS", "WLDP_BASELINE_SECURITY_CRITERION_VBS",
                "基于虚拟化的安全", "用硬件虚拟化隔离关键进程与内核"),
            new CriterionDef("MEMORY_INTEGRITY", "WLDP_BASELINE_SECURITY_CRITERION_MEMORY_INTEGRITY",
                "内存完整性", "HVCI，阻止恶意代码进入受保护的内核区"),
            new CriterionDef("SIGNED_DRIVERS_ONLY", "WLDP_BASELINE_SECURITY_CRITERION_SIGNED_DRIVERS_ONLY",
                "仅签名驱动", "禁止测试签名/关闭完整性检查"),
            new CriterionDef("KERNEL_DEBUGGER_PROTECTION", "WLDP_BASELINE_SECURITY_CRITERION_KERNEL_DEBUGGER_PROTECTION",
                "内核调试器保护", "禁止用内核调试器绕过保护"),
            new CriterionDef("WHQL_ONLY", "WLDP_BASELINE_SECURITY_CRITERION_WHQL_ONLY",
                "仅 Windows 认证驱动", "只允许通过 WHQL 硬件签名的驱动"),
            new CriterionDef("VULNERABLE_DRIVER_BLOCKLIST", "WLDP_BASELINE_SECURITY_CRITERION_VULNERABLE_DRIVER_BLOCKLIST",
                "易受攻击驱动阻止列表", "阻止已知有漏洞的驱动加载")
        };

        public static CriterionDef Find(string id)
        {
            foreach (CriterionDef def in Criteria)
                if (string.Equals(def.Id, id, StringComparison.OrdinalIgnoreCase)) return def;
            return null;
        }

        // 真实检测结果短缓存：TPM/DeviceGuard 的 WMI 查询在非提权时可能耗时数秒，
        // 缓存可让托盘定时刷新与重复查询保持即时响应。
        static readonly object CacheLock = new object();
        static readonly Dictionary<string, Detection> RealCache = new Dictionary<string, Detection>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, DateTime> RealCacheAt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        static Dictionary<string, CState> _overrideCache;
        static DateTime _overrideStamp = DateTime.MinValue;

        public static void InvalidateCache()
        {
            lock (CacheLock)
            {
                RealCache.Clear();
                RealCacheAt.Clear();
                _overrideCache = null;
                _overrideStamp = DateTime.MinValue;
            }
        }

        /// <summary>实时检测（真实结果 + 模拟注入覆盖）。</summary>
        public static Detection Detect(CriterionDef def)
        {
            Detection real = DetectReal(def);

            CState injected;
            if (Overrides().TryGetValue(def.Id, out injected))
            {
                var simulated = new Detection(injected, "0x00000000",
                    "模拟注入[" + Store.StateText(injected) + "]；真实检测=" + Store.StateText(real.State) + "（" + real.Reason + "）");
                simulated.Simulated = true;
                return simulated;
            }
            return real;
        }

        static Dictionary<string, CState> Overrides()
        {
            lock (CacheLock)
            {
                DateTime stamp = DateTime.MinValue;
                try { if (File.Exists(Store.OverrideFile)) stamp = File.GetLastWriteTimeUtc(Store.OverrideFile); }
                catch { }

                if (_overrideCache != null && stamp == _overrideStamp) return _overrideCache;

                _overrideCache = Store.LoadOverrides();
                _overrideStamp = stamp;
                return _overrideCache;
            }
        }

        static Detection DetectReal(CriterionDef def)
        {
            lock (CacheLock)
            {
                Detection cached;
                DateTime at;
                if (RealCache.TryGetValue(def.Id, out cached) && RealCacheAt.TryGetValue(def.Id, out at)
                    && (DateTime.UtcNow - at).TotalSeconds < 60)
                    return cached;
            }

            Detection fresh = DetectRaw(def);

            lock (CacheLock)
            {
                RealCache[def.Id] = fresh;
                RealCacheAt[def.Id] = DateTime.UtcNow;
            }
            return fresh;
        }

        static Detection DetectRaw(CriterionDef def)
        {
            switch (def.Id)
            {
                case "TPM": return Detectors.SecurityProcessor();
                case "SECURE_BOOT": return Detectors.SecureBoot();
                case "VBS": return Detectors.Vbs();
                case "MEMORY_INTEGRITY": return Detectors.MemoryIntegrity();
                case "SIGNED_DRIVERS_ONLY": return Detectors.SignedDriversOnly();
                case "KERNEL_DEBUGGER_PROTECTION": return Detectors.KernelDebuggerProtection();
                case "WHQL_ONLY": return Detectors.WhqlOnly();
                case "VULNERABLE_DRIVER_BLOCKLIST": return Detectors.VulnerableDriverBlocklist();
                default: return Detection.Unknown("0x80004005", "未知项目");
            }
        }

        public static List<CriterionResult> EvaluateCurrent()
        {
            var list = new List<CriterionResult>();
            foreach (CriterionDef def in Criteria)
            {
                var result = new CriterionResult(def);
                Detection now = Detect(def);
                result.CurrentState = now.State;
                result.CurrentCode = now.Code;
                result.CurrentReason = now.Reason;
                result.Simulated = now.Simulated;
                list.Add(result);
            }
            return list;
        }

        /// <summary>冻结一次“启动测量”快照（服务启动 / apply 时调用）。</summary>
        public static string SnapshotBoot()
        {
            InvalidateCache();
            string bootId = Guid.NewGuid().ToString("D");
            string stamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

            var sb = new StringBuilder();
            sb.AppendLine("version=" + Version.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("bootId=" + bootId);
            sb.AppendLine("bootedAtUtc=" + stamp);
            foreach (CriterionResult r in EvaluateCurrent())
                sb.AppendLine(r.Def.Id + "|" + Store.StateText(r.CurrentState) + "|" + r.CurrentCode + "|" + Clean(r.CurrentReason));

            try { File.WriteAllText(Store.BootFile, sb.ToString(), Encoding.UTF8); }
            catch (Exception ex) { Store.Log("SnapshotBoot write failed: " + ex.Message); }

            Store.Log("boot snapshot " + bootId + " written");
            return bootId;
        }

        public class BootInfo
        {
            public int Version = Baseline.Version;
            public string BootId = "";
            public string BootedAtUtc = "";
            public Dictionary<string, string[]> Entries = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        }

        public static BootInfo LoadBoot()
        {
            var info = new BootInfo();
            try
            {
                if (!File.Exists(Store.BootFile)) return info;
                foreach (string rawLine in File.ReadAllLines(Store.BootFile))
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0) continue;
                    if (line.StartsWith("version=")) { int v; if (int.TryParse(line.Substring(8), out v)) info.Version = v; continue; }
                    if (line.StartsWith("bootId=")) { info.BootId = line.Substring(7).Trim(); continue; }
                    if (line.StartsWith("bootedAtUtc=")) { info.BootedAtUtc = line.Substring(12).Trim(); continue; }

                    string[] parts = line.Split(new[] { '|' }, 4);
                    if (parts.Length < 3) continue;
                    string reason = parts.Length > 3 ? parts[3] : "";
                    info.Entries[parts[0]] = new[] { parts[1], parts[2], reason };
                }
            }
            catch (Exception ex) { Store.Log("LoadBoot failed: " + ex.Message); }
            return info;
        }

        /// <summary>合并：BootState 取快照，CurrentState 取实时。</summary>
        public static List<CriterionResult> Merged(BootInfo boot)
        {
            var list = EvaluateCurrent();
            foreach (CriterionResult r in list)
            {
                string[] entry;
                if (boot != null && boot.Entries.TryGetValue(r.Def.Id, out entry))
                {
                    CState bootState;
                    if (Store.TryParseState(entry[0], out bootState)) r.BootState = bootState;
                    r.BootCode = entry[1];
                    r.BootReason = entry.Length > 2 ? entry[2] : "";
                }
                else
                {
                    r.BootState = CState.Unknown;
                    r.BootCode = "0x80070490";
                    r.BootReason = "尚无启动快照：启动服务或执行 apply 后才会产生";
                }
            }
            return list;
        }

        public static Verdict Rollup(IEnumerable<CState> states)
        {
            bool anyUnknown = false;
            foreach (CState s in states)
            {
                if (s == CState.Off) return Verdict.NotIn;
                if (s == CState.Unknown || s == CState.NotSupported) anyUnknown = true;
            }
            return anyUnknown ? Verdict.Unknown : Verdict.In;
        }

        public static Verdict VerdictOf(List<CriterionResult> list, bool useBoot)
        {
            var states = new List<CState>();
            foreach (CriterionResult r in list) states.Add(useBoot ? r.BootState : r.CurrentState);
            return Rollup(states);
        }

        /// <summary>当前应展示的结果：开关关时返回占位，避免白跑一次评估。</summary>
        public static List<CriterionResult> CurrentView()
        {
            return Store.IsDetectionEnabled() ? Merged(LoadBoot()) : DisabledList();
        }
        /// <summary>检测总开关关闭时的占位结果：全部 Unknown，并说明原因。</summary>
        public static List<CriterionResult> DisabledList()
        {
            var list = new List<CriterionResult>();
            foreach (CriterionDef def in Criteria)
            {
                var result = new CriterionResult(def);
                result.BootState = CState.Unknown;
                result.CurrentState = CState.Unknown;
                result.BootCode = "0x80070490";
                result.CurrentCode = "0x80070490";
                result.BootReason = "检测已关闭（快速开关）：未做任何评估";
                result.CurrentReason = result.BootReason;
                list.Add(result);
            }
            return list;
        }

        // ---- 序列化 ----

        public static string StateName(CState state)
        {
            switch (state)
            {
                case CState.On: return "On";
                case CState.Off: return "Off";
                case CState.NotSupported: return "NotSupported";
                default: return "Unknown";
            }
        }

        public static string VerdictName(Verdict verdict)
        {
            switch (verdict)
            {
                case Verdict.In: return "In";
                case Verdict.NotIn: return "NotIn";
                default: return "Unknown";
            }
        }

        public static string StateName(CState state, bool chinese)
        {
            if (!chinese) return StateName(state);
            switch (state)
            {
                case CState.On: return "开启";
                case CState.Off: return "关闭";
                case CState.NotSupported: return "不支持";
                default: return "未知";
            }
        }

        public static string VerdictName(Verdict verdict, bool chinese)
        {
            if (!chinese) return VerdictName(verdict);
            switch (verdict)
            {
                case Verdict.In: return "达成";
                case Verdict.NotIn: return "未达成";
                default: return "未知";
            }
        }

        public static string Json(string source)
        {
            bool enabled = Store.IsDetectionEnabled();
            BootInfo boot = LoadBoot();
            List<CriterionResult> list = enabled ? Merged(boot) : DisabledList();
            Verdict state = enabled ? VerdictOf(list, true) : Verdict.Unknown;
            Verdict next = enabled ? VerdictOf(list, false) : Verdict.Unknown;

            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append("\"schema\":\"WindowsBaselineTestSimulator/1\",");
            sb.Append("\"source\":").Append(Quote(source)).Append(",");
            sb.Append("\"detectionEnabled\":").Append(enabled ? "true" : "false").Append(",");
            sb.Append("\"engine\":").Append(Quote("WindowsBaselineTestSimulator 8-criteria simulator (local, audit-only, not a security boundary)")).Append(",");
            sb.Append("\"baselineVersion\":").Append(Version.ToString(CultureInfo.InvariantCulture)).Append(",");
            sb.Append("\"bootId\":").Append(Quote(boot.BootId)).Append(",");
            sb.Append("\"bootedAtUtc\":").Append(Quote(boot.BootedAtUtc)).Append(",");
            sb.Append("\"state\":").Append(Quote(enabled ? VerdictName(state) : "Disabled")).Append(",");
            sb.Append("\"nextBootState\":").Append(Quote(enabled ? VerdictName(next) : "Disabled")).Append(",");
            sb.Append("\"criteriaReturned\":").Append(list.Count.ToString(CultureInfo.InvariantCulture)).Append(",");

            sb.Append("\"criteria\":[");
            for (int i = 0; i < list.Count; i++)
            {
                CriterionResult r = list[i];
                if (i > 0) sb.Append(",");
                sb.Append("{");
                sb.Append("\"criterion\":").Append(Quote(r.Def.Criterion)).Append(",");
                sb.Append("\"id\":").Append(Quote(r.Def.Id)).Append(",");
                sb.Append("\"name\":").Append(Quote(r.Def.Name)).Append(",");
                sb.Append("\"bootState\":").Append(Quote(StateName(r.BootState))).Append(",");
                sb.Append("\"bootResultCode\":").Append(Quote(r.BootCode)).Append(",");
                sb.Append("\"currentState\":").Append(Quote(StateName(r.CurrentState))).Append(",");
                sb.Append("\"currentResultCode\":").Append(Quote(r.CurrentCode)).Append(",");
                sb.Append("\"simulated\":").Append(r.Simulated ? "true" : "false").Append(",");
                sb.Append("\"bootReason\":").Append(Quote(r.BootReason)).Append(",");
                sb.Append("\"currentReason\":").Append(Quote(r.CurrentReason));
                sb.Append("}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        public static string HumanReport(string source)
        {
            bool enabled = Store.IsDetectionEnabled();
            BootInfo boot = LoadBoot();
            List<CriterionResult> list = enabled ? Merged(boot) : DisabledList();
            Verdict state = enabled ? VerdictOf(list, true) : Verdict.Unknown;
            Verdict next = enabled ? VerdictOf(list, false) : Verdict.Unknown;

            var sb = new StringBuilder();
            sb.AppendLine("WindowsBaselineTestSimulator v" + Version + " · Windows 基线保护（8 项）模拟器   来源：" + source);
            sb.AppendLine("检测开关         : " + (enabled ? "开（正在评估）" : "关（快速开关已关闭，未做任何评估）"));
            sb.AppendLine("启动测量 bootId : " + (boot.BootId.Length > 0 ? boot.BootId : "(无)"));
            sb.AppendLine("本次启动判定     : " + VerdictName(state, true) + "   [" + VerdictName(state) + "]");
            sb.AppendLine("下次启动预期     : " + VerdictName(next, true) + "   [" + VerdictName(next) + "]");
            sb.AppendLine("-------------------------------------------------------------------------------");
            sb.AppendLine(Pad("检测项", 26) + Pad("本次启动", 12) + Pad("下次启动", 12) + "备注");
            sb.AppendLine("-------------------------------------------------------------------------------");
            foreach (CriterionResult r in list)
            {
                string flag = r.Simulated ? "*模拟注入" : "";
                sb.AppendLine(Pad(r.Def.Name, 26)
                    + Pad(StateName(r.BootState, true), 12)
                    + Pad(StateName(r.CurrentState, true), 12)
                    + flag);
                if (r.CurrentState != CState.On)
                    sb.AppendLine("    原因：" + r.CurrentReason);
            }
            sb.AppendLine("-------------------------------------------------------------------------------");
            sb.AppendLine("提示：任何一项缺失即整体未达成；改动后需执行 apply（模拟重启）才会改变“本次启动”判定。");
            return sb.ToString();
        }

        static string Clean(string text)
        {
            if (text == null) return "";
            return text.Replace("|", "/").Replace("\r", " ").Replace("\n", " ");
        }

        /// <summary>按“显示宽度”补齐：CJK 字符占两格。</summary>
        static int DisplayWidth(string text)
        {
            int width = 0;
            foreach (char c in text) width += (c >= 0x1100) ? 2 : 1;
            return width;
        }

        static string Pad(string text, int width)
        {
            int gap = width - DisplayWidth(text);
            return gap <= 0 ? text : text + new string(' ', gap);
        }

        static string Quote(string text) { return "\"" + Escape(text) + "\""; }

        static string Escape(string text)
        {
            if (text == null) return "";
            var sb = new StringBuilder(text.Length + 8);
            foreach (char c in text)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
