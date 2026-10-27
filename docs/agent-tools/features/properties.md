# 属性、样式、伪类与元素操作

补充：同名附加属性（例如TextSearch.Text）会随控件类型初始化注册。完整props仍保留全部属性；按裸名称过滤/设置时，当前SDK通过HasClrWrapper标记优先选择目标的实际CLR包装属性，明确owner-qualified ID仍可访问同名附加属性。没有唯一包装时继续报歧义，不任意取第一项。原始Client完整属性集合不保证Name唯一，调用方应按Id识别。

本页描述0.1.0当前属性、样式与临时修改合同。旧工具schema对照和兼容profile已退出实现；公开接口使用当前能力目录。

## 输入与实际行为

| 工具 | 输入 | 行为 |
| --- | --- | --- |
| `props` | `nodeId`，`propertyNames=null` | 返回目标的真实 Avalonia 注册属性、有效值和来源；省略/null 返回全部，名单按名称或 owner-qualified ID 精确过滤。名单内 null 被忽略，空名单返回空数组。默认不运行任意 CLR getter。 |
| `set-prop` | `nodeId,propertyName,value:string,isXamlValue=false` | 普通文本按目标类型转换；名称歧义要求使用 `props` 的 ID。只修改可撤销的 styled/attached 属性，不使用 CLR setter 代替框架属性系统。 |
| `styles` | `nodeId,propertyNames=null,includeDefaults=false` | 保留真实样式帧、setter、绑定及校验诊断。名单作用于 setter 和 binding；无匹配 setter 的帧不返回。`includeDefaults=true` 另外提供实际属性 metadata 默认值帧（`Kind=Default`），不是重复当前有效值。 |
| `pseudo-class` | `nodeId,pseudoClass=null,isActive=null` | 省略/null 名称列举该类型声明、当前激活或本会话改过的伪类；非固定通用列表。名称同时接受 `probe` / `:probe`，规范化为 `:probe`；true/false 真正更新框架伪类与选择器样式。 |
| `action` | `nodeId,action` | 精确枚举 `Focus,Enable,Disable,BringIntoView`。Focus 返回框架实际接受结果；启停使用可撤销覆盖；BringIntoView 发出框架滚动请求。 |

输出中的属性预览对象、模板绑定 source 等弱引用 ID，均转换为当前 MCP 应用会话 ID，不能把底层 Agent 的原始 ID 泄露为跨会话可用句柄。

## `null`、`unset` 与 XAML

`value` 必须是 JSON 字符串。普通模式中，严格小写字符串 `"null"` 给引用属性或可空值类型赋真正的 null，仍保留诊断覆盖；非可空值类型拒绝该值。`"unset"` 只释放本会话的诊断覆盖，恢复其下的绑定、本地值或样式。两者不会合并为“清除”。JSON null 不等同于字符串 `"null"`，会报参数错误。其他 string 值保持字面文本；可空 bool 的 `true/false/null` 均实际生效。

覆盖使用 Avalonia Animation 优先级的可释放入口。替换覆盖之前先完成转换与合法性验证；失败时保留之前的值和覆盖。`unset` 后仍在运行的原绑定继续跟随业务源变化。本项目这一恢复策略经过验证，原版的具体优先级和 `unset` 范围仍待对照。

`isXamlValue=true` 使用 Avalonia 12.1.4 的真实 runtime XAML loader，并要求完整 xmlns，支持独立对象及子对象，例如：

```xml
<LinearGradientBrush xmlns="https://github.com/avaloniaui" StartPoint="0%,0%" EndPoint="100%,0%">
  <GradientStop Color="#FFFF0000" Offset="0" />
  <GradientStop Color="#FF0000FF" Offset="1" />
</LinearGradientBrush>
```

对象完整加载、类型兼容及属性验证通过后，才安装新覆盖。非法 XML、错误颜色、缺少 namespace 或不兼容对象均返回错误，原覆盖保持原对象引用。`isXamlValue=false` 给 string 属性传同一 XML 时，它就是普通字符串。独立 `x:String` 文本根由 XML 解析器补足 Avalonia runtime loader 的已知根节点限制，因此也能把字面文本 `null` 作为 XAML string 赋值。

