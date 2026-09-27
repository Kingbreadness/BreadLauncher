// cliptest.cs —— 一次性实验：TextRenderer（GDI）到底认不认 GDI+ 的裁剪区？
// 目的：解释「面板页脚上冒出一个本该被裁掉的加号」。用法：cliptest.exe <输出图>
using System;
using System.Drawing;
using System.Windows.Forms;

class ClipTest
{
    static void Main(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : "cliptest.png";
        using (Bitmap b = new Bitmap(240, 120))
        {
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(Color.Black);
                // 裁剪区：只留上面 40 像素
                g.SetClip(new Rectangle(0, 0, 240, 40), System.Drawing.Drawing2D.CombineMode.Replace);

                // ① GDI+ 画一个填充矩形（落在裁剪区外）
                using (SolidBrush br = new SolidBrush(Color.Red))
                    g.FillRectangle(br, 0, 60, 240, 30);

                // ② GDI 画文字（空格子那个加号走的就是这条路）
                Font f = null;
                try { f = new Font("Segoe MDL2 Assets", 18f); }
                catch (Exception) { f = new Font("Segoe UI", 18f); }
                TextRenderer.DrawText(g, "\uE710", f, new Rectangle(160, 60, 40, 40), Color.White,
                    TextFormatFlags.NoPrefix | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

                // ③ 对照组：同样的文字，画在裁剪区内
                TextRenderer.DrawText(g, "\uE710", f, new Rectangle(20, 0, 40, 40), Color.Lime,
                    TextFormatFlags.NoPrefix | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

                g.ResetClip();
            }
            b.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);

            // 统计裁剪区外（y>=60）还剩什么
            int red = 0, white = 0, lime = 0, limeIn = 0;
            for (int y = 0; y < b.Height; y++)
                for (int x = 0; x < b.Width; x++)
                {
                    Color c = b.GetPixel(x, y);
                    bool outside = y >= 60;
                    if (outside && c.R > 150 && c.G < 80) red++;
                    if (outside && c.R > 150 && c.G > 150 && c.B > 150) white++;
                    if (c.G > 150 && c.R < 80 && c.B < 80) { if (outside) lime++; else limeIn++; }
                }
            Console.WriteLine("裁剪区外（y>=60）：GDI+ 红块像素=" + red + "  GDI 文本像素=" + white);
            Console.WriteLine("对照组（裁剪区内）绿文本像素=" + limeIn + "；裁剪区外的绿=" + lime);
            Console.WriteLine(red == 0 ? "→ GDI+ 的绘制**遵守**裁剪" : "→ GDI+ 的绘制**不遵守**裁剪");
            Console.WriteLine(white == 0 ? "→ TextRenderer 也**遵守**裁剪" : "→ ★TextRenderer **不遵守**裁剪（这就是页脚加号的来源）");
        }
    }
}
