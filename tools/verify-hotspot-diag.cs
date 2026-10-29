using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using Ferry;

// 把诊断结果写文件（UTF-8），不依赖 stdout —— 提权 + 重定向 + 中文
// 三者一起时 stdout 编码会乱，前面试过一次输出全丢。
static class HotspotDiag2
{
    static StringBuilder _sb = new StringBuilder();
    static void W(string s) { _sb.AppendLine(s); }

    static Type _tmT, _niT;
    static MethodInfo _asTaskGeneric;

    static void Main(string[] args)
    {
        // ⚠ 这个程序会**真的启动热点**，在单网卡机器上会把当前 WiFi 顶掉。
        // 2026-10-29 已经在用户机上因此断过一次网，所以加硬闸。
        if (args == null || args.Length == 0 || args[0] != "I-ACCEPT-WIFI-DROP")
        {
            Console.WriteLine("拒绝执行：这个程序会启停热点，可能顶掉本机 WiFi。");
            Console.WriteLine("只允许在 10.113.66.6 测试机上跑；确实要跑就传参 I-ACCEPT-WIFI-DROP");
            Environment.Exit(2);
            return;
        }
        string logPath = @"C:\tmp\verify\hsdiag2.log";
        try { Run(); }
        catch (Exception e)
        {
            W("!!! 顶层异常: " + e.GetType().Name);
            W("    Message: " + e.Message);
            W("    Stack: " + e.StackTrace);
            var ie = e.InnerException;
            int n = 0;
            while (ie != null && n++ < 3)
            {
                W("    Inner: " + ie.GetType().Name + " / " + ie.Message);
                ie = ie.InnerException;
            }
        }
        File.WriteAllText(logPath, _sb.ToString(), new UTF8Encoding(false));
    }

    static void Run()
    {
        var err = Hotspot.EnsureLoaded();
        W("EnsureLoaded = " + (err == "" ? "(ok)" : err));
        if (err != "") return;

        var t = typeof(Hotspot);
        foreach (var f in new string[] { "_lastStatus", "_lastBandTried" })
        {
            var fi = t.GetField(f, BindingFlags.NonPublic | BindingFlags.Static);
            W("  " + f + " = " + (fi == null ? "(无此字段)" : Convert.ToString(fi.GetValue(null))));
        }

        _niT = Type.GetType("Windows.Networking.Connectivity.NetworkInformation, Windows.Networking.Connectivity, ContentType=WindowsRuntime");
        _tmT = Type.GetType("Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager, Windows.Networking.NetworkOperators, ContentType=WindowsRuntime");
        W("  _niT = " + (_niT == null ? "null" : "ok") + "   _tmT = " + (_tmT == null ? "null" : "ok"));

        _asTaskGeneric = FindAsTask();
        W("  AsTask = " + (_asTaskGeneric == null ? "null" : "ok"));

        // 选共享源（复制 1.0.45 的判据，但打印每一步）
        object share = null;
        string shareName = "";
        W("=== 配置档逐条 ===");
        foreach (var p in (IEnumerable)_niT.GetMethod("GetConnectionProfiles").Invoke(null, null))
        {
            var pn = Convert.ToString(p.GetType().GetProperty("ProfileName").GetValue(p, null));
            var lvl = Convert.ToString(p.GetType().GetMethod("GetNetworkConnectivityLevel").Invoke(p, null));
            if (lvl == "None") continue;
            var ad = p.GetType().GetProperty("NetworkAdapter").GetValue(p, null);
            int iana = 0; string id = "";
            if (ad != null)
            {
                iana = Convert.ToInt32(ad.GetType().GetProperty("IanaInterfaceType").GetValue(ad, null));
                id = Convert.ToString(ad.GetType().GetProperty("NetworkAdapterId").GetValue(ad, null));
            }
            W("  " + pn + "  level=" + lvl + "  iana=" + iana + "  id=" + id);
            if (share == null && (iana == 71 || iana == 6) && !pn.StartsWith("本地连接") && !pn.StartsWith("Local Area"))
            {
                share = p; shareName = pn;
            }
        }
        if (share == null) { W("!! 没有可用共享源"); return; }
        W("=== 共享源 = " + shareName + " ===");

        var createM = _tmT.GetMethods();
        MethodInfo create = null;
        foreach (var m in createM)
            if (m.Name == "CreateFromConnectionProfile" && m.GetParameters().Length == 1) { create = m; break; }
        W("  CreateFromConnectionProfile(1) = " + (create == null ? "null" : "ok"));

        var mgr = create.Invoke(null, new object[] { share });
        W("  state(before) = " + mgr.GetType().GetProperty("TetheringOperationalState").GetValue(mgr, null));

        try
        {
            var stopM = MethodNoArg(mgr.GetType(), "StopTetheringAsync");
            W("  StopTetheringAsync(0参) = " + (stopM == null ? "null" : "ok"));
            var op = stopM.Invoke(mgr, null);
            W("  stop = " + AwaitStatus(op, 8000));
        }
        catch (Exception e) { W("  stop 异常: " + e.Message); }
        System.Threading.Thread.Sleep(2000);

        mgr = create.Invoke(null, new object[] { share });
        W("=== 朴素 StartTetheringAsync（不配频段）===");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string st;
        try
        {
            var startM = MethodNoArg(mgr.GetType(), "StartTetheringAsync");
            W("  StartTetheringAsync(0参) = " + (startM == null ? "null" : "ok"));
            var op = startM.Invoke(mgr, null);
            st = AwaitStatus(op, 30000);
        }
        catch (Exception e)
        {
            var ie = e.InnerException ?? e;
            st = "THROW: " + ie.GetType().Name + " / " + ie.Message;
        }
        sw.Stop();
        W("  status = " + st + "   (" + sw.ElapsedMilliseconds + " ms)");

        for (int i = 1; i <= 5; i++)
        {
            System.Threading.Thread.Sleep(3000);
            var m2 = create.Invoke(null, new object[] { share });
            W("  t=" + (i * 3) + "s  state=" + m2.GetType().GetProperty("TetheringOperationalState").GetValue(m2, null)
              + "  NicReady=" + Hotspot.NicReady());
        }
    }


