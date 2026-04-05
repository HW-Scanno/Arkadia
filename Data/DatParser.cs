using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;

namespace Arkadia.Data;

/// <summary>Parses Logiqx XML DAT files (Redump, No-Intro, TOSEC).</summary>
public static class DatParser
{
    public sealed class Result
    {
        public bool              Success      { get; init; }
        public string            Name         { get; init; } = "";
        public string            Version      { get; init; } = "";
        public string            Date         { get; init; } = "";
        public string            Author       { get; init; } = "";
        public List<ParsedGame>  Games        { get; init; } = [];
        public string            ErrorMessage { get; init; } = "";
    }

    /// <summary>
    /// One ROM entry inside a &lt;game&gt; element.
    /// Fields map directly to Logiqx XML attributes; empty string means absent.
    /// </summary>
    public sealed class ParsedRom
    {
        public string Name { get; init; } = "";
        public string Size { get; init; } = "";
        public string Crc  { get; init; } = "";
        public string Md5  { get; init; } = "";
        public string Sha1 { get; init; } = "";
    }

    public sealed class ParsedGame
    {
        public string           Name        { get; init; } = "";
        public string           Region      { get; init; } = "";
        public string           Languages   { get; init; } = "";

        /// <summary>All &lt;rom&gt; children of this &lt;game&gt; element.</summary>
        public List<ParsedRom>  Roms        { get; init; } = [];

        /// <summary>
        /// Stable content identity derived from ROM checksums.
        /// Format: "sha1:&lt;hex&gt;[,sha1:&lt;hex&gt;...]" sorted ascending.
        /// Falls back to "md5:..." if SHA1 is absent.
        /// Empty string if no usable checksums are present.
        /// </summary>
        public string           ContentKey  { get; init; } = "";
    }

    public static Result Parse(string path)
    {
        try
        {
            var doc = new XmlDocument();
            doc.Load(path);

            var root = doc.DocumentElement;
            if (root is null || root.Name != "datafile")
                return Fail("Not a valid Logiqx datafile (missing <datafile> root).");

            // ── Header ──────────────────────────────────────────────────────
            var header  = root["header"];
            var name    = header?["name"]?.InnerText.Trim()    ?? "";
            var version = header?["version"]?.InnerText.Trim() ?? "";
            var date    = header?["date"]?.InnerText.Trim()    ?? version; // fall back to version

            // Normalize date: keep only the date portion if it contains time (e.g. "2026-03-15 23-18-54")
            if (date.Length >= 10)
                date = date[..10].Replace(' ', '-');

            // ── Games ────────────────────────────────────────────────────────
            var games = new List<ParsedGame>();
            foreach (XmlNode node in root.ChildNodes)
            {
                if (node is not XmlElement el) continue;
                if (el.Name != "game" && el.Name != "machine") continue;

                var gameName  = el.GetAttribute("name");
                if (gameName.Length == 0) continue;

                var roms = new List<ParsedRom>();
                foreach (XmlNode child in el.ChildNodes)
                {
                    if (child is not XmlElement rom) continue;
                    if (rom.Name != "rom") continue;
                    roms.Add(new ParsedRom
                    {
                        Name = rom.GetAttribute("name"),
                        Size = rom.GetAttribute("size"),
                        Crc  = rom.GetAttribute("crc"),
                        Md5  = rom.GetAttribute("md5"),
                        Sha1 = rom.GetAttribute("sha1"),
                    });
                }

                games.Add(new ParsedGame
                {
                    Name       = gameName,
                    Region     = ExtractRegion(gameName),
                    Languages  = ExtractLanguages(gameName),
                    Roms       = roms,
                    ContentKey = ComputeContentKey(roms),
                });
            }

            if (games.Count == 0)
                return Fail("No <game> entries found in DAT file.");

            return new Result
            {
                Success = true,
                Name    = name,
                Version = version,
                Date    = date,
                Author  = header?["author"]?.InnerText.Trim() ?? "",
                Games   = games,
            };
        }
        catch (XmlException ex)
        {
            return Fail($"XML parse error: {ex.Message}");
        }
        catch (IOException ex)
        {
            return Fail($"File read error: {ex.Message}");
        }
        catch (Exception ex)
        {
            return Fail($"Unexpected error: {ex.Message}");
        }
    }

    private static Result Fail(string message) =>
        new() { Success = false, ErrorMessage = message };

    /// <summary>
    /// Derives a stable content identity from a game's ROM list.
    /// Uses SHA1 when present; falls back to MD5.
    /// Returns empty string if no usable checksums are available.
    /// </summary>
    internal static string ComputeContentKey(List<ParsedRom> roms)
    {
        var sha1s = roms
            .Select(r => r.Sha1.Trim().ToLowerInvariant())
            .Where(s => s.Length == 40)   // SHA1 is 40 hex chars
            .OrderBy(s => s)
            .ToList();

        if (sha1s.Count > 0)
            return string.Join(",", sha1s.Select(s => $"sha1:{s}"));

        var md5s = roms
            .Select(r => r.Md5.Trim().ToLowerInvariant())
            .Where(m => m.Length == 32)   // MD5 is 32 hex chars
            .OrderBy(m => m)
            .ToList();

        if (md5s.Count > 0)
            return string.Join(",", md5s.Select(m => $"md5:{m}"));

        return "";
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    // Extract region from parenthetical suffix, e.g. "(USA)", "(Europe)", "(Japan)"
    private static string ExtractRegion(string name)
    {
        var regions = new[]
        {
            "USA", "Europe", "Japan", "World", "Germany", "France", "Spain",
            "Italy", "Brazil", "Australia", "Korea", "Canada", "China", "Taiwan",
            "Netherlands", "Sweden", "Norway", "Denmark", "Finland", "Asia",
            "Hong Kong", "Portugal", "Poland", "Russia", "Greece", "Czech",
        };

        foreach (var r in regions)
            if (name.Contains($"({r}", StringComparison.OrdinalIgnoreCase) ||
                name.Contains($", {r})", StringComparison.OrdinalIgnoreCase))
                return r;

        return "";
    }

    // Extract language codes from parenthetical like "(En,Fr,De,Es,It)"
    private static string ExtractLanguages(string name)
    {
        // Look for a paren block that looks like a language list: (En,Fr,De,...)
        var start = 0;
        while (true)
        {
            var open = name.IndexOf('(', start);
            if (open < 0) break;
            var close = name.IndexOf(')', open);
            if (close < 0) break;

            var inner = name[(open + 1)..close];
            // All tokens should be 2-letter codes
            var parts = inner.Split(',');
            if (parts.Length >= 2 && Array.TrueForAll(parts, p => p.Trim().Length == 2))
                return inner.ToLowerInvariant().Replace(" ", "");

            start = close + 1;
        }
        return "";
    }
}
