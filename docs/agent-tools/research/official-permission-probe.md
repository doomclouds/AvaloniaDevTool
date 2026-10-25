# 原版 MCP 有效授权检查

2026-10-25 08:44:16（Asia/Shanghai）正常调用原版2.2.3的 `attach-to-app {}`，服务返回 `result.isError=true`，内容如下：

> Your current subscription tier does not include access to this tool. Please upgrade your subscription to access all features.

这证明**本次官方MCP进程的有效授权不包含该工具**，没有成功发现或附加。它不证明用户当前登录的是哪个账户，也不能据此确定账号的订阅名称、GitHub登录是否完成，或断言换回原有账号后仍会失败。不要把初始化/目录成功、GUI进程存活、登录窗口消失或浏览器登录成功写成MCP授权成功。

## 过程与可追溯证据

- 使用已有安装的原版exe，SHA-256与 [最初schema采样](schema-parity.md) 相同；initialize返回 `2.2.3+92df9d92acf0bbe8b2ffa96370c1bf848df81d27`、协议2025-06-18。
- 独立MCP子进程PID16452，初始化及初始目录15项成功；只额外发送一次 `tools/call`，工具为attach-to-app、参数为空。该失败属于真实原版运行证据，但不是功能行为对照通过。
- 准备的 [差分目标](differential-fixture.md) PID37892当时存活、只开被动发现，13项样本检查通过；没有成功被官方工具附加。没有调用其他检查/编辑/输入工具。
- MCP关闭stdin后正常退出0；目标在核对PID与exe路径后回收。原有GUI PID21188保留，未操作它的账户/认证数据。
- 用户说明原来通过GitHub登录。浏览器已正常进入“GitHub → Avalonia Customer Portal”登录页，尚需用户本人完成账号验证；没有读取密码管理器、认证缓存或许可证密钥，没有改订阅。

结构化索引：[official-permission-probe.json](../verification/official-permission-probe.json)。脱敏协议记录保存在本机 `artifacts/agent-tools-2026-10-25/official-permission-probe/protocol.sanitized.jsonl`，SHA-256为 `5ef095dbba396668f0201b378452f3c86be24d3b638fb1e028e30b14ee0afdc2`。记录屏蔽SDK日志正文、敏感键和OAuth查询参数；不是未经处理的认证日志。

## 对完成度的影响

没有新增任何原版核心行为的已验证条目。C03、DET01、ANC01、PSEUDO03仍保留未知，当前48/52只是本地公开核心验收，所有原版成功输出和未公开边界仍待对照。

下一步是在原有GitHub账户登录后重新启动官方MCP进程并重复正常许可检查；仅当附加成功并读到自有目标身份/样本内容，才能进入三树、父链、伪类、属性与detach差分。若有效订阅确实不含MCP，则保留未知，不增加自研工具的许可限制，不绕过原版权限。

08:49:03追加复核：浏览器仍在 `github.com/login`，Sign in按钮可见；新建MCP子进程PID44484再次正常调用attach-to-app，仍返回同一订阅权限错误，随后关闭stdin正常退出0。没有重复操作认证表单。复核协议与哈希见结构化索引的rechecks；对照等待用户完成原账号验证和有效MCP授权。

08:50:24用户确认GitHub登录完成，浏览器已回到 `portal.avaloniaui.net/`。重新启动MCP子进程PID54180，再次调用attach-to-app仍返回相同权限拒绝。驱动关闭stdin后等待未退出，按所有权终止该子进程（退出1）；这不是正常退出或应用崩溃的证明，原有GUI未被停止。此轮协议、哈希与清理在索引的afterPortalLogin字段。门户登录已验证，MCP有效授权仍未取得，不能把两者合并为同一成功条件。

用户随后说明桌面界面的社区订阅已经可用。2026-10-25重新核对 [桌面DevTools版本说明](https://avaloniaui.net/devtools)：Community包含常用桌面树、属性、布局及样式/伪类检查；[MCP具体文档](https://docs.avaloniaui.net/tools/developer-tools/mcp#setting-your-license-key) 则明确排除Free/Community，要求Plus许可。该区分与本次MCP拒绝一致。自研MCP不依赖官方MCP授权，因此不把此限制复制为自研工具门槛；受阻的是原版成功调用的对照证据。
