using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Ferry
{
    /// <summary>一次探测的结果。</summary>
    internal struct ProbeResult
    {
        public bool Ok;
        public int Ms;
        public string Route;
        /// <summary>失败原因（DNS / 拒绝 / 超时 / TLS / 网络）。成功时为空。</summary>
        public string Note;
    }

    internal delegate ProbeResult ProbeFunc(string host, int port, int timeoutMs);

    /// <summary>
    /// 测速页：对固定的几个站点做「建连延迟 + 失败率」测试。
    ///
    /// 为什么固定站点、不让用户随便加：
    ///   · 测速要能横向比较，站点一变数字就没意义了；
    ///   · 这些站点的 DNS 在国内是稳定的，不会因为解析抖动被误判成线路问题；
    ///   · 国内 / 境外各选几个，正好把「渡口」和「本机代理」两条路都覆盖到。
    ///
    /// 为什么用 TCP 建连而不是 ping：
    ///   ICMP 过不了 SOCKS5，隧道里根本没有 ping 这回事；
    ///   建连耗时包含 TCP 握手 + SOCKS 协商，才是用户真正感受到的那一段。
    ///   失败次数就是「丢包」在这条链路上的等价物（建不起连 = 这一次没了）。
    /// </summary>
    internal class SpeedTestView
    {
        const int Attempts = 3;
        const int TimeoutMs = 4000;

        class Target
        {
            public string Name;
            public string Host;
            public int Port;
            public bool Domestic;
            public TextBlock Delay;
            public TextBlock Loss;
            public TextBlock Route;
        }

        readonly ProbeFunc _probe;
        readonly List<Target> _targets = new List<Target>();
        readonly TextBlock _summary;
        readonly Button _runBtn;
        readonly Action<string> _log;
        bool _running;

        public SpeedTestView(ProbeFunc probe, TextBlock summary, Button runBtn, Action<string> log)
        {
            _probe = probe;
            _summary = summary;
            _runBtn = runBtn;
            _log = log;
        }

        static readonly object[][] Spec =
        {
            // 名称            主机                 端口  国内
            new object[] { "百度",        "www.baidu.com",     443, true  },
            new object[] { "腾讯",        "www.tencent.com",   443, true  },
            new object[] { "阿里云",      "www.aliyun.com",    443, true  },
            new object[] { "哔哩哔哩",    "www.bilibili.com",  443, true  },
            new object[] { "淘宝",        "www.taobao.com",    443, true  },
            new object[] { "网易",        "www.163.com",       443, true  },
            new object[] { "12306",       "www.12306.cn",      443, true  },
            new object[] { "GitHub",      "github.com",        443, false },
            new object[] { "Cloudflare",  "1.1.1.1",           443, false },
            new object[] { "Google",      "www.google.com",    443, false },
        };

        public FrameworkElement Build()
        {
            var root = new StackPanel();

            var card = Ui.Card(0, 6);
            root.Children.Add(card);

            var grid = new Grid { Margin = new Thickness(18, 12, 18, 10) };
            card.Child = grid;

            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });

            AddHeader(grid, 0);

            int row = 1;
            foreach (var spec in Spec)
            {
                var t = new Target
                {
                    Name = (string)spec[0],
                    Host = (string)spec[1],
                    Port = (int)spec[2],
                    Domestic = (bool)spec[3]
                };
                _targets.Add(t);
                AddRow(grid, row++, t);
            }

            root.Children.Add(new TextBlock
            {
                Text = "延迟 = TCP 建连 + TLS 握手（443，和浏览器走同一条路），" +
                       "只测 SOCKS 握手会被代理的「先应答后连接」骗成 0ms。" +
                       "国内站点优先走渡口，境外优先走本机代理；未借网时测的是直连裸链路。",
                FontSize = Palette.FsTiny,
                Foreground = Palette.MutedB,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 12, 2, 0)
            });

            return root;
        }

        static void AddHeader(Grid g, int row)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddCell(g, row, 0, "站点", TextAlignment.Left);
            AddCell(g, row, 1, "目标", TextAlignment.Left);
            AddCell(g, row, 2, "延迟", TextAlignment.Right);
            AddCell(g, row, 3, "失败", TextAlignment.Right);
            AddCell(g, row, 4, "路径", TextAlignment.Left);
        }

        void AddRow(Grid g, int row, Target t)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var name = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 9, 0, 9)
            };
            name.Children.Add(new Border
            {
                BorderBrush = t.Domestic ? Palette.AccentB : Palette.BlueB,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4, 0, 4, 0),
                Margin = new Thickness(0, 1, 7, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = t.Domestic ? "内" : "外",
                    FontSize = 10,
                    Foreground = t.Domestic ? Palette.AccentB : Palette.BlueB
                }
            });
            name.Children.Add(new TextBlock
            {
                Text = t.Name,
                FontSize = Palette.FsBody,
                Foreground = Palette.TextB,
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn(name, 0);
            Grid.SetRow(name, row);
            g.Children.Add(name);

            var host = new TextBlock
            {
                Text = t.Host,
                FontFamily = new FontFamily(Palette.FontMono),
                FontSize = Palette.FsTiny,
                Foreground = Palette.MutedB,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 9, 0, 9)
            };
            Grid.SetColumn(host, 1);
            Grid.SetRow(host, row);
            g.Children.Add(host);

            // 数字列右对齐：一列 3ms / 120ms / 900ms 混排时，右对齐才能一眼比大小
            t.Delay = Cell(g, row, 2, "—", Palette.MutedB, Palette.FontMono, TextAlignment.Right);
            t.Loss = Cell(g, row, 3, "—", Palette.MutedB, Palette.FontMono, TextAlignment.Right);
            t.Route = Cell(g, row, 4, "—", Palette.MutedB, Palette.FontUI, TextAlignment.Left);
        }

        static TextBlock Cell(Grid g, int row, int col, string text, Brush fg, string font,
                              TextAlignment align)
        {
            var t = new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily(font),
                FontSize = Palette.FsBody,
                Foreground = fg,
                TextAlignment = align,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = align == TextAlignment.Right
                    ? new Thickness(0, 9, 20, 9)
                    : new Thickness(0, 9, 0, 9)
            };
            Grid.SetColumn(t, col);
            Grid.SetRow(t, row);
            g.Children.Add(t);
            return t;
        }

        static void AddCell(Grid g, int row, int col, string text, TextAlignment align)
        {
            var t = new TextBlock
            {
                Text = text,
                FontSize = Palette.FsTiny,
                Foreground = Palette.MutedB,
                TextAlignment = align,
                Margin = align == TextAlignment.Right
                    ? new Thickness(0, 0, 20, 8)
                    : new Thickness(0, 0, 0, 8)
            };
            Grid.SetColumn(t, col);
            Grid.SetRow(t, row);
            g.Children.Add(t);
        }

        public void Reset()
        {
            foreach (var t in _targets)
            {
                t.Delay.Text = "—"; t.Delay.Foreground = Palette.MutedB;
                t.Loss.Text = "—"; t.Loss.Foreground = Palette.MutedB;
                t.Route.Text = "—"; t.Route.Foreground = Palette.MutedB;
            }
            _summary.Text = "";
        }

        public void RunAll()
        {
            if (_running) return;
            _running = true;
            _runBtn.IsEnabled = false;
            _runBtn.Content = "测试中…";
            Reset();

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                // 线程池里的未处理异常会**直接终结进程**（.NET 2.0 起不再静默吞掉）。
                // 测速要碰网络、碰 UI 回调，任何一处漏了 try 就是「点一下软件没了」。
                try { RunAllCore(); }
                catch (Exception ex) { FailAll(ex); }
            });
        }

        /// <summary>测速主流程，跑在后台线程上。任何异常都由上面的 RunAll 兜住。</summary>
        void RunAllCore()
        {
            int cnSum = 0, cnN = 0, fgSum = 0, fgN = 0;
            int failTotal = 0;

            foreach (var t in _targets)
            {
                int sum = 0, ok = 0;
                string route = "";
                string note = "";
                for (int i = 0; i < Attempts; i++)
                {
                    var r = _probe(t.Host, t.Port, TimeoutMs);
                    if (r.Ok)
                    {
                        ok++;
                        sum += r.Ms;
                        if (!string.IsNullOrEmpty(r.Route)) route = r.Route;
                    }
                    else if (!string.IsNullOrEmpty(r.Note))
                    {
                        // 失败原因留在「路径」列 —— 只写「超时」的话，
                        // 分不清是 DNS 挂了、端口被封还是网关掐了 TLS。
                        note = r.Note;
                    }
                    int done = i + 1;
                    int okNow = ok;
                    int sumNow = sum;
                    string routeNow = route;
                    Post(delegate
                    {
                        t.Loss.Text = (done - okNow) + " / " + done;
                        t.Loss.Foreground = done == okNow ? Palette.MutedB : Palette.BadB;
                        if (okNow > 0)
                        {
                            int avg = sumNow / okNow;
                            t.Delay.Text = avg + " ms";
                            t.Delay.Foreground = avg < 120 ? Palette.GoodB
                                              : avg < 300 ? Palette.WarnB
                                              : Palette.BadB;
                            t.Route.Text = routeNow;
                            // 三种路径三种颜色：渡口=主色、本机代理=蓝、直连=灰
                            t.Route.Foreground = routeNow == "渡口" ? Palette.AccentB
                                               : routeNow == "直连" ? Palette.MutedB
                                               : Palette.BlueB;
                        }
                    });
                }

                int fail = Attempts - ok;
                failTotal += fail;
                if (ok > 0)
                {
                    int avg = sum / ok;
                    if (t.Domestic) { cnSum += avg; cnN++; }
                    else { fgSum += avg; fgN++; }
                }

                var tt = t;
                Post(delegate
                {
                    tt.Loss.Text = fail + " / " + Attempts;
                    tt.Loss.Foreground = fail == 0 ? Palette.MutedB : Palette.BadB;
                    if (ok == 0)
                    {
                        tt.Delay.Text = "超时";
                        tt.Delay.Foreground = Palette.BadB;
                        tt.Route.Text = string.IsNullOrEmpty(note) ? "—" : note;
                        tt.Route.Foreground = Palette.BadB;
                    }
                });
            }

            Post(delegate
            {
                var sb = new System.Text.StringBuilder();
                if (cnN > 0) sb.Append("国内平均 ").Append(cnSum / cnN).Append(" ms");
                if (fgN > 0)
                {
                    if (sb.Length > 0) sb.Append("　·　");
                    sb.Append("境外平均 ").Append(fgSum / fgN).Append(" ms");
                }
                if (sb.Length == 0) sb.Append("全部超时：检查本机网络，或先开始借网再测");
                if (failTotal > 0) sb.Append("　·　失败 ").Append(failTotal).Append(" 次");
                sb.Append("　·　").Append(DateTime.Now.ToString("HH:mm:ss"));
                _summary.Text = sb.ToString();

                _running = false;
                _runBtn.IsEnabled = true;
                _runBtn.Content = "一键测试";
            });

            if (_log != null)
            {
                _log("测速完成：国内 " + (cnN > 0 ? (cnSum / cnN) + "ms" : "全部失败") +
                     "，境外 " + (fgN > 0 ? (fgSum / fgN) + "ms" : "全部失败") +
                     "，失败 " + failTotal + " 次");
            }
        }

        /// <summary>测速中途出错：把还在「—」的行标成失败，恢复按钮，别让界面卡在「测试中…」。</summary>
        void FailAll(Exception ex)
        {
            Post(delegate
            {
                foreach (var t in _targets)
                {
                    if (t.Delay.Text == "—")
                    {
                        t.Delay.Text = "失败";
                        t.Delay.Foreground = Palette.BadB;
                    }
                }
                _summary.Text = "测速出错：" + ex.Message;
                _running = false;
                _runBtn.IsEnabled = true;
                _runBtn.Content = "一键测试";
            });
            if (_log != null) _log("测速异常：" + ex.Message + "（已中止，界面已恢复）");
        }

        void Post(Action a)
        {
            var d = _summary.Dispatcher;
            if (d.CheckAccess()) a();
            else d.BeginInvoke(DispatcherPriority.Background, a);
        }
    }
}
