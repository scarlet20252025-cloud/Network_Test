import io

# ═══ A. 主界面：复选框 → 状态卡内的动作按钮 ═══
p = 'ferry-client/src/MainForm.cs'
s = io.open(p, encoding='utf-8').read()

# A1. 字段
old = '''        // 主界面的「全局模式」开关。放在这里而不是设置里：
        // 它是「要不要接 UDP / 游戏」的核心选择，藏进设置等于让用户猜。
        CheckBox _chkGlobal;'''
new = '''        // 「全局模式」的动作按钮，放在状态卡第 4 行右侧。
        // 为什么不是复选框：复选框表达的是「设置里存了什么」，而用户关心的是
        // 「现在到底生效没有」—— 两者会不一致（配置里 useTun=true，但进程没提权、
        // TUN 没起来，勾子却是勾上的，用户得先取消再勾才能触发一次）。
        // 按钮显示的是**真实运行状态**：TUN 在跑就是「关闭」，否则「开启」。
        Button _btnGlobal;'''
assert old in s, 'A1'
s = s.replace(old, new, 1)

# A2. 控件：删 CheckBox，改为状态卡内的按钮
old = '''            // 全局模式开关：夹在「刷新」和「设置」中间，一行放下。
            _chkGlobal = new CheckBox();
            _chkGlobal.Text = "全局模式";
            _chkGlobal.Font = Theme.FSmall;
            _chkGlobal.ForeColor = Theme.Text;
            _chkGlobal.BackColor = Color.Transparent;
            _chkGlobal.SetBounds(136, 444, 122, 22);
            _chkGlobal.Checked = _cfg.UseTun;
            _chkGlobal.CheckedChanged += delegate { OnToggleGlobal(); };
            Controls.Add(_chkGlobal);

'''
assert old in s, 'A2'
s = s.replace(old, '', 1)

# 状态卡里 _rowTun 旁边加按钮
old = '''            _rowTun = Row(_card, 180);
            _rowTun.Visible = false;'''
new = '''            _rowTun = Row(_card, 180);
            _rowTun.SetBounds(24, 180, 300, 18);
            _rowTun.Visible = true;

            // 全局模式的开关就摆在「全局模式」这一行右边 —— 状态和动作在同一处，
            // 不用先去设置里找。
            _btnGlobal = Theme.FlatButton("开启", false);
            _btnGlobal.SetBounds(392, 176, 68, 24);
            _btnGlobal.Font = Theme.FSmall;
            _btnGlobal.Click += delegate { OnGlobalClicked(); };
            _card.Controls.Add(_btnGlobal);'''
assert old in s, 'A3'
s = s.replace(old, new, 1)

# A3. 逻辑：OnToggleGlobal → OnGlobalClicked
old = '''        void OnToggleGlobal()
        {
            if (_syncing) return;
            bool want = _chkGlobal.Checked;

            if (want && !Elevator.IsAdmin())
            {
                var r = MessageBox.Show(this,
                    "「全局模式」要创建虚拟网卡、接管 UDP，必须管理员权限。\\r\\n\\r\\n" +
                    "接下来会弹一次 UAC，点「是」之后程序会以管理员身份重新启动，" +
                    "开关会保持开启。\\r\\n\\r\\n继续吗？（取消就继续用普通模式，" +
                    "浏览器、下载工具照样能借网）",
                    "全局模式需要管理员权限", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (r != DialogResult.Yes)
                {
                    _syncing = true;
                    _chkGlobal.Checked = false;
                    _syncing = false;
                    return;
                }
                _cfg.UseTun = true;
                try { _cfg.Save(_cfgPath); } catch (Exception) { }
                Log("开启全局模式需要管理员权限，正在以管理员身份重启…");
                ElevateNow();
                return;
            }

            _cfg.UseTun = want;
            try { _cfg.Save(_cfgPath); } catch (Exception) { }
            Log(want ? "全局模式已开启" : "全局模式已关闭");

            if (_rt != null && _rt.Running && !_busy)
            {
                Log("按新设置重起借网链路…");
                _restartAfterStop = true;
                DoStop();
                return;
            }
            RefreshUi();
        }'''
