using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using CampusLink.Infrastructure;
using CampusLink.Models;
using CampusLink.Services;
using CampusLink.ViewModels;
using Newtonsoft.Json;

namespace CoreTests
{
    internal static class Program
    {
        private static int _passed;
        private static int _failed;

        private static void Check(string name, Action action)
        {
            try
            {
                action();
                _passed++;
                Console.WriteLine("[PASS] " + name);
            }
            catch (Exception ex)
            {
                _failed++;
                Console.WriteLine("[FAIL] " + name + " -> " + ex.Message);
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static void Equal(string expected, string actual, string what)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                throw new Exception(what + " 期望 [" + expected + "] 实际 [" + actual + "]");
            }
        }

        private static int Main()
        {
            var log = new LogService(false);

            Console.WriteLine("== 基础工具 ==");
            Check("EncodeUriComponent 普通字符", () =>
                Equal("abc123-_.!~*'()", PortalClient.EncodeUriComponent("abc123-_.!~*'()"), "普通字符"));
            Check("EncodeUriComponent 空格/斜杠", () =>
                Equal("a%20b%2Fc%3F", PortalClient.EncodeUriComponent("a b/c?"), "特殊字符"));
            Check("EncodeUriComponent 中文 UTF-8", () =>
                Equal("%E4%B8%AD%E6%96%87", PortalClient.EncodeUriComponent("中文"), "中文编码"));
            Check("EncodeUriComponent 百分号", () =>
                Equal("100%25", PortalClient.EncodeUriComponent("100%"), "百分号"));
            Check("DoubleEncode 二次编码", () =>
                Equal("a%2520b", PortalClient.DoubleEncode("a b"), "双重编码"));
            Check("DoubleEncode 空串", () =>
                Equal("", PortalClient.DoubleEncode(null), "空串"));
            Check("NormalizeMac 去掉分隔符并大写", () =>
                Equal("AABBCCDDEEFF", RuijieRsa.NormalizeMac("aa-bb-cc-dd-ee-ff"), "MAC 归一化"));
            Check("NormalizeMac 处理空值", () =>
                Equal("", RuijieRsa.NormalizeMac(null), "空 MAC"));

            Console.WriteLine("== DPAPI / 持久化 ==");
            Check("DPAPI 加密后可解密", () =>
            {
                const string plain = "P@ssw0rd-中文-!@#";
                var cipher = AccountStore.Protect(plain);
                Assert(cipher.Length > 0, "密文为空");
                Equal(plain, AccountStore.Unprotect(cipher), "解密结果");
            });
            Check("AccountProfile 序列化不含明文密码", () =>
            {
                var profile = new AccountProfile { UserId = "20231234", Password = "secret", PasswordCipher = "cipher" };
                var json = JsonConvert.SerializeObject(profile);
                Assert(json.IndexOf("secret", StringComparison.Ordinal) < 0, "明文密码被序列化");
                Assert(json.IndexOf("cipher", StringComparison.Ordinal) >= 0, "密文未序列化");
            });
            Check("AccountProfile 反序列化保留字段", () =>
            {
                var json = JsonConvert.SerializeObject(new AccountProfile
                {
                    UserId = "20231234",
                    DisplayName = "宿舍主号",
                    PortalHost = "222.201.54.55",
                    AutoReconnect = true
                });
                var restored = JsonConvert.DeserializeObject<AccountProfile>(json);
                Equal("20231234", restored.UserId, "UserId");
                Equal("宿舍主号", restored.DisplayName, "DisplayName");
            });

            Console.WriteLine("== JSON 容错 ==");
            Check("标准 JSON 解析", () =>
                Equal("success", JsonHelper.GetString(JsonHelper.ParseObject("{\"result\":\"success\"}"), "result"), "result"));
            Check("带前后垃圾字符的 JSON", () =>
                Equal("success", JsonHelper.GetString(JsonHelper.ParseObject("var x= {\"result\":\"success\"};"), "result"), "result"));
            Check("字符串数字转 int", () =>
                Assert(JsonHelper.GetInt(JsonHelper.ParseObject("{\"keepaliveInterval\":\"120\"}"), "keepaliveInterval") == 120, "int 解析"));
            Check("布尔字符串转 bool", () =>
                Assert(JsonHelper.GetBool(JsonHelper.ParseObject("{\"isAutoLogin\":\"true\"}"), "isAutoLogin"), "bool 解析"));

            Console.WriteLine("== RSA ==");
            Check("RSA 无填充加密可被私钥还原", () =>
            {
                var p = BigInteger.Parse("32416190071");
                var q = BigInteger.Parse("32416187567");
                var n = p * q;
                var e = new BigInteger(65537);
                var phi = (p - 1) * (q - 1);
                var d = ModInverse(e, phi);

                const string password = "Ab3!";
                var cipherHex = RuijieRsa.EncryptPassword("10001", n.ToString("x"), password);

                var digitCount = (BitLength(n) + 15) / 16;
                Assert(cipherHex.Length == digitCount * 4, "密文长度应为 " + digitCount * 4 + "，实际 " + cipherHex.Length);

                var encrypted = BigInteger.Parse("0" + cipherHex, NumberStyles.HexNumber);
                var decrypted = BigInteger.ModPow(encrypted, d, n);

                var reversed = Reverse(password);
                var chunkSize = 2 * digitCount;
                var digits = new List<int>();
                for (var i = 0; i < chunkSize; i += 2)
                {
                    var low = i < reversed.Length ? reversed[i] : '\0';
                    var high = i + 1 < reversed.Length ? reversed[i + 1] : '\0';
                    digits.Add((int)low + ((int)high << 8));
                }

                var expectedValue = BigInteger.Zero;
                for (var i = digits.Count - 1; i >= 0; i--)
                {
                    expectedValue = expectedValue * 65536 + digits[i];
                }

                Assert(decrypted == expectedValue, "解密结果与期望明文不一致");
            });
            Check("RSA 拼接 MAC 后与直接加密等价", () =>
            {
                var p = BigInteger.Parse("32416190071");
                var q = BigInteger.Parse("32416187567");
                var n = p * q;
                var modulusHex = n.ToString("x");
                var withMac = RuijieRsa.EncryptPasswordWithMac("10001", modulusHex, "pwd", "aabbccddeeff", ">");
                var direct = RuijieRsa.EncryptReversed("10001", modulusHex, "pwd>AABBCCDDEEFF");
                Equal(direct, withMac, "EncryptPasswordWithMac");
            });

            Console.WriteLine("== 门户协议（mock） ==");
            TestProbeAndLogin(log);
            TestAlreadyOnline(log);
            TestLoginFailureMessage(log);
            TestPasswordEncryptMode(log);
            TestOtherEndpoints(log);
            TestProbeFailure(log);

            Console.WriteLine("== 自助服务（mock） ==");
            TestSelfService(log);

            Console.WriteLine("== 账号持久化 / 会话语义 ==");
            TestPersistence(log);
            TestOnlineWithoutLogin(log);
            TestVerifiedLogin(log);
            TestAdapters();

            Console.WriteLine("== 自动更新 ==");
            TestUpdateVersionParse();
            TestUpdateCheckApi(log);
            TestUpdateCheckFallback(log);
            TestUpdateDownload(log);
            TestUpdateMultiSource(log);
            TestUpdateAlternateAsset(log);

            Console.WriteLine();
            Console.WriteLine("通过 " + _passed + " 项，失败 " + _failed + " 项");
            return _failed == 0 ? 0 : 1;
        }

