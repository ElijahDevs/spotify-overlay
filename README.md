# Spotify Overlay

A small Spotify overlay for Windows. Keep track info and playback controls on top of your game, without blocking clicks on the window underneath.

The buttons stay clickable. The artwork, track text, and background let clicks pass through. When you need to move it, press **Ctrl+Alt+L**, drag the header, and press the shortcut again.

## Download

Open this repository's **Releases** page and download `SpotifyOverlay.exe`. The Windows x64 build includes .NET, so there is nothing else to install. Quit an older copy before starting a new one.

Use windowed or borderless fullscreen mode in your game. Exclusive fullscreen can cover the overlay.

## Controls

| Shortcut | Action |
| --- | --- |
| Ctrl+Alt+Right | Next track |
| Ctrl+Alt+Left | Previous track |
| Ctrl+Alt+Space | Play or pause |
| Ctrl+Alt+O | Show or hide |
| Ctrl+Alt+L | Move the overlay / finish moving |

Right-click the tray icon for opacity, reset position, and quit. If another app has taken a shortcut, the tray menu still works.

## Search and Liked Songs

Click **Search / library** to search songs, browse Liked Songs, play a result, add it to the queue, or like/unlike it. **Like current song** saves the track playing on your connected Spotify account.

These features need Spotify sign-in and a personal developer app. Basic playback controls work through the Spotify desktop app without this setup.

1. Open the [Spotify Developer Dashboard](https://developer.spotify.com/dashboard) with your Premium account and create an app. Select **Web API**.
2. Add this exact redirect address in the app settings:

   ```text
   http://127.0.0.1:43821/callback
   ```

3. Copy the app's **Client ID**. Open **Search / library**, paste it, and click **Connect Spotify**. There is no need to enter a client secret.
4. Finish signing in in your browser, then return to the overlay.

Use the same account in Spotify desktop and the overlay. Start a song in Spotify desktop before using play or queue so there is an active player. For an access error, check the app's allowed users in the dashboard.

Spotify currently requires Premium for the owner of a development app. See [Spotify's app requirements](https://developer.spotify.com/documentation/web-api/concepts/quota-modes) if setup is blocked.

## Settings

Position, opacity, and error logs are stored in `%LOCALAPPDATA%\SpotifyOverlay`. Sign-in tokens are encrypted for your Windows user account. **Disconnect** removes the saved sign-in from this PC; you can also revoke access from your Spotify account's Apps page.

## Build from source

You'll need Windows and the .NET 10 SDK.

```powershell
dotnet build src/SpotifyOverlay/SpotifyOverlay.csproj -c Release
```

To build the standalone executable:

```powershell
./build.ps1
```

The result is `artifacts/publish/SpotifyOverlay.exe`.

## Checks

The API checks use fake responses and do not change a Spotify account:

```powershell
dotnet run --project tests/ApiChecks -c Release
```

Two optional Windows checks exercise the overlay's hit testing and the library layout:

```powershell
dotnet run --project tests/OverlayChecks -c Release
dotnet run --project tests/LayoutChecks -c Release
```

The overlay check briefly opens a window. The layout check saves previews under `artifacts/layout`.

Builds and simulated requests have been checked, including token refresh and like/unlike. Live Spotify sign-in and account actions still need to be checked with a real developer app.
