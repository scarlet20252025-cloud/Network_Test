using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

// 量 WPF 窗口的 UI 线程响应延迟（WM_NULL + SendMessageTimeout）。
// 用法: diagui.exe <pid>
class DiagUi
{
    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam,
                                            IntPtr lParam, uint flags, uint timeout,
                                            out IntPtr result);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SendMessageTimeoutW")]
    static extern IntPtr SendMessageTimeoutRaw(IntPtr hWnd, uint Msg, IntPtr wParam,
                                               IntPtr lParam, uint flags, uint timeout,
                                               out IntPtr result);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowTextW(IntPtr hWnd, StringBuilder s, int n);

    static uint targetPid;
    static IntPtr found = IntPtr.Zero;

    static bool Cb(IntPtr h, IntPtr l)
    {
        uint pid;
        GetWindowThreadProcessId(h, out pid);
        if (pid == targetPid && IsWindowVisible(h)) { found = h; return false; }
        return true;
    }

    static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        targetPid = uint.Parse(args[0]);
        EnumWindows(Cb, IntPtr.Zero);
        if (found == IntPtr.Zero) { Console.WriteLine("no window"); return; }

        var sb = new StringBuilder(256);
        GetWindowTextW(found, sb, 256);
        Console.WriteLine("hwnd=0x" + found.ToString("X") + " title=" + sb.ToString());

        IntPtr res;
        for (int i = 0; i < 5; i++)
            SendMessageTimeout(found, 0, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0002, 2000, out res);

        var lat = new List<double>();
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < 20)
        {
            var t0 = Stopwatch.StartNew();
            IntPtr ret = SendMessageTimeout(found, 0, IntPtr.Zero, IntPtr.Zero,
                                            0x0001 | 0x0002, 2000, out res);
            t0.Stop();
            lat.Add(ret != IntPtr.Zero ? t0.Elapsed.TotalMilliseconds : 9999);
            System.Threading.Thread.Sleep(20);
        }

        lat.Sort();
        int n = lat.Count;
        Console.WriteLine("samples=" + n);
        Console.WriteLine("p50=" + lat[n / 2].ToString("F1"));
        Console.WriteLine("p90=" + lat[(int)(n * 0.9)].ToString("F1"));
        Console.WriteLine("p99=" + lat[(int)(n * 0.99)].ToString("F1"));
        Console.WriteLine("max=" + lat[n - 1].ToString("F1"));

        int slow = 0;
        foreach (var x in lat) if (x > 25) slow++;
        Console.WriteLine("slow(>25ms)=" + slow + "/" + n);
    }
}
