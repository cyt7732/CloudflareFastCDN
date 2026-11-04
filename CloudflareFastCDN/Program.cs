using CloudflareFastCDN.Services;
using CloudflareFastCDN.Settings;
using CloudflareFastCDN.Utils;
using CloudflareFastCDN.Web;
using System.Net;
using System.Net.Sockets;

namespace CloudflareFastCDN
{
    internal class Program
    {
        private const int FirstRoundPingCount = 4;
        private const int SecondRoundPingCount = 5;
        private const int SecondRoundMaxPacketLoss = 1;
        private const double SecondRoundCandidateRatio = 0.25d;
        private const int FinalHttpCandidateCount = 10;
        private const int FinalSecondRoundCandidateCount = 7;
        private const int FinalCachedCandidateCount = 3;
        private const int HttpProbeCount = 5;
        private const int HttpMinSuccessCount = 4;
        private const double AssignedDelayMaxMultiplier = 2d;
        private static readonly TimeSpan AssignedDelayMinimumThreshold = TimeSpan.FromMilliseconds(300);
        private static readonly TimeSpan FinalProbeMinInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan FinalProbeMaxInterval = TimeSpan.FromSeconds(8);
        // 候选之间的等待。原来 30~45 秒是串行时代的保守值：
        // 一个候选只测 5 次，却要在候选之间空等半分钟，整轮 20 个候选光等待就吃掉 12 分钟。
        // 实测单个候选内部 5~8 秒的探测间隔已经足够，候选之间不需要更长。
        private static readonly TimeSpan FinalCandidateMinInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan FinalCandidateMaxInterval = TimeSpan.FromSeconds(8);
        private const int SubnetProbePingCount = 2;
        private const int SubnetSampleCount = 6;
        private const int SeedPoolCapacity = 10;    // 种子库容量

        // ── 两级验证：并发粗筛 + 串行精测 ────────────────────────────────
        // 串行精测每个候选约 35 秒（5 次探测 + 5~8 秒间隔），是吞吐的唯一瓶颈。
        // 原来 TCP 通过后直接全量串行精测，20 个候选 21 分钟、每小时只能验证 14 个 IP。
        // 现在先用并发粗筛淘汰明显差的，只让少数候选进精测：
        //   粗筛：60 个候选 × 3 次探测，6 路并发，约 1 分钟
        //   精测：8 个粗筛优胜 + 4 个种子 + 当前 IP，串行出精确分
        // 关键约束：并发只用于粗筛，最终排名一定串行精测 ——
        // 并发时互相抢带宽，测出来的是「拥堵环境下的表现」，不能用于切换判定。
        private const int CoarseScreenCandidateCount = 60;   // 每轮粗筛候选数（TCP 通过里延时最低的）
        private const int CoarseScreenConcurrency = 6;       // 粗筛并发路数
        private const int CoarseScreenProbeCount = 3;        // 粗筛每个候选探测次数（1 冷 + 2 热）
        private const int CoarseScreenMinSuccess = 2;        // 3 次里至少成功 2 次才算通过粗筛
        private const int CoarseScreenPassCount = 8;         // 粗筛后进入精测的数量
        private const int SeedRefineCount = 4;               // 种子库每轮精测数量（不再全测 10 个）
        private const int CoarseScreenFallbackCount = 8;     // 粗筛全军覆没时回退 TCP 前 N 个
        private static readonly TimeSpan CoarseScreenProbeInterval = TimeSpan.FromSeconds(2);

        // ── 冷启动稳定性门槛 ──────────────────────────────────────────
        // 低频访问每次都是新连接，稳态再快也扛不住「隔几次就给你抽风一个 1.5 秒」。
        // 精测阶段额外做 3 次全新连接探测（测完整建连+TLS+请求）：
        //   冷启动中位 > 700ms  → 典型值就已经很慢
        //   冷启动 cv  > 0.5    → 典型值还行，但忽快忽慢，实测见过 0.36 秒和 1.8 秒交替
        // 命中任一条件直接判不通过，不让它进排名、更不会被分配。
        private const int ColdProbeCount = 3;
        private static readonly TimeSpan ColdProbeInterval = TimeSpan.FromSeconds(2);
        private const double ColdMedianThresholdMs = 700;
        private const double ColdCvThreshold = 0.5;

        // ── 当前 IP 健康监测（连续跑模式下的快速纠偏）─────────────────────
        // 连续跑时主循环被一轮一轮占满（每轮 10~14 分钟），当前 IP 只在每轮精测时被顺带检查，
        // 踩到坑要等十几分钟才换。这里起一个独立后台任务，专门盯当前 IP：
        //   平时每 3 分钟测一次（单次全新连接，测完整首字节）
        //   一旦偏慢 → 黄牌，改成每 1 分钟测一次
        //   连续 3 次偏慢（原本那次 + 黄牌 2 次）→ 直接快速切换
        //   换完回到 3 分钟节奏
        // 「偏慢」= 单次首字节 > max(基准冷启动 × 1.8, 700ms)，或探测直接失败。
        private static readonly TimeSpan HealthCheckInterval = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan HealthYellowInterval = TimeSpan.FromMinutes(1);
        private const int HealthBadThreshold = 3;
        private const double HealthSlowRatio = 1.8;
        private const double HealthSlowFloorMs = 700;

        /// <summary>保护「读当前 IP → 写 DNS → 写切换状态」这一串动作，避免后台监测和主流程同时改 DNS。</summary>
        private static readonly SemaphoreSlim SwitchLock = new(1, 1);

        private static CancellationTokenSource? _healthCts;

        private static readonly TimeSpan SupplementalHttpCheckInterval = TimeSpan.FromMinutes(5);
        private const int SupplementalHttpCheckCount = 3;

        // 网段粗分：TCP 快筛的延时按本轮分位数映射到 [240, 900]，中位数正好 500
        private const double TcpBestScore = 240;
        private const double TcpMedianScore = 500;
        private const double TcpWorstScore = 900;
        private const double TcpLossPenalty = 300;  // 丢一个包（4 中 3）加 75 分

        // 网段分数表的标尺是历史沿用的（好网段 ≈ 250~300，默认分 500）。
        // 新的 ProbeScore 口径下好 IP 只有 ~100~140，直接喂进去会把「出过好 IP 的网段」
        // 权重放大 9 倍，等于把那十几个网段锁死。这里换算回同一标尺。
        private const double SubnetHitScoreScale = 2.3;

        private static List<DnsUpdateProvider>? _dnsUpdateProviders;
        private static int _dnsUpdateProvidersVersion = -1;

        static void Main(string[] args)
        {
            MainAsync(args).GetAwaiter().GetResult();
        }

        static async Task MainAsync(string[] args)
        {
            bool isDocker = File.Exists("/.dockerenv");
            TimestampedConsole.Configure(isDocker);
            SettingsManager.Initialize(args, isDocker);
            PrintStartupConfiguration(isDocker);

            DashboardState.Initialize();

            // 启动时把磁盘上的种子库加载到管理页面，避免重启后显示为空
            try
            {
                var startupSeedPool = new SeedPool();
                startupSeedPool.Load();
                DashboardState.SetSeedPoolInfo(startupSeedPool.GetEntries().Select(r => (r.Ip, r.Score, r.DelayMs, r.LastSeen)));
                if (startupSeedPool.Count > 0)
                {
                    Console.WriteLine($"种子库已载入管理页面：{startupSeedPool.Count} 个 IP");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"加载种子库到管理页面失败：{ex.Message}");
            }

            var webServer = await WebServer.StartAsync();
            if (webServer == null && !TryBuildDnsUpdateProviders(out _, out var configurationError))
            {
                // 没有 Web 管理页面时无法在运行中补全配置，保持原有的直接退出行为
                Console.WriteLine(configurationError);
                return;
            }

            if (AppConfig.UpdateIPList)
            {
                await IPProcessor.UpdateIPList();
            }

            var dnsUpdateProviders = await GetDnsUpdateProviders();

            // 健康监测尽早启动：第一轮全量优选可能要跑十几分钟，
            // 期间当前 IP 一旦变慢，后台任务就能发现并切换，不用等主流程。
            StartHealthMonitor();

            SelectionResult selection;
            if (AppConfig.ForceInitialFullSelection)
            {
                Console.WriteLine("FORCE_INITIAL_FULL_SELECTION 已启用，首次启动直接进行全量优选");
                selection = await SingleSelect(dnsUpdateProviders, "首次启动");
            }
            else
            {
                selection = await TryCreateInitialSelectionFromDomains(dnsUpdateProviders)
                    ?? await SingleSelect(dnsUpdateProviders, "首次启动");
            }

            while (true)
            {
                var fullSelectionTrigger = await WaitForNextSelection(selection);
                if (fullSelectionTrigger != null)
                {
                    selection = await SingleSelect(await GetDnsUpdateProviders(), fullSelectionTrigger);
                }
            }
        }

