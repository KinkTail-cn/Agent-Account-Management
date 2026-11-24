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

        internal static List<IntPtr> WindowsFor(string id, string client, bool includeMinimized = false)
        {
            bool google = AntigravityLogin.IsGoogle(client);
            var profile = Path.Combine(google ? AntigravityLogin.DesktopProfile(id, client) : Path.Combine(AgentSessions.ProfileRoot(id), client), "UserData");
            var result = new List<IntPtr>();
            if (!Directory.Exists(profile)) return result;
            var pids = new HashSet<uint>();
            // Limit the WMI query to this record's generated GUID, then check the full argument exactly.
            string image = google ? (client == "antigravity-app" ? "Name='Antigravity.exe'" : "Name='Antigravity IDE.exe'") : "(Name='chrome.exe' OR Name='msedge.exe')";
            string expectedImage = google ? AgentSessions.InstalledPath(client) : null;
            if (google && expectedImage == null) return result;
            string query = "SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE " + image + " AND CommandLine LIKE '%" + Guid.Parse(id).ToString("D") + "%'";
            using (var search = new ManagementObjectSearcher(query))
            using (var records = search.Get())
                foreach (ManagementObject row in records)
                    using (row)
                        if ((!google || String.Equals(row["ExecutablePath"] as string, expectedImage, StringComparison.OrdinalIgnoreCase)) &&
                            OwnsProfile(row["CommandLine"] as string, profile)) pids.Add((uint)row["ProcessId"]);
            if (pids.Count == 0) return result;
            EnumWindows((handle, unused) => {
                uint pid;
                GetWindowThreadProcessId(handle, out pid);
                if (pids.Contains(pid) && IsWindowVisible(handle) && (includeMinimized || !IsIconic(handle))) result.Add(handle);
                return result.Count < 12;
            }, IntPtr.Zero);
            return result;
        }

        private static readonly string[] GoogleButtons = {
            "Continue with Google", "Sign in with Google", "使用 Google 继续", "使用 Google 登录", "使用 Google 账号继续"
        };
        private static readonly string[] Welcome = {
            "Welcome to Antigravity!", "Welcome to Antigravity", "欢迎使用 Antigravity！", "欢迎使用 Antigravity"
        };

        private static Condition Named(string[] names, ControlType type)
        {
            return new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, type),
                new OrCondition(names.Select(name => (Condition)new PropertyCondition(AutomationElement.NameProperty, name,
                    PropertyConditionFlags.IgnoreCase)).ToArray()));
        }

        private static bool HasName(AutomationElement root, string name)
        {
            return root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty,
                name, PropertyConditionFlags.IgnoreCase)) != null;
        }

        internal static string GoogleWindowIdentity(string id, string client, string expected, bool startSignIn = false)
        {
            var windows = WindowsFor(id, client);
            if (windows.Count == 0) return "closed";
            foreach (var handle in windows)
            {
                try
                {
                    var result = InspectGoogleWindow(AutomationElement.FromHandle(handle), client, expected, startSignIn);
                    if (result != "unknown") return result;
                }
                catch (ElementNotAvailableException) { }
                catch (ElementNotEnabledException) { }
                catch (System.Runtime.InteropServices.COMException) { }
            }
            return "unknown";
        }

        internal static string AwaitDesktop(Account account, bool login)
        {
            var watch = Stopwatch.StartNew();
            var observed = "closed";
            do
            {
                observed = GoogleWindowIdentity(account.id, account.agentClient, account.login, account.agentClient == "antigravity-app");
                if (observed != "closed" && observed != "signInLoading" &&
                    (observed != "unknown" || !login || account.agentClient != "antigravity-app")) return observed;
                Thread.Sleep(400);
            } while (watch.ElapsedMilliseconds < (login ? 24000 : 16000));
            if (observed == "closed") throw new InvalidOperationException("目标账号的窗口尚未就绪，请稍后再打开客户端。");
            return observed == "signInLoading" ? "out" : observed;
        }

        internal static string InspectGoogleWindow(AutomationElement root, string client, string expected, bool startSignIn)
        {
            if (client == "antigravity-app" && root.FindFirst(TreeScope.Descendants, Named(Welcome, ControlType.Text)) != null)
            {
                var button = root.FindFirst(TreeScope.Descendants, Named(GoogleButtons, ControlType.Button));
                if (button != null && !button.Current.IsOffscreen)
                {
                    if (!startSignIn) return "out";
                    if (!button.Current.IsEnabled) return "signInLoading";
                    object pattern;
                    if (button.TryGetCurrentPattern(InvokePattern.Pattern, out pattern))
                    {
                        ((InvokePattern)pattern).Invoke();
                        return "signInStarted";
                    }
                    return "signInLoading";
                }
            }
            if (client == "antigravity-app")
            {
                if (HasName(root, "Let's tailor the experience for you. What best describes your work?") &&
                    HasName(root, "Software Engineering") && HasName(root, "Skip")) return "onboarding";
                if (HasName(root, "Google Antigravity Terms of Service") &&
                    HasName(root, "Google Privacy Policy") && HasName(root, "Continue")) return "privacy";
            }
            if (!String.IsNullOrWhiteSpace(expected) && expected.Contains("@"))
            {
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
