<#
    构建 WindowsBaselineTestSimulator（仅用系统自带的 .NET Framework 4.8 编译器，不联网、不需要 SDK）
    产出两个二进制，源码完全相同：
        out\WindowsBaselineTestSimulator.exe   子系统 win32(无控制台) —— 托盘快捷开关 + Windows 服务宿主
        out\wbts.exe           子系统 console      —— 命令行（query/status/set/reset/apply/run）
    用法：
        .\build.ps1
        .\build.ps1 -Run
#>
[CmdletBinding()]
param(
    [switch]$Run
)

$ErrorActionPreference = 'Stop'

$root     = Split-Path -Parent $MyInvocation.MyCommand.Path
$srcDir   = Join-Path $root 'src'
$outDir   = Join-Path $root 'out'
$assetDir = Join-Path $root 'assets'
$guiExe   = Join-Path $outDir 'WindowsBaselineTestSimulator.exe'
$cliExe   = Join-Path $outDir 'wbts.exe'
$iconPath = Join-Path $assetDir 'baseline.ico'
$manifest = Join-Path $srcDir 'app.manifest'

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw '找不到 .NET Framework 4.x 的 csc.exe' }
$frameworkDir = Split-Path -Parent $csc

foreach ($dir in @($outDir, $assetDir)) {
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
}

function New-AppIcon {
    param([string]$Path)

    Add-Type -AssemblyName System.Drawing
    $bmp = New-Object System.Drawing.Bitmap 32, 32
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $fill = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(16, 124, 16))
    $g.FillEllipse($fill, 2, 2, 28, 28)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), 2.6
    $g.DrawEllipse($pen, 6, 6, 20, 20)
    $pen2 = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(210, 16, 124, 16)), 2.0
    $g.DrawLine($pen2, 7, 16, 15, 15)
    $g.DrawLine($pen2, 15, 15, 23, 17)

    $handle = $bmp.GetHicon()
    $icon = [System.Drawing.Icon]::FromHandle($handle)
    $stream = [System.IO.File]::Create($Path)
    try { $icon.Save($stream) } finally { $stream.Close() }
    $icon.Dispose(); $g.Dispose(); $fill.Dispose(); $pen.Dispose(); $pen2.Dispose(); $bmp.Dispose()
}

if (-not (Test-Path $iconPath)) {
    try { New-AppIcon -Path $iconPath } catch { Write-Warning ('生成图标失败，将使用默认图标：' + $_.Exception.Message) }
}

$sources = @(Get-ChildItem -Path $srcDir -Filter *.cs -File | Sort-Object Name | ForEach-Object { $_.FullName })
if ($sources.Count -eq 0) { throw "未找到源文件：$srcDir" }

function Invoke-Csc {
    param([string]$Target, [string]$OutPath)

    $cscArgs = @(
        '/nologo'
        ('/target:' + $Target)
        '/platform:x64'
        '/codepage:65001'
        '/optimize+'
        '/debug-'
        ('/out:' + $OutPath)
    )

    foreach ($name in @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Management.dll', 'System.ServiceProcess.dll')) {
        $path = Join-Path $frameworkDir $name
        if (-not (Test-Path $path)) { throw "缺少引用程序集：$path" }
        $cscArgs += ('/r:' + $path)
    }

    if ($Target -eq 'exe') { $cscArgs += '/define:CONSOLE_BUILD' }
    if (Test-Path $iconPath) { $cscArgs += ('/win32icon:' + $iconPath) }
    if (Test-Path $manifest) { $cscArgs += ('/win32manifest:' + $manifest) }
    $cscArgs += $sources

    Write-Host ('  csc /target:' + $Target + ' -> ' + (Split-Path -Leaf $OutPath))
    & $csc $cscArgs
    if ($LASTEXITCODE -ne 0) { throw ('编译失败（/target:' + $Target + '），退出码 ' + $LASTEXITCODE) }
}

Write-Host ('源文件 ' + $sources.Count + ' 个：' + (($sources | ForEach-Object { Split-Path -Leaf $_ }) -join ', '))
Invoke-Csc -Target 'winexe' -OutPath $guiExe
Invoke-Csc -Target 'exe'    -OutPath $cliExe

foreach ($path in @($guiExe, $cliExe)) {
    $info = Get-Item $path
    Write-Host ('构建成功：' + $info.Name + '  ' + [math]::Round($info.Length / 1KB, 1) + ' KB  ' + $info.FullName)
}

if ($Run) { & $cliExe help }
