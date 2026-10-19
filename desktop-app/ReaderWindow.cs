// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Z.ai Chat Recovery contributors
// In-app reader for recovered chats. Loads any backup folder (app or python
// CLI format) and renders each conversation as a chat transcript.

using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace ZaiChatRecovery.Desktop;

internal sealed class ReaderView : UserControl
{
    // look & type scale live in Ui (shared with the main window); hosted as
    // the main window's "Read my chats" tab, or inside ReaderWindow
    private string _dir;
    private readonly List<ChatSummary> _all;
    private readonly ListBox _list = new();
    private readonly TextBox _search = new() { Watermark = "Search chats…", FontSize = Ui.Body, MinHeight = 44 };
    private readonly TextBlock _chatTitle = new() { FontSize = Ui.Title, FontWeight = FontWeight.Bold, Foreground = Ui.Brush(Ui.Text), TextWrapping = TextWrapping.Wrap };
    private readonly Button _openWeb = new() { Content = "Open on chat.z.ai", IsEnabled = false };
    private readonly Button _pasteBtn = new() { Content = "Continue by paste" };
    private readonly Button _reopenBtn = new() { Content = "Open chats in browser" };
    private readonly TextBlock _status = new() { FontSize = Ui.Meta, Foreground = Ui.Brush(Ui.Muted), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _head = new() { FontSize = Ui.H2, FontWeight = FontWeight.Bold, Foreground = Ui.Brush(Ui.Accent) };
    private List<MessageView> _currentViews = new();
    private readonly StackPanel _msgs = new() { Spacing = 10 };
    private readonly ScrollViewer _scroller = new();
    private bool _comboWired;
    private bool _snapsMulti;

    public ReaderView(string folder)
    {
        _dir = folder;

        _all = BackupReader.ScanBackupFolder(folder);
        _scroller.Content = _msgs;

        _search.TextChanged += (_, _) => RefreshList();

        _pasteBtn.Click += async (_, _) => await PasteContinueAsync();
        _reopenBtn.Click += async (_, _) => await ReopenInBrowserAsync();

        // click = read here; double-click = open this chat on chat.z.ai in the
        // default browser - no intermediate page needed
        _list.DoubleTapped += (_, _) =>
        {
            if (_current is { } cur)
                OpenUrl("https://chat.z.ai/c/" + cur.Id);
        };

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
                Text = s?.Title ?? "", FontSize = Ui.Msg, FontWeight = FontWeight.SemiBold,
                Foreground = Ui.Brush(Ui.Text), TextWrapping = TextWrapping.Wrap
            });
            b.Children.Add(new TextBlock
            {
                Text = $"{s?.Messages ?? 0} messages · {s?.UpdatedText}", FontSize = Ui.Meta,
                Foreground = Ui.Brush(Ui.Muted)
            });
            return b;
        });

        // time travel: if this folder is one of several dated snapshots, offer a date picker
        var parent = Path.GetDirectoryName(Path.GetFullPath(folder));
        var snaps = parent is null ? new List<string>() : Snapshots.List(parent);
        _snapsMulti = snaps.Count > 1;
        if (_snapsMulti)
        {
            var names = snaps.Select(Path.GetFileName).ToList()!;
            _snapCombo.ItemsSource = names;
            _snapCombo.SelectedItem = Path.GetFileName(Path.GetFullPath(folder));
            if (!_comboWired)
            {
                _comboWired = true;
                _snapCombo.SelectionChanged += (_, _) =>
                {
                    if (_snapCombo.SelectedItem is string name)
                    {
                        var dir = Path.Combine(parent!, name);
                        if (!string.Equals(Path.GetFullPath(dir), Path.GetFullPath(_dir), StringComparison.OrdinalIgnoreCase))
                            LoadFolder(dir);
                    }
                };
            }
            _status.Text = "Double-click a chat to open it on chat.z.ai. Showing the newest backup - pick an earlier date to go back in time.";
        }

        RenderReader();
        RefreshList();
        if (_all.Count > 0) _list.SelectedIndex = 0;
        if (_status.Text.Length == 0)
            _status.Text = "Click a chat to read it here. Double-click it to open it on chat.z.ai in your browser.";
        else if (!_status.Text.StartsWith("Double-click"))
            _status.Text = "Double-click a chat to open it on chat.z.ai. " + _status.Text;
    }

    private readonly Avalonia.Controls.ComboBox _snapCombo = new() { MinWidth = 170 };

    /// <summary>Compose the window; re-run when the text-size setting changes.
    /// Controls persist, so the selected chat, search text and message scroll
    /// survive a rebuild.</summary>
    private void RenderReader()
    {
        // same re-parenting guard as the main window (see MainWindow.Render)
        foreach (var c in new Control[] { _head, _search, _list, _chatTitle, _openWeb,
                                          _pasteBtn, _reopenBtn, _snapCombo, _status, _scroller })
            DetachHosted(c);

        _head.Text = $"Your chats ({_all.Count})";
        foreach (var b in new[] { _openWeb, _pasteBtn, _reopenBtn })
        {
            b.FontSize = Ui.Meta;
            b.MinHeight = Ui.BtnHCompact;
            b.Padding = new(14, 6);
        }

        var hint = new TextBlock
        {
            Text = "Saved on this computer from the account you recovered. Click a chat to read it.",
            Foreground = Ui.Brush(Ui.Muted), TextWrapping = TextWrapping.Wrap, FontSize = Ui.Meta
        };

        var actions = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
        foreach (var b in new[] { _reopenBtn, _pasteBtn })
        {
            b.Margin = new(0, 0, 10, 6);
            actions.Children.Add(b);
        }
        if (_snapsMulti)
        {
            actions.Children.Add(new TextBlock
            {
                Text = "Backup date:", FontSize = Ui.Meta, VerticalAlignment = VerticalAlignment.Center,
                Margin = new(0, 0, 6, 6), Foreground = Ui.Brush(Ui.Muted)
            });
            _snapCombo.FontSize = Ui.Meta;
            _snapCombo.Margin = new(0, 0, 10, 6);
            actions.Children.Add(_snapCombo);
        }

        var titleHost = new ContentControl { Content = _chatTitle, HorizontalAlignment = HorizontalAlignment.Stretch };

        var left = new Border
        {
            Background = Ui.Brush(Ui.Panel),
            BorderBrush = Ui.Brush(Ui.PanelBorder),
            BorderThickness = new(1),
            CornerRadius = new CornerRadius(10), Padding = new(8),
            Child = _list
        };
        var right = new Border
        {
            Background = Ui.Brush(Ui.Panel),
            BorderBrush = Ui.Brush(Ui.PanelBorder),
            BorderThickness = new(1),
            CornerRadius = new CornerRadius(10), Padding = new(16),
            Child = _scroller
        };
        var listWidth = Math.Round(340 * Ui.Scale);
        var body = new Grid { ColumnDefinitions = new($"{listWidth}, *"), Margin = new(0, 12, 0, 0) };
        body.Children.Add(left);
        Grid.SetColumn(left, 0);
        body.Children.Add(right);
        Grid.SetColumn(right, 1);
        var topGrid = new Grid { ColumnDefinitions = new("*, Auto"), Margin = new(0, 8, 0, 0) };
        topGrid.Children.Add(titleHost);
        _openWeb.VerticalAlignment = VerticalAlignment.Center;
        topGrid.Children.Add(_openWeb);
        Grid.SetColumn(_openWeb, 1);

        // Grid (not StackPanel) so the body gets bounded height - a StackPanel
        // gives infinite height, which makes the ScrollViewer size to content
        // instead of the pane, and scrolling silently stops working.
        var root = new Grid { Margin = new(20), RowDefinitions = new("Auto,Auto,Auto,Auto,Auto,Auto,*") };
        root.Children.Add(_head);
        root.Children.Add(hint);
        hint.Margin = new(0, 6, 0, 8);
        Grid.SetRow(hint, 1);
        root.Children.Add(_search);
        _search.Margin = new(0, 0, 0, 8);
        Grid.SetRow(_search, 2);
        root.Children.Add(topGrid);
        Grid.SetRow(topGrid, 3);
        root.Children.Add(actions);
        Grid.SetRow(actions, 4);
        root.Children.Add(_status);
        _status.Margin = new(0, 4, 0, 0);
        Grid.SetRow(_status, 5);
        root.Children.Add(body);
        Grid.SetRow(body, 6);
        Content = root;
    }

    private void LoadFolder(string dir)
    {
        _dir = dir;
        _all.Clear();
        _all.AddRange(BackupReader.ScanBackupFolder(dir));
        _head.Text = $"Your chats ({_all.Count})";
        RefreshList();
        if (_all.Count > 0) _list.SelectedIndex = 0;
        else _chatTitle.Text = "(nothing in this backup)";
        _current = null;
        _openWeb.IsEnabled = false;
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

    private ChatSummary? _current;

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
        _currentViews = views;
        _chatTitle.Text = s.Title + $"   ({views.Count} messages)";
        _openWeb.IsEnabled = true;
        _msgs.Children.Clear();
        if (views.Count == 0)
        {
            _msgs.Children.Add(new TextBlock
            {
                Text = "(No message content in this backup file.)",
                Foreground = Ui.Brush(Ui.Muted)
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
            Background = Ui.Brush(user ? Ui.UserTint : Ui.Panel),
            BorderBrush = Ui.Brush(Ui.PanelBorder),
            BorderThickness = new(1),
            CornerRadius = new CornerRadius(12),
            Padding = new(14, 10),
            MaxWidth = 760,
            HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        };
        var inner = new StackPanel { Spacing = 6 };
        inner.Children.Add(new TextBlock
        {
            Text = v.Role + (v.Stamp.Length > 0 ? "  ·  " + v.Stamp : ""),
            FontSize = Ui.Meta, FontWeight = FontWeight.Bold,
            Foreground = Ui.Brush(user ? Ui.Accent : Ui.Muted)
        });
        if (v.Thinking.Length > 0)
        {
            var exp = new Expander
            {
                Header = new TextBlock { Text = "assistant's thinking", FontSize = Ui.Meta, Foreground = Ui.Brush(Ui.Muted) },
                Content = new TextBlock
                {
                    Text = v.Thinking.Length > 1200 ? v.Thinking[..1200] + "…" : v.Thinking,
                    FontSize = Ui.Meta, Foreground = Ui.Brush(Ui.Muted), TextWrapping = TextWrapping.Wrap
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
                    Background = Ui.Brush(Ui.Bg),
                    CornerRadius = new CornerRadius(8), Padding = new(10),
                    Child = new TextBlock
                    {
                        Text = seg.Text, FontSize = Ui.Meta, TextWrapping = TextWrapping.Wrap,
                        Foreground = Ui.Brush(Ui.Text)
                    }
                });
            }
            else if (seg.Text.Trim().Length > 0)
            {
                inner.Children.Add(new TextBlock
                {
                    Text = seg.Text, FontSize = Ui.Msg,
                    Foreground = Ui.Brush(Ui.Text), TextWrapping = TextWrapping.Wrap
                });
            }
        }
        if (v.Tools.Length > 0)
            inner.Children.Add(new TextBlock
            {
                Text = "🔧 used: " + v.Tools, FontSize = Ui.Meta,
                Foreground = Ui.Brush(Ui.Muted)
            });
        foreach (var a in v.Attachments)
        {
            if (a.Missing || a.Path is null)
                inner.Children.Add(new TextBlock
                {
                    Text = "(attachment not saved: " + a.Name + ")", FontSize = Ui.Meta,
                    Foreground = Ui.Brush(Ui.Muted), FontStyle = FontStyle.Italic
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
            Background = Ui.Brush(Ui.Bg),
            BorderBrush = Ui.Brush(Ui.PanelBorder),
            BorderThickness = new(1), CornerRadius = new CornerRadius(8),
            Padding = new(10, 6), MaxWidth = 520,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(new TextBlock
        {
            Text = (a.Kind == "audio" ? "🎵 " : "📄 ") + a.Name +
                   (a.Size > 0 ? $"  ({a.Size / 1024.0 / 1024.0:0.#} MB)" : ""),
            FontSize = Ui.Msg, VerticalAlignment = VerticalAlignment.Center,
            Foreground = Ui.Brush(Ui.Text)
        });
        var open = new Button { Content = "Open", Padding = new(12, 4), FontSize = Ui.Meta, MinHeight = Ui.BtnHCompact };
        open.Click += (_, _) => TryOpen(full, false);
        var show = new Button { Content = "Show in folder", Padding = new(12, 4), FontSize = Ui.Meta, MinHeight = Ui.BtnHCompact };
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

    private async Task PasteContinueAsync()
    {
        if (_current is not { } cur) return;
        try
        {
            var text = BuildPasteText(cur.Title, _currentViews);
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                _status.Text = "Clipboard is not available on this system.";
                return;
            }
            await clipboard.SetTextAsync(text);
            OpenUrl("https://chat.z.ai/");
            _status.Text = "Conversation copied. Paste it into the new chat that just opened, and continue where you left off.";
        }
        catch (Exception ex) { _status.Text = "Could not copy: " + ex.Message; }
    }

    internal static string BuildPasteText(string title, List<MessageView> views)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Below is a saved conversation from my Z.ai chat history. Treat it as context and continue from where it left off.");
        sb.AppendLine();
        sb.AppendLine("# " + title);
        foreach (var v in views)
        {
            if (v.Text.Trim().Length == 0) continue;
            sb.AppendLine();
            sb.AppendLine(v.Role.Contains("USER", StringComparison.OrdinalIgnoreCase) ? "Me:" : "GLM:");
            sb.AppendLine(v.Text.Trim());
        }
        if (sb.Length > 28000) sb.Length = 28000;
        return sb.ToString();
    }

    private async Task ReopenInBrowserAsync()
    {
        _reopenBtn.IsEnabled = false;
        try
        {
            _status.Text = "Preparing your chat index…";
            // union across every dated snapshot, written to a temp copy - the
            // snapshots themselves stay untouched
            var path = await Task.Run(() =>
                ReopenPage.WriteToTemp(Recovery.AccountEmail, ReopenPage.UnionFor(_dir)));
            OpenUrl(path);
            _status.Text = "Opened a page in your web browser. Every chat on it still works on"
                         + " chat.z.ai - click Open next to any chat. Chats missing from your"
                         + " newest backup are marked and open too.";
        }
        catch (Exception ex) { _status.Text = "Could not build the page: " + ex.Message; }
        finally { _reopenBtn.IsEnabled = true; }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            // UseShellExecute opens URLs only on Windows; on Unix it execs the
            // URL as a program name and fails silently - use the OS opener.
            if (OperatingSystem.IsWindows())
            {
                var psi = new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true };
                System.Diagnostics.Process.Start(psi);
            }
            else if (OperatingSystem.IsMacOS())
                System.Diagnostics.Process.Start("open", url);
            else
                System.Diagnostics.Process.Start("xdg-open", url);
        }
        catch { }
    }
}


/// <summary>Standalone reader window (the --open-reader path); the main window
/// embeds the same view as its "Read my chats" tab.</summary>
internal sealed class ReaderWindow : Window
{
    public ReaderWindow(string folder)
    {
        Title = $"Your recovered chats — {Path.GetFileName(folder)}";
        Width = 1120; Height = 780;
        MinWidth = 900; MinHeight = 600;
        Background = Ui.Brush(Ui.Bg);
        Ui.TrySetIcon(this);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = new ReaderView(folder);
    }
}
