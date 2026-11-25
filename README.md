# WindowsBaselineTestSimulator —— Windows 11 基线保护（8 项检测）模拟器

对齐微软 **Windows baseline protection**（旧名 Baseline Security Mode / BSM，Insider build 26340.10013 起预览）的语义，
在本机复刻「8 项开机测量 → 一个版本化判定 → 应用可查询」这条链路。
用于在没装 Insider 预览版、或 `wldp.dll` 还没有 `Experimental_WldpQueryBaselineSecurityMode` 导出的机器上，
先做集成、演示和自测。

> **这不是安全边界。** 官方预览 API 本身也明确写了：本地 audit 模式的计算结果无法被远端密码学验证，
> 只能用于遥测、审计、诊断和引导用户。本工具同理，且它是**模拟器**：不修改任何真实安全设置。

---

## 1. 与微软 8 项的对应关系

| # | 微软 criterion（JSON 里原样输出） | 中文名 | 本工具的真实检测来源 | 局限 |
|---|---|---|---|---|
| 1 | `WLDP_BASELINE_SECURITY_CRITERION_TPM` | 安全处理器 | `root\cimv2\security\microsofttpm:Win32_Tpm` 的 `SpecVersion ≥ 2.0` | 该命名空间受 ACL 保护，**非提权直接返回 Unknown**（服务身份 LocalSystem 不受影响） |
| 2 | `..._SECURE_BOOT` | 安全启动 | `HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot\State\UEFISecureBootEnabled` | Legacy/BIOS 启动时该键不存在 → Unknown |
| 3 | `..._VBS` | 基于虚拟化的安全 | `Win32_DeviceGuard.VirtualizationBasedSecurityStatus == 2` | — |
| 4 | `..._MEMORY_INTEGRITY` | 内存完整性 | `Win32_DeviceGuard.SecurityServicesRunning` 含 `2`（HVCI） | VBS 未运行时必然为 Off |
| 5 | `..._SIGNED_DRIVERS_ONLY` | 仅签名驱动 | `SystemStartOptions` 中是否含 `TESTSIGNING` / `NOINTEGRITYCHECKS` | 读的是启动配置而非实时状态 |
| 6 | `..._KERNEL_DEBUGGER_PROTECTION` | 内核调试器保护 | `SystemStartOptions` 中是否含 `DEBUG` / `BOOTDEBUG` | 同上 |
| 7 | `..._WHQL_ONLY` | 仅 Windows 认证驱动 | **代理检测**：枚举`Win32_SystemDriver` 里 `State=Running` 的驱动，逐个用 `WinVerifyTrust` 验签（先内嵌签名，未签名则回落 `.cat` 目录签名），再看签名者是否 `Microsoft Windows Hardware Compatibility Publisher` | 代理口径 ≠ 真实口径：真实项检查「只允许 WHQL 驱动」的策略，本项检查「本次启动已加载的驱动是否全部微软签名/WHQL」。可用 `whqlMode` 切换 |
| 8 | `..._VULNERABLE_DRIVER_BLOCKLIST` | 易受攻击驱动阻止列表 | `CI\Config\VulnerableDriverBlocklistEnable == 1` | — |

判定规则与微软一致：**全过才是 `In`**；任一项 `Off` 即 `NotIn`；有项读不到则是 `Unknown`（且绝不猜成 On/Off）。

## 2. 三种形态

| 形态 | 二进制 | 作用 |
|---|---|---|
| Windows 服务 | `out\WindowsBaselineTestSimulator.exe service` | 以 LocalSystem 运行；启动即冻结一次「启动测量」快照，并通过命名管道对外提供查询 |
| 托盘快捷开关 | `out\WindowsBaselineTestSimulator.exe tray` | 状态灯 + 面板（`Ctrl+Alt+B`）；逐项「模拟关闭」、一键全部模拟不合格、应用并模拟重启 |
| 快速开关 | 托盘菜单 / `Ctrl+Alt+D` / 开始菜单「快速开关」/ `wbts.exe toggle` | 一键开/关检测：**软开关**（无需管理员）；另有需管理员的“启停 Windows 服务” |
| 命令行 | `out\wbts.exe` | 脚本集成：`query` / `status` / `set` / `reset` / `apply` / `toggle` / `svc` / `whql` / `run` |

