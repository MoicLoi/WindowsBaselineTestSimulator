<#
    用 Windows 自带组件（msi.dll 的 WindowsInstaller COM + makecab.exe）直接编排 MSI。
    不需要 WiX / Windows SDK / 联网。

    产物：out\WindowsBaselineTestSimulator.msi
      - 安装到 %ProgramFiles%\WindowsBaselineTestSimulator
      - 注册并启动服务 WindowsBaselineTestSimulator（LocalSystem、自动启动）
      - 开始菜单：打开面板 / 快速开关 / 卸载入口
      - 开机启动：Startup 快捷方式（托盘常驻）
      - 卸载时停止并删除服务

    用法：
        .\packaging\New-Msi.ps1
        .\packaging\New-Msi.ps1 -Version 1.0.1
#>
[CmdletBinding()]
param(
    [string]$Version = '1.0.0',
    [string]$OutFile
)

$ErrorActionPreference = 'Stop'

$root    = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$outDir  = Join-Path $root 'out'
$workDir = Join-Path $root 'packaging\build'
$icon    = Join-Path $root 'assets\baseline.ico'

if (-not $OutFile) { $OutFile = Join-Path $outDir ('WindowsBaselineTestSimulator-' + $Version + '-win-x64.msi') }

$gui = Join-Path $outDir 'WindowsBaselineTestSimulator.exe'
$cli = Join-Path $outDir 'wbts.exe'
foreach ($f in @($gui, $cli)) { if (-not (Test-Path $f)) { throw "缺少 $f，请先运行 build.ps1" } }

if (Test-Path $workDir) { Remove-Item $workDir -Recurse -Force }
New-Item -ItemType Directory -Path $workDir -Force | Out-Null

$makecab = Join-Path $env:WINDIR 'System32\makecab.exe'
if (-not (Test-Path $makecab)) { throw '找不到 makecab.exe' }

# ---------------------------------------------------------------- 1. 生成 cab

$files = @(
    [pscustomobject]@{ Key = 'File_BaselineSim'; Source = $gui; Name = 'WindowsBaselineTestSimulator.exe'; Seq = 1 }
    [pscustomobject]@{ Key = 'File_bls';         Source = $cli; Name = 'wbts.exe';         Seq = 2 }
)

$ddf = Join-Path $workDir 'files.ddf'
$lines = @(
    '.Set CabinetNameTemplate=app.cab'
    ('.Set DiskDirectory1=' + $workDir)
    '.Set Cabinet=on'
    '.Set Compress=on'
    '.Set CompressionType=MSZIP'
    '.Set MaxDiskSize=0'
)
foreach ($f in $files) { $lines += ('"' + $f.Source + '" ' + $f.Key) }
Set-Content -Path $ddf -Value $lines -Encoding ASCII

Push-Location $workDir
try {
    & $makecab /f $ddf | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "makecab 失败（退出码 $LASTEXITCODE）" }
} finally { Pop-Location }

$cabPath = Join-Path $workDir 'app.cab'
if (-not (Test-Path $cabPath)) { throw "未生成 app.cab：$cabPath" }
Write-Host ('cab: ' + [math]::Round((Get-Item $cabPath).Length / 1KB, 1) + ' KB')

# ---------------------------------------------------------------- 2. 建立数据库

if (Test-Path $OutFile) { Remove-Item $OutFile -Force }

$installer = New-Object -ComObject WindowsInstaller.Installer
$db = $installer.OpenDatabase($OutFile, 3)   # 3 = msiOpenDatabaseModeCreate

function Invoke-MsiSql {
    param([string]$Sql)
    $view = $db.OpenView($Sql)
    try { $view.Execute() } finally { $view.Close() }
}

function Add-MsiStreamRow {
    param([string]$Table, [string]$KeyValue, [string]$StreamPath)
    $view = $db.OpenView("SELECT ``Name``, ``Data`` FROM ``$Table``")
    try {
        $view.Execute()
        $record = $installer.CreateRecord(2)
        $record.StringData(1) = $KeyValue
        $record.SetStream(2, $StreamPath)
        $view.Modify(1, $record)   # 1 = msiViewModifyInsert
    } finally { $view.Close() }
}

# ---------------------------------------------------------------- 3. 建表
# 注意：MSI 字符串类型上限是 CHAR(255)，写 256 会直接报 OpenView,Sql；
#       _Streams / _Tables / _Columns 由引擎自动创建，不要再 CREATE。

