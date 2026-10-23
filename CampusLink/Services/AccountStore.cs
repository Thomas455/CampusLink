using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using CampusLink.Infrastructure;
using CampusLink.Models;
using Newtonsoft.Json;

namespace CampusLink.Services
{
    /// <summary>
    /// 账号与设置持久化：%APPDATA%\CampusLink\accounts.json / settings.json。
    /// 密码使用 DPAPI（当前用户）加密，拷贝到别的机器上无法解密。
    /// </summary>
    public class AccountStore
    {
        private readonly LogService _log;
        private readonly string _directory;
        private readonly string _accountsPath;
        private readonly string _settingsPath;

        public AccountStore(LogService log) : this(log, null)
        {
        }

        /// <summary>baseDirectory 为空时用 %APPDATA%\CampusLink（测试可传入临时目录）。</summary>
        public AccountStore(LogService log, string baseDirectory)
        {
            _log = log;
            _directory = string.IsNullOrWhiteSpace(baseDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CampusLink")
                : baseDirectory;
            _accountsPath = Path.Combine(_directory, "accounts.json");
            _settingsPath = Path.Combine(_directory, "settings.json");
        }

        public string DataDirectory => _directory;

        public List<AccountProfile> LoadAccounts()
        {
            var list = new List<AccountProfile>();
            try
            {
                if (!File.Exists(_accountsPath)) return list;
                var json = File.ReadAllText(_accountsPath, Encoding.UTF8);
                var loaded = JsonConvert.DeserializeObject<List<AccountProfile>>(json);
                if (loaded == null) return list;

                foreach (var item in loaded)
                {
                    if (item == null) continue;
                    item.Password = Unprotect(item.PasswordCipher);
                    if (string.IsNullOrEmpty(item.Id)) item.Id = Guid.NewGuid().ToString("N");
                    list.Add(item);
                }
                _log.Info("已加载 " + list.Count + " 个账号配置");
            }
            catch (Exception ex)
            {
                _log.Error("读取账号配置失败", ex);
            }
            return list;
        }

        public void SaveAccounts(IEnumerable<AccountProfile> accounts)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                var snapshot = new List<AccountProfile>();
                foreach (var account in accounts)
                {
                    var copy = account.Clone();
                    copy.PasswordCipher = Protect(account.Password);
                    copy.Password = "";
                    snapshot.Add(copy);
                }
                var json = JsonConvert.SerializeObject(snapshot, Formatting.Indented);
                WriteAtomic(_accountsPath, json);
                _log?.Info("账号配置已保存（" + snapshot.Count + " 个）→ " + _accountsPath);
            }
            catch (Exception ex)
            {
                _log?.Error("保存账号配置失败", ex);
            }
        }

        public AppSettings LoadSettings()
        {
            try
            {
                if (!File.Exists(_settingsPath)) return new AppSettings();
                var json = File.ReadAllText(_settingsPath, Encoding.UTF8);
                return JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
            }
            catch (Exception ex)
            {
                _log.Warn("读取设置失败，使用默认值：" + ex.Message);
                return new AppSettings();
            }
        }

        public void SaveSettings(AppSettings settings)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                WriteAtomic(_settingsPath, JsonConvert.SerializeObject(settings, Formatting.Indented));
            }
            catch (Exception ex)
            {
                _log.Error("保存设置失败", ex);
            }
        }

        private static void WriteAtomic(string path, string content)
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, content, new UTF8Encoding(false));
            if (File.Exists(path))
            {
                File.Replace(temp, path, null);
            }
            else
            {
                File.Move(temp, path);
            }
        }

        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            try
            {
                var bytes = Encoding.UTF8.GetBytes(plain);
                var cipher = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(cipher);
            }
            catch
            {
                return "";
            }
        }

        public static string Unprotect(string cipher)
        {
            if (string.IsNullOrEmpty(cipher)) return "";
            try
            {
                var bytes = Convert.FromBase64String(cipher);
                var plain = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch
            {
                return "";
            }
        }
    }
}
