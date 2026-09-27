// ConfigStore.cs —— 设置与本地缓存的读写（全部放在软件自己目录，不碰 C 盘、不写注册表）
//
// 为什么不用注册表：用户明确要求「便携、不留后台、不污染系统」。
// 为什么用 JavaScriptSerializer：.NET Framework 自带，零安装，够用。

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace BreadLauncher
{
    public class Settings
    {
        /// <summary>旧版字段：已固定应用的 Key 列表。现在只在「从旧配置迁移到分组」时读一次。</summary>
        public List<string> Pinned = new List<string>();
        /// <summary>分组（大文件夹）。列表顺序即桌面显示顺序；Keys 顺序即组内顺序。</summary>
        public List<AppGroup> Groups = new List<AppGroup>();
        /// <summary>首次见到某应用的时间（Unix 秒），用来判断「新增」。</summary>
        public Dictionary<string, long> FirstSeen = new Dictionary<string, long>();
        /// <summary>全部应用页是否用网格显示。</summary>
        public bool GridMode = false;
        /// <summary>亚克力半透明（默认关闭：纯色更稳，用户可在设置里打开试试）。</summary>
        public bool Acrylic = true;      // ★默认开启（用户要的：一打开就是半透明效果）
        /// <summary>文件夹大小（百分比，100 = 标准；设置菜单里可选 85 / 100 / 120）。</summary>
        public int FolderScale = 100;
        /// <summary>面板位置（左上角，屏幕坐标）。负值 = 还没定过，按「贴鼠标所在屏的任务栏上方居中」自动放。</summary>
        public int PanelX = -1;
        public int PanelY = -1;
        /// <summary>面板宽度（逻辑像素）；0 或负 = 用默认 640。</summary>
        public int PanelW = 0;
        /// <summary>面板高度；0 = 自动（按分组数量收紧到刚好放得下）。用户手动拉过高就固定住。</summary>
        public int PanelH = 0;
        /// <summary>是否已经做过「首次运行预置几个常用应用」，做过了就不再自动加，尊重用户取消固定的选择。</summary>
        public bool Seeded = false;
        /// <summary>启动应用之后要不要关掉面板。**默认 false = 不关**（用户要连着启动好几个应用；
        /// 想恢复老行为就在设置菜单里打开「启动应用后关闭面板」）。旧配置里没有这个字段 → false ✓ 正好是新默认。</summary>
        public bool CloseAfterLaunch = false;
        /// <summary>用户自己指定的「额外扫描目录」（选择器里「来源 → 自定义文件夹」管理）。
        /// 每个目录扫「它自己 + 一级子文件夹」，只认 .lnk / .exe / .url —— 详见 AppsFolderScanner.AddCustomFolders。
        /// 空列表 = 没有自定义来源（「来源」菜单里选「自定义」会是空的）。</summary>
        public List<string> ScanDirs = new List<string>();
        /// <summary>自定义显示名：`AppEntry.Key` → 用户起的名字（右键应用 →「重命名…」）。
        /// 跨分组、跨重启都生效；把名字设回空 = 删除这条记录（恢复原名）。</summary>
        public Dictionary<string, string> Renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>「名字常驻显示」的应用（AppEntry.Key 列表）：勾上之后它的名字一直画在图标下面，
        /// 不用悬停。右键应用 →「名字常驻显示」切换。</summary>
        public List<string> NameLabels = new List<string>();

        /// <summary>鼠标停在小图标上时是否浮出名字（设置菜单里可关；默认开）。</summary>
        public bool HoverNames = true;

        /// <summary>「亚克力会影响鼠标穿透」那条提示有没有看过（只看一次，别每次启动都弹）。</summary>
        public bool AcrylicHintShown = false;


        public const int NewBadgeDays = 7;
    }

    /// <summary>扫描结果缓存的一条记录。</summary>
    public class AppRecord
    {
        public string N { get; set; }
        public string P { get; set; }
        /// <summary>可选的真实图标来源（.url 里的 IconFile、同名 .lnk 的路径），没有就是 null。</summary>
        public string I { get; set; }
        /// <summary>桌面上有没有同名快捷方式（选择器里用来标「桌面」/ 只看桌面）。</summary>
        public bool D { get; set; }
        /// <summary>来自用户自定义扫描目录（选择器「来源 → 自定义文件夹」）。</summary>
        public bool C { get; set; }
    }

    public class AppCache
    {
        public string ScannedUtc { get; set; }
        public List<AppRecord> Apps { get; set; }

        public AppCache()
        {
            Apps = new List<AppRecord>();
        }
    }

    public static class ConfigStore
    {
        public static string Json(object o)
        {
            JavaScriptSerializer ser = new JavaScriptSerializer();
            ser.MaxJsonLength = 64 * 1024 * 1024;
            return ser.Serialize(o);
        }

        /// <summary>最近一次「读文件失败」的原因（UI 拿它提醒用户一次）。读成功后自动清空。</summary>
        public static string LastReadError;

        /// <summary>连坏文件的备份都写不出去时置位：这时候宁可不落盘，也不能覆盖掉用户唯一那份数据。</summary>
        public static bool SuppressWrite;

        public static T Read<T>(string file, T fallback) where T : class
        {
            try
            {
                if (!File.Exists(file)) return fallback;
                string text = File.ReadAllText(file, Encoding.UTF8);
                if (string.IsNullOrEmpty(text.Trim())) return fallback;
                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = 64 * 1024 * 1024;
                T obj = ser.Deserialize<T>(text);
                LastReadError = null;
                return obj == null ? fallback : obj;
            }
            catch (Exception ex)
            {
                // 配置坏了不能让软件打不开：先尽力把原文留一份备份，再用默认值继续。
                LastReadError = file + "：" + ex.GetType().Name + " " + ex.Message;
                Log(Program.AppDir, "读文件失败 " + LastReadError);
                if (BackupBad(file) == false) SuppressWrite = true;
                return fallback;
            }
        }

        /// <summary>
        /// 把坏文件另存一份。先把内容读进内存再写 —— 只读介质上写不成也不会再抛。
        /// 备份不成功返回 false（调用方据此禁止后续落盘，避免默认值覆盖掉唯一那份数据）。
        /// </summary>
        private static bool BackupBad(string file)
        {
            try
            {
                if (!File.Exists(file)) return true;
                byte[] bytes = File.ReadAllBytes(file);
                File.WriteAllBytes(file + ".bad", bytes);
                Log(Program.AppDir, "坏文件已备份为 " + file + ".bad（" + bytes.Length + " 字节）");
                return true;
            }
            catch (Exception ex)
            {
                Log(Program.AppDir, "坏文件备份失败（原文件保持不动）：" + ex.Message);
                return false;
            }
        }

        private static int _writeSeq;

        /// <summary>
        /// 落盘。三个坑都在这里堵住：
        ///   1) 旧写法是「先删原文件再改名」，进程正好死在两步之间 → 整份配置消失；
        ///      改成先写临时文件、再 File.Replace 原子替换（NTFS 元数据级，不存在空窗）。
        ///   2) 写不进去（只读目录 / 满盘 / 被杀软占着）时旧写法会抛异常，异常一路逃到
        ///      Application.Run 之外 —— 面板根本弹不出来。现在一律兜住并返回 false。
        ///   3) 每次落盘把**上一版**留成 settings.json.prev：用户误删分组 / 误清空之后还能救回来
        ///      （settings.json 里那个 Pinned 旧字段也一直留着最老的 6 条 key，等于第二道保险）。
        /// </summary>
        public static bool Write(string file, object o)
        {
            if (SuppressWrite) return false;

            string tmp = null;
            bool keepTmp = false;
            try
            {
                string dir = Path.GetDirectoryName(file);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                int seq = System.Threading.Interlocked.Increment(ref _writeSeq);
                tmp = file + "." + seq.ToString() + ".tmp";
                File.WriteAllText(tmp, Json(o), new UTF8Encoding(false));

                if (File.Exists(file))
                {
                    string bak = file + ".old";
                    try
                    {
                        File.Replace(tmp, file, bak);
                        tmp = null;
                    }
                    catch (Exception)
                    {
                        // 个别文件系统（FAT32 / 网络盘）不支持 Replace：退回「删 + 改名」。
                        // 注意顺序风险：原文件删掉之后，tmp 就是唯一一份数据 —— 改名再失败也绝不能把它清掉。
                        // 删之前先留一份 .prev，万一这次落盘把分组搞没了还能捞回来。
                        try { if (File.Exists(file)) File.Copy(file, file + ".prev", true); }
                        catch (Exception) { }
                        try { File.Delete(file); }
                        catch (Exception) { }
                        try
                        {
                            File.Move(tmp, file);
                            tmp = null;
                        }
                        catch (Exception)
                        {
                            keepTmp = true;
                            throw;
                        }
                    }
                    // 上一版不删掉，留成 settings.json.prev：分组被误删/误清空时还能拿回来
                    try
                    {
                        if (File.Exists(bak))
                        {
                            string prev = file + ".prev";
                            try { if (File.Exists(prev)) File.Delete(prev); }
                            catch (Exception) { }
                            File.Move(bak, prev);
                        }
                    }
                    catch (Exception) { }
                }
                else
                {
                    File.Move(tmp, file);
                    tmp = null;
                }
                return true;
            }
            catch (Exception ex)
            {
                Log(Program.AppDir, "写文件失败 " + file + "：" + ex.GetType().Name + " " + ex.Message);
                return false;
            }
            finally
            {
                if (tmp != null)
                {
                    if (keepTmp)
                    {
                        // 宁可留一个能手工救回来的 .tmp，也不能一声不响地把用户的分组丢掉
                        Log(Program.AppDir, "落盘最后一步改名失败，临时文件保留为 " + tmp + "（里面是最新配置，可手工改名回 settings.json）");
                    }
                    else
                    {
                        try { if (File.Exists(tmp)) File.Delete(tmp); }
                        catch (Exception) { }
                    }
                }
            }
        }

        /// <summary>
        /// 把分组数据收拾干净：去空、去重、跨组去重；老版本只有 Pinned 时迁移成一个「常用」组。
        /// Load 和 Save 都走这里，保证内存和磁盘上的不变量：一个 Key 只属于一个组。
        /// </summary>
        public static void Normalize(Settings s)
        {
            if (s == null) return;
            if (s.Pinned == null) s.Pinned = new List<string>();
            if (s.FirstSeen == null) s.FirstSeen = new Dictionary<string, long>();
            if (s.Groups == null) s.Groups = new List<AppGroup>();

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<AppGroup> clean = new List<AppGroup>();
            for (int i = 0; i < s.Groups.Count; i++)
            {
                AppGroup g = s.Groups[i];
                if (g == null) continue;
                if (g.Name == null) g.Name = string.Empty;
                g.Name = g.Name.Trim();
                if (g.Keys == null) g.Keys = new List<string>();
                List<string> keys = new List<string>();
                for (int k = 0; k < g.Keys.Count; k++)
                {
                    string key = g.Keys[k];
                    if (string.IsNullOrEmpty(key)) continue;
                    key = key.Trim();
                    if (key.Length == 0) continue;
                    if (seen.Contains(key)) continue; // 一个应用只留在最先出现的组里
                    seen.Add(key);
                    keys.Add(key);
                }
                g.Keys = keys;
                clean.Add(g);
            }
            s.Groups = clean;

            // 旧配置升级：只有「已固定」没有分组时，把它们装进一个「常用」大文件夹。
            // ★必须带 `Seeded == false`：`Normalize` 读和写都会跑一遍，而 `Pinned` 在全工程里
            //   **没有任何地方会清空它**（见 README/AGENTS 承诺的「你删掉它就不会自己回来」）。
            //   不加这个守卫的话，用户把分组**全删光**的那一刻这里会当场复活一个装老固定项的「常用」组，
            //   再删再回来，永远清不空（实测用户配置：Pinned 6 条、这 6 条一条都不在任何分组里、Seeded=True）。
            //   老配置（没有 Seeded 这个键）反序列化后 Seeded=false → 迁移照做，升级路径不受影响。
            if (s.Seeded == false && s.Groups.Count == 0 && s.Pinned.Count > 0)
            {
                AppGroup g = new AppGroup("常用");
                HashSet<string> migrated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < s.Pinned.Count; i++)
                {
                    string key = s.Pinned[i];
                    if (string.IsNullOrEmpty(key)) continue;
                    key = key.Trim();
                    if (key.Length == 0 || migrated.Contains(key)) continue;
                    migrated.Add(key);
                    g.Keys.Add(key);
                }
                if (g.Keys.Count > 0) s.Groups.Add(g);
            }
        }

        // ---------------- 设置 ----------------

        public static string SettingsFile(string appDir) { return Path.Combine(appDir, "settings.json"); }
        public static string CacheFile(string appDir) { return Path.Combine(appDir, "cache", "apps-cache.json"); }
        public static string IconDir(string appDir) { return Path.Combine(appDir, "cache", "icons"); }
        public static string LogFile(string appDir) { return Path.Combine(appDir, "cache", "log.txt"); }

        public static Settings LoadSettings(string appDir)
        {
            return LoadSettingsFile(SettingsFile(appDir));
        }

        /// <summary>从指定的 settings 文件读（自检 / 预览出图用，不碰真实配置）。</summary>
        public static Settings LoadSettingsFile(string file)
        {
            Settings s = Read<Settings>(file, null);
            if (s == null) s = new Settings();
            if (s.Pinned == null) s.Pinned = new List<string>();
            Normalize(s);
            return s;
        }

        public static bool SaveSettings(string appDir, Settings s)
        {
            return SaveSettingsFile(SettingsFile(appDir), s);
        }

        public static bool SaveSettingsFile(string file, Settings s)
        {
            if (s == null) return false;
            Normalize(s);
            return Write(file, s);
        }

        // ---------------- 应用列表缓存 ----------------

        public static AppCache LoadCache(string appDir)
        {
            return Read<AppCache>(CacheFile(appDir), null);
        }

        public static void SaveCache(string appDir, AppCache cache)
        {
            Write(CacheFile(appDir), cache);
        }
        public static bool IsCacheStale(AppCache cache, int hours)
        {
            if (cache == null || cache.Apps == null || cache.Apps.Count == 0) return true;
            DateTime t;
            if (!DateTime.TryParse(cache.ScannedUtc, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out t))
                return true;
            return (DateTime.UtcNow - t).TotalHours > hours;
        }

        // ---------------- 日志（出问题时有据可查，也能被用户一眼看到） ----------------

        private static readonly object LogLock = new object();

        public static void Log(string appDir, string msg)
        {
            try
            {
                lock (LogLock)
                {
                    Directory.CreateDirectory(Path.Combine(appDir, "cache"));
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine;
                    File.AppendAllText(LogFile(appDir), line, new UTF8Encoding(false));
                }
            }
            catch { }
        }
    }
}