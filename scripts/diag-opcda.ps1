# OPC DA COM 注册诊断脚本（兼容 Windows PowerShell 5.1 与 PowerShell 7+）
# 用途：排查 "Could not connect to server: 0x800401F3 (REGDB_E_CLASSNOTREG)" 之类错误
# 用法：
#   .\diag-opcda.ps1                                    # 默认查 SampleCompany.DaSample
#   .\diag-opcda.ps1 -ProgId "Matrikon.OPC.Simulation.1"
#   .\diag-opcda.ps1 -Save                              # 输出同时存到 diag-opcda.log
#   .\diag-opcda.ps1 -Exe "C:\path\to\server.exe"        # 指定 exe 路径核位宽
#
# 不需要管理员权限（只读注册表）。

param(
    [string]$ProgId = "SampleCompany.DaSample",
    [string]$Exe    = "$PSScriptRoot\..\vendor\ClassicClient\x86\DemoServer\OpcDaAeServer.exe",
    [switch]$Save
)

$ErrorActionPreference = "Continue"
$lines = New-Object System.Collections.ArrayList

function W {
    param([string]$msg, [ConsoleColor]$color = "Gray")
    Write-Host $msg -ForegroundColor $color
    [void]$lines.Add($msg)
}

function Section {
    param([string]$title)
    W ""
    W ("=" * 70) DarkCyan
    W ("  $title") Cyan
    W ("=" * 70) DarkCyan
}

function YesNo {
    param([bool]$cond, [string]$yes, [string]$no)
    if ($cond) { return $yes } else { return $no }
}

$osBits  = YesNo ([Environment]::Is64BitOperatingSystem) "64-bit" "32-bit"
$psBits  = YesNo ([Environment]::Is64BitProcess)         "x64"    "x86"

W "OPC DA 注册诊断 — $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
W "ProgID: $ProgId"
W "EXE   : $Exe"
W "OS    : $((Get-CimInstance Win32_OperatingSystem).Caption) ($osBits)"
W "PS    : $($PSVersionTable.PSVersion) ($psBits 进程)"

# ──────────────────────────────────────────────────────────────────
# 1) ProgID 是否注册（64-bit / Wow6432Node 双查）
# ──────────────────────────────────────────────────────────────────
Section "1) ProgID 注册位置"

$progid64 = Get-Item "Registry::HKEY_CLASSES_ROOT\$ProgId" -ErrorAction SilentlyContinue
$progid32 = Get-Item "Registry::HKEY_CLASSES_ROOT\Wow6432Node\$ProgId" -ErrorAction SilentlyContinue

if ($progid64) {
    $desc = $progid64.GetValue('')
    W "  [OK] 64-bit hive: HKCR\$ProgId  描述: $desc" Green
} else {
    W "  [--] 64-bit hive 未找到: HKCR\$ProgId" Yellow
}
if ($progid32) {
    $desc = $progid32.GetValue('')
    W "  [OK] Wow6432Node : HKCR\Wow6432Node\$ProgId  描述: $desc" Green
} else {
    W "  [--] Wow6432Node 未找到: HKCR\Wow6432Node\$ProgId" Yellow
}

# ──────────────────────────────────────────────────────────────────
# 2) ProgID -> CLSID
# ──────────────────────────────────────────────────────────────────
Section "2) ProgID -> CLSID"

$clsid = $null
if ($progid64) {
    $k = Get-Item "Registry::HKEY_CLASSES_ROOT\$ProgId\CLSID" -ErrorAction SilentlyContinue
    if ($k) { $clsid = $k.GetValue(''); W "  HKCR\$ProgId\CLSID = $clsid" }
}
if (-not $clsid -and $progid32) {
    $k = Get-Item "Registry::HKEY_CLASSES_ROOT\Wow6432Node\$ProgId\CLSID" -ErrorAction SilentlyContinue
    if ($k) { $clsid = $k.GetValue(''); W "  HKCR\Wow6432Node\$ProgId\CLSID = $clsid" }
}
if (-not $clsid) {
    W "  [!!] ProgID 下没有 CLSID 子项 -- ProgID 半残注册" Red
}

