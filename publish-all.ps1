#Requires -Version 5.1
<#
.SYNOPSIS
    VibrantbitLauncher.Avalonia 多平台一键发布。

.DESCRIPTION
    一条命令产出 7 个平台的发布包：

        win-x64  win-x86  win-arm64
        linux-x64  linux-arm64
        osx-x64   osx-arm64

    产物结构：

        publish/<rid>/              原始发布目录（单文件可执行 + 运行时附属文件）
        dist/<名字>-<rid>.tar.gz    Linux / macOS 用（跨平台，能带权限位）
        dist/<名字>-<rid>.zip       Windows 用（双击解压友好，仅 win-* 出）
        dist/<名字>-<rid>.app       macOS 的 .app 包（仅 osx-*，另附同名 tar.gz）
        dist/SHA256SUMS.txt         全部包的校验和

    macOS 首次运行：

        解压后先双击 install.command（自动补可执行位 + 去下载隔离属性 + ad-hoc 签名），
        再把 VibrantbitLauncher.Avalonia.app 拖进「应用程序」文件夹。
        细节见包内 README-macOS.txt。

    设计要点：

    1. 默认框架依赖（不含 .NET 运行时，包小约 20MB）。
       目标机器需要自备运行时：
         Windows  → .NET 10 Desktop Runtime
         Linux    → .NET 10 Runtime
         macOS    → .NET 10 Runtime
       加 -SelfContained 可改成自带运行时（包变大，但目标机器免装）。

    2. 串行发布。多个 RID 并行 publish 会争抢同一个 obj/project.assets.json，
       反而更容易出错，所以按顺序来（7 个平台整轮约 3~5 分钟）。

    3. 失败不中断。某个 RID 挂了会继续发其它 RID，最后统一汇总。

.PARAMETER Rids
    要发布的 RID 列表。默认全部 7 个。调试时可只传一两个。

.PARAMETER Configuration
    Debug 或 Release，默认 Release。

.PARAMETER OutputRoot
    原始发布目录的根，默认 <工程根>\publish。

.PARAMETER DistDir
    打包产物的目录，默认 <工程根>\dist。

.PARAMETER SelfContained
    自带 .NET 运行时。包会大很多，但目标机器免装运行时。

.PARAMETER NoTrim
    关闭 csproj 里的 TrimPlatformPayload（按平台剥掉其它平台的后端程序集）。
    只有在「目标平台起不来、且怀疑是剥离导致」时才用这个开关。

.PARAMETER NoPackage
    只发布到 publish/<rid>/，不打包成 tar.gz / zip。

.PARAMETER Clean
    发布前先删掉 publish/ 和 dist/。

.EXAMPLE
    .\publish-all.ps1
    一条命令发布全部 7 个平台（框架依赖）。

.EXAMPLE
    .\publish-all.ps1 -Rids win-x64,linux-x64
    只发两个平台，用于快速验证。

.EXAMPLE
    .\publish-all.ps1 -SelfContained -Clean
    发免装 .NET 运行时的自包含版本，并先清空旧产物。

.NOTES
    如果 PowerShell 阻止脚本执行：
        Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
#>
[CmdletBinding()]
param(
    [string[]] $Rids = @('win-x64', 'win-x86', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64'),

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [string] $OutputRoot,
    [string] $DistDir,

    [switch] $SelfContained,
    [switch] $NoTrim,
    [switch] $NoPackage,
    [switch] $Clean
)

$ErrorActionPreference = 'Stop'

# 兜底错误陷阱。没有它的话，脚本一旦在 try/catch 之外抛错，
# 在「输出被重定向到文件」的场景下只会看到脚本静默退出、退出码 1，很难查。
trap {
    Write-Host ''
    Write-Host '!! 发布脚本异常终止 !!' -ForegroundColor Red
    Write-Host ($_ | Out-String) -ForegroundColor Red
    Write-Host ('  出错位置：' + $_.InvocationInfo.PositionMessage) -ForegroundColor Red
    exit 1
}

# ---------------------------------------------------------------------------
# 常量与前置检查
# ---------------------------------------------------------------------------
$root    = $PSScriptRoot
$appName = 'VibrantbitLauncher.Avalonia'
$csproj  = Join-Path $root "$appName.csproj"

if (-not $OutputRoot) { $OutputRoot = Join-Path $root 'publish' }
if (-not $DistDir)    { $DistDir    = Join-Path $root 'dist' }

if (-not (Test-Path $csproj)) { throw "找不到工程文件：$csproj" }

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '未找到 dotnet CLI。请先安装 .NET 10 SDK：https://dotnet.microsoft.com/download/dotnet/10.0'
}
$sdkVersion = (& dotnet --version).Trim()