$productCode = '{' + ([guid]::NewGuid().ToString().ToUpper()) + '}'
$upgradeCode = '{8F3C1A78-5B2E-4B4E-9E0B-6C1D2A7F4C11}'
$componentId = '{' + ([guid]::NewGuid().ToString().ToUpper()) + '}'

$schema = @(
    'CREATE TABLE `Property` (`Property` CHAR(72) NOT NULL, `Value` CHAR(0) LOCALIZABLE PRIMARY KEY `Property`)'
    'CREATE TABLE `Directory` (`Directory` CHAR(72) NOT NULL, `Directory_Parent` CHAR(72), `DefaultDir` CHAR(255) NOT NULL LOCALIZABLE PRIMARY KEY `Directory`)'
    'CREATE TABLE `Component` (`Component` CHAR(72) NOT NULL, `ComponentId` CHAR(38), `Directory_` CHAR(72) NOT NULL, `Attributes` SHORT NOT NULL, `Condition` CHAR(255), `KeyPath` CHAR(72) PRIMARY KEY `Component`)'
    'CREATE TABLE `Feature` (`Feature` CHAR(38) NOT NULL, `Feature_Parent` CHAR(38), `Title` CHAR(64), `Description` CHAR(255), `Display` SHORT, `Level` SHORT NOT NULL, `Directory_` CHAR(72), `Attributes` SHORT NOT NULL PRIMARY KEY `Feature`)'
    'CREATE TABLE `FeatureComponents` (`Feature_` CHAR(38) NOT NULL, `Component_` CHAR(72) NOT NULL PRIMARY KEY `Feature_`, `Component_`)'
    'CREATE TABLE `File` (`File` CHAR(72) NOT NULL, `Component_` CHAR(72) NOT NULL, `FileName` CHAR(255) NOT NULL, `FileSize` LONG NOT NULL, `Version` CHAR(72), `Language` CHAR(20), `Attributes` SHORT, `Sequence` LONG NOT NULL PRIMARY KEY `File`)'
    'CREATE TABLE `Media` (`DiskId` SHORT NOT NULL, `LastSequence` SHORT NOT NULL, `DiskPrompt` CHAR(64), `Cabinet` CHAR(255), `VolumeLabel` CHAR(32), `Source` CHAR(72) PRIMARY KEY `DiskId`)'
    'CREATE TABLE `InstallExecuteSequence` (`Action` CHAR(72) NOT NULL, `Condition` CHAR(255), `Sequence` SHORT PRIMARY KEY `Action`)'
    'CREATE TABLE `AdminExecuteSequence` (`Action` CHAR(72) NOT NULL, `Condition` CHAR(255), `Sequence` SHORT PRIMARY KEY `Action`)'
    'CREATE TABLE `ServiceInstall` (`ServiceInstall` CHAR(72) NOT NULL, `Name` CHAR(255) NOT NULL, `DisplayName` CHAR(255), `ServiceType` LONG NOT NULL, `StartType` LONG NOT NULL, `ErrorControl` LONG NOT NULL, `StartName` CHAR(64), `Password` CHAR(255), `Arguments` CHAR(255), `Component_` CHAR(72) NOT NULL, `Description` CHAR(255) PRIMARY KEY `ServiceInstall`)'
    'CREATE TABLE `ServiceControl` (`ServiceControl` CHAR(72) NOT NULL, `Name` CHAR(255) NOT NULL, `Event` SHORT NOT NULL, `Arguments` CHAR(255), `Wait` SHORT, `Component_` CHAR(72) NOT NULL PRIMARY KEY `ServiceControl`)'
    'CREATE TABLE `Shortcut` (`Shortcut` CHAR(72) NOT NULL, `Directory_` CHAR(72) NOT NULL, `Name` CHAR(128) NOT NULL, `Component_` CHAR(72) NOT NULL, `Target` CHAR(72) NOT NULL, `Arguments` CHAR(255), `Hotkey` SHORT, `Icon_` CHAR(72), `IconIndex` SHORT, `ShowCmd` SHORT, `WkDir` CHAR(72) PRIMARY KEY `Shortcut`)'
    'CREATE TABLE `Icon` (`Name` CHAR(72) NOT NULL, `Data` OBJECT NOT NULL PRIMARY KEY `Name`)'
    'CREATE TABLE `Upgrade` (`UpgradeCode` CHAR(38) NOT NULL, `VersionMin` CHAR(20), `VersionMax` CHAR(20), `Language` LONG, `Attributes` LONG, `Remove` CHAR(255), `ActionProperty` CHAR(72) PRIMARY KEY `UpgradeCode`)'
    'CREATE TABLE `LaunchCondition` (`Condition` CHAR(255) NOT NULL, `Description` CHAR(255) NOT NULL PRIMARY KEY `Condition`)'
)

