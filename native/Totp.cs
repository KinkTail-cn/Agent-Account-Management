using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace KeyIsle
{
    internal sealed class TotpConfig
    {
        public string secret, algorithm;
        public int digits, period;
    }

    internal sealed class CodeWindow
    {
        public string code;
        public long starts, expires;
    }

    internal static class Totp
    {
        private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        internal static TotpConfig Parse(string input)
        {
            if (String.IsNullOrWhiteSpace(input) || input.Length > 4096)
                throw new InvalidDataException("请输入 2FA 密钥或验证器设置链接。");
            string text = input.Trim();
            var config = new TotpConfig { secret = text, algorithm = "SHA1", digits = 6, period = 30 };
            if (text.Contains("://"))
            {
                Uri uri;
                if (!Uri.TryCreate(text, UriKind.Absolute, out uri) || uri.Scheme != "otpauth" || uri.Host != "totp")
                    throw new InvalidDataException("请使用基于时间的 otpauth://totp/ 设置链接。");
                var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string token in uri.Query.TrimStart('?').Split('&'))
                {
                    var pair = token.Split(new[] { '=' }, 2);
                    if (pair.Length != 2) continue;
                    string key = Uri.UnescapeDataString(pair[0]);
                    if (parameters.ContainsKey(key)) throw new InvalidDataException("设置链接中有重复参数。");
                    parameters[key] = Uri.UnescapeDataString(pair[1].Replace("+", " "));
                }
                string value;
                if (!parameters.TryGetValue("secret", out config.secret)) throw new InvalidDataException("设置链接缺少 2FA 密钥。");
                if (parameters.TryGetValue("algorithm", out value)) config.algorithm = value.ToUpperInvariant().Replace("-", "");
                if (parameters.TryGetValue("digits", out value)) config.digits = Number(value);
                if (parameters.TryGetValue("period", out value)) config.period = Number(value);
            }
            string base32 = new String(config.secret.Where(c => !Char.IsWhiteSpace(c) && c != '-').ToArray()).ToUpperInvariant();
            string unpadded = base32.TrimEnd('=');
            if (unpadded.Length != base32.Length && (base32.Length % 8 != 0 || base32.Length - unpadded.Length > 6))
                throw new InvalidDataException("Base32 密钥的末尾填充无效。");
            config.secret = unpadded;
            Validate(config);
            return config;
        }

        private static int Number(string value)
        {
            int parsed;
            return Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) ? parsed : 0;
        }

        internal static void Validate(TotpConfig config)
        {
            if (config.algorithm != "SHA1" && config.algorithm != "SHA256" && config.algorithm != "SHA512")
                throw new InvalidDataException("支持 SHA-1、SHA-256 和 SHA-512 验证码。");
            if (config.digits < 6 || config.digits > 8 || config.period < 1 || config.period > 86400)
                throw new InvalidDataException("验证码支持 6 至 8 位，周期为 1 至 86400 秒。");
            byte[] decoded = Decode(config.secret);
            Array.Clear(decoded, 0, decoded.Length);
        }

        private static byte[] Decode(string secret)
        {
            const string error = "请输入完整的 Base32 密钥（字母 A–Z、数字 2–7）或 otpauth://totp/ 链接。";
            if (String.IsNullOrEmpty(secret) || secret.Length > 2048) throw new InvalidDataException(error);
            int tail = secret.Length % 8;
            if (tail == 1 || tail == 3 || tail == 6) throw new InvalidDataException(error);
            var bytes = new byte[secret.Length * 5 / 8];
            int buffer = 0, bits = 0, index = 0;
            foreach (char character in secret)
            {
                int digit = Alphabet.IndexOf(character);
                if (digit < 0) throw new InvalidDataException(error);
                buffer = (buffer << 5) | digit;
                bits += 5;
                if (bits >= 8)
                {
                    bits -= 8;
                    bytes[index++] = (byte)(buffer >> bits);
                    buffer &= (1 << bits) - 1;
                }
            }
            if (buffer != 0 || index == 0) throw new InvalidDataException(error);
            return bytes;
        }

        internal static CodeWindow Generate(TotpConfig config, long unixSeconds)
        {
            long counter = unixSeconds / config.period;
            long start = counter * config.period;
            byte[] bytes = BitConverter.GetBytes(counter);
            if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
            byte[] key = Decode(config.secret);
            try
            {
                using (HMAC hash = CreateHash(config.algorithm, key))
                {
                    byte[] digest = hash.ComputeHash(bytes);
                    int offset = digest[digest.Length - 1] & 15;
                    int truncated = ((digest[offset] & 127) << 24) | (digest[offset + 1] << 16) | (digest[offset + 2] << 8) | digest[offset + 3];
                    int modulus = config.digits == 6 ? 1000000 : config.digits == 7 ? 10000000 : 100000000;
                    string code = (truncated % modulus).ToString("D" + config.digits, CultureInfo.InvariantCulture);
                    return new CodeWindow { code = code, starts = start * 1000, expires = (start + config.period) * 1000 };
                }
            }
            finally { Array.Clear(key, 0, key.Length); }
        }

        internal static long NowSeconds()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        private static HMAC CreateHash(string algorithm, byte[] key)
        {
            switch (algorithm)
            {
                case "SHA1": return new HMACSHA1(key);
                case "SHA256": return new HMACSHA256(key);
                case "SHA512": return new HMACSHA512(key);
                default: throw new InvalidDataException("无法识别验证码算法。");
            }
        }
    }
}
