# Windows build helper — 走 vendor (Technosoftware) 必备的 Platform=x64 + CustomTestTarget=net8.0-windows
# 用法:
#   .\build.ps1                       # build Release
#   .\build.ps1 -Configuration Debug
#   .\build.ps1 -Target test          # build + 跑测试
#   .\build.ps1 -Target run           # build + 启动 Dc.App
#   .\build.ps1 -Target publish       # 自包含 win-x64 publish 到 build\publish
#   .\build.ps1 -Target installer     # publish + 跑 Inno Setup 6 出 .exe 到 build\installer
param(
    [string]$Configuration = "Debug",
    [ValidateSet("build", "test", "run", "clean", "publish", "installer")]
    [string]$Target = "build",
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
$props = @("-p:Platform=x64", "-p:CustomTestTarget=net8.0-windows")
$repoRoot = $PSScriptRoot
$publishDir = Join-Path $repoRoot "build\publish"
$installerDir = Join-Path $repoRoot "build\installer"

function Find-Iscc {
    # 标准 Inno Setup 6 安装路径；用户装别处可设环境变量 INNO_SETUP_DIR
    $candidates = @()
    if ($env:INNO_SETUP_DIR) { $candidates += (Join-Path $env:INNO_SETUP_DIR "ISCC.exe") }
    $candidates += "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    $candidates += "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
    foreach ($p in $candidates) {
        if ($p -and (Test-Path $p)) { return $p }
    }
    return $null
}

function Invoke-Publish {
    # installer 用 Release 自包含 + win-x64
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    dotnet publish "$repoRoot\src\Dc.App\Dc.App.csproj" `
        -c Release -r win-x64 --self-contained true `
        -p:Platform=x64 -p:CustomTestTarget=net8.0-windows `
        -p:Version=$Version `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $LASTEXITCODE" }
    Write-Host "[publish] -> $publishDir" -ForegroundColor Green
}

switch ($Target) {
    "clean" {
        dotnet clean Dc.sln --configuration $Configuration @props
        if (Test-Path "$repoRoot\build") { Remove-Item "$repoRoot\build" -Recurse -Force }
    }
    "build" {
        dotnet build Dc.sln --configuration $Configuration @props
    }
    "test" {
        dotnet build Dc.sln --configuration $Configuration @props
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        # 单元测试（fakes-based）
        dotnet test tests/Dc.Infrastructure.Tests --no-build --configuration $Configuration -p:Platform=x64
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        # 集成测试（真 socket / 内嵌 UA server）— 跨平台
        dotnet test tests/Dc.Integration.Tests --no-build --configuration $Configuration -p:Platform=x64
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        # 集成测试（COM）— 仅 Windows 真跑；其它 OS 上 dotnet test 会报 TFM 不兼容直接失败，所以加守护
        if ($IsWindows -or $PSVersionTable.PSVersion.Major -lt 6) {
            dotnet test tests/Dc.Integration.Tests.Com --no-build --configuration $Configuration -p:Platform=x64
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        } else {
            Write-Host "[skip] Dc.Integration.Tests.Com — 非 Windows" -ForegroundColor Yellow
        }
    }
    "run" {
        dotnet build src/Dc.App --configuration $Configuration @props
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        # 直接启 exe（不用 dotnet run，避免留 dotnet host 僵尸）；先杀残留实例，免得单实例 Mutex 挡住新窗口
        Get-Process Dc.App -ErrorAction SilentlyContinue | Stop-Process -Force
        $exe = Join-Path $repoRoot "src\Dc.App\bin\x64\$Configuration\net8.0-windows\Dc.App.exe"
        if (-not (Test-Path $exe)) {
            $exe = Join-Path $repoRoot "src\Dc.App\bin\$Configuration\net8.0-windows\Dc.App.exe"
        }
        if (-not (Test-Path $exe)) { throw "Dc.App.exe 未找到，先确认构建成功：$exe" }
        Write-Host "[run] launching $exe" -ForegroundColor Green
        Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe)
    }
    "publish" {
        Invoke-Publish
    }
    "installer" {
        $iscc = Find-Iscc
        if (-not $iscc) {
            Write-Error "ISCC.exe 未找到。装 Inno Setup 6: https://jrsoftware.org/isdl.php  或设环境变量 INNO_SETUP_DIR 指向其安装目录。"
            exit 1
        }
        Invoke-Publish
        if (-not (Test-Path $installerDir)) { New-Item -ItemType Directory -Path $installerDir | Out-Null }
        Write-Host "[installer] ISCC = $iscc" -ForegroundColor Cyan
        & $iscc "/DMyAppVersion=$Version" "$repoRoot\installer\Dc.iss"
        if ($LASTEXITCODE -ne 0) { throw "ISCC 编译失败: $LASTEXITCODE" }
        $outFile = Join-Path $installerDir "Dc-Setup-x64-$Version.exe"
        if (Test-Path $outFile) {
            $sz = [math]::Round((Get-Item $outFile).Length / 1MB, 1)
            Write-Host "[installer] -> $outFile  ($sz MB)" -ForegroundColor Green
        }
    }
}
