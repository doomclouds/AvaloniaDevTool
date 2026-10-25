using System.Runtime.InteropServices;

namespace AvaloniaDevTool.NativeAutomation.Fixture;

public static class NativePatternFixtureControls
{
    internal static void AddTo(Form owner)
    {
        var prefix = Environment.GetEnvironmentVariable("AVDT_NATIVE_PATTERN_PREFIX");
        if (string.IsNullOrEmpty(prefix)) return;

        owner.Shown += (_, _) =>
        {
            var window = new Form
            {
                Name = "NativePatternsWindow", Text = "AVDT Native Patterns Fixture",
                Width = 560, Height = 510, StartPosition = FormStartPosition.CenterScreen
            };
            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            var password = new TextBox { Name = "NativePassword", Text = "fixture-secret-秘密", UseSystemPasswordChar = true, Width = 380 };
            var scroll = new ListBox { Name = "NativeScroll", Width = 380, Height = 100 };
            scroll.Items.AddRange(Enumerable.Range(0, 100).Select(index => (object)$"Native scroll item {index:D3}").ToArray());
            var scrollStatus = new Label { Name = "NativeScrollStatus", Text = "top:0", Width = 380 };
            var modifierStatus = new Label { Name = "NativeModifierStatus", Text = "keys:none", Width = 420 };
            var modifierEditor = new TextBox { Name = "NativeModifierEditor", Text = "modifier-target", Width = 380 };
            modifierEditor.KeyDown += (_, e) => modifierStatus.Text = $"key:{e.KeyCode};ctrl:{e.Control};alt:{e.Alt};shift:{e.Shift}";
            var actionStatus = new Label { Name = "NativeControlledStatus", Text = "controlled:idle", Width = 380 };
            var complete = new ControlledInvokeControl(prefix, "Complete", false, () => actionStatus.Text = "controlled:completed");
            var fail = new ControlledInvokeControl(prefix, "Fail", true, () => actionStatus.Text = "controlled:failed");
            panel.Controls.AddRange([password, scroll, scrollStatus, modifierEditor, modifierStatus, complete, fail, actionStatus]);
            window.Controls.Add(panel);
            var observer = new System.Windows.Forms.Timer { Interval = 20 };
            observer.Tick += (_, _) => scrollStatus.Text = $"top:{scroll.TopIndex}";
            window.FormClosed += (_, _) => observer.Dispose();
            observer.Start();
            window.Show(owner);
        };
    }

    private sealed class ControlledInvokeControl : Control
    {
        private readonly ControlledInvokeProvider _provider;

        internal ControlledInvokeControl(string prefix, string suffix, bool fail, Action completed)
        {
            Name = "NativeControlled" + suffix;
            Text = "Controlled UIA " + suffix;
            Width = 380;
            Height = 36;
            BackColor = System.Drawing.Color.LightSteelBlue;
            _provider = new ControlledInvokeProvider(this, prefix, suffix, fail, completed);
        }

        protected override void WndProc(ref Message message)
        {
            // A dedicated HWND provider makes completion controllable without a fake backend.
            if (message.Msg == 0x003D && unchecked((int)message.LParam.ToInt64()) == -25)
            {
                message.Result = UiaReturnRawElementProvider(Handle, message.WParam, message.LParam, _provider);
                return;
            }
            base.WndProc(ref message);
        }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class ControlledInvokeProvider(Control control, string prefix, string suffix, bool fail, Action completed)
        : IFixtureRawElementProviderSimple, IFixtureInvokeProvider
    {
        public int ProviderOptions => 2;
        public object? GetPatternProvider(int patternId) => patternId == 10000 ? this : null;
        public object? GetPropertyValue(int propertyId) => propertyId switch
        {
            30003 => 50000,
            30005 => control.Text,
            30010 => true,
            30011 => control.Name,
            30016 or 30017 => true,
            30024 => "AVDT.NativeFixture",
            _ => null
        };

        public IFixtureRawElementProviderSimple HostRawElementProvider
        {
            get
            {
                Marshal.ThrowExceptionForHR(UiaHostProviderFromHwnd(control.Handle, out var provider));
                return provider;
            }
        }

        public void Invoke()
        {
            using var entered = EventWaitHandle.OpenExisting(prefix + "-" + suffix + "-entered");
            using var release = EventWaitHandle.OpenExisting(prefix + "-" + suffix + "-release");
            entered.Set();
            if (!release.WaitOne(TimeSpan.FromSeconds(10)))
                throw new COMException("The controlled fixture action was not released.", unchecked((int)0x80131505));
            if (control.InvokeRequired) control.BeginInvoke(completed); else completed();
            if (fail) throw new COMException("The controlled fixture provider rejected the action.", unchecked((int)0x80070005));
        }
    }

    [ComVisible(true), Guid("D6DD68D1-86FD-4332-8666-9ABEDEA2D24C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IFixtureRawElementProviderSimple
    {
        int ProviderOptions { get; }
        [return: MarshalAs(UnmanagedType.IUnknown)] object? GetPatternProvider(int patternId);
        [return: MarshalAs(UnmanagedType.Struct)] object? GetPropertyValue(int propertyId);
        IFixtureRawElementProviderSimple HostRawElementProvider { get; }
    }

    [ComVisible(true), Guid("54FCB24B-E18E-47A2-B4D3-ECCBE77599A2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IFixtureInvokeProvider
    {
        void Invoke();
    }

    [DllImport("UIAutomationCore.dll")]
    private static extern nint UiaReturnRawElementProvider(nint window, nint wParam, nint lParam, IFixtureRawElementProviderSimple provider);
    [DllImport("UIAutomationCore.dll")]
    private static extern int UiaHostProviderFromHwnd(nint window, out IFixtureRawElementProviderSimple provider);
}
