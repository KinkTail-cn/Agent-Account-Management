using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Web;
using System.Windows.Automation;
using System.Windows.Forms;
using KeyIsle;

internal static class QuotaFormats
{
    private static void Check(bool test, string name)
    {
        if (!test) throw new Exception(name);
    }

    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "-p")
        {
            Check(!Console.IsInputRedirected, "The login prompt requires a real terminal");
            Console.Error.WriteLine("Open https://accounts.google.com/o/oauth2/v2/auth?client_id=fixture&response_type=code&state=safe%2Bstate&prompt=consent&redirect_uri=https%3A%2F%2Fexample.invalid%2Fcallback");
            Console.Error.WriteLine("Waiting for authentication (timeout 60s)...");
            string answer = Console.ReadLine();
            if (answer == "invalid")
            {
                Console.Error.WriteLine("Error: authentication failed: token exchange failed: oauth2: \"invalid_grant\"");
                Console.Error.WriteLine("error: authentication failed or timed out");
                Environment.ExitCode = 1;
                return;
            }
            if (answer == "expired")
            {
                Console.Error.WriteLine("Error: authentication timed out.");
                Environment.ExitCode = 1;
                return;
            }
            if (answer == "wrong") Console.Error.WriteLine("Signed in as other@example.invalid");
            Check(answer == "fixture-approved" || answer == "wrong", "Exact line received by the terminal");
            Console.WriteLine(@"{""status"":""SUCCESS"",""command"":{""name"":""usage"",""data"":{""groups"":[{""name"":""Gemini"",""buckets"":[{""window"":""5h"",""remaining_fraction"":0.62}]}]}}}");
            return;
        }
        string home = Path.Combine(Path.GetTempPath(), "key-isle-return-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("LOCALAPPDATA", home);
        var json = QuotaData.Serializer();
        var codex = QuotaData.Codex(json.DeserializeObject(@"{""rateLimits"":{""primary"":{""usedPercent"":99}},""rateLimitsByLimitId"":{
            ""codex"":{""primary"":{""usedPercent"":0,""windowDurationMins"":300,""resetsAt"":1800000000},""secondary"":{""usedPercent"":100,""windowDurationMins"":10080}},
            ""other"":{""limitName"":""Other Model"",""primary"":{""resetsAt"":1800000300}}}}"));
        Check(codex.windows.Count == 3 && codex.windows[0].remaining == 100 && codex.windows[0].reset == 1800000000000, "Codex windows");
        Check(codex.windows[1].remaining == 0 && codex.windows[1].period == "7 天", "Exhausted weekly bucket");
        Check(codex.windows[2].remaining == null && codex.windows[2].model == "Other Model", "Unknown fraction");
        var claude = QuotaData.Claude(json.DeserializeObject(@"{""context_window"":{""used_percentage"":90},""rate_limits"":{""five_hour"":{""used_percentage"":12.5},""seven_day_sonnet"":{""used_percentage"":50}}}"));
        Check(claude.windows.Count == 2 && claude.windows[0].remaining == 87.5 && claude.windows[1].model == "sonnet", "Claude subscription windows");
        Check(QuotaData.Claude(json.DeserializeObject(@"{""context_window"":{""used_percentage"":99}}")).windows.Count == 0, "No context substitution");
        var ag = QuotaData.Antigravity(json.DeserializeObject(@"{""status"":""SUCCESS"",""command"":{""name"":""usage"",""data"":{""groups"":[
            {""name"":""Gemini Models"",""buckets"":[{""window"":""5h"",""remaining_fraction"":0.79,""reset_time"":""2027-01-01T00:00:00Z""},{""window"":""weekly"",""remaining_fraction"":0}]},
            {""name"":""Unavailable"",""enabled"":false,""buckets"":[{""remaining_fraction"":1}]}]}}}"));
        Check(ag.windows.Count == 2 && ag.windows[0].remaining == 79 && ag.windows[0].reset == 1798761600000, "Antigravity group quota");
        Check(ag.windows[1].remaining == 0, "Zero is a genuine remaining fraction");
        Check(QuotaData.Antigravity(json.DeserializeObject(@"{""status"":""ERROR"",""usage"":{""total_tokens"":1000}}")).windows.Count == 0, "No token substitution");
        try { CheckLogin(); CheckDesktopProfile(); CheckWelcome(); }
        finally { if (Directory.Exists(home)) Directory.Delete(home, true); }
        Console.WriteLine("Quota formats, authorization retries, desktop profile pinning and native sign-in actions passed.");
    }

    private static void CheckWelcome()
    {
        using (var shown = new ManualResetEventSlim())
        using (var clicked = new ManualResetEventSlim())
        {
            Form form = null;
            IntPtr hwnd = IntPtr.Zero;
            var ui = new Thread(() => {
                using (form = new Form { Text = "Isolated login fixture", ShowInTaskbar = false })
                {
                    var heading = new Label { Text = "Welcome to Antigravity!", Dock = DockStyle.Top };
                    var button = new Button { Text = "Continue with Google", Dock = DockStyle.Bottom };
                    form.Controls.Add(heading);
                    form.Controls.Add(button);
                    button.Click += (sender, args) => clicked.Set();
                    form.Shown += (sender, args) => { hwnd = form.Handle; shown.Set(); };
                    Application.Run(form);
                }
            });
            ui.SetApartmentState(ApartmentState.STA);
            ui.IsBackground = true;
            ui.Start();
            Check(shown.Wait(5000), "Test welcome window did not appear");
            try
            {
                var window = AutomationElement.FromHandle(hwnd);
                Check(WebQuota.InspectGoogleWindow(window, "antigravity-app", "one@example.invalid", false) == "out" && !clicked.IsSet,
                    "Refreshing quota must report the welcome screen without invoking sign-in");
                Check(WebQuota.InspectGoogleWindow(window, "antigravity-app", "one@example.invalid", true) == "signInStarted" && clicked.Wait(3000),
                    "Explicit desktop login must invoke the official welcome button");
            }
            finally { form.BeginInvoke(new Action(form.Close)); ui.Join(5000); }
        }
    }

    private static void CheckDesktopProfile()
    {
        string id = Guid.NewGuid().ToString("D");
        const string client = "antigravity-app";
        string root = AntigravityLogin.Base(id, client);
        string legacy = Guid.NewGuid().ToString("D");
        string legacyPath = Path.Combine(root, "Sessions", legacy);
        Directory.CreateDirectory(Path.Combine(legacyPath, "UserData"));
        File.WriteAllText(Path.Combine(root, "active-session"), legacy);
        Check(AntigravityLogin.DesktopProfile(id, client) == legacyPath, "Existing desktop profile must be adopted");
        string replacement = Guid.NewGuid().ToString("D");
        Directory.CreateDirectory(Path.Combine(root, "Sessions", replacement, "Home"));
        File.WriteAllText(Path.Combine(root, "active-session"), replacement);
        Check(AntigravityLogin.DesktopProfile(id, client) == legacyPath, "Renewing quotas must not move a desktop session");
        Check(AntigravityLogin.Active(id, client) != legacyPath, "Quota CLI follows its own authorization");
        string second = Guid.NewGuid().ToString("D");
        Check(AntigravityLogin.DesktopProfile(second, client) == AntigravityLogin.Base(second, client), "Desktop opens before any CLI authorization");
        Check(!File.Exists(Path.Combine(AntigravityLogin.Base(second, client), "active-session")), "Opening the desktop must not manufacture quota authorization");
    }

    private static void Phase(AntigravityLogin login, Account account, string expected)
    {
        var t = Stopwatch.StartNew();
        while (t.ElapsedMilliseconds < 6000)
        {
            if (login.Inspect(account).phase == expected) return;
            Thread.Sleep(30);
        }
        throw new Exception("Did not reach " + expected + ", saw " + login.Inspect(account).phase);
    }

    private static void CheckLogin()
    {
        var windows = new ConcurrentDictionary<string, string>();
        using (var login = new AntigravityLogin(() => typeof(QuotaFormats).Assembly.Location, (folder, url) => {
            var request = HttpUtility.ParseQueryString(new Uri(url).Query);
            Check(request["state"] == "safe+state" && request["redirect_uri"] == "https://example.invalid/callback", "OAuth callback binding");
            Check(request["prompt"].Contains("select_account") && request["prompt"].Contains("consent"), "Explicit account choice");
            windows[request["login_hint"]] = folder;
        }))
        {
            var a = new Account { id = Guid.NewGuid().ToString(), login = "first@example.invalid", agentClient = "antigravity-app" };
            var b = new Account { id = Guid.NewGuid().ToString(), login = "second@example.invalid", agentClient = "antigravity-app" };
            var first = login.Begin(a).attemptId;
            var second = login.Begin(b).attemptId;
            Phase(login, a, "authorizing");
            Phase(login, b, "authorizing");
            Check(windows[a.login] != windows[b.login], "Concurrent sign-ins must have distinct homes");
            Check(login.Act(a, first, "code", "fixture-approved").phase == "verifying", "Submission has a persistent pending state");
            Phase(login, a, "ready");
            string active = AntigravityLogin.Active(a.id, a.agentClient);
            Check(active == windows[a.login] && QuotaData.Cached(a.id, a.agentClient).windows[0].remaining == 62, "Authenticated session promotion");
            login.Act(b, second, "cancel", null);
            Phase(login, b, "cancelled");
            Check(!AntigravityLogin.HasBoundSession(b), "Cancellation must not publish an empty session");
            var another = login.Begin(a).attemptId;
            Phase(login, a, "authorizing");
            login.Act(a, another, "code", "wrong");
            Phase(login, a, "error");
            Check(AntigravityLogin.Active(a.id, a.agentClient) == active, "Failed re-login must preserve the previous session");
            var browser = windows[b.login];
            var rejected = login.Begin(b, second).attemptId;
            Phase(login, b, "authorizing");
            Check(windows[b.login] == browser && second != rejected, "Retry reuses this account's browser with a new authorization request");
            try { login.Act(b, second, "code", "fixture-approved"); throw new Exception("Stale request accepted"); }
            catch (InvalidOperationException) { }
            login.Act(b, rejected, "code", "invalid");
            Phase(login, b, "error");
            Check(login.Inspect(b).message.Contains("Google 未接受") && !AntigravityLogin.HasBoundSession(b), "Actionable rejection");
            var expired = login.Begin(b, rejected).attemptId;
            Phase(login, b, "authorizing");
            login.Act(b, expired, "code", "expired");
            Phase(login, b, "expired");
            var completed = login.Begin(b, expired).attemptId;
            Phase(login, b, "authorizing");
            login.Act(b, completed, "code", "fixture-approved");
            Phase(login, b, "ready");
            Check(Path.GetFileName(AntigravityLogin.Active(b.id, b.agentClient)) == completed, "Only a confirmed retry becomes active");
        }
    }
}
