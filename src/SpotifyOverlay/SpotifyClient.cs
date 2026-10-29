using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpotifyOverlay;

internal sealed record Song(string Uri, string Title, string Artist, string Album);
internal sealed record SongPage(IReadOnlyList<Song> Songs, bool HasMore);

internal sealed class SpotifyCredentials
{
    public string ClientId { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
}

internal sealed class SpotifyClient : IDisposable
{
    public const string RedirectUri = "http://127.0.0.1:43821/callback";
    private const string Scopes = "user-library-read user-library-modify user-read-currently-playing user-modify-playback-state";
    private readonly HttpClient http;
    private readonly string credentialPath;
    private readonly SemaphoreSlim tokenLock = new(1, 1);
    private SpotifyCredentials credentials = new();

    public bool IsConnected => credentials.RefreshToken.Length > 0;
    public string ClientId => credentials.ClientId;

    public SpotifyClient() : this(new HttpClient { Timeout = TimeSpan.FromSeconds(25) },
        Path.Combine(Settings.Folder, "spotify-account.bin"))
    {
    }

    internal SpotifyClient(HttpClient http, string credentialPath)
    {
        this.http = http;
        this.credentialPath = credentialPath;
        try
        {
            if (File.Exists(credentialPath))
            {
                byte[] json = ProtectedData.Unprotect(File.ReadAllBytes(credentialPath), null, DataProtectionScope.CurrentUser);
                credentials = JsonSerializer.Deserialize<SpotifyCredentials>(json) ?? new();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            App.Log(error);
        }
    }

    public async Task ConnectAsync(string clientId, CancellationToken cancellationToken)
    {
        if (clientId.Length != 32 || !clientId.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException("Paste the 32-character Client ID from your Spotify developer app.");
        }

        string verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string state = Base64Url(RandomNumberGenerator.GetBytes(32));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        using var listener = new TcpListener(IPAddress.Loopback, 43821);
        try
        {
            listener.Start();
        }
        catch (SocketException)
        {
            throw new InvalidOperationException("The sign-in port is busy. Close another overlay sign-in window and try again.");
        }

        string url = "https://accounts.spotify.com/authorize?response_type=code"
            + "&client_id=" + Uri.EscapeDataString(clientId)
            + "&redirect_uri=" + Uri.EscapeDataString(RedirectUri)
            + "&scope=" + Uri.EscapeDataString(Scopes)
            + "&code_challenge_method=S256&code_challenge=" + challenge + "&state=" + state;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

        string code = await ReceiveCodeAsync(listener, state, timeout.Token);
        using var response = await http.PostAsync("https://accounts.spotify.com/api/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = RedirectUri,
                ["client_id"] = clientId,
                ["code_verifier"] = verifier
            }), timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("Spotify could not complete sign-in. Check the Client ID and redirect address, then try again.");
        }

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        var next = ReadTokens(json.RootElement, clientId, "");
        SaveCredentials(next);
        credentials = next;
    }

    internal static async Task<string> ReceiveCodeAsync(TcpListener listener, string expectedState, CancellationToken cancellationToken)
    {
        while (true)
        {
            using TcpClient connection = await listener.AcceptTcpClientAsync(cancellationToken);
            using NetworkStream stream = connection.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            string? request;
            try
            {
                request = await ReadLineAsync(reader, requestTimeout.Token);
                int headerSize = 0;
                while (true)
                {
                    string? headerLine = await ReadLineAsync(reader, requestTimeout.Token);
                    if (string.IsNullOrEmpty(headerLine))
                    {
                        break;
                    }

                    headerSize += headerLine.Length;
                    if (headerSize > 16384)
                    {
                        throw new IOException("Sign-in request headers are too long.");
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }
            string[] parts = (request ?? "").Split(' ');
            var query = new Dictionary<string, string>();
            bool valid = parts.Length == 3 && parts[0] == "GET" && parts[1].Length < 8192
                && Uri.TryCreate("http://127.0.0.1:43821" + parts[1], UriKind.Absolute, out _);
            if (valid)
            {
                var callback = new Uri("http://127.0.0.1:43821" + parts[1]);
                valid = callback.AbsolutePath == "/callback";
                foreach (string pair in callback.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] item = pair.Split('=', 2);
                    query[WebUtility.UrlDecode(item[0])] = item.Length == 2 ? WebUtility.UrlDecode(item[1]) : "";
                }
            }

            valid &= query.TryGetValue("state", out string? returnedState) && returnedState == expectedState;
            bool accepted = valid && query.TryGetValue("code", out string? value) && !string.IsNullOrWhiteSpace(value);
            byte[] body = Encoding.UTF8.GetBytes(accepted
                ? "Spotify sign-in received. Return to Spotify Overlay to finish."
                : "Spotify sign-in was not completed. Return to the overlay and try again.");
            byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: "
                + body.Length + "\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
            await stream.WriteAsync(header, cancellationToken);
            await stream.WriteAsync(body, cancellationToken);
            if (accepted)
            {
                return query["code"];
            }

            if (valid && query.ContainsKey("error"))
            {
                throw new InvalidOperationException("Spotify sign-in was declined. You can try again whenever you're ready.");
            }
        }
    }

    private static async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        char[] character = new char[1];
        while (await reader.ReadAsync(character.AsMemory(), cancellationToken) > 0)
        {
            if (character[0] == '\n')
            {
                return line.ToString().TrimEnd('\r');
            }

            line.Append(character[0]);
            if (line.Length > 8192)
            {
                throw new IOException("Sign-in request line is too long.");
            }
        }
        return line.Length > 0 ? line.ToString() : null;
    }

