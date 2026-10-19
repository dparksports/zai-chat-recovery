// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Z.ai Chat Recovery contributors
// Licensed under the Apache License, Version 2.0 (see LICENSE file in the repository root).
//
// One place for the app's look: palette, type scale, and the "Bigger text"
// setting. Panic-first rules baked in: nothing smaller than Small (13px),
// nothing a user must act on smaller than Body (17px), primary controls at
// least 48px tall, and Muted meets WCAG AA contrast on Bg and Panel.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ZaiChatRecovery.Desktop;

internal static class Ui
{
    // ---- palette (Shields Up, light) --------------------------------------
    public static readonly Color Bg = Color.Parse("#F5F5F7");
    public static readonly Color Panel = Color.Parse("#FFFFFF");
    public static readonly Color PanelBorder = Color.Parse("#E5E5EA");
    public static readonly Color Text = Color.Parse("#1D1D1F");
    // #86868B was 3.4:1 on Bg - below AA for small text; #6E6E73 clears it.
    public static readonly Color Muted = Color.Parse("#6E6E73");
    public static readonly Color Accent = Color.Parse("#007AFF");
    public static readonly Color AccentDark = Color.Parse("#0062CC");
    public static readonly Color Good = Color.Parse("#1E8E3E");
    public static readonly Color Bad = Color.Parse("#D70015");
    public static readonly Color Warn = Color.Parse("#B45309");
    public static readonly Color InfoBg = Color.Parse("#E5F1FF");
    public static readonly Color InfoBorder = Color.Parse("#A8CFFF");
    public static readonly Color UserTint = Color.Parse("#E5F1FF");

    public static IBrush Brush(Color c) => new SolidColorBrush(c);

    // ---- text-size setting (persisted beside last-backup.txt) -------------
    private static string SettingsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZaiChatRecovery");
    private static string ScaleFile => Path.Combine(SettingsDir, "text-size.txt");

    public static double Scale { get; private set; } = LoadScale();

    public static string ScaleName => Scale switch { 1.5 => "Largest", 1.25 => "Large", _ => "Normal" };

    private static double LoadScale()
    {
        try
        {
            var s = File.ReadAllText(ScaleFile).Trim();
            return s == "large" ? 1.25 : s == "largest" ? 1.5 : 1.0;
        }
        catch { return 1.0; }
    }

    public static void CycleScale()
    {
        Scale = Scale switch { 1.0 => 1.25, 1.25 => 1.5, _ => 1.0 };
        try
        {
            Directory.CreateDirectory(SettingsDir);
            File.WriteAllText(ScaleFile, Scale switch { 1.25 => "large", 1.5 => "largest", _ => "normal" });
        }
        catch { }
    }

    // ---- named sizes (properties: re-read on every rebuild so a text-size
    //      change re-applies without restarting) ----------------------------
    public static double H1 => 30 * Scale;      // hero headline (the reassurance)
    public static double H2 => 26 * Scale;      // window titles, section heads
    public static double Title => 20 * Scale;   // reader: current chat title
    public static double BodyL => 18 * Scale;   // hero sub-line, list headers
    public static double Body => 17 * Scale;    // default body, status, buttons
    public static double Msg => 15.5 * Scale;   // chat transcript text
    public static double Meta => 14 * Scale;    // dates, counts, hints
    public static double Small => 13 * Scale;   // absolute floor (footers)

    public static double BtnH => 48 * Scale;        // primary & toolbar buttons
    public static double BtnHCompact => 40 * Scale; // dense inline actions

    // ---- tiny builders so call sites stay readable ------------------------

    public static TextBlock Txt(string text, double size, Color? fg = null) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap,
        Foreground = Brush(fg ?? Text),
    };

    /// <summary>Map any failure to plain words + the next step. The technical
    /// detail still goes to the log; the status line never shows raw
    /// exception text to a user who is already frightened.</summary>
    public static string PlainError(Exception ex) => ex switch
    {
        HttpRequestException => "We couldn't reach chat.z.ai just now. Check your internet connection, then try again.",
        TaskCanceledException => "chat.z.ai took too long to answer. Wait a moment, then try again.",
        _ => "Something went wrong - nothing on your account was changed. Try again, and if it repeats, see the details below.",
    };

    // ---- polish primitives (the "Shields Up" look) ------------------------

    /// <summary>White card with a hairline border and a soft shadow - the
    /// app's single surface metaphor. Shadow via BoxShadows, not effects.</summary>
    public static Border Card() => new()
    {
        Background = Brush(Panel),
        BorderBrush = Brush(PanelBorder),
        BorderThickness = new(1),
        CornerRadius = new CornerRadius(14),
        Padding = new(22, 18),
        BoxShadow = new Avalonia.Media.BoxShadows(
            new Avalonia.Media.BoxShadow { OffsetY = 4, Blur = 18, Color = Color.FromArgb(26, 0, 0, 0) }),
    };

    /// <summary>Quiet all-caps brand/section label.</summary>
    public static TextBlock SmallCaps(string text) => new()
    {
        Text = text, FontSize = Meta, FontWeight = FontWeight.SemiBold,
        LetterSpacing = 1.2, Foreground = Brush(Muted),
    };

    /// <summary>Accent-filled action (the one big thing to press).</summary>
    public static void StylePrimary(Button b)
    {
        b.FontSize = Body; b.MinHeight = BtnH;
        b.MinWidth = Math.Round(200 * Scale);
        b.Padding = new(24, 10); b.FontWeight = FontWeight.SemiBold;
        b.Background = Brush(Accent); b.Foreground = Brushes.White;
        b.CornerRadius = new CornerRadius(10);
        b.HorizontalAlignment = HorizontalAlignment.Left;
    }

    /// <summary>Outlined secondary action.</summary>
    public static void StyleSecondary(Button b)
    {
        b.FontSize = Body; b.MinHeight = BtnH;
        b.Padding = new(18, 10);
        b.Background = Brush(Panel); b.Foreground = Brush(Text);
        b.BorderBrush = Brush(PanelBorder); b.BorderThickness = new(1);
        b.CornerRadius = new CornerRadius(10);
    }

    /// <summary>Borderless tertiary control (text-size, details, tips).</summary>
    public static void StyleQuiet(Button b)
    {
        b.FontSize = Meta; b.MinHeight = BtnHCompact;
        b.Padding = new(12, 6);
        b.Background = Brushes.Transparent; b.Foreground = Brush(Accent);
        b.BorderThickness = new(0);
        b.CornerRadius = new CornerRadius(8);
    }

    public static Button Primary(string text) { var b = new Button { Content = text }; StylePrimary(b); return b; }
    public static Button Secondary(string text) { var b = new Button { Content = text }; StyleSecondary(b); return b; }

    /// <summary>Window icon from the embedded app.ico. A missing icon is
    /// cosmetic - it must never take the app down.</summary>
    public static void TrySetIcon(Avalonia.Controls.Window w)
    {
        try
        {
            using var s = Avalonia.Platform.AssetLoader.Open(new Uri("avares://ZaiChatRecovery/Assets/app.ico"));
            w.Icon = new Avalonia.Controls.WindowIcon(s);
        }
        catch { }
    }
}
