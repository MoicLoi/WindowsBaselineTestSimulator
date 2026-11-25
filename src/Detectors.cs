using System;
using System.Collections.Generic;
using System.Globalization;
using System.Management;
using Microsoft.Win32;

namespace WindowsBaselineTestSimulator
{
    /// <summary>
    /// 单项检测结果。Code 采用类似 HRESULT 的形式，便于对照微软文档里的 ResultCode。
    /// </summary>
    public class Detection
    {
        public CState State = CState.Unknown;
        public string Code = "0x80004005";
        public string Reason = "";
        public bool Simulated;

        public Detection() { }

        public Detection(CState state, string code, string reason)
        {
            State = state;
            Code = code;
            Reason = reason;
        }

        public static Detection On(string reason) { return new Detection(CState.On, "0x00000000", reason); }
        public static Detection Off(string reason) { return new Detection(CState.Off, "0x00000000", reason); }
        public static Detection Unsupported(string reason) { return new Detection(CState.NotSupported, "0x80070490", reason); }
        public static Detection Unknown(string code, string reason) { return new Detection(CState.Unknown, code, reason); }
    }

    /// <summary>
    /// 8 项真实检测。全部为只读查询，不修改任何系统设置。
    /// 非提权时部分查询会失败 —— 失败一律降级为 Unknown 并给出代码，绝不猜成 On/Off。
    /// </summary>
    public static class Detectors
    {
        // 1. 安全处理器（TPM 2.0 / Microsoft Pluton）
        public static Detection SecurityProcessor()
        {
            // TPM 命名空间受 ACL 保护，非提权查询会挂数秒后才报拒绝访问；
            // 以服务身份（LocalSystem）运行时无需提权，所以这里提前短路，避免无谓等待。
            if (!IsElevated())
                return Detection.Unknown("0x80070005",
                    "需要管理员权限才能读取 TPM；以服务身份运行（LocalSystem）不受此限");

            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    @"root\cimv2\security\microsofttpm",
                    "SELECT SpecVersion FROM Win32_Tpm"))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        string spec = Convert.ToString(mo["SpecVersion"], CultureInfo.InvariantCulture);
                        if (string.IsNullOrEmpty(spec)) continue;

