# MCP 与执行电脑选择

用户只配置一个 stdio MCP。网关在本机运行，默认使用 LocalExecutor；`select-machine` 指定 IP 后使用 HttpExecutor 调用对应电脑上的执行 Console。MCP 目录在两种模式下保持一致。

## 启动与连接

```cmd
AvaloniaDevTool.exe mcp
AvaloniaDevTool.exe serve
```

第一条供 MCP 客户端配置启动，stdout 只承载协议，日志写 stderr。第二条在被测电脑的交互桌面启动 HTTP 执行器；默认端口 7443，路径 `/mcp`，默认监听 IPv4 接口。可用 `--listen IP` 指定绑定地址；端口冲突直接报错，不自动改用其他端口。

```toml
[mcp_servers.avalonia]
command = 'C:\Users\a\.local\bin\AvaloniaDevTool.exe'
args = ["mcp"]
startup_timeout_sec = 120
```

以上是安装后的稳定命令路径。源码消费可运行已构建的 `src/Hosts/AvaloniaDevTool.Cli/bin/Debug/net10.0/AvaloniaDevTool.Cli.dll`，前面加 `dotnet`；普通构建分发仍需完整运行资产目录。只有 Packaging 产物提供单 exe 分发。

## 选择机器、应用和控件

```text
select-machine {}                     本机 LocalExecutor
select-machine {"ip":"192.168.1.50"}  HTTP Console
select-machine {"ip":"127.0.0.1"}     本机 Console，仍走完整 HTTP
attach-to-app {}                      发现当前执行电脑的端点
attach-to-app {"id":<返回的id>}        附加选定应用
```

IP 只能是地址，不是 URL 或主机名。用户工具不暴露端口参数；维护和隔离测试可在网关启动环境中设置 `AVDT_EXECUTOR_PORT`，默认仍为 7443，Console 端须配置相同端口。

应用必须引用 Agent SDK 并在框架启动后 Attach。应用 id 是发现结果，不是 PID；不要猜测或复用其他机器返回的 id。工具不会自动选第一个应用。

## 切换和失效

选择候选机器时只初始化 MCP、核对执行端身份和工具契约，不附加应用。有在途操作或未终结的原生动作时返回 `machine_busy`，不持有堵住 native 弹窗处理的全局锁。成功切换后结束旧执行会话、退休旧句柄并发布新会话，需要重新发现和附加。

机器或后端连接切换使全部旧句柄失效。换应用、成功预览重载、native worker 更换分别使相应层级的引用失效。后端数值节点是不可解释的 opaque 值，网关只查映射表，不解析后端位编码。

远端失联不会切回本机。请求可能已执行时返回明确的不确定结果，不自动重发写操作。准备候选失败时明确保留原选择；旧会话清理失败则保持不可用，需要显式重新选择。

远端 Console 不提供 `select-machine`。它只运行本机执行服务，不允许网关级联。

## 图片、资产与路径

截图以 MCP image 内容及实际尺寸、坐标空间、来源和限制返回。HttpExecutor 保留图像内容与元数据，网关只按已知工具契约转换其中的句柄；不会把 PNG 放进文本字段，不重新抓图或重压缩。单独一个远端文件路径不是图片返回。

`attach-to-file`、文件弹窗和文件路径都属于当前执行电脑，B 的路径不会自动映射到 A。原始资产使用已有完整读取/导出协议；调用方在自身电脑保存内容时应读取返回字节，不能把 A 的路径当 B 的文件。

## 并发和脚本

managed 工具保留共享调用 gate；native/file-dialog 使用同一 native 会话的独立通道。managed 点击等待模态时，原生工具仍能回答真实文件弹窗。不要在网关把全部调用串行锁住。

脚本可作为标准 HTTP MCP 客户端调用 Console，负责初始化、会话、工具调用与关闭。脚本和 Agent 各自持有控制会话并遵守单控制者策略；交接时关闭旧控制会话并重新取得对象，不能跨会话传递旧 nodeId。

脚本编排与业务判断留在调用方；服务不新增 run-scenario、业务断言或脚本执行接口。

## 0.1.0 当前合同

版本固定为0.1.0，尚无需要维护的历史发布。只保留mcp与serve两个角色；没有profile或transport选择参数，也没有旧工具聚合类与SDK类型转发。原诊断入口的独有能力已纳入统一执行目录，同名功能选择唯一参数与结果合同。

外部目录为统一执行目录加select-machine；Console执行目录不含机器选择。准确工具名和参数以当前构建tools/list为准，工具数不代表完成率。
