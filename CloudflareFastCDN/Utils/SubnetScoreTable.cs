using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace CloudflareFastCDN.Utils
{
    public class SubnetScoreRecord
    {
        public string Subnet { get; set; } = string.Empty;

        /// <summary>HTTP 验证出的最好评分（权威值，有它就优先用它）。</summary>
        public double BestScore { get; set; }

        /// <summary>HTTP 验证通过次数。</summary>
        public int Hits { get; set; }

        /// <summary>TCP 快筛粗分的运行平均。</summary>
        public double TcpScore { get; set; }

        /// <summary>TCP 粗测累计样本数。</summary>
        public int TcpSamples { get; set; }

        public string LastSeen { get; set; } = string.Empty;
    }

    /// <summary>
    /// 网段分数表：给每个 /24 网段一个分数，抽样时按 1/分数^3 加权。
    ///
    /// 分数来源分两级：
    ///   1. HTTP 验证过（Hits &gt; 0）→ 用该网段出过的最好评分，这是权威值
    ///   2. 只有 TCP 粗测        → 用粗分的运行平均
    ///   3. 从没抽到过           → DefaultScore（500）
    ///
    /// 粗分不是精确测量，只用来做方向性判断：本轮 1000 个抽样按延时排序，
    /// 分位数线性映射到 [240, 900]，中位数正好等于 500。
    /// 这样「未探索」和「中游网段」权重相当，缩圈是一轮一轮慢慢收的。
    ///
    /// 全量网段清单启动时从 ip.txt 展开（约 5955 个 /24）。
    /// </summary>
    public class SubnetScoreTable
    {
        /// <summary>从没抽到过的网段默认分。</summary>
        private const double DefaultScore = 500;

        /// <summary>权重 = 1 / 分数^ScorePower，指数越大越集中在好网段。</summary>
        private const int ScorePower = 3;

        /// <summary>TCP 粗分运行平均的窗口：最多算最近 8 个样本，保证能跟着网络变化调整。</summary>
        private const int TcpAverageWindow = 8;

        /// <summary>记录条数上限，仅防脏数据无限膨胀（全量网段约 5955）。</summary>
        private const int MaxEntries = 12000;

        private static readonly string TableFile =
            File.Exists("/.dockerenv") ? "/data/subnet_scores.json" : "subnet_scores.json";

        private Dictionary<string, SubnetScoreRecord> _entries = new();

        /// <summary>全量 /24 网段清单（来自 ip.txt）。</summary>
        private List<string> _subnets = new();

        /// <summary>累积权重缓存，用于加权抽样时二分查找。</summary>
        private double[]? _cumulative;
        private double _totalWeight;
        private int _cumulativeCount = -1;

        public int Count => _entries.Count;
        public int TotalSubnets => _subnets.Count;

        /// <summary>
        /// 加载历史记录，并从 ip.txt 展开全量 /24 网段清单。
        /// </summary>
        public void Load(string ipFile = "ip.txt")
        {
            LoadEntries();
            LoadAllSubnets(ipFile);
        }

        private void LoadEntries()
        {
            if (!File.Exists(TableFile))
                return;

            try
            {
                var json = File.ReadAllText(TableFile);
                var list = JsonSerializer.Deserialize<List<SubnetScoreRecord>>(json) ?? new();
                _entries = list
                    .Where(item => !string.IsNullOrEmpty(item.Subnet))
                    .GroupBy(item => item.Subnet)
                    .ToDictionary(group => group.Key, group => group.First());
            }
            catch
            {
                _entries = new();
            }
        }

        /// <summary>
        /// 把 ip.txt 里的 CIDR 展开成全部 /24 网段。
        /// </summary>
        private void LoadAllSubnets(string ipFile)
        {
            _subnets = new List<string>();
            _cumulative = null;
            _cumulativeCount = -1;

            if (!File.Exists(ipFile))
            {
                Console.WriteLine($"网段分数表：找不到 {ipFile}，全量网段清单为空");
                return;
            }

            var seen = new HashSet<string>();
            try
            {
                foreach (var raw in File.ReadLines(ipFile))
                {
                    var line = raw.Trim();
                    if (line.Length == 0)
                        continue;

                    var parts = line.Split('/');
                    if (!IPAddress.TryParse(parts[0], out var addr))
                        continue;
                    if (addr.AddressFamily != AddressFamily.InterNetwork)
                        continue;
                    // 裸 IP 按 /32 处理，归到它所在的 /24
                    var prefix = 32;
                    if (parts.Length == 2 && !int.TryParse(parts[1], out prefix))
                        continue;
                    if (parts.Length > 2)
                        continue;
                    // 防御：/8 以下会展开出海量网段，正常 CF 段是 /13~/22
                    if (prefix < 8)
                        continue;

                    var bytes = addr.GetAddressBytes();
                    var value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
                    var hostBits = 32 - prefix;
                    var mask = hostBits >= 32 ? 0u : (0xFFFFFFFFu << hostBits);
                    var network = value & mask;
                    var firstSubnet = network & 0xFFFFFF00u;
                    var blocks = prefix >= 24 ? 1 : 1 << (24 - prefix);

                    for (var i = 0; i < blocks; i++)
                    {
                        var v = firstSubnet + (uint)(i * 256);
                        var subnet = $"{(v >> 24) & 0xFF}.{(v >> 16) & 0xFF}.{(v >> 8) & 0xFF}.0/24";
                        if (seen.Add(subnet))
                            _subnets.Add(subnet);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"网段分数表：展开网段清单失败 {ex.Message}");
            }
        }

        /// <summary>
        /// 网段的当前分数：有 HTTP 成绩用 HTTP 的，其次用 TCP 粗测平均，都没有给默认分。
        /// </summary>
        private double ScoreOf(string subnet)
        {
            if (_entries.TryGetValue(subnet, out var record))
            {
                if (record.Hits > 0 && record.BestScore > 0)
                    return record.BestScore;
                if (record.TcpSamples > 0 && record.TcpScore > 0)
                    return record.TcpScore;
            }
            return DefaultScore;
        }

        /// <summary>
        /// 记录一次命中：该网段出了个通过验证的 IP。
        /// </summary>
        public void RecordHit(IPAddress ip, double score)
        {
            var subnet = GetSubnet24(ip);
            if (subnet == null)
                return;

            if (_entries.TryGetValue(subnet, out var record))
            {
                record.Hits++;
                if (score < record.BestScore || record.BestScore <= 0)
                    record.BestScore = score;
                record.LastSeen = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            }
            else
            {
                _entries[subnet] = new SubnetScoreRecord
                {
                    Subnet = subnet,
                    BestScore = score,
                    Hits = 1,
                    LastSeen = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                };
            }

            InvalidateWeights();
            TrimIfNeeded();
        }

        /// <summary>
        /// 记录一次粗测样本（TCP 快筛延时换算的粗分，或 HTTP 验证失败记的差评）。
        /// 做限定窗口的运行平均，单个样本的抖动不会把整段 /24 带偏。
        /// </summary>
        public void RecordSample(IPAddress ip, double coarseScore)
        {
            var subnet = GetSubnet24(ip);
            if (subnet == null)
                return;

            if (!_entries.TryGetValue(subnet, out var record))
            {
                record = new SubnetScoreRecord { Subnet = subnet };
                _entries[subnet] = record;
            }

            var window = Math.Min(record.TcpSamples, TcpAverageWindow - 1);
            record.TcpScore = (record.TcpScore * window + coarseScore) / (window + 1);
            record.TcpSamples++;
            record.LastSeen = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            InvalidateWeights();
            TrimIfNeeded();
        }

        private void TrimIfNeeded()
        {
            if (_entries.Count <= MaxEntries)
                return;

            var worst = _entries.Values
                .OrderByDescending(item => item.Hits > 0 && item.BestScore > 0
                    ? item.BestScore
                    : item.TcpSamples > 0 && item.TcpScore > 0 ? item.TcpScore : DefaultScore)
                .ThenBy(item => item.Hits)
                .First();
            _entries.Remove(worst.Subnet);
            InvalidateWeights();
        }

        private void InvalidateWeights()
        {
            _cumulative = null;
            _cumulativeCount = -1;
        }

        private void EnsureWeights()
        {
            if (_cumulative != null && _cumulativeCount == _subnets.Count)
                return;

            var cumulative = new double[_subnets.Count];
            var acc = 0d;
            for (var i = 0; i < _subnets.Count; i++)
            {
                var score = ScoreOf(_subnets[i]);
                acc += 1.0 / Math.Pow(score, ScorePower);
                cumulative[i] = acc;
            }

            _cumulative = cumulative;
            _totalWeight = acc;
            _cumulativeCount = _subnets.Count;
        }

        /// <summary>
        /// 按网段分数加权抽样：分数越低的网段越容易被抽到。
        /// </summary>
        public List<IPAddress> SampleWeighted(int count, Random rng)
        {
            var result = new List<IPAddress>();
            if (count <= 0 || _subnets.Count == 0)
                return result;

            EnsureWeights();
            if (_totalWeight <= 0 || _cumulative == null)
                return result;

            var picked = new HashSet<string>();
            var guard = count * 20;

            while (result.Count < count && guard-- > 0)
            {
                var r = rng.NextDouble() * _totalWeight;
                var idx = Array.BinarySearch(_cumulative, r);
                if (idx < 0)
                    idx = ~idx;
                if (idx >= _subnets.Count)
                    idx = _subnets.Count - 1;

                var subnet = _subnets[idx];
                if (!picked.Add(subnet))
                    continue;

                var ip = GetRandomIpFromSubnet(subnet, rng);
                if (ip != null)
                    result.Add(ip);
            }

            return result;
        }

        public static string? GetSubnet24(IPAddress ip)
        {
            if (ip.AddressFamily != AddressFamily.InterNetwork)
                return null;

            var bytes = ip.GetAddressBytes();
            return $"{bytes[0]}.{bytes[1]}.{bytes[2]}.0/24";
        }

        private static IPAddress? GetRandomIpFromSubnet(string subnet, Random rng)
        {
            var slashIdx = subnet.IndexOf('/');
            var baseStr = slashIdx >= 0 ? subnet[..slashIdx] : subnet;
            if (!IPAddress.TryParse(baseStr, out var baseIp))
                return null;

            var bytes = baseIp.GetAddressBytes();
            bytes[3] = (byte)rng.Next(1, 255);
            return new IPAddress(bytes);
        }

        public void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(TableFile);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(_entries.Values.ToList(), new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(TableFile, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"保存网段分数表失败: {ex.Message}");
            }
        }
    }
}
