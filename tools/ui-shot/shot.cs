using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ferry;

// WPF 界面离屏渲染 —— 由 tools/render-ui.ps1 打包送到远端机器上跑。
//
// 为什么在远端渲染：界面问题只能看真实渲染（结构测试全过、页面照样是死的），
// 但用户的桌面常被别的程序占着，而且他不希望验证过程在自己机器上弹窗。
//
// 三个坑，都踩过：
//  1. 不能用 Show()：SSH 登录的是 Session 0（services），没有交互桌面，
//     创建 HWND 会失败。所以直接对 Content 做 Measure/Arrange 再渲染。
//  2. 字体/字号/前景色设在 Window 上、由子元素继承；离屏渲染时继承链断了
//     → 用回默认 Segoe UI，中文行高偏小、底部内容被算出画布外。
//     必须把这三个继承属性显式补到根元素上。
//  3. 主窗口是「侧栏 + 分页」的固定版式，必须按真实窗口尺寸 1000x660 排列。
//     早先按子元素高度累加，日志页会被算成 18px 高，截出来是一根细条。
static class Shot
{
    static Panel FindPanel(object root)
    {
        var p = root as Panel;
        if (p != null) return p;
        var sv = root as ScrollViewer;
        if (sv != null) return sv.Content as Panel;
        var dec = root as Decorator;
        if (dec != null) return FindPanel(dec.Child);
        var cc = root as ContentControl;
        if (cc != null) return FindPanel(cc.Content);
        return null;
    }

    static void Prepare(Window w, FrameworkElement root)
    {
        TextElement.SetFontFamily(root, w.FontFamily);
        TextElement.SetFontSize(root, w.FontSize);
        TextElement.SetForeground(root, w.Foreground);
    }

    static void SavePng(FrameworkElement root, string path, double w, double h, double scale)
    {
        int pw = (int)Math.Ceiling(w * scale);
        int ph = (int)Math.Ceiling(h * scale);
        var rtb = new RenderTargetBitmap(pw, ph, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(path)) enc.Save(fs);
        Console.WriteLine("saved " + Path.GetFileName(path) + "  " + pw + "x" + ph);
    }

    /// <summary>深度遍历视觉树，收集所有元素（自检找控件用）。</summary>
    static void Collect(DependencyObject d, List<DependencyObject> acc)
    {
        acc.Add(d);
        int n = VisualTreeHelper.GetChildrenCount(d);
        for (int i = 0; i < n; i++) Collect(VisualTreeHelper.GetChild(d, i), acc);
    }

    /// <summary>固定版式：按窗口真实尺寸排列（主窗口五个页面都走这里）。</summary>
    static void SaveFixed(Window win, string path, double w, double h, double scale)
    {
        var root = (FrameworkElement)win.Content;
        if (root == null) { Console.WriteLine("no content: " + win.Title); return; }
        Prepare(win, root);
        root.Measure(new Size(w, h));
        root.Arrange(new Rect(0, 0, w, h));
        root.UpdateLayout();
        // 离屏渲染不会触发 Loaded，滚动条样式要在这里补一次
        ScrollBars.ForceApply(root);
        root.UpdateLayout();
        SavePng(root, path, w, h, scale);
    }

    /// <summary>自适应高度：设置窗、反馈窗这种 SizeToContent 的窗口用。</summary>
    static void SaveAuto(Window win, string path, double w, double scale)
    {
        var root = (FrameworkElement)win.Content;
        if (root == null) { Console.WriteLine("no content: " + win.Title); return; }
        Prepare(win, root);

        root.Measure(new Size(w, double.PositiveInfinity));
        double h = root.DesiredSize.Height;
        var panel = FindPanel(root);
        if (panel != null)
        {
            double sum = 0;
            foreach (UIElement c in panel.Children)
            {
                var fe = c as FrameworkElement;
                if (fe != null && fe.Visibility == Visibility.Visible) sum += fe.DesiredSize.Height;
            }
            if (sum > h) h = sum;
        }
        if (h < 200) h = 200;

        root.Arrange(new Rect(0, 0, w, h));
        root.UpdateLayout();
        ScrollBars.ForceApply(root);
        root.UpdateLayout();
        SavePng(root, path, w, h, scale);
    }

