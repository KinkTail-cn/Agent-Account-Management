using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;

namespace KeyIsle
{
    // Read the same visible figures a user sees in their managed browser's official usage page.
    internal static class WebQuota
    {
        private delegate bool WindowVisitor(IntPtr hwnd, IntPtr unused);
        [DllImport("user32.dll")] private static extern bool EnumWindows(WindowVisitor visitor, IntPtr unused);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CommandLineToArgvW(string line, out int length);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);

        private static bool OwnsProfile(string line, string profile)
        {
            if (String.IsNullOrEmpty(line)) return false;
            int length;
            var args = CommandLineToArgvW(line, out length);
            if (args == IntPtr.Zero) return false;
            try
            {
                bool match = false;
                for (int i = 1; i < length; i++)
                {
                    string arg = Marshal.PtrToStringUni(Marshal.ReadIntPtr(args, i * IntPtr.Size));
                    if (arg.StartsWith("--type=", StringComparison.OrdinalIgnoreCase)) return false;
                    if (String.Equals(arg, "--user-data-dir=" + profile, StringComparison.OrdinalIgnoreCase)) match = true;
                }
                return match;
            }
            finally { LocalFree(args); }
        }

        internal static List<IntPtr> WindowsFor(string id, string client)
        {
            bool google = AntigravityLogin.IsGoogle(client);
            var profile = Path.Combine(google ? AntigravityLogin.Active(id, client) : Path.Combine(AgentSessions.ProfileRoot(id), client), "UserData");
            var result = new List<IntPtr>();
            if (!Directory.Exists(profile)) return result;
            var pids = new HashSet<uint>();
            // Limit the WMI query to this record's generated GUID, then check the full argument exactly.
            string image = google ? (client == "antigravity-app" ? "Name='Antigravity.exe'" : "Name='Antigravity IDE.exe'") : "(Name='chrome.exe' OR Name='msedge.exe')";
            string query = "SELECT ProcessId, CommandLine FROM Win32_Process WHERE " + image + " AND CommandLine LIKE '%" + Guid.Parse(id).ToString("D") + "%'";
            using (var search = new ManagementObjectSearcher(query))
            using (var records = search.Get())
                foreach (ManagementObject row in records)
                    using (row)
                        if (OwnsProfile(row["CommandLine"] as string, profile)) pids.Add((uint)row["ProcessId"]);
            if (pids.Count == 0) return result;
            EnumWindows((handle, unused) => {
                uint pid;
                GetWindowThreadProcessId(handle, out pid);
                if (pids.Contains(pid) && IsWindowVisible(handle) && !IsIconic(handle)) result.Add(handle);
                return result.Count < 12;
            }, IntPtr.Zero);
            return result;
        }

        internal static string GoogleWindowIdentity(string id, string client, string expected)
        {
            if (String.IsNullOrEmpty(expected) || !expected.Contains("@")) return "unknown";
            foreach (var handle in WindowsFor(id, client))
            {
                var root = AutomationElement.FromHandle(handle);
                var items = root.FindAll(TreeScope.Descendants, new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)));
                foreach (AutomationElement item in items)
                {
                    string label = item.Current.Name ?? "";
                    if (label.Length > 200 || !label.Contains("@")) continue;
                    // Only accept a signed-in account label, never an address from files, chat, or the chooser.
                    var match = System.Text.RegularExpressions.Regex.Match(label,
                        @"^(?:signed in as|logged in as|current account|google account|当前(?:登录)?账号|已登录为|Google 帐号|Google 账号)\s*[:：]?\s*(?:[^@\s]+\s+){0,4}[\(<]?(?<email>[\w.!%+\-]+@[\w.\-]+)(?:[\)>]|\s|$)",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (match.Success) return match.Groups["email"].Value.Equals(expected.Trim(), StringComparison.OrdinalIgnoreCase) ? "in" : "mismatch";
                }
            }
            return "unknown";
        }

        private static bool AtUsagePage(AutomationElement root, string client, ref bool atLogin)
        {
            bool claude = client == "claude-web";
            var toolbars = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ToolBar));
            foreach (AutomationElement bar in toolbars)
            {
                // Browser chrome only. Toolbars inside the actual website cannot attest to its URL.
                bool inDocument = false;
                for (var parent = bar; parent != null && !parent.Equals(root); parent = TreeWalker.ControlViewWalker.GetParent(parent))
                    if (parent.Current.ControlType == ControlType.Document) { inDocument = true; break; }
                if (inDocument) continue;
                var fields = bar.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
                foreach (AutomationElement field in fields)
                {
                    object value;
                    if (!field.TryGetCurrentPattern(ValuePattern.Pattern, out value)) continue;
                    string address = ((ValuePattern)value).Current.Value.Trim();
                    Uri uri;
                    if (address.Length > 2048 || !Uri.TryCreate(address.Contains("://") ? address : "https://" + address, UriKind.Absolute, out uri)
                        || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0) continue;
                    string path = uri.AbsolutePath.TrimEnd('/');
                    if (claude && uri.Host == "claude.ai" && path == "/settings/usage") return true;
                    if (!claude && uri.Host == "chatgpt.com" && path == "/codex/settings/usage") return true;
                    if ((claude && uri.Host == "claude.ai" && path == "/login") ||
                        (!claude && ((uri.Host == "chatgpt.com" && path == "/auth/login") || (uri.Host == "auth.openai.com" && path == "/log-in"))))
                        atLogin = true;
                }
            }
            return false;
        }

        internal static QuotaReport Read(string id, string client, CancellationToken token)
        {
            bool atLogin = false;
            var timer = Stopwatch.StartNew();
            foreach (IntPtr handle in WindowsFor(id, client))
            {
                token.ThrowIfCancellationRequested();
                if (timer.ElapsedMilliseconds > 6500) break;
                try
                {
                    var root = AutomationElement.FromHandle(handle);
                    if (!AtUsagePage(root, client, ref atLogin)) continue;
                    var doc = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
                    if (doc == null) continue;
                    var text = new StringBuilder();
                    var labels = doc.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
                    foreach (AutomationElement label in labels)
                    {
                        if (text.Length > 15000 || timer.ElapsedMilliseconds > 6500) break;
                        string item = label.Current.Name;
                        if (!String.IsNullOrWhiteSpace(item) && item.Length <= 250) text.AppendLine(item);
                    }
                    if (text.Length == 0) continue;
                    var result = QuotaData.Web(client, text.ToString());
                    result.source = "官方用量页";
                    return result;
                }
                catch (ElementNotAvailableException) { }
                catch (InvalidDataException) { }
            }
            if (atLogin) throw new QuotaException("login", "请先在这个账号的官方网页中登录。");
            return null;
        }
    }
}
