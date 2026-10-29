using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace SpotifyOverlay;

// Keep the controls in their own transparent window. Windows passes clicks on
// its transparent pixels to the game, while the visible buttons still receive input.
internal sealed class ButtonLayer : Window
{
    private const int ExtendedWindowStyle = -20;
    private const long NoActivateStyle = 0x08000000;
    private const long ToolWindowStyle = 0x00000080;
    private const long AppWindowStyle = 0x00040000;
    private const long ClickThroughStyle = 0x00000020;

    private readonly MainWindow overlay;
    private readonly Canvas canvas = new();
    private readonly List<(Button Button, Border Placeholder)> controls = new();
    private HwndSource? source;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    public ButtonLayer(MainWindow overlay, params Button[] buttons)
    {
        this.overlay = overlay;
        Owner = overlay;
        Title = "Spotify Overlay controls";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        UseLayoutRounding = true;
        Content = canvas;

        foreach (Button button in buttons)
        {
            var parent = (Panel)button.Parent;
            int index = parent.Children.IndexOf(button);
            var placeholder = new Border
            {
                Width = button.ActualWidth,
                Height = button.ActualHeight,
                Margin = button.Margin,
                HorizontalAlignment = button.HorizontalAlignment,
                VerticalAlignment = button.VerticalAlignment
            };
            Grid.SetColumn(placeholder, Grid.GetColumn(button));
            Grid.SetRow(placeholder, Grid.GetRow(button));

            button.Style = (Style)button.FindResource(typeof(Button));
            parent.Children.Remove(button);
            parent.Children.Insert(index, placeholder);
            button.Margin = new Thickness(0);
            canvas.Children.Add(button);
            controls.Add((button, placeholder));
        }

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            long style = GetWindowLongPtr(handle, ExtendedWindowStyle).ToInt64();
            style = (style | NoActivateStyle | ToolWindowStyle) & ~(AppWindowStyle | ClickThroughStyle);
            SetWindowLongPtr(handle, ExtendedWindowStyle, new IntPtr(style));
            source = HwndSource.FromHwnd(handle);
            source.AddHook(WindowMessage);
        };
        Closed += (_, _) => source?.RemoveHook(WindowMessage);
    }

    public void Sync()
    {
        if (!overlay.IsVisible || overlay.WindowState == WindowState.Minimized)
        {
            Hide();
            return;
        }

        Left = overlay.Left;
        Top = overlay.Top;
        Width = overlay.ActualWidth;
        Height = overlay.ActualHeight;
        Opacity = overlay.Opacity;

        foreach (var (button, placeholder) in controls)
        {
            Point position = placeholder.TranslatePoint(new Point(), overlay);
            Canvas.SetLeft(button, position.X);
            Canvas.SetTop(button, position.Y);
            button.Width = placeholder.ActualWidth;
            button.Height = placeholder.ActualHeight;
        }

        if (!IsVisible)
        {
            Show();
        }
    }

    private IntPtr WindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x21) // MA_NOACTIVATE: deliver the click without taking focus.
        {
            handled = true;
            return new IntPtr(3);
        }

        return IntPtr.Zero;
    }
}
