# 官方 MCP 四项未决语义的静态取证

核对日期：**2026-10-25（Asia/Shanghai）**。本轮使用 REA 6.7.0 与 ILSpy MCP，只读分析已发布产物，未启动新的 Ghidra 冷导入、未修改安装或认证、未执行原版 `tools/call`。结论是：**桥层的树选择和伪类转发已有可复核证据，但四项 MCP 运行语义仍未证实，不能据此计为原版行为通过。**

## 产物与证据身份

| 产物 | 本轮身份与来源 |
| --- | --- |
| 原版程序 | 安装位置见 [schema-parity.md](schema-parity.md)；Windows x64，55,258,952 字节；SHA-256 `76a88bb2fd68d40a7b663a0f710c8bc18dc85bf9a2f732dc6f2f84c6ac0f9f08`。本轮 REA 重新确认 PE 无 CLI 目录，不能通过普通托管反编译取得 MCP 方法体。 |
| 原版运行声明 | [official-mcp-2.2.3-schema.json](official-mcp-2.2.3-schema.json)，SHA-256 `977822db586d02321e6583857af59a9de0f1efe4c465059e7abf71fe3e9acdab`；采样为 `2026-10-24T23:06:53.322625+00:00` 的 initialize / tools/list，仅初始未附加会话。服务版本 `2.2.3+92df9d92acf0bbe8b2ffa96370c1bf848df81d27`。 |
| 桥包 | 本轮从 NuGet 官方 flat-container 下载 `AvaloniaUI.DiagnosticsSupport 2.2.3` 到临时目录，未安装入项目；nupkg 1,843,111 字节，SHA-256 `24815c9879f1540ade19a149ee72b54444a0d3360834be99c9cefd9364301549`。 |
| 桥 DLL | 包内 `lib/net10.0/AvaloniaUI.DiagnosticsSupport.Avalonia.dll`；1,370,624 字节；SHA-256 `6ce4138e84b9ed5fbd3aa446ef292e023e92ecd0a2ae18e261293c47ac27a6d2`；MVID `098e181d-5238-45a7-a325-c9de5bdee015`；2.2.3.0，与既有 [诊断服务研究](../../research/diagnostic-services-deep-analysis.md) 的产物身份一致。 |

当前 REA 进程开始时没有活动二进制，也没有保留记录；旧研究临时目录未找到，因此没有声称复用了仍可查询的 Ghidra 数据库。此次选取 6 个桥层类型，扫描 93 个方法、797 条 CIL 调用边；元数据、精选成员与精选 `#US` 覆盖均为 complete。该覆盖只适用于所选类型，不是桌面主程序的全量分析。

| 编号 | 本轮 canonical Evidence ID | 用途 |
| --- | --- | --- |
| E1 | `ev_34cf4416641c99d1a99a39bffc43311cdd96d132f62dd48636bdc4a2f693da0f` | 主程序身份、无 CLI 元数据；本轮重新取得，与旧身份 ID 相同 |
| E2 | `ev_4b55408b82063dc18a3c07df3e964b6fc4082b80b3276be4b02df28b80309453` | 桥 DLL 身份、MVID、框架及完整元数据状态 |
| E3 | `ev_25873d7d1d20c4420d74df9210c825c253dc58242c5ae964c8b0dc10d3058c9e` | 6 个精选类型的原始成员/CIL 记录 |
| E4 | `ev_4b193b1ac808075fd196f9009d0fca51ec3c865668cbee0ee8c2e7b752d91b19` | E3 的完整 93 方法视图；含下文方法 token 和 body 范围 |
| E5 | `ev_58fb6aed58f9ea089d56cd95054d53487a047100baad9e9b846b573d7285b39f` | E3 的完整 797 条调用边视图 |

## 四项问题的结果

| 问题 | 已观察或重建的证据 | 仍不能确定的 MCP 语义 |
| --- | --- | --- |
| `detach` 的目录时机、参数、进程处置 | 归档初始目录只有 15 项，无 detach，并声明 `tools.listChanged=true`。完整主程序文件的区分大小写字节搜索中，小写 ASCII `detach` 4 处均属 Skia `sk_dynamicmemorywstream_detach_*`，UTF-16LE 为 0。此为有限字面值搜索，不是功能不存在证明。 | 是否附加成功后新增工具，新增通知的时机；是否无参数；外部 app 与自有 previewer 如何区别处置；重复断开、失联或切换目标时的行为，均未解决。 |
| `ancestors` 是否包含自身及顺序 | 运行 schema 和原版字节均包含 `Gets parent chain from root.`。桥层 `TreeHandler.RequestNode`（`0x06000790`）从请求对象开始加入白名单，再逐次 GetParent，最后从取得的上层节点 CreateTreeNode，结果同时记录 OriginalNodeId。[E3–E5；ILSpy 重建] | 这证明桥层具有包含原对象的定位路径算法，不能证明 MCP wrapper 必然调用此方法、如何展平层级、是否排除自身或顶层组。文案冲突没有被实测消除；根/中间/模板节点的返回链仍未知。 |
| `pseudoClass` nullable、省略行为及冒号 | schema 接受 `pseudoClass: string|null = null`、`isActive: bool|null = null`。桥层 `VisualHandler.SetPseudoClass`（`0x06000267`）签名为 RuntimeObjectId、string、bool，找到 StyledElement 后直接调用 `Avalonia.Controls.PseudoClassesExtensions.Set`，该方法体未做冒号增补或可空分支。`GetVisualCapabilities`（`0x06000266`）通过继承的 PseudoClassesAttribute、SelectMany、Distinct 得到可用列表，并按 Type 缓存。[E3–E5；ILSpy 重建] | MCP wrapper 如何把 null/省略变成查询或 bool、仅给名称时是否切换/启用/报错、名称为空串和有无冒号时如何处理，仍未知。桥方法接受非 nullable bool，不能据此推出 wrapper 把 null 当 false；桥方法未规范化也不证明 wrapper 或被调用的框架方法没有规范化。 |
| `treeKind` 默认值 | schema 的字符串枚举为 Logical/Visual/Merged，default 却为数字 0。桥层 ElementTreeKind 的 ILSpy 重建值为 **Logical=1、Visual=2、Merged=3**；`TreeHandler` 构造器（`0x06000789`）优先选择 Merged 作为 `_defaultTreeId`，否则取第一棵树。`GetTree`（`0x0600078f`）先处理显式非零 TreeId，再按 NodeId 找树，最后取 `_defaultTreeId`。[E3–E5；ILSpy 重建] | **ElementTreeKind、TreeId、MCP treeKind 不是已证实相同的坐标。** 桥层默认 Merged 不能证明 MCP 省略时为 Merged；桥枚举从 1 开始也不能把 schema 0 映射成 Logical。MCP wrapper 可能有单独枚举、映射或零值分支，其方法体未恢复。 |

