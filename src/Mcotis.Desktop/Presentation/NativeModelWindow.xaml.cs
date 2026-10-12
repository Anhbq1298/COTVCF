using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Mcotis.Application;
using Microsoft.Win32;

namespace COT_VCF.Presentation;

public partial class NativeModelWindow : Window
{
    public NativeModelViewModel Model { get; }
    public NativeModelWindow(DocumentSession session)
    {
        Model = new(session); InitializeComponent(); DataContext = Model;
        Loaded += (_, _) => { Viewer.ShowPlane("3D"); Viewer.FitView(); };
        Closing += OnClosing;
        PreviewKeyDown += async (_, e) =>
        {
            if (!Model.Editable) return;
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; Save(false); }
            else if (e.Key == Key.F5 && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; await Model.RunAsync(); }
        };
    }
    internal static void CreateTemplate(Window owner)
    {
        var form = new TemplateBuildingWindow { Owner = owner };
        if (form.ShowDialog() == true && form.CreatedSession is { } session) new NativeModelWindow(session).Show();
    }
    private void Template_Click(object sender, RoutedEventArgs e) => CreateTemplate(this);
    private void Options_Click(object sender, RoutedEventArgs e) => new AnalysisOptionsWindow(Model.Session.Document.Settings,
        settings => Model.Session.Execute(DocumentCommands.SetSettings(settings))) { Owner = this }.ShowDialog();
    private async void Run_Click(object sender, RoutedEventArgs e) => await Model.RunAsync();
    private void Undo_Click(object sender, RoutedEventArgs e) { if (Model.Editable) Model.Session.Undo(); }
    private void Redo_Click(object sender, RoutedEventArgs e) { if (Model.Editable) Model.Session.Redo(); }
    private bool Save(bool saveAs)
    {
        try
        {
            if (saveAs || Model.Session.FilePath is null)
            {
                var dialog = new SaveFileDialog { Filter = "Mcotis native model (*.mcotis)|*.mcotis", DefaultExt = ".mcotis", FileName = "TemplateBuilding.mcotis" };
                if (dialog.ShowDialog(this) != true) return false;
                Model.Files.SaveAs(dialog.FileName);
            }
            else Model.Files.Save();
            Model.Message = "Model saved. Results are held in this window; they are not stored in the model file.";
            return true;
        }
        catch (Exception ex) { Model.Message = "Save failed: " + ex.Message; return false; }
    }
    private void Save_Click(object sender, RoutedEventArgs e) => Save(false);
    private void SaveAs_Click(object sender, RoutedEventArgs e) => Save(true);
    private bool MayDiscard()
    {
        if (!Model.Session.IsDirty) return true;
        return MessageBox.Show(this, "Save changes to this native model?", "Mcotis", MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
        { MessageBoxResult.Yes => Save(false), MessageBoxResult.No => true, _ => false };
    }
    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Mcotis native model (*.mcotis)|*.mcotis" };
        if (dialog.ShowDialog(this) != true || !MayDiscard()) return;
        try { Model.Files.Open(dialog.FileName); Model.Message = "Native model opened. Run analysis to calculate results."; Viewer.FitView(); }
        catch (Exception ex) { Model.Message = "Open failed: " + ex.Message; }
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (Model.Busy) { Model.Message = "Wait for analysis to finish before closing this window."; e.Cancel = true; }
        else if (!MayDiscard()) e.Cancel = true;
    }
}
