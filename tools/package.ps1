<#
.SYNOPSIS
  构建 CampusLink 发布产物：完整目录 + 更新用 zip + 安装程序。

.DESCRIPTION
  - 更新（自动更新）用：CampusLink-<版本>-win.zip（整目录，安装目录内直接覆盖）
  - 首次安装用：CampusLink-Setup-<版本>.exe（Inno Setup，写注册表/开始菜单/卸载器）

  产物输出到仓库根目录 dist\。

.PARAMETER Configuration
  默认 Release。

.PARAMETER SkipInstaller
  只出 zip，不编译安装程序。

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\package.ps1
#>
param(
    [string]$Configuration = 'Release',
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectDir = Join-Path $repoRoot 'CampusLink'
$projectFile = Join-Path $projectDir 'CampusLink.csproj'
$distDir = Join-Path $repoRoot 'dist'
$installerScript = Join-Path $repoRoot 'installer\CampusLink.iss'

function Find-MSBuild {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $path = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' 2>$null | Select-Object -First 1
        if ($path -and (Test-Path $path)) { return $path }
    }
    $candidates = @(
        'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe',
        'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe',
        'C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe',
        'C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe'
    )
    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) { return $candidate }
    }
    throw '找不到 MSBuild.exe，请安装 Visual Studio 的 .NET 桌面开发工作负载'
}

function Find-ISCC {
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )
    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) { return $candidate }
    }
    return $null
}

Write-Host '==> 编译 Release' -ForegroundColor Cyan
$msbuild = Find-MSBuild
& $msbuild $projectFile -t:Restore,Build -p:Configuration=$Configuration -v:q -nologo
if ($LASTEXITCODE -ne 0) { throw "MSBuild 失败（exit $LASTEXITCODE）" }

$builtExe = Join-Path $projectDir "bin\$Configuration\CampusLink.exe"
if (-not (Test-Path $builtExe)) { throw "没有找到编译产物：$builtExe" }

$version = (Get-Item $builtExe).VersionInfo.FileVersion
$version = ($version -split '\.')[0..2] -join '.'
Write-Host "==> 版本 $version" -ForegroundColor Cyan

$stageDir = Join-Path $distDir "CampusLink-$version"
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null

Write-Host '==> 收集发布文件（排除 pdb）' -ForegroundColor Cyan
Get-ChildItem (Join-Path $projectDir "bin\$Configuration") -File |
    Where-Object { $_.Extension -ne '.pdb' } |
    ForEach-Object { Copy-Item $_.FullName $stageDir -Force }

Get-ChildItem $stageDir -File | Select-Object Name, Length | Format-Table -AutoSize

$zipPath = Join-Path $distDir "CampusLink-$version-win.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Write-Host '==> 生成更新用 zip（整目录）' -ForegroundColor Cyan
Compress-Archive -Path (Join-Path $stageDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
Write-Host ("    {0} ({1:N0} KB)" -f $zipPath, ((Get-Item $zipPath).Length / 1KB))

if (-not $SkipInstaller) {
    $iscc = Find-ISCC
    if (-not $iscc) {
        Write-Warning '没有找到 Inno Setup（ISCC.exe），跳过安装程序。安装后重跑即可：winget install JRSoftware.InnoSetup'
    } else {
        Write-Host '==> 编译安装程序（Inno Setup）' -ForegroundColor Cyan
        & $iscc "/DAppVersion=$version" "/DSourceDir=$stageDir" $installerScript | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Inno Setup 编译失败（exit $LASTEXITCODE）" }
    }
}

Write-Host ''
Write-Host '==> 产物：' -ForegroundColor Green
Get-ChildItem $distDir -File | Where-Object { $_.Extension -in '.zip', '.exe' } |
    Select-Object Name, @{ Name = 'Size'; Expression = { '{0:N0} KB' -f ($_.Length / 1KB) } } | Format-Table -AutoSize
