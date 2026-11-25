<#
    安装 WindowsBaselineTestSimulator 为 Windows 服务（需要管理员）
    用法（管理员 PowerShell）：
        powershell -ExecutionPolicy Bypass -File .\install-service.ps1
        powershell -ExecutionPolicy Bypass -File .\install-service.ps1 -NoTray -NoStart
    说明：
        - 服务以 LocalSystem 身份运行，因此 TPM 这类需要提权才能读的项会被正确判定
        - 服务启动即冻结一次“启动测量”快照（等价于一次开机）
        - 同时（默认）把托盘程序加入当前用户的登录启动项
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'WindowsBaselineTestSimulator'),
    [switch]$NoTray,
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'

$serviceName = 'WBTS'
$serviceDisplay = 'Windows 基线保护（模拟器）'
$serviceDesc = '模拟 Windows 11 基线保护的 8 项开机检测：启动时冻结快照，并通过本地命名管道对外提供查询。仅供测试，不修改任何系统安全设置。'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host '需要管理员权限。请以管理员身份打开 PowerShell 后重新执行：' -ForegroundColor Yellow
    Write-Host ('  powershell -ExecutionPolicy Bypass -File "' + $PSCommandPath + '"')
    exit 1
}

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$srcGui = Join-Path $root 'out\WindowsBaselineTestSimulator.exe'
$srcCli = Join-Path $root 'out\wbts.exe'

if (-not (Test-Path $srcGui) -or -not (Test-Path $srcCli)) {
    throw '未找到 out\WindowsBaselineTestSimulator.exe 或 out\wbts.exe，请先运行 build.ps1'
}

Write-Host ('安装目录：' + $InstallDir)
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
Copy-Item $srcGui (Join-Path $InstallDir 'WindowsBaselineTestSimulator.exe') -Force
Copy-Item $srcCli (Join-Path $InstallDir 'wbts.exe') -Force

$exe = Join-Path $InstallDir 'WindowsBaselineTestSimulator.exe'
$binPath = '"' + $exe + '" service'

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host '检测到已存在的服务，先停止并删除…'
    if ($existing.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 1
    }
    sc.exe delete $serviceName | Out-Null
    Start-Sleep -Milliseconds 800
}

Write-Host '创建服务…'
New-Service -Name $serviceName -BinaryPathName $binPath -DisplayName $serviceDisplay -StartupType Automatic | Out-Null
sc.exe description $serviceName $serviceDesc | Out-Null

if (-not $NoStart) {
    Write-Host '启动服务…'
    Start-Service -Name $serviceName
}

if (-not $NoTray) {
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    New-Item -Path $runKey -Force | Out-Null
    Set-ItemProperty -Path $runKey -Name 'WindowsBaselineTestSimulatorTray' -Value ('"' + $exe + '" tray')
    Write-Host '已把托盘程序加入当前用户登录启动项（WindowsBaselineTestSimulatorTray）'
}

Start-Sleep -Seconds 2
Write-Host ''
Write-Host '服务状态：'
Get-Service -Name $serviceName | Select-Object Name, Status, StartType | Format-Table -AutoSize

Write-Host '当前判定（经由服务管道查询）：'
& (Join-Path $InstallDir 'wbts.exe') status
