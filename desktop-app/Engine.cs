// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Z.ai Chat Recovery contributors
// Licensed under the Apache License, Version 2.0 (see LICENSE file in the repository root).
//
// Cross-platform recovery engine for chat.z.ai (Windows / macOS / Linux).
// Scans Chromium-family leveldb local storage AND Firefox webappsstore.sqlite
// for the session JWT, then calls three read-only API endpoints and renders
// Markdown transcripts + lossless JSON + direct chat URLs.

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZaiChatRecovery.Desktop;

internal sealed record TokenCandidate(string Token, string Email, string Where, long MtimeTicks, int Pos);

/// <summary>OS-aware discovery of browser local-storage locations.</summary>
internal static class BrowserMatrix
{
    /// <summary>All Chromium-family "Local Storage/leveldb" directories, any OS.
    /// Layout: Windows nests profiles under "User Data"; macOS/Linux put them
    /// directly under the browser root; snap Chromium lives under ~/snap.</summary>
    public static IEnumerable<(string leveldbDir, string label)> ChromiumLeveldbs()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new List<(string root, string name, bool userDataLayer)>();
        if (OperatingSystem.IsWindows())
        {
            string la = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
            if (la.Length > 0)
            {
                roots.Add((Path.Combine(la, "Google", "Chrome"), "Chrome", true));
                roots.Add((Path.Combine(la, "Google", "Chrome Beta"), "Chrome Beta", true));
                roots.Add((Path.Combine(la, "Microsoft", "Edge"), "Edge", true));
                roots.Add((Path.Combine(la, "BraveSoftware", "Brave-Browser"), "Brave", true));
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            string asp = Path.Combine(home, "Library", "Application Support");
            roots.Add((Path.Combine(asp, "Google", "Chrome"), "Chrome", false));
            roots.Add((Path.Combine(asp, "Microsoft Edge"), "Edge", false));
            roots.Add((Path.Combine(asp, "BraveSoftware", "Brave-Browser"), "Brave", false));
            roots.Add((Path.Combine(asp, "Chromium"), "Chromium", false));
        }
        else
        {
            string cfg = Path.Combine(home, ".config");
            roots.Add((Path.Combine(cfg, "google-chrome"), "Chrome", false));
            roots.Add((Path.Combine(cfg, "chromium"), "Chromium", false));
            roots.Add((Path.Combine(cfg, "microsoft-edge"), "Edge", false));
            roots.Add((Path.Combine(cfg, "BraveSoftware", "Brave-Browser"), "Brave", false));
            roots.Add((Path.Combine(home, "snap", "chromium", "common", "chromium"), "Chromium (snap)", false));
        }

        foreach (var (root, name, userDataLayer) in roots)
        {
            if (!Directory.Exists(root)) continue;
            var profileParent = userDataLayer ? Path.Combine(root, "User Data") : root;
            if (!Directory.Exists(profileParent)) continue;
            foreach (var prof in Directory.GetDirectories(profileParent))
            {
                var nm = Path.GetFileName(prof);
                if (userDataLayer && nm is "Crashpad" or "ShaderCache" or "GrShaderCache"
                        or "GraphiteDawnCache" or "System Profile" or "Guest Profile"
                        or "Temp" or "BrowserMetrics")
                    continue;
                var ldb = Path.Combine(prof, "Local Storage", "leveldb");
                if (Directory.Exists(ldb))
                    yield return (ldb, $"{name}/{nm}");
            }
        }
    }

    /// <summary>Firefox keeps site localStorage in webappsstore.sqlite (unencrypted).</summary>
    public static IEnumerable<(string sqlitePath, string label)> FirefoxStorage()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string profilesRoot = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetEnvironmentVariable("APPDATA") ?? "", "Mozilla", "Firefox", "Profiles")
            : OperatingSystem.IsMacOS()
                ? Path.Combine(home, "Library", "Application Support", "Firefox", "Profiles")
                : Path.Combine(home, ".mozilla", "firefox");
        if (!Directory.Exists(profilesRoot)) yield break;
        foreach (var prof in Directory.GetDirectories(profilesRoot))
        {
            var db = Path.Combine(prof, "webappsstore.sqlite");
            if (File.Exists(db))
                yield return (db, $"Firefox/{Path.GetFileName(prof)}");
        }
    }
}

internal static class Recovery
{
    private const string Api = "https://chat.z.ai/api/v1";
    private const string Ua = "zai-chat-recovery/1.1 (desktop, read-only)";
    private static readonly Regex JwtRe = new(
        @"eyJhbGciOi[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}",
        RegexOptions.Compiled);

