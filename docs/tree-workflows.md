# 控件树工作流

> 范围说明：本文保留 Agent/Client 扩展服务的协议、预算、恢复和历史验证资料。文中 Inspector 界面、操作和旧阶段结果仅适用于完整桌面基线 `main/fa1c623`，不代表当前分支 GUI 或统一回归；当前默认 official 的 16 项合同以 [官方能力基准](agent-tools/research/official-capabilities.md) 和对应功能文档为准。扩展入口须显式使用 `--profile extended`，并以该目录的 `tools/list` 确认参数。

Inspector 左侧提供展开一级、递归展开、折叠子树和取消。递归展开默认最多遍历 1000 个节点、深度 32，每批 32 项让出 UI 调度；公开 `ExpandSubtreeAsync` 接受 `TreeExpansionOptions`，支持 `CancellationToken`。可通过界面设置节点、深度和是否进入 Merged 模板组。到达预算或取消会保留已加载部分，并在树区域明确显示结果。折叠后重新展开会读取当前子节点。

原生直接子项使用 `tree.children-page` / `DiagnosticsClient.GetChildrenPageAsync` 真实分页，参数为 `ObjectId`、`Kind`、`Offset=0`、`Count=100`、`Cursor=null`。`Count` 必须为 1..2000，`Offset` 为 0..20000；越界报 `invalid_request`，不会自动夹取。响应包含 `Children`、实际 `Offset`、`TotalCount`、`HasMore` 和 `NextCursor`。可单独传下一页游标，游标携带起点；同时传非零 Offset 时必须与游标一致。Offset 等于总数返回空终页，大于总数返回错误。单分支超过 20,000 个直接子项返回明确的 `tree_limit`，不会伪装完整前缀。旧 `tree.children` 的参数和数组响应保持不变。

游标绑定当前会话、父节点、树类型、过滤后的子项顺序以及真实分支观察的身份和版本。集合改变后即使恢复原顺序，旧游标仍返回 `cursor_invalidated`；释放观察、切换会话或换用父节点／树类型也不能续读旧页。畸形游标返回 `invalid_cursor`。调用方应丢弃旧分页并从零开始。Agent 不为分页保存强引用快照，只在请求期间以最多 20,001 项检查分支边界，返回当前页；各页继续使用共享弱 ID 与原来的 Merged `TemplateGroup`、`TemplateRoot`、`TemplatePart`、`ContentSlot` 角色及引用字段。

Inspector 首次展开加载 100 个直接子项。“分页与模板导航”折叠面板内的“加载更多子项”和页摘要可继续加载全部安全范围内的行。工作流区域可滚动并限制高度，默认窗口展开该面板后仍保留树视口。加载更多追加当前行，不替换选择；搜索、拾取和祖先定位会继续读取后续页直到找到目标。折叠后重新展开会为保留的直接子项选择读取所需后续页并绑定新行。递归展开仍受既有节点预算约束，仅请求剩余预算所需的页；取消保留已经加载的页，折叠及树刷新按既有流程取消请求并释放分支观察。游标失效时保留已有行和选择，并明确提示折叠后重新展开。

原生 Agent 的分支更新基于 Avalonia 公开集合的 `CollectionChanged`、`TemplateApplied` 和内容呈现器的 `Child` 属性事件。集合事件到达时比较排除诊断视觉后的子节点 ID，忽略高亮层自身的添加和移除；Reset 或混合变更仍依据过滤后的当前分支判断。树的变更不再逐分支执行 150 ms 子节点指纹轮询。会话计时器仍负责发现窗口根、清理过期弱引用及批量属性通知。视觉、逻辑、Merged 三种观察分别计数；Merged 订阅真实视觉和逻辑集合，并监控模板及内容插槽变更。

`tree.children` 为兼容原客户端保留“读取并观察该分支”的行为。`tree.observe` 可显式开始观察；`tree.release` 接受 `TreeReleaseRequest(ObjectIds, Kind)` 释放指定分支，不改变属性检查、断点、拾取等独立观察。折叠子树释放所有已加载后代分支，清除其客户端缓存；刷新或切换树模式先释放旧树观察，再创建新一代节点。释放请求每批最多 20,000 个 ID，每批检查取消和会话身份，防止多次展开累计的大树超过协议上限或旧 ID 影响新会话。观察仅弱引用会话、目标和事件源，根移除、对象回收、断开及会话释放均会清理订阅。真实元素 ID 仍来自共享弱注册表，模板组及内容插槽继续使用弱合成节点身份。

选择恢复沿用原生稳定 ID、祖先定位和代数检查，避免慢刷新覆盖较新的手工选择或拾取。折叠保留当前真实对象的属性检查；重新展开会把选择重新绑定到当前行。已重建的模板合成节点可以失效，真实逻辑内容则保持 ID。

`tree.selector` / `GetElementSelectorAsync` 生成 Avalonia 的类型、名称、类、逻辑祖先和 `/template/` 路径语法，并在当前全部根范围使用 Avalonia 12.1 的实际 SelectorParser 和 Selector.Match 验证。结果包含匹配数量、唯一标志和验证截断标志；20,000 节点验证上限到达时不会声明唯一。没有足够的区分条件时保留歧义，不用诊断 ObjectId 拼造不可复用的 selector。当前适配器通过明确限定的反射访问 Avalonia 12.1 内部 parser/matcher；版本不兼容时返回不支持。自定义类型需要 XAML 命名空间映射，不能安全表达的名称或类字符返回不支持，避免输出不能解析的转义。