# tar：Linux / macOS 的 .tar.gz 需要它。
# Windows 自带的 tar 是 bsdtar，不支持 --mode → 打出来的归档会丢权限位
# （到了 macOS 上 .app 里的可执行文件没有 x 位，双击没反应）。
# Git 自带的 GNU tar 支持 --mode，优先用它；找不到就退回系统 tar 并给出警告。
$tarExe = $null
$tarGnu = $false

$gitTar = Join-Path $env:ProgramFiles 'Git\usr\bin\tar.exe'
$candidates = @()
if (Test-Path -LiteralPath $gitTar) { $candidates += $gitTar }
$sysTar = Get-Command tar -ErrorAction SilentlyContinue
if ($sysTar) { $candidates += $sysTar.Source }

foreach ($cand in $candidates) {
    $ver = (& $cand --version 2>$null | Select-Object -First 1)
    if (-not $tarExe) { $tarExe = $cand; $tarGnu = ($ver -match 'GNU tar') }
    if ($ver -match 'GNU tar') { $tarExe = $cand; $tarGnu = $true; break }
}

if (-not $tarExe) {
    Write-Warning '未找到 tar，Linux/macOS 的 .tar.gz 将无法生成（Windows 10 1803+ 自带 tar.exe）。'
}
elseif (-not $tarGnu) {
    Write-Warning "当前 tar 是 bsdtar，不支持 --mode：包内权限位会丢失（包内已附 install.command / install.sh 用于事后修复）。"
}

# 版本号唯一来源：csproj 的 <Version>
$versionMatch = Select-String -Path $csproj -Pattern '<Version>([^<]+)</Version>' | Select-Object -First 1
if (-not $versionMatch) { throw "无法从 $appName.csproj 读取 <Version>。" }
$version = $versionMatch.Matches[0].Groups[1].Value.Trim()

# ---------------------------------------------------------------------------
# 运行期辅助
# ---------------------------------------------------------------------------

# Linux / macOS 上打的包丢了可执行位（Windows 文件系统没有权限位），
# 所以包里放一个安装脚本，解压后 `sh install.sh` 即可。
$installSh = @'
#!/bin/sh
# VibrantbitLauncher.Avalonia - 解压后的初始化步骤
set -e
cd "$(dirname "$0")"

chmod +x VibrantbitLauncher.Avalonia
echo "[OK] 已赋予可执行权限。"
echo
echo "运行方式： ./VibrantbitLauncher.Avalonia"
echo
echo "注意：这是「框架依赖」发布，需要机器上已安装 .NET 10 Runtime。"
echo "      Debian/Ubuntu 可用微软官方源安装 dotnet-runtime-10.0；"
echo "      或者改用带 -SelfContained 发布的版本（已内置运行时，免装）。"
'@

# macOS 包内附的「首次运行修复」脚本。
# macOS 比 Linux 多两道坎：
#   ① 从 Windows 打出来的 tar 没有可执行位 → .app 双击没反应；
#   ② Apple Silicon 上「未签名」的 arm64 二进制会被内核直接杀掉（表现为闪退 / 已损坏），
#      必须做一次 ad-hoc 自签名（codesign -s -），本地签名，不需要开发者证书。
# 两步都塞进 install.command，用户双击一下就好。
$macInstallCmd = @'
#!/bin/sh
# VibrantbitLauncher.Avalonia - macOS 首次运行修复（双击本文件执行）
set -e
cd "$(dirname "$0")"

APP="VibrantbitLauncher.Avalonia.app"
BIN="$APP/Contents/MacOS/VibrantbitLauncher.Avalonia"