    [STAThread]
    static void Main(string[] args)
    {
        var app = new Application();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 参数：outDir scale appDir
        var outDir = args.Length > 0 ? args[0] : ".";
        var scale = args.Length > 1 ? double.Parse(args[1]) : 1.0;
        var appDir = args.Length > 2 ? args[2] : Path.Combine(Path.GetTempPath(), "ferry-ui");
        Directory.CreateDirectory(appDir);
        var cfgPath = Path.Combine(appDir, "config.json");

        // 没有 config.json 时 Cfg.Load 给默认值 —— 正好覆盖「未激活」这条路径
        var pages = new[] { "1-tunnel", "2-log", "3-speed", "4-settings", "5-about" };
        for (int i = 0; i < pages.Length; i++)
        {
            var m = new MainWindow(cfgPath, appDir);
            m.SelectPageForShot(i);
            m.RefreshForShot();
            if (i == 1) m.FillLogForShot();      // 日志页灌点内容，才看得到换行和滚动条
            SaveFixed(m, Path.Combine(outDir, "shot-" + pages[i] + ".png"),
                Palette.WinW, Palette.WinH, scale);
        }

        var cfg = Cfg.Load(cfgPath);

        var s = new SettingsWindow(cfg, cfgPath, appDir, delegate { });
        SaveAuto(s, Path.Combine(outDir, "shot-6-settings-window.png"), 520, scale);

        var f = new FeedbackWindow(cfg);
        SaveAuto(f, Path.Combine(outDir, "shot-7-feedback.png"), 500, scale);

        // 单独特写：滚动条样式（细、无箭头、圆角悬浮）
        var sc = new Window
        {
            Title = "scrollbar",
            Background = Palette.BgB,
            FontFamily = new System.Windows.Media.FontFamily(Palette.FontUI),
            FontSize = Palette.FsBody,
            Foreground = Palette.TextB
        };
        // 样例窗口必须自带底色，否则半透明滑块在透明背景上「看不见」
        var sv = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            Background = Palette.BgB
        };
        var stack = new StackPanel { Margin = new Thickness(16), Background = Palette.BgB };
        for (int i = 1; i <= 24; i++)
            stack.Children.Add(new TextBlock
            {
                Text = "滚动条样例行 " + i,
                FontSize = Palette.FsSmall,
                Foreground = Palette.TextB,
                Margin = new Thickness(0, 3, 0, 3)
            });
        sv.Content = stack;
        ScrollBars.ApplyTo(sv);
        sc.Content = sv;
        SaveFixed(sc, Path.Combine(outDir, "shot-8-scrollbar.png"), 300, 220, scale);

        // ── 自检：直连状态下点「一键测试」──
        //
        // 1.2.4 及以前这条路径直接崩（处理器里 `!_rt.Running` 没判空，直连时 _rt 是 null）。
        // 这里构造的 MainWindow 从未借网，正好是那条路径；RaiseEvent 会同步走进
        // 真实的 Click 处理器，抛异常就会从 SavePng 之前窜出来。
        try
        {
            var probe = new MainWindow(cfgPath, appDir);
            probe.SelectPageForShot(2);                  // 切到测速页
            var logText = probe.SelfTestSpeedClick();
            bool reached = logText != null &&
                           logText.IndexOf("直连", StringComparison.Ordinal) >= 0;
            Console.WriteLine("SELFTEST speed-click: no-throw reached-runall=" + reached);
            if (!reached) Environment.ExitCode = 3;
        }
        catch (Exception ex)
        {
            Console.WriteLine("SELFTEST speed-click: THREW " + ex.GetType().Name + ": " + ex.Message);
            Environment.ExitCode = 2;
        }

