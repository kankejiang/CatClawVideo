using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CatClawVideo.Core.Services;

/// <summary>
/// TVBox JS Spider 的加解密宿主（对齐 <c>crawler.js.Crypto</c> + <c>Global.rsaEncrypt/rsaDecrypt</c>）。
/// 全部用 System.Security.Cryptography 原生实现，无第三方依赖。
/// </summary>
public static class SpiderCrypto
{
    // ═══════════════════ AES ═══════════════════

    /// <summary>
    /// JS: <c>aesX(mode, encrypt, input, inBase64, key, iv, outBase64)</c>。
    /// mode 形如 "AES/CBC/PKCS5"（内部补 "Padding"）、"AES/ECB/PKCS5"、"AES/CBC/ZeroPadding" 等。
    /// key/iv 不足 16 字节补零到 16（对齐 Java 版 Arrays.copyOf 语义——**同时会截断超长 key**）。
    /// </summary>
    public static string AesX(string mode, bool encrypt, string input, bool inBase64, string key, string? iv, bool outBase64)
    {
        try
        {
            var keyBuf = new byte[16];
            Encoding.UTF8.GetBytes(key ?? "").AsSpan(0, Math.Min(16, Encoding.UTF8.GetByteCount(key ?? ""))).CopyTo(keyBuf);
            var ivBuf = new byte[16];
            if (!string.IsNullOrEmpty(iv))
                Encoding.UTF8.GetBytes(iv).AsSpan(0, Math.Min(16, Encoding.UTF8.GetByteCount(iv))).CopyTo(ivBuf);

            var m = ParseMode(mode);
            using var aes = Aes.Create();
            aes.Key = keyBuf;
            aes.IV = ivBuf;
            aes.Mode = m.CipherMode;
            aes.Padding = m.PaddingMode;
            aes.BlockSize = 128;

            using var transform = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor();
            var inBytes = inBase64 ? UrlSafeBase64Decode(input) : Encoding.UTF8.GetBytes(input);
            var outBytes = transform.TransformFinalBlock(inBytes, 0, inBytes.Length);
            return outBase64 ? Convert.ToBase64String(outBytes) : Encoding.UTF8.GetString(outBytes);
        }
        catch
        {
            return "";
        }
    }

    private static (CipherMode CipherMode, PaddingMode PaddingMode) ParseMode(string mode)
    {
        var upper = (mode ?? "").Replace("Padding", "", StringComparison.OrdinalIgnoreCase).ToUpperInvariant();
        var parts = upper.Split('/');
        var cipher = parts.Length > 1 ? parts[1] : "CBC";
        var padding = parts.Length > 2 ? parts[2] : "PKCS5";
        var cm = cipher switch
        {
            "ECB" => CipherMode.ECB,
            "CFB" => CipherMode.CFB,
            "OFB" => CipherMode.OFB,
            _ => CipherMode.CBC,
        };
        var pm = padding switch
        {
            "PKCS5" or "PKCS7" => PaddingMode.PKCS7,
            "ZERO" or "NONE" => PaddingMode.None,
            "ISO10126" => PaddingMode.ISO10126,
            "ANSIX923" => PaddingMode.ANSIX923,
            _ => PaddingMode.PKCS7,
        };
        return (cm, pm);
    }

    // ═══════════════════ RSA（协议方法 rsaX） ═══════════════════

    /// <summary>
    /// JS: <c>rsaX(pub, encrypt, input, inBase64, key, outBase64)</c>。
    /// 固定 RSA/ECB/PKCS1，pub=公钥操作（加密/验签用钥），分段循环（encrypt 块长 modulus/8-11，decrypt modulus/8）。
    /// </summary>
    public static string RsaX(bool pub, bool encrypt, string input, bool inBase64, string key, bool outBase64)
        => RsaCore(input, inBase64, key, outBase64, encrypt, pub,
            useOaep: false, oaepHash: null, segment: true, respectBlockOption: false, autoBlock: true);

    /// <summary>JS: <c>rsaEncrypt(data, key[, options])</c>——带 options 的扩展形态（config/type/long/block）。</summary>
    public static string RsaEncrypt(string data, string key, JsonElement? options)
    {
        var (config, type, segLong, autoBlock) = ParseRsaOptions(options);
        var useOaep = config?.Contains("OAEP", StringComparison.OrdinalIgnoreCase) == true;
        var hash = config?.Contains("SHA-256", StringComparison.OrdinalIgnoreCase) == true ||
                   config?.Contains("SHA256", StringComparison.OrdinalIgnoreCase) == true
            ? "SHA256"
            : config?.Contains("SHA-1", StringComparison.OrdinalIgnoreCase) == true ||
              config?.Contains("SHA1", StringComparison.OrdinalIgnoreCase) == true
                ? "SHA1"
                : null;
        return RsaCore(data, false, key, outBase64: true,
            encrypt: type == 1, pub: type == 1,
            useOaep: useOaep, oaepHash: hash, segment: segLong == 2,
            respectBlockOption: true, autoBlock: autoBlock);
    }