if [ ! -f "$BIN" ]; then
    echo "[X] 找不到 $BIN"
    echo "    请确认本脚本与 $APP 在同一个目录下。"
    printf '按回车键退出...'; read -r _ || true
    exit 1
fi

# ① 补可执行位（Windows 上打包出来的 tar 会丢掉权限位）
chmod +x "$BIN"

# ② 去掉浏览器下载留下的隔离属性，否则会被 Gatekeeper 拦下
xattr -dr com.apple.quarantine "$APP" 2>/dev/null || true

# ③ ad-hoc 自签名：本地签名，不需要开发者证书、不需要联网
if command -v codesign >/dev/null 2>&1; then
    codesign --force --deep --sign - "$APP" >/dev/null 2>&1 || true
fi

echo ""
echo "[OK] 修复完成。"
echo "     接下来：把 $APP 拖进「应用程序」，双击启动即可。"
echo ""
echo "提醒：本包是「框架依赖」版本，机器上需要先装 .NET 10 Runtime（不是 SDK）。"
echo "      官方下载：https://dotnet.microsoft.com/download/dotnet/10.0"
echo "      Homebrew 装的 dotnet 可能需要手动指定运行时位置："
echo "        export DOTNET_ROOT=$(brew --prefix)/opt/dotnet/libexec"
echo ""
printf '按回车键关闭此窗口...'; read -r _ || true
'@

# 文件名用纯 ASCII，避免 Windows 上打包时文件名编码踩坑；内容是 UTF-8。
$macReadme = @'
VibrantbitLauncher.Avalonia  macOS 使用说明
===========================================

一、先选对架构
    Apple 芯片（M1/M2/M3/M4…） → osx-arm64
    Intel 芯片                  → osx-x64
    （「关于本机」→「芯片 / 处理器」一栏即可确认）

二、先装 .NET 10 Runtime
    这是「框架依赖」发布，包内不含 .NET 运行时，必须先装：
      · 官方安装包 https://dotnet.microsoft.com/download/dotnet/10.0
        选 Runtime（装 SDK 也能用）
      · 或 Homebrew：brew install --cask dotnet
    用 Homebrew 装的话，启动时若报「找不到 .NET runtime」，
    在「终端」里执行下面这行，把运行时位置告诉程序：
        export DOTNET_ROOT=$(brew --prefix)/opt/dotnet/libexec
    （想永久生效就把它写进 ~/.zshrc）

三、安装与启动
    1. 解压 VibrantbitLauncher.Avalonia-osx-arm64.tar.gz（双击即可）
    2. 双击同目录下的 install.command，它会自动完成三件事：
         · 补上可执行位（从 Windows 打包的 tar 会丢权限位）
         · 去掉下载隔离属性（绕过 Gatekeeper）
         · 做一次 ad-hoc 签名（Apple Silicon 必需）
    3. 把 VibrantbitLauncher.Avalonia.app 拖进「应用程序」，双击启动

    若双击 install.command 提示没有执行权限，在「终端」里进入该目录后跑：
        sh install.command

四、启动不起来怎么办
    · 提示「已损坏，无法打开」/ 双击闪退
        → 多半是漏了第三步的签名，在「终端」里执行：
            xattr -dr com.apple.quarantine /Applications/VibrantbitLauncher.Avalonia.app
            codesign --force --deep --sign - /Applications/VibrantbitLauncher.Avalonia.app
    · 提示找不到 .NET，或者双击毫无反应
        → 装 .NET 10 Runtime；Homebrew 安装的按第二步设 DOTNET_ROOT
    · 想看具体报错
        → 在「终端」里直接跑可执行文件，日志会打在终端上：
            /Applications/VibrantbitLauncher.Avalonia.app/Contents/MacOS/VibrantbitLauncher.Avalonia
    · 首次启动系统会问「是否允许 App 访问网络 / 文件」，选「允许」

五、卸载
    直接从「应用程序」拖进废纸篓即可。
    游戏数据在 ~/.minecraft，需要一并清理时手动删除。
'@

