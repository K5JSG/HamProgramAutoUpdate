using System.Text.Json.Serialization;

namespace HamProgramAutoUpdate.Services.Updaters.Programs.Chirp;

/// <summary>The learned Turnstile checkbox position, produced once on a
/// visible desktop (see ChirpUpdaterSource\coord_test.py) and reused on every
/// hidden-desktop run after that - same file/shape the Python updater already
/// used, just read from C# now.</summary>
public sealed class CaptchaCoords
{
    [JsonPropertyName("client_x")] public int ClientX { get; init; }
    [JsonPropertyName("client_y")] public int ClientY { get; init; }

    public static readonly CaptchaCoords Default = new() { ClientX = 117, ClientY = 332 };

    public static CaptchaCoords LoadOrDefault(string path)
    {
        try
        {
            if (!File.Exists(path)) return Default;
            var json = File.ReadAllText(path);
            var coords = System.Text.Json.JsonSerializer.Deserialize<CaptchaCoords>(json);
            return coords is { ClientX: > 0, ClientY: > 0 } ? coords : Default;
        }
        catch (Exception)
        {
            return Default;
        }
    }
}
