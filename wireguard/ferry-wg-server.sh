#!/usr/bin/env bash
# 渡口 · WireGuard 服务端一键配置（Linux）
#
# 用法（root 或 sudo）：
#   bash ferry-wg-server.sh install [公网IP或域名] [端口]
#   bash ferry-wg-server.sh add-client <客户端公钥> <客户端隧道IP>
#   bash ferry-wg-server.sh status
#   bash ferry-wg-server.sh uninstall
#
# 为什么 Linux 比 Windows 适合做这件事：
#   Windows 客户端版的 WinNAT 需要 Hyper-V 的 WMI 提供程序，很多机器上根本没有
#   （New-NetNat 报 0x80041010 WBEM_E_INVALID_CLASS）。Linux 只要两行：
#     sysctl -w net.ipv4.ip_forward=1
#     iptables -t nat -A POSTROUTING -s <隧道网段> -o <出口网卡> -j MASQUERADE
#
# 注意：这是「平时用」的线路。
#   校园网完全断网时，本机连不上公网 Linux —— 那种场景仍然靠局域网内的 xt + SSH。
set -euo pipefail

WG_DIR=/etc/wireguard
IFACE=${IFACE:-wg0}
TUN_NET=${TUN_NET:-10.66.67}
TUN_MASK=${TUN_MASK:-24}

need_root() {
  [ "$(id -u)" -eq 0 ] || { echo "请用 root 或 sudo 运行"; exit 1; }
}

install_wg() {
  if command -v wg >/dev/null 2>&1; then
    echo "WireGuard 已安装：$(wg --version 2>/dev/null | head -1)"
    return
  fi
  echo "安装 WireGuard…"
  if command -v apt-get >/dev/null 2>&1; then
    apt-get update -qq && apt-get install -y wireguard qrencode
  elif command -v dnf >/dev/null 2>&1; then
    dnf install -y wireguard-tools qrencode
  elif command -v yum >/dev/null 2>&1; then
    yum install -y epel-release && yum install -y wireguard-tools qrencode
  elif command -v pacman >/dev/null 2>&1; then
    pacman -Sy --noconfirm wireguard-tools qrencode
  else
    echo "未知的包管理器，请手动装 wireguard-tools" >&2
    exit 1
  fi
}

detect_wan_if() {
  # 默认路由所在的网卡就是出口
  ip route show default 2>/dev/null | awk '{print $5; exit}' | head -1
}

cmd_install() {
  need_root
  local endpoint=${1:-}
  local port=${2:-51820}

  install_wg
  mkdir -p "$WG_DIR"
  chmod 700 "$WG_DIR"

  if [ -f "$WG_DIR/$IFACE.conf" ]; then
    echo "$WG_DIR/$IFACE.conf 已存在，不覆盖。要重来先 uninstall。" >&2
    exit 1
  fi

  local priv pub
  priv=$(wg genkey)
  pub=$(echo "$priv" | wg pubkey)

  local wan
  wan=$(detect_wan_if)
  if [ -z "$wan" ]; then
    echo "探测不到默认出口网卡，请设置环境变量 WAN_IF=eth0 后重试" >&2
    exit 1
  fi

  cat > "$WG_DIR/$IFACE.conf" <<EOF
[Interface]
PrivateKey = $priv
Address = $TUN_NET.1/$TUN_MASK
ListenPort = $port
# 客户端之间互相可见（可选；不需要就删掉这行）
# PostUp/PostDown 里管的是 NAT，不是这里
PostUp   = sysctl -w net.ipv4.ip_forward=1
PostUp   = iptables -t nat -A POSTROUTING -s $TUN_NET.0/$TUN_MASK -o $wan -j MASQUERADE
PostUp   = iptables -A FORWARD -i %i -j ACCEPT
PostUp   = iptables -A FORWARD -o %i -j ACCEPT
PostDown = iptables -t nat -D POSTROUTING -s $TUN_NET.0/$TUN_MASK -o $wan -j MASQUERADE || true
PostDown = iptables -D FORWARD -i %i -j ACCEPT || true
PostDown = iptables -D FORWARD -o %i -j ACCEPT || true
EOF
  chmod 600 "$WG_DIR/$IFACE.conf"

  systemctl enable --now "wg-quick@$IFACE"

  echo
  echo "=== 服务端就绪 ==="
  echo "  接口     : $IFACE  ($TUN_NET.1/$TUN_MASK)"
  echo "  端口     : $port/udp"
  echo "  出口网卡 : $wan"
  echo "  服务端公钥: $pub"
  echo
  echo "把这个公钥填到客户端配置的 [Peer] PublicKey"
  if [ -n "$endpoint" ]; then
    echo "客户端 Endpoint = $endpoint:$port"
  else
    echo "客户端 Endpoint = <公网IP>:$port"
  fi
  echo
  echo "⚠ 云服务器必须在**安全组**里放行 UDP $port，否则客户端握手不会有回应。"
  echo "   这不是 iptables 的事，是云厂商控制台那层。"
}

cmd_add_client() {
  need_root
  local pubkey=${1:?用法: add-client <客户端公钥> [客户端隧道IP]}
  local ip=${2:-}
  if [ -z "$ip" ]; then
    # 自动找下一个空位（.2 起）
    local n=2
    while grep -q "AllowedIPs = $TUN_NET.$n/32" "$WG_DIR/$IFACE.conf" 2>/dev/null; do n=$((n+1)); done
    ip="$TUN_NET.$n"
  fi
  wg set "$IFACE" peer "$pubkey" allowed-ips "$ip/32"
  # 持久化
  printf '\n[Peer]\nPublicKey = %s\nAllowedIPs = %s/32\n' "$pubkey" "$ip" >> "$WG_DIR/$IFACE.conf"
  echo "已添加 peer $pubkey -> $ip"
}

cmd_status() {
  need_root
  wg show
  echo
  echo "=== 转发与 NAT ==="
  sysctl net.ipv4.ip_forward
  iptables -t nat -S POSTROUTING | grep -F "$TUN_NET" || echo "（没有 $TUN_NET 的 MASQUERADE 规则）"
}

cmd_uninstall() {
  need_root
  systemctl disable --now "wg-quick@$IFACE" 2>/dev/null || true
  rm -f "$WG_DIR/$IFACE.conf"
  echo "已卸载 $IFACE"
}

case "${1:-}" in
  install)    shift; cmd_install "$@" ;;
  add-client) shift; cmd_add_client "$@" ;;
  status)     cmd_status ;;
  uninstall)  cmd_uninstall ;;
  *)
    sed -n '2,20p' "$0"
    ;;
esac
