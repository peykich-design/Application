using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace PadMapper.App.Services;

/// <summary>トレイとウィンドウのアイコンをコードで描く（画像ファイルを持たないため）。</summary>
internal static class IconFactory
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>コントローラーの形のアイコン。paused なら灰色。</summary>
    public static Icon Create(bool paused, int size = 32)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            var body = paused ? Color.FromArgb(140, 140, 140) : Color.FromArgb(0, 120, 212);
            float s = size / 32f;
            using var path = new GraphicsPath();
            // 本体（左右のグリップ付きの角丸）
            path.AddEllipse(2 * s, 9 * s, 12 * s, 18 * s);
            path.AddEllipse(18 * s, 9 * s, 12 * s, 18 * s);
            path.AddRectangle(new RectangleF(8 * s, 9 * s, 16 * s, 12 * s));
            using (var brush = new SolidBrush(body)) g.FillPath(brush, path);
            using var white = new SolidBrush(Color.White);
            // 十字キー
            g.FillRectangle(white, 6 * s, 14 * s, 6 * s, 2 * s);
            g.FillRectangle(white, 8 * s, 12 * s, 2 * s, 6 * s);
            // ボタン
            g.FillEllipse(white, 21 * s, 11.5f * s, 3 * s, 3 * s);
            g.FillEllipse(white, 21 * s, 16.5f * s, 3 * s, 3 * s);
            g.FillEllipse(white, 18.5f * s, 14 * s, 3 * s, 3 * s);
            g.FillEllipse(white, 23.5f * s, 14 * s, 3 * s, 3 * s);
        }
        var h = bmp.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(h).Clone();
        }
        finally
        {
            DestroyIcon(h);
        }
    }

    public static BitmapSource CreateImage(int size = 64)
    {
        using var icon = Create(false, size);
        var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        src.Freeze();
        return src;
    }
}
