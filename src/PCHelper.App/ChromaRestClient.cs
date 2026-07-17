using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCHelper.App;

public sealed record ChromaConnectionResult(bool Connected, string Message);

/// <summary>
/// Native client for Razer's OFFICIAL, documented Chroma SDK REST API
/// (http://localhost:54235/razer/chromasdk — see
/// https://assets.razerzone.com/dev_portal/REST/html/index.html). This is a
/// documented vendor API, not a reverse-engineered protocol, so it satisfies
/// RigPilot's "documented vendor APIs only" rule. It controls every connected
/// Razer Chroma device — including the Lian Li O11 Dynamic Razer Edition
/// controller, which enumerates as a Chroma ChromaLink device — plus Razer
/// keyboards, mice, headsets, mousepads, and keypads.
///
/// Flow: POST app info to the root to open a session, PUT a CHROMA_STATIC
/// effect to each device category, DELETE the session to release. Colours are
/// BGR integers per the SDK. The Chroma SDK server only runs while Razer
/// Synapse (with Chroma Connect) is installed and running; when it is absent
/// the connection simply fails cleanly.
/// </summary>
public sealed class ChromaRestClient(string baseUri = "http://localhost:54235/razer/chromasdk", TimeSpan? timeout = null)
{
    private static readonly string[] DeviceCategories =
        ["keyboard", "mouse", "headset", "mousepad", "keypad", "chromalink"];

    private readonly string _baseUri = baseUri;
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(4);

    /// <summary>The app-registration payload the SDK requires before it accepts effects.</summary>
    public static ChromaAppInfo AppInfo { get; } = new(
        "RigPilot",
        "RigPilot desktop control suite lighting bridge.",
        new ChromaContact("RigPilot", "https://github.com"),
        DeviceCategories,
        "application");

    /// <summary>Packs an #RRGGBB colour (with optional brightness scale) into the SDK's BGR integer.</summary>
    public static int ToBgr(string rgbHex, int brightnessPercent)
    {
        string value = rgbHex.Trim().TrimStart('#');
        if (value.Length != 6 || !int.TryParse(value, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int rgb))
        {
            throw new FormatException("Colour must use #RRGGBB format.");
        }

        double scale = Math.Clamp(brightnessPercent, 0, 100) / 100.0;
        int red = (int)((rgb >> 16 & 0xFF) * scale);
        int green = (int)((rgb >> 8 & 0xFF) * scale);
        int blue = (int)((rgb & 0xFF) * scale);
        return blue << 16 | green << 8 | red;
    }

    public async Task<ChromaConnectionResult> SetStaticColourAsync(string rgbHex, int brightnessPercent, CancellationToken cancellationToken)
    {
        int bgr = ToBgr(rgbHex, brightnessPercent);
        using HttpClient http = new() { Timeout = _timeout };

        ChromaSession? session;
        try
        {
            using HttpResponseMessage init = await http.PostAsJsonAsync(_baseUri, AppInfo, cancellationToken).ConfigureAwait(false);
            init.EnsureSuccessStatusCode();
            session = await init.Content.ReadFromJsonAsync<ChromaSession>(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new ChromaConnectionResult(false,
                "Razer Chroma SDK server is not reachable. Install and run Razer Synapse with Chroma Connect, then retry.");
        }

        if (session?.Uri is not string sessionUri || sessionUri.Length == 0)
        {
            return new ChromaConnectionResult(false, "The Chroma SDK did not return a session URI.");
        }

        int applied = 0;
        try
        {
            foreach (string category in DeviceCategories)
            {
                ChromaStaticEffect effect = new("CHROMA_STATIC", new ChromaStaticParam(bgr));
                using HttpResponseMessage put = await http.PutAsJsonAsync($"{sessionUri}/{category}", effect, cancellationToken).ConfigureAwait(false);
                if (put.IsSuccessStatusCode)
                {
                    applied++;
                }
            }
        }
        finally
        {
            try
            {
                using HttpRequestMessage delete = new(HttpMethod.Delete, sessionUri);
                await http.SendAsync(delete, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                // Session cleanup is best-effort; the SDK reaps idle sessions.
            }
        }

        return applied > 0
            ? new ChromaConnectionResult(true, $"Applied a static colour to {applied} Razer Chroma device categor{(applied == 1 ? "y" : "ies")} (Lian Li O11 Razer Edition, keyboard, mouse, and any other Chroma devices).")
            : new ChromaConnectionResult(false, "The Chroma session opened but no device category accepted the effect.");
    }
}

public sealed record ChromaAppInfo(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("author")] ChromaContact Author,
    [property: JsonPropertyName("device_supported")] IReadOnlyList<string> DeviceSupported,
    [property: JsonPropertyName("category")] string Category);

public sealed record ChromaContact(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("contact")] string Contact);

public sealed record ChromaSession(
    [property: JsonPropertyName("sessionid")] long SessionId,
    [property: JsonPropertyName("uri")] string? Uri);

public sealed record ChromaStaticEffect(
    [property: JsonPropertyName("effect")] string Effect,
    [property: JsonPropertyName("param")] ChromaStaticParam Param);

public sealed record ChromaStaticParam(
    [property: JsonPropertyName("color")] int Color);
