<div align="center">

# CloudflareFastCDN Pro

**自动优选 Cloudflare CDN IP，持续更新 DNS A 记录**

[![Release](https://img.shields.io/github/v/release/cyt7732/CloudflareFastCDN?display_name=tag)](https://github.com/cyt7732/CloudflareFastCDN/releases)
[![Docker](https://img.shields.io/badge/Docker-ghcr.io-2496ED?logo=docker&logoColor=white)](https://github.com/cyt7732/CloudflareFastCDN/pkgs/container/cloudflarefastcdn)
[![.NET](https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/License-MIT-green)](LICENSE)
[![Fork](https://img.shields.io/badge/Fork%20of-aiqinxuancai%2FCloudflareFastCDN-blue)](https://github.com/aiqinxuancai/CloudflareFastCDN)

</div>

---

## 这是什么

一个运行在 Docker 里的 Cloudflare 优选 IP 工具：**持续测试 IP → 按稳定性评分排序 → 自动更新 DNS A 记录**。

支持 Cloudflare、腾讯云 DNSPod、阿里云解析。

本项目是 [aiqinxuancai/CloudflareFastCDN](https://github.com/aiqinxuancai/CloudflareFastCDN) 的**深度改造版**——保留了它的 Docker 部署、Web 管理页和 DNS 商适配，但**筛选流程、评分系统、决策逻辑全部重写**。

> 如果你只需要原版功能，请使用上游项目。

## 为什么改造

原版在实际使用中暴露了几个问题：

| 问题 | 现象 | 后果 |
|---|---|---|
| **只看平均延迟** | 延迟 247ms 但一半请求超时的 IP 照样入选 | 用户一半概率转圈 |
| **ICMP 筛选太严** | 4/4、5/5 全通才通过，CF 很多节点限 ICMP | 大量好 IP 被误杀 |
| **种子库分数冻结** | 100 个种子，每轮只有 10 个能进 HTTP 验证 | 33 轮才能刷一遍，等于摆设 |
| **当前 IP 脱管** | 被淘汰的 IP 不再参与测试 | 当前 IP 变差了也没人管 |
| **见好就换** | 差 5% 也换，频繁横跳 | DNS 抖动 |
| **故障恢复慢** | IP 挂了要跑 15 分钟全量优选 | 用户等 15 分钟 |

## 改造后的机制

### 筛选流程

```
每轮（RUN_MINUTES，默认 60 分钟；可设 0 = 连续跑）
  │
  ├─ 抽样 MAX_IPS 个
  │   ├─ 50% 网段加权（从历史高分 /24 网段抽）
  │   └─ 50% 全量随机（探索新网段）
  │
  ├─ TCP 快筛（443 端口，4 次允许丢 1 次）
  │
  ├─ 粗筛候选 = TCP 延迟最低 60 个
  │
  ├─ 并发粗筛（6 路，每候选 3 次探测、间隔 2 秒）
  │   └─ 只做淘汰，不参与最终排名
  │
  ├─ 精测候选 = 种子库前 4 + 粗筛前 8 + 当前 IP（如不在其中）
  │
  ├─ 串行精测（5 次探测，间隔 5~8 秒）
  │   ├─ 第 1 次 = 冷启动（新建 TCP + TLS），单独记录，不参与排名
  │   ├─ 第 2 次起 = 稳态，取中位数 → 评分 = 稳态中位数 × (1 + 稳态cv) × (1 + 丢包率)
  │   └─ 冷启动稳定性门槛：追加 3 次全新连接探测，
  │       中位 > 700ms 或 cv > 0.5 或有效样本 < 2 → 淘汰，不进排名
  │
  ├─ 前 10 名入库（种子库，容量 10，按评分淘汰）
  │
  ├─ 网段分数表记录（/24 网段的最高分与命中次数）
  │
  └─ 切换判定
      ├─ 当前 IP 本轮测试失败              → 换
      ├─ 当前 IP 排到候选池后 1/3          → 换
      ├─ 第 1 名比当前好 15% 以上          → 换
      └─ 其余                              → 不换
```

### 三种触发

| 触发 | 频率 | 行为 |
|---|---|---|
| **定时优选** | `RUN_MINUTES`（默认 60 分钟，可设 0 = 连续跑） | 跑一轮完整优选（约 10 分钟） |
| **健康监测** | 3 分钟（黄牌后 1 分钟） | 独立后台任务盯当前 IP，连续 3 次偏慢直接从种子库快速切换 |
| **心跳检查** | 5 分钟 | 检查当前 IP，连续 3 次失败触发灾备 |
| **手动** | 随时 | 「立即优选」/「立即复检」 |

### 当前 IP 健康监测（快速纠偏）

连续跑（`RUN_MINUTES=0`）时主循环被一轮一轮占满，当前 IP 只能等每轮精测才发现变慢（最多十几分钟）。
为此起一个**独立后台任务**专门盯当前 IP，不占用轮次节奏：

```
平时：每 3 分钟用全新连接测一次当前 IP（测完整首字节）
      ↓ 偏慢（超过基准冷启动 × 1.8，或 > 700ms，或探测失败）
黄牌：改每 1 分钟测一次，确认是不是真的变慢
      ↓ 连续 3 次偏慢（原本那次 + 黄牌 2 次）
动作：从种子库挑最快的几个，几秒内完成切换
      ↓
复位：回到 3 分钟节奏
```

分级确认的意义：一次偶发抽搐（比如 2.4 秒）会黄牌但 1 分钟后复测恢复正常就解除，
避免把平时 371ms 的好 IP 误切走。

### 灾备快速切换

当前 IP 连续 3 次心跳失败（约 15 分钟）→ 从种子库快速测一遍 TCP → **10 秒内换到可用 IP**，不跑全量优选。

### 稳定性评分：冷启动与稳态分开

同一个 `HttpClient` 连续探测同一个 IP 时，**第 1 次是冷启动**（新建 TCP 连接 + TLS 握手），
**第 2 次起是连接复用**（只发请求）。这是两个完全不同的分布，平均在一起会得到一个
既不代表首访、也不代表稳态的中间值。

```
冷启动 ColdMs = 第 1 次探测的延时        → 只展示「首次访问要多久」，不参与排名
稳态 WarmMs   = 第 2 次起成功探测的中位数  → 排名依据
评分 Score    = WarmMs × (1 + 稳态cv) × (1 + 丢包率)
```

为什么要拆开：

- 混在一起时，`cv` 会被抬到 0.7 以上——那不是网络抖动，是「一个冷启动值 + 四个热路径值」混合的必然结果
- 冷启动实测 400~470ms，稳态 60~120ms；平均后得到的 140~190ms 两边都不代表
- 分开之后，稳态的真实跨度（约 60%）远大于测量误差，排名才有意义

**选优、心跳、复检全部走同一个评分函数**，所以任何两个分数都是同一量纲，可以直接比较。

> 冷启动延时所有可用 CF IP 都挤在 400~470ms，排不出有意义的差别——它由链路物理决定，
> 没有备案就是这个成本，换 IP 改变不了。

### 为什么还要一道冷启动门槛

实测发现：**稳态快 ≠ 打开快**。同一个 IP 可能稳态只有 100ms，但每次新建连接要 1 秒以上。
对种子库 10 个 IP 实测首字节：评分 150 以下的都在 350~380ms，评分 200 以上的全部 700ms+，最差 1.4 秒。

更麻烦的是**偶发抽风**：有的 IP 平时 370ms，但每隔几分钟就冒一次 1~2 秒。
低频访问每次都走新连接，这种抖动最伤体验，而稳态评分完全看不出来。

所以精测阶段额外做 3 次全新连接探测（每次新建 HttpClient，测完整建连 + TLS + 请求），
用中位数和 cv 判断真实稳定性：

- 冷启动中位 > 700ms → 典型值就已经很慢
- 冷启动 cv > 0.5 → 典型值还行，但忽快忽慢

命中任一直接淘汰，不进排名、不会被分配。被淘汰的种子 IP 也会从种子库移除，
否则下一轮还会被当种子带进精测、白占名额。

## 快速开始

### Docker Compose（推荐）

```yaml
services:
  cloudflare-fast-cdn:
    image: ghcr.io/cyt7732/cloudflarefastcdn:latest
    container_name: cloudflare-fast-cdn
    restart: unless-stopped
    network_mode: host
    cap_add:
      - NET_RAW
    environment:
      TZ: Asia/Shanghai
      CLOUDFLARE_KEY: your_api_token
      CLOUDFLARE_DOMAINS: your.domain.com
      HTTP_PROBE_URL: https://your.domain.com/
      WEB_USERNAME: admin
      MAX_IPS: "1000"
      PING_THREADS: "16"
      RUN_MINUTES: "60"
      ENABLE_SUPPLEMENTAL_HTTP_CHECK: "true"
    volumes:
      - ./data:/data
```

```bash
docker compose up -d
```

启动后访问 `http://<主机IP>:19401/`。

> **`cap_add: NET_RAW` 必须保留**，TCP 快筛和 ICMP 探测需要原始套接字权限。

### 环境要求

- Docker（建议 `network_mode: host`，避免容器内 DNS 解析问题）
- 一个 Cloudflare API Token（DNS 编辑权限）
- 一个已接入 Cloudflare 的域名

## 配置

### 核心环境变量

| 变量 | 默认 | 说明 |
|---|---|---|
| `CLOUDFLARE_KEY` | - | Cloudflare API Token（DNS 编辑权限） |
| `CLOUDFLARE_DOMAINS` | - | 第 1 名 IP 更新的域名（逗号分隔多个） |
| `CLOUDFLARE_DOMAINS2` | - | 第 2 名 IP 更新的域名 |
| `CLOUDFLARE_DOMAINS3` | - | 第 3 名 IP 更新的域名 |
| `HTTP_PROBE_URL` | `https://www.visa.cn/` | HTTP 验证地址，**建议用已开启 CF 代理的自有站点** |
| `MAX_IPS` | 400 | 每轮抽样 IP 数（建议 1000） |
| `PING_THREADS` | 8 | 并发线程数（建议 16） |
| `RUN_MINUTES` | 60 | 优选周期（分钟）；设 0 = 测完立刻开始下一轮（连续跑） |
| `ENABLE_SUPPLEMENTAL_HTTP_CHECK` | false | 开启每 5 分钟心跳检查 |
| `UPDATE_IP_LIST` | false | 启动时更新 Cloudflare 官方 IP 段 |

完整配置见 Web 管理页「设置」标签，或 `.env.example`。

### 数据文件

所有状态存在 `/data` 目录（建议绑定挂载到宿主机）：

| 文件 | 内容 |
|---|---|
| `seed_pool.json` | 种子 IP 库（10 个精品 IP，每轮精测评分最好的前 4 个；冷启动不稳的会被剔除） |
| `subnet_scores.json` | 网段分数表（`/24` 网段的最高分与命中次数） |
| `switch_state.json` | 当前 IP、评分、连续失败次数 |
| `subnet_cache.json` | 子网缓存（兼容保留，新流程不主动使用） |
| `web_history.json` | 优选历史与延迟采样（Web 页面图表数据） |
| `settings.json` | Web 页面保存的配置 |

**`seed_pool.json` 和 `subnet_scores.json` 跑得越久越准**，建议纳入备份。

## Web 管理页

访问 `http://<主机IP>:19401/`：

- **概览**：当前状态、下次检查倒计时、Top1 延迟、已完成轮数
- **已分配 IP**：当前 DNS 指向的 IP、基线延迟、最近检查
- **种子 IP 库**：10 个精品 IP 及其评分、延迟、最后命中时间
- **延迟曲线**：已分配 IP 的 HTTP 延迟趋势
- **最近一次优选**：本轮候选明细、评分、DNS 更新结果
- **日志**：实时日志，支持关键字筛选
- **设置**：全部配置项（部分需重启生效）

## 与上游的功能对比

| 功能 | 上游 | 本版 |
|---|---|---|
| 延迟测速协议 | ICMP | **TCP 443** |
| 丢包容忍 | 4/4、5/5 全通 | 3/4、4/5 |
| 评分依据 | 平均延迟 | **稳态中位数 × (1+cv) × (1+丢包率)**，冷启动单列 |
| 两级验证 | 无 | **并发粗筛 60 个 + 串行精测** |
| 冷启动门槛 | 无 | **3 次全新连接探测，中位 > 700ms 或 cv > 0.5 淘汰** |
| 候选构成 | 两轮 ICMP 前 7 + 子网缓存 3 | **种子前 4 + 粗筛前 8 + 当前 IP** |
| 种子库 | 100 个，分数冻结 | **10 个，每轮精测前 4，冷启动差的剔除** |
| 网段加权 | 无（10 个网段缓存） | **全量网段记录 + 加权抽样** |
| 切换策略 | 见好就换 | **15% 显著更优 + 后 1/3 兜底** |
| 当前 IP 保护 | 无 | **强制进候选池 + 独立健康监测** |
| 快速纠偏 | 无 | **健康监测 3 分钟黄牌 / 连续 3 次偏慢切换** |
| 故障恢复 | 全量优选（15 分钟） | **灾备快速切换（10 秒）** |
| 心跳检查 | 可选 | **可选（建议开启）** |

## 已知限制

- **物理天花板**：不备案意味着流量必须绕境外节点。好 IP 冷启动约 **350~400ms**、差 IP 700ms~1.4s，
  这是「免费方案」的极限；稳态延时（连接复用后）能压到 80~120ms
- **上游同步**：改造幅度较大，上游更新无法直接 merge，需手动 cherry-pick
- **探测噪声**：单次探测会有偶发抽风（几分钟一次 1~2 秒），健康监测用「黄牌 + 1 分钟确认 + 连续 3 次」分级处理，
  避免把好 IP 误切；其余靠每轮精测兜底

## 致谢

- [aiqinxuancai/CloudflareFastCDN](https://github.com/aiqinxuancai/CloudflareFastCDN) — 本项目的基础
- [XIU2/CloudflareSpeedTest](https://github.com/XIU2/CloudflareSpeedTest) — TCP 测速与丢包率思路
- [Cloudflare](https://www.cloudflare.com/) — 免费 CDN

## License

MIT
