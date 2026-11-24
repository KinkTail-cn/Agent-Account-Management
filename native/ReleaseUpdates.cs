using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace KeyIsle
{
    internal sealed class ReleaseUpdates : IDisposable
    {
        internal const string Repository = "https://github.com/KinkTail-cn/Agent-Account-Management";
        internal const string PackageName = "Agent-Account-Management-windows-x64.zip";
        internal const string Executable = Program.DisplayName + ".exe";
        internal static readonly string Root = Path.Combine(Program.Home, "Updates");
        internal static readonly Version Current = new Version(typeof(Program).Assembly.GetName().Version.ToString(3));

        private const string Api = "https://api.github.com/repos/KinkTail-cn/Agent-Account-Management/releases/latest";
        private const long MaxSize = 64 * 1024 * 1024;
        private readonly HttpClient http;
        private readonly SemaphoreSlim operation = new SemaphoreSlim(1);
        private readonly CancellationTokenSource closing = new CancellationTokenSource();
        private string latest, packageDigest, stagedDirectory, executableDigest;
        private long size;
        private bool checkedOnline;

        internal ReleaseUpdates() : this(new HttpClientHandler { SslProtocols = System.Security.Authentication.SslProtocols.Tls12 }) { }

        internal ReleaseUpdates(HttpMessageHandler transport)
        {
            http = new HttpClient(transport) { Timeout = TimeSpan.FromSeconds(90) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Agent-Account-Management/" + Current);
        }

        private static Version ParseTag(string tag)
        {
            if (tag == null || !Regex.IsMatch(tag, @"\Av(?:0|[1-9]\d{0,3})\.(?:0|[1-9]\d{0,3})\.(?:0|[1-9]\d{0,3})\z"))
                throw new InvalidDataException("发布页的版本号无法识别。");
            return new Version(tag.Substring(1));
        }

        private static bool ValidHash(string value)
        {
            return value != null && Regex.IsMatch(value, @"\A[a-fA-F0-9]{64}\z");
        }

        internal static string FileHash(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        private string State
        {
            get
            {
                if (stagedDirectory != null) return "ready";
                if (latest != null) return ParseTag(latest) > Current ? "available" : "current";
                return checkedOnline ? "empty" : "idle";
            }
        }

        internal object Info()
        {
            return new { current = Current.ToString(), latest = latest, state = State };
        }

        internal async Task<object> Check(bool automatic)
        {
            await operation.WaitAsync(closing.Token);
            try
            {
                string stamp = Path.Combine(Root, "last-check");
                if (automatic && File.Exists(stamp))
                {
                    TimeSpan age = DateTime.UtcNow - File.GetLastWriteTimeUtc(stamp);
                    if (age >= TimeSpan.Zero && age < TimeSpan.FromDays(1))
                    {
                        string tag = new FileInfo(stamp).Length <= 64 ? File.ReadAllText(stamp).Trim() : "";
                        if (tag.Length > 0)
                        {
                            try { ParseTag(tag); latest = tag; return Info(); }
                            catch (InvalidDataException) { }
                        }
                        else { checkedOnline = true; return Info(); }
                    }
                }
                await FetchLatest();
                try
                {
                    Directory.CreateDirectory(Root);
                    File.WriteAllText(stamp, latest ?? "");
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                return Info();
            }
            finally { operation.Release(); }
        }

        private async Task FetchLatest()
        {
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(closing.Token))
            {
                limit.CancelAfter(TimeSpan.FromSeconds(15));
                using (var response = await http.GetAsync(Api, HttpCompletionOption.ResponseHeadersRead, limit.Token))
                {
                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        latest = null;
                        packageDigest = stagedDirectory = null;
                        checkedOnline = true;
                        return;
                    }
                    if (response.StatusCode == HttpStatusCode.Forbidden || (int)response.StatusCode == 429)
                        throw new InvalidOperationException("GitHub 暂时限制了查询，请稍后重试或打开发行页。");
                    response.EnsureSuccessStatusCode();
                    byte[] bytes;
                    using (var body = await response.Content.ReadAsStreamAsync())
                    using (var memory = new MemoryStream())
                    {
                        await CopyLimited(body, memory, 1024 * 1024, limit.Token);
                        bytes = memory.ToArray();
                    }
                    var json = new JavaScriptSerializer();
                    var release = json.Deserialize<GithubRelease>(System.Text.Encoding.UTF8.GetString(bytes));
                    if (release == null || release.draft || release.prerelease) throw new InvalidDataException("暂时没有可用的正式版。");
                    Version version = ParseTag(release.tag_name);
                    var asset = (release.assets ?? new GithubAsset[0]).SingleOrDefault(a => a.name == PackageName);
                    string digest = asset != null && asset.digest != null && asset.digest.StartsWith("sha256:", StringComparison.Ordinal)
                        ? asset.digest.Substring(7).ToLowerInvariant() : null;
                    if (version > Current && (asset == null || !ValidHash(digest) || asset.size < 1 || asset.size > MaxSize))
                        throw new InvalidDataException("该版本的 Windows 更新包尚未就绪，请稍后重试。");
                    if (release.tag_name != latest || packageDigest != digest) stagedDirectory = null;
                    latest = release.tag_name;
                    size = asset == null ? 0 : asset.size;
                    packageDigest = digest;
                    checkedOnline = true;
                }
            }
        }

        private static async Task<long> CopyLimited(Stream source, Stream target, long max, CancellationToken token)
        {
            var buffer = new byte[65536];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, 0, buffer.Length, token)) != 0)
            {
                total += read;
                if (total > max) throw new InvalidDataException("下载数据超过预期大小。");
                await target.WriteAsync(buffer, 0, read, token);
            }
            return total;
        }

        internal async Task<object> Download()
        {
            await operation.WaitAsync(closing.Token);
            string temporary = null;
            try
            {
                await FetchLatest();
                if (State != "available") return Info();
                temporary = Directory.CreateDirectory(Path.Combine(Root, Guid.NewGuid().ToString("N"))).FullName;
                string zip = Path.Combine(temporary, PackageName);
                string url = Repository + "/releases/download/" + latest + "/" + PackageName;
                using (var limit = CancellationTokenSource.CreateLinkedTokenSource(closing.Token))
                {
                    limit.CancelAfter(TimeSpan.FromMinutes(2));
                    using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, limit.Token))
                    {
                        response.EnsureSuccessStatusCode();
                        using (var stream = await response.Content.ReadAsStreamAsync())
                        using (var file = new FileStream(zip, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            if (await CopyLimited(stream, file, size, limit.Token) != size)
                                throw new InvalidDataException("下载未完成，请重新下载。");
                    }
                }
                string exe = Path.Combine(temporary, Executable);
                executableDigest = await Task.Run(() => Extract(zip, packageDigest, ParseTag(latest), exe), closing.Token);
                File.Delete(zip);
                stagedDirectory = temporary;
                temporary = null;
                return Info();
            }
            finally
            {
                if (temporary != null) Cleanup(temporary);
                operation.Release();
            }
        }

        internal static string Extract(string archive, string expected, Version version, string target)
        {
            if (!ValidHash(expected) || !String.Equals(FileHash(archive), expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新包校验失败，请重新下载。");
            using (var zip = ZipFile.OpenRead(archive))
            {
                var entry = zip.Entries.SingleOrDefault(e => e.FullName == Executable);
                if (entry == null || entry.Length < 1 || entry.Length > MaxSize) throw new InvalidDataException("更新包缺少有效的程序文件。");
                using (var input = entry.Open())
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[65536];
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (output.Length + read > entry.Length) throw new InvalidDataException("更新包大小不符。");
                        output.Write(buffer, 0, read);
                    }
                    if (output.Length != entry.Length) throw new InvalidDataException("程序文件不完整。");
                }
            }
            if (AssemblyName.GetAssemblyName(target).Version.ToString(3) != version.ToString())
                throw new InvalidDataException("程序版本与发布版本不一致。");
            return FileHash(target);
        }

        internal void Restart()
        {
            if (State != "ready" || !ValidHash(executableDigest)) throw new InvalidOperationException("请先下载更新。");
            string staged = Path.Combine(stagedDirectory, Executable);
            if (!File.Exists(staged) || FileHash(staged) != executableDigest)
            {
                Cleanup(stagedDirectory);
                stagedDirectory = executableDigest = null;
                throw new InvalidDataException("已下载的程序未通过校验，请重新下载更新。");
            }
            string target = typeof(Program).Assembly.Location;
            string next = Sibling(target, stagedDirectory);
            try
            {
                // Stage on the same volume as the installed exe and check write access before the app exits.
                File.Copy(staged, next, false);
                string helper = Path.Combine(stagedDirectory, "apply.exe");
                File.Copy(target, helper, true);
                using (var parent = Process.GetCurrentProcess())
                {
                    var plan = new InstallPlan { target = target, tag = latest, hash = executableDigest,
                        pid = parent.Id, started = parent.StartTime.ToUniversalTime().Ticks };
                    File.WriteAllText(Path.Combine(stagedDirectory, "install.json"), new JavaScriptSerializer().Serialize(plan));
                }
                using (Process.Start(new ProcessStartInfo(helper, "--apply-update") { UseShellExecute = false })) { }
            }
            catch
            {
                if (File.Exists(next)) File.Delete(next);
                throw;
            }
        }

        private static string Sibling(string target, string staging)
        {
            return target + "." + Path.GetFileName(staging) + ".next";
        }

        internal static void ApplyPending()
        {
            string helper = typeof(Program).Assembly.Location;
            string dir = Path.GetDirectoryName(helper);
            Guid guid;
            if (!String.Equals(Path.GetDirectoryName(dir), Root, StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(Path.GetFileName(dir), "N", out guid) || Path.GetFileName(helper) != "apply.exe")
                throw new InvalidOperationException("请从应用的「软件更新」打开更新程序。");
            var plan = new JavaScriptSerializer().Deserialize<InstallPlan>(File.ReadAllText(Path.Combine(dir, "install.json")));
            string target = Path.GetFullPath(plan.target);
            string next = Sibling(target, dir);
            if (ParseTag(plan.tag) <= Current || !ValidHash(plan.hash) || !File.Exists(target) ||
                !String.Equals(Path.GetExtension(target), ".exe", StringComparison.OrdinalIgnoreCase) || FileHash(target) != FileHash(helper) ||
                FileHash(next) != plan.hash || AssemblyName.GetAssemblyName(next).Version.ToString(3) != plan.tag.Substring(1))
                throw new InvalidDataException("更新文件校验失败。请重新打开原程序下载更新。");
            try
            {
                using (var parent = Process.GetProcessById(plan.pid))
                    if (parent.StartTime.ToUniversalTime().Ticks == plan.started && !parent.WaitForExit(30000))
                        throw new InvalidOperationException("程序尚未退出，请返回应用后再试。");
            }
            catch (ArgumentException) { } // Parent has already exited.
            string backup = target + "." + guid.ToString("N") + ".previous";
            bool replaced = false;
            try
            {
                File.Replace(next, target, backup);
                replaced = true;
                Launch(target);
            }
            catch
            {
                if (replaced) File.Replace(backup, target, null);
                Launch(target);
                throw;
            }
            try { File.Delete(backup); } catch (IOException) { }
        }

        private static void Launch(string path)
        {
            using (var proc = Process.Start(new ProcessStartInfo(path) {
                UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)
            }))
                if (proc == null) throw new InvalidOperationException("请手动重新打开应用。");
        }

        internal static void Cleanup(string directory)
        {
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return;
                foreach (string name in new[] { PackageName, Executable, "apply.exe", "install.json" })
                    File.Delete(Path.Combine(directory, name));
                Directory.Delete(directory, false);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        internal static void CleanOldDownloads()
        {
            try
            {
                if (!Directory.Exists(Root)) return;
                foreach (string dir in Directory.EnumerateDirectories(Root))
                {
                    Guid ignored;
                    if (Guid.TryParseExact(Path.GetFileName(dir), "N", out ignored) && Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow.AddDays(-2))
                        Cleanup(dir);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public void Dispose() { closing.Cancel(); http.Dispose(); }

        private sealed class GithubRelease
        {
            public string tag_name { get; set; }
            public bool draft { get; set; }
            public bool prerelease { get; set; }
            public GithubAsset[] assets { get; set; }
        }
        private sealed class GithubAsset
        {
            public string name { get; set; }
            public string digest { get; set; }
            public long size { get; set; }
        }
        private sealed class InstallPlan
        {
            public string target { get; set; }
            public string tag { get; set; }
            public string hash { get; set; }
            public int pid { get; set; }
            public long started { get; set; }
        }
    }
}