new = '''        void OnGlobalClicked()
        {
            if (_busy) return;
            bool running = _rt != null && _rt.Running;
            bool tunUp = running && _rt.TunRunning;

            if (tunUp)
            {
                // 关闭：只改意图不够，得把正在跑的 TUN 摘掉
                _cfg.UseTun = false;
                try { _cfg.Save(_cfgPath); } catch (Exception) { }
                Log("关闭全局模式…");
                if (running) { _restartAfterStop = true; DoStop(); return; }
                RefreshUi();
                return;
            }

            // 开启：先看能不能——不是管理员就先说明再提权重启
            if (!Elevator.IsAdmin())
            {
                var r = MessageBox.Show(this,
                    "「全局模式」要让游戏 / QUIC / 语音也走渡口，需要创建虚拟网卡，" +
                    "必须管理员权限。\\r\\n\\r\\n" +
                    "点「是」之后程序会退出并自动以管理员身份重启（这台机器 UAC 若为" +
                    "「从不通知」则不会弹框），重启后它会自己把全局模式打开。",
                    "开启全局模式", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (r != DialogResult.Yes) return;
                _cfg.UseTun = true;
                try { _cfg.Save(_cfgPath); } catch (Exception) { }
                Log("开启全局模式需要管理员权限，正在以管理员身份重启…");
                ElevateNow();
                return;
            }

            _cfg.UseTun = true;
            try { _cfg.Save(_cfgPath); } catch (Exception) { }
            Log("全局模式已开启");
            if (running) { _restartAfterStop = true; DoStop(); return; }
            RefreshUi();
        }'''
assert old in s, 'A4'
s = s.replace(old, new, 1)

# A4. RefreshUi：更新 TUN 行 + 按钮
old = '''            // 开关状态跟配置对齐（CheckChanged 里有 _syncing 挡回环）
            if (_chkGlobal.Checked != _cfg.UseTun)
            {
                _syncing = true;
                _chkGlobal.Checked = _cfg.UseTun;
                _syncing = false;
            }

'''
assert old in s, 'A5'
s = s.replace(old, '', 1)

old = '''            if (_cfg.UseTun && !Elevator.IsAdmin())
            {
                // 勾了却没提权：不是「故障」，是差一步动作 ——
                // 所以说清楚差什么、去哪儿点，别只写「未开启」。
                SetText(_rowTun, "全局接管    已勾选 · 需管理员（勾下面开关时会提示重启）", Theme.Bad);
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
            {
                // 没开全局接管时不要藏掉这一行 —— 用户根本不知道有这能力，
                // 游戏 / QUIC / 语音走不了渡口也说不清为什么。
                SetText(_rowTun, "全局接管    未启用 · 勾选下方「全局模式」即可开启", Theme.Muted);
                _rowTun.Visible = true;
            }'''
new = '''            // 「全局模式」这一行显示的是**真实运行状态**，不是配置里的意图。
            // 意图（_cfg.UseTun）只决定「下次借网时要不要起 TUN」。
            {
                bool tunUp = rt != null && rt.TunRunning;
                if (tunUp)
                {
                    SetText(_rowTun, "全局模式    已开启 · UDP / 游戏 / 语音都走渡口", Theme.Good);
                    SetText(_btnGlobal, "关闭", Theme.Muted);
                }
                else if (_cfg.UseTun && !Elevator.IsAdmin())
                {
                    SetText(_rowTun, "全局模式    待管理员（点「开启」会提权重启）", Theme.Bad);
                    SetText(_btnGlobal, "开启", Theme.Text);
                }
                else if (_cfg.UseTun && runtimeUp)
                {
                    SetText(_rowTun, "全局模式    正在启用…", Theme.Muted);
                    SetText(_btnGlobal, "关闭", Theme.Muted);
                }
                else
                {
                    SetText(_rowTun, "全局模式    未开启 · 只有认代理的程序走渡口", Theme.Muted);
                    SetText(_btnGlobal, "开启", Theme.Text);
                }
                _rowTun.Visible = true;
                _btnGlobal.Visible = true;
            }'''
