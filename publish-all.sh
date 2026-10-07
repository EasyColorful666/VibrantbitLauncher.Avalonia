#!/usr/bin/env bash
# =============================================================================
# VibrantbitLauncher.Avalonia 多平台一键发布（Linux / macOS / Git Bash）
#
# 与 publish-all.ps1 功能等价。一条命令产出 7 个平台的发布包：
#     win-x64  win-x86  win-arm64
#     linux-x64  linux-arm64
#     osx-x64   osx-arm64
#
# 产物结构：
#     publish/<rid>/              原始发布目录
#     dist/<名字>-<rid>.tar.gz    Linux / macOS 用
#     dist/<名字>-<rid>.zip       Windows 用（仅 win-* 出）
#     dist/<名字>-<rid>.app       macOS 的 .app（仅 osx-*，另附同名 tar.gz）
#     dist/SHA256SUMS.txt         全部包的校验和
#
# macOS 首次运行：解压后先双击 install.command（自动补可执行位 + 去下载隔离属性 +
#                 ad-hoc 签名），再把 .app 拖进「应用程序」；详见包内 README-macOS.txt。
#
# 默认框架依赖（不含 .NET 运行时）。目标机器需自备：
#     Windows → .NET 10 Desktop Runtime
#     Linux   → .NET 10 Runtime
#     macOS   → .NET 10 Runtime
# 想免装运行时加 --self-contained。
#
# 用法：
#     ./publish-all.sh                       # 发布全部 7 个平台
#     ./publish-all.sh --rids win-x64        # 只发一个
#     ./publish-all.sh -r win-x64,linux-x64  # 只发两个
#     ./publish-all.sh --self-contained      # 自带运行时
#     ./publish-all.sh --clean               # 先清空 publish/ 与 dist/
#     ./publish-all.sh --no-package          # 只发布，不打包
#     ./publish-all.sh --no-trim             # 关闭按平台剥离（排查用）
#     ./publish-all.sh -h                    # 帮助
# =============================================================================

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_NAME="VibrantbitLauncher.Avalonia"
CSPROJ="$SCRIPT_DIR/$APP_NAME.csproj"

ALL_RIDS=(win-x64 win-x86 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64)

RIDS=("${ALL_RIDS[@]}")
CONFIGURATION="Release"
OUTPUT_ROOT="$SCRIPT_DIR/publish"
DIST_DIR="$SCRIPT_DIR/dist"
SELF_CONTAINED=0
NO_TRIM=0
NO_PACKAGE=0
CLEAN=0

usage() {
    sed -n '2,35p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
    exit 0
}

# ------------------------------- 参数解析 -----------------------------------
while [[ $# -gt 0 ]]; do
    case "$1" in
        -r|--rids)
            [[ $# -ge 2 ]] || { echo "错误：$1 需要一个参数" >&2; exit 2; }
            IFS=',' read -r -a RIDS <<< "$2"
            shift 2
            ;;
        -c|--configuration)
            [[ $# -ge 2 ]] || { echo "错误：$1 需要一个参数" >&2; exit 2; }
            CONFIGURATION="$2"
            shift 2
            ;;
        -o|--output-root)
            [[ $# -ge 2 ]] || { echo "错误：$1 需要一个参数" >&2; exit 2; }
            OUTPUT_ROOT="$2"
            shift 2
            ;;
        -d|--dist-dir)
            [[ $# -ge 2 ]] || { echo "错误：$1 需要一个参数" >&2; exit 2; }
            DIST_DIR="$2"
            shift 2
            ;;
        --self-contained) SELF_CONTAINED=1; shift ;;
        --no-trim)        NO_TRIM=1;        shift ;;
        --no-package)     NO_PACKAGE=1;     shift ;;
        --clean)          CLEAN=1;          shift ;;
        -h|--help)        usage ;;
        *)
            echo "未知参数：$1（用 -h 查看帮助）" >&2
            exit 2
            ;;
    esac
done

# ------------------------------- 前置检查 -----------------------------------
[[ -f "$CSPROJ" ]] || { echo "找不到工程文件：$CSPROJ" >&2; exit 1; }

if ! command -v dotnet >/dev/null 2>&1; then
    echo "未找到 dotnet CLI。请先安装 .NET 10 SDK：https://dotnet.microsoft.com/download/dotnet/10.0" >&2
    exit 1
fi
SDK_VERSION="$(dotnet --version | tr -d '\r')"

# ------------------------------ tar 能力探测 ---------------------------------
# 在 Windows 上打包时，归档里的权限位会全部丢失（Windows 文件系统没有「可执行位」），
# 解压到 macOS / Linux 后：.app 的主可执行文件无法执行、.sh 不能直接 ./ 跑。
# GNU tar 支持 --mode 强制写入权限位，Windows 自带的 bsdtar 不支持。
# 拿不到 GNU tar 时不做处理，靠包内附的 install.command / install.sh 让用户事后修复。
TAR_GNU=0
if command -v tar >/dev/null 2>&1 && tar --version 2>/dev/null | grep -qi 'GNU tar'; then
    TAR_GNU=1
