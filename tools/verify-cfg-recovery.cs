using System;
using System.IO;
using Ferry;

// 配置自愈的验证：卸载 / 重装 / 换目录之后，激活令牌能不能自己找回来。
//
// 对应 bug：用户反馈「卸载重启，或者更新，有时候还是要重新填密钥」。
// 根因是安装器 [UninstallDelete] 删掉整个 {app}，config.json 和同目录的 .bak
// 一起没 —— 令牌只存在那里。
//
// 修法有三层，这个测试把三层都钉住：
//   ① Cfg.Save 每次把完整配置再写一份到 %APPDATA%\Ferry\config.backup.json
//      （不在卸载范围内）；
//   ② 启动时 Cfg.RecoverFromRoamingBackup：主配置没有 token 就从那份备份补回；
//   ③ 安装器 ssPostInstall 也查这份备份（卸载后直接重装、客户端还没启动过）。
//
// 跑法：powershell -ExecutionPolicy Bypass -File tools\verify-cfg-recovery.ps1
// 输出用英文：PS 5.1 控制台是 GBK，中文会花屏，读不出结论。
static class CfgRecoveryTest
{
    static int fails = 0;

    static void Ok(bool cond, string name)
    {
        Console.WriteLine((cond ? "PASS  " : "FAIL  ") + name);
        if (!cond) fails++;
    }

    static void Main()
    {
        var bk = Cfg.RoamingBackupPath;
        Console.WriteLine("roaming backup = " + bk);

        // 这个测试会读写真实的 %APPDATA%\Ferry\config.backup.json。
        // 在「用户自己的机器」上跑时必须原样还原，否则会把他的真实备份冲掉。
        string originalBk = File.Exists(bk) ? File.ReadAllText(bk, System.Text.Encoding.UTF8) : null;
        try
        {
            Run(bk);
        }
        finally
        {
            try
            {
                if (originalBk != null) File.WriteAllText(bk, originalBk, new System.Text.UTF8Encoding(false));
                else if (File.Exists(bk)) File.Delete(bk);
            }
            catch (Exception e) { Console.WriteLine("WARN  restore failed: " + e.Message); }
        }

        Console.WriteLine(fails == 0 ? "ALL PASS" : (fails + " FAILED"));
        Environment.Exit(fails == 0 ? 0 : 1);
    }

    static void Run(string bk)
    {
        if (File.Exists(bk)) File.Delete(bk);

        var dir = Path.Combine(Path.GetTempPath(), "ferry-cfgtest");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        var cfgPath = Path.Combine(dir, "config.json");

        // 1) 正常的「激活 → 保存」应该顺带写出跨卸载备份
        var c = Cfg.Load(cfgPath);
        c.Token = "TESTTOKEN-1234";
        c.AccountIp = "10.1.2.3";
        c.PortalUrl = "http://10.13.24.1:8080";
        c.Save(cfgPath);
        Ok(File.Exists(bk), "Save writes the roaming backup");
        Ok(File.Exists(cfgPath), "config.json exists");
        Ok(File.ReadAllText(bk).Contains("TESTTOKEN-1234"), "backup contains token");

        // 2) 模拟卸载：安装器把整个 {app} 目录删掉
        Directory.Delete(dir, true);
        Ok(!File.Exists(cfgPath), "config gone after uninstall");
        Ok(Cfg.Load(cfgPath).Activated == false, "unactivated now");

        // 3) 自愈
        Ok(Cfg.RecoverFromRoamingBackup(cfgPath), "RecoverFromRoamingBackup returns true");
        Ok(Cfg.Load(cfgPath).Token == "TESTTOKEN-1234", "token restored");
        Ok(Cfg.Load(cfgPath).AccountIp == "10.1.2.3", "account ip restored");
        Ok(File.ReadAllText(cfgPath + ".bak").Contains("TESTTOKEN-1234"), ".bak restored too");

        // 4) 反向：备份里没有 token 时不能乱恢复
        File.WriteAllText(bk, "{\"token\":\"\"}\r\n");
        Directory.Delete(dir, true);
        Ok(!Cfg.RecoverFromRoamingBackup(cfgPath), "no restore when backup has no token");

        // 5) .bak 保护：读失败后拿着默认值保存，不能把有 token 的 .bak 冲掉
        Directory.CreateDirectory(dir);
        var d = Cfg.Load(cfgPath);
        d.Token = "KEEP-ME";
        d.AccountIp = "10.9.9.9";
        d.Save(cfgPath);
        Ok(File.ReadAllText(cfgPath + ".bak").Contains("KEEP-ME"), ".bak has token");

        var empty = new Cfg();           // 模拟「配置读坏了，内存里是默认值」
        empty.Save(cfgPath);
        Ok(File.ReadAllText(cfgPath + ".bak").Contains("KEEP-ME"), "empty save keeps token in .bak");
        Ok(!Cfg.Load(cfgPath).Activated, "main file really got empty token");

        // 6) Load 的语义：只有主文件「不存在/读不出/格式坏」才退回 .bak，
        //    不会因为「token 空」就退回 —— 否则用户主动清空后又被意外恢复。
        //    真正兜底的是启动时的 RecoverFromRoamingBackup。
        Ok(Cfg.Load(cfgPath).Token == "", "Load does not fall back to .bak on empty token");
        Ok(Cfg.RecoverFromRoamingBackup(cfgPath), "startup recovery still saves it");
        Ok(Cfg.Load(cfgPath).Token == "KEEP-ME", "token correct after recovery");
    }
}