> 为什么有两个 exe：`WindowsBaselineTestSimulator.exe` 是 `win32` 子系统（无控制台窗口），适合托盘与服务；
> 控制台输出在 GUI 子系统下不可靠，所以 CLI 单独编成 `wbts.exe`。两者源码完全相同。

## 3. 快速开始

### 3.0 用 MSI 安装（推荐，需要管理员）

```powershell
# 双击 out\WindowsBaselineTestSimulator.msi，或在管理员 PowerShell 里：
msiexec /i "D:\WinBaselineTest\out\WindowsBaselineTestSimulator.msi"

# 静默安装 / 卸载 / 带日志
msiexec /i "out\WindowsBaselineTestSimulator.msi" /qn
msiexec /x "out\WindowsBaselineTestSimulator.msi" /qn
msiexec /i "out\WindowsBaselineTestSimulator.msi" /l*v install.log
```

MSI 会做这些事：

| 动作 | 细节 |
|---|---|
| 装文件 | `%ProgramFiles%\WindowsBaselineTestSimulator\WindowsBaselineTestSimulator.exe`、`wbts.exe`（内嵌 cab，单文件分发） |
| 注册服务 | `WindowsBaselineTestSimulator`，LocalSystem、自动启动、启动参数 `service`；安装时启动，卸载时先停止再删除 |
| 开始菜单 | 文件夹「Windows 基线保护 (模拟器)」：打开面板 / 快速开关（检测 开-关）；卸载走「设置 > 应用」或 `msiexec /x` |
| 开机启动 | Startup 快捷方式（托盘常驻，`tray`） |
| 升级 | 固定 UpgradeCode + 每次新 PackageCode，新版覆盖安装会自动移除旧版 |
| 卸载 | 控制面板「应用」或 `msiexec /x`；程序目录、服务、快捷方式一并清理（`%ProgramData%` 状态保留，便于比对） |

重新生成安装包（改完源码后）：

```powershell
.\build.ps1
.\packaging\New-Msi.ps1                 # 缺省 1.0.0
.\packaging\New-Msi.ps1 -Version 1.0.1
.\packaging\Test-Msi.ps1                # 结构 + 解包 + 安装可行性自检
```

### 3.1 免安装（不需要管理员）

```powershell
.\build.ps1                 # 只用系统自带 .NET Framework 4.8 的 csc，不联网
.\out\WindowsBaselineTestSimulator.exe tray  # 托盘常驻；Ctrl+Alt+B 打开面板
.\out\wbts.exe status        # 另开一个窗口看判定
```

免安装模式下没有服务，`wbts.exe` 与托盘都在**当前进程权限**下评估，所以第 1 项（TPM）通常是 Unknown。

想先看管道服务的样子（不装服务）：

```powershell
.\out\wbts.exe run           # 前台跑管道服务，Ctrl+C 退出
```

### 3.2 安装为系统服务（管理员）

```powershell
# 管理员 PowerShell
powershell -ExecutionPolicy Bypass -File .\install-service.ps1
```

它会：复制程序到 `%ProgramFiles%\WindowsBaselineTestSimulator` → 创建自启服务 → 把托盘加入当前用户登录启动项 → 打印判定。

```powershell
# 卸载
powershell -ExecutionPolicy Bypass -File .\uninstall-service.ps1
```

服务以 LocalSystem 运行，因此 8 项里唯一需要提权的 TPM 也能正常判定。

### 3.3 检测快速开关

两层开关，避免每次都去服务管理器：

| 层级 | 作用 | 权限 | 触发方式 |
|---|---|---|---|
| **软开关**（`detection`） | 立即停止/恢复评估：关掉后服务与托盘都不再跑 8 项检测（含驱动扫描），`query` 返回 `state:"Disabled"`、`detectionEnabled:false` | 不需要管理员 | 托盘菜单「检测服务：开 / 关」、`Ctrl+Alt+D`、开始菜单「快速开关（检测 开-关）」、`wbts.exe toggle` / `enable` / `disable`、面板上的「检测 开/关」按钮 |
| **硬开关**（Windows 服务） | 真正启停 `WindowsBaselineTestSimulator` 服务 | 需要管理员 | 托盘菜单「启动 / 停止 Windows 服务（需管理员）」会弹 UAC；或 `wbts.exe svc start|stop|restart|status` |

