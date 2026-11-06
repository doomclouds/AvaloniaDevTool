# 官方 Agent/MCP 当前验收进度

> 历史页提示：本页保留较早canonical阶段的52项公开契约验收、368项统一测试及当时未完成清单，正文证据不重写。当前默认agent、Windows native/OS输入/真实文件流程及最新统一结果请看 [当前实现状态](../implementation-status.md)；不要把本页旧工具入口、旧总数或原生能力待办当作当前状态，也不要据其推算产品完成率。

更新：2026-10-25，分支 `codex/agent-tools`，完整旧产品基线 `fa1c623`。固定分母取自 [官方能力基准](research/official-capabilities.md) 的 52 项，不删除未知、不加入扩展工具。

**本地公开契约与核心行为：A=48、P=0、U=0、?=4，48/52（92.3%）通过验收。** A 只证明该行的本地公开要求；这不是工程工时完成率，也不是原版逐字段兼容率。原版成功 `tools/call` 的输出、错误和未公开边界尚未取得对照，不能据此宣称“完整复刻”。四项 ? 都有本地实现和测试，但其必要原版语义仍不能判定。

后续 [正常原版许可检查](research/official-permission-probe.md) 已实际调用attach-to-app，返回订阅等级不含工具权限，未附加目标；没有增加通过项。GitHub门户登录现已完成，重启MCP后仍拒绝；Community桌面工具可用与MCP授权分开记录。

修改前基线是 [A7/P35/U6/?4](progress-baseline.md)。新增官方入口已贯穿 CLI、Client、Agent，统一批次 368/368 通过，其中 25 个官方 stdio 用例；17 项目 Debug/Release 构建零警告零错误。源码指纹、DLL 哈希、每项目结果和官方方法清单见 [验证索引](verification/2026-10-25.json) 与 [测试说明](../testing.md)。这 368 项包括旧扩展回归，不能全部计为官方能力项。

另完成 [真实Windows桌面Sample冒烟](verification/desktop.md) 16/16：主窗发现、属性/绑定、真实应用handler、目标PNG和detach恢复。它独立于Headless常规批次，不计入368；没有测试原生file picker或OS输入。

## 逐项状态

A=所需公开契约与可观察核心行为已验证；P=仍有明确缺口；U=关键分支未实现；?=原版语义不足以判定。E 编号对应下表后的实际测试方法。

