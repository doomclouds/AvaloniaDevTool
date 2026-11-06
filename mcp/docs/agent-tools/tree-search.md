# 树、父链和搜索

默认 official profile 的三个工具按 [2.2.3 初始输入声明](research/official-mcp-2.2.3-schema.json) 接受参数；返回结构采用本项目 DTO，原版没有公开 outputSchema，尚无带目标的原版调用样本，不能据此声称输出字节兼容。

| 工具 | 参数 | 本地明确行为 |
| --- | --- | --- |
| `tree` | `nodeId=0`, `treeKind=Logical`, `depth=10` | 省略/0 从 roots 开始；指定节点从其直接子项开始。Logical 读取逻辑子项，Visual 读取可视子项，Merged 保留逻辑内容并加入模板组/内容插槽。 |
| `ancestors` | 必填 `nodeId` | 返回真实 Merged 父链，从 root 到自身，包含自身；没有额外 treeKind 参数。 |
| `search` | 必填 `query`, `scopeNodeId=0`, `searchTemplates=false`, `limit=-1` | 按完整类型名或 x:Name 做不区分大小写的部分匹配；不匹配预览文本。省略/0 搜索当前所有 roots，指定 scope 限制在该节点及后代。false 遍历 Logical，true 遍历 Merged，结果不含合成模板组。 |

`tree` 返回扁平 `entries`；每项有 `node`、`parentId`、`depth`。最外层 depth 为 0，指定 scope 时 parentId 是该 scope，roots 的 parentId 为 null。`depth=1` 只返回最外一层，默认 10 返回十层，`-1` 返回完整子树，`0` 返回空 entries。扁平表示使超过 64 层的树不依赖 JSON 的嵌套深度限制。

`search.limit=-1` 不设置结果数量上限；正数限制匹配结果数量，`0` 返回空结果。只有遇到更多匹配时 `truncated=true`；`visited` 是实际访问数量。小于 -1 的 limit/depth 会明确失败。

节点身份来自目标对象注册表；同一对象经 Logical/Visual/Merged、tree/search/ancestors 返回同一公开身份。公开身份同时编码连接代际；切换连接后旧值会失败，不会指向新会话中编号碰巧相同的对象。对 tree/ancestors/search 的作用域输入还会检查节点确实能追溯到当前 root；已移除控件返回 `object_detached`，已回收对象或过期模板合成节点返回 `object_expired`。这些检查和一次快照遍历在目标 UI 线程的一次调用中完成。

Merged 的 TemplateGroup/ContentSlot 是标明 role 的导航节点；实际元素身份可给 props、screenshot、action 等工具使用，合成节点没有 Avalonia 属性或可截图控件。

为了使失常树的处理有明确结果，一次遍历/父链最多允许 100000 个节点；超过后整个请求以 `tree_limit` 失败，不会把不完整结果伪装为 `depth=-1` 的完整结果。Merged 单分支还继承 20000 子项的明确错误边界。工具仅遍历已经存在于运行时树中的对象，不主动实例化虚拟化项目。

## 原版待对照项

- U02：tree 文案提到 null，但实际 schema 是不可空 integer；本项目以省略/0 作为根入口，JSON null 不属于已声明输入。
- U03：官方 treeKind 字符串枚举的 schema default 是数字 0；本地省略时采用 Logical，不能计作原版默认行为已确认。
- U04：官网和初始 schema 的祖先方向描述矛盾；本地 root-first/包含自身已明确，原版顺序和是否包含自身仍待验证。
- SEARCH03/04：模板边界、排序、大小写、scope 包含自身和 limit=0 的原版细节没有样本。本地定义如上，测试验证的是实现行为。
- 默认输出结构、深度计数相对于 root/指定 scope 的精确起点、错误消息以及诊断限制均不是已验证的原版输出协议。

## 聚焦验证

真实 `dotnet ... mcp --pipe ...` 子进程与真实 Avalonia Agent 通信；不依赖窗口点击，也不以直接调用 OfficialTools 代替 stdio。用例位于 `tests/AvaloniaDevTool.Cli.Tests/OfficialTreeMcpTests.cs`。

| 验收 ID | 测试方法 | 样本/断言 |
| --- | --- | --- |
| TREE01、TREE03、TREE04 | `Official_tree_observes_roots_children_depth_and_full_subtrees_beyond_64_levels` | 省略/0 根、指定节点子项、默认=10、depth=1/0/-1、81层后代、83项父链、错误 enum/depth。 |
| TREE02、SEARCH01–04 | `Official_search_uses_type_name_scope_template_switch_and_unlimited_default` | 类型/name 查询、预览文本不匹配、135项默认不限量、显式7/-1/0、真实 scope、模板 false/true、三种树实际差异。 |
| ANC01、TREE04、C06（部分） | `Official_ancestors_and_tree_handles_preserve_identity_and_reject_detached_nodes` | root/中间/叶节点父链、tree/search 同一身份可给 props/action、移除节点后三种作用域操作均拒绝。 |

运行时使用临时目录 `--artifacts-path` 隔离产物，仅执行这些聚焦测试。跨会话/XAML 更新生命周期另由连接/预览聚焦测试覆盖；没有用本文件的节点移除样本替代 XAML 更新验收。

2026-10-25 实测：上述 3 个 stdio 用例全部通过；同目录红测试在旧默认入口下全部失败，实际捕获了 100 项搜索上限、nodeId=0 根入口和返回结构差异。保留的两个旧 Agent 搜索/祖先测试 `ExplorationIntegrationTests.Search_preserves_duplicate_names_and_ancestor_paths_identify_real_objects`、`Search_reports_empty_results_and_explicit_work_limits` 同样通过。结果仅证明本地行为，前述原版未知项仍保持待对照。