说明：

- 软开关的值写在 `%ProgramData%\WindowsBaselineTestSimulator\config.ini` 的 `detection=on|off`；服务在运行时就由服务代写（管道命令 `enable` / `disable` / `toggle`），所以普通用户也能切。
- 关掉后不是“不响应”，而是明确回答“检测已被关闭”，集成方不会把它误判成“设备不安全”。
- 软开关不改任何系统设置，与“模拟注入”是两回事：后者只改某几项的**判定值**，前者连评估都不做。
- 服务首次启动时会给 `%ProgramData%\WindowsBaselineTestSimulator` 授予 `BUILTIN\Users: Modify`，因此服务停掉后普通用户依然能切软开关（否则 SYSTEM 创建的目录对普通用户只读，软开关会失效）。
- 面板与托盘图标在关闭状态会变灰并显示“检测已关闭”，`query` 的 `state` 为 `"Disabled"`，集成方不会误判成“设备不安全”。

## 4. 状态语义（与微软一致）

- **BootState（本次启动）**：服务启动 / `apply` 时冻结的快照，来自「启动测量」。**只有它进总判定。**
- **CurrentState（下次启动）**：每次查询实时评估，只汇总成 `nextBootState`。
- 因此改任何一项后，`state` 不变、`nextBootState` 变 —— 与微软「改基线项需要重启才生效」完全一致。
- `apply` 就是「模拟重启」：把当前状态冻结为新的 BootState（并换一个新 `bootId`）。

判定组合的读法：

| state | nextBootState | 含义 |
|---|---|---|
| In | In | 达标 |
| NotIn/Unknown | In | 配置已正确，只差重启 |
| In | NotIn | 本次还安全，但某项已被关掉 |
| NotIn | NotIn | 有项确实没开 |

## 4.5 第 7 项（WHQL_ONLY）的代理检测

微软那一项检查的是「系统被配置为只允许 WHQL 驱动」，由安全处理器在启动时测量，本机没有等价 API。
本工具用可观察的等价事实做代理——**看本次启动实际加载的驱动装了什么签名**：

1. `Win32_SystemDriver` 里 `State = Running` 的驱动（WMI，约 170 ms/200 个）。
2. 逐个 `WinVerifyTrust(DRIVER_ACTION_VERIFY)` 验内嵌签名；
3. 返回 `TRUST_E_NOSIGNATURE` 时，用 `CryptCATAdminCalcHashFromFileHandle2` 算文件哈希 →
   `CryptCATAdminEnumCatalogFromHash` 找目录 → `WinVerifyTrust(WTD_CHOICE_CATALOG)` 验 `.cat` 签名（这是 OEM/WHQL 驱动的常见形态）；
4. 验签链失败（如离线下根证书建链失败）时，退回读内嵌证书主题，仍能判断「谁签的」；
5. 按签名者分类：

| 签名者 | 分类 | 对判定的贡献 |
|---|---|---|
| `Microsoft Windows Hardware Compatibility Publisher` | WHQL / 认证签名 | 满足 |
| `Microsoft Windows` / 其它 Microsoft | Windows 内置驱动 | 满足 |
| 其它厂商（如 `Qi An Xin Technology Group Inc.`） | 仅第三方签名 | **不满足** |
| 无内嵌签名且无目录 | 未签名 | **不满足** |

判定输出会带上完整取证，例如本机实测结果：

```
仅 Windows 认证驱动   关闭
  原因：存在非微软认证驱动：QesEngEx.sys←CN=Qi An Xin Technology Group Inc. …
        扫描 214 个运行中驱动（WHQL 39，Windows 内置 174，仅第三方 1，未签名 0，
        读取失败 0，1267 ms）。代理口径：本次启动已加载的驱动并非全部经 WHQL/微软签名
```

单文件诊断：

```powershell
.\out\wbts.exe whql 'C:\Windows\System32\drivers\afunix.sys'      # → microsoft（目录签名）
.\out\wbts.exe whql 'C:\Program Files\Google\Chrome\Application\chrome.exe'  # → thirdparty
```

