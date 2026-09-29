using System;
using System.Globalization;
using System.Text;

namespace diff_reg
{
    /// <summary>
    /// A single-line, PowerShell Write-Progress style bar on stderr. Progress goes to stderr so the
    /// report on stdout stays pipeable, and suppresses itself when stderr is redirected.
    /// </summary>
    internal sealed class ProgressReporter
    {
        private const int UpdateIntervalMs = 100;
        private const int MaxBarWidth = 28;
        private const int MinBarWidth = 8;
        private const int SizeFieldWidth = 9;

        private readonly bool _enabled;
        private int _lastTick;
        private int _lastRenderedLength;
        private bool _lineDirty;

        public ProgressReporter(bool enabled)
        {
            bool redirected;
            try { redirected = Console.IsErrorRedirected; }
            catch (Exception) { redirected = true; }
            _enabled = enabled && !redirected;
            _lastTick = Environment.TickCount - UpdateIntervalMs;
        }

        public bool Enabled { get { return _enabled; } }

        /// <summary>Throttled to ~10 updates a second; cheap enough to call on every input line.</summary>
        public void Report(string activity, long current, long total)
        {
            if (!_enabled) return;
            int now = Environment.TickCount;
            if (unchecked(now - _lastTick) < UpdateIntervalMs) return;
            _lastTick = now;
            Render(activity, current, total);
        }

        /// <summary>Renders unconditionally, ignoring the throttle. Used for the initial 0% frame.</summary>
        public void ReportNow(string activity, long current, long total)
        {
            if (!_enabled) return;
            _lastTick = Environment.TickCount;
            Render(activity, current, total);
        }

        private void Render(string activity, long current, long total)
        {
            if (current > total) current = total;
            double fraction = total > 0 ? (double)current / total : 1.0;

            // Fixed-width size fields keep the line length constant as the numbers grow, so the
            // bar does not change width from frame to frame.
            string tail = "  " + ((int)Math.Round(fraction * 100)).ToString(CultureInfo.InvariantCulture).PadLeft(3)
                          + "%   " + FormatBytes(current).PadLeft(SizeFieldWidth)
                          + " / " + FormatBytes(total).PadLeft(SizeFieldWidth);

            // The percentage and byte counts are the information; the bar is decoration, so it
            // gives up its width first and drops out entirely on a very narrow console.
            int width = ConsoleWidth() - 1;
            int barWidth = Math.Min(MaxBarWidth, width - activity.Length - tail.Length - 4);

            var sb = new StringBuilder(width);
            sb.Append(activity);
            if (barWidth >= MinBarWidth)
            {
                int filled = (int)Math.Round(fraction * barWidth);
                sb.Append("  [").Append('#', filled).Append('-', barWidth - filled).Append(']');
            }
            sb.Append(tail);

            string line = sb.ToString();
            if (line.Length > width) line = line.Substring(0, Math.Max(0, width));

            Console.Error.Write('\r');
            Console.Error.Write(line);
            if (_lastRenderedLength > line.Length)
                Console.Error.Write(new string(' ', _lastRenderedLength - line.Length));
            _lastRenderedLength = line.Length;
            _lineDirty = true;
        }

        /// <summary>Erases the progress line so ordinary output starts on a clean row.</summary>
        public void Clear()
        {
            if (!_enabled || !_lineDirty) return;
            Console.Error.Write('\r');
            Console.Error.Write(new string(' ', Math.Min(_lastRenderedLength, ConsoleWidth() - 1)));
            Console.Error.Write('\r');
            _lastRenderedLength = 0;
            _lineDirty = false;
        }

        /// <summary>Clears the bar and leaves a permanent summary line in its place.</summary>
        public void Complete(string message)
        {
            Clear();
            if (_enabled) Console.Error.WriteLine(message);
        }

        private static int ConsoleWidth()
        {
            try
            {
                int w = Console.WindowWidth;
                return w > 20 ? w : 80;
            }
            catch (Exception)
            {
                // Throws when no console is attached.
                return 80;
            }
        }

        public static string FormatBytes(long bytes)
        {
            const double kb = 1024.0, mb = kb * 1024.0, gb = mb * 1024.0;
            if (bytes >= gb) return (bytes / gb).ToString("F1", CultureInfo.InvariantCulture) + " GB";
            if (bytes >= mb) return (bytes / mb).ToString("F1", CultureInfo.InvariantCulture) + " MB";
            if (bytes >= kb) return (bytes / kb).ToString("F1", CultureInfo.InvariantCulture) + " KB";
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }
    }
}
