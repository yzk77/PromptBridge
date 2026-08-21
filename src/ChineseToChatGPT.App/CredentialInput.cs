using ChineseToChatGPT.Core;

namespace ChineseToChatGPT.App;

public sealed record CredentialInput(
    string? GoogleApiKey = null,
    string? AlibabaAccessKeyId = null,
    string? AlibabaAccessKeySecret = null,
    string? BaiduAppId = null,
    string? BaiduSecretKey = null,
    string? TencentSecretId = null,
    string? TencentSecretKey = null,
    string? TencentRegion = null)
{
    public IEnumerable<(string Name, string Value)> PresentValues()
    {
        if (!string.IsNullOrWhiteSpace(GoogleApiKey))
        {
            yield return (CredentialNames.GoogleApiKey, GoogleApiKey.Trim());
        }

        if (!string.IsNullOrWhiteSpace(AlibabaAccessKeyId))
        {
            yield return (CredentialNames.AlibabaAccessKeyId, AlibabaAccessKeyId.Trim());
        }

        if (!string.IsNullOrWhiteSpace(AlibabaAccessKeySecret))
        {
            yield return (CredentialNames.AlibabaAccessKeySecret, AlibabaAccessKeySecret.Trim());
        }

        if (!string.IsNullOrWhiteSpace(BaiduAppId))
        {
            yield return (CredentialNames.BaiduAppId, BaiduAppId.Trim());
        }

        if (!string.IsNullOrWhiteSpace(BaiduSecretKey))
        {
            yield return (CredentialNames.BaiduSecretKey, BaiduSecretKey.Trim());
        }

        if (!string.IsNullOrWhiteSpace(TencentSecretId))
        {
            yield return (CredentialNames.TencentSecretId, TencentSecretId.Trim());
        }

        if (!string.IsNullOrWhiteSpace(TencentSecretKey))
        {
            yield return (CredentialNames.TencentSecretKey, TencentSecretKey.Trim());
        }
    }

    public string? GetOverride(string credentialName) =>
        PresentValues().FirstOrDefault(item => item.Name == credentialName).Value;
}