        private static void PrintStartupConfiguration(bool isDocker)
        {
            Console.WriteLine("启动配置：");
            Console.WriteLine($"  运行环境: {(isDocker ? "Docker" : "Local")}");
            Console.WriteLine($"  日志时间格式: {TimestampedConsole.TimestampFormatPattern}");
            Console.WriteLine($"  日志时区: {TimestampedConsole.EffectiveTimeZoneId}");
            if (isDocker)
            {
                Console.WriteLine($"  TZ: {TimestampedConsole.RequestedTimeZoneId ?? "(not set)"}");
            }
            Console.WriteLine($"  设置文件: {Path.GetFullPath(SettingsManager.SettingsFile)}");
            Console.WriteLine($"  WEB_PORT: {AppConfig.WebPort}");
            Console.WriteLine($"  WEB_HOST: {AppConfig.WebHost}");
            Console.WriteLine($"  DNS提供商: {(TryBuildDnsUpdateProviders(out var providers, out _) ? string.Join(", ", providers.Select(provider => provider.ProviderName)) : "(未配置)")}");
            if (AppConfig.DryRun)
            {
                Console.WriteLine("  ★ 演练模式已开启（DRY_RUN=true）：正常探测与评分，但不写任何 DNS 记录");
            }
            foreach (var item in SettingsManager.BuildSummary())
            {
                Console.WriteLine($"  {item.Key}: {item.Value}");
            }
        }

        /// <summary>
        /// 获取当前DNS设置对应的更新器；配置不完整时进入待配置状态，直到Web页面保存了有效配置。
        /// </summary>
        private static async Task<List<DnsUpdateProvider>> GetDnsUpdateProviders()
        {
            string? reportedError = null;
            while (true)
            {
                var settingsChanged = SettingsManager.WaitForChangeAsync();
                var dnsVersion = SettingsManager.DnsVersion;
                if (_dnsUpdateProviders != null && dnsVersion == _dnsUpdateProvidersVersion)
                {
                    return _dnsUpdateProviders;
                }

                if (TryBuildDnsUpdateProviders(out var providers, out var error))
                {
                    if (reportedError != null)
                    {
                        Console.WriteLine($"DNS配置已完成：{string.Join(", ", providers.Select(provider => provider.ProviderName))}");
                    }

                    _dnsUpdateProviders = providers;
                    _dnsUpdateProvidersVersion = dnsVersion;
                    return providers;
                }

                if (error != reportedError)
                {
                    Console.WriteLine($"{error}；请在Web管理页面的设置中完成配置");
                    reportedError = error;
                }

                DashboardState.SetPhase(DashboardPhase.NotConfigured, error);
                await settingsChanged;
            }
        }

        private static bool TryBuildDnsUpdateProviders(out List<DnsUpdateProvider> providers, out string error)
        {
            providers = new List<DnsUpdateProvider>();
            error = string.Empty;

            if (HasAnyDomains(AppConfig.CloudflareDomains, AppConfig.CloudflareDomains2, AppConfig.CloudflareDomains3))
            {
                if (string.IsNullOrWhiteSpace(AppConfig.CloudflareKey))
                {
                    error = "配置了 CLOUDFLARE_DOMAINS*，但缺少 CLOUDFLARE_KEY";
                    return false;
                }

                providers.Add(new DnsUpdateProvider(
                    "Cloudflare",
                    "CLOUDFLARE",
                    new CloudflareAPIManager(AppConfig.CloudflareKey),
                    AppConfig.CloudflareDomains,
                    AppConfig.CloudflareDomains2,
                    AppConfig.CloudflareDomains3));
            }

            if (HasAnyDomains(AppConfig.TencentCloudDomains, AppConfig.TencentCloudDomains2, AppConfig.TencentCloudDomains3))
            {
                if (string.IsNullOrWhiteSpace(AppConfig.TencentCloudSecretId) ||
                    string.IsNullOrWhiteSpace(AppConfig.TencentCloudSecretKey))
                {
                    error = "配置了 TENCENTCLOUD_DOMAINS*，但缺少 TENCENTCLOUD_SECRET_ID 或 TENCENTCLOUD_SECRET_KEY";
                    return false;
                }

                providers.Add(new DnsUpdateProvider(
                    "TencentCloud",
                    "TENCENTCLOUD",
                    new TencentCloudDnsManager(AppConfig.TencentCloudSecretId, AppConfig.TencentCloudSecretKey),
                    AppConfig.TencentCloudDomains,
                    AppConfig.TencentCloudDomains2,
                    AppConfig.TencentCloudDomains3));
            }

            if (HasAnyDomains(AppConfig.AlibabaCloudDomains, AppConfig.AlibabaCloudDomains2, AppConfig.AlibabaCloudDomains3))
            {
                if (string.IsNullOrWhiteSpace(AppConfig.AlibabaCloudAccessKeyId) ||
                    string.IsNullOrWhiteSpace(AppConfig.AlibabaCloudAccessKeySecret))
                {
                    error = "配置了 ALIBABACLOUD_DOMAINS*，但缺少 ALIBABACLOUD_ACCESS_KEY_ID 或 ALIBABACLOUD_ACCESS_KEY_SECRET";
                    return false;
                }

                providers.Add(new DnsUpdateProvider(
                    "AlibabaCloud",
                    "ALIBABACLOUD",
                    new AlibabaCloudDnsManager(AppConfig.AlibabaCloudAccessKeyId, AppConfig.AlibabaCloudAccessKeySecret),
                    AppConfig.AlibabaCloudDomains,
                    AppConfig.AlibabaCloudDomains2,
                    AppConfig.AlibabaCloudDomains3));
            }

            if (providers.Count == 0)
            {
                error = "缺少 DNS 域名配置，请至少设置 Cloudflare、腾讯云或阿里云其中一家的域名及凭证";
                return false;
            }

            return true;
        }

        private static bool HasAnyDomains(params string[][] domainGroups)
        {
            return domainGroups.Any(domainGroup => domainGroup.Length > 0);
        }

        private static async Task<SelectionResult?> TryCreateInitialSelectionFromDomains(IReadOnlyList<DnsUpdateProvider> dnsUpdateProviders)
        {
            Console.WriteLine("首次启动先解析并测试已配置域名的当前IPv4地址");
            DashboardState.SetPhase(DashboardPhase.InitialCheck, "解析已配置域名");

            var resolvedIps = new Dictionary<IPAddress, ResolvedDomainIp>();
            foreach (var dnsUpdateProvider in dnsUpdateProviders)
            {
                for (int rank = 0; rank < 3; rank++)
                {
                    foreach (var domain in dnsUpdateProvider.GetDomains(rank))
                    {
                        // 优先直接查 DNS 服务商 API，避开本地 DNS 缓存导致的「同一域名解析出不同 IP」
                        var apiIps = await dnsUpdateProvider.Manager.GetARecordIps(domain);
                        List<IPAddress> ipv4Addresses;
                        if (apiIps.Count > 0)
                        {
                            ipv4Addresses = apiIps
                                .Select(ip => IPAddress.TryParse(ip, out var parsed) ? parsed : null)
                                .Where(ip => ip != null && ip.AddressFamily == AddressFamily.InterNetwork)
                                .Select(ip => ip!)
                                .Distinct()
                                .ToList();
                        }
                        else
                        {
                            IPAddress[] addresses;
                            try
                            {
                                addresses = await Dns.GetHostAddressesAsync(domain);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"首次域名预检解析失败：{domain}，原因：{FormatError(ex.Message)}，开始全量优选");
                                return null;
                            }

                            ipv4Addresses = addresses
                                .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                                .Distinct()
                                .ToList();
                        }

                        if (ipv4Addresses.Count == 0)
                        {
                            Console.WriteLine($"首次域名预检未解析到IPv4地址：{domain}，开始全量优选");
                            return null;
                        }

                        Console.WriteLine($"首次域名预检解析（{(apiIps.Count > 0 ? "API" : "本地DNS")}）：{domain} -> {string.Join(", ", ipv4Addresses)}");
                        foreach (var ip in ipv4Addresses)
                        {
                            if (!resolvedIps.TryGetValue(ip, out var resolvedIp))
                            {
                                resolvedIp = new ResolvedDomainIp(ip, rank + 1);
                                resolvedIps.Add(ip, resolvedIp);
                            }
                            else
                            {
                                resolvedIp.Rank = Math.Min(resolvedIp.Rank, rank + 1);
                            }

                            resolvedIp.Domains.Add(domain);
                        }
                    }
                }
            }