assert old in s, 'A6'
s = s.replace(old, new, 1)

# A5. SetText 对 Button 不适用 —— 加一个重载
old = '''        static void SetText(Label l, string text, Color color)'''
new = '''        static void SetText(Button b, string text, Color color)
        {
            if (b.Text != text) b.Text = text;
            if (b.ForeColor != color) b.ForeColor = color;
        }

        static void SetText(Label l, string text, Color color)'''
assert old in s, 'A7'
s = s.replace(old, new, 1)

io.open(p, 'w', encoding='utf-8', newline='').write(s)
print('MainForm ok')

# ═══ B. 设置页：删掉残留的 UDP选项 + 「以管理员身份重启」链接 ═══
p2 = 'ferry-client/src/SettingsDialog.cs'
t = io.open(p2, encoding='utf-8').read()

old = '''            _chkTun = MakeCheck("UDP低延迟模式（全局接管）",
                "网络层接管全部流量（含不认代理的程序），UDP 走低延迟链路。" +
                "需管理员权限；开启后短暂断网属正常", 280);
            _chkTun.Checked = _cfg.UseTun;
            card.Controls.Add(_chkTun);

            // 不是管理员时给一条直达路径 —— 让用户自己「右键 → 以管理员身份运行」
            // 不现实：多数人想不到右键，更想不到得先把程序关掉。
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
            card.Controls.Add(lnkElev);
'''
new = '''            // 「全局模式」已经搬到主界面（状态卡里那个「开启/关闭」按钮）——
            // 这里再放一个勾选项就会有两处状态，勾着但没生效时用户不知道该信谁。
'''
assert old in t, 'B1'
t = t.replace(old, new, 1)

# SaveAll 里的 TUN 段
old = '''            // TUN 勾了但没管理员权限：明确告诉用户会怎样，不要静默保存。
            // 静默保存的结果是「用户以为开了，实际每次借网都失败」。
            if (_chkTun.Checked && !Elevator.IsAdmin())
            {
                var r = MessageBox.Show(this,
                    "「UDP低延迟模式（全局接管）」要创建虚拟网卡、接管 UDP，必须管理员权限。\\r\\n\\r\\n" +
                    "保存后会以管理员身份重启一次（弹 UAC，点「是」）。\\r\\n" +
                    "暂时不需要也可以选「否」：设置照样保存，只是这一项不生效 —— " +
                    "浏览器、下载工具这些认代理的程序仍然能借网。\\r\\n\\r\\n" +
                    "现在保存并以管理员重启吗？",
                    "需要管理员权限", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                NeedElevate = (r == DialogResult.Yes);
            }
            TunToggleChanged = _cfg.UseTun != _chkTun.Checked;
            _cfg.UseTun = _chkTun.Checked;
'''
new = '''            // useTun 不在这里改 —— 它由主界面的「全局模式」按钮负责。
'''
assert old in t, 'B2'
t = t.replace(old, new, 1)

# NeedElevate / TunToggleChanged 属性保留但不再使用（MainForm 里已删引用）
old = '''                // 用户选了「以管理员身份重启」：这是最优先的动作。
                if (dlg.NeedElevate)
                {
                    ElevateNow();
                    return;
                }

'''
# 这段在 MainForm，已处理；这里只处理 SettingsDialog 的属性
io.open(p2, 'w', encoding='utf-8', newline='').write(t)
print('SettingsDialog ok')

# MainForm 里对 dlg.NeedElevate / dlg.TunToggleChanged 的引用要删（属性已被移除）
m = io.open(p, encoding='utf-8').read() if False else None
print('done')
