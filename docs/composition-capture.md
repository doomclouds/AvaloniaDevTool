# 合成器自身绘制采集

`composition.snapshot` 读取已提交的 Avalonia 合成器子树，为**每个节点自身的绘制内容**返回一张透明 PNG，同时返回层级和合成元数据。`layout.snapshot` 继续提供独立的几何检查能力。

## 开源源码证据

适配器依据公开的 Avalonia **12.1.4** 源码实现，固定提交为 `2d79ba55fae1b1e71c69f93a668080c9029e6a31`，无需反编译商业诊断应用：

- [CompositingRenderer](https://github.com/AvaloniaUI/Avalonia/blob/2d79ba55fae1b1e71c69f93a668080c9029e6a31/src/Avalonia.Base/Rendering/Composition/CompositingRenderer.cs)：将 `visual.Render` 记录到该节点自身的 `CompositionRenderData`，子节点单独维护。
- [Visual.Composition](https://github.com/AvaloniaUI/Avalonia/blob/2d79ba55fae1b1e71c69f93a668080c9029e6a31/src/Avalonia.Base/Visual.Composition.cs)：同步变换、透明度和裁剪，并按 `ZIndex` 与插入索引稳定排列子节点；自定义合成节点作为额外子节点加入。
- [Compositor](https://github.com/AvaloniaUI/Avalonia/blob/2d79ba55fae1b1e71c69f93a668080c9029e6a31/src/Avalonia.Base/Rendering/Composition/Compositor.cs)：支持在目标渲染完成后执行服务端任务。公开的 `CreateCompositionVisualSnapshot` 会包含子节点，不适合直接生成逐节点自身绘制图像。
- [ServerCompositor](https://github.com/AvaloniaUI/Avalonia/blob/2d79ba55fae1b1e71c69f93a668080c9029e6a31/src/Avalonia.Base/Rendering/Composition/Server/ServerCompositor.cs)：在应用批次、更新目标并完成渲染后，在合成器访问保护范围内执行目标渲染后的任务。
- [ServerCompositionDrawListVisual](https://github.com/AvaloniaUI/Avalonia/blob/2d79ba55fae1b1e71c69f93a668080c9029e6a31/src/Avalonia.Base/Rendering/Composition/Server/ServerCompositionDrawListVisual.cs)：`ComputeOwnContentBounds` 不包含子节点，`RenderCore` 仅重放当前节点的绘制命令。
- [ServerCompositionVisual.Render](https://github.com/AvaloniaUI/Avalonia/blob/2d79ba55fae1b1e71c69f93a668080c9029e6a31/src/Avalonia.Base/Rendering/Composition/Server/ServerCompositionVisual/ServerCompositionVisual.Render.cs)：将变换、裁剪、透明度、缓存、遮罩和效果的处理，与 `RenderCore` 及树遍历分开执行。

这些成员属于特定版本的 internal/PrivateApi，NuGet 包的引用程序集隐藏了相关成员。反射集中在 `Avalonia12CompositionAdapter` 中，其余服务无需访问合成器内部实现。

## 采集流程与图像语义

1. UI 线程验证所选控件已挂接到可视树，复制弱对象 ID 和显示元数据，并建立应用 `Visual` 到合成器服务端对象的映射。采集不会隐藏、移除或重新挂接应用节点，也不会临时改写其属性。
2. 异步的**目标渲染后合成器任务**统一读取真实服务端层级、当前变换、透明度、可见性和绘制内容。等待结果期间会释放 UI 线程，不会同步阻塞 UI 等待渲染线程。
3. 任务按节点自身内容边界创建透明的后端离屏目标，保留负坐标和越界绘制，并使用继承的渲染及文本选项重放该节点的 `RenderCore`。生成这张 PNG 时不遍历子节点。
4. PNG 保存本地坐标中的原始绘制内容，位于**节点及祖先的变换、透明度、裁剪、遮罩、效果和缓存处理之前**。`ContentBounds` 对齐采集像素网格，其矩形决定 PNG 在节点本地坐标中的位置。例如，蓝色父节点和红色子节点会得到一张蓝色父图及一张独立红色子图，父图不含红色子节点的副本。
5. `Transform` 将节点映射到父节点，`TransformToRoot` 将其映射到所选根节点，根节点使用单位变换。`ChildIndex` 表示服务端绘制顺序。按层级重组时，递归应用父级变换、裁剪及透明度作用域，先绘制节点自身图像，再按顺序绘制子节点。所选根节点之外的祖先不属于这份独立子树快照。

`CaptureId`/`ParentCaptureId` 仅在一次采集中标识节点；`ObjectId` 使用既有的应用弱对象 ID。没有对应 `Visual` 的自定义节点或表面合成节点，其 **`ObjectId` 为 null**，同时保留自身采集 ID、真实合成器类型与绘制内容，不会分配虚构的应用对象 ID。诊断用 `HighlightAdorner` 及其合成器子树会被排除。

`Clip` 表示显式几何裁剪，类型包括 Rectangle、Ellipse、PathEvenOdd、PathNonZero 和 UnavailablePath。路径类型携带不含 Avalonia `F1` 前缀的 SVG 路径数据，填充规则由类型区分。已知 `PathGeometry` 输入限制为最多 128 个图形、1024 个线段及 8192 个路径字符。任意 `StreamGeometry` 或后端几何没有公开的反向路径导出 API，因此返回边界和 UnavailablePath。`BoundsClip` 单独描述矩形或圆角 `ClipToBounds`，包含各角半径，避免两种裁剪同时存在时丢失显式几何裁剪信息。

## Inspector 图像场景与交互

“3D几何”页中的嵌套页面保留几何检查，并提供自身绘制图像场景。`CompositionView` 与 `MainWindowViewModel.Composition` 通过类型化客户端采集所选子树，展示图层列表、图像状态、深度、绘制顺序、`ZIndex`、透明度及采集限制。切换目标、会话变化或断开连接时会取消过期请求，避免过期结果替换当前场景。

`CompositionScene` 管理一份不可变采集及解码后的 PNG，校验层级、尺寸、PNG 签名和实际图像尺寸，并限制解码预算。替换场景或断开连接时会释放旧场景中的所有位图。

`CompositionProjection.MapPlane` 使用完整的透视 `Matrix`，把图像平面的四角映射到四个投影角点。`CompositionSceneControl` 将真实 PNG 绘制到投影平面，而非用几何色块代替图像。它沿祖先链应用可见性和裁剪，支持矩形、椭圆、已提供的路径及圆角边界裁剪。当前视图按祖先透明度乘积绘制各层；效果、遮罩、缓存和分组透明度等限制会在采集说明中显示，不承诺与目标完整画面精确等价。

视图支持左键拖动旋转、右键或 Shift+左键拖动平移、滚轮缩放、重置相机、正视、点击命中选择，以及层间距、边框、渐变底色和深度范围设置。场景选择与图层列表同步，有应用 `ObjectId` 的节点可关联到检查树。

## 预算与生命周期

类型化请求默认最多采集 128 个节点、深度 12、单张 PNG 256 KiB、PNG 总计 2 MiB、总栅格像素 2,097,152，缩放比例为 1。硬上限为 256 个节点、深度 64、单张 PNG 1 MiB、PNG 总计 2 MiB、总像素 4,194,304，缩放比例范围 0.1–4。单个图像维度最多 16,384 像素。应用可视节点到合成器的元数据映射另设 10,000 节点上限。Inspector 的 `CompositionScene` 对齐总像素 4,194,304、PNG 总计 2 MiB、单个维度 16,384 的解码上限，并在解码前核对 PNG 头尺寸；超出时返回 `invalid_capture`。

节点数量或深度导致的遗漏会设置 `Truncated`。图像预算不足时保留节点，返回 PixelBudgetExceeded 或 ImageBudgetExceeded，不附带误导性的替代图像。空节点保留在层级中，状态为 Empty，且没有 PNG。PNG 编码写入有界流。适配器测量**完整 JSON 快照的序列化大小，包括 base64 膨胀和全部文本**，为 RPC 信封预留 4096 字节，必要时移除图像，确保低于 `RpcPeer.MaximumFrameBytes` 的 4 MiB 帧上限。几何占位图不会被作为真实采集内容返回。

每个会话同时只运行一次采集。取消请求、根节点分离、会话释放和 10 秒渲染器超时都会清理采集状态。即使渲染器停止后尚未执行排队回调，回调持有的状态也会清空。服务端对象只在采集期间被强引用，服务不缓存控件、合成节点或原生资源。所有原生目标和绘制上下文均在渲染任务内释放，PNG 流在编码后立即释放，并观察延迟到达的任务异常。绘制回调本身是同步的框架或应用代码，无法在单个回调执行中途强制中断。

## 已验证行为与实际限制

`CompositionInspectionTests` 使用 Avalonia 12.1.4 的真实 **Skia + Headless 绘制**，关闭 Headless 绘制桩，9 项测试已通过，覆盖：

- 独立蓝色父图和红色子图的真实 PNG 像素，确认没有重复后代内容或祖先背景。
- 不透明节点的嵌套缩放和平移、服务端 Z 顺序、矩形几何裁剪及 `ClipToBounds`；重组结果与 `Window.CaptureRenderedFrame` **逐像素严格一致**。
- 两层嵌套 0.5 透明度作用域，与同一正常合成器画面对比时，各通道误差不超过 **2/255**，对应每个作用域最多一个 8 位舍入单位；快照明确不声明支持精确重组。
- 负坐标越界绘制，以及没有应用对象映射的自定义合成节点的真实像素。
- 节点、深度、图像与像素预算，诊断覆盖层排除，空节点，效果限制，路径与圆角裁剪元数据，以及取消和根节点分离。
- 通过真实命名管道执行类型化客户端采集，并在采集后继续成功执行检查请求。

`CompositionSceneTests` 的 5 项测试也使用真实 Skia + Headless 绘制，已验证：

- 透视 `Matrix` 精确对应四个投影角点；真实四色 PNG 的四个颜色区域在透视变换后的像素位置保持正确。
- `CompositionSceneControl` 绘制真实图像并沿父级裁剪链裁剪，深度筛选正确，拒绝与元数据不一致的 PNG 尺寸，释放场景后清空图像。
- 通过 Headless 指针事件分别验证旋转、平移、缩放、重置、正视与点击命中选择。
- ViewModel 连接真实 Agent 后采集图像、显示采集状态并执行正视命令；断开连接后清空图层并释放场景资源。

上述结果属于自动化 Headless 渲染和交互证据，**尚未据此宣称完成真实桌面窗口的人工验收**。

`SupportsExactComposite` 采取保守判断。遍历截断、图像遗漏、效果、透明度遮罩、位图缓存、未知裁剪路径、圆角裁剪要求、非平凡变换或分数透明度都会产生明确限制。先栅格化再缩放或旋转，可能不同于直接渲染矢量命令；PNG 的 alpha 量化也可能产生微小重组误差。该能力用于诊断绘制分解，不保证所有渲染器和节点的重组结果均与截图一致。

效果与透明度遮罩仅作为元数据返回，不会分别烘焙到每张图中：它们可能作用于整个子树，逐节点重复应用会产生错误效果。采集绕过缓存资源，避免将后代内容意外包含在节点自身图中。未知几何裁剪、圆角裁剪和路径填充规则需要由使用方正确处理，无法处理时应显示限制。Agent 不使用整棵子树的 `RenderTargetBitmap` 作为回退方案；测试重组中的 `RenderTargetBitmap` 仅作为验证输出表面。

采集依赖真实合成器及后端离屏目标。缺少渲染器、目标分离、不支持的变换或适配器成员缺失都会返回明确 RPC 错误。**NativeAOT 裁剪、GPU 上下文丢失、导入 GPU 表面读回、亚克力或背景采样，以及跨后端像素一致性均未验证**，不能仅依据 Skia CPU 测试声明支持。自定义合成节点的自身绘制可能执行应用渲染回调，与正常绘制一帧时相同。

运行采集端测试：

```powershell
dotnet test tests/AvaloniaDevTool.Agent.Tests/AvaloniaDevTool.Agent.Tests.csproj --artifacts-path artifacts/composition-tests --filter FullyQualifiedName~CompositionInspectionTests
```

运行 Inspector 图像场景测试：

```powershell
dotnet test tests/AvaloniaDevTool.Inspector.Tests/AvaloniaDevTool.Inspector.Tests.csproj --artifacts-path artifacts/composition-ui-tests --filter FullyQualifiedName~CompositionSceneTests
```

该标志依据已知合成条件作特征判定，不会在每次请求中自动重组并逐像素校验。严格像素相等仅对上述具体测试输入成立，不能由一个 true 值推导任意内容或 GPU 路径已获验证。
