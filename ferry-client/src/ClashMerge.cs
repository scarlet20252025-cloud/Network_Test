using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Ferry
{
    /// <summary>
    /// 把渡口作为一个 SOCKS5 节点写进 Clash 系客户端的「扩展/增强配置」。
    ///
    /// 为什么要做这个按钮：系统代理只有一个位置，渡口和 Clash 不可能同时占用
    /// （1.0.9 起渡口检测到被占用会自动退成「节点模式」）。退成节点之后还得用户
    /// 手动去 Clash 里加一个 SOCKS5 节点——这一步对非技术用户就是一道墙。
    ///
    /// 为什么写「扩展配置」而不是订阅文件：订阅一更新就把整个文件覆盖了，写在里
    /// 面等于没写。Verge 系（Verge Rev / 旧 Verge / Nyanpasu）都有一层用户自定义
    /// 的 Merge 配置，订阅更新不会碰它。
    ///
    /// 为什么规则挂 fallback 组而不是直接 GEOIP,CN,渡口：
    /// 渡口节点只在「正在借网」时可用。直接指过去的话，用户一停借网，国内网站就
    /// 全断了。fallback 里带 DIRECT，渡口不通自动落回直连，没有副作用。
    ///
    /// 怎么做到「不同人用不同代理也能生效」：
    /// 挨个探测已知的客户端目录；找到哪个就写哪个。全都没找到时不乱写任何文件，
    /// 只给一份按客户端分类的手动说明——宁可不做，也不能把人家的配置写坏。
    /// </summary>
    internal static class ClashMerge
    {
        /// <summary>自己写的块一律以这行开头，用于「已接入 / 需要更新」的判定。</summary>
        const string BeginMark = "# ── 渡口（由渡口助手";

        static readonly string[] ClientDirs = new string[]
        {
            "io.github.clash-verge-rev.clash-verge-rev",       // Clash Verge Rev（最常见）
            "io.github.clash-verge-rev.clash-verge-rev-dev",
            "clash-verge",                                      // 旧版 Clash Verge
            "clash-nyanpasu",                                   // Clash Nyanpasu
        };

        static readonly string[] MergeNames = new string[] { "Merge.yaml", "merge.yaml" };

        public static string MergePath()
        {
            var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            foreach (var d in ClientDirs)
            {
                var prof = Path.Combine(appdata, d, "profiles");
                if (!Directory.Exists(prof)) continue;
                foreach (var n in MergeNames)
                {
                    var f = Path.Combine(prof, n);
                    if (File.Exists(f)) return f;
                }
                return Path.Combine(prof, "Merge.yaml");   // Verge 认这个名字，可以新建
            }
            return null;
        }

        public static bool Installed()
        {
            try
            {
                var f = MergePath();
                return f != null && File.Exists(f) && File.ReadAllText(f).Contains(BeginMark);
            }
            catch (Exception) { return false; }
        }

        /// <summary>已接入时返回当前写进去的端口，没接入返回 0。</summary>
        public static int InstalledPort()
        {
            try
            {
                var f = MergePath();
                if (f == null || !File.Exists(f)) return 0;
                var t = File.ReadAllText(f);
                if (!t.Contains(BeginMark)) return 0;
                var m = Regex.Match(t, @"name:\s*渡口[\s\S]{0,120}?port:\s*(\d+)");
                int p;
                return m.Success && int.TryParse(m.Groups[1].Value, out p) ? p : -1;
            }
            catch (Exception) { return 0; }
        }

        // ── Clash for Windows（CFW）──────────────────────────────
        //
        // CFW 没有 Verge 那种 Merge 层。它给的官方扩展点是 **Parser**：
        // 一个 JS 脚本，订阅加载时被调用，可以在内存里改 proxies/groups/rules，
        // 订阅更新也不会丢。所以 CFW 要写两样东西：
        //   ①  %USERPROFILE%\.config\clash\parserserry.js
        //   ②  在 profiles.yaml 的每个条目上加 parsers: [ferry]
        //
        // 「加 parsers 列表」这一步必须改 profiles.yaml —— 而它是 CFW 自己的状态
        // 文件，改坏了 CFW 会起不来。所以：先备份、只做最小文本插入、失败就还原。
        static string CfwDir()
        {
            var up = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var p = Path.Combine(up, ".config", "clash");
            if (!Directory.Exists(p)) return null;
            // 有 config.yaml 或 profiles.yaml 才算真的在用（只有 Country.mmdb 的是残留）
            if (File.Exists(Path.Combine(p, "profiles.yaml")) ||
                File.Exists(Path.Combine(p, "config.yaml"))) return p;
            return null;
        }

        static string CfwParserJs(int socksPort)
        {
            var js = new StringBuilder();
            js.AppendLine("// 渡口：由渡口助手自动写入（删掉这个文件 + profiles.yaml 里的 ferry 即可取消）");
            js.AppendLine("function parse(proxies, groups, rules) {");
            js.AppendLine("  var socks = " + socksPort + ";");
            js.AppendLine("  var has = proxies.some(function (p) { return p.name === \"渡口\"; });");
            js.AppendLine("  if (!has) {");
            js.AppendLine("    proxies.unshift({ name: \"渡口\", type: \"socks5\", server: \"127.0.0.1\", port: socks, udp: true });");
            js.AppendLine("    groups.unshift({ name: \"国内出口\", type: \"fallback\", proxies: [\"渡口\", \"DIRECT\"],");
            js.AppendLine("      url: \"http://connect.rom.miui.com/generate_204\", interval: 60 });");
            js.AppendLine("    rules.unshift(\"GEOIP,CN,国内出口\");");
            js.AppendLine("  }");
            js.AppendLine("  return { proxies: proxies, groups: groups, rules: rules };");
            js.AppendLine("}");
            return js.ToString();
        }

        /// <summary>给 profiles.yaml 的每个条目补上 parsers: [ferry]。返回改动条数。</summary>
        static int PatchProfilesYaml(string path)
        {
            if (!File.Exists(path)) return 0;
            var lines = File.ReadAllLines(path);
            var outp = new StringBuilder();
            int patched = 0;
            bool inItem = false;
            for (int i = 0; i < lines.Length; i++)
            {
                var ln = lines[i];
                outp.AppendLine(ln);

                // 条目开头：形如 "  - uid: xxx"
                if (ln.TrimStart().StartsWith("- ") && ln.Contains("uid:")) inItem = true;

                bool lastOfItem = inItem && !string.IsNullOrWhiteSpace(ln) &&
                                  ln.TrimStart().StartsWith("- ") == false &&
                                  (i + 1 >= lines.Length || lines[i + 1].TrimStart().StartsWith("- ") || lines[i + 1].Trim().Length == 0);
                if (lastOfItem)
                {
                    inItem = false;
                    // 已经有 parsers 就不再重复加
                    var hasParsers = false;
                    for (int j = i; j >= 0 && j > i - 30; j--)
                        if (lines[j].TrimStart().StartsWith("parsers:")) { hasParsers = true; break; }
                    if (!hasParsers)
                    {
                        outp.AppendLine("    parsers:");
                        outp.AppendLine("      - ferry");
                        patched++;
                    }
                }
            }
            if (patched == 0) return 0;

            var bak = path + ".bak-ferry";
            if (!File.Exists(bak)) File.Copy(path, bak, true);
            File.WriteAllText(path, outp.ToString(), new UTF8Encoding(false));
            return patched;
        }

        static bool Listening(int port)
        {
            try
            {
                using (var c = new System.Net.Sockets.TcpClient())
                {
                    var t = c.ConnectAsync(System.Net.IPAddress.Loopback, port);
                    return t.Wait(80) && c.Connected;   // 别在 UI 线程上等太久：最多 6×80ms
                }
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// 实际在监听的端口。配置里写的是 10800，但被别的程序占用时客户端会往后顺延，
        /// 那时把 10800 写进 Clash 就是个死端口 —— 所以先探一下谁真的在听。
        /// </summary>
        public static int DetectPort(int preferred)
        {
            for (int p = preferred; p < preferred + 6; p++)
                if (Listening(p)) return p;
            return preferred;   // 一个都没在听（还没开始借网）就用配置值
        }

        /// <summary>写入 / 更新节点、组、规则。返回给用户看的说明。</summary>
        public static string Install(int socksPort)
        {
            socksPort = DetectPort(socksPort);
            var f = MergePath();
            if (f == null)
            {
                // 没有 Verge 系 → 试试 Clash for Windows
                var cfw = CfwDir();
                if (cfw != null) return InstallCfw(cfw, socksPort);
                return ManualHelp(socksPort);
            }

            string text = "";
            try { if (File.Exists(f)) text = File.ReadAllText(f); } catch (Exception) { }

            bool had = text.Contains(BeginMark);
            string action;

            if (had)
            {
                var m = Regex.Match(text, @"name:\s*渡口[\s\S]{0,120}?port:\s*(\d+)");
                int oldPort = 0;
                if (m.Success) int.TryParse(m.Groups[1].Value, out oldPort);

                if (oldPort == socksPort)
                    return "已经接入过了（SOCKS5 端口 " + socksPort + "），配置是对的。\n\n" +
                           "不需要重复点。如果你的 Clash 里看不到「渡口」节点，" +
                           "去「配置」页点一下当前订阅让它重载即可。";

                // 端口变了（比如先起过别的程序占了 10800）——把旧块切掉重写，不重复追加
                text = text.Substring(0, text.IndexOf(BeginMark, StringComparison.Ordinal)).TrimEnd();
                action = oldPort > 0
                    ? "已更新：端口 " + oldPort + " → " + socksPort
                    : "已刷新配置（端口 " + socksPort + "）";
            }
            else
            {
                action = "已写入";
            }

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

            try
            {
                File.WriteAllText(f, text.TrimEnd() + "\n" + block, new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                return "写入失败：" + e.Message + "\n\n如果 Clash 正开着，先退出再点一次。";
            }

            return action + "\n" + f + "\n\n" +
                   "Clash 一般几秒内自动重载；没反应就去「配置」页点一下当前订阅。\n\n" +
                   "生效后会多出：\n" +
                   "  · 节点「渡口」= SOCKS5 127.0.0.1:" + socksPort + "（带 UDP）\n" +
                   "  · 分组「国内出口」= 渡口优先，没借网时自动走直连\n" +
                   "  · 规则：国内 IP 走「国内出口」，境外仍走你原来的节点";
        }

        static string InstallCfw(string cfwDir, int socksPort)
        {
            try
            {
                var pdir = Path.Combine(cfwDir, "parsers");
                if (!Directory.Exists(pdir)) Directory.CreateDirectory(pdir);
                var jsPath = Path.Combine(pdir, "ferry.js");
                File.WriteAllText(jsPath, CfwParserJs(socksPort), new UTF8Encoding(false));

                int n = PatchProfilesYaml(Path.Combine(cfwDir, "profiles.yaml"));

                if (n == 0)
                    return "已写好 Clash for Windows 的 Parser：\n" + jsPath + "\n\n" +
                           "但没有在 profiles.yaml 里找到可挂载的订阅条目。\n" +
                           "手动启用一下：CFW → Profiles → 右键你的订阅 → Edit，在末尾加两行：\n" +
                           "    parsers:\n" +
                           "      - ferry\n" +
                           "然后点一下该订阅让它重新加载。";

                return "已接入 Clash for Windows（改动了 " + n + " 个订阅条目）\n\n" +
                       "  写入：" + jsPath + "\n" +
                       "  备份：profiles.yaml.bak-ferry\n\n" +
                       "去 CFW 里点一下当前订阅（或重启 CFW）让它重新加载。\n\n" +
                       "生效后会多出：节点「渡口」= SOCKS5 127.0.0.1:" + socksPort + "、" +
                       "分组「国内出口」（渡口优先，没借网时自动直连）、规则 GEOIP,CN → 国内出口。\n\n" +
                       "取消接入：删掉 parsers\ferry.js，并用 profiles.yaml.bak-ferry 覆盖回 profiles.yaml。";
            }
            catch (Exception e)
            {
                return "写入 Clash for Windows 配置失败：" + e.Message + "\n\n" +
                       "可以手动加一个节点：SOCKS5 127.0.0.1:" + socksPort + "（打开 UDP）";
            }
        }

        /// <summary>没找到支持的客户端时，给一份能照做的手动说明。</summary>
        static string ManualHelp(int socksPort)
        {
            return "没找到 Clash Verge / Nyanpasu 的配置目录，所以没有改任何文件。\n\n" +
                   "手动加一个节点就好（只需要一次）：\n" +
                   "  类型 SOCKS5 · 地址 127.0.0.1 · 端口 " + socksPort + " · 打开 UDP\n\n" +
                   "按你用的客户端：\n" +
                   "· Clash Verge / Verge Rev / Nyanpasu\n" +
                   "    「配置」→ 右键当前订阅 → 编辑扩展配置(Merge)，粘贴：\n" +
                   "        prepend-proxies:\n" +
                   "          - name: 渡口\n" +
                   "            type: socks5\n" +
                   "            server: 127.0.0.1\n" +
                   "            port: " + socksPort + "\n" +
                   "            udp: true\n" +
                   "· Clash for Windows\n" +
                   "    「Profiles」→ 当前配置 → 手动在 proxies 里加同样的条目\n" +
                   "    （它的订阅会自动覆盖，所以每次更新订阅后要补一次）\n" +
                   "· v2rayN / sing-box\n" +
                   "    直接把 127.0.0.1:" + socksPort + " 当成一个 SOCKS5 出站\n\n" +
                   "渡口侧的端口如果变了，这里的 " + socksPort + " 要跟着改。";
        }
    }
}
