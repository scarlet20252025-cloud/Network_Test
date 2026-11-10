using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace Ferry
{
    /// <summary>
    /// 意见反馈窗口（WPF 版）。
    ///
    /// 支持带附件：选择文件 → 上传 → 提交时把 id 列表一起发过去。
    /// WPF 里没有 WinForms 的 OpenFileDialog 依赖，用 Microsoft.Win32.OpenFileDialog。
    /// </summary>
    internal class FeedbackWindow : Window
    {
        readonly Cfg _cfg;
        readonly System.Collections.Generic.List<Button> _kindChips =
            new System.Collections.Generic.List<Button>();
        int _kindIndex;
        TextBox _txtContact, _txtBody;
        TextBlock _msg;
        Button _btnSend;
        readonly List<string> _picked = new List<string>();
        TextBlock _fileList;

        static readonly string[] Kinds = { "出了问题 / 报错", "想要新功能", "太慢 / 卡顿", "使用方法咨询", "其他" };
        static readonly string[] KindKeys = { "bug", "feature", "perf", "ask", "other" };

        public FeedbackWindow(Cfg cfg)
        {
            _cfg = cfg;
            BuildUi();
        }

        void BuildUi()
        {
            ScrollBars.EnsureInstalled();

            Title = "意见反馈";
            Width = 500;
            SizeToContent = SizeToContent.Height;
            MaxHeight = 760;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = Palette.BgB;
            // 125% / 150% 缩放下窗口内的 1px 线条和 12px 小字容易发虚，
            // 打开布局取整 + 像素对齐会明显干净一些。
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            FontFamily = new FontFamily(Palette.FontUI);
            FontSize = Palette.FsBody;
            Foreground = Palette.TextB;

            // 根容器必须自带背景：Window.Background 只画窗口表面，
            // 离屏渲染（以及窗口刚创建的那一帧）拿到的是透明 → 一片白。
            var root = new StackPanel
            {
                Margin = new Thickness(22, 18, 22, 18),
                Background = Palette.BgB
            };
            Content = root;

            root.Children.Add(new TextBlock
            {
                Text = "遇到问题，或者有想让它做到的事？",
                FontSize = Palette.FsBody,
                FontWeight = FontWeights.SemiBold,
                Foreground = Palette.TextB,
                TextWrapping = TextWrapping.Wrap
            });
            root.Children.Add(new TextBlock
            {
                Text = "每一条都会被读到。能修的会修，暂时做不到的也会告诉你为什么。",
                FontSize = Palette.FsSmall,
                Foreground = Palette.MutedB,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 14)
            });

            root.Children.Add(new TextBlock
            {
                Text = "反馈类型",
                FontSize = Palette.FsBody,
                Foreground = Palette.TextB,
                Margin = new Thickness(0, 0, 0, 4)
            });
            // 原来用 ComboBox：默认模板的下拉是白底，配深色主题就是白底白字，
            // 整块看不清。改成一排胶囊按钮，选中态自己画，颜色完全可控。
            var chips = new WrapPanel();
            root.Children.Add(chips);
            for (int i = 0; i < Kinds.Length; i++)
            {
                int idx = i;
                var chip = new Button
                {
                    Content = Kinds[i],
                    FontSize = Palette.FsTiny,
                    Padding = new Thickness(10, 5, 10, 5),
                    Margin = new Thickness(0, 0, 6, 6),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    BorderThickness = new Thickness(1)
                };
                Ui.Round(chip, 8);
                chip.Click += delegate { SelectKind(idx); };
                chips.Children.Add(chip);
                _kindChips.Add(chip);
            }
            SelectKind(0);

            root.Children.Add(new TextBlock
            {
                Text = "联系方式（选填，方便回复你）",
                FontSize = Palette.FsBody,
                Foreground = Palette.TextB,
                Margin = new Thickness(0, 12, 0, 4)
            });
            _txtContact = new TextBox
            {
                FontSize = Palette.FsBody,
                Padding = new Thickness(8, 6, 8, 6),
                BorderBrush = Palette.Line2B,
                BorderThickness = new Thickness(1),
                Background = Palette.Card2B,
                Foreground = Palette.TextB,
                CaretBrush = Palette.TextB
            };
            root.Children.Add(_txtContact);

            root.Children.Add(new TextBlock
            {
                Text = "详细描述",
                FontSize = Palette.FsBody,
                Foreground = Palette.TextB,
                Margin = new Thickness(0, 12, 0, 4)
            });
            _txtBody = new TextBox
            {
                FontSize = Palette.FsBody,
                Height = 120,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(8, 6, 8, 6),
                BorderBrush = Palette.Line2B,
                BorderThickness = new Thickness(1),
                Background = Palette.Card2B,
                Foreground = Palette.TextB,
                CaretBrush = Palette.TextB
            };
            ScrollBars.ApplyTo(_txtBody);
            root.Children.Add(_txtBody);

            // 附件
            root.Children.Add(new TextBlock
            {
                Text = "截图或文件（选填，可多选）",
                FontSize = Palette.FsBody,
                Foreground = Palette.TextB,
                Margin = new Thickness(0, 12, 0, 4)
            });

            var fileRow = new Grid();
            fileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            fileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.Children.Add(fileRow);

            var btnFile = new Button
            {
                Content = "选择文件…",
                FontSize = Palette.FsSmall,
                Padding = new Thickness(14, 6, 14, 6),
                BorderThickness = new Thickness(1),
                BorderBrush = Palette.Line2B,
                Background = Palette.CardB,
                Foreground = Palette.TextB,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btnFile.Click += delegate { PickFiles(); };
            Grid.SetColumn(btnFile, 0);
            fileRow.Children.Add(btnFile);

            _fileList = new TextBlock
            {
                Text = "未选择",
                FontSize = Palette.FsSmall,
                Foreground = Palette.MutedB,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(10, 0, 0, 0)
            };
            Grid.SetColumn(_fileList, 1);
            fileRow.Children.Add(_fileList);

            // 状态提示
            _msg = new TextBlock
            {
                Text = "",
                FontSize = Palette.FsSmall,
                Foreground = Palette.MutedB,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 12, 0, 0)
            };
            root.Children.Add(_msg);

            // 按钮
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
            root.Children.Add(btnRow);

            _btnSend = new Button
            {
                Content = "提交反馈",
                FontSize = Palette.FsBody,
                Padding = new Thickness(28, 8, 28, 8),
                BorderThickness = new Thickness(0),
                Background = Palette.AccentB,
                Foreground = Palette.OnAccentB,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            _btnSend.Click += delegate { DoSend(); };
            btnRow.Children.Add(_btnSend);

            var btnCancel = new Button
            {
                Content = "取消",
                FontSize = Palette.FsBody,
                Padding = new Thickness(28, 8, 28, 8),
                BorderThickness = new Thickness(1),
                BorderBrush = Palette.Line2B,
                Background = Palette.CardB,
                Foreground = Palette.TextB,
                Margin = new Thickness(10, 0, 0, 0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btnCancel.Click += delegate { Close(); };
            btnRow.Children.Add(btnCancel);
        }


        /// <summary>胶囊选中态：选中的填主色，其余描边透明底。</summary>
        void SelectKind(int index)
        {
            _kindIndex = index;
            for (int i = 0; i < _kindChips.Count; i++)
            {
                var b = _kindChips[i];
                bool on = i == index;
                b.Background = on ? Palette.AccentB : Brushes.Transparent;
                b.Foreground = on ? Palette.OnAccentB : Palette.TextB;
                b.BorderBrush = on ? Palette.AccentB : Palette.Line2B;
                b.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }

        void PickFiles()
        {
            var ofd = new OpenFileDialog
            {
                Multiselect = true,
                Title = "选择要附上的截图或文件",
                Filter = "图片或文档|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.txt;*.log;*.json;*.zip;*.csv"
            };
            if (ofd.ShowDialog(this) != true) return;

            foreach (var path in ofd.FileNames)
            {
                if (_picked.Count >= 6)
                {
                    MessageBox.Show(this, "最多 6 个附件。", "渡口");
                    break;
                }
                try
                {
                    var fi = new FileInfo(path);
                    if (fi.Length > 8 * 1024 * 1024)
                    {
                        MessageBox.Show(this, Path.GetFileName(path) + " 超过 8 MB，跳过。", "渡口");
                        continue;
                    }
                }
                catch (Exception) { continue; }
                _picked.Add(path);
            }
            _fileList.Text = _picked.Count == 0 ? "未选择"
                : string.Join("、", _picked.ConvertAll(Path.GetFileName).ToArray());
        }

        void DoSend()
        {
            var body = (_txtBody.Text ?? "").Trim();
            if (body.Length < 4)
            {
                _msg.Foreground = Palette.BadB;
                _msg.Text = "描述太短了，至少写 4 个字";
                return;
            }

            var kind = KindKeys[Math.Max(0, Math.Min(KindKeys.Length - 1, _kindIndex))];
            var contact = (_txtContact.Text ?? "").Trim();

            _btnSend.IsEnabled = false;
            _btnSend.Content = "提交中…";
            _msg.Foreground = Palette.MutedB;
            _msg.Text = "";

            var pickedCopy = new List<string>(_picked);

            ThreadPool.QueueUserWorkItem(delegate
            {
                var ids = new List<string>();
                foreach (var path in pickedCopy)
                {
                    try
                    {
                        var up = Api.UploadAttachment(_cfg, path);
                        if (up.Length > 0) ids.Add(up);
                    }
                    catch (Exception) { }
                }

                string err;
                try { err = Api.Feedback(_cfg, kind, body, contact, string.Join(",", ids.ToArray())); }
                catch (Exception e) { err = "提交失败：" + e.Message; }

                Dispatcher.BeginInvoke(new Action(delegate
                {
                    _btnSend.IsEnabled = true;
                    _btnSend.Content = "提交反馈";
                    if (err.Length == 0)
                    {
                        _msg.Foreground = Palette.GoodB;
                        _msg.Text = "已收到，谢谢反馈";
                        var t = new System.Windows.Threading.DispatcherTimer
                        {
                            Interval = TimeSpan.FromMilliseconds(1200)
                        };
                        t.Tick += delegate { t.Stop(); Close(); };
                        t.Start();
                    }
                    else
                    {
                        _msg.Foreground = Palette.BadB;
                        _msg.Text = err;
                    }
                }));
            });
        }
    }
}