# 以 UTF-8 无 BOM 写文件。
# PS 5.1 的 `Set-Content -Encoding UTF8` 会写 BOM：
#   Info.plist 带 BOM 会让 macOS 的 plutil 报错；
#   SHA256SUMS.txt 带 BOM 会让 Linux / macOS 上的 `sha256sum -c` 直接失败。
# 而 `-Encoding ASCII` 会把中文注释变成问号。所以文本类产物统一走这里。
function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Content
    )
    $enc = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $Content, $enc)
}

# 打包 tar.gz。
#   优先用 Git 自带的 GNU tar，走两步：
#     ① `tar --mode=u+rwx,go+rx --force-local -cf` 先打成裸 tar
#     ② 再用 .NET 的 GZipStream 压成 .tar.gz
#   为什么分两步，三个都是踩过的坑：
#     · `--mode=u+rwx,go+rx`（0755）——macOS 上 .app 的主可执行文件、Linux 上的裸二进制
#       都必须带可执行位，否则解压后跑不起来；Windows 自带的 bsdtar 没有这个选项。
#     · `--force-local`——GNU tar 会把 `C:\...` 里的冒号当成「远程主机」语法，
#       报 "Cannot connect to C: resolve failed"，必须显式关掉。
#     · 不用 `-z`——GNU tar 的 -z 会 fork 外部 gzip.exe，而 PowerShell 这边的 PATH
#       里没有 Git 的 usr\bin，会报 "gzip: command not found"。
#   拿不到 GNU tar 时退化为 bsdtar 直接 -czf（无 --mode），
#   包内权限位由包内的 install.command / install.sh 兜底。
function New-TarGz {
    param(
        [Parameter(Mandatory = $true)][string]   $Out,
        [Parameter(Mandatory = $true)][string]   $SourceDir,
        [Parameter(Mandatory = $true)][string[]] $Entries
    )

    if (-not $tarGnu) {
        # tar -czf 对同名文件是截断覆盖，不需要预先删除
        & $tarExe -czf $Out -C $SourceDir @Entries
        if ($LASTEXITCODE -ne 0) { throw ("打包失败：{0}" -f (Split-Path -Leaf $Out)) }
        return
    }

    $tmpTar = [System.IO.Path]::ChangeExtension($Out, '.tar')
    if (Test-Path -LiteralPath $tmpTar) {
        Remove-Item -LiteralPath $tmpTar -Force -ErrorAction SilentlyContinue
    }

    $tarArgs = @('--mode=u+rwx,go+rx', '--force-local', '-cf', $tmpTar, '-C', $SourceDir) + $Entries
    & $tarExe @tarArgs
    if ($LASTEXITCODE -ne 0) { throw ("tar 打包失败：{0}" -f (Split-Path -Leaf $Out)) }

    $inStream = [System.IO.File]::OpenRead($tmpTar)
    try {
        $outStream = [System.IO.File]::Create($Out)
        try {
            $gz = New-Object -TypeName System.IO.Compression.GZipStream `
                            -ArgumentList @($outStream, [System.IO.Compression.CompressionMode]::Compress, $true)
            try { $inStream.CopyTo($gz) } finally { $gz.Dispose() }
        }
        finally { $outStream.Dispose() }
    }
    finally { $inStream.Dispose() }

    Remove-Item -LiteralPath $tmpTar -Force -ErrorAction SilentlyContinue
}

# 递归删除目录。
#
# 为什么不直接 Remove-Item：某些执行环境会把「删除」改写成「移到回收站」，
# 而回收站操作有可能**删成功了却仍然抛错**（WorkBuddy 沙箱的 safe-delete 钩子就是这样，
# 报 "Error during a `trash` operation: Some operations were aborted"）。
# 所以这里 catch 之后复查目录是否真的还在：已经不在了就当成功，
# 否则才认为是真失败并抛出——否则会出现「脚本报错但产物其实已经清理干净」的假故障。
function Remove-DirectoryTree {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [string] $What = '目录'
    )

    if (-not (Test-Path -LiteralPath $Path)) { return }

    try {
        Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
    }
    catch {
        if (Test-Path -LiteralPath $Path) {
            throw ("无法删除{0} {1}：{2}" -f $What, $Path, $_.Exception.Message)
        }
        Write-Warning ("删除{0}时报错，但目录已被移除，按成功处理：{1}" -f $What, $Path)
    }
}

# 删除单个文件。（与 Remove-DirectoryTree 同一个坑：某些环境把「删除」改写成
# 「移到回收站」，可能删成功了却仍然抛错——沙箱的 safe-delete 钩子会报
# SAFE_DELETE_FAIL_CLOSED。所以 catch 之后复查文件是否真的还在。）
function Remove-FileSafe {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [string] $What = '文件'
    )

    if (-not (Test-Path -LiteralPath $Path)) { return }

    try {
        Remove-Item -LiteralPath $Path -Force -ErrorAction Stop
    }
    catch {
        if (Test-Path -LiteralPath $Path) {
            throw ("无法删除{0} {1}：{2}" -f $What, $Path, $_.Exception.Message)
        }
        Write-Warning ("删除{0}时报错，但文件已被移除，按成功处理：{1}" -f $What, $Path)
    }
}

# macOS 的 Info.plist 模板（版本号在运行时填入）
function New-MacInfoPlist([string] $appVersion) {
    @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>VibrantbitLauncher</string>
    <key>CFBundleDisplayName</key>
    <string>VibrantbitLauncher</string>
    <key>CFBundleIdentifier</key>
    <string>com.vibrantbit.launcher</string>
    <key>CFBundleVersion</key>
    <string>$appVersion</string>
    <key>CFBundleShortVersionString</key>
    <string>$appVersion</string>
    <key>CFBundleExecutable</key>
    <string>VibrantbitLauncher.Avalonia</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>LSMinimumSystemVersion</key>
    <string>11.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
</dict>
</plist>
"@
}

# ---------------------------------------------------------------------------
# 打印表头
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host '================================================================' -ForegroundColor Cyan
Write-Host ' VibrantbitLauncher.Avalonia  多平台一键发布' -ForegroundColor Cyan
Write-Host '================================================================' -ForegroundColor Cyan
Write-Host ("  版本号     : {0}" -f $version)
Write-Host ("  dotnet SDK : {0}" -f $sdkVersion)
Write-Host ("  配置       : {0}" -f $Configuration)
Write-Host ("  部署方式   : {0}" -f $(if ($SelfContained) { '自包含（目标机器免装 .NET 运行时）' } else { '框架依赖（不含 .NET 运行时）' }))
Write-Host ("  平台剥离   : {0}" -f $(if ($NoTrim) { '关闭' } else { '开启（按 RID 剥掉其它平台后端）' }))
Write-Host ("  目标 RID   : {0}" -f ($Rids -join ', '))
Write-Host ''

# ---------------------------------------------------------------------------
# 清理
# ---------------------------------------------------------------------------
if ($Clean) {
    foreach ($d in @($OutputRoot, $DistDir)) {
        if (Test-Path $d) {
            Remove-DirectoryTree -Path $d -What '旧输出目录'
            Write-Host ("  已清理 {0}" -f $d) -ForegroundColor DarkGray
        }
    }
    Write-Host ''
}
New-Item -ItemType Directory -Force -Path $OutputRoot, $DistDir | Out-Null

# ---------------------------------------------------------------------------
# 主循环：逐 RID 发布 + 打包
# ---------------------------------------------------------------------------
$results = New-Object System.Collections.ArrayList

foreach ($rawRid in $Rids) {

    # 归一化 RID（去掉多余空白，统一小写 —— RID 本身大小写不敏感）
    # 注意：不能直接给 foreach 的迭代变量赋值（PowerShell 里它是只读的），
    # 所以这里另起一个 $rid。
    $rid = $rawRid.Trim().ToLowerInvariant()
    if (-not $rid) { continue }

    $isWin   = $rid.StartsWith('win-')
    $isOsx   = $rid.StartsWith('osx-')
    $isLinux = $rid.StartsWith('linux-')

    # 单文件可执行名：只有 Windows 带 .exe
    $exeName = if ($isWin) { "$appName.exe" } else { $appName }

    $outDir = Join-Path $OutputRoot $rid

    Write-Host ("[{0}] 发布中 ..." -f $rid) -ForegroundColor Yellow
    $sw = [System.Diagnostics.Stopwatch]::StartNew()

    try {
        Remove-DirectoryTree -Path $outDir -What '上一次的发布目录'

        $pubArgs = @(
            'publish', $csproj,
            '-c', $Configuration,
            '-r', $rid,
            '-o', $outDir,
            '--nologo',
            '-v', 'quiet'
        )
        $pubArgs += if ($SelfContained) { '--self-contained' } else { '--no-self-contained' }
        if ($NoTrim) { $pubArgs += '-p:SkipPlatformTrim=true' }

        $pubLog = & dotnet @pubArgs 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw ("dotnet publish 失败（退出码 {0}）：`n{1}" -f $LASTEXITCODE, ($pubLog -join "`n"))
        }

        $exePath = Join-Path $outDir $exeName
        if (-not (Test-Path $exePath)) {
            $listing = (Get-ChildItem $outDir | Select-Object -ExpandProperty Name) -join ', '
            throw "发布成功但找不到可执行文件 $exeName。目录内容：$listing"
        }

        $exeBytes = (Get-Item $exePath).Length

        # Linux / macOS：往包里放赋权脚本（必须在打包前写入）
        if (-not $isWin -and -not $NoPackage) {
            Write-Utf8NoBom -Path (Join-Path $outDir 'install.sh') -Content $installSh
        }

        # ------------------------------------------------------------------
        # 打包
        # ------------------------------------------------------------------
        $packages = New-Object System.Collections.ArrayList

        if (-not $NoPackage) {

            if ($isOsx) {
                # macOS：先组 .app bundle，再打成 tar.gz
                $appStage = Join-Path $OutputRoot ("_app_" + $rid)
                Remove-DirectoryTree -Path $appStage -What 'macOS 暂存目录'

                $bundle   = Join-Path $appStage "$appName.app"
                $macOsDir = Join-Path $bundle 'Contents/MacOS'
                $resDir   = Join-Path $bundle 'Contents/Resources'
                New-Item -ItemType Directory -Force -Path $macOsDir, $resDir | Out-Null

                Copy-Item $exePath (Join-Path $macOsDir $exeName) -Force
                Write-Utf8NoBom -Path (Join-Path $bundle 'Contents/Info.plist') `
                                -Content (New-MacInfoPlist $version)

                # 包内附「首次运行修复」脚本与说明：Windows 上打包出来的 tar 丢了
                # 可执行位，且 Apple Silicon 上未签名的 arm64 会被内核直接拒绝。
                Write-Utf8NoBom -Path (Join-Path $appStage 'install.command') -Content $macInstallCmd
                Write-Utf8NoBom -Path (Join-Path $appStage 'README-macOS.txt') -Content $macReadme

                $tarName = "$appName-$rid.tar.gz"
                $tarPath = Join-Path $DistDir $tarName
                New-TarGz -Out $tarPath -SourceDir $appStage `
                          -Entries @("$appName.app", 'install.command', 'README-macOS.txt')
                [void]$packages.Add($tarName)
            }
            else {
                $tarName = "$appName-$rid.tar.gz"
                $tarPath = Join-Path $DistDir $tarName
                New-TarGz -Out $tarPath -SourceDir $outDir -Entries @('.')
                [void]$packages.Add($tarName)

                if ($isWin) {
                    # Windows 额外出一份 zip：双击解压比 tar.gz 顺手。
                    # 必须先清掉旧包：Compress-Archive 覆盖同名文件时内部要删一次，
                    # 而在某些环境里「删除」被改写成回收站操作、会直接把整个平台搞失败。
                    $zipName = "$appName-$rid.zip"
                    $zipPath = Join-Path $DistDir $zipName
                    Remove-FileSafe -Path $zipPath -What '旧 zip 包'
                    try {
                        Compress-Archive -Path (Join-Path $outDir '*') -DestinationPath $zipPath -Force
                        [void]$packages.Add($zipName)
                    }
                    catch {
                        # zip 只是附带的便利格式，tar.gz 已经在上面出好了，
                        # 这里失败不该让整个平台算失败。
                        Write-Warning ("[{0}] zip 生成失败（tar.gz 不受影响）：{1}" -f $rid, $_.Exception.Message)
                    }
                }
            }
        }

        $sw.Stop()
        $secs = [math]::Round($sw.Elapsed.TotalSeconds, 1)
        $mb   = [math]::Round($exeBytes / 1MB, 1)

        Write-Host ("[{0}] 完成：{1} MB，用时 {2}s{3}" -f `
            $rid, $mb, $secs,
            $(if ($packages.Count -gt 0) { "，包：" + ($packages -join ', ') } else { '' })) `
            -ForegroundColor Green

        [void]$results.Add([pscustomobject]@{
            Rid      = $rid
            Ok       = $true
            SizeMB   = $mb
            Seconds  = $secs
            Packages = ($packages -join ', ')
            Error    = ''
        })
    }
    catch {
        $sw.Stop()
        Write-Host ("[{0}] 失败：{1}" -f $rid, $_.Exception.Message) -ForegroundColor Red
        [void]$results.Add([pscustomobject]@{
            Rid      = $rid
            Ok       = $false
            SizeMB   = 0
            Seconds  = [math]::Round($sw.Elapsed.TotalSeconds, 1)
            Packages = ''
            Error    = $_.Exception.Message
        })
    }
}

# ---------------------------------------------------------------------------
# 校验和
# ---------------------------------------------------------------------------
if (-not $NoPackage) {
    $sumFile = Join-Path $DistDir 'SHA256SUMS.txt'

    $lines = Get-ChildItem -Path $DistDir -File |
             Where-Object { $_.Name -ne 'SHA256SUMS.txt' } |
             Sort-Object Name |
             ForEach-Object {
                 '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
             }

    if ($lines) {
        # 必须无 BOM + LF 换行：
        #   `sha256sum -c SHA256SUMS.txt`（Linux / macOS）遇到 BOM 会直接失败，
        #   换行若用 CRLF，文件名末尾会多出一个 \r，同样报 "No such file or directory"。
        Write-Utf8NoBom -Path $sumFile -Content (($lines -join "`n") + "`n")
        Write-Host ''
        Write-Host ("校验和已写入：{0}" -f $sumFile) -ForegroundColor DarkGray
    }
}

# ---------------------------------------------------------------------------
# 汇总
# ---------------------------------------------------------------------------
# 注意：表头刻意用 ASCII。中文列名在等宽字体下的显示宽度是字符数的两倍，
# 而 .NET 的 -f 对齐只按字符数算，用中文会让整张表错位。
Write-Host ''
Write-Host '=========================== 发布汇总 ===========================' -ForegroundColor Cyan
$fmt = '{0,-12} {1,-7} {2,9} {3,9}  {4}'
Write-Host ($fmt -f 'RID', 'Result', 'Size(MB)', 'Time(s)', 'Packages') -ForegroundColor DarkGray
Write-Host ($fmt -f '------------', '-------', '---------', '---------', '--------') -ForegroundColor DarkGray
foreach ($r in $results) {
    $status = if ($r.Ok) { 'OK' } else { 'FAILED' }
    $color  = if ($r.Ok) { 'Gray' } else { 'Red' }
    Write-Host ($fmt -f $r.Rid, $status, ('{0:N1}' -f $r.SizeMB), ('{0:N1}' -f $r.Seconds), $r.Packages) -ForegroundColor $color
}

$failed = @($results | Where-Object { -not $_.Ok })
if ($failed.Count -gt 0) {
    Write-Host '失败详情：' -ForegroundColor Red
    foreach ($f in $failed) {
        Write-Host ("  {0}: {1}" -f $f.Rid, $f.Error) -ForegroundColor Red
    }
    Write-Host ''
}

$okCount = @($results | Where-Object { $_.Ok }).Count
Write-Host ("成功 {0} / {1} 个平台。" -f $okCount, $results.Count) `
    -ForegroundColor $(if ($failed.Count -eq 0) { 'Green' } else { 'Yellow' })
Write-Host ("产物目录：{0}" -f $DistDir)
Write-Host ''

if ($failed.Count -gt 0) { exit 1 }
exit 0
