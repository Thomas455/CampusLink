using System;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace CampusLink.Infrastructure
{
    /// <summary>
    /// 门户的响应理论上都是 JSON，但老设备偶发返回带 JS 前缀/尾随字符的内容，
    /// 所以这里做一次宽松解析 + 正则兜底。
    /// </summary>
    public static class JsonHelper
    {
        public static JObject ParseObject(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            var candidate = text.Trim();
            try
            {
                return JObject.Parse(candidate);
            }
            catch
            {
                // 去掉可能存在的 BOM / 前后垃圾字符，截取第一个 { 到最后一个 }
                var start = candidate.IndexOf('{');
                var end = candidate.LastIndexOf('}');
                if (start >= 0 && end > start)
                {
                    var slice = candidate.Substring(start, end - start + 1);
                    try { return JObject.Parse(slice); }
                    catch { }
                }
            }
            return null;
        }

        public static string GetString(JObject obj, string name, string fallback = "")
        {
            if (obj == null) return fallback;
            var token = obj[name];
            if (token == null || token.Type == JTokenType.Null) return fallback;
            var value = token.ToString();
            return string.IsNullOrEmpty(value) ? fallback : value;
        }

        public static bool GetBool(JObject obj, string name, bool fallback = false)
        {
            var value = GetString(obj, name, null);
            if (string.IsNullOrEmpty(value)) return fallback;
            if (bool.TryParse(value, out var b)) return b;
            if (value == "1") return true;
            if (value == "0") return false;
            return fallback;
        }

        public static int GetInt(JObject obj, string name, int fallback = 0)
        {
            var value = GetString(obj, name, null);
            return int.TryParse(value, out var i) ? i : fallback;
        }

        /// <summary>从非 JSON 文本中尽量抠出 "key":"value"。</summary>
        public static string ExtractJsonString(string text, string key)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var m = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : "";
        }
    }
}
