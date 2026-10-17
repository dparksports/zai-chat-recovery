// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Z.ai Chat Recovery contributors
// Tiny modal confirmation dialog (Avalonia has no built-in MessageBox).

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ZaiChatRecovery.Desktop;

internal static class ConfirmBox
{
    public static async Task<bool> ShowAsync(Window owner, string title, string message)
    {
        var tcs = new TaskCompletionSource<bool>();
        var dlg = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.Height,
            Width = 460,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = new SolidColorBrush(Color.Parse("#F5F5F7")),
        };
        var text = new TextBlock
        {
            Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#1D1D1F")),
        };
        var cancel = new Button { Content = "Cancel", Padding = new(16, 8) };
        var go = new Button
        {
            Content = "Restore", Padding = new(16, 8),
            Background = new SolidColorBrush(Color.Parse("#007AFF")),
            Foreground = Brushes.White,
        };
        cancel.Click += (_, _) => { tcs.TrySetResult(false); dlg.Close(); };
        go.Click += (_, _) => { tcs.TrySetResult(true); dlg.Close(); };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right };
        row.Children.Add(cancel);
        row.Children.Add(go);
        dlg.Content = new StackPanel { Margin = new(20), Spacing = 18, Children = { text, row } };
        await dlg.ShowDialog(owner);
        return await tcs.Task;
    }
}