fi

# 打包 tar.gz：$1 = 输出文件，$2 = -C 起始目录，其余 = 要打包的条目
make_tar() {
    local out="$1" dir="$2"; shift 2
    rm -f "$out"
    if [[ $TAR_GNU -eq 1 ]]; then
        # 强制 0755：macOS 的 .app / Linux 的裸二进制都必须带可执行位
        tar --mode='u+rwx,go+rx' -czf "$out" -C "$dir" "$@"
    else
        echo "  [提示] 当前 tar 不支持 --mode，包内权限位可能丢失（包内已附修复脚本）。" >&2
        tar -czf "$out" -C "$dir" "$@"
    fi
}

# 版本号唯一来源：csproj 的 <Version>
VERSION="$(sed -n 's|.*<Version>\([^<]*\)</Version>.*|\1|p' "$CSPROJ" | head -n1)"
[[ -n "$VERSION" ]] || { echo "无法从 csproj 读取 <Version>。" >&2; exit 1; }

# ------------------------------- 打印表头 -----------------------------------
DEPLOY_DESC=$([[ $SELF_CONTAINED -eq 1 ]] && echo "自包含（目标机器免装 .NET 运行时）" || echo "框架依赖（不含 .NET 运行时）")
TRIM_DESC=$([[ $NO_TRIM -eq 1 ]] && echo "关闭" || echo "开启（按 RID 剥掉其它平台后端）")

echo ""
echo "================================================================"
echo " VibrantbitLauncher.Avalonia  多平台一键发布"
echo "================================================================"
printf '  版本号     : %s\n' "$VERSION"
printf '  dotnet SDK : %s\n' "$SDK_VERSION"
printf '  配置       : %s\n' "$CONFIGURATION"
printf '  部署方式   : %s\n' "$DEPLOY_DESC"
printf '  平台剥离   : %s\n' "$TRIM_DESC"
printf '  目标 RID   : %s\n' "${RIDS[*]}"
echo ""

# --------------------------------- 清理 -------------------------------------
if [[ $CLEAN -eq 1 ]]; then
    for d in "$OUTPUT_ROOT" "$DIST_DIR"; do
        if [[ -d "$d" ]]; then rm -rf "$d"; echo "  已清理 $d"; fi
    done
    echo ""
fi
mkdir -p "$OUTPUT_ROOT" "$DIST_DIR"

# ------------------------ Linux/macOS 的赋权脚本 ----------------------------
read -r -d '' INSTALL_SH <<'EOS' || true
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
echo "      或者改用带 --self-contained 发布的版本（已内置运行时，免装）。"
EOS

# ------------------------ macOS 包内的修复脚本与说明 ------------------------
# macOS 比 Linux 多两道坎：
#   ① 从 Windows 打出来的 tar 没有可执行位 → .app 双击没反应；
#   ② Apple Silicon 上「未签名」的 arm64 二进制会被内核直接杀掉（表现为闪退 / 已损坏），
#      必须做一次 ad-hoc 自签名（codesign -s -）。
# 这两步都塞进 install.command，用户双击一下就好。
read -r -d '' MAC_INSTALL_CMD <<'EOS' || true
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
EOS

# 文件名用纯 ASCII，避免 Windows 上打包时文件名编码踩坑；内容是 UTF-8。
read -r -d '' MAC_README <<'EOS' || true
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
EOS

# ------------------------------ 主循环 --------------------------------------
declare -a SUMMARY=()
FAILED=0
OK_COUNT=0

# 表头刻意用 ASCII：中文是双宽字符，而 printf 的对齐只按字符数算，用中文会整张表错位
FMT='%-12s %-7s %9s %9s  %s'

