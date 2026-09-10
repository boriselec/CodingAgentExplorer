using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodingAgentExplorer.Services;

/// <summary>
/// Strips tools and text blocks from a request before it is forwarded. Only the system prompt
/// and &lt;system-reminder&gt; blocks are scrubbed, never ordinary message content.
/// </summary>
/// <remarks>
/// Rules are unconditional, so identical client payloads produce identical output and the
/// upstream prompt cache survives past a one-time miss when a rule changes.
/// </remarks>
public static class RequestScrubber
{
    // EndConversation ignores permissions.deny, --disallowedTools and PreToolUse hooks, so the
    // proxy is the last hop that can drop it.
    private static readonly string[] StrippedTools = ["EndConversation"];

    // Messages dropped whole. Matched on the start of the message text, and only for role
    // "system": dropping a user or assistant turn would corrupt the conversation.
    private static readonly string[] StrippedMessages =
    [
        "The following skills are available for use with the Skill tool:"
    ];

    // Literal start and end of each block to remove; everything in between goes too. RemoveEnd
    // says whether the end anchor is part of the block or the first text to keep.
    private static readonly (string Start, string End, bool RemoveEnd)[] StrippedText =
    [
        ("When you use a pronoun for someone", "including visible thinking.", true),
        ("# userEmail", "unless the user explicitly asks.", true),
        ("# Memory", "verify it still exists before recommending it.", true),
        ("Available agent types for the Agent tool:", "While bypass permissions mode is active:", false)
    ];

    // Every rule needs its start anchor present verbatim in the body, so a body without any of
    // them cannot change and never has to be parsed.
    private static readonly string[] Anchors =
        [.. StrippedTools, .. StrippedMessages, .. StrippedText.Select(rule => rule.Start)];

    /// <summary>
    /// Returns the scrubbed JSON, or null when nothing was removed or the body is not JSON,
    /// in which case the original bytes are forwarded.
    /// </summary>
    public static string? Scrub(string body)
    {
        if (!Anchors.Any(anchor => body.Contains(anchor, StringComparison.Ordinal)))
            return null;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }

        if (root is not JsonObject obj)
            return null;

        var changed = RemoveTools(obj);
        changed |= ScrubSystemPrompt(obj);
        changed |= ScrubMessages(obj);

        return changed ? obj.ToJsonString() : null;
    }

    private static bool RemoveTools(JsonObject root)
    {
        if (root["tools"] is not JsonArray tools)
            return false;

        var changed = false;
        for (var i = tools.Count - 1; i >= 0; i--)
        {
            if (tools[i] is JsonObject tool
                && tool["name"] is JsonValue name
                && name.TryGetValue<string>(out var toolName)
                && StrippedTools.Contains(toolName))
            {
                tools.RemoveAt(i);
                changed = true;
            }
        }

        return changed;
    }

    // A "system" message in messages[] is injected instruction like the system prompt, so all of
    // it is scrubbed, and the ones listed in StrippedMessages are dropped outright. In a user or
    // assistant turn only the <system-reminder> blocks are scrubbed.
    private static bool ScrubMessages(JsonObject root)
    {
        if (root["messages"] is not JsonArray messages)
            return false;

        var changed = false;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var node = messages[i];
            if (node is not JsonObject message || !IsSystemRole(message))
            {
                changed |= ScrubTextBlocks(node, StripReminders);
                continue;
            }

            if (IsStripped(message))
            {
                messages.RemoveAt(i);
                changed = true;
                continue;
            }

            changed |= ScrubTextBlocks(message, Strip);
        }

        return changed;
    }

    private static bool IsSystemRole(JsonObject message)
        => message["role"] is JsonValue role
            && role.TryGetValue<string>(out var roleName)
            && roleName == "system";

    private static bool IsStripped(JsonObject message)
    {
        var text = message["content"] switch
        {
            JsonValue value when value.TryGetValue<string>(out var content) => content,
            JsonArray blocks when blocks.FirstOrDefault() is JsonObject first
                && first["text"] is JsonValue block
                && block.TryGetValue<string>(out var content) => content,
            _ => null
        };

        return text is not null
            && StrippedMessages.Any(prefix => text.StartsWith(prefix, StringComparison.Ordinal));
    }

    // "system" is a bare string on some clients, an array of text blocks on others.
    private static bool ScrubSystemPrompt(JsonObject root)
    {
        if (root["system"] is JsonValue value && value.TryGetValue<string>(out var text))
        {
            var stripped = Strip(text);
            if (stripped == text)
                return false;

            root["system"] = stripped;
            return true;
        }

        return ScrubTextBlocks(root["system"], Strip);
    }

    // "content" catches messages that carry a bare string, "text" the block form.
    private static bool ScrubTextBlocks(JsonNode? node, Func<string, string> strip)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var changed = false;
                foreach (var (key, value) in obj.ToArray())
                {
                    if (key is "text" or "content"
                        && value is JsonValue candidate
                        && candidate.TryGetValue<string>(out var text))
                    {
                        var stripped = strip(text);
                        if (stripped != text)
                        {
                            obj[key] = stripped;
                            changed = true;
                        }
                    }
                    else
                    {
                        changed |= ScrubTextBlocks(value, strip);
                    }
                }
                return changed;
            }

            case JsonArray array:
            {
                var changed = false;
                foreach (var item in array)
                    changed |= ScrubTextBlocks(item, strip);
                return changed;
            }

            default:
                return false;
        }
    }

    private static string StripReminders(string text)
    {
        const string open = "<system-reminder>";
        const string close = "</system-reminder>";

        StringBuilder? result = null;
        var pos = 0;

        int from;
        while ((from = text.IndexOf(open, pos, StringComparison.Ordinal)) >= 0)
        {
            var to = text.IndexOf(close, from + open.Length, StringComparison.Ordinal);
            if (to < 0)
                break;
            to += close.Length;

            result ??= new StringBuilder(text.Length);
            result.Append(text, pos, from - pos);
            result.Append(Strip(text[from..to]));
            pos = to;
        }

        if (result is null)
            return text;

        result.Append(text, pos, text.Length - pos);
        return result.ToString();
    }

    private static string Strip(string text)
    {
        foreach (var (start, end, removeEnd) in StrippedText)
            text = RemoveBlocks(text, start, end, removeEnd);

        return text;
    }

    // One left-to-right pass per rule: the text to keep is appended to a single builder, so a
    // long body is not reallocated once per block removed.
    private static string RemoveBlocks(string text, string start, string end, bool removeEnd)
    {
        StringBuilder? result = null;
        var pos = 0;

        int from;
        while ((from = text.IndexOf(start, pos, StringComparison.Ordinal)) >= 0)
        {
            var to = text.IndexOf(end, from + start.Length, StringComparison.Ordinal);
            if (to < 0)
                break;
            if (removeEnd)
                to += end.Length;

            result ??= new StringBuilder(text.Length);
            result.Append(text, pos, from - pos);

            // Take the blank line the block leaves behind with it.
            while (result.Length > 0 && result[^1] == '\n')
                result.Length--;

            pos = to;
        }

        if (result is null)
            return text;

        result.Append(text, pos, text.Length - pos);
        return result.ToString();
    }
}
