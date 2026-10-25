import io

# ── SettingsDialog ──
p = 'ferry-client/src/SettingsDialog.cs'
s = io.open(p, encoding='utf-8').read()

# 1) 属性
old = '''        public bool TunToggleChanged { get; private set; }'''
new = '''        public bool TunToggleChanged { get; private set; }

        /// <summary>保存后需要以管理员身份重启（用户勾了 TUN 但当前不是管理员）。
        /// 重启动作交给主窗口做 —— 它才知道怎么先把借网停干净。</summary>
        public bool NeedElevate { get; private set; }'''
assert old in s
s = s.replace(old, new, 1)

# 2) TUN 勾了但不是管理员：改成「保存 + 可选重启」
old2 = '''            if (_chkTun.Checked && !TunMode.IsAdmin())
            {
                var r = MessageBox.Show(this,
                    "「UDP低延迟模式（全局接管）」需要管理员权限。\\r\\n\\r\\n" +
                    "当前不是管理员，开启后借网时 TUN 会启动失败（浏览器等认代理的程序仍能正常借网，" +
                    "但不认代理的程序和 UDP 走不了渡口）。\\r\\n\\r\\n" +
                    "建议：关闭本程序，右键「以管理员身份运行」后重新勾选。\\r\\n\\r\\n" +
                    "要现在就保存这个设置吗？",
                    "需要管理员权限", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r == DialogResult.Yes) { return; }
            }'''
new2 = '''            if (_chkTun.Checked && !Elevator.IsAdmin())
            {
                var r = MessageBox.Show(this,
                    "「UDP低延迟模式（全局接管）」要创建虚拟网卡、接管 UDP，必须管理员权限。\\r\\n\\r\\n" +
                    "保存后会以管理员身份重启一次（弹 UAC，点「是」）。\\r\\n" +
                    "暂时不需要也可以选「否」：设置照样保存，只是这一项不生效 —— " +
                    "浏览器、下载工具这些认代理的程序仍然能借网。\\r\\n\\r\\n" +
                    "现在保存并以管理员重启吗？",
                    "需要管理员权限", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                NeedElevate = (r == DialogResult.Yes);
            }'''
assert old2 in s
s = s.replace(old2, new2, 1)

# 3) 自启任务：非管理员不能建 HIGHEST
old3 = '''                // /RL HIGHEST = 以最高权限运行（这就是免 UAC 的关键）
                // /SC ONLOGON = 登录时触发
                // /F = 已存在就覆盖，保证重复保存设置不会失败
                var args = "/Create /TN \\"" + TaskName + "\\" /TR \\"\\\\\\"" + exe + "\\\\\\"\\" " +
                           "/SC ONLOGON /RL HIGHEST /F";'''
new3 = '''                // /SC ONLOGON = 登录时触发；/F = 已存在就覆盖。
                //
                // /RL HIGHEST 只有在**当前进程已经是管理员**时才加：
                // 普通用户建「最高权限」任务会被拒绝访问（schtasks 返回 1）。
                // 0.9.6 起客户端是 asInvoker，纯代理模式普通权限就够 ——
                // 所以普通用户的自动启动任务就是普通任务，不弹 UAC。
                var rl = Elevator.IsAdmin() ? " /RL HIGHEST" : "";
                var args = "/Create /TN \\"" + TaskName + "\\" /TR \\"\\\\\\"" + exe + "\\\\\\"\\" " +
                           "/SC ONLOGON" + rl + " /F";'''
assert old3 in s
s = s.replace(old3, new3, 1)

# 4) TUN 下方加「以管理员身份重启」链接（非管理员时可见）
old4 = '''            _chkTun.Checked = _cfg.UseTun;
            card.Controls.Add(_chkTun);'''
new4 = '''            _chkTun.Checked = _cfg.UseTun;
            card.Controls.Add(_chkTun);

            // 不是管理员时给一条直达路径。
            // 让用户自己去「右键 → 以管理员身份运行」是不现实的 ——
            // 多数人不会想到右键，更不会想到要先把程序关掉。
            var lnkElev = new LinkLabel();
            lnkElev.Text = "以管理员身份重启程序…";
            lnkElev.Font = Theme.FSmall;
            lnkElev.LinkColor = Theme.Muted;
            lnkElev.ActiveLinkColor = Theme.Accent;
            lnkElev.LinkBehavior = LinkBehavior.HoverUnderline;
            lnkElev.SetBounds(40, 340, 220, 18);
            lnkElev.Visible = !Elevator.IsAdmin();
            lnkElev.LinkClicked += delegate
            {
                _chkTun.Checked = true;
                SaveAll();
            };
            card.Controls.Add(lnkElev);'''
assert old4 in s
s = s.replace(old4, new4, 1)

