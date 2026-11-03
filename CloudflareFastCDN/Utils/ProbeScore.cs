namespace CloudflareFastCDN.Utils
{
    /// <summary>
    /// 一次 HTTP 探测的完整结果。
    ///
    /// 关键点：同一个 HttpClient 连续探测时，**第 1 次是冷启动**（新建 TCP + TLS 握手），
    /// 之后几次是连接复用（只发请求）。这是两个完全不同的分布，混在一起平均会造出
    /// 一个既不代表首访、也不代表稳态的中间值，而且 cv 会被抬高到 0.7 以上，
    /// 把真正的质量差异淹没掉。所以这里把两者分开保存。
    /// </summary>
    public sealed class ProbeStats
    {
        /// <summary>成功的探测次数。</summary>
        public int Success { get; init; }

        /// <summary>计划探测的总次数。</summary>
        public int Total { get; init; }

        /// <summary>第 1 次探测是否成功。</summary>
        public bool ColdSucceeded { get; init; }

        /// <summary>冷启动延时（第 1 次探测），失败时为 0。</summary>
        public double ColdMs { get; init; }

        /// <summary>第 2 次起的成功探测延时（稳态）。</summary>
        public IReadOnlyList<double> WarmMs { get; init; } = Array.Empty<double>();

        public string Error { get; init; } = string.Empty;

        public bool SkipNode { get; init; }
    }

    /// <summary>
    /// 统一评分器。选优、心跳、复检必须全部走这里，保证任何两个分数都是同一个量纲。
    ///
    /// 评分只用**稳态**探测：
    ///   稳态延时 = 第 2 次起成功探测的中位数（对个别慢探测不敏感）
    ///   评分     = 稳态延时 × (1 + 稳态 cv) × (1 + 丢包率)
    ///
    /// 冷启动延时单独记录，用于展示「首次访问要多久」，不参与排名 ——
    /// 因为所有可用 CF IP 的冷启动都挤在 400~470ms，排不出有意义的差别。
    /// </summary>
    public static class ProbeScore
    {
        public static ProbeResult Compute(ProbeStats stats)
        {
            var lossRate = stats.Total > 0 ? 1d - (double)stats.Success / stats.Total : 1d;

            var warm = stats.WarmMs;
            if (warm.Count == 0)
            {
                // 只有一次成功探测（或一次都没成功）。
                // 有冷启动值就拿它当稳态兜底，否则整条记录作废。
                var fallback = stats.ColdSucceeded ? stats.ColdMs : 0;
                return new ProbeResult
                {
                    ColdMs = stats.ColdMs,
                    ColdSucceeded = stats.ColdSucceeded,
                    WarmMs = fallback,
                    CvWarm = 0,
                    LossRate = lossRate,
                    Score = fallback > 0 ? fallback * (1 + lossRate) : 0,
                    Valid = fallback > 0
                };
            }

            var median = Median(warm);
            var mean = warm.Average();
            var cv = 0d;
            if (warm.Count > 1 && mean > 0)
            {
                var variance = warm.Average(x => Math.Pow(x - mean, 2));
                cv = Math.Sqrt(variance) / mean;
            }

            return new ProbeResult
            {
                ColdMs = stats.ColdMs,
                ColdSucceeded = stats.ColdSucceeded,
                WarmMs = median,
                CvWarm = cv,
                LossRate = lossRate,
                Score = median * (1 + cv) * (1 + lossRate),
                Valid = true
            };
        }

        public static double Median(IReadOnlyList<double> values)
        {
            if (values.Count == 0)
                return 0;

            var sorted = values.OrderBy(x => x).ToArray();
            var mid = sorted.Length / 2;
            return sorted.Length % 2 == 1
                ? sorted[mid]
                : (sorted[mid - 1] + sorted[mid]) / 2;
        }
    }

    public sealed class ProbeResult
    {
        /// <summary>冷启动延时（首访体验），仅用于展示。</summary>
        public double ColdMs { get; init; }

        public bool ColdSucceeded { get; init; }

        /// <summary>稳态延时中位数，排名的主依据。</summary>
        public double WarmMs { get; init; }

        /// <summary>稳态探测的离散系数。</summary>
        public double CvWarm { get; init; }

        public double LossRate { get; init; }

        /// <summary>综合评分，越低越好。</summary>
        public double Score { get; init; }

        /// <summary>数据是否可用（至少要有一个成功探测）。</summary>
        public bool Valid { get; init; }
    }
}
