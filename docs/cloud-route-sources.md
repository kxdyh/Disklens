# 云端经验连接地址来源

官方应用通过 `https://api.disklens.site/` 访问云端经验服务。直连时，客户端可从独立 DNS 主机获取 Cloudflare 公网候选地址；请求 URL、HTTP Host、TLS SNI 和证书校验始终保留正式 API 域名。第三方来源只被查询 DNS，不会收到文件特征、安装身份、授权或贡献请求。

默认地址来源：

- `route-cn.disklens.site`（作者维护的候选地址记录）
- `icook.tw`
- `cloudflare-ip.mofashi.ltd`
- `nrt.xxxxxxxx.nyc.mn`（八个 `x`）

这些是地址来源，不是国内连接速度的承诺，也不代表接入了 Cloudflare 中国网络。来源可随时改变解析结果；只接受 Cloudflare 公布网段内的地址，拒绝私网、FakeIP 和其他 CDN 地址。HTTPS 证书照常验证。

## 连接行为

应用在同一网络上复用选中的连接池。首次 DNS 发现与只读规则探测共用 5 秒预算，候选 DNS 发现本身最多 750 毫秒且各来源并发。最多四个候选来源各取一个地址，与原 API 最多两个地址一起进行只读竞速；先完整返回 HTTP 200 和有效规则数据的连接胜出，立即取消其余探测。每个来源的多个地址轮换选择，不因记录多而占满探测名额。

如果 5 秒内没有有效结果，业务请求回到原 API 的正常解析与超时。完成选路后不再对每次请求施加 5 秒限制。探测只发送读取规则的 GET，不会并发注册或上传贡献。

选中的优选连接发生网络或响应错误后，后续请求回到原 API 的正常解析器。不会重放已发送的写请求，不增加整轮匹配自动重试。用户取消操作不被视为线路故障；暂停新用户加入等业务响应也不会导致降级。网络或代理状态变化后重新选路。有系统代理时沿用代理，自定义 API 地址不使用官方候选来源。

## 维护候选地址

可以直接修改 `route-cn.disklens.site` 的 DNS-only A/AAAA 记录，不必重新发应用。也可以用一条 DNS-only CNAME 指向维护者信任的地址来源域名；同名 A/AAAA 与 CNAME 不混用。添加地址只增加候选，不保证客户端会逐个尝试所有地址。

需要修改应用本机的整个来源列表时，可在可执行文件旁创建 `cloud-route-sources.json`，内容为主机名数组：

```json
[
  "route-cn.disklens.site",
  "icook.tw",
  "cloudflare-ip.mofashi.ltd",
  "nrt.xxxxxxxx.nyc.mn"
]
```

文件最多 8 KiB、最多 8 个 DNS 主机。只填主机名，不填 URL、路径、端口或凭据。设置环境变量 `DISKLENS_ROUTE_DNS_SOURCES` 可覆盖该文件，使用分号、逗号或空白分隔。无效、空或损坏配置回退默认列表，原 API 回退始终保留。配置在创建客户端时读取，修改后重启应用。

## 验证方法

应在真正没有系统代理、TUN 和上游代理的目标网络，或者国内真实探针上，以相同 API 域名、相同 HTTPS 路径比较原解析与候选 IP。比较证书校验、完整 HTTP 响应、成功率和耗时；不能仅比较 ping、第三方网站自身延迟或海外代理出口。工具的进程禁用代理模式并不会绕过 TUN。

官方依据：[Cloudflare IP 网段](https://www.cloudflare.com/ips/)、[Worker 自定义域名](https://developers.cloudflare.com/workers/configuration/routing/custom-domains/)。
