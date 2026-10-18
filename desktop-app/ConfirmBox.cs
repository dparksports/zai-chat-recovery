// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Z.ai Chat Recovery contributors
// Licensed under the Apache License, Version 2.0 (see LICENSE file in the repository root).
// Tiny modal confirmation dialog (Avalonia has no built-in MessageBox).
// Same design language as the main window: card, big readable type, one
// accent-filled action. This dialog guards the app's ONLY possible write,
// so it stays unmissorable: 17px+ text, 48px+ buttons.

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
            Width = Math.Round(560 * Ui.Scale),
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = Ui.Brush(Ui.Bg),
        };
        var text = new TextBlock
        {
            Text = message, TextWrapping = TextWrapping.Wrap, FontSize = Ui.Body,
            Foreground = Ui.Brush(Ui.Text),
        };
        var cancel = new Button { Content = "Cancel" };
        Ui.StyleSecondary(cancel);
        var go = new Button { Content = "Restore" };
        Ui.StylePrimary(go);
        go.HorizontalAlignment = HorizontalAlignment.Left;
        cancel.Click += (_, _) => { tcs.TrySetResult(false); dlg.Close(); };
        go.Click += (_, _) => { tcs.TrySetResult(true); dlg.Close(); };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(cancel);
        row.Children.Add(go);
        var card = Ui.Card();
        card.Child = new StackPanel { Spacing = 18, Children = { text, row } };
        dlg.Content = new Border { Margin = new(18), Child = card };
        await dlg.ShowDialog(owner);
        return await tcs.Task;
    }
}
