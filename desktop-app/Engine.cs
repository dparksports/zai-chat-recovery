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
using System.Text.Json.Nodes;
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

internal sealed record AttachmentExtract(string Kind, string? Url, string? Name);

internal sealed record AttachmentView(string Kind, string? Path, string Name, long Size, bool Missing);

internal sealed record BlockRender(string Vis, string Think, string Tools, List<AttachmentExtract> Attachments);

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

    internal static string PtTimeMs(long v) => v == 0 ? "" : PtTime(v > 1_000_000_000_000 ? v / 1000 : v);

    internal sealed record Row(string Role, string Stamp, string Text, string Think, string Tools,
        List<AttachmentExtract> Attachments)
    {
        public List<AttachmentView> Saved { get; } = new();
    }

    internal static BlockRender RenderBlocks(JsonElement? blocks)
    {
        var vis = new List<string>();
        var think = new List<string>();
        var tools = new List<string>();
        var atts = new List<AttachmentExtract>();
        if (blocks is { ValueKind: JsonValueKind.Array } arr)
            foreach (var b in arr.EnumerateArray())
            {
                if (b.ValueKind != JsonValueKind.Object) continue;
                string type = Str(b, "type") ?? "";
                string? body = Str(b, "text") ?? Str(b, "content");
                switch (type)
                {
                    case "text":
                        if (!string.IsNullOrEmpty(body)) vis.Add(body!);
                        break;
                    case "reasoning":
                    case "thinking":
                        if (!string.IsNullOrEmpty(body)) think.Add(body!);
                        break;
                    case "tool_calls":
                        if (b.TryGetProperty("content", out var tc) && tc.ValueKind == JsonValueKind.Array)
                            foreach (var call in tc.EnumerateArray())
                            {
                                var fn = call.TryGetProperty("function", out var f) ? Str(f, "name") : null;
                                if (fn is { Length: > 0 }) tools.Add(fn);
                            }
                        break;
                    case "image_url":
                    case "image":
                        atts.Add(new("image", UrlOf(b, "image_url") ?? UrlOf(b, "url"), Str(b, "name")));
                        break;
                    case "file":
                    case "document":
                        atts.Add(new("file", UrlOf(b, "file") ?? UrlOf(b, "url"), Str(b, "name") ?? Str(b, "filename")));
                        break;
                    case "audio":
                        atts.Add(new("audio", UrlOf(b, "audio") ?? UrlOf(b, "url"), Str(b, "name")));
                        break;
                    default:
                        if (!string.IsNullOrEmpty(body)) vis.Add(body!);
                        break;
                }
            }
        return new BlockRender(string.Join("\n\n", vis), string.Join("\n\n", think),
            string.Join("; ", tools.Distinct()), atts);

        static string? UrlOf(JsonElement b, string key)
        {
            if (!b.TryGetProperty(key, out var v)) return null;
            if (v.ValueKind == JsonValueKind.String) return v.GetString();
            if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
                return u.GetString();
            return null;
        }
    }

    internal static string? Str(JsonElement o, string k) =>
        o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string ExtensionOf(string source, string kind)
    {
        var ext = Path.GetExtension(new Uri(source, UriKind.RelativeOrAbsolute).GetComponents(UriComponents.Path, UriFormat.Unescaped));
        if (string.IsNullOrEmpty(ext))
            ext = kind switch { "image" => ".png", "audio" => ".mp3", _ => ".bin" };
        return ext.Length > 1 && ext.Length <= 6 ? ext : ".bin";
    }

    internal static List<string> Linearize(Dictionary<string, JsonElement> messages)
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

    public static async Task<(string file, int messages, int attachments)> RenderChatAsync(JsonElement meta, JsonElement detail, string outDir)
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
        var rows = new List<Row>();
        foreach (var k in order)
        {
            if (!messages.TryGetValue(k, out var m)) continue;
            string role = m.TryGetProperty("role", out var r2) && r2.ValueKind == JsonValueKind.String ? r2.GetString() ?? "?" : "?";
            JsonElement? blocks = m.TryGetProperty("content_detail_v", out var cd) ? cd : null;
            var br = RenderBlocks(blocks);
            var vis = br.Vis;
            var think = br.Think;
            if (vis.Length == 0 && m.TryGetProperty("content", out var c2) && c2.ValueKind == JsonValueKind.String)
                vis = c2.GetString() ?? "";
            if (vis.Length == 0 && think.Length == 0 && br.Tools.Length == 0 && br.Attachments.Count == 0) continue;
            n++;
            string stamp = m.TryGetProperty("timestamp", out var ts) ? PtTimeMs(ts.GetInt64()) : "";
            rows.Add(new Row(role.ToUpperInvariant(), stamp, vis, think, br.Tools, br.Attachments));
        }

        // download attachments now, while the session sign-in is alive
        int attCount = 0;
        var attDir = Path.Combine("attachments", id[..8]);
        int seq = 0;
        foreach (var row in rows)
        {
            foreach (var a in row.Attachments)
            {
                seq++;
                var ext = ExtensionOf(a.Name ?? a.Url ?? "", a.Kind);
                var fileName = SafeName(a.Name ?? $"attachment{ext}");
                if (!Path.HasExtension(fileName)) fileName += ext;
                if (string.IsNullOrWhiteSpace(fileName)) fileName = $"attachment{ext}";
                if (a.Url is null)
                {
                    row.Saved.Add(new AttachmentView(a.Kind, null, fileName, 0, true));
                    continue;
                }
                try
                {
                    var rel = $"{attDir.Replace('\\', '/')}/{seq:000}_{fileName}";
                    var bytes = await ApiDownloadAsync(a.Url, Token!);
                    if (bytes.Length == 0 || bytes.Length > 200L * 1024 * 1024)
                        throw new HttpRequestException("empty or oversized download");
                    Directory.CreateDirectory(Path.Combine(outDir, attDir));
                    await File.WriteAllBytesAsync(Path.Combine(outDir, rel.Replace('/', Path.DirectorySeparatorChar)), bytes);
                    row.Saved.Add(new AttachmentView(a.Kind, rel, fileName, bytes.Length, false));
                    attCount++;
                }
                catch (Exception)
                {
                    row.Saved.Add(new AttachmentView(a.Kind, null, fileName, 0, true));
                }
                await Task.Delay(200);
            }
        }

        // markdown
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}").AppendLine();
        sb.AppendLine($"- id: {id}");
        sb.AppendLine($"- direct url: https://chat.z.ai/c/{id}");
        sb.AppendLine($"- created: {PtTimeMs(meta.TryGetProperty("created_at", out var ca) ? ca.GetInt64() : 0)}");
        sb.AppendLine($"- updated: {PtTimeMs(meta.TryGetProperty("updated_at", out var ua2) ? ua2.GetInt64() : 0)}");
        sb.AppendLine().AppendLine("---").AppendLine();
        foreach (var row in rows)
        {
            sb.AppendLine($"## {row.Role}{(row.Stamp.Length > 0 ? "  (" + row.Stamp + ")" : "")}").AppendLine();
            if (row.Think.Length > 0)
                sb.AppendLine($"<details><summary>thinking</summary>\n\n{row.Think[..Math.Min(4000, row.Think.Length)]}\n\n</details>").AppendLine();
            if (row.Tools.Length > 0)
                sb.AppendLine($"> tools used: {row.Tools}").AppendLine();
            if (row.Text.Length > 0) sb.AppendLine(row.Text).AppendLine();
            foreach (var a in row.Saved)
            {
                if (a.Missing)
                    sb.AppendLine($"_(attachment could not be saved: {a.Name})_").AppendLine();
                else if (a.Kind == "image")
                    sb.AppendLine($"![{a.Name}]({a.Path?.Replace(" ", "%20")})").AppendLine();
                else
                    sb.AppendLine($"[{a.Name}]({a.Path?.Replace(" ", "%20")})").AppendLine();
            }
        }
        await File.WriteAllTextAsync(Path.Combine(outDir, fname), sb.ToString());

        // enriched <id>.json: full ordered transcript, structured for the reader
        var arr = new JsonArray();
        foreach (var row in rows)
        {
            var o = new JsonObject
            {
                ["role"] = row.Role,
                ["stamp"] = row.Stamp,
                ["text"] = row.Text,
                ["thinking"] = row.Think,
                ["tools"] = row.Tools,
            };
            if (row.Saved.Count > 0)
            {
                var aa = new JsonArray();
                foreach (var a in row.Saved)
                    aa.Add(new JsonObject
                    {
                        ["kind"] = a.Kind, ["path"] = a.Path, ["name"] = a.Name,
                        ["size"] = a.Size, ["missing"] = a.Missing,
                    });
                o["attachments"] = aa;
            }
            arr.Add(o);
        }
        await File.WriteAllTextAsync(Path.Combine(outDir, $"{id}.json"),
            new JsonObject
            {
                ["schema"] = "zai-recovery/2",
                ["id"] = id,
                ["title"] = title,
                ["updated_at"] = meta.TryGetProperty("updated_at", out var ua3) && ua3.ValueKind == JsonValueKind.Number ? ua3.GetInt64() : 0,
                ["messages"] = arr,
            }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        return (fname, n, attCount);
    }

    /// <summary>Re-fetch one chat from the API and rewrite its backup files in place
    /// (transcript + attachments). Used by the reader's "Fetch attachments".
    /// Refuses to overwrite an existing backup when the API returns no message
    /// content (rate-limited or degraded responses must never destroy data).</summary>
    public static async Task<(int messages, int attachments, string title)> RefetchChatAsync(string chatId, string title, string outDir)
    {
        var meta = new JsonObject { ["id"] = chatId, ["title"] = title };
        using var doc = JsonDocument.Parse(meta.ToJsonString());
        var detail = await GetChatDetailAsync(chatId);
        var treeIds = new List<string>();
        if (detail.TryGetProperty("chat", out var ch) && ch.TryGetProperty("history", out var h)
            && h.TryGetProperty("messages", out var ms) && ms.ValueKind == JsonValueKind.Object)
            treeIds = ms.EnumerateObject().Select(p => p.Name).ToList();
        if (treeIds.Count > 0)
        {
            var probe = await GetMessagesBatchAsync(chatId, treeIds);
            if (probe.Count == 0)
                throw new HttpRequestException("the API returned no message content (possibly rate-limited) - existing backup left untouched");
        }
        var (_, n, att) = await RenderChatAsync(doc.RootElement.Clone(), detail, outDir);
        return (n, att, title);
    }

    public static async Task<byte[]> ApiDownloadAsync(string url, string token)
    {
        Uri u = new(url, UriKind.Absolute);
        for (int hop = 0; hop < 6; hop++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, u);
            req.Headers.Authorization = new("Bearer", token);
            req.Headers.TryAddWithoutValidation("User-Agent", Ua);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            if ((int)resp.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var loc = resp.Headers.Location;
                if (loc is null) break;
                var next = loc.IsAbsoluteUri ? loc : new Uri(u, loc);
                if (next.Scheme == "http") next = new UriBuilder(next) { Scheme = "https", Port = 443 }.Uri;
                u = next;
                continue;
            }
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsByteArrayAsync();
        }
        throw new HttpRequestException("download failed (redirect loop or no location)");
    }

    public sealed record RecoverySummary(int Done, int Failed, string OutDir, string MostRecentTitle, int Attachments);

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
        int done = 0, failed = 0, i = 0, totalAtt = 0;
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
                var (fname, n, nAtt) = await RenderChatAsync(c, detail, outDir);
                done++;
                totalAtt += nAtt;
                string upd = PtTimeMs(c.TryGetProperty("updated_at", out var u) ? u.GetInt64() : 0);
                indexLines.Add($"- [{title}]({fname}) - {n} messages, updated {upd}");
                urlLines.Add($"| {i} | {title} | {upd} | https://chat.z.ai/c/{id} |");
                await progress(i, sorted.Count, title + (nAtt > 0 ? $" ({nAtt} attachments)" : ""));
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
        return new RecoverySummary(done, failed, outDir, mostRecent, totalAtt);
    }
}

