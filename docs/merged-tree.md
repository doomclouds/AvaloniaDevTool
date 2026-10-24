# Merged 树与模板边界

Agent 的 `TreeKind.Merged` 保留用户内容的逻辑父子关系，同时用 `TemplateGroup` 显式包含控件模板的视觉根。模板中的 `ContentPresenter` 通过 `ContentSlot` 引用已在逻辑树里的用户控件，用户对象不会在模板分支再次出现。没有模板、尚未应用模板或没有视觉根时不生成空模板组。

这是本项目独立实现的数据模型，使用现有命名管道与 JSON RPC，不声称兼容 DeveloperTools 的树 wire 协议，也没有移植其闭源实现。

## 协议

原 `TreeNodeInfo` 五参数构造保持可用；末尾三个字段均有默认值。

| 字段 | 含义 |
| --- | --- |
| `Role` | `Element`、`TemplateGroup`、`TemplateRoot`、`TemplatePart`、`ContentSlot` |
| `TemplateOwnerId` | 已由运行时 `TemplatedParent` 证实的模板 owner；模板组直接指向其 owner |
| `SlotContentId` | 内容插槽引用的真实用户控件 ID；插槽自身没有子节点 |

真实控件沿用现有弱对象 ID，因此 Visual、Logical、Merged 中同一个控件的 ID 相同。组与插槽是合成对象，不能作为 Avalonia 属性编辑、元素动作或高亮的目标；UI 可以通过上述 ID 跳转实际对象。Visual 与 Logical 返回默认 `Element` 角色，不混入合成节点。

Agent 的 roots、children、ancestors 和 search 接受 Merged；祖先链使用与子节点枚举相同的父子判定，模板根的路径包含模板组，用户内容仍沿逻辑路径到达 owner。弹层内容沿实际呈现根到达，跨 TopLevel 的逻辑引用不再重复挂载该内容；内部 `Avalonia.Controls.TopLevelHost` 不作为用户节点返回。

MCP `tree` 同样接受 `kind="Merged"`，`includeTemplates=false` 时保留模板组但不继续递归该组，`true` 时继续展开。停止在模板边界属于指定范围，不标记为预算截断。默认 `true`；Visual、Logical 行为不变。MCP `ancestors` 与 `search` 透传 Merged。XamlMcp 适配器没有此模板／内容插槽语义，其 roots、children、ancestors 对 Merged 均明确返回 `unsupported_tree`。

## 生命周期与预算

- owner 和 presenter 的 `ConditionalWeakTable` 分别缓存当前模板组与插槽，保证不发生模板变化时合成 ID 稳定。合成对象只弱引用 owner、模板视觉根、presenter 和内容，保留合成对象也不会保活这些控件。
- 观察到的控件收到 `TemplateApplied` 后立即清除当前组并通知 owner 失效；轮询展开节点时再次检测实际视觉根变化。旧组请求返回 `object_expired`，旧模板真实对象在 Merged 中返回 `object_detached`。后者仍可能被应用自己的引用保留，不能据此声称 CLR 对象已被回收，或撤销其在其他诊断功能中的弱 ID。
- 内容替换后旧插槽引用失效，用户控件按当前逻辑关系重新枚举。关闭弹层后其原呈现路径也不再可达。
- 单分支最多 20,000 个节点，超过时返回 `tree_limit`；祖先链最多 512 个节点并检测循环；搜索沿用 `MaxVisited` 1..20,000、`MaxResults` 1..500 和 `Truncated`。MCP 递归最多 64 层、2,000 个节点，并报告截断。

当前适配针对 Avalonia 12.1.4。模板判断使用该版本 ref 中公开的 `StyledElement.TemplatedParent`、`TemplatedControl.Template`／`TemplateApplied`、`ContentPresenter.Child` 以及公开逻辑／视觉树枚举，无需读取 PrivateApi 的 `VisualChildren`。只有 ContentPresenter 的实际 Child 同时是 owner 的逻辑子控件时才声明内容插槽；不推测 ItemsPresenter 项目插槽、数据模板来源或第三方 presenter 的角色。自定义模板返回 null 会被 Avalonia 自身拒绝，本实现支持的是空／未应用模板，不能修复无效模板本身。

## Inspector 导航

Native 树类型增加第三项 Merged；外部模式仍只提供 Visual／Logical。行内显示模板组、模板根／部件和内容插槽角色。选择合成节点时展示说明，不发送属性、高亮、运行时对象、元素操作或截图请求；导航按钮转到模板所属真实控件或插槽实际内容。

重模板使旧组失效时清理旧组选择；若用户已选择新的真实控件，异步刷新不得清除此选择。MergedTreeInspectorTests 两项真实 Agent＋Client＋VM 测试覆盖角色、owner／slot 导航、工具禁用和重模板竞态。GUI 尚无一键递归展开的两种模板范围；MCP 的 includeTemplates 已区分该行为。

## 证据与验证

静态对照固定为 `AvaloniaUI.DiagnosticsSupport.Avalonia.dll` 2.2.3、`lib/net10.0`，SHA-256 `6ce4138e84b9ed5fbd3aa446ef292e023e92ecd0a2ae18e261293c47ac27a6d2`，MVID `098e181d-5238-45a7-a325-c9de5bdee015`。REA 精选 `AvaloniaElementTree`、`TreeNode`、`ChildNodesRequest` 三个精确类型，83 方法、410 调用边，未匹配类型为零；metadata、CIL、所选字符串 coverage 均为 complete。父证据为 `ev_6329ee2ef389bc6669d54796931b08481b13761e9bcb23f052a00e8fa4ba9878`，summary 为 `ev_7a11eb3a1cd10cedaa69cdad64a22fc063b661435ca2a4943aea462c600e83cc`。完整 canonical bundle 位于本机临时目录 `C:/Users/10062/AppData/Local/Temp/avdt-merged-evidence-20261025.json`，约 4 MB；它不是项目运行依赖。

| 选择方法 | token | 原始 CIL SHA-256 | 解码 tuple SHA-256 |
| --- | --- | --- | --- |
| `CreateTreeNode` | `0x060001a8` | `90abecdf95e4fc4af240932cb4d282bbf60a540fd5d26dbea0341361b7b6ee6a` | `9b67304523dffd26b957ab3ee021a31e228422860bb71d15c52ac017a80c98e2` |
| `GetElementChildrenAndParent` | `0x060001a9` | `1d814b318587fbf8874b2566b483b9819622ee24e97ff50e5ed504098eab5579` | `f963b91c0d7ef62826ea892b37a43a6ee87aeb69c2c2f1139efd04c46c7131e0` |

两方法未截断指令。token 只对上述哈希与 MVID 有意义。静态 CIL 及已有 ILSpy 关键方法重建支持“模板根独立、模板父 ID、内容插槽 ID、停止模板边界”的语义观察，不能证明原桌面 UI 行为，也不是 SDK 运行测试。本项目的合成组结构、ID 生命周期和预算是独立设计。此次没有重做 NativeAOT 冷导入，也没有复制闭源 C# 文本。

`MergedTreeTests` 使用真实 Skia Headless 与命名管道，验证 Button 模板、用户内容不重不漏、插槽引用、所有返回节点的混合祖先路径、重模板旧 ID 与 owner 通知、内容替换、空模板、裸 ContentPresenter、Popup、搜索预算、Visual／Logical 回归和合成组不保活对象。CLI stdio 集成覆盖两种递归模板范围，XamlMcp 的真实连接集成覆盖明确拒绝 Merged。
