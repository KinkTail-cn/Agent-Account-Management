using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace KeyIsle
{
    internal sealed class Account
    {
        public string id { get; set; }
        public string name { get; set; }
        public string login { get; set; }
        public string password { get; set; }
        public string website { get; set; }
        public string note { get; set; }
        public string groupId { get; set; }
        public string agentClient { get; set; }
        public string agentFolder { get; set; }
        public long agentLastOpened { get; set; }
        public TotpConfig otp { get; set; }
        public bool favorite { get; set; }
        public string recordStatus { get; set; }
        public long created { get; set; }
        public long updated { get; set; }
    }

    internal sealed class Group
    {
        public string id, name, color, icon;
    }

    internal sealed class Preferences
    {
        public string theme = "system", view = "list", sort = "recent";
        public bool reduceMotion { get; set; }
        public int glass = 45;
    }

    internal sealed class Vault
    {
        public int version = 1, revision;
        public List<Account> accounts = new List<Account>();
        public List<Group> groups = new List<Group>();
        public Preferences preferences = new Preferences();

        public static Vault New()
        {
            var vault = new Vault();
            vault.groups.Add(new Group { id = Guid.NewGuid().ToString(), name = "工作", color = "indigo", icon = "briefcase" });
            vault.groups.Add(new Group { id = Guid.NewGuid().ToString(), name = "个人", color = "peach", icon = "person" });
            vault.groups.Add(new Group { id = Guid.NewGuid().ToString(), name = "生活", color = "mint", icon = "leaf" });
            return vault;
        }
    }

    internal sealed class VaultStore
    {
        internal const int MaxBytes = 32 * 1024 * 1024;
        internal static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = MaxBytes, RecursionLimit = 24 };
        private readonly string path = Path.Combine(Program.Home, "vault.dat");
        private Vault current;

        public Vault Load()
        {
            if (current != null) return current;
            try
            {
                byte[] cipher;
                using (var stream = File.OpenRead(path))
                {
                    if (stream.Length > MaxBytes || stream.Length == 0) throw new InvalidDataException();
                    using (var reader = new BinaryReader(stream)) cipher = reader.ReadBytes((int)stream.Length);
                }
                byte[] plain = ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
                try
                {
                    var loaded = Json.Deserialize<Vault>(Encoding.UTF8.GetString(plain));
                    Validate(loaded);
                    current = loaded;
                }
                finally { Array.Clear(plain, 0, plain.Length); }
            }
            catch (FileNotFoundException) { current = Vault.New(); }
            return current;
        }

        public int Save(Vault next)
        {
            if (current == null || next == null || next.revision != current.revision)
                throw new InvalidOperationException("账号库状态已改变，请重新打开应用。");
            Validate(next);
            next.revision = checked(current.revision + 1);
            byte[] plain = Encoding.UTF8.GetBytes(Json.Serialize(next));
            string temporary = path + ".new";
            try
            {
                byte[] cipher = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
                if (cipher.Length > MaxBytes) throw new InvalidDataException("账号库已达到本机文件大小上限。");
                using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    file.Write(cipher, 0, cipher.Length);
                    file.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
                current = next;
                return current.revision;
            }
            finally
            {
                Array.Clear(plain, 0, plain.Length);
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private static bool Fits(string value, int limit, bool required = false)
        {
            return value != null && value.Length <= limit && (!required || !String.IsNullOrWhiteSpace(value));
        }

        internal static bool WebAddress(string value)
        {
            Uri uri;
            return Uri.TryCreate(value, UriKind.Absolute, out uri) && !String.IsNullOrWhiteSpace(uri.Host)
                && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
        }

        private static bool ValidId(string id)
        {
            Guid guid;
            return Guid.TryParseExact(id, "D", out guid);
        }

        private static void Validate(Vault vault)
        {
            if (vault == null || vault.version != 1 || vault.revision < 0 || vault.groups == null || vault.accounts == null)
                throw new InvalidDataException("无法识别账号库格式。");
            var colors = new[] { "indigo", "peach", "mint", "sky", "rose", "amber" };
            var icons = new[] { "briefcase", "person", "leaf", "controller", "book", "wallet2", "heart", "folder2" };
            var groupIds = new HashSet<string>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in vault.groups)
                if (g == null || !ValidId(g.id) || !groupIds.Add(g.id) || !Fits(g.name, 40, true) || !names.Add(g.name.Trim()) || !colors.Contains(g.color) || !icons.Contains(g.icon))
                    throw new InvalidDataException("请检查分组名称、颜色与图标。");
            var accountIds = new HashSet<string>();
            foreach (var a in vault.accounts)
            {
                if (a == null || !ValidId(a.id) || !accountIds.Add(a.id) || !Fits(a.name, 100, true) || !Fits(a.login, 250, true)
                    || !Fits(a.password, 1024) || !Fits(a.note, 4000) || !Fits(a.website, 2048) || (a.website != "" && !WebAddress(a.website))
                    || (a.groupId != null && !groupIds.Contains(a.groupId)) || a.created <= 0 || a.updated < a.created)
                    throw new InvalidDataException("请检查账号信息后再保存。");
                if (a.otp != null) Totp.Validate(a.otp);
                if (a.recordStatus != null && !new[] { "normal", "issue", "disabled" }.Contains(a.recordStatus))
                    throw new InvalidDataException("请选择有效的账号状态。");
                if ((a.agentClient != null && !AgentSessions.Known(a.agentClient)) || a.agentLastOpened < 0)
                    throw new InvalidDataException("无法识别 Agent 客户端。");
                if (!String.IsNullOrEmpty(a.agentFolder) && (!Fits(a.agentFolder, 1024) || !Path.IsPathRooted(a.agentFolder)
                    || a.agentFolder.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || a.agentFolder.IndexOf('\"') >= 0))
                    throw new InvalidDataException("请选择有效的项目文件夹。");
            }
            var p = vault.preferences;
            if (p == null || !new[] { "system", "light", "dark" }.Contains(p.theme) || !new[] { "list", "grid" }.Contains(p.view)
                || !new[] { "recent", "name" }.Contains(p.sort) || p.glass < 0 || p.glass > 100)
                throw new InvalidDataException("无法识别外观设置。");
        }
    }
}
