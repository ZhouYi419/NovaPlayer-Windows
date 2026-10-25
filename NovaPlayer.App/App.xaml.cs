using System.IO;
using System.Windows;
using NovaPlayer.Infrastructure.Playback;
using NovaPlayer.App.Services;
using Serilog;
using NovaPlayer.Infrastructure.Database;
using NovaPlayer.Infrastructure.Thumbnails;

namespace NovaPlayer.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var logFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NovaPlayer", "logs");
        Log.Logger = new LoggerConfiguration().MinimumLevel.Information()
            .WriteTo.File(Path.Combine(logFolder, "player-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14).CreateLogger();
        try
        {
            var engine = await Task.Run(() => new MpvPlayerEngine(Log.Logger));
            try
            {
                bool verifyFileOpen = e.Args.Length == 3 && e.Args[1] == "--verify-file-open";
                bool verify = e.Args.Length >= 3 && e.Args[0].StartsWith("--verify", StringComparison.Ordinal) || verifyFileOpen;
                if (verify) ShutdownMode = ShutdownMode.OnExplicitShutdown;
                string profileName = verifyFileOpen ? "launch-profile" : verify && e.Args[0].StartsWith("--verify-library-", StringComparison.Ordinal)
                    ? "profile" : verify && e.Args[0].StartsWith("--verify-experience", StringComparison.Ordinal) ? "experience-profile"
                    : verify ? Path.GetFileNameWithoutExtension(e.Args[2]) + "-profile" : "";
                string root = verify ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(e.Args[2]))!, profileName)
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NovaPlayer");
                string preferencesPath = Path.Combine(root, "settings", "appearance.json");
                var preferences = await PreferencesService.LoadAsync(preferencesPath);
                var shortcuts = await ShortcutService.LoadAsync(Path.Combine(root, "settings", "shortcuts.json"));
                var store = new SqlitePlaybackStore(Path.Combine(root, "data", "player.db"));
                try { await store.InitializeAsync(); } catch (Exception ex) { Log.Error(ex, "数据库初始化失败，将继续启动播放器"); }
                var thumbnails = new ThumbnailService(Path.Combine(AppContext.BaseDirectory, "native", "win-x64", "ffmpeg.exe"), Path.Combine(root, "cache", "thumbnails"));
                await thumbnails.CleanAsync();
                MainWindow = new MainWindow(engine, e.Args, preferences, store, thumbnails, shortcuts); MainWindow.Show();
            }
            catch { engine.Dispose(); throw; }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Application startup failed");
            MessageBox.Show($"播放器启动失败：{ex.Message}\n\n请确认 native/win-x64/libmpv-2.dll 已部署。\n日志：{logFolder}", "Nova Player", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e) { Log.CloseAndFlush(); base.OnExit(e); }
}
