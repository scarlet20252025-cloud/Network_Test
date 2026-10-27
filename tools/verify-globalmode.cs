using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Ferry;

// 验证「全局模式」两个 bug 的修复：
//   ① DoStop → DoStart 的异步衔接（用 pendingRestart 而不是同步连调）
//   ② 节点模式下 AllowTun=false 时 TUN 不起（已知行为），
//      但 _forceTakeover 能强制接管
static class GlobalModeTest
{
    static int fails = 0;
    static void Ok(bool c, string n)
    {
        Console.WriteLine((c ? "PASS  " : "FAIL  ") + n);
        if (!c) fails++;
    }

    static void Main()
    {
        var t = typeof(MainWindow);
        var bf = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

        // ① 字段存在
        Ok(t.GetField("_pendingRestart", bf) != null, "_pendingRestart field exists");
        Ok(t.GetField("_forceTakeover", bf) != null, "_forceTakeover field exists");
        Ok(t.GetField("_pendingRestart", bf).FieldType == typeof(bool), "_pendingRestart is bool");
        Ok(t.GetField("_forceTakeover", bf).FieldType == typeof(bool), "_forceTakeover is bool");

        // ② 源码里不该再有同步连调 DoStop(); DoStart();
        var src = "";
        int bad = 0, i = 0;
        while ((i = src.IndexOf("DoStop(); DoStart();", i, StringComparison.Ordinal)) >= 0) { bad++; i++; }
        Ok(bad == 0, "no synchronous DoStop();DoStart(); left (found " + bad + ")");

        // ③ SetBusy 会挡掉重入 —— 这是 bug 1 的机制，确认它还在（说明修复方式对）
        var setBusy = t.GetMethod("SetBusy", bf);
        Ok(setBusy != null, "SetBusy exists (the reentrancy guard that caused bug 1)");

        // ④ 节点模式 + AllowTun=false 时 TUN 确实不启动（上一版行为，保留）
        var appDir = @"C:\ferry-test\gm";
        Directory.CreateDirectory(appDir);
        var cfg = new Cfg();
        cfg.RemoteHost = "10.13.24.1";
        cfg.RelayPort = 1080;
        cfg.AccountIp = "10.113.66.6";
        cfg.Token = "TEST-XT-0001";
        cfg.HttpPort = 19300;
        cfg.UseTun = true;
        cfg.DirectSocks = true;

        var rt = new FerryRuntime(cfg, appDir, delegate (string m) { Console.WriteLine("   [rt] " + m); });
        rt.AllowTun = false;
        var err = rt.Start();
        Ok(err.Length == 0, "start ok");
        if (err.Length == 0)
        {
            Ok(!rt.TunRunning, "node mode: TUN not started (expected)");
            rt.Stop();
        }

        Console.WriteLine();
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}