这些结论中的“直接调用”和接口签名来自 REA CIL/元数据；具体循环、选择与字段含义结合 ILSpy 重建解释，属于静态推导。它们均没有观察目标执行。有关工具参数和文档目标的完整边界继续以 [official-capabilities.md](official-capabilities.md) 为准。

## 为下一轮保留的原生锚点

对上述同一哈希主程序做完整文件、区分大小写 ASCII / UTF-16LE 有界搜索，得到以下文件偏移。这里只建立字节位置，不建立原生方法地址、归属、参数布局或真实调用图。

| 字面值候选 | 文件偏移（十进制） |
| --- | ---: |
| `ApplicationTools` | 24983254 |
| `TreeTools` | 24983533 |
| `VisualTools` | 24983858 |
| `McpPseudoclassesResult` | 24985345 |
| `<SnapshotNodeChildren>d__1` | 25178023 |
| `<Ancestors>d__2` | 25178051 |
| `<SetPseudoClass>d__3` | 25178567 |
| `treeKind` | 25309040 |
| `Gets parent chain from root.` | 25309151 |
| `pseudoClass`（工具描述内） | 25309504 |
| `ancestors`（小写工具名） | 25380585 |

小写 `ancestors` 的 ASCII 命中为 1 处；`treeKind` 为 1 处；`pseudoClass` 为 3 处。UTF-16LE 的 `treeKind`、`pseudoClass` 均为 0。编译名称、压缩或未保留字面值可能不以这些形式出现，不能从未命中推导代码缺失。既有 RTR 16 与 REA 内置 RTR 9.1 恢复器的边界及多次全图导入超时见 [native-deep-analysis.md](../../research/native-deep-analysis.md)；本轮没有改分析器或重新导入整幅主程序。

## 后续最小对照与本轮产物

四项仍标为“原版语义未知”。后续具有有效许可、正常连接自有目标并获得调用授权时，最小对照应使用能区分三棵树的模板节点与两级祖先样本：

1. 记录初始、成功 attach app、detach、成功 attach file 四种状态的 tools/list 与 listChanged 通知；若广告 detach，先保存其真实 schema，再记录连接断开及自有/外部进程是否仍存活。
2. 对根、两级子节点和模板内节点调用 ancestors，与事先记录的身份对应，直接确认自身、方向及顶层组是否出现。
3. 对同一伪类样本分别比较 pseudoClass 省略/null/空串/带冒号/无冒号，并组合 isActive 省略/null/true/false；保存返回及实际类集合/样式效果，防止用成功响应替代生效证据。
4. 同一稳定目标比较省略 treeKind 与三个显式枚举的树结构；相同结果只有在样本能区分模式时才有证明力。错误数字 0 与省略是不同输入，另记边界。

本轮临时目录为 `C:/Users/10062/AppData/Local/Temp/avdt-runtime-unknowns-20261025`。它不是项目运行依赖，清理后不保证存在。

| 文件 | 内容 / SHA-256 |
| --- | --- |
| `runtime-unknowns-evidence-final.json` | 9 条 REA canonical 记录，8,172,276 字节；`9aa2ae15e3492d09c9ebbbec21c5eb834e318c4a5f90cd76f2b0500a2326267e` |
| `selected-ilspy-reconstruction.json` | 4 个精选类型的 ILSpy 原始重建文本，含依赖未解析警告；`de49c6fff4605ccf57f55dec8c99721e43a68c00c081a90a51fa797ee68bc4f4`。不是产品源码，也没有被执行。 |
| `diagnosticssupport-2.2.3.nupkg`、`package/` | 本轮下载的官方包及解包内容；身份见上表，未加入项目依赖。 |

本轮没有打开原生 active-target 会话，没有需关闭的 Ghidra 进程。没有更改既有官方基准中的未知状态，也没有将本项目实现或测试结果作为原版运行证据。
