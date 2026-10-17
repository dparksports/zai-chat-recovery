// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Z.ai Chat Recovery contributors
// Licensed under the Apache License, Version 2.0 (see LICENSE file in the repository root).
//
// Z.ai Chat Recovery — Windows app.
// Recovers chat.z.ai conversations when the sidebar list renders empty:
// finds the session token in Chromium browser local storage, calls three
// read-only API endpoints, and writes Markdown transcripts, lossless JSON,
// an INDEX.md and a list of direct chat URLs.
//
// Run with --selftest for a headless verification (writes %TEMP%\zai-selftest.txt).

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZaiChatRecovery;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
            return SelfTest.Run();

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }
}

// ---------------------------------------------------------------------------
// recovery engine
// ---------------------------------------------------------------------------

internal sealed record TokenCandidate(string Token, string Email, string Where, long MtimeTicks, int Pos);

internal static class Recovery
{
    private const string Api = "https://chat.z.ai/api/v1";
    private const string Ua = "zai-chat-recovery/1.0 (windows app, read-only)";
    private static readonly Regex JwtRe = new(
        @"eyJhbGciOi[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}",
        RegexOptions.Compiled);

    // The CDN in front of the API emits 307s that downgrade https to http and
    // .NET strips the Authorization header on cross-origin redirects — so we
    // follow redirects manually, re-attaching auth and forcing https each hop.
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false });

    private static async Task<string> ApiSendAsync(HttpMethod method, string path, string? jsonBody, string token)
    {
        Uri url = new(Api + path);
        for (int hop = 0; hop < 6; hop++)
        {
            using var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new("Bearer", token);
            req.Headers.TryAddWithoutValidation("User-Agent", Ua);
            req.Headers.Accept.ParseAdd("application/json");
            if (jsonBody is not null)
                req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            using var resp = await Http.SendAsync(req);
            if ((int)resp.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var loc = resp.Headers.Location;
                if (loc is null)
                    throw new HttpRequestException($"redirect without Location ({(int)resp.StatusCode})");
                var next = loc.IsAbsoluteUri ? loc : new Uri(new Uri(Api), loc);
                if (next.Scheme == "http")
                    next = new UriBuilder(next) { Scheme = "https", Port = 443 }.Uri;
                url = next;
                continue;
            }
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} at {url}");
            return await resp.Content.ReadAsStringAsync();
        }
        throw new HttpRequestException("too many redirects");
    }

    public static string? Token { get; private set; }
    public static string? AccountEmail { get; private set; }

    private static string? JwtEmail(string tok)
    {
        try
        {
            var part = tok.Split('.')[1];
            var bytes = Base64UrlDecode(part);
            using var doc = JsonDocument.Parse(bytes);
            return doc.RootElement.TryGetProperty("email", out var e) && e.ValueKind == JsonValueKind.String
                ? e.GetString() : null;
        }
        catch { return null; }
    }

    private static byte[] Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        while (s.Length % 4 != 0) s += "=";
        return Convert.FromBase64String(s);
    }

    public static List<TokenCandidate> FindTokenCandidates()
    {
        var found = new List<TokenCandidate>();
        string la = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
        if (string.IsNullOrEmpty(la)) return found;

        var roots = new (string Path, string Name)[]
        {
            (Path.Combine(la, "Google", "Chrome"), "Chrome"),
            (Path.Combine(la, "Google", "Chrome Beta"), "Chrome Beta"),
            (Path.Combine(la, "Microsoft", "Edge"), "Edge"),
            (Path.Combine(la, "BraveSoftware", "Brave-Browser"), "Brave"),
        };

        foreach (var (root, name) in roots)
        {
            if (!Directory.Exists(root)) continue;
            var userData = Path.Combine(root, "User Data");
            if (!Directory.Exists(userData)) continue;
            foreach (var profileDir in Directory.GetDirectories(userData))
            {
                var leveldb = Path.Combine(profileDir, "Local Storage", "leveldb");
                if (!Directory.Exists(leveldb)) continue;
                string profile = Path.GetFileName(profileDir);
                foreach (var f in Directory.GetFiles(leveldb))
                {
                    var baseName = Path.GetFileName(f);
                    if (!baseName.EndsWith(".log", StringComparison.OrdinalIgnoreCase) &&
                        !baseName.EndsWith(".ldb", StringComparison.OrdinalIgnoreCase)) continue;
                    byte[] data;
                    try
                    {
                        // FileShare.ReadWrite is required: Chrome keeps a write handle
                        // on the live .log; File.ReadAllBytes (FileShare.Read) would fail.
                        using var fs = new FileStream(f, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete);
                        using var ms = new MemoryStream();
                        fs.CopyTo(ms);
                        data = ms.ToArray();
                    }
                    catch (IOException) { continue; }
                    var hits = new HashSet<(int, string)>();
                    foreach (var blob in new[] { Encoding.ASCII.GetString(data), Encoding.Unicode.GetString(data) })
                        foreach (Match m in JwtRe.Matches(blob))
                            hits.Add((m.Index, m.Value));
                    foreach (var (pos, tok) in hits)
                    {
                        var email = JwtEmail(tok) ?? "unknown";
                        if (email == "unknown") continue;
                        found.Add(new TokenCandidate(tok, email, $"{name}/{profile}/{baseName}",
                            new FileInfo(f).LastWriteTimeUtc.Ticks, pos));
                    }
                }
            }
        }

        // best first: real (non-guest) identities, newest file, highest offset
        return found
            .OrderBy(c => c.Email.StartsWith("guest-"))
            .ThenByDescending(c => c.MtimeTicks)
            .ThenByDescending(c => c.Pos)
            .GroupBy(c => c.Token)
            .Select(g => g.First())
            .ToList();
    }

    private static void SetAuth(string token) { Token = token; }

    public static string LastAuthError { get; private set; } = "";

    public static async Task<bool> TryUseTokenAsync(string token)
    {
        try
        {
            await ApiSendAsync(HttpMethod.Get, "/chats/?page=1&size=1", null, token);
            Token = token;
            AccountEmail = JwtEmail(token) ?? "(unknown)";
            LastAuthError = "";
            return true;
        }
        catch (Exception ex)
        {
            LastAuthError = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    public static async Task<List<JsonElement>> ListAllChatsAsync()
    {
        var chats = new List<JsonElement>();
        for (int page = 1; ; page++)
        {
            var raw = await ApiSendAsync(HttpMethod.Get, $"/chats/?page={page}&size=100", null, Token!);
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                (root.TryGetProperty("items", out var items) || root.TryGetProperty("data", out items)))
                root = items;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) break;
            foreach (var c in root.EnumerateArray()) chats.Add(JsonSerializer.Deserialize<JsonElement>(c.GetRawText()));
            if (root.GetArrayLength() < 100) break;
            if (page > 50) break;
        }
        return chats;
    }

    public static async Task<JsonElement> GetChatDetailAsync(string id)
    {
        var raw = await ApiSendAsync(HttpMethod.Get, $"/chats/{id}", null, Token!);
        using var doc = JsonDocument.Parse(raw);
        return JsonSerializer.Deserialize<JsonElement>(doc.RootElement.GetRawText());
    }

    public static async Task<Dictionary<string, JsonElement>> GetMessagesBatchAsync(string id, IEnumerable<string> msgIds)
    {
        var result = new Dictionary<string, JsonElement>();
        var ids = msgIds.ToList();
        for (int i = 0; i < ids.Count; i += 100)
        {
            var body = JsonSerializer.Serialize(new { ids = ids.GetRange(i, Math.Min(100, ids.Count - i)) });
            var raw = await ApiSendAsync(HttpMethod.Post, $"/chats/{id}/messages/batch", body, Token!);
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                foreach (var prop in data.EnumerateObject())
                    result[prop.Name] = JsonSerializer.Deserialize<JsonElement>(prop.Value.GetRawText());
            await Task.Delay(400);
        }
        return result;
    }

    // ---- rendering ---------------------------------------------------------

    private static string PtTime(long? unixSeconds)
    {
        if (unixSeconds is null or 0) return "";
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
            return TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value), tz)
                .ToString("yyyy-MM-dd HH:mm");
        }
        catch { return DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value).UtcDateTime.ToString("yyyy-MM-dd HH:mm"); }
    }

    private static string PtTimeMs(long msOrS)
    {
        if (msOrS == 0) return "";
        return PtTime(msOrS > 1_000_000_000_000 ? msOrS / 1000 : msOrS);
    }

    private static (string vis, string think) BlocksToText(JsonElement? blocks)
    {
        var vis = new List<string>();
        var think = new List<string>();
        if (blocks is { ValueKind: JsonValueKind.Array } arr)
        {
            foreach (var b in arr.EnumerateArray())
            {
                if (b.ValueKind != JsonValueKind.Object) continue;
                string type = b.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
                string? txt = null;
                if (b.TryGetProperty("text", out var x) && x.ValueKind == JsonValueKind.String) txt = x.GetString();
                else if (b.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String) txt = c.GetString();
                txt ??= type == "image_url" ? "[image]" : null;
                if (string.IsNullOrEmpty(txt)) continue;
                (type == "thinking" ? think : vis).Add(txt);
            }
        }
        return (string.Join("\n\n", vis), string.Join("\n\n", think));
    }

    private static List<string> Linearize(Dictionary<string, JsonElement> messages)
    {
        var children = new Dictionary<string, List<string>>(); // "" = no parent
        foreach (var (k, m) in messages)
        {
            string parent = m.TryGetProperty("parentId", out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString() ?? "" : "";
            if (!children.TryGetValue(parent, out var list)) children[parent] = list = new();
            list.Add(k);
        }
        var order = new List<string>();
        void Walk(string node)
        {
            order.Add(node);
            if (children.TryGetValue(node, out var kids) && kids.Count > 0)
                Walk(kids[^1]);
        }
        if (children.TryGetValue("", out var roots) && roots.Count > 0)
            foreach (var r in roots.Where(messages.ContainsKey)) Walk(r);
        else
            order.AddRange(messages.Keys);
        return order;
    }

    private static string SafeName(string title) =>
        string.Join("_", (title.Length > 50 ? title[..50] : title).Split(Path.GetInvalidFileNameChars())).Trim();

    public static async Task<(string file, int messages)> RenderChatAsync(JsonElement meta, JsonElement detail, string outDir)
    {
        string id = meta.GetProperty("id").GetString()!;
        string title = meta.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "untitled" : "untitled";

        JsonElement messagesEl = default;
        if (detail.TryGetProperty("chat", out var chat) && chat.TryGetProperty("history", out var hist)
            && hist.TryGetProperty("messages", out messagesEl) && messagesEl.ValueKind == JsonValueKind.Object)
        { }
        var messages = new Dictionary<string, JsonElement>();
        if (messagesEl.ValueKind == JsonValueKind.Object)
            foreach (var p in messagesEl.EnumerateObject())
                messages[p.Name] = JsonSerializer.Deserialize<JsonElement>(p.Value.GetRawText());

        var content = await GetMessagesBatchAsync(id, messages.Keys);
        foreach (var (k, cm) in content)
            if (messages.TryGetValue(k, out var node))
            {
                var merged = JsonSerializer.Deserialize<JsonElement>(node.GetRawText());
                // attach content detail as a plain property bag
                using var ms = new MemoryStream();
                using (var jw = new Utf8JsonWriter(ms))
                {
                    jw.WriteStartObject();
                    foreach (var prop in merged.EnumerateObject())
                        prop.WriteTo(jw);
                    jw.WritePropertyName("content_detail_v");
                    cm.WriteTo(jw);
                    jw.WriteEndObject();
                }
                messages[k] = JsonSerializer.Deserialize<JsonElement>(ms.ToArray());
            }

        var order = Linearize(messages);
        string fname = $"{SafeName(title)}__{id[..8]}.md";
        int n = 0;
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}").AppendLine();
        sb.AppendLine($"- id: {id}");
        sb.AppendLine($"- direct url: https://chat.z.ai/c/{id}");
        sb.AppendLine($"- created: {PtTimeMs(meta.TryGetProperty("created_at", out var ca) ? ca.GetInt64() : 0)}");
        sb.AppendLine($"- updated: {PtTimeMs(meta.TryGetProperty("updated_at", out var ua2) ? ua2.GetInt64() : 0)}");
        sb.AppendLine().AppendLine("---").AppendLine();
        foreach (var k in order)
        {
            if (!messages.TryGetValue(k, out var m)) continue;
            string role = m.TryGetProperty("role", out var r2) && r2.ValueKind == JsonValueKind.String ? r2.GetString() ?? "?" : "?";
            JsonElement? blocks = m.TryGetProperty("content_detail_v", out var cd) ? cd : null;
            var (vis, think) = BlocksToText(blocks);
            if (vis.Length == 0 && m.TryGetProperty("content", out var c2) && c2.ValueKind == JsonValueKind.String)
                vis = c2.GetString() ?? "";
            if (vis.Length == 0 && think.Length == 0) continue;
            n++;
            string stamp = m.TryGetProperty("timestamp", out var ts) ? PtTimeMs(ts.GetInt64()) : "";
            sb.AppendLine($"## {role.ToUpper()}{(stamp.Length > 0 ? "  (" + stamp + ")" : "")}").AppendLine();
            if (think.Length > 0)
                sb.AppendLine($"<details><summary>thinking</summary>\n\n{think[..Math.Min(4000, think.Length)]}\n\n</details>").AppendLine();
            if (vis.Length > 0)
                sb.AppendLine(vis).AppendLine();
        }
        string path = Path.Combine(outDir, fname);
        await File.WriteAllTextAsync(path, sb.ToString());
        return (fname, n);
    }

    public sealed record RecoverySummary(int Done, int Failed, string OutDir, List<string> IndexLines, List<string> UrlLines);

    public static async Task<RecoverySummary> RecoverAllAsync(string outDir, int limit,
        Action<string> log, Func<int, int, string, Task> progress)
    {
        Directory.CreateDirectory(outDir);
        var chats = await ListAllChatsAsync();
        var sorted = chats.OrderByDescending(c => c.TryGetProperty("updated_at", out var u) ? u.GetInt64() : 0).ToList();
        if (limit > 0) sorted = sorted.Take(limit).ToList();
        log($"Found {sorted.Count} conversations.");
        var indexLines = new List<string>();
        var urlLines = new List<string>();
        int done = 0, failed = 0, i = 0;
        foreach (var c in sorted)
        {
            i++;
            string id = c.GetProperty("id").GetString()!;
            string title = c.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "untitled" : "untitled";
            try
            {
                var detail = await GetChatDetailAsync(id);
                await File.WriteAllTextAsync(Path.Combine(outDir, $"{id}.json"),
                    JsonSerializer.Serialize(detail, new JsonSerializerOptions { WriteIndented = true }));
                var (fname, n) = await RenderChatAsync(c, detail, outDir);
                done++;
                string upd = PtTimeMs(c.TryGetProperty("updated_at", out var u) ? u.GetInt64() : 0);
                indexLines.Add($"- [{title}]({fname}) - {n} messages, updated {upd}");
                urlLines.Add($"| {i} | {title} | {upd} | https://chat.z.ai/c/{id} |");
                await progress(i, sorted.Count, title);
            }
            catch (Exception ex)
            {
                failed++;
                log($"FAILED {id[..8]}: {ex.Message}");
            }
            await Task.Delay(400);
        }
        await File.WriteAllTextAsync(Path.Combine(outDir, "INDEX.md"),
            $"# Recovered z.ai conversations ({done} chats)\n\nAccount: {AccountEmail}\n\n" + string.Join("\n", indexLines) + "\n");
        await File.WriteAllTextAsync(Path.Combine(outDir, "chat-urls.md"),
            "# Direct chat URLs\n\n| # | Title | Updated | URL |\n|---|---|---|---|\n" + string.Join("\n", urlLines) + "\n");
        return new RecoverySummary(done, failed, outDir, indexLines, urlLines);
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
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "zai-selftest.txt"), sb.ToString());
                return 2;
            }
            var chats = Recovery.ListAllChatsAsync().GetAwaiter().GetResult();
            sb.AppendLine($"result: OK, account={Recovery.AccountEmail}, chats={chats.Count}");
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "zai-selftest.txt"), sb.ToString());
            return 0;
        }
        catch (Exception ex)
        {
            sb.AppendLine($"result: EXCEPTION {ex.Message}");
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "zai-selftest.txt"), sb.ToString());
            return 3;
        }
    }
}

