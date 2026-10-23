using System;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace CampusLink.Services
{
    /// <summary>
    /// 复现锐捷门户前端 security.js 里的 RSAUtils.encryptedString：
    /// - 无填充（no padding）的老式 RSA
    /// - 明文按 16 位 digit 小端打包，每块 chunkSize = 2 * digitCount 个字符
    /// - 输出为定长十六进制串（digitCount * 4 个字符）
    ///
    /// 门户规则（passwordEncrypt=true 时）：明文 = 密码 + 分隔符 + MAC，先把整串反转，再加密。
    /// 自助服务规则：明文 = 密码反转（不拼 MAC）。
    /// </summary>
    public static class RuijieRsa
    {
        /// <summary>按门户规则加密：reverse(password + separator + mac)。</summary>
        public static string EncryptPasswordWithMac(string exponentHex, string modulusHex, string password, string mac, string separator)
        {
            var plain = (password ?? "") + (separator ?? "") + NormalizeMac(mac);
            return EncryptReversed(exponentHex, modulusHex, plain);
        }

        /// <summary>按自助服务规则加密：reverse(password)。</summary>
        public static string EncryptPassword(string exponentHex, string modulusHex, string password)
        {
            return EncryptReversed(exponentHex, modulusHex, password ?? "");
        }

        /// <summary>把字符串反转后做无填充 RSA，返回十六进制密文。</summary>
        public static string EncryptReversed(string exponentHex, string modulusHex, string plain)
        {
            var chars = ReverseChars(plain ?? "");
            return EncryptChars(exponentHex, modulusHex, chars);
        }

        private static string EncryptChars(string exponentHex, string modulusHex, char[] input)
        {
            var exponent = ParseHex(exponentHex);
            var modulus = ParseHex(modulusHex);
            if (modulus <= BigInteger.Zero) throw new ArgumentException("RSA 模数为空或非法");
            if (exponent <= BigInteger.Zero) throw new ArgumentException("RSA 指数为空或非法");

            // 与 jsbn 的 biHighIndex(modulus) 对齐：16 位 digit 的个数
            var digitCount = (GetBitLength(modulus) + 15) / 16;
            var chunkSize = 2 * digitCount;

            var builder = new StringBuilder(Math.Max(chunkSize * 4, 1));

            var index = 0;
            while (index < input.Length)
            {
                var bytes = new byte[chunkSize * 2];
                for (var j = 0; j < chunkSize; j++)
                {
                    var low = index < input.Length ? input[index++] : '\0';
                    var high = index < input.Length ? input[index++] : '\0';
                    // 对应 JS: digits[j] = a[k++] + (a[k++] << 8)，即小端 16 位 digit
                    var digit = (int)low + ((int)high << 8);
                    bytes[j * 2] = (byte)(digit & 0xFF);
                    bytes[j * 2 + 1] = (byte)((digit >> 8) & 0xFF);
                }

                var block = FromLittleEndian(bytes);
                var encrypted = BigInteger.ModPow(block, exponent, modulus);
                var hex = encrypted.ToString("x", CultureInfo.InvariantCulture);
                if (hex.Length < digitCount * 4)
                {
                    hex = new string('0', digitCount * 4 - hex.Length) + hex;
                }
                builder.Append(hex);
            }

            return builder.ToString();
        }

        private static char[] ReverseChars(string text)
        {
            var chars = text.ToCharArray();
            var length = chars.Length;
            for (var i = 0; i < length / 2; i++)
            {
                var tmp = chars[i];
                chars[i] = chars[length - 1 - i];
                chars[length - 1 - i] = tmp;
            }
            return chars;
        }

        private static BigInteger ParseHex(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) throw new ArgumentException("RSA 参数为空");
            var cleaned = hex.Trim().Replace(" ", "").Replace("\r", "").Replace("\n", "");
            if (cleaned.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) cleaned = cleaned.Substring(2);
            // 首位 >= 8 时 BigInteger 会当成负数，补一个 0 保持正数语义
            if (cleaned.Length > 0 && Convert.ToInt32(cleaned[0].ToString(), 16) >= 8)
            {
                cleaned = "0" + cleaned;
            }
            return BigInteger.Parse(cleaned, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        private static BigInteger FromLittleEndian(byte[] bytes)
        {
            // 末尾补 0，确保不按二进制补码解析成负数
            var buffer = new byte[bytes.Length + 1];
            Array.Copy(bytes, buffer, bytes.Length);
            return new BigInteger(buffer);
        }

        private static int GetBitLength(BigInteger value)
        {
            if (value.IsZero) return 0;
            var bytes = value.ToByteArray(); // 小端，可能带一个 0x00 符号字节
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

        /// <summary>把 MAC 归一化为 12 位大写十六进制（无分隔符），符合 registerMac 的常见格式。</summary>
        public static string NormalizeMac(string mac)
        {
            if (string.IsNullOrWhiteSpace(mac)) return "";
            var builder = new StringBuilder(12);
            foreach (var ch in mac)
            {
                if (Uri.IsHexDigit(ch)) builder.Append(char.ToUpperInvariant(ch));
            }
            return builder.ToString();
        }
    }
}
