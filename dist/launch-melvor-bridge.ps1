# Melvor Idle 2 桥启动器
# 用法: powershell -File launch-melvor-bridge.ps1 [-ExitGame]
# 原理: 设置 DOTNET_STARTUP_HOOKS 指向 MelvorBridge.dll 后启动游戏，
#        .NET 运行时会在游戏初始化前自动加载桥。不修改任何游戏文件。
param(
    [switch]$DryRun  # 只设置环境变量打印命令，不真正启动
)

$ErrorActionPreference = 'Stop'

# 游戏目录：环境变量 MELVOR2_GAME_DIR 优先，否则探测常见 Steam 库位置
$GameDir = $env:MELVOR2_GAME_DIR
if (-not $GameDir) {
    $candidates = @(
        "$env:ProgramFiles(x86)\Steam\steamapps\common\Melvor Idle 2",
        'D:\SteamLibrary\steamapps\common\Melvor Idle 2',
        'E:\SteamLibrary\steamapps\common\Melvor Idle 2'
    )
    $GameDir = $candidates | Where-Object { Test-Path (Join-Path $_ 'Melvor2.exe') } | Select-Object -First 1
}
$BridgeDll = Join-Path $PSScriptRoot 'MelvorBridge.dll'
$Port = if ($env:MELVOR_BRIDGE_PORT) { $env:MELVOR_BRIDGE_PORT } else { '27500' }

if (-not (Test-Path $BridgeDll)) { throw "找不到 $BridgeDll，请先 build。" }
if (-not (Test-Path (Join-Path $GameDir 'Melvor2.exe'))) { throw "找不到游戏 $GameDir\Melvor2.exe" }

# Steamworks 需要 Steam 客户端在线（IPC），没有就先拉起并等待
$steamProc = Get-Process steam -ErrorAction SilentlyContinue
if (-not $steamProc) {
    Write-Host '[bridge] Steam 未运行，正在启动 Steam...'
    Start-Process 'steam://open/games'
    $ok = $false
    foreach ($i in 1..30) {
        Start-Sleep -Seconds 3
        if ((Get-Process steam -ErrorAction SilentlyContinue) -and (Get-Process steamwebhelper -ErrorAction SilentlyContinue)) { $ok = $true; break }
    }
    if ($ok) { Start-Sleep -Seconds 10; Write-Host '[bridge] Steam 已就绪' }
    else { Write-Warning '[bridge] Steam 未能在 90 秒内就绪，继续尝试启动游戏（可能失败）' }
}

# steam_appid.txt 让直接启动的 exe 也能初始化 Steamworks（Steam 客户端需在线）
$appidFile = Join-Path $GameDir 'steam_appid.txt'
if (-not (Test-Path $appidFile)) {
    Set-Content -Path $appidFile -Value '3218350' -NoNewline
    Write-Host "[bridge] 已写入 $appidFile (3218350)"
}

# 启动前确保注入仍在（游戏更新后 Steam 会还原 GodotSharp.dll，这里自动重注入）
$InjectorDll = Join-Path $PSScriptRoot 'injector\Injector.dll'
$InjectorBridge = Join-Path $PSScriptRoot 'MelvorBridge.dll'
$GameSharp = Join-Path $GameDir 'data_GamesByMalcs.Melvor2_windows_x86_64\GodotSharp.dll'
if ((Test-Path $InjectorDll) -and (Test-Path $GameSharp)) {
    $status = (dotnet $InjectorDll status $GameSharp 2>$null) -join ' '
    if ($status -notmatch 'injected: True') {
        Write-Host '[bridge] 注入缺失（游戏可能更新过），重新注入...'
        dotnet $InjectorDll inject $GameSharp $InjectorBridge
        if ($LASTEXITCODE -ne 0) { throw '注入失败，请查看上方输出' }
    } else {
        Write-Host '[bridge] 注入完好'
    }
}

# 若游戏已在跑，直接提示（桥随进程注入，重启才生效）
$running = Get-Process -Name 'Melvor2' -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "[bridge] Melvor2 已在运行 (PID $($running.Id))。"
    try {
        $h = Invoke-RestMethod "http://127.0.0.1:$Port/health" -TimeoutSec 2
        Write-Host "[bridge] 桥已在线: $($h | ConvertTo-Json -Compress)"
    } catch {
        Write-Warning "[bridge] 端口 $Port 无响应——当前游戏进程没带桥。关闭游戏后用本脚本重启。"
    }
    exit 0
}

$env:DOTNET_STARTUP_HOOKS = $BridgeDll
$env:MELVOR_BRIDGE_PORT = $Port

if ($DryRun) {
    Write-Host "DOTNET_STARTUP_HOOKS=$env:DOTNET_STARTUP_HOOKS"
    Write-Host "start $GameDir\Melvor2.exe"
    exit 0
}

Write-Host "[bridge] 启动游戏（带桥）..."
Start-Process -FilePath (Join-Path $GameDir 'Melvor2.exe') -WorkingDirectory $GameDir

Write-Host "[bridge] 等待桥上线 (http://127.0.0.1:$Port/health) ..."
$ok = $false
foreach ($i in 1..60) {
    Start-Sleep -Seconds 2
    try {
        $h = Invoke-RestMethod "http://127.0.0.1:$Port/health" -TimeoutSec 2
        Write-Host "[bridge] 在线: $($h | ConvertTo-Json -Compress)"
        $ok = $true
        break
    } catch { Write-Host "  ...($i)" }
}
if (-not $ok) { Write-Warning "[bridge] 60 秒内未上线。查看 %APPDATA%\Games By Malcs\Melvor Idle 2\logs\bridge.log" }