// ---------------------------------------------------------------------------
// UI
// ---------------------------------------------------------------------------

internal sealed class MainForm : Form
{
    private readonly Label _status = new() { ForeColor = Color.FromArgb(226, 232, 240), AutoSize = true, Text = "No session found yet." };
    private readonly Button _scanBtn = new() { Text = "1. Scan for session", Width = 170, Height = 34 };
    private readonly Button _recoverBtn = new() { Text = "2. Recover all chats", Width = 170, Height = 34, Enabled = false };
    private readonly Button _folderBtn = new() { Text = "Choose folder…", Width = 170, Height = 34 };
    private readonly Button _openBtn = new() { Text = "Open output folder", Width = 170, Height = 34, Enabled = false };
    private readonly TextBox _outDir = new() { Width = 560 };
    private readonly ProgressBar _bar = new() { Width = 560, Height = 18 };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9F) };
    private string _lastOutDir = "";

    private static readonly (bool Good, string Tip)[] Tips =
    {
        (true,  "Save direct chat URLs (chat.z.ai/c/<id>) first — they open even while the sidebar is empty"),
        (true,  "Back up transcripts + JSON with this app before touching browser settings"),
        (true,  "Log in with the same method every time (Google vs email can be different accounts)"),
        (true,  "Check Settings → Account for which email holds the chats while you are logged in"),
        (true,  "Hard-reload (Ctrl+Shift+R) or re-login later — frontend bugs get fixed server-side"),
        (false, "Don't log out or clear cookies / site data for chat.z.ai before backing up"),
        (false, "Don't click “clear all conversations” in a panic — an empty sidebar is usually a UI bug"),
        (false, "Don't keep chatting as guest — guest chats live only in local storage"),
        (false, "Don't run cleanup tools (CCleaner-style) that wipe site data before the backup"),
        (false, "Don't uninstall Chrome or delete its profile — the token and drafts live inside it"),
    };

    public MainForm()
    {
        Text = "Z.ai Chat Recovery — read-only conversation recovery";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        BackColor = Color.FromArgb(15, 23, 42);
        Size = new Size(1000, 760);

        _outDir.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "zai-chat-recovery-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        _log.BackColor = Color.FromArgb(11, 18, 32);
        _log.ForeColor = Color.FromArgb(148, 163, 184);

        foreach (var b in new[] { _scanBtn, _recoverBtn, _folderBtn, _openBtn })
        {
            b.FlatStyle = FlatStyle.Flat;
            b.BackColor = Color.FromArgb(30, 41, 59);
            b.ForeColor = Color.FromArgb(226, 232, 240);
        }
        StylePrimary(_recoverBtn);

        var title = new Label
        {
            Text = "Z.ai Chat Recovery",
            Font = new Font("Segoe UI Semibold", 16F),
            ForeColor = Color.FromArgb(34, 211, 238),
            AutoSize = true,
        };
        var subtitle = new Label
        {
            Text = "Empty sidebar ≠ lost chats. Read-only: nothing on your account is modified; requests go to chat.z.ai only.",
            ForeColor = Color.FromArgb(148, 163, 184), AutoSize = true
        };
        Controls.Add(title); Controls.Add(subtitle);
        title.Location = new Point(24, 16);
        subtitle.Location = new Point(24, 50);

        Controls.Add(_status); _status.Location = new Point(24, 92);
        Controls.Add(_scanBtn); _scanBtn.Location = new Point(24, 124);
        Controls.Add(_recoverBtn); _recoverBtn.Location = new Point(210, 124);
        Controls.Add(_folderBtn); _folderBtn.Location = new Point(396, 124);
        Controls.Add(_openBtn); _openBtn.Location = new Point(582, 124);
        Controls.Add(_outDir); _outDir.Location = new Point(24, 172);
        Controls.Add(_bar); _bar.Location = new Point(24, 200);
        Controls.Add(_log); _log.Location = new Point(24, 228); _log.Size = new Size(740, 470);

        var tipsPanel = MakeTipsPanel();
        tipsPanel.Location = new Point(788, 92);
        Controls.Add(tipsPanel);

        var footer = new Label
        {
            Text = "Apache-2.0 · v1.0.0 · your session token never leaves this machine except in requests to chat.z.ai",
            ForeColor = Color.FromArgb(71, 85, 105), AutoSize = true
        };
        Controls.Add(footer); footer.Location = new Point(24, 706);

        _scanBtn.Click += async (_, _) => await ScanAsync();
        _recoverBtn.Click += async (_, _) => await RecoverAsync();
        _folderBtn.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog();
            if (d.ShowDialog(this) == DialogResult.OK) _outDir.Text = d.SelectedPath;
        };
        _openBtn.Click += (_, _) =>
        {
            if (_lastOutDir.Length > 0 && Directory.Exists(_lastOutDir))
                System.Diagnostics.Process.Start("explorer.exe", _lastOutDir);
        };
    }

    private static void StylePrimary(Button b)
    {
        b.BackColor = Color.FromArgb(14, 116, 144);
        b.ForeColor = Color.White;
    }

    private Control MakeTipsPanel()
    {
        var panel = new Panel { BackColor = Color.FromArgb(17, 27, 46), Size = new Size(176, 606), BorderStyle = BorderStyle.FixedSingle };
        var head = new Label
        {
            Text = "Protect your session IDs",
            Font = new Font("Segoe UI Semibold", 10.5F),
            ForeColor = Color.FromArgb(226, 232, 240),
            AutoSize = false, Size = new Size(160, 44), Location = new Point(8, 8)
        };
        panel.Controls.Add(head);
        int y = 56;
        foreach (var (good, tip) in Tips)
        {
            var mark = new Label
            {
                Text = good ? "✓" : "✗",
                ForeColor = good ? Color.FromArgb(34, 197, 94) : Color.FromArgb(239, 68, 68),
                Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                AutoSize = false, Size = new Size(18, 18), Location = new Point(8, y)
            };
            var txt = new Label
            {
                Text = tip,
                ForeColor = good ? Color.FromArgb(203, 213, 225) : Color.FromArgb(226, 232, 240),
                AutoSize = false, Size = new Size(146, 92), Location = new Point(26, y),
                Font = new Font("Segoe UI", 8.5F)
            };
            panel.Controls.Add(mark); panel.Controls.Add(txt);
            y += 104;
        }
        return panel;
    }

    private void Log(string s)
    {
        _log.AppendText(s + Environment.NewLine);
    }

    private async Task ScanAsync()
    {
        _scanBtn.Enabled = false;
        _status.Text = "Scanning browser local storage…";
        Log("Scanning Chromium browser local storage for a chat.z.ai session token…");
        var cands = await Task.Run(Recovery.FindTokenCandidates);
        Log($"  candidates: {cands.Count}");
        foreach (var c in cands.Take(5))
            Log($"  {c.Email}  ({c.Where})");
        string? token = null;
        foreach (var c in cands.Take(8))
        {
            if (c.Email.StartsWith("guest-", StringComparison.Ordinal))
            {
                Log("  skipping guest session — log in to chat.z.ai in the browser, then rescan");
                continue;
            }
            if (await Task.Run(() => Recovery.TryUseTokenAsync(c.Token).GetAwaiter().GetResult()))
            {
                token = c.Token;
                break;
            }
        }
        if (token is null)
        {
            _status.Text = "No valid session found — log in to chat.z.ai in your browser, then click Scan again.";
            Log("No valid token. Open chat.z.ai in Chrome/Edge/Brave, LOG IN (not as guest), close the tab, rescan.");
        }
        else
        {
            _status.Text = $"Session OK — account: {Recovery.AccountEmail}";
            Log($"Token verified. Account: {Recovery.AccountEmail}");
            _recoverBtn.Enabled = true;
        }
        _scanBtn.Enabled = true;
    }

    private async Task RecoverAsync()
    {
        _recoverBtn.Enabled = false; _scanBtn.Enabled = false;
        _bar.Value = 0; _bar.Maximum = 100;
        string outDir = _outDir.Text.Trim();
        try
        {
            var summary = await Recovery.RecoverAllAsync(outDir, 0,
                s => Invoke(() => Log(s)),
                (i, total, title) => { Invoke(() => { _bar.Maximum = total; _bar.Value = i; Log($"  [{i}/{total}] {title}"); }); return Task.CompletedTask; });
            _lastOutDir = summary.OutDir;
            _openBtn.Enabled = true;
            Invoke(() => Log($"Done. {summary.Done} chats recovered, {summary.Failed} failed. Folder: {summary.OutDir}"));
            _status.Text = $"Recovered {summary.Done} chats → {summary.OutDir}";
        }
        catch (Exception ex)
        {
            Invoke(() => Log($"ERROR: {ex.Message}"));
            _status.Text = "Recovery failed — see log.";
        }
        _recoverBtn.Enabled = true; _scanBtn.Enabled = true;
    }
}
