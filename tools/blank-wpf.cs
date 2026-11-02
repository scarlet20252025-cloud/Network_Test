// 最小 WPF 空窗口，用作 UI 延迟的对照基准。
// 目的：如果连一个什么都不做的 WPF 窗口都出现 600ms 级消息延迟，
// 那卡顿就不是渡口的代码问题，而是系统级的（壁纸软件/GPU 合成/CPU 抢占）。
//
// 编译：csc /target:winexe /r:PresentationFramework.dll /r:PresentationCore.dll
//            /r:WindowsBase.dll /out:blankwpf.exe blank-wpf.cs
using System;
using System.Windows;
using System.Windows.Controls;

class BlankWpf
{
    [STAThread]
    static void Main()
    {
        var app = new Application();
        var w = new Window
        {
            Title = "blank-wpf-probe",
            Width = 520,
            Height = 780,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new TextBlock { Text = "probe", Margin = new Thickness(20) }
        };
        app.Run(w);
    }
}
