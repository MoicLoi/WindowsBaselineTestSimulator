# Changelog

本项目遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [1.0.0] - 2026-11-25

首个可发布版本。核心目标：在没有 Windows Insider 预览版（`wldp.dll` 尚无
`Experimental_WldpQueryBaselineSecurityMode` 导出）的机器上，复刻「Windows 基线保护」
的 8 项开机检测语义，供集成、演示与自测。

### 新增

- **8 项检测**，与微软 criterion 一一对应（TPM / Secure Boot / VBS / 内存完整性 /
  仅签名驱动 / 内核调试器保护 / WHQL 仅认证驱动 / 易受攻击驱动阻止列表），
  读不到的项一律 `Unknown` 并带 HRESULT 风格原因码，绝不猜成 `On`。
- **第 7 项代理检测**：枚举本次启动实际加载的内核驱动（`Win32_SystemDriver`，`State=Running`），
  逐个 `WinVerifyTrust` 验内嵌签名，未签名则回落 `.cat` 目录签名（`WTD_CHOICE_CATALOG`），
  验签链失败时退回读证书主题；按签名者分 `WHQL` / `Microsoft` / `thirdparty` / `unsigned`。
  本机实测捕获到仅第三方签名的内核驱动（`QesEngEx.sys`）。
- **启动测量语义**：`BootState`（本次启动，进判定）/ `CurrentState`（下次启动，只决定
  `nextBootState`），`apply` = 模拟重启；与官方「改动需重启才生效」一致。
- **三种形态**：Windows 服务（`WBTS`，LocalSystem）、托盘 + 面板、命令行 `wbts.exe`。
- **快速开关**（软开关 `detection=on|off`）：托盘菜单 / `Ctrl+Alt+D` / 开始菜单快捷方式 /
  `wbts toggle`；关闭后不做任何评估，`query` 返回 `state:"Disabled"`、`detectionEnabled:false`。
  另有需管理员的硬开关（`wbts svc start|stop|restart|status`）。
- **模拟注入**：`wbts set <id> on|off|unknown|reset`，只写 `overrides.ini`，不动系统设置；
  JSON 中 `simulated:true` 并保留真实检测值。
- **本地接口**：命名管道 `WBTS.v1`，单行命令 → 单行 JSON（`schema: WindowsBaselineTestSimulator/1`）。
- **MSI 安装包**：无需 WiX，用 `WindowsInstaller` COM + `makecab` 生成；含服务注册与启停、
  开始菜单、开机启动、按 UpgradeCode 升级、卸载清理。
- **诊断命令**：`wbts whql <文件>` 按 WHQL 口径分类单个驱动/可执行文件。

### 已知限制

- 第 7 项是**代理**（已加载驱动签名集合），不是微软的「策略 + 启动测量」口径。
- 非提权运行时第 1 项（TPM）恒为 `Unknown`；装服务（LocalSystem）或以管理员 `run` 才可读。
- MSI 的端到端安装需要管理员：本仓库自检覆盖到数据库结构、`msiexec /a` 解包与文件哈希，
  `msiexec /i` 在非提权会话会停在 1603。
- 这是模拟器，不是安全边界；官方预览 API 同样声明只能用于遥测/审计/引导，不可作为拦截判据。
