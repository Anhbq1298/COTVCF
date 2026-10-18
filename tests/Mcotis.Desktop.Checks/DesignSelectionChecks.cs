using System.Windows;
using Mcotis.Desktop.Application.Design;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Presentation;

internal static class DesignSelectionChecks
{
    public static void Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/Mcotis;component/Presentation/Theme.xaml", UriKind.Relative),
        });

        var options = new[]
        {
            new DesignMemberOption("CFT-1", "CFT", "Concrete", 5, true),
            new DesignMemberOption("MEGA-1", "Mega", "Concrete", 20, false),
            new DesignMemberOption("RC-1", "Rectangle", "Concrete", 4, false),
        };
        var selected = options.Select(option => option.Member).ToArray();
        var cft = Open(ColumnDesignType.ConcreteFilledTube, options, selected);
        var mega = Open(ColumnDesignType.MegaComposite, options.Select(option => option with
        {
            Suggested = option.Member == "MEGA-1",
        }).ToArray(), selected);

        var cftChecked = Checked(cft);
        var megaChecked = Checked(mega);
        if (!cftChecked.SequenceEqual(["CFT-1"]) || !megaChecked.SequenceEqual(["MEGA-1"]) ||
            !cft.SelectionText.Text.Contains("other selected members are skipped", StringComparison.Ordinal))
            throw new InvalidOperationException($"A design module must take only compatible selected members. CFT: [{string.Join(", ", cftChecked)}]; Mega: [{string.Join(", ", megaChecked)}].");

        cft.Close();
        mega.Close();

        Console.WriteLine("PASS: mixed viewer selection is filtered to the members compatible with each design module");
    }

    private static DesignReferenceWindow Open(ColumnDesignType type, IReadOnlyList<DesignMemberOption> options, IReadOnlyList<string> selected)
    {
        var basis = ColumnDesignCodes.Default(type);
        return new DesignReferenceWindow(basis, options, [], [], null, _ => basis.Section, selected);
    }

    private static string[] Checked(DesignReferenceWindow window) =>
        ((IEnumerable<DesignReferenceWindow.CheckItem>)window.MemberList.ItemsSource).Where(item => item.IsChecked).Select(item => item.Name).ToArray();
}
