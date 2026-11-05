# relay/ferry-socks5-server.py 归档说明

这个文件是**生产正在运行的 SOCKS5 / UDP 中继**的源码快照，
从 pcs (`10.13.24.1`) 的
`C:\Users\PCserver\AppData\Local\Ferry\ferry-socks5-server.py` 拉取。

## 为什么要在仓库里留一份

2026-11-05 之前，这份源码**只存在于 pcs 那一台机器上**，本地仓库里没有。
后果是：排查中继问题时无法在隔离环境复现、无法 diff、无法回滚到
「某个已知良好的版本」，只能在生产上改一段、重启一次、看日志——
最终导致一次 `wait_for` 参数写错的补丁直接把生产中继打挂、用户断网。

**服务端源码必须和客户端、门户一样进版本库。**

## 部署方式（改这份文件之后）

1. 先在隔离端口做完整验证，**不能只测 SOCKS5 握手**：
   ```powershell
   python .\ferry-socks5-server.py --port 1081 --udp --rate-mb 10
   ```
   用脚本跑 ≥100 次完整 `CONNECT`（读到真实 HTTP 字节），
   与生产 1080 做 A/B 对照。
2. 验证通过后：
   ```powershell
   Copy-Item <new> C:\Users\PCserver\AppData\Local\Ferry\ferry-socks5-server.py -Force
   schtasks /end /tn FerrySocks5
   schtasks /run /tn FerrySocks5
   ```
3. 部署脚本必须自带：备份、`py_compile`、监听检查、
   **完整 CONNECT smoke**、失败自动回滚。
4. 动生产前确认有独立于该服务的访问通道（SSH 依赖网络，
   中继挂了可能连 SSH 都进不去）。

`py_compile` 通过 **不等于** 能运行；握手通过 **不等于** 能建上游连接。

## 本快照的版本

2026-11-05 22:45 部署，含上游连接失败重试：

```python
_t = 2.5
_tries = 0
while True:
    _tries += 1
    try:
        r, w = await asyncio.wait_for(
            asyncio.open_connection(host, port, family=socket.AF_INET),
            timeout=_t)
        break
    except Exception:
        if _tries >= 3:
            raise
        _t = 2.5
```

### 为什么是重试而不是别的

- 现象：经中继建连 4–15% 失败，形态恒为「等满 8 秒 → SOCKS5 REP5」。
- 逐项排除：DNS（pcs 上 1.1ms/次）、IPv6、端口耗尽（654/16384）、
  `backlog`、同步 `log()` 刷盘、强制 IPv4、把域名预解析成单个 IP、
  `asyncio.open_connection` → 换成 `socket.create_connection`。
- **关键对照**：在 pcs 本机上，传域名 `socket.create_connection` 30/30 成功；
  钉死成单个 A 记录 28/30；普通 TCP 建连偶发 8 秒才完成。
  → 是 **pcs 到外网这一段偶发 SYN 丢失/重传**，不是中继逻辑。
- SOCKS5 在 `_open_upstream` 成功前还没有回复客户端，
  所以在中继里重试对客户端完全透明。

验证数据（隔离 1081，150 次全 CONNECT + 20 次真实 HTTP GET）：

| | 生产 1080（无重试） | 测试 1081（重试 x3） |
|---|---|---|
| 完整 CONNECT | 136/150，失败 9.3% | **150/150，0%** |
| 真实 HTTP GET | 17/20，失败 15% | **20/20，0%** |
| p50 | 55 ms | 55 ms（不变） |

上线后复测：80/80 全 CONNECT、15/15 HTTP GET、**0% 失败**。

代价：约 10% 的连接需要重试一次，p90 从 67ms 变成约 1 秒——
但这些连接原本是 8 秒后 REP5 失败，现在是 2.5 秒内成功。

## 硬规则

改这份文件时，参考 `docs/踩坑记录.md` 的事故记录。
**一次只改一处、先在隔离端口验证、只重启一次、带自动回滚。**
