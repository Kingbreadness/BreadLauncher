// Program.cs —— 入口 + 自检模式
//
// 自检模式（排查问题用，不弹窗、不动用户数据）：
//   BreadLauncher.exe --scan            列出能扫到多少应用、分组情况
//   BreadLauncher.exe --preview 图.png [settings.json]
//                                       离屏渲染一张分组界面图（检查排版）；
//                                       给了 settings.json 就用它，绝不动 build\settings.json
//   BreadLauncher.exe --previewall 图.png 组序号 [settings.json]
//                                       离屏渲染「查看全部」窗口（组内应用列表）
//                                       ★组序号是必填的：只给两个参数不会回退到第 0 组，
//                                         而是不匹配任何分支 → 落到单实例入口把面板 GUI 弹出来
//   BreadLauncher.exe --launch 名称     直接启动某个条目（命令行验证启动链路）

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace BreadLauncher
{
    internal static class Program
    {
        public static string AppDir;

        [STAThread]
        private static void Main(string[] args)
        {
            AppDir = Path.GetDirectoryName(Application.ExecutablePath);
            if (string.IsNullOrEmpty(AppDir)) AppDir = Environment.CurrentDirectory;

            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { } // 自检输出别变乱码

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args.Length > 0 && args[0] == "--scan")
            {
                RunScan();
                return;
            }
            if (args.Length > 1 && args[0] == "--preview")
            {
                RunPreview(args[1], args.Length > 2 ? args[2] : null);
                return;
            }
            if (args.Length > 2 && args[0] == "--previewall")
            {
                int idx = 0;
                int.TryParse(args[2], out idx);
                RunPreviewAll(args[1], idx, args.Length > 3 ? args[3] : null);
                return;
            }
            if (args.Length > 1 && args[0] == "--previewscan")
            {
                RunPreviewScanFolders(args[1]);
                return;
            }
            if (args.Length > 1 && args[0] == "--launch")
            {
                RunLaunch(args[1]);
                return;
            }

            if (args.Length > 1 && args[0] == "--icontest")
            {
                RunIconTest(args[1]);
                return;
            }

            if (args.Length > 0 && args[0] == "--icons")
            {
                RunIconSources();
                return;
            }

            if (args.Length > 0 && args[0] == "--dupicons")
            {
                RunDupIcons();
                return;
            }

            bool createdNew = false;
            Mutex mutex = new Mutex(true, "BreadLauncher.SingleInstance.v1", out createdNew);
            if (!createdNew)
            {
                BringExistingToFront();
                return;
            }

            try
            {
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                // 「配置写不进去」这类意外绝不该表现成「双击了没反应」：留日志 + 弹一次说明
                try { ConfigStore.Log(AppDir, "启动失败：" + ex); }
                catch { }
                try
                {
                    MessageBox.Show("BreadLauncher 启动失败：\n\n" + ex.Message +
                        "\n\n日志：" + ConfigStore.LogFile(AppDir),
                        "BreadLauncher", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch { }
            }
            finally
            {
                try { mutex.ReleaseMutex(); }
                catch { }
            }
        }

        /// <summary>自检：列出哪些条目补到了「真图标来源」（桌面 .url 的 IconFile、同名 .lnk），以及来源到底是什么。</summary>
        private static void RunIconSources()
        {
            List<AppEntry> list = AppsFolderScanner.Scan(AppDir, SelfScanDirs());
            int n = 0;
            foreach (AppEntry en in list)
            {
                if (string.IsNullOrEmpty(en.IconSource)) continue;
                n++;
                Console.WriteLine(en.Name + "  <=  " + en.IconSource);
            }
            Console.WriteLine("共 " + n + "/" + list.Count + " 条有额外图标来源");
        }

        /// <summary>自检：把每个条目在 32px 下取到的图标做像素哈希，看还有多少条目共用同一张图（判断「通用空白图标」有没有清干净）。</summary>
        private static void RunDupIcons()
        {
            List<AppEntry> list = AppsFolderScanner.Scan(AppDir, SelfScanDirs());
            IconService svc = new IconService(AppDir);
            Dictionary<string, List<string>> groups = new Dictionary<string, List<string>>();
            int noIcon = 0;
            foreach (AppEntry en in list)
            {
                Bitmap bmp = svc.Get(en, 32);
                // Get() 现在可能返回 null：表示「连系统那张通用白纸图都算不上」，界面会画首字母色块。
                // 这里必须判空，否则 --dupicons 直接 NullReferenceException 崩掉（自检分支在 Main 的 try 之外）。
                if (bmp == null) { noIcon++; continue; }
                string h;
                using (System.Security.Cryptography.MD5 md5 = System.Security.Cryptography.MD5.Create())
                {
                    h = BitConverter.ToString(md5.ComputeHash(PixelBytes(bmp))).Replace("-", "");
                }
                List<string> g;
                if (!groups.TryGetValue(h, out g)) { g = new List<string>(); groups[h] = g; }
                g.Add(en.Name + "  [" + (string.IsNullOrEmpty(en.IconSource) ? en.Path : en.IconSource) + "]");
            }
            int dupGroups = 0, dupItems = 0;
            foreach (KeyValuePair<string, List<string>> kv in groups)
            {
                if (kv.Value.Count < 2) continue;
                dupGroups++;
                dupItems += kv.Value.Count;
                Console.WriteLine("-- 重复 " + kv.Value.Count + " 条 --");
                foreach (string s in kv.Value) Console.WriteLine("   " + s);
            }
            Console.WriteLine("条目 " + list.Count + "，不同图标 " + groups.Count + " 种，重复组 " + dupGroups + " 个（涉及 " + dupItems + " 条）");
        }

        private static byte[] PixelBytes(Bitmap bmp)
        {
            System.IO.MemoryStream ms = new System.IO.MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            byte[] b = ms.ToArray();
            ms.Dispose();
            return b;
        }

        private static void RunScan()
        {
            try
            {
                List<AppEntry> list = AppsFolderScanner.Scan(AppDir, SelfScanDirs());
                Console.WriteLine("扫描到 " + list.Count + " 个应用");
                Dictionary<string, int> groups = new Dictionary<string, int>();
                foreach (AppEntry en in list)
                {
                    if (!groups.ContainsKey(en.Letter)) groups[en.Letter] = 0;
                    groups[en.Letter]++;
                }
                Console.Write("分组：");
                foreach (KeyValuePair<string, int> kv in groups) Console.Write(kv.Key + "(" + kv.Value + ") ");
                Console.WriteLine();
                Console.WriteLine();
                int show = Math.Min(40, list.Count);
                for (int i = 0; i < show; i++)
                    Console.WriteLine(list[i].Letter + "  " + list[i].Name + "   " + list[i].Path);
                Console.WriteLine("（日志：cache\\log.txt）");
            }
            catch (Exception ex)
            {
                Console.WriteLine("扫描失败：" + ex);
            }
        }

        private static void RunPreview(string outPath, string settingsFile)
        {
            MainForm f = new MainForm(settingsFile); // 分组版：预览里直接看「大文件夹」排版
            f.PreviewMode = true;
            f.ShowInTaskbar = false;
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(-4000, -2000);
            try
            {
                f.Show();
                Application.DoEvents();

                DateTime end = DateTime.Now.AddSeconds(20);
                while (DateTime.Now < end && f.PendingIcons > 0)
                {
                    Application.DoEvents();
                    Thread.Sleep(50);
                }
                for (int i = 0; i < 20; i++) { Application.DoEvents(); Thread.Sleep(60); }
                using (Bitmap bmp = new Bitmap(f.Width, f.Height))
                {
                    f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                    bmp.Save(outPath, ImageFormat.Png);
                }
                Console.WriteLine("已生成预览图：" + outPath + "  (" + f.Width + "x" + f.Height + ")");
            }
            catch (Exception ex)
            {
                Console.WriteLine("生成预览失败：" + ex);
            }
            finally
            {
                try { f.Close(); } catch { }
            }
        }

        /// <summary>自检：离屏渲染「查看全部」窗口（一个分组里的全部应用）。</summary>
        private static void RunPreviewAll(string outPath, int groupIndex, string settingsFile)
        {
            GroupAppsForm f = null;
            try
            {
                Settings s = string.IsNullOrEmpty(settingsFile)
                    ? ConfigStore.LoadSettings(AppDir)
                    : ConfigStore.LoadSettingsFile(settingsFile);
                if (s.Groups.Count == 0)
                {
                    Console.WriteLine("配置里没有分组，无法预览「查看全部」。");
                    return;
                }
                if (groupIndex < 0 || groupIndex >= s.Groups.Count) groupIndex = 0;

                List<AppEntry> all = LoadAppsForPreview();
                Dictionary<string, AppEntry> byKey = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (AppEntry en in all)
                    if (byKey.ContainsKey(en.Key) == false) byKey[en.Key] = en;

                AppGroup g = s.Groups[groupIndex];
                List<AppEntry> apps = new List<AppEntry>();
                foreach (string key in g.Keys)
                {
                    AppEntry en;
                    if (key != null && byKey.TryGetValue(key, out en)) apps.Add(en);
                }

                IconService svc = new IconService(AppDir);
                f = GroupAppsForm.Create(svc, g, apps, null, s);
                f.ShowInTaskbar = false;
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(-4000, -2000);

                f.Show();
                Application.DoEvents();
                for (int i = 0; i < 20; i++) { Application.DoEvents(); Thread.Sleep(60); }

                using (Bitmap bmp = new Bitmap(f.Width, f.Height))
                {
                    f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                    bmp.Save(outPath, ImageFormat.Png);
                }
                Console.WriteLine("已生成「查看全部」预览图：" + outPath + "  (" + f.Width + "x" + f.Height + ")"
                    + "  分组=" + g.Name + "  条目=" + apps.Count);
            }
            catch (Exception ex)
            {
                Console.WriteLine("生成「查看全部」预览失败：" + ex);
            }
            finally
            {
                try { if (f != null) f.Close(); } catch { }
            }
        }

        /// <summary>自检用：优先用扫描缓存里的应用列表，缓存没有就现扫一遍。</summary>
        private static List<AppEntry> LoadAppsForPreview()
        {
            AppCache cache = ConfigStore.LoadCache(AppDir);
            if (cache != null && cache.Apps != null && cache.Apps.Count > 0)
            {
                List<AppEntry> list = AppsFolderScanner.FromCache(cache);
                if (list.Count > 0) return list;
            }
            return AppsFolderScanner.Scan(AppDir, SelfScanDirs());
        }

        /// <summary>自检模式用：把用户在设置里加的「自定义扫描目录」也带上，
        /// 免得 `--scan` / `--dupicons` 看到的世界和界面里不一样。</summary>
        private static List<string> SelfScanDirs()        {
            try
            {
                Settings s = ConfigStore.LoadSettings(AppDir);
                if (s == null || s.ScanDirs == null || s.ScanDirs.Count == 0) return null;
                return new List<string>(s.ScanDirs);
            }
            catch (Exception) { return null; }
        }

        /// <summary>自检：把「管理自定义文件夹」窗口离屏渲染成一张图（排版改动要看图）。
        /// ★必须真的 Show 一次：没 Show 过的表单 DrawToBitmap 只有背景、子控件一片空白。</summary>
        private static void RunPreviewScanFolders(string outPath)
        {
            List<string> demo = new List<string>();
            demo.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            demo.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            demo.Add(@"Z:\已拔掉的U盘\games");        // 故意放一个不存在的：预览里要能看到「当前不存在，先留着」
            ScanFoldersForm f = ScanFoldersForm.CreateForPreview(demo);
            try
            {
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(-4000, -2000);
                f.Show();
                Application.DoEvents();
                using (Bitmap bmp = new Bitmap(f.Width, f.Height))
                {
                    f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                    bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
                }
                Console.WriteLine("写出预览图：" + outPath + "  (" + f.Width + "x" + f.Height + ")");
            }
            finally
            {
                f.Close();
                f.Dispose();
            }
        }

        private static void RunLaunch(string key)        {
            List<AppEntry> list = AppsFolderScanner.Scan(AppDir, SelfScanDirs());
            AppEntry hit = null;
            foreach (AppEntry en in list)
            {
                if (string.Equals(en.Key, key, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(en.Name, key, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(en.DisplayName, key, StringComparison.OrdinalIgnoreCase))
                {
                    hit = en;
                    break;
                }
            }
            if (hit == null)
            {
                Console.WriteLine("没找到条目：" + key);
                return;
            }
            string err;
            bool ok = Launcher.Launch(hit, out err);
            Console.WriteLine((ok ? "启动成功" : "启动失败") + "：" + hit.Name + "  " + hit.Path + (err == null ? "" : "  错误：" + err));
        }

        /// <summary>
        /// 自检：在工程自己这个进程里给某个条目取图标，先走磁盘缓存，再清掉缓存强制重取一次，
        /// 用来判断取图失败到底发生在进程内，还是 API 本身。
        /// </summary>
        private static void RunIconTest(string key)
        {
            string outDir = Path.Combine(AppDir, "icontest");
            Directory.CreateDirectory(outDir);

            List<AppEntry> list = AppsFolderScanner.Scan(AppDir, SelfScanDirs());
            AppEntry hit = null;
            foreach (AppEntry en in list)
            {
                if (string.Equals(en.Key, key, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(en.Name, key, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(en.DisplayName, key, StringComparison.OrdinalIgnoreCase)) { hit = en; break; }
            }
            if (hit == null) { Console.WriteLine("没找到条目：" + key); return; }

            Console.WriteLine("条目：" + hit.Name + "  路径：" + hit.Path + "  IsRealFile=" + hit.IsRealFile);
            IconService svc = new IconService(AppDir);
            int[] sizes = new int[] { 24, 30, 48, 64 };
            foreach (int size in sizes)
            {
                string file = svc.CacheFilePath(hit, size);
                Console.WriteLine("  尺寸 " + size + " 缓存文件已存在=" + File.Exists(file));

                Bitmap a = svc.Get(hit, size);
                if (a == null)
                {
                    Console.WriteLine("      尺寸 " + size + "：无可用图标（界面会画首字母色块），跳过");
                    continue;
                }
                string pa = Path.Combine(outDir, "A-缓存路径-" + size + ".png");
                a.Save(pa, ImageFormat.Png);
                Console.WriteLine("      A 直接 Get -> " + a.Width + "x" + a.Height + "  " + pa);

                try { if (File.Exists(file)) File.Delete(file); } catch (Exception ex) { Console.WriteLine("      删缓存失败 " + ex.Message); }
                svc.ClearDiskCache();
                Bitmap b = svc.Get(hit, size);
                if (b == null)
                {
                    Console.WriteLine("      尺寸 " + size + "：强制重取后仍无可用图标，跳过");
                    continue;
                }
                string pb = Path.Combine(outDir, "B-强制重取-" + size + ".png");
                b.Save(pb, ImageFormat.Png);
                Console.WriteLine("      B 强制重取 -> " + b.Width + "x" + b.Height + "  " + pb);
            }
            Console.WriteLine("完成");
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string cls, string title);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        /// <summary>已经开着一个面板时，再点任务栏图标就把它叫到前面，而不是开第二个。</summary>
        private static void BringExistingToFront()
        {
            try
            {
                IntPtr h = FindWindow(null, "BreadLauncher");
                if (h == IntPtr.Zero) return;
                ShowWindow(h, 9);          // SW_RESTORE：最小化 / 被挡住时先恢复，再抢前台才有效
                SetForegroundWindow(h);
            }
            catch { }
        }
    }
}
