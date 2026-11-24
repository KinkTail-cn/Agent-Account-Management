using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace KeyIsle
{
    internal sealed class GoogleLoginState
    {
        public string phase, message, attemptId;
        public long expiresAt;
        public bool hasActive;
    }

    internal sealed class AntigravityLogin : IDisposable
    {
        private sealed class Attempt
        {
            internal string Id, Client, Login, Folder, BrowserFolder, Generation, Url, Phase = "starting", Message;
            internal bool DifferentIdentity;
            internal long CodeDeadline, VerificationStarted;
            internal readonly Stopwatch Clock = Stopwatch.StartNew();
            internal LoginTerminal Terminal;
            internal CancellationTokenSource Stop = new CancellationTokenSource();
        }

        private readonly Dictionary<string, Attempt> pending = new Dictionary<string, Attempt>();
        private readonly object gate = new object();
        private readonly Func<string> findCli;
        private readonly Action<string, string> openBrowser;

        internal AntigravityLogin() : this(AgentSessions.FindQuotaCli, AgentSessions.OpenGoogleBrowser) { }
        internal AntigravityLogin(Func<string> findCli, Action<string, string> openBrowser)
        {
            this.findCli = findCli;
            this.openBrowser = openBrowser;
        }

        internal static bool IsGoogle(string client) { return client == "antigravity-app" || client == "antigravity-ide"; }

        internal static string Base(string id, string client)
        {
            if (!IsGoogle(client)) throw new InvalidDataException("请选择 Antigravity 账号。");
            return Path.Combine(AgentSessions.ProfileRoot(id), client);
        }

        internal static string Active(string id, string client)
        {
            string root = Base(id, client);
            string pointer = Path.Combine(root, "active-session");
            if (!File.Exists(pointer)) return root;
            Guid g;
            if (!Guid.TryParseExact(File.ReadAllText(pointer), "D", out g))
                throw new InvalidDataException("登录环境无法识别，请重新登录这个账号。");
            return Path.Combine(root, "Sessions", g.ToString("D"));
        }

        internal static bool HasBoundSession(Account account)
        {
            return IsGoogle(account.agentClient) && File.Exists(Path.Combine(Base(account.id, account.agentClient), "active-session"));
        }

        private static bool InProgress(Attempt attempt)
        {
            return attempt.Phase == "starting" || attempt.Phase == "authorizing" || attempt.Phase == "verifying";
        }

        internal GoogleLoginState Begin(Account account, string retryAttempt = null)
        {
            string root = Base(account.id, account.agentClient);
            string binary = findCli();
            if (binary == null) throw new InvalidOperationException("请先安装官方 Antigravity CLI，以开启独立登录。");
            string key = account.id + "/" + account.agentClient;
            lock (gate)
            {
                Attempt old;
                pending.TryGetValue(key, out old);
                if (retryAttempt != null && (old == null || old.Generation != retryAttempt || old.Login != account.login))
                    throw new InvalidOperationException("登录请求已更换，请重新打开此账号的登录窗口。");
                if (old != null)
                {
                    if (old.Login == account.login && InProgress(old)) return State(old);
                    old.Stop.Cancel();
                }
                string generation = Guid.NewGuid().ToString("D");
                var attempt = new Attempt {
                    Id = account.id, Client = account.agentClient, Login = account.login, Generation = generation,
                    Folder = Directory.CreateDirectory(Path.Combine(root, "Sessions", generation)).FullName
                };
                attempt.BrowserFolder = retryAttempt != null && old.Phase != "ready" ? old.BrowserFolder : attempt.Folder;
                pending[key] = attempt;
                Task.Run(() => Authorize(binary, attempt));
                return State(attempt);
            }
        }

        private void Authorize(string binary, Attempt a)
        {
            try
            {
                var start = AgentSessions.GoogleChild(binary, a.Folder, true);
                start.Arguments = "-p /usage --output-format json";
                var transcript = new LoginOutput(line => Observe(a, line));
                a.Stop.Token.ThrowIfCancellationRequested();
                using (var terminal = LoginTerminal.Start(start, transcript.Receive))
                using (a.Stop.Token.Register(terminal.Stop))
                {
                    lock (gate) a.Terminal = terminal;
                    while (!terminal.Wait(300))
                    {
                        a.Stop.Token.ThrowIfCancellationRequested();
                        lock (gate)
                        {
                            long now = a.Clock.ElapsedMilliseconds;
                            if (a.Phase == "verifying" && now - a.VerificationStarted >= 60000)
                                throw new LoginFailure("error", "Antigravity 验证超时。请检查网络后重新获取授权码。");
                            if ((a.Phase == "authorizing" && a.CodeDeadline > 0 && now >= a.CodeDeadline + 3000) || now >= 300000)
                                throw LoginFailure.Expired();
                        }
                    }
                    uint exit = terminal.Complete();
                    a.Stop.Token.ThrowIfCancellationRequested();
                    if (exit != 0 || transcript.Report == null)
                        throw transcript.Failure ?? new LoginFailure("error", "Antigravity 未确认本次登录。请重新获取授权码。");
                    lock (gate)
                    {
                        a.Stop.Token.ThrowIfCancellationRequested();
                        if (a.Url == null) throw new LoginFailure("error", "未收到本次账号专属的 Google 授权请求。");
                        if (a.DifferentIdentity) throw new LoginFailure("error", "Google 返回了其他账号的邮箱，请重新选择本次要登录的账号。");
                        Publish(a, transcript.Report);
                    }
                }
            }
            catch (Exception error)
            {
                lock (gate)
                {
                    if (InProgress(a))
                    {
                        var failure = error as LoginFailure;
                        a.Phase = a.Stop.IsCancellationRequested ? "cancelled" : failure == null ? "error" : failure.Phase;
                        if (failure != null) a.Message = failure.Message;
                        else a.Message = error is EntryPointNotFoundException
                            ? "登录终端需要 Windows 10 1809 或更新版本。"
                            : "登录终端未能完成本次请求。请重新获取授权码。";
                    }
                }
            }
            finally { lock (gate) { a.Terminal = null; a.Url = null; } }
        }

        private static void Publish(Attempt a, QuotaReport report)
        {
            File.WriteAllText(Path.Combine(a.Folder, "quota-enabled"), "1");
            string root = Base(a.Id, a.Client);
            string next = Path.Combine(root, "active-" + a.Generation + ".tmp");
            string active = Path.Combine(root, "active-session");
            try
            {
                File.WriteAllText(next, a.Generation);
                if (File.Exists(active)) File.Replace(next, active, null);
                else File.Move(next, active);
            }
            finally { if (File.Exists(next)) File.Delete(next); }
            a.Phase = "ready";
            a.Url = null;
            if (report.windows.Count != 0) QuotaData.Save(a.Id, a.Client, report);
        }

        private sealed class LoginFailure : Exception
        {
            internal readonly string Phase;
            internal LoginFailure(string phase, string message) : base(message) { Phase = phase; }
            internal static LoginFailure Expired()
            {
                return new LoginFailure("expired", "本次授权请求已过期。请重新获取授权码，使用新页面显示的码。");
            }
        }

        // A terminal combines stderr, echoed input and the JSON /usage result. Retain only the result.
        private sealed class LoginOutput
        {
            private readonly Action<string> observe;
            private StringBuilder json;
            internal LoginFailure Failure;
            internal QuotaReport Report;

            internal LoginOutput(Action<string> observe) { this.observe = observe; }

            internal void Receive(string line)
            {
                string text = line.Trim();
                if (json != null || text.StartsWith("{", StringComparison.Ordinal))
                {
                    if (json == null) json = new StringBuilder();
                    if (line.Length + json.Length > 1024 * 1024) { json = null; return; }
                    json.Append(line);
                    if (!text.EndsWith("}", StringComparison.Ordinal)) return;
                    object result;
                    try { result = QuotaData.Serializer().DeserializeObject(json.ToString()); }
                    catch (ArgumentException) { return; }
                    catch (InvalidOperationException) { return; }
                    json = null;
                    if (QuotaData.Field(result, "status") as string == "SUCCESS" &&
                        QuotaData.Field(QuotaData.Field(result, "command"), "name") as string == "usage")
                        Report = QuotaData.Antigravity(result);
                    else Classify(QuotaData.Field(result, "error") as string);
                    return;
                }
                if (text.StartsWith("error:", StringComparison.OrdinalIgnoreCase)) Classify(text);
                observe(line);
            }

            private void Classify(string error)
            {
                if (String.IsNullOrEmpty(error)) return;
                if (Regex.IsMatch(error, @"invalid_grant|invalid (?:authorization |verification )?code|code (?:has )?(?:expired|already used)", RegexOptions.IgnoreCase))
                    Failure = new LoginFailure("error", "Google 未接受这个授权码，可能已过期或已使用。请重新获取授权码。");
                else if (Failure != null) return;
                else if (Regex.IsMatch(error, @"authentication timed out|timed out waiting for (?:authentication|authorization)", RegexOptions.IgnoreCase))
                    Failure = LoginFailure.Expired();
                else if (Regex.IsMatch(error, @"access_denied|access denied|consent denied", RegexOptions.IgnoreCase))
                    Failure = new LoginFailure("error", "Google 未授予本次登录权限。可重新发起授权。");
                else if (Regex.IsMatch(error, @"dial tcp|connection (?:refused|reset)|TLS handshake|i/o timeout|no such host|context deadline exceeded|proxyconnect", RegexOptions.IgnoreCase))
                    Failure = new LoginFailure("error", "连接授权服务失败。请检查网络后重新获取授权码。");
            }
        }

        internal static string AccountChooser(string line, string login)
        {
            // Accept only a fresh OAuth request printed by this attempt's official child process.
            var match = Regex.Match(line, @"https://accounts\.google\.com/[^\s<>""\x1b]+", RegexOptions.IgnoreCase);
            if (!match.Success || match.Length > 12000) return null;
            Uri url;
            if (!Uri.TryCreate(match.Value, UriKind.Absolute, out url) || url.Scheme != "https" || url.Host != "accounts.google.com"
                || !url.IsDefaultPort || url.UserInfo.Length > 0 || (url.AbsolutePath != "/o/oauth2/v2/auth" && url.AbsolutePath != "/o/oauth2/auth")) return null;
            var query = HttpUtility.ParseQueryString(url.Query);
            if (query["response_type"] != "code" || String.IsNullOrEmpty(query["client_id"]) || String.IsNullOrEmpty(query["redirect_uri"])) return null;
            var prompts = new HashSet<string>((query["prompt"] ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            prompts.Remove("none");
            prompts.Add("select_account");
            var preserved = url.Query.TrimStart('?').Split('&').Where(pair => {
                string name = HttpUtility.UrlDecode(pair.Split('=')[0]);
                return name != "prompt" && name != "login_hint";
            }).ToList();
            preserved.Add("prompt=" + Uri.EscapeDataString(String.Join(" ", prompts)));
            if (!String.IsNullOrWhiteSpace(login) && login.Contains("@")) preserved.Add("login_hint=" + Uri.EscapeDataString(login.Trim()));
            return new UriBuilder(url) { Query = String.Join("&", preserved) }.Uri.AbsoluteUri;
        }

        private void Observe(Attempt a, string line)
        {
            var timeout = Regex.Match(line, @"Waiting for authentication \(timeout (\d{1,4})s\)", RegexOptions.IgnoreCase);
            if (timeout.Success)
            {
                lock (gate) a.CodeDeadline = a.Clock.ElapsedMilliseconds + Int32.Parse(timeout.Groups[1].Value) * 1000L;
                return;
            }
            var identity = Regex.Match(line, @"^(?:signed in as|logged in as|authenticated as)\s*[:：]?\s*(?<email>[\w.!%+\-]+@[\w.\-]+)\s*$",
                RegexOptions.IgnoreCase);
            if (identity.Success && a.Login.Contains("@") && !a.Login.Trim().Equals(identity.Groups["email"].Value, StringComparison.OrdinalIgnoreCase))
                lock (gate) a.DifferentIdentity = true;
            string url = AccountChooser(line, a.Login);
            if (url == null) return;
            lock (gate)
            {
                if (a.Phase != "starting" || a.Stop.IsCancellationRequested) return;
                a.Url = url;
                try { openBrowser(a.BrowserFolder, url); a.Phase = "authorizing"; }
                catch { a.Phase = "error"; a.Message = "无法打开账号独立浏览器，请检查 Chrome 或 Edge。"; a.Stop.Cancel(); }
            }
        }

        internal GoogleLoginState Inspect(Account account)
        {
            lock (gate)
            {
                Attempt attempt;
                bool active = HasBoundSession(account);
                var result = pending.TryGetValue(account.id + "/" + account.agentClient, out attempt)
                    ? State(attempt) : new GoogleLoginState { phase = active ? "ready" : "idle" };
                result.hasActive = active;
                return result;
            }
        }

        internal GoogleLoginState Act(Account account, string generation, string action, string code)
        {
            lock (gate)
            {
                Attempt a;
                if (!pending.TryGetValue(account.id + "/" + account.agentClient, out a)) throw new InvalidOperationException("请先开始本次登录。");
                if (a.Generation != generation) throw new InvalidOperationException("此操作属于上一轮登录，请使用新窗口中的授权码。");
                if (action == "cancel")
                {
                    if (InProgress(a)) { a.Phase = "cancelled"; a.Stop.Cancel(); }
                    return State(a);
                }
                if (action == "code" && a.Phase == "verifying") return State(a);
                if (a.Phase != "authorizing" || a.Url == null) throw new InvalidOperationException("此授权请求已结束。");
                if (a.CodeDeadline > 0 && a.Clock.ElapsedMilliseconds >= a.CodeDeadline)
                {
                    a.Phase = "expired";
                    a.Message = LoginFailure.Expired().Message;
                    a.Stop.Cancel();
                    return State(a);
                }
                if (action == "browser") { openBrowser(a.BrowserFolder, a.Url); return State(a); }
                code = (code ?? "").Trim();
                if (action != "code" || code.Length == 0 || code.Length > 4096 || !Regex.IsMatch(code, @"\A[A-Za-z0-9._~+/=\-]+\z"))
                    throw new InvalidDataException("请粘贴 Google 页面显示的完整授权码。");
                if (a.Terminal == null || a.Terminal.Wait(0)) throw new InvalidOperationException("登录终端已结束，请重新获取授权码。");
                try
                {
                    a.Terminal.TypeCode(code);
                    a.VerificationStarted = a.Clock.ElapsedMilliseconds;
                    a.Phase = "verifying";
                }
                catch (IOException)
                {
                    a.Phase = "error";
                    a.Message = "授权码未送达登录终端，请重新获取授权码。";
                    a.Stop.Cancel();
                }
                return State(a);
            }
        }

        private static GoogleLoginState State(Attempt a)
        {
            return new GoogleLoginState {
                attemptId = a.Generation, phase = a.Phase, message = a.Message,
                expiresAt = a.Phase == "authorizing" && a.CodeDeadline > 0
                    ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + a.CodeDeadline - a.Clock.ElapsedMilliseconds : 0
            };
        }

        internal void Forget(Account account)
        {
            lock (gate)
            {
                Attempt a;
                string key = account.id + "/" + account.agentClient;
                if (pending.TryGetValue(key, out a)) { a.Stop.Cancel(); pending.Remove(key); }
            }
        }

        public void Dispose()
        {
            lock (gate) foreach (var a in pending.Values) a.Stop.Cancel();
        }
    }
}
