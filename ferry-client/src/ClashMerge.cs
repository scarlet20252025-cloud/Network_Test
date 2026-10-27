using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Ferry
{
    /// <summary>
    /// 把渡口作为一个 SOCKS5 节点接进 Clash 系客户端。
    ///
    /// 为什么需要：系统代理只有一个位置，渡口和 Clash 不可能同时占用它（1.0.9 起
    /// 渡口检测到被占用会自动退成「节点模式」）。退成节点之后还得用户手动加节点，
    /// 这一步对非技术用户就是一道墙。
    ///
    /// 两类客户端写法完全不同：
    ///
    ///   Verge 系（Verge / Rev / Nyanpasu）
    ///     有官方的「扩展配置」Merge.yaml，写 prepend-proxies 即可，订阅更新不覆盖。
    ///
    ///   Clash for Windows（CFW）
    ///     没有 Merge 层。运行配置是 %USERPROFILE%\.config\clash\config.yaml
    ///     （由订阅生成），直接改能立刻生效，但订阅一更新就被覆盖。所以还要写它的
    ///     Parser（cfw-settings.yaml 里的 parsers:，一段 module.exports.parse 的 JS），
    ///     Parser 在每次加载订阅时执行，能扛住更新。
    ///     两样都写：config.yaml 负责「现在就能用」，Parser 负责「以后不被覆盖」。
    /// </summary>
    internal static class ClashMerge
    {
        const string BeginMark = "# ── 渡口（由渡口助手";

        static readonly string[] VergeDirs = new string[]
        {
            "io.github.clash-verge-rev.clash-verge-rev",
            "io.github.clash-verge-rev.clash-verge-rev-dev",
            "clash-verge",
            "clash-nyanpasu",
        };

        // ══════════════════ Verge 系 ══════════════════

        public static string MergePath()
        {
            var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            foreach (var d in VergeDirs)
            {
                var prof = Path.Combine(appdata, d, "profiles");
                if (!Directory.Exists(prof)) continue;
                foreach (var n in new string[] { "Merge.yaml", "merge.yaml" })
                {
                    var f = Path.Combine(prof, n);
                    if (File.Exists(f)) return f;
                }
                return Path.Combine(prof, "Merge.yaml");
            }
            return null;
        }

        // ══════════════════ Clash for Windows ══════════════════

        public static string CfwDir()
        {
            var up = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var p = Path.Combine(up, ".config", "clash");
            if (!Directory.Exists(p)) return null;
            if (File.Exists(Path.Combine(p, "config.yaml")) ||
                File.Exists(Path.Combine(p, "profiles.yaml")) ||
                File.Exists(Path.Combine(p, "cfw-settings.yaml"))) return p;
            return null;   // 只有 Country.mmdb 的是残留，不算
        }

        // ══════════════════ 公共 ══════════════════

        public static bool Installed()
        {
            try
            {
                var f = MergePath();
                if (f != null && File.Exists(f) && File.ReadAllText(f).Contains(BeginMark)) return true;
                var cfw = CfwDir();
                if (cfw != null)
                {
                    var cfg = Path.Combine(cfw, "config.yaml");
                    if (File.Exists(cfg) && File.ReadAllText(cfg).Contains("name: 渡口")) return true;
                }
                return false;
            }
            catch (Exception) { return false; }
        }

        public static string Install(int socksPort)
        {
            var f = MergePath();
            if (f != null) return InstallVerge(f, socksPort);

            var cfw = CfwDir();
            if (cfw != null) return InstallCfw(cfw, socksPort);

            return ManualHelp(socksPort);
        }

        static string InstallVerge(string f, int socksPort)
        {
            string text = "";
            try { if (File.Exists(f)) text = File.ReadAllText(f); } catch (Exception) { }

            string action;
            if (text.Contains(BeginMark))
            {
                var m = Regex.Match(text, @"name:\s*渡口[\s\S]{0,120}?port:\s*(\d+)");
                int oldPort = 0;
                if (m.Success) int.TryParse(m.Groups[1].Value, out oldPort);
                if (oldPort == socksPort)
                    return "已经接入过了（SOCKS5 端口 " + socksPort + "），配置是对的，不需要重复点。\n\n" +
                           "如果 Clash 里看不到「渡口」节点，去「配置」页点一下当前订阅让它重载。";

                text = text.Substring(0, text.IndexOf(BeginMark, StringComparison.Ordinal)).TrimEnd();
                action = oldPort > 0 ? "已更新：端口 " + oldPort + " → " + socksPort : "已刷新配置";
            }
            else action = "已写入";

            var block = new StringBuilder();
            block.AppendLine();
            block.AppendLine(BeginMark + "自动写入；整段删掉就等于取消接入） ──");
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

            try { File.WriteAllText(f, text.TrimEnd() + "\n" + block, new UTF8Encoding(false)); }
            catch (Exception e) { return "写入失败：" + e.Message + "\n\nClash 正开着的话先退出再试。"; }

            return action + "\n" + f + "\n\n" +
                   "Clash 一般几秒内自动重载；没反应就去「配置」页点一下当前订阅。\n\n" +
                   "生效后会多出：节点「渡口」= SOCKS5 127.0.0.1:" + socksPort + "、" +
                   "分组「国内出口」（渡口优先，没借网时自动直连）、规则 GEOIP,CN → 国内出口。";
        }

        /// <summary>
        /// CFW：既要「现在生效」（改 config.yaml），也要「以后不被订阅覆盖」（写 Parser）。
        /// </summary>
        static string InstallCfw(string dir, int socksPort)
        {
            var cfgPath = Path.Combine(dir, "config.yaml");
            var settingsPath = Path.Combine(dir, "cfw-settings.yaml");

            var notes = new List<string>();
            bool cfgOk = false, parserOk = false;

            if (File.Exists(cfgPath))
            {
                try
                {
                    var r = PatchClashConfig(cfgPath, socksPort);
                    if (r == "ok") { cfgOk = true; notes.Add("已写入 config.yaml（重启 CFW 后生效）"); }
                    else if (r == "already") { cfgOk = true; notes.Add("config.yaml 里已经有「渡口」节点"); }
                    else if (r == "noproxies") notes.Add("config.yaml 里找不到 proxies: 段，没有直接改它（怕写坏）");
                }
                catch (Exception e) { notes.Add("改 config.yaml 失败：" + e.Message); }
            }
            else notes.Add("没找到 config.yaml（CFW 还没加载过订阅？）");

            try
            {
                if (AddCfwParser(settingsPath, socksPort))
                {
                    parserOk = true;
                    notes.Add("已写 cfw-settings.yaml 的 Parser（订阅更新也不会丢）");
                }
                else notes.Add("cfw-settings.yaml 里已有 parsers 段，没有动它（避免覆盖你自己写的预处理）");
            }
            catch (Exception e) { notes.Add("写 Parser 失败：" + e.Message); }

            if (!cfgOk && !parserOk) return ManualHelp(socksPort);

            var sb = new StringBuilder();
            sb.AppendLine("已接入 Clash for Windows（SOCKS5 127.0.0.1:" + socksPort + "）");
            sb.AppendLine();
            foreach (var n in notes) sb.AppendLine("  · " + n);
            sb.AppendLine();
            sb.AppendLine("下一步：在 CFW 里点一下当前订阅（或重启 CFW），然后看节点列表里有没有「渡口」。");
            sb.AppendLine();
            sb.AppendLine("说明：config.yaml 是 CFW 从订阅生成的，订阅更新会被覆盖 —— " +
                          "Parser 那一步就是为了兜住这种情况，每次加载订阅都会重新加上节点。");
            return sb.ToString();
        }

        /// <summary>在 config.yaml 里插节点/组/规则。返回 ok / already / noproxies。</summary>
        static string PatchClashConfig(string path, int socksPort)
        {
            var text = File.ReadAllText(path);
            if (text.Contains("name: 渡口")) return "already";

            var lines = new List<string>(text.Replace("\r\n", "\n").Split('\n'));

            int iProxies = -1, iGroups = -1, iRules = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                var t = lines[i].TrimEnd();
                if (t == "proxies:") iProxies = i;
                else if (t == "proxy-groups:") iGroups = i;
                else if (t == "rules:") iRules = i;
            }
            if (iProxies < 0) return "noproxies";

            // 从后往前插，避免行号漂移
            var inserts = new List<KeyValuePair<int, List<string>>>();
            if (iRules >= 0)
                inserts.Add(new KeyValuePair<int, List<string>>(iRules + 1, new List<string> { "  - GEOIP,CN,国内出口" }));
            if (iGroups >= 0)
                inserts.Add(new KeyValuePair<int, List<string>>(iGroups + 1, new List<string>
                {
                    "  - name: 国内出口",
                    "    type: fallback",
                    "    proxies: [渡口, DIRECT]",
                    "    url: http://connect.rom.miui.com/generate_204",
                    "    interval: 60"
                }));
            inserts.Add(new KeyValuePair<int, List<string>>(iProxies + 1, new List<string>
            {
                "  - name: 渡口",
                "    type: socks5",
                "    server: 127.0.0.1",
                "    port: " + socksPort,
                "    udp: true"
            }));

            inserts.Sort(delegate (KeyValuePair<int, List<string>> a, KeyValuePair<int, List<string>> b)
            { return b.Key.CompareTo(a.Key); });
            foreach (var ins in inserts) lines.InsertRange(ins.Key, ins.Value);

            File.Copy(path, path + ".bak-ferry", true);
            File.WriteAllText(path, string.Join("\n", lines.ToArray()), new UTF8Encoding(false));
            return "ok";
        }

        /// <summary>
        /// 往 cfw-settings.yaml 追加 Parser。已有 parsers 段就不动（用户可能自己写了预处理）。
        /// 返回 true = 写成功。
        /// </summary>
        static bool AddCfwParser(string path, int socksPort)
        {
            string text = "";
            if (File.Exists(path)) text = File.ReadAllText(path);
            if (text.Contains("渡口")) return true;
            if (Regex.IsMatch(text, "(?m)^parsers:")) return false;

            var sb = new StringBuilder();
            if (text.Trim().Length > 0) sb.AppendLine(text.TrimEnd());
            sb.AppendLine("parsers:");
            sb.AppendLine("  - reg: ^.*$");
            sb.AppendLine("    code: |");
            sb.AppendLine("      // 渡口：由渡口助手自动写入（删掉这段 + config.yaml 里的渡口节点即可取消）");
            sb.AppendLine("      module.exports.parse = async (raw, { yaml }, info) => {");
            sb.AppendLine("        let cfg;");
            sb.AppendLine("        try { cfg = yaml.parse(raw); } catch (e) { return raw; }");
            sb.AppendLine("        if (!cfg || !Array.isArray(cfg.proxies)) return raw;");
            sb.AppendLine("        if (cfg.proxies.some((p) => p.name === '渡口')) return raw;");
            sb.AppendLine("        cfg.proxies.unshift({ name: '渡口', type: 'socks5', server: '127.0.0.1', port: " + socksPort + ", udp: true });");
            sb.AppendLine("        if (!Array.isArray(cfg['proxy-groups'])) cfg['proxy-groups'] = [];");
            sb.AppendLine("        cfg['proxy-groups'].unshift({ name: '国内出口', type: 'fallback', proxies: ['渡口', 'DIRECT'], url: 'http://connect.rom.miui.com/generate_204', interval: 60 });");
            sb.AppendLine("        if (!Array.isArray(cfg.rules)) cfg.rules = [];");
            sb.AppendLine("        cfg.rules.unshift('GEOIP,CN,国内出口');");
            sb.AppendLine("        return yaml.stringify(cfg);");
            sb.AppendLine("      };");

            if (text.Trim().Length > 0) File.Copy(path, path + ".bak-ferry", true);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return true;
        }

        static string ManualHelp(int socksPort)
        {
            return "没找到 Clash Verge / Clash for Windows 的配置目录，所以没有改任何文件。\n\n" +
                   "手动加一个节点就好（只需要一次）：\n" +
                   "  类型 SOCKS5 · 地址 127.0.0.1 · 端口 " + socksPort + " · 打开 UDP\n\n" +
                   "按你用的客户端：\n" +
                   "· Clash Verge / Rev / Nyanpasu\n" +
                   "    「配置」→ 右键当前订阅 → 编辑扩展配置(Merge)，粘贴：\n" +
                   "        prepend-proxies:\n" +
                   "          - name: 渡口\n" +
                   "            type: socks5\n" +
                   "            server: 127.0.0.1\n" +
                   "            port: " + socksPort + "\n" +
                   "            udp: true\n" +
                   "· Clash for Windows\n" +
                   "    确认配置目录是 %USERPROFILE%\\.config\\clash（里面要有 config.yaml）。\n" +
                   "    在 Settings → Profiles → Parsers 里粘贴渡口的 Parser 脚本。\n" +
                   "· v2rayN / sing-box\n" +
                   "    把 127.0.0.1:" + socksPort + " 当成一个 SOCKS5 出站。";
        }
    }
}
