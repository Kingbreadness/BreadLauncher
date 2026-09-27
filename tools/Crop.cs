using System; using System.Drawing; using System.Drawing.Drawing2D; using System.Drawing.Imaging; using System.IO;
class Crop {
  static void Main(string[] a) {
    try {
      using (Bitmap b = new Bitmap(a[0])) {
        int x=int.Parse(a[3]), y=int.Parse(a[4]), w=int.Parse(a[5]), h=int.Parse(a[6]), s=int.Parse(a[7]);
        w=Math.Min(w, b.Width-x); h=Math.Min(h, b.Height-y);
        using (Bitmap o = new Bitmap(w*s, h*s, PixelFormat.Format32bppArgb))
        using (Graphics g = Graphics.FromImage(o)) {
          g.Clear(Color.White);
          g.InterpolationMode = s > 2 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
          g.PixelOffsetMode = s > 2 ? PixelOffsetMode.Half : PixelOffsetMode.HighQuality;
          using (Bitmap sub = b.Clone(new Rectangle(x,y,w,h), PixelFormat.Format32bppArgb))
            g.DrawImage(sub, new Rectangle(0,0,w*s,h*s));
          o.Save(a[2], ImageFormat.Png);
        }
      }
      File.WriteAllText(a[2] + ".ok", "ok");
    } catch (Exception ex) { File.WriteAllText(a[2] + ".err", ex.ToString()); }
  }
}
