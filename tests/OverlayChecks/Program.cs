using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using SpotifyOverlay;

class Program
{
    [StructLayout(LayoutKind.Sequential)]
    struct ScreenPoint { public int X; public int Y; }

    [DllImport("user32.dll")]
    static extern IntPtr WindowFromPoint(ScreenPoint point);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    static void Check(bool value, string label)
    {
        if (!value) throw new Exception(label);
        Console.WriteLine("PASS: " + label);
    }

    [STAThread]
    static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var overlay = new MainWindow();
        double savedLeft = overlay.Left;
        double savedTop = overlay.Top;
        // Keep the check away from the user's original overlay.
        overlay.Left = 500;
        overlay.Top = 300;
        overlay.Show();
        int exitCode = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                var layer = overlay.OwnedWindows.Cast<Window>().Single();
                var mainHandle = new WindowInteropHelper(overlay).Handle;
                var layerHandle = new WindowInteropHelper(layer).Handle;
                Check((GetWindowLongPtr(mainHandle, -20).ToInt64() & 0x20) != 0,
                    "Background window is click-through");
                Check((GetWindowLongPtr(layerHandle, -20).ToInt64() & 0x20) == 0,
                    "Button window accepts mouse input");
                Check((GetWindowLongPtr(layerHandle, -20).ToInt64() & 0x08000000) != 0,
                    "Buttons do not activate the overlay");

                foreach (var button in ((Canvas)layer.Content).Children.OfType<Button>())
                {
                    Point point = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
                    IntPtr hit = WindowFromPoint(new ScreenPoint { X = (int)point.X, Y = (int)point.Y });
                    Check(hit == layerHandle, button.Name + " receives native hit testing");
                }

                foreach (Point local in new[] { new Point(35, 24), new Point(50, 70), new Point(230, 75), new Point(200, 211) })
                {
                    Point point = overlay.PointToScreen(local);
                    IntPtr hit = WindowFromPoint(new ScreenPoint { X = (int)point.X, Y = (int)point.Y });
                    Check(hit != layerHandle && hit != mainHandle, "Background passes through at " + local);
                }

                var hide = ((Canvas)layer.Content).Children.OfType<Button>().Single(b => b.Name == "HideButton");
                hide.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(!overlay.IsVisible && !layer.IsVisible, "Hide removes both overlay windows");
                overlay.Show();
                Check(layer.IsVisible, "Showing the overlay restores its buttons");
                overlay.Left += 30;
                Check(layer.Left == overlay.Left, "Buttons follow position changes");
            }
            catch (Exception error)
            {
                Console.WriteLine("FAIL: " + error);
                exitCode = 1;
            }
            finally
            {
                overlay.Left = savedLeft;
                overlay.Top = savedTop;
                overlay.Close();
                app.Shutdown();
            }
        };
        timer.Start();
        app.Run();
        return exitCode;
    }
}
