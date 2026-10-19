// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Z.ai Chat Recovery contributors
// Licensed under the Apache License, Version 2.0 (see LICENSE file in the repository root).
//
// Z.ai Chat Recovery — desktop app (Windows / macOS / Linux, Avalonia).
// Flow for non-technical users: Check my chats (diagnosis) -> Save all my chats.
// Run with --selftest for a headless verification (%TEMP% or $TMPDIR/zai-selftest.txt).

using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace ZaiChatRecovery.Desktop;

internal static class Program
{
    internal static string? ReaderPath;

    static Program()
    {
        // never fail silently: WinExe crashes leave no console behind
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrash(e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => { WriteCrash(e.Exception); e.SetObserved(); };
    }

    internal static bool UiSmokeMode;

    /// <summary>UI regression run: render every tab in several orders -
    /// users start everywhere, and the crash class this catches ("control
    /// already has a visual parent" on tab revisit) only shows on the second
    /// visit. Result lands in %TEMP%/zai-uismoke.txt.</summary>
    private static int UiSmoke()
    {
        UiSmokeMode = true;
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(Array.Empty<string>());
            return 0;
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "zai-uismoke.txt"), "EXCEPTION " + ex); } catch { }
            return 4;
        }
    }

    internal static void WriteCrash(object? ex) =>
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "zai-recovery-crash.log"),
            $"[{DateTime.Now:O}] {ex}{Environment.NewLine}");

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
            return SelfTest.Run();
        if (args.Contains("--ui-smoke", StringComparer.OrdinalIgnoreCase))
            return UiSmoke();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--open-reader", StringComparison.OrdinalIgnoreCase))
                ReaderPath = args[i + 1];
            if (args[i].Equals("--fetch-attachments", StringComparison.OrdinalIgnoreCase))
                return FetchAttachmentsHeadless(args[i + 1]);
            if (args[i].Equals("--restore", StringComparison.OrdinalIgnoreCase))
                return RestoreHeadless(args[i + 1]);
            if (args[i].Equals("--verify", StringComparison.OrdinalIgnoreCase))
                return VerifyHeadless(args[i + 1]);
            if (args[i].Equals("--manifest", StringComparison.OrdinalIgnoreCase))
            {
                Snapshots.WriteManifest(args[i + 1]);
                Console.Out.Write("manifest written");
                return 0;
            }
            if (args[i].Equals("--reopen-page", StringComparison.OrdinalIgnoreCase))
                return ReopenPageHeadless(args[i + 1]);
            if (args[i].Equals("--dedup", StringComparison.OrdinalIgnoreCase) && i + 2 < args.Length)
            {
                var (linked, saved) = Snapshots.Deduplicate(args[i + 1], args[i + 2]);
                Console.Out.Write($"linked {linked} files, {saved} bytes shared");
                return 0;
            }
            if (args[i].Equals("--auto-backup", StringComparison.OrdinalIgnoreCase))
                return AutoBackupHeadless();
        }
        if (args.Contains("--auto-backup", StringComparer.OrdinalIgnoreCase))
            return AutoBackupHeadless();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Monthly automatic backup (scheduled): snapshot everything to a new
    /// dated folder, dedupe against the previous snapshot, write the manifest.</summary>
    private static int AutoBackupHeadless()
    {
        var sb = new StringBuilder();
        try
        {
            if (FindWorkingToken() is null)
            {
                sb.AppendLine("result: NO VALID TOKEN (open chat.z.ai, log in; the next scheduled run will retry)");
                WriteResult("zai-autobackup.txt", sb);
                return 2;
            }
            var prev = Snapshots.Latest();
            var dir = Snapshots.NextSnapshotDir();
            Directory.CreateDirectory(dir);
            var summary = Recovery.RecoverAllAsync(dir, 0, _ => { }, (_, _, _) => Task.CompletedTask).GetAwaiter().GetResult();
            var (linked, _) = Snapshots.Deduplicate(dir, prev);
            Snapshots.WriteManifest(dir);
            try
            {
                var settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZaiChatRecovery");
                Directory.CreateDirectory(settings);
                File.WriteAllText(Path.Combine(settings, "last-backup.txt"), dir);
            }
            catch { }
            sb.AppendLine($"result: OK, {summary.Done} chats, {summary.Attachments} attachments, snapshot {Path.GetFileName(dir)} ({linked} files shared with previous)");
            WriteResult("zai-autobackup.txt", sb);
            return 0;
        }
        catch (Exception ex)
        {
            sb.AppendLine("result: EXCEPTION " + ex.Message);
            WriteResult("zai-autobackup.txt", sb);
            return 3;
        }
    }

    private static int VerifyHeadless(string root)
    {
        var r = Snapshots.VerifyAll(root);
        var report = Snapshots.PlainReport(r);
        WriteResult("zai-verify.txt", new StringBuilder(report + Environment.NewLine + string.Join(Environment.NewLine, r.Problems)));
        return r.FilesBad == 0 ? 0 : 4;
    }

    /// <summary>Build the reopen page for any backup folder (uniting every
    /// chat id across its sibling snapshots) without touching the snapshots:
    /// written to a temp file whose path is left in zai-reopenpage.txt.</summary>
    private static int ReopenPageHeadless(string folder)
    {
        try
        {
            var entries = ReopenPage.UnionFor(folder);
            var path = ReopenPage.WriteToTemp(Recovery.AccountEmail, entries);
            WriteResult("zai-reopenpage.txt", new StringBuilder(path + Environment.NewLine + $"{entries.Count} chats"));
            try { Console.Out.Write(path); } catch { /* WinExe may have no console */ }
            return 0;
        }
        catch (Exception ex)
        {
            WriteResult("zai-reopenpage.txt", new StringBuilder("EXCEPTION " + ex.Message));
            return 3;
        }
    }

    private static void WriteResult(string file, StringBuilder sb)
    {
        try { File.WriteAllText(Path.Combine(Path.GetTempPath(), file), sb.ToString()); } catch { }
    }

    private static string? FindWorkingToken()
    {
        foreach (var c in Recovery.FindTokenCandidates())
        {
            if (c.Email.StartsWith("guest-", StringComparison.Ordinal)) continue;
            if (Recovery.TryUseTokenAsync(c.Token).GetAwaiter().GetResult())
                return c.Token;
        }
        return null;
    }

    private static int RestoreHeadless(string jsonPath)
    {
        var sb = new StringBuilder();
        try
        {
            if (FindWorkingToken() is null)
            {
                sb.AppendLine("result: NO VALID TOKEN");
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "zai-restore.txt"), sb.ToString());
                return 2;
            }
            var (newId, n) = Recovery.RestoreToAccountAsync(jsonPath).GetAwaiter().GetResult();
            sb.AppendLine($"result: OK, restored as chat {newId} ({n} messages)");
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "zai-restore.txt"), sb.ToString());
            return 0;
        }
        catch (Exception ex)
        {
            sb.AppendLine("result: EXCEPTION " + ex.Message);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "zai-restore.txt"), sb.ToString());
            return 3;
        }
    }

    private static int FetchAttachmentsHeadless(string jsonPath)
    {
        var sb = new StringBuilder();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(jsonPath));
            var r = doc.RootElement;
            string id = r.TryGetProperty("id", out var idEl) && idEl.ValueKind == System.Text.Json.JsonValueKind.String
                ? idEl.GetString()! : Path.GetFileNameWithoutExtension(jsonPath);
            string title = r.TryGetProperty("title", out var tEl) && tEl.ValueKind == System.Text.Json.JsonValueKind.String
                ? tEl.GetString() ?? id : id;
            string dir = Path.GetDirectoryName(Path.GetFullPath(jsonPath)) ?? ".";
            sb.AppendLine($"chat: {title} ({id})");
            string? tok = null;
            foreach (var c in Recovery.FindTokenCandidates())
            {
                if (c.Email.StartsWith("guest-", StringComparison.Ordinal)) continue;
                if (Recovery.TryUseTokenAsync(c.Token).GetAwaiter().GetResult()) { tok = c.Token; break; }
            }
            if (tok is null)
            {
                sb.AppendLine("result: NO VALID TOKEN");
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "zai-fetch.txt"), sb.ToString());
                return 2;
            }
            var (n, att, _) = Recovery.RefetchChatAsync(id, title, dir).GetAwaiter().GetResult();
            sb.AppendLine($"result: OK, {n} messages, {att} attachments saved");
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "zai-fetch.txt"), sb.ToString());
            return 0;
        }
        catch (Exception ex)
        {
            sb.AppendLine("result: EXCEPTION " + ex.Message);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "zai-fetch.txt"), sb.ToString());
            return 3;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var b = AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
        // Windows: pin Segoe UI (ships with every 10/11). macOS/Linux keep the
        // native system default (SF Pro / the distro's UI font) - overriding
        // there risks missing-family tofu for no visual gain.
        if (OperatingSystem.IsWindows())
            b = b.With(new Avalonia.Media.FontManagerOptions { DefaultFamilyName = "Segoe UI" });
        return b;
    }
}

