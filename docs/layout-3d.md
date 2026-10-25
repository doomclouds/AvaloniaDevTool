# 几何层级与自研 3D 视图

> 范围说明：本文保留 Agent/Client 扩展服务的协议、预算、恢复和历史验证资料。文中 Inspector 界面、操作和旧阶段结果仅适用于完整桌面基线 `main/fa1c623`，不代表当前分支 GUI 或统一回归；当前默认 official 的 16 项合同以 [官方能力基准](agent-tools/research/official-capabilities.md) 和对应功能文档为准。扩展入口须显式使用 `--profile extended`，并以该目录的 `tools/list` 确认参数。

图层页现有“视觉几何”和“合成图像”两个独立入口。本文说明 layout.snapshot 的 VisualGeometry 分支；真实合成器自身绘制 PNG、透视图像、裁剪、相机和资源释放见 [合成层采集](composition-capture.md)。

几何采集通过 `layout.snapshot` 返回真实视觉树的边界、父子关系和二维仿射变换，供检查器把每层矩形投影到自研的层级视图。返回的 `CaptureKind` 固定为 `VisualGeometry`。该服务不读取像素、不调用控件绘制，不表示已还原原版 DeveloperTools 的 Compositor 位图或 3D 展示算法。

## 坐标和数据

根对象必须是已附加到视觉树且具有有限正 Bounds 的 Control。宽高采用控件的实际排列尺寸，单位为 Avalonia 设备无关像素；根层矩阵为单位矩阵，根自身的父布局偏移不计入根局部坐标系。

每个后代 Visual 通过 `TransformToVisual(root)` 取得真实矩阵，包含祖先布局偏移、RenderTransform 与 RenderTransformOrigin 的影响。前端应变换每个局部顶点，而不是把旋转后的轴对齐包围框当成真实控件形状：

```text
rootX = localX * M11 + localY * M21 + OffsetX
rootY = localX * M12 + localY * M22 + OffsetY
```

本版只传递有限的二维仿射矩阵。无法转换到根坐标系、含非有限值或含透视分量的变换返回明确 RPC 错误，不能静默替换为单位矩阵。Depth 是相对根的树层级，供自研前端独立决定 z 间距；它不是 Compositor 的实际 z 坐标。

ObjectId 复用会话弱对象注册表，ParentId 指向同一快照内的父层；同一存活对象的多次快照保持相同 ID。服务不会为几何层保留控件强引用。诊断 HighlightAdorner 整个子树均排除，避免把高亮覆盖层当成业务视觉层。

## 可见性与裁剪

IsVisible 合并根之外的祖先与子树祖先的 IsVisible。它表示视觉树可见性条件，不代表像素覆盖、透明度或遮挡测试。

IsClipped 表示该层或任一祖先声明了 Clip 或 ClipToBounds。该字段没有计算完整裁剪 polygon，也不意味着整个控件被裁掉。几何视图应据此提示存在裁剪，而不能假装获得精确可见像素区域。

## 边界和错误

请求 MaxNodes 默认 128，范围 1 到 256；MaxDepth 默认 12，范围 0 到 64。根算作一个节点且深度为 0。遍历按视觉子节点顺序深度优先，逐项枚举而不预先物化整个兄弟集合；观察到确实存在未返回的子层时才设置 Truncated。Visited 为实际返回的非诊断层数量。

根之外的祖先扫描另有 1024 层硬上限，以保证祖先状态检查也有界。超过时返回 layout_limit；通常应用窗口的层级远小于该边界。

| 错误代码 | 含义 |
| --- | --- |
| invalid_request | 节点或深度参数越界 |
| object_expired | 会话对象句柄已失效或未知 |
| unsupported_object | 根不是业务 Control |
| detached_object | 根已脱离视觉树 |
| invalid_layout | 根宽高不为有限正值，或子层 Bounds 无效 |
| unsupported_transform | 无法提供有限仿射矩阵 |
| layout_limit | 根外祖先链超过硬上限 |

采集必须在 UI 线程执行。AgentSession 应通过现有 UI 调度路径分派，不为本服务额外创建后台控件访问。

## 自动化验证

LayoutInspectionTests 使用真实 Avalonia Headless 控件与布局，覆盖父子关系、稳定 ID、嵌套布局偏移、缩放和旋转矩阵、祖先隐藏与裁剪声明、深度与数量边界、精确边界不误报截断、覆盖层排除、失效和分离对象，以及后台线程访问拒绝。

