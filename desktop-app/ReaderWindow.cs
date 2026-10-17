// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Z.ai Chat Recovery contributors
// In-app reader for recovered chats. Loads any backup folder (app or python
// CLI format) and renders each conversation as a chat transcript.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace ZaiChatRecovery.Desktop;

internal sealed class ReaderWindow : Window
{
    // same palette as the main window (Shields Up light)
    private static readonly Color Bg = Color.Parse("#F5F5F7");
    private static readonly Color Panel = Color.Parse("#FFFFFF");
    private static readonly Color PanelBorder = Color.Parse("#E5E5EA");
    private static readonly Color Text = Color.Parse("#1D1D1F");
    private static readonly Color Muted = Color.Parse("#86868B");
    private static readonly Color Accent = Color.Parse("#007AFF");
    private static readonly Color UserTint = Color.Parse("#E5F1FF");

    private readonly string _dir;
    private readonly List<ChatSummary> _all;
    private readonly ListBox _list = new();
    private readonly TextBox _search = new() { Watermark = "Search chats…" };
    private readonly TextBlock _chatTitle = new() { FontSize = 18, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Text), TextWrapping = TextWrapping.Wrap };
    private readonly Button _openWeb = new() { Content = "Open on chat.z.ai", Padding = new(12, 6), IsEnabled = false };
    private readonly Button _fetchBtn = new() { Content = "Fetch attachments", Padding = new(12, 6) };
    private readonly TextBlock _status = new() { FontSize = 11, Foreground = new SolidColorBrush(Muted), TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _msgs = new() { Spacing = 10 };
    private readonly ScrollViewer _scroller = new();

    public ReaderWindow(string folder)
    {
        _dir = folder;
        Title = $"Your recovered chats — {System.IO.Path.GetFileName(folder)}";
        Width = 1120; Height = 780;
        MinWidth = 900; MinHeight = 600;
        Background = new SolidColorBrush(Bg);
        StartPositionFix();
        Background = new SolidColorBrush(Bg);

        _all = BackupReader.ScanBackupFolder(folder);
        _scroller.Content = _msgs;

        var head = new TextBlock
        {
            Text = $"Your chats ({_all.Count})",
            FontSize = 22, FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Accent)
        };
        var hint = new TextBlock
        {
            Text = "Saved on this computer from the account you recovered. Click a chat to read it.",
            Foreground = new SolidColorBrush(Muted), TextWrapping = TextWrapping.Wrap
        };
        _search.TextChanged += (_, _) => RefreshList();

        _fetchBtn.Click += async (_, _) => await FetchCurrentAsync();

        var titleHost = new ContentControl { Content = _chatTitle, HorizontalAlignment = HorizontalAlignment.Stretch };
        _openWeb.Click += (_, _) =>
        {
            if (_current is { } cur)
                OpenUrl("https://chat.z.ai/c/" + cur.Id);
        };

        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItem is ChatSummary s) Show(s);
        };
        // items must BE ChatSummary objects (not pre-built panels) so that
        // SelectedItem pattern-matches in the handler above
        _list.ItemTemplate = new FuncDataTemplate<ChatSummary>((s, _) =>
        {
            var b = new StackPanel { Spacing = 2, Margin = new(4, 6, 4, 6) };
            b.Children.Add(new TextBlock
            {
                Text = s?.Title ?? "", FontSize = 13, FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(Text), TextWrapping = TextWrapping.Wrap
            });
            b.Children.Add(new TextBlock
            {
                Text = $"{s?.Messages ?? 0} messages · {s?.UpdatedText}", FontSize = 11,
                Foreground = new SolidColorBrush(Muted)
            });
            return b;
        });

        var left = new Border
        {
            Background = new SolidColorBrush(Panel),
            BorderBrush = new SolidColorBrush(PanelBorder),
            BorderThickness = new(1),
            CornerRadius = new CornerRadius(10), Padding = new(8),
            Child = _list
        };
        var right = new Border
        {
            Background = new SolidColorBrush(Panel),
            BorderBrush = new SolidColorBrush(PanelBorder),
            BorderThickness = new(1),
            CornerRadius = new CornerRadius(10), Padding = new(16),
            Child = _scroller
        };
        var body = new Grid { ColumnDefinitions = new("320, *"), Margin = new(0, 12, 0, 0) };
        body.Children.Add(left);
        Grid.SetColumn(left, 0);
        body.Children.Add(right);
        Grid.SetColumn(right, 1);
        var topGrid = new Grid { ColumnDefinitions = new("*, 165, 165"), Margin = new(0, 8, 0, 0) };
        topGrid.Children.Add(titleHost);
        _openWeb.VerticalAlignment = VerticalAlignment.Center;
        topGrid.Children.Add(_openWeb);
        Grid.SetColumn(_openWeb, 1);
        _fetchBtn.VerticalAlignment = VerticalAlignment.Center;
        topGrid.Children.Add(_fetchBtn);
        Grid.SetColumn(_fetchBtn, 2);

        // Grid (not StackPanel) so the body gets bounded height - a StackPanel
        // gives infinite height, which makes the ScrollViewer size to content
        // instead of the pane, and scrolling silently stops working.
        var root = new Grid { Margin = new(20), RowDefinitions = new("Auto,Auto,Auto,Auto,*") };
        root.Children.Add(head);
        root.Children.Add(hint);
        hint.Margin = new(0, 6, 0, 8);
        Grid.SetRow(hint, 1);
        root.Children.Add(_search);
        _search.Margin = new(0, 0, 0, 8);
        Grid.SetRow(_search, 2);
        root.Children.Add(topGrid);
        Grid.SetRow(topGrid, 3);
        root.Children.Add(_status);
        _status.Margin = new(0, 4, 0, 0);
        Grid.SetRow(_status, 4);
        root.Children.Add(body);
        Grid.SetRow(body, 5);
        root.RowDefinitions = new("Auto,Auto,Auto,Auto,Auto,*");
        Content = root;
        RefreshList();
        if (_all.Count > 0) _list.SelectedIndex = 0;
    }

    private ChatSummary? _current;

    private void StartPositionFix() => WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterScreen;

    private void RefreshList()
    {
        var q = (_search.Text ?? "").Trim();
        _list.ItemsSource = _all
            .Where(s => q.Length == 0 || s.Title.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private void Show(ChatSummary s)
    {
        _current = s;
        var views = BackupReader.LoadChat(s);
        _chatTitle.Text = s.Title + $"   ({views.Count} messages)";
        _openWeb.IsEnabled = true;
        _msgs.Children.Clear();
        if (views.Count == 0)
        {
            _msgs.Children.Add(new TextBlock
            {
                Text = "(No message content in this backup file.)",
                Foreground = new SolidColorBrush(Muted)
            });
        }
        foreach (var v in views)
            _msgs.Children.Add(Bubble(v));
        _scroller.Offset = Vector.Zero;
        _scroller.ScrollToEnd();
    }

    private Control Bubble(MessageView v)
    {
        bool user = v.Role.Contains("USER", StringComparison.OrdinalIgnoreCase);
        var card = new Border
        {
            Background = new SolidColorBrush(user ? UserTint : Panel),
            BorderBrush = new SolidColorBrush(PanelBorder),
            BorderThickness = new(1),
            CornerRadius = new CornerRadius(12),
            Padding = new(14, 10),
            MaxWidth = 720,
            HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        };
        var inner = new StackPanel { Spacing = 6 };
        inner.Children.Add(new TextBlock
        {
            Text = v.Role + (v.Stamp.Length > 0 ? "  ·  " + v.Stamp : ""),
            FontSize = 11, FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(user ? Accent : Muted)
        });
        if (v.Thinking.Length > 0)
        {
            var exp = new Expander
            {
                Header = new TextBlock { Text = "assistant's thinking", FontSize = 11, Foreground = new SolidColorBrush(Muted) },
                Content = new TextBlock
                {
                    Text = v.Thinking.Length > 1200 ? v.Thinking[..1200] + "…" : v.Thinking,
                    FontSize = 12, Foreground = new SolidColorBrush(Muted), TextWrapping = TextWrapping.Wrap
                }
            };
            inner.Children.Add(exp);
        }
        foreach (var seg in SplitFences(v.Text))
        {
            if (seg.IsCode)
            {
                inner.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#F5F5F7")),
                    CornerRadius = new CornerRadius(8), Padding = new(10),
                    Child = new TextBlock
                    {
                        Text = seg.Text, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(Text)
                    }
                });
            }
            else if (seg.Text.Trim().Length > 0)
            {
                inner.Children.Add(new TextBlock
                {
                    Text = seg.Text, FontSize = 13.5,
                    Foreground = new SolidColorBrush(Text), TextWrapping = TextWrapping.Wrap
                });
            }
        }
        if (v.Tools.Length > 0)
            inner.Children.Add(new TextBlock
            {
                Text = "🔧 used: " + v.Tools, FontSize = 11,
                Foreground = new SolidColorBrush(Muted)
            });
        foreach (var a in v.Attachments)
        {
            if (a.Missing || a.Path is null)
                inner.Children.Add(new TextBlock
                {
                    Text = "(attachment not saved: " + a.Name + ")", FontSize = 11,
                    Foreground = new SolidColorBrush(Muted), FontStyle = FontStyle.Italic
                });
            else
                inner.Children.Add(AttachmentCard(a));
        }
        card.Child = inner;
        return card;
    }

    private Control AttachmentCard(AttachmentView a)
    {
        var full = Path.Combine(_dir, a.Path!.Replace('/', Path.DirectorySeparatorChar));
        if (a.Kind == "image")
        {
            try
            {
                return new Image
                {
                    Source = new Avalonia.Media.Imaging.Bitmap(full),
                    MaxWidth = 380, Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
            }
            catch { /* fall through to file card */ }
        }
        var card = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F5F5F7")),
            BorderBrush = new SolidColorBrush(PanelBorder),
            BorderThickness = new(1), CornerRadius = new CornerRadius(8),
            Padding = new(10, 6), MaxWidth = 480,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(new TextBlock
        {
            Text = (a.Kind == "audio" ? "🎵 " : "📄 ") + a.Name +
                   (a.Size > 0 ? $"  ({a.Size / 1024.0 / 1024.0:0.#} MB)" : ""),
            FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Text)
        });
        var open = new Button { Content = "Open", Padding = new(10, 3), FontSize = 11 };
        open.Click += (_, _) => TryOpen(full, false);
        var show = new Button { Content = "Show in folder", Padding = new(10, 3), FontSize = 11 };
        show.Click += (_, _) => TryOpen(full, true);
        row.Children.Add(open);
        row.Children.Add(show);
        card.Child = row;
        return card;
    }

    private static void TryOpen(string full, bool reveal)
    {
        try
        {
            if (!File.Exists(full)) return;
            if (OperatingSystem.IsWindows())
            {
                System.Diagnostics.Process.Start("explorer.exe",
                    reveal ? $"/select,\"{full}\"" : full);
            }
            else if (OperatingSystem.IsMacOS())
                System.Diagnostics.Process.Start("open", reveal ? $"-R {full}" : full);
            else
                System.Diagnostics.Process.Start("xdg-open",
                    reveal ? Path.GetDirectoryName(full) ?? full : full);
        }
        catch { }
    }

    private async Task FetchCurrentAsync()
    {
        if (_current is not { } cur) { _status.Text = "Select a chat first."; return; }
        _fetchBtn.IsEnabled = false;
        try
        {
            if (Recovery.Token is null)
            {
                _status.Text = "Looking for your saved sign-in…";
                var ok = false;
                foreach (var c in await Task.Run(Recovery.FindTokenCandidates))
                {
                    if (c.Email.StartsWith("guest-", StringComparison.Ordinal)) continue;
                    if (await Task.Run(() => Recovery.TryUseTokenAsync(c.Token).GetAwaiter().GetResult()))
                    {
                        ok = true;
                        break;
                    }
                }
                if (!ok)
                {
                    _status.Text = "No working sign-in - open chat.z.ai in your browser, log in, then try again.";
                    return;
                }
            }
            _status.Text = "Fetching this chat and its attachments from chat.z.ai…";
            var (n, att, _) = await Task.Run(() => Recovery.RefetchChatAsync(cur.Id, cur.Title, _dir).GetAwaiter().GetResult());
            _all.Clear();
            _all.AddRange(BackupReader.ScanBackupFolder(_dir));
            RefreshList();
            var idx = _all.FindIndex(x => x.Id == cur.Id);
            if (idx >= 0) _list.SelectedIndex = idx;
            _status.Text = att > 0
                ? $"Saved {att} attachment(s) - {n} messages refreshed."
                : $"No attachments on this chat - {n} messages refreshed.";
        }
        catch (Exception ex) { _status.Text = "Could not fetch: " + ex.Message; }
        finally { _fetchBtn.IsEnabled = true; }
    }

    private readonly record struct Segment(string Text, bool IsCode);

    private static IEnumerable<Segment> SplitFences(string text)
    {
        var parts = text.Split("```", StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 && text.Trim().Length > 0) yield return new Segment(text, false);
        // parts alternate text/code only if text starts outside a fence; treat odd indexes as code heuristically
        for (int i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            bool code = i % 2 == 1;
            if (code)
            {
                // drop the language token on the first code line
                var nl = p.IndexOf('\n');
                if (nl > 0 && p[..nl].Trim().Length < 20) p = p[(nl + 1)..];
            }
            if (p.Trim().Length > 0) yield return new Segment(p, code);
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true };
            System.Diagnostics.Process.Start(psi);
        }
        catch { }
    }
}
