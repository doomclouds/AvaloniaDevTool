using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AvaloniaDevTool.NativeAutomation.Fixture;

internal static class Program
{
    [STAThread]
    private static void Main(string[] arguments)
    {
        if (NativeHwndReuseFixture.TryRun(arguments)) return;
        if (NativeMultiPickerFixture.TryRun(arguments)) return;
        Console.OutputEncoding = new UTF8Encoding(false);
        ApplicationConfiguration.Initialize();
        var window = new Form { Text = "AVDT Native Scope Fixture", Name = "NativeFixture", Width = 600, Height = 380 };
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        var editor = new TextBox { Name = "NativeEditor", Text = "initial", Width = 350 };
        var toggle = new CheckBox { Name = "NativeToggle", Text = "Native toggle", AutoSize = true };
        var status = new Label { Name = "NativeStatus", Text = "idle", Width = 400, AutoSize = false };
        var invoke = new Button { Name = "NativeInvoke", Text = "Invoke counter", Width = 180 };
        var count = 0;
        invoke.Click += (_, _) => status.Text = $"invoked:{++count}";
        var modal = new Button { Name = "NativeModal", Text = "Open owned modal", Width = 180 };
        modal.Click += (_, _) =>
        {
            using var dialog = new Form { Name = "NativeOwnedModal", Text = "AVDT Owned Modal", Width = 400, Height = 180 };
            var close = new Button { Name = "NativeModalClose", Text = "Close modal", Dock = DockStyle.Top };
            close.Click += (_, _) => dialog.Close();
            dialog.Controls.Add(close);
            dialog.ShowDialog(window);
        };
        var list = new ListBox { Name = "NativeSelection", Width = 300, Height = 70 };
        var secondary = new Button { Name = "NativeSecondary", Text = "Open secondary window", Width = 180 };
        secondary.Click += (_, _) =>
        {
            var child = new Form { Name = "NativeSecondaryWindow", Text = "AVDT Secondary Window", Width = 400, Height = 180 };
            child.Controls.Add(new TextBox { Name = "NativeSecondaryEditor", Text = "secondary", Dock = DockStyle.Top });
            var interrupt = new Button { Name = "NativeEnableFocusInterruption", Text = "Open focus interrupter", Dock = DockStyle.Bottom };
            interrupt.Click += (_, _) =>
            {
                var third = new Form { Name = "NativeFocusInterrupter", Text = "AVDT Focus Interrupter", Width = 400, Height = 180 };
                third.Controls.Add(new TextBox { Name = "NativeInterrupterEditor", Text = "untouched", Dock = DockStyle.Top });
                editor.GotFocus += (_, _) => { if (!third.IsDisposed && third.Visible) third.Activate(); };
                third.Show(window);
            };
            child.Controls.Add(interrupt);
            child.Show(window);
        };
        list.Items.AddRange(["First native item", "Second native item"]);
        var pointer = new Panel { Name = "NativePointer", Width = 350, Height = 60, BackColor = System.Drawing.Color.LightBlue };
        pointer.MouseDown += (_, e) => status.Text = e.Button == MouseButtons.Right ? "pointer:right" : "pointer:down";
        pointer.MouseDoubleClick += (_, _) => status.Text = "pointer:double";
        pointer.MouseWheel += (_, e) => status.Text = $"pointer:wheel:{e.Delta}";
        pointer.MouseMove += (_, e) => { if (e.Button == MouseButtons.Left) status.Text = "pointer:drag"; };
        panel.Controls.AddRange([editor, toggle, invoke, modal, secondary, list, pointer, status]);
        window.Controls.Add(panel);
        NativePatternFixtureControls.AddTo(window);
        window.Shown += (_, _) =>
        {
            using var process = Process.GetCurrentProcess();
            Console.WriteLine(JsonSerializer.Serialize(new { processId = process.Id, startTimeUtc = process.StartTime.ToUniversalTime() }));
            Console.Out.Flush();
        };
        Application.Run(window);
    }
}
