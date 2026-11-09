using System.Globalization;
using System.IO;
using Microsoft.Extensions.Configuration;

namespace Stratoscope.Core;

/// <summary>One engine to watch.</summary>
public sealed class EngineConfig
{
    public string Name { get; set; } = "Strata";
    /// <summary>"strata" (default) or "demo".</summary>
    public string Engine { get; set; } = "strata";
    /// <summary>The engine server, for example http://127.0.0.1:8095. Used when no router is set, or as fallback.</summary>
    public string? Url { get; set; }
    /// <summary>A llama-swap style router in front of the engine.</summary>
    public string? Router { get; set; }
    /// <summary>The engine log file (only readable when the engine runs on this machine).</summary>
    public string? Log { get; set; }
    /// <summary>A chat UI to link to.</summary>
    public string? Webui { get; set; }
    /// <summary>Demo engine only: prefill, decode or idle.</summary>
    public string? Scenario { get; set; }
    /// <summary>The model id your client config uses for this engine, when it differs from the name the engine reports.</summary>
    public string? ClientModel { get; set; }
    /// <summary>The fallback for the summarise marker (the global <c>compact_at</c>), used when no client config says.</summary>
    public double? CompactAt { get; set; }
    /// <summary>False when the config files of clients such as Kilo and OpenCode must not be read (<c>client_configs</c>: none).</summary>
    public bool ReadClientConfigs { get; set; } = true;
}

/// <summary>The look of the panel.</summary>
public enum ThemeChoice
{
    /// <summary>Follows the Windows apps theme (default).</summary>
    System,
    Light,
    Dark,
}

/// <summary>Where in the taskbar the readout sits.</summary>
public enum ReadoutPosition
{
    /// <summary>Centred in the free gap between the last task-button cluster and the notification area (default); next to the notification area when the gap is too small.</summary>
    Center,
    /// <summary>Directly left of the notification area.</summary>
    Tray,
    /// <summary>Right after the last task button, so it moves with the buttons.</summary>
    AfterButtons,
    /// <summary>At the left edge of the taskbar, in the free space left of centred buttons.</summary>
    Left,
}

/// <summary>
/// Settings. Precedence for each value: environment variable, then the JSON file, then the default.
///
///     STRATOSCOPE_ENGINE      strata (default) | demo
///     STRATOSCOPE_URL         engine server (Strata default: http://127.0.0.1:8095)
///     STRATOSCOPE_ROUTER_URL  llama-swap style router in front of the engine ("none" disables)
///     STRATOSCOPE_LOG         engine log file
///     STRATOSCOPE_WEBUI_URL   adds an "Open chat UI" link
///     STRATOSCOPE_COMPACT_AT  token count at which your client summarises the chat; drawn as a marker on the context bar
///                             (fallback for when no client config can be read)
///     STRATOSCOPE_CLIENT_MODEL    the model id your client config uses for this engine, if it differs from the name the engine reports
///     STRATOSCOPE_CLIENT_CONFIGS  "none" stops Stratoscope from reading the config files of clients such as Kilo and OpenCode
///     STRATOSCOPE_READOUT_POSITION  center (default) | tray (next to the notification area) | after-buttons | left
///     STRATOSCOPE_UI_SCALE    auto (default) or 100 to 250
///     STRATOSCOPE_THEME       system (default) | light | dark: the look of the panel; the readout always matches the taskbar
///     STRATOSCOPE_CONFIG      path of the JSON file (default: %LOCALAPPDATA%\Stratoscope\config.json)
///
/// The JSON file may hold a "servers" list (objects with name, engine, url, router, log, webui, client_model) to watch several engines.
/// Setting STRATOSCOPE_URL or STRATOSCOPE_ROUTER_URL watches that one engine instead of the list.
/// </summary>
public sealed class Config
{
    public List<EngineConfig> Servers { get; } = new List<EngineConfig>();
    public double? CompactAt { get; set; }
    /// <summary>False when <c>client_configs</c> is "none": the config files of Kilo and OpenCode are not read.</summary>
    public bool ReadClientConfigs { get; set; } = true;
    public ReadoutPosition ReadoutPosition { get; set; } = ReadoutPosition.Center;
    /// <summary>Overrides the Windows display scale for this app (100 to 250), or null to follow Windows ("auto").</summary>
    public int? UiScalePercent { get; set; }
    /// <summary>The look of the panel: follow the Windows apps theme (default), or always light or dark. The readout always matches the taskbar.</summary>
    public ThemeChoice Theme { get; set; } = ThemeChoice.System;
    /// <summary>Things in the settings that could not be used, in English, for the menu and --dump.</summary>
    public List<string> Problems { get; } = new List<string>();
    /// <summary>The file that was read (or would be read).</summary>
    public string? Path { get; set; }
    /// <summary>
    /// Somebody said where the server is: there is a settings file, or STRATOSCOPE_ENGINE, STRATOSCOPE_URL or STRATOSCOPE_ROUTER_URL is set.
    /// When it is false, the server is the default one until <see cref="ServerDetection"/> finds the real one.
    /// </summary>
    public bool Configured { get; private set; }

