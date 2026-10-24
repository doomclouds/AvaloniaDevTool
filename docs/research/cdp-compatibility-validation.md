# CDP 在 Avalonia 12.1.4 的兼容性验证

固定提交的CDP后端在.NET 10／Avalonia 12.1.4上可以编译，并已通过真实HTTP、WebSocket和Skia Headless目标验证DOM、CSS读取、高亮、真实PNG截图和输入。原版存在一处输入监听退订缺口；使用其公开清理注册接口在适配宿主补救后，选定的8项测试全部通过，无需修改上游库C#。

这足以支持继续做CDP后端适配验证；不等于整个Inspector、多平台产品或全部CDP命令已经可直接替换本项目。

## 固定对象与改动范围

| 项目 | 本次对象 |
| --- | --- |
| 上游 | [wieslawsoltes/CDP](https://github.com/wieslawsoltes/CDP) |
| 精确commit | `349d829e8c50f0f3c6f7d5560dfbfd6a9f5f9f84` |
| 验证日期 | 2026年10月25日 |
| 本机SDK／运行时 | .NET SDK 10.0.400／Runtime 10.0.11，Windows |
| 原版配置 | net10.0，Avalonia 12.0.5 |
| 验证配置 | net10.0，Avalonia 12.1.4，Skia绘制的Headless窗口 |
| 后端项目 | `src/Avalonia.Diagnostics.Cdp/Avalonia.Diagnostics.Cdp.csproj`，其PackageId为 `Chrome.DevTools.Avalonia` |
| 许可 | 固定提交的LICENSE为MIT，版权标注2026 Wiesław Šoltés |

克隆的HEAD在开始时已核对，工作树干净。未构建整个多平台Inspector解决方案，也没有把第三方源码引入本项目。所有PoC和试验补丁均在以下临时克隆内：

```text
C:/Users/10062/AppData/Local/Temp/avdt-open-source-audit-e91f75f3c56240ac9777241b861bbaa6/CDP
```

原版central package文件硬编码12.0.5，单独传 `-p:AvaloniaVersion=12.1.4` 不会改变它。因此临时将这组Avalonia包的版本写为 `$(AvaloniaVersion)`，默认值仍为12.0.5。最终tracked diff只有这份版本参数化；库C#已经恢复为固定提交原样。禁用 `GeneratePackageOnBuild`，没有打包发布、注册工具或修改用户配置。

## 实际执行与结果

在上述CDP克隆目录执行：

```powershell
git rev-parse HEAD

# 原版基线，未修改central package时执行。
dotnet build src/Avalonia.Diagnostics.Cdp/Avalonia.Diagnostics.Cdp.csproj -p:GeneratePackageOnBuild=false --verbosity minimal

# 以下命令需要先应用临时版本参数化。
dotnet build src/Avalonia.Diagnostics.Cdp/Avalonia.Diagnostics.Cdp.csproj -p:AvaloniaVersion=12.1.4 -p:GeneratePackageOnBuild=false

# PoC首次不加入适配清理。
dotnet test validation/CdpCompatibilityProbe/CdpCompatibilityProbe.csproj -p:AvaloniaVersion=12.1.4 -p:GeneratePackageOnBuild=false --logger "trx;LogFileName=baseline-12.1.4.trx" --results-directory validation/evidence

# 最终验证：上游C#原样，宿主启用公开清理适配。
dotnet test validation/CdpCompatibilityProbe/CdpCompatibilityProbe.csproj -p:AvaloniaVersion=12.1.4 -p:GeneratePackageOnBuild=false -p:UseCleanupAdapter=true --logger "trx;LogFileName=adapter-12.1.4.trx" --results-directory validation/evidence

git diff --exit-code -- src
```

| 阶段 | 结果 | 能证明什么 |
| --- | --- | --- |
| 原版12.0.5后端构建 | 831警告，0错误，约25.75秒 | 固定提交的必要后端图能在本机SDK构建 |
| 仅改包版本到12.1.4 | 836警告，0错误，约20.31秒 | 编译无需Avalonia API适配C#补丁 |
| 12.1.4原版C#／无清理适配 | 7通过、1失败，共8项 | 基本链路可运行，Input.enable退订失败 |
| 临时库清理补丁 | 8通过、0失败 | 缺口可由很小的清理补丁修复 |
| 撤销库补丁、使用公开适配钩子 | 8通过、0失败 | 最终可不维护这份上游C#分叉 |

警告主要包含可空引用、过时API及裁剪／AOT分析。后两阶段属于增量构建，警告数不应与整图首次构建直接比较；通过JIT测试也不证明Native AOT或裁剪安全。

## 选定的测试与真实效果

PoC项目只引用后端及其必要图。它原样链接上游 [DomTreeTests.cs](https://github.com/wieslawsoltes/CDP/blob/349d829e8c50f0f3c6f7d5560dfbfd6a9f5f9f84/tests/Avalonia.Diagnostics.Cdp.Tests/DomTreeTests.cs) 的5项测试，覆盖树、盒模型、属性别名和运行时元素助手。这5项主要直接调用处理器，不能冒充真实网络端到端测试。

另加3项真实HTTP／WebSocket测试，每项在Headless应用中建立实际Window、Button和TextBox，并监听临时loopback端口。没有使用MockCdpService：

| 接口／链路 | 验证断言 |
| --- | --- |
| HTTP `/json/version`、`/json/list` | 返回版本及实际注册目标，随后连接对应 `webSocketDebuggerUrl` 路径 |
| `DOM.getDocument` | 真正返回document与窗口树 |
| `DOM.querySelector` | 以名称找到实际Button、TextBox节点 |
| `DOM.getBoxModel` | 返回的content quad用于计算实际输入坐标 |
| `CSS.getComputedStyleForNode` | 读到真实Button的 `width=180px` |
| `Overlay.highlightNode`、`hideHighlight` | 真实视觉树内出现／移除HighlightAdorner，而非仅响应空JSON |
| `Page.captureScreenshot` | PNG解码为600×420，角落像素为目标背景 `#ff112233`，3313字节 |
| `Input.dispatchMouseEvent` | 实际Button.Click计数变为1，并能让TextBox获得焦点 |
| `Input.insertText`、`dispatchKeyEvent` | TextBox先变为 `CDP text`，Backspace后为 `CDP tex` |
| 正常WebSocket断连 | Core会话数归零，NodeMap中原节点消失，高亮移除 |
| `Input.enable`后断连 | 原版状态字典残留；公开适配后由0→1→0 |

截图尺寸和已知背景像素是必要断言。上游 [PageDomain](https://github.com/wieslawsoltes/CDP/blob/349d829e8c50f0f3c6f7d5560dfbfd6a9f5f9f84/src/Avalonia.Diagnostics.Cdp/Domains/PageDomain.cs) 在捕获异常后会返回有效的mock PNG，只有“返回了base64／PNG可解码”不足以证明真实截图。

同样，不能因为某个CDP命令返回 `{}` 就宣布其功能完成。例如固定提交的Overlay部分命令（包括highlightRect和若干调试覆盖开关）只返回空对象；本次只把确实观察到效果的高亮动作记为通过。

## 输入退订缺口与无分叉补救

原版 `Input.enable` 将会话保存在 [InputDomain](https://github.com/wieslawsoltes/CDP/blob/349d829e8c50f0f3c6f7d5560dfbfd6a9f5f9f84/src/Avalonia.Diagnostics.Cdp/Domains/InputDomain.cs) 的静态状态字典。正常断连后，Core会话数已经归零，但该字典仍为1。PoC通过只读反射观察私有状态来确认这一点；这不是仅从源码猜测。

临时补丁曾给InputDomain提供清理入口，并由Avalonia会话OnCleanup调用，测试全过。随后撤销这两份C#改动，改用现成的 [CdpSessionCleanupRegistry](https://github.com/wieslawsoltes/CDP/blob/349d829e8c50f0f3c6f7d5560dfbfd6a9f5f9f84/src/Chrome.DevTools.Protocol/Composition/CdpCompositionContracts.cs)：

```csharp
CdpSessionCleanupRegistry.Register(static session =>
{
    if (session is Avalonia.Diagnostics.Cdp.CdpSession avaloniaSession)
        Dispatcher.UIThread.Post(() =>
            _ = InputDomain.HandleAsync(avaloniaSession, "disable", new JsonObject()));
});
```

适配器在宿主初始化时注册一次，UI线程负责移除监听。该回调是进程级注册，不能每次连接都创建新的捕获委托。最终在上游库C#无diff的条件下，清理测试再次通过。释放为UI调度后的完成，不声称Close握手返回的瞬间全部事件都已移除。

原版失败、临时库补丁和最终公开适配均有独立TRX与请求响应记录。测试结束后还显式清理了本次创建的订阅／窗口和HTTP监听；没有停止其他dotnet进程或修改系统URL注册。

## 依赖和资源成本

仅后端project.assets.json就包含**13个传递项目引用、50个NuGet包**，不含后端自身。其必要图包括：

- Chrome.DevTools.Protocol及Extensions。
- Extensions继续引用Automation、Jint、Profiling和Skia模块。
- Xaml.Compiler及6个测试管理集成项目（含Integration.Core）。
- Roslyn脚本／Workspaces／Features、Jint 4.10.1、SQLite 10.0.9、SkiaSharp 3.119.4，以及Profiler API等依赖。

这不是只引入两个小DLL。PoC加测试后为14个项目引用、84个NuGet包，Debug目录398个文件、665,645,012字节（约634.8 MiB）；去掉PDB后346,589,140字节，根目录managed DLL合计48,723,864字节。体积大量来自多RID Skia／HarfBuzz native文件及PDB。

上述是**未做RID裁剪的Debug测试目录**，不能当成NuGet包体积、生产发布体积或进程内存。没有为了测体积执行publish或裁剪。若采用此后端，应优先验证可选域拆分和依赖裁剪成本，再决定把它作为默认Agent还是可选后端。

## 与现有Inspector的适配建议

1. **先新增后端适配层，保留界面。** 把当前Client服务抽象为可替换后端，让CDP会话管理目标发现、请求与事件；不要让Avalonia界面直接依赖上游所有Inspector项目。
2. **隔离对象身份。** CDP nodeId在对应WebSocket会话内使用；映射为本项目节点ID时仍应附会话身份。断连重建树与属性缓存。
3. **读取和解释先接通。** 本次已证实的DOM、盒模型、CSS读取、高亮、截图和输入可以成为第一批适配能力。CSS视图不等同于本项目完整Avalonia属性描述，应在能力模型中表示差异。
4. **属性编辑保留明确语义。** 上游CSS.setStyleTexts／setStyleSheetText等路径会调用LosslessXamlMutationEngine；本次未调用这些命令，更未允许修改源码。它们不能直接映射成本项目“可撤销、保留绑定的诊断覆盖”。源码修改必须作为独立、显式操作设计。
5. **补齐边界。** 使用公开清理注册钩子解决已复现退订；对收到的图片检查真实尺寸／错误状态；为客户端消息、队列和资源保留预算。CDP的loopback HTTP连接也不等同于当前CurrentUserOnly命名管道的访问边界。
6. **继续按功能验收。** 不将未测试的Runtime脚本、源码编辑、Profiler、MCP CLI、BiDi、其他框架或跨平台能力加入已支持清单。

上游 [McpCliTests.cs](https://github.com/wieslawsoltes/CDP/blob/349d829e8c50f0f3c6f7d5560dfbfd6a9f5f9f84/tests/CDP.Inspector.CLI.Tests/McpCliTests.cs) 明确使用MockCdpService。它可证明CLI命令映射，但本次没有把这些mock测试当作真实目标兼容性证据。

## 证据位置

所有运行证据在临时克隆的 `validation/evidence/`：

| 文件 | 内容 |
| --- | --- |
| `build-12.1.4.log` | 包版本调整后的整图构建及836条警告 |
| `baseline-12.1.4.trx` | 原版C# 7／8通过，输入监听清理失败 |
| `patched-12.1.4.trx` | 临时库补丁8／8通过 |
| `adapter-12.1.4.trx` | 无上游C#改动、公开适配8／8通过 |
| `baseline-*.json`、`patched-*.json`、`adapter-*.json` | 各阶段实际请求、响应和效果计数 |
| `actual-600x420.png` | 实际目标PNG |
| `temporary-compatibility.patch` | 版本参数化及曾验证、已撤销的库清理补丁 |
| `summary.json` | 提交、环境、结果及边界的结构化摘要 |

PoC源文件位于 `validation/CdpCompatibilityProbe/`。它是本次验证夹具，没有纳入产品项目，临时目录清理后不保证仍可复现原路径。

本次未执行：可见Chrome DevTools前端、原生OS输入、多平台、AOT／裁剪、源码修改／发布、全部CDP域、突发连接中断及长期内存压力测试。