foreach ($sql in $schema) { Invoke-MsiSql $sql }
Write-Host '表结构已创建'

# ---------------------------------------------------------------- 4. 写入数据

$props = [ordered]@{
    'ProductName'            = 'WindowsBaselineTestSimulator'
    'ProductVersion'         = $Version
    'ProductCode'            = $productCode
    'UpgradeCode'            = $upgradeCode
    'Manufacturer'           = 'WindowsBaselineTestSimulator'
    'ProductLanguage'        = '2052'
    'ProductIcon'            = 'AppIcon'
    'ARPPRODUCTICON'         = 'AppIcon'
    'ARPNOMODIFY'            = '1'
    'ALLUSERS'               = '1'
    'DiskPrompt'             = '[1]'
    'SecureCustomProperties' = 'OLDPRODUCTS'
}
foreach ($key in $props.Keys) {
    $value = [string]$props[$key]
    $value = $value.Replace("'", "''")
    Invoke-MsiSql ("INSERT INTO ``Property`` (``Property``, ``Value``) VALUES ('$key', '$value')")
}

$directories = @(
    [pscustomobject]@{ Dir = 'TARGETDIR';          Parent = $null;               Default = 'SourceDir' }
    [pscustomobject]@{ Dir = 'ProgramFilesFolder'; Parent = 'TARGETDIR';         Default = '.' }
    [pscustomobject]@{ Dir = 'INSTALLDIR';         Parent = 'ProgramFilesFolder'; Default = 'WindowsBaselineTestSimulator' }
    [pscustomobject]@{ Dir = 'ProgramMenuFolder';  Parent = 'TARGETDIR';         Default = '.' }
    [pscustomobject]@{ Dir = 'MENUDIR';            Parent = 'ProgramMenuFolder'; Default = 'WindowsBaselineTestSimulator' }
    [pscustomobject]@{ Dir = 'StartupFolder';      Parent = 'TARGETDIR';         Default = '.' }
)
foreach ($d in $directories) {
    $parent = if ($null -eq $d.Parent) { 'NULL' } else { "'" + $d.Parent + "'" }
    $default = $d.Default.Replace("'", "''")
    Invoke-MsiSql ("INSERT INTO ``Directory`` (``Directory``, ``Directory_Parent``, ``DefaultDir``) VALUES ('" + $d.Dir + "', $parent, '$default')")
}

Invoke-MsiSql ("INSERT INTO ``Component`` (``Component``, ``ComponentId``, ``Directory_``, ``Attributes``, ``Condition``, ``KeyPath``) VALUES ('Comp_Main', '$componentId', 'INSTALLDIR', 0, NULL, 'File_BaselineSim')")
Invoke-MsiSql "INSERT INTO ``Feature`` (``Feature``, ``Feature_Parent``, ``Title``, ``Description``, ``Display``, ``Level``, ``Directory_``, ``Attributes``) VALUES ('MainFeature', NULL, 'WindowsBaselineTestSimulator', '模拟 Windows 11 基线保护 8 项检测的服务、托盘快速开关与命令行', 1, 1, 'INSTALLDIR', 0)"
Invoke-MsiSql "INSERT INTO ``FeatureComponents`` (``Feature_``, ``Component_``) VALUES ('MainFeature', 'Comp_Main')"

foreach ($f in $files) {
    $size = (Get-Item $f.Source).Length
    Invoke-MsiSql ("INSERT INTO ``File`` (``File``, ``Component_``, ``FileName``, ``FileSize``, ``Version``, ``Language``, ``Attributes``, ``Sequence``) VALUES ('" + $f.Key + "', 'Comp_Main', '" + $f.Name + "', $size, NULL, NULL, 0, " + $f.Seq + ")")
}
$lastSeq = ($files | Measure-Object -Property Seq -Maximum).Maximum
Invoke-MsiSql ("INSERT INTO ``Media`` (``DiskId``, ``LastSequence``, ``DiskPrompt``, ``Cabinet``, ``VolumeLabel``, ``Source``) VALUES (1, $lastSeq, 'WindowsBaselineTestSimulator', '#app.cab', NULL, NULL)")