# 5) 卡片与窗口因新链接各长一点
pairs = [
    ('card.SetBounds(20, 74, 430, 350);', 'card.SetBounds(20, 74, 430, 374);'),
    ('lblIv.SetBounds(24, 444, 130, 20);', 'lblIv.SetBounds(24, 468, 130, 20);'),
    ('_numInterval.SetBounds(166, 442, 90, 26);', '_numInterval.SetBounds(166, 466, 90, 26);'),
    ('lblSec.SetBounds(264, 444, 30, 20);', 'lblSec.SetBounds(264, 468, 30, 20);'),
    ('_lblHint.SetBounds(24, 476, 424, 34);', '_lblHint.SetBounds(24, 500, 424, 34);'),
    ('lnkUninstall.SetBounds(24, 534, 110, 20);', 'lnkUninstall.SetBounds(24, 558, 110, 20);'),
    ('_lblVer.SetBounds(142, 535, 150, 18);', '_lblVer.SetBounds(142, 559, 150, 18);'),
    ('lnkCheck.SetBounds(300, 534, 80, 20);', 'lnkCheck.SetBounds(300, 558, 80, 20);'),
    ('ok.SetBounds(238, 588, 104, 34);', 'ok.SetBounds(238, 612, 104, 34);'),
    ('cancel.SetBounds(350, 588, 104, 34);', 'cancel.SetBounds(350, 612, 104, 34);'),
    ('fbBtn.SetBounds(20, 588, 180, 34);', 'fbBtn.SetBounds(20, 612, 180, 34);'),
    ('ClientSize = new Size(470, 642);', 'ClientSize = new Size(470, 666);'),
]
for a, b in pairs:
    assert a in s, 'missing: ' + a
    s = s.replace(a, b, 1)

io.open(p, 'w', encoding='utf-8', newline='').write(s)
print('SettingsDialog ok')

# ── MainForm ──
p2 = 'ferry-client/src/MainForm.cs'
t = io.open(p2, encoding='utf-8').read()

# 设置返回后：要重启就先重启
old5 = '''                if (dlg.TunToggleChanged && _rt != null && _rt.Running && !_busy)'''
new5 = '''                // 用户选了「以管理员身份重启」：这是最优先的动作，
                // 其他刷新都不必做了，反正马上要退出。
                if (dlg.NeedElevate)
                {
                    ElevateNow();
                    return;
                }

                if (dlg.TunToggleChanged && _rt != null && _rt.Running && !_busy)'''
assert old5 in t
t = t.replace(old5, new5, 1)

# ElevateNow 方法：放在 OpenSettings 之后
old6 = '''        void DoUninstall()'''
new6 = '''        /// <summary>
        /// 以管理员身份重启本程序。
        /// 先把借网停干净 —— 否则新进程起来时系统代理还指着旧进程的端口，
        /// 那几秒全网上不去（用户会以为"重启一下就坏了"）。
        /// </summary>
        void ElevateNow()
        {
            try
            {
                if (_rt != null)
                {
                    FerryCore.RestoreDirect(_cfg, _appDir);
                    _rt.Stop();
                    _rt = null;
                }
            }
            catch (Exception) { }
            Log("以管理员身份重启…");
            Elevator.RestartElevated();
        }

        void DoUninstall()'''
assert old6 in t
t = t.replace(old6, new6, 1)

# TUN 状态行：区分「勾了但不是管理员」
old7 = '''            if (_cfg.UseTun)
            {
                bool tunUp = rt != null && rt.TunRunning;
                SetText(_rowTun, "全局接管    " + (tunUp ? "已开启（UDP低延迟模式）" : "未开启"),
                        tunUp ? Theme.Good : Theme.Muted);
                _rowTun.Visible = true;
            }
            else
            {'''
new7 = '''            if (_cfg.UseTun && !Elevator.IsAdmin())
            {
                // 勾了却没提权：这不是「故障」，是差一步动作 ——
                // 所以说清楚差什么、去哪儿点，别只写「未开启」。
                SetText(_rowTun, "全局接管    已勾选 · 需管理员（设置里点「以管理员身份重启」）", Theme.Bad);
                _rowTun.Visible = true;
            }
            else if (_cfg.UseTun)
            {
                bool tunUp = rt != null && rt.TunRunning;
                SetText(_rowTun, "全局接管    " + (tunUp ? "已开启（UDP低延迟模式）" : "未开启"),
                        tunUp ? Theme.Good : Theme.Muted);
                _rowTun.Visible = true;
            }
            else
            {'''
assert old7 in t
t = t.replace(old7, new7, 1)

io.open(p2, 'w', encoding='utf-8', newline='').write(t)
print('MainForm ok')
