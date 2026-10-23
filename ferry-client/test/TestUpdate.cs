// 验证自动更新链路：能否拉到版本 + 版本比较是否正确。
//
// 为什么单独写：GUI 里那个「更新」按钮要有新版本才出现，
// 而实际环境里版本号一直没变过，等于从未验证过。
// 这里直接调 Api.Version + 复刻客户端的比较逻辑，能把整条判断链跑通。
//
// 用法：TestUpdate.exe <portalUrl> [模拟的当前版本]
using System;
using System.Text;

namespace Ferry
{
    static class TestUpdate
    {
        static int Main(string[] args)
        {
            try { return Run(args); }
            catch (Exception e) { Console.WriteLine("异常: " + e.Message); return 9; }
        }

        static int Run(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            string portal = args.Length > 0 ? args[0] : "http://10.13.24.1:8080";
            string mine = args.Length > 1 ? args[1] : "0.8.0.0";

            Console.WriteLine("=== 自动更新判断链验证 ===");
            Console.WriteLine("门户: " + portal);
            Console.WriteLine("本机版本: " + mine);
            Console.WriteLine();

            var v = Api.Version(portal);
            Console.WriteLine("[1] Api.Version -> Ok=" + v.Ok +
                              "  latest=" + (v.Latest ?? "") +
                              "  url=" + (v.Url ?? ""));
            if (!v.Ok)
            {
                Console.WriteLine("    FAIL 拿不到版本信息（门户 /version.json 有问题）");
                return 1;
            }

            int cmp = CompareVer(v.Latest, mine);
            Console.WriteLine("[2] CompareVer(latest=" + v.Latest + ", mine=" + mine + ") = " + cmp);
            Console.WriteLine("    " + (cmp > 0 ? "→ 会提示更新" :
                                        cmp == 0 ? "→ 版本相同，不提示" : "→ 本地更新，不提示"));

            // 三种典型输入的比较结果必须都对
            int fail = 0;
            fail += Check("0.9.9", "0.8.0.0", 1, "新版四段 vs 老版四段");
            fail += Check("0.8.0", "0.8.0.0", 0, "三段 vs 四段，等价");
            fail += Check("0.8.0", "0.8.1.0", -1, "本地更新");
            fail += Check("1.0.0", "0.99.99.0", 1, "主版本号进位");

            Console.WriteLine();
            Console.WriteLine(fail == 0 ? "=== 全部通过 ===" : "=== 有 " + fail + " 项失败 ===");
            return fail;
        }

        static int Check(string a, string b, int expect, string label)
        {
            int got = CompareVer(a, b);
            bool ok = (expect > 0 && got > 0) || (expect == 0 && got == 0) || (expect < 0 && got < 0);
            Console.WriteLine("    " + (ok ? "OK  " : "FAIL") + " " + label +
                              "  (" + a + " vs " + b + " = " + got + ")");
            return ok ? 0 : 1;
        }

        // 和 MainForm.CompareVer 完全一致（按整数比，不是字符串比）
        static int CompareVer(string a, string b)
        {
            var pa = (a ?? "").Split('.');
            var pb = (b ?? "").Split('.');
            int n = Math.Max(pa.Length, pb.Length);
            for (int i = 0; i < n; i++)
            {
                int x = i < pa.Length ? ParseInt(pa[i]) : 0;
                int y = i < pb.Length ? ParseInt(pb[i]) : 0;
                if (x != y) return x > y ? 1 : -1;
            }
            return 0;
        }

        static int ParseInt(string s)
        {
            var sb = new StringBuilder();
            foreach (var c in s) { if (char.IsDigit(c)) sb.Append(c); else break; }
            int v;
            return int.TryParse(sb.ToString(), out v) ? v : 0;
        }
    }
}
