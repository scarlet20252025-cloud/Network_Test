using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace Ferry
{
    /// <summary>
    /// SSH 隧道：起 `ssh -D 127.0.0.1:&lt;socksPort&gt;`，掉线自动重连。
    ///
    /// 为什么用 ssh.exe 而不是自己实现 SSH：
    ///   Windows 10 1803+ 自带 OpenSSH 客户端（C:\Windows\System32\OpenSSH\ssh.exe），
    ///   这意味着**目标机器什么都不用装**。自己写 SSH 协议要处理密钥交换、
    ///   加密套件、known_hosts —— 那是几个月的工程，而 ssh.exe 是现成且久经考验的。
    ///
    /// 为什么不继续用 ssh-tunnel-keeper.ps1：
    ///   原版是 PowerShell 脚本，每次重连都要重新启动一个 powershell.exe（几百毫秒），
    ///   而且要把密码/端口当命令行参数传。C# 里直接管进程更轻、更可控。
    ///   文件仍然保留在安装目录 —— 老用户从 PS 版升级上来时不会因为找不到文件报错。
    /// </summary>
    class Tunnel
    {
        readonly Cfg _cfg;
        readonly int _socksPort;
        readonly Action<string> _log;
        readonly object _lock = new object();

        Process _proc;
        Thread _watch;
        volatile bool _running;
        volatile bool _stopping;
        int _restarts;

        public Tunnel(Cfg cfg, int socksPort, Action<string> log)
        {
            _cfg = cfg;
            _socksPort = socksPort;
            _log = log ?? delegate { };
        }

        public bool Alive { get { return _proc != null && !_proc.HasExited; } }
        public int Restarts { get { return _restarts; } }

        public bool Start()
        {
            var ssh = FindSsh();
            if (ssh == null)
            {
                _log("找不到 ssh.exe。请启用「设置 → 应用 → 可选功能 → OpenSSH 客户端」，或安装 Git。");
                return false;
            }

            lock (_lock)
            {
                _stopping = false;
                _running = true;
                if (!Launch(ssh)) { _running = false; return false; }
            }

            _watch = new Thread(WatchLoop);
            _watch.IsBackground = true;
            _watch.Name = "ferry-tunnel-watch";
            _watch.Start();
            return true;
        }

        public void Stop()
        {
            _stopping = true;
            _running = false;
            lock (_lock)
            {
                try { if (_proc != null && !_proc.HasExited) _proc.Kill(); } catch (Exception) { }
                _proc = null;
            }
            _log("隧道已停止");
        }

        bool Launch(string ssh)
        {
            try
            {
                var args = BuildArgs(ssh);
                var psi = new ProcessStartInfo(ssh, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardError = true;
                psi.RedirectStandardOutput = true;

                var p = new Process();
                p.StartInfo = psi;
                p.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e)
                {
                    if (!string.IsNullOrEmpty(e.Data)) _log("[ssh] " + e.Data);
                };
                p.OutputDataReceived += delegate (object s, DataReceivedEventArgs e)
                {
                    if (!string.IsNullOrEmpty(e.Data)) _log("[ssh] " + e.Data);
                };
                p.Start();
                p.BeginErrorReadLine();
                p.BeginOutputReadLine();
                _proc = p;
                _log("隧道已启动：ssh -D 127.0.0.1:" + _socksPort + " → " +
                     _cfg.RemoteHost + ":" + _cfg.SshPort);
                return true;
            }
            catch (Exception e)
            {
                _log("隧道启动失败：" + e.Message);
                return false;
            }
        }

        string BuildArgs(string ssh)
        {
            var key = FindKey();
            var sb = new StringBuilder();
            // -N 不执行远程命令，-D 动态转发（SOCKS5），-T 不分配终端
            sb.Append("-N -T ");
            sb.Append("-D 127.0.0.1:").Append(_socksPort).Append(' ');
            sb.Append("-p ").Append(_cfg.SshPort).Append(' ');
            // 服务器断开就退出，让看护线程重连；不设的话 ssh 会挂着不动
            sb.Append("-o ServerAliveInterval=15 -o ServerAliveCountMax=3 ");
            sb.Append("-o ExitOnForwardFailure=yes ");
            // 首次连接自动接受主机密钥（用户没有交互终端可以回答 yes）
            sb.Append("-o StrictHostKeyChecking=accept-new ");
            sb.Append("-o BatchMode=yes ");   // 绝不停下来问密码
            if (key != null)
                sb.Append("-i \"").Append(key).Append("\" ");
            sb.Append(_cfg.SshUser.Length > 0 ? _cfg.SshUser : "pcserver")
              .Append('@').Append(_cfg.RemoteHost);
            return sb.ToString();
        }

        /// <summary>
        /// 找密钥。按优先级：安装目录的 ferry_key → ~/.ssh/id_ed25519 → id_rsa。
        /// 安装目录优先是为了让「一台机器一个密钥」的情况不污染用户的 ~/.ssh。
        /// </summary>
        static string FindKey()
        {
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var local = Path.Combine(appDir, "ferry_key");
            if (File.Exists(local)) return local;

            var sshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
            var ed = Path.Combine(sshDir, "id_ed25519");
            if (File.Exists(ed)) return ed;
            var rsa = Path.Combine(sshDir, "id_rsa");
            if (File.Exists(rsa)) return rsa;
            return null;
        }

        static string FindSsh()
        {
            var sys = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"OpenSSH\ssh.exe");
            if (File.Exists(sys)) return sys;

            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var git = Path.Combine(pf, @"Git\usr\bin\ssh.exe");
            if (File.Exists(git)) return git;

            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                if (dir.Trim().Length == 0) continue;
                try
                {
                    var p = Path.Combine(dir.Trim(), "ssh.exe");
                    if (File.Exists(p)) return p;
                }
                catch (Exception) { }
            }
            return null;
        }

        /// <summary>
        /// 看护：ssh 退出就重连。
        ///
        /// 退避策略：5 s 起，每次失败翻倍，封顶 60 s。不做无限快速重试 ——
        /// 远端整机关掉时，每秒起一个 ssh 只会把 CPU 和日志刷满。
        /// </summary>
        void WatchLoop()
        {
            int backoff = 5;
            while (_running && !_stopping)
            {
                Thread.Sleep(1000);
                if (_stopping) return;

                Process p;
                lock (_lock) p = _proc;
                if (p == null) continue;

                bool dead;
                try { dead = p.HasExited; }
                catch (Exception) { dead = true; }
                if (!dead) { backoff = 5; continue; }

                if (_stopping || !_running) return;

                _restarts++;
                _log("隧道断开（第 " + _restarts + " 次），" + backoff + " 秒后重连…");
                for (int i = 0; i < backoff && !_stopping; i++) Thread.Sleep(1000);
                if (_stopping) return;

                var ssh = FindSsh();
                if (ssh == null) { _log("找不到 ssh.exe，停止重连"); _running = false; return; }
                lock (_lock)
                {
                    if (_stopping) return;
                    Launch(ssh);
                }
                backoff = Math.Min(backoff * 2, 60);
            }
        }
    }
}