    /// <summary>JS: <c>rsaDecrypt(data, key[, options])</c>。</summary>
    public static string RsaDecrypt(string data, string key, JsonElement? options)
    {
        var (config, type, segLong, autoBlock) = ParseRsaOptions(options);
        var useOaep = config?.Contains("OAEP", StringComparison.OrdinalIgnoreCase) == true;
        var hash = config?.Contains("SHA-256", StringComparison.OrdinalIgnoreCase) == true ||
                   config?.Contains("SHA256", StringComparison.OrdinalIgnoreCase) == true
            ? "SHA256"
            : config?.Contains("SHA-1", StringComparison.OrdinalIgnoreCase) == true ||
              config?.Contains("SHA1", StringComparison.OrdinalIgnoreCase) == true
                ? "SHA1"
                : null;
        return RsaCore(data, true, key, outBase64: false,
            encrypt: type != 2, pub: type != 2, // 默认私钥解密
            useOaep: useOaep, oaepHash: hash, segment: segLong == 2,
            respectBlockOption: true, autoBlock: autoBlock);
    }

    private static (string? Config, int Type, int Long, bool AutoBlock) ParseRsaOptions(JsonElement? options)
    {
        string? config = null;
        var type = 1;
        var segLong = 1;
        var autoBlock = true;
        if (options is { ValueKind: JsonValueKind.Object } o)
        {
            try
            {
                if (o.TryGetProperty("config", out var c) && c.ValueKind == JsonValueKind.String)
                    config = c.GetString();
                if (o.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.Number)
                    type = t.GetInt32();
                if (o.TryGetProperty("long", out var l) && l.ValueKind == JsonValueKind.Number)
                    segLong = l.GetInt32();
                if (o.TryGetProperty("block", out var bl) && bl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    autoBlock = bl.GetBoolean();
            }
            catch { }
        }
        return (config, type, segLong, autoBlock);
    }

    private static string RsaCore(string input, bool inBase64, string key, bool outBase64,
        bool encrypt, bool pub, bool useOaep, string? oaepHash,
        bool segment, bool respectBlockOption, bool autoBlock)
    {
        try
        {
            using var rsa = RSA.Create();
            var der = Convert.FromBase64String(StripPem(key));
            if (pub) rsa.ImportSubjectPublicKeyInfo(der, out _);
            else rsa.ImportPkcs8PrivateKey(der, out _);

            var modulusBytes = rsa.KeySize / 8;
            var inBytes = inBase64 ? UrlSafeBase64Decode(input) : Encoding.UTF8.GetBytes(input);

            var fOaep = useOaep
                ? oaepHash == "SHA256" ? RSAEncryptionPadding.OaepSHA256
                : oaepHash == "SHA1" ? RSAEncryptionPadding.OaepSHA1
                : RSAEncryptionPadding.OaepSHA1
                : RSAEncryptionPadding.Pkcs1;

            byte[] outBytes;
            var blockLen = encrypt
                ? Math.Max(1, modulusBytes - (useOaep ? 42 : 11)) // PKCS1 加密最大块 = modulus-11；OAEP-SHA1 = modulus-42
                : modulusBytes;
            if (respectBlockOption && !autoBlock) blockLen = 117;

            if (encrypt)
            {
                if (segment && inBytes.Length > blockLen)
                {
                    var buf = new MemoryStream();
                    for (var i = 0; i < inBytes.Length; i += blockLen)
                    {
                        var chunk = inBytes[i..Math.Min(i + blockLen, inBytes.Length)];
                        var part = rsa.Encrypt(chunk, fOaep);
                        buf.Write(part);
                    }
                    outBytes = buf.ToArray();
                }
                else
                {
                    outBytes = rsa.Encrypt(inBytes, fOaep);
                }
            }
            else
            {
                if (segment && inBytes.Length > modulusBytes)
                {
                    var buf = new MemoryStream();
                    for (var i = 0; i < inBytes.Length; i += modulusBytes)
                    {
                        var chunk = inBytes[i..Math.Min(i + modulusBytes, inBytes.Length)];
                        var part = rsa.Decrypt(chunk, fOaep);
                        buf.Write(part);
                    }
                    outBytes = buf.ToArray();
                }
                else
                {
                    outBytes = rsa.Decrypt(inBytes, fOaep);
                }
            }

            return outBase64 ? Convert.ToBase64String(outBytes) : Encoding.UTF8.GetString(outBytes);
        }
        catch
        {
            return "";
        }
    }

    /// <summary>剥 PEM 头尾与换行（对齐 Crypto.generateKey）。</summary>
    private static string StripPem(string key) =>
        (key ?? "")
        .Replace("\r\n", "").Replace("\n", "")
        .Replace("-----BEGIN PUBLIC KEY-----", "").Replace("-----END PUBLIC KEY-----", "")
        .Replace("-----BEGIN PRIVATE KEY-----", "").Replace("-----END PRIVATE KEY-----", "")
        .Replace("-----BEGIN RSA PUBLIC KEY-----", "").Replace("-----END RSA PUBLIC KEY-----", "")
        .Replace("-----BEGIN RSA PRIVATE KEY-----", "").Replace("-----END RSA PRIVATE KEY-----", "");

    /// <summary>URL-safe Base64 解码（对齐 Java 版 `_→/`、`-→+` 语义）。</summary>
    private static byte[] UrlSafeBase64Decode(string input)
    {
        var s = input.Replace("_", "/").Replace("-", "+").Trim();
        s = s.PadRight((s.Length + 3) / 4 * 4, '=');
        return Convert.FromBase64String(s);
    }
}
