using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

class IconProbe
{
    const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0;
    const uint SHGFI_SYSICONINDEX = 0x4000, SHGFI_SMALLICON = 1;
    static string OutDir;

    [StructLayout(LayoutKind.Sequential)]
    struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO { public IntPtr hIcon; public int iIcon; public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName; }
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER { public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant; }
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public uint[] bmiColors; }
    [Flags] enum SIIGBF { RESIZETOFIT = 0, BIGGERSIZEOK = 1, MEMORYONLY = 2, ICONONLY = 4, THUMBNAILONLY = 8, INCACHEONLY = 0x10 }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm); }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHCreateItemFromParsingName(string p, IntPtr b, ref Guid iid, out IntPtr ppv);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SHGetFileInfo(string p, uint attr, ref SHFILEINFO psfi, uint cb, uint flags);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, IntPtr bits, ref BITMAPINFO bmi, uint usage);
    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int cb, IntPtr pv);
    [DllImport("user32.dll")] static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int cx, int cy, uint istep, IntPtr hbrFlicker, uint flags);

    static void Main(string[] args)
    {
        string path = args[0];
        OutDir = args[1];
        int size = args.Length > 2 ? int.Parse(args[2]) : 64;
        Directory.CreateDirectory(OutDir);
        Console.WriteLine("=== " + path + "  (" + (File.Exists(path) ? new FileInfo(path).Length + " bytes" : "MISSING") + ")");

        // 0) 源文件是不是 PNG 压缩的 ICO？
        if (path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)) ProbeIcoFile(path, size);

        // 1) Icon.ExtractAssociatedIcon
        try {
            using (Icon ic = Icon.ExtractAssociatedIcon(path)) {
                if (ic != null) { using (Bitmap b = ic.ToBitmap()) { Report("1-ExtractAssociatedIcon", b, size, true); } }
            }
        } catch (Exception ex) { Console.WriteLine("1-ExtractAssociatedIcon EX " + ex.Message); }

        // 2) new Icon(path,size,size)
        try {
            using (Icon ic = new Icon(path, size, size)) { using (Bitmap b = ic.ToBitmap()) { Report("2-newIcon", b, size, true); } }
        } catch (Exception ex) { Console.WriteLine("2-newIcon EX " + ex.Message); }

        // 3) ShellItemImageFactory -> Image.FromHbitmap  (已降级为**兜底**：仅「老式无 alpha 位图」才走它 ——
        //    Image.FromHbitmap 会把 alpha 通道扔掉，原本透明的地方变成不透明的纯黑)
        // 4) ShellItemImageFactory -> FromHbitmapKeepAlpha  (当前代码路径，见 IconService.FromHbitmapKeepAlpha)
        IntPtr hbm = IntPtr.Zero;
        try {
            Guid iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
            IntPtr ppv;
            int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out ppv);
            if (hr == 0 && ppv != IntPtr.Zero) {
                IShellItemImageFactory f = (IShellItemImageFactory)Marshal.GetObjectForIUnknown(ppv);
                Marshal.Release(ppv);
                SIZE s; s.cx = size; s.cy = size;
                hr = f.GetImage(s, SIIGBF.ICONONLY | SIIGBF.RESIZETOFIT, out hbm);
                Marshal.ReleaseComObject(f);
            }
            if (hbm != IntPtr.Zero) {
                using (Bitmap b = Image.FromHbitmap(hbm)) Report("3-ShellImage-FromHbitmap", b, size, true);
                using (Bitmap b = FromHbitmapKeepAlpha(hbm)) Report("4-ShellImage-KeepAlpha", b, size, true);
            } else Console.WriteLine("3-ShellImage: hbm=0 hr=0x" + hr.ToString("X8"));
        } catch (Exception ex) { Console.WriteLine("3/4 EX " + ex.Message); }
        finally { if (hbm != IntPtr.Zero) DeleteObject(hbm); }

        // 5) SHGetFileInfo HICON -> 自己画到 32bpp DIB（标准做法）
        try {
            SHFILEINFO info = new SHFILEINFO();
            IntPtr ret = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(typeof(SHFILEINFO)), SHGFI_ICON | SHGFI_LARGEICON);
            if (info.hIcon != IntPtr.Zero) {
                using (Bitmap b = DrawHIconToDib(info.hIcon, size)) Report("5-SHGetFileInfo-DIB", b, size, true);
                // ★故意不 DestroyIcon：SHGetFileInfo 给的常是系统共享句柄，销毁它会把系统图标缓存弄坏，
                //   之后一批取图开始报「对象当前正在其他地方使用」。这是开发探针、用完即退，漏一个句柄无所谓。
                // DestroyIcon(info.hIcon);
            } else Console.WriteLine("5-SHGetFileInfo: no icon ret=" + ret);
        } catch (Exception ex) { Console.WriteLine("5 EX " + ex.Message); }
    }

    static void ProbeIcoFile(string path, int size)
    {
        try {
            byte[] data = File.ReadAllBytes(path);
            if (data.Length < 6) return;
            int count = data[4] | (data[5] << 8);
            Console.WriteLine("ICO 帧数=" + count);
            int bestOff = -1, bestW = 0; bool bestPng = false;
            for (int i = 0; i < count; i++) {
                int o = 6 + i * 16;
                if (o + 16 > data.Length) break;
                int w = data[o] == 0 ? 256 : data[o];
                int h = data[o + 1] == 0 ? 256 : data[o + 1];
                ushort bpp = (ushort)(data[o + 6] | (data[o + 7] << 8));
                int len = BitConverter.ToInt32(data, o + 8);
                int off = BitConverter.ToInt32(data, o + 12);
                bool png = len > 8 && data[off] == 0x89 && data[off + 1] == 0x50;
                Console.WriteLine(string.Format("   帧{0}: {1}x{2} bpp={3} len={4} off={5} png={6}", i, w, h, bpp, len, off, png));
                if (w >= bestW && w <= 256) { bestW = w; bestOff = off; bestPng = png; }
            }
            if (bestOff >= 0 && bestPng) {
                int len = BitConverter.ToInt32(data, bestOff - 4);
                using (MemoryStream ms = new MemoryStream(data, bestOff, len))
                using (Bitmap b = new Bitmap(ms)) Report("6-IcoPngDirect", b, size, true);
            }
        } catch (Exception ex) { Console.WriteLine("6-IcoPngDirect EX " + ex.Message); }
    }

    static void Report(string tag, Bitmap b, int size, bool save)
    {
        int transparent = 0, partial = 0, opaque = 0;
        for (int y = 0; y < b.Height; y++) for (int x = 0; x < b.Width; x++) {
            int a = b.GetPixel(x, y).A;
            if (a == 0) transparent++; else if (a == 255) opaque++; else partial++;
        }
        Color c0 = b.GetPixel(0, 0), c1 = b.GetPixel(b.Width - 1, 0), cm = b.GetPixel(b.Width / 2, b.Height / 2);
        Console.WriteLine(string.Format("   {0}: {1}x{2} fmt={3} 全透明={4} 半透明={5} 不透明={6} | TL={7} TR={8} 中心={9}",
            tag, b.Width, b.Height, b.PixelFormat, transparent, partial, opaque, c0, c1, cm));
        if (save) {
            using (Bitmap dst = new Bitmap(size, size, PixelFormat.Format32bppArgb)) {
                using (Graphics g = Graphics.FromImage(dst)) {
                    g.Clear(Color.Transparent);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(b, new Rectangle(0, 0, size, size));
                }
                dst.Save(Path.Combine(OutDir, tag + ".png"), ImageFormat.Png);
            }
        }
    }

    // 把 HBITMAP 的像素按原样（含 alpha 字节）读出来，绕开 FromHbitmap 丢 alpha 的老毛病
    static Bitmap FromHbitmapKeepAlpha(IntPtr hbm)
    {
        IntPtr hdc = GetDC(IntPtr.Zero);
        try {
            BITMAPINFO bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = 40;
            bmi.bmiHeader.biWidth = 0; bmi.bmiHeader.biHeight = 0;
            bmi.bmiHeader.biPlanes = 1; bmi.bmiHeader.biBitCount = 0;
            IntPtr hdr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(BITMAPINFOHEADER)));
            try {
                GetObject(hbm, Marshal.SizeOf(typeof(BITMAPINFOHEADER)), hdr);
                BITMAPINFOHEADER bi = (BITMAPINFOHEADER)Marshal.PtrToStructure(hdr, typeof(BITMAPINFOHEADER));
                bmi.bmiHeader = bi;
                int w = bi.biWidth, h = Math.Abs(bi.biHeight);
                bmi.bmiHeader.biWidth = w;
                bmi.bmiHeader.biHeight = -h;   // top-down
                bmi.bmiHeader.biBitCount = 32;
                bmi.bmiHeader.biCompression = 0;
                bmi.bmiHeader.biSizeImage = 0;
                int stride = w * 4;
                IntPtr bits = Marshal.AllocHGlobal(stride * h);
                try {
                    int ok = GetDIBits(hdc, hbm, 0, (uint)h, bits, ref bmi, 0);
                    if (ok == 0) throw new Exception("GetDIBits=0");
                    using (Bitmap tmp = new Bitmap(w, h, stride, PixelFormat.Format32bppArgb, bits)) {
                        return new Bitmap(tmp);
                    }
                } finally { Marshal.FreeHGlobal(bits); }
            } finally { Marshal.FreeHGlobal(hdr); }
        } finally { ReleaseDC(IntPtr.Zero, hdc); }
    }

    // 经典路线：32bpp DIB + DrawIconEx，图标自带 alpha 时能保住
    static Bitmap DrawHIconToDib(IntPtr hIcon, int size)
    {
        Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp)) {
            g.Clear(Color.Transparent);
            IntPtr hdc = g.GetHdc();
            try { DrawIconEx(hdc, 0, 0, hIcon, size, size, 0, IntPtr.Zero, 3 /*DI_NORMAL*/); }
            finally { g.ReleaseHdc(hdc); }
        }
        return bmp;
    }
}