        private static void TestPersistence(LogService log)
        {
            var dir = Path.Combine(Path.GetTempPath(), "campuslink-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dir);
                var store = new AccountStore(log, dir);
                var vm = new MainViewModel(log, store);

                vm.AddAccountCommand.Execute(null);
                vm.EditorDisplayName = "测试账号";
                vm.EditorUserId = "2023123456";
                vm.SetEditorPassword("P@ss-中文-123");
                vm.EditorPortalHost = "222.201.54.55";
                vm.SelectedAdapter = vm.Adapters.FirstOrDefault();
                vm.SaveAccountCommand.Execute(null);

                Check("添加账号后立即落盘且可重新加载", () =>
                {
                    Assert(vm.Accounts.Count == 1, "内存里没有账号");
                    Assert(File.Exists(Path.Combine(dir, "accounts.json")), "accounts.json 没有生成");

                    var reloaded = new MainViewModel(log, new AccountStore(log, dir));
                    Assert(reloaded.Accounts.Count == 1, "重新加载后账号数为 " + reloaded.Accounts.Count);
                    Equal("2023123456", reloaded.Accounts[0].Profile.UserId, "学号");
                    Equal("P@ss-中文-123", reloaded.Accounts[0].Profile.Password, "密码（DPAPI 解密）");
                    Equal(vm.SelectedAdapter == null ? "" : vm.SelectedAdapter.Id,
                        reloaded.Accounts[0].Profile.NicId, "网卡 Id");
                });

                Check("删除账号后配置也会持久化", () =>
                {
                    var reloaded = new MainViewModel(log, new AccountStore(log, dir));
                    var persisted = new AccountStore(log, dir);
                    persisted.SaveAccounts(Enumerable.Empty<AccountProfile>());
                    var after = new MainViewModel(log, new AccountStore(log, dir));
                    Assert(after.Accounts.Count == 0, "删除后仍有账号：" + after.Accounts.Count);
                });

                Check("旧版本配置文件（含 Mac/SourceIp 字段）仍能加载", () =>
                {
                    var legacy = "[{\"Id\":\"old1\",\"UserId\":\"2020000000\",\"PasswordCipher\":\"\",\"Mac\":\"AABBCCDDEEFF\",\"SourceIp\":\"192.168.1.5\",\"PortalHost\":\"222.201.54.55\"}]";
                    File.WriteAllText(Path.Combine(dir, "accounts.json"), legacy, new UTF8Encoding(false));
                    var loaded = new AccountStore(log, dir).LoadAccounts();
                    Assert(loaded.Count == 1, "旧配置没有加载出来");
                    Equal("2020000000", loaded[0].UserId, "旧配置学号");
                });
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static string HexOf(string text)
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) builder.Append(b.ToString("x2"));
            return builder.ToString();
        }

        private static void TestOnlineWithoutLogin(LogService log)
        {
            var sessionId = "4c2f5624ac0597f382192e487ba92bef";
            var userIndex = HexOf(sessionId + "_10.1.2.3_2023123456");

            using (var portal = new MockPortal(req =>
            {
                if (req.Path == "/") return MockResponse.Redirect(Absolute(req, "/eportal/redirectortosuccess.jsp"));
                if (req.Path == "/eportal/redirectortosuccess.jsp")
                    return MockResponse.Redirect(Absolute(req, "/eportal/success.jsp?userIndex=" + userIndex));
                if (req.Path == "/eportal/InterFace.do" && req.Query.Contains("method=getOnlineUserInfo"))
                    return MockResponse.Json("{\"result\":\"wait\",\"message\":\"用户信息不完整，请稍后重试\",\"userId\":\"2023123456\",\"userName\":\"测试用户\",\"userIp\":\"10.1.2.3\",\"userMac\":\"aabbccddeeff\"}");
                return new MockResponse { Status = 404 };
            }))
            {
                var settings = new AppSettings { ProbeUrls = "http://127.0.0.1:" + portal.Port + "/" };
                var profile = new AccountProfile
                {
                    UserId = "2023123456",
                    Password = "随便乱输的密码",
                    PortalHost = "127.0.0.1:" + portal.Port
                };
                var item = new AccountItemViewModel(profile, log, () => settings);
                var result = item.LoginAsync(false).GetAwaiter().GetResult();

                Check("探测到已在线时不算登录成功（不校验账号密码）", () =>
                {
                    Assert(result, "网络判定应可用");
                    Assert(!item.Session.Verified, "不应标记为已登录");
                    Assert(item.StateText.Contains("免认证"), "状态应显示免认证，实际：" + item.StateText);
                });

                Check("已在线时能从成功页识别在线账号并比对", () =>
                {
                    Equal("2023123456", item.Session.OnlineUserId, "在线账号");
                    Assert(item.Session.OnlineAccountMatches == true, "应判定为与本账号一致");
                    Equal("10.1.2.3", item.Session.OnlineUserIp, "在线 IP");
                    Assert(item.Session.LastMessage.Contains("与本账号一致"), item.Session.LastMessage);
                });
            }

            var otherIndex = HexOf(sessionId + "_10.1.2.3_2023000000");
            using (var portal = new MockPortal(req =>
            {
                if (req.Path == "/") return MockResponse.Redirect(Absolute(req, "/eportal/redirectortosuccess.jsp"));
                if (req.Path == "/eportal/redirectortosuccess.jsp")
                    return MockResponse.Redirect(Absolute(req, "/eportal/success.jsp?userIndex=" + otherIndex));
                if (req.Path == "/eportal/InterFace.do" && req.Query.Contains("method=getOnlineUserInfo"))
                    return MockResponse.Json("{\"result\":\"success\",\"userId\":\"2023000000\",\"userIp\":\"10.1.2.3\",\"userMac\":\"aabbccddeeff\"}");
                return new MockResponse { Status = 404 };
            }))
            {
                var settings = new AppSettings { ProbeUrls = "http://127.0.0.1:" + portal.Port + "/" };
                var profile = new AccountProfile
                {
                    UserId = "2023123456",
                    Password = "x",
                    PortalHost = "127.0.0.1:" + portal.Port
                };
                var item = new AccountItemViewModel(profile, log, () => settings);
                item.LoginAsync(false).GetAwaiter().GetResult();

                Check("在线账号不是本账号时给出警告", () =>
                {
                    Equal("2023000000", item.Session.OnlineUserId, "在线账号");
                    Assert(item.Session.OnlineAccountMatches == false, "应判定为不一致");
                    Assert(item.Session.LastMessage.Contains("不是本账号"), item.Session.LastMessage);
                });
            }
        }

