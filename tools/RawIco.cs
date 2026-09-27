using System; using System.Drawing; using System.Drawing.Imaging; using System.IO; using System.Runtime.InteropServices;
class RawIco {
  [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int cb, IntPtr pv);
  [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, IntPtr bits, ref BITMAPINFO bmi, uint usage);
  [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
  [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
  [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
  [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
  [StructLayout(LayoutKind.Sequential)] struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }
  [StructLayout(LayoutKind.Sequential)] struct BITMAPINFOHEADER { public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant; }
  [StructLayout(LayoutKind.Sequential)] struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; [MarshalAs(UnmanagedType.ByValArray, SizeConst=256)] public uint[] bmiColors; }

  static void Main(string[] a) {
    string path = a[0]; string outDir = a[1]; Directory.CreateDirectory(outDir);
    if (path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)) DumpIco(path, outDir);
    else Console.WriteLine("(exe 需要走资源提取，另行处理)");
  }

  static void DumpIco(string path, string outDir) {
    byte[] d = File.ReadAllBytes(path);
    int n = d[4] | (d[5] << 8);
    for (int i = 0; i < n; i++) {
      int o = 6 + i*16;
      int w = d[o]==0?256:d[o], h = d[o+1]==0?256:d[o+1];
      int len = BitConverter.ToInt32(d, o+8), off = BitConverter.ToInt32(d, o+12);
      bool png = d[off]==0x89 && d[off+1]==0x50;
      if (w != h) { continue; }
      if (png) {
        byte[] sub = new byte[len]; Array.Copy(d, off, sub, 0, len);
        using (MemoryStream ms = new MemoryStream(sub)) using (Bitmap b = new Bitmap(ms)) {
          Console.WriteLine(string.Format("raw {0}px PNG  fmt={1}", w, b.PixelFormat));
          Stats(b, "raw-" + w + "-png");
          b.Save(Path.Combine(outDir, "raw-" + w + ".png"), ImageFormat.Png);
        }
        continue;
      }
      // DIB：40 字节头 + XOR 位图 + AND 掩码
      int stride = w * 4;
      IntPtr bits = Marshal.AllocHGlobal(stride * h);
      try {
        for (int y = 0; y < h; y++)                     // ICO 里是自下而上
          Marshal.Copy(d, off + 40 + y*stride, (IntPtr)((long)bits + (h-1-y)*stride), stride);
        using (Bitmap b = new Bitmap(w, h, stride, PixelFormat.Format32bppArgb, bits)) {
          Bitmap copy = new Bitmap(b);
          Console.WriteLine(string.Format("raw {0}px DIB  fmt={1}", w, copy.PixelFormat));
          Stats(copy, "raw-" + w + "-dib");
          copy.Save(Path.Combine(outDir, "raw-" + w + ".png"), ImageFormat.Png);
          copy.Dispose();
        }
      } finally { Marshal.FreeHGlobal(bits); }
    }
  }

  static void Stats(Bitmap b, string tag) {
    int t=0,p=0,op=0;
    for(int y=0;y<b.Height;y++) for(int x=0;x<b.Width;x++){ int al=b.GetPixel(x,y).A; if(al==0)t++; else if(al==255)op++; else p++; }
    Console.WriteLine(string.Format("   {0}: 全透明={1} 半透明={2} 不透明={3} | TL={4} TR={5} 中心={6}", tag, t,p,op, b.GetPixel(0,0), b.GetPixel(b.Width-1,0), b.GetPixel(b.Width/2,b.Height/2)));
  }
}
