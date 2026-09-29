using System;
using System.Globalization;
using System.Text;

namespace diff_reg
{
    /// <summary>Registry value type codes, as stored in the hive and written by hex(N): in .reg files.</summary>
    internal enum RegValueKind
    {
        None = 0,
        Sz = 1,
        ExpandSz = 2,
        Binary = 3,
        Dword = 4,
        DwordBigEndian = 5,
        Link = 6,
        MultiSz = 7,
        ResourceList = 8,
        FullResourceDescriptor = 9,
        ResourceRequirementsList = 10,
        Qword = 11
    }

    internal sealed class RegFormatException : Exception
    {
        public RegFormatException(string message) : base(message) { }
    }

    /// <summary>
    /// A registry value normalised to exactly what Windows stores: a type number and a byte array.
    /// Two values are equal when both match, so different .reg spellings of the same stored bits
    /// (dword:00000001 vs hex(4):01,00,00,00, "abc" vs hex(1):61,00,62,00,63,00,00,00, hex line
    /// wrapping, hex digit case) compare equal, while different types never do.
    /// </summary>
    internal sealed class RegValue : IEquatable<RegValue>
    {
        private static readonly byte[] NoBytes = new byte[0];
        private static readonly string[] HexPair = BuildHexPairs();

        /// <summary>The "name"=- form, which deletes a value rather than setting one.</summary>
        public static readonly RegValue DeleteMarker = new RegValue(0, NoBytes, true);

        public int Kind { get; private set; }
        public byte[] Data { get; private set; }
        public bool IsDeleteMarker { get; private set; }

        private RegValue(int kind, byte[] data, bool isDeleteMarker)
        {
            Kind = kind;
            Data = data ?? NoBytes;
            IsDeleteMarker = isDeleteMarker;
        }

        public RegValue(int kind, byte[] data) : this(kind, data, false) { }

        private static string[] BuildHexPairs()
        {
            var pairs = new string[256];
            for (int i = 0; i < 256; i++) pairs[i] = i.ToString("x2", CultureInfo.InvariantCulture);
            return pairs;
        }

        // ---------------------------------------------------------------- parsing