        private static void TestVerifiedLogin(LogService log)
        {
            using (var portal = new MockPortal(req =>
            {
                if (req.Path == "/") return MockResponse.Redirect(Absolute(req, "/eportal/index.jsp?c=ACSW&mac=AABBCCDDEEFF"));
                if (req.Path.StartsWith("/eportal/index.jsp", StringComparison.Ordinal))
                    return MockResponse.Text("<input name=\"passwordEncrypt\" value=\"false\" />");
                if (req.Path == "/eportal/InterFace.do")
                    return MockResponse.Json("{\"userIndex\":\"UI-777\",\"result\":\"success\",\"keepaliveInterval\":120}");
                return new MockResponse { Status = 404 };
            }))
            {
                var settings = new AppSettings { ProbeUrls = "http://127.0.0.1:" + portal.Port + "/" };
                var profile = new AccountProfile
                {
                    UserId = "2023123456",
                    Password = "pwd",
                    PortalHost = "127.0.0.1:" + portal.Port
                };
                var item = new AccountItemViewModel(profile, log, () => settings);
                var result = item.LoginAsync(false).GetAwaiter().GetResult();

                Check("门户要求认证时走真实登录并标记 Verified", () =>
                {
                    Assert(result, "登录应成功");
                    Assert(item.Session.Verified, "应标记为已登录");
                    Equal("UI-777", item.Session.UserIndex, "userIndex");
                    Equal("在线", item.StateText, "状态文案");
                });
            }
        }

        private static void TestAdapters()
        {
            Check("网卡枚举返回合法 MAC", () =>
            {
                var adapters = NetworkAdapterService.GetAdapters();
                foreach (var adapter in adapters)
                {
                    Assert(adapter.Mac.Length == 12, "MAC 长度异常：" + adapter.Mac);
                }
            });
            Check("网卡查找找不到时返回 null", () =>
            {
                Assert(NetworkAdapterService.Find("不存在的网卡") == null, "应返回 null");
            });
        }

        private static void TestUpdateVersionParse()
        {
            Check("更新版本号解析", () =>
            {
                Equal("1.2.3", UpdateService.ParseVersionTag("v1.2.3").ToString(), "v1.2.3");
                Equal("1.2.3", UpdateService.ParseVersionTag("1.2.3").ToString(), "1.2.3");
                Equal("1.2.3", UpdateService.ParseVersionTag("v1.2.3-beta.1").ToString(), "1.2.3-beta");
                Equal("1.2.0", UpdateService.ParseVersionTag("v1.2").ToString(), "v1.2");
                Assert(UpdateService.ParseVersionTag("latest") == null, "非法版本应返回 null");
            });
            Check("更新版本比较", () =>
            {
                Assert(UpdateService.ParseVersionTag("v1.1.1") > UpdateService.ParseVersionTag("v1.1.0"), "1.1.1 > 1.1.0");
                Assert(!(UpdateService.ParseVersionTag("v1.1.0") > UpdateService.ParseVersionTag("1.1.0")), "同版本不算更新");
                Assert(!(UpdateService.ParseVersionTag("v1.0.9") > UpdateService.ParseVersionTag("v1.1.0")), "旧版本不算更新");
            });
        }

        private static void TestUpdateCheckApi(LogService log)
        {
            var apiJson = "{\"tag_name\":\"v9.9.9\",\"name\":\"CampusLink 9.9.9\",\"body\":\"- 修复了若干问题\\n- 新增自动更新\",\"html_url\":\"https://github.com/Thomas455/CampusLink/releases/tag/v9.9.9\",\"assets\":[{\"name\":\"CampusLink.zip\",\"browser_download_url\":\"https://example.com/CampusLink.zip\",\"size\":100},{\"name\":\"CampusLink.exe\",\"browser_download_url\":\"https://example.com/CampusLink.exe\",\"size\":200}]}";

            using (var portal = new MockPortal(req => req.Path == "/api/latest"
                ? MockResponse.Json(apiJson)
                : new MockResponse { Status = 404 }))
            {
                var service = new UpdateService(log, "Thomas455/CampusLink",
                    "http://127.0.0.1:" + portal.Port + "/api/latest", null, "1.0.0");
                var info = service.CheckAsync().GetAwaiter().GetResult();

                Check("API 检查到新版本并优先选 exe 附件", () =>
                {
                    Assert(info.Checked, "应标记已检查");
                    Assert(info.HasUpdate, "应发现更新：" + info.Message);
                    Equal("9.9.9", info.LatestVersion, "最新版本");
                    Assert(info.Asset != null && info.Asset.Name == "CampusLink.exe", "应优先选择 exe 附件");
                    Assert(info.Asset.Url.EndsWith("CampusLink.exe"), "附件地址");
                    Assert(info.ReleaseNotes.Contains("自动更新"), "发布说明");
                    Equal("https://github.com/Thomas455/CampusLink/releases/tag/v9.9.9", info.HtmlUrl, "发布页地址");
                });
            }

            using (var portal = new MockPortal(req => MockResponse.Json(
                "{\"tag_name\":\"v0.0.1\",\"name\":\"old\",\"body\":\"\",\"html_url\":\"x\",\"assets\":[]}")))
            {
                var service = new UpdateService(log, "Thomas455/CampusLink",
                    "http://127.0.0.1:" + portal.Port + "/api/latest", null, "1.0.0");
                var info = service.CheckAsync().GetAwaiter().GetResult();
                Check("版本不比当前新时提示已是最新", () =>
                {
                    Assert(info.Checked, "应标记已检查");
                    Assert(!info.HasUpdate, "不应提示更新");
                    Assert(info.Message.Contains("已是最新版本"), "提示文案：" + info.Message);
                });
            }
        }