    static MethodInfo MethodNoArg(Type t, string name)
    {
        foreach (var m in t.GetMethods())
            if (m.Name == name && m.GetParameters().Length == 0) return m;
        return null;
    }

    static MethodInfo FindAsTask()
    {
        // 直接借用 Hotspot 自己解析好的那个（EnsureLoaded 已经填好）。
        // 自己 Assembly.Load("System.Runtime.WindowsRuntime") 用简名会抛
        // FileNotFoundException —— 这个坑 Hotspot.cs 里 2026-10-28 就踩过，
        // 它在 GAC_MSIL 里，必须给完整强名称。
        var fi = typeof(Hotspot).GetField("_asTaskGeneric", BindingFlags.NonPublic | BindingFlags.Static);
        return fi == null ? null : (MethodInfo)fi.GetValue(null);
    }

    static string AwaitStatus(object op, int timeoutMs)
    {
        try
        {
            var ot = op.GetType();
            var resType = ot.IsGenericType ? ot.GetGenericArguments()[0] : null;
            if (resType == null) return "(非泛型)";
            var task = _asTaskGeneric.MakeGenericMethod(resType).Invoke(null, new object[] { op });
            var wait = task.GetType().GetMethod("Wait", new Type[] { typeof(int) });
            bool done = (bool)wait.Invoke(task, new object[] { timeoutMs });
            if (!done) return "(超时 " + timeoutMs + "ms)";
            var result = task.GetType().GetProperty("Result").GetValue(task, null);
            if (result == null) return "(null result)";
            var sp = result.GetType().GetProperty("Status");
            return sp == null ? "(无 Status 属性)" : Convert.ToString(sp.GetValue(result, null));
        }
        catch (Exception e)
        {
            return "AwaitStatus 异常: " + e.Message;
        }
    }
}
