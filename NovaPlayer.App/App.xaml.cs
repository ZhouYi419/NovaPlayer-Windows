using System.IO;
using System.Windows;
using NovaPlayer.Infrastructure.Playback;
using NovaPlayer.App.Services;
using Serilog;

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
                string preferencesPath = e.Args.FirstOrDefault()?.StartsWith("--verify", StringComparison.Ordinal) == true
                    ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(e.Args[2]))!, $"preferences-{Guid.NewGuid():N}.json")
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NovaPlayer", "settings", "appearance.json");
                var preferences = await PreferencesService.LoadAsync(preferencesPath);
                MainWindow = new MainWindow(engine, e.Args, preferences); MainWindow.Show();
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