        private static void TestUpdateCheckFallback(LogService log)
        {
            using (var portal = new MockPortal(req =>
            {
                if (req.Path == "/api/latest") return new MockResponse { Status = 403, Reason = "Forbidden" };
                if (req.Path == "/Thomas455/CampusLink/releases/latest")
                    return MockResponse.Redirect(Absolute(req, "/Thomas455/CampusLink/releases/tag/v9.8.7"));
                return new MockResponse { Status = 404 };
            }))
            {
                var webBase = "http://127.0.0.1:" + portal.Port;
                var service = new UpdateService(log, "Thomas455/CampusLink", webBase + "/api/latest", webBase, "1.0.0");
                var info = service.CheckAsync().GetAwaiter().GetResult();

                Check("API 限流时改用发布页 302 兜底", () =>
                {
                    Assert(info.Checked, "应标记已检查");
                    Assert(info.HasUpdate, "应发现更新：" + info.Message);
                    Equal("9.8.7", info.LatestVersion, "最新版本");
                    Assert(info.Asset != null && info.Asset.Url.Contains("/releases/download/v9.8.7/CampusLink.exe"),
                        "应按约定推断附件地址：" + (info.Asset == null ? "null" : info.Asset.Url));
                });
            }

            // 没有 Release 时 GitHub 会把 latest 跳到 /releases
            using (var portal = new MockPortal(req =>
            {
                if (req.Path == "/api/latest") return new MockResponse { Status = 403, Reason = "Forbidden" };
                if (req.Path == "/Thomas455/CampusLink/releases/latest")
                    return MockResponse.Redirect(Absolute(req, "/Thomas455/CampusLink/releases"));
                return new MockResponse { Status = 404 };
            }))
            {
                var webBase = "http://127.0.0.1:" + portal.Port;
                var service = new UpdateService(log, "Thomas455/CampusLink", webBase + "/api/latest", webBase, "1.0.0");
                var info = service.CheckAsync().GetAwaiter().GetResult();
                Check("没有 Release 时提示未发布而不是乱识别版本", () =>
                {
                    Assert(!info.HasUpdate, "不应提示更新");
                    Assert(info.Message.Contains("还没有发布版本"), "提示文案：" + info.Message + " checked=" + info.Checked);
                });
            }
        }

        private static void TestUpdateDownload(LogService log)
        {
            var exeBytes = new byte[300 * 1024];
            for (var i = 0; i < exeBytes.Length; i++) exeBytes[i] = (byte)(i % 251);

            using (var portal = new MockPortal(req =>
            {
                if (req.Path == "/CampusLink.exe")
                {
                    return new MockResponse
                    {
                        Body = exeBytes,
                        Headers = { ["Content-Type"] = "application/octet-stream" }
                    };
                }
                return new MockResponse { Status = 404 };
            }))
            {
                var service = new UpdateService(log, "Thomas455/CampusLink", "http://127.0.0.1:" + portal.Port + "/api");
                var info = new UpdateInfo
                {
                    HasUpdate = true,
                    LatestVersion = "9.9.9",
                    Asset = new UpdateAsset
                    {
                        Name = "CampusLink.exe",
                        Url = "http://127.0.0.1:" + portal.Port + "/CampusLink.exe",
                        Size = exeBytes.Length
                    }
                };

                var reports = new List<double>();
                var progress = new SyncProgress(reports);
                var path = service.DownloadAsync(info, progress, System.Threading.CancellationToken.None)
                    .GetAwaiter().GetResult();

                Check("下载更新包并上报进度", () =>
                {
                    Assert(File.Exists(path), "下载文件不存在");
                    Assert(new FileInfo(path).Length == exeBytes.Length, "下载大小不一致");
                    Assert(reports.Count > 0, "没有进度回调");
                    Assert(reports[reports.Count - 1] >= 99.0, "进度应到 100，实际 " + reports[reports.Count - 1]);
                    try { File.Delete(path); } catch { }
                });
            }

            // zip 包自动解出 exe
            var pkgDir = Path.Combine(Path.GetTempPath(), "campuslink-pkg-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(pkgDir);
            File.WriteAllBytes(Path.Combine(pkgDir, "CampusLink.exe"), new byte[] { 1, 2, 3, 4, 5 });
            var zipPath = Path.Combine(Path.GetTempPath(), "campuslink-test-" + Guid.NewGuid().ToString("N") + ".zip");
            System.IO.Compression.ZipFile.CreateFromDirectory(pkgDir, zipPath);

            using (var portal = new MockPortal(req => req.Path == "/pkg.zip"
                ? new MockResponse { Body = File.ReadAllBytes(zipPath) }
                : new MockResponse { Status = 404 }))
            {
                var service = new UpdateService(log, "Thomas455/CampusLink", "http://127.0.0.1:" + portal.Port + "/api");
                var info = new UpdateInfo
                {
                    HasUpdate = true,
                    LatestVersion = "9.9.9",
                    Asset = new UpdateAsset
                    {
                        Name = "CampusLink.zip",
                        Url = "http://127.0.0.1:" + portal.Port + "/pkg.zip"
                    }
                };

                var path = service.DownloadAsync(info, null, System.Threading.CancellationToken.None)
                    .GetAwaiter().GetResult();

                Check("zip 更新包自动解出 exe", () =>
                {
                    Assert(path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase), "应返回 exe 路径：" + path);
                    Assert(File.Exists(path), "解出的 exe 不存在");
                });
            }

            try { File.Delete(zipPath); Directory.Delete(pkgDir, true); } catch { }
        }

