using System;
using System.IO;
using System.Text.Json;

namespace SpotifyOverlay;

public class Settings
{
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpotifyOverlay");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public double Left { get; set; } = 24;

    public double Top { get; set; } = 24;

    public double Opacity { get; set; } = 0.94;

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
            }
        }
        catch (IOException error)
        {
            App.Log(error);
        }
        catch (UnauthorizedAccessException error)
        {
            App.Log(error);
        }
        catch (JsonException error)
        {
            App.Log(error);
        }

        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch (IOException error)
        {
            App.Log(error);
        }
        catch (UnauthorizedAccessException error)
        {
            App.Log(error);
        }
    }
}

