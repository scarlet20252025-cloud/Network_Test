using System;
using System.IO;
using System.Text;

namespace Ferry
{
    /// <summary>
    /// 把渡口作为一个 SOCKS5 节点写进 Clash Verge 的「全局扩展配置」(Merge.yaml)。
    ///
    /// 为什么需要：系统代理只有一个位置，渡口和 Clash 不可能同时占用它（1.0.9 起
    /// 渡口检测到被占用会自动退成「节点模式」）。退成节点之后，用户还得手动去
    /// Clash 里加一个 SOCKS5 节点——这一步对非技术用户就是一道墙，所以做成按钮。
    ///
    /// 为什么写 Merge.yaml 而不是订阅文件：订阅一更新就把整个文件覆盖了，写在里面
    /// 等于没写。Merge.yaml 是 Clash Verge 官方提供的「增强层」，订阅更新不动它。
    ///
    /// 为什么规则挂一个 fallback 组而不是直接 GEOIP,CN,渡口：
    /// 渡口节点只有「正在借网」时才可用。直接指过去的话，用户一停借网，国内网站
    /// 就全断了。fallback 组里带 DIRECT，渡口不通会自动落回直连，不会有副作用。
    /// </summary>
    internal static class ClashMerge
    {
        const string Marker = "渡口";   // 用来判断是否已经写过

        static string VergeDir()
        {
            var a = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var p = Path.Combine(a, "io.github.clash-verge-rev.clash-verge-rev");
            return Directory.Exists(p) ? p : null;
        }

        public static string MergePath()
        {
            var d = VergeDir();
            if (d == null) return null;
            var prof = Path.Combine(d, "profiles");
            if (!Directory.Exists(prof)) return null;
            return Path.Combine(prof, "Merge.yaml");
        }

        public static bool Installed()
        {
            try
            {
                var f = MergePath();
                return f != null && File.Exists(f) && File.ReadAllText(f).Contains(Marker);
            }
            catch (Exception) { return false; }
        }

        /// <summary>写入节点+组+规则。返回给用户看的说明。</summary>
        public static string Install(int socksPort)
        {
            var f = MergePath();
            if (f == null)
            {
                return "没找到 Clash Verge 的配置目录。\n\n" +
                       "如果你用的是别的 Clash 客户端，请手动添加一个节点：\n" +
                       "  类型 SOCKS5 · 地址 127.0.0.1 · 端口 " + socksPort + " · 勾选 UDP\n\n" +
                       "Clash Verge 的预期位置：\n%APPDATA%\\io.github.clash-verge-rev.clash-verge-rev\\profiles\\Merge.yaml";
            }

            string text = "";
            try { if (File.Exists(f)) text = File.ReadAllText(f); } catch (Exception) { }

            if (text.Contains(Marker))
                return "已经接入过了 —— Clash 的扩展配置里已经有「渡口」节点。\n\n" +
                       "如果端口变了（比如 " + socksPort + "），把 Merge.yaml 里那个 port 改一下即可。";

            var block = new StringBuilder();
            block.AppendLine();
            block.AppendLine("# ── 渡口（由渡口助手自动写入；删掉这一段就等于取消接入） ──");
            block.AppendLine("prepend-proxies:");
            block.AppendLine("  - name: 渡口");
            block.AppendLine("    type: socks5");
            block.AppendLine("    server: 127.0.0.1");
            block.AppendLine("    port: " + socksPort);
            block.AppendLine("    udp: true");
            block.AppendLine();
            block.AppendLine("prepend-proxy-groups:");
            block.AppendLine("  - name: 国内出口");
            block.AppendLine("    type: fallback");
            block.AppendLine("    proxies: [渡口, DIRECT]");
            block.AppendLine("    url: http://connect.rom.miui.com/generate_204");
            block.AppendLine("    interval: 60");
            block.AppendLine();
            block.AppendLine("prepend-rules:");
            block.AppendLine("  - GEOIP,CN,国内出口");

            if (text.Trim().Length == 0)
                text = "# Profile Enhancement Merge Template for Clash Verge\n\nprofile:\n  store-selected: true\n";

            try
            {
                File.WriteAllText(f, text.TrimEnd() + "\n" + block, new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                return "写入失败：" + e.Message + "\n\n文件可能被占用，先关掉 Clash Verge 再试。";
            }

            return "已写入 Clash 的全局扩展配置（" + f + "）。\n\n" +
                   "Clash Verge 一般几秒内自动重载；没反应就去「配置」页点一下当前订阅。\n\n" +
                   "生效后你会看到：\n" +
                   "  · 节点「渡口」= SOCKS5 127.0.0.1:" + socksPort + "\n" +
                   "  · 分组「国内出口」= 渡口优先，渡口没开时自动走直连\n" +
                   "  · 规则：国内 IP 走「国内出口」，境外仍走你原来的节点（翻墙不受影响）";
        }
    }
}
