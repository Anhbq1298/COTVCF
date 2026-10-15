using System.Globalization;
using System.Windows.Data;
using Mcotis.Desktop.Presentation.Controls;

namespace Mcotis.Desktop.Presentation;

/// <summary>A tool button of the model view: checked while its tool (the parameter) is the tool; checking it chooses the
/// tool, unchecking it goes back to selecting.</summary>
public sealed class DrawingToolConverter : IValueConverter
{
    public static DrawingToolConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is DrawingTool tool && parameter is DrawingTool button && tool == button;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true && parameter is DrawingTool button ? button : DrawingTool.None;
}

/// <summary>The Select button: checked while neither Pan nor a drawing tool is on (values: the viewer's PanMode, the
/// drawing tool). One way: its click handler sets both.</summary>
public sealed class SelectToolConverter : IMultiValueConverter
{
    public static SelectToolConverter Instance { get; } = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length == 2 && values[0] is false && values[1] is DrawingTool.None;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        [Binding.DoNothing, Binding.DoNothing];
}
