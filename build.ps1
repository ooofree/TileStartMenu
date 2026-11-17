# 构建 TileStartMenu（默认 Release）
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 [-Config Release]
param(
    [string]$Config = "Release"
)

$ErrorHandlePreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

# 解析 dotnet：优先 PATH，其次标准安装位置（某些机器上 dotnet 不在 PATH）
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -First 1).Source
if (-not $dotnet) {
    foreach ($candidate in @(
        (Join-Path $env:ProgramFiles "dotnet\dotnet.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "dotnet\dotnet.exe"),
        (Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe")
    )) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) { $dotnet = $candidate; break }
    }
}
if (-not $dotnet) {
    throw "未找到 .NET SDK。请先安装 .NET 9 SDK（https://dotnet.microsoft.com/download/dotnet/9.0）"
}

& $dotnet build (Join-Path $root "TileStartMenu.sln") -c $Config
if ($LASTEXITCODE -ne 0) {
    throw "构建失败，退出码 $LASTEXITCODE"
}

$exe = Join-Path $root ("src\TileStartMenu.App\bin\{0}\net9.0-windows\TileStartMenu.App.exe" -f $Config)
Write-Output ""
Write-Output "可执行文件：$exe"
if (Test-Path -LiteralPath $exe) {
    Write-Output "大小：$([math]::Round((Get-Item -LiteralPath $exe).Length / 1KB, 1)) KB"
}
