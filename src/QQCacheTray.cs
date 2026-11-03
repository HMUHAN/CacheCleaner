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
[assembly: System.Reflection.AssemblyVersion("1.2.7.0")]

namespace QQCacheTray
{
    internal static class Program
    {
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
            foreach (string a in args)
            {
                if (string.Equals(a, "--boot", StringComparison.OrdinalIgnoreCase)) bootMode = true;
                if (string.Equals(a, "--dry-run", StringComparison.OrdinalIgnoreCase)) dryRun = true;
                if (string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)) selfTest = true;
                if (string.Equals(a, "--clean", StringComparison.OrdinalIgnoreCase)) cleanOnce = true;
                if (string.Equals(a, "--diag", StringComparison.OrdinalIgnoreCase)) diag = true;
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
            bool needResident = (bootCfg.Enabled && bootCfg.AutoCleanEnabled)
                             || (bootCfg.WxEnabled && bootCfg.WxAutoCleanEnabled);

            if (bootMode && !needResident)
            {
                Environment.ExitCode = RunBootCheck();
                return;
            }

            if (bootMode)
            {
                Logger.Log(string.Format("开机启动：定时清理已开启（QQ={0}，微信={1}），保持常驻",
                    bootCfg.Enabled && bootCfg.AutoCleanEnabled,
                    bootCfg.WxEnabled && bootCfg.WxAutoCleanEnabled));
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

                if (cfg.UpdateCheck && (DateTime.Now - cfg.LastUpdateCheck).TotalDays >= 1)
                {
                    try
                    {
                        string url = string.IsNullOrEmpty(cfg.UpdateUrl)
                            ? UpdateChecker.DefaultManifestUrl
                            : cfg.UpdateUrl;

                        UpdateChecker.Info info = UpdateChecker.Fetch(url);
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

                Config r1 = new Config(); r1.Enabled = true; r1.AutoCleanEnabled = false;
                r1.WxEnabled = false; r1.WxAutoCleanEnabled = false;
                Config r2 = new Config(); r2.Enabled = true; r2.AutoCleanEnabled = false;
                r2.WxEnabled = true; r2.WxAutoCleanEnabled = true;
                Config r3 = new Config(); r3.Enabled = false; r3.AutoCleanEnabled = true;
                r3.WxEnabled = true; r3.WxAutoCleanEnabled = false;
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

                using (Form d1 = BootModePrompt.Build())
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

                sb.AppendLine("RESULT: OK");
                File.WriteAllText(Path.Combine(Config.Dir, "selftest-report.txt"), sb.ToString(), Encoding.UTF8);
                return 0;
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
                            case "update_url": c.UpdateUrl = val; break;
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
                sb.AppendLine("update_url=" + (UpdateUrl ?? ""));
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
            return (c.Enabled && c.AutoCleanEnabled) || (c.WxEnabled && c.WxAutoCleanEnabled);
        }

        public static bool IsDueNow(Config cfg)
        {
            if (!cfg.Enabled) return false;
            return DateTime.Now >= DueAt(cfg);
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

                    List<string> files = new List<string>();
                    Walk(catPath, files, log, 0);

                    foreach (string f in files)
                    {
                        st.Scanned++;
                        string rel = f.Substring(catPath.Length).TrimStart('\\');
                        if (IsExcluded(rel, cfg, cat)) { st.Skipped++; continue; }

                        long len = 0;
                        DateTime mtime;
                        try
                        {
                            FileInfo fi = new FileInfo(f);
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

                List<string> files = new List<string>();
                Walk(catPath, files, log, 0);

                foreach (string f in files)
                {
                    st.Scanned++;
                    long len = 0;
                    DateTime mtime;
                    try
                    {
                        FileInfo fi = new FileInfo(f);
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
            List<string> files = new List<string>();
            Walk(dir, files, delegate { }, 0);
            foreach (string f in files)
            {
                try { sum += new FileInfo(f).Length; }
                catch { }
            }
            return sum;
        }

        private static void Walk(string dir, List<string> acc, Action<string> log, int depth)
        {
            if (depth > 10) return;
            try
            {
                foreach (string f in Directory.GetFiles(dir)) acc.Add(f);
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

            if (_cfg.UpdateCheck && (DateTime.Now - _cfg.LastUpdateCheck).TotalDays >= 1)
            {
                CheckForUpdates(false);
            }

            _icon.ShowBalloonTip(4000, "QQ 缓存管家", "已在后台运行，右键图标可设置自动清理。",
                ToolTipIcon.Info);

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

                try { info = UpdateChecker.Fetch(url); }
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

    internal sealed class SettingsForm : Form
    {
        private readonly Config _cfg;
        public Config Result;
        public UpdateChecker.Info UpdateInfo;    // 有更新时传入，界面顶部显示更新条
        public bool UpdateNow;                   // 用户在更新条上点了“立即更新”

        private AppPanel _qq;
        private AppPanel _wx;

        private CheckBox _chkAutoStart;
        private Label _lblTaskWarn;
        private Button _btnKillTask;

        private sealed class AppPanel
        {
            public bool IsWx;
            public CheckBox Enabled;
            public CheckBox Auto;
            public DateTimePicker Time;
            public NumericUpDown Interval;
            public NumericUpDown Keep;
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
            public Label Size;
            public Label Countdown;
            public Button CleanNow;
            public Button Rescan;
        }

        public SettingsForm(Config cfg)
        {
            _cfg = cfg;
            BuildUi();
            LoadPanel(_wx, cfg);
            LoadPanel(_qq, cfg);
            _chkAutoStart.Checked = AutoStart.IsEnabled();
            DetectLegacyTask();
        }

        private System.Windows.Forms.Timer _tick;

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            // 窗口有句柄之后才能安全地跨线程更新，也才有可用的目录信息
            foreach (AppPanel p in new AppPanel[] { _wx, _qq })
            {
                RefreshSize(p);
                UpdateCountdown(p);
            }

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
            Text = "QQ 缓存管家 - 设置";
            Icon = IconFactory.Create(32);
            Font = new Font("Microsoft YaHei UI", 9F);
            ClientSize = new Size(470, 700);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;

            TabControl tabs = new TabControl();
            tabs.SetBounds(12, 10, 446, 520);
            tabs.Font = new Font("Microsoft YaHei UI", 9F);
            Controls.Add(tabs);

            TabPage pageWx = new TabPage("微信");
            pageWx.BackColor = SystemColors.Control;
            tabs.TabPages.Add(pageWx);

            TabPage pageQq = new TabPage("QQ");
            pageQq.BackColor = SystemColors.Control;
            tabs.TabPages.Add(pageQq);

            _wx = BuildPage(pageWx, true);
            _qq = BuildPage(pageQq, false);

            _chkAutoStart = new CheckBox();
            _chkAutoStart.Text = "开机自动启动（登录时请求管理员权限）";
            _chkAutoStart.SetBounds(16, 540, 430, 24);
            Controls.Add(_chkAutoStart);

            _lblTaskWarn = new Label();
            _lblTaskWarn.ForeColor = Color.FromArgb(180, 90, 0);
            _lblTaskWarn.SetBounds(16, 566, 438, 32);
            _lblTaskWarn.Visible = false;
            Controls.Add(_lblTaskWarn);

            _btnKillTask = new Button();
            _btnKillTask.Text = "停用旧的计划任务";
            _btnKillTask.SetBounds(16, 600, 150, 26);
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
        }

        private AppPanel BuildPage(TabPage page, bool isWx)
        {
            AppPanel p = new AppPanel();
            p.IsWx = isWx;

            p.Enabled = new CheckBox();
            p.Enabled.Text = "启用自动清理";
            p.Enabled.SetBounds(8, 10, 400, 24);
            p.Enabled.CheckedChanged += delegate { UpdatePanel(p); };
            page.Controls.Add(p.Enabled);

            p.Auto = new CheckBox();
            p.Auto.Text = "开启定时自动清理（不勾选：开机只检查日期，没到期 90 秒后退出）";
            p.Auto.SetBounds(24, 38, 400, 24);
            p.Auto.CheckedChanged += delegate { UpdatePanel(p); };
            page.Controls.Add(p.Auto);

            Label lTime = new Label();
            lTime.Text = "每次";
            lTime.SetBounds(24, 70, 34, 22);
            page.Controls.Add(lTime);

            p.Time = new DateTimePicker();
            p.Time.Format = DateTimePickerFormat.Custom;
            p.Time.CustomFormat = "HH:mm";
            p.Time.ShowUpDown = true;
            p.Time.SetBounds(62, 68, 70, 24);
            page.Controls.Add(p.Time);

            Label lTime2 = new Label();
            lTime2.Text = "清理";
            lTime2.SetBounds(140, 70, 60, 22);
            page.Controls.Add(lTime2);

            Label l1 = new Label();
            l1.Text = "每隔";
            l1.SetBounds(24, 102, 40, 22);
            page.Controls.Add(l1);

            p.Interval = new NumericUpDown();
            p.Interval.Minimum = 0;
            p.Interval.Maximum = 90;
            p.Interval.SetBounds(66, 100, 60, 24);
            page.Controls.Add(p.Interval);

            Label l2 = new Label();
            l2.Text = "天清理一次（0 = 当天）";
            l2.SetBounds(134, 102, 160, 22);
            page.Controls.Add(l2);

            Label l3 = new Label();
            l3.Text = "保留最近";
            l3.SetBounds(24, 134, 60, 22);
            page.Controls.Add(l3);

            p.Keep = new NumericUpDown();
            p.Keep.Minimum = 0;
            p.Keep.Maximum = 365;
            p.Keep.SetBounds(88, 132, 60, 24);
            page.Controls.Add(p.Keep);

            Label l4 = new Label();
            l4.Text = "天（0 = 只留当天）";
            l4.SetBounds(156, 134, 200, 22);
            page.Controls.Add(l4);

            Label l5 = new Label();
            l5.Text = isWx ? "微信目录" : "QQ 目录";
            l5.SetBounds(24, 166, 80, 22);
            page.Controls.Add(l5);

            p.Root = new TextBox();
            p.Root.SetBounds(106, 164, 200, 24);
            page.Controls.Add(p.Root);

            Button btnBrowse = new Button();
            btnBrowse.Text = "浏览…";
            btnBrowse.SetBounds(312, 162, 52, 26);
            btnBrowse.Click += delegate { BrowseRoot(p); };
            page.Controls.Add(btnBrowse);

            Button btnAuto = new Button();
            btnAuto.Text = "自动";
            btnAuto.SetBounds(368, 162, 46, 26);
            btnAuto.Click += delegate { p.Root.Text = ""; RefreshSize(p); };
            page.Controls.Add(btnAuto);

            p.Detect = new Label();
            p.Detect.Text = "正在检测…";
            p.Detect.ForeColor = Color.Gray;
            p.Detect.SetBounds(106, 190, 300, 18);
            page.Controls.Add(p.Detect);

            GroupBox grp = new GroupBox();
            grp.Text = "清理内容";
            grp.SetBounds(8, 214, 408, 112);
            page.Controls.Add(grp);

            p.Pic = MakeCheck("图片", 16, 26);
            p.Video = MakeCheck("视频", 206, 26);
            p.Emoji = MakeCheck("表情", 16, 54);
            p.Voice = MakeCheck(isWx ? "语音" : "语音", 206, 54);
            grp.Controls.Add(p.Pic);
            grp.Controls.Add(p.Video);
            grp.Controls.Add(p.Emoji);
            grp.Controls.Add(p.Voice);

            if (!isWx)
            {
                p.SysEmoji = MakeCheck("系统表情资源（+150 MB）", 16, 82);
                p.PersonalEmoji = MakeCheck("我收藏的表情", 206, 82);
                grp.Controls.Add(p.SysEmoji);
                grp.Controls.Add(p.PersonalEmoji);
            }
            else
            {
                Label tip = new Label();
                tip.Text = "注：聊天数据库和接收的文件不会被清理。";
                tip.ForeColor = Color.Gray;
                tip.SetBounds(16, 82, 380, 20);
                grp.Controls.Add(tip);
            }

            GroupBox grpStatus = new GroupBox();
            grpStatus.Text = "状态";
            grpStatus.SetBounds(8, 330, 408, 116);
            page.Controls.Add(grpStatus);

            p.Last = new Label();
            p.Last.SetBounds(12, 18, 270, 20);
            grpStatus.Controls.Add(p.Last);

            p.Next = new Label();
            p.Next.SetBounds(12, 38, 270, 20);
            grpStatus.Controls.Add(p.Next);

            p.Countdown = new Label();
            p.Countdown.SetBounds(12, 58, 270, 20);
            p.Countdown.ForeColor = Color.FromArgb(0, 100, 180);
            grpStatus.Controls.Add(p.Countdown);

            p.Size = new Label();
            p.Size.SetBounds(12, 78, 240, 20);
            grpStatus.Controls.Add(p.Size);

            p.Rescan = new Button();
            p.Rescan.Text = "重新统计";
            p.Rescan.SetBounds(292, 74, 100, 26);
            p.Rescan.Click += delegate { RefreshSize(p); };
            grpStatus.Controls.Add(p.Rescan);

            p.CleanNow = new Button();
            p.CleanNow.Text = "立即清理";
            p.CleanNow.SetBounds(8, 452, 110, 26);
            p.CleanNow.Click += delegate { CleanNow(p); };
            page.Controls.Add(p.CleanNow);

            return p;
        }

        private CheckBox MakeCheck(string text, int x, int y)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.SetBounds(x, y, 180, 22);
            return c;
        }

        private static DateTime SafeDate(DateTime dt)
        {
            if (dt < new DateTime(1900, 1, 1)) dt = DateTime.Today.AddHours(3);
            if (dt > new DateTime(2999, 1, 1)) dt = DateTime.Today.AddHours(3);
            return dt;
        }

        private static DateTime TimeValue(int h, int m)
        {
            return DateTime.Today.AddHours(Math.Max(0, Math.Min(23, h)))
                                 .AddMinutes(Math.Max(0, Math.Min(59, m)));
        }

        private static decimal Clamp(int v, int lo, int hi)
        {
            return Math.Max(lo, Math.Min(hi, v));
        }

        private void LoadPanel(AppPanel p, Config c)
        {
            if (p == null) return;

            if (p.IsWx)
            {
                p.Enabled.Checked = c.WxEnabled;
                p.Auto.Checked = c.WxAutoCleanEnabled;
                p.Time.Value = SafeDate(TimeValue(c.WxCleanHour, c.WxCleanMinute));
                p.Interval.Value = Clamp(c.WxIntervalDays, 0, 90);
                p.Keep.Value = Clamp(c.WxKeepDays, 0, 365);
                p.Root.Text = c.WxRoot ?? "";
                p.Pic.Checked = c.WxPic;
                p.Video.Checked = c.WxVideo;
                p.Emoji.Checked = c.WxEmoji;
                p.Voice.Checked = c.WxVoice;
                p.Last.Text = "上次清理：" + (c.WxLastRun == DateTime.MinValue
                    ? "从未清理" : c.WxLastRun.ToString("yyyy-MM-dd HH:mm"));
                p.Next.Text = "下次检查：" + (c.WxEnabled
                    ? Cleaner.WxDueAt(c).ToString("yyyy-MM-dd HH:mm") : "已关闭");
            }
            else
            {
                p.Enabled.Checked = c.Enabled;
                p.Auto.Checked = c.AutoCleanEnabled;
                p.Time.Value = SafeDate(TimeValue(c.CleanHour, c.CleanMinute));
                p.Interval.Value = Clamp(c.IntervalDays, 0, 90);
                p.Keep.Value = Clamp(c.KeepDays, 0, 365);
                p.Root.Text = c.QqRoot ?? "";
                p.Pic.Checked = c.Pic;
                p.Video.Checked = c.Video;
                p.Emoji.Checked = c.Emoji;
                p.Voice.Checked = c.Ptt;
                if (p.SysEmoji != null) p.SysEmoji.Checked = c.IncludeSystemEmoji;
                if (p.PersonalEmoji != null) p.PersonalEmoji.Checked = c.IncludePersonalEmoji;
                p.Last.Text = "上次清理：" + (c.LastRun == DateTime.MinValue
                    ? "从未清理" : c.LastRun.ToString("yyyy-MM-dd HH:mm"));
                p.Next.Text = "下次检查：" + (c.Enabled
                    ? Cleaner.DueAt(c).ToString("yyyy-MM-dd HH:mm") : "已关闭");
            }

            UpdatePanel(p);
        }

        private void SavePanel(AppPanel p, Config c)
        {
            if (p.IsWx)
            {
                c.WxEnabled = p.Enabled.Checked;
                c.WxAutoCleanEnabled = p.Auto.Checked;
                c.WxCleanHour = p.Time.Value.Hour;
                c.WxCleanMinute = p.Time.Value.Minute;
                c.WxIntervalDays = (int)p.Interval.Value;
                c.WxKeepDays = (int)p.Keep.Value;
                c.WxPic = p.Pic.Checked;
                c.WxVideo = p.Video.Checked;
                c.WxEmoji = p.Emoji.Checked;
                c.WxVoice = p.Voice.Checked;
                c.WxRoot = (p.Root.Text ?? "").Trim();
            }
            else
            {
                c.Enabled = p.Enabled.Checked;
                c.AutoCleanEnabled = p.Auto.Checked;
                c.CleanHour = p.Time.Value.Hour;
                c.CleanMinute = p.Time.Value.Minute;
                c.IntervalDays = (int)p.Interval.Value;
                c.KeepDays = (int)p.Keep.Value;
                c.Pic = p.Pic.Checked;
                c.Video = p.Video.Checked;
                c.Emoji = p.Emoji.Checked;
                c.Ptt = p.Voice.Checked;
                if (p.SysEmoji != null) c.IncludeSystemEmoji = p.SysEmoji.Checked;
                if (p.PersonalEmoji != null) c.IncludePersonalEmoji = p.PersonalEmoji.Checked;
                c.QqRoot = (p.Root.Text ?? "").Trim();
            }
        }

        private void UpdatePanel(AppPanel p)
        {
            bool on = p.Enabled.Checked;

            // 定时相关的才受总开关影响；手动清理永远可用
            p.Auto.Enabled = on;
            p.Time.Enabled = on && p.Auto.Checked;
            p.Interval.Enabled = on;

            p.Keep.Enabled = true;
            p.CleanNow.Enabled = true;
        }

        private void BrowseRoot(AppPanel p)
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = p.IsWx
                    ? "选择微信的 WeChat Files / xwechat_files 目录"
                    : "选择 QQ 的 Tencent Files 目录（也可以直接选账号目录）";
                dlg.ShowNewFolderButton = false;

                string cur = (p.Root.Text ?? "").Trim();
                if (Directory.Exists(cur))
                {
                    string parent = Path.GetDirectoryName(cur);
                    dlg.SelectedPath = (!string.IsNullOrEmpty(parent) && Directory.Exists(parent)) ? parent : cur;
                }
                else
                {
                    string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    string guess = Path.Combine(docs, p.IsWx ? "WeChat Files" : "Tencent Files");
                    if (Directory.Exists(guess)) dlg.SelectedPath = guess;
                }

                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    p.Root.Text = dlg.SelectedPath;
                    RefreshSize(p);
                }
            }
        }

        private void UpdateCountdown(AppPanel p)
        {
            if (p.Countdown == null) return;
            DateTime last = p.IsWx ? _cfg.WxLastRun : _cfg.LastRun;
            DateTime due = Cleaner.NextDue(last, (int)p.Interval.Value,
                                           p.Time.Value.Hour, p.Time.Value.Minute);

            p.Countdown.Text = Cleaner.CountdownText(p.Enabled.Checked, p.Auto.Checked, due);
        }

        private void RefreshSize(AppPanel p)
        {
            p.Size.Text = "当前占用：统计中…";
            p.Rescan.Enabled = false;

            Config c = new Config();
            SavePanel(p, c);
            bool isWx = p.IsWx;

            ThreadPool.QueueUserWorkItem(delegate
            {
                long size = isWx ? Cleaner.MeasureWx(c) : Cleaner.MeasureTotal(c);
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        p.Size.Text = "当前占用：" + Cleaner.FormatSize(size);
                        p.Rescan.Enabled = true;
                        p.Detect.Text = Cleaner.DescribeTargets(c, isWx);
                        UpdateCountdown(p);
                    });
                }
                catch { }
            });
        }

        private void CleanNow(AppPanel p)
        {
            if (MessageBox.Show("确定立即清理" + (p.IsWx ? "微信" : "QQ") + "缓存？",
                    "QQ 缓存管家", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

            Config c = new Config();
            SavePanel(p, c);

            bool isWx = p.IsWx;
            p.CleanNow.Enabled = false;
            Cursor = Cursors.WaitCursor;

            ThreadPool.QueueUserWorkItem(delegate
            {
                CleanResult r = isWx ? Cleaner.RunWx(c, false, Logger.Log) : Cleaner.Run(c, false, Logger.Log);

                try
                {
                    BeginInvoke((Action)delegate
                    {
                        p.CleanNow.Enabled = true;
                        Cursor = Cursors.Default;

                        if (!string.IsNullOrEmpty(r.Error))
                        {
                            MessageBox.Show("清理失败：" + r.Error, "QQ 缓存管家", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                        else
                        {
                            if (isWx)
                            {
                                c.SaveWxLastRun(DateTime.Now);
                                _cfg.WxLastRun = DateTime.Now;
                            }
                            else
                            {
                                c.SaveLastRun(DateTime.Now);
                                _cfg.LastRun = DateTime.Now;
                            }

                            p.Last.Text = "上次清理：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                            p.Next.Text = "下次检查：" + (isWx
                                ? Cleaner.WxDueAt(c) : Cleaner.DueAt(c)).ToString("yyyy-MM-dd HH:mm");

                            MessageBox.Show(string.Format("已清理 {0} 个文件，释放 {1}", r.TotalDeleted,
                                Cleaner.FormatSize(r.TotalFreed)), "QQ 缓存管家");
                        }
                        RefreshSize(p);
                    });
                }
                catch { }
            });
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

        private Panel _updateBar;
        private Label _updateText;

        private void BuildUpdateBar()
        {
            _updateBar = new Panel();
            _updateBar.SetBounds(16, 566, 438, 32);
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

            // 设的时间已经过去 → 弹“确定 / 立即清理”
            // 只有“0 天 = 当天清理”时，时:分过了才有意义；N≥1 时到期日在未来，不打扰
            TimeSpan nowTime = DateTime.Now.TimeOfDay;
            bool pastQq = c.Enabled && c.AutoCleanEnabled && c.IntervalDays == 0 &&
                          nowTime >= new TimeSpan(_qq.Time.Value.Hour, _qq.Time.Value.Minute, 0);
            bool pastWx = c.WxEnabled && c.WxAutoCleanEnabled && c.WxIntervalDays == 0 &&
                          nowTime >= new TimeSpan(_wx.Time.Value.Hour, _wx.Time.Value.Minute, 0);

            if (pastQq || pastWx)
            {
                string which = pastQq && pastWx ? "QQ 和微信" : (pastQq ? "QQ" : "微信");
                DateTime at = DateTime.Today.Add(pastQq ? _qq.Time.Value.TimeOfDay : _wx.Time.Value.TimeOfDay);

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

            if (!willResident && !BootModePrompt.Ask())
            {
                return;   // 选了“更改配置”
            }

            string autoMsg;
            bool autoOk = AutoStart.Set(_chkAutoStart.Checked, out autoMsg);
            if (!autoOk && !string.IsNullOrEmpty(autoMsg))
            {
                MessageBox.Show("开机自启设置未完全成功：\n\n" + autoMsg,
                    "QQ 缓存管家", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

        private TaskbarAlert(string title)
        {
            Text = title;
            ShowInTaskbar = true;
            ShowIcon = false;
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            WindowState = FormWindowState.Minimized;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-4000, -4000);
            Size = new Size(200, 100);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _shownAt = DateTime.Now;
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
                if (_instance != null && !_instance.IsDisposed)
                {
                    _instance.Text = title;
                    _instance.Flash();
                    return;
                }

                _instance = new TaskbarAlert(title);
                _instance.Show();

                // 60 秒没人理就自己收掉，别在任务栏留一个孤儿按钮
                System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
                t.Interval = 60000;
                t.Tick += delegate { t.Stop(); t.Dispose(); Dismiss(); };
                t.Start();
            }
            catch { }
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

        public static Info Fetch(string url)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            // 加时间戳绕开 raw.githubusercontent 的 CDN 缓存（最长约 5 分钟）
            string fresh = url + (url.IndexOf('?') >= 0 ? "&" : "?") + "t=" + DateTime.UtcNow.Ticks;

            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(fresh);
            req.Timeout = 8000;
            req.ReadWriteTimeout = 8000;
            req.UserAgent = "QQCacheTray/" + CurrentVersion;
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
                using (WebClient wc = new WebClient())
                {
                    wc.Headers.Add("User-Agent", "QQCacheTray/" + UpdateChecker.CurrentVersion);
                    wc.DownloadFile(url, tmp);
                }

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
            return f;
        }

        public static Label MakeLabel(Form f, string text, int x, int y, int width)
        {
            Label l = new Label();
            l.Font = f.Font;
            l.Text = text;
            l.AutoSize = false;

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

            Size need = TextRenderer.MeasureText(text, f.Font);
            b.Size = new Size(Math.Max(96, need.Width + 36), Math.Max(30, need.Height + 14));
            return b;
        }
    }

    // 保存前的“开机只检查一次、90 秒后退出”提示
    internal static class BootModePrompt
    {
        public static Form Build()
        {
            Form f = PromptLayout.NewForm("QQ 缓存管家", new Size(480, 180));

            string text = "当前配置不会常驻：开机只检查一次，未到期 90 秒后退出；\r\n" +
                          "期间没有托盘图标，也不会按时间清理。\r\n\r\n" +
                          "如需常驻，请勾选任一应用的「启用自动清理」和「开启定时自动清理」。";

            Label l = PromptLayout.MakeLabel(f, text, 16, 14, PromptLayout.MaxTextWidth);
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

        public static bool Ask()
        {
            using (Form f = Build()) return f.ShowDialog() == DialogResult.OK;
        }
    }

    // “设置的时间已经过去”的提示
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

