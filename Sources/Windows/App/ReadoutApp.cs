using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;
using Stratoscope.Core;
using static Windows.Win32.PInvoke;

namespace Stratoscope.Windows;

/// <summary>
/// The running app: refreshes engine telemetry faster while requests run, and shows it in the taskbar readout. There is no notification-area
/// icon: the readout is the only presence, so it stays visible, grey, while no engine answers.
///
/// Everything that depends on the display or on Windows settings goes through one <see cref="UiController"/>. Any event that could change
/// the look (display scale, text size, theme, accent, high contrast, monitors, taskbar, explorer restart) ends in <see cref="Rebuild"/>:
/// the readout and the panel are drawn again for the new DPI and theme.
/// </summary>
internal sealed class ReadoutApp : ApplicationContext
{
    private const int IdlePollMilliseconds = 2000;
    private const int LivePollMilliseconds = 500;
    public const string QuitEventName = "Local\\Stratoscope.Windows.Quit";
    private readonly SynchronizationContext? uiContext = SynchronizationContext.Current;
    private readonly EventWaitHandle quitEvent;
    private readonly RegisteredWaitHandle quitWait;

    private readonly string? configPath;
    private readonly HttpJsonSource source = new();
    private readonly System.Windows.Forms.Timer pollTimer = new() { Interval = IdlePollMilliseconds };
    private readonly System.Windows.Forms.Timer reloadTimer = new System.Windows.Forms.Timer { Interval = 600 };
    /// <summary>The light counter read for the speed curves: every <see cref="SpeedHistory.SampleSeconds"/>, the panel repaints with it while it is open.</summary>
    private readonly System.Windows.Forms.Timer sampleTimer = new System.Windows.Forms.Timer { Interval = (int)(SpeedHistory.SampleSeconds * 1000) };
    private bool sampling;
    private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
    private readonly UiController ui;
    private readonly StatusPanel panel;
    private readonly ReadoutHost readout;
    private FileSystemWatcher? watcher;

    private Config config = new Config();
    private List<ServerMonitor> monitors = new List<ServerMonitor>();
    private int selected;
    private bool polling;
    private bool pollRequested;
    private long lastPollStarted;
    private CancellationTokenSource? routerEvents;

