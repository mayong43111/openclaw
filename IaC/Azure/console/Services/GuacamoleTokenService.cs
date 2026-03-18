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
        var data = Encoding.UTF8.GetBytes(plaintext);

        using var aes = Aes.Create();
        aes.Key = _key;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var ciphertext = encryptor.TransformFinalBlock(data, 0, data.Length);

        // payload = IV (16 bytes) + ciphertext
        var payload = new byte[aes.IV.Length + ciphertext.Length];
        Buffer.BlockCopy(aes.IV, 0, payload, 0, aes.IV.Length);
        Buffer.BlockCopy(ciphertext, 0, payload, aes.IV.Length, ciphertext.Length);

        // HMAC-SHA256 signature of the payload, using the same key
        using var hmac = new HMACSHA256(_key);
        var signature = hmac.ComputeHash(payload);

        // Final result: signature (32 bytes) + payload (IV + ciphertext)
        var result = new byte[signature.Length + payload.Length];
        Buffer.BlockCopy(signature, 0, result, 0, signature.Length);
        Buffer.BlockCopy(payload, 0, result, signature.Length, payload.Length);

        return Convert.ToBase64String(result);
    }
}
