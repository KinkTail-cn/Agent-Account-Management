using System;
using System.IO;
using System.Linq;
using System.Text;

namespace KeyIsle
{
    internal static class ClaudeQuota
    {
        internal static string LaunchArguments(string id)
        {
            string root = Path.Combine(AgentSessions.ProfileRoot(id), "claude-code");
            string settings = Path.Combine(root, "key-isle-statusline.json");
            // The shell only expands the fixed variable; the executable path is passed as environment data.
            string command = "\"$KEY_ISLE_STATUS_EXE\" --claude-quota " + Guid.Parse(id).ToString("D");
            var config = new { statusLine = new { type = "command", command = command } };
            string contents = QuotaData.Serializer().Serialize(config);
            if (!File.Exists(settings) || File.ReadAllText(settings) != contents)
                File.WriteAllText(settings, contents, new UTF8Encoding(false));
            return "--settings \"" + settings + "\"";
        }

        // Invoked by Claude Code's documented status-line interface, not by the WebView.
        internal static void Receive(string id)
        {
            Guid parsed;
            if (!Guid.TryParseExact(id, "D", out parsed)) return;
            var buffer = new char[4096];
            var text = new StringBuilder();
            using (var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8))
            {
                int count;
                while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (text.Length + count > 256 * 1024) return;
                    text.Append(buffer, 0, count);
                }
            }
            var payload = QuotaData.Serializer().DeserializeObject(text.ToString());
            var report = QuotaData.Claude(payload);
            // A newly opened session often has no limits before its first model response.
            string model = QuotaData.Text(QuotaData.Field(QuotaData.Field(payload, "model"), "display_name")) ?? "Claude Code";
            if (report.windows.Count > 0) QuotaData.Save(id, "claude-code", report);
            string line = String.Join(" · ", report.windows.Where(w => w.remaining.HasValue)
                .Select(w => w.period + " " + w.remaining.Value.ToString("0.#") + "% 剩余"));
            using (var writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)))
                writer.WriteLine(line.Length == 0 ? model : model + " · " + line);
        }
    }
}
