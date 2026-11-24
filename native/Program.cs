using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle(KeyIsle.Program.DisplayName)]
[assembly: AssemblyProduct(KeyIsle.Program.DisplayName)]
[assembly: AssemblyDescription("个人账号管理")]
[assembly: AssemblyVersion("1.3.0.0")]

namespace KeyIsle
{
    internal static class Program
    {
        internal const string DisplayName = "agent账号切换器";
        internal static readonly string Home = Path.Combine(
            Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeyIsle");
        internal static readonly string LoaderDirectory = Path.Combine(Home, "runtime", "1.0.3537.50-x64");
        internal static int ActivateMessage;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegisterWindowMessage(string name);
        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--claude-quota")
            {
                try { ClaudeQuota.Receive(args[1]); }
                catch { Environment.ExitCode = 1; }
                return;
            }
            if (args.Length == 1 && args[0] == "--apply-update")
            {
                try { ReleaseUpdates.ApplyPending(); }
                catch (Exception error)
                {
                    MessageBox.Show("更新未完成。\n" + error.Message, DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Environment.ExitCode = 1;
                }
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            AppDomain.CurrentDomain.AssemblyResolve += ResolveLibrary;
            try
            {
                string instanceName;
                using (var hash = SHA256.Create())
                    instanceName = "KeyIsle." + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(Home.ToUpperInvariant()))).Replace("-", "");
                ActivateMessage = RegisterWindowMessage(instanceName);
                bool first;
                using (var instance = new Mutex(true, @"Local\" + instanceName, out first))
                {
                    if (!first)
                    {
                        PostMessage(new IntPtr(0xffff), ActivateMessage, IntPtr.Zero, IntPtr.Zero);
                        return;
                    }
                    Directory.CreateDirectory(LoaderDirectory);
                    ReleaseUpdates.CleanOldDownloads();
                    InstallLoader();
                    RunWindow();
                }
            }
            catch (Exception error)
            {
                MessageBox.Show("无法启动" + DisplayName + "。\n" + error.Message, DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RunWindow()
        {
            Application.Run(new DesktopWindow());
        }

        private static Assembly ResolveLibrary(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name;
            if (name != "Microsoft.Web.WebView2.Core" && name != "Microsoft.Web.WebView2.WinForms") return null;
            return Assembly.Load(ResourceBytes("library/" + name + ".dll"));
        }

        internal static byte[] ResourceBytes(string name)
        {
            using (Stream stream = typeof(Program).Assembly.GetManifestResourceStream(name))
            using (var buffer = new MemoryStream())
            {
                if (stream == null) throw new FileNotFoundException(name);
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }
        }

        private static void InstallLoader()
        {
            string path = Path.Combine(LoaderDirectory, "WebView2Loader.dll");
            byte[] embedded = ResourceBytes("library/WebView2Loader.dll");
            if (File.Exists(path))
            {
                using (var hash = SHA256.Create())
                using (var file = File.OpenRead(path))
                    if (BitConverter.ToString(hash.ComputeHash(file)) == BitConverter.ToString(hash.ComputeHash(embedded))) return;
            }
            string pending = path + ".new";
            File.WriteAllBytes(pending, embedded);
            if (File.Exists(path)) File.Replace(pending, path, null);
            else File.Move(pending, path);
        }
    }
}
