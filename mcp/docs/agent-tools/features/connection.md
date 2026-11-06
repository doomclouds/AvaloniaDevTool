# 连接、发现与公开契约

默认 `official` profile 以原版2.2.3实际采样的15项功能性输入schema声明工具；官网另列的detach按自研无参数工具提供，本地固定共16项。输入声明、注解和execution以原始stdio测试核对；描述文字由本项目撰写，不复用官方说明。原版输出格式没有公开schema，也尚无成功调用样本，本地DTO不作为线格式等价证明。

## 目标发现与附加

`attach-to-app {}` / `{ "id": 0 }` 列举当前可发现的本项目Agent端点。Windows枚举本机命名管道；Unix枚举当前临时目录中的CoreFxPipe。默认发现本项目协议前缀，显式`--pipe`的其他名称也只在实际存在时列出，不能凭配置伪造运行目标。

应用ID是当前MCP服务维护的单调标识，不是PID。发现不会消耗Agent的单连接会话；未附加前名称可能只是管道名，成功附加后更新为目标提供的名称/PID。仅凭管道名不能证明它已完成桥握手。最多列举256项并显式标记截断；使用返回id才能附加。

`--pipe`提供初始连接目标，实际第一次需要Client的调用会连接。没有Agent的应用不会出现在目录中；仅引用包而未调用`DiagnosticsAgent.Attach`也不会创建端点。发现结果带接入指导，本地连接/握手超过5秒返回`connection_timeout`；取消仍响应调用方token，不把用户取消伪装成超时。版本不符保留`unsupported_version`和实际版本号。

普通桌面应用由SDK跟踪Avalonia lifetime；自定义/Headless宿主可显式提供TopLevelProvider。SDK不再包含Inspector启动快捷键，MCP连接不依赖F12。

## 节点与会话

公开节点ID编码MCP连接代际及底层对象ID，限制在52bit范围，0保留为根/Application入口。三种树、搜索及引用属性共享同一公开身份。切换应用或成功替换XAML内容会使旧代际失效，旧节点不能因新目标碰巧使用相同底层编号而写错对象。

官方工具调用在单个会话门内串行。节点调用在前后读取目标内容代际；期间内容改变返回stale_session，调用方需重新tree/search。框架事件引发的应用副作用不会自动回滚，该错误只拒绝把过期结果当作当前快照。

detach断开Client并释放自有previewer；重复detach可用。外部附加应用不获得进程所有权，不能被该操作关闭。原版目录时机、参数和清理规则仍未知。

## 验证

`OfficialConnectionTests`的5个真实stdio用例进入2026-10-25统一批次：完整schema及缺省注解/执行元信息、双独立app切换与旧节点拒绝、错误桥版本、真实窗口缺启动调用及启用后恢复、无桥包独立应用保持运行且明确接入错误。正常预览仍不执行fixture的Main；无桥包样本只在显式`--without-diagnostics`测试模式运行Main。

协议和MCP错误文字是本项目诊断合同，不声称复现原版未采样文字。成功目录不等于附加成功，声明execution字段也不证明异步tasks操作已经实现。
