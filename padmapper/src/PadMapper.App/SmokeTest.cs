using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PadMapper.App.Views;
using PadMapper.App.Views.Pages;
using PadMapper.Core.Logging;

namespace PadMapper.App;

/// <summary>
/// CI 用の起動確認（--smoke-test &lt;出力先&gt;）。全ページを開いて画像に保存し、オーバーレイも表示してから終了する。
/// 実際のキー入力は送らない。結果は smoke-result.txt（成功なら "OK"）。
/// </summary>
internal static class SmokeTest
{
    public static async Task RunAsync(MainWindow window, AppHost host, string outDir, FileLogger log)
    {
        Directory.CreateDirectory(outDir);
        var result = Path.Combine(outDir, "smoke-result.txt");
        var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        watchdog.Token.Register(() =>
        {
            File.WriteAllText(result, "TIMEOUT");
            Environment.Exit(2);
        });
        try
        {
            await Task.Delay(1500);
            var pages = new (Type type, string name)[]
            {
                (typeof(HomePage), "home"), (typeof(ProfilesPage), "profiles"), (typeof(GyroPage), "gyro"),
                (typeof(AppsPage), "apps"), (typeof(DiagnosticsPage), "diagnostics"), (typeof(SettingsPage), "settings"),
            };
            foreach (var lang in new[] { "ja", "en" })
            {
                Services.Loc.Apply(lang);
                foreach (var (type, name) in pages)
                {
                    window.NavigateTo(type);
                    await Task.Delay(800);
                    Save(window, Path.Combine(outDir, $"{lang}-{name}.png"));
                }
            }
            Services.Loc.Apply("ja");

            host.Overlay?.Open();
            await Task.Delay(500);
            if (host.Overlay != null) Save(host.Overlay, Path.Combine(outDir, "overlay.png"));
            host.Overlay?.Close2();

            var problems = log.RecentProblems.Where(p => p.Level == LogLevel.Error).ToList();
            File.WriteAllText(result, problems.Count == 0
                ? "OK"
                : "ERRORS\n" + string.Join("\n", problems.Select(p => p.Message)));
            log.Info("スモークテスト完了");
            host.Dispose();
            Application.Current.Shutdown(problems.Count == 0 ? 0 : 3);
        }
        catch (Exception ex)
        {
            log.Error("スモークテスト失敗", ex);
            File.WriteAllText(result, "FAILED\n" + ex);
            Application.Current.Shutdown(1);
        }
    }

    /// <summary>ウィンドウの中身を背景色付きで PNG に保存する（Mica の背景は画像に写らないため塗りつぶす）。</summary>
    private static void Save(Window window, string path)
    {
        if (window.Content is not FrameworkElement root || root.ActualWidth < 1 || root.ActualHeight < 1) return;
        var dpi = VisualTreeHelper.GetDpi(window);
        int w = (int)(root.ActualWidth * dpi.DpiScaleX), h = (int)(root.ActualHeight * dpi.DpiScaleY);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var bg = Application.Current.TryFindResource("ApplicationBackgroundBrush") as Brush ?? Brushes.DimGray;
            var rect = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
            dc.DrawRectangle(bg, null, rect);
            dc.DrawRectangle(new VisualBrush(root), null, rect);
        }
        var bmp = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
