using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// 额度来源统一契约。实现只能返回已脱敏的 UsageSnapshot，不能把凭据或原始响应传给上层。
/// </summary>
internal interface IUsageProvider : IDisposable
{
    string ProviderId { get; }
    Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken);
    string GetCredentialDiagnostic();
    string GetNetworkDiagnostic();
}
