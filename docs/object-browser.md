# CLR 只读对象浏览

> 范围说明：本文保留 Agent/Client 扩展服务的协议、预算、恢复和历史验证资料。文中 Inspector 界面、操作和旧阶段结果仅适用于完整桌面基线 `main/fa1c623`，不代表当前分支 GUI 或统一回归；当前默认 official 的 16 项合同以 [官方能力基准](agent-tools/research/official-capabilities.md) 和对应功能文档为准。扩展入口须显式使用 `--profile extended`，并以该目录的 `tools/list` 确认参数。

对象浏览把会话中的弱对象 ID 延伸到 Control.DataContext、公共 CLR 成员以及集合元素。界面或 MCP 可以逐页读取对象，再通过 ValueObjectId 按需展开嵌套值。服务不自动递归，也不提供 CLR setter；本页的只读语义表示工具不会主动写成员，不保证目标对象自己的 getter 没有副作用。

## Inspector 对象页

Native Inspector 的第十页为“对象”。连接后先在控件树选择一个 Control，再激活此页读取其公共成员；打开 DataContext 可进入业务对象。“打开值”或双击行进入嵌套对象，字典复杂键另有“打开键”。面包屑、后退和“所选控件”支持逐层返回；返回祖先会恢复该层的成员／集合选项及分页偏移。

样式绑定页的 Source／DataContext 字段可用 Native 弱 ID 直接进入这里，DynamicResource 的非字符串 ResourceKey 也可打开真实键对象。来源上下文保留原控件及绑定属性，“返回原绑定”回到原属性卡片，控件树选择始终保持原位。没有可用 ID 时入口禁用；不会把业务对象送去控件树定位，也不把显示文本当作对象身份。失效源保留路径和 `object_expired`，刷新不会显示缓存的旧行，返回绑定后必须重新观察来源。

公共成员和集合元素分开分页，每页 50 项。打开 IList 或 IDictionary 时自动切换集合元素；选择“公共成员 / 字段”仍可检查集合对象自身的成员。上一页、下一页和刷新均作用于当前对象，刷新保留当前偏移；集合后续页直接读取元素，不会重复执行未显示的成员 getter。达到 Offset 10,000 时禁用继续翻页，并明确提示仍可能有后续项。

每行展示实际类型、有界预览、弱对象 ID、自身引用／祖先循环标记、预览截断和独立 getter 错误。真正的 null 使用 IsNull 标记；失败行保留 unavailable 和异常，不能按空值解释。对象已回收时显示 object_expired，保留后退和面包屑操作，可回到父对象重读 getter 后再次打开。仅实现 IEnumerable 的对象明确说明未枚举，不显示为空集合。

对象页只在激活时或显式刷新时请求数据。控件切换清理旧路径；离开对象页取消等待；选择、页面、请求代数和会话 ID 共同阻止旧响应覆盖新状态。断连和窗口释放清理路径、行与待处理请求，已移除行的命令也会失效。它不会后台递归对象图，也不会固定目标对象的生命周期。

XamlMcp 后端的 IInspectionBackend 当前没有 CLR 对象能力，因此此页禁用。它没有回退到反射 Inspector 自身或把缺失数据补成空值。

Sample 的 DataContext.RuntimeDemo 提供可重现业务数据：公共字段、嵌套 Parent 循环、Self、125 项列表、122 项字典、null、长文本及 throwing getter。列表和字典可以实际跨三页检查；本页没有 CLR setter 或编辑按钮。

## 请求与客户端

| 协议方法 | 请求 | 返回 |
| --- | --- | --- |
| `runtime.members` | RuntimeMembersRequest(ObjectId, Offset=0, Limit=50) | RuntimeObjectSnapshot |
| `runtime.items` | RuntimeItemsRequest(ObjectId, Offset=0, Limit=50) | RuntimeItemsSnapshot |

Client API 为 `GetRuntimeMembersAsync(objectId, offset=0, limit=50)` 和 `GetRuntimeItemsAsync(objectId, offset=0, limit=50)`，均支持 CancellationToken。

单页默认 50 项，Limit 范围为 1 到 100，Offset 范围为 0 到 10,000。Offset 超过当前总数但仍在安全范围内时返回空页；超出安全范围则返回 invalid_request。HasMore 表示按本次观察到的总数仍有后续数据，不表示允许突破 Offset 安全上限。

对象已回收或 ID 不属于当前会话时返回 object_expired。全部读取都在目标应用 UI 线程进行；服务会在访问注册表之前拒绝工作线程调用。

## 成员枚举

服务从具体类型向基类逐层查找：

- 公共、实例、具有公共 getter 的属性。
- 公共实例字段。
- 排除静态成员、索引器和没有公共 getter 的属性。
- 同类成员按名称去重，保留最派生的可读声明；属性和字段属于不同种类。
- 按名称排序后分页，只调用本页的 getter，不会为取得总数执行其他页的 getter。

每个 RuntimeMemberInfo 保存 MemberId、Name、Kind、DeclaringType、DeclaredType、Value、IsReadOnly、Error 和 ErrorTruncated。

MemberId 格式为 `DeclaringFullName:Property:Name` 或 `DeclaringFullName:Field:Name`。Kind 进入 ID，避免字段和属性歧义。它与 Avalonia 属性编辑使用的 `Owner:Property` ID 是不同命名空间，不能拿来调用 properties.set。

IsReadOnly 在本版始终为 true，即使原 CLR 字段或属性有 setter。成员拥有 setter 也不会开启编辑入口。

