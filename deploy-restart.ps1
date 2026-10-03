# 构建 TeacherNotifier 并部署到 ClassIsland 插件目录，然后重启 ClassIsland。
#
# 用法：
#   pwsh -File .\deploy-restart.ps1                 # 构建 + 部署 + 重启
#   pwsh -File .\deploy-restart.ps1 -SkipBuild      # 只部署已有构建产物
#   pwsh -File .\deploy-restart.ps1 -NoRestart      # 只构建部署，不重启
#
# 路径可用参数覆盖，例如：
#   pwsh -File .\deploy-restart.ps1 -ClassIslandDir 'D:\ClassIsland'

[CmdletBinding()]
param(
    # ClassIsland 安装根目录（含 ClassIsland.exe 与 data 目录）
    [string]$ClassIslandDir = 'C:\Users\jingh\Downloads\ClassIsland_app_windows_x64_selfContained_folder',

    # 插件在 ClassIsland 中的目录名（= 插件 id）
    [string]$PluginId = 'com.jingh.teacher-notifier',

    # 跳过 dotnet build
    [switch]$SkipBuild,

    # 部署后不重启 ClassIsland
    [switch]$NoRestart
)

$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$buildOut = Join-Path $repoRoot 'bin\Release\net8.0-windows'
$pluginDir = Join-Path $ClassIslandDir "data\Plugins\$PluginId"
$classIslandExe = Join-Path $ClassIslandDir 'ClassIsland.exe'

function Write-Step([string]$text) {
    Write-Host "==> $text" -ForegroundColor Cyan
}

# ---------- 校验环境 ----------
if (-not (Test-Path $ClassIslandDir)) {
    throw "找不到 ClassIsland 目录：$ClassIslandDir（用 -ClassIslandDir 参数指定）"
}
if (-not (Test-Path $classIslandExe)) {
    throw "目录里没有 ClassIsland.exe：$classIslandExe"
}

# ---------- 构建 ----------
if (-not $SkipBuild) {
    Write-Step '构建（Release）'

    # 优先用 PATH 里的 dotnet；只有用户级 SDK 时可用 DOTNET_ROOT 指定
    $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
    if (-not $dotnet -and $env:DOTNET_ROOT) {
        $dotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
    }
    if (-not $dotnet -or -not (Test-Path $dotnet)) {
        throw '找不到 dotnet。请安装 .NET 8 SDK，或设置 DOTNET_ROOT 环境变量。'
    }

    Push-Location $repoRoot
    try {
        & $dotnet build -c Release -v minimal
        if ($LASTEXITCODE -ne 0) { throw "构建失败（退出码 $LASTEXITCODE）" }
    }
    finally {
        Pop-Location
    }
}

if (-not (Test-Path $buildOut)) {
    throw "找不到构建产物：$buildOut（先不加 -SkipBuild 跑一次）"
}

# ---------- 关闭 ClassIsland ----------
$running = @(Get-Process -Name 'ClassIsland.Desktop', 'ClassIsland' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Step '关闭 ClassIsland'
    # 先尝试正常关闭主窗口，避免直接杀进程留下脏状态
    foreach ($p in $running) {
        try { $p.CloseMainWindow() | Out-Null } catch { }
    }
    Start-Sleep -Seconds 3

    foreach ($p in @(Get-Process -Name 'ClassIsland.Desktop', 'ClassIsland' -ErrorAction SilentlyContinue)) {
        Write-Host "    强制结束 PID $($p.Id) ($($p.ProcessName))"
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Seconds 2
}

# ---------- 部署 ----------
Write-Step "部署到 $pluginDir"
New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null

# 只复制运行时需要的文件，不复制 obj 等中间产物
$files = @(
    'TeacherNotifier.dll',
    'TeacherNotifier.deps.json',
    'TeacherNotifier.runtimeconfig.json',
    'TeacherNotifier.pdb',
    'manifest.yml',
    'README.md',
    'icon.png'
)
foreach ($f in $files) {
    $src = Join-Path $buildOut $f
    if (Test-Path $src) {
        Copy-Item $src $pluginDir -Force
        Write-Host "    $f"
    }
    else {
        Write-Warning "缺少文件：$f"
    }
}

$dll = Join-Path $pluginDir 'TeacherNotifier.dll'
Write-Host "    已部署版本：$((Get-Item $dll).VersionInfo.FileVersion)" -ForegroundColor Green

# ---------- 重启 ----------
if ($NoRestart) {
    Write-Step '已跳过重启（-NoRestart）'
}
else {
    Write-Step '启动 ClassIsland'
    Start-Process -FilePath $classIslandExe -WorkingDirectory $ClassIslandDir
    Write-Host '    已启动。' -ForegroundColor Green
    Write-Host "    日志目录：$(Join-Path $ClassIslandDir 'data\Logs')"
    Write-Host '    可在日志里搜索「老师消息通知」确认插件加载结果。'
}

Write-Step '完成'