# 服务：ServiceType 16=own process，StartType 2=auto，ErrorControl 1=normal
Invoke-MsiSql "INSERT INTO ``ServiceInstall`` (``ServiceInstall``, ``Name``, ``DisplayName``, ``ServiceType``, ``StartType``, ``ErrorControl``, ``StartName``, ``Password``, ``Arguments``, ``Component_``, ``Description``) VALUES ('SvcInstall', 'WBTS', 'WindowsBaselineTestSimulator（基线保护模拟器）', 16, 2, 1, 'LocalSystem', NULL, 'service', 'Comp_Main', '模拟 Windows 11 基线保护的 8 项开机检测：启动时冻结快照，并通过本地命名管道对外提供查询。仅供测试，不修改任何系统安全设置。')"
Invoke-MsiSql "INSERT INTO ``ServiceControl`` (``ServiceControl``, ``Name``, ``Event``, ``Arguments``, ``Wait``, ``Component_``) VALUES ('SvcStart', 'WBTS', 1, NULL, 1, 'Comp_Main')"
Invoke-MsiSql "INSERT INTO ``ServiceControl`` (``ServiceControl``, ``Name``, ``Event``, ``Arguments``, ``Wait``, ``Component_``) VALUES ('SvcStop', 'WBTS', 2, NULL, 1, 'Comp_Main')"

$shortcuts = @(
    [pscustomobject]@{ Key = 'SC_Panel';   Dir = 'MENUDIR';       Name = '打开面板.lnk';              Args = 'panel' }
    [pscustomobject]@{ Key = 'SC_Toggle';  Dir = 'MENUDIR';       Name = '快速开关（检测 开-关）.lnk'; Args = 'toggle' }
    [pscustomobject]@{ Key = 'SC_Startup'; Dir = 'StartupFolder'; Name = '基线检测托盘.lnk';           Args = 'tray' }
)
foreach ($s in $shortcuts) {
    $name = $s.Name.Replace("'", "''")
    Invoke-MsiSql ("INSERT INTO ``Shortcut`` (``Shortcut``, ``Directory_``, ``Name``, ``Component_``, ``Target``, ``Arguments``, ``Hotkey``, ``Icon_``, ``IconIndex``, ``ShowCmd``, ``WkDir``) VALUES ('" + $s.Key + "', '" + $s.Dir + "', '$name', 'Comp_Main', 'File_BaselineSim', '" + $s.Args + "', NULL, 'AppIcon', 0, 1, 'INSTALLDIR')")
}

Invoke-MsiSql ("INSERT INTO ``Upgrade`` (``UpgradeCode``, ``VersionMin``, ``VersionMax``, ``Language``, ``Attributes``, ``Remove``, ``ActionProperty``) VALUES ('$upgradeCode', '0.0.0', NULL, 0, 257, 'OLDPRODUCTS', 'OLDPRODUCTS')")
Invoke-MsiSql "INSERT INTO ``LaunchCondition`` (``Condition``, ``Description``) VALUES ('VersionNT >= 1000', '需要 Windows 10 或 Windows 11。')"

