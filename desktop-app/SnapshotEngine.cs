// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Z.ai Chat Recovery contributors
// Dated snapshots ("go back in time"): every backup run creates a new
// yyyy-MM-dd HHmm folder under Documents/Z.ai Backups. Snapshots are never
// modified; identical files across snapshots are hardlinked so unchanged
// chats cost nothing. Each snapshot carries a SHA-256 manifest for
// verification, and an optional monthly schedule remembers to run for you.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace ZaiChatRecovery.Desktop;

internal static class Hardlink
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string newFile, string existingFile, IntPtr attrs);

    [DllImport("libc", SetLastError = true)]
    private static extern int link(string existing, string newPath);

    public static bool TryCreate(string dest, string existing) =>
        OperatingSystem.IsWindows() ? CreateHardLink(dest, existing, IntPtr.Zero) : link(existing, dest) == 0;
}

internal static class Snapshots
{
    public static string Root =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Z.ai Backups");

    private static readonly System.Text.RegularExpressions.Regex NameRe =
        new(@"^\d{4}-\d{2}-\d{2} \d{4}$");

    /// <summary>Next snapshot folder name (does not create it).</summary>
    public static string NextSnapshotDir() => Path.Combine(Root, DateTime.Now.ToString("yyyy-MM-dd HHmm"));

    public static List<string> List(string? root = null)
    {
        root ??= Root;
        return Directory.Exists(root)
            ? Directory.GetDirectories(root)
                .Where(d => NameRe.IsMatch(Path.GetFileName(d)))
                .OrderByDescending(d => d, StringComparer.Ordinal)
                .ToList()
            : new List<string>();
    }

    public static string? Latest(string? root = null) => List(root).FirstOrDefault();

    /// <summary>Replace files in the new snapshot that are byte-identical to the
    /// previous snapshot with hardlinks to them. Folders stay complete and
    /// independent; unchanged content costs no extra space.</summary>
    public static (int linked, long savedBytes) Deduplicate(string snap, string? prev)
    {
        if (prev is null || !Directory.Exists(prev) || !Directory.Exists(snap)) return (0, 0);
        int linked = 0;
        long saved = 0;
        foreach (var file in Directory.GetFiles(snap, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase)) continue;
            var rel = Path.GetRelativePath(snap, file);
            var prevFile = Path.Combine(prev, rel);
            if (!File.Exists(prevFile)) continue;
            var a = new FileInfo(file);
            var b = new FileInfo(prevFile);
            if (a.Length != b.Length || !SameContent(file, prevFile)) continue;
            try
            {
                File.Delete(file);
                if (Hardlink.TryCreate(file, prevFile))
                {
                    linked++;
                    saved += a.Length;
                }
                else
                {
                    File.Copy(prevFile, file);
                }
            }
            catch
            {
                try { if (!File.Exists(file)) File.Copy(prevFile, file); } catch { }
            }
        }
        return (linked, saved);
    }

    private static bool SameContent(string x, string y)
    {
        using var hx = SHA256.Create();
        using var hy = SHA256.Create();
        using var fx = File.OpenRead(x);
        using var fy = File.OpenRead(y);
        var a = hx.ComputeHash(fx);
        var b = hy.ComputeHash(fy);
        return a.AsSpan().SequenceEqual(b);
    }

    /// <summary>manifest.json in every snapshot: every file's SHA-256, so backups
    /// can be verified later ("check my backups") and corruption detected.</summary>
    public static void WriteManifest(string snap)
    {
        var files = new SortedDictionary<string, string>();
        foreach (var f in Directory.GetFiles(snap, "*", SearchOption.AllDirectories))
        {
            // manifest.json is the manifest itself; reopen-chats.html is a
            // regenerable index page - covering it would make any later
            // regeneration fail verification, and it protects no user data.
            if (f.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase)
                || f.EndsWith("reopen-chats.html", StringComparison.OrdinalIgnoreCase)) continue;
            using var h = SHA256.Create();
            using var fs = File.OpenRead(f);
            files[Path.GetRelativePath(snap, f).Replace('\\', '/')] =
                Convert.ToHexString(h.ComputeHash(fs)).ToLowerInvariant();
        }
        File.WriteAllText(Path.Combine(snap, "manifest.json"),
            JsonSerializer.Serialize(new { generated = DateTime.Now, files },
                new JsonSerializerOptions { WriteIndented = true }));
    }

    public sealed record VerifyResult(int Snapshots, int FilesOk, int FilesBad, List<string> Problems);

    /// <summary>Verify every snapshot against its manifest. Snapshots without a
    /// manifest are reported as unverified rather than failed.</summary>
    public static VerifyResult VerifyAll(string? root = null)
    {
        root ??= Root;
        var problems = new List<string>();
        int ok = 0, bad = 0, snaps = 0;
        foreach (var snap in List(root).OrderBy(d => d))
        {
            snaps++;
            var manifest = Path.Combine(snap, "manifest.json");
            if (!File.Exists(manifest))
            {
                problems.Add($"{Path.GetFileName(snap)}: no manifest (older snapshot, not verified)");
                continue;
            }
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                var files = doc.RootElement.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Object
                    ? f : default;
                if (files.ValueKind != JsonValueKind.Object) { problems.Add($"{Path.GetFileName(snap)}: manifest unreadable"); continue; }
                foreach (var prop in files.EnumerateObject())
                {
                    var path = Path.Combine(snap, prop.Name.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(path))
                    {
                        bad++;
                        problems.Add($"{Path.GetFileName(snap)}: missing {prop.Name}");
                        continue;
                    }
                    using var h = SHA256.Create();
                    using var fs = File.OpenRead(path);
                    var actual = Convert.ToHexString(h.ComputeHash(fs)).ToLowerInvariant();
                    if (actual == prop.Value.GetString()) ok++;
                    else
                    {
                        bad++;
                        problems.Add($"{Path.GetFileName(snap)}: changed/corrupt {prop.Name}");
                    }
                }
            }
            catch (Exception ex)
            {
                problems.Add($"{Path.GetFileName(snap)}: {ex.Message}");
            }
        }
        return new VerifyResult(snaps, ok, bad, problems);
    }

    public static string PlainReport(VerifyResult r) =>
        r.FilesBad == 0 && r.Problems.Count == 0
            ? $"All {r.FilesOk} files across {r.Snapshots} backups verified — everything is intact."
            : $"Verified {r.FilesOk} files; {r.FilesBad} problem(s) found. " +
              string.Join(" · ", r.Problems.Take(5));
}