        // ── 自检：关于页点「检查更新」必须留下可见状态文字 ──
        //
        // 1.2.5 的症状是「点了没用」：没新版本 / 连不上门户时直接 return，
        // 界面上只有按钮多一圈焦点虚线框。这里断言状态文字非空。
        MainWindow up = null;
        try
        {
            up = new MainWindow(cfgPath, appDir);
            up.SelectPageForShot(4);                     // 关于页
            var state = up.SelfTestUpdateClick();
            bool visible = !string.IsNullOrWhiteSpace(state);
            Console.WriteLine("SELFTEST update-click: text=\"" + state + "\" visible=" + visible);
            SaveFixed(up, Path.Combine(outDir, "shot-9-about-update.png"),
                Palette.WinW, Palette.WinH, scale);
            if (!visible) Environment.ExitCode = 4;
        }
        catch (Exception ex)
        {
            Console.WriteLine("SELFTEST update-click: THREW " + ex.GetType().Name + ": " + ex.Message);
            Environment.ExitCode = 5;
        }

        // ── 自检：标题栏窗口按钮（最小化 / 关闭）必须有悬停反馈 ──
        //
        // 1.2.6 及以前这两个按钮完全没反馈：模板里那层 Border 的 Background 写死
        // 成 Transparent，也没有触发器，鼠标移上去只有光标变手型。用户报
        // 「光标放右上角没有反馈」。这里断言：找到 2 个 30×26 的按钮，且各自的
        // Style 上至少有 2 条触发器（IsMouseOver + IsPressed）—— 样式被改空就会红。
        try
        {
            if (up == null) throw new InvalidOperationException("窗口未创建，无法检查窗口按钮");
            var all = new List<DependencyObject>();
            // 从 Content 开始遍历：窗口没 Show 过，Window 自身还没有视觉子树，
            // 直接传 Window 会得到 0 个元素（第一版就是这么踩的，found=0）。
            Collect((DependencyObject)up.Content, all);

            int found = 0, withTrig = 0;
            var winBtns = new List<Button>();
            foreach (var d in all)
            {
                var bt = d as Button;
                if (bt == null) continue;
                // 窗口按钮的识别特征：内容是一个矢量图标 + 有提示文字。
                // 只按 30×26 挑会捞到 7 个同尺寸的其它按钮（下拉箭头、开关等）。
                if (!(bt.Content is System.Windows.Shapes.Path)) continue;
                if (bt.ToolTip == null) continue;
                found++;
                winBtns.Add(bt);
                int n = bt.Style == null ? 0 : bt.Style.Triggers.Count;
                if (n >= 2) withTrig++;
                Console.WriteLine("SELFTEST winbtn: triggers=" + n);
            }
            Console.WriteLine("SELFTEST winbtn: found=" + found + " with-triggers=" + withTrig);
            if (found != 2 || withTrig != 2) Environment.ExitCode = 6;

            // 悬停预览：把 IsMouseOver 触发器的设值直接落到按钮上。
            // 离屏渲染没有鼠标，改不了 IsMouseOver；但模板的 Border 底色走的是
            // TemplateBinding Background，所以把同一个画刷写成局部值，看到的
            // 就是悬停时真实的样子。只为截图，不参与断言。
            foreach (var bt in winBtns)
            {
                var trg = bt.Style.Triggers[0] as Trigger;
                if (trg == null) continue;
                foreach (SetterBase sb in trg.Setters)
                {
                    var st2 = sb as Setter;
                    if (st2 != null) bt.SetValue(st2.Property, st2.Value);
                }
            }
            SaveFixed(up, Path.Combine(outDir, "shot-10-titlebar-hover.png"),
                Palette.WinW, Palette.WinH, scale);
        }
        catch (Exception ex)
        {
            Console.WriteLine("SELFTEST winbtn: THREW " + ex.GetType().Name + ": " + ex.Message);
            Environment.ExitCode = 7;
        }

        app.Shutdown();
    }
}
