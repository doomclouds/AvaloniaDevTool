# 开源复用核查

核查日期：2026 年 10 月 25 日。

新版独立 AvaloniaUI.DeveloperTools 的公开仓库当前只有说明、问题模板与示例，未包含安装包2.2.3桌面工具的完整实现源码。与此同时，旧版官方诊断工具和多个社区诊断项目确实公开了源码，且与本项目有大量重叠。完整同类项目的源码复用调查应早于大规模原生反编译。

本核查把“新版官方源码是否公开”和“是否已有可复用的同类实现”分别回答。仓库MIT徽章只能说明其中内容的许可，不能证明新版安装包的完整实现已放在该仓库。

## 官方旧版与社区迁移版

| 项目 | 已核实范围 | 可复用方向 | 约束 |
| --- | --- | --- | --- |
| [AvaloniaUI/Avalonia.Diagnostics](https://github.com/AvaloniaUI/Avalonia.Diagnostics) | 官方旧版进程内工具；源码和MIT许可公开，仓库已归档 | 控件树、样式与属性系统、事件链、覆盖层等源码参考 | 旧版API需要Avalonia12适配 |
| [Sylinko/ClassicDiagnostics.Avalonia](https://github.com/Sylinko/ClassicDiagnostics.Avalonia) | 基于旧工具迁移到Avalonia12；含实际src及测试 | 专用编辑器、CLR属性、嵌套对象、树订阅、Margin/Padding覆盖层、根管理 | 当前代码基线Avalonia12.0.0，偏进程内；README明确不做远程开发协议 |

ClassicDiagnostics 已固定检查commit `9199c26495ed8708204c6caa4a6d2ec62093fbd1`，未将该项目代码复制到本仓库：

- [MIT许可](https://github.com/Sylinko/ClassicDiagnostics.Avalonia/blob/9199c26495ed8708204c6caa4a6d2ec62093fbd1/LICENSE)：包含AvaloniaUI与Sylinko的版权及许可声明；复用代码时保留对应声明。
- [树提供者](https://github.com/Sylinko/ClassicDiagnostics.Avalonia/blob/9199c26495ed8708204c6caa4a6d2ec62093fbd1/src/Elements/Trees/TreeProviders.cs)：订阅LogicalChildren／VisualChildren变化，处理PresentationRoot。
- [属性编辑器选择](https://github.com/Sylinko/ClassicDiagnostics.Avalonia/blob/9199c26495ed8708204c6caa4a6d2ec62093fbd1/src/Elements/Properties/PropertyEditorFactory.cs)：布尔、数值、枚举／flags、颜色、画刷、图像、Geometry和复杂对象导航。
- [高亮覆盖层](https://github.com/Sylinko/ClassicDiagnostics.Avalonia/blob/9199c26495ed8708204c6caa4a6d2ec62093fbd1/src/Views/Controls/ControlHighlightAdorner.cs)：可释放高亮及Margin／Padding可视化。
- [项目依赖](https://github.com/Sylinko/ClassicDiagnostics.Avalonia/blob/9199c26495ed8708204c6caa4a6d2ec62093fbd1/src/ClassicDiagnostics.Avalonia.csproj)：ColorPicker、DataGrid、Simple主题、IgnoresAccessChecksToGenerator。它包含针对Avalonia内部API的访问，直接整体引入会改变当前轻量Agent的依赖边界。

本轮阅读了实现和测试文件，尚未运行ClassicDiagnostics完整测试套件，因此不把“有测试源码”记为“该版本已在本机验证兼容”。

## 同类Agent、协议和MCP实现

进一步核查的项目：

- [wieslawsoltes/CDP](https://github.com/wieslawsoltes/CDP)：真实CDP诊断域、独立Inspector与MCP。需要区分实装域和空返回分支；例如部分DOM断点方法没有实现。
- [AvaloniaCommunity/Avalonia.Markup.Declarative](https://github.com/AvaloniaCommunity/Avalonia.Markup.Declarative)：真实进程内AgentTools、loopback MCP、截图差分、布局和属性诊断，部分逻辑与Declarative核心绑定。
- [trrahul/XamlMcp](https://github.com/trrahul/XamlMcp)：独立Server、Agent.Hosting、Avalonia适配、命名管道、认证与兼容层，源码中的SDK与端到端测试值得优先研究。

具体commit、许可、模块、真实测试和可复用边界见 [Agent工具源码审计](open-source-agent-tools-audit.md)。

## 后续工作顺序

1. 按功能目录对照开源实现，逐项注明源码位置、许可、框架版本、已有测试和实际缺口。
2. 对可覆盖能力优先采用成熟依赖、可独立移植模块或其测试设计；保留当前会话／DTO边界，先验证适配成本。
3. 为每次引入保留来源与许可，不把第三方实现当作本项目原创，也不按README功能名推断空分支已经可用。
4. 将REA集中到公开源码无法回答的新版差异、协议行为和原生编排。原生全量自动分析已经暴露很高成本，后续采用明确问题驱动的小范围取证。
5. 已完成的自研模块继续做整合回归；在复用对照完成前不因重复功能名继续盲目扩写。

## 原版源码公开范围

- [新版DeveloperTools公开仓库](https://github.com/AvaloniaUI/AvaloniaUI.DeveloperTools)：当前目录为说明、示例和历史问题入口。
- [Accelerate／AvaloniaPro](https://github.com/AvaloniaUI/AvaloniaPro)：Issue tracker与讨论，不是完整产品源码仓库。
- [官方FAQ](https://docs.avaloniaui.net/tools/faq)：明确旧版DevTools等工具仍为MIT开源并允许fork。

以上结论限定于本轮实际读取的仓库和commit，不作“全网不存在其他实现”的断言。

