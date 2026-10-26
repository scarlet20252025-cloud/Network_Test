using System;
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
// 远端是一台专门的测试机（10.113.66.6），随便跑。
//
// 三个坑，都踩过：
//  1. 不能用 Show()：SSH 登录的是 Session 0（services），没有交互桌面，
//     创建 HWND 会失败。所以直接对 Content 做 Measure/Arrange 再渲染。
//  2. 字体/字号/前景色设在 Window 上、由子元素继承；离屏渲染时继承链断了
//     → 用回默认 Segoe UI，中文行高偏小、底部内容被算出画布外。
//     必须把这三个继承属性显式补到根元素上。
//  3. root.DesiredSize 不可靠（Window 的 SizeToContent 会先给它一个约束），
//     画布高度要按子元素 DesiredSize 自己累加，否则底部按钮被切一半。
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

    static void Save(Window w, string path, double width, double scale)
    {
        var root = (FrameworkElement)w.Content;
        if (root == null) { Console.WriteLine("no content: " + w.Title); return; }

        TextElement.SetFontFamily(root, w.FontFamily);
        TextElement.SetFontSize(root, w.FontSize);
        TextElement.SetForeground(root, w.Foreground);

        root.Measure(new Size(width, double.PositiveInfinity));

        double h = 0;
        var panel = FindPanel(root);
        if (panel != null)
        {
            foreach (UIElement c in panel.Children)
            {
                var fe = c as FrameworkElement;
                if (fe != null && fe.Visibility == Visibility.Visible) h += fe.DesiredSize.Height;
            }
        }
        if (h < root.DesiredSize.Height) h = root.DesiredSize.Height;
        if (h < 200) h = 200;

        root.Arrange(new Rect(0, 0, width, h));
        root.UpdateLayout();

        // 再按实际布局后的位置校正一次（子元素可能比 Measure 时更高）
        if (panel != null)
        {
            double maxBottom = 0;
            foreach (UIElement c in panel.Children)
            {
                var fe = c as FrameworkElement;
                if (fe == null || fe.Visibility != Visibility.Visible) continue;
                Point pos = fe.TranslatePoint(new Point(0, 0), root);
                double bottom = pos.Y + fe.ActualHeight;
                if (bottom > maxBottom) maxBottom = bottom;
            }
            if (maxBottom + 1 > h)
            {
                h = maxBottom + 1;
                root.Arrange(new Rect(0, 0, width, h));
                root.UpdateLayout();
            }
        }

        int pw = (int)Math.Ceiling(width * scale);
        int ph = (int)Math.Ceiling(h * scale);
        var rtb = new RenderTargetBitmap(pw, ph, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(path)) enc.Save(fs);
        Console.WriteLine("saved " + Path.GetFileName(path) + "  " + pw + "x" + ph +
            " @" + (96 * scale) + "dpi  h=" + h.ToString("F1"));
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
        var cfg = Cfg.Load(cfgPath);

        var s = new SettingsWindow(cfg, cfgPath, appDir, delegate { });
        Save(s, Path.Combine(outDir, "shot-settings.png"), 500, scale);

        var f = new FeedbackWindow(cfg);
        Save(f, Path.Combine(outDir, "shot-feedback.png"), 500, scale);

        var m = new MainWindow(cfgPath, appDir);
        Save(m, Path.Combine(outDir, "shot-main.png"), 520, scale);

        app.Shutdown();
    }
}
