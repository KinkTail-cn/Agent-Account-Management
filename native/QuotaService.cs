using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace KeyIsle
{
    internal sealed class QuotaException : Exception
    {
        public readonly string State;
        public QuotaException(string state, string message) : base(message) { State = state; }
    }

    internal sealed class QuotaReply
    {
        public string state, message, auth = "unknown";
        public string quotaAuth;
        public QuotaReport report;
        public long checkedAt;
    }

    internal sealed class QuotaService : IDisposable
    {
        private readonly SemaphoreSlim slots = new SemaphoreSlim(2);
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly Dictionary<string, Task<QuotaReply>> pending = new Dictionary<string, Task<QuotaReply>>();
        private readonly Dictionary<string, QuotaReply> recent = new Dictionary<string, QuotaReply>();

        // Access to these two dictionaries stays on the application's UI thread.
        internal async Task<QuotaReply> Read(string id, string client, string login, bool refresh)
        {
            string key = client + "/" + id + "/" + login;
            Task<QuotaReply> task;
            if (pending.TryGetValue(key, out task)) return await task;
            QuotaReply previous;
            if (recent.TryGetValue(key, out previous))
            {
                long age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - previous.checkedAt;
                long ttl = previous.state == "ready" ? 180000 : 30000;
                if (!refresh && age >= 0 && age < ttl) return previous;
            }
            task = ReadBounded(id, client, login, refresh, lifetime.Token);
            pending[key] = task;
            try { return recent[key] = await task; }
            finally { pending.Remove(key); }
        }

        private async Task<QuotaReply> ReadBounded(string id, string client, string login, bool refresh, CancellationToken token)
        {
            await slots.WaitAsync(token);
            try { return await Task.Run(() => Query(id, client, login, refresh, token), token); }
            finally { slots.Release(); }
        }

        private static QuotaReply Query(string id, string client, string login, bool refresh, CancellationToken token)
        {
            var reply = new QuotaReply { report = QuotaData.Cached(id, client) };
            bool google = AntigravityLogin.IsGoogle(client);
            if (google)
            {
                try { reply.auth = "desktop-" + WebQuota.GoogleWindowIdentity(id, client, login); }
                catch (Exception) { reply.auth = "desktop-unknown"; }
            }
            try
            {
                QuotaReport report;
                switch (client)
                {
                    case "claude-web":
                    case "chatgpt-web":
                        reply.auth = "web";
                        if (refresh)
                        {
                            var captured = WebQuota.Read(id, client, token);
                            if (captured != null)
                            {
                                reply.auth = "in";
                                QuotaData.Save(id, client, captured);
                                report = captured;
                                break;
                            }
                            throw new QuotaException("web", "请打开此账号的官方用量页，待数字显示后刷新，也可摘录页面文字。");
                        }
                        if (reply.report == null) throw new QuotaException("web", "打开此账号的官方用量页，待数字显示后刷新。");
                        report = reply.report;
                        break;
                    case "claude-code":
                        ReadClaudeStatus(id, login, reply, token);
                        if (reply.report == null)
                            throw new QuotaException("waiting", "从「Agent 切换」重新打开 Claude Code。支持的订阅会在首次模型响应后上报额度。");
                        report = reply.report;
                        break;
                    case "antigravity-ide":
                    case "antigravity-app":
                        if (!File.Exists(Path.Combine(AntigravityLogin.Active(id, client), "quota-enabled")))
                            throw new QuotaException("link", "可为此账号授权额度查询。");
                        if (!refresh && reply.report == null)
                            throw new QuotaException("waiting", "完成额度授权后，请点击刷新。");
                        report = ReadAntigravity(id, client, token);
                        if (report.windows.Count > 0) reply.quotaAuth = "authorized";
                        break;
                    case "chatgpt-desktop":
                    case "codex-cli":
                        report = ReadCodex(id, client, login, reply, token);
                        break;
                    default: throw new QuotaException("unavailable", "此账号尚未绑定客户端。");
                }
                if (report.windows.Count == 0) throw new QuotaException("empty", "服务商此次没有返回可显示的额度。");
                if (client != "claude-code" && !client.EndsWith("-web")) QuotaData.Save(id, client, report);
                reply.report = report;
                reply.state = "ready";
            }
            catch (QuotaException error)
            {
                reply.state = error.State;
                reply.message = error.Message;
                if (google) reply.quotaAuth = error.State == "link" || error.State == "login" || error.State == "reauth" ? "unlinked" : "unknown";
                else
                {
                    if (error.State == "login") reply.auth = "out";
                    if (error.State == "reauth") reply.auth = "expired";
                    if (error.State == "first") reply.auth = "first";
                    if (error.State == "mismatch") reply.auth = "mismatch";
                }
                if (error.State == "login" || error.State == "reauth" || error.State == "mismatch" || error.State == "plan" || error.State == "first")
                {
                    reply.report = null;
                    try { File.Delete(QuotaData.CachePath(id, client)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                reply.state = "error";
                reply.message = "暂时无法读取额度，请检查客户端登录状态和网络后刷新。";
            }
            reply.checkedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return reply;
        }

        private static void CheckIdentity(string expected, object identity)
        {
            string email = QuotaData.Text(QuotaData.Field(identity, "email"));
            if (email != null && !String.IsNullOrEmpty(expected) && expected.Contains("@")
                && !email.Equals(expected.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new QuotaException("mismatch", "客户端登录邮箱与这条账号记录不符，请核对登录身份。");
        }

        private static void ReadClaudeStatus(string id, string login, QuotaReply reply, CancellationToken token)
        {
            var start = AgentSessions.QuotaCommand(id, "claude-code");
            start.Arguments = "auth status";
            var identity = QuotaData.Serializer().DeserializeObject(Output(start, 9, token));
            object loggedIn = QuotaData.Field(identity, "loggedIn");
            if (Object.Equals(loggedIn, false)) throw new QuotaException("login", "请先在 Claude Code 中登录此账号。");
            if (!Object.Equals(loggedIn, true)) throw new QuotaException("error", "Claude Code 暂未返回登录状态。");
            CheckIdentity(login, identity);
            reply.auth = "in";
        }

        private static QuotaReport ReadCodex(string id, string client, string login, QuotaReply reply, CancellationToken token)
        {
            var start = AgentSessions.QuotaCommand(id, client);
            start.Arguments = "app-server --listen stdio://";
            return Run(start, 14, token, process => {
                Rpc(process, 1, "initialize", new { clientInfo = new { name = "key_isle", title = Program.DisplayName, version = typeof(Program).Assembly.GetName().Version.ToString(3) } });
                SendRpcLine(process, "{\"method\":\"initialized\"}");
                var identity = Rpc(process, 2, "account/read", new { refreshToken = false });
                object account = QuotaData.Field(identity, "account");
                if (account == null) throw new QuotaException("login", "请先在这个账号的客户端登录 ChatGPT，再刷新额度。");
                CheckIdentity(login, account);
                reply.auth = "in";
                string type = QuotaData.Field(account, "type") as string;
                if (String.Equals(type, "apiKey", StringComparison.OrdinalIgnoreCase))
                    throw new QuotaException("plan", "此环境使用 API 密钥，Codex 订阅额度仅适用于 ChatGPT 登录。");
                return QuotaData.Codex(Rpc(process, 3, "account/rateLimits/read", null));
            });
        }

        private static object Rpc(Process process, int id, string method, object parameters)
        {
            var json = QuotaData.Serializer();
            var request = new Dictionary<string, object> { { "id", id }, { "method", method } };
            if (parameters != null) request["params"] = parameters;
            SendRpcLine(process, json.Serialize(request));
            for (int i = 0; i < 60; i++)
            {
                string line = BoundedLine(process.StandardOutput);
                if (line == null) break;
                var message = json.DeserializeObject(line);
                if (!Object.Equals(QuotaData.Field(message, "id"), id)) continue;
                object error = QuotaData.Field(message, "error");
                if (error == null) return QuotaData.Field(message, "result");
                string reason = QuotaData.Field(error, "message") as string ?? "";
                if (Regex.IsMatch(reason, "authenticat|not logged in|unauthorized|sign.in required|\\b401\\b", RegexOptions.IgnoreCase))
                    throw new QuotaException("reauth", "此账号需要重新登录 ChatGPT。");
                if (Regex.IsMatch(reason, "429|too many requests", RegexOptions.IgnoreCase))
                    throw new QuotaException("error", "查询过于频繁，请稍后刷新。");
                break;
            }
            throw new QuotaException("error", "Codex 没有返回额度，请检查网络和 CLI 版本。");
        }

        private static void SendRpcLine(Process process, string json)
        {
            // The app-server protocol is UTF-8 even when Windows uses another console code page.
            byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");
            var stream = process.StandardInput.BaseStream;
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        private static string BoundedLine(StreamReader reader)
        {
            var line = new StringBuilder();
            int ch;
            while ((ch = reader.Read()) >= 0 && ch != '\n')
            {
                if (line.Length >= 1024 * 1024) throw new InvalidDataException("Quota record too large.");
                line.Append((char)ch);
            }
            return ch < 0 && line.Length == 0 ? null : line.ToString();
        }

        private static T Run<T>(ProcessStartInfo start, int seconds, CancellationToken token, Func<Process, T> read)
        {
            token.ThrowIfCancellationRequested();
            using (var process = new Process { StartInfo = start })
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                if (!process.Start()) throw new IOException("Quota process failed to start.");
                Action stop = () => { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } };
                using (timeout.Token.Register(() => stop()))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
                    try
                    {
                        process.BeginErrorReadLine();
                        process.StandardInput.AutoFlush = true;
                        return read(process);
                    }
                    finally
                    {
                        stop();
                        token.ThrowIfCancellationRequested();
                        if (timeout.IsCancellationRequested) throw new QuotaException("timeout", "额度查询超时，可以稍后刷新。");
                    }
                }
            }
        }

        private static string Output(ProcessStartInfo start, int seconds, CancellationToken token)
        {
            return Run(start, seconds, token, process => {
                process.StandardInput.Close();
                var buffer = new char[4096];
                var text = new StringBuilder();
                int n;
                while ((n = process.StandardOutput.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (text.Length + n > 1024 * 1024) throw new InvalidDataException("Quota report too large.");
                    text.Append(buffer, 0, n);
                }
                return text.ToString();
            });
        }

        private static QuotaReport ReadAntigravity(string id, string client, CancellationToken token)
        {
            var start = AgentSessions.QuotaCommand(id, client);
            start.Arguments = "--version";
            var match = Regex.Match(Output(start, 5, token), @"\b(\d+\.\d+\.\d+)\b");
            Version version;
            if (!match.Success || !Version.TryParse(match.Value, out version) || version < new Version(1, 1, 11))
                throw new QuotaException("missing", "Antigravity 的额度报告需要 1.1.11 或更新版本的官方 CLI。");
            start.Arguments = "-p /usage --output-format json --print-timeout 18s";
            string text = Output(start, 24, token);
            if (String.IsNullOrWhiteSpace(text)) throw new QuotaException("unavailable", "此登录环境还没有可用的 Antigravity 用量报告。");
            return QuotaData.Antigravity(QuotaData.Serializer().DeserializeObject(text));
        }

        public void Dispose() { lifetime.Cancel(); }

        internal void Forget(string id, string client)
        {
            string prefix = client + "/" + id + "/";
            foreach (string key in new List<string>(recent.Keys))
                if (key.StartsWith(prefix, StringComparison.Ordinal)) recent.Remove(key);
        }
    }
}
