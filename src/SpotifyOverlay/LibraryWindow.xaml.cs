using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SpotifyOverlay;

public partial class LibraryWindow : Window
{
    private readonly SpotifyClient spotify;
    private readonly ObservableCollection<Song> songs = new();
    private readonly CancellationTokenSource lifetime = new();
    private bool busy;
    private bool browsingLikes;
    private bool selectedLiked;
    private bool likeKnown;
    private bool hasMore;
    private int offset;
    private int selectionVersion;
    private string query = "";
    private bool closed;

    internal LibraryWindow(SpotifyClient spotify)
    {
        InitializeComponent();
        this.spotify = spotify;
        Results.ItemsSource = songs;
        ClientIdBox.Text = spotify.ClientId;
        UpdateAccount();
        Closed += (_, _) =>
        {
            closed = true;
            lifetime.Cancel();
        };
    }

    private void UpdateAccount()
    {
        SetupPanel.Visibility = spotify.IsConnected ? Visibility.Collapsed : Visibility.Visible;
        AccountButton.Content = spotify.IsConnected ? "Disconnect" : "Connect Spotify";
        BrowsePanel.IsEnabled = spotify.IsConnected && !busy;
        Status.Text = spotify.IsConnected ? "Search for a song or open Liked Songs." : "Enter your Client ID, then click Connect Spotify.";
    }

    private async void ConnectClick(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            if (spotify.IsConnected)
            {
                spotify.Disconnect();
                songs.Clear();
                hasMore = false;
                UpdateAccount();
                return;
            }
            Status.Text = "Finish signing in in your browser. This window will update when you're done.";
            await spotify.ConnectAsync(ClientIdBox.Text.Trim(), lifetime.Token);
            UpdateAccount();
            Status.Text = "Connected. Search for a song or open Liked Songs.";
        });
    }

    private void DashboardClick(object sender, RoutedEventArgs e) => OpenUrl("https://developer.spotify.com/dashboard");
    private async void SearchClick(object sender, RoutedEventArgs e) => await BeginSearchAsync();
    private async void SearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await BeginSearchAsync();
        }
    }

    private async Task BeginSearchAsync()
    {
        if (busy)
        {
            return;
        }

        string text = SearchBox.Text.Trim();
        if (text.Length == 0)
        {
            Status.Text = "Enter a song, artist, or album to search.";
            return;
        }
        query = text;
        browsingLikes = false;
        offset = 0;
        await LoadPageAsync(reset: true);
    }

    private async void LikedClick(object sender, RoutedEventArgs e)
    {
        if (busy)
        {
            return;
        }

        browsingLikes = true;
        offset = 0;
        await LoadPageAsync(reset: true);
    }

    private async void MoreClick(object sender, RoutedEventArgs e) => await LoadPageAsync(reset: false);

    private async Task LoadPageAsync(bool reset)
    {
        await RunAsync(async () =>
        {
            Status.Text = "Loading songs…";
            SongPage page = browsingLikes ? await spotify.LikedAsync(offset) : await spotify.SearchAsync(query, offset);
            if (closed)
            {
                return;
            }

            if (reset)
            {
                songs.Clear();
            }

            foreach (Song song in page.Songs)
            {
                songs.Add(song);
            }

            offset += browsingLikes ? 20 : 10;
            hasMore = page.HasMore && (browsingLikes || offset <= 1000);
            Status.Text = songs.Count == 0 ? "No songs found." : songs.Count + (browsingLikes ? " liked songs loaded." : " search results loaded.");
        });
    }

    private async void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int version = ++selectionVersion;
        likeKnown = false;
        UpdateActions();
        if (Results.SelectedItem is not Song song)
        {
            return;
        }

        try
        {
            bool liked = await spotify.IsLikedAsync(song);
            if (closed || version != selectionVersion)
            {
                return;
            }

            selectedLiked = liked;
            likeKnown = true;
            UpdateActions();
        }
        catch (Exception error)
        {
            if (!closed && version == selectionVersion)
            {
                Status.Text = FriendlyError(error);
            }
        }
    }

    private async void LikeClick(object sender, RoutedEventArgs e)
    {
        if (!likeKnown || Results.SelectedItem is not Song song)
        {
            return;
        }

        bool liked = !selectedLiked;
        await RunAsync(async () =>
        {
            await spotify.SetLikedAsync(song, liked);
            selectedLiked = liked;
            if (browsingLikes && !liked)
            {
                songs.Remove(song);
            }

            Status.Text = liked ? "Saved to Liked Songs." : "Removed from Liked Songs.";
        });
    }

    private async void PlayClick(object sender, RoutedEventArgs e) => await PlaySelectedAsync();
    private async void ResultDoubleClick(object sender, MouseButtonEventArgs e) => await PlaySelectedAsync();
    private async Task PlaySelectedAsync()
    {
        if (Results.SelectedItem is not Song song)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await spotify.PlayAsync(song);
            Status.Text = "Playing " + song.Title;
        });
    }

    private async void QueueClick(object sender, RoutedEventArgs e)
    {
        if (Results.SelectedItem is not Song song)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await spotify.QueueAsync(song);
            Status.Text = "Added to your Spotify queue.";
        });
    }

    private void OpenClick(object sender, RoutedEventArgs e)
    {
        if (Results.SelectedItem is Song song)
        {
            OpenUrl(song.Uri);
        }
    }

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            Status.Text = FriendlyError(error);
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (busy || closed)
        {
            return;
        }

        busy = true;
        AccountButton.IsEnabled = false;
        ClientIdBox.IsEnabled = false;
        BrowsePanel.IsEnabled = false;
        Results.IsEnabled = false;
        UpdateActions();
        try
        {
            await action();
        }
        catch (Exception error)
        {
            if (!closed)
            {
                Status.Text = FriendlyError(error);
            }
        }
        finally
        {
            busy = false;
            if (!closed)
            {
                AccountButton.IsEnabled = true;
                ClientIdBox.IsEnabled = true;
                BrowsePanel.IsEnabled = spotify.IsConnected;
                Results.IsEnabled = true;
                UpdateActions();
            }
        }
    }

    private void UpdateActions()
    {
        ActionsPanel.IsEnabled = !busy && Results.SelectedItem is Song;
        LikeButton.IsEnabled = likeKnown;
        LikeButton.Content = likeKnown && selectedLiked ? "Unlike song" : "Like song";
        MoreButton.IsEnabled = !busy && hasMore && spotify.IsConnected;
    }

    internal static string FriendlyError(Exception error) => error switch
    {
        OperationCanceledException => "Sign-in or request timed out. You can try again.",
        System.Net.Http.HttpRequestException => "Couldn't reach Spotify. Check your connection and try again.",
        InvalidOperationException => error.Message,
        _ => "Something went wrong. Please try again."
    };
}
