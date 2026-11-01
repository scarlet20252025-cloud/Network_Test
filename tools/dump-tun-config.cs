// 把 TunMode.BuildConfig 真正生成的 YAML dump 出来，交给 mihomo -t 验证。
// 为什么不是在 Python 里正则扫源码：正则只能看"写了什么"，看不出
// 运行时拼出来的实际内容（动态 DNS 那段、变量插值）。2026-11-01 的
// 裸 IP 事故就是因为只看了源码没跑真实产物。
// 用法：csc 编译后运行，参数是输出路径。
using System;
using System.Reflection;
using System.Text;

class DumpCfg
{
    static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string exe = args.Length > 1 ? args[1] : @"dist\渡口助手.exe";
        string outp = args.Length > 0 ? args[0] : "tun.yaml";

        var asm = Assembly.LoadFrom(exe);
        var t = asm.GetType("Ferry.TunMode");
        if (t == null) { Console.Error.WriteLine("找不到 Ferry.TunMode"); Environment.Exit(2); }
        var m = t.GetMethod("BuildConfig", BindingFlags.NonPublic | BindingFlags.Instance);
        if (m == null) { Console.Error.WriteLine("找不到 BuildConfig"); Environment.Exit(2); }

        var inst = Activator.CreateInstance(t, new object[] { null, null, null });
        string yaml = (string)m.Invoke(inst, new object[] { 10801, "10.13.24.1", 10802 });
        System.IO.File.WriteAllText(outp, yaml, new UTF8Encoding(false));
        Console.WriteLine("已写出: " + outp + " (" + yaml.Length + " 字符)");
    }
}
