# 把 32-bit OPC 相关 COM 组件（OPCEnum + 用户指定 DA Server）同步到 64-bit 注册表视图。
#
# 等同 OPC Foundation "Core Components Redistributable" 装包做的最核心几件事：
#   1) OPCEnum 的 CLSID/AppID/ProgID 同步到 64-bit 视图（让 x64 客户端能 CoCreate IOPCServerList2）
#   2) 用户的 OPC DA Server 同步到 64-bit 视图（OPCEnum 才能枚举它，CoCreateInstanceEx 才找得到）
#
# LocalServer32 (out-of-proc EXE) 跨位激活由 Windows COM 原生支持，不需要 proxy DLL。
#
# 用法（管理员）：
#   .\register-da-x64.ps1                              # OPCEnum + SampleCompany.DaSample
#   .\register-da-x64.ps1 -ProgId "Foo.Bar"            # OPCEnum + Foo.Bar
#   .\register-da-x64.ps1 -SkipOpcEnum                 # 仅同步用户 ProgID（OPCEnum 已同步过时用）
#   .\register-da-x64.ps1 -Unregister                  # 撤销
#
# 兼容 Windows PowerShell 5.1。

#Requires -RunAsAdministrator

param(
    [string]$ProgId = "SampleCompany.DaSample",
    [switch]$SkipOpcEnum,
    [switch]$Unregister
)

$ErrorActionPreference = "Stop"

# OPCEnum 服务 CLSID / AppID / ProgID（OPC Foundation 固定值）
$OPCENUM_CLSID  = '{13486D51-4821-11D2-A494-3CB306C10000}'
$OPCENUM_APPID  = '{13486D44-4821-11D2-A494-3CB306C10000}'
$OPCENUM_PROGIDS = @('OPC.ServerList.1', 'OPC.ServerList')

# ──────────────────────────────────────────────────────────────────
# 工具函数
# ──────────────────────────────────────────────────────────────────
function Open-HkcrView {
    param([string]$view, [bool]$writable = $false)
    $rv = if ($view -eq '32') {
        [Microsoft.Win32.RegistryView]::Registry32
    } else {
        [Microsoft.Win32.RegistryView]::Registry64
    }
    return [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::ClassesRoot, $rv)
}