        private static void TestUpdateMultiSource(LogService log)
        {
            // GitCode 列表接口：返回两个版本，取最高
            var gitcodeJson = "[{\"tag_name\":\"v1.0.5\",\"name\":\"old\",\"body\":\"\",\"html_url\":\"x\",\"assets\":[]}," +
                              "{\"tag_name\":\"v1.3.0\",\"name\":\"new\",\"body\":\"GitCode 发布说明\",\"html_url\":\"https://gitcode.com/Thomas455/CampusLink/releases/tag/v1.3.0\"," +
                              "\"assets\":[{\"name\":\"CampusLink.exe\",\"browser_download_url\":\"https://gitcode.com/dl/CampusLink.exe\",\"size\":10}]}]";
            var githubJson = "{\"tag_name\":\"v1.2.0\",\"name\":\"gh\",\"body\":\"GitHub 发布说明\",\"html_url\":\"https://github.com/Thomas455/CampusLink/releases/tag/v1.2.0\",\"assets\":[{\"name\":\"CampusLink.exe\",\"browser_download_url\":\"https://github.com/dl/CampusLink.exe\",\"size\":10}]}";

            using (var portal = new MockPortal(req =>
            {
                if (req.Path == "/gitcode") return MockResponse.Json(gitcodeJson);
                if (req.Path == "/github") return MockResponse.Json(githubJson);
                return new MockResponse { Status = 404 };
            }))
            {
                var webBase = "http://127.0.0.1:" + portal.Port;
                var gitcode = UpdateService.CreateSource("GitCode", "Thomas455/CampusLink", webBase, webBase + "/gitcode");
                gitcode.ApiReturnsArray = true;
                var github = UpdateService.CreateSource("GitHub", "Thomas455/CampusLink", webBase, webBase + "/github");
                var service = new UpdateService(log, new[] { gitcode, github }, "1.0.0");
                var info = service.CheckAsync().GetAwaiter().GetResult();

                Check("多源检查：取版本最高的源（GitCode 1.3.0 > GitHub 1.2.0）", () =>
                {
                    Assert(info.HasUpdate, "应发现更新：" + info.Message);
                    Equal("1.3.0", info.LatestVersion, "最新版本");
                    Equal("GitCode", info.SourceName, "来源");
                    Assert(info.Asset != null && info.Asset.Url.Contains("gitcode.com"), "应使用 GitCode 附件");
                    Assert(info.AlternateAssets.Count == 0, "低版本源的附件不应作为备选");
                });
            }

            // GitCode 旧、GitHub 新 → 选 GitHub，并把 GitCode 同版本附件作为备选
            using (var portal = new MockPortal(req =>
            {
                if (req.Path == "/gitcode") return MockResponse.Json(
                    "[{\"tag_name\":\"v1.2.0\",\"body\":\"\",\"html_url\":\"x\",\"assets\":[{\"name\":\"CampusLink.exe\",\"browser_download_url\":\"https://gitcode.com/dl/CampusLink.exe\"}]}]");
                if (req.Path == "/github") return MockResponse.Json(
                    "{\"tag_name\":\"v1.5.0\",\"name\":\"gh\",\"body\":\"\",\"html_url\":\"https://github.com/x\",\"assets\":[{\"name\":\"CampusLink.exe\",\"browser_download_url\":\"https://github.com/dl/CampusLink.exe\"}]}");
                return new MockResponse { Status = 404 };
            }))
            {
                var webBase = "http://127.0.0.1:" + portal.Port;
                var gitcode = UpdateService.CreateSource("GitCode", "Thomas455/CampusLink", webBase, webBase + "/gitcode");
                gitcode.ApiReturnsArray = true;
                var github = UpdateService.CreateSource("GitHub", "Thomas455/CampusLink", webBase, webBase + "/github");
                var service = new UpdateService(log, new[] { gitcode, github }, "1.0.0");
                var info = service.CheckAsync().GetAwaiter().GetResult();

                Check("多源检查：GitHub 版本更高时选 GitHub", () =>
                {
                    Equal("1.5.0", info.LatestVersion, "最新版本");
                    Equal("GitHub", info.SourceName, "来源");
                    Assert(info.AlternateAssets.Count == 0, "低版本源的附件不应作为备选");
                });
            }

            // GitCode API 空数组 + tag 兜底
            using (var portal = new MockPortal(req =>
            {
                if (req.Path == "/gitcode") return MockResponse.Json("[]");
                if (req.Path.EndsWith("/info/refs", StringComparison.Ordinal))
                    return MockResponse.Text("001e# service=git-upload-pack\n0000" +
                                             "003f4c2f5624ac0597f382192e487ba92bee refs/tags/v2.0.0\n");
                return new MockResponse { Status = 404 };
            }))
            {
                var webBase = "http://127.0.0.1:" + portal.Port;
                var gitcode = UpdateService.CreateSource("GitCode", "Thomas455/CampusLink", webBase, webBase + "/gitcode");
                gitcode.ApiReturnsArray = true;
                var service = new UpdateService(log, new[] { gitcode }, "1.0.0");
                var info = service.CheckAsync().GetAwaiter().GetResult();

                Check("GitCode 无 Release 时用 tag 兜底发现新版本（无附件提示）", () =>
                {
                    Assert(info.HasUpdate, "应从 tag 发现更新");
                    Equal("2.0.0", info.LatestVersion, "最新版本");
                    Assert(info.Asset == null, "没有附件");
                    Assert(info.Message.Contains("安装包附件"), "应提示缺少附件：" + info.Message);
                });
            }
        }

        private static void TestUpdateAlternateAsset(LogService log)
        {
            var payload = new byte[] { 9, 8, 7, 6 };
            using (var portal = new MockPortal(req => req.Path == "/good.exe"
                ? new MockResponse { Body = payload }
                : new MockResponse { Status = 404 }))
            {
                var baseUrl = "http://127.0.0.1:" + portal.Port;
                var service = new UpdateService(log, "Thomas455/CampusLink", baseUrl + "/api");
                var info = new UpdateInfo
                {
                    HasUpdate = true,
                    LatestVersion = "2.0.0",
                    Asset = new UpdateAsset { Name = "CampusLink.exe", Url = baseUrl + "/missing.exe" }
                };
                info.AlternateAssets.Add(new UpdateAsset { Name = "CampusLink.exe", Url = baseUrl + "/good.exe" });

                var path = service.DownloadAsync(info, null, System.Threading.CancellationToken.None)
                    .GetAwaiter().GetResult();

                Check("主下载地址失败时自动尝试备选源附件", () =>
                {
                    Assert(File.Exists(path), "下载文件不存在");
                    Equal("4", new FileInfo(path).Length.ToString(), "文件大小");
                    try { File.Delete(path); } catch { }
                });
            }
        }

