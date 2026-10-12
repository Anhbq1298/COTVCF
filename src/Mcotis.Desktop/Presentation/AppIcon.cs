using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace COT_VCF.Presentation;

/// <summary>The software logo as a window icon. It comes from the multi-size AppIcon.ico (16–256 px, the same file
/// as the .exe icon) so Windows picks a frame made for each place: title bar, taskbar, Alt-Tab.</summary>
public static class AppIcon
{
    public static ImageSource Window { get; } =
        BitmapFrame.Create(new Uri("pack://application:,,,/Mcotis;component/Presentation/AppIcon.ico", UriKind.Absolute));
}
