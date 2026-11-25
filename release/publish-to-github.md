# 发布到 GitHub

本仓库的成品已经生成在 `dist\`：

```
dist\
  WindowsBaselineTestSimulator-1.0.0-win-x64.zip          ← Release 上传的主产物（139 KB）
  RELEASE-NOTES-v1.0.0.md                                 ← Release 正文（已含 SHA256 表）
  WindowsBaselineTestSimulator-1.0.0-win-x64\             ← 解压后的目录，可直接上传或自取
      WindowsBaselineTestSimulator.exe
      wbts.exe
      WindowsBaselineTestSimulator-1.0.0-win-x64.msi
      README.md  CHANGELOG.md  LICENSE  SHA256SUMS.txt
```

`dist\` 与 `out\` 都在 `.gitignore` 里，源码进仓库、产物挂 Release。

## 0. 走本地代理（本机 nano.exe）

本机 `nano.exe` 在 `127.0.0.1:65532` 同时提供 HTTP 与 SOCKS5 代理（`0.0.0.0:65532`）。
仓库级配置（推荐，只影响本仓库）：

```powershell
git config http.proxy  http://127.0.0.1:65532
git config https.proxy http://127.0.0.1:65532
git config --local --get-regexp 'proxy'      # 确认
```

临时用一次：`git -c http.proxy=http://127.0.0.1:65532 push`；
想全局生效就把 `--local` 换成 `--global`。
验证代理是否可用：

```powershell
curl.exe -x http://127.0.0.1:65532 -sS -o NUL -w "code=%{http_code}" https://api.github.com
```

> 端口以 `Get-NetTCPConnection -State Listen | Where-Object OwningProcess -eq (Get-Process nano).Id` 为准，
> 换网络环境或重启代理后端口可能变。

## 1. 首次推送仓库

```powershell
cd D:\WinBaselineTest
git init -b main
git add .
git config user.name  "你的名字"
git config user.email "you@example.com"
git commit -m "feat: WindowsBaselineTestSimulator 1.0.0（8 项基线检测模拟器 + MSI + 快速开关）"
git remote add origin https://github.com/<你的账号>/WindowsBaselineTestSimulator.git
git push -u origin main
```

> `git` 已装但未配置 `user.name/user.email`，上面用了仓库级配置；也可以先 `git config --global` 设一次。

## 2. 打 tag 并建 Release

```powershell
git tag -a v1.0.0 -m "v1.0.0"
git push origin v1.0.0
```

**方式 A：网页上传（不装 gh）**

1. 仓库 → Releases → *Draft a new release*
2. Tag 选 `v1.0.0`，标题填 `v1.0.0`
3. 正文粘贴 `dist\RELEASE-NOTES-v1.0.0.md` 的内容
4. 附件拖入 `dist\WindowsBaselineTestSimulator-1.0.0-win-x64.zip`
   （可选再加 `...\SHA256SUMS.txt` 与 `.msi`）
5. Publish release

**方式 B：命令行（GitHub CLI）**

```powershell
winget install GitHub.cli      # 或 scoop install gh
gh auth login

gh release create v1.0.0 `
  "dist\WindowsBaselineTestSimulator-1.0.0-win-x64.zip" `
  "dist\WindowsBaselineTestSimulator-1.0.0-win-x64\SHA256SUMS.txt" `
  --title "v1.0.0" `
  --notes-file "dist\RELEASE-NOTES-v1.0.0.md"
```

## 3. 仓库设置建议

- **About**：`Windows 11 基线保护（8 项）的本地模拟器：服务 + 托盘快速开关 + MSI，供应用集成与自测`
- **Topics**：`windows-11` `security-baseline` `winverifytrust` `wdac` `hvci` `tpm` `msi` `wix-alternative` `winstaller` `csharp`
- **License**：MIT（`LICENSE` 已放仓库根；发布前把版权行换成你的名字/组织）
- **Releases**：勾选 *Set as the latest release*

## 4. CI（可选）

`.github/workflows/build.yml` 已就绪：push / PR 时在 `windows-latest` 上跑
`build.ps1 → New-Msi.ps1 → Test-Msi.ps1 → New-Release.ps1` 并上传 artifact；
**打 `v*` tag 时**自动用 `softprops/action-gh-release` 建 Release 并附上 zip 与 `SHA256SUMS.txt`。

## 5. 发布前自检清单

```powershell
.\build.ps1                      # 应输出两个 exe
.\packaging\New-Msi.ps1          # 应输出 msi 与 ProductCode/PackageCode
.\packaging\Test-Msi.ps1         # /a 退出 0 + 哈希一致 + /i 停在 1603（非提权属正常）
.\packaging\New-Release.ps1      # 生成 dist\ 与 RELEASE-NOTES
(Get-FileHash dist\WindowsBaselineTestSimulator-1.0.0-win-x64.zip).Hash
```

- [x] 解压后 `wbts.exe status` 可运行（已实测）
- [x] `SHA256SUMS.txt` 与包内文件一一对应（已实测）
- [ ] MSI 真机安装（需管理员，本仓库自检只到 `/a` 解包）
- [ ] 把 `LICENSE` 版权行改成你的名字/组织
