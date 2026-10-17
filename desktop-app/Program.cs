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
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
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
            try { desktop.MainWindow = new MainWindow(); }
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
    private readonly TextBlock _folderLabel = new() { Foreground = new SolidColorBrush(Muted), TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _bar = new() { Height = 16, Minimum = 0, Maximum = 100, Value = 0 };
    private readonly TextBox _log = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    private string _outDir = "";
    private bool _autoRescan;
    private bool _busy;

    // Panic-grade copy: short words, one idea per line, big type in the panel
    private static readonly (bool Good, string Tip)[] Tips =
    {
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

        _outDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is var h && h.Length > 0 ? h
                : Environment.CurrentDirectory,
            "Documents",
            "Recovered chats", DateTime.Now.ToString("yyyy-MM-dd"));
        _folderLabel.Text = "Chats will be saved to: " + _outDir;

        _checkBtn.Click += async (_, _) => await CheckAsync();
        _saveBtn.Click += async (_, _) => await SaveAsync();
        _openBtn.Click += (_, _) => OpenFolder();
        Activated += async (_, _) => { if (_autoRescan && !_busy) await CheckAsync(); };

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
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _checkBtn, _saveBtn, _openBtn } };
        _log.Height = 330;
        var footer = new TextBlock
        {
            Text = "Free and open source (Apache-2.0) · Your saved sign-in never leaves this computer · Requests go to chat.z.ai only",
            Foreground = new SolidColorBrush(Color.Parse("#64748b")), TextWrapping = TextWrapping.Wrap, FontSize = 11
        };
        return new StackPanel { Spacing = 12, Children = { title, sub, statusBox, buttons, _folderLabel, _bar, _log, footer } };
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
            _openBtn.IsEnabled = true;
            SetStatus($"All done - {summary.Done} chats saved to your computer.\nThey are also still on the website; the missing list was a display problem.\nPress \"Open the folder\" to read them.",
                new SolidColorBrush(Good));
            Log($"Done. {summary.Done} chats, {summary.Failed} failed. Folder: {summary.OutDir}");
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
}

// ---------------------------------------------------------------------------
// headless self-test
// ---------------------------------------------------------------------------

internal static class SelfTest
{
    public static int Run()
    {
        var sb = new StringBuilder();
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