            if (resolvedIps.Count == 0)
            {
                Console.WriteLine("首次域名预检没有可测试的IPv4地址，开始全量优选");
                return null;
            }

            var assignedIps = new List<AssignedIp>();
            var httpPing = new Httping();
            var hasFailure = false;
            foreach (var resolvedIp in resolvedIps.Values)
            {
                DashboardState.SetPhase(DashboardPhase.InitialCheck, $"HTTP检查 Top{resolvedIp.Rank} {resolvedIp.IP}");
                var probe = await httpPing.PingWithOutcome(resolvedIp.IP, HttpProbeCount, FinalProbeMinInterval, FinalProbeMaxInterval);
                var stats = ProbeScore.Compute(probe);
                DashboardState.RecordCheck(resolvedIp.Rank, resolvedIp.IP.ToString(), stats.Valid ? stats.WarmMs : null, "首次预检", null, stats.ColdMs);
                if (!stats.Valid || probe.Success < HttpMinSuccessCount)
                {
                    hasFailure = true;
                    Console.WriteLine($"首次域名预检失败：{resolvedIp.IP}（{string.Join(", ", resolvedIp.Domains)}），原因：{FormatError(probe.Error)}");
                    continue;
                }

                assignedIps.Add(new AssignedIp(resolvedIp.IP, TimeSpan.FromMilliseconds(stats.WarmMs), resolvedIp.Rank, stats.Score, stats.ColdMs));
                Console.WriteLine($"首次域名预检正常：Top{resolvedIp.Rank} {resolvedIp.IP}（{string.Join(", ", resolvedIp.Domains)}）冷启动 {stats.ColdMs:0.00}ms 稳态 {stats.WarmMs:0.00}ms → 评分 {stats.Score:0.00}");
            }

            if (hasFailure)
            {
                Console.WriteLine("首次域名预检存在无法连接的IPv4地址，开始全量优选");
                return null;
            }

            Console.WriteLine($"首次域名预检的{assignedIps.Count}个IPv4地址均正常，跳过首次全量优选");
            DashboardState.SetAssignedIps(assignedIps.Select(item => (item.Rank, item.IP.ToString(), item.BaselineDelay.TotalMilliseconds, item.ColdMs)));

            // 首次预检成功时初始化切换基准
            if (assignedIps.Count > 0)
            {
                var switchState = SwitchState.Load();
                if (string.IsNullOrEmpty(switchState.CurrentIp))
                {
                    var first = assignedIps[0];
                    switchState.Initialize(first.IP.ToString(), first.Score, first.BaselineDelay.TotalMilliseconds, first.ColdMs);
                    Console.WriteLine($"切换基准初始化：{first.IP} 冷启动 {first.ColdMs:0.00}ms 稳态 {first.BaselineDelay.TotalMilliseconds:0.00}ms 评分 {first.Score:0.00}（首次预检）");
                }
            }

            return new SelectionResult(assignedIps);
        }

        /// <summary>
        /// 等待下一周期；返回需要执行全量优选的触发原因，返回 null 表示本周期无需全量优选。
        /// </summary>
        private static async Task<string?> WaitForNextSelection(SelectionResult selection)
        {
            var regularInterval = TimeSpan.FromMinutes(AppConfig.RunMinutes);
            if (regularInterval <= TimeSpan.Zero)
            {
                // 连续跑模式下，如果本轮一个可用 IP 都没选出来（通常是网络异常或整片超时），
                // 必须强制歇一下，否则会「失败 → 立刻重跑 → 再失败」把请求打爆。
                if (selection.AssignedIps.Count == 0)
                {
                    Console.WriteLine("本轮没有可用 IP，歇 60 秒后重试（避免空轮死循环）");
                    await Task.Delay(TimeSpan.FromMinutes(1));
                }

                Console.WriteLine("RUN_MINUTES 小于等于0，立即开始下一轮优选");
                return "定时优选";
            }

            var nextRunAt = TimestampedConsole.Now.Add(regularInterval);
            DashboardState.SetWaiting(nextRunAt);

            if (!AppConfig.EnableSupplementalHttpCheck)
            {
                Console.WriteLine(selection.AssignedIps.Count == 0
                    ? $"等待{AppConfig.RunMinutes}分钟；本轮没有已分配IP，到期后重新优选"
                    : $"等待{AppConfig.RunMinutes}分钟；到期后先复检{selection.AssignedIps.Count}个已分配IP");
                var request = await RuntimeControl.WaitAsync(regularInterval);
                return await HandleWaitFinished(selection, request);
            }

            if (selection.AssignedIps.Count == 0)
            {
                Console.WriteLine($"等待{AppConfig.RunMinutes}分钟；本轮没有可用IP，将在{SupplementalHttpCheckInterval.TotalMinutes:0}分钟后触发重新优选");
            }
            else
            {
                Console.WriteLine($"等待{AppConfig.RunMinutes}分钟；期间每{SupplementalHttpCheckInterval.TotalMinutes:0}分钟对 {selection.AssignedIps[0].IP} 进行补充HTTP检查，到期后直接开始新一轮优选");
            }

            while (true)
            {
                var remaining = nextRunAt - TimestampedConsole.Now;
                if (remaining <= TimeSpan.Zero)
                {
                    return await HandleWaitFinished(selection, ManualRequest.None);
                }

                var delay = remaining < SupplementalHttpCheckInterval
                    ? remaining
                    : SupplementalHttpCheckInterval;
                var request = await RuntimeControl.WaitAsync(delay);

                if (request.Command != ManualCommand.None || TimestampedConsole.Now >= nextRunAt)
                {
                    return await HandleWaitFinished(selection, request);
                }

                if (selection.AssignedIps.Count == 0)
                {
                    Console.WriteLine("本轮没有可用于补充HTTP检查的IP，触发重新优选并重新计时");
                    return "无可用IP";
                }

                DashboardState.SetPhase(DashboardPhase.SupplementalCheck, $"补充HTTP检查 {selection.AssignedIps[0].IP}");
                var healthy = await RunSupplementalHttpCheck(selection.AssignedIps[0]);
                if (!healthy)
                {
                    Console.WriteLine($"补充HTTP检查连续{SupplementalHttpCheckCount}次失败，启动灾备快速切换");
                    var failover = await RunFailover();
                    if (failover != null)
                    {
                        selection = failover;
                        nextRunAt = TimestampedConsole.Now + TimeSpan.FromMinutes(AppConfig.RunMinutes);
                        DashboardState.SetWaiting(nextRunAt);
                        continue;
                    }

                    Console.WriteLine("灾备失败，退回全量优选");
                    return "补充检查失败";
                }

                DashboardState.SetWaiting(nextRunAt);
            }
        }

        private static async Task<string?> HandleWaitFinished(SelectionResult selection, ManualRequest request)
        {
            if (request.Command == ManualCommand.FullSelection)
            {
                Console.WriteLine($"立即开始全量优选，原因：{request.Trigger}");
                return request.Trigger;
            }

            // 定时到达：直接跑一轮优选（持续探索），不再用「复检通过就跳过」
            if (request.Command == ManualCommand.None)
            {
                Console.WriteLine("到达定时优选时间，直接开始全量优选（不复检跳过）");
                return "定时触发";
            }

            // 手动复检：只做健康检查
            var manual = request.Command == ManualCommand.Revalidate;
            if (await ValidateAssignedIps(selection.AssignedIps, manual))
            {
                return null;
            }

            if (selection.AssignedIps.Count == 0)
            {
                return "无可用IP";
            }

            return "手动复检未通过";
        }

