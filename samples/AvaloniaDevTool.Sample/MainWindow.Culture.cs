using System.ComponentModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;

namespace AvaloniaDevTool.Sample;

public sealed partial class MainWindow
{
    private readonly CultureDemoModel _cultureDemo = new();
    private void InitializeCultureDemo()
    {
        this.FindControl<TextBlock>("CultureFrench")!.Bind(TextBlock.TextProperty, new Binding(nameof(CultureDemoModel.Amount))
        { Source = _cultureDemo, ConverterCulture = CultureInfo.GetCultureInfo("fr-FR"), StringFormat = "N2" });
        this.FindControl<TextBlock>("CultureEnglish")!.Bind(TextBlock.TextProperty, new Binding(nameof(CultureDemoModel.Amount))
        { Source = _cultureDemo, ConverterCulture = CultureInfo.GetCultureInfo("en-US"), StringFormat = "N2" });
        this.FindControl<TextBox>("CultureAmountInput")!.Bind(TextBox.TextProperty, new Binding(nameof(CultureDemoModel.Amount))
        {
            Source = _cultureDemo, Mode = BindingMode.TwoWay,
            ConverterCulture = CultureInfo.GetCultureInfo("fr-FR"), UpdateSourceTrigger = UpdateSourceTrigger.LostFocus
        });
    }
    private void AdvanceCultureAmountClick(object? sender, RoutedEventArgs args) => _cultureDemo.Amount += 1.25m;
    private sealed class CultureDemoModel : INotifyPropertyChanged
    {
        private decimal _amount = 12345.67m;
        public event PropertyChangedEventHandler? PropertyChanged;
        public decimal Amount
        {
            get => _amount;
            set { if (_amount == value) return; _amount = value; PropertyChanged?.Invoke(this, new(nameof(Amount))); }
        }
    }
}
