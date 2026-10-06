using System.Windows;

namespace COT_VCF.Presentation;

/// <summary>Help: the workflow overview and the Tab 07 FEM procedure documentation.</summary>
public partial class HelpWindow : Window
{
    public enum Topic { Workflow, FemProcedure }

    public HelpWindow() => InitializeComponent();

    public void Select(Topic topic) => Topics.SelectedIndex = (int)topic;
}
