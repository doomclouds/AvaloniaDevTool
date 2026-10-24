# Inspector 与 MCP 远程入口

Native WebSocket 复用完整诊断协议。目标显式启动 Remote host 后，Inspector 和 MCP 使用同一 Client；XamlMcp 仍是独立只读后端。传输 API、认证、TLS 和生命周期见 [远程传输](remote-transport.md)。

## 使用方式

Debug Sample 使用 `dotnet run --project samples/AvaloniaDevTool.Sample -- --remote` 开启回环监听，底部显示实际地址并提供复制本次令牌。Inspector 输入该地址及独立的遮罩令牌。地址不携带令牌，目标退出使本次令牌失效。

也可启动 Inspector 时指定 `--endpoint ws://127.0.0.1:PORT/diagnostics --token-env VARIABLE`。Sample 对应 `--remote-token-env VARIABLE`，可用 `--remote-port PORT` 选择端口，默认 0 自动分配。变量必须在相应进程的启动环境中；不同终端不会自动共享后设变量。Inspector 不把令牌写入配置、地址或状态消息，更换地址会清除旧令牌。遮罩只描述可见界面，不保证同用户的调试／自动化工具不能读取内存值；验收输出须剔除令牌控件。

同一 Agent 的管道与 WebSocket 共用一个活动会话，竞争入口明确拒绝。先断开再切换；断开恢复诊断修改，后续连接创建新会话，关闭 Inspector 不结束目标应用。

MCP 服务先配置专用令牌环境变量，再调用 `attach-to-remote`，参数 endpoint 为完整 ws／wss 地址、tokenEnvironmentVariable 为环境变量名称。参数不传令牌值。成功后 tree／props／set-prop、资源、推送及资产导出都使用 Native 连接。

`export-asset` 读取完整分块字节并校验长度和 SHA-256，destinationPath 是 MCP 所在电脑的绝对路径，默认不覆盖已有文件；它不写远端目标电脑，也不保存截断预览。

## 证据与限制

RemoteInspectorTests 验证真实 Kestrel／Agent／Client／VM 连接、编辑恢复、重连、错误令牌修正、遮罩和参数解析。RemoteMcpTests 通过生产 stdio 使用环境凭据附加，读取／编辑真实控件并完整导出大于 1 MiB 的嵌入资产；重复导出不覆盖旧文件。

桌面已使用独立 Sample 和 Inspector 完成 Windows 回环连接，展开官方 ColorPicker、独立读取 Image.Source PNG、从绑定来源导航并返回，以及原生保存框导出真实 Inter 字体。LAN／VPN、跨主机可信 TLS、WSL2、移动及 WASM 仍没有部署证明，回环测试不能替代这些证据。
