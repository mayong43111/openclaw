using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenClaw.Console.Services;

/// <summary>
/// Generates encrypted JSON auth tokens for Apache Guacamole.
/// Format: HMAC-SHA256(IV + ciphertext) ‖ IV ‖ AES-128-CBC(plaintext), base64-encoded.
/// See: https://guacamole.apache.org/doc/gug/json-auth.html
/// </summary>
public class GuacamoleTokenService
{
    private readonly byte[] _key;
    private readonly string _rdpUsername;
    private readonly string _rdpPassword;

    public GuacamoleTokenService(IConfiguration config)
    {
        var hexKey = config["Guacamole:JsonSecretKey"]
            ?? throw new InvalidOperationException("Guacamole:JsonSecretKey not configured");
        _key = Convert.FromHexString(hexKey);

        _rdpUsername = config["Vm:RdpUsername"] ?? "azureuser";
        _rdpPassword = config["Vm:RdpPassword"]
            ?? throw new InvalidOperationException("Vm:RdpPassword not configured");
    }

    /// <summary>
    /// Creates an encrypted JSON auth token for a single RDP connection.
    /// </summary>
    public string CreateToken(string connectionName, string hostname)
    {
        var expires = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds().ToString();

        var payload = new
        {
            username = "console",
            expires,
            connections = new Dictionary<string, object>
            {
                [connectionName] = new
                {
                    protocol = "rdp",
                    parameters = new Dictionary<string, string>
                    {
                        ["hostname"] = hostname,
                        ["port"] = "3389",
                        ["username"] = _rdpUsername,
                        ["password"] = _rdpPassword,
                        ["security"] = "nla",
                        ["ignore-cert"] = "true",
                        ["resize-method"] = "reconnect",
                        ["color-depth"] = "24",
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(payload);
        return Encrypt(json);
    }

    private string Encrypt(string plaintext)
    {
        var json = Encoding.UTF8.GetBytes(plaintext);

        // 1. HMAC-SHA256 signature of the raw JSON
        using var hmac = new HMACSHA256(_key);
        var signature = hmac.ComputeHash(json);

        // 2. Plaintext to encrypt = signature (32 bytes) + JSON
        var combined = new byte[signature.Length + json.Length];
        Buffer.BlockCopy(signature, 0, combined, 0, signature.Length);
        Buffer.BlockCopy(json, 0, combined, signature.Length, json.Length);

        // 3. AES-128-CBC with NULL IV (all zeros) — Guacamole uses the
        //    HMAC prefix as an effective IV, so the actual IV is zeroed.
        using var aes = Aes.Create();
        aes.Key = _key;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.IV = new byte[16]; // NULL IV

        using var encryptor = aes.CreateEncryptor();
        var ciphertext = encryptor.TransformFinalBlock(combined, 0, combined.Length);

        // 4. Base64 encode the ciphertext only (no IV in output)
        return Convert.ToBase64String(ciphertext);
    }
}
