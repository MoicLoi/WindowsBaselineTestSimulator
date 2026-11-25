<#
    生成 GitHub Release 发布包（全部离线，只用系统自带组件）。

    产物（dist\）：
      WindowsBaselineTestSimulator-<ver>-win-x64\          解压即用的目录
        WindowsBaselineTestSimulator.exe                   托盘 + 服务宿主
        wbts.exe                                           命令行
        WindowsBaselineTestSimulator-<ver>-win-x64.msi     MSI 安装包
        README.md  CHANGELOG.md  LICENSE
        SHA256SUMS.txt
      WindowsBaselineTestSimulator-<ver>-win-x64.zip       上面目录的压缩包
      RELEASE-NOTES-v<ver>.md                              可直接贴进 GitHub Release 的说明

    用法：
        .\packaging\New-Release.ps1
        .\packaging\New-Release.ps1 -Version 1.0.1
#>
[CmdletBinding()]
param(
    [string]$Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'

$root  = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$out   = Join-Path $root 'out'
$dist  = Join-Path $root 'dist'
$stageName = "WindowsBaselineTestSimulator-$Version-win-x64"
$stage = Join-Path $dist $stageName
$zip   = Join-Path $dist ($stageName + '.zip')

Write-Host '=== 1/5 编译二进制 ==='
& (Join-Path $root 'build.ps1')
if ($LASTEXITCODE -ne 0) { throw 'build.ps1 失败' }

Write-Host ''
Write-Host '=== 2/5 生成 MSI ==='
& (Join-Path $root 'packaging\New-Msi.ps1') -Version $Version
if ($LASTEXITCODE -ne 0) { throw 'New-Msi.ps1 失败' }

Write-Host ''
Write-Host '=== 3/5 收集到 dist ==='
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

$gui = Join-Path $out 'WindowsBaselineTestSimulator.exe'
$cli = Join-Path $out 'wbts.exe'
$msi = Join-Path $out ("WindowsBaselineTestSimulator-$Version-win-x64.msi")

foreach ($f in @($gui, $cli, $msi)) {
    if (-not (Test-Path $f)) { throw "缺少 $f" }
    Copy-Item $f $stage -Force
}
foreach ($doc in @('README.md', 'CHANGELOG.md', 'LICENSE')) {
    $p = Join-Path $root $doc
    if (Test-Path $p) { Copy-Item $p $stage -Force }
}

Write-Host ''
Write-Host '=== 4/5 SHA256SUMS ==='
$sums = Join-Path $stage 'SHA256SUMS.txt'
$lines = @()
Get-ChildItem -Path $stage -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | Sort-Object Name | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $lines += ($hash + '  ' + $_.Name)
    '  {0}  {1}' -f $hash.Substring(0, 16), $_.Name
}
Set-Content -Path $sums -Value $lines -Encoding ASCII

Write-Host ''
Write-Host '=== 5/5 打 zip + 发布说明 ==='
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal

$notes = Join-Path $root (Join-Path 'release' ("v$Version.md"))
$notesOut = Join-Path $dist ("RELEASE-NOTES-v$Version.md")
if (Test-Path $notes) {
    $body = [IO.File]::ReadAllText($notes)
    $table = (($lines | ForEach-Object { '    ' + $_ }) -join [Environment]::NewLine)
    $body = $body + [Environment]::NewLine + '## SHA256' + [Environment]::NewLine + [Environment]::NewLine + '```' + [Environment]::NewLine + $table + [Environment]::NewLine + '```' + [Environment]::NewLine
    [IO.File]::WriteAllText($notesOut, $body, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host ('  发布说明：' + $notesOut)
} else {
    Write-Warning "未找到发布说明模板：$notes"
}

Write-Host ''
Write-Host '发布包已生成：'
Get-ChildItem -Path $dist -File | Sort-Object Name | ForEach-Object {
    '  {0,-58} {1,8} KB' -f $_.Name, [math]::Round($_.Length / 1KB, 1)
}
Write-Host ''
Write-Host ('上传目录：' + $dist)
Write-Host ('校验文件：' + $sums)
