using System;
using System.Security.Cryptography;
using System.Text;

namespace ClassAgent.Config
{
    /// <summary>
    /// 插件本地配置的加密封装。密文包含随机 salt/IV 和 HMAC，避免把 API key 以明文写入配置或日志。
    /// </summary>
    internal static class SecretStore
    {
        private const string Prefix = "CLASSAGENT-ENC-v1:";
        private static readonly byte[] KeyMaterial = Encoding.UTF8.GetBytes(
            "com.icc.class-agent/plugin-config/v1");

        public static string ProtectText(string plain)
        {
            plain ??= "";
            var salt = RandomNumberGenerator.GetBytes(16);
            var iv = RandomNumberGenerator.GetBytes(16);
            var data = Encoding.UTF8.GetBytes(plain);
            DeriveKeys(salt, out var encryptionKey, out var authenticationKey);
            try
            {
                byte[] cipher;
                using (var aes = Aes.Create())
                {
                    aes.Key = encryptionKey;
                    aes.IV = iv;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    using var encryptor = aes.CreateEncryptor();
                    cipher = encryptor.TransformFinalBlock(data, 0, data.Length);
                }

                var payload = Combine(salt, iv, cipher);
                using var hmac = new HMACSHA256(authenticationKey);
                return Prefix + Convert.ToBase64String(Combine(payload, hmac.ComputeHash(payload)));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(data);
                CryptographicOperations.ZeroMemory(encryptionKey);
                CryptographicOperations.ZeroMemory(authenticationKey);
            }
        }

        public static bool TryUnprotectText(string protectedText, out string plain)
        {
            plain = "";
            if (string.IsNullOrWhiteSpace(protectedText)
                || !protectedText.StartsWith(Prefix, StringComparison.Ordinal)) return false;

            try
            {
                var payload = Convert.FromBase64String(protectedText.Substring(Prefix.Length));
                const int saltLength = 16;
                const int ivLength = 16;
                const int macLength = 32;
                if (payload.Length <= saltLength + ivLength + macLength) return false;

                var cipherLength = payload.Length - saltLength - ivLength - macLength;
                var salt = Slice(payload, 0, saltLength);
                var iv = Slice(payload, saltLength, ivLength);
                var cipher = Slice(payload, saltLength + ivLength, cipherLength);
                var suppliedMac = Slice(payload, saltLength + ivLength + cipherLength, macLength);
                DeriveKeys(salt, out var encryptionKey, out var authenticationKey);
                try
                {
                    var signed = Slice(payload, 0, payload.Length - macLength);
                    using var hmac = new HMACSHA256(authenticationKey);
                    if (!CryptographicOperations.FixedTimeEquals(suppliedMac, hmac.ComputeHash(signed)))
                        return false;

                    using var aes = Aes.Create();
                    aes.Key = encryptionKey;
                    aes.IV = iv;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    using var decryptor = aes.CreateDecryptor();
                    var decrypted = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
                    try { plain = Encoding.UTF8.GetString(decrypted); }
                    finally { CryptographicOperations.ZeroMemory(decrypted); }
                    return true;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(encryptionKey);
                    CryptographicOperations.ZeroMemory(authenticationKey);
                }
            }
            catch (CryptographicException) { return false; }
            catch (FormatException) { return false; }
        }

        public static string TryUnprotect(string cipher)
            => TryUnprotectText(cipher, out var plain) ? plain : "";

        private static void DeriveKeys(byte[] salt, out byte[] encryptionKey, out byte[] authenticationKey)
        {
            var keys = Rfc2898DeriveBytes.Pbkdf2(
                KeyMaterial, salt, 100000, HashAlgorithmName.SHA256, 64);
            encryptionKey = Slice(keys, 0, 32);
            authenticationKey = Slice(keys, 32, 32);
            CryptographicOperations.ZeroMemory(keys);
        }

        private static byte[] Combine(params byte[][] arrays)
        {
            var length = 0;
            foreach (var array in arrays) length += array.Length;
            var result = new byte[length];
            var offset = 0;
            foreach (var array in arrays)
            {
                Buffer.BlockCopy(array, 0, result, offset, array.Length);
                offset += array.Length;
            }
            return result;
        }

        private static byte[] Slice(byte[] source, int offset, int length)
        {
            var result = new byte[length];
            Buffer.BlockCopy(source, offset, result, 0, length);
            return result;
        }
    }
}
