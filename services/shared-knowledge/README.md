# DiskLens 云端共享经验服务

此目录包含 DiskLens 用户端调用的 Cloudflare Worker 和用于本地开发的 D1 数据库迁移。数据库初始为空，不包含官方库中的分析、公告或版本记录。

## 在本机运行

需要 Node.js 24 和 npm。在仓库根目录执行：

```sh
cd services/shared-knowledge
npm ci
npm run check
npm test
npm run db:init
npm run dev
```

本地监听 `http://127.0.0.1:8791`，数据保存在 `.local-state/`。可以在另一终端运行 `npm run test:http`，验证注册、授权、贡献、另一安装匹配经验和撤回等步骤。这个脚本只允许连接本机，请使用单独的开发数据库。

`wrangler.jsonc` 是本地配置示例。若自行部署，请准备独立的 D1 资源和站点，并自行配置发行文件的地址。源码许可见仓库根目录的 [LICENSE](../../LICENSE)。

## 用户端接口

| 用途 | 接口 |
| --- | --- |
| 共享协议和公开路径索引 | `GET /v1/rules`、`GET /v1/path-index` |
| 注册及启用、关闭共享 | `POST /v1/install`、`GET /v1/consent`、`PUT /v1/consent` |
| 批量匹配经验 | `POST /v1/query` |
| 列出、提交和撤回自己的贡献 | `GET /v1/contributions`、`POST /v1/contributions`、`DELETE /v1/contributions` |
| 撤回单条、继续撤回后的物理清理、报告错误 | `DELETE /v1/contributions/{id}`、`DELETE /v1/contributions/cleanup`、`POST /v1/feedback` |
| 公告与版本元数据 | `GET /v1/announcements`、`GET /v1/releases/latest.json`、`GET /v1/releases/history.json`、`GET /v1/releases/manifest.json` |

共享按用户授权启用；应用按匿名路径特征匹配，低置信度和无置信度的分析不进入共享库。旧版本使用的安装专属匿名观察值只可由原安装访问，公开索引不含这类标识。云端分析说明供核验用途，应用仍以本机保护和用户确认决定是否清理。

官方服务台供维护者发布新版本、发布公告、控制新用户加入贡献功能。暂停加入不影响已经开启贡献的用户。服务台代码和官方部署配置维护在独立私有项目中。