    // The CDN in front of the API emits 307s that downgrade https to http, and
    // .NET strips the Authorization header on cross-origin redirects — so we
    // follow redirects manually, re-attaching auth and forcing https each hop.
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false });

    public static string? Token { get; private set; }
    public static string? AccountEmail { get; private set; }
    public static string LastAuthError { get; private set; } = "";

    // ---- token discovery ---------------------------------------------------

    private static string? JwtEmail(string tok)
    {
        try
        {
            var part = tok.Split('.')[1];
            var s = part.Replace('-', '+').Replace('_', '/');
            while (s.Length % 4 != 0) s += "=";
            using var doc = JsonDocument.Parse(Convert.FromBase64String(s));
            return doc.RootElement.TryGetProperty("email", out var e) && e.ValueKind == JsonValueKind.String
                ? e.GetString() : null;
        }
        catch { return null; }
    }

    private static byte[] ReadAllSharing(string path)
    {
        // Chrome keeps a live write handle on the current .log; FileShare.ReadWrite
        // lets us read it anyway (a no-op on macOS/Linux).
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    private static void ScanFile(byte[] data, string where, long mtime, List<TokenCandidate> found)
    {
        var hits = new HashSet<(int, string)>();
        foreach (var blob in new[] { Encoding.ASCII.GetString(data), Encoding.Unicode.GetString(data) })
            foreach (Match m in JwtRe.Matches(blob))
                hits.Add((m.Index, m.Value));
        foreach (var (pos, tok) in hits)
        {
            var email = JwtEmail(tok) ?? "unknown";
            if (email == "unknown") continue;
            found.Add(new TokenCandidate(tok, email, where, mtime, pos));
        }
    }

    public static List<TokenCandidate> FindTokenCandidates()
    {
        var found = new List<TokenCandidate>();
        foreach (var (leveldb, label) in BrowserMatrix.ChromiumLeveldbs())
        {
            foreach (var f in Directory.GetFiles(leveldb))
            {
                var name = Path.GetFileName(f);
                if (!name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith(".ldb", StringComparison.OrdinalIgnoreCase)) continue;
                try { ScanFile(ReadAllSharing(f), $"{label}/{name}", new FileInfo(f).LastWriteTimeUtc.Ticks, found); }
                catch (IOException) { }
            }
        }
        foreach (var (sqlite, label) in BrowserMatrix.FirefoxStorage())
        {
            try { ScanFile(ReadAllSharing(sqlite), label, new FileInfo(sqlite).LastWriteTimeUtc.Ticks, found); }
            catch (IOException) { }
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

    // ---- api ---------------------------------------------------------------

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
        for (int page = 1; page <= 50; page++)
        {
            var raw = await ApiSendAsync(HttpMethod.Get, $"/chats/?page={page}&size=100", null, Token!);
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                (root.TryGetProperty("items", out var items) || root.TryGetProperty("data", out items)))
                root = items;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) break;
            foreach (var c in root.EnumerateArray())
                chats.Add(JsonSerializer.Deserialize<JsonElement>(c.GetRawText()));
            if (root.GetArrayLength() < 100) break;
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
            var tz = TimeZoneInfo.FindSystemTimeZoneById(
                OperatingSystem.IsWindows() ? "Pacific Standard Time" : "America/Los_Angeles");
            return TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value), tz)
                .ToString("yyyy-MM-dd HH:mm");
        }
        catch { return DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value).UtcDateTime.ToString("yyyy-MM-dd HH:mm"); }
    }

    private static string PtTimeMs(long v) => v == 0 ? "" : PtTime(v > 1_000_000_000_000 ? v / 1000 : v);

    private static (string vis, string think) BlocksToText(JsonElement? blocks)
    {
        var vis = new List<string>();
        var think = new List<string>();
        if (blocks is { ValueKind: JsonValueKind.Array } arr)
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
            if (children.TryGetValue(node, out var kids) && kids.Count > 0) Walk(kids[^1]);
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

        var messages = new Dictionary<string, JsonElement>();
        if (detail.TryGetProperty("chat", out var chat) && chat.TryGetProperty("history", out var hist)
            && hist.TryGetProperty("messages", out var messagesEl) && messagesEl.ValueKind == JsonValueKind.Object)
            foreach (var p in messagesEl.EnumerateObject())
                messages[p.Name] = JsonSerializer.Deserialize<JsonElement>(p.Value.GetRawText());

        var content = await GetMessagesBatchAsync(id, messages.Keys);
        foreach (var (k, cm) in content)
        {
            if (!messages.TryGetValue(k, out var node)) continue;
            using var ms = new MemoryStream();
            using (var jw = new Utf8JsonWriter(ms))
            {
                jw.WriteStartObject();
                foreach (var prop in node.EnumerateObject()) prop.WriteTo(jw);
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
            if (vis.Length > 0) sb.AppendLine(vis).AppendLine();
        }
        await File.WriteAllTextAsync(Path.Combine(outDir, fname), sb.ToString());
        return (fname, n);
    }

    public sealed record RecoverySummary(int Done, int Failed, string OutDir, string MostRecentTitle);

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
        string mostRecent = "";
        foreach (var c in sorted)
        {
            i++;
            string id = c.GetProperty("id").GetString()!;
            string title = c.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "untitled" : "untitled";
            if (mostRecent.Length == 0) mostRecent = title;
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
        return new RecoverySummary(done, failed, outDir, mostRecent);
    }
}
