using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace KeyIsle
{
    internal sealed class DesktopWindow : Form
    {
        private const string Origin = "https://keyisle.local";
        private const string Page = Origin + "/index.html";
        private readonly VaultStore store = new VaultStore();
        private readonly AgentSessions agents = new AgentSessions();
        private readonly QuotaService quotas = new QuotaService();
        private readonly AntigravityLogin googleLogin = new AntigravityLogin();
        private readonly ReleaseUpdates updates = new ReleaseUpdates();
        private readonly WebView2 browser = new WebView2();
        private readonly Timer clipboardTimer = new Timer { Interval = 30000 };
        private uint sensitiveClipboard;

        [DllImport("user32.dll")]
        private static extern uint GetClipboardSequenceNumber();
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        internal DesktopWindow()
        {
            Text = Program.DisplayName;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            BackColor = Color.FromArgb(246, 246, 251);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;
            float scale;
            using (var graphics = CreateGraphics()) scale = graphics.DpiX / 96f;
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Size = new Size(Math.Min((int)(1260 * scale), (int)(area.Width * .96)), Math.Min((int)(836 * scale), (int)(area.Height * .94)));
            MinimumSize = new Size(Math.Min((int)(490 * scale), area.Width), Math.Min((int)(470 * scale), area.Height));
            browser.Dock = DockStyle.Fill;
            browser.DefaultBackgroundColor = BackColor;
            Controls.Add(browser);
            clipboardTimer.Tick += delegate { ClearSensitiveClipboard(); };
            Deactivate += delegate
            {
                if (browser.CoreWebView2 != null) browser.CoreWebView2.PostWebMessageAsJson("{\"event\":\"inactive\"}");
            };
        }

        protected override async void OnShown(EventArgs args)
        {
            base.OnShown(args);
            try
            {
                CoreWebView2Environment.SetLoaderDllFolderPath(Program.LoaderDirectory);
                var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Program.Home, "WebView"));
                await browser.EnsureCoreWebView2Async(environment);
                if (IsDisposed) return;
                var core = browser.CoreWebView2;
                core.Settings.IsGeneralAutofillEnabled = false;
                core.Settings.IsPasswordAutosaveEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.Settings.IsSwipeNavigationEnabled = false;
                core.Settings.AreHostObjectsAllowed = false;
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += ServeResource;
                core.NavigationStarting += delegate(object s, CoreWebView2NavigationStartingEventArgs e) { e.Cancel = e.Uri != Page; };
                core.NewWindowRequested += delegate(object s, CoreWebView2NewWindowRequestedEventArgs e) { e.Handled = true; };
                core.PermissionRequested += delegate(object s, CoreWebView2PermissionRequestedEventArgs e) { e.State = CoreWebView2PermissionState.Deny; };
                core.WebMessageReceived += ReceiveMessage;
                core.Navigate(Page);
            }
            catch (Exception e)
            {
                if (IsDisposed) return;
                string message = e is WebView2RuntimeNotFoundException
                    ? "请安装 Microsoft Edge WebView2 Evergreen 运行时（x64），再打开" + Program.DisplayName + "。"
                    : "界面启动失败。\n" + e.Message;
                MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        }

        private void ServeResource(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            var uri = new Uri(e.Request.Uri);
            Stream stream = null;
            if (uri.GetLeftPart(UriPartial.Authority) == Origin && e.Request.Method == "GET")
                stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("site" + uri.AbsolutePath);
            var environment = browser.CoreWebView2.Environment;
            if (stream == null)
            {
                e.Response = environment.CreateWebResourceResponse(null, 404, "Not Found", "Cache-Control: no-store");
                return;
            }
            string type;
            switch (Path.GetExtension(uri.AbsolutePath))
            {
                case ".html": type = "text/html; charset=utf-8"; break;
                case ".js": type = "text/javascript; charset=utf-8"; break;
                case ".css": type = "text/css; charset=utf-8"; break;
                case ".woff2": type = "font/woff2"; break;
                case ".webp": type = "image/webp"; break;
                default: type = "image/png"; break;
            }
            const string policy = "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self'; font-src 'self'; connect-src 'none'; form-action 'none'; frame-ancestors 'none'; base-uri 'none'";
            e.Response = environment.CreateWebResourceResponse(stream, 200, "OK", "Content-Type: " + type + "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nContent-Security-Policy: " + policy);
        }

        private async void ReceiveMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (e.Source != Page) return;
            int id = 0;
            string method = "";
            object response;
            try
            {
                var request = VaultStore.Json.Deserialize<Dictionary<string, object>>(e.WebMessageAsJson);
                id = Convert.ToInt32(request["id"]);
                method = (string)request["method"];
                object payload = request["payload"];
                object value = null;
                switch (method)
                {
                    case "load": value = store.Load(); break;
                    case "updateInfo": value = updates.Info(); break;
                    case "updateCheck": value = await updates.Check((bool)payload); break;
                    case "updateDownload": value = await updates.Download(); break;
                    case "updateRestart": updates.Restart(); BeginInvoke(new Action(Close)); break;
                    case "updatePage":
                        using (Process.Start(new ProcessStartInfo(ReleaseUpdates.Repository + "/releases") { UseShellExecute = true })) { }
                        break;
                    case "save":
                        var oldAccounts = store.Load().accounts;
                        var next = VaultStore.Json.ConvertToType<Vault>(payload);
                        value = store.Save(next);
                        var currentAccounts = next.accounts.ToDictionary(a => a.id);
                        foreach (var old in oldAccounts)
                        {
                            Account changed;
                            if (old.agentClient != null && (!currentAccounts.TryGetValue(old.id, out changed) ||
                                changed.login != old.login || changed.agentClient != old.agentClient))
                            {
                                googleLogin.Forget(old);
                                quotas.Forget(old.id, old.agentClient);
                            }
                        }
                        break;
                    case "parseOtp": value = Totp.Parse((string)payload); break;
                    case "agentCatalog": value = AgentSessions.Catalog(); break;
                    case "agentQuota":
                        var query = (Dictionary<string, object>)payload;
                        var record = store.Load().accounts.First(a => a.id == (string)query["id"] && a.agentClient == (string)query["client"]);
                        value = await quotas.Read(record.id, record.agentClient, record.login, (bool)query["refresh"]);
                        break;
                    case "googleLoginState":
                    case "googleLoginBegin":
                        var google = store.Load().accounts.First(a => a.id == (string)payload);
                        value = method == "googleLoginBegin" ? googleLogin.Begin(google) : googleLogin.Inspect(google);
                        break;
                    case "googleLoginAction":
                    case "googleLoginRetry":
                        var change = (Dictionary<string, object>)payload;
                        var pendingAccount = store.Load().accounts.First(a => a.id == (string)change["id"]);
                        value = method == "googleLoginRetry"
                            ? googleLogin.Begin(pendingAccount, (string)change["attempt"])
                            : googleLogin.Act(pendingAccount, (string)change["attempt"], (string)change["action"], (string)change["code"]);
                        quotas.Forget(pendingAccount.id, pendingAccount.agentClient);
                        break;
                    case "openQuotaPage":
                        AgentSessions.OpenWebQuota(store.Load().accounts.First(a => a.id == (string)payload));
                        break;
                    case "importWebQuota":
                        var excerpt = (Dictionary<string, object>)payload;
                        var webAccount = store.Load().accounts.First(a => a.id == (string)excerpt["id"]);
                        QuotaData.Save(webAccount.id, webAccount.agentClient, QuotaData.Web(webAccount.agentClient, (string)excerpt["text"]));
                        quotas.Forget(webAccount.id, webAccount.agentClient);
                        break;
                    case "agentFolder":
                        using (var picker = new FolderBrowserDialog { Description = "选择 Agent 的项目文件夹" })
                            value = picker.ShowDialog(this) == DialogResult.OK ? picker.SelectedPath : null;
                        break;
                    case "launchAgent":
                        var selection = (Dictionary<string, object>)payload;
                        var target = store.Load().accounts.First(a => a.id == (string)selection["id"]);
                        bool login = (bool)selection["login"];
                        bool focused = agents.Launch(target, login);
                        value = focused;
                        if (AntigravityLogin.IsGoogle(target.agentClient))
                        {
                            string desktop = await System.Threading.Tasks.Task.Run(() => WebQuota.AwaitDesktop(target, login));
                            quotas.Forget(target.id, target.agentClient);
                            value = new { focused = focused, desktop = desktop };
                        }
                        break;
                    case "codes":
                        var requested = new HashSet<string>(VaultStore.Json.ConvertToType<string[]>(payload));
                        long now = Totp.NowSeconds();
                        value = store.Load().accounts.Where(a => a.otp != null && requested.Contains(a.id))
                            .ToDictionary(a => a.id, a => Totp.Generate(a.otp, now));
                        break;
                    case "copy": CopyField((Dictionary<string, object>)payload); break;
                    case "openWebsite":
                        var account = store.Load().accounts.First(a => a.id == (string)payload);
                        if (!VaultStore.WebAddress(account.website)) throw new InvalidDataException("请输入有效的网址。");
                        Process.Start(new ProcessStartInfo(account.website) { UseShellExecute = true });
                        break;
                    case "theme":
                        int dark = (string)payload == "dark" ? 1 : 0;
                        DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
                        break;
                    default: throw new InvalidOperationException("无法识别此操作。");
                }
                response = new { id = id, ok = true, value = value };
            }
            catch (Exception error)
            {
                string message = "操作失败，请重试。";
                if (method == "load") message = "无法读取本机账号库。请使用保存这些账号的 Windows 用户，并检查数据文件是否可读。";
                else if (method.StartsWith("update") && error is UnauthorizedAccessException) message = "程序所在文件夹不可写。请从发行页下载 ZIP，解压到自己的文件夹。";
                else if (error is InvalidDataException || error is InvalidOperationException) message = error.Message;
                else if (method.StartsWith("update")) message = "更新操作未完成，请检查网络和磁盘空间，或打开发行页手动下载。";
                else if (method == "save") message = "保存失败。请检查磁盘空间与文件夹权限，再次保存。";
                else if (method == "copy") message = "剪贴板正被占用，请再试一次。";
                response = new { id = id, ok = false, error = message };
            }
            if (!IsDisposed && !Disposing && browser.CoreWebView2 != null)
                browser.CoreWebView2.PostWebMessageAsJson(VaultStore.Json.Serialize(response));
        }

        private void CopyField(Dictionary<string, object> payload)
        {
            var account = store.Load().accounts.First(a => a.id == (string)payload["id"]);
            string field = (string)payload["field"];
            string text;
            int lifetime = 30000;
            switch (field)
            {
                case "login": text = account.login; break;
                case "password": text = account.password; break;
                case "website": text = account.website; break;
                case "otp":
                    if (account.otp == null) throw new InvalidDataException("此账号尚未绑定 2FA。");
                    var window = Totp.Generate(account.otp, Totp.NowSeconds());
                    text = window.code;
                    lifetime = (int)Math.Max(1L, Math.Min(30000L, window.expires - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                    break;
                default: throw new InvalidDataException("无法复制此字段。");
            }
            if (String.IsNullOrEmpty(text)) return;
            var data = new DataObject();
            data.SetText(text, TextDataFormat.UnicodeText);
            bool sensitive = field == "password" || field == "otp";
            if (sensitive)
            {
                data.SetData("CanIncludeInClipboardHistory", new MemoryStream(new byte[4]));
                data.SetData("CanUploadToCloudClipboard", new MemoryStream(new byte[4]));
            }
            Clipboard.SetDataObject(data, true, 4, 50);
            clipboardTimer.Stop();
            sensitiveClipboard = 0;
            if (sensitive)
            {
                sensitiveClipboard = GetClipboardSequenceNumber();
                clipboardTimer.Interval = lifetime;
                clipboardTimer.Start();
            }
        }

        private void ClearSensitiveClipboard()
        {
            clipboardTimer.Stop();
            if (sensitiveClipboard != 0 && GetClipboardSequenceNumber() == sensitiveClipboard)
            {
                try { Clipboard.Clear(); }
                catch (ExternalException) { if (!Disposing) clipboardTimer.Start(); return; }
            }
            sensitiveClipboard = 0;
        }

        protected override void WndProc(ref Message message)
        {
            if (Program.ActivateMessage != 0 && message.Msg == Program.ActivateMessage)
            {
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                Activate();
            }
            base.WndProc(ref message);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            ClearSensitiveClipboard();
            clipboardTimer.Dispose();
            agents.Dispose();
            quotas.Dispose();
            googleLogin.Dispose();
            updates.Dispose();
            base.OnFormClosed(e);
        }
    }
}
