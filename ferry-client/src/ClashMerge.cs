using System;
using System.Text;

namespace Ferry
{
    /// <summary>
    /// 给用户一段可以直接粘贴的 Clash 节点配置。
    ///
    /// **这个类故意不写任何文件。**
    ///
    /// 曾经试过自动写进去：Verge 系写 profiles\Merge.yaml，Clash for Windows 写
    /// config.yaml + cfw-settings.yaml 的 parsers。结果是：
    ///
    ///   · CFW 有绿色版（解压即用），配置目录根本不在 %USERPROFILE%\.config\clash；
    ///   · Verge / Rev / Nyanpasu / CFW 的存储位置和格式各不相同，改一个就要适配一轮；
    ///   · 更糟的是写错了会把人家的翻墙配置弄坏 —— 那比「不做」严重得多。
    ///
    /// 而且这条路本来就是在绕弯：用户既然装了 Clash，他要的是「借网之后还能翻墙」，
    /// 不是「在 Clash 里多一个节点」。真正的解法在客户端自己那边（见 ProxyServer 的
    /// 分流：国内走渡口、境外交给用户本机的代理），用户什么都不用配。
    ///
    /// 所以这里退回到最朴素的形态：把配置片段显示出来，需要的人自己复制。
    /// </summary>
    internal static class ClashMerge
    {
        /// <summary>渡口节点的 YAML 片段（贴进 Clash 的 proxies 里即可）。</summary>
        public static string Snippet(int socksPort)
        {
            var sb = new StringBuilder();
            sb.AppendLine("  - name: 渡口");
            sb.AppendLine("    type: socks5");
            sb.AppendLine("    server: 127.0.0.1");
            sb.AppendLine("    port: " + socksPort);
            sb.AppendLine("    udp: true");
            return sb.ToString();
        }

        /// <summary>完整的说明（按钮点开时显示）。</summary>
        public static string Help(int socksPort)
        {
            return
                "一般情况不需要做任何事。\n\n" +
                "渡口借网时会自己分流：国内的网站走渡口，境外的交给你本机已经在用的\n" +
                "代理（Clash / 加速器都行），所以 Clash 照常开着就好。\n\n" +
                "—— 只有下面这种情况才需要手动加节点 ——\n" +
                "你希望「某个境外网站也走渡口出口」（比如它在国内有 CDN，走渡口更快）。\n" +
                "那就在你的 Clash 里加一个 SOCKS5 节点：\n\n" +
                "  地址 127.0.0.1    端口 " + socksPort + "    勾选 UDP\n\n" +
                "YAML 片段（贴进 proxies: 下面）：\n\n" +
                Snippet(socksPort) + "\n" +
                "加完之后把需要的规则指到「渡口」这个节点上。";
        }
    }
}
