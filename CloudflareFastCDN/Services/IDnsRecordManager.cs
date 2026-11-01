namespace CloudflareFastCDN.Services
{
    internal interface IDnsRecordManager
    {
        string ProviderName { get; }

        Task<bool> AddOrUpdateARecord(string fullDomain, string ipAddress);

        /// <summary>
        /// 读取域名当前的 A 记录 IP（直接查服务商 API，避开本地 DNS 缓存）。
        /// 默认实现返回空，表示不支持，调用方回退到 DNS 解析。
        /// </summary>
        Task<IReadOnlyList<string>> GetARecordIps(string fullDomain)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }
}
