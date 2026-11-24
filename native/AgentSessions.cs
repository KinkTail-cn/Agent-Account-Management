using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace KeyIsle
{
    internal sealed class AgentClient
    {
        public readonly string id, provider, label;
        internal readonly Func<string> Locate;

        internal AgentClient(string id, string provider, string label, Func<string> locate)
        {
            this.id = id;
            this.provider = provider;
            this.label = label;
            Locate = locate;
        }
    }

    internal sealed class AgentSessions : IDisposable
    {
        private static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        private static readonly string Roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        private static readonly string User = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        private static readonly string Programs = Path.Combine(Local, "Programs");
        private static readonly string PF = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        private static readonly string PF86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        private static readonly AgentClient[] Clients = {
            new AgentClient("chatgpt-desktop", "chatgpt", "ChatGPT 桌面版", () => FirstFile(
                Path.Combine(Local, @"Microsoft\WindowsApps\chatgpt-desktop.exe"),
                Path.Combine(Local, @"Microsoft\WindowsApps\OpenAI.Codex_2p2nqsd0c76g0\chatgpt-desktop.exe"))),
            new AgentClient("codex-cli", "chatgpt", "Codex CLI", FindCodex),
            new AgentClient("chatgpt-web", "chatgpt", "ChatGPT 网页窗口", FindBrowser),
            new AgentClient("claude-code", "claude", "Claude Code", FindClaude),
            new AgentClient("claude-web", "claude", "Claude 网页窗口", FindBrowser),
            new AgentClient("antigravity-app", "antigravity", "Antigravity 桌面版", () => FirstFile(
                Path.Combine(Programs, @"Antigravity\Antigravity.exe"), Path.Combine(PF, @"Antigravity\Antigravity.exe"))),
            new AgentClient("antigravity-ide", "antigravity", "Antigravity IDE", () => FirstFile(
                Path.Combine(Programs, @"Antigravity IDE\Antigravity IDE.exe"), Path.Combine(PF, @"Antigravity IDE\Antigravity IDE.exe")))
        };
        private readonly Dictionary<string, Process> launched = new Dictionary<string, Process>();

        internal static bool Known(string client) { return Clients.Any(c => c.id == client); }

        internal static object Catalog()
        {
            return Clients.Select(c => new { c.id, c.provider, c.label, available = c.Locate() != null }).ToArray();
        }

        private static string FirstFile(params string[] candidates)
        {
            return candidates.FirstOrDefault(p => p != null && File.Exists(p));
        }

        private static string OnPath(string filename)
        {
            foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                string directory = entry.Trim().Trim('"');
                if (directory.Length < 3 || !Path.IsPathRooted(directory)) continue;
                try
                {
                    string file = Path.Combine(directory, filename);
                    if (File.Exists(file)) return file;
                }
                catch (ArgumentException) { }
            }
            return null;
        }

        internal static string FindCodex()
        {
            return FirstFile(Path.Combine(Local, @"OpenAI\Codex\bin\codex.exe"), Path.Combine(User, @".local\bin\codex.exe"),
                OnPath("codex.exe"), Path.Combine(Roaming, @"npm\node_modules\@openai\codex\node_modules\@openai\codex-win32-x64\vendor\x86_64-pc-windows-msvc\codex\codex.exe"));
        }

        internal static string FindClaude()
        {
            return FirstFile(Path.Combine(User, @".local\bin\claude.exe"), OnPath("claude.exe"),
                Path.Combine(Local, @"Microsoft\WinGet\Packages\Anthropic.ClaudeCode_Microsoft.Winget.Source_8wekyb3d8bbwe\claude.exe"));
        }

        private static string FindBrowser()
        {
            return FirstFile(Path.Combine(Local, @"Google\Chrome\Application\chrome.exe"), Path.Combine(PF, @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(PF86, @"Google\Chrome\Application\chrome.exe"), Path.Combine(PF86, @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(PF, @"Microsoft\Edge\Application\msedge.exe"));
        }

        internal static string FindQuotaCli()
        {
            return FirstFile(Path.Combine(Local, @"KeyIsle\tools\agy\1.5.16\agy.exe"),
                Path.Combine(Local, @"agy\bin\agy.exe"), OnPath("agy.exe"));
        }

        private static string DirectoryAt(string parent, string name)
        {
            var directory = Directory.CreateDirectory(Path.Combine(parent, name));
            return directory.FullName;
        }

        private static void SetCodexHome(ProcessStartInfo start, string profile)
        {
            string home = DirectoryAt(profile, "CodexHome");
            start.EnvironmentVariables["CODEX_HOME"] = home;
            string config = Path.Combine(home, "config.toml");
            if (!File.Exists(config))
            {
                using (var file = new FileStream(config, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(file, new UTF8Encoding(false)))
                    writer.WriteLine("cli_auth_credentials_store = \"file\"");
            }
        }

        // The IDE and the companion agent also use home-relative .gemini state.
        private static void SetAntigravityHome(ProcessStartInfo start, string profile)
        {
            string home = DirectoryAt(profile, "Home");
            string root = Path.GetPathRoot(home);
            start.EnvironmentVariables["USERPROFILE"] = home;
            start.EnvironmentVariables["HOME"] = home;
            start.EnvironmentVariables["HOMEDRIVE"] = root.TrimEnd('\\');
            start.EnvironmentVariables["HOMEPATH"] = home.Substring(root.TrimEnd('\\').Length);
            start.EnvironmentVariables["APPDATA"] = DirectoryAt(home, @"AppData\Roaming");
            start.EnvironmentVariables["LOCALAPPDATA"] = DirectoryAt(home, @"AppData\Local");
            start.EnvironmentVariables["XDG_CONFIG_HOME"] = DirectoryAt(home, ".config");
            start.EnvironmentVariables["XDG_DATA_HOME"] = DirectoryAt(home, @".local\share");
            start.EnvironmentVariables["CLOUDSDK_CONFIG"] = DirectoryAt(home, @".config\gcloud");
            start.EnvironmentVariables["ANTIGRAVITY_GEMINI_DIR"] = DirectoryAt(home, ".gemini");
            // Use the vendor's remote/file-backed login mode in this child and its language server.
            start.EnvironmentVariables["SSH_TTY"] = "key-isle-profile";
            start.EnvironmentVariables["AGY_CLI_FORCE_FILE_TOKEN_STORAGE"] = "true";
            start.EnvironmentVariables.Remove("AGY_ADC_AUTH");
            start.EnvironmentVariables.Remove("ANTIGRAVITY_WSL_DISTRO");
            start.EnvironmentVariables.Remove("JETSKI_LS_PORT");
            start.EnvironmentVariables.Remove("AGY_DESKTOP_HOST_DIR");
            start.EnvironmentVariables.Remove("VSCODE_IPC_HOOK_CLI");
            start.EnvironmentVariables.Remove("VSCODE_PORTABLE");
        }

        internal static string ProfileRoot(string id)
        {
            Guid parsed;
            if (!Guid.TryParseExact(id, "D", out parsed)) throw new InvalidDataException("无效的账号标识。");
            return Path.Combine(Program.Home, "AgentProfiles", parsed.ToString("D"));
        }

        internal static ProcessStartInfo Prepare(Account account)
        {
            var client = Clients.FirstOrDefault(c => c.id == account.agentClient);
            if (client == null) throw new InvalidDataException("请先为这个账号选择客户端。");
            string executable = client.Locate();
            if (executable == null) throw new InvalidOperationException("未检测到 " + client.label + "。安装后可以重新检测，或在编辑账号中选择其他客户端。");
            string profile = client.provider == "antigravity" ? AntigravityLogin.Active(account.id, client.id) : DirectoryAt(ProfileRoot(account.id), client.id);
            bool acceptsFolder = client.id == "codex-cli" || client.id == "claude-code" || client.id == "antigravity-ide";
            string workspace = acceptsFolder ? account.agentFolder : null;
            if (String.IsNullOrEmpty(workspace)) workspace = DirectoryAt(Path.Combine(Program.Home, "Workspaces"), account.id);
            else if (!Directory.Exists(workspace)) throw new InvalidDataException("项目文件夹已移动。请编辑账号，重新选择项目文件夹。");
            var start = Child(executable, workspace);
            if (client.provider == "chatgpt" && client.id != "chatgpt-web") SetCodexHome(start, profile);
            if (client.id == "claude-code")
            {
                start.EnvironmentVariables["CLAUDE_CONFIG_DIR"] = DirectoryAt(profile, "ClaudeConfig");
                start.EnvironmentVariables["KEY_ISLE_STATUS_EXE"] = typeof(Program).Assembly.Location.Replace('\\', '/');
                start.Arguments = ClaudeQuota.LaunchArguments(account.id);
            }
            if (client.provider == "antigravity") SetAntigravityHome(start, profile);
            if (client.id == "codex-cli" || client.id == "claude-code") return start;
            string data = DirectoryAt(profile, "UserData");
            // All arguments below are fixed strings or validated filesystem paths.
            start.Arguments = "--user-data-dir=\"" + data + "\"";
            if (client.id == "chatgpt-desktop") start.EnvironmentVariables["CODEX_ELECTRON_USER_DATA_PATH"] = data;
            if (client.id.EndsWith("-web", StringComparison.Ordinal))
            {
                string url = client.provider == "chatgpt" ? "https://chatgpt.com/" : "https://claude.ai/";
                start.Arguments += " --no-first-run --no-default-browser-check --app=" + url;
            }
            if (client.id == "antigravity-ide")
            {
                start.Arguments += " --new-window --extensions-dir=\"" + DirectoryAt(profile, "Extensions") + "\"";
                if (!String.IsNullOrEmpty(account.agentFolder)) start.Arguments += " \"" + Path.Combine(workspace, ".") + "\"";
            }
            return start;
        }

        private static ProcessStartInfo Child(string executable, string workspace)
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = workspace };
            foreach (string variable in new[] { "OPENAI_API_KEY", "CODEX_API_KEY", "OPENAI_BASE_URL", "CODEX_ACCESS_TOKEN",
                "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL", "CLAUDE_CODE_OAUTH_TOKEN", "CLAUDE_CODE_OAUTH_TOKEN_FILE_DESCRIPTOR",
                "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY", "CLAUDECODE", "GEMINI_API_KEY",
                "GOOGLE_API_KEY", "GOOGLE_APPLICATION_CREDENTIALS", "ELECTRON_RUN_AS_NODE" })
                start.EnvironmentVariables.Remove(variable);
            return start;
        }

        internal static ProcessStartInfo QuotaCommand(string id, string client)
        {
            string profile = AntigravityLogin.IsGoogle(client) ? AntigravityLogin.Active(id, client) : Path.Combine(ProfileRoot(id), client);
            string probeDir = DirectoryAt(Path.Combine(Program.Home, "Quota"), "Working");
            bool codex = client == "chatgpt-desktop" || client == "codex-cli";
            bool claude = client == "claude-code";
            string executable = codex ? FindCodex() : claude ? FindClaude() : FindQuotaCli();
            if (executable == null) throw new QuotaException("missing", "未检测到用于查询的官方 CLI。");
            string configDir = Path.Combine(profile, codex ? "CodexHome" : claude ? "ClaudeConfig" : "Home");
            if (!Directory.Exists(configDir)) throw new QuotaException("first", "先在这个账号的客户端完成首次登录。");
            var start = Child(executable, probeDir);
            if (codex) start.EnvironmentVariables["CODEX_HOME"] = configDir;
            else if (claude) start.EnvironmentVariables["CLAUDE_CONFIG_DIR"] = configDir;
            else
            {
                SetAntigravityHome(start, profile);
                start.EnvironmentVariables["AGY_CLI_DISABLE_AUTO_UPDATE"] = "true";
                start.EnvironmentVariables["AGY_CLI_FORCE_FILE_TOKEN_STORAGE"] = "true";
                start.EnvironmentVariables["AGY_CLI_NONINTERACTIVE_HEADLESS"] = "true";
            }
            start.CreateNoWindow = true;
            start.RedirectStandardInput = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.StandardOutputEncoding = Encoding.UTF8;
            return start;
        }

        internal static ProcessStartInfo GoogleChild(string binary, string profile, bool interactive)
        {
            var start = Child(binary, DirectoryAt(Path.Combine(Program.Home, "Quota"), "Working"));
            SetAntigravityHome(start, profile);
            start.EnvironmentVariables["AGY_CLI_DISABLE_AUTO_UPDATE"] = "true";
            if (interactive) start.EnvironmentVariables.Remove("AGY_CLI_NONINTERACTIVE_HEADLESS");
            return start;
        }

        internal static void OpenGoogleBrowser(string profile, string url)
        {
            string binary = FindBrowser();
            if (binary == null) throw new InvalidOperationException("请安装 Chrome 或 Edge 完成 Google 授权。");
            string browser = DirectoryAt(profile, "GoogleSignIn");
            var start = new ProcessStartInfo(binary) { UseShellExecute = false, WorkingDirectory = profile,
                Arguments = "--user-data-dir=\"" + browser + "\" --no-first-run --no-default-browser-check --new-window \"" + url + "\"" };
            using (var process = Process.Start(start)) { }
        }

        internal static void OpenWebQuota(Account account)
        {
            if (account.agentClient != "claude-web" && account.agentClient != "chatgpt-web")
                throw new InvalidDataException("请选择网页账号。");
            var start = Prepare(account);
            string data = Path.Combine(ProfileRoot(account.id), account.agentClient, "UserData");
            string url = account.agentClient == "claude-web" ? "https://claude.ai/settings/usage" : "https://chatgpt.com/codex/settings/usage";
            start.Arguments = "--user-data-dir=\"" + data + "\" --no-first-run --no-default-browser-check --new-window " + url;
            using (var process = Process.Start(start)) { }
        }

        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("kernel32.dll")] private static extern bool AttachConsole(uint pid);
        [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
        [DllImport("kernel32.dll")] private static extern bool FreeConsole();

        private static bool Focus(Process process, bool terminal)
        {
            try
            {
                if (process.HasExited) return false;
                process.Refresh();
                IntPtr window = process.MainWindowHandle;
                if (window == IntPtr.Zero && terminal && AttachConsole((uint)process.Id))
                {
                    try { window = GetConsoleWindow(); }
                    finally { FreeConsole(); }
                }
                if (window == IntPtr.Zero || !IsWindowVisible(window)) return false;
                if (IsIconic(window)) ShowWindowAsync(window, 9);
                return SetForegroundWindow(window);
            }
            catch (InvalidOperationException) { return false; }
            catch (Win32Exception) { return false; }
        }

        internal bool Launch(Account account, bool login)
        {
            bool terminal = account.agentClient == "codex-cli" || account.agentClient == "claude-code";
            string key = account.agentClient + "/" + account.id + (terminal && login ? "/login" : "");
            if (AntigravityLogin.IsGoogle(account.agentClient))
            {
                if (!AntigravityLogin.HasBoundSession(account)) throw new InvalidOperationException("请先点击「首次登录」，完成这个账号的独立登录。");
                key += "/" + AntigravityLogin.Active(account.id, account.agentClient);
            }
            Process existing;
            if (launched.TryGetValue(key, out existing) && Focus(existing, terminal)) return true;
            var start = Prepare(account);
            if (terminal && login) start.Arguments = account.agentClient == "codex-cli" ? "login" : "auth login";
            try
            {
                Process process = Process.Start(start);
                if (process == null) throw new InvalidOperationException("客户端没有启动，请检查安装。");
                if (launched.TryGetValue(key, out existing)) existing.Dispose();
                launched[key] = process;
                return false;
            }
            catch (Win32Exception) { throw new InvalidOperationException("Windows 无法启动所选客户端，请检查安装或应用执行别名。"); }
        }

        public void Dispose()
        {
            foreach (Process process in launched.Values) if (process != null) process.Dispose();
            launched.Clear();
        }
    }
}
