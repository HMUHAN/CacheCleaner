// QQ 缓存管家 - 托盘小工具
// 单文件 C# / .NET Framework 4.x / WinForms，用 csc 编译成 exe
// 功能：托盘常驻 + 设置面板 + 每 N 天自动清理 QQ（NT 版）图片/视频/表情/语音缓存

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("QQ 缓存管家")]
[assembly: System.Reflection.AssemblyDescription("按容量上限清理 QQ / 微信本地聊天缓存")]
[assembly: System.Reflection.AssemblyVersion("1.3.7.0")]

namespace QQCacheTray
{
    internal static class Program
    {
        internal static bool SettingsOnStart;
        internal static int SettingsTab;
        internal static bool ForceDark;
        internal static bool UpdateOnStart;
        internal static string PendingToast = "";

        // 只在测试时用：不走界面，直接下载并替换，方便自动化验证
        private static int RunSelfUpdateOnce()
        {
            try
            {
                Logger.EnsureDir();
                Config cfg = Config.Load();

                string used = cfg.UpdateUrl;
                string lastErr = "";
                UpdateChecker.Info info = string.IsNullOrEmpty(cfg.UpdateUrl)
                    ? UpdateChecker.FetchAny(out used, out lastErr)
                    : UpdateChecker.Fetch(cfg.UpdateUrl);
                if (info == null)
                {
                    Logger.Log("update-now: 清单获取失败 " + lastErr);
                    return 2;
                }

                Logger.Log("update-now: 远端 " + info.Version + " 当前 " + UpdateChecker.CurrentVersion +
                           " 来源 " + used);
                if (!UpdateChecker.IsNewer(info.Version))
                {
                    Logger.Log("update-now: 已是最新");
                    return 0;
                }

                string err, actual;
                string file = SelfUpdater.Download(info.Url, info.Sha256, out err, out actual);
                if (file == null)
                {
                    Logger.Log("update-now: 下载失败 " + err);
                    return 3;
                }

                string err2;
                if (!SelfUpdater.Apply(file, info.Sha256, "", out err2))
                {
                    Logger.Log("update-now: 替换失败 " + err2);
                    return 4;
                }

                Logger.Log("update-now: 已启动替换程序，主进程退出");
                return 0;
            }
            catch (Exception ex)
            {
                Logger.Log("update-now 异常：" + ex);
                return 1;
            }
        }

        // 只在测试时用：立刻按容量上限跑一次
        private static int RunLimitOnce()
        {
            try
            {
                Logger.EnsureDir();
                Config cfg = Config.Load();
                int code = 0;
                if (cfg.Enabled) { Cleaner.EnforceLimit(cfg, false, Logger.Log); code++; }
                if (cfg.WxEnabled) { Cleaner.EnforceLimit(cfg, true, Logger.Log); code++; }
                Logger.Log("limit-now 完成，处理了 " + code + " 个应用");
                return 0;
            }
            catch (Exception ex)
            {
                Logger.Log("limit-now 异常：" + ex);
                return 1;
            }
        }

        [STAThread]
        private static void Main(string[] args)
        {
            // 最早的一行痕迹：写到一个任何账户都写得进去的位置，
            // 用来回答“计划任务到底有没有把程序拉起来”
            try
            {
                File.AppendAllText(@"C:\Windows\Temp\qqcachetray-trace.log",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                    " cmd=" + Environment.CommandLine +
                    " user=" + WindowsIdentity.GetCurrent().Name + "\r\n");
            }
            catch { }

            bool selfTest = false;
            bool diag = false;
            bool bootMode = false;
            bool settingsMode = false;
            bool forceUpdate = false;
            bool updateNow = false;
            bool limitNow = false;
            foreach (string a in args)
            {
                if (string.Equals(a, "--boot", StringComparison.OrdinalIgnoreCase)) bootMode = true;
                if (string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)) selfTest = true;
                if (string.Equals(a, "--diag", StringComparison.OrdinalIgnoreCase)) diag = true;
                if (string.Equals(a, "--settings", StringComparison.OrdinalIgnoreCase)) settingsMode = true;
                if (string.Equals(a, "qq", StringComparison.OrdinalIgnoreCase)) Program.SettingsTab = 1;
                if (string.Equals(a, "wx", StringComparison.OrdinalIgnoreCase)) Program.SettingsTab = 0;
                if (string.Equals(a, "--dark", StringComparison.OrdinalIgnoreCase)) Program.ForceDark = true;
                if (string.Equals(a, "--update", StringComparison.OrdinalIgnoreCase)) forceUpdate = true;
                if (string.Equals(a, "--update-now", StringComparison.OrdinalIgnoreCase)) updateNow = true;
                if (string.Equals(a, "--limit-now", StringComparison.OrdinalIgnoreCase)) limitNow = true;
            }

            Program.SettingsOnStart = settingsMode;
            Program.UpdateOnStart = forceUpdate;

            if (updateNow)
            {
                Environment.ExitCode = RunSelfUpdateOnce();
                return;
            }

            if (limitNow)
            {
                Environment.ExitCode = RunLimitOnce();
                return;
            }

            if (diag)
            {
                RunDiag();
                return;
            }

            Logger.EnsureDir();

            // 开机自启：两个应用都没启用容量上限清理时，只亮一下图标提示后退出
            Config bootCfg = Config.Load();
            bool needResident = Cleaner.WillStayResident(bootCfg);

            if (bootMode && !needResident)
            {
                Environment.ExitCode = RunBootCheck();
                return;
            }

            if (bootMode)
            {
                Logger.Log(string.Format("开机启动：按容量上限常驻检查（QQ={0}，微信={1}）[boot-resident]",
                    bootCfg.Enabled && !bootCfg.OneShot, bootCfg.WxEnabled && !bootCfg.WxOneShot));
            }

            if (selfTest)
            {
                Environment.ExitCode = RunSelfTest();
                return;
            }

            int selfPid = Process.GetCurrentProcess().Id;
            Logger.Log(string.Format("启动 pid={0} 提权={1} 完整性={2} 父进程={3}",
                selfPid, Guard.IsElevated(), Guard.IntegrityName(selfPid), Guard.DescribeParent(selfPid)));

            bool elevated = Guard.IsElevated();

            // 提权实例负责清场：低权限实例既不能注册托盘也不能写配置，还会占着单实例锁
            if (elevated) Guard.KillStaleInstances();

            Mutex mutex = null;
            bool createdNew = false;
            int tries = elevated ? 10 : 1;
            for (int i = 0; i < tries; i++)
            {
                mutex = new Mutex(true, "QQCacheTray_SingleInstance_9F1C", out createdNew);
                if (createdNew) break;
                mutex.Dispose();
                mutex = null;
                if (i + 1 < tries) Thread.Sleep(300);
            }

            if (mutex == null) return;   // 已有实例在跑