for rid in "${RIDS[@]}"; do
    rid="$(echo "$rid" | tr -d '[:space:]' | tr '[:upper:]' '[:lower:]')"
    [[ -n "$rid" ]] || continue

    is_win=0; is_osx=0
    case "$rid" in win-*) is_win=1 ;; osx-*) is_osx=1 ;; esac

    exe_name="$APP_NAME"
    [[ $is_win -eq 1 ]] && exe_name="$APP_NAME.exe"

    out_dir="$OUTPUT_ROOT/$rid"

    echo "[$rid] 发布中 ..."
    start_ts=$(date +%s)

    rm -rf "$out_dir"

    pub_args=(publish "$CSPROJ" -c "$CONFIGURATION" -r "$rid" -o "$out_dir" --nologo -v quiet)
    if [[ $SELF_CONTAINED -eq 1 ]]; then pub_args+=(--self-contained); else pub_args+=(--no-self-contained); fi
    [[ $NO_TRIM -eq 1 ]] && pub_args+=("-p:SkipPlatformTrim=true")

    if ! dotnet "${pub_args[@]}"; then
        end_ts=$(date +%s)
        echo "[$rid] 失败：dotnet publish 非零退出" >&2
        SUMMARY+=("$(printf "$FMT" "$rid" "FAILED" "-" "$((end_ts-start_ts))s" "-")")
        FAILED=1
        continue
    fi

    exe_path="$out_dir/$exe_name"
    if [[ ! -f "$exe_path" ]]; then
        end_ts=$(date +%s)
        echo "[$rid] 失败：找不到可执行文件 $exe_name" >&2
        echo "      目录内容：$(ls "$out_dir" | tr '\n' ' ')" >&2
        SUMMARY+=("$(printf "$FMT" "$rid" "FAILED" "-" "$((end_ts-start_ts))s" "-")")
        FAILED=1
        continue
    fi

    exe_bytes=$(wc -c < "$exe_path" | tr -d ' ')
    size_mb=$(awk -v b="$exe_bytes" 'BEGIN { printf "%.1f", b/1048576 }')

    # Linux / macOS：赋权脚本必须在打包前写进去
    if [[ $is_win -eq 0 && $NO_PACKAGE -eq 0 ]]; then
        printf '%s\n' "$INSTALL_SH" > "$out_dir/install.sh"
    fi

    packages=""

    if [[ $NO_PACKAGE -eq 0 ]]; then
        if [[ $is_osx -eq 1 ]]; then
            app_stage="$OUTPUT_ROOT/_app_$rid"
            rm -rf "$app_stage"
            bundle="$app_stage/$APP_NAME.app"
            mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources"

            cp "$exe_path" "$bundle/Contents/MacOS/$exe_name"
            cat > "$bundle/Contents/Info.plist" <<PLIST
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
    <string>$VERSION</string>
    <key>CFBundleShortVersionString</key>
    <string>$VERSION</string>
    <key>CFBundleExecutable</key>
    <string>$APP_NAME</string>
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
PLIST

            # macOS 包内必须附上「首次运行修复」脚本与说明：
            # Windows 上打包出来的 tar 丢了可执行位，且 arm64 未签名会被内核拒绝。
            printf '%s\n' "$MAC_INSTALL_CMD" > "$app_stage/install.command"
            printf '%s\n' "$MAC_README"      > "$app_stage/README-macOS.txt"
            chmod +x "$app_stage/install.command" 2>/dev/null || true

            tar_name="$APP_NAME-$rid.tar.gz"
            make_tar "$DIST_DIR/$tar_name" "$app_stage" \
                     "$APP_NAME.app" "install.command" "README-macOS.txt"
            packages="$tar_name"
        else
            tar_name="$APP_NAME-$rid.tar.gz"
            make_tar "$DIST_DIR/$tar_name" "$out_dir" .
            packages="$tar_name"

            if [[ $is_win -eq 1 ]] && command -v zip >/dev/null 2>&1; then
                zip_name="$APP_NAME-$rid.zip"
                rm -f "$DIST_DIR/$zip_name"
                ( cd "$out_dir" && zip -qr "$DIST_DIR/$zip_name" . )
                packages="$packages, $zip_name"
            fi
        fi
    fi

    end_ts=$(date +%s)
    secs=$((end_ts - start_ts))

    echo "[$rid] 完成：${size_mb} MB，用时 ${secs}s${packages:+，包：$packages}"
    SUMMARY+=("$(printf "$FMT" "$rid" "OK" "$size_mb" "${secs}s" "${packages:--}")")
    OK_COUNT=$((OK_COUNT + 1))
done

# ------------------------------ 校验和 --------------------------------------
if [[ $NO_PACKAGE -eq 0 ]]; then
    sum_file="$DIST_DIR/SHA256SUMS.txt"
    rm -f "$sum_file"
    shopt -s nullglob
    entries=("$DIST_DIR"/*)
    shopt -u nullglob

    if [[ ${#entries[@]} -gt 0 ]]; then
        : > "$sum_file"
        for f in "${entries[@]}"; do
            [[ -f "$f" ]] || continue
            [[ "$(basename "$f")" == "SHA256SUMS.txt" ]] && continue
            if command -v sha256sum >/dev/null 2>&1; then
                ( cd "$DIST_DIR" && sha256sum "$(basename "$f")" ) >> "$sum_file"
            elif command -v shasum >/dev/null 2>&1; then
                ( cd "$DIST_DIR" && shasum -a 256 "$(basename "$f")" ) >> "$sum_file"
            fi
        done
        echo ""
        echo "校验和已写入：$sum_file"
    fi
fi

# -------------------------------- 汇总 --------------------------------------
echo ""
echo "=========================== 发布汇总 ==========================="
printf "$FMT\n" "RID" "Result" "Size(MB)" "Time(s)" "Packages"
printf "$FMT\n" "------------" "-------" "---------" "---------" "--------"
for line in "${SUMMARY[@]}"; do printf '%s\n' "$line"; done

echo ""
echo "成功 $OK_COUNT / ${#SUMMARY[@]} 个平台。"
echo "产物目录：$DIST_DIR"
echo ""

exit $FAILED
