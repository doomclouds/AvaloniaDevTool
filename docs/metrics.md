# 数值指标采集

> 范围说明：本文保留 Agent/Client 扩展服务的协议、预算、恢复和历史验证资料。文中 Inspector 界面、操作和旧阶段结果仅适用于完整桌面基线 `main/fa1c623`，不代表当前分支 GUI 或统一回归；当前默认 official 的 16 项合同以 [官方能力基准](agent-tools/research/official-capabilities.md) 和对应功能文档为准。扩展入口须显式使用 `--profile extended`，并以该目录的 `tools/list` 确认参数。

指标服务使用 .NET `MeterListener` 采集当前应用内的原始数值测量。它与控件树和属性注册表独立；测量回调可以在应用任意线程执行，回调只写入有界数据，不读取 Avalonia 控件。

本版提供采集、停止、时间窗快照、帧聚合、同步图表、单图清空和有界通知。CPU／GC数据取自目标实际发布的仪表，不由服务合成；样式／资源 Profiler 是独立服务。原版详细功能目录见 [功能目录](research/developer-tools-feature-catalog.md)。

## 接口

| 方法 | 参数 | 返回 |
| --- | --- | --- |
| `metrics.start` | `MetricsStartRequest` | 新采集的 `MetricsStatusDto` |
| `metrics.stop` | 无 | 停止后的状态，保留已有历史供查询 |
| `metrics.snapshot` | 无 | 描述、序列、保留原始样本、窗口统计与显示帧 |
| `metrics.status` | 无 | 配置、计数、容量和错误状态 |
| `metrics.clear` | `MetricsClearRequest` | 清空指定序列或全部序列后的状态；不重启采集 |
| `metrics.samples-added` | 服务端通知 | 最多64个原始样本、涉及的仪表描述和当前状态 |

Client 对应提供 `StartMetricsAsync`、`StopMetricsAsync`、`GetMetricsSnapshotAsync`、`GetMetricsStatusAsync`、`ClearMetricsAsync` 和 `MetricsSamplesAdded` 事件。`MetricsClearRequest.SeriesId` 省略时清空全部；指定时只移除该序列的历史和待通知样本，保留仪表、tag身份、采集ID与单调递增样本序号，未知序列报错。停止后也可清空。与其他 Client 通知一样，调用方需要自行切回其界面线程。

服务由 `AgentSession` 拥有，构造、启动、停止和释放在 UI 线程执行；`Snapshot` 与 `Status` 可并发读取。会话释放必须调用 `MetricsService.Dispose`。每次 Start 会停止旧监听并创建新的 `CaptureId`；仪表／序列ID和样本序号只在该采集内有效。

## 筛选与有界配置

名字筛选区分大小写，支持 `*` 和 `?`。省略 `MeterNames` 时只监听 `Avalonia*` 和 `System.Runtime`；显式传空集合表示不过滤Meter。省略或传空的 `InstrumentNames` 表示接受所选Meter的全部仪表。每组最多16个非空模式，每个最多128字符。状态会回显实际生效的模式。

| 参数 | 默认 | 允许范围 |
| --- | --- | --- |
| `Capacity` | 512 | 1～1024个历史样本 |
| `PendingCapacity` | 256 | 1～2048个待通知样本 |
| `MaxSeries` | 64 | 1～128个仪表与tag组合 |
| `MaxInstruments` | 128 | 1～128个已接受仪表 |
| `MaxTags` | 8 | 0～8个保留tag |
| `PollIntervalMs` | 1000 | 100～60000 ms |
| `NotificationIntervalMs` | 200 | 50～5000 ms |
| `FrameIntervalMs` | 250 | 10～60000 ms，且不超过历史时间窗 |
| `HistoryWindowMs` | 60000 | 100～600000 ms |
| `AggregateFrames` | true | true：按帧绘图；false：显示全部保留的原始点 |

历史同时受真实时间和样本数约束。`WindowEnd` 为采集器当前UTC时间，`WindowStart=WindowEnd-HistoryWindowMs`；边界时间的样本保留，早于左边界的样本在测量、查询、通知或停止时过期，计入 `ExpiredHistorySamples`。UTC时钟回拨时窗口末端不回退。没有测量的静默时段也会在下次查询时过期，不需要新样本触发。Stop 在停止时执行最后一次过期，然后冻结窗口和历史，停止后查询不会继续淘汰。