    public ReadoutApp(string? configFile)
    {
        configPath = configFile;
        ui = new UiController(new WindowsSystemSettings(), SystemMonitors.Instance, () => config.UiScalePercent, TaskbarCentre, () => config.Theme);
        panel = new StatusPanel(() => PanelModelBuilder.Panel(monitors, selected, DateTime.UtcNow, SettingsPageModel(), config.HistorySmoothing), OnPanelAction, ui);
        readout = new ReadoutHost(SynchronizationContext.Current, ui);
        readout.ItemClicked += (index, rect) => { SelectServer(index); TogglePanel(rect); };
        readout.ContextMenuRequested += point => TogglePanel(ReadoutAnchor());
        readout.Unavailable += sentence => MessageBox.Show(sentence, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
        readout.EnvironmentChanged += reason => ui.Notify(reason, force: reason.Contains("display") || reason.Contains("taskbar") || reason.Contains("dpi"));
        ui.Changed += reason => Rebuild();

        LoadConfig();
        pollTimer.Tick += async (s, e) => { ui.Notify("poll"); await PollAllAsync(); };
        reloadTimer.Tick += (s, e) => { reloadTimer.Stop(); LoadConfig(); };
        sampleTimer.Tick += async (s, e) => await SampleAllAsync();
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;

        // "Stratoscope.exe --quit" (used by the installer and uninstaller) signals this event: a clean exit, no process killing.
        quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        quitWait = ThreadPool.RegisterWaitForSingleObject(quitEvent, (state, timedOut) => BeginInvokeOnUi(ExitThread), null, Timeout.Infinite, true);

        readout.Start();
        ui.Notify("start");
        pollTimer.Start();
        sampleTimer.Start();
        var _ = PollAllAsync();
    }

    /// <summary>Where the DPI is read: the middle of the taskbar, which is on the monitor the readout and the icon are on.</summary>
    private static Point TaskbarCentre()
    {
        var tb = TaskbarProbe.FindTaskbar();
        if (!tb.IsNull && GetWindowRect(tb, out var r)) return new Point((r.left + r.right) / 2, (r.top + r.bottom) / 2);
        var p = Screen.PrimaryScreen!.Bounds;
        return new Point(p.Left + p.Width / 2, p.Top + p.Height / 2);
    }

    /// <summary>
    /// Where the panel opens after a click on the readout: at the cursor when the cursor is on the taskbar (a mouse click), otherwise
    /// at the notification area (the icon was activated with the keyboard).
    /// </summary>
    private Rectangle ReadoutAnchor()
    {
        var cursor = Cursor.Position;
        var info = TaskbarProbe.Read(ReadoutHost.ReadoutClass);
        if (info == null || info.Bounds.Contains(cursor)) return new Rectangle(cursor.X - 8, cursor.Y - 8, 16, 16);
        var n = info.Notification;
        return new Rectangle(n.Left + n.Width / 2 - 8, n.Top + n.Height / 2 - 8, 16, 16);
    }

    // MARK: Settings

    private void LoadConfig()
    {
        config = Config.Load(null, configPath);
        UseServers();
        WatchConfig();
        ui.Notify("settings");
        UpdateUi();
        readout.Refresh();
        var _ = PollAllAsync();
        StartDetection();
    }

    private void UseServers()
    {
        routerEvents?.Cancel();
        routerEvents?.Dispose();
        monitors = config.Servers.Select(s => new ServerMonitor(s.Name, EngineRegistry.Make(s, source))).ToList();
        selected = Servers.Clamp(selected, monitors.Count);
        routerEvents = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        var lifetime = routerEvents.Token;
        foreach (var router in config.Servers.Select(s => s.Router).Distinct())
        {
            if (!Uri.TryCreate(router, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) continue;
            _ = source.WatchRouterEventsAsync(Endpoints.Join(uri.AbsoluteUri, "api", "events"),
                () => BeginInvokeOnUi(() => RequestEarlyPoll(lifetime)), lifetime);
        }
    }

    // MARK: Finding the server

    private const string NothingFound = "No engine found\nStart Strata or your router, Stratoscope finds it by itself.";
    private CancellationTokenSource? detection;

    /// <summary>
    /// Nobody said where the server is (no settings file, no environment variable): look for it, quietly, until it is found. Without a find, a
    /// short status stands next to the readout for the first minute; after that the readout is only its grey ring.
    /// </summary>
    private void StartDetection()
    {
        detection?.Cancel();
        readout.ShowStatus(null);
        if (config.Configured) { detection = null; return; }
        var run = detection = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        var _ = DetectAsync(run.Token);
    }

    private async Task DetectAsync(CancellationToken token)
    {
        var clients = config.ReadClientConfigs ? new ClientConfigReader(configHome: Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")) : null;
        using var probe = new HttpJsonSource(TimeSpan.FromSeconds(1.5));
        try
        {
            while (!token.IsCancellationRequested)
            {
                var found = await ServerDetection.FindAsync(probe, clients, token).ConfigureAwait(false);
                if (found.Count > 0)
                {
                    BeginInvokeOnUi(() =>
                    {
                        if (token.IsCancellationRequested) return;
                        readout.ShowStatus(null);
                        config.UseDetected(found);
                        UseServers();
                        UpdateUi();
                        readout.Refresh();
                        var _ = PollAllAsync();
                    });
                    return;
                }
                readout.ShowStatus(NothingFound);
                await Task.Delay(TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void WatchConfig()
    {
        watcher?.Dispose();
        watcher = null;
        var path = config.Path;
        var dir = path == null ? null : Path.GetDirectoryName(path);
        if (path == null || dir == null || !Directory.Exists(dir)) return;
        try
        {
            watcher = new FileSystemWatcher(dir, Path.GetFileName(path)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size, EnableRaisingEvents = true };
            FileSystemEventHandler changed = (s, e) => { if (reloadTimer.Enabled) return; BeginInvokeOnUi(() => { reloadTimer.Stop(); reloadTimer.Start(); }); };
            watcher.Changed += changed; watcher.Created += changed; watcher.Deleted += changed;
            watcher.Renamed += (s, e) => changed(s, e);
        }
        catch (Exception e) when (e is IOException || e is ArgumentException || e is UnauthorizedAccessException) { }
    }

    private void BeginInvokeOnUi(Action action)
    {
        // The context that was current when the app started is the WinForms one of the UI thread.
        if (uiContext != null) uiContext.Post(_ => action(), null);
        else action();
    }

    // MARK: Events that change the look

    private void OnPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e) => BeginInvokeOnUi(() => ui.Notify("preference " + e.Category));
    private void OnDisplayChanged(object? sender, EventArgs e) => BeginInvokeOnUi(() => ui.Notify("display settings", force: true));

    /// <summary>Draws everything again for the current DPI, text size, theme and monitors.</summary>
    private void Rebuild()
    {
        UpdateUi();
        readout.Refresh();
        panel.ApplyEnvironment();
    }

    // MARK: Polling

    private async Task PollAllAsync()
    {
        if (polling) return;
        polling = true;
        pollRequested = false;
        lastPollStarted = Stopwatch.GetTimestamp();
        try
        {
            var current = monitors;
            await Task.WhenAll(current.Select(m => m.PollAsync(cancellation.Token)));
            if (ReferenceEquals(current, monitors))
            {
                SetPollInterval(pollRequested || current.Any(m => m.Snapshot is { IsBusy: true, RouterLimited: false })
                    ? LivePollMilliseconds : IdlePollMilliseconds);
                UpdateUi();
            }
        }
        catch (OperationCanceledException) { }
        finally { polling = false; }
    }

    private void RequestEarlyPoll(CancellationToken lifetime)
    {
        if (lifetime.IsCancellationRequested) return;
        pollRequested = true;
        SetPollInterval(LivePollMilliseconds);
        if (!polling && Stopwatch.GetElapsedTime(lastPollStarted).TotalMilliseconds >= LivePollMilliseconds)
            _ = PollAllAsync();
    }

    private void SetPollInterval(int milliseconds)
    {
        if (pollTimer.Interval != milliseconds) pollTimer.Interval = milliseconds;
    }

    /// <summary>
    /// The counter read of every server (light: for the custom engine only /metrics, 46 KB against the 370 KB of the full poll's /ui/state). The panel
    /// repaints at this rate while it is open; the taskbar readout keeps its own rule and repaints on a change of what it shows, at the full poll.
    /// </summary>
    private async Task SampleAllAsync()
    {
        if (sampling) return;
        sampling = true;
        try
        {
            var current = monitors;
            var added = await Task.WhenAll(current.Select(m => m.SampleAsync(cancellation.Token)));
            if (ReferenceEquals(current, monitors) && panel.IsOpen && added.Any(a => a)) panel.Refresh();
        }
        catch (OperationCanceledException) { }
        finally { sampling = false; }
    }

    private void UpdateUi()
    {
        // The readout is the only presence of the app, so it is always visible, grey while a server does not answer.
        readout.SuppressTips = panel.IsOpen;
        readout.Update(PanelModelBuilder.Readout(monitors), monitors.Count > 0, config.ReadoutPosition);
        // The panel is redrawn when a poll brought other data, and every ten seconds for the ages ("3 min ago") that tick with the clock;
        // a poll with the same numbers leaves it alone.
        var version = string.Join(",", monitors.Select(m => m.DataVersion)) + "|" + selected;
        if (version != panelVersion || (DateTime.UtcNow - panelRefreshedAt).TotalSeconds >= 10)
        {
            panelVersion = version; panelRefreshedAt = DateTime.UtcNow;
            panel.Refresh();
        }
    }

    private string panelVersion = "";
    private DateTime panelRefreshedAt = DateTime.MinValue;

    // MARK: Panel and menu

    private void SelectServer(int index)
    {
        selected = Servers.Clamp(index, monitors.Count);
        panel.Refresh();
    }

    private void OnPanelAction(PanelAction action, int index)
    {
        switch (action)
        {
            case PanelAction.SelectTab: SelectServer(index); break;
            case PanelAction.OpenLink:
                var links = SelectedLinks();
                if (index < links.Count) Open(links[index]);
                break;
            case PanelAction.Settings: ShowSettings(true); break;
            case PanelAction.SettingsBack: ShowSettings(false); break;
            case PanelAction.ToggleStartup:
                if (!Autostart.Set(!Autostart.IsEnabled)) MessageBox.Show("Windows did not let Stratoscope change its Startup shortcut.", AppInfo.Name);
                panel.Refresh();
                break;
            case PanelAction.SetTheme: SetTheme((ThemeChoice)index); break;
            case PanelAction.SetSmoothing: SetSmoothing((HistorySmoothing)index); break;
            case PanelAction.OpenSettingsFile: OpenSettingsFile(); break;
            case PanelAction.ReloadSettings: LoadConfig(); break;
            case PanelAction.CheckForUpdates: OpenUrl(AppInfo.ReleasesUrl); break;
            case PanelAction.Quit: ExitThread(); break;
        }
    }

    private List<EngineLink> SelectedLinks() =>
        monitors.Count == 0 ? new List<EngineLink>() : monitors[Servers.Clamp(selected, monitors.Count)].Snapshot?.Links ?? new List<EngineLink>();

    /// <summary>The settings page of the panel is open; every other way to open the panel starts on the status.</summary>
    private bool settingsOpen;

    private PanelSettings? SettingsPageModel()
    {
        if (!settingsOpen) return null;
        var page = new PanelSettings { StartWithWindows = Autostart.IsEnabled, Theme = config.Theme, Smoothing = config.HistorySmoothing };
        page.Problems.AddRange(config.Problems);
        return page;
    }

    /// <summary>Opens the panel on the status, or closes it. The settings page is only ever reached from the panel's own row.</summary>
    private void TogglePanel(Rectangle anchor)
    {
        if (!panel.IsOpen) settingsOpen = false;
        panel.Toggle(anchor, readout.Edge);
        readout.SuppressTips = panel.IsOpen;
        if (panel.IsOpen) RequestEarlyPoll(cancellation.Token);
    }

    private void ShowSettings(bool open)
    {
        settingsOpen = open;
        panel.Refresh();
    }

    /// <summary>The choice is stored in the settings file; the file watcher reloads it, like any other edit.</summary>
    private void SetTheme(ThemeChoice choice)
    {
        var path = config.Path ?? Config.DefaultPath(null);
        if (!ConfigFile.SetValue(path, "theme", choice.ToString().ToLowerInvariant()))
            MessageBox.Show("Stratoscope could not change the theme in " + path + ".", AppInfo.Name);
    }

    /// <summary>Stored like the theme: in the settings file, which the watcher reloads.</summary>
    private void SetSmoothing(HistorySmoothing choice)
    {
        var path = config.Path ?? Config.DefaultPath(null);
        if (!ConfigFile.SetValue(path, "history_smoothing", choice.ToString().ToLowerInvariant()))
            MessageBox.Show("Stratoscope could not change the history smoothing in " + path + ".", AppInfo.Name);
    }

    private static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return;
        Start(uri.AbsoluteUri);
    }

    /// <summary>Opens a link of the engine: a web address in the browser, a file in its editor. Nothing else is started.</summary>
    private static void Open(EngineLink link)
    {
        if (Uri.TryCreate(link.Target, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        { Start(uri.AbsoluteUri); return; }
        if (File.Exists(link.Target)) Start(link.Target);
    }

    private static void Start(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception || e is InvalidOperationException) { }
    }

    private void OpenSettingsFile()
    {
        var path = config.Path;
        if (path == null) return;
        try
        {
            Config.WriteStarterIfMissing(path);
            Process.Start(new ProcessStartInfo("notepad.exe", "\"" + path + "\"") { UseShellExecute = true });
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is System.ComponentModel.Win32Exception) { }
    }

    // MARK: Shutdown

    protected override void ExitThreadCore()
    {
        SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        pollTimer.Stop(); sampleTimer.Stop(); reloadTimer.Stop();
        quitWait.Unregister(null);
        quitEvent.Dispose();
        cancellation.Cancel();
        routerEvents?.Dispose();
        watcher?.Dispose();
        readout.Dispose();
        panel.Dispose();
        source.Dispose();
        base.ExitThreadCore();
    }
}