function Copy-RegSubtree {
    param($srcBase, [string]$srcSub, $dstBase, [string]$dstSub)
    $src = $srcBase.OpenSubKey($srcSub, $false)
    if (-not $src) { return $false }
    $dst = $dstBase.CreateSubKey($dstSub, $true)
    foreach ($valueName in $src.GetValueNames()) {
        $kind  = $src.GetValueKind($valueName)
        $value = $src.GetValue($valueName, $null,
                    [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        $dst.SetValue($valueName, $value, $kind)
    }
    foreach ($subName in $src.GetSubKeyNames()) {
        Copy-RegSubtree $srcBase "$srcSub\$subName" $dstBase "$dstSub\$subName" | Out-Null
    }
    $dst.Close(); $src.Close()
    return $true
}

function Remove-Subkey {
    param($base, [string]$sub)
    try { $base.DeleteSubKeyTree($sub, $false) | Out-Null; return $true }
    catch { return $false }
}

# 把 (CLSID + AppID + ProgIDs) 从 32-bit 视图同步到 64-bit 视图。
function Sync-ToX64 {
    param(
        [string]$label,
        [string]$clsid,
        [string]$appid,
        [string[]]$progIds
    )
    Write-Host ""
    Write-Host "→ 同步 $label" -ForegroundColor Yellow
    Write-Host "   CLSID  = $clsid" -ForegroundColor Gray
    if ($appid)   { Write-Host "   AppID  = $appid" -ForegroundColor Gray }

    $src = Open-HkcrView '32' $false
    $dst = Open-HkcrView '64' $true

    if (Copy-RegSubtree $src "CLSID\$clsid" $dst "CLSID\$clsid") {
        Write-Host "   [OK] CLSID\$clsid" -ForegroundColor Green
    } else {
        Write-Host "   [!!] 32-bit 视图没有 CLSID\$clsid (skip)" -ForegroundColor Red
    }
    if ($appid -and (Copy-RegSubtree $src "AppID\$appid" $dst "AppID\$appid")) {
        Write-Host "   [OK] AppID\$appid" -ForegroundColor Green
    }
    foreach ($p in $progIds) {
        if (-not $p) { continue }
        # ProgID 主项 + 嵌套的 CurVer 子项一起拷
        $dstProg = $dst.OpenSubKey($p, $false)
        if ($dstProg) { $dstProg.Close(); Write-Host "   [--] 64-bit 视图已有 $p (skip)" -ForegroundColor Gray; continue }
        if (Copy-RegSubtree $src $p $dst $p) {
            Write-Host "   [OK] $p" -ForegroundColor Green
        }
    }
    $src.Close(); $dst.Close()
}

function Unsync-FromX64 {
    param(
        [string]$label,
        [string]$clsid,
        [string]$appid,
        [string[]]$progIds   # 保留参数签名，但不再删除 — 顶级 ProgID 在两视图共享，删了会破坏原 server 注册
    )
    Write-Host ""
    Write-Host "→ 撤销 $label（仅 CLSID/AppID；ProgID 顶级 key 不动）" -ForegroundColor Yellow
    $w = Open-HkcrView '64' $true
    # CLSID 在 64-bit 视图被 WOW64 redirect 隔离（HKCR\CLSID vs Wow6432Node\CLSID 是独立子树），删安全
    if ($clsid -and (Remove-Subkey $w "CLSID\$clsid")) {
        Write-Host "   removed: CLSID\$clsid" -ForegroundColor Gray
    }
    # AppID 同上，受 redirector 隔离
    if ($appid -and (Remove-Subkey $w "AppID\$appid")) {
        Write-Host "   removed: AppID\$appid" -ForegroundColor Gray
    }
    # ⚠ 顶级 ProgID（如 "SampleCompany.DaSample"）不在 redirector list，两视图共享同一份。
    # 删除会把 server /regserver 原始 ProgID 一起干掉，破坏注册。不删。
    $w.Close()
}

# 给定 ProgID 解析其 CLSID 和 AppID（先查 32-bit，再 64-bit）。
function Resolve-ProgId {
    param([string]$progIdValue)
    $v32 = Open-HkcrView '32' $false
    $v64 = Open-HkcrView '64' $false
    $clsid = $null; $appid = $null; $fromView = $null
    foreach ($v in @(@{View=$v32;Name='32'}, @{View=$v64;Name='64'})) {
        $k = $v.View.OpenSubKey("$progIdValue\CLSID", $false)
        if ($k) { $clsid = $k.GetValue(''); $fromView = $v.Name; $k.Close(); break }
    }
    if ($clsid) {
        $ck = $v32.OpenSubKey("CLSID\$clsid", $false)
        if ($ck) { $appid = $ck.GetValue('AppID'); $ck.Close() }
        if (-not $appid) {
            $ck = $v64.OpenSubKey("CLSID\$clsid", $false)
            if ($ck) { $appid = $ck.GetValue('AppID'); $ck.Close() }
        }
    }
    $v32.Close(); $v64.Close()
    return @{ ProgId=$progIdValue; Clsid=$clsid; AppId=$appid; FromView=$fromView }
}

# ──────────────────────────────────────────────────────────────────
# 主流程
# ──────────────────────────────────────────────────────────────────

Write-Host "OPC 注册同步 — $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
$action = if ($Unregister) { '撤销' } else { '同步' }
Write-Host "动作: $action  /  ProgID: $ProgId  /  SkipOpcEnum: $SkipOpcEnum"

# 解析用户 ProgID
$user = Resolve-ProgId $ProgId
if (-not $user.Clsid) {
    Write-Host ""
    Write-Host "[!!] ProgID '$ProgId' 未在两个视图找到 CLSID 映射" -ForegroundColor Red
    Write-Host "    先确认 OPC server 已 /regserver" -ForegroundColor Yellow
    exit 1
}
Write-Host "用户 server: ProgID=$($user.ProgId)  CLSID=$($user.Clsid)  AppID=$($user.AppId)  (来源: $($user.FromView)-bit)" -ForegroundColor Cyan

if ($Unregister) {
    Unsync-FromX64 -label "用户 server ($ProgId)" -clsid $user.Clsid -appid $user.AppId -progIds @($ProgId)
    if (-not $SkipOpcEnum) {
        Unsync-FromX64 -label "OPCEnum" -clsid $OPCENUM_CLSID -appid $OPCENUM_APPID -progIds $OPCENUM_PROGIDS
    }
    Write-Host ""
    Write-Host "撤销完成。" -ForegroundColor Green
    exit 0
}

if (-not $SkipOpcEnum) {
    Sync-ToX64 -label "OPCEnum (IOPCServerList2)" -clsid $OPCENUM_CLSID -appid $OPCENUM_APPID -progIds $OPCENUM_PROGIDS
}

Sync-ToX64 -label "用户 DA Server ($ProgId)" -clsid $user.Clsid -appid $user.AppId -progIds @($ProgId)

Write-Host ""
Write-Host "同步完成。x64 客户端现在可以激活 OPCEnum 与这个 32-bit OPC server。" -ForegroundColor Green
Write-Host ""
Write-Host "下一步：" -ForegroundColor Cyan
Write-Host "  1) (可选) 跑 .\diag-opcda.ps1 复查 64-bit 视图"
Write-Host "  2) 启动 Dc.App，OPC 浏览页选 DA，ProgID 填 $ProgId，连接"
