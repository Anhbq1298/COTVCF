using System.Windows;
using System.Windows.Controls;
using Mcotis.Application;
using Mcotis.Core;

namespace Mcotis.Desktop.Presentation;

public partial class TemplateBuildingWindow : Window
{
    public TemplateBuildingViewModel Form { get; } = new();
    public DocumentSession? CreatedSession { get; private set; }
    public TemplateBuildingWindow() { InitializeComponent(); DataContext = Form; }
    private void FinishEdits()
    {
        foreach (var grid in new[] { XGrid, YGrid, StoreyGrid, SectionGrid })
        {
            grid.CommitEdit(DataGridEditingUnit.Cell, true);
            grid.CommitEdit(DataGridEditingUnit.Row, true);
        }
    }
    private void UpdateTables_Click(object sender, RoutedEventArgs e) { FinishEdits(); Form.UpdateTables(); }
    internal bool CreateModel()
    {
        FinishEdits();
        try
        {
            var session = new DocumentSession();
            session.Execute(TemplateBuilding.Create(Form.ReadInput()));
            CreatedSession = session;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or DocumentException)
        { Form.ReportError(ex.Message); return false; }
    }
    private void Create_Click(object sender, RoutedEventArgs e) { if (CreateModel()) DialogResult = true; }
}