// ---------------------------------------------------------------------------
// backup reader - loads ANY existing backup folder (app or python CLI format)
// ---------------------------------------------------------------------------

internal sealed record ChatSummary(string Id, string Title, string UpdatedText, int Messages, string JsonPath);

internal sealed record MessageView(string Role, string Stamp, string Text, string Thinking, string Tools,
    List<AttachmentView> Attachments);

internal static class BackupReader
{
    /// <summary>Scan a backup folder for chat JSONs (skips index files).</summary>
    public static List<ChatSummary> ScanBackupFolder(string dir)
    {
        var list = new List<ChatSummary>();
        foreach (var f in Directory.GetFiles(dir, "*.json"))
        {
            var name = Path.GetFileName(f);
            if (name.StartsWith("_") || name.StartsWith("INDEX", StringComparison.OrdinalIgnoreCase)
                                     || name.StartsWith("chat-urls", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                var r = doc.RootElement;
                if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("id", out _))
                    continue;
                string id = r.GetProperty("id").GetString() ?? Path.GetFileNameWithoutExtension(f);
                string title = r.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString() ?? "(untitled)" : "(untitled)";
                long upd = r.TryGetProperty("updated_at", out var u) && u.ValueKind == JsonValueKind.Number
                    ? u.GetInt64() : 0;
                int count = 0;
                if (r.TryGetProperty("messages", out var ms))
                {
                    if (ms.ValueKind == JsonValueKind.Array) count = ms.GetArrayLength();
                    else if (ms.ValueKind == JsonValueKind.Object) count = ms.EnumerateObject().Count();
                }
                else if (r.TryGetProperty("chat", out var ch) && ch.TryGetProperty("history", out var h)
                         && h.TryGetProperty("messages", out var m2) && m2.ValueKind == JsonValueKind.Object)
                    count = m2.EnumerateObject().Count();
                list.Add(new ChatSummary(id, title, Recovery.PtTimeMs(upd), count, f));
            }
            catch (Exception) { /* not one of ours */ }
        }
        return list.OrderByDescending(s => s.UpdatedText).ToList();
    }

