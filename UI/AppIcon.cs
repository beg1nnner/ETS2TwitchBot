using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace EtsBot;

/// <summary>Иконка приложения рисуется кодом (отдельный .ico-файл не нужен).</summary>
public static class AppIcon
{
    private static Drawing.Bitmap Draw(int size)
    {
        var bmp = new Drawing.Bitmap(size, size, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Drawing.Graphics.FromImage(bmp);
        g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(Drawing.Color.Transparent);

        float r = size * 0.28f;
        float d = r * 2;
        using var path = new Drawing.Drawing2D.GraphicsPath();
        path.AddArc(0, 0, d, d, 180, 90);
        path.AddArc(size - d - 1, 0, d, d, 270, 90);
        path.AddArc(size - d - 1, size - d - 1, d, d, 0, 90);
        path.AddArc(0, size - d - 1, d, d, 90, 90);
        path.CloseFigure();

        using var br = new Drawing.Drawing2D.LinearGradientBrush(
            new Drawing.Rectangle(0, 0, size, size),
            Drawing.Color.FromArgb(124, 92, 255), Drawing.Color.FromArgb(0, 184, 230), 45f);
        g.FillPath(br, path);

        using var font = new Drawing.Font("Segoe UI", size * 0.5f, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
        using var sf = new Drawing.StringFormat
        {
            Alignment = Drawing.StringAlignment.Center,
            LineAlignment = Drawing.StringAlignment.Center,
        };
        g.DrawString("E", font, Drawing.Brushes.White, new Drawing.RectangleF(0, 0, size, size + size * 0.04f), sf);
        return bmp;
    }

    public static Drawing.Icon CreateIcon()
    {
        using var bmp = Draw(32);
        var h = bmp.GetHicon();
        using var tmp = Drawing.Icon.FromHandle(h);
        return (Drawing.Icon)tmp.Clone();
    }

    public static ImageSource CreateImageSource()
    {
        using var bmp = Draw(64);
        using var ms = new MemoryStream();
        bmp.Save(ms, Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;
        var dec = BitmapDecoder.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frame = dec.Frames[0];
        frame.Freeze();
        return frame;
    }
}
