# Dc.App 发布管线 wire 格式 v1.1

Dc.App 通过普通 TCP 向 broker 推数据。本文档定义帧格式 + payload schema，
供 broker / 订阅端实现参考。

**版本**：v1.1（2026-05-18 起）。v1.0 → v1.1 的破坏性变化：每帧在长度前缀后多 2 字节
`[magic][format-id]` 头，使 broker 可自适应识别格式。

## 传输

- **协议**：TCP（无 TLS / 无握手 / 无应答）
- **方向**：Dc.App → broker（单向 push）
- **连接**：一个采集任务（CollectorTask）一条 TCP 连接，复用
- **服务端**：broker 监听，被动接收
- **重连**：Dc.App 在 send 失败后冷却 2 秒，下次 PublishAsync 重连

## 帧格式

```
+----------------+----------+-------------+--------------------+
| length (4B BE) | magic 1B | format-id 1B|  payload (剩余字节) |
+----------------+----------+-------------+--------------------+
```

- `length` — 32-bit big-endian 无符号整数（实际用 int32，<= ~16MB）。**包含**后续 magic + format-id + payload 三段
- `magic` — 固定 `0xDC`，标识 v1.1 帧。收到非 0xDC 时按 v1.0 raw payload 处理（兼容）
- `format-id` — 单字节序列化格式标识：
  - `0x01` = msgpack
  - `0x02` = json
- `payload` — 单条消息序列化字节流，长度 = length - 2

帧无分隔符；接收端按长度严格读完才能开始读下一帧。

## 序列化格式

发送端由 Dc.App 启动时 `appsettings.json` 的 `Messaging:Format` 决定：

| 值 | format-id | 工具 | 备注 |
|---|---|---|---|
| `msgpack` (默认) | `0x01` | MessagePack-CSharp `ContractlessStandardResolver` | 紧凑 + 性能优 |
| `json`           | `0x02` | System.Text.Json，camelCase                          | 人类可读，调试用 |

接收端**不需要预先知道格式** — 直接读 `format-id` 字节按需 dispatch decoder。
单次部署虽仍只跑一种格式（DI 单例），但 broker 可同时服务多套 Dc.App 实例混合格式。

## Payload schema — `TagValue`

```
record TagValue {
    string         item;        // DA: OPC Item ID；AE: SourceID；UA: NodeId
    object?        value;       // DA: 标量；AE: Dictionary<string,object?>；UA: 标量
    ushort         quality;     // OPC 质量字节（DA：原 0x00..0xFF；AE 恒 0xC0 Good）
    DateTimeOffset timestamp;   // UTC，ISO 8601 (json) / msgpack timestamp
}
```

**字段语义**（同源于 `Dc.Opc.Abstractions.TagValue`）：

### `item`
- DA: 采集项 ID，与任务下 `Tag.Item` 一致（如 `Channel1.Device1.Tag1`）
- AE: 事件源 ID（如 `ReactorA/HiTempSensor`）
- UA: NodeId 字符串（如 `ns=2;s=Demo.Static.Scalar.Int32`）

### `value`
- DA: 实际值，类型对应 `Tag.DataType`（boolean / int / float / string / …）
- AE: 字典，键见下表
- UA: 实际值

#### AE 事件 `value` 字典字段

| 键 | 类型 | 说明 |
|---|---|---|
| `source` | string | 事件源 ID（同 `item`） |
| `message` | string | 事件文本 |
| `severity` | int | 0–1000（vendor），1000 = 最严重 |
| `event_type` | string | `Simple` / `Tracking` / `Condition` |
| `category` | int | 事件类别 ID（vendor `QueryEventCategories`） |
| `condition` | string? | 条件名（Condition 事件才有） |
| `sub_condition` | string? | 子条件 |
| `change_mask` | string | 变化掩码文本 |
| `new_state` | string | 新状态文本（`Active`/`Inactive`/...） |
| `ack_required` | bool | 是否需要 ack |
| `active_time` | DateTimeOffset? | 条件激活时间 |
| `cookie` | int | 服务器分配的 ack 句柄 |
| `actor_id` | string? | 操作者 |
| `refresh` | bool | 是否处于 refresh 序列 |
| `last_refresh` | bool | refresh 序列最后一条 |

### `quality`
- DA：原始 OPC quality byte，按位判断质量段：
  - `(quality & 0xC0) == 0xC0` → Good
  - `(quality & 0xC0) == 0x40` → Uncertain
  - `(quality & 0xC0) == 0x00` → Bad
- AE：恒 `0xC0`（事件即事实，无质量降级）
- UA：当前实现固定 `0xC0`（NetStandard OPC.Ua 已剥离质量字段语义）

### `timestamp`
- UTC 时间
- JSON: `"2026-05-17T10:24:33.512+00:00"` ISO 8601
- MessagePack: 标准 timestamp 扩展类型

## 验证

仓库带 `wpf/tools/Dc.WireDump`，最小 TCP 接收器：

```powershell
# msgpack 模式
dotnet run --project wpf\tools\Dc.WireDump -- --port 5000 --format msgpack

# json 模式（先改 appsettings.json 的 Messaging:Format 为 json，重启 Dc.App）
dotnet run --project wpf\tools\Dc.WireDump -- --port 5000 --format json
```

Dc.App 那侧任务的 TCP 地址改成 `127.0.0.1:5000` 即可对端验证。每条 TagValue 被
解码后按时间戳 + 序号美化打印。

## 断网缓存重发（v1.1+）

`appsettings.json` 启用本地队列后，Publisher 在 broker 不可达时把帧字节写到磁盘 queue，
恢复后后台 flusher 自动按 FIFO 顺序补发：

```json
"Messaging": {
  "Format": "msgpack",
  "Queue": {
    "Enabled": true,
    "Directory": "queue",
    "MaxBytes": 104857600
  }
}
```

- 文件路径：`<Directory>/<host>_<port>.bin` + `.cursor` sidecar 记已发 offset
- 帧字节就是 wire 帧本身（4B length + magic + format-id + payload）— 重启可直接重放
- queue 文件超 `MaxBytes` → drop-oldest（先 compact 去掉已发段；仍超则按帧丢最旧未发）
- 单 Publisher 单后台 flusher，2s 周期，连得上就 drain；连不上跳过本轮
- `PublishAsync` 在 queue 模式下失败时抛 `BrokerUnavailableException`（旧 `InvalidOperationException` 仅用于 queue 关闭时的老行为），上层据此区分"真丢"vs"暂存"

## 未来扩展（v2 候选）

- **批量帧**：一帧多条 TagValue，降低 syscall 压力
- **格式头**：1 字节 magic + 1 字节 format id 写在长度之前，自描述、可在线切换
- **应答 / ACK**：broker 反向确认，实现至少一次语义
- **TLS**：cert pinning
- **心跳推送**：Dc.App 把 OPC 订阅器的 HeartBeat 也送上行（当前只内部维护）

破坏性变更若发生，按 semver 升 major + 文档 wire-format-v2.md。