    /// <summary>Load one chat's transcript: new schema, python schema, or md fallback.</summary>
    public static List<MessageView> LoadChat(ChatSummary s)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(s.JsonPath));
            var r = doc.RootElement;
            if (r.TryGetProperty("messages", out var ms) && ms.ValueKind == JsonValueKind.Array)
            {
                var views = new List<MessageView>();
                foreach (var m in ms.EnumerateArray())
                {
                    string role = m.TryGetProperty("role", out var ro) ? ro.GetString() ?? "" : "";
                    string stamp = m.TryGetProperty("stamp", out var st) ? st.GetString() ?? "" : "";
                    string text = m.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
                    string think = m.TryGetProperty("thinking", out var th) ? th.GetString() ?? "" : "";
                    string tools = m.TryGetProperty("tools", out var tl) ? tl.GetString() ?? "" : "";
                    var atts = new List<AttachmentView>();
                    if (m.TryGetProperty("attachments", out var aa) && aa.ValueKind == JsonValueKind.Array)
                        foreach (var a in aa.EnumerateArray())
                            atts.Add(new AttachmentView(
                                Recovery.Str(a, "kind") ?? "file",
                                Recovery.Str(a, "path"),
                                Recovery.Str(a, "name") ?? "attachment",
                                a.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : 0,
                                a.TryGetProperty("missing", out var mi) && mi.ValueKind == JsonValueKind.True));
                    if (text.Length == 0 && think.Length == 0 && atts.Count == 0) continue;
                    views.Add(new MessageView(role, stamp, text, think, tools, atts));
                }
                if (views.Count > 0) return views;
            }
            if (r.TryGetProperty("chat", out var chat) && chat.TryGetProperty("history", out var hist)
                && hist.TryGetProperty("messages", out var tree) && tree.ValueKind == JsonValueKind.Object)
            {
                var messages = new Dictionary<string, JsonElement>();
                foreach (var p in tree.EnumerateObject())
                    messages[p.Name] = JsonSerializer.Deserialize<JsonElement>(p.Value.GetRawText());
                var views = new List<MessageView>();
                foreach (var k in Recovery.Linearize(messages))
                {
                    if (!messages.TryGetValue(k, out var m)) continue;
                    string role = m.TryGetProperty("role", out var ro) ? (ro.GetString() ?? "?").ToUpperInvariant() : "?";
                    string stamp = m.TryGetProperty("timestamp", out var ts) ? Recovery.PtTimeMs(ts.GetInt64()) : "";
                    JsonElement? blocks = null;
                    if (m.TryGetProperty("content_detail", out var cd) && cd.ValueKind == JsonValueKind.Object
                        && cd.TryGetProperty("content_blocks", out var cb)) blocks = cb;
                    else if (m.TryGetProperty("content_blocks", out var cb2)) blocks = cb2;
                    var br = Recovery.RenderBlocks(blocks);
                    var vis = br.Vis;
                    if (vis.Length == 0 && m.TryGetProperty("content", out var c2) && c2.ValueKind == JsonValueKind.String)
                        vis = c2.GetString() ?? "";
                    var atts = br.Attachments
                        .Select(a => new AttachmentView(a.Kind, null, a.Name ?? "attachment", 0, true))
                        .ToList();
                    if (vis.Length == 0 && br.Think.Length == 0 && atts.Count == 0) continue;
                    views.Add(new MessageView(role, stamp, vis, br.Think, br.Tools, atts));
                }
                if (views.Count > 0) return views;
            }
        }
        catch (Exception) { }
        // fall back to the markdown twin (our own format, trivially parseable)
        var md = FindMdFor(Path.GetDirectoryName(s.JsonPath) ?? ".", s.Id);
        return md is null ? new List<MessageView>() : ParseMdFile(md);
    }

    private static string? FindMdFor(string dir, string id)
    {
        var byId = Directory.GetFiles(dir, "*" + id[..Math.Min(8, id.Length)] + "*.md");
        return byId.FirstOrDefault();
    }

    /// <summary>Parse our own markdown transcript format back into message views.</summary>
    public static List<MessageView> ParseMdFile(string path)
    {
        var views = new List<MessageView>();
        var dir = Path.GetDirectoryName(path) ?? ".";
        string? role = null, stamp = null, tools = "";
        var body = new StringBuilder();
        var think = new StringBuilder();
        var atts = new List<AttachmentView>();
        bool inThink = false;
        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.TrimEnd();
            if (line.StartsWith("## "))
            {
                Flush();
                var header = line[3..].Trim();
                int paren = header.IndexOf("  (", StringComparison.Ordinal);
                if (paren > 0)
                {
                    stamp = header[(paren + 3)..].TrimEnd(')');
                    role = header[..paren].Trim();
                }
                else { role = header; stamp = ""; }
                body.Clear(); think.Clear(); atts.Clear(); tools = "";
                continue;
            }
            if (line.StartsWith("> tools used: ", StringComparison.OrdinalIgnoreCase))
            {
                tools = line["> tools used: ".Length..].Trim();
                continue;
            }
            var link = TryAttachmentLink(line);
            if (link is { } a)
            {
                bool exists = File.Exists(Path.Combine(dir, a.Path?.Replace('/', Path.DirectorySeparatorChar) ?? ""));
                atts.Add(new AttachmentView(a.Kind, a.Path, a.Name, 0, !exists));
                continue;
            }
            if (line.StartsWith("<details>", StringComparison.OrdinalIgnoreCase)) { inThink = true; continue; }
            if (line.StartsWith("</details>", StringComparison.OrdinalIgnoreCase)) { inThink = false; continue; }
            if (inThink && line.StartsWith("<summary>", StringComparison.OrdinalIgnoreCase)) continue;
            if (inThink) think.AppendLine(line);
            else if (role is not null) body.AppendLine(line);
        }
        Flush();
        return views;

        void Flush()
        {
            if (role is not null && (body.Length > 0 || think.Length > 0 || atts.Count > 0))
                views.Add(new MessageView(role, stamp ?? "", body.ToString().Trim(), think.ToString().Trim(), tools, atts.ToList()));
        }

        static (string Kind, string Path, string Name)? TryAttachmentLink(string line)
        {
            var m = System.Text.RegularExpressions.Regex.Match(line,
                @"^(!?)\[([^\]]*)\]\((attachments/[^)\s]+)\)\s*$");
            if (!m.Success) return null;
            return (m.Groups[1].Value == "!" ? "image" : "file",
                    Uri.UnescapeDataString(m.Groups[3].Value),
                    m.Groups[2].Value.Length > 0 ? m.Groups[2].Value : "attachment");
        }
    }
}
