using System.Windows;
using COT_VCF.Application;
using COT_VCF.Application.Quantities;
using COT_VCF.Application.Workers;
using COT_VCF.Domain;
using COT_VCF.Infrastructure;
using COT_VCF.Presentation;
using COT_VCF.SAP2000;

namespace COT_VCF;
public partial class App : System.Windows.Application
{
    private SapGateway? gateway;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // A UI-thread error must not silently close the app (and lose the SAP session): log it, report it, keep running.
        DispatcherUnhandledException += (_, args) =>
        {
            var log = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "COTVCF", "error.log");
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(log)!);
                System.IO.File.AppendAllText(log, $"{DateTime.Now:O}{Environment.NewLine}{args.Exception}{Environment.NewLine}{Environment.NewLine}");
            }
            catch (System.IO.IOException) { }
            catch (UnauthorizedAccessException) { }
            MessageBox.Show(MainWindow, $"{args.Exception.Message}{Environment.NewLine}{Environment.NewLine}Details were written to {log}. " +
                "The app is still running; save your work.", "Mcotis — unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        gateway = new();
        var state = new ApplicationState();
        var workflow = new WorkflowService(gateway, state);
        var config = new Configuration.ConfigService(gateway, state, workflow);
        var studyExecution = new StudyExecutionCoordinator(
            new ConnectedSapInstanceHostFactory(gateway, new SapInstanceHostFactory()));
        // Tab 08 reads a candidate's own text export; where that cannot be used, a private copy of the model in a
        // SAP2000 instance of its own — never the connected one.
        var quantityTakeoff = new QuantityTakeoffService(
            new IsolatedSapTableReader(new SapInstanceHostFactory()));
        var outputDialogs = new OutputSettingsDialogService();
        var outputPreparation = new OutputGroupPreparationService(gateway);
        var viewModel = new MainViewModel(state, workflow, new SelectionService(), config, studyExecution,
            outputDialogs: outputDialogs, outputGroupPreparation: outputPreparation,
            printStore: new ReportPrintSettingsStore(), quantityTakeoff: quantityTakeoff);
        // Open the built-in model before showing the window so startup never displays the blank chooser.
        viewModel.LoadDefaultFemModel();
        MainWindow = new Presentation.MainWindow { DataContext = viewModel };
        MainWindow.Show();
        // The model view opens on its own; the Define / Run / Result … pane appears when a menu item is chosen.
        ((Presentation.MainWindow)MainWindow).ClosePane();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if (MainWindow?.DataContext is MainViewModel viewModel) viewModel.FlushFemWorkingSession();
        gateway?.Dispose();
        base.OnExit(e);
    }
}
