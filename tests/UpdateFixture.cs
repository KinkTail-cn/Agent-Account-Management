using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

[assembly: AssemblyVersion("1.2.0.0")]

namespace KeyIsle
{
    // Compile with native/ReleaseUpdates.cs only. This older process is replaced by the release under test.
    internal static class Program
    {
        internal const string DisplayName = "agent账号切换器";
        internal static readonly string Home = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA"), "KeyIsle");

        private sealed class Releases : HttpMessageHandler
        {
            internal byte[] package;
            internal string digest, tag;
            internal bool empty, badChecksum;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                string url = request.RequestUri.AbsoluteUri;
                if (url == "https://api.github.com/repos/KinkTail-cn/Agent-Account-Management/releases/latest")
                {
                    if (empty) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                    var asset = new { name = ReleaseUpdates.PackageName, size = package.Length,
                        digest = "sha256:" + (badChecksum ? new String('0', 64) : digest) };
                    var release = new { tag_name = tag, draft = false, prerelease = false, assets = new[] { asset } };
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                        Content = new StringContent(new JavaScriptSerializer().Serialize(release))
                    });
                }
                if (url != ReleaseUpdates.Repository + "/releases/download/" + tag + "/" + ReleaseUpdates.PackageName)
                    throw new Exception("Unexpected download URL");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(package) });
            }
        }

        private static void Expect(object result, string state)
        {
            if ((string)result.GetType().GetProperty("state").GetValue(result) != state)
                throw new Exception("Unexpected state; expected " + state);
        }

        private static void Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--apply-update") { ReleaseUpdates.ApplyPending(); return; }
            string zip = args[0];
            var api = new Releases { package = File.ReadAllBytes(zip), tag = args[1], digest = ReleaseUpdates.FileHash(zip), empty = true };
            using (var updater = new ReleaseUpdates(api))
            {
                Expect(updater.Check(false).GetAwaiter().GetResult(), "empty");
                api.empty = false;
                api.tag = "v1.2.0";
                Expect(updater.Check(false).GetAwaiter().GetResult(), "current");
                api.tag = args[1];
                Expect(updater.Check(false).GetAwaiter().GetResult(), "available");
                api.badChecksum = true;
                try { updater.Download().GetAwaiter().GetResult(); throw new Exception("Unexpected checksum acceptance"); }
                catch (InvalidDataException) { }
                api.badChecksum = false;
                Expect(updater.Download().GetAwaiter().GetResult(), "ready");
                var data = new {
                    version = 1, revision = 1, groups = new object[0],
                    accounts = new[] { new { id = Guid.NewGuid().ToString(), name = "Update journey fixture", login = "upgrade@example.invalid",
                        password = "", note = "", website = "", created = 1, updated = 1 } },
                    preferences = new { theme = "system", view = "list", sort = "recent", glass = 45 }
                };
                byte[] cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(data)), null, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(Path.Combine(Home, "vault.dat"), cipher);
                updater.Restart();
                Console.WriteLine("Release downloaded, verified, staged and installer started.");
            }
        }
    }
}