**三种口径可选**（`%ProgramData%\WindowsBaselineTestSimulator\config.ini`）：

```ini
whqlMode=proxy        # 默认：上面的代理检测
# whqlMode=assume-on  # 直接视为满足（不检测）
# whqlMode=assume-off # 直接视为不满足
# whqlMode=unknown    # 回到“本地无等价 API”的旧行为
```

约束：单次评估 5 s / 500 个驱动预算，超预算会截断并在原因里标注；结果在进程内缓存 60 s（服务与托盘受益，CLI 每次新进程都会重新扫，约 1.3 s）。

## 5. 模拟注入（快捷开关的核心）
「模拟关闭」写的是 `%ProgramData%\WindowsBaselineTestSimulator\overrides.ini`，**不改动真实系统设置**：

```powershell
.\out\wbts.exe set MEMORY_INTEGRITY off   # 只影响 CurrentState
.\out\wbts.exe query                      # state=Unknown/In，nextBootState=NotIn
.\out\wbts.exe apply                      # 模拟重启 → state=NotIn
.\out\wbts.exe set MEMORY_INTEGRITY reset # 恢复真实检测
.\out\wbts.exe reset                      # 清空所有注入
```

可用 id：`TPM` `SECURE_BOOT` `VBS` `MEMORY_INTEGRITY` `SIGNED_DRIVERS_ONLY`
`KERNEL_DEBUGGER_PROTECTION` `WHQL_ONLY` `VULNERABLE_DRIVER_BLOCKLIST`

被注入的项在 JSON 里带 `"simulated": true`，原因字段同时保留真实检测值，便于区分「真没开」和「假装没开」。

## 6. 给应用读的接口（管道协议）

管道名 `\\.\pipe\WBTS.v1`，单行请求 → 单行响应。

| 命令 | 响应 |
|---|---|
| `ping` | `pong` |
| `query` | 单行 JSON（见下） |
| `set <id> <on\|off\|unknown\|reset>` | `ok` / `err ...` |
| `enable` / `disable` / `toggle` | `ok detection=on|off`（软开关） |
| `reset` | `ok` |
| `apply` | `ok boot=<新 bootId>` |

JSON 结构（字段名刻意对齐官方 API 的 BootState / CurrentState / ResultCode）：

```json
{
  "schema": "WindowsBaselineTestSimulator/1",
  "source": "service",
  "detectionEnabled": true,
  "baselineVersion": 1,
  "bootId": "…",
  "bootedAtUtc": "2026-11-25T…",
  "state": "NotIn",
  "nextBootState": "NotIn",
  "criteriaReturned": 8,
  "criteria": [
    {
      "criterion": "WLDP_BASELINE_SECURITY_CRITERION_TPM",
      "id": "TPM",
      "name": "安全处理器",
      "bootState": "Off",
      "bootResultCode": "0x00000000",
      "currentState": "Off",
      "currentResultCode": "0x00000000",
      "simulated": false,
      "bootReason": "…",
      "currentReason": "…"
    }
  ]
}
```

PowerShell 侧最小调用（模拟「应用查询系统」）：

```powershell
function Get-BaselineState {
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'WBTS.v1', [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(300)
        $w = New-Object System.IO.StreamWriter($pipe); $w.WriteLine('query'); $w.Flush()
        $r = New-Object System.IO.StreamReader($pipe)
        return $r.ReadLine() | ConvertFrom-Json
    } finally { $pipe.Dispose() }
}

$s = Get-BaselineState
if ($s.state -ne 'In') { $s.criteria | Where-Object { $_.bootState -ne 'On' } | Select-Object id, bootState, bootReason }
```

## 7. 状态文件