/// <summary>Monthly backup reminder: Windows Task Scheduler, cron elsewhere.
/// On the first of each month the app opens itself with --backup-reminder and
/// shows a nudge - it never contacts z.ai until the user presses a button.
/// Legacy schedules registered --auto-backup; both flags are honored, and any
/// toggle rewrites the schedule to the new flag.</summary>
internal static class Scheduler
{
    private const string TaskName = "ZaiChatRecoveryMonthlyBackup";
    private const string Flag = "--backup-reminder";
    private static string ExePath => Environment.ProcessPath ?? "";

    public static bool IsScheduled()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return Run("schtasks", $"/Query /TN {TaskName}", out _) == 0;
            return Run("crontab", "-l", out var cron) == 0
                && (cron.Contains(Flag) || cron.Contains("--auto-backup"));
        }
        catch { return false; }
    }

    public static bool SetScheduled(bool enable)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var code = enable
                    ? Run("schtasks", $"/Create /F /TN {TaskName} /TR \"\\\"{ExePath}\\\" {Flag}\" /SC MONTHLY /ST 21:00", out _)
                    : Run("schtasks", $"/Delete /F /TN {TaskName}", out _);
                return code == 0;
            }
            // macOS / Linux: maintain our marked line in the user crontab
            // (strip legacy --auto-backup lines too, so toggling migrates them)
            var existing = Run("crontab", "-l", out var cron) == 0 ? cron : "";
            var kept = string.Join("\n", existing.Split('\n')
                .Where(l => !l.Contains(Flag) && !l.Contains("--auto-backup")));
            if (!enable)
            {
                var psi = new ProcessStartInfo("crontab") { RedirectStandardInput = true, UseShellExecute = false };
                using var p = Process.Start(psi)!;
                p.StandardInput.Write(kept);
                p.StandardInput.Close();
                p.WaitForExit(5000);
                return p.ExitCode == 0;
            }
            var line = $"0 21 1 * * \"{ExePath}\" {Flag} >> \"$HOME/.zai-backup-reminder.log\" 2>&1";
            var psi2 = new ProcessStartInfo("crontab") { RedirectStandardInput = true, UseShellExecute = false };
            using var p2 = Process.Start(psi2)!;
            p2.StandardInput.Write(kept + "\n" + line + "\n");
            p2.StandardInput.Close();
            p2.WaitForExit(5000);
            return p2.ExitCode == 0;
        }
        catch { return false; }
    }

    private static int Run(string exe, string args, out string output)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(10000);
        return p.ExitCode;
    }
}