        /// <summary>同步记录进度，避免 Progress&lt;T&gt; 在线程池上异步回调导致断言时还没写完。</summary>
        private sealed class SyncProgress : IProgress<double>
        {
            private readonly List<double> _values;

            public SyncProgress(List<double> values)
            {
                _values = values;
            }

            public void Report(double value)
            {
                _values.Add(value);
            }
        }

        private static string Absolute(MockRequest request, string path)
        {
            var host = request.Headers.TryGetValue("Host", out var value) ? value : "127.0.0.1";
            return "http://" + host + path;
        }

        private static void TestProbeAndLogin(LogService log)
        {
            using (var portal = new MockPortal(req =>
            {
                if (req.Path == "/")
                {
                    return MockResponse.Redirect(Absolute(req, "/eportal/index.jsp?c=ACSW&v=1&mac=aa-bb-cc-dd-ee-ff&ip=10.1.2.3&ssid=Test"));
                }
                if (req.Path.StartsWith("/eportal/index.jsp", StringComparison.Ordinal))
                {
                    return MockResponse.Text(
                        "<html><body><form>"
                        + "<input type=\"hidden\" id=\"passwordEncrypt\" name=\"passwordEncrypt\" value=\"false\" />"
                        + "<input type=\"hidden\" id=\"publicKeyExponent\" name=\"publicKeyExponent\" value=\"\" />"
                        + "</form></body></html>");
                }
                if (req.Path == "/eportal/InterFace.do")
                {
                    if (req.Query.Contains("method=login"))
                    {
                        return MockResponse.Json("{\"userIndex\":\"UI-123\",\"result\":\"success\",\"message\":\"\",\"keepaliveInterval\":60,\"validCodeUrl\":\"\"}");
                    }
                    if (req.Query.Contains("method=keepalive")) return MockResponse.Json("{\"result\":\"success\"}");
                    if (req.Query.Contains("method=registerMac")) return MockResponse.Json("{\"result\":\"success\",\"message\":\"\"}");
                }
                return new MockResponse { Status = 404, Reason = "Not Found" };
            }))
            {
                var client = new PortalClient("127.0.0.1:" + portal.Port, log, null);
                var profile = new AccountProfile
                {
                    UserId = "a b",
                    Password = "p&w",
                    PortalHost = "127.0.0.1:" + portal.Port
                };

                var probe = client.ProbeAsync(new[] { "http://127.0.0.1:" + portal.Port + "/" }, "test").GetAwaiter().GetResult();
                Check("探测拿到 queryString", () =>
                {
                    Assert(probe.Ok, "探测失败：" + probe.Detail);
                    Assert(probe.NeedsAuth, "应标记需要认证");
                    Equal("c=ACSW&v=1&mac=aa-bb-cc-dd-ee-ff&ip=10.1.2.3&ssid=Test", probe.QueryString, "queryString");
                    Equal("aa-bb-cc-dd-ee-ff", probe.Mac, "MAC");
                });

                var page = client.GetLoginPageAsync(probe.QueryString, "test").GetAwaiter().GetResult();
                Check("登录页解析 passwordEncrypt", () =>
                {
                    Equal("false", page.PasswordEncrypt, "passwordEncrypt");
                    Assert(!page.HasRsaKey, "不应有 RSA 公钥");
                });

                var login = client.LoginAsync(profile, probe.QueryString, page, "", "auto", ">", probe.Mac, "test")
                    .GetAwaiter().GetResult();
                Check("登录成功并返回 userIndex", () =>
                {
                    Assert(login.Success, "登录失败：" + login.Message);
                    Equal("UI-123", login.UserIndex, "userIndex");
                    Assert(login.KeepaliveInterval == 60, "keepaliveInterval 应为 60");
                });

                Check("登录请求体是双重 URL 编码", () =>
                {
                    var request = portal.Requests.Find(r => r.Query.Contains("method=login"));
                    Assert(request != null, "没有收到 login 请求");
                    Assert(request.Headers.TryGetValue("Content-Type", out var contentType)
                           && contentType.Contains("application/x-www-form-urlencoded"), "Content-Type 不对：" + contentType);
                    Assert(request.Body.Contains("userId=a%2520b"), "userId 未双重编码：" + request.Body);
                    Assert(request.Body.Contains("password=p%2526w"), "password 未双重编码：" + request.Body);
                    Assert(request.Body.Contains("queryString=c%253DACSW%2526v%253D1"), "queryString 未双重编码：" + request.Body);
                    Assert(request.Body.Contains("passwordEncrypt=false"), "passwordEncrypt 字段缺失");
                });

                var keepalive = client.KeepaliveAsync("UI-123", "test").GetAwaiter().GetResult();
                Check("保活成功", () => Assert(keepalive.Success, "保活失败：" + keepalive.Message));

                var register = client.RegisterMacAsync("aa-bb-cc-dd-ee-ff", "UI-123", "test").GetAwaiter().GetResult();
                Check("无感注册成功且 MAC 已归一化", () =>
                {
                    Assert(register.Success, "注册失败：" + register.Message);
                    var request = portal.Requests.Find(r => r.Query.Contains("method=registerMac"));
                    Assert(request != null, "没有收到 registerMac");
                    Assert(request.Body.Contains("mac=AABBCCDDEEFF"), "MAC 未归一化：" + request.Body);
                    Assert(request.Body.Contains("userIndex=UI-123"), "userIndex 缺失：" + request.Body);
                });
            }
        }

        private static void TestAlreadyOnline(LogService log)
        {
            using (var portal = new MockPortal(req => MockResponse.Redirect(Absolute(req, "/eportal/redirectortosuccess.jsp"))))
            {
                var client = new PortalClient("127.0.0.1:" + portal.Port, log, null);
                var probe = client.ProbeAsync(new[] { "http://127.0.0.1:" + portal.Port + "/" }, "test").GetAwaiter().GetResult();
                Check("已在线时探测标记 Online", () =>
                {
                    Assert(probe.Ok, "探测失败");
                    Assert(probe.Online, "应标记在线");
                    Assert(!probe.NeedsAuth, "不应要求认证");
                });
            }
        }

