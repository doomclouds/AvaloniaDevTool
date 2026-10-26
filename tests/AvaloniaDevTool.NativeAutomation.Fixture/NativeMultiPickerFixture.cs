using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AvaloniaDevTool.NativeAutomation.Fixture;

internal static partial class NativeMultiPickerFixture
{
    internal static bool TryRun(string[] arguments)
    {
        if (arguments.FirstOrDefault() != "--native-multi-picker")
            return false;

        try
        {
            if (arguments.Length != 2 || !Directory.Exists(arguments[1]))
                throw new ArgumentException("The multi-picker fixture requires an existing initial directory.");
            Console.OutputEncoding = new UTF8Encoding(false);
            ApplicationConfiguration.Initialize();
            using var ready = new CountdownEvent(2);
            var threads = new[]
                { StartPicker("first", arguments[1], ready), StartPicker("second", arguments[1], ready) };
            if (!ready.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The two independent picker owners did not appear.");
            using var process = Process.GetCurrentProcess();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                processId = process.Id, startTimeUtc = process.StartTime.ToUniversalTime(), pickerCount = 2
            }));
            Console.Out.Flush();
            foreach (var thread in threads)
                thread.Join();
            // Keep the target identity alive while the test inspects both closed dialogs.
            Thread.Sleep(Timeout.Infinite);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            Environment.Exit(1);
        }

        return true;
    }

    private static Thread StartPicker(string slot, string directory, CountdownEvent ready)
    {
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException, threadScope: true);
                using var owner = new Form
                {
                    Name = "NativeMultiPickerOwner_" + slot, Text = "AVDT Multi Picker Owner " + slot,
                    Width = 380, Height = 180, StartPosition = FormStartPosition.Manual,
                    Location = new System.Drawing.Point(slot == "first" ? 40 : 700, 40)
                };
                owner.Shown += (_, _) =>
                {
                    ready.Signal();
                    owner.BeginInvoke(new Action(() =>
                    {
                        // Each STA owns a real common item dialog and an independent modal loop.
                        using var picker = new OpenFileDialog
                        {
                            AutoUpgradeEnabled = true, InitialDirectory = directory,
                            FileName = slot + "-untouched.txt", Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                            CheckFileExists = true, Multiselect = false, RestoreDirectory = true
                        };
                        picker.ShowDialog(owner);
                        owner.Close();
                    }));
                };
                Application.Run(owner);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                Environment.Exit(1);
            }
        }) { IsBackground = true, Name = "NativeMultiPicker-" + slot };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread;
    }
}
