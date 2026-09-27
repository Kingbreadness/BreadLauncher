// IconService.cs —— 取应用图标（含 UWP），带内存缓存 + 磁盘缓存 + 后台加载
//
// 为什么不用 Icon.ExtractAssociatedIcon：
//   UWP 应用根本没有独立 exe，AUMID 也不是文件，靠扩展名取图标必然是空白。
// 所以走系统的 IShellItemImageFactory（资源管理器画图标就是用这个），
// 拿不到再退回 SHGetFileInfo，最后兜底系统通用图标 —— 保证列表里永远不出现空洞。
// 磁盘缓存放在软件自己的 cache\icons，不写 C 盘用户目录。
//
// 为什么自己写工作线程：一次性给 200 个条目排队取图标，如果直接在 UI 线程取，
// 面板会卡住好几秒。这里固定 3 条后台线程慢慢取，取到一个通知界面重画一次。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace BreadLauncher
{
    public class IconService
    {
        private readonly string _iconDir;
        /// <summary>「通用白纸图标」参照图要落个空文件来问 shell，这里记住图标目录（静态方法也能用）。</summary>
        private static string _probeDir;
        private readonly Dictionary<string, Bitmap> _mem;
        private readonly object _lock = new object();

        private class WorkItem
        {
            public AppEntry Entry;
            public int Size;
            public Action<AppEntry, Bitmap> Done;
        }

        private readonly Queue<WorkItem> _queue = new Queue<WorkItem>();
        private readonly object _queueLock = new object();
        private int _workers;
        private const int MaxWorkers = 3;

        /// <summary>图标缓存版本。★**改动任何取图逻辑都要 +1**（键里带着它，换版本 = 老缓存自动失效）。
        /// v2 = 修好「PNG 压缩帧的 .ico 被 GDI+ 读成彩色噪点」。</summary>
        private const string CacheVersion = "v2";

        public IconService(string appDir)
        {
            _iconDir = ConfigStore.IconDir(appDir);
            _probeDir = _iconDir;
            _mem = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
            try { Directory.CreateDirectory(_iconDir); }
            catch { }
            EnsureCacheVersion();
        }

        /// <summary>
        /// 缓存版本对不上就把整个图标目录清一次。
        /// ★为什么需要：缓存文件名是 `md5(CacheVersion|key|尺寸|图标来源)`，改了取图逻辑就必须换键，
        ///   否则老缓存里那张坏图会被一直复用（这次修「PNG 压缩帧的 .ico 被读成彩色噪点」就是靠换键生效的）。
        ///   换键的副作用是**老文件变成孤儿**（工程里没有按前缀清理的逻辑），所以这里对着目录里的
        ///   `.iconver` 版本戳判断：一旦升级就把上一版的残留全删掉，用户的目录不会越攒越大。
        /// </summary>
        private void EnsureCacheVersion()
        {
            try
            {
                string verFile = Path.Combine(_iconDir, ".iconver");
                string old = File.Exists(verFile) ? File.ReadAllText(verFile).Trim() : string.Empty;
                if (old == CacheVersion) return;
                ClearDiskCache();                                   // 只删 *.png，版本戳本身留着
                if (!Directory.Exists(_iconDir)) Directory.CreateDirectory(_iconDir);
                File.WriteAllText(verFile, CacheVersion);
            }
            catch (Exception) { }
        }

        /// <summary>同步取图标（已缓存的几乎零成本）。**可能返回 null** —— 表示连系统那张通用白纸图都算不上，
        /// 界面约定：null 就画 `Theme.DrawAppTile` 首字母色块。所有调用点都要能处理 null。</summary>
        public Bitmap Get(AppEntry e, int size)
        {
            string cacheKey = CacheKey(e, size);
            lock (_lock)
            {
                Bitmap hit;
                if (_mem.TryGetValue(cacheKey, out hit)) return hit;
            }

            Bitmap result = null;
            string file = Path.Combine(_iconDir, FileName(e, size));
            try
            {
                if (File.Exists(file)) result = LoadPngDetached(file);
            }
            catch { result = null; }

            if (result == null)
            {
                result = Extract(e, size);
                if (result != null)
                {
                    try { SavePng(file, result); }
                    catch { }
                }
                else
                {
                    // ★取不到（连白纸/乱码都算不上）时**要把旧文件删掉**：留着的话下次会被读回来，
                    //   界面就永远画不出首字母色块（契约是「null → 画色块」，见上面的说明）。
                    try { if (File.Exists(file)) File.Delete(file); }
                    catch { }
                }
            }

            lock (_lock)
            {
                Bitmap exist;
                if (_mem.TryGetValue(cacheKey, out exist))
                {
                    // 别人（绘制线程 / 另一个 worker）先把它放好了：把自己刚做的那张释放掉，
                    // 否则每竞争一次就漏一张 GDI 位图（终结器要等两次 GC 才收）。
                    if (object.ReferenceEquals(exist, result) == false)
                    {
                        try { result.Dispose(); }
                        catch { }
                    }
                    return exist;
                }
                _mem[cacheKey] = result;
            }
            return result;
        }

        /// <summary>后台取图标，完成后回调（回调在后台线程，调用方自己切回 UI 线程）。</summary>
        public void BeginGet(AppEntry e, int size, Action<AppEntry, Bitmap> done)
        {
            WorkItem wi = new WorkItem();
            wi.Entry = e;
            wi.Size = size;
            wi.Done = done;

            lock (_queueLock)
            {
                _queue.Enqueue(wi);
                if (_workers < MaxWorkers)
                {
                    _workers++;
                    Thread t = new Thread(WorkerLoop);
                    t.IsBackground = true;
                    t.Name = "BreadLauncher-Icon";
                    t.Start();
                }
                // 唤醒睡着的取图线程，不然要等它 30 秒超时才会来干活
                Monitor.PulseAll(_queueLock);
            }
        }

        private void WorkerLoop()
        {
            CoInitializeEx(IntPtr.Zero, 0);
            try
            {
                while (true)
                {
                    WorkItem wi;
                    lock (_queueLock)
                    {
                        // 没活干就睡着等（最多 30 秒醒一次看有没有新任务）。
                        // 线程是后台线程，软件一关进程就退，不会常驻。
                        while (_queue.Count == 0) Monitor.Wait(_queueLock, 30000);
                        wi = _queue.Dequeue();
                    }

                    Bitmap bmp = null;
                    try { bmp = Get(wi.Entry, wi.Size); }
                    catch (Exception ex) { ConfigStoreLog(wi.Entry, ex); }

                    if (wi.Done != null)
                    {
                        try { wi.Done(wi.Entry, bmp); }
                        catch { }
                    }
                }
            }
            finally
            {
                try { CoUninitialize(); }
                catch { }
            }
        }
        public void ClearDiskCache()
        {
            lock (_lock) { _mem.Clear(); }
            try
            {
                if (!Directory.Exists(_iconDir)) return;
                foreach (string f in Directory.GetFiles(_iconDir, "*.png"))
                {
                    try { File.Delete(f); }
                    catch { }
                }
            }
            catch { }
        }

        public int MemoryCount
        {
            get { lock (_lock) { return _mem.Count; } }
        }

        public int PendingCount
        {
            get { lock (_queueLock) { return _queue.Count; } }
        }

        /// <summary>自检用：该条目在该尺寸下的磁盘缓存完整路径。</summary>
        public string CacheFilePath(AppEntry e, int size) { return Path.Combine(_iconDir, FileName(e, size)); }

        // ---------------- 具体取图 ----------------

        private Bitmap Extract(AppEntry e, int size)
        {
            // ★取图必须**串行**：界面线程（缓存没命中时）和后台预取线程会同时调 shell 的取图接口，
            //   而 IShellItemImageFactory / ExtractAssociatedIcon 这类调用不是线程安全的，
            //   并发时会偶发「对象当前正在其他地方使用」，严重时异常冒到 OnPaint → 「白底红叉」。
            //   取图不是热路径，串行完全够。
            lock (_extractLock)
            {
                return ExtractLocked(e, size);
            }
        }

        private static readonly object _extractLock = new object();

        private Bitmap ExtractLocked(AppEntry e, int size)
        {
            Bitmap bmp = null;
            try
            {
                // 先看有没有外部给的图标来源（桌面 .url 里的 IconFile、同名 .lnk），有就优先用它
                if (!string.IsNullOrEmpty(e.IconSource)) bmp = TryIconSource(e.IconSource, size);
                // ★通用白纸图 / 彩色乱码图都不算「取到了」：继续往下试别的来源，最后退回首字母色块
                if (LooksGeneric(bmp, size) || LooksLikeNoise(bmp)) bmp = null;

                // ★只有「还没拿到图」时才继续试别的来源。
                //   以前这里是 if (bmp == null && e.IsRealFile) {…真实文件…} else {…UWP…}：
                //   已经用 IconSource 拿到图的条目会让第一个条件为假、掉进 else，
                //   好图标又被 shell:AppsFolder 那次尝试覆盖（失败就变 null → 最后落到通用白纸图标）。
                //   Steam 游戏（.url 里写着 <Steam>\steam\games\*.ico）就是被这里坑的。
                if (bmp == null)
                {
                    if (e.IsRealFile)
                    {
                        bmp = TryShellItemImage(e.Path, size);
                        if (bmp == null) bmp = TryShellFileIcon(e.Path, size);
                    }
                    else
                    {
                        // UWP：AUMID 必须放进 shell:AppsFolder 命名空间才解析得到
                        bmp = TryShellItemImage("shell:AppsFolder\\" + e.Path, size);
                        if (bmp == null) bmp = TryShellItemImage(e.Path, size);
                    }
                }
            }
            catch (Exception ex)
            {
                ConfigStoreLog(e, ex);
            }
            // ★所有来源都失败时**不再拿 `SystemIcons.Application`（系统那张蓝窗口图标）顶替** ——
            //   契约是「取不到 → 返回 null → 界面画首字母色块」（见 Get 上面的说明）。以前这里塞了一张通用图，
            //   于是「取不到」被升格成「一张看着像图标的图」：面板上一堆格子长同一个蓝窗口，用户根本认不出
            //   是哪个软件（和当初那张「白纸」是同一类问题），而且它还会被写进磁盘缓存、重开依旧。
            //   ——实机确认过：修之前所有图标缓存里并没有出现过这张通用图（0 张），所以这次改的是语义，
            //   不是正在发生的现象；万一将来某条路全失败，用户看到的是色块而不是假图标。
            if (bmp == null) return null;
            // ★如果折腾到最后拿到的只是系统那张「未知文件类型」的白纸（或白纸 + 快捷方式箭头），
            //   就返回 null —— 让界面画「首字母色块」。Office 那几个工具（图标在虚拟化包里、磁盘上根本不存在）
            //   系统自己也只给得出白纸，画张色块比白纸好看也好认。
            //   实测：白纸 79%、真图标最高 26%（Word/Excel 16%、Apex 7%），阈值 75% 余量充足。
            //   ★整段必须包 try/catch：这几行原本在 catch 之外，一旦判别本身出错（建探针文件失败、
            //   取系统图标抛 GDI+ 异常……）异常会一路冒到 OnPaint，界面直接变成「白色 + 红色大叉」。
            //   判别失败时按「不是通用图」处理（fail-open），绝不能因为它把绘制搞崩。
            try
            {
                if (LooksGeneric(bmp, size)) return null;
                // ★最后一道保险：不管上面哪条路拿到的图，只要是「彩色乱码」就不给用户看 ——
                //   宁可画首字母色块。用户就是靠一张彩色雪花图标发现「有的软件没图标」的。
                if (LooksLikeNoise(bmp)) return null;
            }
            catch (Exception exGen)
            {
                ConfigStoreLog(e, exGen);
            }
            return bmp;
        }

        /// <summary>
        /// 用「外部给的图标来源」取图：
        ///   .ico 优先**自己解 PNG 压缩帧**（见 DecodeIcoPngFrame），退回到老的 Icon 构造函数；
        ///   其它（.exe / .lnk / .msc）先交给 shell —— .lnk 的图标只有 shell 解得对，最后再退回 ExtractAssociatedIcon。
        /// </summary>
        private static Bitmap TryIconSource(string path, int size)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                if (!File.Exists(path)) return null;
                if (path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                {
                    byte[] ico = null;
                    try { ico = File.ReadAllBytes(path); }
                    catch (Exception) { ico = null; }
                    List<IcoFrame> frames = ico == null ? null : ReadIcoFrames(ico, size);
                    // ★只有当「最合适的那一帧是 PNG 压缩帧」时才自己解 —— GDI+ 的 Icon 路径会把 PNG 帧读成
                    //   彩色噪点（见下面 DecodeIcoFrame 的实测）。选中的是老式 DIB 帧时，老路径是可靠的、而且是
                    //   原生尺寸（不做无谓的缩放）。
                    if (frames != null && frames.Count > 0 && frames[0].Png)
                    {
                        // 逐个候选试：万一某一帧数据损坏，还能退到下一帧（以前是整条路直接放弃）
                        for (int i = 0; i < frames.Count; i++)
                        {
                            if (frames[i].Png == false) continue;
                            Bitmap dec = DecodeIcoFrame(ico, frames[i], size);
                            if (dec != null) return dec;
                        }
                    }
                    try
                    {
                        using (Icon ic = new Icon(path, size, size))
                            return Normalize(ic.ToBitmap(), size);
                    }
                    catch { }
                }
                Bitmap bmp = TryShellItemImage(path, size);
                // ★shell 对「解不出来」的路径会给一张「未知文件类型」的通用白纸图（不是 null）。
                //   Office 那些图标指向虚拟化路径（...\VFS\Windows\Installer\...，文件并不存在）的快捷方式就是这种：
                //   以前拿到白纸就直接 return，永远不会走到下面的 ExtractAssociatedIcon ——
                //   而 .lnk 本身其实还能取出真图标。所以这里把通用图当成「没拿到」，继续往下试。
                if (bmp != null && LooksGeneric(bmp, size) == false) return bmp;
                using (Icon ic = Icon.ExtractAssociatedIcon(path))
                {
                    if (ic != null) return Normalize(ic.ToBitmap(), size);
                }
            }
            catch (Exception ex)
            {
                if (Program.AppDir != null)
                    ConfigStore.Log(Program.AppDir, "读图标来源失败 " + path + "：" + ex.Message);
            }
            return null;
        }

        /// <summary>ICO 里的一帧（只记位置与声明尺寸，不解码）。</summary>
        private class IcoFrame
        {
            public int Off;
            public int Len;
            public int W;      // 目录里声明的宽（0 表示 256）
            public bool Png;   // 这一帧是不是 PNG 压缩的
        }

        /// <summary>
        /// 解析 .ico 的帧目录，并按「该优先用哪一帧」排好序：
        ///   ① 先排**不小于**请求尺寸的帧（里面按尺寸从小到大 = 够用就不放大）；
        ///   ② 再排比请求尺寸小的帧（按离请求尺寸最近）。
        /// ★候选把**老式 DIB 帧也一起算进来**：Steam 那种 .ico 里 24x24 是 DIB、32 以上才是 PNG，
        ///   请求 24 时不该去解 256 的 PNG 再缩下来（会糊）。返回 null = 不是能解析的 ICO。
        /// </summary>
        private static List<IcoFrame> ReadIcoFrames(byte[] data, int size)
        {
            if (data == null || data.Length < 22) return null;
            if (data[0] != 0 || data[1] != 0 || data[2] != 1 || data[3] != 0) return null;   // 不是 ICO（CUR 也拒绝）
            int count = data[4] | (data[5] << 8);
            List<IcoFrame> list = new List<IcoFrame>();
            for (int i = 0; i < count; i++)
            {
                int o = 6 + i * 16;
                if (o + 16 > data.Length) break;                 // 目录比文件还长：截断，别越界
                int w = data[o] == 0 ? 256 : data[o];
                int len = BitConverter.ToInt32(data, o + 8);
                int off = BitConverter.ToInt32(data, o + 12);
                if (len <= 8 || off < 0 || off + len > data.Length) continue;
                IcoFrame f = new IcoFrame();
                f.Off = off;
                f.Len = len;
                f.W = w;
                f.Png = (data[off] == 0x89 && data[off + 1] == 0x50 && data[off + 2] == 0x4E && data[off + 3] == 0x47);
                list.Add(f);
            }
            if (list.Count == 0) return null;
            // 按「该优先用哪一帧」排序：frames[0] 就是最合适的那一帧（调用方据此决定走 PNG 解码还是老路径）
            list.Sort(delegate (IcoFrame a, IcoFrame b) { return FrameScore(a, size).CompareTo(FrameScore(b, size)); });
            return list;
        }

        /// <summary>某一帧在某个请求尺寸下的「优先分」：不小于请求尺寸的排前面（越小越优先），
        /// 小于请求尺寸的排后面（越大越优先）。</summary>
        private static int FrameScore(IcoFrame f, int size)
        {
            return f.W >= size ? (f.W - size) : (1000 + (size - f.W));
        }

        /// <summary>
        /// 解一帧 PNG 压缩的 ICO 帧（缩放到请求尺寸）。
        /// ★为什么必须自己解：`new Icon(path, size, size).ToBitmap()` 碰上 **PNG 压缩帧** 会返回一张
        ///   **彩色噪点图**（GDI+ 把 PNG 数据当成老式 DIB 读了），尺寸大时甚至直接抛
        ///   「请求的范围扩展超过了数组的结尾」。同一份 .ico 实测：
        ///   `new Icon(24)` 正常（那一帧是老格式）、`new Icon(32/36/48/64)` 噪声分 **0.98~0.99**、
        ///   `new Icon(96/128/256)` 抛异常；而「按 PNG 解帧再缩放」是 **0.23~0.39** 正常。
        ///   用户看到的 Wallpaper Engine 那张「彩色雪花」图标就是这么来的。
        /// </summary>
        private static Bitmap DecodeIcoFrame(byte[] data, IcoFrame f, int size)
        {
            try
            {
                using (MemoryStream ms = new MemoryStream(data, f.Off, f.Len))
                using (Bitmap frame = new Bitmap(ms))
                    return Normalize(frame, size);   // Normalize 里会画进一张新位图，所以流可以随 using 关掉
            }
            catch (Exception ex)
            {
                if (Program.AppDir != null)
                    ConfigStore.Log(Program.AppDir, "解 .ico 的 PNG 帧失败（" + f.W + "px，" + f.Len + " 字节）：" + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 系统「未知文件类型」那张通用白纸图标（按尺寸懒加载的参照图）。
        /// 做法是**建一个未知扩展名的空文件**再问 `Icon.ExtractAssociatedIcon`（句柄是自己的，随便释放）；
        /// ★不要改用 `SHGetFileInfo` + `DestroyIcon` —— 那是系统共享句柄，销毁会弄坏系统图标缓存（见 §11.27）。
        /// </summary>
        /// <summary>参照图只缓存**像素数组**（int[]），不缓存 Bitmap —— 见 GenericReference 的说明。</summary>
        private static readonly Dictionary<int, int[]> _genericRef = new Dictionary<int, int[]>();
        private static readonly object _genericLock = new object();

        private static int[] GenericReference(int size)
        {
            lock (_genericLock)
            {
                int[] hit;
                if (_genericRef.TryGetValue(size, out hit)) return hit;
                int[] made = null;
                string probe = null;
                try
                {
                    // ★不要用 SHGetFileInfo + DestroyIcon 去问「未知扩展名」的图标：
                    //   它返回的是系统共享的图标句柄，DestroyIcon 会破坏系统图标缓存
                    //   （实测之后所有图标操作都开始报「对象当前正在其他地方使用」）。
                    //   改成：建一个空文件、用 ExtractAssociatedIcon 取（句柄是自己的，随便释放）。
                    // ★而且**只留像素数组、立刻释放 Bitmap**：这个参照图会被后台取图线程和界面绘制线程
                    //   同时读，而 GDI+ 不是线程安全的 —— 共享 Bitmap 就会偶发「对象当前正在其他地方使用」，
                    //   严重时异常冒到 OnPaint 直接变成「白底红叉」。
                    if (string.IsNullOrEmpty(_probeDir)) return null;
                    probe = Path.Combine(_probeDir, "generic-probe.zzzunknown");
                    if (!Directory.Exists(_probeDir)) Directory.CreateDirectory(_probeDir);
                    if (!File.Exists(probe)) File.WriteAllText(probe, string.Empty);
                    using (Icon ic = Icon.ExtractAssociatedIcon(probe))
                    {
                        if (ic != null)
                        {
                            using (Bitmap b = Normalize(ic.ToBitmap(), size))
                            {
                                if (b != null && b.Width == size && b.Height == size)
                                {
                                    made = new int[size * size];
                                    for (int y = 0; y < size; y++)
                                        for (int x = 0; x < size; x++)
                                            made[y * size + x] = b.GetPixel(x, y).ToArgb();
                                }
                            }
                        }
                    }
                }
                catch { made = null; }      // 判别用的参照取不到就当作「没有参照」→ 上层 fail-open
                finally
                {
                    try { if (probe != null && File.Exists(probe)) File.Delete(probe); }
                    catch { }
                }
                // ★取不到就**别写进字典**：一次偶发失败（探针文件被占用、目录刚建、shell 抽风）会让这个尺寸的
                //   参照图永久是 null，`LooksGeneric` 从此在该尺寸下静默失效、白纸图会被当成真图标收下。
                if (made != null) _genericRef[size] = made;
                else if (Program.AppDir != null)
                    ConfigStore.Log(Program.AppDir, "通用白纸参照图没取到（size=" + size + "）—— 这次不做白纸判别，下次再试");
                return made;
            }
        }

        /// <summary>这张图是不是「其实没解析出来」的通用白纸图？抽样比较，允许少量抗锯齿差异。</summary>
        private static bool LooksGeneric(Bitmap bmp, int size)        {
            if (bmp == null) return false;
            int[] refPx = GenericReference(size);      // 只拿像素数组（不共享 GDI+ 对象）
            if (refPx == null) return false;
            if (bmp.Width != size || bmp.Height != size) return false;
            int same = 0, total = 0;
            for (int y = 0; y < bmp.Height; y += 2)
            {
                for (int x = 0; x < bmp.Width; x += 2)
                {
                    total++;
                    int a = bmp.GetPixel(x, y).ToArgb();   // 只读自己那张（每线程独立），不碰共享对象
                    int b = refPx[y * size + x];
                    int da = Math.Abs(((a >> 24) & 0xFF) - ((b >> 24) & 0xFF));
                    int dr = Math.Abs(((a >> 16) & 0xFF) - ((b >> 16) & 0xFF));
                    int dg = Math.Abs(((a >> 8) & 0xFF) - ((b >> 8) & 0xFF));
                    int db = Math.Abs((a & 0xFF) - (b & 0xFF));
                    if (da <= 24 && dr <= 24 && dg <= 24 && db <= 24) same++;
                }
            }
            // 阈值 75%：实测白纸（含「白纸 + 快捷方式箭头」）是 79%，而真图标最高只有 26%
            //（Word/Excel 16%、Apex 7%），中间余量很大。再加一道「大部分是白的」双保险。
            return total > 0 && (same * 100 / total) >= 75 && MostlyWhite(bmp);
        }

        /// <summary>
        /// 这张图是不是「彩色乱码」（像素数据被错读的产物）？判据两条都要满足：
        ///   ① 相邻像素「差得离谱」的比例 ≥ 85%；② 颜色数 ≥ 24。
        /// ★数字全是实测来的（199 张真实图标缓存）：
        ///   · 坏图（`new Icon(...)` 读 .ico 的 PNG 压缩帧）比例 **92~99%**、颜色 200+；
        ///   · 真图标比例最高 **66.7%**（细密 logo 58~67%、像素风游戏 54%），离阈值还有 18 个点；
        ///   · ② 单列出来是为了**不误杀「两色条纹/棋盘格」这类低彩高对比的真图标**（它们①会很高，但只有几个颜色）；
        ///   · 试过把「纵向相邻对」也算进①：真图标最高会飙到 **81.3%**，离阈值只剩 3.7 个点 —— 所以只算横向。
        /// </summary>
        private static bool LooksLikeNoise(Bitmap bmp)
        {
            if (bmp == null || bmp.Width < 8 || bmp.Height < 8) return false;
            int bad = 0, n = 0;
            HashSet<int> colors = new HashSet<int>();
            for (int y = 0; y < bmp.Height; y += 2)          // 隔行采样：够判、也够快（每个图标只跑一次）
            {
                for (int x = 0; x + 1 < bmp.Width; x++)
                {
                    Color p = bmp.GetPixel(x, y);
                    if (p.A < 40) continue;                  // 透明区不参与（透明像素的 RGB 无意义）
                    if (colors.Count < 32) colors.Add(p.ToArgb());
                    Color q = bmp.GetPixel(x + 1, y);
                    n++;
                    int d = Math.Abs(p.R - q.R) + Math.Abs(p.G - q.G) + Math.Abs(p.B - q.B);
                    if (d > 60) bad++;
                }
            }
            if (n < 8) return false;                         // 样本太少（极小图 / 几乎全透明）：不判
            return (bad * 100 / n) >= 85 && colors.Count >= 24;
        }

        /// <summary>大部分像素是不透明白色 —— 通用白纸图的特征（用来和「白底真图标」区分开）。</summary>
        private static bool MostlyWhite(Bitmap bmp)
        {
            int white = 0, cnt = 0;
            for (int y = 0; y < bmp.Height; y += 2)
            {
                for (int x = 0; x < bmp.Width; x += 2)
                {
                    cnt++;
                    Color c = bmp.GetPixel(x, y);
                    if (c.A > 200 && c.R >= 200 && c.G >= 200 && c.B >= 200) white++;
                }
            }
            return cnt > 0 && (white * 100 / cnt) >= 55;
        }

        private static Bitmap TryShellItemImage(string parsingName, int size)
        {
            if (string.IsNullOrEmpty(parsingName)) return null;
            IShellItemImageFactory factory = null;
            IntPtr hbm = IntPtr.Zero;
            try
            {
                Guid iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
                // 注意：必须拿 IntPtr 再自己 QI。
                // 之前写成 PreserveSig=false + out IShellItemImageFactory 的写法，
                // 在这个系统上会抛 InvalidCastException（被 catch 吞掉后表现为「图标全是兜底图」）。
                IntPtr ppv;
                int hrCreate = SHCreateItemFromParsingNameRaw(parsingName, IntPtr.Zero, ref iid, out ppv);
                if (hrCreate != 0 || ppv == IntPtr.Zero)
                {
                    ConfigStore.Log(Program.AppDir, "SHCreateItemFromParsingName 失败 hr=0x" + hrCreate.ToString("X8") + " 目标=" + parsingName);
                    return null;
                }
                try { factory = (IShellItemImageFactory)Marshal.GetObjectForIUnknown(ppv); }
                finally { Marshal.Release(ppv); }
                if (factory == null) return null;
                SIZE s;
                s.cx = size;
                s.cy = size;
                int hr = factory.GetImage(s, SIIGBF.ICONONLY | SIIGBF.RESIZETOFIT, out hbm);
                if (hr != 0 || hbm == IntPtr.Zero)
                {
                    ConfigStore.Log(Program.AppDir, "GetImage 失败 hr=0x" + hr.ToString("X8") + " hbm=" + hbm + " 目标=" + parsingName + " 尺寸=" + size);
                    return null;
                }
                // 关键：不能用 Image.FromHbitmap —— 它会把 alpha 通道扔掉，
                // 原本透明的地方会变成不透明的纯黑，图标看着就多出一圈黑方块。
                Bitmap raw = FromHbitmapKeepAlpha(hbm);
                if (raw == null) raw = Image.FromHbitmap(hbm);   // 老式无 alpha 位图才走这条
                Bitmap norm = Normalize(raw, size);
                raw.Dispose();
                return norm;
            }
            catch (Exception ex)
            {
                ConfigStore.Log(Program.AppDir, "TryShellItemImage 异常：" + ex.GetType().Name + " " + ex.Message
                    + " | 目标=" + parsingName
                    + " | 长度=" + (parsingName == null ? -1 : parsingName.Length)
                    + " | UTF16=" + (parsingName == null ? "" : Convert.ToBase64String(Encoding.Unicode.GetBytes(parsingName))));
                return null;
            }
            finally
            {
                if (hbm != IntPtr.Zero) DeleteObject(hbm);
                if (factory != null)
                {
                    try { Marshal.ReleaseComObject(factory); }
                    catch { }
                }
            }
        }

        private static Bitmap TryShellFileIcon(string path, int size)
        {
            // 先用 ExtractAssociatedIcon：句柄是自己的，随便释放，不会碰到系统共享图标句柄
            //（SHGetFileInfo 那条要 DestroyIcon，对共享句柄销毁会弄坏系统图标缓存，见 §11.27）。
            try
            {
                using (Icon own = Icon.ExtractAssociatedIcon(path))
                {
                    if (own != null) return Normalize(own.ToBitmap(), size);
                }
            }
            catch { }

            IntPtr hIcon = IntPtr.Zero;
            try
            {
                SHFILEINFO info = new SHFILEINFO();
                uint flags = SHGFI_ICON | SHGFI_LARGEICON;
                IntPtr ret = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(typeof(SHFILEINFO)), flags);
                hIcon = info.hIcon;
                if (ret == IntPtr.Zero || hIcon == IntPtr.Zero) return null;
                using (Icon ic = Icon.FromHandle(hIcon))
                {
                    return Normalize(ic.ToBitmap(), size);
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                // ★故意**不**调 DestroyIcon：SHGetFileInfo 给的常常是系统共享的图标句柄，
                //   销毁它会把系统图标缓存弄坏，之后一批取图开始报「对象当前正在其他地方使用」
                //   （21:45 那份日志里还在刷这个错，就是这条路径留下的）。
                //   这里宁可漏一个句柄 —— 面板是「用完就退」的进程，句柄随进程一起回收，不会累积。
                //   要真正干净就用上面的 ExtractAssociatedIcon（句柄是自己的），根本走不到这里。
                // if (hIcon != IntPtr.Zero) DestroyIcon(hIcon);
                if (hIcon != IntPtr.Zero) { /* 见上：不销毁共享句柄 */ }
            }
        }

        /// <summary>
        /// 把 HBITMAP 的像素原样读出来（含 alpha）。
        /// Image.FromHbitmap 会丢 alpha，透明处变成不透明黑色 —— 图标四周那圈黑就是它干的。
        /// 绕开办法：先用 GetObject 拿宽高，再用 GetDIBits 自己读一份 32bpp 的位。
        /// 读不出来（老式图标没有 alpha 通道）就返回 null，调用方退回 Image.FromHbitmap。
        /// </summary>
        private static Bitmap FromHbitmapKeepAlpha(IntPtr hbm)
        {
            try
            {
                int cb = Marshal.SizeOf(typeof(BITMAP));
                IntPtr p = Marshal.AllocHGlobal(cb);
                BITMAP bm;
                try
                {
                    if (GetObject(hbm, cb, p) == 0) return null;
                    bm = (BITMAP)Marshal.PtrToStructure(p, typeof(BITMAP));
                }
                finally { Marshal.FreeHGlobal(p); }

                int w = bm.bmWidth;
                int h = Math.Abs(bm.bmHeight);
                if (w <= 0 || h <= 0 || w > 4096 || h > 4096) return null;

                BITMAPINFO bmi = new BITMAPINFO();
                bmi.bmiColors = new uint[256];
                bmi.bmiHeader.biSize = 40;
                bmi.bmiHeader.biWidth = w;
                bmi.bmiHeader.biHeight = -h;        // 负数表示自上而下，省得再翻一次
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = 32;
                bmi.bmiHeader.biCompression = 0;    // BI_RGB

                int stride = w * 4;
                IntPtr bits = Marshal.AllocHGlobal(stride * h);
                IntPtr hdc = GetDC(IntPtr.Zero);
                try
                {
                    if (GetDIBits(hdc, hbm, 0, (uint)h, bits, ref bmi, 0) == 0) return null;
                    using (Bitmap tmp = new Bitmap(w, h, stride, PixelFormat.Format32bppArgb, bits))
                    {
                        Bitmap copy = new Bitmap(tmp);
                        // 全透明说明这张位图根本没有 alpha 信息（拿到的会是空图），
                        // 那就别用它，让调用方退回老办法
                        if (AllTransparent(copy)) { copy.Dispose(); return null; }
                        return copy;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(bits);
                    if (hdc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, hdc);
                }
            }
            catch { return null; }
        }

        /// <summary>整张图是不是全透明（只读 alpha 字节，不逐像素 GetPixel）。</summary>
        private static bool AllTransparent(Bitmap b)
        {
            BitmapData d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = d.Stride;
                byte[] row = new byte[stride];
                for (int y = 0; y < b.Height; y++)
                {
                    Marshal.Copy((IntPtr)((long)d.Scan0 + (long)y * stride), row, 0, stride);
                    for (int x = 3; x < b.Width * 4; x += 4)
                        if (row[x] != 0) return false;
                }
                return true;
            }
            finally { b.UnlockBits(d); }
        }

        /// <summary>统一成 32bppArgb 的 size×size 位图：后面缩放、画圆角都不会出黑边。</summary>
        public static Bitmap Normalize(Image src, int size)
        {
            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                if (src != null) g.DrawImage(src, new Rectangle(0, 0, size, size));
            }
            return bmp;
        }

        private static Bitmap LoadPngDetached(string file)
        {
            byte[] bytes = File.ReadAllBytes(file);
            using (MemoryStream ms = new MemoryStream(bytes))
            using (Bitmap tmp = new Bitmap(ms))
            {
                return new Bitmap(tmp); // 拷贝一份，避免位图一直占着文件流
            }
        }

        private static void SavePng(string file, Bitmap bmp)
        {
            if (bmp == null) return;      // 没图可存：别让 NullReferenceException 被上层空 catch 吞掉
            string dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (MemoryStream ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                File.WriteAllBytes(file, ms.ToArray());
            }
        }

        private static string CacheKey(AppEntry e, int size)
        {
            // ★缓存版本号：**取图逻辑一改就得 +1**（常量在上面）。缓存键只认「版本|key|尺寸|图标来源」，
            //   不看内容 —— 不 +1 的话，老缓存里那张坏图会被一直复用（这次把 PNG 压缩帧的 .ico 从
            //   「彩色噪点」修成真图标，就是靠 v1 → v2 让旧文件自动失效的）。
            string k = CacheVersion + "|" + (e == null ? "?" : e.Key) + "|" + size;
            // 图标来源变了（比如刚补上 .url 里的真图标）就换一个缓存文件名，不用手动清缓存
            if (e != null && !string.IsNullOrEmpty(e.IconSource)) k += "|" + e.IconSource.ToLowerInvariant();
            return k;
        }

        private static string FileName(AppEntry e, int size)
        {
            string raw = CacheKey(e, size);
            using (MD5 md5 = MD5.Create())
            {
                byte[] h = md5.ComputeHash(Encoding.UTF8.GetBytes(raw));
                StringBuilder sb = new StringBuilder(h.Length * 2);
                for (int i = 0; i < h.Length; i++) sb.Append(h[i].ToString("x2"));
                return sb.ToString() + ".png";
            }
        }

        private static void ConfigStoreLog(AppEntry e, Exception ex)
        {
            if (Program.AppDir != null)
                ConfigStore.Log(Program.AppDir, "取图标失败 " + (e == null ? "?" : e.Name) + "：" + ex.Message);
        }

        // ---------------- P/Invoke ----------------

        private const uint SHGFI_ICON = 0x00000100;
        private const uint SHGFI_LARGEICON = 0x00000000;

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx;
            public int cy;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [Flags]
        private enum SIIGBF
        {
            RESIZETOFIT = 0x00,
            BIGGERSIZEOK = 0x01,
            MEMORYONLY = 0x02,
            ICONONLY = 0x04,
            THUMBNAILONLY = 0x08,
            INCACHEONLY = 0x10
        }

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHCreateItemFromParsingName")]
        private static extern int SHCreateItemFromParsingNameRaw(
            string pszPath, IntPtr pbc, ref Guid riid, out IntPtr ppv);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
                                                   ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAP
        {
            public int bmType;
            public int bmWidth;
            public int bmHeight;
            public int bmWidthBytes;
            public ushort bmPlanes;
            public ushort bmBitsPixel;
            public IntPtr bmBits;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public uint[] bmiColors;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern int GetObject(IntPtr hObject, int nCount, IntPtr lpObject);

        [DllImport("gdi32.dll")]
        private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint uStartScan,
                                            uint cScanLines, IntPtr lpvBits, ref BITMAPINFO lpbmi, uint uUsage);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr pvReserved, int dwCoInit);

        [DllImport("ole32.dll")]
        private static extern void CoUninitialize();
    }
}
