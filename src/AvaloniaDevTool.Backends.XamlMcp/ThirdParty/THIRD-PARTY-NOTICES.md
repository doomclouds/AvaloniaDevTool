# Third party components for the XamlMcp backend

This adapter is an independent implementation over public APIs. No XamlMcp implementation source was copied into this project.

- XamlMcp.Protocol and XamlMcp.Server 1.0.0-preview.3, copyright (c) 2026 Rahul TR. Apache-2.0. Source: https://github.com/trrahul/XamlMcp at commit 1718f946cb275a71e021de58f4caa552e04feaa8. The original license is in XamlMcp-LICENSE.txt.
- The independent target sample references XamlMcp.Avalonia 1.0.0-preview.3 and its same-version Agent.Hosting/Protocol packages under the same attribution and license.
- The adapter uses ModelContextProtocol/ModelContextProtocol.Core 2.3.0. Copyright Model Context Protocol a Series of LF Projects, LLC. Package metadata declares Apache-2.0. Its pinned source LICENSE, including the licensing-transition and retained MIT-contribution notices, is in McpSdk-2.3.0-LICENSE.txt (source commit 9a327829ef066c44a0d737c58bf3c7617d7c701c).
- The published upstream Server includes MCP SDK 1.4.1 in its separate runtime directory. Its package metadata also declares Apache-2.0; the original combined LICENSE is in McpSdk-1.4.1-LICENSE.txt (source commit 2b7fd35fbe58dfb9f00eae8b3393e1a7361b5e01).

The public Server tool payload is restored through NuGet and copied unchanged into Upstream/XamlMcpServer, together with these notices. Its transitive .NET and optional driver dependencies retain their own licenses and package attributions. No optional OS driver is enabled by this read-only adapter.

The license texts are reproduced for attribution, not as a claim that all project code has been relicensed. No user MCP configuration or global tool registration is created.

