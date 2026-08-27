using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// 将现有官方 ChatGPT/Codex OAuth 查询适配到统一 Provider 契约，迁移期间不改变请求端点和凭据读取规则。
/// </summary>
internal sealed class OfficialUsageProvider : IUsageProvider
{
    private readonly OfficialQuotaService quotaService;
    private bool disposed;

    public string ProviderId
    {
        get { return "chatgpt-codex"; }
    }

    public OfficialUsageProvider()
        : this(new OfficialQuotaService())
    {
    }

    internal OfficialUsageProvider(OfficialQuotaService quotaService)
    {
        if (quotaService == null)
        {
            throw new ArgumentNullException("quotaService");
        }
        this.quotaService = quotaService;
    }

    public async Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken)
    {
        if (disposed)
        {
            return UsageSnapshot.Failure(ProviderId, UsageStatus.UnknownError, "provider_disposed", DateTimeOffset.Now);
        }

        QuotaSnapshot raw = await quotaService.QueryAsync(cancellationToken).ConfigureAwait(true);
        return UsageSnapshot.FromQuotaSnapshot(raw, ProviderId);
    }

    public string GetCredentialDiagnostic()
    {
        return quotaService.GetCredentialDiagnostic();
    }

    public string GetNetworkDiagnostic()
    {
        return quotaService.GetProxyDiagnostic();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        quotaService.Dispose();
    }
}