        private static void TestLoginFailureMessage(LogService log)
        {
            using (var portal = new MockPortal(req =>
            {
                if (req.Path == "/") return MockResponse.Redirect(Absolute(req, "/eportal/index.jsp?c=ACSW&mac=AABBCCDDEEFF"));
                if (req.Path.StartsWith("/eportal/index.jsp", StringComparison.Ordinal))
                    return MockResponse.Text("<input name=\"passwordEncrypt\" value=\"false\" />");
                if (req.Path == "/eportal/InterFace.do")
                    return MockResponse.Json("{\"result\":\"fail\",\"message\":\"WEB认证设备未注册，请确认SAM+/portal/设备上的参数配置是否一致\"}");
                return new MockResponse { Status = 404 };
            }))
            {
                var client = new PortalClient("127.0.0.1:" + portal.Port, log, null);
                var profile = new AccountProfile { UserId = "20231234", Password = "x", PortalHost = "127.0.0.1:" + portal.Port };
                var probe = client.ProbeAsync(new[] { "http://127.0.0.1:" + portal.Port + "/" }, "test").GetAwaiter().GetResult();
                var page = client.GetLoginPageAsync(probe.QueryString, "test").GetAwaiter().GetResult();
                var login = client.LoginAsync(profile, probe.QueryString, page, "", "auto", ">", probe.Mac, "test")
                    .GetAwaiter().GetResult();
                Check("设备未注册时给出排错提示", () =>
                {
                    Assert(!login.Success, "应登录失败");
                    Assert(login.Message.Contains("queryString"), "错误提示应包含 queryString 排查建议：" + login.Message);
                });
            }
        }

        private static void TestPasswordEncryptMode(LogService log)
        {
            const string modulus =
                "94dd2a8675fb779e6b9f7103698634cd400f27a154afa67af6166a43fc26417c1d6c48e0e21e8b4b0c6a6b3a3d0a3f52a3f4e4f4b3a4d5c6b7a8f9e0d1c2b3a4b5c6d7e8f9a0b1c2d3e4f5a6b7c8d9e0f1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a6b7c8d9e0f1a2b3c4d5e6f7a8b9";

            using (var portal = new MockPortal(req =>
            {
                if (req.Path == "/") return MockResponse.Redirect(Absolute(req, "/eportal/index.jsp?c=ACSW&mac=AABBCCDDEEFF"));
                if (req.Path.StartsWith("/eportal/index.jsp", StringComparison.Ordinal))
                {
                    return MockResponse.Text(
                        "<input type=\"hidden\" name=\"passwordEncrypt\" value=\"true\" />"
                        + "<input type=\"hidden\" name=\"publicKeyExponent\" value=\"10001\" />"
                        + "<input type=\"hidden\" name=\"publicKeyModulus\" value=\"" + modulus + "\" />");
                }
                if (req.Path == "/eportal/InterFace.do")
                    return MockResponse.Json("{\"userIndex\":\"UI-9\",\"result\":\"success\",\"keepaliveInterval\":120}");
                return new MockResponse { Status = 404 };
            }))
            {
                var client = new PortalClient("127.0.0.1:" + portal.Port, log, null);
                var profile = new AccountProfile { UserId = "20231234", Password = "plainpwd", PortalHost = "127.0.0.1:" + portal.Port };
                var probe = client.ProbeAsync(new[] { "http://127.0.0.1:" + portal.Port + "/" }, "test").GetAwaiter().GetResult();
                var page = client.GetLoginPageAsync(probe.QueryString, "test").GetAwaiter().GetResult();
                Check("登录页识别 RSA 公钥", () => Assert(page.HasRsaKey, "未识别到 RSA 公钥"));

                var login = client.LoginAsync(profile, probe.QueryString, page, "", "auto", ">", probe.Mac, "test")
                    .GetAwaiter().GetResult();
                Check("passwordEncrypt=true 时提交密文", () =>
                {
                    Assert(login.Success, "登录失败：" + login.Message);
                    var request = portal.Requests.Find(r => r.Query.Contains("method=login"));
                    Assert(request != null, "没有收到 login 请求");
                    Assert(request.Body.Contains("passwordEncrypt=true"), "应声明 passwordEncrypt=true");
                    Assert(!request.Body.Contains("password=plainpwd"), "不应提交明文密码");
                    var expected = PortalClient.DoubleEncode(
                        RuijieRsa.EncryptPasswordWithMac("10001", modulus, "plainpwd", probe.Mac, ">"));
                    Assert(request.Body.Contains("password=" + expected), "RSA 密文与预期不一致");
                });
            }
        }

        private static void TestOtherEndpoints(LogService log)
        {
            using (var portal = new MockPortal(req =>
            {
                if (req.Path == "/eportal/InterFace.do")
                {
                    if (req.Query.Contains("method=getOnlineUserInfo"))
                        return MockResponse.Json("{\"result\":\"success\",\"userId\":\"20231234\",\"userIp\":\"10.1.2.3\",\"userMac\":\"aabbccddeeff\",\"userUrl\":\"http://x\"}");
                    if (req.Query.Contains("method=getServices"))
                        return MockResponse.Json("{\"result\":\"success\",\"defaultService\":\"internet\",\"services\":\"[{\\\"serviceName\\\":\\\"s1\\\",\\\"serviceShowName\\\":\\\"服务一\\\"}]\",\"isService\":\"true\"}");
                    if (req.Query.Contains("method=pageInfo"))
                        return MockResponse.Json("{\"result\":\"success\",\"isAutoLogin\":\"true\",\"announcement\":\"公告\",\"validCodeUrl\":\"/eportal/validcode\"}");
                    if (req.Query.Contains("method=logoutByUserIdAndPass")) return MockResponse.Json("{\"result\":\"success\"}");
                    if (req.Query.Contains("method=logout")) return MockResponse.Json("{\"result\":\"success\"}");
                    if (req.Query.Contains("method=cancelMacWithUserNameAndMac")) return MockResponse.Json("{\"result\":\"success\"}");
                }
                return new MockResponse { Status = 404 };
            }))
            {
                var client = new PortalClient("127.0.0.1:" + portal.Port, log, null);

                var info = client.GetOnlineUserInfoAsync("UI-1", "test").GetAwaiter().GetResult();
                Check("getOnlineUserInfo 解析字段", () =>
                {
                    Assert(info.Success, "查询失败：" + info.Message);
                    Equal("10.1.2.3", info.UserIp, "userIp");
                    Equal("aabbccddeeff", info.UserMac, "userMac");
                });

                var services = client.GetServicesAsync("c=ACSW&mac=AABBCCDDEEFF", "test").GetAwaiter().GetResult();
                Check("getServices 解析服务数组", () =>
                {
                    Assert(services.Success, "查询失败：" + services.Message);
                    Equal("internet", services.DefaultService, "defaultService");
                    Assert(services.Services.Count == 1, "服务数量应为 1，实际 " + services.Services.Count);
                    Equal("s1", services.Services[0].ServiceName, "serviceName");
                    Equal("服务一", services.Services[0].ShowName, "serviceShowName");
                });

                var pageInfo = client.GetPageInfoAsync("c=ACSW", "test").GetAwaiter().GetResult();
                Check("pageInfo 解析 isAutoLogin/公告", () =>
                {
                    Assert(pageInfo.Success, "查询失败：" + pageInfo.Message);
                    Assert(pageInfo.IsAutoLogin, "isAutoLogin 应为 true");
                    Equal("公告", pageInfo.Announcement, "announcement");
                });

                var logoutAll = client.LogoutAllAsync("20231234", "p&ss", "test").GetAwaiter().GetResult();
                Check("logoutByUserIdAndPass 单次编码", () =>
                {
                    Assert(logoutAll.Success, "下线失败：" + logoutAll.Message);
                    var request = portal.Requests.Find(r => r.Query.Contains("method=logoutByUserIdAndPass"));
                    Assert(request.Body.Contains("pass=p%26ss"), "pass 应单次编码：" + request.Body);
                });

                var cancel = client.CancelMacAsync("20231234", "aa:bb:cc:dd:ee:ff", "test").GetAwaiter().GetResult();
                Check("cancelMac 提交归一化 MAC", () =>
                {
                    Assert(cancel.Success, "解绑失败：" + cancel.Message);
                    var request = portal.Requests.Find(r => r.Query.Contains("method=cancelMacWithUserNameAndMac"));
                    Assert(request.Body.Contains("usermac=AABBCCDDEEFF"), "MAC 未归一化：" + request.Body);
                });
            }
        }

