using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace KeyIsle
{
    internal sealed class QuotaWindow
    {
        public string model { get; set; }
        public string period { get; set; }
        public double? remaining { get; set; }
        public long? reset { get; set; }
    }

    internal sealed class QuotaReport
    {
        public string source { get; set; }
        public long observed { get; set; }
        public List<QuotaWindow> windows = new List<QuotaWindow>();

        internal static QuotaReport Start(string source)
        {
            return new QuotaReport { source = source, observed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        }
    }

    // Only the fields in QuotaReport cross into the vault UI or the on-disk quota cache.
    internal static class QuotaData
    {
        internal static JavaScriptSerializer Serializer()
        {
            return new JavaScriptSerializer { MaxJsonLength = 1024 * 1024, RecursionLimit = 48 };
        }

        internal static string CachePath(string id, string client)
        {
            Guid guid;
            if (!Guid.TryParseExact(id, "D", out guid) || !AgentSessions.Known(client))
                throw new InvalidDataException("账号的客户端绑定无效。");
            return Path.Combine(Program.Home, "Quota", guid.ToString("D"), client + ".dat");
        }

        internal static QuotaReport Cached(string id, string client)
        {
            string path = CachePath(id, client);
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length > 128 * 1024) return null;
                byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
                return Serializer().Deserialize<QuotaReport>(Encoding.UTF8.GetString(plain));
            }
            catch (IOException) { return null; }
            catch (CryptographicException) { return null; }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        internal static void Save(string id, string client, QuotaReport report)
        {
            string path = CachePath(id, client);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            byte[] plain = Encoding.UTF8.GetBytes(Serializer().Serialize(report));
            try
            {
                File.WriteAllBytes(temp, ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
                if (!File.Exists(path))
                {
                    try { File.Move(temp, path); return; }
                    catch (IOException) { if (!File.Exists(path)) throw; }
                }
                File.Replace(temp, path, null);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        internal static IDictionary<string, object> Map(object obj)
        {
            return obj as IDictionary<string, object>;
        }

        internal static object Field(object obj, string name)
        {
            var map = Map(obj);
            object value;
            return map != null && map.TryGetValue(name, out value) ? value : null;
        }

        internal static string Text(object obj)
        {
            string value = obj as string;
            if (String.IsNullOrWhiteSpace(value)) return null;
            value = new String(value.Where(c => !Char.IsControl(c)).Take(100).ToArray()).Trim();
            return value.Length != 0 ? value : null;
        }

        private static double? Number(object value, double maximum)
        {
            if (!(value is int || value is long || value is decimal || value is double)) return null;
            double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return number >= 0 && number <= maximum ? (double?)number : null;
        }

        private static double? Remaining(object used)
        {
            double? value = Number(used, 100);
            return value.HasValue ? (double?)Math.Round(100 - value.Value, 3) : null;
        }

        private static long? Epoch(object value)
        {
            var seconds = Number(value, 253402300799);
            return seconds > 0 ? (long?)(seconds.Value * 1000) : null;
        }

        private static long? IsoTime(object value)
        {
            DateTimeOffset parsed;
            return value is string && DateTimeOffset.TryParse((string)value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out parsed) && parsed.Year >= 1970 ? (long?)parsed.ToUnixTimeMilliseconds() : null;
        }

        private static void Add(QuotaReport report, string model, string period, double? remaining, long? reset)
        {
            if (report.windows.Count >= 100 || (!remaining.HasValue && !reset.HasValue)) return;
            report.windows.Add(new QuotaWindow { model = model, period = period, remaining = remaining, reset = reset });
        }

        private static string Duration(object mins, string fallback)
        {
            var value = Number(mins, 525600);
            if (!value.HasValue || value <= 0 || value != Math.Floor(value.Value)) return fallback;
            int m = (int)value.Value;
            if (m % 1440 == 0) return (m / 1440) + " 天";
            return m % 60 == 0 ? (m / 60) + " 小时" : m + " 分钟";
        }

        private static void CodexBucket(QuotaReport report, string id, object bucket)
        {
            string label = Text(Field(bucket, "limitName")) ?? Text(Field(bucket, "limitId")) ?? Text(id) ?? "Codex";
            if (String.Equals(label, "codex", StringComparison.OrdinalIgnoreCase)) label = "Codex 共享额度";
            foreach (var key in new[] { "primary", "secondary" })
            {
                object window = Field(bucket, key);
                if (Map(window) == null) continue;
                Add(report, label, Duration(Field(window, "windowDurationMins"), key == "primary" ? "主要周期" : "次要周期"),
                    Remaining(Field(window, "usedPercent")), Epoch(Field(window, "resetsAt")));
            }
        }

        internal static QuotaReport Codex(object result)
        {
            var report = QuotaReport.Start("Codex 额度接口");
            var buckets = Map(Field(result, "rateLimitsByLimitId"));
            if (buckets != null)
                foreach (var bucket in buckets.OrderBy(p => p.Key, StringComparer.Ordinal))
                    CodexBucket(report, bucket.Key, bucket.Value);
            if (report.windows.Count == 0) CodexBucket(report, "codex", Field(result, "rateLimits"));
            return report;
        }

        internal static QuotaReport Claude(object status)
        {
            var report = QuotaReport.Start("Claude Code 状态栏");
            var limits = Map(Field(status, "rate_limits"));
            if (limits == null) return report;
            foreach (var pair in limits)
            {
                bool shortWindow = pair.Key == "five_hour";
                bool weekly = pair.Key == "seven_day" || pair.Key.StartsWith("seven_day_", StringComparison.Ordinal);
                if (!shortWindow && !weekly) continue;
                string model = pair.Key.StartsWith("seven_day_", StringComparison.Ordinal) ? Text(pair.Key.Substring(10)) : "Claude 共享额度";
                Add(report, model ?? "Claude", shortWindow ? "5 小时" : "7 天",
                    Remaining(Field(pair.Value, "used_percentage")), Epoch(Field(pair.Value, "resets_at")));
            }
            return report;
        }

        private static IEnumerable<object> Items(object value)
        {
            var items = value as IList;
            return items == null ? Enumerable.Empty<object>() : items.Cast<object>().Take(100);
        }

        internal static QuotaReport Antigravity(object result)
        {
            var report = QuotaReport.Start("Antigravity /usage");
            object command = Field(result, "command");
            if (!String.Equals(Field(result, "status") as string, "SUCCESS", StringComparison.OrdinalIgnoreCase)
                || Field(command, "name") as string != "usage") return report;
            foreach (var group in Items(Field(Field(command, "data"), "groups")))
            {
                string name = Text(Field(group, "name"));
                if (name == null || Object.Equals(Field(group, "enabled"), false)) continue;
                foreach (var bucket in Items(Field(group, "buckets")))
                {
                    if (Object.Equals(Field(bucket, "enabled"), false) || Object.Equals(Field(bucket, "disabled"), true)) continue;
                    string window = Text(Field(bucket, "window"));
                    string period = window == "5h" ? "5 小时" : window == "weekly" ? "7 天" :
                        (Text(Field(bucket, "name")) ?? window ?? "当前周期");
                    var fraction = Number(Field(bucket, "remaining_fraction"), 1);
                    Add(report, name, period, fraction.HasValue ? (double?)Math.Round(fraction.Value * 100, 3) : null,
                        IsoTime(Field(bucket, "reset_time")));
                }
            }
            return report;
        }

        internal static QuotaReport Web(string client, string text)
        {
            if ((client != "claude-web" && client != "chatgpt-web") || String.IsNullOrWhiteSpace(text) || text.Length > 16000)
                throw new InvalidDataException("请粘贴此账号官方用量页中的额度文字。");
            var report = QuotaReport.Start("官方用量页 · 手动摘录");
            string model = client == "claude-web" ? "Claude 共享额度" : "Codex 共享额度";
            string period = "当前周期";
            const string number = @"(?<![\d.\-])(?<n>\d{1,3}(?:\.\d+)?)\s*[%％]";
            const string direction = @"(?<d>remaining|left|available|used|consumed|utilized|剩余|可用|已用|已使用)";
            var pattern = new Regex(number + @"\s*" + direction + "|" + direction + @"\s*[:：]?\s*" + number,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            string previous = "";
            foreach (string raw in text.Replace("\r", "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.Length > 250) continue;
                if (Regex.IsMatch(line, @"context|上下文|cache|缓存|storage|存储|discount|折扣", RegexOptions.IgnoreCase))
                {
                    previous = "";
                    continue;
                }
                var match = pattern.Match(line);
                if (!match.Success && Regex.IsMatch(previous, @"^(?:[\d.%％\s]+|剩余|已用|已使用|used|remaining)$", RegexOptions.IgnoreCase))
                    match = pattern.Match(previous + " " + line);
                if (match.Success)
                {
                    double value;
                    if (!Double.TryParse(match.Groups["n"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value) || value > 100)
                        throw new InvalidDataException("额度百分比应在 0 至 100 之间。");
                    bool used = Regex.IsMatch(match.Groups["d"].Value, @"used|consumed|utilized|已", RegexOptions.IgnoreCase);
                    Add(report, model, period, used ? 100 - value : value, null);
                    previous = "";
                    continue;
                }
                if (Regex.IsMatch(line, @"reset|重置|刷新", RegexOptions.IgnoreCase))
                {
                    var stamp = Regex.Match(line, @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d\d:\d\d)");
                    if (report.windows.Count > 0 && stamp.Success) report.windows.Last().reset = IsoTime(stamp.Value);
                }
                else if (line.Length <= 75 && !Regex.IsMatch(line, @"[\d.]+\s*[%％]") && !line.Contains("@"))
                {
                    if (Regex.IsMatch(line, @"session|weekly|week|hour|daily|period|周期|小时|会话|周|每日", RegexOptions.IgnoreCase))
                        period = Text(line);
                    if (Regex.IsMatch(line, @"sonnet|opus|haiku|gpt-|codex|all models|所有模型", RegexOptions.IgnoreCase))
                        model = Text(line);
                }
                previous = line;
            }
            if (report.windows.Count == 0) throw new InvalidDataException("没有识别到额度，请包含带有「已用」或「剩余」的百分比。");
            return report;
        }
    }
}
