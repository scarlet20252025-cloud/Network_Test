using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Ferry
{
    /// <summary>
    /// 清理 1.0.9~1.0.12 期间写进 Clash Verge 的「渡口」配置块。
    ///
    /// **这是个定时炸弹**：那几版会往 `profiles\Merge.yaml` 写
    ///
    ///     prepend-rules:
    ///       - GEOIP,CN,国内出口
    ///
    /// 这条规则排在所有规则**最前面**，把所有国内流量劫持到「国内出口」组
    /// → fallback [渡口, DIRECT] → 渡口没借网时端口是空的
    /// → **国内网站全部打不开**（用户实测：外网能开，国内全死）。
    ///
    /// 1.0.13 起不再写配置文件了，但**已经写进去的不会自己消失**。
    /// 所以启动时主动清一次：只删带我们自己标记的那一段，其它内容一个字不动。
    /// 清完提示用户重载一下订阅（Clash 不会自动重新读 Merge）。
    /// </summary>
    internal static class ClashCleanup
    {
        const string Marker = "渡口（由渡口助手";

        static readonly string[] VergeDirs = new string[]
        {
            "io.github.clash-verge-rev.clash-verge-rev",
            "io.github.clash-verge-rev.clash-verge-rev-dev",
            "clash-verge",
            "clash-nyanpasu",
        };

        /// <summary>
        /// 清掉历史遗留的渡口配置块。返回清理的文件路径（没清则返回 null）。
        ///
        /// 为什么只在启动时做一次、且不重试：这是**补救**，不是常规流程。
        /// 失败也不该影响借网（清理不成功最多是国内站还是打不开，
        /// 而借网本身照常工作）。
        /// </summary>
        public static string CleanLegacy(Action<string> log)
        {
            try
            {
                var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                foreach (var d in VergeDirs)
                {
                    var prof = Path.Combine(appdata, d, "profiles");
                    if (!Directory.Exists(prof)) continue;
                    foreach (var n in new string[] { "Merge.yaml", "merge.yaml" })
                    {
                        var f = Path.Combine(prof, n);
                        if (!File.Exists(f)) continue;
                        if (!File.ReadAllText(f).Contains(Marker)) continue;

                        var text = File.ReadAllText(f);
                        var cut = text.IndexOf(Marker, StringComparison.Ordinal);
                        // 连标记行前面的注释头一起切掉
                        var lineStart = text.LastIndexOf('\n', Math.Max(0, cut - 1));
                        if (lineStart < 0) lineStart = 0;
                        var head = text.Substring(0, lineStart).TrimEnd();

                        File.Copy(f, f + ".bak-ferry", true);
                        File.WriteAllText(f, head + "\n", new UTF8Encoding(false));

                        if (log != null)
                            log("已清理旧版本留在 Clash 配置里的渡口规则（备份 .bak-ferry），" +
                                "请重载一次订阅");
                        return f;
                    }
                }
            }
            catch (Exception) { }
            return null;
        }
    }
}