        private static void TestProbeFailure(LogService log)
        {
            int closedPort;
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                closedPort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }

            var client = new PortalClient("127.0.0.1:" + closedPort, log, null);
            var probe = client.ProbeAsync(new[] { "http://127.0.0.1:" + closedPort + "/" }, "test").GetAwaiter().GetResult();
            Check("连接失败时探测返回错误而不是抛异常", () =>
            {
                Assert(!probe.Ok, "应返回失败");
                Assert(!string.IsNullOrWhiteSpace(probe.Detail), "应带失败说明");
            });
        }

        private static void TestSelfService(LogService log)
        {
            var captchaPng = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4 };
            var mode = "success";

            using (var portal = new MockPortal(req =>
            {
                if (req.Path.EndsWith("login_self.jsf", StringComparison.Ordinal))
                {
                    return MockResponse.Text(
                        "<html><input type=\"hidden\" id=\"publicKey\" value=\"10001&amp;94dd2a8675fb779e6b9f7103698634cd400f27a154afa67af6166a43fc26417\" /></html>");
                }
                if (req.Path.EndsWith("verifycode.jsp", StringComparison.Ordinal))
                {
                    return new MockResponse { Body = captchaPng, Headers = { ["Content-Type"] = "image/png" } };
                }
                if (req.Path.EndsWith("login_judge.jsf", StringComparison.Ordinal))
                {
                    if (mode == "success") return MockResponse.Text("self.location='module/webcontent/web/index_self.jsf';");
                    if (mode == "captcha") return MockResponse.Text("self.location='login_self.jsf?verifyError=true&name=x';");
                    return MockResponse.Text("self.location='login_self.jsf?errorMsg=%D3%C3%BB%A7%B2%BB%B4%E6%D4%DA%BB%F2%C3%DC%C2%EB%B4%ED%CE%F3';");
                }
                return new MockResponse { Status = 404 };
            }))
            {
                var client = new SelfServiceClient("http://127.0.0.1:" + portal.Port + "/selfservice", log);
                var challenge = client.BeginLoginAsync("test").GetAwaiter().GetResult();
                Check("自助服务取到公钥和验证码", () =>
                {
                    Assert(challenge.CaptchaImage != null && challenge.CaptchaImage.Length > 0, "验证码为空：" + challenge.Detail);
                    Assert(challenge.PublicKey.Contains("10001"), "公钥异常：" + challenge.PublicKey);
                    Assert(challenge.PublicKey.Contains("&"), "公钥应包含分隔符：" + challenge.PublicKey);
                });

                var ok = client.LoginAsync("20231234", "pwd", challenge.PublicKey, "AB12", "test").GetAwaiter().GetResult();
                Check("自助服务登录成功判定", () => Assert(ok.Success, "应判定成功：" + ok.Message));

                mode = "captcha";
                var captcha = client.LoginAsync("20231234", "pwd", challenge.PublicKey, "AB12", "test").GetAwaiter().GetResult();
                Check("自助服务验证码错误识别", () =>
                {
                    Assert(!captcha.Success, "应判失败");
                    Assert(captcha.CaptchaWrong, "应识别为验证码错误：" + captcha.Message);
                });

                mode = "password";
                var wrong = client.LoginAsync("20231234", "pwd", challenge.PublicKey, "AB12", "test").GetAwaiter().GetResult();
                Check("自助服务密码错误识别", () =>
                {
                    Assert(!wrong.Success, "应判失败");
                    Assert(wrong.PasswordWrong, "应识别为密码错误：" + wrong.Message);
                });
            }
        }

        private static string Reverse(string text)
        {
            var chars = text.ToCharArray();
            Array.Reverse(chars);
            return new string(chars);
        }

        private static int BitLength(BigInteger value)
        {
            if (value.IsZero) return 0;
            var bytes = value.ToByteArray();
            var length = bytes.Length;
            while (length > 1 && bytes[length - 1] == 0) length--;
            var last = bytes[length - 1];
            var leadingZeros = 0;
            for (var mask = 0x80; mask > 0; mask >>= 1)
            {
                if ((last & mask) != 0) break;
                leadingZeros++;
            }
            return (length - 1) * 8 + (8 - leadingZeros);
        }

        private static BigInteger ModInverse(BigInteger a, BigInteger m)
        {
            BigInteger t = 0, newT = 1, r = m, newR = a;
            while (newR != 0)
            {
                var quotient = r / newR;
                var tmp = t - quotient * newT; t = newT; newT = tmp;
                tmp = r - quotient * newR; r = newR; newR = tmp;
            }
            if (t < 0) t += m;
            return t;
        }
    }
}
