using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;
using Forms = System.Windows.Forms;

namespace SpotifyOverlay;

public partial class MainWindow : Window
{
    private const int ExtendedWindowStyle = -20;
    private const long NoActivateStyle = 0x08000000;
    private const long AppWindowStyle = 0x00040000;
    private const long ClickThroughStyle = 0x00000020;

    private readonly Settings settings = Settings.Load();
    private readonly SpotifyClient spotify = new();
    private readonly DispatcherTimer timer = new()
    {
        Interval = TimeSpan.FromSeconds(1)
    };

    private readonly Dictionary<int, bool> hotkeys = new();
    private Forms.NotifyIcon? tray;
    private System.Drawing.Icon? trayIcon;
    private HwndSource? source;
    private ButtonLayer? buttonLayer;
    private LibraryWindow? library;
    private bool likingCurrent;
    private IntPtr hwnd;
    private GlobalSystemMediaTransportControlsSessionManager? manager;
    private GlobalSystemMediaTransportControlsSession? session;
    private bool locked = true;
    private bool refreshing;
    private bool commanding;
    private bool closing;
    private string lastTrack = "";
    private DateTime hintUntil;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr h, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr h, int id);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr h, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    public MainWindow()
    {
        InitializeComponent();
        Opacity = double.IsFinite(settings.Opacity) ? Math.Clamp(settings.Opacity, 0.35, 1) : 0.94;
        Left = settings.Left;
        Top = settings.Top;
        ClampPosition();
        SourceInitialized += Setup;
        Loaded += async (_, _) =>
        {
            buttonLayer = new ButtonLayer(this, Previous, PlayPause, Next, HideButton, LibraryButton, LikeCurrentButton);
            buttonLayer.Sync();
            timer.Start();
            await Refresh();
        };
        LocationChanged += (_, _) => buttonLayer?.Sync();
        StateChanged += (_, _) => buttonLayer?.Sync();
        LayoutUpdated += (_, _) => buttonLayer?.Sync();
        IsVisibleChanged += (_, _) => buttonLayer?.Sync();
        timer.Tick += async (_, _) =>
        {
            await Refresh();
            if (IsVisible)
            {
                SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x13);
            }
        };
        Closed += (_, _) => Cleanup();
    }

    private void Setup(object? sender, EventArgs e)
    {
        hwnd = new WindowInteropHelper(this).Handle;
        source = HwndSource.FromHwnd(hwnd);
        source.AddHook(WindowMessage);
        SetLocked(locked);
        Register(1, 0x27);
        Register(2, 0x25);
        Register(3, 0x20);
        Register(4, 0x4F);
        Register(5, 0x4C);
        CreateTray();
        if (hotkeys.Values.Any(ok => !ok))
        {
            Notify("Some shortcuts are in use by another app.");
            tray?.ShowBalloonTip(6000, "Spotify Overlay", "Some shortcuts are unavailable. Controls are in the tray menu.", Forms.ToolTipIcon.Warning);
        }
    }

    private void Register(int id, uint key) => hotkeys[id] = RegisterHotKey(hwnd, id, 0x4003, key); // Ctrl+Alt, no key repeat

    private IntPtr WindowMessage(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        // Suppress activation only while clicks are meant to pass through to the game.
        if (msg == 0x21 && locked)
        {
            handled = true;
            return new IntPtr(3);
        }

        if (msg == 0x0312)
        {
            handled = true;
            switch (w.ToInt32())
            {
                case 1:
                    _ = Command("next");
                    break;
                case 2:
                    _ = Command("previous");
                    break;
                case 3:
                    _ = Command("play");
                    break;
                case 4:
                    ToggleVisible();
                    break;
                case 5:
                    if (!IsVisible)
                    {
                        Show();
                    }

                    SetLocked(!locked);
                    break;
            }
        }

        return IntPtr.Zero;
    }

    private void SetLocked(bool value)
    {
        locked = value;
        long style = GetWindowLongPtr(hwnd, ExtendedWindowStyle).ToInt64() | AppWindowStyle;
        long inputStyles = NoActivateStyle | ClickThroughStyle;
        style = locked ? style | inputStyles : style & ~inputStyles;
        SetWindowLongPtr(hwnd, ExtendedWindowStyle, new IntPtr(style));
        // Apply the changed styles without moving, resizing, or activating the window.
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0037);
        ModeLabel.Text = locked ? "BUTTONS ONLY · CTRL ALT L TO MOVE" : "DRAG HEADER · CTRL ALT L TO FINISH";
        ModeLabel.Foreground = (Brush)new BrushConverter().ConvertFromString(locked ? "#91A699" : "#1ED760")!;
    }

    private void CreateTray()
    {
        using var bitmap = new System.Drawing.Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            using var green = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(30, 215, 96));
            g.FillEllipse(green, 1, 1, 30, 30);
            using var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(18, 25, 22), 2.6f);
            g.DrawArc(pen, 5, 9, 23, 14, 205, 125);
            g.DrawArc(pen, 7, 15, 18, 9, 205, 125);
            g.DrawArc(pen, 9, 21, 14, 5, 205, 125);
        }

        var iconHandle = bitmap.GetHicon();
        using (var temporary = System.Drawing.Icon.FromHandle(iconHandle))
        {
            trayIcon = (System.Drawing.Icon)temporary.Clone();
        }

        DestroyIcon(iconHandle);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show / hide    Ctrl+Alt+O", null, (_, _) => ToggleVisible());
        menu.Items.Add("Search / library", null, (_, _) => OpenLibrary());
        menu.Items.Add("Move overlay / finish moving    Ctrl+Alt+L", null, (_, _) =>
        {
            Show();
            SetLocked(!locked);
        });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Next track    Ctrl+Alt+Right", null, async (_, _) => await Command("next"));
        menu.Items.Add("Previous track    Ctrl+Alt+Left", null, async (_, _) => await Command("previous"));
        menu.Items.Add("Play / pause    Ctrl+Alt+Space", null, async (_, _) => await Command("play"));
        var opacity = new Forms.ToolStripMenuItem("Opacity");
        foreach (var percent in new[] { 60, 80, 94, 100 })
        {
            opacity.DropDownItems.Add(percent + "%", null, (_, _) =>
            {
                Opacity = percent / 100.0;
                buttonLayer?.Sync();
                Save();
            });
        }
        menu.Items.Add(opacity);
        menu.Items.Add("Reset position", null, (_, _) =>
        {
            Left = 24;
            Top = 24;
            ClampPosition();
            Save();
            Show();
        });
        menu.Items.Add("Help", null, (_, _) => ShowHelp());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit Spotify Overlay", null, (_, _) => Close());
        tray = new Forms.NotifyIcon
        {
            Icon = trayIcon,
            Text = "Spotify Overlay · Ctrl+Alt+O",
            ContextMenuStrip = menu,
            Visible = true
        };
        tray.DoubleClick += (_, _) =>
        {
            Show();
            SetLocked(false);
        };
    }

    private void ToggleVisible()
    {
        if (IsVisible)
        {
            Hide();
        }
        else
        {
            Show();
        }
    }

    private void ShowHelp()
    {
        string[] labels =
        {
            "Next track",
            "Previous track",
            "Play / pause",
            "Show / hide",
            "Lock / unlock"
        };
        var failures = hotkeys.Where(k => !k.Value).Select(k => labels[k.Key - 1]);
        string unavailable = failures.Any() ? string.Join(", ", failures) : "none";
        string help = "Start a song in Spotify desktop. Use borderless mode in your game.\n\n"
            + "Ctrl+Alt+Right: next track\n"
            + "Ctrl+Alt+Left: previous track\n"
            + "Ctrl+Alt+Space: play / pause\n"
            + "Ctrl+Alt+O: show / hide\n"
            + "Ctrl+Alt+L: move overlay / finish moving\n\n"
            + "Buttons are always clickable. Everything else passes clicks through.\n"
            + "Use move mode to drag the header, then press Ctrl+Alt+L again.\n\n"
            + "Unavailable shortcuts: " + unavailable;

        MessageBox.Show(help, "Spotify Overlay help");
    }

    private void ClampPosition()
    {
        // Screen coordinates are physical pixels; convert to WPF units for high-DPI displays.
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var bounds = new Rect(Left, Top, Width, Height);
        bool visible = Forms.Screen.AllScreens.Any(screen =>
        {
            var a = screen.WorkingArea;
            var p = transform.Transform(new Point(a.Left, a.Top));
            var q = transform.Transform(new Point(a.Right, a.Bottom));
            return new Rect(p, q).IntersectsWith(bounds);
        });
        if (!double.IsFinite(Left) || !double.IsFinite(Top) || !visible)
        {
            Left = SystemParameters.WorkArea.Left + 24;
            Top = SystemParameters.WorkArea.Top + 24;
        }
    }

    private void Save()
    {
        settings.Left = Left;
        settings.Top = Top;
        settings.Opacity = Opacity;
        settings.Save();
    }

    private void DragHeader(object sender, MouseButtonEventArgs e)
    {
        if (!locked && e.ChangedButton == MouseButton.Left)
        {
            try
            {
                DragMove();
                Save();
            }
            catch (InvalidOperationException error)
            {
                App.Log(error);
            }
        }
    }

    private void Notify(string text)
    {
        Hint.Text = text;
        hintUntil = DateTime.UtcNow.AddSeconds(6);
    }

    private GlobalSystemMediaTransportControlsSession? FindSpotify()
    {
        var sessions = manager?.GetSessions()
            .Where(s => s.SourceAppUserModelId.Contains("spotify", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return sessions?.FirstOrDefault(s =>
            s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            ?? sessions?.FirstOrDefault();
    }

    private async Task Refresh()
    {
        if (refreshing || closing)
        {
            return;
        }

        refreshing = true;
        try
        {
            manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            session = FindSpotify();
            if (DateTime.UtcNow > hintUntil)
            {
                Hint.Text = "SKIP  CTRL ALT →     •     PLAY / PAUSE  CTRL ALT SPACE";
            }

            if (session == null)
            {
                Track.Text = "Waiting for Spotify";
                Artist.Text = "Open Spotify desktop and start a song";
                PlaybackLabel.Text = "OFFLINE";
                Cover.Source = null;
                Progress.Value = 0;
                TimeLabel.Text = "0:00 / 0:00";
                lastTrack = "";
                Next.IsEnabled = Previous.IsEnabled = PlayPause.IsEnabled = false;
            }
            else
            {
                var current = session;
                var media = await current.TryGetMediaPropertiesAsync();
                if (closing)
                {
                    return;
                }

                Track.Text = string.IsNullOrWhiteSpace(media.Title) ? "Spotify" : media.Title;
                Artist.Text = string.IsNullOrWhiteSpace(media.Artist) ? media.AlbumTitle : media.Artist;
                var playback = current.GetPlaybackInfo();
                bool playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                PlaybackLabel.Text = playing ? "PLAYING" : "PAUSED";
                PlayIcon.Text = playing ? "\uE769" : "\uE768";
                Next.IsEnabled = playback.Controls.IsNextEnabled;
                Previous.IsEnabled = playback.Controls.IsPreviousEnabled;
                PlayPause.IsEnabled = playback.Controls.IsPlayPauseToggleEnabled;
                var timeline = current.GetTimelineProperties();
                var duration = timeline.EndTime - timeline.StartTime;
                var position = timeline.Position - timeline.StartTime;
                if (playing && timeline.LastUpdatedTime != default)
                {
                    position += DateTimeOffset.UtcNow - timeline.LastUpdatedTime;
                }

                var seconds = Math.Clamp(position.TotalSeconds, 0, Math.Max(0, duration.TotalSeconds));
                Progress.Value = duration.TotalSeconds > 0 ? seconds / duration.TotalSeconds : 0;
                TimeLabel.Text = FormatTime(TimeSpan.FromSeconds(seconds)) + " / " + FormatTime(duration);
                string key = current.SourceAppUserModelId + "|" + media.Title + "|" + media.Artist + "|" + media.AlbumTitle;
                if (lastTrack != key)
                {
                    lastTrack = key;
                    await LoadArtwork(media.Thumbnail);
                }
            }

        }
        catch (Exception ex)
        {
            App.Log(ex);
            manager = null;
            session = null;
            Track.Text = "Reconnecting to Spotify…";
            Artist.Text = "Windows media controls are temporarily unavailable";
            PlaybackLabel.Text = "RETRYING";
            Next.IsEnabled = Previous.IsEnabled = PlayPause.IsEnabled = false;
        }
        finally
        {
            refreshing = false;
        }
    }

    private async Task LoadArtwork(IRandomAccessStreamReference? thumbnail)
    {
        Cover.Source = null;
        if (thumbnail == null || closing)
        {
            return;
        }

        try
        {
            using var stream = await thumbnail.OpenReadAsync();
            if (stream.Size > 12 * 1024 * 1024)
            {
                return;
            }

            using var reader = new DataReader(stream);
            await reader.LoadAsync((uint)stream.Size);
            byte[] bytes = new byte[(int)stream.Size];
            reader.ReadBytes(bytes);

            using var memory = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 112;
            image.StreamSource = memory;
            image.EndInit();
            image.Freeze();
            if (!closing)
            {
                Cover.Source = image;
            }
        }
        catch (Exception error) when (error is IOException or COMException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            App.Log(error);
        }
    }

    private static string FormatTime(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            time = TimeSpan.Zero;
        }

        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : ((int)time.TotalMinutes) + time.ToString(@"\:ss");
    }

    private async Task Command(string action)
    {
        if (commanding || closing)
        {
            return;
        }

        commanding = true;
        try
        {
            manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var target = FindSpotify();
            if (target == null)
            {
                Notify("Open Spotify desktop and start a song first");
                return;
            }

            bool ok = action switch
            {
                "next" => await target.TrySkipNextAsync(),
                "previous" => await target.TrySkipPreviousAsync(),
                _ => await target.TryTogglePlayPauseAsync()
            };
            if (!ok)
            {
                Notify("Spotify couldn't do that. Check playback in Spotify.");
            }
            await Refresh();
        }
        catch (Exception ex)
        {
            App.Log(ex);
            Notify("Couldn't reach Spotify — try again in a moment");
        }
        finally
        {
            commanding = false;
        }
    }

    private async void NextClick(object sender, RoutedEventArgs e) => await Command("next");
    private async void PreviousClick(object sender, RoutedEventArgs e) => await Command("previous");
    private async void PlayClick(object sender, RoutedEventArgs e) => await Command("play");
    private void LibraryClick(object sender, RoutedEventArgs e) => OpenLibrary();

    private void OpenLibrary()
    {
        if (library == null)
        {
            library = new LibraryWindow(spotify);
            library.Closed += (_, _) => library = null;
        }
        library.Show();
        if (library.WindowState == WindowState.Minimized)
        {
            library.WindowState = WindowState.Normal;
        }

        library.Activate();
    }

    private async void LikeCurrentClick(object sender, RoutedEventArgs e)
    {
        if (likingCurrent)
        {
            return;
        }

        if (!spotify.IsConnected)
        {
            OpenLibrary();
            Notify("Connect Spotify to like songs.");
            return;
        }
        likingCurrent = true;
        LikeCurrentButton.IsEnabled = false;
        try
        {
            Song? song = await spotify.CurrentAsync();
            if (song == null)
            {
                Notify("Start a song in Spotify first.");
                return;
            }
            await spotify.SetLikedAsync(song, true);
            Notify("Saved " + song.Title + " to Liked Songs.");
        }
        catch (Exception error) { Notify(LibraryWindow.FriendlyError(error)); }
        finally
        {
            likingCurrent = false;
            LikeCurrentButton.IsEnabled = true;
        }
    }

    private void HideClick(object sender, RoutedEventArgs e) => Hide();
    private void Cleanup()
    {
        closing = true;
        library?.Close();
        timer.Stop();
        Save();
        foreach (int id in hotkeys.Keys)
        {
            UnregisterHotKey(hwnd, id);
        }

        source?.RemoveHook(WindowMessage);
        if (tray != null)
        {
            tray.Visible = false;
            tray.ContextMenuStrip?.Dispose();
            tray.Dispose();
        }

        trayIcon?.Dispose();
    }
}