        /// <summary>Parses the text to the right of the = on a .reg value line.</summary>
        public static RegValue Parse(string text)
        {
            string s = text.Trim();
            if (s.Length == 0) throw new RegFormatException("empty value data");
            if (s == "-") return DeleteMarker;

            if (s[0] == '"')
                return new RegValue((int)RegValueKind.Sz, EncodeSz(UnquoteString(s)));

            if (s.StartsWith("dword:", StringComparison.OrdinalIgnoreCase))
            {
                string hex = s.Substring(6).Trim();
                uint v;
                if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v))
                    throw new RegFormatException("malformed dword: " + hex);
                return new RegValue((int)RegValueKind.Dword,
                    new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24) });
            }

            if (s.StartsWith("hex", StringComparison.OrdinalIgnoreCase))
            {
                int colon = s.IndexOf(':');
                if (colon < 0) throw new RegFormatException("hex value with no colon: " + s);

                int kind = (int)RegValueKind.Binary;
                string spec = s.Substring(3, colon - 3).Trim();
                if (spec.Length > 0)
                {
                    if (spec.Length < 3 || spec[0] != '(' || spec[spec.Length - 1] != ')')
                        throw new RegFormatException("malformed hex type specifier: " + spec);
                    // The type number is hexadecimal: regedit writes hex(b) for REG_QWORD.
                    if (!int.TryParse(spec.Substring(1, spec.Length - 2).Trim(),
                            NumberStyles.HexNumber, CultureInfo.InvariantCulture, out kind))
                        throw new RegFormatException("malformed hex type number: " + spec);
                }
                return new RegValue(kind, ParseHexBytes(s, colon + 1));
            }

            throw new RegFormatException("unrecognised value data: " + s);
        }

        /// <summary>Unescapes a quoted .reg string. A backslash escapes the character after it.</summary>
        private static string UnquoteString(string s)
        {
            var sb = new StringBuilder(s.Length);
            int i = 1;
            bool closed = false;
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '\\' && i < s.Length) { sb.Append(s[i++]); continue; }
                if (c == '"') { closed = true; break; }
                sb.Append(c);
            }
            if (!closed) throw new RegFormatException("unterminated string: " + s);
            return sb.ToString();
        }

        /// <summary>REG_SZ data is the UTF-16LE text plus a NUL terminator.</summary>
        private static byte[] EncodeSz(string text)
        {
            var bytes = new byte[(text.Length + 1) * 2];
            Encoding.Unicode.GetBytes(text, 0, text.Length, bytes, 0);
            return bytes;
        }

        /// <summary>
        /// Parses a comma-separated hex byte list. An empty list yields a zero-length array, which
        /// is a real distinction: hex(1): is REG_SZ with no bytes, while "" is REG_SZ with 00 00.
        /// </summary>
        private static byte[] ParseHexBytes(string s, int start)
        {
            int count = 0;
            bool inToken = false;
            for (int i = start; i < s.Length; i++)
            {
                char c = s[i];
                if (c == ',') { count++; inToken = false; }
                else if (!char.IsWhiteSpace(c)) inToken = true;
            }
            if (inToken) count++;
            if (count == 0) return NoBytes;

            var bytes = new byte[count];
            int at = 0, acc = 0;
            inToken = false;
            for (int i = start; i < s.Length; i++)
            {
                char c = s[i];
                if (c == ',') { bytes[at++] = (byte)acc; acc = 0; inToken = false; continue; }
                if (char.IsWhiteSpace(c)) continue;
                int d = HexDigit(c);
                if (d < 0) throw new RegFormatException("malformed hex byte list: " + s.Substring(start));
                acc = (acc << 4) | d;
                inToken = true;
            }
            if (inToken) bytes[at++] = (byte)acc;
            return bytes;
        }

        private static int HexDigit(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        // ---------------------------------------------------------------- equality

        public bool Equals(RegValue other)
        {
            if (ReferenceEquals(other, null)) return false;
            if (ReferenceEquals(other, this)) return true;
            if (Kind != other.Kind || IsDeleteMarker != other.IsDeleteMarker) return false;
            byte[] a = Data, b = other.Data;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        public override bool Equals(object obj) { return Equals(obj as RegValue); }

        public override int GetHashCode()
        {
            unchecked
            {
                int h = Kind * 397 ^ (IsDeleteMarker ? 1 : 0);
                h = h * 397 ^ Data.Length;
                for (int i = 0; i < Data.Length; i++) h = h * 31 + Data[i];
                return h;
            }
        }

        // ---------------------------------------------------------------- display

        private const int DisplayByteLimit = 16;
        private const int DisplayTextLimit = 160;

        /// <summary>Human-readable rendering for the report. Truncates long data unless full is set.</summary>
        public string ToDisplay(bool full)
        {
            if (IsDeleteMarker) return "<deleted>";

            switch (Kind)
            {
                case (int)RegValueKind.Sz:
                case (int)RegValueKind.ExpandSz:
                case (int)RegValueKind.Link:
                {
                    string text;
                    if (TryDecodeSz(Data, out text))
                    {
                        string prefix = Kind == (int)RegValueKind.ExpandSz ? "expand:"
                                      : Kind == (int)RegValueKind.Link ? "link:" : "";
                        return prefix + QuoteForDisplay(text, full);
                    }
                    break;
                }
                case (int)RegValueKind.MultiSz:
                {
                    string[] parts;
                    if (TryDecodeMultiSz(Data, out parts))
                    {
                        var sb = new StringBuilder("multi:[");
                        for (int i = 0; i < parts.Length; i++)
                        {
                            if (i > 0) sb.Append(", ");
                            sb.Append(QuoteForDisplay(parts[i], full));
                        }
                        return sb.Append(']').ToString();
                    }
                    break;
                }
                case (int)RegValueKind.Dword:
                    if (Data.Length == 4)
                        return "dword:" + ToUInt32Le(Data).ToString("x8", CultureInfo.InvariantCulture);
                    break;
                case (int)RegValueKind.DwordBigEndian:
                    if (Data.Length == 4)
                        return "dword-be:" + (((uint)Data[0] << 24) | ((uint)Data[1] << 16) |
                                              ((uint)Data[2] << 8) | Data[3]).ToString("x8", CultureInfo.InvariantCulture);
                    break;
                case (int)RegValueKind.Qword:
                    if (Data.Length == 8)
                    {
                        ulong q = 0;
                        for (int i = 7; i >= 0; i--) q = (q << 8) | Data[i];
                        return "qword:" + q.ToString("x16", CultureInfo.InvariantCulture);
                    }
                    break;
            }

            // Anything not cleanly decodable falls back to raw bytes, tagged with its type.
            return HexPrefix() + HexBytesForDisplay(full);
        }

        private string HexPrefix()
        {
            return Kind == (int)RegValueKind.Binary
                ? "hex:"
                : "hex(" + Kind.ToString("x", CultureInfo.InvariantCulture) + "):";
        }

        private string HexBytesForDisplay(bool full)
        {
            if (Data.Length == 0) return "";
            int shown = full ? Data.Length : Math.Min(Data.Length, DisplayByteLimit);
            var sb = new StringBuilder(shown * 3 + 24);
            for (int i = 0; i < shown; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(HexPair[Data[i]]);
            }
            if (shown < Data.Length)
                sb.Append(",... (").Append(Data.Length.ToString("N0", CultureInfo.InvariantCulture)).Append(" bytes)");
            return sb.ToString();
        }

        private static string QuoteForDisplay(string text, bool full)
        {
            bool truncated = false;
            if (!full && text.Length > DisplayTextLimit)
            {
                text = text.Substring(0, DisplayTextLimit);
                truncated = true;
            }
            var sb = new StringBuilder(text.Length + 8);
            sb.Append('"');
            foreach (char c in text)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < ' ') sb.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                else sb.Append(c);
            }
            sb.Append('"');
            if (truncated) sb.Append("...");
            return sb.ToString();
        }

        // ---------------------------------------------------------------- .reg output

        /// <summary>Appends a complete .reg value line (with CRLF) for this value.</summary>
        public void AppendRegLine(StringBuilder sb, string name)
        {
            string lhs = name.Length == 0 ? "@" : "\"" + EscapeRegText(name) + "\"";

            if (IsDeleteMarker) { sb.Append(lhs).Append("=-\r\n"); return; }

            if (Kind == (int)RegValueKind.Sz)
            {
                string text;
                if (TryDecodeSz(Data, out text))
                {
                    sb.Append(lhs).Append("=\"").Append(EscapeRegText(text)).Append("\"\r\n");
                    return;
                }
            }
            else if (Kind == (int)RegValueKind.Dword && Data.Length == 4)
            {
                sb.Append(lhs).Append("=dword:")
                  .Append(ToUInt32Le(Data).ToString("x8", CultureInfo.InvariantCulture)).Append("\r\n");
                return;
            }

            AppendHexValue(sb, lhs + "=" + HexPrefix(), Data);
        }

        /// <summary>Writes a hex byte list wrapped at regedit's ~80 column convention.</summary>
        private static void AppendHexValue(StringBuilder sb, string prefix, byte[] data)
        {
            const int maxLine = 78;
            sb.Append(prefix);
            int col = prefix.Length;
            for (int i = 0; i < data.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                    col++;
                    if (col + 2 >= maxLine) { sb.Append("\\\r\n  "); col = 2; }
                }
                sb.Append(HexPair[data[i]]);
                col += 2;
            }
            sb.Append("\r\n");
        }

        /// <summary>Escapes a name or string for .reg output: backslash and quote only.</summary>
        public static string EscapeRegText(string s)
        {
            if (s.IndexOf('\\') < 0 && s.IndexOf('"') < 0) return s;
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                if (c == '\\' || c == '"') sb.Append('\\');
                sb.Append(c);
            }
            return sb.ToString();
        }

        // ---------------------------------------------------------------- decoding helpers

        /// <summary>
        /// True when the bytes are a well-formed NUL-terminated UTF-16 string. Zero-length data is
        /// deliberately rejected so hex(1): never round-trips to "" — they store different bytes.
        /// </summary>
        private static bool TryDecodeSz(byte[] d, out string text)
        {
            text = null;
            if (d.Length < 2 || (d.Length & 1) != 0) return false;
            if (d[d.Length - 2] != 0 || d[d.Length - 1] != 0) return false;
            string s = Encoding.Unicode.GetString(d, 0, d.Length - 2);
            if (s.IndexOf('\0') >= 0) return false;
            text = s;
            return true;
        }

        private static bool TryDecodeMultiSz(byte[] d, out string[] parts)
        {
            parts = null;
            if ((d.Length & 1) != 0) return false;
            if (d.Length == 0) { parts = new string[0]; return true; }
            if (d[d.Length - 2] != 0 || d[d.Length - 1] != 0) return false;
            // Drop the list terminator, then the final string's own terminator.
            string all = Encoding.Unicode.GetString(d, 0, d.Length - 2);
            if (all.Length > 0 && all[all.Length - 1] == '\0') all = all.Substring(0, all.Length - 1);
            parts = all.Length == 0 ? new string[0] : all.Split('\0');
            return true;
        }

        private static uint ToUInt32Le(byte[] d)
        {
            return d[0] | ((uint)d[1] << 8) | ((uint)d[2] << 16) | ((uint)d[3] << 24);
        }
    }
}
