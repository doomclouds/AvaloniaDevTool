# 资源作用域与查询

资源页通过本机 Agent 查询运行中的 Avalonia 12.1.4 应用。此实现使用公开的 `IResourceDictionary`、`IResourceProvider`、`StyleBase`、`Styles` API；没有引入商业 DeveloperTools 的实现或 wire format。

## 操作

- 刷新显示应用资源、应用样式、所选控件及其父链的局部资源和样式资源；合并字典和主题字典保留真实父子结构。共享引用只展开一次，循环引用会停止并给出说明。
- 默认查询所选字典自己的条目。勾选“包含嵌套字典”后，聚合该字典的合并与主题字典条目。相同键保留每个来源，行中显示原字典路径、作用域、主题；这不是资源解析后的唯一胜出值列表。
- 按字符串键或值类型筛选，按 Key、Type、Source 排序，可切换降序。每页 50 项，上一页／下一页依据 `HasMore` 启用。切换字典、修改筛选／排序会从第一页查询；刷新保留所选字典 ID。
- 可转换的已有字符串键支持临时修改与恢复。聚合行直接修改其原始字典，不会在父字典创建影子键。应用后来写入的新值在冲突、恢复与断连时保留。
- 非字符串键显示类型与会话内弱对象 ID，不调用用户键或值的任意 `ToString()`。所有条目可用“重读”按原字典和键 ID 读取。协议还提供键／引用值的对象 ID，可交给只读对象浏览器 API；键对象与字典被回收后返回 `object_expired`。

键预览最多 256 个 UTF-16 字符，值预览最多 1024 个字符，超长内容附省略号且不会拆分 surrogate pair。预览截断时 GUI 禁止应用该预览作为完整值；恢复诊断修改仍可用。自定义复杂值只显示类型，不执行其格式化方法。

## 新协议

旧的 `inspection.resources`、`inspection.resourceEntries` 与编辑接口保留；新客户端使用独立 DTO：

| 方法 | 请求／响应 | 说明 |
| --- | --- | --- |
| `inspection.resourceTreeQuery` | `ResourceTreeQueryRequest` → `ResourceTreeSnapshot` | 作用域树、provider 类型、共享／循环标记、扫描量与限制 |
| `inspection.resourceEntriesQuery` | `ResourceEntriesQueryRequest` → `ResourceEntriesSnapshot` | IncludeNested、key/type 筛选、排序、Offset/Limit、逐条来源 |
| `inspection.resourceReadEntry` | `ResourceReadEntryRequest` → `ResourceQueryEntryInfo` | 用 `DictionaryId` + `KeyObjectId` 重读，非字符串键无需文本转换 |

条目包含 `OwnerDictionaryId`、`ProviderPath`、`Scope`、`Theme`、`StringKey`、`KeyObjectId`、`ValueObjectId`、编辑／覆盖／预览截断状态。编辑必须使用 `OwnerDictionaryId` 和完整的 `StringKey`；只有已存在的本地字符串键可以修改。

默认树预算 512 个已访问节点，最大 1024；条目查询默认 512 个节点、10000 项，最大 2048 个节点／20000 项。层级深度最多 16，所选控件父链最多 128。节点预算同时计算样式遍历，避免无资源样式造成无界扫描。页大小默认 50、最大 200；筛选文本最多 256 字符；路径／类型／预览均有界，树另有 400000 个输出字符预算，响应保持在现有 4 MiB JSON 帧预算内。

`TotalCount` 是本次扫描后匹配的条目数。`Truncated` 为真时该总数只覆盖已访问部分；`HasMore` 仅表示该已扫描结果还有下一页，不能用它判断整个资源图完整。`Offset > TotalCount` 返回 `invalid_offset`；负数、无效排序、超预算与不合法页大小返回 `invalid_query`。跨页不冻结应用资源；应用修改后应刷新第一页。

服务只缓存弱对象 ID 与有界来源字符串，不持有查询字典、键或复杂资源的强引用。查询中的临时局部引用在返回后释放。为恢复正在进行的诊断编辑而保存的原值／替代值延续已有会话语义。

## 已知边界与目录状态

R01–R04 目前记为**部分实现**：原生 Agent/Client/GUI 已覆盖可枚举字典的作用域、条目、嵌套聚合及查询，真实管道和 GUI 测试通过；不能据此声称与原版所有资源 provider 或排序语义完全等价。

- 非 `IResourceDictionary` 的自定义 provider 显示类型与“不可枚举”，并返回明确限制；不伪装成空字典。无法通过公开 API 遍历的自定义 style 也显式报告。
- 聚合保留重复键与来源；不计算主题／优先级解析后的胜出关系，不声称每一行当前都可由目标控件解析到。
- `Application.Resources` 与所选节点的资源父链都显示，但单次 IncludeNested 以所选字典为根，不自动合并所有应用与父级作用域。
- 字典与键 ID 为单会话弱引用身份；共享字典可在多条聚合路径中出现，读取同一 owner/key 的值相同。路径为有界可读预览，真正编辑身份由 owner ID 与完整字符串键确定。
- XamlMcp 后端仍按其已验证的只读能力边界工作，资源新查询目前由本项目 Native 后端提供。

## 开源依据与验证

先前已核查的 [XamlMcp 固定提交资源／工具实现](research/open-source-agent-tools-audit.md#xamlmcp) 提供资源 host chain、主题查询和明确 capability/gap 的参考；其查询工具没有独立、可恢复的 set-resource。CDP 的分域调用和 [真实兼容验证](research/cdp-compatibility-validation.md) 提醒我们区分协议清单、空返回与实际行为。此处沿用本项目 DTO、弱对象表和冲突恢复规则，没有复制上述项目源码或增加依赖。

`ResourceQueryTests` 在真实 Skia headless Avalonia 与命名管道客户端上覆盖局部／style／merged／theme、重复键来源、原 owner 修改、分页／筛选／排序／预算、不可枚举 provider、共享／循环、UTF-8、长值、危险 ToString、弱对象失效；原有 DynamicResource、恢复及冲突回归继续执行。`ResourceQueryViewTests` 覆盖真实 GUI 查询编辑、AXAML 控件及来源文本、筛选／字典切换的延迟响应与断连响应失效。