目录：`%ProgramData%\WindowsBaselineTestSimulator\`（不可写时退回 `%LOCALAPPDATA%\WindowsBaselineTestSimulator\`）

| 文件 | 作用 |
|---|---|
| `boot.txt` | 启动测量快照（version / bootId / bootedAtUtc / 每项 state|code|reason） |
| `overrides.ini` | 模拟注入清单（删掉即恢复真实检测） |
| `config.ini` | 可选设置，例如 `whqlMode=proxy`（默认）/`assume-on`/`assume-off`/`unknown` |
| `sim.log` | 服务与托盘的事件日志（排查用） |

## 8. 已知限制

- **是模拟器，不是微软的实现**：判定只来自本机可读的配置/测量代理，不是安全处理器的启动度量。
- 第 7 项是**代理检测**：看的是“已加载驱动的签名集合”，真实项看的是“只允许 WHQL 驱动的策略”。
  多数机器结论一致，但对“已安装未加载的驱动”“驱动包内含非 WHQL 组件”不敏感。
- 非提权运行时第 1 项恒为 `Unknown`；要看真实结果请装服务，或以管理员启动 `wbts.exe run`。
- **MSI 未在非提权会话里端到端安装**：`msiexec /i` 会停在 1603。已自检的部分是数据库结构、行数据、
  `msiexec /a` 解包与文件 SHA-256 一致。首次真机安装请用管理员执行，失败看 `msiexec /i ... /l*v install.log`。
- 硬开关（启停服务）需要管理员；软开关不需要，推荐日常用软开关。
- 仅 Windows 10 / 11 x64；异常 DPI 或多虚拟显示器环境（如本机 Parsec / IddDesk）下窗口尺寸可能不理想，
  可用 `WBS_SIZE=1280x800` 强制指定。
- 真实能力上线后应改用官方 API：`GetProcAddress(wldp.dll, "Experimental_WldpQueryBaselineSecurityMode")`。

## 9. 目录结构

```
WindowsBaselineTestSimulator\
  build.ps1                    构建（csc，双目标：winexe + console）
  install-service.ps1          PowerShell 安装服务（MSI 的备用路径，管理员）
  uninstall-service.ps1        卸载（管理员）
  LICENSE  CHANGELOG.md  README.md
  .github\workflows\build.yml   CI：构建 + MSI + 自检 + release
  src\
    Program.cs                 入口与模式分发
    Detectors.cs               8 项真实检测
    Baseline.cs                模型 / 判定 / 快照 / 持久化 / JSON / 开关
    Ipc.cs                     命名管道服务端与客户端 + 命令分发
    ServiceHost.cs             Windows 服务宿主（WBTS）
    TrayApp.cs                 托盘图标 + 面板（DataGridView + 全局热键）
    Cli.cs                     CLI
    WhqlProxy.cs               第 7 项：驱动签名验签与代理判定
    app.manifest               PerMonitorV2 DPI 感知
  packaging\
    New-Msi.ps1                WindowsInstaller COM + makecab 生成 MSI（无需 WiX）
    Test-Msi.ps1               校验 MSI：表结构、解包、安装可行性
    New-Release.ps1            生成 dist\（zip + msi + SHA256SUMS + 发布说明）
  release\
    v1.0.0.md                  Release 正文模板
    publish-to-github.md       发布步骤（git / gh / 网页）
  out\                         WindowsBaselineTestSimulator.exe, wbts.exe, *.msi
  dist\                        WindowsBaselineTestSimulator-1.0.0-win-x64.zip 等
```

## 10. 发布（GitHub Release）

```powershell
.\build.ps1
.\packaging\New-Msi.ps1                     # 缺省 1.0.0
.\packaging\Test-Msi.ps1                    # 结构 + 解包 + 哈希自检
.\packaging\New-Release.ps1 -Version 1.0.0  # 产出 dist\
```

`dist\` 内容：

```
WindowsBaselineTestSimulator-1.0.0-win-x64.zip      ← Release 主产物
RELEASE-NOTES-v1.0.0.md                             ← Release 正文（含 SHA256 表）
WindowsBaselineTestSimulator-1.0.0-win-x64\         ← 解压即用目录
    WindowsBaselineTestSimulator.exe  wbts.exe
    WindowsBaselineTestSimulator-1.0.0-win-x64.msi
    README.md  CHANGELOG.md  LICENSE  SHA256SUMS.txt
```

发版步骤（git 初始化、打 tag、网页或 `gh release create` 上传）见
[`release/publish-to-github.md`](release/publish-to-github.md)；`v*` tag 会自动触发 CI 建 Release。