using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EtsBot;

/// <summary>Режим --shots: окна программы сами рисуют себя в PNG в высоком разрешении (для документации).</summary>
public static class ShotHelper
{
    public static async Task Snap(FrameworkElement el, string path, double scale = 2.0, int delayMs = 900, Brush? bg = null)
    {
        await Task.Delay(delayMs);
        el.UpdateLayout();
        int w = (int)Math.Ceiling(el.ActualWidth * scale);
        int h = (int)Math.Ceiling(el.ActualHeight * scale);
        var rtb = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        if (bg != null)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) dc.DrawRectangle(bg, null, new Rect(0, 0, el.ActualWidth, el.ActualHeight));
            rtb.Render(dv);
        }
        rtb.Render(el);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
