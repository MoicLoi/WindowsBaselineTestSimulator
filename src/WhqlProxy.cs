using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowsBaselineTestSimulator
{
    /// <summary>
    /// 第 7 项「仅 Windows 认证驱动」的**代理检测**。
    ///
    /// 真实实现里这一项由安全处理器在启动时测量得出，本机没有等价 API；
    /// 这里用可观察的等价事实做代理：
    ///   枚举「本次启动已加载」的内核驱动（Win32_SystemDriver, State=Running），
    ///   逐个验证签名 —— 先查文件内嵌签名，未签名则回落到目录（.cat）签名，
    ///   再按签名者分类：
    ///     Microsoft Windows Hardware Compatibility Publisher → WHQL / 认证签名
    ///     Microsoft Windows / Microsoft Corporation           → Windows 内置驱动
    ///     其它签名者                                          → 仅第三方签名（判定为不满足）
    ///     无签名且无目录                                      → 未签名（判定为不满足）
    ///
    /// 语义上的差异必须在结果里说清楚：真实项是「系统被配置为只允许 WHQL 驱动」，
    /// 代理项是「当前加载的驱动恰好全部是微软签名/认证的」。两者在多数机器上结论一致，
    /// 但前者更严格（它管的是策略，不是某次加载的集合）。
    /// </summary>
    public static class WhqlProxy
    {
        static readonly Guid DriverActionVerify = new Guid("F750E6C3-38EE-11D1-85E5-00C04FC295EE");
        static readonly Guid GenericVerifyV2 = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

        const uint TrustENoSignature = 0x800B0100;

        // 单次评估的时间预算：驱动数量多数百个，正常几百毫秒就能跑完
        const int TimeBudgetMs = 5000;
        const int MaxDrivers = 500;
        const int MaxNamedOffenders = 5;

        // ---- 结果 ----

        public class Result
        {
            public CState State = CState.Unknown;
            public string Code = "0x80004005";
            public string Reason = "";
            public int Scanned;
            public int Whql;
            public int MicrosoftInbox;
            public int ThirdParty;
            public int Unsigned;
            public int Unreadable;
            public long ElapsedMs;
            public bool Truncated;
        }

        // ---- WinVerifyTrust ----

        [StructLayout(LayoutKind.Sequential)]
        struct WintrustFileInfo
        {
            public uint cbStruct;
            public IntPtr pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WintrustCatalogInfo
        {
            public uint cbStruct;
            public uint dwCatalogVersion;
            public IntPtr pcwszCatalogFilePath;
            public IntPtr pcwszMemberTag;
            public IntPtr pcwszMemberFilePath;
            public IntPtr hMemberFile;
            public IntPtr pbCalculatedFileHash;
            public uint cbCalculatedFileHash;
            public IntPtr pcCatalogContext;
            public IntPtr hCatAdmin;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WintrustData
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pUnion;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct CatalogInfo
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string wszCatalogFile;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        static extern uint WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid actionId, IntPtr data);

        [DllImport("wintrust.dll")]
        static extern IntPtr WTHelperProvDataFromStateData(IntPtr stateData);

        [DllImport("wintrust.dll")]
        static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provData, uint idxSigner, bool fCounterSigner, uint idxCounterSigner);

        [DllImport("wintrust.dll")]
        static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint idxCert);

        [DllImport("crypt32.dll", CharSet = CharSet.Unicode)]
        static extern uint CertGetNameString(IntPtr cert, uint type, uint flags, IntPtr para, StringBuilder name, uint cch);

        [DllImport("wintrust.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CryptCATAdminAcquireContext2(out IntPtr admin, IntPtr pgSubsystem, string hashAlg, IntPtr strongHashPolicy, uint flags);

        [DllImport("wintrust.dll", SetLastError = true)]
        static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr admin, IntPtr file, ref uint cbHash, byte[] hash, uint flags);

        [DllImport("wintrust.dll", SetLastError = true)]
        static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr admin, byte[] hash, uint cbHash, uint flags, ref IntPtr prev);

        [DllImport("wintrust.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CryptCATCatalogInfoFromContext(IntPtr catInfo, ref CatalogInfo info, uint flags);

        [DllImport("wintrust.dll")]
        static extern bool CryptCATAdminReleaseCatalogContext(IntPtr admin, IntPtr catInfo, uint flags);

        [DllImport("wintrust.dll")]
        static extern bool CryptCATAdminReleaseContext(IntPtr admin, uint flags);

        // ---- 主流程 ----

        /// <summary>对单个文件做分类（供 CLI 诊断用）。</summary>
        public class FileVerdict
        {
            public string Path;
            public string Kind;      // whql | microsoft | thirdparty | unsigned | unreadable
            public string Signer;
            public string CatalogPath;
            public uint Status;
        }

        public static FileVerdict Describe(string path)
        {
            var verdict = new FileVerdict();
            verdict.Path = path;

            IntPtr admin = IntPtr.Zero;
            IntPtr guidPtr = IntPtr.Zero;
            try
            {
                guidPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Guid)));
                Marshal.StructureToPtr(DriverActionVerify, guidPtr, false);
                if (!CryptCATAdminAcquireContext2(out admin, guidPtr, "SHA256", IntPtr.Zero, 0)) admin = IntPtr.Zero;

                string signer;
                uint status = VerifyFile(path, out signer);
                verdict.Status = status;

                if (status == 0)
                {
                    verdict.Signer = signer;
                    verdict.Kind = KindOf(signer);
                    return verdict;
                }

                if (status != TrustENoSignature)
                {
                    string embedded = TryEmbeddedSigner(path);
                    if (embedded != null)
                    {
                        verdict.Signer = embedded + "（链验证失败 0x" + status.ToString("X8") + "，按内嵌签名主题判定）";
                        verdict.Kind = KindOf(embedded);
                        return verdict;
                    }
                    verdict.Kind = "unreadable";
                    return verdict;
                }

                if (admin == IntPtr.Zero)
                {
                    verdict.Kind = "unsigned";
                    return verdict;
                }

                byte[] hash;
                string catalogPath;
                if (!TryFindCatalog(admin, path, out hash, out catalogPath))
                {
                    verdict.Kind = "unsigned";
                    return verdict;
                }

                verdict.CatalogPath = catalogPath;
                string catalogSigner;
                uint catalogStatus = VerifyCatalog(admin, catalogPath, path, hash, out catalogSigner);
                if (catalogStatus != 0)
                {
                    verdict.Kind = "unreadable";
                    verdict.Status = catalogStatus;
                    return verdict;
                }

                verdict.Signer = catalogSigner;
                verdict.Kind = KindOf(catalogSigner);
                return verdict;
            }
            catch
            {
                verdict.Kind = "unreadable";
                return verdict;
            }
            finally
            {
                if (admin != IntPtr.Zero) CryptCATAdminReleaseContext(admin, 0);
                if (guidPtr != IntPtr.Zero) Marshal.FreeHGlobal(guidPtr);
            }
        }

        static string KindOf(string signer)
        {
            if (signer == null) return "unreadable";
            if (signer.IndexOf("Hardware Compatibility", StringComparison.OrdinalIgnoreCase) >= 0) return "whql";
            if (signer.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0) return "microsoft";
            return "thirdparty";
        }

        /// <summary>
        /// 验证链失败时（例如根证书不在本机受信存储、离线下无法建链），
        /// 仍可读出内嵌签名的主题名来判定“谁签的”，这比直接放弃更有用。
        /// </summary>
        static string TryEmbeddedSigner(string path)
        {
            try
            {
                var cert = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(path);
                return cert == null ? null : cert.Subject;
            }
            catch { return null; }
        }

        public static Result Check()
        {
            var result = new Result();
            var watch = Stopwatch.StartNew();
            IntPtr admin = IntPtr.Zero;
            IntPtr guidPtr = IntPtr.Zero;

            try
            {
                guidPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Guid)));
                Marshal.StructureToPtr(DriverActionVerify, guidPtr, false);
                if (!CryptCATAdminAcquireContext2(out admin, guidPtr, "SHA256", IntPtr.Zero, 0))
                    admin = IntPtr.Zero; // 目录校验不可用时仍可做内嵌签名判定

                var offenders = new List<string>();
                int running = EnumerateAndClassify(result, offenders, admin, watch);

                result.ElapsedMs = watch.ElapsedMilliseconds;
                result.Scanned = running;

                string coverage = string.Format(CultureInfo.InvariantCulture,
                    "扫描 {0} 个运行中驱动（WHQL {1}，Windows 内置 {2}，仅第三方 {3}，未签名 {4}，读取失败 {5}，{6} ms{7}）",
                    running, result.Whql, result.MicrosoftInbox, result.ThirdParty, result.Unsigned, result.Unreadable,
                    result.ElapsedMs, result.Truncated ? "，已按预算截断" : "");

                if (running == 0)
                {
                    result.State = CState.Unknown;
                    result.Code = "0x80070490";
                    result.Reason = "无法枚举运行中的内核驱动（WMI 不可用或被拒绝）";
                    return result;
                }

                if (result.ThirdParty > 0 || result.Unsigned > 0)
                {
                    result.State = CState.Off;
                    result.Code = "0x00000000";
                    var named = new StringBuilder();
                    for (int i = 0; i < offenders.Count && i < MaxNamedOffenders; i++)
                    {
                        if (named.Length > 0) named.Append("、");
                        named.Append(offenders[i]);
                    }
                    result.Reason = "存在非微软认证驱动：" + named
                        + (offenders.Count > MaxNamedOffenders ? " 等" : "")
                        + "。" + coverage
                        + "。代理口径：本次启动已加载的驱动并非全部经 WHQL/微软签名";
                    return result;
                }

                result.State = CState.On;
                result.Code = "0x00000000";
                result.Reason = "已加载驱动全部为微软签名或 WHQL 认证。" + coverage
                    + "。代理口径：真实项检查的是“仅允许 WHQL 驱动”的策略，本项检查的是本次加载集合";
                return result;
            }
            catch (Exception ex)
            {
                result.State = CState.Unknown;
                result.Code = "0x80004005";
                result.Reason = "代理检测异常：" + ex.GetType().Name + ": " + ex.Message;
                return result;
            }
            finally
            {
                if (admin != IntPtr.Zero) CryptCATAdminReleaseContext(admin, 0);
                if (guidPtr != IntPtr.Zero) Marshal.FreeHGlobal(guidPtr);
            }
        }

        static int EnumerateAndClassify(Result result, List<string> offenders, IntPtr admin, Stopwatch watch)
        {
            var paths = new List<string>();

            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT Name, State, PathName FROM Win32_SystemDriver"))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        string state = Convert.ToString(mo["State"], CultureInfo.InvariantCulture);
                        if (!string.Equals(state, "Running", StringComparison.OrdinalIgnoreCase)) continue;
                        string path = Convert.ToString(mo["PathName"], CultureInfo.InvariantCulture);
                        if (string.IsNullOrEmpty(path)) continue;
                        if (paths.Count >= MaxDrivers) { result.Truncated = true; break; }
                        paths.Add(path);
                    }
                }
            }
            catch (Exception ex)
            {
                Store.Log("WhqlProxy WMI failed: " + ex.Message);
                return 0;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int scanned = 0;

            foreach (string path in paths)
            {
                if (watch.ElapsedMilliseconds > TimeBudgetMs) { result.Truncated = true; break; }
                if (!seen.Add(path)) continue;
                scanned++;

                string signer;
                uint status = VerifyFile(path, out signer);

                if (status == 0)
                {
                    ClassifySigner(signer, result, offenders, Path.GetFileName(path));
                    continue;
                }

                if (status != TrustENoSignature)
                {
                    string embedded = TryEmbeddedSigner(path);
                    if (embedded != null) ClassifySigner(embedded, result, offenders, Path.GetFileName(path));
                    else result.Unreadable++;
                    continue;
                }

                if (admin == IntPtr.Zero)
                {
                    // 没有目录句柄：无法区分「目录签名」与「未签名」，保守计入未签名
                    result.Unsigned++;
                    AddOffender(offenders, Path.GetFileName(path));
                    continue;
                }

                byte[] hash;
                string catalog;
                if (!TryFindCatalog(admin, path, out hash, out catalog))
                {
                    result.Unsigned++;
                    AddOffender(offenders, Path.GetFileName(path));
                    continue;
                }

                string catalogSigner;
                uint catalogStatus = VerifyCatalog(admin, catalog, path, hash, out catalogSigner);
                if (catalogStatus != 0)
                {
                    result.Unreadable++;
                    continue;
                }

                ClassifySigner(catalogSigner, result, offenders, Path.GetFileName(path));
            }

            return scanned;
        }

        static void ClassifySigner(string signer, Result result, List<string> offenders, string fileName)
        {
            if (signer == null)
            {
                result.Unreadable++;
                return;
            }

            if (signer.IndexOf("Hardware Compatibility", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                result.Whql++;
                return;
            }

            if (signer.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                result.MicrosoftInbox++;
                return;
            }

            result.ThirdParty++;
            AddOffender(offenders, fileName + "←" + signer);
        }

        static void AddOffender(List<string> offenders, string text)
        {
            if (offenders.Count < 32) offenders.Add(text);
        }

        // ---- 签名验证 ----

        static uint VerifyFile(string path, out string signer)
        {
            signer = null;
            IntPtr pathPtr = IntPtr.Zero;
            IntPtr infoPtr = IntPtr.Zero;
            IntPtr dataPtr = IntPtr.Zero;
            Guid action = DriverActionVerify;

            try
            {
                pathPtr = Marshal.StringToHGlobalUni(path);

                var fi = new WintrustFileInfo();
                fi.cbStruct = (uint)Marshal.SizeOf(typeof(WintrustFileInfo));
                fi.pcwszFilePath = pathPtr;
                infoPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WintrustFileInfo)));
                Marshal.StructureToPtr(fi, infoPtr, false);

                var wd = new WintrustData();
                wd.cbStruct = (uint)Marshal.SizeOf(typeof(WintrustData));
                wd.dwUIChoice = 2;          // WTD_UI_NONE
                wd.dwUnionChoice = 1;       // WTD_CHOICE_FILE
                wd.pUnion = infoPtr;
                wd.dwStateAction = 1;       // WTD_STATEACTION_VERIFY
                wd.dwProvFlags = 0x100 | 0x10; // WTD_SAFER_FLAG | WTD_REVOCATION_CHECK_NONE
                dataPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WintrustData)));
                Marshal.StructureToPtr(wd, dataPtr, false);

                uint status = WinVerifyTrust(IntPtr.Zero, action, dataPtr);
                if (status == 0) ReadSigner(dataPtr, out signer);
                CloseState(dataPtr, action);
                return status;
            }
            catch { return 0x80004005; }
            finally
            {
                if (dataPtr != IntPtr.Zero) Marshal.FreeHGlobal(dataPtr);
                if (infoPtr != IntPtr.Zero) Marshal.FreeHGlobal(infoPtr);
                if (pathPtr != IntPtr.Zero) Marshal.FreeHGlobal(pathPtr);
            }
        }

        static bool TryFindCatalog(IntPtr admin, string path, out byte[] hash, out string catalogPath)
        {
            hash = null;
            catalogPath = null;

            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    IntPtr handle = stream.SafeFileHandle.DangerousGetHandle();
                    uint size = 0;
                    if (!CryptCATAdminCalcHashFromFileHandle2(admin, handle, ref size, null, 0) || size == 0) return false;

                    var buffer = new byte[size];
                    if (!CryptCATAdminCalcHashFromFileHandle2(admin, handle, ref size, buffer, 0)) return false;

                    IntPtr prev = IntPtr.Zero;
                    IntPtr catInfo = CryptCATAdminEnumCatalogFromHash(admin, buffer, size, 0, ref prev);
                    if (catInfo == IntPtr.Zero) return false;

                    try
                    {
                        var info = new CatalogInfo();
                        info.cbStruct = (uint)Marshal.SizeOf(typeof(CatalogInfo));
                        if (!CryptCATCatalogInfoFromContext(catInfo, ref info, 0)) return false;
                        hash = buffer;
                        catalogPath = info.wszCatalogFile;
                        return true;
                    }
                    finally
                    {
                        CryptCATAdminReleaseCatalogContext(admin, catInfo, 0);
                    }
                }
            }
            catch { return false; }
        }

        static uint VerifyCatalog(IntPtr admin, string catalogPath, string memberPath, byte[] hash, out string signer)
        {
            signer = null;
            IntPtr catPtr = IntPtr.Zero;
            IntPtr tagPtr = IntPtr.Zero;
            IntPtr memberPtr = IntPtr.Zero;
            IntPtr hashPtr = IntPtr.Zero;
            IntPtr infoPtr = IntPtr.Zero;
            IntPtr dataPtr = IntPtr.Zero;
            Guid action = GenericVerifyV2;

            try
            {
                catPtr = Marshal.StringToHGlobalUni(catalogPath);
                memberPtr = Marshal.StringToHGlobalUni(memberPath);
                hashPtr = Marshal.AllocHGlobal(hash.Length);
                Marshal.Copy(hash, 0, hashPtr, hash.Length);

                var tag = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) tag.Append(b.ToString("X2", CultureInfo.InvariantCulture));
                tagPtr = Marshal.StringToHGlobalUni(tag.ToString());

                var ci = new WintrustCatalogInfo();
                ci.cbStruct = (uint)Marshal.SizeOf(typeof(WintrustCatalogInfo));
                ci.pcwszCatalogFilePath = catPtr;
                ci.pcwszMemberTag = tagPtr;
                ci.pcwszMemberFilePath = memberPtr;
                ci.pbCalculatedFileHash = hashPtr;
                ci.cbCalculatedFileHash = (uint)hash.Length;
                ci.hCatAdmin = admin;
                infoPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WintrustCatalogInfo)));
                Marshal.StructureToPtr(ci, infoPtr, false);

                var wd = new WintrustData();
                wd.cbStruct = (uint)Marshal.SizeOf(typeof(WintrustData));
                wd.dwUIChoice = 2;
                wd.dwUnionChoice = 2;       // WTD_CHOICE_CATALOG
                wd.pUnion = infoPtr;
                wd.dwStateAction = 1;
                wd.dwProvFlags = 0x100 | 0x10;
                dataPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WintrustData)));
                Marshal.StructureToPtr(wd, dataPtr, false);

                uint status = WinVerifyTrust(IntPtr.Zero, action, dataPtr);
                if (status == 0) ReadSigner(dataPtr, out signer);
                CloseState(dataPtr, action);
                return status;
            }
            catch { return 0x80004005; }
            finally
            {
                if (dataPtr != IntPtr.Zero) Marshal.FreeHGlobal(dataPtr);
                if (infoPtr != IntPtr.Zero) Marshal.FreeHGlobal(infoPtr);
                if (hashPtr != IntPtr.Zero) Marshal.FreeHGlobal(hashPtr);
                if (memberPtr != IntPtr.Zero) Marshal.FreeHGlobal(memberPtr);
                if (tagPtr != IntPtr.Zero) Marshal.FreeHGlobal(tagPtr);
                if (catPtr != IntPtr.Zero) Marshal.FreeHGlobal(catPtr);
            }
        }

        static void ReadSigner(IntPtr dataPtr, out string signer)
        {
            signer = null;

            var after = (WintrustData)Marshal.PtrToStructure(dataPtr, typeof(WintrustData));
            IntPtr prov = WTHelperProvDataFromStateData(after.hWVTStateData);
            if (prov == IntPtr.Zero) return;

            IntPtr chain = WTHelperGetProvSignerFromChain(prov, 0, false, 0);
            if (chain == IntPtr.Zero) return;

            IntPtr providerCert = WTHelperGetProvCertFromChain(chain, 0);
            if (providerCert == IntPtr.Zero) return;

            // CRYPT_PROVIDER_CERT: cbStruct(4) + padding -> pCert 在 x64 偏移 8
            IntPtr certContext = Marshal.ReadIntPtr(providerCert, 8);
            if (certContext == IntPtr.Zero) return;

            var name = new StringBuilder(512);
            if (CertGetNameString(certContext, 4, 0, IntPtr.Zero, name, (uint)name.Capacity) > 1)
                signer = name.ToString();
        }

        static void CloseState(IntPtr dataPtr, Guid action)
        {
            try
            {
                var close = (WintrustData)Marshal.PtrToStructure(dataPtr, typeof(WintrustData));
                close.dwStateAction = 2; // WTD_STATEACTION_CLOSE
                Marshal.StructureToPtr(close, dataPtr, false);
                WinVerifyTrust(IntPtr.Zero, action, dataPtr);
            }
            catch { }
        }
    }
}
