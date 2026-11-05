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
[assembly: System.Reflection.AssemblyDescription("定时清理 QQ 本地聊天缓存")]
[assembly: System.Reflection.AssemblyVersion("1.3.5.0")]

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
                if (cfg.Enabled && cfg.LimitEnabled) { Cleaner.EnforceLimit(cfg, false, Logger.Log); code++; }
                if (cfg.WxEnabled && cfg.WxLimitEnabled) { Cleaner.EnforceLimit(cfg, true, Logger.Log); code++; }
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

            bool dryRun = false;
            bool selfTest = false;
            bool diag = false;
            bool cleanOnce = false;
            bool bootMode = false;
            bool settingsMode = false;
            bool forceUpdate = false;
            bool updateNow = false;
            bool limitNow = false;
            foreach (string a in args)
            {
                if (string.Equals(a, "--boot", StringComparison.OrdinalIgnoreCase)) bootMode = true;
                if (string.Equals(a, "--dry-run", StringComparison.OrdinalIgnoreCase)) dryRun = true;
                if (string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)) selfTest = true;
                if (string.Equals(a, "--clean", StringComparison.OrdinalIgnoreCase)) cleanOnce = true;
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

            if (cleanOnce)
            {
                Environment.ExitCode = RunCleanOnce();
                return;
            }

            // 开机自启：两个应用都没开“定时自动清理”时，只检查一次日期，没到期就 90 秒后退出
            Config bootCfg = Config.Load();
            bool needResident = (bootCfg.Enabled && bootCfg.ScheduleOn)
                             || (bootCfg.WxEnabled && bootCfg.WxScheduleOn);

            if (bootMode && !needResident)
            {
                Environment.ExitCode = RunBootCheck();
                return;
            }

            if (bootMode)
            {
                Logger.Log(string.Format("开机启动：自动清理已开启（QQ={0}，微信={1}，上限模式={2}），保持常驻",
                    bootCfg.Enabled && bootCfg.AutoCleanEnabled,
                    bootCfg.WxEnabled && bootCfg.WxAutoCleanEnabled,
                    (bootCfg.Enabled && bootCfg.LimitEnabled) || (bootCfg.WxEnabled && bootCfg.WxLimitEnabled)));
            }

            if (selfTest)
            {
                Environment.ExitCode = RunSelfTest();
                return;
            }

            if (dryRun)
            {
                Environment.ExitCode = RunHeadlessDryRun();
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

        // 开机检查模式（--boot）：定时清理关闭时使用
        private static int RunBootCheck()
        {
            try
            {
                Config cfg = Config.Load();
                Logger.Log("开机检查：定时清理未开启，只检查是否到期");

                bool did = false;
                string bootMsg = "";

                if (cfg.Enabled && cfg.LimitEnabled)
                {
                    Logger.Log("开机检查：QQ 容量上限");
                    CleanResult lq = Cleaner.EnforceLimit(cfg, false, Logger.Log);
                    if (lq != null && lq.TotalDeleted > 0)
                        bootMsg += string.Format("QQ：已清理 {0} 个文件，释放 {1}\n", lq.TotalDeleted, Cleaner.FormatSize(lq.TotalFreed));
                    did = true;
                }

                if (cfg.WxEnabled && cfg.WxLimitEnabled)
                {
                    Logger.Log("开机检查：微信容量上限");
                    CleanResult lw = Cleaner.EnforceLimit(cfg, true, Logger.Log);
                    if (lw != null && lw.TotalDeleted > 0)
                        bootMsg += string.Format("微信：已清理 {0} 个文件，释放 {1}\n", lw.TotalDeleted, Cleaner.FormatSize(lw.TotalFreed));
                    did = true;
                }

                if (cfg.LimitEnabled || cfg.WxLimitEnabled)
                {
                    if (bootMsg.Length > 0) Program.PendingToast = bootMsg.TrimEnd('\n');
                    return 0;
                }

                if (Cleaner.IsDueNow(cfg))
                {
                    Logger.Log("QQ 已到期，清理一次");
                    CleanResult r = Cleaner.Run(cfg, false, Logger.Log);
                    if (string.IsNullOrEmpty(r.Error)) cfg.SaveLastRun(DateTime.Now);
                    did = true;
                }

                if (Cleaner.IsWxDueNow(cfg))
                {
                    Logger.Log("微信已到期，清理一次");
                    CleanResult r2 = Cleaner.RunWx(cfg, false, Logger.Log);
                    if (string.IsNullOrEmpty(r2.Error)) cfg.SaveWxLastRun(DateTime.Now);
                    did = true;
                }

                if (did) return 0;

                // 未到期：在 90 秒窗口里顺带查一次更新；弹窗期间不退出，
                // 选了“立即更新”就先完成替换再退出，不会被 90 秒打断
                DateTime until = DateTime.Now.AddSeconds(90);

                if (cfg.UpdateCheck && (DateTime.Now - cfg.LastUpdateCheck).TotalHours >= 12)
                {
                    try
                    {
                        string url = string.IsNullOrEmpty(cfg.UpdateUrl)
                            ? UpdateChecker.DefaultManifestUrl
                            : cfg.UpdateUrl;

                        string usedUrl;
                        string lastErr;
                        UpdateChecker.Info info = string.IsNullOrEmpty(cfg.UpdateUrl)
                            ? UpdateChecker.FetchAny(out usedUrl, out lastErr)
                            : UpdateChecker.Fetch(url);
                        cfg.LastUpdateCheck = DateTime.Now;
                        cfg.Save();

                        if (info != null && UpdateChecker.IsNewer(info.Version))
                        {
                            Logger.Log("开机检查发现新版本：" + info.Version);

                            // 启动 5 秒后再提示，别和开机动作抢注意力
                            Thread.Sleep(5000);
                            Toast.ShowToast("「QQ 缓存管家」发现新版本 " + info.Version +
                                            "，打开程序可在设置里更新", 9000, true);
                            Logger.Log("已弹出更新通知（开机模式，仅提示）");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log("开机检查更新失败：" + ex.Message);
                    }
                }

                Logger.Log("未到期，90 秒后自动退出");
                while (DateTime.Now < until)
                {
                    // 泵消息，更新通知条才能正常显示并被计时器关掉
                    Application.DoEvents();
                    Thread.Sleep(100);
                }
                Logger.Log("已退出");
                return 0;
            }
            catch (Exception ex)
            {
                Logger.Log("开机检查异常：" + ex);
                return 0;
            }
        }

        // 后台清理模式（--clean）：手动/计划任务用，无界面、跑完就退
        private static int RunCleanOnce()
        {
            try
            {
                Config cfg = Config.Load();
                Logger.Log("后台清理任务启动：数据目录 " + Config.Dir);

                int code = 0;

                if (cfg.Enabled && Cleaner.IsDueNow(cfg))
                {
                    CleanResult r = Cleaner.Run(cfg, false, Logger.Log);
                    if (string.IsNullOrEmpty(r.Error)) cfg.SaveLastRun(DateTime.Now);
                    else if (!r.Error.StartsWith("未找到")) code = 1;
                }
                else
                {
                    Logger.Log("QQ：未开启或未到期，跳过");
                }

                if (cfg.WxEnabled && Cleaner.IsWxDueNow(cfg))
                {
                    CleanResult r2 = Cleaner.RunWx(cfg, false, Logger.Log);
                    if (string.IsNullOrEmpty(r2.Error)) cfg.SaveWxLastRun(DateTime.Now);
                    else if (!r2.Error.StartsWith("未找到")) code = 1;
                }
                else
                {
                    Logger.Log("微信：未开启或未到期，跳过");
                }

                return code;
            }
            catch (Exception ex)
            {
                Logger.Log("后台清理异常：" + ex);
                return 1;
            }
        }

        // 诊断：把关键路径和异常写到 %TEMP%，用于排查“没图标、没数据目录”
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

                Config dueTest = new Config();
                dueTest.Enabled = true;
                dueTest.IntervalDays = 7;
                dueTest.LastRun = DateTime.Now.AddDays(-8);
                bool dueOld = Cleaner.IsDue(dueTest);
                dueTest.LastRun = DateTime.Now.AddDays(-1);
                bool dueNew = Cleaner.IsDue(dueTest);
                dueTest.Enabled = false;
                dueTest.LastRun = DateTime.Now.AddDays(-8);
                bool dueOff = Cleaner.IsDue(dueTest);
                sb.AppendLine(string.Format("闸门测试: 8天前={0} 1天前={1} 已关闭={2} -> {3}",
                    dueOld, dueNew, dueOff, (dueOld && !dueNew && !dueOff) ? "PASS" : "FAIL"));

                // 往返测试会写 config.ini：先备份真实配置，测完立即还原，避免自检把用户设置清掉
                string cfgBackup = File.Exists(Config.ConfigPath) ? File.ReadAllText(Config.ConfigPath) : null;

                Config round = new Config();
                round.Enabled = false;
                round.AutoCleanEnabled = true;
                round.CleanHour = 4;
                round.CleanMinute = 30;
                round.IntervalDays = 3;
                round.KeepDays = 5;
                round.Emoji = false;
                round.Save();
                Config back = Config.Load();
                bool ok = back.Enabled == false && back.AutoCleanEnabled == true
                          && back.CleanHour == 4 && back.CleanMinute == 30
                          && back.IntervalDays == 3 && back.KeepDays == 5 && back.Emoji == false;

                try
                {
                    if (cfgBackup != null) File.WriteAllText(Config.ConfigPath, cfgBackup);
                    else File.Delete(Config.ConfigPath);
                }
                catch { }
                sb.AppendLine();
                string c1 = Cleaner.CountdownText(true, true, DateTime.Today, 7, 3, 0);
                string c2 = Cleaner.CountdownText(true, true, DateTime.Now.AddDays(-30), 7, 3, 0);
                string c3 = Cleaner.CountdownText(true, false, DateTime.Today, 7, 3, 0);
                string c4 = Cleaner.CountdownText(false, true, DateTime.Today, 7, 3, 0);
                bool cOk = c1.Contains("天") && c2.Contains("已到期")
                           && c3.Contains("下次清理日期") && c3.Contains("开机自启") && c4.Contains("已关闭");
                DateTime now = DateTime.Now;
                DateTime n0 = Cleaner.NextDue(now.AddDays(-3), 0, 3, 0);          // 昨天清过 → 今天 03:00
                DateTime n1 = Cleaner.NextDue(now.Date.AddHours(13), 0, 3, 0);    // 今天 13:00 清过 → 明天 03:00
                DateTime n2 = Cleaner.NextDue(now.Date.AddHours(0), 7, 3, 0);     // 七天
                DateTime n3 = Cleaner.NextDue(now.Date.AddHours(0), 0, 23, 59);   // 今天 23:59（还没到）
                bool nOk = n0.Date == DateTime.Today
                           && n1 == DateTime.Today.AddDays(1).AddHours(3)
                           && n2 == DateTime.Today.AddDays(7).AddHours(3)
                           && n3 == DateTime.Today.AddHours(23).AddMinutes(59);
                sb.AppendLine("0 天测试: " + (nOk ? "PASS" : "FAIL")
                              + " 今天=" + n0.ToString("MM-dd HH:mm")
                              + " 今天已清=" + n1.ToString("MM-dd HH:mm")
                              + " 七天=" + n2.ToString("MM-dd HH:mm")
                              + " 今天稍后=" + n3.ToString("MM-dd HH:mm"));

                Config r1 = new Config(); r1.Enabled = true; r1.ScheduleOn = false;
                r1.WxEnabled = false; r1.WxScheduleOn = false;
                Config r2 = new Config(); r2.Enabled = true; r2.ScheduleOn = false;
                r2.WxEnabled = true; r2.WxScheduleOn = true;
                Config r3 = new Config(); r3.Enabled = false; r3.ScheduleOn = true;
                r3.WxEnabled = true; r3.WxScheduleOn = false;
                bool rOk = !Cleaner.WillStayResident(r1) && Cleaner.WillStayResident(r2) && !Cleaner.WillStayResident(r3);
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
                              + " 微信定时=" + Cleaner.WillStayResident(r2)
                              + " 只有次开关=" + Cleaner.WillStayResident(r3));

                sb.AppendLine("倒计时测试: " + (cOk ? "PASS" : "FAIL"));
                sb.AppendLine("  " + c1 + " / " + c2 + " / " + c3 + " / " + c4);

                sb.AppendLine("配置往返测试: " + (ok ? "PASS" : "FAIL"));
                sb.AppendLine("last-clean.json 内容: " + ReadJsonSafe());

                using (Form d1 = BootModePrompt.Build("自检提示：当前配置不会常驻后台。"))
                using (Form d2 = SchedulePrompt.Build("QQ", DateTime.Today.AddHours(3)))
                {
                    bool dOk = true;
                    foreach (Form d in new Form[] { d1, d2 })
                    {
                        foreach (Control c in d.Controls)
                        {
                            if (c.Right > d.ClientSize.Width - 8 || c.Bottom > d.ClientSize.Height - 8)
                            {
                                dOk = false;
                                sb.AppendLine("DIALOG_OVERFLOW: " + c.Text + " 右下(" + c.Right + "," + c.Bottom +
                                              ") 超出 " + d.ClientSize.Width + "x" + d.ClientSize.Height);
                            }
                        }
                    }
                    sb.AppendLine("对话框布局: " + (dOk ? "PASS" : "FAIL")
                                  + " 开机提示=" + d1.ClientSize.Width + "x" + d1.ClientSize.Height
                                  + " 过期提示=" + d2.ClientSize.Width + "x" + d2.ClientSize.Height);
                }

                UpdateChecker.Info ui2 = new UpdateChecker.Info();
                ui2.Version = "1.2.3"; ui2.Url = "https://example.com/a.exe"; ui2.Sha256 = "abc"; ui2.Notes = "n";
                using (Form d3 = UpdatePrompt.Build("1.2.2.0", ui2, true))
                {
                    foreach (Control c in d3.Controls)
                        sb.AppendLine("UPD_CTRL: " + c.GetType().Name + " text=[" + c.Text + "] bounds=" + c.Bounds);
                }

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
                    else if (line.IndexOf(" OVERLAP", StringComparison.Ordinal) >= 0) failCount++;
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
                    if (kids[i].Bounds.IntersectsWith(kids[j].Bounds))
                    {
                        if (!kids[i].Visible || !kids[j].Visible) continue;
                        sb.AppendLine(string.Format("OVERLAP: \"{0}\" <-> \"{1}\"", kids[i].Text, kids[j].Text));
                    }
                }
            }
            foreach (Control c in parent.Controls) CheckOverlap(sb, c, depth + 1);
        }

        // 命令行预演：不弹窗、不删除，把结果写到数据目录，便于验证引擎
        private static int RunHeadlessDryRun()
        {
            try
            {
                Config cfg = Config.Load();
                CleanResult r = Cleaner.Run(cfg, true, Logger.Log);

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("QQ 缓存管家 预演报告 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("保留天数：" + cfg.KeepDays);
                sb.AppendLine("数据目录：" + Config.Dir);
                foreach (CleanStat st in r.Stats)
                {
                    sb.AppendLine(string.Format("{0} / {1}: 扫描 {2}, 将删除 {3}, 释放 {4}",
                        st.Account, st.Category, st.Scanned, st.Deleted, Cleaner.FormatSize(st.Freed)));
                }
                sb.AppendLine(string.Format("合计：{0} 个文件，{1}", r.TotalDeleted, Cleaner.FormatSize(r.TotalFreed)));
                if (!string.IsNullOrEmpty(r.Error)) sb.AppendLine("错误：" + r.Error);

                string path = Path.Combine(Config.Dir, "dryrun-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
                Console.WriteLine(sb.ToString());
                return 0;
            }
            catch (Exception ex)
            {
                try
                {
                    File.WriteAllText(Path.Combine(Config.Dir, "dryrun-error.txt"), ex.ToString(), Encoding.UTF8);
                }
                catch { }
                return 1;
            }
        }
    }

    // ---------------------------------------------------------------- 配置

    internal sealed class Config
    {
        public bool Enabled = true;
        public bool AutoCleanEnabled = false;
        public int CleanHour = 3;
        public int CleanMinute = 0;
        public int IntervalDays = 7;
        public int KeepDays = 0;
        public bool Pic = true;
        public bool Video = true;
        public bool Emoji = true;
        public bool Ptt = true;
        public bool IncludeSystemEmoji = false;
        public bool IncludePersonalEmoji = false;
        public bool AutostartEnabled = false;
        public bool UpdateCheck = true;
        public bool DarkMode = false;
        public string UpdateUrl = "";
        public DateTime LastUpdateCheck = DateTime.MinValue;
        public string QqRoot = "";
        public DateTime LastRun = DateTime.MinValue;

        // 微信
        public bool WxEnabled = true;
        public bool WxAutoCleanEnabled = false;
        public int WxCleanHour = 3;
        public int WxCleanMinute = 0;
        public int WxIntervalDays = 7;
        public int WxKeepDays = 0;
        public bool WxPic = true;
        public bool WxVideo = true;
        public bool WxEmoji = true;

        // 容量上限模式（与定时清理互斥）
        public bool ScheduleOn = true;
        public bool LimitEnabled = false;
        public int LimitMB = 2048;
        public bool LimitDeleteAll = false;
        public bool WxScheduleOn = true;
        public bool WxLimitEnabled = false;
        public int WxLimitMB = 2048;
        public bool WxLimitDeleteAll = false;
        public bool WxVoice = true;
        public string WxRoot = "";
        public DateTime WxLastRun = DateTime.MinValue;

        // 运行期标记：保存设置后要不要立刻补一次清理（不写入配置文件）
        public bool PendingCleanQq = false;
        public bool PendingCleanWx = false;

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
                            case "auto_clean": c.AutoCleanEnabled = ParseBool(val, false); break;
                            case "clean_hour": c.CleanHour = ParseInt(val, 3); break;
                            case "clean_minute": c.CleanMinute = ParseInt(val, 0); break;
                            case "interval_days": c.IntervalDays = ParseInt(val, 7); break;
                            case "keep_days": c.KeepDays = ParseInt(val, 0); break;
                            case "pic": c.Pic = ParseBool(val, true); break;
                            case "video": c.Video = ParseBool(val, true); break;
                            case "emoji": c.Emoji = ParseBool(val, true); break;
                            case "ptt": c.Ptt = ParseBool(val, true); break;
                            case "include_system_emoji": c.IncludeSystemEmoji = ParseBool(val, false); break;
                            case "include_personal_emoji": c.IncludePersonalEmoji = ParseBool(val, false); break;
                            case "autostart": c.AutostartEnabled = ParseBool(val, false); break;
                            case "update_check": c.UpdateCheck = ParseBool(val, true); break;
                            case "dark_mode": c.DarkMode = ParseBool(val, false); break;
                            case "update_url": c.UpdateUrl = val; break;
                            case "last_update_check": c.LastUpdateCheck = ParseDateTime(val); break;
                            case "qq_root": c.QqRoot = val; break;
                            case "wx_enabled": c.WxEnabled = ParseBool(val, true); break;
                            case "wx_auto_clean": c.WxAutoCleanEnabled = ParseBool(val, false); break;
                            case "wx_clean_hour": c.WxCleanHour = ParseInt(val, 3); break;
                            case "wx_clean_minute": c.WxCleanMinute = ParseInt(val, 0); break;
                            case "wx_interval_days": c.WxIntervalDays = ParseInt(val, 7); break;
                            case "wx_keep_days": c.WxKeepDays = ParseInt(val, 0); break;
                            case "wx_pic": c.WxPic = ParseBool(val, true); break;
                            case "wx_video": c.WxVideo = ParseBool(val, true); break;
                            case "wx_emoji": c.WxEmoji = ParseBool(val, true); break;
                            case "schedule_on": c.ScheduleOn = ParseBool(val, true); break;
                            case "wx_schedule_on": c.WxScheduleOn = ParseBool(val, true); break;
                            case "limit_enabled": c.LimitEnabled = ParseBool(val, false); break;
                            case "limit_mb": c.LimitMB = ParseInt(val, 2048); break;
                            case "limit_delete_all": c.LimitDeleteAll = ParseBool(val, false); break;
                            case "wx_limit_enabled": c.WxLimitEnabled = ParseBool(val, false); break;
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

            // 两种模式互斥：容量上限优先
            if (c.LimitEnabled) c.AutoCleanEnabled = false;
            if (c.WxLimitEnabled) c.WxAutoCleanEnabled = false;

            if (c.IntervalDays < 0) c.IntervalDays = 0;
            if (c.WxIntervalDays < 0) c.WxIntervalDays = 0;
            if (c.KeepDays < 0) c.KeepDays = 0;
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
                sb.AppendLine("auto_clean=" + (AutoCleanEnabled ? "1" : "0"));
                sb.AppendLine("clean_hour=" + CleanHour);
                sb.AppendLine("clean_minute=" + CleanMinute);
                sb.AppendLine("interval_days=" + IntervalDays);
                sb.AppendLine("keep_days=" + KeepDays);
                sb.AppendLine("pic=" + (Pic ? "1" : "0"));
                sb.AppendLine("video=" + (Video ? "1" : "0"));
                sb.AppendLine("emoji=" + (Emoji ? "1" : "0"));
                sb.AppendLine("ptt=" + (Ptt ? "1" : "0"));
                sb.AppendLine("include_system_emoji=" + (IncludeSystemEmoji ? "1" : "0"));
                sb.AppendLine("include_personal_emoji=" + (IncludePersonalEmoji ? "1" : "0"));
                sb.AppendLine("autostart=" + (AutostartEnabled ? "1" : "0"));
                sb.AppendLine("update_check=" + (UpdateCheck ? "1" : "0"));
                sb.AppendLine("dark_mode=" + (DarkMode ? "1" : "0"));
                sb.AppendLine("update_url=" + (UpdateUrl ?? ""));
                sb.AppendLine("last_update_check=" + (LastUpdateCheck == DateTime.MinValue
                    ? ""
                    : LastUpdateCheck.ToString("yyyy-MM-dd HH:mm:ss")));
                sb.AppendLine("qq_root=" + (QqRoot ?? ""));
                sb.AppendLine("wx_enabled=" + (WxEnabled ? "1" : "0"));
                sb.AppendLine("wx_auto_clean=" + (WxAutoCleanEnabled ? "1" : "0"));
                sb.AppendLine("wx_clean_hour=" + WxCleanHour);
                sb.AppendLine("wx_clean_minute=" + WxCleanMinute);
                sb.AppendLine("wx_interval_days=" + WxIntervalDays);
                sb.AppendLine("wx_keep_days=" + WxKeepDays);
                sb.AppendLine("wx_pic=" + (WxPic ? "1" : "0"));
                sb.AppendLine("wx_video=" + (WxVideo ? "1" : "0"));
                sb.AppendLine("wx_emoji=" + (WxEmoji ? "1" : "0"));
                sb.AppendLine("schedule_on=" + (ScheduleOn ? "1" : "0"));
                sb.AppendLine("wx_schedule_on=" + (WxScheduleOn ? "1" : "0"));
                sb.AppendLine("limit_enabled=" + (LimitEnabled ? "1" : "0"));
                sb.AppendLine("limit_mb=" + LimitMB);
                sb.AppendLine("limit_delete_all=" + (LimitDeleteAll ? "1" : "0"));
                sb.AppendLine("wx_limit_enabled=" + (WxLimitEnabled ? "1" : "0"));
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

    internal sealed class CleanStat
    {
        public string Account = "";
        public string Category = "";
        public int Scanned;
        public int Deleted;
        public int Failed;
        public int Skipped;
        public long Freed;
    }

    internal sealed class CleanResult
    {
        public List<CleanStat> Stats = new List<CleanStat>();
        public int TotalDeleted;
        public int TotalFailed;
        public long TotalFreed;
        public bool DryRun;
        public string Error = "";
        public TimeSpan Elapsed;
    }

    internal static class Cleaner
    {
        // 两种触发方式共用的闸门：距上次清理满 N 天才行动，避免重复清理
        public static bool IsDue(Config cfg)
        {
            if (!cfg.Enabled) return false;
            if (cfg.LastRun == DateTime.MinValue) return true;
            return DateTime.Now - cfg.LastRun >= TimeSpan.FromDays(cfg.IntervalDays);
        }

        // 到期时刻 = 上次清理那天 + N 天，再对准设定的 时:分
        // 间隔为 0 表示“当天”：今天就按这个时:分清理；清完顺延到明天，避免每分钟重复清
        public static DateTime NextDue(DateTime last, int intervalDays, int hour, int minute)
        {
            int interval = Math.Max(0, intervalDays);
            DateTime baseDate = interval == 0
                ? DateTime.Today
                : (last == DateTime.MinValue ? DateTime.Today : last.Date);

            DateTime due = baseDate.AddDays(interval)
                                   .AddHours(Math.Max(0, Math.Min(23, hour)))
                                   .AddMinutes(Math.Max(0, Math.Min(59, minute)));

            // 这个时刻已经在上次清理之前发生过，才跳到下一轮
            if (last != DateTime.MinValue && due <= last)
                due = due.AddDays(Math.Max(1, interval));

            return due;
        }

        public static DateTime DueAt(Config cfg)
        {
            return NextDue(cfg.LastRun, cfg.IntervalDays, cfg.CleanHour, cfg.CleanMinute);
        }

        // 定时模式用这个：必须真的到了那一天的设定时刻
        // 保存后程序会不会常驻：任一应用“总开关 + 定时”同时开启
        public static bool WillStayResident(Config c)
        {
            return (c.Enabled && c.ScheduleOn) || (c.WxEnabled && c.WxScheduleOn);
        }

        public static bool IsDueNow(Config cfg)
        {
            if (!cfg.Enabled) return false;
            return DateTime.Now >= DueAt(cfg);
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

        // 超过上限就清理：删旧文件（保留到 90% 以下）或全删
        public static CleanResult EnforceLimit(Config cfg, bool wx, Action<string> log)
        {
            CleanResult result = new CleanResult();
            Action<string> sink = log ?? Silently;

            int limitMB = wx ? cfg.WxLimitMB : cfg.LimitMB;
            bool deleteAll = wx ? cfg.WxLimitDeleteAll : cfg.LimitDeleteAll;
            long limit = Math.Max(100, limitMB) * 1024L * 1024L;

            List<CacheFile> files = CollectCacheFiles(cfg, wx, sink);
            long total = 0;
            foreach (CacheFile f in files) total += f.Length;

            string tag = wx ? "微信" : "QQ";
            if (total <= limit)
            {
                sink(string.Format("{0} 缓存 {1}，未超过上限 {2}", tag, FormatSize(total), FormatSize(limit)));
                return result;
            }

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

        public static CleanResult Run(Config cfg, bool dryRun, Action<string> log)
        {
            Stopwatch sw = Stopwatch.StartNew();
            CleanResult result = new CleanResult();
            result.DryRun = dryRun;

            List<string> dirs = GetNtDataDirs(cfg, log);
            if (dirs.Count == 0)
            {
                result.Error = "未找到 QQ 数据目录（Tencent Files\\*\\nt_qq\\nt_data）";
                log(result.Error);
                return result;
            }

            DateTime cutoff = DateTime.Today.AddDays(-cfg.KeepDays);
            List<string> cats = cfg.Categories();
            log(string.Format("开始{0}：保留 {1} 之后，目录 {2}",
                dryRun ? "预演" : "清理", cutoff.ToString("yyyy-MM-dd HH:mm"), string.Join(",", cats.ToArray())));

            int probeFail = 0;
            int probeOk = 0;

            foreach (string ntData in dirs)
            {
                string account = "";
                try
                {
                    string ntqq = Path.GetDirectoryName(ntData);
                    account = Path.GetFileName(Path.GetDirectoryName(ntqq));
                }
                catch { }

                foreach (string cat in cats)
                {
                    string catPath = Path.Combine(ntData, cat);
                    if (!Directory.Exists(catPath)) continue;

                    if (!dryRun)
                    {
                        if (ProbeWritable(catPath, log)) probeOk++;
                        else probeFail++;
                    }

                    CleanStat st = new CleanStat();
                    st.Account = account;
                    st.Category = cat;

                    List<FileInfo> files = new List<FileInfo>();
                    Walk(catPath, files, log, 0);

                    foreach (FileInfo fi in files)
                    {
                        st.Scanned++;
                        string f = fi.FullName;
                        string rel = f.Substring(catPath.Length).TrimStart('\\');
                        if (IsExcluded(rel, cfg, cat)) { st.Skipped++; continue; }

                        long len;
                        DateTime mtime;
                        try
                        {
                            len = fi.Length;
                            mtime = fi.LastWriteTime;
                        }
                        catch { continue; }

                        if (mtime >= cutoff) continue;

                        if (dryRun)
                        {
                            st.Deleted++;
                            st.Freed += len;
                            continue;
                        }

                        try
                        {
                            File.Delete(f);
                            st.Deleted++;
                            st.Freed += len;
                        }
                        catch (Exception ex)
                        {
                            st.Failed++;
                            if (st.Failed <= 3) log("删除失败：" + f + " -> " + ex.Message);
                        }
                    }

                    if (!dryRun && st.Deleted > 0) RemoveEmptyDirs(catPath, cfg, cat);

                    result.Stats.Add(st);
                    result.TotalDeleted += st.Deleted;
                    result.TotalFailed += st.Failed;
                    result.TotalFreed += st.Freed;

                    log(string.Format("{0} / {1}：扫描 {2}，删除 {3}，失败 {4}，释放 {5}",
                        st.Account, st.Category, st.Scanned, st.Deleted, st.Failed, FormatSize(st.Freed)));
                }
            }

            sw.Stop();
            result.Elapsed = sw.Elapsed;

            if (!dryRun && (probeOk + probeFail) > 0)
            {
                log(string.Format("写入探测：{0} 个目录可写，{1} 个不可写", probeOk, probeFail));
            }

            log(string.Format("完成：删除 {0} 个文件，释放 {1}，失败 {2}，耗时 {3:N1} 秒",
                result.TotalDeleted, FormatSize(result.TotalFreed), result.TotalFailed, sw.Elapsed.TotalSeconds));
            return result;
        }

        // 在缓存目录里真的建一个文件再删掉，用来确认“能删”而不是“能看见”
        private static bool ProbeWritable(string dir, Action<string> log)
        {
            try
            {
                string probe = Path.Combine(dir, ".qqcachetray-write-probe");
                File.WriteAllText(probe, "1");
                File.Delete(probe);
                return true;
            }
            catch (Exception ex)
            {
                log("写入探测失败：" + dir + " -> " + ex.Message);
                return false;
            }
        }


        // ---------------------------------------------------------------- 微信

        public static DateTime WxDueAt(Config cfg)
        {
            return NextDue(cfg.WxLastRun, cfg.WxIntervalDays, cfg.WxCleanHour, cfg.WxCleanMinute);
        }

        public static bool IsWxDueNow(Config cfg)
        {
            if (!cfg.WxEnabled) return false;
            return DateTime.Now >= WxDueAt(cfg);
        }

        private static bool WxCatEnabled(Config cfg, string cat)
        {
            if (cat == "Pic") return cfg.WxPic;
            if (cat == "Video") return cfg.WxVideo;
            if (cat == "Emoji") return cfg.WxEmoji;
            if (cat == "Voice") return cfg.WxVoice;
            return false;
        }

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

        public static CleanResult RunWx(Config cfg, bool dryRun, Action<string> log)
        {
            Stopwatch sw = Stopwatch.StartNew();
            CleanResult result = new CleanResult();
            result.DryRun = dryRun;

            List<KeyValuePair<string, string>> dirs = GetWxCategoryDirs(cfg, log);
            if (dirs.Count == 0)
            {
                result.Error = "未找到微信数据目录（WeChat Files\\*\\FileStorage 或 xwechat_files）";
                log(result.Error);
                return result;
            }

            DateTime cutoff = DateTime.Today.AddDays(-cfg.WxKeepDays);
            log(string.Format("开始清理微信：保留 {0} 之后", cutoff.ToString("yyyy-MM-dd HH:mm")));

            int probeFail = 0, probeOk = 0;

            foreach (KeyValuePair<string, string> kv in dirs)
            {
                string[] parts = kv.Key.Split('|');
                string account = parts[0];
                string cat = parts[1];
                if (!WxCatEnabled(cfg, cat)) continue;

                string catPath = kv.Value;
                if (!dryRun)
                {
                    if (ProbeWritable(catPath, log)) probeOk++;
                    else probeFail++;
                }

                CleanStat st = new CleanStat();
                st.Account = account;
                st.Category = cat;

                List<FileInfo> files = new List<FileInfo>();
                Walk(catPath, files, log, 0);

                foreach (FileInfo fi in files)
                {
                    st.Scanned++;
                    string f = fi.FullName;
                    long len;
                    DateTime mtime;
                    try
                    {
                        len = fi.Length;
                        mtime = fi.LastWriteTime;
                    }
                    catch { continue; }

                    if (mtime >= cutoff) continue;

                    if (dryRun) { st.Deleted++; st.Freed += len; continue; }

                    try
                    {
                        File.Delete(f);
                        st.Deleted++;
                        st.Freed += len;
                    }
                    catch (Exception ex)
                    {
                        st.Failed++;
                        if (st.Failed <= 3) log("删除失败：" + f + " -> " + ex.Message);
                    }
                }

                if (!dryRun && st.Deleted > 0) RemoveEmptyDirs(catPath, cfg, "Wx");

                result.Stats.Add(st);
                result.TotalDeleted += st.Deleted;
                result.TotalFailed += st.Failed;
                result.TotalFreed += st.Freed;

                log(string.Format("{0} / {1}：扫描 {2}，删除 {3}，失败 {4}，释放 {5}",
                    st.Account, st.Category, st.Scanned, st.Deleted, st.Failed, FormatSize(st.Freed)));
            }

            sw.Stop();
            result.Elapsed = sw.Elapsed;
            if (!dryRun && (probeOk + probeFail) > 0)
                log(string.Format("写入探测：{0} 个目录可写，{1} 个不可写", probeOk, probeFail));

            log(string.Format("微信清理完成：删除 {0} 个文件，释放 {1}，失败 {2}，耗时 {3:N1} 秒",
                result.TotalDeleted, FormatSize(result.TotalFreed), result.TotalFailed, sw.Elapsed.TotalSeconds));
            return result;
        }

        public static long MeasureWx(Config cfg)
        {
            long total = 0;
            foreach (KeyValuePair<string, string> kv in GetWxCategoryDirs(cfg, delegate { }))
            {
                string cat = kv.Key.Split('|')[1];
                if (!WxCatEnabled(cfg, cat)) continue;
                total += Measure(kv.Value);
            }
            return total;
        }

        // 倒计时文案：直接给到期时刻
        public static string CountdownText(bool enabled, bool auto, DateTime due)
        {
            if (!enabled) return "下次清理日期：已关闭";

            if (!auto)
                return string.Format("下次清理日期：{0}（开机自启时检查）", due.ToString("yyyy-MM-dd"));

            TimeSpan left = due - DateTime.Now;
            if (left.TotalMinutes <= 0)
                return string.Format("距离下次清理：已到期，等待执行（{0}）", due.ToString("yyyy-MM-dd HH:mm"));

            int days = (int)left.TotalDays;
            string remain = days > 0
                ? string.Format("{0} 天 {1} 小时 {2} 分", days, left.Hours, left.Minutes)
                : string.Format("{0} 小时 {1} 分", left.Hours, left.Minutes);
            return string.Format("距离下次清理：{0}（{1}）", remain, due.ToString("yyyy-MM-dd HH:mm"));
        }

        // 倒计时文案（精确到分钟）；没开定时就显示“应该清理的日期”
        public static string CountdownText(bool enabled, bool auto, DateTime last,
                                           int intervalDays, int hour, int minute)
        {
            if (!enabled) return "下次清理日期：已关闭";

            DateTime baseDate = last == DateTime.MinValue ? DateTime.Today : last.Date;
            DateTime due = baseDate.AddDays(intervalDays)
                                   .AddHours(Math.Max(0, Math.Min(23, hour)))
                                   .AddMinutes(Math.Max(0, Math.Min(59, minute)));

            if (!auto)
                return string.Format("下次清理日期：{0}（开机自启时检查）",
                    due.ToString("yyyy-MM-dd"));

            TimeSpan left = due - DateTime.Now;
            if (left.TotalMinutes <= 0)
                return string.Format("距离下次清理：已到期，等待执行（{0}）", due.ToString("yyyy-MM-dd HH:mm"));

            int days = (int)left.TotalDays;
            string remain = days > 0
                ? string.Format("{0} 天 {1} 小时 {2} 分", days, left.Hours, left.Minutes)
                : string.Format("{0} 小时 {1} 分", left.Hours, left.Minutes);
            return string.Format("距离下次清理：{0}（{1}）", remain, due.ToString("yyyy-MM-dd HH:mm"));
        }

        // 给界面显示“自动检测到了什么”
        public static string DescribeTargets(Config cfg, bool wx)
        {
            List<string> paths = new List<string>();
            if (wx)
            {
                foreach (KeyValuePair<string, string> kv in GetWxCategoryDirs(cfg, delegate { }))
                {
                    if (!paths.Contains(kv.Value)) paths.Add(kv.Value);
                }
            }
            else
            {
                foreach (string d in GetNtDataDirs(cfg, delegate { })) paths.Add(d);
            }

            if (paths.Count == 0) return "自动检测：未找到，请手动选择目录";

            // 只显示账号名（路径倒数第三段），一行放得下
            string[] segs = paths[0].TrimEnd('\\').Split('\\');
            string tail = segs.Length >= 3 ? segs[segs.Length - 3] : segs[segs.Length - 1];
            if (tail.Length > 30) tail = "…" + tail.Substring(tail.Length - 30);

            return string.Format("自动检测：{0} 个目录 · {1}", paths.Count, tail);
        }

        public static long MeasureTotal(Config cfg)
        {
            long total = 0;
            foreach (string ntData in GetNtDataDirs(cfg, delegate { }))
            {
                foreach (string cat in cfg.Categories())
                {
                    string p = Path.Combine(ntData, cat);
                    if (Directory.Exists(p)) total += Measure(p);
                }
            }
            return total;
        }

        private static long Measure(string dir)
        {
            long sum = 0;
            List<FileInfo> files = new List<FileInfo>();
            Walk(dir, files, delegate { }, 0);
            foreach (FileInfo fi in files)
            {
                try { sum += fi.Length; }
                catch { }
            }
            return sum;
        }

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

        private static void RemoveEmptyDirs(string catPath, Config cfg, string cat)
        {
            List<string> all = new List<string>();
            try
            {
                CollectDirs(catPath, all, 0);
            }
            catch { return; }

            for (int i = all.Count - 1; i >= 0; i--)
            {
                string d = all[i];
                try
                {
                    string rel = d.Substring(catPath.Length).TrimStart('\\');
                    if (IsExcluded(rel, cfg, cat)) continue;
                    if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue;
                    if (Directory.GetFileSystemEntries(d).Length == 0) Directory.Delete(d, false);
                }
                catch { }
            }
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
        private readonly ToolStripMenuItem _miNext;
        private readonly ToolStripMenuItem _miClean;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly Form _marshal;
        private Config _cfg;
        private bool _busy;
        private bool _settingsOpen;
        private int _limitTick;
        private UpdateChecker.Info _pendingUpdate;
        private bool _promptOpen;

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

            _miNext = new ToolStripMenuItem("下次清理：-");
            _miNext.Enabled = false;

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
            _menu.Items.Add(_miNext);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_miClean);
            _menu.Items.Add(miCleanWx);
            _menu.Items.Add(miSettings);
            _menu.Items.Add(miUpdate);
            _menu.Items.Add(miLogs);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(miExit);

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
                AutoStart.Normalize();

                // 配置里开着自启就重写一遍启动脚本，保证里面记的 exe 路径是最新的
                if (_cfg.AutostartEnabled)
                {
                    string msg;
                    if (!AutoStart.Set(true, out msg)) Logger.Log("开机自启脚本写入失败：" + msg);
                }
            });

            // 首次运行不立刻删，从今天开始计时，避免一装上就猛清一次
            bool firstRun = _cfg.LastRun == DateTime.MinValue;
            if (firstRun)
            {
                _cfg.SaveLastRun(DateTime.Now);
                _cfg.Save();
                Logger.Log("首次运行：清理计时从今天开始，N 天后自动执行；需要立刻清理请右键「立即清理」");
            }

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
            if (_cfg.AutoCleanEnabled && Cleaner.IsDueNow(_cfg))
            {
                RunClean(false);
            }
            if (_cfg.WxAutoCleanEnabled && Cleaner.IsWxDueNow(_cfg))
            {
                RunClean(true);
            }

            // 每 10 分钟检查一次容量上限
            _limitTick++;
            if (_limitTick >= 10)
            {
                _limitTick = 0;
                try
                {
                    string limitMsg = "";
                    if (_cfg.Enabled && _cfg.LimitEnabled)
                    {
                        CleanResult lq = Cleaner.EnforceLimit(_cfg, false, Logger.Log);
                        if (lq != null && lq.TotalDeleted > 0)
                            limitMsg += string.Format("QQ：已清理 {0} 个文件，释放 {1}\n", lq.TotalDeleted, Cleaner.FormatSize(lq.TotalFreed));
                    }
                    if (_cfg.WxEnabled && _cfg.WxLimitEnabled)
                    {
                        CleanResult lw = Cleaner.EnforceLimit(_cfg, true, Logger.Log);
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
            }

            // 常驻时每 12 小时查一次更新（长时间不重启也能收到通知）
            if (_cfg.UpdateCheck && (DateTime.Now - _cfg.LastUpdateCheck).TotalHours >= 12)
            {
                CheckForUpdates(false);
            }

            _timer.Interval = 60 * 1000;   // 每分钟看一次，到点就清
            _timer.Start();
        }

        private void ConfirmAndClean(bool wx)
        {
            if (_busy) return;
            int keepDays = wx ? _cfg.WxKeepDays : _cfg.KeepDays;
            string keep = keepDays == 0 ? "只保留今天" : ("保留最近 " + keepDays + " 天");
            DialogResult r = MessageBox.Show(
                "将清理" + (wx ? "微信" : "QQ") + "的图片、视频、表情、语音缓存（" + keep + "）。\n\n" +
                "只删本地缓存，不动聊天记录数据库；旧图片点开时会重新下载。\n\n确定继续？",
                "QQ 缓存管家", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (r == DialogResult.OK) RunClean(wx);
        }

        private void RunClean(bool wx)
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
                    r = wx ? Cleaner.RunWx(snapshot, false, Logger.Log)
                           : Cleaner.Run(snapshot, false, Logger.Log);
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

                    if (string.IsNullOrEmpty(r.Error))
                    {
                        if (wx) snapshot.SaveWxLastRun(DateTime.Now);
                        else snapshot.SaveLastRun(DateTime.Now);
                        snapshot.Save();
                    }

                    string msg;
                    if (!string.IsNullOrEmpty(r.Error))
                    {
                        msg = "清理失败：" + r.Error;
                        _icon.ShowBalloonTip(5000, "QQ 缓存管家", msg, ToolTipIcon.Error);
                    }
                    else
                    {
                        msg = string.Format("{0}：已清理 {1} 个文件，释放 {2}",
                            wx ? "微信" : "QQ", r.TotalDeleted, Cleaner.FormatSize(r.TotalFreed));
                        if (r.TotalFailed > 0) msg += string.Format("（{0} 个文件被占用，已跳过）", r.TotalFailed);
                        Toast.ShowToast(msg);
                    }

                    RefreshStatus();
                });
            });
        }

        private void RunPendingCleans()
        {
            if (_busy) return;
            _busy = true;
            _miClean.Enabled = false;

            Config snapshot = _cfg;
            ThreadPool.QueueUserWorkItem(delegate
            {
                StringBuilder sb = new StringBuilder();

                if (snapshot.PendingCleanQq)
                {
                    CleanResult r = Cleaner.Run(snapshot, false, Logger.Log);
                    if (string.IsNullOrEmpty(r.Error)) snapshot.SaveLastRun(DateTime.Now);
                    sb.AppendLine("QQ：" + (string.IsNullOrEmpty(r.Error) ? string.Format("清理 {0} 个文件，释放 {1}",
                        r.TotalDeleted, Cleaner.FormatSize(r.TotalFreed)) : ("失败 " + r.Error)));
                }

                if (snapshot.PendingCleanWx)
                {
                    CleanResult r = Cleaner.RunWx(snapshot, false, Logger.Log);
                    if (string.IsNullOrEmpty(r.Error)) snapshot.SaveWxLastRun(DateTime.Now);
                    sb.AppendLine("微信：" + (string.IsNullOrEmpty(r.Error) ? string.Format("清理 {0} 个文件，释放 {1}",
                        r.TotalDeleted, Cleaner.FormatSize(r.TotalFreed)) : ("失败 " + r.Error)));
                }

                snapshot.PendingCleanQq = false;
                snapshot.PendingCleanWx = false;

                PostToUi(delegate
                {
                    _busy = false;
                    _miClean.Enabled = true;
                    Toast.ShowToast(sb.ToString().Trim());
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

        private void RefreshStatus()
        {
            _miNext.Text = "下次清理：" + NextRunText();

            ThreadPool.QueueUserWorkItem(delegate
            {
                long size = Cleaner.MeasureTotal(_cfg);
                PostToUi(delegate
                {
                    _miStatus.Text = "QQ 缓存：" + Cleaner.FormatSize(size) + "（点击设置）";
                });
            });
        }

        private string NextRunText()
        {
            if (!_cfg.Enabled) return "已关闭";
            if (!_cfg.AutoCleanEnabled) return "未启用（仅开机检查一次）";
            return Cleaner.DueAt(_cfg).ToString("MM-dd HH:mm") + " 待执行";
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
                        RefreshStatus();

                        if (_cfg.PendingCleanQq || _cfg.PendingCleanWx) RunPendingCleans();
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

        private void ShowUpdatePrompt(UpdateChecker.Info info)
        {
            // 和设置窗口一样，模态框开着时不要再叠一层
            if (_promptOpen) return;
            _promptOpen = true;
            try
            {
            bool canSelfUpdate = !string.IsNullOrEmpty(info.Sha256);
            if (!canSelfUpdate) Logger.Log("清单没有 sha256，只提供下载页");

            using (Form f = UpdatePrompt.Build(UpdateChecker.CurrentVersion.ToString(), info, canSelfUpdate))
            {
                DialogResult r = f.ShowDialog();

                if (r == DialogResult.Yes) DoSelfUpdate(info);
                else if (r == DialogResult.OK && !string.IsNullOrEmpty(info.Url))
                {
                    try { Process.Start(info.Url); } catch { }
                }
            }
            }
            finally { _promptOpen = false; }
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
                using (Brush tb = new SolidBrush(textC)) g.DrawString(CenterText ?? "", _big, tb, r1, sf);

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
        private Label _lblTaskWarn;
        private Panel _updateBar;
        private Label _updateText;
        private Button _btnKillTask;

        private sealed class AppPanel
        {
            public bool IsWx;
            public CheckBox Enabled;
            public FlatCheck ScheduleOn;

            public Button ModeTimed;
            public Button ModeLimit;
            public bool LimitMode;

            public FlatSpinner Hour;
            public FlatSpinner Minute;
            public FlatSpinner Interval;
            public FlatSpinner LimitMB;
            public Button ActOld;
            public Button ActAll;
            public bool DeleteAll;
            public FlatSpinner Keep;

            public TextBox Root;
            public Label Detect;
            public CheckBox Pic;
            public CheckBox Video;
            public CheckBox Emoji;
            public CheckBox Voice;
            public CheckBox SysEmoji;
            public CheckBox PersonalEmoji;

            public Label Last;
            public Label Next;
            public Label Countdown;
            public Label Size;
            public Button CleanNow;
            public Button Rescan;

            public Donut Ring;

            public Label LblEvery;
            public Label LblDays;
            public Label LblClean;
            public Label LblHour;
            public Label LblMin;
            public Label LblLimit;
            public Label LblMB;
        }

        public SettingsForm(Config cfg)
        {
            _cfg = cfg;
            BuildUi();
            LoadPanel(_wx, cfg);
            LoadPanel(_qq, cfg);
            _chkAutoStart.Checked = AutoStart.IsEnabled();
            _chkDark.Checked = cfg.DarkMode || Program.ForceDark;
            ApplyTheme(_chkDark.Checked);
            DetectLegacyTask();
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

        private System.Windows.Forms.Timer _tick;

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            // 只统计当前可见的标签页，另一个等切过去再算（大缓存时打开窗口秒开）
            MarkDirtyAll();
            EnsureMeasured(_tabIndex == 0 ? _wx : _qq);
            UpdateCountdown(_wx);
            UpdateCountdown(_qq);

            _tick = new System.Windows.Forms.Timer();
            _tick.Interval = 20000;   // 20 秒刷一次，显示精确到分钟
            _tick.Tick += delegate
            {
                UpdateCountdown(_wx);
                UpdateCountdown(_qq);
            };
            _tick.Start();

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
            if (_tick != null) { _tick.Stop(); _tick.Dispose(); }
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
            _tabWx.Click += delegate { SelectTab(0); };
            Controls.Add(_tabWx);

            _tabQq = MakeSeg("QQ", 118, 8, 100);
            _tabQq.Click += delegate { SelectTab(1); };
            Controls.Add(_tabQq);

            _tabHost = new Panel();
            _tabHost.SetBounds(12, 40, 446, 502);
            _tabHost.BackColor = Color.White;
            Controls.Add(_tabHost);

            _pageWx = new Panel();
            _pageWx.SetBounds(0, 0, 446, 502);
            _pageWx.BackColor = Color.White;
            _tabHost.Controls.Add(_pageWx);

            _pageQq = new Panel();
            _pageQq.SetBounds(0, 0, 446, 502);
            _pageQq.BackColor = Color.White;
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
            _lblTaskWarn.SetBounds(16, 618, 438, 32);
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

            BuildUpdateBar();

            AcceptButton = btnSave;
            CancelButton = btnCancel;

            SelectTab(Program.SettingsTab);
        }

        private void SelectTab(int idx)
        {
            _tabIndex = idx;
            if (_pageWx == null) return;

            _pageWx.Visible = idx == 0;
            _pageQq.Visible = idx == 1;

            StyleTab(_tabWx, idx == 0);
            StyleTab(_tabQq, idx == 1);

            EnsureMeasured(idx == 0 ? _wx : _qq);   // 第一次切到这个标签时补算大小
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

            Label legend1 = MakeLabel("● " + (isWx ? "微信" : "QQ") + "缓存", 52, 154, 130, 18);
            legend1.Tag = "keepcolor";
            legend1.ForeColor = Color.FromArgb(139, 92, 246);
            page.Controls.Add(legend1);

            Label legend2 = MakeLabel("● 电脑已用空间", 176, 154, 140, 18);
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
            p.Enabled.Text = "启用自动清理";
            p.Enabled.SetBounds(6, 202, 220, 24);
            p.Enabled.CheckedChanged += delegate { UpdatePanel(p); };
            page.Controls.Add(p.Enabled);

            p.ModeTimed = MakeSeg("定时清理", 8, 230, 100);
            p.ModeTimed.Click += delegate { p.LimitMode = false; UpdatePanel(p); SyncRing(p); };
            page.Controls.Add(p.ModeTimed);

            p.ScheduleOn = new FlatCheck();
            p.ScheduleOn.Text = "开启（不勾选：开机只检查一次后退出）";
            p.ScheduleOn.SetBounds(212, 232, 232, 24);
            p.ScheduleOn.CheckedChanged += delegate { UpdatePanel(p); };
            page.Controls.Add(p.ScheduleOn);

            p.ModeLimit = MakeSeg("容量上限", 112, 230, 100);
            p.ModeLimit.Click += delegate { p.LimitMode = true; UpdatePanel(p); SyncRing(p); };
            page.Controls.Add(p.ModeLimit);

            // ---- 定时行：每隔 N 天    每次 HH 时 MM 分 清理 ----
            p.LblEvery = MakeLabel("每隔", 8, 268, 36, 22);
            page.Controls.Add(p.LblEvery);

            p.Interval = new FlatSpinner();
            p.Interval.Minimum = 0;
            p.Interval.Maximum = 90;
            p.Interval.SetBounds(44, 266, 54, 24);
            page.Controls.Add(p.Interval);

            p.LblDays = MakeLabel("天    每次", 102, 268, 66, 22);
            page.Controls.Add(p.LblDays);

            p.Hour = new FlatSpinner();
            p.Hour.Minimum = 0;
            p.Hour.Maximum = 23;
            p.Hour.SetBounds(168, 266, 46, 24);
            page.Controls.Add(p.Hour);

            p.LblHour = MakeLabel("时", 218, 268, 18, 22);
            page.Controls.Add(p.LblHour);

            p.Minute = new FlatSpinner();
            p.Minute.Minimum = 0;
            p.Minute.Maximum = 59;
            p.Minute.SetBounds(240, 266, 46, 24);
            page.Controls.Add(p.Minute);

            p.LblMin = MakeLabel("分", 290, 268, 18, 22);
            page.Controls.Add(p.LblMin);

            p.LblClean = MakeLabel("清理", 316, 268, 40, 22);
            page.Controls.Add(p.LblClean);

            // ---- 上限行：文件到达 N MB 时 [删除旧文件][删除全部] ----
            p.LblLimit = MakeLabel("文件到达", 8, 268, 64, 22);
            page.Controls.Add(p.LblLimit);

            p.LimitMB = new FlatSpinner();
            p.LimitMB.Minimum = 100;
            p.LimitMB.Maximum = 200000;
            p.LimitMB.Increment = 256;
            p.LimitMB.SetBounds(74, 266, 70, 24);
            page.Controls.Add(p.LimitMB);

            p.LblMB = MakeLabel("MB 时", 148, 268, 46, 22);
            page.Controls.Add(p.LblMB);

            p.ActOld = MakeSeg("删除旧文件", 196, 264, 100);
            p.ActOld.Click += delegate { p.DeleteAll = false; UpdatePanel(p); };
            page.Controls.Add(p.ActOld);

            p.ActAll = MakeSeg("删除全部", 300, 264, 100);
            p.ActAll.Click += delegate { p.DeleteAll = true; UpdatePanel(p); };
            page.Controls.Add(p.ActAll);

            // ---- 保留 / 目录 ----
            page.Controls.Add(MakeLabel("保留最近", 8, 300, 70, 22));

            p.Keep = new FlatSpinner();
            p.Keep.Minimum = 0;
            p.Keep.Maximum = 365;
            p.Keep.SetBounds(80, 298, 54, 24);
            page.Controls.Add(p.Keep);

            page.Controls.Add(MakeLabel("天（0 = 只留当天）", 140, 300, 200, 22));

            page.Controls.Add(MakeLabel("目录", 8, 332, 36, 22));

            p.Root = new TextBox();
            p.Root.SetBounds(44, 330, 282, 24);
            page.Controls.Add(p.Root);

            Button btnBrowse = MakeSeg("浏览", 330, 328, 48);
            btnBrowse.Click += delegate { BrowseRoot(p); };
            page.Controls.Add(btnBrowse);

            Button btnAuto = MakeSeg("自动", 382, 328, 48);
            btnAuto.Click += delegate { p.Root.Text = ""; RefreshSize(p); };
            page.Controls.Add(btnAuto);

            p.Detect = MakeLabel("正在检测…", 44, 356, 380, 18);
            p.Detect.ForeColor = Color.Gray;
            page.Controls.Add(p.Detect);

            // ---- 清理内容 ----
            page.Controls.Add(MakeLabel("清理内容", 8, 382, 70, 22));

            p.Pic = MakeCheck("图片", 84, 380);
            p.Pic.SetBounds(84, 380, 70, 22);
            p.Video = MakeCheck("视频", 160, 380);
            p.Video.SetBounds(160, 380, 70, 22);
            p.Emoji = MakeCheck("表情", 236, 380);
            p.Emoji.SetBounds(236, 380, 70, 22);
            p.Voice = MakeCheck("语音", 312, 380);
            p.Voice.SetBounds(312, 380, 70, 22);

            page.Controls.Add(p.Pic);
            page.Controls.Add(p.Video);
            page.Controls.Add(p.Emoji);
            page.Controls.Add(p.Voice);

            if (!isWx)
            {
                p.SysEmoji = MakeCheck("系统表情", 84, 404);
                p.SysEmoji.SetBounds(84, 404, 80, 22);
                p.PersonalEmoji = MakeCheck("收藏表情", 170, 404);
                p.PersonalEmoji.SetBounds(170, 404, 80, 22);
                page.Controls.Add(p.SysEmoji);
                page.Controls.Add(p.PersonalEmoji);
            }

            // ---- 状态 ----
            p.Last = MakeLabel("上次清理：从未清理", 8, 426, 300, 18);
            page.Controls.Add(p.Last);

            p.Next = MakeLabel("", 8, 444, 430, 18);
            page.Controls.Add(p.Next);

            p.Countdown = MakeLabel("", 8, 460, 232, 18);
            p.Countdown.ForeColor = Color.FromArgb(0, 100, 180);
            page.Controls.Add(p.Countdown);

            p.Size = MakeLabel("当前占用：—", 8, 478, 232, 18);
            page.Controls.Add(p.Size);

            p.CleanNow = MakeSeg("立即清理", 246, 472, 100);
            p.CleanNow.Click += delegate { CleanNow(p); };
            page.Controls.Add(p.CleanNow);

            p.Rescan = MakeSeg("重新统计", 352, 472, 78);
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

        private void SyncRing(AppPanel p)
        {
            p.Interval.Visible = !p.LimitMode;
            p.Hour.Visible = !p.LimitMode;
            p.Minute.Visible = !p.LimitMode;
            if (p.LblHour != null) p.LblHour.Visible = !p.LimitMode;
            if (p.LblMin != null) p.LblMin.Visible = !p.LimitMode;
            if (p.LblEvery != null) p.LblEvery.Visible = !p.LimitMode;
            if (p.LblDays != null) p.LblDays.Visible = !p.LimitMode;
            if (p.LblClean != null) p.LblClean.Visible = !p.LimitMode;

            p.LimitMB.Visible = p.LimitMode;
            p.ActOld.Visible = p.LimitMode;
            p.ActAll.Visible = p.LimitMode;
            if (p.LblLimit != null) p.LblLimit.Visible = p.LimitMode;
            if (p.LblMB != null) p.LblMB.Visible = p.LimitMode;
        }

        private void LoadPanel(AppPanel p, Config c)
        {
            if (p == null) return;

            bool wx = p.IsWx;

            p.Enabled.Checked = wx ? c.WxEnabled : c.Enabled;
            p.ScheduleOn.Checked = wx ? c.WxScheduleOn : c.ScheduleOn;
            p.LimitMode = wx ? c.WxLimitEnabled : c.LimitEnabled;

            p.Hour.Value = Clamp(wx ? c.WxCleanHour : c.CleanHour, 0, 23);
            p.Minute.Value = Clamp(wx ? c.WxCleanMinute : c.CleanMinute, 0, 59);
            p.Interval.Value = Clamp(wx ? c.WxIntervalDays : c.IntervalDays, 0, 90);
            p.LimitMB.Value = Clamp(wx ? c.WxLimitMB : c.LimitMB, 100, 200000);
            p.DeleteAll = wx ? c.WxLimitDeleteAll : c.LimitDeleteAll;
            p.Keep.Value = Clamp(wx ? c.WxKeepDays : c.KeepDays, 0, 365);
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
            bool limit = p.LimitMode;

            if (wx)
            {
                c.WxEnabled = p.Enabled.Checked;
                c.WxScheduleOn = p.Enabled.Checked && p.ScheduleOn.Checked;
                c.WxLimitEnabled = limit;
                c.WxAutoCleanEnabled = p.Enabled.Checked && p.ScheduleOn.Checked && !limit;
                c.WxCleanHour = (int)p.Hour.Value;
                c.WxCleanMinute = (int)p.Minute.Value;
                c.WxIntervalDays = (int)p.Interval.Value;
                c.WxLimitMB = (int)p.LimitMB.Value;
                c.WxLimitDeleteAll = p.DeleteAll;
                c.WxKeepDays = (int)p.Keep.Value;
                c.WxRoot = p.Root.Text.Trim();
                c.WxPic = p.Pic.Checked;
                c.WxVideo = p.Video.Checked;
                c.WxEmoji = p.Emoji.Checked;
                c.WxVoice = p.Voice.Checked;
            }
            else
            {
                c.Enabled = p.Enabled.Checked;
                c.ScheduleOn = p.Enabled.Checked && p.ScheduleOn.Checked;
                c.LimitEnabled = limit;
                c.AutoCleanEnabled = p.Enabled.Checked && p.ScheduleOn.Checked && !limit;
                c.CleanHour = (int)p.Hour.Value;
                c.CleanMinute = (int)p.Minute.Value;
                c.IntervalDays = (int)p.Interval.Value;
                c.LimitMB = (int)p.LimitMB.Value;
                c.LimitDeleteAll = p.DeleteAll;
                c.KeepDays = (int)p.Keep.Value;
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
            p.ModeTimed.Enabled = on;
            p.ScheduleOn.Enabled = on;
            p.ModeLimit.Enabled = on;
            p.Keep.Enabled = true;
            p.CleanNow.Enabled = true;

            SyncRing(p);

            // 选中态
            Color inactive = _dark ? DarkPanel : Color.FromArgb(245, 246, 250);
            Color inactiveText = _dark ? DarkText : Color.Black;
            Color picked = _dark ? Color.FromArgb(88, 66, 160) : Color.FromArgb(233, 226, 255);

            p.ModeTimed.BackColor = (!p.LimitMode) ? Color.FromArgb(139, 92, 246) : inactive;
            p.ModeTimed.ForeColor = (!p.LimitMode) ? Color.White : inactiveText;
            p.ModeLimit.BackColor = p.LimitMode ? Color.FromArgb(139, 92, 246) : inactive;
            p.ModeLimit.ForeColor = p.LimitMode ? Color.White : inactiveText;

            p.ActOld.BackColor = (!p.DeleteAll) ? picked : inactive;
            p.ActOld.ForeColor = (!p.DeleteAll) ? (_dark ? DarkText : Color.Black) : inactiveText;
            p.ActAll.BackColor = p.DeleteAll ? picked : inactive;
            p.ActAll.ForeColor = p.DeleteAll ? (_dark ? DarkText : Color.Black) : inactiveText;

            bool timedOn = on && !p.LimitMode;
            bool limitOn = on && p.LimitMode;
            p.Hour.Enabled = timedOn;
            p.Minute.Enabled = timedOn;
            p.Interval.Enabled = timedOn;
            p.LimitMB.Enabled = limitOn;
            p.ActOld.Enabled = limitOn;
            p.ActAll.Enabled = limitOn;
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

        private void UpdateCountdown(AppPanel p)
        {
            Config tmp = new Config();
            SavePanel(p, tmp);
            if (!tmp.Enabled && !tmp.WxEnabled) { p.Next.Text = ""; p.Countdown.Text = "下次清理：已关闭"; return; }

            if (p.LimitMode)
            {
                p.Next.Text = "检查方式：容量上限（每 10 分钟检查一次）";
                p.Countdown.Text = "";
                return;
            }

            DateTime due = p.IsWx ? Cleaner.WxDueAt(tmp) : Cleaner.DueAt(tmp);
            p.Next.Text = "下次检查：" + due.ToString("yyyy-MM-dd HH:mm");
            string cd = Cleaner.CountdownText(p.Enabled.Checked, !p.LimitMode, due);
            int cut = cd.IndexOf('（');
            if (cut > 0) cd = cd.Substring(0, cut);
            p.Countdown.Text = cd;
        }

        // ---- 缓存统计：后台线程跑，避免大缓存时设置窗口卡住 ----
        private readonly Dictionary<AppPanel, int> _refreshGen = new Dictionary<AppPanel, int>();
        private bool _wxMeasured, _qqMeasured;

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

            Config tmp = new Config();
            SavePanel(p, tmp);

            if (p.IsWx) _wxMeasured = true; else _qqMeasured = true;

            int gen;
            if (!_refreshGen.TryGetValue(p, out gen)) gen = 0;
            gen++;
            _refreshGen[p] = gen;

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

            p.Ring.TotalBytes = total;
            p.Ring.AppBytes = cache;
            p.Ring.OtherBytes = Math.Max(0, (total - free) - cache);
            p.Ring.CenterText = Cleaner.FormatSize(cache);

            if (p.LimitMode)
            {
                long limit = (long)Math.Max(100, (int)p.LimitMB.Value) * 1024L * 1024L;
                double pct = limit <= 0 ? 0 : cache * 100.0 / limit;
                p.Ring.SubText = string.Format("占设置上限 {0:0.#}%", pct);
            }
            else
            {
                double pct = cache * 100.0 / total;
                p.Ring.SubText = string.Format("占磁盘 {0:0.##}%", pct);
            }
            p.Ring.Invalidate();

            p.Size.Text = "当前占用：" + Cleaner.FormatSize(cache);
            p.Detect.Text = "自动检测：" + (string.IsNullOrEmpty(root) ? "未找到，请手动选择目录" : root);

            UpdateCountdown(p);
        }

        private void CleanNow(AppPanel p)
        {
            Config tmp = new Config();
            SavePanel(p, tmp);
            tmp.PendingCleanQq = false;
            tmp.PendingCleanWx = false;

            CleanResult r;
            if (p.LimitMode)
            {
                r = Cleaner.EnforceLimit(tmp, p.IsWx, Logger.Log);
            }
            else
            {
                r = p.IsWx
                    ? Cleaner.RunWx(tmp, false, Logger.Log)
                    : Cleaner.Run(tmp, false, Logger.Log);
            }

            if (string.IsNullOrEmpty(r.Error))
            {
                if (p.IsWx) tmp.SaveWxLastRun(DateTime.Now); else tmp.SaveLastRun(DateTime.Now);
                tmp.Save();
            }

            string msg;
            if (!string.IsNullOrEmpty(r.Error))
                msg = "清理失败：" + r.Error;
            else if (r.TotalDeleted == 0)
                msg = (p.IsWx ? "微信" : "QQ") + "：没有需要清理的文件";
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

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

        private const int WM_SETREDRAW = 0x000B;

        // 整棵控件树关/开重绘：主题切换时先关，改完颜色再一次性刷出来
        // 让整棵控件树都失效，配合 RedrawWindow 一次性重画
        private static void InvalidateTree(Control c)
        {
            if (c == null) return;
            c.Invalidate();
            foreach (Control child in c.Controls) InvalidateTree(child);
        }

        private static void SetRedrawTree(Control c, bool enabled)
        {
            if (c == null) return;
            if (c.IsHandleCreated)
                SendMessage(c.Handle, WM_SETREDRAW, enabled ? new IntPtr(1) : IntPtr.Zero, IntPtr.Zero);
            foreach (Control child in c.Controls) SetRedrawTree(child, enabled);
        }

        private static readonly Color DarkBg = Color.FromArgb(22, 22, 24);
        private static readonly Color DarkPanel = Color.FromArgb(34, 34, 38);
        private static readonly Color DarkText = Color.FromArgb(232, 223, 200);

        private void ApplyTheme(bool dark)
        {
            SetRedrawTree(this, false);
            try
            {
                _dark = dark;
                ApplyTo(this, dark);

                if (_wx != null) { _wx.Ring.Dark = dark; _wx.Ring.Invalidate(); }
                if (_qq != null) { _qq.Ring.Dark = dark; _qq.Ring.Invalidate(); }

                // 分段按钮的选中/未选中配色
                foreach (AppPanel p in new AppPanel[] { _wx, _qq })
                    if (p != null) UpdatePanel(p);

                SelectTab(_tabIndex);
                BackColor = dark ? DarkBg : Color.FromArgb(246, 247, 250);
            }
            finally
            {
                SetRedrawTree(this, true);
                // 让所有控件都失效，再一次性刷出来（一个绘制批次），避免半新半旧的中间帧
                InvalidateTree(this);
                RedrawWindow(this.Handle, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0004 | 0x0080 | 0x0100);
                Invalidate(true);
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
                return;   // 内部 TextBox 由控件自己管，不要再递归改它
            }

            if (c is FlatCheck)
            {
                ((FlatCheck)c).Dark = dark;
                c.Invalidate();
                return;
            }

            foreach (Control child in c.Controls) ApplyTo(child, dark);

            if (c is Donut)
            {
                ((Donut)c).Dark = dark;
                c.BackColor = dark ? DarkBg : Color.White;
                return;
            }

            if (c is TabControl)
            {
                c.BackColor = dark ? DarkBg : Color.FromArgb(235, 236, 240);   // 页面四周露出的就是它的底色
                return;
            }

            if (c is TabPage || c is Panel)
            {
                c.BackColor = dark ? DarkBg : Color.White;
                return;
            }

            if (c is FlatSpinner)
            {
                ((FlatSpinner)c).Dark = dark;
                c.Invalidate();
                return;
            }

            if (c is FlatCheck)
            {
                FlatCheck fc = (FlatCheck)c;
                fc.Dark = dark;
                fc.ForeColor = dark ? DarkText : Color.Black;
                fc.BackColor = Color.Transparent;
                fc.Invalidate();
                return;
            }

            if (c is Label || c is CheckBox)
            {
                if (c.Tag != null && (string)c.Tag == "keepcolor") return;
                c.ForeColor = dark ? DarkText : Color.Black;
                c.BackColor = Color.Transparent;
                return;
            }

            if (c is Button)
            {
                Button b = (Button)c;
                b.FlatStyle = FlatStyle.Flat;
                b.BackColor = dark ? DarkPanel : Color.FromArgb(245, 246, 250);
                b.ForeColor = dark ? DarkText : Color.Black;
                b.FlatAppearance.BorderColor = dark ? Color.FromArgb(70, 70, 78) : Color.FromArgb(200, 205, 215);
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
            SavePanel(_qq, c);
            SavePanel(_wx, c);
            c.AutostartEnabled = _chkAutoStart.Checked;
            c.DarkMode = _chkDark.Checked;

            // 设的时间已经过去 → 弹“确定 / 立即清理”
            // 只有“0 天 = 当天清理”时，时:分过了才有意义；N≥1 时到期日在未来，不打扰
            TimeSpan nowTime = DateTime.Now.TimeOfDay;
            bool pastQq = c.Enabled && c.AutoCleanEnabled && c.IntervalDays == 0 &&
                          nowTime >= new TimeSpan((int)_qq.Hour.Value, (int)_qq.Minute.Value, 0);
            bool pastWx = c.WxEnabled && c.WxAutoCleanEnabled && c.WxIntervalDays == 0 &&
                          nowTime >= new TimeSpan((int)_wx.Hour.Value, (int)_wx.Minute.Value, 0);

            if (pastQq || pastWx)
            {
                string which = pastQq && pastWx ? "QQ 和微信" : (pastQq ? "QQ" : "微信");
                DateTime at = DateTime.Today.Add(pastQq
                    ? new TimeSpan((int)_qq.Hour.Value, (int)_qq.Minute.Value, 0)
                    : new TimeSpan((int)_wx.Hour.Value, (int)_wx.Minute.Value, 0));

                if (!SchedulePrompt.Ask(which, at))
                {
                    // 点“确定”：不保存、也不清理；把这两个应用的计时推到下一轮，
                    // 免得托盘定时器看到“已过期”又立刻补清一次
                    if (pastQq && Cleaner.DueAt(_cfg) <= DateTime.Now)
                    {
                        _cfg.SaveLastRun(DateTime.Now);
                        Logger.Log("用户选择暂不清理，QQ 本次跳过、计时顺延");
                    }
                    if (pastWx && Cleaner.WxDueAt(_cfg) <= DateTime.Now)
                    {
                        _cfg.SaveWxLastRun(DateTime.Now);
                        Logger.Log("用户选择暂不清理，微信本次跳过、计时顺延");
                    }
                    _cfg.Save();
                    return;
                }

                c.PendingCleanQq = pastQq;
                c.PendingCleanWx = pastWx;
            }

            // 两个应用都没有“总开关 + 定时”同时开启 → 开机只会检查一次然后退出
            bool willResident = Cleaner.WillStayResident(c);

            bool resQq = c.Enabled && c.ScheduleOn;
            bool resWx = c.WxEnabled && c.WxScheduleOn;
            bool offQq = c.Enabled && !c.ScheduleOn;
            bool offWx = c.WxEnabled && !c.WxScheduleOn;
            string nl = Environment.NewLine;
            PromptLayout.SetDark(_dark);

            if (!resQq && !resWx)
            {
                string m1 = "QQ 和微信都没有勾选「开启」：开机只检查一次，检查完 90 秒后自动退出，不会常驻。" + nl +
                            "如需常驻，请勾选对应应用的「开启」。";
                if (!BootModePrompt.Ask(m1)) return;
            }
            else if (offQq || offWx)
            {
                string alive = (resQq && resWx) ? "QQ 和微信" : (resQq ? "QQ" : "微信");
                string idle = (offQq && offWx) ? "QQ 和微信" : (offQq ? "QQ" : "微信");
                string m2 = idle + " 没有勾选「开启」：开机检查后 90 秒自动退出，不会常驻后台。" + nl +
                            alive + " 会正常常驻。";
                if (!BootModePrompt.Ask(m2)) return;
            }

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

        private static string StartupCmdPath
        {
            get { return Path.Combine(StartupDir, "QQCacheTray-Autostart.cmd"); }
        }

        public static bool IsEnabled()
        {
            try { return File.Exists(StartupCmdPath); }
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

                    File.WriteAllText(StartupCmdPath, BuildStartupCmd(), Encoding.Default);
                    Logger.Log("已写入开机启动脚本：" + StartupCmdPath);
                    return true;
                }

                if (File.Exists(StartupCmdPath)) File.Delete(StartupCmdPath);
                Logger.Log("已删除开机启动脚本");
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

        private static string BuildStartupCmd()
        {
            string exe = Application.ExecutablePath;
            // exe 自带 requireAdministrator 清单，start 会直接触发 UAC
            return "@echo off\r\n" +
                   "rem Generated by QQCacheTray: launches the app (UAC prompt appears automatically)\r\n" +
                   "start \"\" \"" + exe + "\" --boot\r\n";
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
    internal static class BootModePrompt
    {
        public static Form Build(string message)
        {
            Form f = PromptLayout.NewForm("QQ 缓存管家", new Size(480, 180));
            Label l = PromptLayout.MakeLabel(f, message, 16, 14, PromptLayout.MaxTextWidth);
            f.Controls.Add(l);

            Button change = PromptLayout.MakeButton(f, "更改配置", DialogResult.Cancel);
            Button save = PromptLayout.MakeButton(f, "我已知晓，保存配置", DialogResult.OK);

            int y = l.Bottom + 16;
            int right = 16 + PromptLayout.MaxTextWidth;

            save.Location = new Point(right - save.Width, y);
            change.Location = new Point(save.Left - PromptLayout.Gap - change.Width, y);

            f.Controls.Add(change);
            f.Controls.Add(save);
            f.AcceptButton = save;
            f.CancelButton = change;
            f.ClientSize = new Size(right + 16, y + save.Height + 16);
            return f;
        }

        public static bool Ask(string message)
        {
            using (Form f = Build(message)) return f.ShowDialog() == DialogResult.OK;
        }
    }

    internal static class SchedulePrompt
    {
        public static Form Build(string app, DateTime at)
        {
            Form f = PromptLayout.NewForm("QQ 缓存管家", new Size(460, 170));

            string text = app + " 设置的清理时间 " + at.ToString("HH:mm") + " 已经过去。\r\n\r\n" +
                          "点「确定」暂不清理，回去重新设置时间；\r\n" +
                          "点「立即清理」现在清理一次。";

            Label l = PromptLayout.MakeLabel(f, text, 16, 14, PromptLayout.MaxTextWidth);
            f.Controls.Add(l);

            Button ok = PromptLayout.MakeButton(f, "确定", DialogResult.Cancel);
            Button clean = PromptLayout.MakeButton(f, "立即清理", DialogResult.OK);

            int y = l.Bottom + 16;
            int right = 16 + PromptLayout.MaxTextWidth;

            clean.Location = new Point(right - clean.Width, y);
            ok.Location = new Point(clean.Left - PromptLayout.Gap - ok.Width, y);

            f.Controls.Add(ok);
            f.Controls.Add(clean);
            f.AcceptButton = clean;
            f.CancelButton = ok;
            f.ClientSize = new Size(right + 16, y + clean.Height + 16);
            return f;
        }

        public static bool Ask(string app, DateTime at)
        {
            using (Form f = Build(app, at)) return f.ShowDialog() == DialogResult.OK;
        }
    }

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

