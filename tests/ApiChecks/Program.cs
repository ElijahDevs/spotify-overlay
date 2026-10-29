using System.Net;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SpotifyOverlay;

var folder = Path.Combine(Path.GetTempPath(), "SpotifyOverlayChecks", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
var path = Path.Combine(folder, "test-account.bin");
var credentials = new SpotifyCredentials
{
    ClientId = new string('a', 32), AccessToken = "test-access", RefreshToken = "test-refresh",
    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30)
};
File.WriteAllBytes(path, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(credentials), null, DataProtectionScope.CurrentUser));
var fake = new FakeSpotify();
using var client = new SpotifyClient(new HttpClient(fake), path);
Check(client.IsConnected, "Encrypted account is loaded");
var results = await client.SearchAsync("space & symbols", 10);
Check(results.Songs.Count == 1 && results.HasMore, "Search parses tracks and pagination");
Check(fake.Requests.Last().Url.Contains("limit=10&offset=10&q=space%20%26%20symbols"), "Search is escaped and uses the current page limit");
var song = results.Songs[0];
var liked = await client.LikedAsync(20);
Check(liked.Songs[0].Title == "Test song", "Liked Songs parses saved-track entries");
Check(await client.IsLikedAsync(song), "Saved state is checked");
await client.SetLikedAsync(song, true);
Check(fake.Requests.Last().Method == "PUT" && fake.Requests.Last().Url.Contains("/me/library?uris=spotify%3Atrack%3A"), "Like uses the current library endpoint");
await client.SetLikedAsync(song, false);
Check(fake.Requests.Last().Method == "DELETE", "Unlike uses DELETE");
await client.PlayAsync(song);
Check(fake.Requests.Last().Body.Contains("spotify:track:123"), "Play submits the selected track");
await client.QueueAsync(song);
Check(fake.Requests.Last().Method == "POST" && fake.Requests.Last().Url.Contains("/me/player/queue?uri="), "Queue sends a track URI");
Check((await client.CurrentAsync())?.Title == "Test song", "Current song is read from Spotify rather than guessed by title");
fake.UnauthorizedOnce = true;
await client.SearchAsync("retry", 0);
Check(fake.Refreshes == 1, "401 refreshes the token and retries once");
Check(!Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("new-access"), "Refreshed tokens are encrypted on disk");
fake.NextError = HttpStatusCode.NotFound;
await ExpectError(() => client.PlayAsync(song), "No active Spotify player", "Missing player gets an actionable message");
fake.NextError = HttpStatusCode.TooManyRequests;
await ExpectError(() => client.SearchAsync("rate limit", 0), "request limit", "Rate limits do not cause an automatic retry loop");
using (var listener = new TcpListener(IPAddress.Loopback, 0))
{
    listener.Start();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var task = SpotifyClient.ReceiveCodeAsync(listener, "expected-state", timeout.Token);
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    using var browser = new HttpClient();
    await browser.GetStringAsync($"http://127.0.0.1:{port}/callback?state=wrong&code=bad");
    Check(!task.IsCompleted, "OAuth callback rejects a mismatched state");
    await browser.GetStringAsync($"http://127.0.0.1:{port}/callback?state=expected-state&code=good");
    Check(await task == "good", "OAuth callback accepts the matching state");
}
client.Disconnect();
Check(!client.IsConnected && !File.Exists(path), "Disconnect clears the saved account");
Console.WriteLine("All Spotify checks passed.");

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS: " + message);
}
static async Task ExpectError(Func<Task> action, string expected, string label)
{
    try { await action(); throw new Exception("Expected an error"); }
    catch (InvalidOperationException error) { Check(error.Message.Contains(expected), label); }
}

sealed class FakeSpotify : HttpMessageHandler
{
    public List<(string Method, string Url, string Body)> Requests = new();
    public bool UnauthorizedOnce;
    public int Refreshes;
    public HttpStatusCode? NextError;
    const string Track = "{\"uri\":\"spotify:track:123\",\"name\":\"Test song\",\"artists\":[{\"name\":\"Test artist\"}],\"album\":{\"name\":\"Test album\"}}";
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string url = request.RequestUri!.AbsoluteUri;
        string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.Method.Method, url, body));
        if (url.Contains("/api/token"))
        {
            Refreshes++;
            return Json("{\"access_token\":\"new-access\",\"expires_in\":3600}");
        }
        if (UnauthorizedOnce) { UnauthorizedOnce = false; return new(HttpStatusCode.Unauthorized); }
        if (NextError.HasValue) { var status = NextError.Value; NextError = null; return new(status); }
        if (request.Headers.Authorization?.Scheme != "Bearer") throw new Exception("Missing authorization header");
        if (url.Contains("/search?")) return Json("{\"tracks\":{\"items\":[" + Track + "],\"next\":\"page2\"}}");
        if (url.Contains("/me/tracks?")) return Json("{\"items\":[{\"track\":" + Track + "}],\"next\":null}");
        if (url.Contains("/contains?")) return Json("[true]");
        if (url.EndsWith("currently-playing")) return Json("{\"item\":" + Track + "}");
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }
    static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

namespace SpotifyOverlay
{
    internal static class App { public static void Log(Exception error) => Console.WriteLine(error.GetType().Name); }
    internal static class Settings { public static string Folder => Path.Combine(Path.GetTempPath(), "SpotifyOverlayChecks"); }
}