        private static async Task<bool> ValidateAssignedIps(IReadOnlyList<AssignedIp> assignedIps, bool manual)
        {
            RuntimeControl.ClearPendingRevalidation();
            var reason = manual ? "收到手动复检请求" : "到达定时优选时间";
            if (assignedIps.Count == 0)
            {
                Console.WriteLine($"{reason}，但没有已分配IP，开始全量优选");
                return false;
            }

            Console.WriteLine($"{reason}，先复检{assignedIps.Count}个已分配IP；每个IP检测{HttpProbeCount}次，单次间隔{FinalProbeMinInterval.TotalSeconds:0}-{FinalProbeMaxInterval.TotalSeconds:0}秒，延迟达到动态阈值 max({AssignedDelayMinimumThreshold.TotalMilliseconds:0}ms, 基线×{AssignedDelayMaxMultiplier:0.#}) 则重新优选");
            var httpPing = new Httping();

            for (int i = 0; i < assignedIps.Count; i++)
            {
                var assignedIp = assignedIps[i];
                DashboardState.SetPhase(DashboardPhase.Revalidating, $"复检 Top{assignedIp.Rank} {assignedIp.IP}（{i + 1}/{assignedIps.Count}）");
                var probe = await httpPing.PingWithOutcome(assignedIp.IP, HttpProbeCount, FinalProbeMinInterval, FinalProbeMaxInterval);
                var stats = ProbeScore.Compute(probe);
                var warmDelay = TimeSpan.FromMilliseconds(stats.WarmMs);

                var passed = probe.Success >= HttpProbeCount && stats.Valid;
                var updatedBaseline = passed && warmDelay > TimeSpan.Zero && warmDelay < assignedIp.BaselineDelay
                    ? warmDelay
                    : assignedIp.BaselineDelay;
                DashboardState.RecordCheck(
                    assignedIp.Rank,
                    assignedIp.IP.ToString(),
                    passed ? stats.WarmMs : null,
                    manual ? "手动复检" : "定时复检",
                    updatedBaseline.TotalMilliseconds,
                    stats.ColdMs);

                if (probe.Success < HttpProbeCount)
                {
                    Console.WriteLine($"已分配IP复检失败：Top{assignedIp.Rank} {assignedIp.IP} HTTP成功：{probe.Success}/{HttpProbeCount}，原因：{FormatError(probe.Error)}，开始全量优选");
                    return false;
                }

                if (IsAssignedDelayInvalid(assignedIp.BaselineDelay, warmDelay))
                {
                    Console.WriteLine($"已分配IP复检延迟异常：Top{assignedIp.Rank} {assignedIp.IP} 稳态{warmDelay.TotalMilliseconds:0.00}ms，基线{assignedIp.BaselineDelay.TotalMilliseconds:0.00}ms，开始全量优选");
                    return false;
                }

                // 刷新切换基准：必须写 ProbeScore 口径的评分，不能写原始延时
                var switchState = SwitchState.Load();
                if (string.Equals(switchState.CurrentIp, assignedIp.IP.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    switchState.UpdateBaseline(stats.Score, stats.WarmMs, stats.ColdMs);
                }

                if (stats.WarmMs > 0 && warmDelay < assignedIp.BaselineDelay)
                {
                    Console.WriteLine($"已分配IP复检正常：Top{assignedIp.Rank} {assignedIp.IP} 成功{probe.Success}/{HttpProbeCount} 冷启动{stats.ColdMs:0.00}ms 稳态{stats.WarmMs:0.00}ms（低于基线{assignedIp.BaselineDelay.TotalMilliseconds:0.00}ms，基线更新）→ 评分 {stats.Score:0.00}");
                    assignedIp.BaselineDelay = warmDelay;
                    assignedIp.ColdMs = stats.ColdMs;
                }
                else
                {
                    Console.WriteLine($"已分配IP复检正常：Top{assignedIp.Rank} {assignedIp.IP} 成功{probe.Success}/{HttpProbeCount} 冷启动{stats.ColdMs:0.00}ms 稳态{stats.WarmMs:0.00}ms 基线{assignedIp.BaselineDelay.TotalMilliseconds:0.00}ms → 评分 {stats.Score:0.00}");
                }

                if (i < assignedIps.Count - 1)
                {
                    var delay = GetRandomDelay(FinalCandidateMinInterval, FinalCandidateMaxInterval);
                    Console.WriteLine($"等待{delay.TotalSeconds:0}秒后复检下一个已分配IP");
                    await Task.Delay(delay);
                }
            }

            Console.WriteLine("已分配IP复检均正常");
            return true;
        }

        /// <summary>
        /// 从 DNS 服务商 API 读取当前 A 记录，测出它的评分，用于初始化切换基准。
        /// </summary>
        private static async Task<(string Ip, double Score, double DelayMs)?> ProbeCurrentDnsIps(IReadOnlyList<DnsUpdateProvider> providers)
        {
            var httpPing = new Httping();
            foreach (var provider in providers)
            {
                foreach (var domain in provider.GetDomains(0))
                {
                    IReadOnlyList<string> apiIps;
                    try
                    {
                        apiIps = await provider.Manager.GetARecordIps(domain);
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (var ipStr in apiIps)
                    {
                        if (!IPAddress.TryParse(ipStr, out var ip))
                        {
                            continue;
                        }

                        var probe = await httpPing.PingWithOutcome(ip, HttpProbeCount, FinalProbeMinInterval, FinalProbeMaxInterval);
                        if (probe.Success < HttpMinSuccessCount)
                        {
                            continue;
                        }

                        var result = ProbeScore.Compute(probe);
                        if (!result.Valid)
                        {
                            continue;
                        }

                        Console.WriteLine($"DNS 基准反推 {ip} 冷启动 {result.ColdMs:0.00}ms 稳态 {result.WarmMs:0.00}ms → 评分 {result.Score:0.00}");
                        return (ip.ToString(), result.Score, result.WarmMs);
                    }
                }
            }
            return null;
        }

        /// <summary>启动当前 IP 健康监测（独立后台任务，不占用主流程的轮次节奏）。</summary>
        private static void StartHealthMonitor()
        {
            _healthCts = new CancellationTokenSource();
            _ = Task.Run(() => HealthMonitorLoopAsync(_healthCts.Token));
            Console.WriteLine(
                $"当前 IP 健康监测已启动：平时每 {HealthCheckInterval.TotalMinutes:0} 分钟检查一次，" +
                $"连续 {HealthBadThreshold} 次偏慢触发快速切换（黄牌期间每 {HealthYellowInterval.TotalMinutes:0} 分钟）");
        }

        private static async Task HealthMonitorLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(HealthCheckInterval, ct);

                    var state = SwitchState.Load();
                    if (string.IsNullOrEmpty(state.CurrentIp))
                    {
                        continue;
                    }

                    var badCount = 0;
                    while (!ct.IsCancellationRequested)
                    {
                        var (slow, ms) = await ProbeCurrentOnceAsync(state.CurrentIp);
                        if (!slow)
                        {
                            if (badCount > 0)
                            {
                                Console.WriteLine($"健康检查：当前 IP 恢复正常（{ms:0}ms），解除黄牌");
                            }
                            break;
                        }

                        badCount++;
                        Console.WriteLine($"健康检查：当前 IP {state.CurrentIp} 偏慢（{(ms > 0 ? $"{ms:0}ms" : "探测失败")}，{badCount}/{HealthBadThreshold}）");
                        if (badCount >= HealthBadThreshold)
                        {
                            break;
                        }

                        await Task.Delay(HealthYellowInterval, ct);
                    }

                    if (badCount >= HealthBadThreshold)
                    {
                        Console.WriteLine($"健康检查：连续 {HealthBadThreshold} 次偏慢，触发快速切换");
                        await QuickSwitchAsync("健康检查连续偏慢");
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"健康检查异常：{ex.Message}");
                }
            }
        }

        /// <summary>单次全新连接探测当前 IP，判断是否「偏慢」（超过基准冷启动 1.8 倍或 700ms 下限）。</summary>
        private static async Task<(bool Slow, double Ms)> ProbeCurrentOnceAsync(string ip)
        {
            if (!IPAddress.TryParse(ip, out var addr))
            {
                return (false, 0);
            }

            var state = SwitchState.Load();
            var baseline = state.CurrentColdMs > 0 ? state.CurrentColdMs : 400;
            var threshold = Math.Max(baseline * HealthSlowRatio, HealthSlowFloorMs);

            var httpPing = new Httping();
            var samples = await httpPing.ColdProbeAsync(addr, 1, TimeSpan.Zero);
            if (samples.Count == 0)
            {
                return (true, 0);
            }

            var ms = samples[0];
            return (ms > threshold, ms);
        }

        /// <summary>
        /// 快速切换：从种子库挑评分最好的几个，各测 1 次全新连接，选最快的换上。
        /// 不做全量优选，几秒内完成；评分先记 0，下一轮全量优选会重测补上真实评分。
        /// </summary>
        private static async Task QuickSwitchAsync(string reason)
        {
            if (!await SwitchLock.WaitAsync(TimeSpan.FromMinutes(2)))
            {
                Console.WriteLine("快速切换：主流程正在改 DNS，跳过本次，等下一轮全量优选");
                return;
            }

            try
            {
                var state = SwitchState.Load();
                var currentIp = state.CurrentIp;

                var seedPool = new SeedPool();
                seedPool.Load();
                var candidates = seedPool.GetIPs()
                    .Where(ip => !string.Equals(ip.ToString(), currentIp, StringComparison.OrdinalIgnoreCase))
                    .Take(5)
                    .ToList();

                if (candidates.Count == 0)
                {
                    Console.WriteLine("快速切换：种子库没有其他可用 IP，等下一轮全量优选");
                    return;
                }

                var httpPing = new Httping();
                var measured = new List<(IPAddress Ip, double Ms)>();
                foreach (var ip in candidates)
                {
                    var samples = await httpPing.ColdProbeAsync(ip, 1, TimeSpan.Zero);
                    if (samples.Count > 0)
                    {
                        measured.Add((ip, samples[0]));
                    }
                }

                if (measured.Count == 0)
                {
                    Console.WriteLine("快速切换：种子库 IP 全部不可用，等下一轮全量优选");
                    return;
                }

                measured.Sort((a, b) => a.Ms.CompareTo(b.Ms));
                var best = measured[0];
                Console.WriteLine($"快速切换选择：{best.Ip}（首字节 {best.Ms:0}ms，候选 {measured.Count} 个）");

                var dnsUpdateProviders = await GetDnsUpdateProviders();
                var updated = false;
                foreach (var provider in dnsUpdateProviders)
                {
                    foreach (var domain in provider.GetDomains(0))
                    {
                        try
                        {
                            var ok = AppConfig.DryRun
                                ? LogDryRunUpdate(provider.ProviderName, domain, best.Ip.ToString())
                                : await provider.Manager.AddOrUpdateARecord(domain, best.Ip.ToString());
                            if (ok)
                            {
                                updated = true;
                                if (!AppConfig.DryRun)
                                {
                                    Console.WriteLine($"快速切换[{provider.ProviderName}] {domain} → {best.Ip}");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"快速切换更新失败 {domain}：{ex.Message}");
                        }
                    }
                }

                if (!updated)
                {
                    Console.WriteLine("快速切换：DNS 更新失败，等下一轮全量优选");
                    return;
                }

                state.MarkSwitched(best.Ip.ToString(), 0, best.Ms, best.Ms);
                state.Save();
                Console.WriteLine($"快速切换完成（原因：{reason}）：{currentIp} → {best.Ip}");
            }
            finally
            {
                SwitchLock.Release();
            }
        }

        /// <summary>
        /// 灾备：当前 IP 连续失败时，从种子库快速选一个可用 IP 换上。
        /// 只做 TCP 快速探测，不跑全量优选。
        /// </summary>
        private static async Task<SelectionResult?> RunFailover()
        {
            Console.WriteLine("=== 灾备模式：从种子库快速选 IP ===");
            var seedPool = new SeedPool();
            seedPool.Load();
            var seedIps = seedPool.GetIPs();
            if (seedIps.Count == 0)
            {
                Console.WriteLine("种子库为空，无法灾备，退回全量优选");
                return null;
            }

            var tcpPing = new TcpPing(seedIps, 2);
            var results = await tcpPing.RunAsync();
            var passed = results.Where(item => item.Received > 0).ToList();
            if (passed.Count == 0)
            {
                Console.WriteLine("种子库 IP 全部不可用，退回全量优选");
                return null;
            }

            var best = passed[0];
            Console.WriteLine($"灾备选择：{best.IP}（TCP {best.Delay.TotalMilliseconds:0.00}ms，通过 {passed.Count}/{seedIps.Count}）");

            var dnsUpdateProviders = await GetDnsUpdateProviders();
            var assignedIpsByRank = new Dictionary<int, PingData>();
            foreach (var provider in dnsUpdateProviders)
            {
                foreach (var domain in provider.GetDomains(0))
                {
                    try
                    {
                        var updated = AppConfig.DryRun
                            ? LogDryRunUpdate(provider.ProviderName, domain, best.IP.ToString())
                            : await provider.Manager.AddOrUpdateARecord(domain, best.IP.ToString());
                        if (updated)
                        {
                            assignedIpsByRank.TryAdd(0, best);
                            if (!AppConfig.DryRun)
                            {
                                Console.WriteLine($"灾备更新[{provider.ProviderName}] {domain} → {best.IP}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"灾备更新失败 {domain}：{ex.Message}");
                    }
                }
            }

            if (assignedIpsByRank.Count == 0)
            {
                Console.WriteLine("灾备更新 DNS 全部失败，退回全量优选");
                return null;
            }

            var switchState = SwitchState.Load();
            // 灾备只做了 TCP 快筛，没有 HTTP 评分，所以评分记 0：
            // 下一轮全量优选会把这个 IP 强制拉进候选池重测，届时基准自动补上。
            // 评分 <= 0 时 Evaluate 会跳过「显著更优」分支，只按排名兜底，不会误判。
            switchState.MarkSwitched(best.IP.ToString(), 0, best.Delay.TotalMilliseconds);
            switchState.Save();

            var assignedIps = assignedIpsByRank
                .OrderBy(item => item.Key)
                .Select(item => new AssignedIp(item.Value.IP, item.Value.Delay, item.Key + 1))
                .ToList();
            Console.WriteLine($"灾备完成：{assignedIps[0].IP}");
            return new SelectionResult(assignedIps);
        }

        private static async Task<bool> RunSupplementalHttpCheck(AssignedIp assignedIp)
        {
            var selectedIp = assignedIp.IP;
            Console.WriteLine($"补充HTTP检查：{selectedIp}，最多{SupplementalHttpCheckCount}轮，每轮{HttpProbeCount}次探测（与选优同一口径）");

            var httpPing = new Httping();
            for (int attempt = 1; attempt <= SupplementalHttpCheckCount; attempt++)
            {
                var probe = await httpPing.PingWithOutcome(selectedIp, HttpProbeCount, FinalProbeMinInterval, FinalProbeMaxInterval);
                var stats = ProbeScore.Compute(probe);
                var passed = !probe.SkipNode && probe.Success >= HttpMinSuccessCount && stats.Valid;

                DashboardState.RecordCheck(assignedIp.Rank, selectedIp.ToString(), passed ? stats.WarmMs : null, "补充检查", null, stats.ColdMs);

                if (passed)
                {
                    Console.WriteLine($"补充HTTP检查 [{attempt}/{SupplementalHttpCheckCount}] 通过 {probe.Success}/{HttpProbeCount} 冷启动 {stats.ColdMs:0.00}ms 稳态 {stats.WarmMs:0.00}ms → 评分 {stats.Score:0.00}");

                    // 刷新切换基准：与选优同一口径，分数永远可比
                    var switchState = SwitchState.Load();
                    if (string.Equals(switchState.CurrentIp, selectedIp.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        switchState.UpdateBaseline(stats.Score, stats.WarmMs, stats.ColdMs);
                    }

                    assignedIp.BaselineDelay = TimeSpan.FromMilliseconds(stats.WarmMs);
                    assignedIp.ColdMs = stats.ColdMs;
                    return true;
                }

                Console.WriteLine($"补充HTTP检查 [{attempt}/{SupplementalHttpCheckCount}] 失败 {probe.Success}/{HttpProbeCount}：{FormatError(probe.Error)}");
            }

            return false;
        }

        private static int CalculateSecondRoundCandidateCount(int sampledIpCount)
        {
            return sampledIpCount <= 0
                ? 0
                : (int)Math.Ceiling(sampledIpCount * SecondRoundCandidateRatio);
        }

        private static bool IsAssignedDelayInvalid(TimeSpan baselineDelay, TimeSpan currentDelay)
        {
            if (baselineDelay <= TimeSpan.Zero)
            {
                return false;
            }

            var multipliedThreshold = baselineDelay.TotalMilliseconds * AssignedDelayMaxMultiplier;
            var thresholdMilliseconds = Math.Max(AssignedDelayMinimumThreshold.TotalMilliseconds, multipliedThreshold);
            return currentDelay.TotalMilliseconds >= thresholdMilliseconds;
        }

        public static List<T> SampleData<T>(IList<T> sourceData, int sampleSize)
        {
            if (sampleSize >= sourceData.Count)
                return new List<T>(sourceData);

            Random random = new Random();
            List<T> shuffledData = new List<T>(sourceData);

            for (int i = shuffledData.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                T temp = shuffledData[i];
                shuffledData[i] = shuffledData[j];
                shuffledData[j] = temp;
            }

            return shuffledData.Take(sampleSize).ToList();
        }

        /// <summary>
        /// TCP 延时分位数 → 网段粗分。中位数正好映射到默认分 500，
        /// 这样「未探索」和「中游网段」权重相当，缩圈不会一下子收得太狠。
        /// </summary>
        private static double TcpPercentileScore(double percentile)
        {
            if (percentile <= 0.5)
                return TcpBestScore + (TcpMedianScore - TcpBestScore) * (percentile / 0.5);
            return TcpMedianScore + (TcpWorstScore - TcpMedianScore) * ((percentile - 0.5) / 0.5);
        }

        private static async Task<SelectionResult> SingleSelect(IReadOnlyList<DnsUpdateProvider> dnsUpdateProviders, string trigger)
        {
            RuntimeControl.ClearPending();
            DashboardState.BeginRun(trigger, AppConfig.BandwidthPriority ? "带宽优先" : "延迟优先");
            Console.WriteLine($"开始全量优选，触发原因：{trigger}");

            var selection = await RunFullSelection(dnsUpdateProviders);
            DashboardState.CompleteRun(selection.AssignedIps.Select(item => (item.Rank, item.IP.ToString(), item.BaselineDelay.TotalMilliseconds, item.ColdMs)));
            return selection;
        }

        /// <summary>
        /// 并发粗筛：快速淘汰明显差的候选，只把有希望的送进串行精测。
        /// 3 次探测（1 冷 + 2 热）、2 秒间隔、6 路并发，目的不是精确评分，
        /// 而是「别让一个慢 IP 占着串行队列空耗 35 秒」。最终排名仍由串行精测决定。
        /// </summary>
        private static async Task<List<(IPAddress Ip, double Score)>> CoarseScreenAsync(
            IReadOnlyList<IPAddress> candidates,
            Httping httpPing)
        {
            var passed = new List<(IPAddress Ip, double Score)>();
            var gate = new SemaphoreSlim(Math.Max(1, CoarseScreenConcurrency));
            var done = 0;
            var total = candidates.Count;

            var tasks = candidates.Select(async ip =>
            {
                await gate.WaitAsync();
                try
                {
                    // 粗筛不进入 5 分钟退避：一个 403 卡住整轮不划算，直接判失败
                    var probe = await httpPing.PingWithOutcome(
                        ip,
                        CoarseScreenProbeCount,
                        CoarseScreenProbeInterval,
                        CoarseScreenProbeInterval,
                        pauseOnBackoff: false);
                    var stats = ProbeScore.Compute(probe);

                    if (!probe.SkipNode && probe.Success >= CoarseScreenMinSuccess && stats.Valid)
                    {
                        lock (passed)
                        {
                            passed.Add((ip, stats.Score));
                        }
                    }

                    var n = Interlocked.Increment(ref done);
                    DashboardState.SetSelectionDetail($"HTTP粗筛 {n}/{total} {ip}");
                }
                finally
                {
                    gate.Release();
                }
            }).ToArray();

            await Task.WhenAll(tasks);
            return passed.OrderBy(item => item.Score).ToList();
        }

        private static async Task<SelectionResult> RunFullSelection(IReadOnlyList<DnsUpdateProvider> dnsUpdateProviders)
        {
            var processor = new IPProcessor();
            var seedPool = new SeedPool();
            seedPool.Load();
            var subnetTable = new SubnetScoreTable();
            subnetTable.Load();
            var switchState = SwitchState.Load();

            // 状态丢失时，从当前 DNS 记录反推基准，避免误判「首次分配」导致乱换
            if (string.IsNullOrEmpty(switchState.CurrentIp))
            {
                var baseline = await ProbeCurrentDnsIps(dnsUpdateProviders);
                if (baseline.HasValue)
                {
                    switchState.Initialize(baseline.Value.Ip, baseline.Value.Score, baseline.Value.DelayMs);
                    Console.WriteLine($"切换基准初始化：{baseline.Value.Ip} 评分 {baseline.Value.Score:0.00}（延迟 {baseline.Value.DelayMs:0.00}ms，从当前 DNS 记录反推）");
                }
                else
                {
                    Console.WriteLine("无法读取当前 DNS 记录，本轮按首次分配处理");
                }
            }

            var seedIps = seedPool.GetIPs();
            if (seedIps.Count > 0)
            {
                Console.WriteLine($"种子库加载：{seedIps.Count} 个历史优质 IP");
            }
            DashboardState.SetSeedPoolInfo(seedPool.GetEntries().Select(r => (r.Ip, r.Score, r.DelayMs, r.LastSeen)));

            // 1. 抽样：50% 网段评分加权 + 50% 全量随机
            //    加权那半偏向历史表现好的网段，随机那半作为评分偏差的保险
            var allIps = processor.LoadIPRanges();
            var weightedCount = (int)Math.Round(AppConfig.MaxIps * 0.5);
            var rng = new Random();
            var weightedIps = subnetTable.SampleWeighted(weightedCount, rng);
            // 加权抽样不足时用全量随机补足，保证总数达到 MAX_IPS
            var randomCount = Math.Max(0, AppConfig.MaxIps - weightedIps.Count);
            var randomIps = SampleData(allIps, randomCount);
            var sampledIps = weightedIps.Concat(randomIps).Distinct().ToList();

            Console.WriteLine($"抽样：{sampledIps.Count} 个（网段加权 {weightedIps.Count} + 全量随机 {randomIps.Count}）");
            Console.WriteLine($"网段分数表：全量 {subnetTable.TotalSubnets} 个网段，已记录 {subnetTable.Count} 个");
            DashboardState.UpdateRun(run => run.Sampled = sampledIps.Count, $"TCP 快筛（{sampledIps.Count}个IP）");

            // 2. TCP 快筛
            var tcpPing = new TcpPing(sampledIps, FirstRoundPingCount);
            var tcpResults = await tcpPing.RunAsync();
            var tcpPassed = tcpResults
                .Where(item => item.Sended == FirstRoundPingCount && item.Received >= FirstRoundPingCount - 1)
                .ToList();

            Console.WriteLine($"TCP 快筛：{tcpPassed.Count}/{sampledIps.Count} 通过");
            DashboardState.UpdateRun(run => run.FirstRoundPassed = tcpPassed.Count);

            // 用本轮全部 TCP 样本给网段打粗分（不只取通过的）。
            // 每轮只有前 10 能进 HTTP 验证，光靠 HTTP 成绩根本学不到网段好坏；
            // TCP 延时虽然粗，但 1000 个样本足够做方向性判断，一轮一轮缩圈。
            var tcpSampleCount = tcpResults.Count;
            if (tcpSampleCount < sampledIps.Count * 0.1)
            {
                // 整体不通是网络异常，不是网段差，这时更新会把整张表毒掉
                Console.WriteLine($"TCP 响应仅 {tcpSampleCount}/{sampledIps.Count}，疑似网络异常，本轮不更新网段分数表");
            }
            else
            {
                var tcpScored = new HashSet<IPAddress>();
                for (var i = 0; i < tcpSampleCount; i++)
                {
                    var item = tcpResults[i];
                    var percentile = tcpSampleCount <= 1 ? 0.5d : (double)i / (tcpSampleCount - 1);
                    var coarse = TcpPercentileScore(percentile);
                    var lossRate = item.Sended > 0 ? 1d - (double)item.Received / item.Sended : 1d;
                    coarse = Math.Min(TcpWorstScore, coarse + lossRate * TcpLossPenalty);
                    subnetTable.RecordSample(item.IP, coarse);
                    tcpScored.Add(item.IP);
                }

                // 一次都没连上的 IP 不进 tcpResults，按最差分记
                var tcpDeadCount = 0;
                foreach (var ip in sampledIps)
                {
                    if (tcpScored.Contains(ip))
                        continue;
                    subnetTable.RecordSample(ip, TcpWorstScore);
                    tcpDeadCount++;
                }

                Console.WriteLine($"网段分数表：本轮记录 {tcpScored.Count} 个延时样本 + {tcpDeadCount} 个完全不通");
            }

            if (tcpPassed.Count == 0)
            {
                subnetTable.Save();
                Console.WriteLine("没有 IP 通过 TCP 快筛，跳过本轮");
                return SelectionResult.Empty;
            }

            // 3. 粗筛候选 = TCP 延迟最低的 N 个
            var coarseCandidates = tcpPassed.Take(CoarseScreenCandidateCount).Select(item => item.IP).ToList();
            Console.WriteLine($"HTTP 粗筛候选：{coarseCandidates.Count} 个（TCP 延迟最低，{CoarseScreenConcurrency} 路并发）");
            DashboardState.SetSelectionDetail($"HTTP粗筛（{coarseCandidates.Count}个IP，{CoarseScreenConcurrency}路并发）");

            // 4. 并发粗筛：只留少数有希望的进串行精测
            var httpPing = new Httping();
            var coarseStarted = DateTimeOffset.Now;
            var coarsePassed = await CoarseScreenAsync(coarseCandidates, httpPing);
            var coarseSeconds = (DateTimeOffset.Now - coarseStarted).TotalSeconds;
            Console.WriteLine($"HTTP 粗筛：{coarsePassed.Count}/{coarseCandidates.Count} 通过，耗时 {coarseSeconds:0} 秒");

            List<IPAddress> refineIps;
            if (coarsePassed.Count > 0)
            {
                refineIps = coarsePassed.Take(CoarseScreenPassCount).Select(item => item.Ip).ToList();
            }
            else
            {
                // 粗筛全军覆没（多半是整体网络异常），回退 TCP 顺序，避免整轮空转
                refineIps = tcpPassed.Take(CoarseScreenFallbackCount).Select(item => item.IP).ToList();
                Console.WriteLine($"粗筛无候选通过，回退到 TCP 顺序前 {refineIps.Count} 个");
            }

            // 5. 精测候选 = 种子库最优的几个 + 粗筛优胜 + 当前 IP（保证当前 IP 每轮都被测，不会脱管）
            var seedRefineIps = seedIps.Take(SeedRefineCount).ToList();
            var candidates = seedRefineIps.Concat(refineIps).Distinct().ToList();
            if (IPAddress.TryParse(switchState.CurrentIp, out var currentCandidateIp) && !candidates.Contains(currentCandidateIp))
            {
                candidates.Add(currentCandidateIp);
                Console.WriteLine($"当前 IP {currentCandidateIp} 不在种子库，已强制加入候选池");
            }
            Console.WriteLine($"本轮精测：{candidates.Count} 个（种子 {seedRefineIps.Count} + 粗筛 {refineIps.Count} + 当前IP）");

            DashboardState.UpdateRun(run =>
            {
                run.CoarseCandidates = coarseCandidates.Count;
                run.CoarsePassed = coarsePassed.Count;
                run.HttpCandidates = candidates.Count;
            }, $"HTTP 精测（{candidates.Count}个IP）");

            // 6. 串行精测
            var scored = new List<(IPAddress Ip, double Score, TimeSpan Delay, double ColdMs)>();
            var failedCandidates = new List<IPAddress>();
            var seedSet = new HashSet<IPAddress>(seedIps);
            int index = 0;
            foreach (var ip in candidates)
            {
                index++;
                DashboardState.SetSelectionDetail($"HTTP验证 {index}/{candidates.Count} {ip}");
                var probe = await httpPing.PingWithOutcome(ip, HttpProbeCount, FinalProbeMinInterval, FinalProbeMaxInterval);
                var stats = ProbeScore.Compute(probe);

                var passed = !probe.SkipNode && probe.Success >= HttpMinSuccessCount && stats.Valid;
                string? failReason = passed ? null : FormatError(probe.Error);
                var coldMedian = 0d;
                var coldCv = 0d;

                if (passed)
                {
                    // 冷启动稳定性检测：HTTP 探测过了才值得再花这点时间，防住「稳态快、新连接抽风」
                    var coldSamples = await httpPing.ColdProbeAsync(ip, ColdProbeCount, ColdProbeInterval);
                    (coldMedian, coldCv) = ProbeScore.Analyze(coldSamples);

                    if (coldSamples.Count < 2)
                    {
                        passed = false;
                        failReason = $"冷启动有效样本不足（{coldSamples.Count}/{ColdProbeCount}）";
                    }
                    else if (coldMedian > ColdMedianThresholdMs || coldCv > ColdCvThreshold)
                    {
                        passed = false;
                        failReason = $"冷启动不稳（中位 {coldMedian:0}ms，cv {coldCv:0.00}）";
                    }
                }

                if (passed)
                {
                    scored.Add((ip, stats.Score, TimeSpan.FromMilliseconds(stats.WarmMs), stats.ColdMs));
                    Console.WriteLine(
                        $"最终结果 [{index}] {ip} 成功 {probe.Success}/{HttpProbeCount} " +
                        $"冷启动 {stats.ColdMs:0.00}ms 稳态 {stats.WarmMs:0.00}ms → 评分 {stats.Score:0.00}" +
                        $"（稳态cv {stats.CvWarm:0.00}，丢包 {stats.LossRate:P0}；" +
                        $"冷启动3测 中位 {coldMedian:0}ms cv {coldCv:0.00}）");
                }
                else if (probe.Success >= HttpMinSuccessCount && stats.Valid)
                {
                    // HTTP 本身没问题，是冷启动关被刷下来。种子库里也一并剔除，
                    // 否则下一轮还会被当种子带进精测，白占一个位置。
                    failedCandidates.Add(ip);
                    if (seedSet.Contains(ip))
                    {
                        seedPool.Remove(ip);
                    }
                    Console.WriteLine($"最终结果 [{index}] {ip} HTTP 通过，但{failReason} → 淘汰");
                }
                else
                {
                    failedCandidates.Add(ip);
                    Console.WriteLine($"最终结果 [{index}] {ip} 成功 {probe.Success}/{HttpProbeCount} 未通过：{failReason}");
                }

                DashboardState.UpdateRun(run =>
                {
                    run.Candidates.Add(new CandidateResult
                    {
                        Ip = ip.ToString(),
                        Source = seedSet.Contains(ip) ? "种子" : "新抽",
                        HttpSuccess = probe.Success,
                        HttpTotal = HttpProbeCount,
                        HttpAvgMs = stats.Valid ? Math.Round(stats.WarmMs, 2) : null,
                        Passed = passed,
                        Error = failReason
                    });
                    run.HttpPassed = scored.Count;
                });

                if (index < candidates.Count)
                {
                    var delay = GetRandomDelay(FinalCandidateMinInterval, FinalCandidateMaxInterval);
                    Console.WriteLine($"等待{delay.TotalSeconds:0}秒后测试下一个候选");
                    await Task.Delay(delay);
                }
            }

            // 6. 排序
            //    HTTP 验证失败的网段也记一笔差评（比 TCP 粗测更硬的证据）
            foreach (var ip in failedCandidates)
            {
                subnetTable.RecordSample(ip, TcpWorstScore);
            }
            scored.Sort((a, b) => a.Score.CompareTo(b.Score));
            if (scored.Count == 0)
            {
                subnetTable.Save();
                Console.WriteLine("没有 IP 通过 HTTP 验证，本轮结束");
                return SelectionResult.Empty;
            }

            var top = scored[0];
            Console.WriteLine($"本轮最优：{top.Ip} 评分 {top.Score:0.00}（冷启动 {top.ColdMs:0.00}ms 稳态 {top.Delay.TotalMilliseconds:0.00}ms）");

            // 7. 更新种子库（前 N 名）
            var seedCount = Math.Min(scored.Count, SeedPoolCapacity);
            for (int i = 0; i < seedCount; i++)
            {
                var item = scored[i];
                seedPool.AddOrUpdate(item.Ip, item.Score, item.Delay.TotalMilliseconds);
            }
            seedPool.Save();
            DashboardState.SetSeedPoolInfo(seedPool.GetEntries().Select(r => (r.Ip, r.Score, r.DelayMs, r.LastSeen)));
            Console.WriteLine($"种子库更新：本轮入库 {seedCount} 个，当前共 {seedPool.Count} 个");

            // 8. 更新网段分数表（换算到网段表自己的标尺，见 SubnetHitScoreScale）
            foreach (var item in scored)
            {
                subnetTable.RecordHit(item.Ip, item.Score * SubnetHitScoreScale);
            }
            subnetTable.Save();
            Console.WriteLine($"网段分数表：已记录 {subnetTable.Count}/{subnetTable.TotalSubnets} 个网段（本轮失败 {failedCandidates.Count} 个）");

            // 9. 切换判定
            // 计算当前 IP 在本轮评分里的排名（1-based；0 表示本轮测试失败）
            var currentRank = 0;
            if (IPAddress.TryParse(switchState.CurrentIp, out var rankedCurrentIp))
            {
                var rankedIndex = scored.FindIndex(item => item.Ip.Equals(rankedCurrentIp));
                if (rankedIndex >= 0)
                {
                    currentRank = rankedIndex + 1;
                }
            }

            var (shouldSwitch, reason) = switchState.Evaluate(top.Ip.ToString(), top.Score, currentRank, scored.Count);
            Console.WriteLine($"切换判定：{reason}");
            if (!shouldSwitch)
            {
                Console.WriteLine("保持当前 IP 不变");
                // 返回当前 IP，避免外层误判「无可用IP」而立即重跑
                if (IPAddress.TryParse(switchState.CurrentIp, out var keepIp))
                {
                    var currentInScored = scored.FirstOrDefault(item => item.Ip.Equals(keepIp));
                    var measured = currentInScored.Ip != null;

                    var keepDelayMs = measured ? currentInScored.Delay.TotalMilliseconds : switchState.CurrentDelayMs;
                    var keepColdMs = measured ? currentInScored.ColdMs : switchState.CurrentColdMs;
                    var keepScore = measured ? currentInScored.Score : switchState.CurrentScore;

                    // 用本轮实测值刷新基准，保证下一轮比较仍是同一口径（否则会一直用上任时的旧分）
                    if (measured)
                    {
                        switchState.UpdateBaseline(keepScore, keepDelayMs, keepColdMs);
                    }

                    var keep = new AssignedIp(keepIp, TimeSpan.FromMilliseconds(keepDelayMs), 1, keepScore, keepColdMs);
                    return new SelectionResult(new[] { keep });
                }
                return SelectionResult.Empty;
            }

            // 10. 更新 DNS（与后台健康监测互斥，避免两边同时改 DNS）
            var assignedIpsByRank = new Dictionary<int, AssignedIp>();
            await SwitchLock.WaitAsync();
            try
            {
                foreach (var provider in dnsUpdateProviders)
                {
                    foreach (var domain in provider.GetDomains(0))
                    {
                        var updated = false;
                        try
                        {
                            if (AppConfig.DryRun)
                            {
                                updated = LogDryRunUpdate(provider.ProviderName, domain, top.Ip.ToString());
                            }
                            else
                            {
                                Console.WriteLine($"开始更新[{provider.ProviderName}]域名 {domain} → {top.Ip}");
                                updated = await provider.Manager.AddOrUpdateARecord(domain, top.Ip.ToString());
                            }

                            if (updated)
                            {
                                assignedIpsByRank.TryAdd(0, new AssignedIp(top.Ip, top.Delay, 1, top.Score, top.ColdMs));
                                if (!AppConfig.DryRun)
                                {
                                    Console.WriteLine($"已完成更新[{provider.ProviderName}]域名 {domain}");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(ex.ToString());
                        }

                        DashboardState.UpdateRun(run => run.DnsUpdates.Add(new DnsUpdateResult
                        {
                            Provider = provider.ProviderName,
                            Domain = domain,
                            Rank = 1,
                            Ip = top.Ip.ToString(),
                            Success = updated
                        }));
                    }
                }

                if (assignedIpsByRank.Count > 0)
                {
                    switchState.MarkSwitched(top.Ip.ToString(), top.Score, top.Delay.TotalMilliseconds, top.ColdMs);
                }
            }
            finally
            {
                SwitchLock.Release();
            }

            var assignedIps = assignedIpsByRank
                .OrderBy(item => item.Key)
                .Select(item => item.Value)
                .ToList();

            if (assignedIps.Count > 0)
            {
                Console.WriteLine($"本轮实际分配IP：{string.Join(", ", assignedIps.Select(item => $"Top{item.Rank} {item.IP}(冷启动{item.ColdMs:0.00}ms 稳态{item.BaselineDelay.TotalMilliseconds:0.00}ms 评分{item.Score:0.00})"))}");
            }
            else
            {
                Console.WriteLine("本轮没有记录到成功分配的IP");
            }

            if (assignedIps.Count == 0 && IPAddress.TryParse(switchState.CurrentIp, out var fallbackIp))
            {
                Console.WriteLine("DNS 更新未成功，保持当前记录");
                var fallback = new AssignedIp(fallbackIp, TimeSpan.FromMilliseconds(switchState.CurrentDelayMs), 1);
                return new SelectionResult(new[] { fallback });
            }

            return new SelectionResult(assignedIps);
        }

        private static TimeSpan GetRandomDelay(TimeSpan minDelay, TimeSpan maxDelay)
        {
            if (maxDelay <= minDelay)
            {
                return minDelay;
            }

            var milliseconds = Random.Shared.NextInt64((long)minDelay.TotalMilliseconds, (long)maxDelay.TotalMilliseconds + 1);
            return TimeSpan.FromMilliseconds(milliseconds);
        }

        private sealed record SelectionResult(IReadOnlyList<AssignedIp> AssignedIps)
        {
            public static SelectionResult Empty { get; } = new(Array.Empty<AssignedIp>());
        }

        private sealed class AssignedIp
        {
            public AssignedIp(IPAddress ip, TimeSpan baselineDelay, int rank, double score = 0, double coldMs = 0)
            {
                IP = ip;
                BaselineDelay = baselineDelay;
                Rank = rank;
                Score = score;
                ColdMs = coldMs;
            }

            public IPAddress IP { get; }

            /// <summary>稳态延时（热路径）。</summary>
            public TimeSpan BaselineDelay { get; set; }

            public int Rank { get; }

            /// <summary>ProbeScore 口径的评分。</summary>
            public double Score { get; }

            /// <summary>冷启动延时（首次访问）。</summary>
            public double ColdMs { get; set; }
        }

        private static string FormatError(string? error)
        {
            return string.IsNullOrWhiteSpace(error) ? "Unknown" : error;
        }

        /// <summary>
        /// 演练模式（DRY_RUN=true）下代替真实 DNS 更新。
        /// 只打日志并返回 true，让流程照常往下走 —— 否则「无可用 IP」会触发无限重跑。
        /// </summary>
        private static bool LogDryRunUpdate(string providerName, string domain, string ip)
        {
            Console.WriteLine($"[演练模式] 本应更新[{providerName}] {domain} → {ip}，已跳过（未发出任何 DNS 请求）");
            return true;
        }

        private sealed class DnsUpdateProvider
        {
            private readonly string[][] _domainGroups;

            public DnsUpdateProvider(string providerName, string variablePrefix, IDnsRecordManager manager, params string[][] domainGroups)
            {
                ProviderName = providerName;
                VariablePrefix = variablePrefix;
                Manager = manager;
                _domainGroups = domainGroups;
            }

            public string ProviderName { get; }
            public string VariablePrefix { get; }
            public IDnsRecordManager Manager { get; }

            public string[] GetDomains(int rank)
            {
                return rank >= 0 && rank < _domainGroups.Length
                    ? _domainGroups[rank]
                    : Array.Empty<string>();
            }

            public string GetVariableName(int rank)
            {
                return $"{VariablePrefix}_DOMAINS{(rank == 0 ? string.Empty : (rank + 1).ToString())}";
            }
        }

        private sealed class ResolvedDomainIp
        {
            public ResolvedDomainIp(IPAddress ip, int rank)
            {
                IP = ip;
                Rank = rank;
            }

            public IPAddress IP { get; }
            public int Rank { get; set; }
            public HashSet<string> Domains { get; } = new(StringComparer.OrdinalIgnoreCase);
        }
    }
}