internal sealed class App : Application
{
    public App()
    {
        // Shields Up look and feel, light: gray canvas, white cards, blue accent
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Light;
        Resources["SystemAccentColor"] = Color.Parse("#007AFF");
        Resources["SystemAccentColorLight1"] = Color.Parse("#4098FF");
        Resources["SystemAccentColorLight2"] = Color.Parse("#66AAFF");
        Resources["SystemAccentColorDark1"] = Color.Parse("#0062CC");
    }

    public override void Initialize() { }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                desktop.MainWindow = Program.ReaderPath is { Length: > 0 } p && Directory.Exists(p)
                    ? new ReaderWindow(p)
                    : new MainWindow();
                if (Program.UiSmokeMode && desktop.MainWindow is MainWindow mw)
                    mw.Loaded += (_, _) => _ = mw.RunUiSmokeAsync();
            }
            catch (Exception ex) { Program.WriteCrash(ex); throw; }
        }
        base.OnFrameworkInitializationCompleted();
    }
}

internal sealed class MainWindow : Window
{
    // ---- the screen IS the state: one hero card, one reassurance, one action
    private readonly TextBlock _heroTitle = new() { FontSize = Ui.H1, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Brush(Ui.Text) };
    private readonly TextBlock _heroBody = new() { FontSize = Ui.BodyL, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Brush(Ui.Muted) };
    private readonly Button _primaryBtn = new() { Content = "Recover my chats" };
    private readonly Button _openBtn = new() { Content = "Open the folder" };
    private readonly Button _verifyBtn = new() { Content = "Check my backups" };
    private readonly Button _scaleBtn = new() { Content = "Text size: Normal" };
    private readonly CheckBox _autoBox = new() { Content = "Remember for me — back up every month", FontSize = Ui.Meta };
    private readonly ProgressBar _bar = new() { Height = 10, Minimum = 0, Maximum = 100, Value = 0, IsVisible = false };
    private readonly TextBlock _progressWords = new() { FontSize = Ui.Body, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Brush(Ui.Muted), IsVisible = false };
    private readonly TextBlock _folderLabel = new() { Foreground = Ui.Brush(Ui.Muted), TextWrapping = TextWrapping.Wrap, FontSize = Ui.Small };
    private readonly TextBox _log = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontSize = Ui.Small, Height = 220 };
    private string? _backupDir;
    private string _outDir = "";
    private bool _autoRescan;
    private bool _busy;
    private Func<Task>? _primaryAction;

    private enum Tab { Recover, Read, Photos, Backups, Tips, Settings }
    private ReaderView? _reader;
    private string? _readerDir;
    private readonly TextBlock _verifyOut = new() { FontSize = Ui.Body, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Brush(Ui.Text) };
    private bool _verifyRan;

    // Photos & files tab: attachment health of the newest backup
    private sealed record ChatAtts(string Id, string Title, int Saved, int Missing);
    private List<ChatAtts>? _atts;
    private readonly TextBlock _attsSummary = new() { FontSize = Ui.BodyL, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Brush(Ui.Text) };
    private readonly TextBlock _attsStatus = new() { FontSize = Ui.Body, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Brush(Ui.Muted) };
    private readonly Button _attsBtn = new() { Content = "Update missing from website" };
    private readonly StackPanel _attsList = new() { Spacing = 6 };
    private Tab _tab = Tab.Recover;

    // panic-grade copy: short words, one idea per line. Shown from the
    // "Dos and don'ts" button - reassurance on demand, never in the way.
    private static readonly (bool Good, string Tip)[] Tips =
    {
        (true,  "Log in to chat.z.ai with your Google account first. Your chats live there."),
        (true,  "Save your chat links first. They still open."),
        (true,  "Press Save all my chats. Keep a copy on this computer."),
        (true,  "Use the same login every time."),
        (true,  "Check the email on the website. Right account?"),
        (false, "Don't log out yet."),
        (false, "Don't clear cookies or site data."),
        (false, "Don't delete your chats in a panic."),
        (false, "Don't keep chatting as a guest. Guest chats are not saved."),
        (false, "Don't run cleaning apps yet. Wait for the backup."),
    };

    public MainWindow()
    {
        Title = "Z.ai Chat Recovery";
        Width = 900; Height = 780;
        Background = Ui.Brush(Ui.Bg);
        Ui.TrySetIcon(this);
        MinWidth = 780; MinHeight = 640;

        _outDir = Snapshots.NextSnapshotDir();
        _folderLabel.Text = "Backups go to: " + Snapshots.Root + "  (a new dated folder each time — older ones are never touched)";

        _openBtn.IsEnabled = false;

        _primaryBtn.Click += async (_, _) => { if (_primaryAction is { } a && !_busy) await a(); };
        _openBtn.Click += (_, _) => OpenFolder();
        _verifyBtn.Click += async (_, _) => await VerifyBackupsAsync();
        _attsBtn.Click += async (_, _) => await UpdateMissingAsync();
        _scaleBtn.Click += (_, _) => { Ui.CycleScale(); Render(); };
        _autoBox.IsChecked = Scheduler.IsScheduled();
        _autoBox.IsCheckedChanged += (_, _) =>
        {
            var want = _autoBox.IsChecked == true;
            if (Scheduler.SetScheduled(want))
                Log(want ? "Monthly automatic backup scheduled (first of each month, 9 PM)."
                         : "Automatic backup turned off.");
            else
            {
                _autoBox.IsChecked = !want;
                Log("Could not change the schedule on this system - monthly backups can still be run by hand.");
            }
        };
        Activated += async (_, _) => { if (_autoRescan && !_busy) await CheckAsync(); };
        DetectExistingBackup();
        SetWelcome();
        Render();
    }

    private void SetWelcome()
    {
        var body = "Finds your saved chat.z.ai sign-in (log in once in your browser), then saves every chat to this computer. Read-only.";
        if (_backupDir is not null)
            body += "\n\nEarlier backup found - \"Read my chats\" below is ready.";
        SetHero("Recover your chats", body, Ui.Text, "Recover my chats", CheckAsync);
    }

    /// <summary>Set the hero card: the reassurance, the explanation, and the
    /// single next action. Technical detail lives in the Settings tab.</summary>
    private void SetHero(string title, string body, Color titleColor, string primaryText, Func<Task>? primary)
    {
        _heroTitle.Text = title;
        _heroTitle.Foreground = Ui.Brush(titleColor);
        _heroBody.Text = body;
        _heroBody.IsVisible = body.Length > 0;
        _primaryBtn.Content = primaryText;
        _primaryBtn.IsEnabled = primary is not null;
        _primaryAction = primary;
    }

    /// <summary>Compose the whole window: a left tab rail and the selected
    /// tab's page. Re-run on text-size changes - controls persist, so enabled
    /// states, text and scroll positions survive a rebuild.</summary>
    private void Render()
    {
        // Re-parenting a control that still sits in the outgoing (discarded)
        // tree throws "already has a visual parent" - Avalonia only detaches
        // the top of the old tree, so detach our persistent controls first.
        // Without this, any tab revisit crashed the app (found the hard way).
        foreach (var c in new Control[] { _heroTitle, _heroBody, _bar, _progressWords,
                                          _primaryBtn, _openBtn, _verifyBtn, _scaleBtn, _autoBox,
                                          _folderLabel, _log, _verifyOut,
                                          _attsSummary, _attsBtn, _attsStatus, _attsList })
            DetachHosted(c);
        if (_reader is { } r) DetachHosted(r);

        _scaleBtn.Content = "Text size: " + Ui.ScaleName;
        Ui.StylePrimary(_primaryBtn);
        foreach (var b in new[] { _openBtn, _verifyBtn, _scaleBtn }) Ui.StyleSecondary(b);

        var side = new StackPanel { Spacing = 4 };
        var brand = Ui.SmallCaps("Z.AI CHAT RECOVERY");
        brand.Margin = new(10, 2, 8, 14);
        side.Children.Add(brand);
        side.Children.Add(TabButton("Recover my chats", Tab.Recover));
        side.Children.Add(TabButton("Read my chats", Tab.Read));
        side.Children.Add(TabButton("Photos & files", Tab.Photos));
        side.Children.Add(TabButton("Check my backups", Tab.Backups));
        side.Children.Add(TabButton("Dos and don'ts", Tab.Tips));
        side.Children.Add(TabButton("Settings", Tab.Settings));

        var sideCard = Ui.Card();
        sideCard.Padding = new(10);
        sideCard.Margin = new(0, 0, 18, 0);
        sideCard.Child = side;

        Control page = _tab switch
        {
            Tab.Read => ReadTab(),
            Tab.Photos => PhotosTab(),
            Tab.Backups => BackupsTab(),
            Tab.Tips => TipsTab(),
            Tab.Settings => SettingsTab(),
            _ => RecoverTab(),
        };

        var grid = new Grid { ColumnDefinitions = new("Auto, *"), Margin = new(28) };
        grid.Children.Add(sideCard);
        var scroll = new ScrollViewer { Content = page };
        grid.Children.Add(scroll);
        Grid.SetColumn(scroll, 1);
        Content = grid;
    }

    private static void DetachHosted(Control c)
    {
        switch (c.Parent)
        {
            case Panel p: p.Children.Remove(c); break;
            case ContentControl cc: cc.Content = null; break;
            case Border b: b.Child = null; break;
            case Decorator d: d.Child = null; break;
        }
    }

    private Button TabButton(string label, Tab t)
    {
        var active = _tab == t;
        var b = new Button
        {
            Content = label, FontSize = Ui.Body, MinHeight = Ui.BtnH,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new(14, 10), CornerRadius = new CornerRadius(10),
            BorderThickness = new(0),
            Background = active ? Ui.Brush(Ui.InfoBg) : Brushes.Transparent,
            Foreground = active ? Ui.Brush(Ui.Accent) : Ui.Brush(Ui.Text),
            FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal,
        };
        b.Click += (_, _) =>
        {
            if (_tab == t) return;
            _tab = t;
            Render();
            if (t == Tab.Backups && !_verifyRan) _ = VerifyBackupsAsync();
            if (t == Tab.Photos && _atts is null) _ = LoadAttsAsync();
        };
        return b;
    }

    private Control RecoverTab()
    {
        var hero = Ui.Card();
        var heroStack = new StackPanel { Spacing = 12 };
        heroStack.Children.Add(_heroTitle);
        heroStack.Children.Add(_heroBody);
        heroStack.Children.Add(new StackPanel { Spacing = 6, Children = { _bar, _progressWords } });
        heroStack.Children.Add(_primaryBtn);
        hero.Child = heroStack;

        var footer = new TextBlock
        {
            Text = "Free and open source (Apache-2.0) · Your saved sign-in never leaves this computer · Requests go to chat.z.ai only",
            Foreground = Ui.Brush(Ui.Muted), TextWrapping = TextWrapping.Wrap, FontSize = Ui.Small, Margin = new(0, 6, 0, 0)
        };

        var col = new StackPanel { Spacing = 16, MaxWidth = Math.Round(680 * Ui.Scale) };
        col.Children.Add(hero);
        col.Children.Add(footer);
        return col;
    }

    /// <summary>The reader, embedded as a tab page. Built once per backup
    /// folder so chat selection and scroll survive tab switches.</summary>
    private Control ReadTab()
    {
        var col = new StackPanel { MaxWidth = Math.Round(900 * Ui.Scale) };
        if (_backupDir is null)
        {
            var card = Ui.Card();
            card.Child = Ui.Txt("No backups on this computer yet. Recover your chats first - the reader will be ready here right after.", Ui.Body, Ui.Text);
            col.Children.Add(card);
            return col;
        }
        if (_reader is null || _readerDir != _backupDir)
        {
            _reader = new ReaderView(_backupDir);
            _readerDir = _backupDir;
        }
        col.Children.Add(_reader);
        return col;
    }

    private Control BackupsTab()
    {
        var card = Ui.Card();
        var sp = new StackPanel { Spacing = 12 };
        sp.Children.Add(new TextBlock { Text = "Your backups", FontSize = Ui.H2, FontWeight = FontWeight.SemiBold });
        sp.Children.Add(Ui.Txt("Every file in every dated backup, checked against its built-in integrity list. Nothing leaves this computer.", Ui.Meta, Ui.Muted));
        _verifyBtn.Content = _verifyRan ? "Check again" : "Check my backups";
        _verifyBtn.HorizontalAlignment = HorizontalAlignment.Left;
        sp.Children.Add(_verifyBtn);
        sp.Children.Add(_verifyOut);
        card.Child = sp;
        var col = new StackPanel { MaxWidth = Math.Round(680 * Ui.Scale) };
        col.Children.Add(card);
        return col;
    }

    internal async Task RunUiSmokeAsync()
    {
        var result = "OK";
        try
        {
            foreach (var t in new[] { Tab.Photos, Tab.Recover, Tab.Read, Tab.Settings,
                                      Tab.Backups, Tab.Tips, Tab.Photos, Tab.Recover,
                                      Tab.Read, Tab.Backups, Tab.Settings })
            {
                _tab = t;
                Render();
                await Task.Delay(30);
            }
            SetWelcome();
            Render();
        }
        catch (Exception ex)
        {
            result = "EXCEPTION " + ex.GetType().Name + ": " + ex.Message;
        }
        try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "zai-uismoke.txt"), result); } catch { }
        Environment.Exit(0);
    }

    private Task OpenReaderTabAsync()
    {
        _tab = Tab.Read;
        Render();
        return Task.CompletedTask;
    }

    /// <summary>Photos &amp; files: attachment health of the newest backup, in
    /// plain words. Files go missing because their download links on the
    /// website expire; one button re-downloads them - only ever into the
    /// newest backup, whose integrity list is re-stamped afterwards so
    /// "Check my backups" stays green.</summary>
    private Control PhotosTab()
    {
        var card = Ui.Card();
        var sp = new StackPanel { Spacing = 12 };
        sp.Children.Add(new TextBlock { Text = "Photos & files", FontSize = Ui.H2, FontWeight = FontWeight.SemiBold });
        sp.Children.Add(Ui.Txt("Photos and files inside your chats are saved with your backups. Their links on the website expire, so some can go missing in an older backup. This tab re-downloads the missing ones.", Ui.Meta, Ui.Muted));
        if (_atts is null)
            _attsSummary.Text = "Looking through your newest backup…";
        sp.Children.Add(_attsSummary);
        Ui.StylePrimary(_attsBtn);
        _attsBtn.HorizontalAlignment = HorizontalAlignment.Left;
        sp.Children.Add(_attsBtn);
        sp.Children.Add(_attsStatus);
        sp.Children.Add(_attsList);
        card.Child = sp;
        var col = new StackPanel { MaxWidth = Math.Round(680 * Ui.Scale) };
        col.Children.Add(card);
        return col;
    }

    private string? AttsDir() => Snapshots.Latest() ?? _backupDir;

    private async Task LoadAttsAsync()
    {
        try
        {
            var dir = AttsDir();
            if (dir is null || !Directory.Exists(dir))
            {
                _atts = new List<ChatAtts>();
                _attsSummary.Text = "No backups on this computer yet - recover your chats first.";
                _attsSummary.Foreground = Ui.Brush(Ui.Warn);
                _attsBtn.IsEnabled = false;
                return;
            }
            _atts = await Task.Run(() =>
            {
                var list = new List<ChatAtts>();
                foreach (var s in BackupReader.ScanBackupFolder(dir))
                {
                    var (saved, missing) = CountAtts(s.JsonPath);
                    if (saved + missing > 0)
                        list.Add(new ChatAtts(s.Id, s.Title, saved, missing));
                }
                return list.OrderByDescending(a => a.Missing).ThenByDescending(a => a.Saved).ToList();
            });
            FillAtts();
        }
        catch (Exception ex)
        {
            _attsStatus.Text = Ui.PlainError(ex);
        }
    }

    private void FillAtts()
    {
        if (_atts is null) return;
        var dir = AttsDir();
        int saved = _atts.Sum(a => a.Saved), missing = _atts.Sum(a => a.Missing);
        _attsList.Children.Clear();
        _attsSummary.Foreground = Ui.Brush(Ui.Text);
        if (_atts.Count == 0)
            _attsSummary.Text = "No photos or files are recorded in this backup.";
        else if (missing == 0)
            _attsSummary.Text = $"All {saved} photos and files in backup \"{Path.GetFileName(dir ?? "")}\" are saved. Nothing to do.";
        else
            _attsSummary.Text = $"{saved} photos and files saved · {missing} missing. Press the button to re-download the missing ones from the website.";
        foreach (var a in _atts)
            _attsList.Children.Add(AttsRow(a));
        _attsBtn.IsEnabled = missing > 0 && !_attsBusy;
    }

    private Control AttsRow(ChatAtts a)
    {
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = Ui.Msg };
        tb.Inlines.Add(new Run { Text = a.Title, FontWeight = FontWeight.SemiBold, Foreground = Ui.Brush(Ui.Text) });
        tb.Inlines.Add(new Run { Text = $"  · {a.Saved} saved", Foreground = Ui.Brush(Ui.Muted) });
        if (a.Missing > 0)
            tb.Inlines.Add(new Run { Text = $" · {a.Missing} missing", FontWeight = FontWeight.SemiBold, Foreground = Ui.Brush(Ui.Warn) });
        return new Border
        {
            Background = Ui.Brush(Ui.Bg), CornerRadius = new CornerRadius(8),
            Padding = new(10, 6), Child = tb,
        };
    }

    private bool _attsBusy;

    private async Task UpdateMissingAsync()
    {
        if (_attsBusy || _atts is null) return;
        var dir = AttsDir();
        if (dir is null || !Directory.Exists(dir)) return;
        _attsBusy = true;
        _attsBtn.IsEnabled = false;
        _attsStatus.Foreground = Ui.Brush(Ui.Muted);
        try
        {
            if (Recovery.Token is null)
            {
                _attsStatus.Text = "Looking for your saved sign-in…";
                var ok = false;
                foreach (var c in await Task.Run(Recovery.FindTokenCandidates))
                {
                    if (c.Email.StartsWith("guest-", StringComparison.Ordinal)) continue;
                    if (await Task.Run(() => Recovery.TryUseTokenAsync(c.Token).GetAwaiter().GetResult())) { ok = true; break; }
                }
                if (!ok)
                {
                    _attsStatus.Text = "No working sign-in - open chat.z.ai in your browser, log in, then try again.";
                    _attsStatus.Foreground = Ui.Brush(Ui.Warn);
                    return;
                }
            }
            var todo = _atts.Where(a => a.Missing > 0).ToList();
            int done = 0, files = 0, failed = 0;
            foreach (var a in todo)
            {
                done++;
                _attsStatus.Text = $"Updating chat {done} of {todo.Count} - {a.Title}";
                try
                {
                    var (_, att, _) = await Task.Run(() => Recovery.RefetchChatAsync(a.Id, a.Title, dir).GetAwaiter().GetResult());
                    files += att;
                }
                catch { failed++; }
                await Task.Delay(400);
            }
            // files in the newest backup changed: re-stamp its integrity list
            if (Path.GetDirectoryName(Path.GetFullPath(dir)) is { } parent &&
                string.Equals(parent, Snapshots.Root, StringComparison.OrdinalIgnoreCase))
            {
                try { Snapshots.WriteManifest(dir); } catch { }
            }
            await LoadAttsAsync();
            _attsStatus.Text = failed == 0
                ? $"Done - {files} photos and files saved into backup \"{Path.GetFileName(dir)}\"."
                : $"Done - {files} saved, {failed} chat(s) could not be updated (the website may be busy - try again later).";
        }
        finally
        {
            _attsBusy = false;
            if (_atts is not null) _attsBtn.IsEnabled = _atts.Any(x => x.Missing > 0);
        }
    }

    /// <summary>Count saved/missing attachments in a v2 backup json without
    /// loading full transcripts.</summary>
    private static (int saved, int missing) CountAtts(string jsonPath)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(jsonPath));
            var r = doc.RootElement;
            if (!r.TryGetProperty("messages", out var ms) || ms.ValueKind != System.Text.Json.JsonValueKind.Array)
                return (0, 0);
            int saved = 0, missing = 0;
            foreach (var m in ms.EnumerateArray())
                if (m.TryGetProperty("attachments", out var aa) && aa.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var a in aa.EnumerateArray())
                        if (a.TryGetProperty("missing", out var mi) && mi.ValueKind == System.Text.Json.JsonValueKind.True) missing++;
                        else saved++;
            return (saved, missing);
        }
        catch { return (0, 0); }
    }

    /// <summary>The DO/DON'T reassurance card - its own tab, so it never
    /// competes with the recovery action but stays one click away.</summary>
    private Control TipsTab()
    {
        var card = Ui.Card();
        var sp = new StackPanel { Spacing = 12 };
        sp.Children.Add(new TextBlock
        {
            Text = "Your chats are almost never gone.",
            FontSize = Ui.H2, FontWeight = FontWeight.SemiBold,
            Foreground = Ui.Brush(Ui.Text), TextWrapping = TextWrapping.Wrap
        });
        sp.Children.Add(Ui.Txt("If the z.ai chat list ever looks empty, these habits keep a scare from becoming a loss.", Ui.Msg, Ui.Muted));
        bool? lastGood = null;
        foreach (var (good, tip) in Tips)
        {
            if (lastGood != good)
            {
                sp.Children.Add(new TextBlock
                {
                    Text = good ? "DO" : "DON'T",
                    FontSize = Ui.Body, FontWeight = FontWeight.Bold, Margin = new(0, 8, 0, 0),
                    Foreground = Ui.Brush(good ? Ui.Good : Ui.Bad)
                });
                lastGood = good;
            }
            var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = Ui.Msg, Margin = new(6, 0, 0, 0) };
            tb.Inlines.Add(new Run { Text = good ? "✓  " : "✗  ", FontSize = Ui.Body, FontWeight = FontWeight.Bold, Foreground = Ui.Brush(good ? Ui.Good : Ui.Bad) });
            tb.Inlines.Add(new Run { Text = tip, Foreground = Ui.Brush(Ui.Text) });
            sp.Children.Add(tb);
        }
        card.Child = sp;
        var col = new StackPanel { MaxWidth = Math.Round(680 * Ui.Scale) };
        col.Children.Add(card);
        return col;
    }

    private Control SettingsTab()
    {
        var col = new StackPanel { Spacing = 16, MaxWidth = Math.Round(680 * Ui.Scale) };

        var s1 = Ui.Card();
        var sp1 = new StackPanel { Spacing = 16 };
        sp1.Children.Add(new TextBlock { Text = "Settings", FontSize = Ui.H2, FontWeight = FontWeight.SemiBold });
        var sizeRow = new StackPanel { Spacing = 6 };
        sizeRow.Children.Add(Ui.Txt("Text size", Ui.BodyL, Ui.Text));
        sizeRow.Children.Add(Ui.Txt("Make the whole app bigger and easier to read.", Ui.Meta, Ui.Muted));
        _scaleBtn.HorizontalAlignment = HorizontalAlignment.Left;
        sizeRow.Children.Add(_scaleBtn);
        sp1.Children.Add(sizeRow);
        var autoRow = new StackPanel { Spacing = 6 };
        autoRow.Children.Add(Ui.Txt("Automatic backups", Ui.BodyL, Ui.Text));
        autoRow.Children.Add(Ui.Txt("Back up all chats on the first of each month, 9 PM, without reminding you.", Ui.Meta, Ui.Muted));
        _autoBox.VerticalAlignment = VerticalAlignment.Center;
        autoRow.Children.Add(_autoBox);
        sp1.Children.Add(autoRow);
        s1.Child = sp1;

        var s2 = Ui.Card();
        var sp2 = new StackPanel { Spacing = 8 };
        sp2.Children.Add(new TextBlock { Text = "Technical details", FontSize = Ui.H2, FontWeight = FontWeight.SemiBold });
        sp2.Children.Add(Ui.Txt("What the app did, in technical words. Nothing here ever leaves this computer.", Ui.Meta, Ui.Muted));
        sp2.Children.Add(_folderLabel);
        sp2.Children.Add(_log);
        _openBtn.HorizontalAlignment = HorizontalAlignment.Left;
        sp2.Children.Add(_openBtn);
        s2.Child = sp2;

        col.Children.Add(s1);
        col.Children.Add(s2);
        return col;
    }

    private void Log(string s) => _log.Text += s + Environment.NewLine;

    private async Task CheckAsync()
    {
        if (_busy) return;
        _busy = true; _autoRescan = false;
        SetHero("Checking…", "", Ui.Muted, "Checking…", null);
        Log("Looking for a chat.z.ai sign-in in browser storage…");
        try
        {
            var cands = await Task.Run(Recovery.FindTokenCandidates);
            Log($"  candidates found: {cands.Count}");
            foreach (var c in cands.Take(5)) Log($"  {c.Email}  ({c.Where})");

            string? working = null;
            foreach (var c in cands.Take(8))
            {
                if (c.Email.StartsWith("guest-", StringComparison.Ordinal))
                {
                    Log("  skipping a guest sign-in - guest mode has its own, empty chat list");
                    continue;
                }
                if (await Task.Run(() => Recovery.TryUseTokenAsync(c.Token).GetAwaiter().GetResult()))
                {
                    working = c.Token;
                    break;
                }
            }

            if (working is null)
            {
                SetHero("Sign in first",
                    "Open chat.z.ai in your browser and log in with your Google account (not as a guest), then come back - this app re-checks by itself.",
                    Ui.Warn, "Try again", CheckAsync);
                Log("No working sign-in. Log in to chat.z.ai in the browser, then switch back to this window.");
                _autoRescan = true;
            }
            else
            {
                var preview = await Task.Run(async () =>
                {
                    var chats = await Recovery.ListAllChatsAsync();
                    return chats.Count == 0
                        ? (0, "")
                        : (chats.Count, chats.Max(c => c.TryGetProperty("updated_at", out var u) ? u.GetInt64() : 0).ToString());
                });
                if (preview.Item1 == 0)
                {
                    SetHero("This account has no chats",
                        $"Signed in as {Recovery.AccountEmail}. Your chats may be under a different login (for example Google vs email). Log in with that account in the browser, then try again.",
                        Ui.Warn, "Try again", CheckAsync);
                    _autoRescan = true;
                }
                else
                {
                    SetHero($"Your {preview.Item1} chats are safe",
                        $"Still on the z.ai website, on the account {Recovery.AccountEmail}. Save a copy to this computer.",
                        Ui.Good, "Save all my chats", SaveAsync);
                }
                Log($"Signed in as {Recovery.AccountEmail} ({preview.Item1} chats visible).");
            }
        }
        catch (Exception ex)
        {
            SetHero("Couldn't check just now", Ui.PlainError(ex), Ui.Bad, "Try again", CheckAsync);
            Log("ERROR: " + ex.Message);
        }
        _busy = false;
    }

    private async Task SaveAsync()
    {
        if (_busy) return;
        _busy = true;
        _bar.Value = 0; _bar.IsVisible = true;
        _progressWords.IsVisible = true; _progressWords.Text = "Starting…";
        SetHero("Saving…", "", Ui.Muted, "Saving…", null);
        var prev = Snapshots.Latest();
        _outDir = Snapshots.NextSnapshotDir();
        Directory.CreateDirectory(_outDir);
        try
        {
            var summary = await Recovery.RecoverAllAsync(_outDir, 0,
                s => Log(s),
                (i, total, title) =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        _bar.Maximum = total; _bar.Value = i;
                        _progressWords.Text = $"Saving chat {i} of {total} - {title}";
                        Log($"  saving chat {i} of {total}: {title}");
                    });
                    return Task.CompletedTask;
                });
            var (linked, savedMb) = Snapshots.Deduplicate(_outDir, prev);
            Snapshots.WriteManifest(_outDir);
            _openBtn.IsEnabled = true;
            _backupDir = _outDir;
            RememberBackup(_outDir);
            SetHero($"{summary.Done} chats saved",
                $"Backup \"{Path.GetFileName(_outDir)}\" - older backups are untouched.",
                Ui.Good, "Read my chats", OpenReaderTabAsync);
            Log($"Done. {summary.Done} chats, {summary.Failed} failed. Backup folder: {_outDir}");
            if (prev is not null)
                Log($"  {linked} unchanged files shared with the previous backup (no extra space used).");
            Log("  Integrity manifest written (manifest.json).");
        }
        catch (Exception ex)
        {
            SetHero("Couldn't finish saving", Ui.PlainError(ex), Ui.Bad, "Try again", SaveAsync);
            Log("ERROR: " + ex.Message);
        }
        _bar.IsVisible = false; _progressWords.IsVisible = false;
        _busy = false;
    }

    private async Task VerifyBackupsAsync()
    {
        if (_backupDir is null)
        {
            _verifyOut.Text = "No backups on this computer yet.";
            _verifyOut.Foreground = Ui.Brush(Ui.Warn);
            return;
        }
        _verifyBtn.IsEnabled = false;
        _verifyOut.Text = "Checking every file…";
        _verifyOut.Foreground = Ui.Brush(Ui.Muted);
        try
        {
            var root = Snapshots.List(Path.GetDirectoryName(_backupDir)).Count > 0
                ? Path.GetDirectoryName(_backupDir)!
                : _backupDir;
            var result = await Task.Run(() => Snapshots.VerifyAll(root));
            _verifyRan = true;
            var good = result.FilesBad == 0;
            _verifyOut.Text = Snapshots.PlainReport(result);
            _verifyOut.Foreground = Ui.Brush(good ? Ui.Good : Ui.Bad);
            foreach (var p in result.Problems) Log("  " + p);
            Log($"Check finished: {result.FilesOk} files OK, {result.FilesBad} problems, {result.Snapshots} backup(s).");
        }
        catch (Exception ex)
        {
            _verifyOut.Text = Ui.PlainError(ex);
            _verifyOut.Foreground = Ui.Brush(Ui.Bad);
        }
        _verifyBtn.Content = "Check again";
        _verifyBtn.IsEnabled = true;
    }

    private void OpenFolder()
    {
        if (!Directory.Exists(_outDir)) return;
        try
        {
            if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start("explorer.exe", _outDir);
            else if (OperatingSystem.IsMacOS())
                System.Diagnostics.Process.Start("open", _outDir);
            else
                System.Diagnostics.Process.Start("xdg-open", _outDir);
        }
        catch (Exception ex) { Log("Could not open folder: " + ex.Message); }
    }

    private static string SettingsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZaiChatRecovery");

    private static string LastBackupFile => Path.Combine(SettingsDir, "last-backup.txt");

    private static void RememberBackup(string dir)
    {
        try { Directory.CreateDirectory(SettingsDir); File.WriteAllText(LastBackupFile, dir); } catch { }
    }

    /// <summary>Find a backup made by a previous run: the newest dated snapshot
    /// under Z.ai Backups, the remembered one, or a legacy "Recovered chats"
    /// folder. Enables "Read my chats" and "Check my backups" at startup.</summary>
    private void DetectExistingBackup()
    {
        try
        {
            var latest = Snapshots.Latest();
            if (latest is not null)
            {
                _backupDir = latest;
                Log($"Found your backups: {Snapshots.Root} ({Path.GetFileName(latest)} is the newest)");
                return;
            }
            if (File.Exists(LastBackupFile))
            {
                var dir = File.ReadAllText(LastBackupFile).Trim();
                if (Directory.Exists(dir) && Directory.GetFiles(dir, "*.json").Length > 0)
                {
                    _backupDir = dir;
                    Log($"Found your earlier backup: {dir}");
                    return;
                }
            }
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Recovered chats");
            if (Directory.Exists(root))
            {
                var newest = Directory.GetDirectories(root)
                    .Where(d => Directory.GetFiles(d, "*.json").Length > 0)
                    .OrderByDescending(d => Directory.GetLastWriteTimeUtc(d))
                    .FirstOrDefault();
                if (newest is { } n)
                {
                    _backupDir = n;
                    Log($"Found your earlier backup: {n}");
                }
            }
        }
        catch { }
    }

    private void OpenReader()
    {
        if (_backupDir is null || !Directory.Exists(_backupDir)) return;
        new ReaderWindow(_backupDir).Show(this);   // kept for --open-reader parity
    }
}

