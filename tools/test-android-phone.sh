#!/usr/bin/env bash
# 在插着 USB 的手机上装 APK、开隧道、收集证据。
#
# 用法（仓库根目录，git-bash）：
#   tools/test-android-phone.sh              # 完整流程
#   tools/test-android-phone.sh --check      # 只跑自检（隧道已开时）
#   tools/test-android-phone.sh --stop       # 停掉隧道，把手机恢复直连
#
# 前置：
#   · 手机开 USB 调试，并且**在手机上点过「允许通过 USB 安装」**
#     （vivo/OPPO 会拦 adb install，报 INSTALL_FAILED_ABORTED: User rejected permissions；
#       adb uninstall 不需要确认，所以「卸载重装」绕不过去）
#   · 本机 adb 必须是新的（1.0.41+）。system32 里那个 1.0.26 的守护进程起不来，
#     所以脚本优先用 PT_ADB 指定的路径。
#
# 为什么用假 token：这一版验的是「隧道通不通」，不是激活流程。
# App 只要 hasToken() 为真就允许开始借网，塞个假 token 最省事。
set -u

ADB="${PT_ADB:-C:/tmp/ptools/platform-tools/adb.exe}"
PKG="com.ferry.app.debug"
APK="dist/ferry-debug.apk"
PORTAL="http://10.13.24.1:8080"
REMOTE="10.13.24.1"
REMOTE_PORT=1080

# MSYS 会把 /data/... 当成本地路径改写掉，必须关掉
export MSYS_NO_PATHCONV=1

die() { echo "!! $*" >&2; exit 1; }

[ -x "$ADB" ] || die "找不到 adb：$ADB（用 PT_ADB=<path> 指定）"

S="$("$ADB" devices | awk '/\tdevice$/{print $1; exit}')"
[ -n "$S" ] || { "$ADB" devices; die "没有已授权的设备"; }
echo "=== 设备 $S ==="

# 覆盖安装不需要每次都点允许（首次需要）
install_apk() {
    [ -f "$APK" ] || die "找不到 $APK（先跑 tools/build-apk-remote.ps1）"
    echo "=== 安装 $APK ==="
    "$ADB" -s "$S" install -r -g "$APK" | tail -2
}

seed_config() {
    echo "=== 预置配置（跳过激活）==="
    local tmp; tmp="$(mktemp)"
    cat > "$tmp" <<XMLEOF
<?xml version='1.0' encoding='utf-8' standalone='yes' ?>
<map>
    <string name="portalUrl">$PORTAL</string>
    <string name="token">debug-test-token</string>
    <string name="code">debug-test</string>
    <string name="remoteHost">$REMOTE</string>
    <int name="remotePort" value="$REMOTE_PORT" />
</map>
XMLEOF
    "$ADB" -s "$S" push "$tmp" /data/local/tmp/ferry-prefs.xml >/dev/null
    "$ADB" -s "$S" shell "run-as $PKG mkdir -p shared_prefs" >/dev/null 2>&1
    "$ADB" -s "$S" shell "run-as $PKG cp /data/local/tmp/ferry-prefs.xml shared_prefs/ferry.xml"
    rm -f "$tmp"
    # 系统 VPN 授权框要人手点，appops 的 ACTIVATE_VPN 就是它背后的开关
    "$ADB" -s "$S" shell appops set "$PKG" ACTIVATE_VPN allow
}

start_vpn() {
    echo "=== 启动并借网 ==="
    "$ADB" -s "$S" shell am force-stop "$PKG" >/dev/null
    sleep 2
    "$ADB" -s "$S" shell "am start -n $PKG/com.ferry.app.MainActivity --ez autostart true" | head -1
    sleep 14
    "$ADB" -s "$S" shell "ip -4 addr show tun0 2>&1 | head -2"
}

run_selfcheck() {
    echo "=== 自检 ==="
    "$ADB" -s "$S" shell "am start -n $PKG/com.ferry.app.MainActivity --ez selfcheck true" | head -1
    sleep 20
    "$ADB" -s "$S" shell "run-as $PKG cat files/selfcheck.txt"
}

collect() {
    echo "=== 证据：路由表（应看到 46 条公网 CIDR 指向 tun0）==="
    "$ADB" -s "$S" shell "ip route show table all 2>/dev/null | grep -c 'dev tun0'"
    echo "=== 证据：到远端的连接 ==="
    "$ADB" -s "$S" shell "netstat -an 2>/dev/null | grep '$REMOTE:$REMOTE_PORT'" | head -5
    echo "=== 证据：hev 会话日志（最后 10 行）==="
    "$ADB" -s "$S" shell "run-as $PKG tail -10 files/hev/hev.log"
}

stop_all() {
    echo "=== 停掉隧道，恢复直连 ==="
    "$ADB" -s "$S" shell am force-stop "$PKG" >/dev/null
    sleep 2
    "$ADB" -s "$S" shell "ip -4 addr show tun0 2>&1 | head -1"
}

case "${1:---all}" in
    --check) run_selfcheck; collect ;;
    --stop)  stop_all ;;
    --all|*) install_apk; seed_config; start_vpn; run_selfcheck; collect; stop_all ;;
esac
