using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace CloudflareFastCDN.Utils
{
    public class SubnetScoreRecord
    {
        public string Subnet { get; set; } = string.Empty;
        public double BestScore { get; set; }
        public int Hits { get; set; }
        public int Failures { get; set; }
        public string LastSeen { get; set; } = string.Empty;
    }

    /// <summary>
    /// 网段分数表：给每个 /24 网段一个分数，抽样时按 1/分数^3 加权。
    ///
    /// 分数规则（越低的网段越值得抽）：
    ///   命中过（Hits &gt; 0）  → 该网段出过的最好评分
    ///   只失败过（Failures &gt; 0）→ MissScore（900）
    ///   从没抽到过        → DefaultScore（500）
    ///
    /// 全量网段清单启动时从 ip.txt 展开（约 6000 个 /24），
    /// 未记录的一律按 DefaultScore 处理，所以「探索」是自然发生的，不需要额外分支。
    /// </summary>
    public class SubnetScoreTable
    {
        /// <summary>从没抽到过的网段默认分。</summary>
        private const double DefaultScore = 500;

        /// <summary>抽到过但 HTTP 验证全失败的网段分数（比默认分差）。</summary>
        private const double MissScore = 900;

        /// <summary>权重 = 1 / 分数^ScorePower，指数越大越集中在好网段。</summary>
        private const int ScorePower = 3;

        /// <summary>记录条数上限，仅防脏数据无限膨胀（全量网段约 6000）。</summary>
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
        /// 网段的当前分数：命中过取最好成绩，只失败过给 MissScore，没抽到过给 DefaultScore。
        /// </summary>
        private double ScoreOf(string subnet)
        {
            if (_entries.TryGetValue(subnet, out var record))
            {
                if (record.Hits > 0 && record.BestScore > 0)
                    return record.BestScore;
                if (record.Failures > 0)
                    return MissScore;
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
        /// 记录一次失败：该网段抽到的 IP 没能通过 HTTP 验证。
        /// 只加失败计数，不覆盖已有的好成绩。
        /// </summary>
        public void RecordMiss(IPAddress ip)
        {
            var subnet = GetSubnet24(ip);
            if (subnet == null)
                return;

            if (_entries.TryGetValue(subnet, out var record))
            {
                record.Failures++;
                record.LastSeen = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            }
            else
            {
                _entries[subnet] = new SubnetScoreRecord
                {
                    Subnet = subnet,
                    Hits = 0,
                    Failures = 1,
                    LastSeen = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                };
            }

            InvalidateWeights();
            TrimIfNeeded();
        }

        private void TrimIfNeeded()
        {
            if (_entries.Count <= MaxEntries)
                return;

            var worst = _entries.Values
                .OrderByDescending(item => item.Hits > 0 ? item.BestScore : MissScore)
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