                        string first = spec.Split(',')[0].Trim();
                        double version;
                        if (double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out version))
                        {
                            if (version >= 2.0) return Detection.On("TPM SpecVersion=" + spec);
                            return Detection.Off("TPM 版本过低（SpecVersion=" + spec + "）");
                        }
                        return Detection.Unknown("0x80004005", "无法解析 SpecVersion=" + spec);
                    }
                }
                return Detection.Unsupported("Win32_Tpm 无实例：可能无 TPM，或该密钥在固件中被禁用");
            }
            catch (UnauthorizedAccessException ex)
            {
                return Detection.Unknown("0x80070005", "WMI 需要管理员权限：" + ex.Message);
            }
            catch (ManagementException ex)
            {
                if (IsAccessDenied(ex))
                    return Detection.Unknown("0x80070005", "拒绝访问：读取 TPM 需要管理员权限，或改用服务身份（服务默认 LocalSystem）");
                return Detection.Unknown("0x80041010", "WMI 查询失败：" + ex.Message);
            }
            catch (Exception ex)
            {
                return Detection.Unknown("0x80004005", ex.GetType().Name + ": " + ex.Message);
            }
        }

        static bool IsAccessDenied(Exception ex)
        {
            string message = ex == null ? "" : ex.Message;
            return message.IndexOf("拒绝访问", StringComparison.Ordinal) >= 0
                || message.IndexOf("Access is denied", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool IsElevated()
        {
            try
            {
                var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)
                    || string.Equals(identity.Name, @"NT AUTHORITY\SYSTEM", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // 2. 安全启动
        public static Detection SecureBoot()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State"))
                {
                    if (key == null)
                        return Detection.Unknown("0x80070490", @"缺少 HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot\State（可能为 Legacy/BIOS 启动）");

                    object value = key.GetValue("UEFISecureBootEnabled");
                    if (value == null)
                        return Detection.Unknown("0x80070490", "UEFISecureBootEnabled 值不存在");

                    int state = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                    return state == 1
                        ? Detection.On("UEFISecureBootEnabled=1")
                        : Detection.Off("UEFISecureBootEnabled=0");
                }
            }
            catch (Exception ex)
            {
                return Detection.Unknown("0x80070005", "读取注册表失败：" + ex.Message);
            }
        }

        // 3. 基于虚拟化的安全（VBS）
        public static Detection Vbs()
        {
            Detection vbs, hvci;
            DeviceGuard(out vbs, out hvci);
            return vbs;
        }

        // 4. 内存完整性（HVCI）
        public static Detection MemoryIntegrity()
        {
            Detection vbs, hvci;
            DeviceGuard(out vbs, out hvci);
            return hvci;
        }

        static void DeviceGuard(out Detection vbs, out Detection hvci)
        {
            vbs = Detection.Unknown("0x80004005", "未执行 Device Guard 查询");
            hvci = Detection.Unknown("0x80004005", "未执行 Device Guard 查询");

            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    @"root\Microsoft\Windows\DeviceGuard",
                    "SELECT VirtualizationBasedSecurityStatus, SecurityServicesRunning FROM Win32_DeviceGuard"))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        int status = Convert.ToInt32(mo["VirtualizationBasedSecurityStatus"], CultureInfo.InvariantCulture);
                        int[] services = ToIntArray(mo["SecurityServicesRunning"]);
                        bool hvciRunning = Array.IndexOf(services, 2) >= 0;

                        if (status == 2) vbs = Detection.On("VirtualizationBasedSecurityStatus=2（运行中）");
                        else if (status == 1) vbs = Detection.Off("VirtualizationBasedSecurityStatus=1（已配置但未运行）");
                        else vbs = Detection.Off("VirtualizationBasedSecurityStatus=" + status + "（未启用）");

                        hvci = hvciRunning
                            ? Detection.On("SecurityServicesRunning 含 2（HVCI 运行中）")
                            : Detection.Off("SecurityServicesRunning=[" + Join(services) + "] 不含 2（HVCI）");
                        return;
                    }
                }
                vbs = Detection.Unknown("0x80041010", "Win32_DeviceGuard 无实例");
                hvci = Detection.Unknown("0x80041010", "Win32_DeviceGuard 无实例");
            }
            catch (Exception ex)
            {
                string code = ex is UnauthorizedAccessException ? "0x80070005" : "0x80041010";
                string reason = "Device Guard WMI 查询失败：" + ex.Message;
                vbs = Detection.Unknown(code, reason);
                hvci = Detection.Unknown(code, reason);
            }
        }

        // 5. 仅签名驱动（测试签名/关闭完整性检查会被判为不满足）
        public static Detection SignedDriversOnly()
        {
            string[] options;
            if (!TryStartOptions(out options))
                return Detection.Unknown("0x80070490", @"无法读取 HKLM\SYSTEM\CurrentControlSet\Control\SystemStartOptions");

            if (HasOption(options, "TESTSIGNING")) return Detection.Off("启动项启用了 TESTSIGNING");
            if (HasOption(options, "NOINTEGRITYCHECKS")) return Detection.Off("启动项启用了 NOINTEGRITYCHECKS");
            return Detection.On("启动项未启用 TESTSIGNING / NOINTEGRITYCHECKS");
        }

        // 6. 内核调试器保护
        public static Detection KernelDebuggerProtection()
        {
            string[] options;
            if (!TryStartOptions(out options))
                return Detection.Unknown("0x80070490", @"无法读取 HKLM\SYSTEM\CurrentControlSet\Control\SystemStartOptions");

            if (HasOption(options, "DEBUG")) return Detection.Off("启动项启用了 DEBUG");
            if (HasOption(options, "BOOTDEBUG")) return Detection.Off("启动项启用了 BOOTDEBUG");
            return Detection.On("启动项未启用 DEBUG / BOOTDEBUG");
        }

        // 7. 仅 Windows 认证（WHQL）驱动
        //    真实实现里这一项由安全处理器启动测量得出；本机没有等价 API，
        //    默认用「本次启动已加载的驱动是否全部微软签名/WHQL」作为代理（见 WhqlProxy）。
        //    config.ini:
        //      whqlMode=proxy       默认，代理检测
        //      whqlMode=assume-on   强制视为满足
        //      whqlMode=assume-off  强制视为不满足
        //      whqlMode=unknown     强制未知（等价旧行为）
        //      assumeWHQL_ONLY=on|off  旧键，仍兼容
        public static Detection WhqlOnly()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CI\Config"))
                {
                    if (key != null)
                    {
                        object value = key.GetValue("WhqlOnly");
                        if (value != null)
                        {
                            int state = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                            return state == 1
                                ? Detection.On(@"CI\Config\WhqlOnly=1（显式策略）")
                                : Detection.Off(@"CI\Config\WhqlOnly=0（显式策略）");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return Detection.Unknown("0x80070005", "读取 CI\\Config 失败：" + ex.Message);
            }

            string legacy = Store.GetSetting("assumeWHQL_ONLY", "");
            if (!string.IsNullOrEmpty(legacy)) return FromAssumption(legacy, "assumeWHQL_ONLY");

            string mode = Store.GetSetting("whqlMode", "proxy");
            if (string.IsNullOrEmpty(mode)) mode = "proxy";
            mode = mode.Trim().ToLowerInvariant();

            if (mode == "proxy" || mode == "auto" || mode == "on-proxy")
            {
                WhqlProxy.Result result = WhqlProxy.Check();
                return new Detection(result.State, result.Code, result.Reason);
            }

            return FromAssumption(mode.Replace("assume-", "").Replace("assume", ""), "whqlMode");
        }

        static Detection FromAssumption(string raw, string source)
        {
            if (string.Equals((raw ?? "").Trim(), "unknown", StringComparison.OrdinalIgnoreCase))
                return Detection.Unknown("0x80004005", "按 config.ini 的 " + source + "=unknown 强制为未知（未做检测）");

            CState assumed;
            if (Store.TryParseState(raw, out assumed) && assumed != CState.Unknown)
                return new Detection(assumed, "0x00000000", "按 config.ini 的 " + source + "=" + raw + " 假定（未做实际检测）");
            return Detection.Unknown("0x80004005", "config.ini 的 " + source + "=" + raw + " 无效，且未启用代理检测");
        }

        // 8. 易受攻击驱动阻止列表
        public static Detection VulnerableDriverBlocklist()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CI\Config"))
                {
                    if (key == null) return Detection.Unknown("0x80070490", @"缺少 CI\Config 键");

                    object value = key.GetValue("VulnerableDriverBlocklistEnable");
                    if (value == null) return Detection.Unknown("0x80070490", "VulnerableDriverBlocklistEnable 值不存在");

                    int state = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                    return state == 1
                        ? Detection.On("VulnerableDriverBlocklistEnable=1")
                        : Detection.Off("VulnerableDriverBlocklistEnable=0");
                }
            }
            catch (Exception ex)
            {
                return Detection.Unknown("0x80070005", "读取 CI\\Config 失败：" + ex.Message);
            }
        }

        // ---- 辅助 ----

        static bool TryStartOptions(out string[] options)
        {
            options = new string[0];
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control"))
                {
                    if (key == null) return false;
                    string raw = Convert.ToString(key.GetValue("SystemStartOptions"), CultureInfo.InvariantCulture);
                    if (string.IsNullOrEmpty(raw)) return false;
                    options = raw.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    return options.Length > 0;
                }
            }
            catch
            {
                return false;
            }
        }

        static bool HasOption(string[] options, string name)
        {
            foreach (string raw in options)
            {
                string token = raw.Trim();
                if (token.Length == 0) continue;
                int eq = token.IndexOf('=');
                string key = eq > 0 ? token.Substring(0, eq) : token;
                if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        static int[] ToIntArray(object value)
        {
            var array = value as Array;
            if (array == null) return new int[0];
            var list = new List<int>();
            foreach (object item in array)
            {
                try { list.Add(Convert.ToInt32(item, CultureInfo.InvariantCulture)); }
                catch { }
            }
            return list.ToArray();
        }

        static string Join(int[] values)
        {
            var parts = new List<string>();
            foreach (int v in values) parts.Add(v.ToString(CultureInfo.InvariantCulture));
            return string.Join(",", parts.ToArray());
        }
    }
}
