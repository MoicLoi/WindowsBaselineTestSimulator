<#
    卸载 WindowsBaselineTestSimulator 服务（需要管理员）
    用法：
        powershell -ExecutionPolicy Bypass -File .\uninstall-service.ps1
        powershell -ExecutionPolicy Bypass -File .\uninstall-service.ps1 -KeepFiles -KeepData
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'WindowsBaselineTestSimulator'),
    [switch]$KeepFiles,
    [switch]$KeepData
)

$ErrorActionPreference = 'Stop'
$serviceName = 'WBTS'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host '需要管理员权限。请以管理员身份打开 PowerShell 后重新执行：' -ForegroundColor Yellow
    Write-Host ('  powershell -ExecutionPolicy Bypass -File "' + $PSCommandPath + '"')
    exit 1
}

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        Write-Host '停止服务…'
        Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 1
    }
    Write-Host '删除服务…'
    sc.exe delete $serviceName | Out-Null
} else {
    Write-Host '服务不存在，跳过。'
}

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (Get-ItemProperty -Path $runKey -Name 'WindowsBaselineTestSimulatorTray' -ErrorAction SilentlyContinue) {
    Remove-ItemProperty -Path $runKey -Name 'WindowsBaselineTestSimulatorTray' -ErrorAction SilentlyContinue
    Write-Host '已移除托盘登录启动项'
}

Get-Process BaselineSim -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

if (-not $KeepFiles -and (Test-Path $InstallDir)) {
    Remove-Item -Path $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host ('已删除程序目录：' + $InstallDir)
}

if (-not $KeepData) {
    $dataDir = Join-Path $env:ProgramData 'WindowsBaselineTestSimulator'
    if (Test-Path $dataDir) {
        Remove-Item -Path $dataDir -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host ('已删除状态目录：' + $dataDir)
    }
}

Write-Host '卸载完成。'
