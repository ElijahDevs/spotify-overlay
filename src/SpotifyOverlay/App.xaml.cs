using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;

namespace SpotifyOverlay;

public partial class App : System.Windows.Application
{
    private Mutex? instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        instance = new Mutex(true, @"Local\SpotifyOverlay", out bool firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("Spotify Overlay is already running.", "Spotify Overlay");
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, error) =>
        {
            Log(error.Exception);
            MessageBox.Show("Spotify Overlay ran into a problem and needs to close.", "Spotify Overlay");
            error.Handled = true;
            Shutdown(1);
        };
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    internal static void Log(Exception error)
    {
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            string path = Path.Combine(Settings.Folder, "errors.log");
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {error}{Environment.NewLine}");
        }
        catch (IOException logError)
        {
            Debug.WriteLine(logError);
        }
        catch (UnauthorizedAccessException logError)
        {
            Debug.WriteLine(logError);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        instance?.Dispose();
        base.OnExit(e);
    }
}

