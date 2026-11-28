using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace OneClickFilm
{
    internal static class AnalysisKeyStore
    {
        private static string KeyFileFor(string id)
        {
            string root = Environment.GetEnvironmentVariable("ONE_CLICK_FILM_SETTINGS");
            if (String.IsNullOrWhiteSpace(root))
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "一键成片");
            return Path.Combine(root, "本机凭证", id == null ? "openai-analysis.dpapi" : id + ".dpapi");
        }

        internal static bool HasSavedKey { get { return File.Exists(KeyFileFor(null)); } }

        internal static string Read(string id = null)
        {
            byte[] clear = null;
            try
            {
                byte[] encrypted;
                using (var file = new FileStream(KeyFileFor(id), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                {
                    if (file.Length == 0 || file.Length > 16384)
                        throw Unreadable();
                    using (var reader = new BinaryReader(file))
                        encrypted = reader.ReadBytes((int)file.Length);
                }
                clear = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                return Normalize(Encoding.UTF8.GetString(clear));
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (CryptographicException) { throw Unreadable(); }
            catch (ArgumentException) { throw Unreadable(); }
            finally { if (clear != null) Array.Clear(clear, 0, clear.Length); }
        }

        internal static void Save(string value, string id = null)
        {
            byte[] clear = Encoding.UTF8.GetBytes(Normalize(value));
            byte[] encrypted;
            try { encrypted = ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser); }
            catch (CryptographicException)
            { throw new InvalidOperationException("Windows 无法加密项目 Key。请检查当前 Windows 用户配置。"); }
            finally { Array.Clear(clear, 0, clear.Length); }

            string path = KeyFileFor(id);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    file.Write(encrypted, 0, encrypted.Length);
                    file.Flush(true);
                }
                if (File.Exists(path)) File.Replace(pending, path, null);
                else File.Move(pending, path);
            }
            finally { File.Delete(pending); }
        }

        internal static void Clear() { File.Delete(KeyFileFor(null)); }

        internal static void DiscardImageKey(string id)
        {
            // The profile already points to its committed key. A locked obsolete file is harmless.
            if (String.IsNullOrEmpty(id)) throw new ArgumentException("缺少图片渠道凭证编号。");
            try { File.Delete(KeyFileFor(id)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        internal static string Normalize(string value)
        {
            value = (value ?? "").Trim();
            if (value.Length == 0 || value.Length > 1024)
                throw new ArgumentException("请粘贴完整的 OpenAI 项目 Key。");
            foreach (char c in value)
                if (c < '!' || c > '~')
                    throw new ArgumentException("项目 Key 中包含空格或无效字符。");
            return value;
        }

        private static InvalidOperationException Unreadable()
        {
            return new InvalidOperationException("无法读取本机保存的 OpenAI 项目 Key。请在设置中替换或清除后重新保存。");
        }
    }
}
