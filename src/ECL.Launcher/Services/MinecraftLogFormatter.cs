// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System.Text.RegularExpressions;

namespace ECL.Services;

/// <summary>
/// Turns Minecraft's log4j2 XML stream into readable lines for the log panel.
///
/// <para>The client is configured with <c>client-1.12.xml</c>, whose console appender
/// emits one &lt;log4j:Event&gt; element per record spread over several lines. Dumping
/// that verbatim into the UI is unreadable, so the event header (level/thread) is
/// remembered and prefixed onto the following message.</para>
///
/// <para>The file on disk keeps the raw stream; only the UI gets the cleaned text.</para>
/// </summary>
public sealed class MinecraftLogFormatter
{
    private static readonly Regex EventRx = new(
        @"level=""(?<level>[^""]+)"".*?thread=""(?<thread>[^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex CdataRx = new(
        @"<!\[CDATA\[(?<msg>.*?)\]\]>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex PlainMsgRx = new(
        @"<log4j:Message>(?<msg>.*?)</log4j:Message>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private string _prefix = "";

    /// <summary>Returns the display text, or null when the line carried no payload.</summary>
    public string? Format(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        string trimmed = raw.TrimStart();

        // Header line of a multi-line event: buffer it and wait for the message.
        if (trimmed.StartsWith("<log4j:Event", StringComparison.Ordinal))
        {
            var ev = EventRx.Match(trimmed);
            if (ev.Success)
            {
                string level = Shorten(ev.Groups["level"].Value);
                string thread = ev.Groups["thread"].Value;
                _prefix = $"[{thread}/{level}] ";
            }
            // A single-line event still carries the message on the same line.
            if (!trimmed.Contains("</log4j:Event>", StringComparison.Ordinal)) return null;
        }

        var msg = CdataRx.Match(trimmed);
        if (!msg.Success) msg = PlainMsgRx.Match(trimmed);
        if (msg.Success)
        {
            string text = msg.Groups["msg"].Value;
            return _prefix + text;
        }

        // Pure structure lines (closing tags, empty events) are dropped.
        if (trimmed.StartsWith("</log4j:Event", StringComparison.Ordinal) ||
            trimmed.StartsWith("</log4j:", StringComparison.Ordinal) ||
            trimmed.StartsWith("<log4j:", StringComparison.Ordinal))
            return null;

        return raw;
    }

    private static string Shorten(string level) => level switch
    {
        "INFO" => "INFO",
        "WARN" => "WARN",
        "ERROR" or "FATAL" => "ERROR",
        "DEBUG" => "DEBUG",
        "TRACE" => "TRACE",
        _ => level
    };
}
