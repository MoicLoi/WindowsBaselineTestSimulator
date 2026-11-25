<#
    校验 out\WindowsBaselineTestSimulator.msi：
      1) 用 WindowsInstaller COM 逐表统计行数，并抽取关键行
      2) msiexec /a（管理员安装 = 解包）验证 File/Media/cab 链路
      3) msiexec /i 静默安装，验证包能被解析（非提权应停在 1925/1603，而不是解析错误）
#>
[CmdletBinding()]
param(
    [string]$Msi,
    [string]$ExtractDir
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $Msi) { $Msi = (Get-ChildItem (Join-Path $root 'out\*.msi') | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName }
if (-not $ExtractDir) { $ExtractDir = Join-Path $root 'packaging\build\extract' }

if (-not (Test-Path $Msi)) { throw "找不到 $Msi" }
Write-Host ('MSI: ' + $Msi + '  ' + [math]::Round((Get-Item $Msi).Length / 1KB, 1) + ' KB')

# ---------------------------------------------------------------- 1. 表结构

$installer = New-Object -ComObject WindowsInstaller.Installer
$db = $installer.OpenDatabase($Msi, 0)

$tables = @('Property','Directory','Component','Feature','FeatureComponents','File','Media','InstallExecuteSequence','AdminExecuteSequence','ServiceInstall','ServiceControl','Shortcut','Icon','Upgrade','LaunchCondition','_Streams')
$rows = @{}
foreach ($t in $tables) {
    try {
        $view = $db.OpenView("SELECT * FROM ``$t``")
        $view.Execute()
        $n = 0
        while ($null -ne $view.Fetch()) { $n++ }
        $view.Close()
        $rows[$t] = $n
    } catch { $rows[$t] = 'ERR' }
}

Write-Host ''
Write-Host '表行数：'
foreach ($k in ($rows.Keys | Sort-Object)) { '  {0,-24} {1}' -f $k, $rows[$k] }

function Show-Rows {
    param([string]$Sql, [string]$Title)
    Write-Host ''
    Write-Host $Title
    $view = $db.OpenView($Sql)
    $view.Execute()
    while ($r = $view.Fetch()) {
        $vals = @()
        for ($i = 1; $i -le $r.FieldCount(); $i++) { $vals += [string]$r.StringData($i) }
        '  ' + ($vals -join ' | ')
    }
    $view.Close()
}

Show-Rows 'SELECT `Property`, `Value` FROM `Property`' 'Property：'
Show-Rows 'SELECT `ServiceInstall`, `Name`, `StartType`, `StartName`, `Arguments` FROM `ServiceInstall`' 'ServiceInstall：'
Show-Rows 'SELECT `ServiceControl`, `Name`, `Event`, `Wait` FROM `ServiceControl`' 'ServiceControl：'
Show-Rows 'SELECT `Shortcut`, `Directory_`, `Name`, `Arguments` FROM `Shortcut`' 'Shortcut：'
Show-Rows 'SELECT `File`, `FileName`, `FileSize` FROM `File`' 'File：'
Show-Rows 'SELECT `DiskId`, `LastSequence`, `Cabinet` FROM `Media`' 'Media：'
Show-Rows 'SELECT `Action`, `Sequence` FROM `InstallExecuteSequence`' 'InstallExecuteSequence：'

$db = $null
[System.Runtime.InteropServices.Marshal]::ReleaseComObject($installer) | Out-Null

# ---------------------------------------------------------------- 2. 解包验证

if (Test-Path $ExtractDir) { Remove-Item $ExtractDir -Recurse -Force }
New-Item -ItemType Directory -Path $ExtractDir -Force | Out-Null

Write-Host ''
Write-Host ('管理员安装（/a）解包到：' + $ExtractDir)
$log = Join-Path $ExtractDir 'admin.log'
$proc = Start-Process -FilePath 'msiexec.exe' -ArgumentList @('/a', ('"' + $Msi + '"'), '/qn', ('TARGETDIR=' + $ExtractDir), '/l*v', ('"' + $log + '"')) -Wait -PassThru
Write-Host ('  msiexec /a 退出码：' + $proc.ExitCode)

$extracted = @()
$extracted += Get-ChildItem -Path $ExtractDir -Recurse -File -Filter 'WindowsBaselineTestSimulator.exe' -ErrorAction SilentlyContinue
$extracted += Get-ChildItem -Path $ExtractDir -Recurse -File -Filter 'wbts.exe' -ErrorAction SilentlyContinue
if ($extracted.Count -eq 0) {
    Write-Warning ('未解出文件，详见日志：' + $log)
} else {
    foreach ($f in $extracted) {
        $source = Join-Path $root ('out\' + $f.Name)
        $h1 = (Get-FileHash $f.FullName -Algorithm SHA256).Hash
        $h2 = (Get-FileHash $source -Algorithm SHA256).Hash
        '  {0,-20} {1}  {2}' -f $f.Name, [math]::Round($f.Length / 1KB, 1), ($(if ($h1 -eq $h2) { '哈希一致' } else { '哈希不一致!' }))
    }
}

# ---------------------------------------------------------------- 3. 安装能被解析

Write-Host ''
$installLog = Join-Path $ExtractDir 'install.log'
$proc2 = Start-Process -FilePath 'msiexec.exe' -ArgumentList @('/i', ('"' + $Msi + '"'), '/qn', '/l*v', ('"' + $installLog + '"')) -Wait -PassThru

if ($proc2.ExitCode -eq 0) {
    # 管理员环境（如 CI）：真的装上了，做一次端到端验证后再卸掉，保持机器干净
    Write-Host 'msiexec /i 退出码：0（已实际安装，做端到端验证）'
    $installedDir = Join-Path $env:ProgramFiles 'WindowsBaselineTestSimulator'

    foreach ($n in @('WindowsBaselineTestSimulator.exe', 'wbts.exe')) {
        $exists = Test-Path (Join-Path $installedDir $n)
        $state = '缺失!'
        if ($exists) { $state = '存在' }
        '  文件 {0,-34} {1}' -f $n, $state
    }

    $svc = Get-Service -Name 'WBTS' -ErrorAction SilentlyContinue
    $svcState = '未注册!'
    if ($svc) { $svcState = [string]$svc.Status }
    '  服务 WBTS                        ' + $svcState

    $menu = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\WindowsBaselineTestSimulator'
    $menuState = '缺失!'
    if (Test-Path $menu) { $menuState = '已创建' }
    '  开始菜单                         ' + $menuState

    Write-Host '  卸载（msiexec /x）…'
    $proc3 = Start-Process -FilePath 'msiexec.exe' -ArgumentList @('/x', ('"' + $Msi + '"'), '/qn') -Wait -PassThru
    '  卸载退出码：' + $proc3.ExitCode

    Start-Sleep -Seconds 2
    $svcAfter = Get-Service -Name 'WBTS' -ErrorAction SilentlyContinue
    $after = '无'
    if ($svcAfter) { $after = '仍有（' + $svcAfter.Status + '）' }
    '  卸载后服务残留：' + $after
} else {
    Write-Host ('msiexec /i 退出码：' + $proc2.ExitCode + '（非提权下预期 1603/1925，而不是解析类错误）')
}
$logText = Get-Content -Path $installLog -Raw -ErrorAction SilentlyContinue
if ($logText) {
    $hits = [regex]::Matches($logText, 'Error \d+:.*|Installation success|Product: .*') | Select-Object -First 6
    foreach ($h in $hits) { '  ' + $h.Value.Trim() }
}
Write-Host ''
Write-Host '校验完成。'
