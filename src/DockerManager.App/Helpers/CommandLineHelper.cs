using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace DockerManager.App.Helpers;

public static class CommandLineHelper
{
    /// <summary>
    /// Parses a command line string or JSON array into a list of arguments (1-on-1 compatible with Docker CMD).
    /// Supports double quotes, single quotes, backslash escapes, and Dockerfile/JSON array syntax (e.g. ["nginx", "-g", "daemon off;"]).
    /// </summary>
    public static List<string>? ParseCommandLine(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var trimmed = input.Trim();

        // 1. Support JSON / Dockerfile exec form: ["nginx", "-g", "daemon off;"]
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
        {
            try
            {
                var jsonList = JsonSerializer.Deserialize<List<string>>(trimmed);
                if (jsonList != null && jsonList.Count > 0)
                {
                    return jsonList;
                }
            }
            catch
            {
                // Fallback to token parsing if not valid JSON
            }
        }

        // 2. Tokenize shell / CLI command string
        var list = new List<string>();
        var current = new StringBuilder();
        bool inDoubleQuotes = false;
        bool inSingleQuotes = false;
        bool escaped = false;
        bool tokenHasStarted = false;

        for (int i = 0; i < trimmed.Length; i++)
        {
            char c = trimmed[i];

            if (escaped)
            {
                current.Append(c);
                escaped = false;
                tokenHasStarted = true;
                continue;
            }

            if (c == '\\' && !inSingleQuotes)
            {
                escaped = true;
                tokenHasStarted = true;
                continue;
            }

            if (inSingleQuotes)
            {
                if (c == '\'')
                {
                    inSingleQuotes = false;
                }
                else
                {
                    current.Append(c);
                }
                continue;
            }

            if (inDoubleQuotes)
            {
                if (c == '"')
                {
                    inDoubleQuotes = false;
                }
                else
                {
                    current.Append(c);
                }
                continue;
            }

            // Outside quotes
            if (c == '"')
            {
                inDoubleQuotes = true;
                tokenHasStarted = true;
            }
            else if (c == '\'')
            {
                inSingleQuotes = true;
                tokenHasStarted = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (tokenHasStarted)
                {
                    list.Add(current.ToString());
                    current.Clear();
                    tokenHasStarted = false;
                }
            }
            else
            {
                current.Append(c);
                tokenHasStarted = true;
            }
        }

        if (escaped)
        {
            current.Append('\\');
        }

        if (tokenHasStarted)
        {
            list.Add(current.ToString());
        }

        return list.Count > 0 ? list : null;
    }

    /// <summary>
    /// Formats a list of command arguments into a clean command line string,
    /// automatically quoting arguments that contain spaces or special characters.
    /// </summary>
    public static string FormatCommandLine(IEnumerable<string>? args)
    {
        if (args == null)
            return string.Empty;

        var formattedList = new List<string>();
        foreach (var arg in args)
        {
            if (string.IsNullOrEmpty(arg))
            {
                formattedList.Add("\"\"");
                continue;
            }

            bool needsQuotes = arg.Any(c => char.IsWhiteSpace(c) || c == '"' || c == '\'');
            if (needsQuotes)
            {
                var escaped = arg.Replace("\\", "\\\\").Replace("\"", "\\\"");
                formattedList.Add($"\"{escaped}\"");
            }
            else
            {
                formattedList.Add(arg);
            }
        }

        return string.Join(" ", formattedList);
    }
}