    public async Task<SongPage> SearchAsync(string query, int offset)
    {
        using var json = await SendAsync(HttpMethod.Get,
            "search?type=track&limit=10&offset=" + offset + "&q=" + Uri.EscapeDataString(query));
        var page = json!.RootElement.GetProperty("tracks");
        var songs = page.GetProperty("items").EnumerateArray().Select(ParseSong).Where(s => s != null).Cast<Song>().ToList();
        return new SongPage(songs, HasNext(page));
    }

    public async Task<SongPage> LikedAsync(int offset)
    {
        using var json = await SendAsync(HttpMethod.Get, "me/tracks?limit=20&offset=" + offset);
        var page = json!.RootElement;
        var songs = page.GetProperty("items").EnumerateArray()
            .Select(item => item.TryGetProperty("track", out var track) ? ParseSong(track) : null)
            .Where(s => s != null).Cast<Song>().ToList();
        return new SongPage(songs, HasNext(page));
    }

    public async Task<bool> IsLikedAsync(Song song)
    {
        using var json = await SendAsync(HttpMethod.Get, "me/library/contains?uris=" + Uri.EscapeDataString(song.Uri));
        return json!.RootElement[0].GetBoolean();
    }

    public async Task SetLikedAsync(Song song, bool liked)
    {
        using var response = await SendAsync(liked ? HttpMethod.Put : HttpMethod.Delete,
            "me/library?uris=" + Uri.EscapeDataString(song.Uri));
    }

    public async Task<Song?> CurrentAsync()
    {
        using var json = await SendAsync(HttpMethod.Get, "me/player/currently-playing");
        return json != null && json.RootElement.TryGetProperty("item", out var item) ? ParseSong(item) : null;
    }

    public async Task PlayAsync(Song song)
    {
        using var response = await SendAsync(HttpMethod.Put, "me/player/play", new
        {
            uris = new[] { song.Uri }
        });
    }

    public async Task QueueAsync(Song song)
    {
        using var response = await SendAsync(HttpMethod.Post, "me/player/queue?uri=" + Uri.EscapeDataString(song.Uri));
    }

    public void Disconnect()
    {
        if (File.Exists(credentialPath))
        {
            File.Delete(credentialPath);
        }

        credentials = new SpotifyCredentials();
    }

    private async Task<JsonDocument?> SendAsync(HttpMethod method, string endpoint, object? body = null)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            string token = await AccessTokenAsync(attempt > 0);
            using var request = new HttpRequestMessage(method, "https://api.spotify.com/v1/" + endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body != null)
            {
                request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            }

            using var response = await http.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                string message = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "Spotify sign-in expired. Disconnect and connect again.",
                    HttpStatusCode.Forbidden => "Spotify denied this action. Check Premium, your app's allowed users, and sign-in permissions.",
                    HttpStatusCode.NotFound => "No active Spotify player. Open Spotify and play a song there first.",
                    HttpStatusCode.TooManyRequests => "Spotify's request limit was reached. Please wait before trying again.",
                    _ => "Spotify could not complete the request (" + (int)response.StatusCode + "). Try again in a moment."
                };
                throw new InvalidOperationException(message);
            }
            string content = await response.Content.ReadAsStringAsync();
            return string.IsNullOrWhiteSpace(content) ? null : JsonDocument.Parse(content);
        }
        throw new InvalidOperationException("Connect to Spotify again.");
    }

    private async Task<string> AccessTokenAsync(bool forceRefresh)
    {
        await tokenLock.WaitAsync();
        try
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Connect your Spotify account in Search / library first.");
            }

            if (!forceRefresh && credentials.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                return credentials.AccessToken;
            }

            using var response = await http.PostAsync("https://accounts.spotify.com/api/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = credentials.RefreshToken,
                    ["client_id"] = credentials.ClientId
                }));
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException("Spotify could not renew sign-in. Disconnect and connect again.");
            }

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var next = ReadTokens(json.RootElement, credentials.ClientId, credentials.RefreshToken);
            SaveCredentials(next);
            credentials = next;
            return credentials.AccessToken;
        }
        finally
        {
            tokenLock.Release();
        }
    }

    private void SaveCredentials(SpotifyCredentials value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(credentialPath)!);
        byte[] encrypted = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(value), null, DataProtectionScope.CurrentUser);
        string temporary = credentialPath + ".tmp";
        File.WriteAllBytes(temporary, encrypted);
        File.Move(temporary, credentialPath, overwrite: true);
    }

    private static SpotifyCredentials ReadTokens(JsonElement json, string clientId, string oldRefreshToken) => new()
    {
        ClientId = clientId,
        AccessToken = json.GetProperty("access_token").GetString()!,
        RefreshToken = json.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString()! : oldRefreshToken,
        ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(json.GetProperty("expires_in").GetInt32())
    };

    internal static Song? ParseSong(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty("uri", out var uri))
        {
            return null;
        }

        string value = uri.GetString() ?? "";
        if (!value.StartsWith("spotify:track:", StringComparison.Ordinal))
        {
            return null;
        }

        string title = json.TryGetProperty("name", out var name) ? name.GetString() ?? "Unknown track" : "Unknown track";
        string artist = json.TryGetProperty("artists", out var artists)
            ? string.Join(", ", artists.EnumerateArray().Select(a => a.GetProperty("name").GetString())) : "";
        string album = json.TryGetProperty("album", out var record) && record.TryGetProperty("name", out var albumName)
            ? albumName.GetString() ?? "" : "";
        return new Song(value, title, artist, album);
    }

    private static bool HasNext(JsonElement page) => page.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String;
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public void Dispose()
    {
        http.Dispose();
        tokenLock.Dispose();
    }
}
