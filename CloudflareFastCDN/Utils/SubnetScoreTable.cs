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
        public string LastSeen { get; set; } = string.Empty;
    }

    /// <summary>
    /// 网段分数表：记录每个 /24 网段出过的最好评分和命中次数，
    /// 用于指导下一轮抽样（高分网段多抽，低分网段少抽或跳过）。
    /// </summary>
    public class SubnetScoreTable
    {
        private const int MaxEntries = 6000;
        private const int WeightedPoolSize = 200;

        private static readonly string TableFile =
            File.Exists("/.dockerenv") ? "/data/subnet_scores.json" : "subnet_scores.json";

        private Dictionary<string, SubnetScoreRecord> _entries = new();

        public int Count => _entries.Count;

        public void Load()
        {
            if (!File.Exists(TableFile))
                return;

            try
            {
                var json = File.ReadAllText(TableFile);
                var list = JsonSerializer.Deserialize<List<SubnetScoreRecord>>(json) ?? new();
                _entries = list.ToDictionary(item => item.Subnet, item => item);
            }
            catch
            {
                _entries = new();
            }
        }

        /// <summary>
        /// 记录一次命中：该网段出了个进前 N 的 IP。
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
                {
                    record.BestScore = score;
                }
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

            if (_entries.Count > MaxEntries)
            {
                var worst = _entries.Values
                    .OrderByDescending(item => item.BestScore)
                    .ThenBy(item => item.Hits)
                    .First();
                _entries.Remove(worst.Subnet);
            }
        }

        /// <summary>
        /// 按权重抽样：从高分网段里随机取 IP。
        /// 权重 = Hits / BestScore，出好 IP 多且分数低的网段权重高。
        /// </summary>
        public List<IPAddress> SampleWeighted(int count, Random rng)
        {
            if (count <= 0 || _entries.Count == 0)
                return new List<IPAddress>();

            var pool = _entries.Values
                .Where(item => item.BestScore > 0)
                .OrderBy(item => item.BestScore / Math.Max(1, item.Hits))
                .Take(WeightedPoolSize)
                .ToList();

            if (pool.Count == 0)
                return new List<IPAddress>();

            var result = new List<IPAddress>();
            for (int i = 0; i < count; i++)
            {
                var record = pool[rng.Next(pool.Count)];
                var ip = GetRandomIpFromSubnet(record.Subnet, rng);
                if (ip != null)
                {
                    result.Add(ip);
                }
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
