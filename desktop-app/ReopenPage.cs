// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Z.ai Chat Recovery contributors
// Licensed under the Apache License, Version 2.0 (see LICENSE file in the repository root).
//
// reopen-chats.html - the one-click way back to every recovered chat. Written
// into each new backup, and regenerated on demand by the reader (to a temp
// copy, so dated snapshots stay untouched) uniting every chat id found in ANY
// snapshot. Batch opens run synchronously inside one click because browsers
// only allow window.open during a user gesture - paced/delayed opens get
// popup-blocked; blocked tabs are counted and explained in plain words.

using System.Net;
using System.Text;

namespace ZaiChatRecovery.Desktop;

internal static class ReopenPage
{
    public sealed record Entry(string Id, string Title, string UpdatedText, string? CountText, string? OnlyIn);

    // Kept byte-compatible with the Python tool's page so both backups feel identical.
    private const string Head = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Your chats still open - Z.ai Chat Recovery</title>
<style>
  :root { --bg:#F5F5F7; --panel:#FFFFFF; --line:#E5E5EA; --text:#1D1D1F; --muted:#6E6E73;
          --accent:#007AFF; --good:#1E8E3E; --warn:#B45309; }
  * { box-sizing:border-box; }
  html { -webkit-text-size-adjust:100%; }
  body { margin:0; padding:0 20px 64px; background:var(--bg); color:var(--text);
         font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,Ubuntu,Cantarell,"Noto Sans","Helvetica Neue",Arial,sans-serif;
         font-size:18px; line-height:1.55; }
  .wrap { max-width:820px; margin:0 auto; }
  h1 { font-size:30px; line-height:1.25; margin:38px 0 8px; font-weight:800; }
  .sub { font-size:19px; color:var(--muted); margin:0 0 20px; }
  .banner { background:#E5F1FF; border:1px solid #A8CFFF; border-radius:12px;
            padding:14px 18px; margin:0 0 20px; font-size:17px; }
  .brow { display:flex; flex-wrap:wrap; gap:12px; align-items:center; margin:0 0 14px; }
  button.big { min-height:52px; padding:12px 24px; font-size:18px; font-weight:600;
               color:#fff; background:var(--accent); border:0; border-radius:12px; cursor:pointer; }
  button.big:disabled { opacity:.4; cursor:default; }
  .progress { font-size:16.5px; color:var(--muted); }
  #q { display:block; width:100%; min-height:52px; font-size:18px; padding:10px 16px;
       border:1.5px solid var(--line); border-radius:12px; background:#fff; color:var(--text);
       font-family:inherit; }
  ol.chats { list-style:none; margin:14px 0 0; padding:0; }
  li.chat { display:flex; gap:14px; align-items:center; background:var(--panel);
            border:1px solid var(--line); border-radius:14px; padding:14px 18px; margin-bottom:10px; }
  li.chat .meta { flex:1; min-width:0; }
  li.chat .t { font-size:18.5px; font-weight:600; overflow-wrap:anywhere; }
  li.chat .d { font-size:15px; color:var(--muted); margin-top:3px; }
  li.chat .d .old { color:var(--warn); font-weight:600; }
  a.open { flex-shrink:0; display:inline-block; min-height:48px; padding:10px 20px;
           font-size:17px; font-weight:600; color:var(--accent); background:#fff;
           border:2px solid var(--accent); border-radius:12px; text-decoration:none; }
  a.open:visited { color:var(--accent); }
  li.opened { opacity:.75; }
  li.opened a.open { color:var(--good); border-color:var(--good); }
  .tip { display:none; background:#FFF4E0; border:1px solid #E8C57E; color:var(--warn);
         border-radius:12px; padding:12px 16px; font-size:16px; margin-top:14px; }
  .tip.show { display:block; }
  footer { margin-top:36px; font-size:14.5px; color:var(--muted); }
</style>
</head>
<body>
<div class="wrap">
  <h1>Your chats still open</h1>
  <p class="sub">{{COUNTLABEL}}, saved on this computer. Every one is still on the z.ai website -
     even if your chat list there looks empty. Click any chat to read it.</p>
  <div class="banner"><strong>One thing first:</strong> this page uses your browser's own z.ai login.
     Make sure the browser showing this page is signed in as {{ACCOUNT}} -
     if a chat asks you to sign in, sign in, then come back. Nothing is lost by closing this page.</div>
  <div class="brow">
    <button class="big" id="next" type="button">Open the next 10 chats</button>
    <span class="progress" id="prog"></span>
  </div>
  <input id="q" type="search" placeholder="Search your chats by name&hellip;" autocomplete="off">
  <ol class="chats" id="list">
""";

    private const string Tail = """
  </ol>
  <p class="tip" id="tip"><strong>Your browser stopped some tabs.</strong> That is its pop-up guard,
     not a problem with your chats. Click the remaining chats one by one -
     or allow pop-up windows for this page and press the button again.</p>
  <footer>Made by Z.ai Chat Recovery on {{GENERATED}}. The links open on chat.z.ai and keep working
     even while its chat list looks empty. Keep this file - it is your chat index.</footer>
</div>
<script>
(function () {
  var items = Array.prototype.slice.call(document.querySelectorAll("li.chat"));
  var btn = document.getElementById("next");
  var prog = document.getElementById("prog");
  var tip = document.getElementById("tip");
  var blocked = 0;
  function remaining() {
    return items.filter(function (li) {
      return li.offsetParent !== null && !li.classList.contains("opened");
    });
  }
  function update() {
    var opened = 0;
    items.forEach(function (li) { if (li.classList.contains("opened")) opened++; });
    var left = remaining().length;
    prog.textContent = opened === items.length
      ? "All " + items.length + " chats opened \u2713"
      : opened + " of " + items.length + " opened \u00b7 " + left + " to go";
    btn.disabled = remaining().length === 0;
  }
  function mark(li) {
    li.classList.add("opened");
    li.querySelector("a.open").textContent = "Opened \u2713";
  }
  btn.addEventListener("click", function () {
    var batch = remaining().slice(0, 10);
    for (var i = 0; i < batch.length; i++) {
      var w = window.open(batch[i].querySelector("a.open").href, "_blank");
      if (w) mark(batch[i]);
      else blocked++;
    }
    if (blocked > 0) tip.className = "tip show";
    update();
  });
  items.forEach(function (li) {
    li.querySelector("a.open").addEventListener("click", function () { mark(li); update(); });
  });
  document.getElementById("q").addEventListener("input", function (e) {
    var q = e.target.value.trim().toLowerCase();
    items.forEach(function (li) {
      li.style.display = li.getAttribute("data-title").indexOf(q) !== -1 ? "" : "none";
    });
    update();
  });
  update();
})();
</script>
</body>
</html>
""";

    public static string Build(string? accountEmail, IReadOnlyList<Entry> entries)
    {
        var sb = new StringBuilder(18 * 1024 + 160 * entries.Count);
        int n = entries.Count;
        sb.Append(Head
            .Replace("{{COUNTLABEL}}", $"{n} chat" + (n == 1 ? "" : "s"))
            .Replace("{{ACCOUNT}}", WebUtility.HtmlEncode(accountEmail ?? "your z.ai account")));
        foreach (var e in entries)
        {
            var segs = new List<string>();
            if (e.UpdatedText.Length > 0) segs.Add("updated " + e.UpdatedText);
            if (!string.IsNullOrEmpty(e.CountText)) segs.Add(e.CountText);
            sb.Append("<li class=\"chat\" data-title=\"")
              .Append(WebUtility.HtmlEncode(e.Title.ToLowerInvariant()))
              .Append("\">\n  <div class=\"meta\"><div class=\"t\">")
              .Append(WebUtility.HtmlEncode(e.Title))
              .Append("</div>");
            if (segs.Count > 0)
                sb.Append("<div class=\"d\">").Append(WebUtility.HtmlEncode(string.Join(" · ", segs))).Append("</div>");
            if (!string.IsNullOrEmpty(e.OnlyIn))
                sb.Append("<div class=\"d\"><span class=\"old\">")
                  .Append(WebUtility.HtmlEncode(e.OnlyIn)).Append("</span></div>");
            sb.Append("</div>\n  <a class=\"open\" href=\"https://chat.z.ai/c/").Append(e.Id)
              .Append("\" target=\"_blank\" rel=\"noopener\">Open &#8594;</a>\n</li>\n");
        }
        sb.Append(Tail.Replace("{{GENERATED}}", DateTime.Now.ToString("yyyy-MM-dd HH:mm")));
        return sb.ToString();
    }

    /// <summary>Temp copy for on-demand use (the reader never writes into dated
    /// snapshots - they are immutable, and the page is regenerable anyway).</summary>
    public static string WriteToTemp(string? accountEmail, IReadOnlyList<Entry> entries)
    {
        var path = Path.Combine(Path.GetTempPath(), "zai-reopen-chats.html");
        File.WriteAllText(path, Build(accountEmail, entries));
        return path;
    }

    /// <summary>Every chat id seen in ANY snapshot beside this folder (or the
    /// folder alone when it is not one of the dated snapshots - e.g. a
    /// python-CLI backup). Newest snapshot wins for title/date; chats missing
    /// from the newest snapshot are tagged "only in your older backup".</summary>
    public static List<Entry> UnionFor(string folder)
    {
        var full = Path.GetFullPath(folder);
        var parent = Path.GetDirectoryName(full);
        var folders = (parent is null ? new List<string>() : Snapshots.List(parent))
            .OrderBy(d => d, StringComparer.Ordinal)   // oldest first: newest overwrites
            .ToList();
        if (folders.All(d => !string.Equals(Path.GetFullPath(d), full, StringComparison.OrdinalIgnoreCase)))
            folders.Add(full);
        string newest = folders[^1];
        var byId = new Dictionary<string, Entry>();
        foreach (var f in folders)
        {
            var tag = string.Equals(Path.GetFullPath(f), Path.GetFullPath(newest), StringComparison.OrdinalIgnoreCase)
                ? null
                : "only in your " + SnapshotDate(Path.GetFileName(f)) + " backup";
            foreach (var s in BackupReader.ScanBackupFolder(f))
                byId[s.Id] = new Entry(s.Id, s.Title, s.UpdatedText,
                    s.Messages > 0 ? $"{s.Messages} messages" : null, tag);
        }
        return byId.Values.OrderByDescending(e => e.UpdatedText, StringComparer.Ordinal).ToList();
    }

    private static string SnapshotDate(string folderName) =>
        folderName.Length >= 10 ? folderName[..10] : folderName;
}