| ID | 状态 | 本轮证据与边界 |
| --- | --- | --- |
| C01 | A | E1，真实独立 stdio initialize、目录和调用。 |
| C02 | A | E1，15 项输入 schema 名字、必填、类型、默认和 enum 与采样逐字段比较。detach 单列。 |
| C03 | ? | 本地固定16项，原版初始15项及 listChanged 已采样；原版附加后目录未知。 |
| C04 | A | E2a/E2b，真实窗口缺启动调用、独立应用缺桥包；发现不伪造端点，超时含接入指导；启用后无需快捷键。 |
| C05 | A | E3，真实不兼容桥协议返回 unsupported_version 和版本号；不冒充原版包组合及错误文字。 |
| C06 | A | E4/E5/E6，跨 app、XAML 更新和文件切换旧 ID 拒绝，不能误操作新对象。 |
| C07 | A | E1/E8/E9/E12，无效 enum、类型、必填、属性、输入和 XAML 均有可见错误及状态断言。 |
| C08 | A | E1，原始 stdio 比较注解（含缺省时不新增）和 execution；不证明 tasks 请求已经实现。 |
| APP01 | A | E4，省略 id 的多进程发现及可选择标识；id 不预设为 PID。 |
| APP02 | A | E4，返回 ID 真附加后读取 Alpha 独有内容及目标 PID。 |
| APP03 | A | E4，两个独立目标切换，Beta 保留其值且旧 Alpha 节点拒绝。 |
| FILE01 | A | E5，两个官方必填路径、中文空格路径和项目产物关联。 |
| FILE02 | A | E5/E7，默认/false 不构建，缺产物明确失败。 |
| FILE03 | A | E6/E7，true 真构建及多 TFM；编译失败即使存在旧 DLL 也不附加。 |
| FILE04 | A | E5，无预运行 app，真实 App 资源、classful、自定义控件及引用库；未执行正常 Main。 |
| FILE05 | A | E5，保存更新内容和完整预览 PNG，重取 ID；错误源保留旧视图，修正后恢复。 |
| DET01 | ? | E4/E5及生命周期用例已验本地断连、外部 app 存活、自有 previewer 退出；原版参数/目录/进程处置未核。 |
| TREE01 | A | E10，省略/0根和普通节点子项。 |
| TREE02 | A | E11，Logical/Visual/Merged 的实际结果差异。 |
| TREE03 | A | E10，默认10、1、0、-1及81层后代；明确异常遍历上限。 |
| TREE04 | A | E10/E13，树身份可被属性、动作和截图消费；原版默认树/null另列未知。 |
| ANC01 | ? | E10/E13 已验真实父链和 root/中间/叶；原版顺序与包含自身的描述冲突尚未消除。 |
| SEARCH01 | A | E11，type和name部分匹配，不以预览文本冒充名称查询。 |
| SEARCH02 | A | E11，0/省略全范围、指定scope排除外部节点。 |
| SEARCH03 | A | E11，真实模板样本 false/true 结果不同。 |
| SEARCH04 | A | E11，135项默认不限量，显式7/-1/0；原版排序/大小写仍未知。 |
| PROPS01 | A | E8，实际绑定值、样式值和null。 |
| PROPS02 | A | E8，省略/null/显式列表过滤；null成员本地忽略，原版规则未知。 |
| SET01 | A | E8，官方三个字段修改真实文本并重读。 |
| SET02 | A | E8，引用null、nullable bool实际生效。 |
| SET03 | A | E8，字符串unset释放覆盖并恢复持续更新的绑定；原版覆盖优先级另待对照。 |
| SET04 | A | E9，完整xmlns对象、子对象和非法XAML无半写；false仍为普通文本。 |
| STYLES01 | A | E14，真实style setter和模板绑定。 |
| STYLES02 | A | E14，省略/null/名单实际过滤帧、setter和绑定。 |
| STYLES03 | A | E14，默认metadata值与有效样式值明确区分。 |
| PSEUDO01 | A | E15，类型专属伪类列表，不是固定通用集合。 |
| PSEUDO02 | A | E15，true/false改变真实选择器颜色。 |
| PSEUDO03 | ? | E15 已验本地null/省略只读、有无冒号规范化；原版wrapper规则未知。 |
| RES01 | A | E16，省略/0均为Application域。 |
| RES02 | A | E16，child/parent/application同名资源均带真实来源和顺序。 |
| RES03 | A | E16及Agent scope专项，onlySelf是目标端遍历边界，含merged/theme。 |
| ASSET01 | A | E17，目标嵌入图片、字体、文本和二进制得到可用URL。 |
| OPEN01 | A | E17，原始字节、长度、SHA-256、MIME和内容块；含超过1MiB文本。16MiB拒绝分支尚无超限实物夹具。 |
| SHOT01 | A | E18，实际目标PNG尺寸和已知像素、零尺寸/超限错误。原版DPI/裁切/内容块未对照。 |
| INPUT01 | A | E12，Click真实handler计数。 |
| INPUT02 | A | E12，KeyDown真实处理及编辑结果。 |
| INPUT03 | A | E12，独立KeyUp处理计数。 |
| INPUT04 | A | E12，Text实际中文/文本；明确焦点和禁用错误，不声称OS/IME输入。 |
| ACT01 | A | E15，真实IsFocused和后续Text目标。 |
| ACT02 | A | E15，Enable覆盖绑定源false后有效启用。 |
| ACT03 | A | E15，Disable有效禁用并保留原绑定恢复路径。 |
| ACT04 | A | E15，离屏目标使ScrollViewer偏移和可见几何变化。 |

## 直接测试映射

下列均属于 `AvaloniaDevTool.Cli.Tests`；完整通过方法与结果在验证 JSON 内。不是直接调用工具方法的单元测试，而是 production CLI 子进程的真实 stdio。