    /// <summary>Uses the servers that were found instead of the default one, normalised like the entries of the settings file.</summary>
    public void UseDetected(IEnumerable<EngineConfig> found)
    {
        Servers.Clear();
        foreach (var server in found) Finish(this, server, null);
    }

    public const string DefaultUrl = "http://127.0.0.1:8095";

    /// <summary>
    /// The file that "Open settings file" creates when there is none: every setting as a commented example. Nothing in it is active, so the
    /// app keeps looking for the server by itself until a line is switched on.
    /// </summary>
    public const string Template =
        "{\r\n" +
        "  // Stratoscope settings. To use a setting, delete the // in front of its line. The file is read again when you save it.\r\n" +
        "\r\n" +
        "  // Where your engine is. Without a line here Stratoscope looks for it by itself.\r\n" +
        "  // \"url\": \"" + DefaultUrl + "\",\r\n" +
        "\r\n" +
        "  // A router in front of the engine (llama-swap): it knows the model name and says when a model is loading.\r\n" +
        "  // \"router\": \"http://127.0.0.1:18080\",\r\n" +
        "\r\n" +
        "  // Several servers, one tab each, instead of url and router.\r\n" +
        "  // \"servers\": [ { \"name\": \"Strata\", \"url\": \"" + DefaultUrl + "\" }, { \"name\": \"Router\", \"router\": \"http://127.0.0.1:18080\" } ],\r\n" +
        "\r\n" +
        "  // The look of the panel: \"system\" (default), \"light\" or \"dark\". The readout always matches the taskbar.\r\n" +
        "  // \"theme\": \"system\",\r\n" +
        "\r\n" +
        "  // Size of the panel and the readout in percent, 100 to 250. Default: as Windows has it.\r\n" +
        "  // \"ui_scale\": 125,\r\n" +
        "\r\n" +
        "  // Where the readout sits in the taskbar: \"center\" (default), \"tray\" (next to the notification area), \"after-buttons\", \"left\".\r\n" +
        "  // \"readout_position\": \"center\",\r\n" +
        "\r\n" +
        "  // The token count at which your chat client summarises, drawn as a mark on the context bar. Only needed when Stratoscope cannot find it in the Kilo or OpenCode config.\r\n" +
        "  // \"compact_at\": 60000\r\n" +
        "}\r\n";

