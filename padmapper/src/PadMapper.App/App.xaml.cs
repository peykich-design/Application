using System.Windows;
using System.Windows.Threading;
using PadMapper.App.Services;
using PadMapper.App.Views;
using PadMapper.Core.Logging;
using PadMapper.Core.Profiles;
using PadMapper.Core.Startup;

namespace PadMapper.App;

/// <summary>
/// 起動の流れ：
/// 1. 自動起動タスクの登録/削除だけを頼まれた場合（UAC で昇格して呼ばれる）はそれだけ行って終了
/// 2. 二重起動なら既存のウィンドウを前面に出してもらって終了
/// 3. 昇格しておらず、自動起動タスクが登録済みなら、タスク経由で（UAC なしで）管理者として起動し直す
/// 4. 本体を起動。--autostart のときはトレイに常駐するだけ
/// </summary>
public partial class App : Application
{
    private const string ShowFlagFile = "show-window.flag";
    private SingleInstance? _instance;
    private FileLogger? _log;
    private AppHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;

        string? smokeDir = null;
        int si = Array.IndexOf(args, "--smoke-test");
        if (si >= 0) smokeDir = si + 1 < args.Length ? args[si + 1] : Path.Combine(Path.GetTempPath(), "padmapper-smoke");
        bool smoke = smokeDir != null;

        var baseDir = smoke ? Path.Combine(smokeDir!, "appdata") : ProfileStore.DefaultBaseDir();
        Directory.CreateDirectory(baseDir);
        _log = new FileLogger(Path.Combine(baseDir, "logs"));
        _log.Info($"起動 args=[{string.Join(" ", args)}] version={typeof(App).Assembly.GetName().Version}");
        HookExceptions();

        // 1. 自動起動タスクの登録・削除（昇格したプロセスとして呼ばれる）
        if (args.Contains(StartupTask.RegisterArg))
        {
            Shutdown(StartupTask.Register(_log) ? 0 : 1);
            return;
        }
        if (args.Contains(StartupTask.UnregisterArg))
        {
            Shutdown(StartupTask.Unregister(_log) ? 0 : 1);
            return;
        }

        if (!smoke)
        {
            // 2. 二重起動の防止
            _instance = SingleInstance.TryAcquire();
            if (_instance == null)
            {
                _log.Info("既に起動しています。既存のウィンドウを表示します");
                SingleInstance.SignalExisting();
                Shutdown(0);
                return;
            }

            // 3. タスク経由で管理者として起動し直す
            if (!StartupTask.IsElevated && !args.Contains("--no-elevate")
                && StartupTask.Query(out _) == StartupTask.TaskState.Registered)
            {
                bool wantWindow = !args.Contains(TaskXmlBuilder.AutostartArgument);
                if (wantWindow) TryWrite(Path.Combine(baseDir, ShowFlagFile));
                _instance.Release();
                if (StartupTask.RunNow(_log))
                {
                    _log.Info("タスク経由で管理者として起動し直します");
                    Shutdown(0);
                    return;
                }
                _instance = SingleInstance.TryAcquire();
                if (_instance == null)
                {
                    Shutdown(0);
                    return;
                }
            }
        }

        // 4. 本体
        WheelForwarding.Register();
        var store = new ProfileStore(baseDir, _log);
        _host = new AppHost(_log, store, smoke);
        Loc.Apply(_host.Settings.Language);
        _host.Start();

        var window = new MainWindow();
        _host.MainWindow = window;
        bool autostart = args.Contains(TaskXmlBuilder.AutostartArgument);
        bool show = smoke || !autostart || ConsumeFlag(Path.Combine(baseDir, ShowFlagFile));
        if (show) window.Show();
        _instance?.ListenForShowRequests(() => Dispatcher.BeginInvoke(() => _host.ShowMainWindow()));

        if (smoke) _ = SmokeTest.RunAsync(window, _host, smokeDir!, _log);
    }

    /// <summary>タスク経由で（UAC なしで）管理者として起動し直し、このプロセスは終了する。</summary>
    internal bool RestartViaTask()
    {
        if (_log == null || _host == null) return false;
        TryWrite(Path.Combine(_host.Store.BaseDir, ShowFlagFile));
        _instance?.Release();
        if (!StartupTask.RunNow(_log))
        {
            _instance = SingleInstance.TryAcquire();
            return false;
        }
        _host.Exit();
        return true;
    }

    private void HookExceptions()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            Crash("UI スレッドの未処理例外", e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Crash("未処理例外", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            _log?.Error("未観測のタスク例外", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>未処理例外：ログに残し、押されている出力を離してから終了する（再起動はタスクスケジューラに任せる）。</summary>
    private void Crash(string what, Exception? ex)
    {
        try
        {
            _log?.Error(what, ex);
            _host?.Dispose();
            _log?.Dispose();
        }
        finally
        {
            Environment.Exit(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        _log?.Dispose();
        base.OnExit(e);
    }

    private static void TryWrite(string path)
    {
        try { File.WriteAllText(path, DateTime.UtcNow.ToString("O")); } catch { }
    }

    /// <summary>「ウィンドウを表示して」の目印（1分以内に作られたもの）があれば消して true。</summary>
    private static bool ConsumeFlag(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            bool fresh = DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromMinutes(1);
            File.Delete(path);
            return fresh;
        }
        catch
        {
            return false;
        }
    }
}
