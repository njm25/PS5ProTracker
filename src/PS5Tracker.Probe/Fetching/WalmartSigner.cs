using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PS5Tracker.Probe.Fetching;

/// <summary>
/// Walmart.io affiliate auth headers: an RSA-SHA256 signature over
/// "consumerId\ntimestampMs\nkeyVersion\n", base64-encoded.
/// </summary>
public sealed class WalmartSigner : IDisposable
{
    readonly RSA _rsa = RSA.Create();
    readonly string _consumerId;
    readonly string _keyVersion;

    public WalmartSigner(string consumerId, string keyVersion, string privateKeyPem)
    {
        _consumerId = consumerId;
        _keyVersion = keyVersion;
        _rsa.ImportFromPem(privateKeyPem);
    }

    public IReadOnlyDictionary<string, string> Headers(DateTimeOffset now)
    {
        var ts = now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var payload = Encoding.UTF8.GetBytes($"{_consumerId}\n{ts}\n{_keyVersion}\n");
        var signature = _rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return new Dictionary<string, string>
        {
            ["WM_CONSUMER.ID"] = _consumerId,
            ["WM_CONSUMER.INTIMESTAMP"] = ts,
            ["WM_SEC.KEY_VERSION"] = _keyVersion,
            ["WM_SEC.AUTH_SIGNATURE"] = Convert.ToBase64String(signature),
            ["Accept"] = "application/json",
        };
    }

    public void Dispose() => _rsa.Dispose();
}
