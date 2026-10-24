# 属性检查与编辑

属性页默认读取 Avalonia 属性。勾选“显示 CLR 属性（执行 getter）”后，同一列表也包含公共实例 CLR 属性的实际值；这个选项默认关闭，以免仅选择控件就执行用户代码。CLR 行保持只读，独立“对象”浏览器仍保留按需读取成员、字段和集合的用途。外部只读后端不开放这个开关。

## 列表、分类和草稿

- `CategoryAttribute` 优先作为分类，没有标注时使用声明类型名称。可按分类筛选，并按名称、当前值升降序或“分类 / 名称”排序；分类模式提供可折叠组头，刷新保留组展开状态。
- 搜索同时匹配名称、类型、声明者、分类和当前值。排序与刷新复用属性行；未应用草稿和仍在结果中的选择会保留。
- CLR 与 Avalonia 行具有独立 ID。只有证实为同一 backing property 的包装成员才去重：styled getter 必须引用对应注册字段；direct property 的公开 getter 委托必须引用该 CLR getter。同名但含义不同的隐藏成员继续显示。
- 公共 getter 失败时显示该行错误。索引器、静态成员、非公开 getter 不加入列表。不调用任意对象的 `ToString()`；未知类型仅显示类型名。
- 属性枚举的单行 `Error` 和属性预览的 `Message` 每条最多 2048 个 UTF-16 字符，包含前缀和 `[truncated]` 标记。普通短异常保留原消息；内部异常最多解包 8 层并按引用检查循环，不调用自定义 `GetBaseException()`。`AggregateException` 只显示数量摘要，不拼接或递归读取子异常消息；覆写的 `Exception.Message` 不执行，并明确显示 `override unavailable`。这些限制覆盖属性读取与预览失败说明，不改变其他服务或传输层的约束。

## 编辑器

布尔值使用复选框，数值使用数值输入器并保留文本草稿以支持 `NaN`、科学计数等值，枚举使用下拉选择，flags 使用成员复选框。`Color` 和可安全替换的 `SolidColorBrush` 使用官方 `Avalonia.Controls.ColorPicker` 12.1.4，附带颜色文本输入和当前值色块。

所有编辑器只修改草稿，点击“应用”才写入目标。ColorPicker 与文本草稿同步时抑制回写，外部值更新不会覆盖脏草稿。颜色格式错误会在行内提示。只读成员显示值和说明，不展示可见输入控件。

画刷专用编辑器支持框架内置的 `SolidColorBrush`、`LinearGradientBrush`、`RadialGradientBrush`、`ConicGradientBrush` 及其不可变接口实现快照。属性声明类型决定可选种类；例如 `IBrush` 可在四种类型间切换，`LinearGradientBrush` 属性只允许线性渐变。纯色原有 ColorPicker / 文本编辑与 `properties.set` 路径继续兼容，Inspector 应用结构化草稿时使用 `properties.brush.set`。

- 共同字段：透明度、变换矩阵、变换原点；纯色支持颜色。编辑器把变换复制为当前矩阵值，不复刻变换对象上的动画或绑定。
- 渐变支持色标添加、删除、颜色、offset，以及 Pad / Reflect / Repeat 扩展方式；应用要求 2–256 个色标、offset 在 0–1、透明度在 0–1，数值必须有限。
- 线性渐变支持起点 / 终点；径向渐变支持中心 / 焦点 / 水平及垂直半径；锥形渐变支持中心 / 起始角度。坐标与半径保留 `%` 相对单位或像素绝对单位。
- 独立预览绘制本地草稿对象。刷新、排序和同一对象的重新选择复用行与画刷草稿；应用才创建全新的目标画刷与色标，不原地修改原画刷。切换至另一对象遵循属性页既有行为，丢弃上一对象尚未应用的行草稿。
- `ImageBrush`、`DrawingBrush`、着色器、自定义画刷、声明类型无法接收受支持可变画刷的属性，以及超过 256 色标的渐变保持只读。结构化 RPC 同样拒绝这些类型，不能将其只读展示计为编辑支持。

应用仍使用可释放的 `BindingPriority.Animation` 诊断覆盖。清除释放覆盖，因此会显示绑定的最新值；不会调用 CLR setter，不会替换原始本地绑定，也不会直接修改现有画刷对象。

## 图片与几何预览

`IImage` 与 `Geometry` 属性行提供“预览属性值”。该操作绘制实际属性对象，不截取目标控件，也不读取“资产”页内容。预览由目标 UI 线程生成有界 PNG，保持图片纵横比；几何使用蓝色填充并平移其自身 bounds，使非零原点也能完整显示。

- `properties.preview` 接受对象 ID、属性 ID、弱引用值 ID 和版本号；实际引用被替换、已释放或属性快照已过期时返回 `Stale`。
- 每边允许 1–512 像素，默认 320×240，不放大原值；PNG 上限 1 MiB。只持有弱引用，缓存最多 1024 项，并按批次淘汰。
- 空值、无有限正尺寸、已释放图片和绘制失败显示“无法预览”原因；非法请求尺寸返回 `InvalidValue`。没有有效值句柄时按钮禁用。
- 预览不修改目标属性、几何、图片或目标控件的可见状态。CLR getter 仍属于用户代码；显式开启后无法保证 getter 没有副作用或会及时返回。

## 协议与验证

`PropertyInfoDto.Presentation` 是可选扩展，保留原有构造方式；其中可选 `Brush` 是画刷结构化快照。新增一个 RPC `properties.brush.set`，客户端入口为 `SetBrushAsync`；读取继续使用 `properties.get`。旧请求只有 `objectId` 时仍仅读取 Avalonia 属性；`PropertiesRequest.IncludeClr=true` 才读取 CLR。客户端提供显式 `GetPropertiesIncludingClrAsync`。预览响应使用 `Available` / `NotAvailable` / `Stale` / `InvalidValue` 状态。

`PropertyPresentationTests` 通过命名管道 RPC 与 Headless Skia 验证默认 getter 惰性、CLR 去重与隐藏成员、异常 getter、未知对象格式化、纯色/flags 元数据、真实图片与非零原点几何 PNG 像素、版本失效、尺寸限制和弱引用释放。异常边界另覆盖 90 万中文字符的 getter / `IImage.Size` 失败、`Message` / `GetBaseException` 覆写、聚合及深层内部异常，且每次失败响应后继续读取 roots 确认会话可用。`PropertyEditorTests` 验证真实 AXAML 复选框、数值框、枚举、flags 与 ColorPicker 的草稿行为、绑定最新值恢复、只读可见控件、属性值预览，以及 MainWindow 原 PropertyList 中的排序选择保留。

实现依据 Avalonia 12.1.4 公开 API，未移植 ClassicDiagnostics 代码。ColorPicker 使用其官方 MIT 包与 Fluent 主题：[NuGet](https://www.nuget.org/packages/Avalonia.Controls.ColorPicker/12.1.4)、[官方文档](https://docs.avaloniaui.net/controls/input/selectors/colorpicker)。

`BrushEditorRpcTests` 通过命名管道验证四种画刷字段往返、Skia 实际像素变化、拒绝非法字段 / 不透明画刷、清除与断连恢复绑定最新值及原对象不变。`BrushEditorViewTests` 验证真实 AXAML 的类型切换、色标增删 / 颜色 / offset、起点、径向半径 / 焦点、锥形角度 / 中心、草稿预览和刷新排序选择保留，以及非法颜色保留在未应用草稿中。参考检查了 ClassicDiagnostics 的 `BrushEditor`（仅纯色）和 Avalonia 12.1.4 画刷接口；本次实现独立编写，无代码移植。