“复制 Selector”使用窗口的系统剪贴板提供程序。`tree.metadata` / `GetElementMetadataAsync` 和“复制元数据”输出 JSON，包含 `sessionId`、`backendId`、弱 `objectId`、CLR 类型、名称、类和已验证 selector。ObjectId 只在对应原生会话中有效；JSON 不包含连接令牌。未提供剪贴板的宿主会明确报 `unsupported_clipboard`。

`tree.template-parent-selector` / `GetTemplateParentSelectorAsync` 是模板父的独立生成入口，返回所选 ObjectId、实际 `TemplatedParent` 的 `TemplateParentId` 和经过同一 parser/matcher 验证的父 Selector；Merged 模板组也能导航其真实 owner。Inspector 的“复制模板所属控件 Selector”写入系统剪贴板，“定位模板父”按返回的稳定 ID 导航。这两个入口在“分页与模板导航”内，对实际模板部件可达，不依赖合成节点说明栏。部件的“复制 Selector”保留从 owner 经 `/template/` 到部件的路径；模板父入口输出 owner 自身的 selector，两者分别在同一模板实例的当前真实树中验证匹配。没有模板父时返回不支持；owner 是缺少 XAML 命名空间映射的自定义类型时同样返回不支持，不能用空串或诊断 ID 拼接替代。

Classic `CopySelectorFromTemplateParent` 的原始语义是沿 `TemplatedParent` 逆向收集，再输出 `owner /template/ selected-part`，最终匹配所选部件。本项目普通“复制 Selector”已经生成这种模板路径，并另提供明确的“从模板父复制到部件”入口：先通过已有类型化 RPC 确认实际模板父，再读取所选部件的已验证路径，复制到系统剪贴板。读取期间改变所选对象、选择代数、树代数或会话会取消旧结果。owner-only 辅助按钮明确标为“复制模板所属控件 Selector”，保留原命令和 AutomationId；“定位模板父”继续导航 owner。此补充没有新增 wire 方法或 DTO。

共享 selector 验证除了计算全树 MatchCount，还必须直接调用 Avalonia 的真实 `Selector.Match` 确认所选目标本身匹配。类型的 CLR 名称与实际 StyleKey 不一致时，不能因另一个对象唯一匹配而报告成功。确定性回归使用内建 `MaskedTextBox`（实际 StyleKey 为 TextBox）及另一个使用 MaskedTextBox StyleKey 的同名控件，先证明旧实现错误返回支持，再验证修复返回明确不支持；模板部件与 owner 的既有实际匹配断言继续保留。

MCP `tree-children` 直接返回上述分页元数据，可通过 `tree-release` 结束读取产生的观察。`tree` 保留原先的递归调用参数；新增可选 `offset`、`count`、`cursor` 任一参数后进入直接子项分页模式，要求 `nodeId` 和 `depth=1`，`Page` 返回完整页元数据，根节点的 `Children` 为该页，`Truncated` 等于 `HasMore`。分页模式每页 1..2000 个直接子项，`maxNodes` 和 `includeTemplates` 只约束原递归模式；递归模式内部也按页读取，在 2000 总节点／64 层预算处明确报告截断。MCP `template-parent-selector` 返回模板父生成结果，`element-selector` 继续返回所选部件的路径。

共同只读后端支持在当前快照上展开和折叠，保持手动刷新模式；不会伪造原生分支推送、selector 或稳定弱 ID 能力。旧版原生 Agent 缺少 `tree.release` 时界面明确提示升级。原生协议的新方法均有 `DiagnosticsClient` 类型化入口。

Inspector 首次探测分页能力时，若旧版 Agent 返回 `method_not_found`、`unknown_method` 或 `unsupported`，会兼容读取原 `tree.children` 并在本地显示分页，明确说明这不是原生游标，也没有分支事件版本保证。能力按当前 `SessionInfo` 对象引用缓存，重连即使复用同一个 sessionId 也会重新探测；已成功使用原生分页的会话，后续特定请求失败不会悄悄降级。兼容续页会重新读取分支并比较所有已加载前缀 ID；前缀变化或缩短时返回 `tree_changed`，保留已有行并要求折叠后重开。它不能检测“删除后又恢复原序”的历史，因此不会声称具备原生 revision 保证。协议、类型化 Client 和 MCP 的分页入口保持明确的能力错误，不伪造目标 Agent 的原生分页支持。

验证覆盖真实 Headless + Skia 的 Agent 和 Inspector：递归节点/深度预算、包含与排除模板、CTS 取消、折叠后无树通知及重新展开的新子节点、稳定 ID、弱引用回收、重模板和并发选择恢复、parser/matcher 验证与歧义、非法标识符以及窗口按钮复制到平台剪贴板。BringIntoView 验证 ScrollViewer 的实际 Offset、目标进入视口，以及滚前/滚后的真实红色目标像素变化。

`TreePagingTests` 验证 2305 个直接子项完整续页、跨 Visual／Logical／Merged 的稳定 ID、单分支显式上限、恢复原序后的旧游标失效、release 生命周期及同一模板实例的 owner／part 实际匹配。`TreePagingInspectorTests` 先展开折叠面板，再通过真实 Headless 指针点击“加载更多”“复制模板父”“定位模板父”，验证页追加、后续页搜索、折叠重绑选择及平台剪贴板；`TreePagingMcpTests` 经 stdio 覆盖大分支、`tree` 分页元数据、递归截断、失效游标以及独立父／部件 selector。只读快照后端仍在其当前快照内分页显示，不能提供原生游标和增量推送；旧版 Agent 未实现分页方法时，Inspector 使用前述兼容路径，类型化分页 RPC 与 MCP 仍明确返回方法不支持。

