using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Globalization;
namespace COT_VCF.Presentation.Controls;
public partial class EngineeringInput : UserControl
{
    public EngineeringInput()
    {
        InitializeComponent();
        var binding = new Binding(nameof(Value)) { Source = this, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged, Converter = new NumberConverter(this), ValidatesOnExceptions = true };
        binding.ValidationRules.Add(new NumberRule(this)); Editor.SetBinding(TextBox.TextProperty, binding);
        Editor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Up or Key.Down) || !double.IsFinite(Increment) || Increment <= 0 || Validation.GetHasError(Editor)) return;
            Value = Math.Clamp(Value + (e.Key == Key.Up ? Increment : -Increment), Minimum, Maximum); e.Handled = true;
        };
    }
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(EngineeringInput), new PropertyMetadata("Value"));
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(nameof(Unit), typeof(string), typeof(EngineeringInput), new PropertyMetadata(""));
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(EngineeringInput), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(EngineeringInput), new PropertyMetadata(double.MinValue));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(EngineeringInput), new PropertyMetadata(double.MaxValue));
    public static readonly DependencyProperty IncrementProperty = DependencyProperty.Register(nameof(Increment), typeof(double), typeof(EngineeringInput), new PropertyMetadata(1d));
    public static readonly DependencyProperty WholeNumbersOnlyProperty = DependencyProperty.Register(nameof(WholeNumbersOnly), typeof(bool), typeof(EngineeringInput), new PropertyMetadata(false, (d, _) => ((EngineeringInput)d).Editor.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget()));
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Increment { get => (double)GetValue(IncrementProperty); set => SetValue(IncrementProperty, value); }
    public bool WholeNumbersOnly { get => (bool)GetValue(WholeNumbersOnlyProperty); set => SetValue(WholeNumbersOnlyProperty, value); }
    private sealed class NumberConverter(EngineeringInput input) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is double number ? number.ToString(input.WholeNumbersOnly ? "F0" : "F4", culture) : "";
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            double.TryParse(value?.ToString(), NumberStyles.Float | NumberStyles.AllowThousands, culture, out var number) ? number : throw new FormatException("Enter a finite number.");
    }
    private sealed class NumberRule(EngineeringInput input) : ValidationRule
    {
        public override ValidationResult Validate(object value, CultureInfo cultureInfo)
        {
            if (!double.TryParse(value?.ToString(), NumberStyles.Float | NumberStyles.AllowThousands, cultureInfo, out var number) || !double.IsFinite(number)) return new(false, "Enter a finite number.");
            if (number < input.Minimum || number > input.Maximum) return new(false, $"Use a value from {input.Minimum:G6} to {input.Maximum:G6} {input.Unit}.");
            if (input.WholeNumbersOnly && number != Math.Truncate(number)) return new(false, "Enter a whole number.");
            return ValidationResult.ValidResult;
        }
    }
}
