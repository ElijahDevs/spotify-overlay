using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpotifyOverlay;

class Program
{
    [STAThread]
    static void Main()
    {
        var application = new Application();
        Directory.CreateDirectory("artifacts/layout");
        var clientType = typeof(MainWindow).Assembly.GetType("SpotifyOverlay.SpotifyClient");
        var client = Activator.CreateInstance(clientType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, new object[] { new HttpClient(), Path.GetFullPath("artifacts/layout/no-account.bin") }, null);
        var window = (Window)Activator.CreateInstance(typeof(LibraryWindow), BindingFlags.Instance | BindingFlags.NonPublic,
            null, new[] { client }, null);
        var content = (FrameworkElement)window.Content;
        foreach (int width in new[] { 810, 660 })
        {
            content.Measure(new Size(width - 18, 640));
            content.Arrange(new Rect(0, 0, width - 18, 640));
            content.UpdateLayout();
            foreach (string name in new[] { "AccountButton", "ClientIdBox", "SearchBox", "Results", "ActionsPanel", "Status" })
            {
                var control = (FrameworkElement)window.FindName(name);
                Point point = control.TranslatePoint(new Point(), content);
                if (point.X < 0 || point.Y < 0 || point.X + control.ActualWidth > content.ActualWidth + 1
                    || point.Y + control.ActualHeight > content.ActualHeight + 1)
                    throw new Exception(name + " is outside the window at width " + width);
            }
            var bitmap = new RenderTargetBitmap(width - 18, 640, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create("artifacts/layout/library-" + width + ".png");
            encoder.Save(file);
            Console.WriteLine("PASS: Library layout at width " + width);
        }
        window.Close();
        ((IDisposable)client).Dispose();
        application.Shutdown();
    }
}

