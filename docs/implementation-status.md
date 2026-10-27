# 0.1.0 实现状态

0.1.0按工具对外提供的十个能力域组织：目标会话、结构对象检查、属性样式、资源资产、框架交互、原生自动化、文件选择、图像视觉、运行诊断、XAML预览。产品提供操作和原始事实，调用方负责业务编排和判断。

## 当前交付形态

| 范围 | 实现 |
| --- | --- |
| 唯一用户MCP | mcp角色只运行stdio网关，默认本机LocalExecutor |
| 选择执行电脑 | select-machine仅接收IP；指定IP走HttpExecutor，包括127.0.0.1；默认端口7443 |
| 远端Console | serve角色由ASP.NET Core承载HTTP MCP，直接执行本机能力，不级联选择机器 |
| 统一能力合同 | 执行88项、网关增加机器选择共89项；原扩展中的非重复能力全部纳入 |
| 无旧兼容层 | 删除profile、旧transport命令、三类聚合Tools、Client/Native空SDK、TypeForwarders及平铺客户端API；保留真实Agent SDK |
| 实际分层 | 29项目、15生产；Contracts/Domain/Application/Infrastructure/SDK/Hosts/Extensions/Packaging，引用无环 |
| 能力对象 | Agent、客户端、工具、原生后端的状态与资源由独立对象拥有，生产手写partial已移除 |
| 会话隔离 | 网关opaque句柄、分层失效、客户端revision与peer专属通知，拒绝旧响应/旧引用 |
| 图片 | PNG与实际尺寸/区域/坐标/限制一起返回，HTTP与stdio保持相同媒体合同 |
| 打包 | 独立Packaging工程嵌入完整自包含Console/Previewer/Worker材料，交付目录仅一个EXE |
| 安装 | 用户.local版本目录、.local/bin稳定入口、用户PATH与HOME变量、旧版本保留、校验与失败回滚 |

## 有意保留的运行边界

- 目标应用需要引用当前Agent SDK并在框架启动后Attach；升级本轮未发布API后应重新构建应用。
- native只在执行电脑的交互桌面操作所选进程及owned窗口，平台实现仍限Windows。
- Previewer仅在attach-to-file时按需启动，普通应用附加不需要预览器；预览工程评估/构建仍需SDK。
- 单exe是分发形态，运行时允许校验解包、缓存目录及隔离进程，不等于单进程或零落盘。
- 0.1.0明确使用MCP 2025-11-25标准合同。固定该版本避免当前SDK默认discovery探测超时后在同连接初始化的竞态；不宣称支持硬固定2026的客户端。
- 远端失联不自动回本机，不重放不确定的修改操作；取消不等于所有执行和清理已经完成。

## 验证记录

完整命令、初始失败和修复后的精准回归、实物EXE验证均记录在[测试说明](testing.md)。格式检查、构建、Headless、真实进程、像素、OS输入、打包及实际网络各自标明证据层，不把专项总数相加为完成率。

已完成的可见演示为HTTP Console附加真实AutomationSample，替换文本、点击三次、提交并读回状态，以及返回真实PNG。演示材料位于artifacts/visible-console-demo；它不代替全部回归。

[架构](architecture.md) · [项目引用](architecture/project-inventory.md) · [迁移记录](architecture/refactoring-blueprint.md) · [MCP](mcp.md) · [打包安装](packaging-and-installation.md)

## 历史资料

2026-10-26及更早的工具目录、兼容研究与测试批次保留在对应研究和verification文件中，仅代表当时源码；它们不是0.1.0的支持声明或待完成清单。独立物理双机LAN与干净环境发布验收应以实际执行结果确认，不借用回环或已有运行时电脑的测试结论。
