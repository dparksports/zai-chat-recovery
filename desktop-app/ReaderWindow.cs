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
        var topGrid = new Grid { ColumnDefinitions = new("*, 180"), Margin = new(0, 8, 0, 0) };
        topGrid.Children.Add(titleHost);
        _openWeb.VerticalAlignment = VerticalAlignment.Center;
        topGrid.Children.Add(_openWeb);
        Grid.SetColumn(_openWeb, 1);

        Content = new StackPanel { Margin = new(20), Spacing = 8, Children = { head, hint, _search, topGrid, body } };
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
        card.Child = inner;
        return card;
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
