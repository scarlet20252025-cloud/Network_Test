using System;
using System.IO;
using System.Reflection;
using Ferry;

// 验证「设置窗里点检查更新」这条链路的两件事：
//   ① Api.Version 能拿到门户版本（连接、解析都对）
//   ② SettingsWindow 的新构造函数（带 onUpdate 回调）能被正确构造
static class UpdateCheckTest
{
    static int fails = 0;
    static void Ok(bool c, string n)
    {
        Console.WriteLine((c ? "PASS  " : "FAIL  ") + n);
        if (!c) fails++;
    }

    [STAThread]
    static void Main()
    {
        // ① 版本接口
        var v = Api.Version("http://10.13.24.1:8080");
        Console.WriteLine("   ok=" + v.Ok + " latest=" + v.Latest);
        Ok(v.Ok, "portal reachable");
        Ok(v.Latest != null && v.Latest.Length > 0, "latest version parsed");

        var mine = typeof(ClashMerge).Assembly.GetName().Version.ToString(3);
        Console.WriteLine("   mine=" + mine + " compare=" + Api.CompareVer(v.Latest, mine));
        Ok(Api.CompareVer("1.0.14", "1.0.13") > 0, "compareVer sees newer");
        Ok(Api.CompareVer("1.0.13", "1.0.13") == 0, "compareVer equal");
        Ok(Api.CompareVer("1.0.9", "1.0.13") < 0, "compareVer older");

        // ② SettingsWindow 的两个构造函数都能构造（不 Show，只验签名与依赖）
        var cfgPath = @"C:\ferry-test\uxtest\config.json";
        Directory.CreateDirectory(Path.GetDirectoryName(cfgPath));
        var cfg = new Cfg();
        cfg.PortalUrl = "http://10.13.24.1:8080";
        cfg.Save(cfgPath);

        var t = typeof(SettingsWindow);
        var c3 = t.GetConstructor(new Type[] { typeof(Cfg), typeof(string), typeof(string), typeof(Action) });
        var c4 = t.GetConstructor(new Type[] { typeof(Cfg), typeof(string), typeof(string), typeof(Action), typeof(Action<string>) });
        Ok(c3 != null, "old ctor (4 args) still exists");
        Ok(c4 != null, "new ctor (5 args, onUpdate) exists");

        try
        {
            var w = (System.Windows.Window)c4.Invoke(new object[] {
                cfg, cfgPath, @"C:\ferry-test\uxtest",
                (Action)delegate { }, (Action<string>)delegate (string s) { } });
            Ok(w != null, "window constructed with onUpdate");
        }
        catch (Exception e)
        {
            var inner = e.InnerException != null ? e.InnerException.GetType().Name + ": " + e.InnerException.Message : "(no inner)";
            Ok(false, "window constructed with onUpdate -> " + inner);
        }

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}
