// AppsFolderScanner.cs —— 用 shell:AppsFolder 一次性拿到「开始菜单里所有能点的应用」
//
// 为什么用它：这是资源管理器自己用的入口，UWP（计算器、设置、商店）和传统 exe 都在里面，
// 不用去动 start2.bin，也不用解析开始菜单数据库。只读枚举，不改任何系统状态。
//
// 性能：本机实测 190+ 项约 0.3 秒，所以策略是「先读本地缓存秒开，后台再刷新」。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace BreadLauncher
{
    public static class AppsFolderScanner
    {
        /// <summary>
        /// 噪声条目：卸载程序、更新器、帮助文档之类不该出现在启动面板里。
        /// 只按「名字」判断，宁可少过滤也不要误杀正常软件。
        /// </summary>
        private static readonly Regex Noise = new Regex(
            "(卸载|uninstall|uninstaller|更新程序|update\\s*(helper|installer)|\\.update|crash|report|feedback|反馈|" +
            "readme|read\\s*me|帮助|help|使用说明|发行说明|release\\s*notes|文档|documentation|说明书|手册|manual|" +
            "许可证|licen[cs]e|诊断|debug|repair|修复工具)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>这些后缀是文档不是应用（资源管理器会把桌面上的 htm/txt 也塞进 AppsFolder）。</summary>
        private static readonly HashSet<string> DocExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".htm", ".html", ".mht", ".txt", ".md", ".chm", ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx"
        };

        public static List<AppEntry> Scan(string appDir)
        {
            return Scan(appDir, null);
        }

        /// <summary>扫一遍应用清单。customDirs = 用户在设置里加的「额外扫描目录」（可为空）。</summary>
        public static List<AppEntry> Scan(string appDir, List<string> customDirs)
        {
            Stopwatch sw = Stopwatch.StartNew();
            List<AppEntry> raw = new List<AppEntry>();
            object shell = null;
            object folder = null;
            object items = null;
            try
            {
                Type t = Type.GetTypeFromProgID("Shell.Application");
                if (t == null) throw new InvalidOperationException("找不到 Shell.Application，系统组件异常");
                shell = Activator.CreateInstance(t);
                folder = shell.GetType().InvokeMember("NameSpace", System.Reflection.BindingFlags.InvokeMethod,
                    null, shell, new object[] { "shell:AppsFolder" });
                if (folder == null) throw new InvalidOperationException("打不开 shell:AppsFolder");
                items = folder.GetType().InvokeMember("Items", System.Reflection.BindingFlags.InvokeMethod,
                    null, folder, null);
                if (items == null) throw new InvalidOperationException("shell:AppsFolder 里没有 Items");

                int count = Convert.ToInt32(items.GetType().InvokeMember("Count", System.Reflection.BindingFlags.GetProperty, null, items, null));
                for (int i = 0; i < count; i++)
                {
                    object item = null;
                    try
                    {
                        item = items.GetType().InvokeMember("Item", System.Reflection.BindingFlags.InvokeMethod,
                            null, items, new object[] { i });
                        if (item == null) continue;
                        Type it = item.GetType();
                        string name = Convert.ToString(it.InvokeMember("Name", System.Reflection.BindingFlags.GetProperty, null, item, null));
                        string path = Convert.ToString(it.InvokeMember("Path", System.Reflection.BindingFlags.GetProperty, null, item, null));
                        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path)) continue;

                        AppEntry en = new AppEntry(name, path);
                        if (IsNoise(en)) continue;
                        ReadLinkTarget(it, item, en);
                        raw.Add(en);
                    }
                    catch (Exception one)
                    {
                        ConfigStore.Log(appDir, "扫描单项失败 #" + i + "：" + one.Message);
                    }
                    finally
                    {
                        Release(item);
                    }
                }

                List<AppEntry> list = Dedupe(raw);
                // ★每次都把「枚举失败」的记录清掉：`LastListError` 是静态字段、从不自己复位，
                //   不清的话一次偶发失败会在**以后每一次**扫描的日志里重复报，看着像一直在坏。
                LastListError = null;
                ApplyIconSources(list, appDir);
                AddDesktopShortcuts(list, appDir);   // 里面报「桌面那次枚举有没有失败」
                AddCustomFolders(list, appDir, customDirs);   // 里面报「自定义目录那次枚举有没有失败」
                Sort(list);
                sw.Stop();
                ConfigStore.Log(appDir, "扫描 shell:AppsFolder 完成：" + list.Count + "/" + count + " 项（去重前 " + raw.Count + "），用时 " + sw.ElapsedMilliseconds + " ms");
                return list;
            }
            finally
            {
                Release(items);
                Release(folder);
                Release(shell);
            }
        }

        private static bool IsNoise(AppEntry en)
        {
            if (Noise.IsMatch(en.Name)) return true;
            return IsNotAnApp(en);
        }

        /// <summary>只按「路径 / 扩展名」判断它是不是应用，**不看名字**。
        /// 桌面上的条目用这个：用户自己放到桌面的东西就是他要的，
        /// 别再套开始菜单那套名字噪声规则（实测 `HiBit Uninstaller` 就被「卸载程序」规则丢掉了）。</summary>
        private static bool IsNotAnApp(AppEntry en)
        {
            string path = en.Path;
            // 桌面上的文件夹也会被塞进 AppsFolder，但那是文件夹不是应用
            try { if (Directory.Exists(path)) return true; }
            catch { }

            try
            {
                string ext = Path.GetExtension(path);
                if (!string.IsNullOrEmpty(ext) && DocExtensions.Contains(ext)) return true;
            }
            catch { }

            return false;
        }

        /// <summary>
        /// 去重：名字相同的多条（典型是「桌面 .url 快捷方式」+「steam:// 协议项」）只保留最靠谱的一条。
        /// 优先级：exe 文件 &gt; 协议地址 &gt; .lnk &gt; .url &gt; UWP AUMID &gt; 其它。
        /// </summary>
        private static List<AppEntry> Dedupe(List<AppEntry> raw)
        {
            Dictionary<string, AppEntry> best = new Dictionary<string, AppEntry>(StringComparer.CurrentCultureIgnoreCase);
            List<string> order = new List<string>();

            foreach (AppEntry en in raw)
            {
                string k = en.Name;
                AppEntry exist;
                if (!best.TryGetValue(k, out exist))
                {
                    best[k] = en;
                    order.Add(k);
                }
                else if (Rank(en) < Rank(exist))
                {
                    best[k] = en;
                }
            }

            List<AppEntry> result = new List<AppEntry>(order.Count);
            foreach (string k in order) result.Add(best[k]);
            return result;
        }

        private static int Rank(AppEntry en)
        {
            string p = en.Path ?? string.Empty;
            if (p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return 0;
            if (p.IndexOf("://", StringComparison.Ordinal) >= 0) return 1;
            if (p.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return 2;
            if (p.EndsWith(".url", StringComparison.OrdinalIgnoreCase)) return 3;
            if (p.IndexOf('!') >= 0) return 4;      // UWP 的 AUMID
            if (File.Exists(p)) return 5;
            return 6;
        }

        /// <summary>排序：# 组在前，然后 A..Z 严格按字母顺序，组内按当前区域规则。</summary>
        public static void Sort(List<AppEntry> list)
        {
            list.Sort(delegate(AppEntry a, AppEntry b)
            {
                int g = GroupRank(a.Letter).CompareTo(GroupRank(b.Letter));
                if (g != 0) return g;
                int n = string.Compare(a.Name, b.Name, true, System.Globalization.CultureInfo.CurrentCulture);
                if (n != 0) return n;
                return string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
            });
        }

        private static int GroupRank(string letter)
        {
            // #（数字/符号/非汉字）排最前，后面 A..Z 严格按字母顺序，不能都算同一档，
            // 否则中文排序会把 C 组的中文插到 B 组前面。
            if (string.IsNullOrEmpty(letter) || letter == "#") return 0;
            return 1 + (letter[0] - 'A');
        }

        public static List<AppEntry> FromCache(AppCache cache)
        {
            List<AppEntry> list = new List<AppEntry>();
            if (cache == null || cache.Apps == null) return list;
            foreach (AppRecord r in cache.Apps)
            {
                if (r == null || string.IsNullOrEmpty(r.P)) continue;
                AppEntry en = new AppEntry(r.N, r.P);
                // 缓存里的图标来源要带上，否则每次重启都要重新找一遍
                if (!string.IsNullOrEmpty(r.I)) en.IconSource = r.I;
                en.OnDesktop = r.D;
                en.FromCustom = r.C;
                list.Add(en);
            }
            Sort(list);
            return list;
        }

        public static AppCache ToCache(List<AppEntry> list)
        {
            AppCache c = new AppCache();
            c.ScannedUtc = DateTime.UtcNow.ToString("o");
            foreach (AppEntry e in list)
                c.Apps.Add(new AppRecord { N = e.Name, P = e.Path, I = e.IconSource, D = e.OnDesktop, C = e.FromCustom });
            return c;
        }

        // ---------------- 图标来源补全 ----------------

        /// <summary>「文件本身就是图标来源」的扩展名：这些交给 shell 取图是对的。
        /// ★`.url` / `.lnk` **不在**这里 —— 它们只是快捷方式，shell 对 .url 经常只给一张通用白纸。</summary>
        private static readonly string[] SelfIconExts = new string[] { ".exe", ".com", ".msi", ".bat", ".cmd", ".scr" };

        private static bool HasOwnIcon(AppEntry e)
        {
            string p = e == null ? null : e.Path;
            if (string.IsNullOrEmpty(p)) return false;
            string ext = string.Empty;
            try { ext = Path.GetExtension(p); }
            catch (Exception) { }
            if (string.IsNullOrEmpty(ext)) return false;
            for (int i = 0; i < SelfIconExts.Length; i++)
                if (string.Equals(ext, SelfIconExts[i], StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>路径本身是 `.url` 时，读它里面写的 `IconFile=`（文件存在才返回，否则 null）。
        /// 这是最准的来源：用户桌面上的 .url 就是靠这个显示真图标的（比如 Lossless Scaling 那只小黄鸭）。</summary>
        private static string IconFromUrlShortcut(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return null;
                if (!path.EndsWith(".url", StringComparison.OrdinalIgnoreCase)) return null;
                if (!File.Exists(path)) return null;
                string url, icon;
                if (!ReadUrlShortcut(path, out url, out icon)) return null;
                if (!string.IsNullOrEmpty(icon) && File.Exists(icon)) return icon;
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>
        /// 给「系统自己拿不到图标」的条目补一个图标来源：
        ///   · steam:// 这类协议项 —— 桌面上同名 .url 里写着 IconFile=真实的 .ico；
        ///   · Microsoft.AutoGenerated.{GUID} 这类 AUMID —— 同名 .lnk 交给 shell 去取图。
        /// 只读文本和文件名，不启动任何程序、不改系统状态。
        /// </summary>
        private static void ApplyIconSources(List<AppEntry> list, string appDir)
        {
            Dictionary<string, string> byUrl = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> byName = new Dictionary<string, string>(StringComparer.CurrentCultureIgnoreCase);
            int urlFiles = 0, lnkFiles = 0;

            foreach (string dir in ShortcutDirs())
            {
                bool recursive = !IsDesktopDir(dir);
                string[] urls = ListFiles(dir, "*.url", recursive);
                for (int i = 0; i < urls.Length; i++)
                {
                    string url, icon;
                    if (!ReadUrlShortcut(urls[i], out url, out icon)) continue;
                    urlFiles++;
                    string baseName = Path.GetFileNameWithoutExtension(urls[i]);
                    if (!string.IsNullOrEmpty(url) && !byUrl.ContainsKey(url)) byUrl[url] = icon;
                    if (!string.IsNullOrEmpty(baseName) && !string.IsNullOrEmpty(icon) && !byName.ContainsKey(baseName)) byName[baseName] = icon;
                }

                string[] lnks = ListFiles(dir, "*.lnk", recursive);
                for (int i = 0; i < lnks.Length; i++)
                {
                    string baseName = Path.GetFileNameWithoutExtension(lnks[i]);
                    if (string.IsNullOrEmpty(baseName) || byName.ContainsKey(baseName)) continue;
                    byName[baseName] = lnks[i];
                    lnkFiles++;
                }
            }

            int filled = 0;
            foreach (AppEntry e in list)
            {
                // ★这里以前写的是 `if (e.IsRealFile) continue;`（「有真文件就能取到图标」）—— 错的：
                //   `.url` / `.lnk` 也是「存在的文件」，但它们只是快捷方式，shell 对 .url 常常只给一张
                //   **通用白纸**。用户报的「桌面看有小黄鸭图标、添加应用列表里是空白」就是这条：
                //   Lossless Scaling.url 里明明写着 IconFile=<Steam>\steam\games\2fec9b….ico（里面是只小黄鸭），
                //   却因为 Path 是个存在文件被整条跳过，IconSource 一直是空。
                //   现在只有「可执行/资源类」文件才算自带图标，.url / .lnk 照样往下走。
                if (HasOwnIcon(e)) continue;
                if (!string.IsNullOrEmpty(e.IconSource) && File.Exists(e.IconSource)) { filled++; continue; }

                // ★条目本身就是一个 .url 时，最准的来源就是它自己写的 IconFile=（同目录按名字匹配那步反而容易漏）
                string ownIcon = IconFromUrlShortcut(e.Path);
                if (!string.IsNullOrEmpty(ownIcon)) { e.IconSource = ownIcon; filled++; continue; }

                string hit = null;
                string p = (e.Path ?? string.Empty).Trim();
                if (p.Length > 0 && !byUrl.TryGetValue(p, out hit)) byUrl.TryGetValue(p.TrimEnd('/'), out hit);
                if (string.IsNullOrEmpty(hit)) byName.TryGetValue(e.Name, out hit);
                // .url / .lnk 都没匹配上，才退回 shell 给的 Raw 目标（.msc 这类管理工具）
                if (string.IsNullOrEmpty(hit)) hit = e.LinkTarget;
                if (!string.IsNullOrEmpty(hit) && File.Exists(hit)) { e.IconSource = hit; filled++; }
                else e.IconSource = null;
            }

            if (appDir != null)
                ConfigStore.Log(appDir, "图标来源补全：.url " + urlFiles + " 个、.lnk " + lnkFiles + " 个，可用来源 " + filled + " 条");
        }

        /// <summary>
        /// Microsoft.AutoGenerated.{GUID} 这种 AUMID 系统给的是通用空白图标，
        /// 但 shell 在 System.Link.TargetParsingPath 里写着真实目标（例如 taskmgr.exe、eventvwr.msc）。
        /// 只对这类条目问，免得影响本来 icon 正常的 UWP 应用。
        /// </summary>
        private static void ReadLinkTarget(Type itemType, object item, AppEntry en)
        {
            if (en == null || string.IsNullOrEmpty(en.Path)) return;
            if (!en.Path.StartsWith("Microsoft.AutoGenerated.", StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                object v = itemType.InvokeMember("ExtendedProperty", System.Reflection.BindingFlags.InvokeMethod,
                    null, item, new object[] { "System.Link.TargetParsingPath" });
                string target = Convert.ToString(v);
                if (!string.IsNullOrEmpty(target))
                {
                    target = Environment.ExpandEnvironmentVariables(target.Trim());
                    if (File.Exists(target)) en.LinkTarget = target;
                }
            }
            catch { }
        }

        private static List<string> ShortcutDirs()
        {
            List<string> dirs = new List<string>();
            AddDir(dirs, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            AddDir(dirs, Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
            AddDir(dirs, Environment.GetFolderPath(Environment.SpecialFolder.Programs));
            AddDir(dirs, Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms));
            return dirs;
        }

        private static void AddDir(List<string> dirs, string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            foreach (string d in dirs) if (string.Equals(d, dir, StringComparison.OrdinalIgnoreCase)) return;
            dirs.Add(dir);
        }

        /// <summary>桌面/自定义目录的**一级**子文件夹（不递归，跳过隐藏/系统目录。
        /// ★还要跳过**联接点 / 符号链接**（ReparsePoint）：它们可能指回上层目录，跟着下去会重复扫，
        ///   严重时（多层互相指向）能绕圈。）</summary>
        private static List<string> SubDirs(string dir)
        {
            List<string> subs = new List<string>();
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return subs;
                string[] all = Directory.GetDirectories(dir);
                for (int i = 0; i < all.Length; i++)
                {
                    try
                    {
                        FileAttributes at = File.GetAttributes(all[i]);
                        if ((at & FileAttributes.Hidden) != 0 || (at & FileAttributes.System) != 0) continue;
                        if ((at & FileAttributes.ReparsePoint) != 0) continue;   // 联接点 / 符号链接：别跟
                    }
                    // ★取不到属性就**跳过**，别 Add：原来 catch 是空的，这一条会绕过上面两道判断
                    //   直接进 subs —— 而「ReparsePoint 判定被整条绕过」恰恰是这个函数最要防的事
                    //   （联接点指回上层会让扫描重复甚至绕圈）。代价只是「读不到属性的目录不扫」，可接受。
                    catch (Exception) { continue; }
                    subs.Add(all[i]);
                }
            }
            catch (Exception) { }
            return subs;
        }

        private static void AddShortcutFiles(List<string> into, string dir, bool recursive)
        {
            try
            {
                into.AddRange(ListFiles(dir, "*.lnk", recursive));
                into.AddRange(ListFiles(dir, "*.url", recursive));
            }
            catch (Exception) { }
        }

        /// <summary>用户自定义目录里认的三种文件：快捷方式（.lnk/.url）+ 直接的可执行文件（.exe）。
        /// 这就是用户在「来源 → 管理自定义文件夹」里加进来的东西，扫描规则要和桌面一致，别把
        /// 图片/文档/压缩包也捞进来。</summary>
        private static void AddCustomFiles(List<string> into, string dir)
        {
            try
            {
                into.AddRange(ListFiles(dir, "*.lnk", false));
                into.AddRange(ListFiles(dir, "*.url", false));
                into.AddRange(ListFiles(dir, "*.exe", false));
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 扫用户自己加的「额外扫描目录」：每个目录扫**它自己 + 一级子文件夹**，只认 .lnk / .exe / .url。
        /// ★为什么只下一层、不递归到底：很多人把游戏各自放一个子文件夹（一层刚好覆盖），
        ///   而真有递归下去上万项的目录（实测见过 11628 项的）—— 递归会又慢又脏。
        /// 同名条目以先到的（开始菜单 / 桌面）为准，只给它打个 FromCustom 标记；新条目按文件本身建。
        /// </summary>
        private static void AddCustomFolders(List<AppEntry> list, string appDir, List<string> dirs)
        {
            if (dirs == null || dirs.Count == 0) return;
            int added = 0, marked = 0, skippedNet = 0, truncated = 0, missing = 0;
            try
            {
                Dictionary<string, AppEntry> byName = new Dictionary<string, AppEntry>(StringComparer.CurrentCultureIgnoreCase);
                Dictionary<string, AppEntry> byPath = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (AppEntry e in list)
                {
                    if (e == null) continue;
                    if (!string.IsNullOrEmpty(e.Name) && !byName.ContainsKey(e.Name)) byName[e.Name] = e;
                    if (!string.IsNullOrEmpty(e.Path) && !byPath.ContainsKey(e.Path)) byPath[e.Path] = e;
                }

                for (int i = 0; i < dirs.Count; i++)
                {
                    string dir = dirs[i];
                    if (string.IsNullOrEmpty(dir)) continue;
                    // ★顺序有讲究：**先**判网络/盘可用性，**再**碰 Directory.Exists。
                    //   断线的映射盘上 Directory.Exists 会等 SMB 超时（1~20 秒），而这整段是
                    //   在选择器里改完目录后、由界面线程同步跑完的 —— 卡住就是"面板白屏几秒"。
                    if (IsNetworkDir(dir)) { skippedNet++; continue; }
                    if (!IsDirReady(dir)) { missing++; continue; }
                    if (!Directory.Exists(dir)) { missing++; continue; }
                    List<string> files = new List<string>();
                    AddCustomFiles(files, dir);
                    foreach (string sub in SubDirs(dir)) AddCustomFiles(files, sub);

                    int addedThisDir = 0;
                    for (int k = 0; k < files.Count; k++)
                    {
                        // ★条数上限：有人会把 System32 或整个盘加进来 —— 几十万条既没意义，也会拖慢
                        //   候选构建 / 图标预取。超过就停，并在日志里写明（用户能在 log.txt 里看到）。
                        if (addedThisDir >= MaxCustomPerDir) { truncated++; break; }
                        string file = files[k];
                        if (byPath.ContainsKey(file))
                        {
                            AppEntry e0 = byPath[file];
                            // ★H4：这条「路径已经在列表里」的分支以前只打个 FromCustom 标记就走，
                            //   **不补图标来源** —— 而它可能是 ApplyIconSources 收尾时被写成 null 的
                            //   `.lnk` / `.url` 条目（名字没匹配上、又没 HasOwnIcon）。这里补一次推导，
                            //   和下面「全新条目」分支同一套规矩。
                            if (string.IsNullOrEmpty(e0.IconSource))
                            {
                                if (file.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                                    e0.IconSource = IconFromUrlShortcut(file);
                                else if (file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                                    e0.IconSource = file;
                            }
                            e0.FromCustom = true;
                            marked++;
                            continue;      // ★不计入 perDir：上限管的是「往列表里**新增**多少条」
                        }

                        string name = AppEntry.CleanName(Path.GetFileNameWithoutExtension(file));
                        if (string.IsNullOrEmpty(name)) continue;

                        AppEntry hit;
                        if (byName.TryGetValue(name, out hit) && hit != null)
                        {
                            if (hit.FromCustom == false) { hit.FromCustom = true; marked++; }
                            continue;      // 只是打标记，不算新增
                        }

                        AppEntry en = new AppEntry(name, file);
                        en.FromCustom = true;
                        // 图标来源：和桌面那条一个规矩 —— .lnk 自己就够（shell 会解析目标），
                        // .url 要读它里面写的 IconFile=，.exe 自己就是最好的来源。
                        if (en.Path.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                            en.IconSource = IconFromUrlShortcut(en.Path);
                        else if (en.Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                            en.IconSource = en.Path;
                        list.Add(en);
                        byName[name] = en;
                        byPath[file] = en;
                        added++;
                        addedThisDir++;
                    }
                }
            }
            catch (Exception ex)
            {
                if (appDir != null) ConfigStore.Log(appDir, "自定义目录扫描失败：" + ex.Message);
            }
            LastCustomMissing = missing;
            LastCustomTruncated = truncated;
            LastCustomSkippedNet = skippedNet;
            // ★**每次都写一行**：以前只在"有变化"时才写，于是"我加了目录怎么什么都没有"这种问题
            //   在日志里一个字都查不到（目录不存在/是网络位置都属于这种）。
            if (appDir != null)
                ConfigStore.Log(appDir, "自定义目录：" + dirs.Count + " 个 → 新增 " + added + " 条、标记已有 " + marked + " 条"
                    + (missing > 0 ? "、不存在 " + missing + " 个" : "")
                    + (skippedNet > 0 ? "、跳过网络位置 " + skippedNet + " 个" : "")
                    + (truncated > 0 ? "、有 " + truncated + " 个目录超过 " + MaxCustomPerDir + " 条被截断" : "")
                    // ★「新增 0 条」必须能区分「真没有」和「没扫到」：枚举失败时把原因带出来。
                    //   （原来只在 AddDesktopShortcuts 里报一句，而它跑在**这一步之前** ——
                    //     于是自定义目录的失败要等下一次扫描才被打印，文案还写着"上面这次"，误导。）
                    + (string.IsNullOrEmpty(LastListError) ? "" : "　⚠ 上面这次有目录没能列出来（不是「没有文件」，是「没读到」）：" + LastListError));
        }

        /// <summary>每个自定义目录最多**新增**这么多条（防止把 System32 / 整个盘加进来把候选灌爆）。</summary>
        private const int MaxCustomPerDir = 1000;

        /// <summary>最近一次扫描里「自定义目录」的情况（选择器的提示要用：不存在几个、跳过网络几个、
        /// 有没有被上限截断）。★别当成全局状态用 —— 只是给界面显示用的最近一次结果。</summary>
        public static int LastCustomMissing;
        public static int LastCustomTruncated;
        public static int LastCustomSkippedNet;

        /// <summary>盘可用吗（U 盘/移动盘没插、光驱没盘 → 直接跳过，别让 `Directory.Exists` 去等设备超时）。</summary>
        private static bool IsDirReady(string dir)
        {
            try
            {
                string root = Path.GetPathRoot(dir);
                if (string.IsNullOrEmpty(root)) return true;
                DriveInfo di = new DriveInfo(root);
                return di.IsReady;
            }
            catch (Exception) { return true; }
        }

        /// <summary>
        /// 网络位置（UNC `\\server\share` 或映射的网络驱动器）**不扫**：
        /// 改完自定义目录后的重扫是**在界面线程上**触发的（选择器 → `RescanForPicker` → `LoadData(true)`），
        /// 一个慢网络盘能把面板卡住好几秒，甚至卡到用户以为死机。本地目录照常。
        /// </summary>
        public static bool IsNetworkDir(string dir)
        {
            try
            {
                if (dir.StartsWith("\\\\", StringComparison.Ordinal)) return true;
                string root = Path.GetPathRoot(dir);
                if (string.IsNullOrEmpty(root)) return false;
                DriveInfo di = new DriveInfo(root);
                return di.DriveType == DriveType.Network;
            }
            catch (Exception) { return false; }   // 判断不了就当本地，别把用户正常的目录跳过
        }

        /// <summary>
        /// 桌面快捷方式也要算「应用」：
        ///   · 桌面上有同名快捷方式的条目 → 打上 OnDesktop 标记（选择器里能筛「只看桌面」、排在最前）；
        ///   · 桌面上有、但 shell:AppsFolder 里没有的 → 直接按快捷方式本身建一个条目（不然这些应用根本扫不出来）。
        /// 快捷方式就是 .lnk/.url 文件本身，启动交给 shell 解析，不需要读注册表也不用管理员权限。
        /// </summary>
        private static void AddDesktopShortcuts(List<AppEntry> list, string appDir)
        {
            int marked = 0, added = 0;
            try
            {
                Dictionary<string, AppEntry> byName = new Dictionary<string, AppEntry>(StringComparer.CurrentCultureIgnoreCase);
                foreach (AppEntry e in list)
                {
                    if (e == null || string.IsNullOrEmpty(e.Name)) continue;
                    if (byName.ContainsKey(e.Name) == false) byName[e.Name] = e;
                }

                List<string> deskDirs = new List<string>();
                AddDir(deskDirs, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
                AddDir(deskDirs, Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));

                foreach (string dir in deskDirs)
                {
                    // 桌面**顶层 + 一级子文件夹**：很多人把快捷方式归类放进桌面上的文件夹里。
                    // ★只扫一级、不递归：实测有人桌面的子文件夹里有一万多项目（资料目录，只有 2 个快捷方式埋在很深），
                    //   真递归会又慢又捞出一堆不该进启动器的东西。
                    List<string> files = new List<string>();
                    AddShortcutFiles(files, dir, false);
                    foreach (string sub in SubDirs(dir)) AddShortcutFiles(files, sub, false);

                    for (int i = 0; i < files.Count; i++)
                    {
                        string file = files[i];
                        string name = AppEntry.CleanName(Path.GetFileNameWithoutExtension(file));
                        if (string.IsNullOrEmpty(name)) continue;

                        AppEntry hit;
                        if (byName.TryGetValue(name, out hit) && hit != null)
                        {
                            if (hit.OnDesktop == false) { hit.OnDesktop = true; marked++; }
                            continue;
                        }

                        // ★这里**只**按「文件夹 / 文档」排除，不套开始菜单的名字噪声规则：
                        //   用户放到桌面的就是他要的（HiBit Uninstaller 这类以前会被当噪声丢掉）。
                        AppEntry en = new AppEntry(name, file);
                        if (IsNotAnApp(en)) continue;
                        en.OnDesktop = true;
                        // ★来源要挑对：`.lnk` 自己就是好来源（shell 会解析它的目标图标），
                        //   但 `.url` 不行 —— 它里面写的 `IconFile=` 才是真图标（不然桌面是只小黄鸭、
                        //   我们这边只剩一张白纸）。
                        if (string.IsNullOrEmpty(en.IconSource))
                        {
                            string ownIco = IconFromUrlShortcut(en.Path);
                            en.IconSource = string.IsNullOrEmpty(ownIco) ? en.Path : ownIco;
                        }
                        list.Add(en);
                        byName[name] = en;
                        added++;
                    }
                }
            }
            catch (Exception ex)
            {
                ConfigStore.Log(appDir, "桌面快捷方式补全失败：" + ex.Message);
            }
            if (appDir != null)
            {
                ConfigStore.Log(appDir, "桌面快捷方式：标记已有 " + marked + " 条、新增 " + added + " 条");
                // ★把「有没有枚举失败」一起打出来：这样「新增 0 条」才分得清是「真没有」还是「没扫到」
                if (string.IsNullOrEmpty(LastListError) == false)
                    ConfigStore.Log(appDir, "⚠ 上面这次扫描里有目录没能列出来（不是「没有文件」，是「没读到」）：" + LastListError);
            }
        }

        private static bool IsDesktopDir(string dir)
        {
            string d = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string c = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            return string.Equals(dir, d, StringComparison.OrdinalIgnoreCase) || string.Equals(dir, c, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>最近一次文件枚举失败的原因（null = 没失败过）。扫描汇总日志会带上它，好区分
        /// 「这个目录本来就没有东西」和「这个目录没扫到」——两者在日志里以前长得一模一样。</summary>
        public static string LastListError;

        private static string[] ListFiles(string dir, string pattern, bool recursive)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return new string[0];
                // 桌面只扫一层：用户桌面下面可能挂着大文件夹，递归扫会拖慢首次启动
                return Directory.GetFiles(dir, pattern, recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
            }
            // ★这是**所有**文件枚举的公共出口（桌面快捷方式 / .url 补图标 / 自定义目录都走它）。
            //   以前静默吞掉 → 用户报「扫描少了应用」「我加了目录怎么什么都没有」时，
            //   日志里只有一句「新增 0 条」，根本分不清是"真没有"还是"没扫到"。
            catch (Exception ex)
            {
                LastListError = dir + "：" + ex.GetType().Name + " " + ex.Message;
                return new string[0];
            }
        }

        /// <summary>读 .url（INI 文本）里的 URL= 和 IconFile=。编码：先按 UTF-8 严格解码，失败就用系统 ANSI。</summary>
        private static bool ReadUrlShortcut(string file, out string url, out string icon)
        {
            url = null;
            icon = null;
            string text;
            try
            {
                byte[] bytes = File.ReadAllBytes(file);
                if (bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                    text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
                else
                {
                    try { text = new UTF8Encoding(false, true).GetString(bytes); }
                    catch { text = Encoding.Default.GetString(bytes); }
                }
            }
            catch { return false; }

            string[] lines = text.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim();
                string v = line.Substring(eq + 1).Trim();
                if (url == null && k.Equals("URL", StringComparison.OrdinalIgnoreCase)) url = v;
                else if (icon == null && k.Equals("IconFile", StringComparison.OrdinalIgnoreCase)) icon = v;
            }
            return url != null || icon != null;
        }

        private static void Release(object comObject)
        {
            try
            {
                if (comObject != null && System.Runtime.InteropServices.Marshal.IsComObject(comObject))
                    System.Runtime.InteropServices.Marshal.ReleaseComObject(comObject);
            }
            catch { }
        }
    }
}