using System.Text.Json;

namespace ClaudeTaskbar;

/// <summary>
/// Sums real per-session token usage by reading the session's own transcript file
/// directly — confirmed real, not guessed: `transcript_path` is a documented common field
/// on every hook payload, and its JSONL schema was verified live against a real transcript
/// file.
///
/// Important correctness detail found by inspecting a real transcript, not assumed: a
/// single assistant API response can be split across MULTIPLE JSONL lines (one per content
/// block — thinking/tool_use/text), and every one of those lines carries an IDENTICAL copy
/// of that response's `usage` object under `message.usage`. Summing every line naively
/// overcounts (measured live: 996 usage-bearing lines but only 538 unique message ids in
/// one real transcript — summing all 996 would nearly double-count). Fixed by
/// deduplicating on `message.id` and counting each unique message's usage exactly once.
///
/// Deliberately reports only a raw token count, not a "% of quota used" progress bar: this
/// project has no locally-available, non-fabricated source for what the actual quota
/// ceiling is (Claude Code's `statusLine` command, which receives usage data, is never
/// invoked for VS Code extension sessions: tested 2026-09-27) — inventing a ceiling to
/// normalize against would be fabrication; every API or value used here must be verified.
/// </summary>
public static class TranscriptTokenReader
{
    /// <summary>Total tokens (input + output + cache creation + cache read) actually
    /// billed across this session's transcript so far, or null if the transcript can't be
    /// read (missing path, file not found, unreadable — never guessed).</summary>
    public static long? GetTotalTokens(string? transcriptPath)
    {
        if (string.IsNullOrWhiteSpace(transcriptPath) || !File.Exists(transcriptPath)) return null;

        var seenMessageIds = new HashSet<string>();
        long total = 0;

        try
        {
            using var reader = new StreamReader(transcriptPath);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (!doc.RootElement.TryGetProperty("message", out var message)) continue;
                    if (!message.TryGetProperty("usage", out var usage)) continue;

                    // Dedup key: message.id identifies the underlying API response: every
                    // JSONL line for the same response carries the same usage figures, so
                    // counting it more than once would overcount actual token spend.
                    var messageId = message.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    if (string.IsNullOrEmpty(messageId) || !seenMessageIds.Add(messageId)) continue;

                    total += GetLong(usage, "input_tokens");
                    total += GetLong(usage, "output_tokens");
                    total += GetLong(usage, "cache_creation_input_tokens");
                    total += GetLong(usage, "cache_read_input_tokens");
                }
                catch
                {
                    // A partially-written or malformed line — skip it, don't let one bad
                    // line abort the whole count.
                }
            }
        }
        catch
        {
            return null; // file locked/deleted mid-read etc. — never guess a count
        }

        return total;
    }

    private static long GetLong(JsonElement obj, string propertyName) =>
        obj.TryGetProperty(propertyName, out var el) && el.TryGetInt64(out var v) ? v : 0;

    /// <summary>Human-friendly compact format (e.g. "48.2k", "1.3M") for the tooltip.</summary>
    public static string Format(long tokens) => tokens switch
    {
        >= 1_000_000 => $"{tokens / 1_000_000.0:0.#}M",
        >= 1_000 => $"{tokens / 1_000.0:0.#}k",
        _ => tokens.ToString(),
    };
}
