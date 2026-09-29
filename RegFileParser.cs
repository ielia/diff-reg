using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace diff_reg
{
    internal sealed class ParseResult
    {
        public RegKeyNode Root;
        public long Bytes;
        public int KeyCount;
        public int ValueCount;
        public int WarningCount;
        public TimeSpan Elapsed;
    }

    /// <summary>
    /// Streaming line-by-line .reg parser. Never materialises the file, so memory scales with the
    /// parsed tree rather than with the file size.
    /// </summary>
    internal static class RegFileParser
    {
        private const int BufferSize = 64 * 1024;
        private const int MaxWarnings = 20;
        private const int ProgressLineInterval = 256;

        public static ParseResult Parse(string path, string activity, ProgressReporter progress)
        {
            var result = new ParseResult { Root = RegKeyNode.CreateRoot() };
            var started = DateTime.UtcNow;

            // Value names repeat constantly across a large hive; pooling them avoids holding tens of
            // thousands of duplicate strings alive.
            var namePool = new Dictionary<string, string>(RegKeyNode.NameComparer);

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                                           BufferSize, FileOptions.SequentialScan))
            // Encoding.Default is the ANSI codepage on .NET Framework, which is right for REGEDIT4
            // files, while a UTF-16 LE BOM is detected and honoured for "Version 5.00" exports.
            using (var reader = new StreamReader(fs, Encoding.Default, true, BufferSize))
            {
                long total = fs.Length;
                result.Bytes = total;
                progress.ReportNow(activity, 0, total);

                RegKeyNode current = null;
                var continuation = new StringBuilder(256);
                bool inContinuation = false;
                bool headerChecked = false;
                int lineNumber = 0;
                int sinceProgress = 0;

                string raw;
                while ((raw = reader.ReadLine()) != null)
                {
                    lineNumber++;
                    if (++sinceProgress >= ProgressLineInterval)
                    {
                        sinceProgress = 0;
                        progress.Report(activity, fs.Position, total);
                    }

                    string line = raw.Trim();

                    bool continues = EndsWithContinuation(line);
                    if (continues) line = line.Substring(0, line.Length - 1).TrimEnd();

                    string logical;
                    if (inContinuation)
                    {
                        continuation.Append(line);
                        if (continues) continue;
                        logical = continuation.ToString();
                        continuation.Length = 0;
                        inContinuation = false;
                    }
                    else if (continues)
                    {
                        continuation.Length = 0;
                        continuation.Append(line);
                        inContinuation = true;
                        continue;
                    }
                    else
                    {
                        logical = line;
                    }

                    if (logical.Length == 0 || logical[0] == ';') continue;

                    if (!headerChecked)
                    {
                        headerChecked = true;
                        if (IsHeader(logical)) continue;
                        Warn(progress, result, path, lineNumber, "missing .reg header line; parsing anyway");
                    }

                    try
                    {
                        ProcessLine(logical, result, namePool, ref current);
                    }
                    catch (RegFormatException ex)
                    {
                        Warn(progress, result, path, lineNumber, ex.Message);
                    }
                }

                if (inContinuation)
                    Warn(progress, result, path, lineNumber, "file ends with an unfinished continuation line");

                progress.Report(activity, total, total);
            }

            result.Elapsed = DateTime.UtcNow - started;
            return result;
        }

        private static bool IsHeader(string line)
        {
            return line.StartsWith("Windows Registry Editor", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("REGEDIT4", StringComparison.OrdinalIgnoreCase);
        }

        private static void ProcessLine(string line, ParseResult result,
                                        Dictionary<string, string> namePool, ref RegKeyNode current)
        {
            if (line[0] == '[')
            {
                int end = line.LastIndexOf(']');
                if (end <= 0) throw new RegFormatException("malformed key line: " + line);

                string keyPath = line.Substring(1, end - 1).Trim();
                bool delete = keyPath.Length > 0 && keyPath[0] == '-';
                if (delete) keyPath = keyPath.Substring(1).Trim();
                if (keyPath.Length == 0) throw new RegFormatException("empty key path");

                current = result.Root.GetOrAddPath(keyPath);
                current.IsExplicit = true;
                if (delete) current.IsDeleteMarker = true;
                result.KeyCount++;
                return;
            }

            if (current == null) throw new RegFormatException("value line before any key: " + line);

            string name;
            int equals;
            if (!TryParseValueName(line, out name, out equals))
                throw new RegFormatException("malformed value line: " + line);

            string pooled;
            if (namePool.TryGetValue(name, out pooled)) name = pooled;
            else namePool.Add(name, name);

            current.SetValue(name, RegValue.Parse(line.Substring(equals + 1)));
            result.ValueCount++;
        }

        /// <summary>
        /// Reads the value name and locates the = that follows it. Scanning for the closing quote
        /// must respect escapes: a name can contain \" and IndexOf would stop at the wrong place.
        /// </summary>
        private static bool TryParseValueName(string line, out string name, out int equals)
        {
            name = null;
            equals = -1;
            int i;

            if (line[0] == '@')
            {
                name = "";
                i = 1;
            }
            else if (line[0] == '"')
            {
                var sb = new StringBuilder(32);
                i = 1;
                bool closed = false;
                while (i < line.Length)
                {
                    char c = line[i++];
                    if (c == '\\' && i < line.Length) { sb.Append(line[i++]); continue; }
                    if (c == '"') { closed = true; break; }
                    sb.Append(c);
                }
                if (!closed) return false;
                name = sb.ToString();
            }
            else
            {
                return false;
            }

            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            if (i >= line.Length || line[i] != '=') return false;
            equals = i;
            return true;
        }

        /// <summary>
        /// A trailing backslash continues the value onto the next line. It only counts as a
        /// continuation outside a quoted string: inside one a literal backslash is always written
        /// \\, so a lone trailing backslash there means the line is malformed, not continued.
        /// </summary>
        private static bool EndsWithContinuation(string s)
        {
            if (s.Length == 0 || s[s.Length - 1] != '\\') return false;
            bool inString = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (inString)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '"') inString = false;
                }
                else if (c == '"')
                {
                    inString = true;
                }
            }
            return !inString;
        }

        private static void Warn(ProgressReporter progress, ParseResult result, string path,
                                 int lineNumber, string message)
        {
            result.WarningCount++;
            // Erase the bar first, or the warning lands on top of it.
            progress.Clear();
            if (result.WarningCount <= MaxWarnings)
            {
                Console.Error.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "warning: {0}({1}): {2}", Path.GetFileName(path), lineNumber, message));
            }
            else if (result.WarningCount == MaxWarnings + 1)
            {
                Console.Error.WriteLine("warning: further warnings suppressed");
            }
        }
    }
}
