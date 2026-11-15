using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TaskScope;

/// <summary>Small shell icons for executables, cached by path.</summary>
public static class IconCache
{
    static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    const uint SHGFI_ICON = 0x100, SHGFI_SMALLICON = 0x1, SHGFI_USEFILEATTRIBUTES = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80;

    public static ImageSource? Get(string path)
    {
        if (Cache.TryGetValue(path, out var img)) return img;
        img = Load(path.Length > 0 ? path : "generic.exe", path.Length == 0);
        Cache[path] = img;
        return img;
    }

    static ImageSource? Load(string path, bool generic)
    {
        var info = new Native.SHFILEINFO();
        uint flags = SHGFI_ICON | SHGFI_SMALLICON | (generic ? SHGFI_USEFILEATTRIBUTES : 0);
        Native.SHGetFileInfo(path, FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf<Native.SHFILEINFO>(), flags);
        if (info.hIcon == IntPtr.Zero) return generic ? null : Get("");
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
        finally { Native.DestroyIcon(info.hIcon); }
    }

    /// <summary>Draws the app's own icon (a rounded tile with a pulse line).</summary>
    public static BitmapSource AppIcon(int size)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            double s = size;
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x25, 0x6E, 0xD8)), null,
                new Rect(0, 0, s, s), s * 0.2, s * 0.2);
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(s * 0.12, s * 0.58), false, false);
                c.PolyLineTo(new[]
                {
                    new Point(s * 0.34, s * 0.58), new Point(s * 0.44, s * 0.28), new Point(s * 0.58, s * 0.78),
                    new Point(s * 0.68, s * 0.48), new Point(s * 0.88, s * 0.48)
                }, true, true);
            }
            dc.DrawGeometry(null, new Pen(Brushes.White, Math.Max(1.5, s * 0.09))
            { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, g);
        }
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }

    public static System.Drawing.Icon AppTrayIcon()
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(AppIcon(32)));
        using var ms = new MemoryStream();
        enc.Save(ms);
        ms.Position = 0;
        using var bmp = new System.Drawing.Bitmap(ms);
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }
}