容量淘汰另计入 `EvictedHistorySamples`；容量上限可能使60秒内的高频原始值提前丢失，因此不声称提供完整60秒分布。通知队列有独立容量，容量淘汰和时间过期均计入 `DroppedPendingSamples`。主动 Clear 和 Stop 清空待通知队列不计作容量或时间损失。超过序列或仪表上限时拒绝新维度，并记录丢弃数；不会无限扩张字典。仪表停止测量后可标为非活动，描述仍占本次采集的名额，重新 Start 可重置。

Tag来自每次 measurement 的tag span，未额外合并Meter或Instrument的声明tag。最多检查前64个tag，排序后选取允许数量；key和值分别限制48和96字符。截断或超量计入 `TruncatedTagSets`，因此截断后的维度不是完整原始维度的无损表示。除基础值类型外，tag对象只显示类型名，不调用任意业务对象的 `ToString()`。

这些上限同时约束快照的序列化大小。测试使用1024样本、128仪表与序列、每样本8个最长tag及截断元数据，验证快照能装入协议的4 MiB帧；通知每次最多发送64样本。

## 原始值和聚合语义

.NET支持的 `byte`、`short`、`int`、`long`、`float`、`double`、`decimal` 均注册独立回调。每条样本包含：

- `Value`：供图表和窗口统计使用的double近似值。
- `NumericType` 与 `RawValue`：原始数值类型和InvariantCulture文本，保留大long及decimal精度。
- `Timestamp`：本采集器收到回调时的UTC时间，并非仪表自身提供的发生时间。
- `SeriesId`：仪表及保留tag组合的身份。

NaN和Infinity不进入历史，计入 `InvalidMeasurements`。

| 仪表 | `ValueSemantics` | 正确解读 |
| --- | --- | --- |
| Counter／UpDownCounter | `Delta` | 每次Add的原始增量；不会自动当作累计总量 |
| ObservableCounter／ObservableUpDownCounter | `ReportedCumulative` | 回调报告的累计值；不会再次求和 |
| Histogram | `Observation` | 每次Record的原始观测 |
| Gauge／ObservableGauge | `CurrentValue` | 当前值 |

Gauge支持来自本项目使用的.NET 10 API和测试，不证明原版DeveloperTools支持Gauge；原版公开资料的分歧仍留在研究目录中。

序列统计仅使用**真实时间窗内、全局有界历史中仍保留且属于该序列的原始样本**。`WindowScope=retained_time_window`，提供count、min、max、mean、P50／P90／P95和末值。百分位采用 `nearest_rank`：排序后取第 `ceil(N*p)` 个值，始终对原始观测求分位，不对帧平均值再次求分位，也不以有界数据冒充全历史分布。计数器增量的窗口平均值不是速率。高流量序列可能挤出其他序列的全部历史，此时后者窗口count为0。

## 显示帧与同步图表

`MetricsSnapshotDto.Frames` 是绘图所用的有界帧，原始 `Samples` 同时保留。开启聚合时，以本次采集开始时间为公共原点，将原始测量划入 `[start,end)` 帧；帧边界不会随查询时间漂移，全部序列使用相同边界。当前未结束帧也可显示，时间窗左边界相交的帧只统计仍保留的原始值。每帧提供count、min、max、mean、P50／P90／P95以及绘图 `Value`：

| 原始语义 | 绘图聚合 | `Aggregation` |
| --- | --- | --- |
| Histogram `Observation` | 帧内观测平均值 | `observation_mean` |
| Counter／UpDownCounter `Delta` | 帧内最后一次原始增量 | `last` |
| Observable累计量、Gauge及其他原始仪表 | 帧内最后一次报告值 | `last` |
| 关闭帧聚合 | 每条原始样本保持原始时间与数值，count为1 | `raw` |

Counter的帧末值仍是某次Add的增量，不被重命名为累计总量；Observable累计值也不会再次求和。所有double统计均是近似值，原始精度文本仍在 `Samples`；聚合均值溢出时返回null，不发送NaN或Infinity。

Inspector指标页提供轮询、通知、显示／聚合帧、时间窗和聚合开关，设置在下次开始时生效。Observable轮询真实控制SDK回调频率，通知间隔控制原始批次推送，显示帧间隔控制指标页可见时的权威快照刷新频率；手动刷新、开始、停止和清空可以提前拉取快照。原始通知不会自行重新计算截断的Histogram分布。

