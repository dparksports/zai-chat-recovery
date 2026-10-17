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

    internal static void WriteCrash(object? ex) =>
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "zai-recovery-crash.log"),
            $"[{DateTime.Now:O}] {ex}{Environment.NewLine}");

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
            return SelfTest.Run();
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
        => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
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
            }
            catch (Exception ex) { Program.WriteCrash(ex); throw; }
        }
        base.OnFrameworkInitializationCompleted();
    }
}

internal sealed class MainWindow : Window
{
    // Shields Up palette, light theme: #F5F5F7 canvas, white cards, #1D1D1F text,
    // #007AFF accent, #D70015 danger, #86868B muted
    private static readonly Color Bg = Color.Parse("#F5F5F7");
    private static readonly Color Panel = Color.Parse("#FFFFFF");
    private static readonly Color PanelBorder = Color.Parse("#E5E5EA");
    private static readonly Color Text = Color.Parse("#1D1D1F");
    private static readonly Color Muted = Color.Parse("#86868B");
    private static readonly Color Cyan = Color.Parse("#007AFF");
    private static readonly Color Good = Color.Parse("#1E8E3E");
    private static readonly Color Bad = Color.Parse("#D70015");

    private readonly TextBlock _status = new() { Text = "Click \"Check my chats\" to start — this app can only read, never change anything.", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Text) };
    private readonly Button _checkBtn = new() { Content = "Check my chats", Padding = new(14, 8) };
    private readonly Button _saveBtn = new() { Content = "Save all my chats", Padding = new(14, 8), IsEnabled = false };
    private readonly Button _openBtn = new() { Content = "Open the folder", Padding = new(14, 8), IsEnabled = false };
    private readonly Button _readBtn = new() { Content = "Read my chats", Padding = new(14, 8), IsEnabled = false };
    private readonly Button _verifyBtn = new() { Content = "Check my backups", Padding = new(14, 8), IsEnabled = false };
    private readonly CheckBox _autoBox = new() { Content = "Remember for me — back up every month" };
    private string? _backupDir;
    private readonly TextBlock _folderLabel = new() { Foreground = new SolidColorBrush(Muted), TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _bar = new() { Height = 16, Minimum = 0, Maximum = 100, Value = 0 };
    private readonly TextBox _log = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    private string _outDir = "";
    private bool _autoRescan;
    private bool _busy;

        // Panic-grade copy: short words, one idea per line, big type in the panel
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
        Width = 1020; Height = 780;
        Background = new SolidColorBrush(Bg);
        MinWidth = 900; MinHeight = 640;

        _outDir = Snapshots.NextSnapshotDir();
        _folderLabel.Text = "Backups go to: " + Snapshots.Root + "  (a new dated folder each time — older ones are never touched)";

        _checkBtn.Click += async (_, _) => await CheckAsync();
        _saveBtn.Click += async (_, _) => await SaveAsync();
        _openBtn.Click += (_, _) => OpenFolder();
        _readBtn.Click += (_, _) => OpenReader();
        _verifyBtn.Click += async (_, _) => await VerifyBackupsAsync();
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

        var root = new Grid { ColumnDefinitions = new("*, 300"), Margin = new(20) };
        root.Children.Add(BuildLeftPanel());
        Grid.SetColumn(root.Children[^1], 0);
        root.Children.Add(BuildTipsPanel());
        Grid.SetColumn(root.Children[^1], 1);
        Content = root;
    }

    private Control BuildLeftPanel()
    {
        var title = new TextBlock { Text = "Z.ai Chat Recovery", FontSize = 26, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Cyan) };
        var sub = new TextBlock
        {
            Text = "An empty sidebar almost never means lost chats. This app checks, then saves everything - read-only.",
            Foreground = new SolidColorBrush(Muted), TextWrapping = TextWrapping.Wrap
        };
        var statusBox = new Border
        {
            Background = new SolidColorBrush(Panel),
            BorderBrush = new SolidColorBrush(PanelBorder),
            BorderThickness = new(1),
            CornerRadius = new CornerRadius(10), Padding = new(16),
            Child = _status, MinHeight = 64
        };
        var loginReminder = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#E5F1FF")),
            BorderBrush = new SolidColorBrush(Color.Parse("#A8CFFF")),
            BorderThickness = new(1),
            CornerRadius = new CornerRadius(10), Padding = new(14, 10),
            Child = new TextBlock
            {
                Text = "ℹ  First time? Open chat.z.ai in your browser and sign in with your Google account. Your chats live in that account — this app uses that saved sign-in to find and save them.",
                TextWrapping = TextWrapping.Wrap, FontSize = 13,
                Foreground = new SolidColorBrush(Text)
            }
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _checkBtn, _saveBtn, _readBtn, _openBtn } };
        var secondRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, Margin = new(0, 2, 0, 0), Children = { _verifyBtn, _autoBox } };
        _autoBox.VerticalAlignment = VerticalAlignment.Center;
        _log.Height = 330;
        var footer = new TextBlock
        {
            Text = "Free and open source (Apache-2.0) · Your saved sign-in never leaves this computer · Requests go to chat.z.ai only",
            Foreground = new SolidColorBrush(Color.Parse("#64748b")), TextWrapping = TextWrapping.Wrap, FontSize = 11
        };
        return new StackPanel { Spacing = 12, Children = { title, sub, loginReminder, statusBox, buttons, secondRow, _folderLabel, _bar, _log, footer } };
    }

    private Control BuildTipsPanel()
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "IF THE LIST GOES EMPTY",
            FontSize = 12, FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Muted)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Your chats are almost never gone.",
            FontSize = 16, FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Text), TextWrapping = TextWrapping.Wrap
        });
        bool? lastGood = null;
        foreach (var (good, tip) in Tips)
        {
            if (lastGood != good)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = good ? "DO" : "DON'T",
                    FontSize = 17, FontWeight = FontWeight.Bold, Margin = new(0, 10, 0, 0),
                    Foreground = new SolidColorBrush(good ? Good : Bad)
                });
                lastGood = good;
            }
            var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14, Margin = new(6, 0, 0, 0) };
            tb.Inlines.Add(new Run { Text = good ? "✓  " : "✗  ", FontSize = 15, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(good ? Good : Bad) });
            tb.Inlines.Add(new Run { Text = tip, Foreground = new SolidColorBrush(Text) });
            panel.Children.Add(tb);
        }
        return new Border
        {
            Background = new SolidColorBrush(Panel),
            BorderBrush = new SolidColorBrush(PanelBorder),
            BorderThickness = new(1),
            CornerRadius = new CornerRadius(10), Padding = new(16),
            Child = new ScrollViewer { Content = panel }
        };
    }

    private void Log(string s) => _log.Text += s + Environment.NewLine;

    private void SetStatus(string text, IBrush color)
    {
        _status.Text = text;
        _status.Foreground = color;
    }

    private async Task CheckAsync()
    {
        if (_busy) return;
        _busy = true; _autoRescan = false;
        _checkBtn.IsEnabled = false;
        SetStatus("Checking your saved sign-in in Chrome, Edge, Brave and Firefox…", new SolidColorBrush(Muted));
        Log("Looking for a chat.z.ai sign-in in browser storage…");
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
            SetStatus("We couldn't find a working sign-in on this computer.\n\nOpen your browser, go to chat.z.ai and log in (not as guest), then come back - we'll re-check automatically when the browser is in front.",
                new SolidColorBrush(Color.Parse("#B45309")));
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
                SetStatus($"Signed in as {Recovery.AccountEmail}, but this account's list is empty.\n\nIf you had chats, they may be under a different login method (for example Google vs email). Log in to chat.z.ai with that account, then press Check again.",
                    new SolidColorBrush(Color.Parse("#B45309")));
                _autoRescan = true;
            }
            else
            {
                SetStatus($"Good news - we can see your {preview.Item1} chats on the account {Recovery.AccountEmail}.\nThey are safe on the server. Press \"Save all my chats\" to keep a copy on this computer.",
                    new SolidColorBrush(Good));
                _saveBtn.IsEnabled = true;
            }
            Log($"Signed in as {Recovery.AccountEmail} ({preview.Item1} chats visible).");
        }
        _checkBtn.IsEnabled = true;
        _busy = false;
    }

    private async Task SaveAsync()
    {
        if (_busy) return;
        _busy = true;
        _checkBtn.IsEnabled = false; _saveBtn.IsEnabled = false;
        _bar.Value = 0;
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
                        Log($"  saving chat {i} of {total}: {title}");
                    });
                    return Task.CompletedTask;
                });
            var (linked, savedMb) = Snapshots.Deduplicate(_outDir, prev);
            Snapshots.WriteManifest(_outDir);
            _openBtn.IsEnabled = true;
            _backupDir = _outDir;
            _readBtn.IsEnabled = true;
            _verifyBtn.IsEnabled = true;
            RememberBackup(_outDir);
            SetStatus($"All done - {summary.Done} chats saved as backup \"{Path.GetFileName(_outDir)}\".\nOlder backups are untouched, so you can always go back to them.\nPress \"Read my chats\" to read them here.",
                new SolidColorBrush(Good));
            Log($"Done. {summary.Done} chats, {summary.Failed} failed. Backup folder: {_outDir}");
            if (prev is not null)
                Log($"  {linked} unchanged files shared with the previous backup (no extra space used).");
            Log("  Integrity manifest written (manifest.json).");
        }
        catch (Exception ex)
        {
            SetStatus("Something went wrong while saving - see the details below. Nothing on your account was changed.",
                new SolidColorBrush(Bad));
            Log("ERROR: " + ex.Message);
        }
        _checkBtn.IsEnabled = true; _saveBtn.IsEnabled = true;
        _busy = false;
    }

    private async Task VerifyBackupsAsync()
    {
        if (_backupDir is null) return;
        _verifyBtn.IsEnabled = false;
        SetStatus("Checking every backup against its integrity list…", new SolidColorBrush(Muted));
        var root = Snapshots.List(Path.GetDirectoryName(_backupDir)).Count > 0
            ? Path.GetDirectoryName(_backupDir)!
            : _backupDir;
        var result = await Task.Run(() => Snapshots.VerifyAll(root));
        var good = result.FilesBad == 0;
        SetStatus(Snapshots.PlainReport(result),
            new SolidColorBrush(good ? Good : Bad));
        foreach (var p in result.Problems) Log("  " + p);
        Log($"Check finished: {result.FilesOk} files OK, {result.FilesBad} problems, {result.Snapshots} backup(s).");
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
                _readBtn.IsEnabled = true;
                _verifyBtn.IsEnabled = true;
                Log($"Found your backups: {Snapshots.Root} ({Path.GetFileName(latest)} is the newest)");
                return;
            }
            if (File.Exists(LastBackupFile))
            {
                var dir = File.ReadAllText(LastBackupFile).Trim();
                if (Directory.Exists(dir) && Directory.GetFiles(dir, "*.json").Length > 0)
                {
                    _backupDir = dir;
                    _readBtn.IsEnabled = true;
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
                    _readBtn.IsEnabled = true;
                    Log($"Found your earlier backup: {n}");
                }
            }
        }
        catch { }
    }

    private void OpenReader()
    {
        if (_backupDir is null || !Directory.Exists(_backupDir)) return;
        new ReaderWindow(_backupDir).Show(this);
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
