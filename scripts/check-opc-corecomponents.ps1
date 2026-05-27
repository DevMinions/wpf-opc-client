# 检查 OPC Foundation Core Components 是否已正确安装
# 用法（普通 PowerShell，不需要管理员）：
#   .\check-opc-corecomponents.ps1
# 兼容 Windows PowerShell 5.1。

$ErrorActionPreference = "Continue"

function Section { param([string]$t) Write-Host ""; Write-Host ("=" * 70) -ForegroundColor DarkCyan; Write-Host "  $t" -ForegroundColor Cyan; Write-Host ("=" * 70) -ForegroundColor DarkCyan }
function OK   { param([string]$m) Write-Host "  [OK] $m" -ForegroundColor Green }
function MISS { param([string]$m) Write-Host "  [--] $m" -ForegroundColor Yellow }
function BAD  { param([string]$m) Write-Host "  [!!] $m" -ForegroundColor Red }

Write-Host "OPC Core Components 安装检查 — $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"

# ──────────────────────────────────────────────────────────────────
# 1) OPCEnum 服务
# ──────────────────────────────────────────────────────────────────
Section "1) OPCEnum 服务"

$svc = Get-Service -Name 'OpcEnum' -ErrorAction SilentlyContinue
if (-not $svc) {
    BAD "OpcEnum 服务未注册"
} else {
    $ci = Get-CimInstance Win32_Service -Filter "Name='OpcEnum'" -ErrorAction SilentlyContinue
    OK "服务存在  状态=$($svc.Status)  启动模式=$($ci.StartMode)"
    Write-Host "       PathName : $($ci.PathName)"
    Write-Host "       StartName: $($ci.StartName)"
    # 解析 PathName 里的真实 exe 路径（去引号、去命令行参数）
    $exe = $ci.PathName
    if ($exe -match '^"([^"]+)"') { $exe = $Matches[1] }
    elseif ($exe -match '^(\S+)')  { $exe = $Matches[1] }
    if (Test-Path $exe) {
        $v = (Get-Item $exe).VersionInfo
        OK "EXE 存在  版本=$($v.FileVersion)  产品=$($v.ProductName)"
    } else {
        BAD "EXE 不存在：$exe"
        Write-Host "       这就是 Start-Service 报 '无法启动' 的原因" -ForegroundColor DarkYellow
    }
}

# ──────────────────────────────────────────────────────────────────
# 2) 关键二进制文件
# ──────────────────────────────────────────────────────────────────
Section "2) Core Components 二进制（SysWOW64 / System32）"

$files = @(
    'C:\Windows\SysWow64\OpcEnum.exe',
    'C:\Windows\System32\OpcEnum.exe',
    'C:\Windows\SysWow64\OPCCOMN_PS.dll',
    'C:\Windows\System32\OPCCOMN_PS.dll',
    'C:\Windows\SysWow64\OPC_AEPS.dll',
    'C:\Windows\System32\OPC_AEPS.dll',
    'C:\Windows\SysWow64\OpcProxy.dll',
    'C:\Windows\System32\OpcProxy.dll',
    'C:\Windows\SysWow64\OpcHda_PS.dll',
    'C:\Windows\System32\OpcHda_PS.dll'
)
foreach ($p in $files) {
    if (Test-Path $p) {
        $v = (Get-Item $p).VersionInfo
        $ver = $v.FileVersion
        if (-not $ver) { $ver = '(no version)' }
        OK "$p  v=$ver"
    } else {
        MISS "$p"
    }
}

# ──────────────────────────────────────────────────────────────────
# 3) 已安装的 OPC 程序条目（控制面板里看到的）
# ──────────────────────────────────────────────────────────────────
Section "3) 已安装的 OPC 程序"

$uninstallKeys = @(
    'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
    'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
)
$opcPrograms = Get-ItemProperty $uninstallKeys -ErrorAction SilentlyContinue |
    Where-Object { $_.DisplayName -match 'OPC' -and $_.DisplayName -notmatch 'OPC UA' } |
    Select-Object DisplayName, DisplayVersion, Publisher, InstallDate, InstallLocation

if (-not $opcPrograms) {
    MISS "没找到任何含 'OPC' 的卸载条目"
} else {
    $opcPrograms | Format-Table DisplayName, DisplayVersion, Publisher, InstallDate -AutoSize
}

# ──────────────────────────────────────────────────────────────────
# 4) OPC Foundation 注册表锚点
# ──────────────────────────────────────────────────────────────────
Section "4) OPC Foundation 注册表"

foreach ($root in 'HKLM:\SOFTWARE\Wow6432Node\OPC Foundation', 'HKLM:\SOFTWARE\OPC Foundation') {
    if (Test-Path $root) {
        OK $root
        Get-ChildItem $root -ErrorAction SilentlyContinue | ForEach-Object {
            Write-Host "       \\$($_.PSChildName)"
        }
    } else {
        MISS $root
    }
}

# ──────────────────────────────────────────────────────────────────
# 5) OPCEnum CLSID 注册（COM 激活角度）
# ──────────────────────────────────────────────────────────────────
Section "5) OPCEnum CLSID 注册位置"

$opcenumClsid = '{13486D51-4821-11D2-A494-3CB306C10000}'
$views = @(
    @{ Name='64-bit hive';  Path="Registry::HKEY_CLASSES_ROOT\CLSID\$opcenumClsid" },
    @{ Name='Wow6432Node';  Path="Registry::HKEY_CLASSES_ROOT\Wow6432Node\CLSID\$opcenumClsid" }
)
foreach ($v in $views) {
    if (Test-Path $v.Path) {
        $ls = (Get-Item (Join-Path $v.Path 'LocalServer32') -ErrorAction SilentlyContinue)
        if ($ls) { OK "$($v.Name)  LocalServer32 = $($ls.GetValue(''))" }
        else     { OK "$($v.Name)  (无 LocalServer32 子项)" }
    } else {
        MISS "$($v.Name)  $($v.Path)"
    }
}

# ──────────────────────────────────────────────────────────────────
# 结论
# ──────────────────────────────────────────────────────────────────
Section "结论"

$exeOk = (Test-Path 'C:\Windows\SysWow64\OpcEnum.exe') -or (Test-Path 'C:\Windows\System32\OpcEnum.exe')
$svcOk = $null -ne $svc

if ($exeOk -and $svcOk) {
    Write-Host "  Core Components 看起来完整 — 扫描功能应该可用" -ForegroundColor Green
    Write-Host "  如果 Start-Service OpcEnum 还报错，看事件查看器 'System' 日志找 DCOM/SCM 错误" -ForegroundColor DarkGray
} elseif ($svcOk -and -not $exeOk) {
    Write-Host "  服务条目在但 EXE 不存在 — 装包没全部部署，或被清理工具误删" -ForegroundColor Yellow
    Write-Host "  修法 A：卸载现有 OPC 软件 → 重装 Core Components Redistributable" -ForegroundColor DarkYellow
    Write-Host "  修法 B：sc.exe delete OpcEnum （管理员）→ 再装 Core Components" -ForegroundColor DarkYellow
    Write-Host "  修法 C：用 CLSID 直连兜底（commit 2d4fead），不依赖 OPCEnum" -ForegroundColor DarkYellow
} elseif (-not $svcOk -and -not $exeOk) {
    Write-Host "  Core Components 完全没装 — 装 OPC Foundation 官方 Core Components Redistributable" -ForegroundColor Yellow
} else {
    Write-Host "  EXE 在但服务没注册 — 罕见，跑 'OpcEnum.exe /Service' 注册" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "脚本结束 — $(Get-Date -Format 'HH:mm:ss')"
