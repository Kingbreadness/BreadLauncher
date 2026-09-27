using System; using System.Drawing; using System.Drawing.Imaging; using System.IO; using System.Runtime.InteropServices;
class AlphaTest {
  [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
  [StructLayout(LayoutKind.Sequential)] struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }
  [StructLayout(LayoutKind.Sequential)] struct BMIH { public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant; }
  [StructLayout(LayoutKind.Sequential)] struct BMI { public BMIH h; [MarshalAs(UnmanagedType.ByValArray, SizeConst=256)] public uint[] c; }
  [Flags] enum SIIGBF { RESIZETOFIT=0, BIGGERSIZEOK=1, MEMORYONLY=2, ICONONLY=4, THUMBNAILONLY=8, INCACHEONLY=0x10 }
  [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface ISIIF { [PreserveSig] int GetImage(SIZE s, SIIGBF f, out IntPtr phbm); }
  [DllImport("shell32.dll", CharSet=CharSet.Unicode)] static extern int SHCreateItemFromParsingName(string p, IntPtr b, ref Guid iid, out IntPtr ppv);
  [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
  [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int cb, IntPtr pv);
  [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, IntPtr bits, ref BMI bmi, uint usage);
  [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
  [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

  static void Main(string[] a) {
    string path=a[0], outDir=a[1]; int size=int.Parse(a[2]); Directory.CreateDirectory(outDir);
    IntPtr hbm=IntPtr.Zero; string tag=Path.GetFileNameWithoutExtension(path);
    try {
      Guid iid=new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"); IntPtr ppv;
      int hr=SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out ppv);
      if (hr!=0) { Console.WriteLine(tag+": SHCreate hr=0x"+hr.ToString("X8")); return; }
      ISIIF f=(ISIIF)Marshal.GetObjectForIUnknown(ppv); Marshal.Release(ppv);
      SIZE s; s.cx=size; s.cy=size;
      hr=f.GetImage(s, SIIGBF.ICONONLY|SIIGBF.RESIZETOFIT, out hbm);
      Marshal.ReleaseComObject(f);
      if (hr!=0||hbm==IntPtr.Zero) { Console.WriteLine(tag+": GetImage hr=0x"+hr.ToString("X8")); return; }
      using (Bitmap b=KeepAlpha(hbm)) { Report(tag, b); b.Save(Path.Combine(outDir, tag+"-keepalpha.png"), ImageFormat.Png); }
    } catch(Exception ex) { Console.WriteLine(tag+": EX "+ex.Message); }
    finally { if(hbm!=IntPtr.Zero) DeleteObject(hbm); }
  }
  static void Report(string tag, Bitmap b) {
    int t=0,p=0,op=0; for(int y=0;y<b.Height;y++) for(int x=0;x<b.Width;x++){int al=b.GetPixel(x,y).A; if(al==0)t++; else if(al==255)op++; else p++;}
    Console.WriteLine(string.Format("{0}: {1}x{2} fmt={3} 全透明={4} 半透明={5} 不透明={6} | TL={7} 中心={8}", tag,b.Width,b.Height,b.PixelFormat,t,p,op,b.GetPixel(0,0),b.GetPixel(b.Width/2,b.Height/2)));
  }
  static Bitmap KeepAlpha(IntPtr hbm) {
    IntPtr hdr=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(BITMAP)));
    BITMAP bm;
    try { GetObject(hbm, Marshal.SizeOf(typeof(BITMAP)), hdr); bm=(BITMAP)Marshal.PtrToStructure(hdr, typeof(BITMAP)); }
    finally { Marshal.FreeHGlobal(hdr); }
    int w=bm.bmWidth, h=Math.Abs(bm.bmHeight), stride=w*4;
    BMI bmi=new BMI(); bmi.c=new uint[256];
    bmi.h.biSize=40; bmi.h.biWidth=w; bmi.h.biHeight=-h; bmi.h.biPlanes=1; bmi.h.biBitCount=32; bmi.h.biCompression=0;
    IntPtr hdc=GetDC(IntPtr.Zero); IntPtr bits=Marshal.AllocHGlobal(stride*h);
    try {
      int ok=GetDIBits(hdc, hbm, 0, (uint)h, bits, ref bmi, 0);
      if (ok==0) throw new Exception("GetDIBits=0 (bm="+w+"x"+bm.bmHeight+" bpp="+bm.bmBitsPixel+")");
      using (Bitmap tmp=new Bitmap(w,h,stride,PixelFormat.Format32bppArgb,bits)) return new Bitmap(tmp);
    } finally { Marshal.FreeHGlobal(bits); ReleaseDC(IntPtr.Zero,hdc); }
  }
}
