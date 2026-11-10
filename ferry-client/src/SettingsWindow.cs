using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Ferry
{
    /// <summary>
    /// 设置弹窗。1.2.0 起只是 <see cref="SettingsView"/> 的一层壳 ——
    /// 同一份设置面板也被主界面的「设置」页直接嵌入，两处永远一致。
    ///
    /// 保留这个窗口是因为：托盘菜单、首次激活、以及从旧版本过来的用户
    /// 都习惯「弹窗改设置」；改成只能去主界面改会是一次没必要的破坏。
    /// </summary>
    internal class SettingsWindow : Window
    {
        readonly SettingsView _view;

        public SettingsWindow(Cfg cfg, string cfgPath, string appDir, Action onUninstall)
            : this(cfg, cfgPath, appDir, onUninstall, null)
        {
        }

        public SettingsWindow(Cfg cfg, string cfgPath, string appDir, Action onUninstall,
                              Action<string> onUpdate)
        {
            _view = new SettingsView(cfg, cfgPath, appDir, onUninstall, onUpdate);
            BuildUi();
        }

        void BuildUi()
        {
            ScrollBars.EnsureInstalled();

            Title = "设置";
            Width = 520;
            SizeToContent = SizeToContent.Height;
            MaxHeight = 860;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = Palette.BgB;
            FontFamily = new FontFamily(Palette.FontUI);
            FontSize = Palette.FsBody;
            Foreground = Palette.TextB;

            var root = new StackPanel
            {
                Margin = new Thickness(24, 20, 24, 22),
                Background = Palette.BgB
            };
            Content = root;

            root.Children.Add(_view.Build());

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 18, 0, 0)
            };
            root.Children.Add(btnRow);

            var ok = Ui.Primary("保存", 30, 9);
            ok.Click += delegate
            {
                if (!_view.Apply()) return;
                DialogResult = true;
                Close();
            };
            btnRow.Children.Add(ok);

            var cancel = Ui.Secondary("取消", 30, 9);
            cancel.Margin = new Thickness(10, 0, 0, 0);
            cancel.Click += delegate { DialogResult = false; Close(); };
            btnRow.Children.Add(cancel);
        }

        /// <summary>开机自启（计划任务）。ProgramWpf 的命令行入口也用它。</summary>
        public static void SetAutoStart(bool on)
        {
            SettingsView.SetAutoStart(on);
        }
    }
}