动态 XAML 需要支持动态代码的目标运行时；Native AOT 目标明确报 `xaml_unavailable`，其普通文本、null、unset 路径保留。XAML 限制为 1 MiB 字符，XML DTD 被禁用；运行时 loader 本身仍决定可加载的对象类型与依赖。这里保证的是目标属性覆盖的事务性，不承诺自定义 XAML 类型构造器没有应用级副作用。

## 可空参数与操作结果

名单内部的 null 被忽略；原版对数组 null 成员的行为未知。`pseudoClass` 非 null 且 `isActive` 省略/null 时，本项目只返回现状，不翻转、不复位；名称省略/null 时即使给 `isActive` 也只列举。两项行为已测，但原版尚未确认。有无冒号均能更新真实选择器；输出统一为带冒号的名字。

Focus 对禁用或不能聚焦的控件可返回 `Succeeded=false`；这不是伪造聚焦成功。BringIntoView 是一次真实框架请求，布局更新后才能观察最终滚动位置；专项使用实际 ScrollViewer 离屏控件验证。启停覆盖仍受祖先禁用等框架有效状态规则约束。

## 验证入口

专项文件：`tests/AvaloniaDevTool.Cli.Tests/OfficialPropertiesMcpTests.cs`。所有测试启动独立 stdio MCP 子进程，以 `--pipe` 连接本测试创建的 Headless Avalonia 目标，没有依赖桌面操作或用户现有进程。

| 方法 | 验收项与状态断言 |
| --- | --- |
| `Stdio_props_filter_real_values_and_set_null_unset_preserve_live_bindings` | PROPS01–02、SET01–03：实际绑定值、样式值、null，省略/null/名单/null成员/空结果，普通文本、JSON null 错误、nullable bool、非法转换、绑定源变化与 unset 后恢复。 |
| `Stdio_set_prop_loads_full_xmlns_object_and_rejects_invalid_xaml_without_partial_write` | SET04：完整 gradient/stop 对象、props 可见、四种非法 XAML/类型失败后保持原 brush 对象，开关 false 的 XML 字面文本和独立 x:String。 |
| `Stdio_styles_filter_real_setters_and_include_metadata_defaults_and_template_binding` | STYLES01–03：真实 style setter、模板绑定及 source ID，省略/null/名单，Width 的 metadata `NaN` 与样式有效值 `85` 区分。 |
| `Stdio_pseudo_class_and_actions_change_styles_focus_enablement_and_scroll_visibility` | PSEUDO01–03、ACT01–04：类型专属声明、冒号规范化、true/false 与 null 参数、红蓝样式变化，实际焦点和后续 Text 输入、启停绑定保留与恢复、ScrollViewer 偏移和控件可见几何。 |

复现命令（构建和结果写入系统 Temp）：

2026-10-25 本轮最终专项 **4/4 通过**。TRX：`%TEMP%/avdt-official-properties-results/10062_PALINK_2026-10-25_07_41_54_net10.0.trx`。对应 Temp 构建的 CLI DLL SHA-256 为 `C286C7AC17B4B324A4EBE05EDB3528BD75DB05963035323CB886732335C1DBBD`，Agent DLL 为 `4ABD496842F8A07265465EF0284854ED57EE98F5B771D477667A55B4C53D5DD1`。这是本地行为验证，未执行官方许可下的结果对照。

```powershell
dotnet test tests/AvaloniaDevTool.Cli.Tests/AvaloniaDevTool.Cli.Tests.csproj --filter FullyQualifiedName~OfficialPropertiesMcpTests --artifacts-path "$env:TEMP/avdt-official-properties-artifacts" --logger trx --results-directory "$env:TEMP/avdt-official-properties-results"
```

原版输出形状、bindings/style frame 格式及上述明确标为未知的边界，没有用本地测试冒充原版对照通过。
