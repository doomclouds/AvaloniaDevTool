# Binding diagnostics attribution

The diagnostic vocabulary and classification approach in BindingDetailsReader.cs are adapted from XamlMcp, copyright (c) 2026 Rahul TR, Apache License 2.0.

Source: https://github.com/trrahul/XamlMcp at commit 1718f946cb275a71e021de58f4caa552e04feaa8, src/XamlMcp.Avalonia/Compat/BindingDiagnosticsCompat.cs and src/XamlMcp.Avalonia/Values/BindingDiagnosticsService.cs. The full license is in XamlMcp-LICENSE.txt. The audited source tree contains no upstream NOTICE file.

Changes made in this project: individual field availability states; richer, isolated Avalonia 12 runtime probes; effective mode and composite child extraction; bounded exception trees; weak source navigation IDs; response text and expression budgets; explicit data-context evidence. No commercial decompiled implementation is included.

The public XamlMcp.Avalonia 1.0.0-preview.3 compatibility API was compiled and its probe resolved against Avalonia 12.1.4. Its public record does not expose mode, fallback/target-null values, converter parameter, update trigger, raw exceptions or child expressions. The native backend therefore uses the small compatibility boundary instead of adding a dependency on the complete upstream agent runtime.
