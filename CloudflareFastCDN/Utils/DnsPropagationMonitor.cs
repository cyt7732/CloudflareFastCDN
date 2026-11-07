using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudflareFastCDN.Utils
{
    /// <summary>
    /// DNS 传播延迟监控。
    ///
    /// 每次成功写入 DNS 记录后调用 <see cref="NotifyUpdated"/>：以「写入完成的时刻」为起点计时，
    /// 后台按固定间隔持续解析该域名，直到**本机解析命中新 IP**，记录端到端传播耗时；
    /// 若配置了权威 NS，还会直查权威确认「源头已更新」的时间。
    ///
    /// 设计要点：
    ///   • 起点是真实写 DNS 的时刻，不是靠轮询猜，所以精度只受探测间隔影响（默认 5 秒）。
    ///   • 本机解析走系统解析栈（含操作系统 DNS 缓存），测到的就是应用真正拿到新记录的时间。
    ///   • 同一域名若再次更新，旧的监控自动作废（以最新一次为准），避免串味。
    ///   • 结果持久化到 /data/dns_propagation.json（容器）或 .//dns_propagation.json（本地），
    ///     通过 /api/dns-propagation 暴露平均/中位/P95 等统计。
    ///
    /// 可通过环境变量调节：
    ///   DNS_PROPAGATION_ENABLED           是否启用（默认 true）
    ///   DNS_PROPAGATION_POLL_SECONDS      探测间隔秒（默认 5，范围 1~60）
    ///   DNS_PROPAGATION_MAX_WAIT_SECONDS  最长等待秒（默认 1200，即 2 倍 TTL 600）
    ///   DNS_PROPAGATION_AUTH_NS           权威 NS（IP 或主机名，逗号分隔，默认空=不查权威）
    /// </summary>
    internal static class DnsPropagationMonitor
    {
        private static readonly bool Enabled = ReadBool("DNS_PROPAGATION_ENABLED", true);
        private static readonly int PollSeconds = ReadInt("DNS_PROPAGATION_POLL_SECONDS", 5, 1, 60);
        private static readonly int MaxWaitSeconds = ReadInt("DNS_PROPAGATION_MAX_WAIT_SECONDS", 1200, 10, 7200);
        private static readonly string[] AuthNs = (Environment.GetEnvironmentVariable("DNS_PROPAGATION_AUTH_NS") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        private static readonly string DataFile =
            File.Exists("/.dockerenv") ? "/data/dns_propagation.json" : "dns_propagation.json";

        private const int MaxSamples = 500;

        private static readonly object SyncRoot = new();
        private static readonly Dictionary<string, CancellationTokenSource> Active = new(StringComparer.OrdinalIgnoreCase);
        private static DnsPropagationData? _data;

        public static bool IsEnabled => Enabled;
        public static int PollIntervalSeconds => PollSeconds;
        public static int MaxWait => MaxWaitSeconds;

        /// <summary>写 DNS 成功后调用：开始为 domain → ip 计时。</summary>
        public static void NotifyUpdated(string provider, string domain, string ip)
        {
            if (!Enabled || string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(ip))
            {
                return;
            }

            CancellationTokenSource cts;
            DateTimeOffset t0 = DateTimeOffset.Now;
            lock (SyncRoot)
            {
                if (Active.TryGetValue(domain, out var old))
                {
                    try { old.Cancel(); } catch { /* ignore */ }
                }

                cts = new CancellationTokenSource();
                Active[domain] = cts;
            }

            Console.WriteLine($"[DNS传播] 开始计时 {domain} → {ip}（等待本机解析生效）");
            _ = Task.Run(() => TrackAsync(provider, domain, ip, t0, cts));
        }

        private static async Task TrackAsync(string provider, string domain, string targetIp, DateTimeOffset t0, CancellationTokenSource cts)
        {
            var token = cts.Token;
            double? hostDelay = null;
            double? authDelay = null;
            var status = "timeout";
            List<string> authIps = new();

            try
            {
                if (AuthNs.Length > 0)
                {
                    authIps = await ResolveHostTargetsAsync(AuthNs, token);
                }

                while (!token.IsCancellationRequested)
                {
                    var elapsed = (DateTimeOffset.Now - t0).TotalSeconds;

                    // 权威确认（可选）：直查权威 NS，绕过缓存，判断源头是否已是新值
                    if (authIps.Count > 0 && authDelay == null)
                    {
                        foreach (var srv in authIps)
                        {
                            var ips = await QueryAAgainstAsync(srv, domain, token);
                            if (ips != null && ips.Contains(targetIp))
                            {
                                authDelay = (DateTimeOffset.Now - t0).TotalMilliseconds;
                                Console.WriteLine($"[DNS传播] {domain} 权威已确认新IP {targetIp}，用时 {authDelay / 1000.0:0.0}s");
                                break;
                            }
                        }
                    }

                    // 本机解析（含系统缓存）
                    bool hostHit = false;
                    try
                    {
                        var addrs = await Dns.GetHostAddressesAsync(domain, token);
                        hostHit = addrs.Any(a => a.AddressFamily == AddressFamily.InterNetwork && a.ToString() == targetIp);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { /* 解析暂时失败，继续等 */ }

                    if (hostHit)
                    {
                        hostDelay = (DateTimeOffset.Now - t0).TotalMilliseconds;
                        status = "success";
                        var authText = authDelay.HasValue ? $"（权威 {authDelay.Value / 1000.0:0.0}s）" : string.Empty;
                        Console.WriteLine($"[DNS传播] {domain} 本机解析已生效 {targetIp}，端到端 {hostDelay.Value / 1000.0:0.0}s{authText}");
                        break;
                    }

                    if (elapsed >= MaxWaitSeconds)
                    {
                        status = "timeout";
                        Console.WriteLine($"[DNS传播] {domain} 等待 {MaxWaitSeconds}s 仍未在本机解析到 {targetIp}，记为超时");
                        break;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(PollSeconds), token);
                }
            }
            catch (OperationCanceledException)
            {
                status = "canceled";
            }
            catch (Exception ex)
            {
                status = "error";
                Console.WriteLine($"[DNS传播] {domain} 监控异常：{ex.Message}");
            }
            finally
            {
                // 被同域名的新一次更新取代时不落样本（保留最新一次的记录）
                if (status != "canceled")
                {
                    Record(provider, domain, targetIp, hostDelay, authDelay, status);
                }

                lock (SyncRoot)
                {
                    if (Active.TryGetValue(domain, out var cur) && ReferenceEquals(cur, cts))
                    {
                        Active.Remove(domain);
                    }
                }

                try { cts.Dispose(); } catch { /* ignore */ }
            }
        }

        private static void Record(string provider, string domain, string ip, double? hostDelay, double? authDelay, string status)
        {
            lock (SyncRoot)
            {
                EnsureLoaded();
                var d = _data!;
                d.Samples.Add(new DnsPropagationSample
                {
                    Time = DateTimeOffset.Now,
                    Provider = provider,
                    Domain = domain,
                    Ip = ip,
                    HostDelayMs = hostDelay.HasValue ? Math.Round(hostDelay.Value) : null,
                    AuthDelayMs = authDelay.HasValue ? Math.Round(authDelay.Value) : null,
                    Status = status
                });

                if (d.Samples.Count > MaxSamples)
                {
                    d.Samples.RemoveRange(0, d.Samples.Count - MaxSamples);
                }

                Save();
            }
        }

        private static void EnsureLoaded()
        {
            if (_data != null)
            {
                return;
            }

            try
            {
                if (File.Exists(DataFile))
                {
                    _data = JsonSerializer.Deserialize(File.ReadAllText(DataFile), DnsPropagationJsonContext.Default.DnsPropagationData) ?? new DnsPropagationData();
                }
                else
                {
                    _data = new DnsPropagationData();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"加载DNS传播记录失败：{ex.Message}");
                _data = new DnsPropagationData();
            }
        }

        private static void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(DataFile);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(DataFile, JsonSerializer.Serialize(_data, DnsPropagationJsonContext.Default.DnsPropagationData));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"保存DNS传播记录失败：{ex.Message}");
            }
        }

        /// <summary>生成 /api/dns-propagation 的响应字节。</summary>
        public static byte[] GetResponseJson()
        {
            lock (SyncRoot)
            {
                EnsureLoaded();
                var data = _data!;

                var response = new DnsPropagationResponse
                {
                    Enabled = Enabled,
                    Overall = BuildStats("全部", data.Samples),
                    ByDomain = data.Samples
                        .GroupBy(s => s.Domain, StringComparer.OrdinalIgnoreCase)
                        .Select(g => BuildStats(g.Key, g.ToList()))
                        .OrderBy(s => s.Domain)
                        .ToList(),
                    Recent = data.Samples.OrderByDescending(s => s.Time).Take(50).ToList()
                };

                return JsonSerializer.SerializeToUtf8Bytes(response, DnsPropagationJsonContext.Default.DnsPropagationResponse);
            }
        }

        private static DnsPropagationStats BuildStats(string domain, List<DnsPropagationSample> samples)
        {
            var values = samples
                .Where(s => s.HostDelayMs.HasValue)
                .Select(s => s.HostDelayMs!.Value)
                .OrderBy(v => v)
                .ToList();

            var stats = new DnsPropagationStats
            {
                Domain = domain,
                Count = samples.Count,
                Success = values.Count
            };

            if (values.Count > 0)
            {
                stats.MinMs = Math.Round(values[0]);
                stats.MaxMs = Math.Round(values[^1]);
                stats.MeanMs = Math.Round(values.Average());
                stats.MedianMs = Math.Round(values.Count % 2 == 1
                    ? values[values.Count / 2]
                    : (values[values.Count / 2 - 1] + values[values.Count / 2]) / 2.0);
                var idx = Math.Min(values.Count - 1, (int)Math.Round(0.95 * (values.Count - 1)));
                stats.P95Ms = Math.Round(values[idx]);
            }

            return stats;
        }

        // ── 权威 NS 直接查询（绕过所有缓存，用于确认"源头已更新"）────────────────

        private static async Task<List<string>> ResolveHostTargetsAsync(string[] hosts, CancellationToken token)
        {
            var ips = new List<string>();
            foreach (var host in hosts)
            {
                if (IPAddress.TryParse(host, out var direct))
                {
                    if (!ips.Contains(direct.ToString())) ips.Add(direct.ToString());
                    continue;
                }

                try
                {
                    var addrs = await Dns.GetHostAddressesAsync(host, token);
                    foreach (var addr in addrs.Where(a => a.AddressFamily == AddressFamily.InterNetwork))
                    {
                        var s = addr.ToString();
                        if (!ips.Contains(s)) ips.Add(s);
                    }
                }
                catch { /* 解析权威主机名失败，忽略 */ }
            }
            return ips;
        }

        private static async Task<List<string>?> QueryAAgainstAsync(string serverIp, string domain, CancellationToken token)
        {
            try
            {
                using var udp = new UdpClient();
                var query = BuildDnsQuery(domain);
                var endpoint = new IPEndPoint(IPAddress.Parse(serverIp), 53);
                await udp.SendAsync(query.AsMemory(), endpoint, token);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));
                var result = await udp.ReceiveAsync(timeoutCts.Token);
                return ParseDnsAnswers(result.Buffer);
            }
            catch
            {
                return null;
            }
        }

        private static byte[] BuildDnsQuery(string domain)
        {
            var id = (ushort)Random.Shared.Next(0, 65536);
            using var ms = new MemoryStream();
            void W16(ushort v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }

            W16(id); W16(0x0100); W16(1); W16(0); W16(0); W16(0);

            foreach (var label in domain.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                var bytes = Encoding.ASCII.GetBytes(label);
                ms.WriteByte((byte)bytes.Length);
                ms.Write(bytes, 0, bytes.Length);
            }
            ms.WriteByte(0);
            W16(1); W16(1); // QTYPE=A, QCLASS=IN
            return ms.ToArray();
        }

        private static List<string>? ParseDnsAnswers(byte[] data)
        {
            if (data.Length < 12)
            {
                return null;
            }

            int questionCount = (data[4] << 8) | data[5];
            int answerCount = (data[6] << 8) | data[7];
            int idx = 12;

            for (int i = 0; i < questionCount; i++)
            {
                idx = SkipName(data, idx);
                idx += 4;
            }

            var ips = new List<string>();
            for (int i = 0; i < answerCount; i++)
            {
                idx = SkipName(data, idx);
                if (idx + 10 > data.Length)
                {
                    break;
                }

                int type = (data[idx] << 8) | data[idx + 1];
                int rdLength = (data[idx + 8] << 8) | data[idx + 9];
                idx += 10;

                if (type == 1 && rdLength == 4 && idx + 4 <= data.Length)
                {
                    ips.Add($"{data[idx]}.{data[idx + 1]}.{data[idx + 2]}.{data[idx + 3]}");
                }

                idx += rdLength;
            }

            return ips;
        }

        private static int SkipName(byte[] data, int idx)
        {
            while (idx < data.Length)
            {
                int len = data[idx];
                if (len == 0)
                {
                    return idx + 1;
                }
                if ((len & 0xC0) == 0xC0)
                {
                    return idx + 2;
                }
                idx += 1 + len;
            }
            return idx;
        }

        // ── 自检（--selftest-propagation 调用）：对当前已生效的 IP 走一遍完整链路 ──
        public static async Task<string> SelfTestAsync(string domain)
        {
            var addrs = await Dns.GetHostAddressesAsync(domain);
            var ip = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString();
            if (ip == null)
            {
                return $"自检失败：{domain} 未解析到 IPv4 地址";
            }

            NotifyUpdated("SELFTEST", domain, ip);
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(3, PollSeconds) + 1));
            var json = Encoding.UTF8.GetString(GetResponseJson());
            return $"自检完成：以当前 IP {ip} 为目标，结果如下\n{json}";
        }

        // ── 配置读取 ──
        private static bool ReadBool(string name, bool defaultValue)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                return defaultValue;
            }
            return value.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";
        }

        private static int ReadInt(string name, int defaultValue, int min, int max)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return int.TryParse(value, out var parsed) ? Math.Clamp(parsed, min, max) : defaultValue;
        }
    }

    internal sealed class DnsPropagationSample
    {
        public DateTimeOffset Time { get; set; }
        public string Provider { get; set; } = string.Empty;
        public string Domain { get; set; } = string.Empty;
        public string Ip { get; set; } = string.Empty;

        /// <summary>端到端传播耗时（写入 → 本机解析命中新 IP），毫秒。</summary>
        public double? HostDelayMs { get; set; }

        /// <summary>写入 → 权威 NS 确认新值的耗时（仅配置了权威 NS 时才有），毫秒。</summary>
        public double? AuthDelayMs { get; set; }

        /// <summary>success / timeout / error。</summary>
        public string Status { get; set; } = string.Empty;
    }

    internal sealed class DnsPropagationStats
    {
        public string Domain { get; set; } = string.Empty;
        public int Count { get; set; }
        public int Success { get; set; }
        public double? MeanMs { get; set; }
        public double? MedianMs { get; set; }
        public double? MinMs { get; set; }
        public double? MaxMs { get; set; }
        public double? P95Ms { get; set; }
    }

    internal sealed class DnsPropagationResponse
    {
        public bool Enabled { get; set; }
        public DnsPropagationStats Overall { get; set; } = new();
        public List<DnsPropagationStats> ByDomain { get; set; } = new();
        public List<DnsPropagationSample> Recent { get; set; } = new();
    }

    internal sealed class DnsPropagationData
    {
        public List<DnsPropagationSample> Samples { get; set; } = new();
    }

    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true)]
    [JsonSerializable(typeof(DnsPropagationData))]
    [JsonSerializable(typeof(DnsPropagationResponse))]
    internal partial class DnsPropagationJsonContext : JsonSerializerContext
    {
    }
}