每个已发现序列有独立图与“清空此图”命令，所有图绑定同一 `WindowStart/WindowEnd` UTC横轴，点的位置由时间差计算，稀疏与密集序列不会各自拉伸成相同样本序横轴。无样本的图保留空图与身份，随后同维度测量可继续填充；停止后图表冻结且仍可清空。图表向左裁切时只投影保留原始值对应的帧，不补造被容量淘汰或未产生的观测。

状态带采集内 `Revision` 和 `LastSequence`。GUI的异步响应同时检查连接、操作代数、采集ID、修订与时间末端，旧pull不能覆盖较新通知，Clear之前发出的pull不能恢复已清数据，重启后旧采集通知也不能复活旧图。通知和显示统计之间可能相差一个显示帧，显示快照保留服务端权威统计。

实时刷新按序列ID原位更新统计行、图行和绑定属性；保留既有图的控件与Clear命令身份，不能通过清空重建集合中断正在按压的按钮。只有序列真正移除或采集切换时才移除对应图。

## 线程和退订

普通测量在SDK发起的线程上同步进入回调。一个锁保护样本、序列和计数；不在回调中发送RPC或触碰业务控件。DispatcherTimer分别触发Observable轮询和通知队列排出，通知在UI线程发送。

Observable回调仍是应用提供的同步函数，应短小且无阻塞。本版会计数并保留轮询异常信息，没有隔离或强制取消任意阻塞回调的能力。停止或释放会停止两只定时器、释放MeterListener并清空待通知队列；已在执行的旧SDK回调通过运行状态与采集代数检查，不能在停止后或新采集中提交旧样本。

## 已执行验证

`MetricsIntegrationTests` 的12项Headless测试覆盖已有及新仪表发现、名字筛选、tag顺序、七种数值与精度文本、历史和通知队列容量、序列及仪表上限、ObservableGauge轮询、UI线程通知、Stop／Dispose静默、重启、异常、非有限值、批大小和包含显示帧的最坏快照预算；还通过真实命名管道验证Start、Status、通知、Stop和Snapshot接口。

新增 `MetricsWindowTests` 的6项使用实际Meter，其中5项注入可控TimeProvider验证精确左边界、静默过期、停止冻结、帧边界、Histogram原始分位、Counter末值与Gauge、原始点模式、单序列／全部清空、连续序号、容量与时间损失分离及无效配置保留原采集；另1项用真正DispatcherTimer比较60000／100ms轮询和5000／50ms通知的实际活动。

`MetricsViewTests` 的3项Inspector Headless测试通过真实控件及管道验证配置、P90文字、多图相同时间轴、单图Clear、停止冻结、5000／50ms显示刷新差异与raw开关；受控真实RPC测试覆盖旧pull晚于新通知、Clear时在途pull与重启后的旧采集通知。既有 `Metrics_tab_shows_raw_histogram_samples_and_retained_window_statistics` 同步验证实际帧均值及三种分位。

`MetricsPointerRefreshTests` 另有1项真实pointer回归：MouseDown后显式等待一次活动页刷新，再在原坐标MouseUp，断言按钮／图行／命令引用仍相同、Click恰好一次且服务端序列实际清空并能继续接收。修正前该例确定性失败，记录为 `sameButton=False, clicks=0, samples=1`；修正后保持原断言通过。

指标专项为Agent **18/18**、Inspector原4项 **4/4**，加pointer回归 **1/1**，无跳过；记录在系统Temp的 `avdt-metrics-phase7/results/metrics-agent.trx`、`metrics-inspector.trx` 和 `avdt-metrics-pointer-phase7/results/metrics-pointer-green.trx`，红灯记录另保留为 `metrics-pointer-red.trx`。这些专项不是全量回归，也不替代桌面多DPI／跨平台图表视觉验收。

实现依据是 [.NET指标采集文档](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/metrics-collection)、[RecordObservableInstruments API](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.metrics.meterlistener.recordobservableinstruments?view=net-10.0) 和 [Avalonia Developer Tools设置](https://docs.avaloniaui.net/tools/developer-tools/settings) 的1000ms轮询、250ms帧、默认聚合、60秒历史及帧内平均／末值描述。窗口边界、精度与nearest-rank是本工具明确公开的合同，不宣称还原未公开的商业内部算法。