## 值表示与循环

RuntimeValueInfo 包含：

| 字段 | 含义 |
| --- | --- |
| TypeName | 实际类型；null 或读取失败时使用声明类型 |
| Preview | 有界文本预览 |
| ValueObjectId | 可继续展开的弱对象 ID |
| IsNull | 区分真正的 null 与字符串 "null"／读取失败 |
| Truncated | 原预览超过上限，现有内容只是前缀 |
| IsSelfReference | 值的 ID 与当前容器 ID 相同 |

只对明确的简单类型格式化：字符串、字符、布尔、枚举、常见数值、DateTime／DateTimeOffset、DateOnly／TimeOnly、TimeSpan、Guid。数值和日期格式使用 InvariantCulture。

复杂对象和自定义结构不调用任意 ToString，只显示类型名，并给现有引用或装箱值分配弱 ID。复杂值可以继续读取成员；方法返回的新对象或临时装箱值如果没有业务代码持有，ID 可能很快失效。服务不会为了让浏览更方便而把它们固定在内存中。

预览最多 2,048 个 UTF-16 字符，截断不会切开合法 surrogate pair。异常文本也限制为 2,048 字符，通过独立 ErrorTruncated 标明。读取失败使用 Error 字段及 unavailable 预览，不能将其误报为值为 null。

自身引用可由 IsSelfReference 直接识别；更长的 A → B → A 循环需要客户端维护祖先 ID 集合。服务每次只读取一层，不会沿循环自动展开。

## 集合范围

本版明确支持 System.Collections.IList 和 IDictionary：

- IList 通过 Count 和有界索引读取一页，元素序号使用原始 Index。
- IDictionary 在本次请求内创建枚举器，跳过 Offset 后读取至多 Limit 项，并在返回前释放枚举器。
- 字典 Key 和 Value 都使用 RuntimeValueInfo，因此键保留类型与预览，复杂值可进一步展开。
- 单个索引或条目读取失败时保留邻接结果，并在对应 RuntimeItemInfo 中返回 Error。
- 不把集合、元素或枚举器存为跨请求强引用。

仅实现 IEnumerable 的延迟或潜在无穷序列不会被猜测性枚举。成员快照将其 CollectionKind 标记为 UnsupportedEnumerable，runtime.items 返回 unsupported_collection。普通非集合和字符串也不会被误报为空集合。

Count 和枚举顺序是目标集合在该次读取期间的观察，不是事务快照。后台修改集合可能导致计数变化、条目错误或 collection_read_failed。服务不保证不同页之间的数据和顺序不变。

## 执行与错误边界

公共 getter、IList 索引器、集合 Count 和 IDictionary 枚举器可能由目标代码实现。调用方取消或 RPC 超时只能停止等待，不能强制中止已经进入目标 UI 线程的 getter。长期阻塞或带副作用的 getter 仍需由目标应用自行修正。

复杂对象的任意 ToString 不会被调用，但显式请求读取的公共 getter 会执行。异常逐项捕获；反射产生的 TargetInvocationException 会解开一层，保留目标异常类型和消息。

只读浏览不会将字符串预览当作可反序列化的完整值，也不会声称已经支持源对象编辑、私有字段、方法执行、动态对象语义或任意 IEnumerable 导出。

## 验证

RuntimeInspectionTests 包含 13 项 UI 线程测试：

- 从 Control.DataContext 进入嵌套对象与循环，重复对象 ID 保持一致。
- override／隐藏成员去重、声明 owner、公共字段及排序。
- throwing getter 的单项错误；索引器、静态和非公共 getter 排除。
- 复杂类与自定义结构不执行 ToString；装箱结构可只读展开。
- 长值和异常截断、surrogate pair 边界。
- 分页只读取当前页 getter；默认 50、最大 100。
- IList 的空值、简单值、复杂值、自身引用、分页及越界验证。
- IDictionary 的键预览、分页与每请求枚举器释放。
- IEnumerable-only 明确 Unsupported，完全不启动枚举。
- 单个列表索引失败不丢失相邻项。
- 返回快照不阻止移除后的嵌套对象 GC。
- 工作线程被拒绝。

2026 年 10 月 25 日在 Windows、.NET 10、Avalonia 12.1.4 下，13 项通过，无跳过。测试使用独立临时 artifacts 输出，不依赖官方 DiagnosticsSupport，也未执行真实目标业务中的任意方法。

RuntimeBrowserTests 的 6 项 Inspector Headless 测试通过真实 Agent、Client 和管道验证：Control → DataContext → 嵌套对象、自身／祖先循环标记、公共字段及 getter 异常／截断、成员及 IList／IDictionary 翻页、复杂字典键、面包屑／后退／刷新、IEnumerable 拒绝枚举、临时对象被 GC 后的可重试错误、getter 重入触发快速换控件／切页／断连的旧响应保护、重连和 Dispose 清理，以及外部后端禁用。AXAML 测试确认第十页与 RuntimeList 控件存在；桌面布局、双击和实际鼠标验收由独立桌面 QA 记录，不由 Headless 测试代替。

RuntimeSelectionLoadRaceTests 通过受控管道延迟高亮响应，固定复现“选择属性已显示、选择流程尚未结束时激活对象页”的时序。旧实现会自动读取两次；修复后选择流程识别页面激活已经推进的对象请求代数，自动读取恰好一次，显式刷新仍再次读取。原真实 Agent getter 重入测试的精确次数断言保留。

