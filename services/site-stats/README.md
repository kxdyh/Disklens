# 官网贡献统计快照

一个独立的 Cloudflare Worker，仅在北京时间每天 00:00 和 12:00 读取既有共享经验 D1，并把聚合结果写入公开 R2 的 `stats/contributions.json`。没有公开查询、刷新或管理接口，不修改应用的云端匹配、贡献上传、注册、公告或版本服务。

## 统计口径

- `todayContributions`：北京时间当日开始至统计时刻，服务端接收的中、高置信度原始分析回执数量。回执以 `(installation_id, analysis_id)` 唯一，重放不会增加。对同一位置提交新的原始分析可以计为新贡献。既有业务不清理这些永久回执，之后撤回经验不会反向抹去当日已经接收的贡献。
- `totalExperiences`：当前公开路径索引可提供的经验身份数量，每个规范 `bucket_key` 只计一次。与 `shared-knowledge/src/path-index.ts` 同样要求：存在未撤回的中、高置信度贡献、越过作者撤回 epoch、指纹有效且可复用、重建键一致。私有观察、不合格置信度和已撤回经验不计入。无大小阈值或候选数量上限；采用每页 1000 项的键集分页扫描完整索引。
- 统计读取只有标识字段和数量，不读取分析正文、凭据或用户私人路径。公开 JSON 只包含聚合数字和时间。单个经验可能由多人贡献，因此两项统计不是同一个分母。

规则直接导入相邻公开服务的 `rules.ts`。部署更新共享协议时也应检查此 Worker，使统计与公开索引规则保持一致。

## 公开数据契约

```json
{
  "schemaVersion": 1,
  "day": "2026-11-19",
  "timeZone": "Asia/Shanghai",
  "todayContributions": 0,
  "totalExperiences": 1443,
  "updatedAt": "2026-11-19T04:12:25.523Z",
  "expiresAt": "2026-11-19T16:00:00.000Z",
  "refreshIntervalSeconds": 43200
}
```

官网可以在本地缓存到 `expiresAt`。跨北京时间午夜后，不能把昨天的 `todayContributions` 显示成“今日新增”；刷新失败时应隐藏当日值或显示暂不可用。`day` 和 `expiresAt` 都应验证。R2 对象使用 `public, max-age=0, must-revalidate`，避免快到午夜时取得的对象被 HTTP 缓存额外延长十二小时。浏览器通常只在本地快照过期时发起一次 R2 请求，不因访问人数而触发 D1 扫描。

只有完整收集成功才会覆盖公开对象；查询失败会保留上一个快照，前端根据日期和有效期判断是否继续展示。

## 自行部署

1. 安装本目录开发依赖，或使用现有 Wrangler 4 与 Node.js 24。
2. 复制 `wrangler.jsonc` 为 `wrangler.production.jsonc`，填入自己的既有 D1 和公开 R2。生产配置被 `.gitignore` 排除；仓库内不含登录凭据。
3. 可选择先对 D1 应用 `migrations/0001_receipt_time_index.sql`，给 `received_at` 添加覆盖索引。它只增加索引，不改数据。没有此索引时，统计仍然正确，但每十二小时的回执计数会扫描整个回执表。索引本身也会增加后续上传的一次索引写入。
4. `wrangler deploy --dry-run --config wrangler.production.jsonc` 验证，再执行 `wrangler deploy --config wrangler.production.jsonc`。此配置只创建独立 Worker 和 Cron Trigger，不覆盖业务 Worker 的路由。
5. 首次上线可运行一次引导脚本，不必等下一次定时刷新：

```sh
node tools/bootstrap.mjs --wrangler /path/to/wrangler/bin/wrangler.js --config wrangler.production.jsonc --out /path/to/snapshot.json
```

此命令默认仅远程只读并写本地 JSON。需要发布时添加 `--upload --bucket <你的公开桶名>`。它复用 Wrangler 已有认证，不接受或储存服务器密钥。R2 桶的 CORS 应允许官网来源执行 GET。

## 验证

`npm test` 使用真实 SQLite 验证中国时区日界、回执防重、撤回/置信度/私有观察过滤、超过 1000 项的完整分页、时间覆盖索引和失败时不覆盖快照。`npm run check` 检查 TypeScript。