            // 混合配置：一方常驻、另一方勾了「开启」——把勾选方的一次性检查补上
            if (bootMode)
            {
                string oneshotMsg = RunOneShotChecks(bootCfg, true);
                if (oneshotMsg.Length > 0) Program.PendingToast = oneshotMsg;
            }

            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayContext());
            }
            finally
            {
                try { mutex.ReleaseMutex(); } catch { }
                mutex.Dispose();
            }
        }

        // 开机检查模式（--boot）：所有启用的应用都勾了「开启」（只检查一次）时使用。
        // 按上限检查一次，亮图标 + 气泡报结果，然后退出（不常驻）。
        private static int RunBootCheck()
        {
            Config cfg = Config.Load();
            bool anyEnabled = cfg.Enabled || cfg.WxEnabled;
            bool anyOneShot = (cfg.Enabled && cfg.OneShot) || (cfg.WxEnabled && cfg.WxOneShot);

            string sub = anyOneShot
                ? "已勾选「开启」，检查完会自动退出"
                : (anyEnabled ? "检查完会自动退出" : "未启用容量上限清理，检查完自动退出");

            NotifyIcon icon = null;
            try
            {
                icon = new NotifyIcon();
                icon.Icon = IconFactory.Create(SystemInformation.SmallIconSize.Width);
                icon.Text = "QQ 缓存管家（开机检查）";
                icon.Visible = true;
                Balloon(icon, "开机检查中…", sub);
                Logger.Log("开机检查：已显示托盘图标和提示气泡（" + sub + "）[boot-visible]");
            }
            catch { }

            try
            {
                if (!anyEnabled)
                {
                    Balloon(icon, "开机检查完成", "未启用容量上限清理，本次不做任何事");
                    Logger.Log("开机检查完成：未启用容量上限清理，本次不做任何事，退出 [boot-oneshot]");
                    return 0;
                }

                string bootMsg = RunOneShotChecks(cfg, false);
                if (bootMsg.Length > 0)
                {
                    Balloon(icon, "开机清理完成", bootMsg);
                    Logger.Log("开机检查完成：已按容量上限清理，一次性检查，本次退出 [boot-oneshot]");
                }
                else
                {
                    Balloon(icon, "开机检查完成", "容量上限未超，无需清理；本次检查后退出");
                    Logger.Log("开机检查完成：容量上限未超，一次性检查，本次退出 [boot-oneshot]");
                }
                return 0;
            }
            catch (Exception ex)
            {
                Logger.Log("开机检查异常：" + ex);
                return 0;
            }
            finally
            {
                PumpFor(3500);
                try { if (icon != null) { icon.Visible = false; icon.Dispose(); } } catch { }
            }
        }

        // 对启用的应用各做一次容量上限检查。
        // onlyOneShot=true 时只查"勾了开启"的（常驻启动时补跑它们的一次性检查）。
        // 返回给人看的汇总（清理了什么）；没删东西返回空串。
        private static string RunOneShotChecks(Config cfg, bool onlyOneShot)
        {
            StringBuilder sb = new StringBuilder();

            if (cfg.Enabled && (!onlyOneShot || cfg.OneShot))
            {
                Logger.Log("开机一次性检查：QQ 容量上限");
                CleanResult lq = Cleaner.EnforceLimit(cfg, false, Logger.Log);
                if (lq != null && lq.TotalDeleted > 0)
                    sb.AppendLine(string.Format("QQ：已清理 {0} 个文件，释放 {1}", lq.TotalDeleted, Cleaner.FormatSize(lq.TotalFreed)));
                Logger.Log("开机一次性检查：QQ 完成 [boot-oneshot]");
            }

            if (cfg.WxEnabled && (!onlyOneShot || cfg.WxOneShot))
            {
                Logger.Log("开机一次性检查：微信容量上限");
                CleanResult lw = Cleaner.EnforceLimit(cfg, true, Logger.Log);
                if (lw != null && lw.TotalDeleted > 0)
                    sb.AppendLine(string.Format("微信：已清理 {0} 个文件，释放 {1}", lw.TotalDeleted, Cleaner.FormatSize(lw.TotalFreed)));
                Logger.Log("开机一次性检查：微信 完成 [boot-oneshot]");
            }

            return sb.ToString().TrimEnd('\n');
        }
        private static void Balloon(NotifyIcon icon, string title, string text)
        {
            try
            {
                if (icon != null) icon.ShowBalloonTip(5000, title, text, ToolTipIcon.Info);
            }
            catch { }
        }

        private static void PumpFor(int ms)
        {
            DateTime end = DateTime.Now.AddMilliseconds(ms);
            while (DateTime.Now < end)
            {
                Application.DoEvents();
                Thread.Sleep(50);
            }
        }

        // --diag：把数据目录/写入权限等环境信息写到 %TEMP%\qqcachetray-diag.txt
        private static void RunDiag()
        {
            StringBuilder sb = new StringBuilder();
            try
            {
                sb.AppendLine("time=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("exe=" + Application.ExecutablePath);
                sb.AppendLine("GetFolderPath(ApplicationData)=" + Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
                sb.AppendLine("Dir=" + Config.Dir);
                sb.AppendLine("TempPath=" + Path.GetTempPath());
                sb.AppendLine("env QQCACHE_DATA=" + Environment.GetEnvironmentVariable("QQCACHE_DATA"));
                sb.AppendLine("startup_dir=" + Environment.GetFolderPath(Environment.SpecialFolder.Startup));
                sb.AppendLine("autostart_script=" + AutoStart.IsEnabled());
                sb.AppendLine("autostart_blocked=" + AutoStart.IsBlocked());
                sb.AppendLine("autostart_state=" + AutoStart.DescribeState());

                try
                {
                    if (!Directory.Exists(Config.Dir)) Directory.CreateDirectory(Config.Dir);
                    sb.AppendLine("CreateDirectory OK, exists=" + Directory.Exists(Config.Dir));
                }
                catch (Exception ex)
                {
                    sb.AppendLine("CreateDirectory FAILED: " + ex.GetType().Name + " " + ex.Message);
                }

                try
                {
                    string probe = Path.Combine(Config.Dir, "probe.txt");
                    File.WriteAllText(probe, "ok", Encoding.UTF8);
                    sb.AppendLine("WriteFile OK: " + probe);
                }
                catch (Exception ex)
                {
                    sb.AppendLine("WriteFile FAILED: " + ex.GetType().Name + " " + ex.Message);
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("DIAG FAILED: " + ex);
            }

            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "qqcachetray-diag.txt"), sb.ToString(), Encoding.UTF8);
            }
            catch { }

            try { Console.WriteLine(sb.ToString()); } catch { }
        }
        // 自检：不创建窗口，静态检查界面布局与配置读写
        private static int RunSelfTest()
        {
            StringBuilder sb = new StringBuilder();
            try
            {
                Config cfg = Config.Load();
                AutoStart.IsEnabled();
                LegacyTask.Exists();

                using (SettingsForm f = new SettingsForm(cfg))
                {
                    sb.AppendLine("QQ 缓存管家 界面自检 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    sb.AppendLine("Form 客户区: " + f.ClientSize.Width + " x " + f.ClientSize.Height);
                    sb.AppendLine("标题: " + f.Text);
                    sb.AppendLine();

                    Dump(sb, f.Controls, 1);
                    sb.AppendLine();

                    foreach (Control c in All(f))
                    {
                        Control p = c.Parent;
                        if (p == null) continue;

                        int limW = p.ClientSize.Width;
                        int limH = p.ClientSize.Height;

                        // TabPage 未显示时还是默认的 200x100，按 TabControl 的实际尺寸算
                        TabPage tp = p as TabPage;
                        if (tp != null && tp.Parent is TabControl)
                        {
                            limW = tp.Parent.Width - 8;
                            limH = tp.Parent.Height - 28;
                        }

                        if (c.Right > limW || c.Bottom > limH)
                        {
                            sb.AppendLine(string.Format("OUT_OF_BOUNDS: {0} \"{1}\" 右下({2},{3}) 超出父容器 {4}x{5}",
                                c.GetType().Name, c.Text, c.Right, c.Bottom, limW, limH));
                        }
                    }
                    CheckOverlap(sb, f, 0);
                }

                // 配置往返：新字段读写
                string cfgBackup = File.Exists(Config.ConfigPath) ? File.ReadAllText(Config.ConfigPath) : null;

                Config round = new Config();
                round.Enabled = false;
                round.OneShot = true;
                round.LimitMB = 1234;
                round.LimitDeleteAll = true;
                round.Pic = false;
                round.WxLimitMB = 3456;
                round.Emoji = false;
                round.Save();
                Config back = Config.Load();
                bool ok = back.Enabled == false && back.OneShot == true && back.LimitMB == 1234
                          && back.LimitDeleteAll == true && back.Pic == false
                          && back.WxLimitMB == 3456 && back.Emoji == false;

                // 旧版定时清理配置的迁移：原来用定时的应用升级后必须“不自动清理”
                // 同时验旧键 schedule_on 的反向迁移（1=常驻 → OneShot=false）
                bool migTimed = false, migLimit = false, migOneShot = false;
                try
                {
                    string oldTimed =
                        "enabled=1\r\nauto_clean=1\r\nschedule_on=1\r\ninterval_days=7\r\nclean_hour=3\r\nkeep_days=0\r\n" +
                        "limit_enabled=0\r\nwx_enabled=1\r\nwx_auto_clean=1\r\nwx_schedule_on=1\r\nwx_limit_enabled=0\r\n";
                    File.WriteAllText(Config.ConfigPath, oldTimed, Encoding.UTF8);
                    Config m1 = Config.Load();
                    migTimed = (m1.Enabled == false) && (m1.WxEnabled == false)
                               && (m1.OneShot == false) && (m1.WxOneShot == false);

                    string oldLimit =
                        "enabled=1\r\nschedule_on=0\r\ninterval_days=7\r\nlimit_enabled=1\r\nlimit_mb=400\r\n" +
                        "wx_enabled=1\r\nwx_limit_enabled=0\r\n";
                    File.WriteAllText(Config.ConfigPath, oldLimit, Encoding.UTF8);
                    Config m2 = Config.Load();
                    migLimit = (m2.Enabled == true) && (m2.LimitMB == 400) && (m2.WxEnabled == false);
                    migOneShot = (m2.OneShot == true);
                }
                catch { }

                try
                {
                    if (cfgBackup != null) File.WriteAllText(Config.ConfigPath, cfgBackup);
                    else File.Delete(Config.ConfigPath);
                }
                catch { }

                Config r1 = new Config(); r1.Enabled = false; r1.WxEnabled = false;
                Config r2 = new Config(); r2.Enabled = true; r2.OneShot = false; r2.WxEnabled = false;
                Config r3 = new Config(); r3.Enabled = false; r3.WxEnabled = true; r3.WxOneShot = false;
                Config r4 = new Config(); r4.Enabled = true; r4.OneShot = true; r4.WxEnabled = false;
                bool rOk = !Cleaner.WillStayResident(r1) && Cleaner.WillStayResident(r2)
                           && Cleaner.WillStayResident(r3) && !Cleaner.WillStayResident(r4);

                string sample = "{\"version\": \"1.2.3\", \"url\": \"https://example.com/a.exe\", \"notes\": \"测试\"}";
                UpdateChecker.Info ui = UpdateChecker.Parse(sample);
                bool uOk = ui != null && ui.Version == "1.2.3"
                           && ui.Url == "https://example.com/a.exe"
                           && UpdateChecker.IsNewer((UpdateChecker.CurrentVersion.Major + 1) + ".0.0")
                           && !UpdateChecker.IsNewer(UpdateChecker.CurrentVersion.ToString());
                sb.AppendLine("更新解析: " + (uOk ? "PASS" : "FAIL")
                              + " 版本=" + (ui == null ? "null" : ui.Version)
                              + " 当前=" + UpdateChecker.CurrentVersion);

                string tmpFile = Path.Combine(Config.Dir, "selftest-hash.txt");
                File.WriteAllText(tmpFile, "abc");
                string h1 = SelfUpdater.Sha256(tmpFile);
                File.Delete(tmpFile);
                bool hOk = h1.Length == 64 &&
                           h1 == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
                sb.AppendLine("哈希校验: " + (hOk ? "PASS" : "FAIL") + " sha256(abc)=" + h1.Substring(0, 12) + "…");

                sb.AppendLine("常驻判定: " + (rOk ? "PASS" : "FAIL")
                              + " 都关=" + Cleaner.WillStayResident(r1)
                              + " 只QQ=" + Cleaner.WillStayResident(r2)
                              + " 只微信=" + Cleaner.WillStayResident(r3)
                              + " 勾了开启=" + Cleaner.WillStayResident(r4));

                Config b1 = new Config(); b1.Enabled = true; b1.WxEnabled = true;
                Config b2 = new Config(); b2.Enabled = true; b2.OneShot = true; b2.WxEnabled = true;
                Config b3 = new Config(); b3.Enabled = true; b3.OneShot = true; b3.WxEnabled = true; b3.WxOneShot = true;
                Config b4 = new Config(); b4.Enabled = false; b4.WxEnabled = false;
                string bt1 = SettingsForm.BootBehaviourWarning(b1);
                string bt2 = SettingsForm.BootBehaviourWarning(b2);
                string bt3 = SettingsForm.BootBehaviourWarning(b3);
                string bt4 = SettingsForm.BootBehaviourWarning(b4);
                bool wOk = bt1.Length == 0
                           && bt2.Contains("QQ：只在开机检查一次后退出")
                           && bt2.Contains("微信：常驻")
                           && bt3.Contains("不会常驻后台")
                           && bt4.Contains("未启用");
                sb.AppendLine("开机提示文案: " + (wOk ? "PASS" : "FAIL")
                              + " 全常驻=空:" + (bt1.Length == 0)
                              + " 混合:" + (bt2.Length > 0)
                              + " 全勾:" + (bt3.Length > 0)
                              + " 全关:" + (bt4.Length > 0));

                sb.AppendLine("配置往返测试: " + (ok ? "PASS" : "FAIL"));
                sb.AppendLine("配置迁移测试: " + ((migTimed && migLimit && migOneShot) ? "PASS" : "FAIL")
                              + " 定时用户=停用:" + migTimed + " 上限用户=保留:" + migLimit
                              + " 旧schedule_on=0→勾开启:" + migOneShot);
                sb.AppendLine("开机自启: " + AutoStart.DescribeState() + " blocked=" + AutoStart.IsBlocked());
                sb.AppendLine("last-clean.json 内容: " + ReadJsonSafe());
                UpdateChecker.Info ui2 = new UpdateChecker.Info();
                ui2.Version = "1.2.3"; ui2.Url = "https://example.com/a.exe"; ui2.Sha256 = "abc"; ui2.Notes = "n";
                using (Form d3 = UpdatePrompt.Build("1.2.2.0", ui2, true))
                {
                    foreach (Control c in d3.Controls)
                        sb.AppendLine("UPD_CTRL: " + c.GetType().Name + " text=[" + c.Text + "] bounds=" + c.Bounds);
                }

                // 容量显示：统计期间不得显示 0 B；切主题不得改数值、不得重新统计
                try
                {
                    string fakeRoot = Path.Combine(Config.Dir, "selftest-cache");
                    try { if (Directory.Exists(fakeRoot)) Directory.Delete(fakeRoot, true); } catch { }
                    string fakeNtData = Path.Combine(fakeRoot, "nt_data");
                    Directory.CreateDirectory(Path.Combine(fakeNtData, "Pic", "2026-11"));
                    Directory.CreateDirectory(Path.Combine(fakeNtData, "Video"));
                    string[] fakeFiles = new string[]
                    {
                        Path.Combine(fakeNtData, "Pic", "2026-11", "a.jpg"),
                        Path.Combine(fakeNtData, "Pic", "2026-11", "b.jpg"),
                        Path.Combine(fakeNtData, "Video", "c.mp4")
                    };
                    long expect = 0;
                    foreach (string f in fakeFiles)
                    {
                        using (FileStream fs = File.Create(f)) fs.SetLength(1024 * 1024);
                        expect += 1024 * 1024;
                    }

                    Config fakeCfg = new Config();
                    fakeCfg.QqRoot = fakeNtData;   // 手动指定根目录，统计只读这棵假目录，不碰真实缓存

                    bool noEarlyScan, gotValue, darkKeep, contrastOk, tabOk, textFitOk;
                    string contrastInfo = "";
                    string textFitInfo = "";
                    string tabInfo = "";
                    string pendingRing = "";
                    string actualRing = "";
                    int scanBeforeShow, scanBeforeToggle, scanAfter;

                    using (SettingsForm uf = new SettingsForm(fakeCfg))
                    {
                        scanBeforeShow = uf.ScanCountForTest;
                        noEarlyScan = scanBeforeShow == 0;   // 配置还没装进控件之前不允许统计

                        uf.Show();
                        Application.DoEvents();
                        uf.MeasureForTest(false);   // 自检可能停在微信页，这里显式统计 QQ 页
                        Application.DoEvents();

                        // 统计还在跑的时候，环形图不能给出 0 B 这种假数值
                        DateTime until = DateTime.Now.AddSeconds(6);
                        while (DateTime.Now < until)
                        {
                            Application.DoEvents();
                            string sizeText = uf.SizeTextForTest(false);
                            string ringText = uf.RingTextForTest(false);
                            if (sizeText.Contains("统计中") && ringText == Cleaner.FormatSize(0))
                                pendingRing = ringText;
                            if (!sizeText.Contains("统计中")) break;
                            System.Threading.Thread.Sleep(10);
                        }

                        actualRing = uf.RingTextForTest(false);
                        gotValue = actualRing == Cleaner.FormatSize(expect)
                                   && uf.DetectTextForTest(false).Contains("nt_data");

                        scanBeforeToggle = uf.ScanCountForTest;
                        uf.ToggleDarkForTest();   // 勾/取消“黑夜模式”，走真实事件
                        Application.DoEvents();
                        scanAfter = uf.ScanCountForTest;
                        darkKeep = uf.RingTextForTest(false) == Cleaner.FormatSize(expect)
                                   && scanAfter == scanBeforeToggle;

                        // 页面串台：默认微信 -> 切 QQ -> 切黑夜，画面必须还是 QQ
                        uf.SelectTabForTest(0);
                        uf.SelectTabForTest(1);
                        uf.ToggleDarkForTest();
                        Application.DoEvents();
                        tabOk = uf.TabIndexForTest == 1 && uf.PageZOrderForTest(false) == 0 && uf.PageVisibleForTest(false)
                                && !uf.PageVisibleForTest(true) && uf.SysEmojiVisibleForTest;
                        tabInfo = "选中=" + uf.TabIndexForTest + " QQ可见=" + uf.PageVisibleForTest(false)
                                + " 微信可见=" + uf.PageVisibleForTest(true) + " QQ层序=" + uf.PageZOrderForTest(false);
                        uf.ToggleDarkForTest();
                        Application.DoEvents();

                        // 文字不裁剪：当前页每个 Label/Button/CheckBox 的文字宽度必须放得下
                        int tfTotal = 0, tfBad = 0; string tfNames;
                        uf.TextFitForTest(false, out tfTotal, out tfBad, out tfNames);
                        textFitOk = tfTotal > 5 && tfBad == 0;
                        textFitInfo = "控件=" + tfTotal + " 溢出=" + tfBad + (tfNames.Length > 0 ? " [" + tfNames + "]" : "");

                        int ctDark = 0, cbDark = 0; string nDark;                        uf.ContrastForTest(false, out ctDark, out cbDark, out nDark);
                        uf.ToggleDarkForTest();   // 切回原来的主题
                        Application.DoEvents();
                        int ctLight = 0, cbLight = 0; string nLight;
                        uf.ContrastForTest(false, out ctLight, out cbLight, out nLight);
                        contrastOk = ctDark > 5 && ctLight > 5 && cbDark == 0 && cbLight == 0;
                        contrastInfo = "暗=" + ctDark + "/" + cbDark + " 亮=" + ctLight + "/" + cbLight
                                     + ((nDark + nLight).Length > 0 ? " 看不见=" + nDark + nLight : "");

                        uf.Close();
                    }

                    try { Directory.Delete(fakeRoot, true); } catch { }

                    bool capOk = noEarlyScan && gotValue && darkKeep && pendingRing.Length == 0;
                    sb.AppendLine("容量显示测试: " + (capOk ? "PASS" : "FAIL")
                                  + " 构造期统计次数=" + scanBeforeShow
                                  + " 数值正确=" + gotValue
                                  + " 期望=" + Cleaner.FormatSize(expect)
                                  + " 实际=[" + actualRing + "]"
                                  + " 统计中误显示=[" + pendingRing + "]"
                                  + " 切主题前统计次数=" + scanBeforeToggle
                                  + " 切主题后=" + scanAfter);
                    sb.AppendLine("主题对比度: " + (contrastOk ? "PASS" : "FAIL") + " " + contrastInfo);
                    sb.AppendLine("页面串台: " + (tabOk ? "PASS" : "FAIL") + " " + tabInfo);
                    sb.AppendLine("文字不裁剪: " + (textFitOk ? "PASS" : "FAIL") + " " + textFitInfo);
                }
                catch (Exception ex)
                {
                    sb.AppendLine("容量显示测试: FAIL 异常 " + ex.Message);
                }

                // 容量上限：没超上限时不该把每个文件建成对象（快路径）
                try
                {
                    string fastRoot = Path.Combine(Config.Dir, "selftest-limit");
                    try { if (Directory.Exists(fastRoot)) Directory.Delete(fastRoot, true); } catch { }
                    string fastNtData = Path.Combine(fastRoot, "nt_data");
                    string fastPic = Path.Combine(fastNtData, "Pic");
                    Directory.CreateDirectory(fastPic);
                    byte[] oneKb = new byte[1024];
                    for (int i = 0; i < 4000; i++) File.WriteAllBytes(Path.Combine(fastPic, "x" + i + ".jpg"), oneKb);

                    Config lc = new Config();
                    lc.QqRoot = fastNtData;
                    lc.LimitMB = 100;                     // 4 MB 远低于 100 MB
                    CleanResult lr = Cleaner.EnforceLimit(lc, false, null);
                    int left = 0;
                    try { left = Directory.GetFiles(fastPic, "*", SearchOption.AllDirectories).Length; } catch { }
                    bool fastOk = lr.TotalDeleted == 0 && !Cleaner.LastLimitCollectedFiles && left == 4000;
                    sb.AppendLine("容量上限快路径: " + (fastOk ? "PASS" : "FAIL")
                                  + " 收集文件列表=" + Cleaner.LastLimitCollectedFiles
                                  + " 删除=" + lr.TotalDeleted + " 剩余=" + left);
                    try { Directory.Delete(fastRoot, true); } catch { }
                }
                catch (Exception ex) { sb.AppendLine("容量上限快路径: FAIL 异常 " + ex.Message); }

                // 唤醒节奏：任一应用启用 → 60 秒；都没启用 → 300 秒
                try
                {
                    DateTime nowTick = DateTime.Now;
                    Config w0 = new Config(); w0.Enabled = false; w0.WxEnabled = false;
                    Config w1 = new Config(); w1.Enabled = true; w1.OneShot = false; w1.WxEnabled = false;
                    Config w2 = new Config(); w2.Enabled = false; w2.WxEnabled = true; w2.WxOneShot = false;
                    Config w3 = new Config(); w3.Enabled = true; w3.OneShot = true; w3.WxEnabled = false;

                    int s0 = TrayContext.NextTickSeconds(w0, nowTick);
                    int s1 = TrayContext.NextTickSeconds(w1, nowTick);
                    int s2 = TrayContext.NextTickSeconds(w2, nowTick);
                    int s3 = TrayContext.NextTickSeconds(w3, nowTick);
                    bool tickOk = s0 == 300 && s1 == 60 && s2 == 60 && s3 == 300;
                    sb.AppendLine("空闲节奏: " + (tickOk ? "PASS" : "FAIL")
                                  + " 都关=" + s0 + " 只QQ=" + s1 + " 只微信=" + s2 + " 勾了开启=" + s3);
                }
                catch (Exception ex) { sb.AppendLine("空闲节奏: FAIL 异常 " + ex.Message); }

                // 上限检查节奏：离上限越远越少扫
                try
                {
                    int m0 = TrayContext.NextLimitMinutes(0.1);
                    int m1 = TrayContext.NextLimitMinutes(0.5);
                    int m2 = TrayContext.NextLimitMinutes(0.9);
                    bool mOk = m0 == 30 && m1 == 10 && m2 == 5;
                    sb.AppendLine("上限节奏: " + (mOk ? "PASS" : "FAIL")
                                  + " 10%=" + m0 + " 50%=" + m1 + " 90%=" + m2);
                }
                catch (Exception ex) { sb.AppendLine("上限节奏: FAIL 异常 " + ex.Message); }

                try
                {
                    Config shot = Config.Load();
                    using (SettingsForm f = new SettingsForm(shot))
                    {
                        UpdateChecker.Info fake = new UpdateChecker.Info();
                        fake.Version = "1.3.9"; fake.Url = "http://example.com/a.exe";
                        fake.Sha256 = "abc"; fake.Notes = "自检用";
                        f.UpdateInfo = fake;
                        f.Show();
                        Application.DoEvents();
                        System.Threading.Thread.Sleep(300);
                        Application.DoEvents();
                        Bitmap bmp = new Bitmap(f.Width, f.Height);
                        f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                        bmp.Save(Path.Combine(Config.Dir, "settings.png"), System.Drawing.Imaging.ImageFormat.Png);
                        bmp.Dispose();
                        f.Close();
                        sb.AppendLine("设置界面截图: settings.png");
                    }
                }
                catch (Exception ex) { sb.AppendLine("设置界面截图失败：" + ex.Message); }

                string body = sb.ToString();
                int failCount = 0;
                foreach (string line in body.Split('\n'))
                {
                    if (line.IndexOf("FAIL", StringComparison.Ordinal) >= 0) failCount++;
                    else if (line.IndexOf("OUT_OF_BOUNDS", StringComparison.Ordinal) >= 0) failCount++;
                    else if (line.IndexOf("DIALOG_OVERFLOW", StringComparison.Ordinal) >= 0) failCount++;
                    else if (line.IndexOf("OVERLAP", StringComparison.Ordinal) >= 0) failCount++;
                }

                if (failCount == 0) sb.AppendLine("RESULT: OK");
                else sb.AppendLine("RESULT: FAIL(" + failCount + ")");

                File.WriteAllText(Path.Combine(Config.Dir, "selftest-report.txt"), sb.ToString(), Encoding.UTF8);
                return failCount == 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                sb.AppendLine("RESULT: EXCEPTION");
                sb.AppendLine(ex.ToString());
                try { File.WriteAllText(Path.Combine(Config.Dir, "selftest-report.txt"), sb.ToString(), Encoding.UTF8); }
                catch { }
                return 1;
            }
        }

        private static string ReadJsonSafe()
        {
            try
            {
                if (!File.Exists(Config.LastCleanJsonPath)) return "(不存在)";
                return File.ReadAllText(Config.LastCleanJsonPath).Replace("\r", " ").Replace("\n", " ");
            }
            catch { return "(读取失败)"; }
        }

        private static void Dump(StringBuilder sb, Control.ControlCollection controls, int depth)
        {
            string pad = new string(' ', depth * 2);
            foreach (Control c in controls)
            {
                sb.AppendLine(string.Format("{0}{1} [{2},{3} {4}x{5}]{6}{7}",
                    pad, c.GetType().Name, c.Left, c.Top, c.Width, c.Height,
                    c.Visible ? "" : " (隐藏)",
                    string.IsNullOrEmpty(c.Text) ? "" : " \"" + c.Text + "\""));
                if (c.Controls.Count > 0) Dump(sb, c.Controls, depth + 1);
            }
        }

        private static IEnumerable<Control> All(Control root)
        {
            foreach (Control c in root.Controls)
            {
                yield return c;
                foreach (Control d in All(c)) yield return d;
            }
        }

        private static void CheckOverlap(StringBuilder sb, Control parent, int depth)
        {
            List<Control> kids = new List<Control>();
            foreach (Control c in parent.Controls)
            {
                kids.Add(c);
            }
            for (int i = 0; i < kids.Count; i++)
            {
                for (int j = i + 1; j < kids.Count; j++)
                {
                    if (kids[i] is TabPage && kids[j] is TabPage) continue;
                    // 同位置互斥显示的页面（微信/QQ）本来就完全重叠，显式打堆叠标记跳过
                    if (IsStacked(kids[i]) && IsStacked(kids[j])) continue;
                    if (kids[i].Bounds.IntersectsWith(kids[j].Bounds))
                    {
                        sb.AppendLine(string.Format("OVERLAP: \"{0}\" <-> \"{1}\"", kids[i].Text, kids[j].Text));
                    }
                }
            }
            foreach (Control c in parent.Controls) CheckOverlap(sb, c, depth + 1);
        }

        private static bool IsStacked(Control c)
        {
            return c != null && string.Equals(c.Tag as string, "stack", StringComparison.Ordinal);
        }

    }

    // ---------------------------------------------------------------- 配置

    internal sealed class Config
    {
        public bool Enabled = true;              // 启用容量上限清理（QQ）
        public bool OneShot = false;             // 勾选「开启」：开机只检查一次后退出（不常驻）
        public bool Pic = true;
        public bool Video = true;
        public bool Emoji = true;
        public bool Ptt = true;
        public bool IncludeSystemEmoji = false;
        public bool IncludePersonalEmoji = false;
        public bool AutostartEnabled = false;
        public bool LegacyCleaned = false;   // 旧版计划任务/启动项只清一次，之后不再拉 schtasks
        public bool UpdateCheck = true;
        public bool DarkMode = false;
        public string UpdateUrl = "";
        public DateTime LastUpdateCheck = DateTime.MinValue;
        public string QqRoot = "";
        public DateTime LastRun = DateTime.MinValue;   // 只用于界面显示“上次清理”

        // 容量上限：唯一的自动清理策略
        public int LimitMB = 2048;
        public bool LimitDeleteAll = false;

        // 微信
        public bool WxEnabled = true;
        public bool WxOneShot = false;
        public bool WxPic = true;
        public bool WxVideo = true;
        public bool WxEmoji = true;
        public bool WxVoice = true;
        public int WxLimitMB = 2048;
        public bool WxLimitDeleteAll = false;
        public string WxRoot = "";
        public DateTime WxLastRun = DateTime.MinValue;

        private static string _dir;

        // 依次尝试：环境变量 → 程序目录 data\ → %ProgramData% → %APPDATA% → %TEMP%
        // 程序目录放第一位：SYSTEM 后台任务和提权界面写的是同一份配置，
        // 而 ProgramData 在这台机器上对受限进程不可写。
        public static string Dir
        {
            get
            {
                if (_dir != null) return _dir;

                string over = Environment.GetEnvironmentVariable("QQCACHE_DATA");
                if (!string.IsNullOrEmpty(over)) { _dir = over; return _dir; }

                string exeDir = Path.GetDirectoryName(Application.ExecutablePath);

                // 放在桌面/下载目录时不往那里写 data，改用本地应用数据目录
                if (!IsAwkwardLocation(exeDir))
                {
                    string portable = Path.Combine(exeDir, "data");
                    if (TryCreate(portable)) { _dir = portable; return _dir; }
                }

                string local = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CacheCleaner");
                if (TryCreate(local)) { _dir = local; return _dir; }

                string common = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "QQCacheTray");
                if (TryCreate(common)) { _dir = common; return _dir; }

                string appData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "QQCacheTray");
                if (TryCreate(appData)) { _dir = appData; return _dir; }

                string temp = Path.Combine(Path.GetTempPath(), "QQCacheTray");
                if (TryCreate(temp)) { _dir = temp; return _dir; }

                _dir = appData;
                return _dir;
            }
        }

        // 受限 = 没提权、且连 %ProgramData% 都写不进去（这台机器上的普通进程就是这样）
        public static bool IsRestricted
        {
            get
            {
                if (Guard.IsElevated()) return false;

                string common = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "QQCacheTray");
                return !TryCreate(common);
            }
        }

        // 不仅看目录在不在，还真写一个文件试试：
        // 提权实例建过目录之后，低权限实例也能看到目录存在，但写不进去
        // 桌面 / 下载 / 临时目录：这些地方不适合生成 data
        private static bool IsAwkwardLocation(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return false;

            try
            {
                string full = Path.GetFullPath(dir).TrimEnd('\\');

                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string[] bad = new string[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    Path.Combine(profile, "Desktop"),
                    Path.Combine(profile, "Downloads"),
                    Path.GetTempPath()
                };

                foreach (string b in bad)
                {
                    if (string.IsNullOrEmpty(b)) continue;
                    if (string.Equals(full, Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { }

            return false;
        }

        private static bool TryCreate(string path)
        {
            try
            {
                if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                string probe = Path.Combine(path, ".writetest");
                File.WriteAllText(probe, "1");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        public static string ConfigPath { get { return Path.Combine(Dir, "config.ini"); } }
        public static string LastRunPath { get { return Path.Combine(Dir, "last-run.txt"); } }
        public static string LastCleanJsonPath { get { return Path.Combine(Dir, "last-clean.json"); } }
        public static string WxLastCleanJsonPath { get { return Path.Combine(Dir, "wx-last-clean.json"); } }
        public static string LogDir { get { return Path.Combine(Dir, "logs"); } }

        public List<string> Categories()
        {
            List<string> list = new List<string>();
            if (Pic) list.Add("Pic");
            if (Video) list.Add("Video");
            if (Emoji) list.Add("Emoji");
            if (Ptt) list.Add("Ptt");
            return list;
        }

        public static Config Load()
        {
            Config c = new Config();
            bool sawOldTimed = false;          // 配置文件里有旧版“定时清理”的键
            bool oldLimitOn = false, oldWxLimitOn = false;
            bool hasOneShot = false, hasWxOneShot = false;
            bool sawLegacySchedule = false, sawLegacyWxSchedule = false;
            bool legacyOneShot = false, legacyWxOneShot = false;
            try
            {
                if (File.Exists(ConfigPath))
                {
                    foreach (string raw in File.ReadAllLines(ConfigPath, Encoding.UTF8))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                        int eq = line.IndexOf('=');
                        if (eq < 0) continue;
                        string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                        string val = line.Substring(eq + 1).Trim();

                        switch (key)
                        {
                            case "enabled": c.Enabled = ParseBool(val, true); break;
                            // —— 旧版定时清理的键：只为迁移判断保留，读进来就忽略 ——
                            case "one_shot": c.OneShot = ParseBool(val, false); hasOneShot = true; break;
                            case "wx_one_shot": c.WxOneShot = ParseBool(val, false); hasWxOneShot = true; break;
                            // 旧键：1.3.6/1.3.7 写的是 schedule_on，1 表示“常驻”，与新语义相反
                            case "schedule_on": legacyOneShot = !ParseBool(val, true); sawLegacySchedule = true; break;
                            case "wx_schedule_on": legacyWxOneShot = !ParseBool(val, true); sawLegacyWxSchedule = true; break;
                            case "auto_clean": sawOldTimed = true; break;
                            case "interval_days": sawOldTimed = true; break;
                            case "clean_hour": sawOldTimed = true; break;
                            case "clean_minute": sawOldTimed = true; break;
                            case "keep_days": sawOldTimed = true; break;
                            case "wx_auto_clean": sawOldTimed = true; break;
                            case "wx_interval_days": sawOldTimed = true; break;
                            case "wx_clean_hour": sawOldTimed = true; break;
                            case "wx_clean_minute": sawOldTimed = true; break;
                            case "wx_keep_days": sawOldTimed = true; break;
                            case "limit_enabled": oldLimitOn = ParseBool(val, false); break;
                            case "wx_limit_enabled": oldWxLimitOn = ParseBool(val, false); break;
                            case "pic": c.Pic = ParseBool(val, true); break;
                            case "video": c.Video = ParseBool(val, true); break;
                            case "emoji": c.Emoji = ParseBool(val, true); break;
                            case "ptt": c.Ptt = ParseBool(val, true); break;
                            case "include_system_emoji": c.IncludeSystemEmoji = ParseBool(val, false); break;
                            case "include_personal_emoji": c.IncludePersonalEmoji = ParseBool(val, false); break;
                            case "autostart": c.AutostartEnabled = ParseBool(val, false); break;
                            case "legacy_cleaned": c.LegacyCleaned = ParseBool(val, false); break;
                            case "update_check": c.UpdateCheck = ParseBool(val, true); break;
                            case "dark_mode": c.DarkMode = ParseBool(val, false); break;
                            case "update_url": c.UpdateUrl = val; break;
                            case "last_update_check": c.LastUpdateCheck = ParseDateTime(val); break;
                            case "qq_root": c.QqRoot = val; break;
                            case "wx_enabled": c.WxEnabled = ParseBool(val, true); break;
                            case "wx_pic": c.WxPic = ParseBool(val, true); break;
                            case "wx_video": c.WxVideo = ParseBool(val, true); break;
                            case "wx_emoji": c.WxEmoji = ParseBool(val, true); break;
                            case "limit_mb": c.LimitMB = ParseInt(val, 2048); break;
                            case "limit_delete_all": c.LimitDeleteAll = ParseBool(val, false); break;
                            case "wx_limit_mb": c.WxLimitMB = ParseInt(val, 2048); break;
                            case "wx_limit_delete_all": c.WxLimitDeleteAll = ParseBool(val, false); break;
                            case "wx_voice": c.WxVoice = ParseBool(val, true); break;
                            case "wx_root": c.WxRoot = val; break;
                        }
                    }
                }
            }
            catch { }

            try
            {
                c.LastRun = ReadLastRun();
                c.WxLastRun = ReadWxLastRun();
            }
            catch { }

            if (c.LimitMB < 100) c.LimitMB = 100;
            if (c.WxLimitMB < 100) c.WxLimitMB = 100;

            // 旧键只在没有新键时生效
            if (!hasOneShot && sawLegacySchedule) c.OneShot = legacyOneShot;
            if (!hasWxOneShot && sawLegacyWxSchedule) c.WxOneShot = legacyWxOneShot;

            // 旧版定时清理已移除：原来用定时的应用升级后默认“不自动清理”，
            // 免得它按默认的 2 GB 上限开始删缓存；原来就开了容量上限的保持不变。
            if (sawOldTimed)
            {
                c.Enabled = c.Enabled && oldLimitOn;
                c.WxEnabled = c.WxEnabled && oldWxLimitOn;
                Logger.Log("检测到旧版定时清理配置：定时清理已移除，只保留原来开启容量上限的应用");
            }
            return c;
        }

        public void Save()
        {
            try
            {
                Logger.EnsureDir();
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# QQ 缓存管家配置");
                sb.AppendLine("enabled=" + (Enabled ? "1" : "0"));
                sb.AppendLine("one_shot=" + (OneShot ? "1" : "0"));
                sb.AppendLine("pic=" + (Pic ? "1" : "0"));
                sb.AppendLine("video=" + (Video ? "1" : "0"));
                sb.AppendLine("emoji=" + (Emoji ? "1" : "0"));
                sb.AppendLine("ptt=" + (Ptt ? "1" : "0"));
                sb.AppendLine("include_system_emoji=" + (IncludeSystemEmoji ? "1" : "0"));
                sb.AppendLine("include_personal_emoji=" + (IncludePersonalEmoji ? "1" : "0"));
                sb.AppendLine("autostart=" + (AutostartEnabled ? "1" : "0"));
                sb.AppendLine("legacy_cleaned=" + (LegacyCleaned ? "1" : "0"));
                sb.AppendLine("update_check=" + (UpdateCheck ? "1" : "0"));
                sb.AppendLine("dark_mode=" + (DarkMode ? "1" : "0"));
                sb.AppendLine("update_url=" + (UpdateUrl ?? ""));
                sb.AppendLine("last_update_check=" + (LastUpdateCheck == DateTime.MinValue
                    ? ""
                    : LastUpdateCheck.ToString("yyyy-MM-dd HH:mm:ss")));
                sb.AppendLine("qq_root=" + (QqRoot ?? ""));
                sb.AppendLine("wx_enabled=" + (WxEnabled ? "1" : "0"));
                sb.AppendLine("wx_one_shot=" + (WxOneShot ? "1" : "0"));
                sb.AppendLine("wx_pic=" + (WxPic ? "1" : "0"));
                sb.AppendLine("wx_video=" + (WxVideo ? "1" : "0"));
                sb.AppendLine("wx_emoji=" + (WxEmoji ? "1" : "0"));
                sb.AppendLine("limit_mb=" + LimitMB);
                sb.AppendLine("limit_delete_all=" + (LimitDeleteAll ? "1" : "0"));
                sb.AppendLine("wx_limit_mb=" + WxLimitMB);
                sb.AppendLine("wx_limit_delete_all=" + (WxLimitDeleteAll ? "1" : "0"));
                sb.AppendLine("wx_voice=" + (WxVoice ? "1" : "0"));
                sb.AppendLine("wx_root=" + (WxRoot ?? ""));
                File.WriteAllText(ConfigPath, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Logger.Log("保存配置失败：" + ex.Message);
            }
        }

        public void SaveLastRun(DateTime t)
        {
            LastRun = t;
            string text = t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            try { File.WriteAllText(LastRunPath, text); }
            catch { }
            try
            {
                File.WriteAllText(LastCleanJsonPath,
                    "{\n  \"last_clean\": \"" + text + "\",\n  \"source\": \"QQCacheTray\"\n}\n",
                    new UTF8Encoding(false));
            }
            catch { }
        }

        public void SaveWxLastRun(DateTime t)
        {
            WxLastRun = t;
            string text = t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            try
            {
                File.WriteAllText(WxLastCleanJsonPath,
                    "{\n  \"last_clean\": \"" + text + "\",\n  \"source\": \"QQCacheTray\"\n}\n",
                    new UTF8Encoding(false));
            }
            catch { }
        }

        private static DateTime ReadWxLastRun()
        {
            try
            {
                if (File.Exists(WxLastCleanJsonPath))
                {
                    string s2 = File.ReadAllText(WxLastCleanJsonPath);
                    Match m = Regex.Match(s2, "\"last_clean\"\\s*:\\s*\"([^\"]+)\"");
                    DateTime dt;
                    if (m.Success && DateTime.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out dt)) return dt;
                }
            }
            catch { }
            return DateTime.MinValue;
        }

        // 优先读 last-clean.json（和插件的约定同名），没有再退回 last-run.txt
        private static DateTime ReadLastRun()
        {
            try
            {
                if (File.Exists(LastCleanJsonPath))
                {
                    string s = File.ReadAllText(LastCleanJsonPath);
                    Match m = Regex.Match(s, "\"last_clean\"\\s*:\\s*\"([^\"]+)\"");
                    DateTime dt;
                    if (m.Success && DateTime.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out dt)) return dt;
                }
            }
            catch { }

            try
            {
                if (File.Exists(LastRunPath))
                {
                    DateTime dt;
                    if (DateTime.TryParse(File.ReadAllText(LastRunPath).Trim(), CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out dt)) return dt;
                }
            }
            catch { }
            return DateTime.MinValue;
        }

        private static bool ParseBool(string v, bool def)
        {
            if (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "yes") return true;
            if (v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase) || v == "no") return false;
            return def;
        }

        private static DateTime ParseDateTime(string v)
        {
            DateTime dt;
            if (DateTime.TryParse(v, out dt)) return dt;
            return DateTime.MinValue;
        }

        private static int ParseInt(string v, int def)
        {
            int n;
            if (int.TryParse(v, out n)) return n;
            return def;
        }
    }

    // ---------------------------------------------------------------- 日志

    internal static class Logger
    {
        private static readonly object Gate = new object();

        public static void EnsureDir()
        {
            try
            {
                if (!Directory.Exists(Config.Dir)) Directory.CreateDirectory(Config.Dir);
                if (!Directory.Exists(Config.LogDir)) Directory.CreateDirectory(Config.LogDir);
            }
            catch { }
        }

        public static void Log(string message)
        {
            lock (Gate)
            {
                try
                {
                    EnsureDir();
                    string path = Path.Combine(Config.LogDir, "tray-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                    File.AppendAllText(path,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine,
                        Encoding.UTF8);
                }
                catch { }
            }
        }

        public static void Trim()
        {
            try
            {
                string[] files = Directory.GetFiles(Config.LogDir, "tray-*.log");
                Array.Sort(files, StringComparer.Ordinal);
                if (files.Length > 20)
                {
                    for (int i = 0; i < files.Length - 20; i++)
                    {
                        try { File.Delete(files[i]); } catch { }
                    }
                }
            }
            catch { }
        }
    }

    // ---------------------------------------------------------------- 清理引擎

    internal sealed class CleanResult
    {
        public int TotalDeleted;
        public int TotalFailed;
        public long TotalFreed;
        public string Error = "";
    }

    internal static class Cleaner
    {
        // 两种触发方式共用的闸门：距上次清理满 N 天才行动，避免重复清理
        // 保存后程序会不会常驻：任一应用启用且没勾「开启」（勾了开启 = 只开机检查一次）
        public static bool WillStayResident(Config c)
        {
            return (c.Enabled && !c.OneShot) || (c.WxEnabled && !c.WxOneShot);
        }
        // ---------------- 容量上限模式 ----------------

        private sealed class CacheFile
        {
            public string Path;
            public long Length;
            public DateTime Time;
        }

        private static readonly Action<string> Silently = delegate { };

        private static List<CacheFile> CollectCacheFiles(Config cfg, bool wx, Action<string> log)
        {
            List<CacheFile> list = new List<CacheFile>();
            List<FileInfo> scanned = new List<FileInfo>();
            Action<string> sink = log ?? Silently;

            if (!wx)
            {
                foreach (string ntData in GetNtDataDirs(cfg, sink))
                {
                    foreach (string cat in cfg.Categories())
                    {
                        string catPath = Path.Combine(ntData, cat);
                        if (!Directory.Exists(catPath)) continue;

                        scanned.Clear();
                        Walk(catPath, scanned, sink, 0);

                        foreach (FileInfo fi in scanned)
                        {
                            string f = fi.FullName;
                            string rel = f.Substring(catPath.Length).TrimStart('\\');
                            if (IsExcluded(rel, cfg, cat)) continue;
                            CacheFile c = new CacheFile();
                            c.Path = f; c.Length = fi.Length; c.Time = fi.LastWriteTime;
                            list.Add(c);
                        }
                    }
                }
            }
            else
            {
                foreach (KeyValuePair<string, string> kv in GetWxCategoryDirs(cfg, sink))
                {
                    if (!Directory.Exists(kv.Value)) continue;

                    scanned.Clear();
                    Walk(kv.Value, scanned, sink, 0);

                    foreach (FileInfo fi in scanned)
                    {
                        CacheFile c = new CacheFile();
                        c.Path = fi.FullName; c.Length = fi.Length; c.Time = fi.LastWriteTime;
                        list.Add(c);
                    }
                }
            }

            return list;
        }

        // 显示用的“缓存所在盘”：取第一个解析到的目录
        public static string PrimaryRoot(Config cfg, bool wx)
        {
            try
            {
                if (!wx)
                {
                    List<string> dirs = GetNtDataDirs(cfg, Silently);
                    if (dirs.Count > 0) return dirs[0];
                }
                else
                {
                    List<KeyValuePair<string, string>> dirs = GetWxCategoryDirs(cfg, Silently);
                    if (dirs.Count > 0) return dirs[0].Value;
                }
            }
            catch { }
            return null;
        }

        // 只统计大小：边遍历边累加，不构造 CacheFile 列表
        private static long SumDir(string dir, Config cfg, string cat, string catPath, Action<string> log, int depth)
        {
            if (depth > 10) return 0;
            long sum = 0;
            try
            {
                foreach (FileInfo fi in new DirectoryInfo(dir).GetFiles())
                {
                    string rel = fi.FullName.Substring(catPath.Length).TrimStart('\\');
                    if (IsExcluded(rel, cfg, cat)) continue;
                    sum += fi.Length;
                }
            }
            catch { return sum; }

            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch { return sum; }

            foreach (string d in subs)
            {
                try
                {
                    if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch { continue; }
                sum += SumDir(d, cfg, cat, catPath, log, depth + 1);
            }
            return sum;
        }

        // 缓存占用（字节）
        public static long MeasureCache(Config cfg, bool wx, Action<string> log)
        {
            Action<string> sink = log ?? Silently;
            long total = 0;

            if (!wx)
            {
                foreach (string ntData in GetNtDataDirs(cfg, sink))
                {
                    foreach (string cat in cfg.Categories())
                    {
                        string catPath = Path.Combine(ntData, cat);
                        if (!Directory.Exists(catPath)) continue;
                        total += SumDir(catPath, cfg, cat, catPath, sink, 0);
                    }
                }
            }
            else
            {
                foreach (KeyValuePair<string, string> kv in GetWxCategoryDirs(cfg, sink))
                {
                    if (!Directory.Exists(kv.Value)) continue;
                    total += SumDir(kv.Value, cfg, "Wx", kv.Value, sink, 0);
                }
            }

            return total;
        }

        // 自检用：上一次 EnforceLimit 有没有走“把每个文件都建成对象”的重路径
        public static bool LastLimitCollectedFiles;
        // 供托盘决定下次多久再查：上一次实测占用 / 上限
        public static long LastLimitTotal;
        public static long LastLimitLimit;
        private static DateTime _lastLimitIdleLog = DateTime.MinValue;

        // 超过上限就清理：删旧文件（保留到 90% 以下）或全删
        public static CleanResult EnforceLimit(Config cfg, bool wx, Action<string> log)
        {
            CleanResult result = new CleanResult();
            Action<string> sink = log ?? Silently;
            LastLimitCollectedFiles = false;

            int limitMB = wx ? cfg.WxLimitMB : cfg.LimitMB;
            bool deleteAll = wx ? cfg.WxLimitDeleteAll : cfg.LimitDeleteAll;
            long limit = Math.Max(100, limitMB) * 1024L * 1024L;

            // 先流式求和：没超上限就走人，不要为每个文件建 CacheFile 对象再排序
            long total = MeasureCache(cfg, wx, sink);

            string tag = wx ? "微信" : "QQ";
            LastLimitTotal = total;
            LastLimitLimit = limit;
            if (total <= limit)
            {
                // 每 10 分钟一条“没超”的日志太吵，压缩到每小时一条
                if ((DateTime.Now - _lastLimitIdleLog).TotalHours >= 1)
                {
                    _lastLimitIdleLog = DateTime.Now;
                    sink(string.Format("{0} 缓存 {1}，未超过上限 {2}", tag, FormatSize(total), FormatSize(limit)));
                }
                return result;
            }

            LastLimitCollectedFiles = true;
            List<CacheFile> files = CollectCacheFiles(cfg, wx, sink);
            total = 0;
            foreach (CacheFile f in files) total += f.Length;

            sink(string.Format("{0} 缓存 {1} 超过上限 {2}，开始{3}", tag,
                FormatSize(total), FormatSize(limit), deleteAll ? "全部删除" : "删除旧文件"));

            files.Sort(delegate (CacheFile a, CacheFile b) { return a.Time.CompareTo(b.Time); });

            long target = deleteAll ? 0 : (long)(limit * 0.9);
            foreach (CacheFile f in files)
            {
                if (total <= target) break;
                try
                {
                    File.Delete(f.Path);
                    result.TotalDeleted++;
                    result.TotalFreed += f.Length;
                    total -= f.Length;
                }
                catch (Exception ex)
                {
                    result.TotalFailed++;
                    if (result.TotalFailed <= 3) sink("删除失败：" + f.Path + " -> " + ex.Message);
                }
            }

            sink(string.Format("上限清理完成：删除 {0} 个文件，释放 {1}，剩余 {2}",
                result.TotalDeleted, FormatSize(result.TotalFreed), FormatSize(total)));
            return result;
        }

        public static string FormatSize(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024 / 1024).ToString("N2") + " GB";
            if (bytes >= 1024L * 1024) return (bytes / 1024.0 / 1024).ToString("N1") + " MB";
            if (bytes >= 1024) return (bytes / 1024.0).ToString("N0") + " KB";
            return bytes + " B";
        }

        public static List<string> GetRoots(Action<string> log)
        {
            List<string> roots = new List<string>();
            List<string> candidates = new List<string>();

            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Tencent Files"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "Tencent Files"));

            // SYSTEM / 其它账户运行时，MyDocuments 指向自己的配置目录，必须把所有用户都扫一遍
            try
            {
                foreach (string profile in Directory.GetDirectories(@"C:\Users"))
                {
                    candidates.Add(Path.Combine(profile, "Documents", "Tencent Files"));
                    candidates.Add(Path.Combine(profile, "OneDrive", "Documents", "Tencent Files"));
                }
            }
            catch (Exception ex) { log("枚举用户目录失败：" + ex.Message); }

            candidates.Add(@"C:\Users\Public\Documents\Tencent Files");
            candidates.Add(@"D:\Tencent Files");
            candidates.Add(@"C:\Tencent Files");

            foreach (string c in candidates)
            {
                if (string.IsNullOrEmpty(c)) continue;
                if (!Directory.Exists(c)) continue;
                bool dup = false;
                foreach (string r in roots)
                {
                    if (string.Equals(r, c, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                }
                if (!dup) roots.Add(c);
            }
            return roots;
        }

        // 手动指定的路径（可能是 Tencent Files、某个账号目录，或直接指向 nt_data）
        private static List<string> ResolveManualRoot(Config cfg, Action<string> log)
        {
            List<string> found = new List<string>();
            if (cfg == null || string.IsNullOrEmpty(cfg.QqRoot)) return found;

            string p = cfg.QqRoot.Trim().TrimEnd('\\');
            if (!Directory.Exists(p))
            {
                log("手动指定的 QQ 目录不存在：" + p);
                return found;
            }

            // 1) 直接指向 nt_data
            if (string.Equals(Path.GetFileName(p), "nt_data", StringComparison.OrdinalIgnoreCase))
            {
                found.Add(p);
                return found;
            }

            // 2) 指向账号目录（下面有 nt_qq\nt_data）
            string asAccount = Path.Combine(p, "nt_qq", "nt_data");
            if (Directory.Exists(asAccount))
            {
                found.Add(asAccount);
                return found;
            }

            // 3) 当成 Tencent Files 根目录，逐个账号找
            try
            {
                foreach (string acc in Directory.GetDirectories(p))
                {
                    if (string.Equals(Path.GetFileName(acc), "nt_qq", StringComparison.OrdinalIgnoreCase)) continue;
                    string ntData = Path.Combine(acc, "nt_qq", "nt_data");
                    if (Directory.Exists(ntData)) found.Add(ntData);
                }
            }
            catch (Exception ex) { log("读取手动目录失败：" + ex.Message); }

            if (found.Count == 0) log("手动指定的目录里没有找到 nt_qq\\nt_data：" + p);
            return found;
        }

        public static List<string> GetNtDataDirs(Config cfg, Action<string> log)
        {
            List<string> manual = ResolveManualRoot(cfg, log);
            if (manual.Count > 0)
            {
                log("使用手动指定的 QQ 目录：" + cfg.QqRoot);
                return manual;
            }

            List<string> result = new List<string>();
            foreach (string root in GetRoots(log))
            {
                string[] accounts;
                try { accounts = Directory.GetDirectories(root); }
                catch (Exception ex) { log("无法读取 " + root + " -> " + ex.Message); continue; }

                foreach (string acc in accounts)
                {
                    if (string.Equals(Path.GetFileName(acc), "nt_qq", StringComparison.OrdinalIgnoreCase)) continue;
                    string ntData = Path.Combine(acc, "nt_qq", "nt_data");
                    if (Directory.Exists(ntData)) result.Add(ntData);
                }
            }
            return result;
        }

        // ---------------------------------------------------------------- 微信


        private static string FindSubDir(string dir, string aliases)
        {
            string[] names = aliases.Split(',');
            try
            {
                foreach (string sub in Directory.GetDirectories(dir))
                {
                    string n = Path.GetFileName(sub);
                    foreach (string a in names)
                    {
                        if (string.Equals(n, a, StringComparison.OrdinalIgnoreCase)) return sub;
                    }
                }
            }
            catch { }
            return null;
        }

        // 返回 (账号|类别) -> 目录
        public static List<KeyValuePair<string, string>> GetWxCategoryDirs(Config cfg, Action<string> log)
        {
            List<KeyValuePair<string, string>> result = new List<KeyValuePair<string, string>>();
            List<string> roots = new List<string>();

            if (cfg != null && !string.IsNullOrEmpty(cfg.WxRoot))
            {
                if (Directory.Exists(cfg.WxRoot)) roots.Add(cfg.WxRoot);
                else log("手动指定的微信目录不存在：" + cfg.WxRoot);
            }

            if (roots.Count == 0)
            {
                List<string> cand = new List<string>();
                string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                cand.Add(Path.Combine(docs, "WeChat Files"));
                cand.Add(Path.Combine(docs, "xwechat_files"));

                try
                {
                    foreach (string profile in Directory.GetDirectories(@"C:\Users"))
                    {
                        cand.Add(Path.Combine(profile, "Documents", "WeChat Files"));
                        cand.Add(Path.Combine(profile, "Documents", "xwechat_files"));
                        cand.Add(Path.Combine(profile, "OneDrive", "Documents", "WeChat Files"));
                    }
                }
                catch (Exception ex) { log("枚举用户目录失败：" + ex.Message); }

                cand.Add(@"D:\WeChat Files");
                cand.Add(@"C:\WeChat Files");

                foreach (string c in cand)
                {
                    if (string.IsNullOrEmpty(c) || !Directory.Exists(c)) continue;
                    if (!roots.Contains(c)) roots.Add(c);
                }
            }

            foreach (string root in roots)
            {
                string[] accounts;
                try { accounts = Directory.GetDirectories(root); }
                catch (Exception ex) { log("无法读取 " + root + " -> " + ex.Message); continue; }

                foreach (string acc in accounts)
                {
                    string name = Path.GetFileName(acc);

                    // 微信 3.x：FileStorage\Image|Video|Emoji|Audio
                    string fs = Path.Combine(acc, "FileStorage");
                    if (Directory.Exists(fs))
                    {
                        AddWx(result, name, "Pic", Path.Combine(fs, "Image"));
                        AddWx(result, name, "Video", Path.Combine(fs, "Video"));
                        AddWx(result, name, "Emoji", Path.Combine(fs, "Emoji"));
                        AddWx(result, name, "Emoji", Path.Combine(fs, "CustomEmotion"));
                        AddWx(result, name, "Voice", Path.Combine(fs, "Audio"));
                        continue;
                    }

                    // 微信 4.x：msgttach（图片等）、msgideo、cache（表情/缩略图）
                    string msg = Path.Combine(acc, "msg");
                    if (Directory.Exists(msg))
                    {
                        AddWx(result, name, "Pic", Path.Combine(msg, "attach"));
                        AddWx(result, name, "Video", Path.Combine(msg, "video"));
                        AddWx(result, name, "Emoji", Path.Combine(acc, "cache"));
                        AddWx(result, name, "Voice", FindSubDir(msg, "voice,audio"));
                        continue;
                    }
                }
            }
            return result;
        }

        private static void AddWx(List<KeyValuePair<string, string>> list, string account, string cat, string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            list.Add(new KeyValuePair<string, string>(account + "|" + cat, dir));
        }

        // 给界面显示“自动检测到了什么”

        private static void Walk(string dir, List<FileInfo> acc, Action<string> log, int depth)
        {
            if (depth > 10) return;
            try
            {
                // 直接用 DirectoryInfo 取 FileInfo：长度和时间已经带回来了，调用方不用再 stat 一次
                foreach (FileInfo f in new DirectoryInfo(dir).GetFiles()) acc.Add(f);
            }
            catch (Exception ex)
            {
                log("跳过目录 " + dir + " -> " + ex.Message);
                return;
            }

            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch { return; }

            foreach (string d in subs)
            {
                try
                {
                    if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch { continue; }
                Walk(d, acc, log, depth + 1);
            }
        }

        private static bool IsExcluded(string rel, Config cfg, string cat)
        {
            if (!string.Equals(cat, "Emoji", StringComparison.OrdinalIgnoreCase)) return false;

            if (!cfg.IncludeSystemEmoji && IsUnder(rel, "BaseEmojiSyastems")) return true;
            if (!cfg.IncludePersonalEmoji && IsUnder(rel, "personal_emoji")) return true;
            return false;
        }

        private static bool IsUnder(string rel, string folder)
        {
            return rel.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
                   rel.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase);
        }


        private static void CollectDirs(string dir, List<string> acc, int depth)
        {
            if (depth > 10) return;
            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch { return; }
            foreach (string d in subs)
            {
                acc.Add(d);
                CollectDirs(d, acc, depth + 1);
            }
        }
    }

    // ---------------------------------------------------------------- 托盘

    internal sealed class TrayContext : ApplicationContext
    {
        private readonly NotifyIcon _icon;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _miStatus;
        private readonly ToolStripMenuItem _miClean;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly Form _marshal;
        private Config _cfg;
        private bool _busy;
        private bool _settingsOpen;
        private int _limitTick;
        private int _limitEveryMinutes = 10;   // 由上次占用比例自适应调整
        private UpdateChecker.Info _pendingUpdate;

        public TrayContext()
        {
            // 构造发生在 Application.Run 之前，SynchronizationContext 还没装好，
            // 用一个不可见窗口做跨线程封送。
            _marshal = new Form();
            _marshal.ShowInTaskbar = false;
            _marshal.FormBorderStyle = FormBorderStyle.None;
            _marshal.StartPosition = FormStartPosition.Manual;
            _marshal.Location = new Point(-32000, -32000);
            _marshal.Size = new Size(1, 1);
            IntPtr unused = _marshal.Handle;

            _cfg = Config.Load();

            _menu = new ContextMenuStrip();
            _menu.Font = new Font("Microsoft YaHei UI", 9F);

            _miStatus = new ToolStripMenuItem("QQ 缓存：统计中…");
            _miStatus.Click += delegate { OpenSettings(); };

            _miClean = new ToolStripMenuItem("立即清理 QQ");
            _miClean.Click += delegate { ConfirmAndClean(false); };

            ToolStripMenuItem miCleanWx = new ToolStripMenuItem("立即清理微信");
            miCleanWx.Click += delegate { ConfirmAndClean(true); };

            ToolStripMenuItem miSettings = new ToolStripMenuItem("设置…");
            miSettings.Click += delegate { OpenSettings(); };

            ToolStripMenuItem miUpdate = new ToolStripMenuItem("检查更新…");
            miUpdate.Click += delegate { CheckForUpdates(true); };

            ToolStripMenuItem miLogs = new ToolStripMenuItem("打开日志目录");
            miLogs.Click += delegate
            {
                try { Process.Start("explorer.exe", "\"" + Config.LogDir + "\""); }
                catch { }
            };

            ToolStripMenuItem miExit = new ToolStripMenuItem("退出");
            miExit.Click += delegate { ExitTray(); };

            _menu.Items.Add(_miStatus);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_miClean);
            _menu.Items.Add(miCleanWx);
            _menu.Items.Add(miSettings);
            _menu.Items.Add(miUpdate);
            _menu.Items.Add(miLogs);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(miExit);

            // 托盘菜单里的“QQ 缓存：x”只在菜单要弹出时才算一次，不在每次开机时全量扫缓存
            _menu.Opening += delegate { EnsureStatus(false); };

            // 开机自启时可能比资源管理器还早，托盘还没建好就注册会失败，
            // 所以先等 Shell_TrayWnd 出现（正常启动时是一次即返回）
            WaitForShell(20);

            _icon = new NotifyIcon();
            _icon.Icon = IconFactory.Create(SystemInformation.SmallIconSize.Width);
            _icon.Text = "QQ 缓存管家";
            _icon.ContextMenuStrip = _menu;
            _icon.Visible = true;
            _icon.DoubleClick += delegate { OpenSettings(); };

            Logger.Trim();
            Logger.Log("托盘启动");
            Logger.Log("数据目录：" + Config.Dir);

            // 放后台做，避免删旧任务时拖住启动
            ThreadPool.QueueUserWorkItem(delegate
            {
                // 旧版留下的计划任务/启动项只需要清一次，之后每次开机都不用再拉 schtasks
                if (!_cfg.LegacyCleaned)
                {
                    AutoStart.Normalize();
                    _cfg.LegacyCleaned = true;
                    try { _cfg.Save(); } catch { }
                }

                // 配置里开着自启就重写一遍启动脚本（exe 路径可能变了），顺便修复被系统禁用的启动项
                if (_cfg.AutostartEnabled)
                {
                    string note;
                    AutoStart.EnsureHealthy(out note);
                    if (!string.IsNullOrEmpty(note))
                    {
                        Logger.Log(note);
                        PostToUi(delegate { Toast.ShowToast(note); });
                    }
                }
            });

            // 后台常驻程序不跟前台抢 CPU（空闲时照样满速，只在竞争时让路）
            try
            {
                Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
                Logger.Log("进程优先级：BelowNormal");
            }
            catch (Exception ex) { Logger.Log("设置进程优先级失败：" + ex.Message); }

            // 首次运行把设置窗口弹出来，免得用户找不到托盘图标以为没启动
            bool firstRun = _cfg.LastRun == DateTime.MinValue;

            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            _timer = new System.Windows.Forms.Timer();
            _timer.Tick += OnTimerTick;


            RefreshStatus();
            ScheduleCheck(15);

            if (_cfg.UpdateCheck && (DateTime.Now - _cfg.LastUpdateCheck).TotalHours >= 12)
            {
                CheckForUpdates(false);
            }

            // 测试用：--settings 直接打开设置窗口，--update 立即检查更新
            if (Program.SettingsOnStart)
            {
                PostToUi(delegate { OpenSettings(); });
            }

            if (Program.UpdateOnStart)
            {
                PostToUi(delegate { CheckForUpdates(true); });
            }

            _icon.ShowBalloonTip(4000, "QQ 缓存管家", "已在后台运行，右键图标可设置自动清理。",
                ToolTipIcon.Info);

            if (!string.IsNullOrEmpty(Program.PendingToast))
            {
                string bootToast = Program.PendingToast;
                Program.PendingToast = "";
                PostToUi(delegate { Toast.ShowToast(bootToast); });
            }

            // 首次运行把设置窗口弹出来，免得用户找不到托盘图标以为没启动
            bool restricted = Config.IsRestricted;
            if (restricted)
            {
                Logger.Log("检测到受限运行：数据目录 = " + Config.Dir + "（%APPDATA% 不可写，托盘图标可能无法注册）");
            }

            if (firstRun || restricted)
            {
                System.Windows.Forms.Timer once = new System.Windows.Forms.Timer();
                once.Interval = 800;
                once.Tick += delegate
                {
                    once.Stop();
                    once.Dispose();

                    if (restricted)
                    {
                        // 只有“用户手动双击”才弹框；开机自启拉起来的受限实例直接记日志退出，
                        // 免得每次开机都要点一次确定
                        string parent = Guard.DescribeParent(Process.GetCurrentProcess().Id);
                        bool userLaunched = parent.StartsWith("explorer", StringComparison.OrdinalIgnoreCase);

                        Logger.Log("受限实例退出（父进程 " + parent + "）");

                        if (userLaunched)
                        {
                            MessageBox.Show(
                                "程序正运行在受限环境中（无法写入 %APPDATA%），托盘图标显示不出来，清理也会失败。\n\n" +
                                "请改用下面任一种方式启动：\n" +
                                "  1. 双击程序目录里的 RunAsAdmin.cmd\n" +
                                "  2. 右键 QQCacheTray.exe → 以管理员身份运行\n\n" +
                                "当前路径：" + Application.ExecutablePath,
                                "QQ 缓存管家", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }

                        ExitTray();
                        return;
                    }

                    if (firstRun) OpenSettings();
                };
                once.Start();
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        // 等资源管理器的任务栏出现：开机自启时进程可能比 shell 起得还早，
        // 这时注册托盘图标会失败，之后也不会自动补上
        private static void WaitForShell(int maxSeconds)
        {
            int loops = Math.Max(1, maxSeconds * 2);
            for (int i = 0; i < loops; i++)
            {
                try
                {
                    if (FindWindow("Shell_TrayWnd", null) != IntPtr.Zero) return;
                }
                catch { return; }
                Thread.Sleep(500);
            }
            Logger.Log("等待任务栏超时（" + maxSeconds + " 秒），仍继续启动");
        }

        private void PostToUi(Action action)
        {
            try
            {
                if (_marshal != null && _marshal.IsHandleCreated) _marshal.BeginInvoke(action);
                else if (action != null) action();
            }
            catch { }
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume) ScheduleCheck(20);
        }

        private void ScheduleCheck(int seconds)
        {
            _timer2Stop();
            _timer2Start(seconds);
        }

        // 下一次唤醒间隔：任一应用启用容量上限清理 → 60 秒（保持 10 分钟检查粒度），否则 5 分钟
        internal static int NextTickSeconds(Config cfg, DateTime now)
        {
            if (cfg == null) return 300;
            bool scheduled = (cfg.Enabled && !cfg.OneShot) || (cfg.WxEnabled && !cfg.WxOneShot);
            return scheduled ? 60 : 300;
        }

        private static double LimitRatio()
        {
            if (Cleaner.LastLimitLimit <= 0) return 0;
            return Cleaner.LastLimitTotal / (double)Cleaner.LastLimitLimit;
        }

        // 离上限越远，查得越少：不到一半上限半小时一次，不到八成十分钟，逼近上限五分钟
        internal static int NextLimitMinutes(double ratio)
        {
            if (ratio >= 0.8) return 5;
            if (ratio >= 0.5) return 10;
            return 30;
        }

        private void _timer2Stop()
        {
            if (_timer != null) _timer.Stop();
        }

        private void _timer2Start(int seconds)
        {
            if (_timer == null) return;
            _timer.Interval = Math.Max(5, seconds) * 1000;
            _timer.Start();
        }

        private void OnTimerTick(object sender, EventArgs e)
        {
            _timer.Stop();

            // 容量上限：按“离上限还有多远”自适应间隔（离得远就少扫几趟），默认 10 分钟
            _limitTick++;
            if (_limitTick >= _limitEveryMinutes)
            {
                _limitTick = 0;
                double worst = 0;
                try
                {
                    string limitMsg = "";
                    if (_cfg.Enabled && !_cfg.OneShot)
                    {
                        CleanResult lq = Cleaner.EnforceLimit(_cfg, false, Logger.Log);
                        worst = Math.Max(worst, LimitRatio());
                        if (lq != null && lq.TotalDeleted > 0)
                            limitMsg += string.Format("QQ：已清理 {0} 个文件，释放 {1}\n", lq.TotalDeleted, Cleaner.FormatSize(lq.TotalFreed));
                    }
                    if (_cfg.WxEnabled && !_cfg.WxOneShot)
                    {
                        CleanResult lw = Cleaner.EnforceLimit(_cfg, true, Logger.Log);
                        worst = Math.Max(worst, LimitRatio());
                        if (lw != null && lw.TotalDeleted > 0)
                            limitMsg += string.Format("微信：已清理 {0} 个文件，释放 {1}\n", lw.TotalDeleted, Cleaner.FormatSize(lw.TotalFreed));
                    }

                    if (limitMsg.Length > 0)
                    {
                        string shown = limitMsg.TrimEnd('\n');
                        PostToUi(delegate { Toast.ShowToast(shown); });
                    }
                }
                catch (Exception ex) { Logger.Log("容量上限检查失败：" + ex.Message); }

                if (worst > 0) _limitEveryMinutes = NextLimitMinutes(worst);
            }

            // 常驻时每 12 小时查一次更新（长时间不重启也能收到通知）
            if (_cfg.UpdateCheck && (DateTime.Now - _cfg.LastUpdateCheck).TotalHours >= 12)
            {
                CheckForUpdates(false);
            }

            // 没事干的时候别每分钟醒一次：没有任何应用启用时 5 分钟醒一次
            _timer.Interval = Math.Max(5, NextTickSeconds(_cfg, DateTime.Now)) * 1000;
            _timer.Start();
        }

        private void ConfirmAndClean(bool wx)
        {
            if (_busy) return;
            int limitMB = wx ? _cfg.WxLimitMB : _cfg.LimitMB;
            bool deleteAll = wx ? _cfg.WxLimitDeleteAll : _cfg.LimitDeleteAll;
            DialogResult r = MessageBox.Show(
                "将按容量上限检查" + (wx ? "微信" : "QQ") + "缓存：\n" +
                (deleteAll
                    ? "超过 " + limitMB + " MB 时全部删除。"
                    : "超过 " + limitMB + " MB 时删除旧文件，直到降到上限的 90% 以下。") +
                "\n\n只删本地缓存，不动聊天记录数据库；旧图片点开时会重新下载。\n\n确定继续？",
                "QQ 缓存管家", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (r == DialogResult.OK) RunLimitClean(wx);
        }

        private void RunLimitClean(bool wx)
        {
            if (_busy) return;
            _busy = true;
            _miClean.Enabled = false;
            _miStatus.Text = (wx ? "微信" : "QQ") + " 缓存：正在清理…";

            Config snapshot = _cfg;
            ThreadPool.QueueUserWorkItem(delegate
            {
                CleanResult r;
                try
                {
                    r = Cleaner.EnforceLimit(snapshot, wx, Logger.Log);
                }
                catch (Exception ex)
                {
                    r = new CleanResult();
                    r.Error = ex.Message;
                    Logger.Log("清理异常：" + ex);
                }

                PostToUi(delegate
                {
                    _busy = false;
                    _miClean.Enabled = true;

                    string msg;
                    if (!string.IsNullOrEmpty(r.Error))
                    {
                        msg = "清理失败：" + r.Error;
                        _icon.ShowBalloonTip(5000, "QQ 缓存管家", msg, ToolTipIcon.Error);
                    }
                    else
                    {
                        if (r.TotalDeleted > 0)
                        {
                            if (wx) snapshot.SaveWxLastRun(DateTime.Now);
                            else snapshot.SaveLastRun(DateTime.Now);
                            snapshot.Save();
                            msg = string.Format("{0}：已清理 {1} 个文件，释放 {2}",
                                wx ? "微信" : "QQ", r.TotalDeleted, Cleaner.FormatSize(r.TotalFreed));
                        }
                        else
                        {
                            msg = (wx ? "微信" : "QQ") + "：未超过容量上限，无需清理";
                        }
                        Toast.ShowToast(msg);
                    }

                    RefreshStatus();
                });
            });
        }
        private void CheckForUpdates(bool manual)
        {
            string url = string.IsNullOrEmpty(_cfg.UpdateUrl)
                ? UpdateChecker.DefaultManifestUrl
                : _cfg.UpdateUrl;

            Logger.Log(manual ? "检查更新：手动触发" : "检查更新：启动时自动触发");

            ThreadPool.QueueUserWorkItem(delegate
            {
                UpdateChecker.Info info = null;
                string err = null;

                try
                {
                    if (string.IsNullOrEmpty(_cfg.UpdateUrl))
                    {
                        string used;
                        string lastErr;
                        info = UpdateChecker.FetchAny(out used, out lastErr);
                        if (info != null) Logger.Log("清单来源：" + used);
                        else err = lastErr;
                    }
                    else
                    {
                        info = UpdateChecker.Fetch(url);
                    }
                }
                catch (Exception ex) { err = ex.Message; }

                Logger.Log(info == null
                    ? "检查更新失败：" + (err ?? "清单不完整")
                    : "检查更新：远端 " + info.Version + "，当前 " + UpdateChecker.CurrentVersion);

                PostToUi(delegate
                {
                    _cfg.LastUpdateCheck = DateTime.Now;
                    _cfg.Save();

                    if (info == null)
                    {
                        if (manual)
                        {
                            MessageBox.Show("检查更新失败：" + (err ?? "清单内容不完整") +
                                            "\n\n地址：" + url,
                                "QQ 缓存管家", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                        return;
                    }

                    if (!UpdateChecker.IsNewer(info.Version))
                    {
                        Logger.Log("检查更新：已是最新版本");
                        if (manual)
                        {
                            MessageBox.Show("当前已是最新版本 " + UpdateChecker.CurrentVersion + "。",
                                "QQ 缓存管家", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        return;
                    }

                    Logger.Log("发现新版本：" + info.Version);
                    _pendingUpdate = info;

                    if (_settingsOpen)
                    {
                        Logger.Log("设置窗口开着，更新条已在界面里");
                        return;
                    }

                    Toast.ShowToast("「QQ 缓存管家」发现新版本 " + info.Version +
                                    "，点托盘「设置」可更新", 9000, true);

                    // 先弹通知，1.5 秒后再挂任务栏按钮并闪烁，形成「先提示、后闪烁」的先后顺序
                    string ver = info.Version;
                    System.Windows.Forms.Timer delay = new System.Windows.Forms.Timer();
                    delay.Interval = 1500;
                    delay.Tick += delegate
                    {
                        Logger.Log("任务栏提醒：定时器触发");
                        delay.Stop();
                        delay.Dispose();
                        TaskbarAlert.OnOpen = delegate { OpenSettings(); };
                        TaskbarAlert.Show("QQ 缓存管家 - 发现新版本 " + ver);
                    };
                    delay.Start();
                });
            });
        }

        private void DoSelfUpdate(UpdateChecker.Info info)
        {
            Toast.ShowToast("正在下载更新，请稍候…");

            ThreadPool.QueueUserWorkItem(delegate
            {
                string err;
                string actual;
                string file = SelfUpdater.Download(info.Url, info.Sha256, out err, out actual);

                // 清单可能刚被重新发布：重新拉一次，若下载到的文件和新清单一致就继续
                if (file == null && !string.IsNullOrEmpty(actual))
                {
                    try
                    {
                        string manifest = string.IsNullOrEmpty(_cfg.UpdateUrl)
                            ? UpdateChecker.DefaultManifestUrl
                            : _cfg.UpdateUrl;
                        UpdateChecker.Info again = UpdateChecker.Fetch(manifest);

                        if (again != null && string.Equals(again.Sha256, actual, StringComparison.OrdinalIgnoreCase))
                        {
                            Logger.Log("清单已更新，按最新哈希接受下载文件");
                            file = Path.Combine(Path.GetTempPath(), "QQCacheTray_update.exe");
                            info = again;
                            err = null;
                        }
                    }
                    catch { }
                }

                if (file == null && !string.IsNullOrEmpty(actual))
                {
                    try { File.Delete(Path.Combine(Path.GetTempPath(), "QQCacheTray_update.exe")); } catch { }
                }

                PostToUi(delegate
                {
                    if (file == null)
                    {
                        Logger.Log("更新下载失败：" + err);
                        MessageBox.Show("更新失败：" + err + "\n\n可以点「下载页」手动下载。",
                            "QQ 缓存管家", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    Logger.Log("更新包已校验：" + file);
                    string err2;
                    if (SelfUpdater.Apply(file, info.Sha256, "", out err2))
                    {
                        Logger.Log("准备重启并替换为新版本");
                        ExitTray();
                    }
                    else
                    {
                        MessageBox.Show("无法启动更新程序：" + err2,
                            "QQ 缓存管家", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                });
            });
        }

        private bool _statusDirty = true;
        private DateTime _statusAt = DateTime.MinValue;

        // 只标脏；缓存大小推迟到托盘菜单弹出时再算（见 EnsureStatus）
        private void RefreshStatus()
        {
            _statusDirty = true;
        }

        private void EnsureStatus(bool force)
        {
            if (!force && !_statusDirty && (DateTime.Now - _statusAt).TotalMinutes < 5) return;

            _statusDirty = false;
            _statusAt = DateTime.Now;
            _miStatus.Text = "QQ 缓存：统计中…";

            Config snap = _cfg;   // 后台只读快照，别和设置窗口的保存抢同一个对象
            ThreadPool.QueueUserWorkItem(delegate
            {
                long size = 0;
                try { size = Cleaner.MeasureCache(snap, false, null); } catch { }
                PostToUi(delegate
                {
                    _miStatus.Text = "QQ 缓存：" + Cleaner.FormatSize(size) + "（点击设置）";
                });
            });
        }

        private void OpenSettings()
        {
            // 兜底：设置窗口是模态的，但 ShowDialog 期间消息循环仍在跑，
            // 托盘双击/菜单可能再次触发，这里必须挡住，否则会嵌套出多个设置窗口
            if (_settingsOpen)
            {
                Logger.Log("设置窗口已打开，忽略重复触发");
                return;
            }

            TaskbarAlert.Dismiss();

            bool updateNow = false;
            _settingsOpen = true;
            try
            {
                using (SettingsForm f = new SettingsForm(_cfg))
                {
                    f.UpdateInfo = _pendingUpdate;
                    DialogResult r = f.ShowDialog();
                    updateNow = f.UpdateNow;
                    if (r == DialogResult.OK)
                    {
                        _cfg = f.Result;
                        _cfg.Save();
                        _cfg.SaveLastRun(f.Result.LastRun);
                        if (f.Result.WxLastRun != DateTime.MinValue) _cfg.SaveWxLastRun(f.Result.WxLastRun);

                        // 勾选/取消「开机自动启动」必须立刻生效，不能等下次启动才写启动脚本
                        try
                        {
                            string autostartMsg;
                            if (!AutoStart.Set(_cfg.AutostartEnabled, out autostartMsg))
                                Logger.Log("应用开机自启设置失败：" + autostartMsg);
                        }
                        catch (Exception ex) { Logger.Log("应用开机自启设置异常：" + ex.Message); }

                        RefreshStatus();


                    }
                }
            }
            finally
            {
                _settingsOpen = false;
            }

            // 设置窗口期间发现的新版本，关掉之后再提示
            if (updateNow && _pendingUpdate != null)
            {
                UpdateChecker.Info info = _pendingUpdate;
                _pendingUpdate = null;
                DoSelfUpdate(info);
                return;
            }

            if (_pendingUpdate != null)
            {
                Toast.ShowToast("「QQ 缓存管家」发现新版本 " + _pendingUpdate.Version +
                                "，点托盘「设置」可更新", 9000, true);
            }
        }


        private void ExitTray()
        {
            try { SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }
            _icon.Visible = false;
            _icon.Dispose();
            if (_timer != null) _timer.Dispose();
            if (_marshal != null) _marshal.Dispose();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _icon != null) _icon.Dispose();
            base.Dispose(disposing);
        }
    }

    // ---------------------------------------------------------------- 设置窗口

    // 存储空间环形图：浅灰=可用，浅紫=电脑已用，紫色=本应用缓存
    internal sealed class Donut : Control
    {
        public long AppBytes;
        public long OtherBytes;
        public long TotalBytes;
        public string CenterText = "";
        public string SubText = "";
        public Color Accent = Color.FromArgb(139, 92, 246);
        public bool Dark;
        public bool Busy;   // 统计中：还没有结果时圆心画“统计中…”，不要画 0 B

        private static readonly Color TrackColor = Color.FromArgb(236, 238, 244);
        private static readonly Color OtherColor = Color.FromArgb(196, 181, 253);

        public Donut()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White;
        }

        private readonly Font _big = new Font("Microsoft YaHei UI", 15F, FontStyle.Bold);
        private readonly Font _small = new Font("Microsoft YaHei UI", 9F);

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _big.Dispose(); _small.Dispose(); }
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            try { PaintCore(e.Graphics); }
            catch (Exception ex) { Logger.Log("环形图绘制失败：" + ex); }
        }

        private static float SafeSweep(double degrees)
        {
            if (double.IsNaN(degrees) || double.IsInfinity(degrees)) return 0f;
            if (degrees < 0) return 0f;
            if (degrees > 359.5) return 359.5f;
            return (float)degrees;
        }

        private void PaintCore(Graphics g)
        {
            Color track = Dark ? Color.FromArgb(48, 48, 54) : TrackColor;
            Color otherC = Dark ? Color.FromArgb(76, 63, 122) : OtherColor;
            Color textC = Dark ? Color.FromArgb(232, 223, 200) : Color.Black;
            Color subC = Dark ? Color.FromArgb(170, 165, 150) : Color.Gray;
            Color accentC = Dark ? Color.FromArgb(167, 139, 250) : Accent;

            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            int thickness = 14;
            int d = Math.Min(Width, Height) - thickness - 18;
            if (d < 30) return;

            Rectangle rect = new Rectangle((Width - d) / 2, 6, d, d);

            using (Pen pen = new Pen(track, thickness))
            {
                pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                g.DrawEllipse(pen, rect);
            }

            double total = Math.Max(1.0, (double)TotalBytes);
            double app = Math.Max(0.0, Math.Min((double)AppBytes, total));
            double other = Math.Max(0.0, Math.Min((double)OtherBytes, total - app));

            float start = -90f;

            if (other > 0.5)
            {
                float sweep = SafeSweep(other / total * 360.0);
                if (sweep > 0.1f)
                {
                    try
                    {
                        using (Pen pen = new Pen(otherC, thickness))
                        {
                            pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                            pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                            g.DrawArc(pen, rect, start, sweep);
                        }
                        start += sweep;
                    }
                    catch { }
                }
            }

            if (app > 0.5)
            {
                float sweep = SafeSweep(app / total * 360.0);
                if (sweep > 0.1f)
                {
                    try
                    {
                        using (Pen pen = new Pen(accentC, thickness))
                        {
                            pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                            pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                            g.DrawArc(pen, rect, start, sweep);
                        }
                    }
                    catch { }
                }
            }

            // 圆心文字
            RectangleF inner = new RectangleF(rect.X, rect.Y, rect.Width, rect.Height);
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;

                RectangleF r1 = new RectangleF(inner.X, inner.Y - 12, inner.Width, inner.Height);
                string center = CenterText;
                if (Busy && string.IsNullOrEmpty(center)) center = "统计中…";
                using (Brush tb = new SolidBrush(textC)) g.DrawString(center ?? "", _big, tb, r1, sf);

                RectangleF r2 = new RectangleF(inner.X, inner.Y + 26, inner.Width, inner.Height);
                using (Brush sb = new SolidBrush(subC)) g.DrawString(SubText ?? "", _small, sb, r2, sf);
            }
        }
    }

    // 自绘数字框：系统 NumericUpDown 的上下箭头是白色主题色，改不掉，只能自己画
    internal sealed class FlatSpinner : Control
    {
        public decimal Minimum;
        public decimal Maximum = 100;
        public decimal Increment = 1;
        public bool Dark;

        private decimal _value;
        private TextBox _edit;
        private Rectangle _upRect;
        private Rectangle _downRect;
        private const int BtnW = 16;

        public decimal Value
        {
            get { return _value; }
            set
            {
                decimal v = value;
                if (v < Minimum) v = Minimum;
                if (v > Maximum) v = Maximum;

                bool changed = v != _value;
                _value = v;

                if (_edit != null)
                {
                    string text = ((long)v).ToString();
                    if (_edit.Text != text) _edit.Text = text;
                }

                if (changed) Invalidate();
            }
        }

        public FlatSpinner()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 24;

            _edit = new TextBox();
            _edit.BorderStyle = BorderStyle.None;
            _edit.TextAlign = HorizontalAlignment.Right;
            _edit.TextChanged += delegate { ParseEdit(); };
            Controls.Add(_edit);
        }

        private void ParseEdit()
        {
            if (_edit == null) return;
            decimal v;
            if (decimal.TryParse(_edit.Text.Trim(), out v)) Value = v;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_edit != null) _edit.SetBounds(4, Math.Max(1, (Height - 17) / 2), Math.Max(10, Width - BtnW - 8), 17);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (_upRect.Contains(e.Location)) { Value = Value + Increment; }
            else if (_downRect.Contains(e.Location)) { Value = Value - Increment; }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            Value = Value + (e.Delta > 0 ? Increment : -Increment);
        }

        private static void DrawArrow(Graphics g, Rectangle r, bool up, Color color)
        {
            int cx = r.Left + r.Width / 2;
            int cy = r.Top + r.Height / 2;
            Point[] pts = up
                ? new Point[] { new Point(cx - 4, cy + 2), new Point(cx + 4, cy + 2), new Point(cx, cy - 3) }
                : new Point[] { new Point(cx - 4, cy - 2), new Point(cx + 4, cy - 2), new Point(cx, cy + 3) };
            using (Brush b = new SolidBrush(color)) g.FillPolygon(b, pts);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Color bg = Dark ? Color.FromArgb(34, 34, 38) : Color.White;
            Color border = Dark ? Color.FromArgb(72, 72, 80) : Color.FromArgb(200, 205, 215);
            Color arrow = Dark ? Color.FromArgb(176, 176, 186) : Color.FromArgb(90, 95, 105);
            Color text = Dark ? Color.FromArgb(232, 223, 200) : Color.Black;

            using (Brush b = new SolidBrush(bg)) g.FillRectangle(b, ClientRectangle);
            using (Pen p = new Pen(border)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);

            int half = Height / 2;
            _upRect = new Rectangle(Width - BtnW, 1, BtnW - 1, half - 1);
            _downRect = new Rectangle(Width - BtnW, half, BtnW - 1, Height - half - 2);

            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            DrawArrow(g, _upRect, true, arrow);
            DrawArrow(g, _downRect, false, arrow);

            if (_edit != null && (_edit.BackColor != bg || _edit.ForeColor != text))
            {
                _edit.BackColor = bg;
                _edit.ForeColor = text;
            }
        }
    }

    // 自绘复选框：系统默认的白框在深色下太刺眼
    internal sealed class FlatCheck : CheckBox
    {
        public bool Dark;

        public FlatCheck()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : BackColor);

            int box = 14;
            int y = (Height - box) / 2;
            Rectangle r = new Rectangle(1, y, box, box);

            Color fill = Dark ? Color.FromArgb(44, 44, 50) : Color.White;
            Color border = Dark ? Color.FromArgb(96, 96, 106) : Color.FromArgb(150, 155, 165);
            Color tick = Dark ? Color.FromArgb(196, 181, 253) : Color.FromArgb(109, 68, 220);
            Color text = Dark ? Color.FromArgb(232, 223, 200) : Color.Black;

            using (Brush b = new SolidBrush(fill)) g.FillRectangle(b, r);
            using (Pen p = new Pen(border)) g.DrawRectangle(p, r);

            if (Checked)
            {
                using (Pen p = new Pen(tick, 2))
                {
                    g.DrawLine(p, r.Left + 3, r.Top + 7, r.Left + 6, r.Top + 11);
                    g.DrawLine(p, r.Left + 6, r.Top + 11, r.Left + 12, r.Top + 3);
                }
            }

            TextRenderer.DrawText(g, Text, Font,
                new Rectangle(box + 6, 0, Width - box - 6, Height), text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }
    }

    internal sealed class SettingsForm : Form
    {
        private readonly Config _cfg;
        public Config Result;
        public UpdateChecker.Info UpdateInfo;    // 有更新时传入，界面顶部显示更新条
        public bool UpdateNow;                   // 用户在更新条上点了“立即更新”

        private AppPanel _qq;
        private AppPanel _wx;

        private Panel _tabHost;
        private Panel _pageWx;
        private Panel _pageQq;
        private Button _tabWx;
        private Button _tabQq;
        private int _tabIndex;

        private CheckBox _chkAutoStart;
        private CheckBox _chkDark;
        private bool _dark;
        private ToolTip _tip;
        private Label _lblTaskWarn;
        private Panel _updateBar;
        private Label _updateText;
        private Button _btnKillTask;

        private sealed class AppPanel
        {
            public bool IsWx;
            public CheckBox Enabled;          // 启用容量上限清理
            public FlatCheck OneShot;         // 勾选「开启」：开机只检查一次后退出
            public FlatSpinner LimitMB;
            public Button ActOld;
            public Button ActAll;
            public bool DeleteAll;

            public TextBox Root;
            public Label Detect;
            public CheckBox Pic;
            public CheckBox Video;
            public CheckBox Emoji;
            public CheckBox Voice;
            public CheckBox SysEmoji;
            public CheckBox PersonalEmoji;

            public Label Last;
            public Label Size;
            public Button CleanNow;
            public Button Rescan;

            public Donut Ring;

            public Label LblLimit;
            public Label LblMB;
        }
        public SettingsForm(Config cfg)
        {
            _cfg = cfg;
            // 缩放自己算（见 OnLoad）：WinForms 自带的 Dpi 自动缩放只管子控件、不管窗口，
            // 高 DPI 下会出现“内容放大、窗口不变、右边和窄控件里的字被裁掉”。
            AutoScaleMode = AutoScaleMode.None;
            BuildUi();
            LoadPanel(_wx, cfg);
            LoadPanel(_qq, cfg);
            _chkAutoStart.Checked = AutoStart.IsEnabled();
            ApplyAutostartLabel();
            _chkDark.Checked = cfg.DarkMode || Program.ForceDark;   // 变更会触发 ApplyTheme；值没变就不用再刷一次
            _dark = _chkDark.Checked;
            DetectLegacyTask();
            _loaded = true;   // 配置已经装进各个控件，从这里开始才允许统计
        }

        private void ApplyAutostartLabel()
        {
            string state = AutoStart.DescribeState();
            _chkAutoStart.Text = "开机自动启动（登录时请求管理员权限）" +
                                 (state == "已被系统禁用" ? " — 已被系统禁用，保存即修复" : "");
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // 按实际 DPI 把整棵控件树放大：WinForms 的 Dpi 自动缩放只处理子控件、
            // 不改窗口尺寸，高 DPI 下会“内容放大但窗口不变”，右侧与窄控件的字被裁掉。
            float f = 1f;
            try { using (Graphics g = CreateGraphics()) f = g.DpiX / 96f; } catch { }
            if (f > 1.01f)
            {
                Scale(new SizeF(f, f));
                ClientSize = new Size((int)Math.Round(470 * f), (int)Math.Round(700 * f));
            }
        }
        // WS_EX_COMPOSITED：让整个窗口（含所有子控件）离屏合成后再一次性显示，
        // 多控件主题切换时不会出现半新半旧的中间帧
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x02000000;
                return cp;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            // 只统计当前可见的标签页，另一个等切过去再算（大缓存时打开窗口秒开）
            MarkDirtyAll();
            EnsureMeasured(_tabIndex == 0 ? _wx : _qq);

            // 有更新时把更新条显示出来，并闪一下任务栏
            if (UpdateInfo != null)
            {
                _updateText.Text = "发现新版本 " + UpdateInfo.Version;
                _updateText.Tag = "当前 " + UpdateChecker.CurrentVersion;
                if (_lblTaskWarn != null) _lblTaskWarn.Visible = false;
                _updateBar.Visible = true;
                FlashTaskbar();
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
        }

        private void BuildUi()
        {
            Text = "缓存清理 - 设置";
            Icon = IconFactory.Create(32);
            Font = new Font("Microsoft YaHei UI", 9F);
            DoubleBuffered = true;
            ClientSize = new Size(470, 700);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            BackColor = Color.FromArgb(246, 247, 250);

            // 自绘的标签切换栏（不用 TabControl，避免系统绘制的浅色边框）
            _tabWx = MakeSeg("微信", 12, 8, 100);
            _tabWx.Click += delegate { SelectTab(0); EnsureMeasured(_wx); };   // 第一次切到这个标签时补算大小
            Controls.Add(_tabWx);

            _tabQq = MakeSeg("QQ", 118, 8, 100);
            _tabQq.Click += delegate { SelectTab(1); EnsureMeasured(_qq); };
            Controls.Add(_tabQq);

            _tabHost = new Panel();
            _tabHost.SetBounds(12, 40, 446, 502);
            _tabHost.BackColor = Color.White;
            Controls.Add(_tabHost);

            _pageWx = new Panel();
            _pageWx.SetBounds(0, 0, 446, 502);
            _pageWx.BackColor = Color.White;
            _pageWx.Tag = "stack";              // 与 QQ 页互斥显示，重叠属预期
            _tabHost.Controls.Add(_pageWx);

            _pageQq = new Panel();
            _pageQq.SetBounds(0, 0, 446, 502);
            _pageQq.BackColor = Color.White;
            _pageQq.Tag = "stack";
            _tabHost.Controls.Add(_pageQq);

            _wx = BuildPage(_pageWx, true);
            _qq = BuildPage(_pageQq, false);

            _chkAutoStart = new FlatCheck();
            _chkAutoStart.Text = "开机自动启动（登录时请求管理员权限）";
            _chkAutoStart.SetBounds(18, 548, 300, 24);
            Controls.Add(_chkAutoStart);

            _chkDark = new FlatCheck();
            _chkDark.Text = "黑夜模式";
            _chkDark.SetBounds(330, 548, 110, 24);
            _chkDark.CheckedChanged += delegate { ApplyTheme(_chkDark.Checked); };
            Controls.Add(_chkDark);

            _lblTaskWarn = new Label();
            _lblTaskWarn.ForeColor = Color.FromArgb(180, 90, 0);
            _lblTaskWarn.SetBounds(16, 618, 438, 26);
            _lblTaskWarn.Visible = false;
            Controls.Add(_lblTaskWarn);

            _btnKillTask = new Button();
            _btnKillTask.Text = "停用旧的计划任务";
            _btnKillTask.SetBounds(16, 656, 150, 26);
            _btnKillTask.Visible = false;
            _btnKillTask.Click += delegate { KillLegacyTask(); };
            Controls.Add(_btnKillTask);

            Button btnSave = new Button();
            btnSave.Text = "保存";
            btnSave.SetBounds(232, 648, 100, 34);
            btnSave.Click += delegate { SaveAndClose(); };
            Controls.Add(btnSave);

            Button btnCancel = new Button();
            btnCancel.Text = "取消";
            btnCancel.SetBounds(342, 648, 100, 34);
            btnCancel.DialogResult = DialogResult.Cancel;
            Controls.Add(btnCancel);

            _tip = new ToolTip();
            BuildUpdateBar();

            AcceptButton = btnSave;
            CancelButton = btnCancel;

            SelectTab(Program.SettingsTab);
        }

        // 只负责切换显示与配色，不在这里统计。
        // 构造期（LoadPanel 之前）统计会读到还没加载的复选框 -> Categories() 为空 -> 算出 0 B；
        // 主题切换也不该顺手发起一次统计。统计由标签点击、OnShown 显式调用 EnsureMeasured 决定。
        private void SelectTab(int idx)
        {
            _tabIndex = idx;
            if (_pageWx == null) return;

            _pageWx.Visible = idx == 0;
            _pageQq.Visible = idx == 1;

            // 两个页面叠在同一位置（后加的在下层）。只切 Visible 不够：
            // 主题切换时重绘顺序会让隐藏页的旧像素留在屏幕上，QQ 页看起来变成微信页。
            Panel front = idx == 0 ? _pageWx : _pageQq;
            try { front.BringToFront(); } catch { }
            front.Invalidate();

            StyleTab(_tabWx, idx == 0);
            StyleTab(_tabQq, idx == 1);
        }

        private void StyleTab(Button b, bool active)
        {
            if (b == null) return;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = active ? Color.FromArgb(139, 92, 246)
                                 : (_dark ? DarkPanel : Color.FromArgb(240, 241, 245));
            b.ForeColor = active ? Color.White : (_dark ? DarkText : Color.Black);
        }

        private static Label MakeLabel(string text, int x, int y, int w, int h)
        {
            Label l = new Label();
            l.Text = text;
            l.SetBounds(x, y, w, h);
            l.TextAlign = ContentAlignment.MiddleLeft;
            return l;
        }

        private static Button MakeSeg(string text, int x, int y, int w)
        {
            Button b = new Button();
            b.Text = text;
            b.SetBounds(x, y, w, 28);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Color.FromArgb(200, 205, 215);
            b.BackColor = Color.FromArgb(245, 246, 250);
            return b;
        }

        private AppPanel BuildPage(Control page, bool isWx)
        {
            AppPanel p = new AppPanel();
            p.IsWx = isWx;
            page.BackColor = Color.White;

            p.Ring = new Donut();
            p.Ring.SetBounds(8, 4, 430, 150);
            p.Ring.Accent = Color.FromArgb(139, 92, 246);
            page.Controls.Add(p.Ring);

            Label legend1 = MakeLabel("● " + (isWx ? "微信" : "QQ") + "缓存", 52, 154, 120, 18);
            legend1.Tag = "keepcolor";
            legend1.ForeColor = Color.FromArgb(139, 92, 246);
            page.Controls.Add(legend1);

            Label legend2 = MakeLabel("● 电脑已用空间", 176, 154, 128, 18);
            legend2.Tag = "keepcolor";
            legend2.ForeColor = Color.FromArgb(196, 181, 253);
            page.Controls.Add(legend2);

            Label legend3 = MakeLabel("● 电脑可用空间", 306, 154, 130, 18);
            legend3.Tag = "keepcolor";
            legend3.ForeColor = Color.FromArgb(200, 205, 215);
            page.Controls.Add(legend3);

            Label cfgTitle = MakeLabel("配置", 8, 178, 100, 22);
            cfgTitle.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
            page.Controls.Add(cfgTitle);

            p.Enabled = new FlatCheck();
            p.Enabled.Text = "启用容量上限清理";
            p.Enabled.SetBounds(6, 202, 260, 24);
            p.Enabled.CheckedChanged += delegate { UpdatePanel(p); };
            page.Controls.Add(p.Enabled);

            p.OneShot = new FlatCheck();
            p.OneShot.Text = "开启（勾选：开机检查一次后退出）";
            p.OneShot.SetBounds(6, 230, 300, 24);
            p.OneShot.CheckedChanged += delegate { UpdatePanel(p); };
            page.Controls.Add(p.OneShot);

            // ---- 上限行：文件到达 [N] MB 时 [删除旧文件] [全部删除] ----
            p.LblLimit = MakeLabel("文件到达", 8, 260, 70, 22);
            page.Controls.Add(p.LblLimit);

            p.LimitMB = new FlatSpinner();
            p.LimitMB.Minimum = 100;
            p.LimitMB.Maximum = 200000;
            p.LimitMB.Increment = 100;
            p.LimitMB.SetBounds(80, 258, 76, 24);
            page.Controls.Add(p.LimitMB);

            p.LblMB = MakeLabel("MB 时", 162, 260, 50, 22);
            page.Controls.Add(p.LblMB);

            p.ActOld = MakeSeg("删除旧文件", 214, 256, 100);
            p.ActOld.Click += delegate { p.DeleteAll = false; UpdatePanel(p); };
            page.Controls.Add(p.ActOld);

            p.ActAll = MakeSeg("全部删除", 318, 256, 100);
            p.ActAll.Click += delegate { p.DeleteAll = true; UpdatePanel(p); };
            page.Controls.Add(p.ActAll);

            // ---- 目录 ----
            page.Controls.Add(MakeLabel("目录", 8, 300, 36, 22));

            p.Root = new TextBox();
            p.Root.SetBounds(44, 298, 282, 24);
            page.Controls.Add(p.Root);

            Button btnBrowse = MakeSeg("浏览", 330, 296, 48);
            btnBrowse.Click += delegate { BrowseRoot(p); };
            page.Controls.Add(btnBrowse);

            Button btnAuto = MakeSeg("自动", 382, 296, 48);
            btnAuto.Click += delegate { p.Root.Text = ""; RefreshSize(p); };
            page.Controls.Add(btnAuto);

            p.Detect = MakeLabel("正在检测…", 44, 328, 380, 18);
            p.Detect.ForeColor = Color.Gray;
            page.Controls.Add(p.Detect);

            // ---- 清理内容 ----
            page.Controls.Add(MakeLabel("清理内容", 8, 350, 70, 22));

            p.Pic = MakeCheck("图片", 84, 348);
            p.Pic.SetBounds(84, 348, 70, 22);
            p.Video = MakeCheck("视频", 160, 348);
            p.Video.SetBounds(160, 348, 70, 22);
            p.Emoji = MakeCheck("表情", 236, 348);
            p.Emoji.SetBounds(236, 348, 70, 22);
            p.Voice = MakeCheck("语音", 312, 348);
            p.Voice.SetBounds(312, 348, 70, 22);

            page.Controls.Add(p.Pic);
            page.Controls.Add(p.Video);
            page.Controls.Add(p.Emoji);
            page.Controls.Add(p.Voice);

            if (!isWx)
            {
                p.SysEmoji = MakeCheck("系统表情", 84, 372);
                p.SysEmoji.SetBounds(84, 372, 80, 22);
                p.PersonalEmoji = MakeCheck("收藏表情", 170, 372);
                p.PersonalEmoji.SetBounds(170, 372, 80, 22);
                page.Controls.Add(p.SysEmoji);
                page.Controls.Add(p.PersonalEmoji);
            }

            // ---- 状态 ----
            p.Last = MakeLabel("上次清理：从未清理", 8, 398, 232, 18);
            page.Controls.Add(p.Last);

            p.Size = MakeLabel("当前占用：—", 8, 416, 232, 18);
            page.Controls.Add(p.Size);

            p.CleanNow = MakeSeg("立即清理", 246, 410, 100);
            p.CleanNow.Click += delegate { CleanNow(p); };
            page.Controls.Add(p.CleanNow);

            p.Rescan = MakeSeg("重新统计", 352, 410, 78);
            p.Rescan.Click += delegate { RefreshSize(p); };
            page.Controls.Add(p.Rescan);

            return p;
        }
        private CheckBox MakeCheck(string text, int x, int y)
        {
            CheckBox c = new FlatCheck();
            c.Text = text;
            c.SetBounds(x, y, 180, 22);
            return c;
        }



        private static decimal Clamp(int v, int lo, int hi)
        {
            return Math.Max(lo, Math.Min(hi, v));
        }

        private void LoadPanel(AppPanel p, Config c)
        {
            if (p == null) return;

            bool wx = p.IsWx;

            p.Enabled.Checked = wx ? c.WxEnabled : c.Enabled;
            p.OneShot.Checked = wx ? c.WxOneShot : c.OneShot;
            p.LimitMB.Value = Clamp(wx ? c.WxLimitMB : c.LimitMB, 100, 200000);
            p.DeleteAll = wx ? c.WxLimitDeleteAll : c.LimitDeleteAll;
            p.Root.Text = (wx ? c.WxRoot : c.QqRoot) ?? "";

            p.Pic.Checked = wx ? c.WxPic : c.Pic;
            p.Video.Checked = wx ? c.WxVideo : c.Video;
            p.Emoji.Checked = wx ? c.WxEmoji : c.Emoji;
            p.Voice.Checked = wx ? c.WxVoice : c.Ptt;

            if (!wx)
            {
                p.SysEmoji.Checked = c.IncludeSystemEmoji;
                p.PersonalEmoji.Checked = c.IncludePersonalEmoji;
            }

            DateTime last = wx ? c.WxLastRun : c.LastRun;
            p.Last.Text = "上次清理：" + (last == DateTime.MinValue ? "从未清理" : last.ToString("yyyy-MM-dd HH:mm"));

            UpdatePanel(p);
        }

        private void SavePanel(AppPanel p, Config c)
        {
            bool wx = p.IsWx;

            if (wx)
            {
                c.WxEnabled = p.Enabled.Checked;
                c.WxOneShot = p.OneShot.Checked;
                c.WxLimitMB = (int)p.LimitMB.Value;
                c.WxLimitDeleteAll = p.DeleteAll;
                c.WxRoot = p.Root.Text.Trim();
                c.WxPic = p.Pic.Checked;
                c.WxVideo = p.Video.Checked;
                c.WxEmoji = p.Emoji.Checked;
                c.WxVoice = p.Voice.Checked;
            }
            else
            {
                c.Enabled = p.Enabled.Checked;
                c.OneShot = p.OneShot.Checked;
                c.LimitMB = (int)p.LimitMB.Value;
                c.LimitDeleteAll = p.DeleteAll;
                c.QqRoot = p.Root.Text.Trim();
                c.Pic = p.Pic.Checked;
                c.Video = p.Video.Checked;
                c.Emoji = p.Emoji.Checked;
                c.Ptt = p.Voice.Checked;

                if (p.SysEmoji != null) c.IncludeSystemEmoji = p.SysEmoji.Checked;
                if (p.PersonalEmoji != null) c.IncludePersonalEmoji = p.PersonalEmoji.Checked;
            }
        }

        private void UpdatePanel(AppPanel p)
        {
            bool on = p.Enabled.Checked;

            Color inactive = _dark ? DarkPanel : Color.FromArgb(245, 246, 250);
            Color inactiveText = _dark ? DarkText : Color.Black;
            Color picked = _dark ? Color.FromArgb(88, 66, 160) : Color.FromArgb(233, 226, 255);

            p.ActOld.BackColor = (!p.DeleteAll) ? picked : inactive;
            p.ActOld.ForeColor = (!p.DeleteAll) ? (_dark ? DarkText : Color.Black) : inactiveText;
            p.ActAll.BackColor = p.DeleteAll ? picked : inactive;
            p.ActAll.ForeColor = p.DeleteAll ? (_dark ? DarkText : Color.Black) : inactiveText;

            p.OneShot.Enabled = on;
            p.LimitMB.Enabled = on;
            p.ActOld.Enabled = on;
            p.ActAll.Enabled = on;
        }
        private void BrowseRoot(AppPanel p)
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = p.IsWx ? "选择微信数据目录" : "选择 QQ 数据目录";
                d.SelectedPath = p.Root.Text;
                if (d.ShowDialog() == DialogResult.OK)
                {
                    p.Root.Text = d.SelectedPath;
                    RefreshSize(p);
                }
            }
        }
        // ---- 缓存统计：后台线程跑，避免大缓存时设置窗口卡住 ----
        private readonly Dictionary<AppPanel, int> _refreshGen = new Dictionary<AppPanel, int>();
        private bool _wxMeasured, _qqMeasured;
        private bool _loaded;      // LoadPanel 是否已经跑过：之前统计读到的是空配置，会算出 0 B
        private int _scanCount;    // 发起过的统计次数，自检用来确认“切主题不会重新统计”

        private void MarkDirtyAll()
        {
            _wxMeasured = false;
            _qqMeasured = false;
        }

        private void EnsureMeasured(AppPanel p)
        {
            if (p == null) return;
            if (!(p.IsWx ? _wxMeasured : _qqMeasured)) RefreshSize(p);
        }

        private void RefreshSize(AppPanel p)
        {
            if (p == null) return;
            if (!_loaded) return;   // LoadPanel 之前不统计：那时复选框还是默认值，算出来是 0

            Config tmp = new Config();
            SavePanel(p, tmp);

            if (p.IsWx) _wxMeasured = true; else _qqMeasured = true;

            int gen;
            if (!_refreshGen.TryGetValue(p, out gen)) gen = 0;
            gen++;
            _refreshGen[p] = gen;
            _scanCount++;

            p.Ring.Busy = true;   // 环形图：没有旧数值时显示“统计中…”，有旧数值就继续显示，别闪 0 B
            p.Size.Text = "当前占用：统计中…";
            p.Rescan.Enabled = false;

            bool isWx = p.IsWx;
            ThreadPool.QueueUserWorkItem(delegate
            {
                long cache = 0;
                try { cache = Cleaner.MeasureCache(tmp, isWx, null); } catch { }

                string root = "";
                try { root = Cleaner.PrimaryRoot(tmp, isWx); } catch { }

                long total = 0, free = 0;
                try
                {
                    string drive = string.IsNullOrEmpty(root) ? "C:" : Path.GetPathRoot(root);
                    DriveInfo di = new DriveInfo(drive);
                    total = di.TotalSize;
                    free = di.AvailableFreeSpace;
                }
                catch { }

                try
                {
                    BeginInvoke((Action)delegate
                    {
                        int cur;
                        if (!_refreshGen.TryGetValue(p, out cur) || cur != gen) return;   // 已有更新的一次统计
                        p.Rescan.Enabled = true;
                        ApplySize(p, cache, root, total, free);
                    });
                }
                catch { }
            });
        }

        private void ApplySize(AppPanel p, long cache, string root, long total, long free)
        {
            if (total <= 0) total = 1;

            p.Ring.Busy = false;

            p.Ring.TotalBytes = total;
            p.Ring.AppBytes = cache;
            p.Ring.OtherBytes = Math.Max(0, (total - free) - cache);
            p.Ring.CenterText = Cleaner.FormatSize(cache);

            long limit = (long)Math.Max(100, (int)p.LimitMB.Value) * 1024L * 1024L;
            double pct = limit <= 0 ? 0 : cache * 100.0 / limit;
            p.Ring.SubText = string.Format("占设置上限 {0:0.#}%", pct);
            p.Ring.Invalidate();

            p.Size.Text = "当前占用：" + Cleaner.FormatSize(cache);
            string show = string.IsNullOrEmpty(root) ? "未找到，请手动选择目录" : TailPath(root);
            p.Detect.Text = "自动检测：" + show;
            if (_tip != null)
            {
                _tip.SetToolTip(p.Detect, string.IsNullOrEmpty(root) ? "" : root);
                _tip.SetToolTip(p.Last, p.Last.Text);
            }
        }

        // 长路径只显示尾部，避免把 `...\Documents\Tencent` 这种半截路径当成微信目录
        internal static string TailPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string s = path.TrimEnd('\\', '/');
            string[] segs = s.Split(new char[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segs.Length <= 3) return s;
            return "…\\" + segs[segs.Length - 3] + "\\" + segs[segs.Length - 2] + "\\" + segs[segs.Length - 1];
        }

        private void CleanNow(AppPanel p)
        {
            Config tmp = new Config();
            SavePanel(p, tmp);

            CleanResult r = Cleaner.EnforceLimit(tmp, p.IsWx, Logger.Log);

            if (string.IsNullOrEmpty(r.Error) && r.TotalDeleted > 0)
            {
                if (p.IsWx) tmp.SaveWxLastRun(DateTime.Now); else tmp.SaveLastRun(DateTime.Now);
                tmp.Save();
            }

            string msg;
            if (!string.IsNullOrEmpty(r.Error))
                msg = "清理失败：" + r.Error;
            else if (r.TotalDeleted == 0)
                msg = (p.IsWx ? "微信" : "QQ") + "：未超过容量上限，无需清理";
            else
                msg = string.Format("{0}：已清理 {1} 个文件，释放 {2}",
                    p.IsWx ? "微信" : "QQ", r.TotalDeleted, Cleaner.FormatSize(r.TotalFreed));

            Toast.ShowToast(msg);
            MarkDirtyAll();
            RefreshSize(p);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct FLASHWINFO
        {
            public uint cbSize;
            public IntPtr hwnd;
            public uint dwFlags;
            public uint uCount;
            public uint dwTimeout;
        }

        private void BuildUpdateBar()
        {
            _updateBar = new Panel();
            _updateBar.SetBounds(16, 580, 438, 32);
            _updateBar.Visible = false;
            _updateBar.BorderStyle = BorderStyle.FixedSingle;
            _updateBar.BackColor = Color.FromArgb(223, 246, 221);   // 浅绿
            Controls.Add(_updateBar);

            _updateText = new Label();
            _updateText.SetBounds(6, 6, 148, 20);
            _updateText.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            _updateText.ForeColor = Color.FromArgb(0, 97, 0);       // 深绿字
            _updateText.BackColor = Color.Transparent;
            _updateBar.Controls.Add(_updateText);

            Button btnUpdate = new Button();
            btnUpdate.Text = "立即更新";
            btnUpdate.SetBounds(158, 3, 88, 26);
            btnUpdate.Click += delegate { UpdateNow = true; Close(); };
            _updateBar.Controls.Add(btnUpdate);

            Button btnPage = new Button();
            btnPage.Text = "下载页";
            btnPage.SetBounds(250, 3, 76, 26);
            btnPage.Click += delegate
            {
                try
                {
                    if (UpdateInfo != null && !string.IsNullOrEmpty(UpdateInfo.Url))
                        Process.Start(UpdateInfo.Url);
                }
                catch { }
            };
            _updateBar.Controls.Add(btnPage);

            Button btnLater = new Button();
            btnLater.Text = "稍后";
            btnLater.SetBounds(330, 3, 60, 26);
            btnLater.Click += delegate { _updateBar.Visible = false; };
            _updateBar.Controls.Add(btnLater);
        }

        private void FlashTaskbar()
        {
            try
            {
                FLASHWINFO fi = new FLASHWINFO();
                fi.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(FLASHWINFO));
                fi.hwnd = Handle;
                fi.dwFlags = 0x3 | 0xC;   // FLASHW_ALL | FLASHW_TIMERNOFG
                fi.uCount = 6;
                fi.dwTimeout = 0;
                FlashWindowEx(ref fi);
            }
            catch { }
        }

        // 整棵控件树都失效，配合一次 Update 整窗重画
        private static void InvalidateTree(Control c)
        {
            if (c == null) return;
            c.Invalidate();
            foreach (Control child in c.Controls) InvalidateTree(child);
        }

        private static readonly Color DarkBg = Color.FromArgb(22, 22, 24);
        private static readonly Color DarkPanel = Color.FromArgb(34, 34, 38);
        private static readonly Color DarkText = Color.FromArgb(232, 223, 200);

        // 主题切换：自上而下改色，最后整窗刷新一次。
        // 不再用 WM_SETREDRAW 批处理——高 DPI 下 Label 会先于父容器重画，导致文字看起来“没画出来”。
        private void ApplyTheme(bool dark)
        {
            SuspendLayout();
            try
            {
                _dark = dark;
                BackColor = dark ? DarkBg : Color.FromArgb(246, 247, 250);
                ApplyTo(this, dark);

                if (_wx != null) { _wx.Ring.Dark = dark; _wx.Ring.Invalidate(); }
                if (_qq != null) { _qq.Ring.Dark = dark; _qq.Ring.Invalidate(); }

                foreach (AppPanel p in new AppPanel[] { _wx, _qq })
                    if (p != null) UpdatePanel(p);

                SelectTab(_tabIndex);
            }
            finally
            {
                ResumeLayout(true);
                InvalidateTree(this);
                // 只把可见页再刷一次：隐藏页的旧像素不能留在屏幕上
                Panel front = _tabIndex == 0 ? _pageWx : _pageQq;
                if (front != null) front.Invalidate();
                Update();
            }
        }

        private void ApplyTo(Control c, bool dark)
        {
            if (c == null) return;
            if (ReferenceEquals(c, _updateBar)) return;   // 更新条固定绿色
            if (ReferenceEquals(c, _tabWx) || ReferenceEquals(c, _tabQq)) return;   // 切换按钮单独上色

            if (c is FlatSpinner)
            {
                ((FlatSpinner)c).Dark = dark;
                c.Invalidate();
                return;   // 内部 TextBox 由控件自己管
            }

            if (c is FlatCheck)
            {
                ((FlatCheck)c).Dark = dark;
                c.Invalidate();
                return;
            }

            Color pageBg = dark ? DarkBg : Color.White;
            Color formBg = dark ? DarkBg : Color.FromArgb(246, 247, 250);
            Color parentBg = c.Parent != null
                ? (c.Parent is Form ? formBg : c.Parent.BackColor)
                : pageBg;

            if (c is Donut)
            {
                ((Donut)c).Dark = dark;
                c.BackColor = pageBg;
                c.Invalidate();
                return;
            }

            if (c is TabControl)
            {
                c.BackColor = dark ? DarkBg : Color.FromArgb(235, 236, 240);
                return;
            }

            if (c is TabPage || c is Panel)
            {
                c.BackColor = pageBg;
                // 页面先上色，再处理子控件（子控件要拿父容器的最终底色）
                foreach (Control child in c.Controls) ApplyTo(child, dark);
                return;
            }

            if (c is Label || c is CheckBox)
            {
                bool keep = c.Tag != null && (string)c.Tag == "keepcolor";
                if (!keep) c.ForeColor = dark ? DarkText : Color.Black;
                c.BackColor = parentBg;   // 显式底色，不用 Transparent（避免高 DPI 下取到旧像素）
                c.Invalidate();
                foreach (Control child in c.Controls) ApplyTo(child, dark);
                return;
            }

            if (c is Button)
            {
                Button b = (Button)c;
                b.FlatStyle = FlatStyle.Flat;
                b.BackColor = dark ? DarkPanel : Color.FromArgb(245, 246, 250);
                b.ForeColor = dark ? DarkText : Color.Black;
                b.FlatAppearance.BorderColor = dark ? Color.FromArgb(70, 70, 78) : Color.FromArgb(200, 205, 215);
                b.Invalidate();
                return;
            }

            if (c is TextBox)
            {
                TextBox tb = (TextBox)c;
                tb.BorderStyle = dark ? BorderStyle.None : BorderStyle.Fixed3D;
                tb.BackColor = dark ? DarkPanel : Color.White;
                tb.ForeColor = dark ? DarkText : Color.Black;
                return;
            }

            if (c is NumericUpDown)
            {
                NumericUpDown nu = (NumericUpDown)c;
                nu.BorderStyle = dark ? BorderStyle.None : BorderStyle.Fixed3D;
                nu.BackColor = dark ? DarkPanel : Color.White;
                nu.ForeColor = dark ? DarkText : Color.Black;
                return;
            }

            if (c is DateTimePicker)
            {
                c.BackColor = dark ? DarkPanel : Color.White;
                c.ForeColor = dark ? DarkText : Color.Black;
                return;
            }

            // 其它容器（GroupBox 等）：先上色再递归
            c.BackColor = parentBg;
            foreach (Control child in c.Controls) ApplyTo(child, dark);
        }
        // 保存设置时的开机行为说明：两边都常驻时返回空串（不打扰）
        internal static string BootBehaviourWarning(Config c)
        {
            bool resQq = c.Enabled && !c.OneShot;
            bool resWx = c.WxEnabled && !c.WxOneShot;
            if (resQq && resWx) return "";

            string qq = !c.Enabled ? "未启用"
                      : (c.OneShot ? "只在开机检查一次后退出" : "常驻，每 5~30 分钟检查");
            string wx = !c.WxEnabled ? "未启用"
                      : (c.WxOneShot ? "只在开机检查一次后退出" : "常驻，每 5~30 分钟检查");

            string tail;
            if (!c.Enabled && !c.WxEnabled)
                tail = "两个都没启用：开机只会亮一下图标说明，不做任何清理。";
            else if (resQq || resWx)
                tail = "标着「只在开机检查一次后退出」的那一方，开机检查完就退出，不会跟着常驻。";
            else
                tail = "两个都会在开机检查一次后退出，不会常驻后台；想让它一直盯着容量，请取消勾选「开启」。";

            return "保存后开机是这样的：\r\n\r\nQQ：" + qq + "\r\n微信：" + wx + "\r\n\r\n" + tail;
        }

        private void SaveAndClose()
        {
            foreach (AppPanel p in new AppPanel[] { _qq, _wx })
            {
                string root = (p.Root.Text ?? "").Trim();
                if (root.Length > 0 && !Directory.Exists(root))
                {
                    MessageBox.Show((p.IsWx ? "微信" : "QQ") + "目录不存在：\n" + root +
                        "\n\n请重新选择，或点「自动」让程序自己找。",
                        "QQ 缓存管家", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }

            Config c = new Config();
            c.LastRun = _cfg.LastRun;
            c.WxLastRun = _cfg.WxLastRun;
            c.LegacyCleaned = _cfg.LegacyCleaned;
            c.UpdateCheck = _cfg.UpdateCheck;
            c.UpdateUrl = _cfg.UpdateUrl;
            c.LastUpdateCheck = _cfg.LastUpdateCheck;
            SavePanel(_qq, c);
            SavePanel(_wx, c);
            c.AutostartEnabled = _chkAutoStart.Checked;
            c.DarkMode = _chkDark.Checked;

            string bootWarn = BootBehaviourWarning(c);
            if (bootWarn.Length > 0) BootNotice.Show(bootWarn, _chkDark.Checked);

            Result = c;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void DetectLegacyTask()
        {
            if (!LegacyTask.Exists()) return;
            _lblTaskWarn.Text = "检测到旧的「QQCacheCleaner-Weekly」计划任务仍在运行，可能与本工具重复清理。建议停用它。";
            if (_updateBar != null) _updateBar.Visible = false;
            _lblTaskWarn.Visible = true;
            _btnKillTask.Visible = true;
        }

        private void KillLegacyTask()
        {
            if (LegacyTask.Delete())
            {
                _lblTaskWarn.Text = "已停用旧计划任务。";
                _btnKillTask.Visible = false;
            }
            else
            {
                MessageBox.Show("停用失败，可能需要在管理员权限下执行。", "QQ 缓存管家",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ---- 自检用：驱动真实控件、读回界面上真正的文字（不参与产品逻辑）----
        internal string RingTextForTest(bool wx) { return (wx ? _wx : _qq).Ring.CenterText; }
        internal string SizeTextForTest(bool wx) { return (wx ? _wx : _qq).Size.Text; }
        internal string DetectTextForTest(bool wx) { return (wx ? _wx : _qq).Detect.Text; }
        internal void ToggleDarkForTest() { _chkDark.Checked = !_chkDark.Checked; }   // 走真实路径：勾选框 -> ApplyTheme
        internal int ScanCountForTest { get { return _scanCount; } }
        // 显式统计某一页：自检不依赖当前停在哪个标签页
        internal void MeasureForTest(bool wx) { if (_loaded) EnsureMeasured(wx ? _wx : _qq); }

        // 自检用：切标签 / 查页面可见性 / 文字是否被控件宽度裁掉
        internal void SelectTabForTest(int idx) { SelectTab(idx); }
        internal int TabIndexForTest { get { return _tabIndex; } }
        internal bool PageVisibleForTest(bool wx) { return (wx ? _pageWx : _pageQq).Visible; }
        internal bool SysEmojiVisibleForTest { get { return _qq.SysEmoji != null && _qq.SysEmoji.Visible; } }
        internal int PageZOrderForTest(bool wx)
        {
            Control page = wx ? (Control)_pageWx : (Control)_pageQq;
            if (page == null || page.Parent == null) return -1;
            return page.Parent.Controls.GetChildIndex(page);
        }

        internal void TextFitForTest(bool wx, out int total, out int bad, out string names)
        {
            total = 0; bad = 0;
            StringBuilder sb = new StringBuilder();
            TextFitWalk(wx ? (Control)_pageWx : (Control)_pageQq, ref total, ref bad, sb);
            names = sb.ToString().TrimEnd(';');
        }

        private static void TextFitWalk(Control parent, ref int total, ref int bad, StringBuilder names)
        {
            if (parent == null) return;
            foreach (Control c in parent.Controls)
            {
                bool isText = (c is Label || c is CheckBox || c is Button) && !string.IsNullOrEmpty(c.Text);
                if (isText && c.Width > 0)
                {
                    total++;
                    int pad = (c is Button) ? 12 : (c is CheckBox ? 22 : 0);
                    Size need = TextRenderer.MeasureText(c.Text, c.Font, new Size(int.MaxValue, c.Height),
                        TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                    if (need.Width + pad > c.Width + 2)
                    {
                        bad++;
                        names.Append(c.Text).Append(';');
                    }
                }
                if (c.Controls.Count > 0) TextFitWalk(c, ref total, ref bad, names);
            }
        }

        // 主题切换后检查文字是否还看得见：前景/背景亮度差太小就是“看不见”
        internal void ContrastForTest(bool wx, out int total, out int bad, out string badNames)
        {
            total = 0; bad = 0;
            StringBuilder sb = new StringBuilder();
            ContrastWalk(wx ? (Control)_pageWx : (Control)_pageQq, ref total, ref bad, sb);
            badNames = sb.ToString().TrimEnd(';');
        }

        private static void ContrastWalk(Control parent, ref int total, ref int bad, StringBuilder badNames)
        {
            if (parent == null) return;
            foreach (Control c in parent.Controls)
            {
                bool hasText = (c is Label || c is CheckBox || c is Button) && !string.IsNullOrEmpty(c.Text);
                if (hasText)
                {
                    total++;
                    if (Math.Abs(Luma(c.ForeColor) - Luma(c.BackColor)) < 0.12)
                    {
                        bad++;
                        badNames.Append(c.Text).Append(';');
                    }
                }
                if (c.Controls.Count > 0) ContrastWalk(c, ref total, ref bad, badNames);
            }
        }

        private static double Luma(Color c)
        {
            return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        }
    }

    internal static class Guard
    {
        private const int TokenIntegrityLevel = 25;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr buf, int len, out int needed);

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_BASIC_INFORMATION
        {
            public IntPtr Reserved1;
            public IntPtr PebBaseAddress;
            public IntPtr Reserved2_0;
            public IntPtr Reserved2_1;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
        }

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr process, int infoClass,
            ref PROCESS_BASIC_INFORMATION info, int len, out int returned);

        // 谁把我拉起来的 —— 排查“开机自启到底是谁在启动”时最有用
        public static string DescribeParent(int pid)
        {
            try
            {
                IntPtr h = OpenProcess(0x1000, false, pid);
                if (h == IntPtr.Zero) return "未知";
                PROCESS_BASIC_INFORMATION pbi = new PROCESS_BASIC_INFORMATION();
                int returned;
                int status = NtQueryInformationProcess(h, 0, ref pbi, Marshal.SizeOf(pbi), out returned);
                CloseHandle(h);
                if (status != 0) return "未知";

                int ppid = pbi.InheritedFromUniqueProcessId.ToInt32();
                string name = "?";
                try
                {
                    using (Process parent = Process.GetProcessById(ppid)) { name = parent.ProcessName; }
                }
                catch { }
                return name + "(" + ppid + ")";
            }
            catch { return "未知"; }
        }

        public static string IntegrityName(int pid)
        {
            int lv = GetIntegrity(pid);
            if (lv == 0x1000) return "低(Low)";
            if (lv == 0x2000) return "中(Medium)";
            if (lv == 0x3000) return "高(High)";
            return "未知";
        }

        public static bool IsElevated()
        {
            try
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                {
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch { return false; }
        }

        // 0x1000=低 0x2000=中 0x3000=高；失败返回 -1
        public static int GetIntegrity(int pid)
        {
            IntPtr proc = IntPtr.Zero, token = IntPtr.Zero, buf = IntPtr.Zero;
            try
            {
                proc = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
                if (proc == IntPtr.Zero) return -1;
                if (!OpenProcessToken(proc, 0x0008, out token)) return -1;

                int needed;
                GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out needed);
                if (needed <= 0) return -1;

                buf = Marshal.AllocHGlobal(needed);
                if (!GetTokenInformation(token, TokenIntegrityLevel, buf, needed, out needed)) return -1;

                IntPtr sid = Marshal.ReadIntPtr(buf);
                byte[] raw = new byte[12];
                Marshal.Copy(sid, raw, 0, 12);
                return BitConverter.ToInt32(raw, 8);
            }
            catch { return -1; }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                if (token != IntPtr.Zero) CloseHandle(token);
                if (proc != IntPtr.Zero) CloseHandle(proc);
            }
        }

        // 结束同一程序的“未提权”实例：它们注册不了托盘、写不了配置，还占着单实例锁
        public static int KillStaleInstances()
        {
            int killed = 0;
            string self = Application.ExecutablePath;
            int selfId = Process.GetCurrentProcess().Id;

            foreach (Process p in Process.GetProcessesByName("QQCacheTray"))
            {
                try
                {
                    if (p.Id == selfId) continue;

                    string path = null;
                    try { path = p.MainModule.FileName; } catch { }
                    if (path == null || !string.Equals(path, self, StringComparison.OrdinalIgnoreCase)) continue;

                    if (GetIntegrity(p.Id) >= 0x3000) continue;   // 已提权的正规实例，不动

                    p.Kill();
                    p.WaitForExit(3000);
                    killed++;
                    Logger.Log("结束了一个未提权的旧实例，PID " + p.Id);
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
            return killed;
        }
    }

    internal static class LegacyTask
    {
        private const string TaskName = "QQCacheCleaner-Weekly";

        public static bool Exists()
        {
            return Run("schtasks.exe", "/query /tn \"" + TaskName + "\"") == 0;
        }

        public static bool Delete()
        {
            return Run("schtasks.exe", "/delete /tn \"" + TaskName + "\" /f") == 0;
        }

        private static int Run(string exe, string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return -1;
                    if (!p.WaitForExit(5000))
                    {
                        try { p.Kill(); } catch { }
                        return -1;
                    }
                    return p.ExitCode;
                }
            }
            catch { return -1; }
        }
    }

    // 开机自启：在“启动”文件夹放一个批处理，登录时请求管理员权限再启动。
    // 这台机器上计划任务（哪怕是 SYSTEM）拿到的都是受限令牌，写不了用户目录，
    // 只有 UAC 授权过的进程有真正权限，所以只能走这条路。
    internal static class AutoStart
    {
        private static readonly string StartupDir =
            Environment.GetFolderPath(Environment.SpecialFolder.Startup);

        private const string LnkName = "QQ 缓存管家.lnk";
        private const string LegacyCmdName = "QQCacheTray-Autostart.cmd";

        // 任务管理器「启动」里被关掉的项，会在这个键下留一条 REG_BINARY：首字节 3 = 禁用，2/无 = 启用
        private const string StartupApprovedKey =
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

        private static string LnkPath { get { return Path.Combine(StartupDir, LnkName); } }
        private static string LegacyCmdPath { get { return Path.Combine(StartupDir, LegacyCmdName); } }
        private static string ApprovedValueName { get { return LnkName; } }

        private static Type ShellType()
        {
            try { return Type.GetTypeFromProgID("WScript.Shell"); }
            catch { return null; }
        }

        private static string ReadLnkTarget(string path)
        {
            object shell = null, sc = null;
            try
            {
                Type t = ShellType();
                if (t == null || !File.Exists(path)) return "";
                shell = Activator.CreateInstance(t);
                sc = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { path });
                object target = sc.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, sc, null);
                return (target as string) ?? "";
            }
            catch { return ""; }
            finally
            {
                try { if (sc != null && Marshal.IsComObject(sc)) Marshal.ReleaseComObject(sc); } catch { }
                try { if (shell != null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell); } catch { }
            }
        }

        private static bool CreateLnk(string exe, out string error)
        {
            error = "";
            object shell = null, sc = null;
            try
            {
                Type t = ShellType();
                if (t == null) { error = "系统不支持创建快捷方式（WScript.Shell 不可用）"; return false; }
                shell = Activator.CreateInstance(t);
                sc = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { LnkPath });
                Type st = sc.GetType();
                st.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { exe });
                st.InvokeMember("Arguments", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { "--boot" });
                st.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(exe) });
                st.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { exe + ",0" });
                st.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { "QQ 缓存管家（开机检查）" });
                st.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, sc, null);
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
            finally
            {
                try { if (sc != null && Marshal.IsComObject(sc)) Marshal.ReleaseComObject(sc); } catch { }
                try { if (shell != null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell); } catch { }
            }
        }

        public static bool IsBlocked()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(StartupApprovedKey))
                {
                    if (k == null) return false;
                    byte[] v = k.GetValue(ApprovedValueName) as byte[];
                    if (v == null || v.Length == 0) return false;
                    return v[0] == 3;
                }
            }
            catch { return false; }
        }

        // 重新启用启动项：12 字节，02 + 8 字节时间戳（全 0 = 立即生效）
        public static bool Repair()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(StartupApprovedKey))
                {
                    if (k == null) return false;
                    byte[] on = new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
                    k.SetValue(ApprovedValueName, on, RegistryValueKind.Binary);
                }
                Logger.Log("已修复开机自启：启动项曾被系统禁用，已重新启用");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log("修复开机自启失败：" + ex.Message);
                return false;
            }
        }

        public static string DescribeState()
        {
            try
            {
                if (!IsEnabled()) return "未设置";
                return IsBlocked() ? "已被系统禁用" : "已启用";
            }
            catch { return "未知"; }
        }

        // 启动时自愈：快捷方式缺失就重建，被禁用就修复。note 是给用户看的说明
        public static bool EnsureHealthy(out string note)
        {
            note = "";
            try
            {
                if (!IsEnabled())
                {
                    string msg;
                    if (!Set(true, out msg)) return false;
                }
                if (!IsBlocked()) return true;

                if (Repair())
                {
                    note = "开机自启此前被系统禁用，已自动修复（下次登录生效）";
                    return true;
                }

                note = "开机自启被系统禁用，自动修复失败；请到“任务管理器 → 启动”里启用 QQ 缓存管家";
                return false;
            }
            catch (Exception ex)
            {
                note = "检查开机自启失败：" + ex.Message;
                return false;
            }
        }

        public static bool IsEnabled()
        {
            try { return File.Exists(LnkPath); }
            catch { return false; }
        }

        public static bool Set(bool on, out string message)
        {
            message = "";
            RemoveLegacy();

            try
            {
                if (on)
                {
                    if (string.IsNullOrEmpty(StartupDir) || !Directory.Exists(StartupDir))
                    {
                        message = "找不到启动文件夹：" + StartupDir;
                        return false;
                    }

                    string exe = Application.ExecutablePath;
                    bool need = true;
                    if (File.Exists(LnkPath))
                    {
                        string target = ReadLnkTarget(LnkPath);
                        if (string.Equals(target, exe, StringComparison.OrdinalIgnoreCase)) need = false;
                    }

                    if (need)
                    {
                        string err;
                        if (!CreateLnk(exe, out err))
                        {
                            message = "创建开机启动快捷方式失败：" + err;
                            return false;
                        }
                        Logger.Log("已写入开机启动快捷方式：" + LnkPath);
                    }
                    else
                    {
                        Logger.Log("开机启动快捷方式已是最新，跳过写入");
                    }

                    if (File.Exists(LegacyCmdPath))
                    {
                        try { File.Delete(LegacyCmdPath); Logger.Log("已删除旧的 .cmd 开机启动脚本"); } catch { }
                    }
                    try
                    {
                        using (RegistryKey k = Registry.CurrentUser.OpenSubKey(StartupApprovedKey, true))
                        {
                            if (k != null) k.DeleteValue(LegacyCmdName, false);
                        }
                    }
                    catch { }

                    if (IsBlocked() && !Repair())
                    {
                        message = "启动项被系统禁用，自动修复失败";
                        return false;
                    }
                    return true;
                }

                bool removed = false;
                if (File.Exists(LnkPath)) { File.Delete(LnkPath); removed = true; }
                if (File.Exists(LegacyCmdPath)) { File.Delete(LegacyCmdPath); removed = true; }
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(StartupApprovedKey, true))
                    {
                        if (k != null)
                        {
                            k.DeleteValue(LnkName, false);
                            k.DeleteValue(LegacyCmdName, false);
                        }
                    }
                }
                catch { }
                Logger.Log(removed ? "已删除开机启动快捷方式" : "开机启动本来就没设置");
                return true;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return false;
            }
        }
        public static void Normalize()
        {
            RemoveLegacy();
        }

        // 清掉历史版本留下的计划任务和启动项
        private static void RemoveLegacy()
        {
            DeleteTask("QQCacheTray-Clean");
            DeleteTask("QQCacheTray-Autostart");

            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (k != null) k.DeleteValue("QQCacheTray", false);
                }
            }
            catch { }
        }

        private static void DeleteTask(string name)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("schtasks.exe",
                    "/delete /tn \"" + name + "\" /f");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return;
                    p.WaitForExit(5000);
                }
            }
            catch { }
        }
    }

    // 任务栏提醒：常驻托盘时没有任务栏按钮，有更新时临时挂一个并闪烁
    internal sealed class TaskbarAlert : Form
    {
        private static TaskbarAlert _instance;

        public static Action OnOpen;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct FLASHWINFO
        {
            public uint cbSize;
            public IntPtr hwnd;
            public uint dwFlags;
            public uint uCount;
            public uint dwTimeout;
        }

        private DateTime _shownAt = DateTime.MinValue;
        private bool _ready;

        private TaskbarAlert(string title)
        {
            Text = title;
            ShowInTaskbar = true;
            ShowIcon = false;
            FormBorderStyle = FormBorderStyle.None;   // 工具窗口不会出现在任务栏
            WindowState = FormWindowState.Normal;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-3200, -3200);       // 放在屏幕外，只在任务栏留一个按钮
            Size = new Size(180, 40);
            Opacity = 0.01;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE：别抢焦点
                return cp;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _shownAt = DateTime.Now;
            _ready = true;

            Logger.Log(string.Format("任务栏提醒：visible={0} taskbar={1} state={2}",
                Visible, ShowInTaskbar, WindowState));
            Flash();
        }

        public void Flash()
        {
            try
            {
                FLASHWINFO fi = new FLASHWINFO();
                fi.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(FLASHWINFO));
                fi.hwnd = Handle;
                fi.dwFlags = 0x3 | 0xC;   // FLASHW_ALL | FLASHW_TIMERNOFG
                fi.uCount = 10;
                fi.dwTimeout = 0;
                FlashWindowEx(ref fi);
            }
            catch { }
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);

            // 首次显示会先触发 Activated（此时 OnShown 还没跑），必须忽略
            if (!_ready) return;

            // 刚弹出来时的自动激活不算点击；最小化状态下的激活也不算
            if ((DateTime.Now - _shownAt).TotalMilliseconds < 2000) return;
            if (WindowState == FormWindowState.Minimized) return;

            // 用户点了任务栏按钮 → 打开设置并收掉提醒
            try
            {
                Action cb = OnOpen;
                Dismiss();
                if (cb != null) cb();
            }
            catch { }
        }

        public static void Show(string title)
        {
            try
            {
                Logger.Log("任务栏提醒：开始显示");
                if (_instance != null && !_instance.IsDisposed)
                {
                    _instance.Text = title;
                    _instance.Flash();
                    return;
                }

                _instance = new TaskbarAlert(title);
                _instance.Show();
                Logger.Log("任务栏提醒：Show 返回，handle=" + _instance.Handle);

                // 60 秒没人理就自己收掉，别在任务栏留一个孤儿按钮
                System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
                t.Interval = 60000;
                t.Tick += delegate { t.Stop(); t.Dispose(); Dismiss(); };
                t.Start();
            }
            catch (Exception ex)
            {
                Logger.Log("任务栏提醒失败：" + ex.Message);
            }
        }

        public static void Dismiss()
        {
            try
            {
                if (_instance != null && !_instance.IsDisposed) _instance.Close();
            }
            catch { }
            _instance = null;
        }
    }

    // 检查更新（方案 B）：只做提示，不自动替换
    internal static class UpdateChecker
    {
        // 改成你自己的仓库；也可以在 config.ini 里用 update_url= 覆盖
        public const string DefaultManifestUrl =
            "https://raw.githubusercontent.com/HMUHAN/CacheCleaner/main/latest.json";

        // 依次尝试：官方 → 加速代理 → 镜像（jsDelivr 缓存较久，放最后兜底）
        public static readonly string[] ManifestSources = new string[]
        {
            DefaultManifestUrl,
            "https://ghproxy.net/" + DefaultManifestUrl,
            "https://raw.gitmirror.com/HMUHAN/CacheCleaner/main/latest.json",
            "https://cdn.jsdelivr.net/gh/HMUHAN/CacheCleaner@main/latest.json"
        };

        // 下载 exe 的候选地址（GitHub 不通时走代理）
        public static string[] DownloadSources(string url)
        {
            System.Collections.Generic.List<string> list = new System.Collections.Generic.List<string>();
            list.Add(url);

            if (url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
            {
                list.Add("https://ghproxy.net/" + url);
                list.Add("https://gh-proxy.com/" + url);
            }

            return list.ToArray();
        }

        // 逐个源尝试，全部失败才返回 null
        public static Info FetchAny(out string usedUrl, out string lastError)
        {
            usedUrl = "";
            lastError = "";

            foreach (string src in ManifestSources)
            {
                try
                {
                    Info info = Fetch(src);
                    if (info != null) { usedUrl = src; return info; }
                    lastError = "清单内容不完整";
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                }
            }

            return null;
        }

        public static Version CurrentVersion
        {
            get { return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version; }
        }

        public sealed class Info
        {
            public string Version = "";
            public string Url = "";
            public string Sha256 = "";
            public string Notes = "";
        }

        public static Info Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;

            Info info = new Info();
            info.Version = JsonField(json, "version");
            info.Url = JsonField(json, "url");
            info.Sha256 = JsonField(json, "sha256");
            info.Notes = JsonField(json, "notes");

            if (string.IsNullOrEmpty(info.Version) || string.IsNullOrEmpty(info.Url)) return null;
            return info;
        }

        private static string JsonField(string json, string key)
        {
            Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"(.*?)\"", RegexOptions.Singleline);
            if (!m.Success) return "";
            return m.Groups[1].Value.Replace("\\n", " ").Replace("\\r", " ").Trim();
        }

        public static bool IsNewer(string remote)
        {
            Version rv;
            if (!Version.TryParse(remote, out rv)) return false;
            return rv > CurrentVersion;
        }

        // 代理（可选），例如 http://127.0.0.1:7890
        public static Info Fetch(string url)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            // 加时间戳绕开 raw.githubusercontent 的 CDN 缓存（最长约 5 分钟）
            string fresh = url + (url.IndexOf('?') >= 0 ? "&" : "?") + "t=" + DateTime.UtcNow.Ticks;

            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(fresh);
            req.Timeout = 8000;
            req.ReadWriteTimeout = 8000;
            req.UserAgent = "QQCacheTray/" + CurrentVersion;
            req.Proxy = WebRequest.DefaultWebProxy;   // 跟随系统代理（不内置任何第三方软件）
            req.CachePolicy = new System.Net.Cache.RequestCachePolicy(
                System.Net.Cache.RequestCacheLevel.NoCacheNoStore);

            using (WebResponse resp = req.GetResponse())
            using (Stream stream = resp.GetResponseStream())
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
            {
                return Parse(reader.ReadToEnd());
            }
        }
    }

    // 自动替换自身：下载 → 校验 → 等进程退出 → 覆盖 → 重启
    internal static class SelfUpdater
    {
        public static string Sha256(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream fs = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(fs);
                StringBuilder sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        public static string Download(string url, string expectedSha, out string error)
        {
            string actual;
            return Download(url, expectedSha, out error, out actual);
        }

        public static string Download(string url, string expectedSha, out string error, out string actualSha)
        {
            error = "";
            actualSha = "";
            string tmp = Path.Combine(Path.GetTempPath(), "QQCacheTray_update.exe");

            try
            {
                string lastErr = "";

                foreach (string src in UpdateChecker.DownloadSources(url))
                {
                    try
                    {
                        // WebClient 没有超时，网络卡住会永远挂着；改用 HttpWebRequest 显式设超时
                        HttpWebRequest dl = (HttpWebRequest)WebRequest.Create(src);
                        dl.Timeout = 20000;
                        dl.ReadWriteTimeout = 30000;
                        dl.UserAgent = "QQCacheTray/" + UpdateChecker.CurrentVersion;
                        dl.Proxy = WebRequest.DefaultWebProxy;

                        using (WebResponse resp = dl.GetResponse())
                        using (Stream rs = resp.GetResponseStream())
                        using (FileStream fs = File.Create(tmp))
                        {
                            byte[] buf = new byte[65536];
                            int n;
                            while ((n = rs.Read(buf, 0, buf.Length)) > 0) fs.Write(buf, 0, n);
                        }

                        lastErr = "";
                        break;
                    }
                    catch (Exception ex)
                    {
                        lastErr = ex.Message;
                    }
                }

                if (!string.IsNullOrEmpty(lastErr)) throw new Exception(lastErr);

                actualSha = Sha256(tmp);

                if (!string.IsNullOrEmpty(expectedSha) &&
                    !string.Equals(actualSha, expectedSha, StringComparison.OrdinalIgnoreCase))
                {
                    // 先不删：可能是清单刚被重新发布（CI 重建后哈希变了），交给上层复核
                    error = "SHA-256 校验失败（清单 " + expectedSha.Substring(0, 12) +
                            "… / 实际 " + actualSha.Substring(0, 12) + "…）";
                    return null;
                }

                return tmp;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                return null;
            }
        }

        public static bool Apply(string newExe, string expectedSha, string restartArgs, out string error)
        {
            error = "";
            try
            {
                string target = Application.ExecutablePath;
                string cmdPath = Path.Combine(Path.GetTempPath(), "qqcachetray-update.cmd");
                int pid = Process.GetCurrentProcess().Id;

                StringBuilder body = new StringBuilder();
                body.AppendLine("@echo off");
                body.AppendLine("setlocal");
                body.AppendLine(":wait");
                body.AppendLine("tasklist /FI \"PID eq " + pid + "\" | find \"" + pid + "\" >nul");
                body.AppendLine("if not errorlevel 1 (");
                body.AppendLine("  timeout /t 1 /nobreak >nul");
                body.AppendLine("  goto wait");
                body.AppendLine(")");
                body.AppendLine("copy /y \"" + newExe + "\" \"" + target + "\" >nul");
                body.AppendLine("if errorlevel 1 (");
                body.AppendLine("  del \"" + newExe + "\"");
                body.AppendLine("  exit /b 1");
                body.AppendLine(")");
                string startLine = "start \"\" \"" + target + "\"";
                if (!string.IsNullOrEmpty(restartArgs)) startLine += " " + restartArgs;
                body.AppendLine(startLine);
                body.AppendLine("del \"" + newExe + "\"");
                body.AppendLine("del \"%~f0\"");

                File.WriteAllText(cmdPath, body.ToString(), Encoding.Default);

                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c \"" + cmdPath + "\"");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }

    // 发现新版本时的提示
    internal static class UpdatePrompt
    {
        public static Form Build(string current, UpdateChecker.Info info, bool canSelfUpdate)
        {
            Form f = PromptLayout.NewForm("QQ 缓存管家 - 发现新版本", new Size(480, 220));

            string notes = string.IsNullOrEmpty(info.Notes) ? "" : "\r\n更新说明：" + info.Notes;
            string text = "当前版本：" + current + "\r\n最新版本：" + info.Version + notes +
                          "\r\n\r\n" +
                          (canSelfUpdate
                              ? "点「立即更新」：自动下载、校验并重启完成升级。"
                              : "点「打开下载页」：手动下载后覆盖原 exe，配置不会丢。");

            Label l = PromptLayout.MakeLabel(f, text, 16, 14, PromptLayout.MaxTextWidth);
            f.Controls.Add(l);

            Button update = PromptLayout.MakeButton(f, "立即更新", DialogResult.Yes);
            Button open = PromptLayout.MakeButton(f, canSelfUpdate ? "下载页" : "打开下载页", DialogResult.OK);
            Button later = PromptLayout.MakeButton(f, "稍后", DialogResult.Cancel);

            int y = l.Bottom + 16;
            int right = 16 + PromptLayout.MaxTextWidth;

            later.Location = new Point(right - later.Width, y);
            open.Location = new Point(later.Left - PromptLayout.Gap - open.Width, y);
            update.Location = new Point(open.Left - PromptLayout.Gap - update.Width, y);
            update.Visible = canSelfUpdate;

            f.Controls.Add(update);
            f.Controls.Add(open);
            f.Controls.Add(later);
            f.AcceptButton = canSelfUpdate ? update : open;
            f.CancelButton = later;
            f.ClientSize = new Size(right + 16, y + later.Height + 16);
            return f;
        }
    }

    // 保存设置时的开机行为提示：只有「知道了」，关掉即继续保存
    internal static class BootNotice
    {
        public static void Show(string text, bool dark)
        {
            PromptLayout.SetDark(dark);
            using (Form f = PromptLayout.NewForm("QQ 缓存管家 - 开机行为", new Size(470, 200)))
            {
                Label l = PromptLayout.MakeLabel(f, text, PromptLayout.Pad, 14, PromptLayout.MaxTextWidth);
                f.Controls.Add(l);

                Button ok = PromptLayout.MakeButton(f, "知道了", DialogResult.OK);
                int y = l.Bottom + 16;
                int right = PromptLayout.Pad + PromptLayout.MaxTextWidth;
                ok.Location = new Point(right - ok.Width, y);
                f.Controls.Add(ok);

                f.AcceptButton = ok;
                f.CancelButton = ok;
                f.ClientSize = new Size(right + PromptLayout.Pad, y + ok.Height + 16);
                f.ShowDialog();
            }
        }
    }

    // 提示框排版：按字体实测尺寸，避免高 DPI 下按钮空白 / 文字被裁
    internal static class PromptLayout
    {
        private static bool? _darkCache;
        private static bool? _forced;

        public static void SetDark(bool dark) { _forced = dark; }

        public static bool Dark
        {
            get
            {
                if (_forced != null) return _forced.Value;
                if (_darkCache == null)
                {
                    try { _darkCache = Config.Load().DarkMode; }
                    catch { _darkCache = false; }
                }
                return _darkCache.Value;
            }
        }

        public static Color Bg { get { return Dark ? Color.FromArgb(24, 24, 27) : SystemColors.Control; } }
        public static Color Text { get { return Dark ? Color.FromArgb(232, 223, 200) : Color.Black; } }

        public const int Pad = 16;
        public const int Gap = 10;
        public const int MaxTextWidth = 440;

        public static Form NewForm(string title, Size client)
        {
            Form f = new Form();
            f.Text = title;
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.StartPosition = FormStartPosition.CenterScreen;
            f.MaximizeBox = false;
            f.MinimizeBox = false;
            f.ShowInTaskbar = false;
            f.AutoScaleMode = AutoScaleMode.None;
            f.Font = SystemFonts.MessageBoxFont;
            f.ClientSize = client;
            f.BackColor = Bg;
            f.ForeColor = Text;
            return f;
        }

        public static Label MakeLabel(Form f, string text, int x, int y, int width)
        {
            Label l = new Label();
            l.Font = f.Font;
            l.Text = text;
            l.AutoSize = false;
            l.ForeColor = Text;
            l.BackColor = Color.Transparent;

            Size need = TextRenderer.MeasureText(text, f.Font, new Size(width, 4000),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);

            l.SetBounds(x, y, width, Math.Max(need.Height + 6, f.Font.Height + 6));
            return l;
        }

        public static Button MakeButton(Form f, string text, DialogResult result)
        {
            Button b = new Button();
            b.Font = f.Font;
            b.Text = text;
            b.DialogResult = result;
            b.AutoSize = false;
            if (Dark)
            {
                b.FlatStyle = FlatStyle.Flat;
                b.BackColor = Color.FromArgb(38, 38, 44);
                b.ForeColor = Text;
                b.FlatAppearance.BorderColor = Color.FromArgb(72, 72, 80);
            }

            Size need = TextRenderer.MeasureText(text, f.Font);
            b.Size = new Size(Math.Max(96, need.Width + 36), Math.Max(30, need.Height + 14));
            return b;
        }
    }

    // 保存前的“开机只检查一次、90 秒后退出”提示
    // 底部提示：不抢焦点、几秒后自动消失
    internal sealed class Toast : Form
    {
        private static Toast _current;
        private readonly System.Windows.Forms.Timer _close = new System.Windows.Forms.Timer();

        private Toast(string text, bool left)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = false;
            BackColor = Color.FromArgb(30, 30, 30);
            Opacity = 0.96;
            Font = new Font("Microsoft YaHei UI", 9F);

            Label lbl = new Label();
            lbl.Text = text;
            lbl.ForeColor = Color.White;
            lbl.Font = new Font("Microsoft YaHei UI", 9F);

            Size need = TextRenderer.MeasureText(text, lbl.Font, new Size(312, 4000),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            int textHeight = Math.Max(need.Height + 8, 56);

            Size = new Size(340, textHeight + 20);
            lbl.SetBounds(14, 10, 312, textHeight);
            Controls.Add(lbl);

            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Location = left
                ? new Point(wa.Left + 12, wa.Bottom - Height - 12)
                : new Point(wa.Right - Width - 12, wa.Bottom - Height - 12);

            _close.Interval = 6000;
            _close.Tick += delegate { Close(); };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE
                cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW
                cp.ExStyle |= 0x02000000;   // WS_EX_COMPOSITED：整窗合成后再显示，避免闪
                return cp;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _close.Start();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _close.Stop();
            _close.Dispose();
            if (ReferenceEquals(_current, this)) _current = null;
            base.OnFormClosed(e);
        }

        public static void ShowToast(string text)
        {
            ShowToast(text, 6000, false);
        }

        public static void ShowToast(string text, int ms, bool left)
        {
            try
            {
                if (string.IsNullOrEmpty(text)) return;
                if (_current != null && !_current.IsDisposed)
                {
                    try { _current.Close(); } catch { }
                }

                Toast t = new Toast(text, left);
                if (ms > 0) t._close.Interval = ms;
                _current = t;
                t.Show();
            }
            catch { }
        }
    }

    internal static class IconFactory
    {
        // 优先用 exe 旁边的 QQCacheTray.ico（想换图标直接替换文件，不必重新编译）
        public static Icon Create(int size)
        {
            try
            {
                string dir = Path.GetDirectoryName(Application.ExecutablePath);
                string ico = Path.Combine(dir, "QQCacheTray.ico");
                if (File.Exists(ico)) return new Icon(ico, size, size);
            }
            catch { }

            try
            {
                Icon embedded = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (embedded != null) return embedded;
            }
            catch { }

            return CreateFallback();
        }

        private static Icon CreateFallback()
        {
            Bitmap bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                using (LinearGradientBrush b = new LinearGradientBrush(
                    new Rectangle(0, 0, 32, 32), Color.FromArgb(70, 150, 255), Color.FromArgb(0, 95, 200), 45f))
                {
                    g.FillEllipse(b, 1, 1, 30, 30);
                }

                using (Font f = new Font("Microsoft YaHei UI", 15F, FontStyle.Bold, GraphicsUnit.Pixel))
                using (SolidBrush w = new SolidBrush(Color.White))
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString("清", f, w, new RectangleF(0, 1, 32, 32), sf);
                }
            }

            IntPtr h = bmp.GetHicon();
            return Icon.FromHandle(h);
        }
    }
}