$installSeq = @(
    [pscustomobject]@{ Action = 'FindRelatedProducts';    Cond = $null;           Seq = 25 }
    [pscustomobject]@{ Action = 'LaunchConditions';       Cond = $null;           Seq = 100 }
    [pscustomobject]@{ Action = 'CostInitialize';         Cond = $null;           Seq = 800 }
    [pscustomobject]@{ Action = 'FileCost';               Cond = $null;           Seq = 900 }
    [pscustomobject]@{ Action = 'CostFinalize';           Cond = $null;           Seq = 1000 }
    [pscustomobject]@{ Action = 'MigrateFeatureStates';   Cond = $null;           Seq = 1200 }
    [pscustomobject]@{ Action = 'InstallValidate';        Cond = $null;           Seq = 1400 }
    [pscustomobject]@{ Action = 'RemoveExistingProducts'; Cond = $null;           Seq = 1450 }
    [pscustomobject]@{ Action = 'InstallInitialize';      Cond = $null;           Seq = 1500 }
    [pscustomobject]@{ Action = 'ProcessComponents';      Cond = $null;           Seq = 1600 }
    [pscustomobject]@{ Action = 'UnpublishFeatures';      Cond = $null;           Seq = 1800 }
    [pscustomobject]@{ Action = 'RemoveShortcuts';        Cond = 'REMOVE~="ALL"'; Seq = 2000 }
    [pscustomobject]@{ Action = 'StopServices';           Cond = 'REMOVE~="ALL"'; Seq = 2050 }
    [pscustomobject]@{ Action = 'DeleteServices';         Cond = 'REMOVE~="ALL"'; Seq = 2100 }
    [pscustomobject]@{ Action = 'RemoveFiles';            Cond = 'REMOVE~="ALL"'; Seq = 2500 }
    [pscustomobject]@{ Action = 'InstallFiles';           Cond = $null;           Seq = 4000 }
    [pscustomobject]@{ Action = 'CreateShortcuts';        Cond = $null;           Seq = 4500 }
    [pscustomobject]@{ Action = 'StartServices';          Cond = $null;           Seq = 5600 }
    [pscustomobject]@{ Action = 'RegisterUser';           Cond = $null;           Seq = 6000 }
    [pscustomobject]@{ Action = 'RegisterProduct';        Cond = $null;           Seq = 6100 }
    [pscustomobject]@{ Action = 'PublishFeatures';        Cond = $null;           Seq = 6300 }
    [pscustomobject]@{ Action = 'PublishProduct';         Cond = $null;           Seq = 6400 }
    [pscustomobject]@{ Action = 'InstallFinalize';        Cond = $null;           Seq = 6600 }
)
foreach ($s in $installSeq) {
    $cond = if ($null -eq $s.Cond) { 'NULL' } else { "'" + $s.Cond.Replace("'", "''") + "'" }
    Invoke-MsiSql ("INSERT INTO ``InstallExecuteSequence`` (``Action``, ``Condition``, ``Sequence``) VALUES ('" + $s.Action + "', $cond, " + $s.Seq + ")")
}

$adminSeq = @(
    [pscustomobject]@{ Action = 'CostInitialize';    Seq = 800 }
    [pscustomobject]@{ Action = 'FileCost';          Seq = 900 }
    [pscustomobject]@{ Action = 'CostFinalize';      Seq = 1000 }
    [pscustomobject]@{ Action = 'InstallValidate';   Seq = 1400 }
    [pscustomobject]@{ Action = 'InstallInitialize'; Seq = 1500 }
    [pscustomobject]@{ Action = 'InstallFiles';      Seq = 4000 }
    [pscustomobject]@{ Action = 'InstallFinalize';   Seq = 6600 }
)
foreach ($s in $adminSeq) {
    Invoke-MsiSql ("INSERT INTO ``AdminExecuteSequence`` (``Action``, ``Condition``, ``Sequence``) VALUES ('" + $s.Action + "', NULL, " + $s.Seq + ")")
}

Add-MsiStreamRow -Table '_Streams' -KeyValue 'app.cab' -StreamPath $cabPath
if (Test-Path $icon) { Add-MsiStreamRow -Table 'Icon' -KeyValue 'AppIcon' -StreamPath $icon }

# ---------------------------------------------------------------- 5. 摘要信息 + 提交

# PID 9 必须是「包代码 GUID」，写成版本号会让 msiexec 直接返回 87
$packageCode = '{' + ([guid]::NewGuid().ToString().ToUpper()) + '}'
$summary = $db.SummaryInformation(20)
try { $summary.Property(1) = 936 } catch { }
$summary.Property(2)  = 'Installation Database'
$summary.Property(3)  = 'WindowsBaselineTestSimulator'
$summary.Property(4)  = 'WindowsBaselineTestSimulator'
$summary.Property(5)  = 'Installer'
$summary.Property(6)  = '模拟 Windows 11 基线保护 8 项检测的服务与托盘快速开关；仅供测试，不修改任何系统安全设置。'
$summary.Property(7)  = 'x64;2052'
$summary.Property(9)  = $packageCode
$summary.Property(14) = 200
$summary.Property(15) = 2
$summary.Property(19) = 2
$summary.Persist()

$db.Commit()
$db = $null
[System.Runtime.InteropServices.Marshal]::ReleaseComObject($installer) | Out-Null

Write-Host ('MSI: ' + $OutFile + '  ' + [math]::Round((Get-Item $OutFile).Length / 1KB, 1) + ' KB')
Write-Host ('ProductCode: ' + $productCode)
Write-Host ('UpgradeCode: ' + $upgradeCode)
Write-Host ('PackageCode: ' + $packageCode)