// ---------------------------------------------------------------------------
// headless self-test
// ---------------------------------------------------------------------------

internal static class SelfTest
{
    public static int Run()
    {
        var sb = new StringBuilder();
        // Force the native graphics library to load — this is exactly what fails
        // at first launch when native-lib resolution breaks (SkiaSharp crash).
        try
        {
            _ = SkiaSharp.SKImageInfo.Empty;
            sb.AppendLine("skia native init: OK");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"skia native init: FAILED {ex.GetType().Name}: {ex.Message}");
            Write(sb); return 4;
        }
        try
        {
            var cands = Recovery.FindTokenCandidates();
            sb.AppendLine($"token candidates: {cands.Count}");
            string? working = null;
            foreach (var c in cands.Take(8))
            {
                sb.AppendLine($"  candidate: {c.Email} ({c.Where})");
                if (c.Email.StartsWith("guest-")) continue;
                if (working is null && Recovery.TryUseTokenAsync(c.Token).GetAwaiter().GetResult())
                    working = c.Token;
            }
            if (working is null && cands.Count > 0 && Recovery.TryUseTokenAsync(cands[0].Token).GetAwaiter().GetResult())
                working = cands[0].Token;
            if (working is null)
            {
                sb.AppendLine($"result: NO VALID TOKEN (last error: {Recovery.LastAuthError})");
                Write(sb); return 2;
            }
            var chats = Recovery.ListAllChatsAsync().GetAwaiter().GetResult();
            sb.AppendLine($"result: OK, account={Recovery.AccountEmail}, chats={chats.Count}");
            Write(sb); return 0;
        }
        catch (Exception ex)
        {
            sb.AppendLine($"result: EXCEPTION {ex.Message}");
            Write(sb); return 3;
        }
    }

    private static void Write(StringBuilder sb)
    {
        string dir = Path.GetTempPath();
        File.WriteAllText(Path.Combine(dir, "zai-selftest.txt"), sb.ToString());
        try { Console.Out.Write(sb.ToString()); } catch { /* WinExe may have no console */ }
    }
}