    /// <summary>The settings the file understands; anything else in it is named as a problem.</summary>
    private static readonly HashSet<string> KnownSettings = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "servers", "engine", "url", "router", "log", "webui", "scenario", "client_model", "client_configs", "compact_at", "readout_position", "ui_scale", "theme",
    };

    public static string DefaultPath(IDictionary<string, string?>? environment = null)
    {
        var overridden = environment != null && environment.TryGetValue("STRATOSCOPE_CONFIG", out var p) ? p : null;
        if (!string.IsNullOrEmpty(overridden)) return overridden!;
        return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stratoscope", "config.json");
    }

    public static IDictionary<string, string?> CurrentEnvironment()
    {
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            env[(string)e.Key] = e.Value as string;
        return env;
    }

    private const string EnvironmentPrefix = "STRATOSCOPE_";

    /// <summary>The environment variables whose name is not the key of the settings file in capitals.</summary>
    private static readonly Dictionary<string, string> EnvironmentKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["STRATOSCOPE_ROUTER_URL"] = "router",
        ["STRATOSCOPE_WEBUI_URL"] = "webui",
    };

    /// <summary>The non-empty STRATOSCOPE_ variables as settings keys: STRATOSCOPE_UI_SCALE is ui_scale, and so on.</summary>
    private static IEnumerable<KeyValuePair<string, string?>> EnvironmentSettings(IDictionary<string, string?> env)
    {
        foreach (var (name, value) in env)
        {
            if (!name.StartsWith(EnvironmentPrefix, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(value)) continue;
            yield return new KeyValuePair<string, string?>(EnvironmentKeys.TryGetValue(name, out var key) ? key : name[EnvironmentPrefix.Length..].ToLowerInvariant(), value);
        }
    }

    /// <summary>
    /// The settings file with the environment on top (the later source wins). Comments, trailing commas and a byte order mark are accepted.
    /// A file that cannot be read or parsed is listed as a problem and leaves the environment and the defaults.
    /// </summary>
    private static IConfigurationRoot Read(Config config, IDictionary<string, string?> env)
    {
        var builder = new ConfigurationBuilder();
        if (File.Exists(config.Path)) builder.AddJsonFile(config.Path!, optional: false, reloadOnChange: false);
        builder.AddInMemoryCollection(EnvironmentSettings(env));
        try
        {
            var root = builder.Build();
            var fromEnvironment = EnvironmentSettings(env).Select(pair => pair.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var child in root.GetChildren())
                if (!KnownSettings.Contains(child.Key) && !fromEnvironment.Contains(child.Key))
                    config.Problems.Add("Ignored \"" + child.Key + "\": there is no such setting.");
            return root;
        }
        catch (Exception e) when (e is FormatException || e is InvalidDataException)
        {
            config.Problems.Add("The settings file is not a JSON object: " + config.Path);
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            config.Problems.Add("Cannot read " + config.Path + ": " + e.Message);
        }
        return new ConfigurationBuilder().AddInMemoryCollection(EnvironmentSettings(env)).Build();
    }

    public static Config Load(IDictionary<string, string?>? environment = null, string? path = null)
    {
        var env = environment ?? CurrentEnvironment();
        var config = new Config { Path = path ?? DefaultPath(env) };
        var settings = Read(config, env);
        string? Get(IConfiguration source, string key) => source[key] is { Length: > 0 } value ? value : null;
        // Somebody said where the server is: the settings name a server (a file with only a look setting does not), or the environment does.
        config.Configured = new[] { "servers", "engine", "url", "router" }.Any(key => settings.GetSection(key).Exists())
            || new[] { "STRATOSCOPE_ENGINE", "STRATOSCOPE_URL", "STRATOSCOPE_ROUTER_URL" }.Any(name => env.TryGetValue(name, out var v) && !string.IsNullOrEmpty(v));

        // Settings that apply to every server.
        var compact = Get(settings, "compact_at");
        if (compact != null && double.TryParse(compact, NumberStyles.Float, CultureInfo.InvariantCulture, out var k)) config.CompactAt = k;
        if (string.Equals(Get(settings, "client_configs")?.Trim(), "none", StringComparison.OrdinalIgnoreCase)) config.ReadClientConfigs = false;

        var position = Get(settings, "readout_position")?.Trim().ToLowerInvariant();
        if (position == "after-buttons") config.ReadoutPosition = ReadoutPosition.AfterButtons;
        else if (position == "left") config.ReadoutPosition = ReadoutPosition.Left;
        else if (position == "tray") config.ReadoutPosition = ReadoutPosition.Tray;
        else if (position != null && position != "center") config.Problems.Add("Ignored readout_position \"" + position + "\": use center, tray, after-buttons or left.");

        var scale = Get(settings, "ui_scale")?.Trim().ToLowerInvariant();
        if (scale != null && scale != "auto")
        {
            if (int.TryParse(scale.TrimEnd('%'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent) && percent >= 100 && percent <= 250) config.UiScalePercent = percent;
            else config.Problems.Add("Ignored ui_scale \"" + scale + "\": use auto or a number from 100 to 250.");
        }

        var theme = Get(settings, "theme")?.Trim().ToLowerInvariant();
        if (theme == "light") config.Theme = ThemeChoice.Light;
        else if (theme == "dark") config.Theme = ThemeChoice.Dark;
        else if (theme != null && theme != "system" && theme != "auto") config.Problems.Add("Ignored theme \"" + theme + "\": use system, light or dark.");

        var defaults = new EngineConfig
        {
            Engine = Get(settings, "engine") ?? "strata",
            Log = Get(settings, "log"),
            Webui = Get(settings, "webui"),
            Scenario = Get(settings, "scenario"),
            ClientModel = Get(settings, "client_model"),
        };

        bool envEngine = env.TryGetValue("STRATOSCOPE_URL", out var eu) && !string.IsNullOrEmpty(eu)
            || env.TryGetValue("STRATOSCOPE_ROUTER_URL", out var er) && !string.IsNullOrEmpty(er);
        var listed = settings.GetSection("servers").GetChildren().ToList();
        if (!envEngine && listed.Count > 0)
        {
            foreach (var item in listed)
            {
                var server = new EngineConfig
                {
                    Engine = Get(item, "engine") ?? defaults.Engine,
                    Url = Get(item, "url"),
                    Router = Get(item, "router"),
                    Log = Get(item, "log") ?? defaults.Log,
                    Webui = Get(item, "webui") ?? defaults.Webui,
                    Scenario = Get(item, "scenario") ?? defaults.Scenario,
                    ClientModel = Get(item, "client_model") ?? defaults.ClientModel,
                };
                Finish(config, server, Get(item, "name"));
            }
        }
        else
        {
            defaults.Url = Get(settings, "url");
            defaults.Router = Get(settings, "router");
            Finish(config, defaults, null);
        }
        return config;
    }

    /// <summary>Normalises the addresses, names the server, and adds it.</summary>
    private static void Finish(Config config, EngineConfig server, string? name)
    {
        server.Url = Address(config, server.Url, "url");
        server.Router = Address(config, server.Router, "router");
        if (server.Router == "none") server.Router = null;
        if (server.Url == "none") server.Url = null;
        server.Engine = server.Engine.ToLowerInvariant();
        server.CompactAt = config.CompactAt;
        server.ReadClientConfigs = config.ReadClientConfigs;
        if (server.Engine == "strata" && server.Url == null && server.Router == null) server.Url = DefaultUrl;
        server.Name = !string.IsNullOrWhiteSpace(name) ? name!.Trim() : DefaultName(server);
        config.Servers.Add(server);
    }

    private static string? Address(Config config, string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value!.Trim();
        if (string.Equals(value, "none", StringComparison.OrdinalIgnoreCase)) return "none";
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return value.TrimEnd('/');
        config.Problems.Add("Ignored " + key + " \"" + value + "\": it is not an http or https address.");
        return null;
    }

    private static string DefaultName(EngineConfig server)
    {
        if (server.Engine == "demo") return "Demo";
        var address = server.Router ?? server.Url;
        return address != null && Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Authority : "Strata";
    }
}