# ──────────────────────────────────────────────────────────────────
# 3) CLSID 详情：LocalServer32 / InprocServer32 / AppID
# ──────────────────────────────────────────────────────────────────
Section "3) CLSID 注册详情"

$appid = $null
function Dump-Clsid {
    param([string]$root, [string]$clsidVal)
    $base = "Registry::HKEY_CLASSES_ROOT\$root\CLSID\$clsidVal"
    $node = Get-Item $base -ErrorAction SilentlyContinue
    if (-not $node) {
        W "  [--] 未注册: HKCR\$root\CLSID\$clsidVal" Yellow
        return $false
    }
    W "  [OK] HKCR\$root\CLSID\$clsidVal" Green
    $name = $node.GetValue('')
    if ($name) { W "      (default)   = $name" }
    $thisAppid = (Get-ItemProperty $base -Name "AppID" -ErrorAction SilentlyContinue).AppID
    if ($thisAppid) {
        W "      AppID       = $thisAppid"
        $script:appid = $thisAppid
    }

    foreach ($sub in 'LocalServer32','LocalServer','InprocServer32','InprocServer','ProgID','VersionIndependentProgID') {
        $p = Join-Path $base $sub
        if (Test-Path $p) {
            $v = (Get-Item $p).GetValue('')
            W "      $sub = $v"
        }
    }
    return $true
}

$exists64 = $false
$exists32 = $false
if ($clsid) {
    $exists64 = Dump-Clsid "" $clsid
    $exists32 = Dump-Clsid "Wow6432Node" $clsid
}

# ──────────────────────────────────────────────────────────────────
# 4) EXE 位宽（PE Header machine 字段）
# ──────────────────────────────────────────────────────────────────
Section "4) EXE 位宽（PE Header）"

$exeBitness = $null
if (Test-Path $Exe) {
    try {
        $fs = [System.IO.File]::OpenRead($Exe)
        $br = New-Object System.IO.BinaryReader($fs)
        [void]$br.BaseStream.Seek(0x3C, 'Begin')
        $peOffset = $br.ReadInt32()
        [void]$br.BaseStream.Seek($peOffset + 4, 'Begin')
        $machine = $br.ReadUInt16()
        $br.Close()
        $fs.Close()
        if ($machine -eq 0x014c) {
            $exeBitness = "x86"
            W "  EXE = $Exe"
            W "  位宽: x86 (32-bit)" Green
        } elseif ($machine -eq 0x8664) {
            $exeBitness = "x64"
            W "  EXE = $Exe"
            W "  位宽: x64 (64-bit)" Green
        } else {
            W ("  EXE machine = 0x{0:X4} (未知)" -f $machine)
        }
    } catch {
        W "  读取 PE 失败: $_" Red
    }
} else {
    W "  [!!] EXE 不存在: $Exe" Red
}

# ──────────────────────────────────────────────────────────────────
# 5) AppID 段（DCOM 安全）
# ──────────────────────────────────────────────────────────────────
Section "5) AppID / DCOM 设置"

if ($appid) {
    foreach ($root in '', 'Wow6432Node') {
        $p = "Registry::HKEY_CLASSES_ROOT\$root\AppID\$appid"
        if (Test-Path $p) {
            $n = (Get-Item $p).GetValue('')
            $runAs   = (Get-ItemProperty $p -Name "RunAs"            -ErrorAction SilentlyContinue).RunAs
            $launch  = (Get-ItemProperty $p -Name "LaunchPermission" -ErrorAction SilentlyContinue).LaunchPermission
            $access  = (Get-ItemProperty $p -Name "AccessPermission" -ErrorAction SilentlyContinue).AccessPermission
            $hive = YesNo ([bool]$root) "Wow6432Node" "64-bit"
            W "  $hive HKCR\AppID\$appid  ($n)"
            if ($runAs)  { W "      RunAs            = $runAs" }
            if ($launch) { W "      LaunchPermission = (二进制 SDDL，$($launch.Length) 字节)" }
            if ($access) { W "      AccessPermission = (二进制 SDDL，$($access.Length) 字节)" }
        }
    }
} else {
    W "  CLSID 未关联 AppID -- 走 COM 默认 DCOM 设置"
}

