#!/usr/bin/env bash
# push-with-retry.sh — 渡口环境下推送 git 仓库，带自动重试
#
# 为什么需要：渡口链路到 github 偶发 TLS 抖动（schannel 报 handshake failed、
# openssl 报 unexpected eof），单次推送成功率约 50%。重试 5 次基本都能成。
#
# 必需参数（缺一不可）：
#   http.version=HTTP/1.1     本机走 HTTP/2 会卡 TLS renegotiate
#   http.sslBackend=openssl   schannel 在 CONNECT 隧道上对 close_notify 过严
#   http.proxy=http://127.0.0.1:10809  dual-proxy（带 IP 择优）
#
# 用法：./push-with-retry.sh [branch] [max_tries]

set -uo pipefail

BRANCH="${1:-main}"
MAX_TRIES="${2:-5}"
PROXY="http://127.0.0.1:10809"

GIT_OPTS=(
  -c http.version=HTTP/1.1
  -c "http.proxy=${PROXY}"
  -c http.sslBackend=openssl
)

for i in $(seq 1 "$MAX_TRIES"); do
  echo "==> 第 $i/$MAX_TRIES 次推送 origin $BRANCH"
  if git "${GIT_OPTS[@]}" push origin "$BRANCH" 2>&1; then
    echo "✅ 推送成功"
    exit 0
  fi
  if [ "$i" -lt "$MAX_TRIES" ]; then
    echo "  失败，3 秒后重试"
    sleep 3
  fi
done

echo "❌ 推送失败 $MAX_TRIES 次"
echo "排查顺序："
echo "  1. 代理是否活着：curl -x $PROXY -s -o /dev/null -w '%{http_code}' https://github.com"
echo "  2. dual-proxy 是否在跑：Get-CimInstance Win32_Process -Filter \"Name='node.exe'\""
echo "  3. ssh -D 是否在跑：Get-CimInstance Win32_Process -Filter \"Name='ssh.exe'\""
exit 1
