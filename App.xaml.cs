using System.Windows;
using COT_VCF.Application;
using COT_VCF.Application.Workers;
using COT_VCF.Domain;
using COT_VCF.Presentation;
using COT_VCF.SAP2000;

namespace COT_VCF;
public partial class App : System.Windows.Application
{
    private SapGateway? gateway;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        gateway = new();
        var state = new ApplicationState();
        var workflow = new WorkflowService(gateway, state);
        var config = new Configuration.ConfigService(gateway, state, workflow);
        var studyExecution = new StudyExecutionCoordinator(
            new ConnectedSapInstanceHostFactory(gateway, new SapInstanceHostFactory()));
        var viewModel = new MainViewModel(state, workflow, new SelectionService(), config, studyExecution);
        MainWindow = new Presentation.MainWindow { DataContext = viewModel };
        MainWindow.Show();
    }
    protected override void OnExit(ExitEventArgs e) { gateway?.Dispose(); base.OnExit(e); }
}