| 引用 | 类 / 方法 |
| --- | --- |
| E1 | `OfficialConnectionTests.Declared_official_contracts_match_all_fifteen_captured_schemas_and_validate_before_binding` |
| E2a | `OfficialConnectionTests.A_running_window_without_agent_startup_is_not_advertised_and_reports_setup_guidance` |
| E2b | `OfficialConnectionTests.An_independent_application_without_the_bridge_package_reports_the_preparation_requirement` |
| E3 | `OfficialConnectionTests.Incompatible_wire_version_reports_a_diagnostic_instead_of_an_attached_session` |
| E4 | `OfficialConnectionTests.Discovery_and_id_switching_use_two_real_processes_and_reject_previous_node_handles` |
| E5 | `OfficialPreviewTests.Official_paths_default_false_real_project_context_live_saves_and_detach_work_over_stdio` |
| E6 | `OfficialPreviewTests.Build_true_creates_the_selected_multitarget_output_before_attaching_and_can_switch_files` |
| E7 | `OfficialPreviewTests.Missing_build_compile_failure_invalid_xaml_and_invalid_paths_never_create_an_attachment` |
| E8 | `OfficialPropertiesMcpTests.Stdio_props_filter_real_values_and_set_null_unset_preserve_live_bindings` |
| E9 | `OfficialPropertiesMcpTests.Stdio_set_prop_loads_full_xmlns_object_and_rejects_invalid_xaml_without_partial_write` |
| E10 | `OfficialTreeMcpTests.Official_tree_observes_roots_children_depth_and_full_subtrees_beyond_64_levels` |
| E11 | `OfficialTreeMcpTests.Official_search_uses_type_name_scope_template_switch_and_unlimited_default` |
| E12 | `OfficialInputMediaMcpTests.Official_input_routes_all_four_events_and_reports_focus_disabled_and_invalid_arguments` |
| E13 | `OfficialTreeMcpTests.Official_ancestors_and_tree_handles_preserve_identity_and_reject_detached_nodes` |
| E14 | `OfficialPropertiesMcpTests.Stdio_styles_filter_real_setters_and_include_metadata_defaults_and_template_binding` |
| E15 | `OfficialPropertiesMcpTests.Stdio_pseudo_class_and_actions_change_styles_focus_enablement_and_scroll_visibility` |
| E16 | `OfficialInputMediaMcpTests.Official_resources_use_real_application_node_ancestor_merged_and_theme_scopes` |
| E17 | `OfficialInputMediaMcpTests.Official_assets_return_full_verified_text_image_font_and_binary_resources` |
| E18 | `OfficialInputMediaMcpTests.Official_screenshot_returns_target_png_dimensions_and_pixels_and_explicit_limits` |

资源预算/截断、预览取消/切换/父进程退出/构建后代清理等其余官方用例也在统一批次中；方法细节见对应 [树](tree-search.md)、[属性](features/properties.md)、[媒体](features/input-media.md)、[预览](features/preview.md) 文档。

## 剩余工作及工作量判断

当前没有把“只剩4项”解释为只剩7.7%工时。剩余分三层：

1. **确定原版规则**：在合法可用的原版目标会话中对照 C03、DET01、ANC01、PSEUDO03。最新 [REA静态取证](research/runtime-unknowns.md) 仍未恢复这些MCP包装层运行规则。
2. **运行等价审计**：所有原版工具的成功输出、错误、默认树、搜索边界、unset优先级、资源解析、键语法/IME、媒体/DPI等仍需样本；对照后可能产生新的实现差异，不能现在宣称零工作量。
3. **完整业务自验收扩展**：原生file picker/覆盖确认、OS输入、通用流程执行器、性能阈值与发版报告尚未完成。这些超出官方16工具，见 [自动化与验收边界](architecture/automation-and-acceptance.md)，不通过调整官方分母掩盖。

因此目标保持进行中；公开核心已形成可用第一版，完整原版等价与整个应用发版前自验收尚未证明。
