// IcoCheck.cs —— .ico 诊断小工具（开发用，不参与成品）
//
// 什么时候用它：用户说「某个图标不对」（彩色雪花 / 空白 / 糊）时，拿那个 .ico 跑一遍，
// 一次看清「每一帧是什么格式」以及「各种取法分别得到什么」：
//   · new Icon(path, s, s).ToBitmap()  ← 仍在用的**兜底**（不是老写法）：主程序取 .ico 时先自己解 PNG 帧、失败才退到它；它碰上 **PNG 压缩帧**会被 GDI+ 读成彩色噪点，大尺寸还会抛异常
//   · 按 PNG 解帧再缩放              ← 主程序取 .ico 时**先走**这条，解不出来才退回上一条（IconService.DecodeIcoFrame）
// 输出里的「噪声分」= 相邻像素差得离谱的比例：真图标 ≤0.67，被错读的 PNG 帧 0.92~0.99。
//
// 编译（只用系统 csc，不依赖 src）：
//   csc /nologo /target:exe /platform:x64 /codepage:65001 /out:build\icocheck.exe ^
//       /reference:System.dll,System.Drawing.dll tools\IcoCheck.cs
// 用法：icocheck.exe <某个 .ico 路径> <输出目录>
// icochk.cs —— 一次性诊断工具：看清「同一个 .ico 用不同方式取出来」到底差在哪
//   new Icon(path, s, s).ToBitmap()      ← 程序现在走的路（怀疑它在 PNG 压缩帧上出乱码）
//   直接解 PNG 帧（Bitmap(stream)）      ← 正确路线候选
// 用法：icochk.exe <ico 路径> <输出目录>
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

class IcoChk
{
    static void Main(string[] args)
    {
        string path = args[0];
        string outDir = args[1];
        Directory.CreateDirectory(outDir);
        byte[] data = File.ReadAllBytes(path);
        int count = data[4] | (data[5] << 8);
        Console.WriteLine("帧数=" + count + "  文件=" + data.Length + " 字节");

        // 1) 帧清单
        for (int i = 0; i < count; i++)
        {
            int o = 6 + i * 16;
            int w = data[o] == 0 ? 256 : data[o];
            int h = data[o + 1] == 0 ? 256 : data[o + 1];
            int len = BitConverter.ToInt32(data, o + 8);
            int off = BitConverter.ToInt32(data, o + 12);
            bool png = len > 8 && data[off] == 0x89 && data[off + 1] == 0x50;
            Console.WriteLine(string.Format("  帧{0}: {1}x{2} len={3} off={4} png={5}", i, w, h, len, off, png));

            // 2) PNG 帧：直接按 PNG 解出来（native 尺寸）
            if (png)
            {
                try
                {
                    using (MemoryStream ms = new MemoryStream(data, off, len))
                    using (Bitmap b = new Bitmap(ms))
                    {
                        Console.WriteLine("      PNG 帧直解 OK：" + b.Width + "x" + b.Height + " fmt=" + b.PixelFormat);
                        b.Save(Path.Combine(outDir, "pngframe-" + i + "-" + w + ".png"), ImageFormat.Png);
                    }
                }
                catch (Exception ex) { Console.WriteLine("      PNG 帧直解 EX " + ex.Message); }
            }
        }

        // 3) new Icon(path, s, s) —— 主程序仍在用的**兜底**方式（PNG 帧解不出来、或选中的是老式 DIB 帧时才走它），逐个尺寸存图看
        int[] sizes = new int[] { 16, 24, 32, 36, 48, 64, 96, 128, 256 };
        for (int i = 0; i < sizes.Length; i++)
        {
            int s = sizes[i];
            try
            {
                using (Icon ic = new Icon(path, s, s))
                using (Bitmap b = ic.ToBitmap())
                {
                    double noise = NoiseScore(b);
                    Console.WriteLine(string.Format("  new Icon({0}) → {1}x{2}  噪声分={3}", s, b.Width, b.Height, noise));
                    b.Save(Path.Combine(outDir, "newicon-" + s + ".png"), ImageFormat.Png);
                }
            }
            catch (Exception ex) { Console.WriteLine("  new Icon(" + s + ") EX " + ex.Message); }
        }

        // 4) 我们打算改成的方式：挑一个 >= 请求尺寸的 PNG 帧，解出来后高质量缩放到请求尺寸
        int[] want = new int[] { 24, 30, 36, 48 };
        for (int i = 0; i < want.Length; i++)
        {
            int s = want[i];
            Bitmap got = DecodePngFrame(data, count, s);
            if (got == null) { Console.WriteLine("  新路线(" + s + ") 没有可用 PNG 帧"); continue; }
            using (got)
            using (Bitmap dst = new Bitmap(s, s, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(dst))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.Clear(Color.Transparent);
                    g.DrawImage(got, new Rectangle(0, 0, s, s));
                }
                Console.WriteLine(string.Format("  新路线({0}) ← {1}x{2} 帧  结果噪声分={3}", s, got.Width, got.Height, NoiseScore(dst)));
                dst.Save(Path.Combine(outDir, "newway-" + s + ".png"), ImageFormat.Png);
            }
        }
    }

    /// <summary>噪声分：相邻像素「明显不同」的比例（0~1）。乱码图接近 1，真图标（含像素风）远小于 1。</summary>
    static double NoiseScore(Bitmap b)
    {
        int bad = 0, n = 0;
        for (int y = 0; y < b.Height; y++)
            for (int x = 0; x + 1 < b.Width; x++)
            {
                Color p = b.GetPixel(x, y), q = b.GetPixel(x + 1, y);
                if (p.A < 40) continue;
                n++;
                int d = Math.Abs(p.R - q.R) + Math.Abs(p.G - q.G) + Math.Abs(p.B - q.B);
                if (d > 60) bad++;
            }
        return n == 0 ? 0 : Math.Round((double)bad / n, 3);
    }

    /// <summary>挑一个「最大的、且不小于请求尺寸」的 PNG 帧解出来；没有就退到最大的 PNG 帧。</summary>
    static Bitmap DecodePngFrame(byte[] data, int count, int want)
    {
        int bestOff = -1, bestLen = 0, bestW = int.MaxValue;
        int bigOff = -1, bigLen = 0, bigW = 0;
        for (int i = 0; i < count; i++)
        {
            int o = 6 + i * 16;
            int w = data[o] == 0 ? 256 : data[o];
            int len = BitConverter.ToInt32(data, o + 8);
            int off = BitConverter.ToInt32(data, o + 12);
            bool png = len > 8 && data[off] == 0x89 && data[off + 1] == 0x50;
            if (!png) continue;
            if (w >= want && w < bestW) { bestW = w; bestOff = off; bestLen = len; }
            if (w > bigW) { bigW = w; bigOff = off; bigLen = len; }
        }
        if (bestOff < 0) { bestOff = bigOff; bestLen = bigLen; }
        if (bestOff < 0) return null;
        using (MemoryStream ms = new MemoryStream(data, bestOff, bestLen))
            return new Bitmap(ms);
    }
}
