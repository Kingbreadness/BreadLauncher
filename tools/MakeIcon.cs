// MakeIcon.cs —— 生成软件图标（assets\BreadLauncher.ico）
//
// 为什么要自己生成：任务栏固定入口只有图标好看才像回事，随手截的图当图标会糊。
// 这里按 Windows 图标规范写多尺寸 ICO（16/32/48/64/256，32 位带透明通道的 DIB），
// 每个尺寸都重新绘制，小尺寸不打折。生成一次即可，编译脚本会在缺图标时自动跑。

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

internal static class MakeIcon
{
    private static int[] Sizes = new int[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

    private static int Main(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : "BreadLauncher.ico";
        try
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using (FileStream fs = new FileStream(outPath, FileMode.Create, FileAccess.Write))
            using (BinaryWriter w = new BinaryWriter(fs))
            {
                w.Write((ushort)0);              // reserved
                w.Write((ushort)1);              // type = icon
                w.Write((ushort)Sizes.Length);   // count

                long entryStart = fs.Position;
                for (int i = 0; i < Sizes.Length; i++)
                {
                    w.Write((byte)(Sizes[i] >= 256 ? 0 : Sizes[i]));
                    w.Write((byte)(Sizes[i] >= 256 ? 0 : Sizes[i]));
                    w.Write((byte)0);            // colors
                    w.Write((byte)0);            // reserved
                    w.Write((ushort)1);          // planes
                    w.Write((ushort)32);         // bpp
                    w.Write((uint)0);            // size, 稍后回填
                    w.Write((uint)0);            // offset, 稍后回填
                }

                long[] sizePos = new long[Sizes.Length];
                long[] offsetPos = new long[Sizes.Length];
                for (int i = 0; i < Sizes.Length; i++)
                {
                    sizePos[i] = entryStart + i * 16 + 8;
                    offsetPos[i] = entryStart + i * 16 + 12;
                }

                for (int i = 0; i < Sizes.Length; i++)
                {
                    byte[] dib = BuildDib(Sizes[i]);
                    long offset = fs.Position;
                    w.Write(dib);
                    long here = fs.Position;
                    fs.Position = sizePos[i];
                    w.Write((uint)dib.Length);
                    fs.Position = offsetPos[i];
                    w.Write((uint)offset);
                    fs.Position = here;
                }
            }
            Console.WriteLine("图标已生成：" + Path.GetFullPath(outPath));
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("生成图标失败：" + ex.Message);
            return 1;
        }
    }

    /// <summary>画一个 size×size 的图标，转成 32bpp 的 DIB（BITMAPINFOHEADER + BGRA + AND 掩码）。</summary>
    private static byte[] BuildDib(int size)
    {
        using (Bitmap bmp = Render(size))
        {
            int rowBytes = size * 4;
            int maskRow = ((size + 31) / 32) * 4;
            int headerSize = 40;
            int dataSize = headerSize + rowBytes * size + maskRow * size;
            byte[] buf = new byte[dataSize];

            // BITMAPINFOHEADER
            WriteInt(buf, 0, headerSize);
            WriteInt(buf, 4, size);
            WriteInt(buf, 8, size * 2);   // XOR + AND 两层高度
            WriteShort(buf, 12, 1);
            WriteShort(buf, 14, 32);
            WriteInt(buf, 16, 0);         // BI_RGB
            WriteInt(buf, 20, rowBytes * size);

            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = bd.Stride;
                byte[] row = new byte[Math.Abs(stride)];
                for (int y = 0; y < size; y++)
                {
                    // DIB 是自下而上
                    IntPtr src = new IntPtr(bd.Scan0.ToInt64() + (long)(size - 1 - y) * stride);
                    System.Runtime.InteropServices.Marshal.Copy(src, row, 0, row.Length);
                    int dst = headerSize + y * rowBytes;
                    for (int x = 0; x < size; x++)
                    {
                        // 源是 BGRA（GDI+ 的 32bppArgb 在内存里就是 BGRA），DIB 也是 BGRA，直接拷
                        buf[dst + x * 4 + 0] = row[x * 4 + 0];
                        buf[dst + x * 4 + 1] = row[x * 4 + 1];
                        buf[dst + x * 4 + 2] = row[x * 4 + 2];
                        buf[dst + x * 4 + 3] = row[x * 4 + 3];
                    }
                }
            }
            finally
            {
                bmp.UnlockBits(bd);
            }
            // AND 掩码全 0（不透明），透明部分靠 alpha 通道
            return buf;
        }
    }

    private static Bitmap Render(int size)
    {
        Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);

            float radius = size * 0.22f;
            using (GraphicsPath path = Round(new RectangleF(0, 0, size, size), radius))
            using (LinearGradientBrush bg = new LinearGradientBrush(new RectangleF(0, 0, size, size),
                       Color.FromArgb(255, 232, 182, 116), Color.FromArgb(255, 156, 88, 40), LinearGradientMode.Vertical))
            {
                g.FillPath(bg, path);
            }

            // 2×2 的奶油白圆角方块，像「应用宫格」，小尺寸也认得出
            // （配色：外皮 = 面包烤色渐变 浅金→焦糖棕，中间 = 奶油白；以前是蓝底白块）
            float pad = size * 0.26f;
            float gap = size * 0.08f;
            float cell = (size - pad * 2 - gap) / 2f;
            float cr = Math.Max(1f, cell * 0.28f);
            using (SolidBrush fg = new SolidBrush(Color.FromArgb(255, 255, 249, 233)))
            {
                for (int i = 0; i < 4; i++)
                {
                    float x = pad + (i % 2) * (cell + gap);
                    float y = pad + (i / 2) * (cell + gap);
                    using (GraphicsPath p = Round(new RectangleF(x, y, cell, cell), cr))
                        g.FillPath(fg, p);
                }
            }
        }
        return bmp;
    }

    private static GraphicsPath Round(RectangleF r, float radius)
    {
        GraphicsPath p = new GraphicsPath();
        float d = Math.Max(0.5f, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private static void WriteInt(byte[] b, int off, int v)
    {
        b[off] = (byte)(v & 0xFF); b[off + 1] = (byte)((v >> 8) & 0xFF);
        b[off + 2] = (byte)((v >> 16) & 0xFF); b[off + 3] = (byte)((v >> 24) & 0xFF);
    }

    private static void WriteShort(byte[] b, int off, int v)
    {
        b[off] = (byte)(v & 0xFF); b[off + 1] = (byte)((v >> 8) & 0xFF);
    }
}