# ──────────────────────────────────────────────────────────────────
# 6) OPCEnum 服务
# ──────────────────────────────────────────────────────────────────
Section "6) OPCEnum 服务"

$svc = Get-Service -Name 'OpcEnum' -ErrorAction SilentlyContinue
if ($svc) {
    $startMode = (Get-CimInstance Win32_Service -Filter "Name='OpcEnum'").StartMode
    W "  [OK] OpcEnum 服务: $($svc.Status), 启动模式: $startMode" Green
} else {
    W "  [--] OpcEnum 服务未安装 -- 影响远程扫描，本机直连 ProgID 不受影响" Yellow
}

# ──────────────────────────────────────────────────────────────────
# 诊断结论
# ──────────────────────────────────────────────────────────────────
Section "诊断结论"

W "  当前 PowerShell 进程: $psBits"
W "  Dc.App 构建：x64（build.ps1 默认 Platform=x64）"

if (-not $clsid) {
    W ""
    W "  -> ProgID 不完整。先用管理员 cmd 重新注册：" Yellow
    $serverDir = Split-Path $Exe -Parent
    W "      1) cd `"$serverDir`""
    W "      2) OpcDaAeServer.exe /unregserver"
    W "      3) OpcDaAeServer.exe /regserver"
    W "    (用 cmd 比 PowerShell 稳，避免 PS 解析参数的怪问题)"
}
elseif ($exeBitness -eq 'x86' -and $exists32 -and -not $exists64) {
    W ""
    W "  -> 经典 32/64 位错配：" Red
    W "    Demo server 是 32-bit，CLSID 只在 Wow6432Node。"
    W "    我们的 Dc.App 是 x64，CoCreateInstance 查 64-bit hive 找不到 -> 0x800401F3。"
    W ""
    W "  修法二选一：" Yellow
    W "    A) 把 Dc.App 改成 x86 跑（vendor 也支持 x86 构建）："
    W "         dotnet build src/Dc.App -p:Platform=x86 -p:CustomTestTarget=net8.0-windows"
    W "         缺点：Dc.sln 没加 x86 配置，要先加；测试可能受影响。"
    W "    B) 装 64-bit OPC DA 服务器（Matrikon、KEPServerEX 均有 x64 版），ProgID 换成对应的。"
    W "       推荐这条 -- 客户机几乎都是 x64，跟生产一致。"
}
elseif ($exeBitness -eq 'x86' -and $exists64) {
    W ""
    W "  -> 64-bit hive 有 CLSID 但 EXE 是 32-bit。" Yellow
    W "    LocalServer32 在 64-bit hive 上注册了 32-bit exe 路径，COM 应该能跨位激活，"
    W "    但部分 Windows 版本/AppID 配置仍会失败。"
    W ""
    W "  建议：用 ProcMon 抓 CoCreateInstance 的注册表访问看到底缺哪条；"
    W "    或换 64-bit demo server / 真服务器测试。"
}
elseif ($exists64 -or $exists32) {
    W ""
    W "  -> CLSID 注册看起来完整。如果还报 0x800401F3，可能是：" Yellow
    W "      - LocalServer32 路径里的 exe 已被移动/删除"
    W "      - AppID 的 LaunchPermission 拒绝当前用户"
    W "    跑 dcomcnfg -> 找到此组件 -> Security 标签确认 Launch/Activation 允许"
}
else {
    W ""
    W "  -> CLSID 完全没有登记到 HKCR\CLSID 也没在 Wow6432Node。" Red
    W "    /regserver 没执行成功，或当时没用管理员权限。"
}

W ""
W "脚本结束 — $(Get-Date -Format 'HH:mm:ss')"

if ($Save) {
    $logPath = Join-Path $PSScriptRoot "diag-opcda.log"
    # UTF-8 BOM 让 Windows Notepad 也能正确显示中文
    $utf8Bom = New-Object System.Text.UTF8Encoding($true)
    [System.IO.File]::WriteAllLines($logPath, $lines, $utf8Bom)
    Write-Host ""
    Write-Host "-> 完整输出已存到: $logPath" -ForegroundColor Cyan
}
