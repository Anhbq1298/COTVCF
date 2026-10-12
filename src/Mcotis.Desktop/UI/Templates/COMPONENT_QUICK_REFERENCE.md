# Quick Reference Guide: Structural Engineering UI Components

Fast lookup for implementing common components in WPF with the design system.

---

## 1. Property Input Control (Dimension + Unit)

```xml
<!-- XAML -->
<Grid Margin="0,8">
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="120" />
        <ColumnDefinition Width="*" />
        <ColumnDefinition Width="80" />
        <ColumnDefinition Width="24" />
    </Grid.ColumnDefinitions>

    <!-- Label -->
    <TextBlock Grid.Column="0" Text="Beam Depth:" VerticalAlignment="Center" 
               Foreground="{StaticResource TextPrimaryBrush}" />

    <!-- Input Value -->
    <TextBox Grid.Column="1" Margin="8,0" Style="{StaticResource StructuredTextBoxStyle}" 
             Text="{Binding BeamDepth, StringFormat=F4, UpdateSourceTrigger=PropertyChanged}" />

    <!-- Unit Dropdown -->
    <ComboBox Grid.Column="2" Margin="8,0" Height="36" SelectedItem="{Binding DepthUnit}">
        <ComboBoxItem>mm</ComboBoxItem>
        <ComboBoxItem>cm</ComboBoxItem>
        <ComboBoxItem>in</ComboBoxItem>
    </ComboBox>

    <!-- Status Indicator -->
    <TextBlock Grid.Column="3" Text="✓" Foreground="{StaticResource StructuralGreenBrush}" 
               VerticalAlignment="Center" FontSize="16" />
</Grid>
```

---

## 2. Error Message Display

```xml
<!-- XAML - Error State -->
<Border Margin="0,4" Padding="8" Background="#FFEBEE" BorderBrush="{StaticResource CriticalRedBrush}" 
        BorderThickness="1" Visibility="{Binding HasError, Converter={StaticResource BoolToVisibilityConverter}}">
    <StackPanel Orientation="Horizontal">
        <TextBlock Text="⚠ " Foreground="{StaticResource CriticalRedBrush}" FontSize="14" Margin="0,0,4,0" />
        <TextBlock Text="{Binding ErrorMessage}" Foreground="{StaticResource CriticalRedBrush}" 
                   FontSize="11" />
    </StackPanel>
</Border>

<!-- C# ViewModel Code -->
private string _errorMessage;
private bool _hasError;

public string ErrorMessage
{
    get => _errorMessage;
    set => SetProperty(ref _errorMessage, value);
}

public bool HasError
{
    get => _hasError;
    set => SetProperty(ref _hasError, value);
}

public void ValidateInput(double depth, double length)
{
    if (depth <= 0)
    {
        ErrorMessage = "Beam depth must be greater than 0 mm";
        HasError = true;
    }
    else if (depth > length / 2)
    {
        ErrorMessage = "Beam depth cannot exceed half the span length";
        HasError = true;
    }
    else
    {
        ErrorMessage = "";
        HasError = false;
    }
}
```

---

## 3. Load Application Control

```xml
<!-- XAML -->
<Border Margin="0,8" Padding="12" Background="{StaticResource SurfaceL1Brush}" 
        BorderBrush="{StaticResource SurfaceL3Brush}" BorderThickness="1">
    <StackPanel>
        <TextBlock Text="Apply Load" FontWeight="SemiBold" Margin="0,0,0,12" 
                   Foreground="{StaticResource TextPrimaryBrush}" />

        <!-- Load Magnitude -->
        <Grid Margin="0,0,0,8">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="80" />
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="80" />
            </Grid.ColumnDefinitions>
            <TextBlock Grid.Column="0" Text="Magnitude:" VerticalAlignment="Center" />
            <TextBox Grid.Column="1" Margin="8,0" Style="{StaticResource StructuredTextBoxStyle}" 
                     Text="{Binding LoadMagnitude, StringFormat=F2}" />
            <TextBlock Grid.Column="2" Text="kN" VerticalAlignment="Center" Margin="8,0,0,0" />
        </Grid>

        <!-- Load Direction -->
        <Grid Margin="0,0,0,8">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="80" />
                <ColumnDefinition Width="*" />
            </Grid.ColumnDefinitions>
            <TextBlock Grid.Column="0" Text="Direction:" VerticalAlignment="Center" />
            <ComboBox Grid.Column="1" Margin="8,0" Height="36" SelectedItem="{Binding LoadDirection}">
                <ComboBoxItem>Downward (-Y)</ComboBoxItem>
                <ComboBoxItem>Upward (+Y)</ComboBoxItem>
                <ComboBoxItem>Right (+X)</ComboBoxItem>
                <ComboBoxItem>Left (-X)</ComboBoxItem>
            </ComboBox>
        </Grid>

        <!-- Apply Button -->
        <Button Content="Apply Load" Style="{StaticResource PrimaryButtonStyle}" Margin="0" 
                Command="{Binding ApplyLoadCommand}" HorizontalAlignment="Right" Width="120" />
    </StackPanel>
</Border>
```

---

## 4. Results Table (Color-Coded Safety Factors)

```xml
<!-- XAML -->
<DataGrid ItemsSource="{Binding ResultRows}" Style="{StaticResource StructuredDataGridStyle}" 
          AutoGenerateColumns="False" CanUserAddRows="False">
    <DataGrid.Columns>
        <!-- Element Name -->
        <DataGridTextColumn Header="Element" Binding="{Binding ElementName}" 
                            IsReadOnly="True" Width="100" />

        <!-- Stress Value -->
        <DataGridTextColumn Header="Stress (MPa)" Binding="{Binding Stress, StringFormat=F2}" 
                            IsReadOnly="True" Width="120" />

        <!-- Safety Factor with Color -->
        <DataGridTemplateColumn Header="Safety Factor" Width="120">
            <DataGridTemplateColumn.CellTemplate>
                <DataTemplate>
                    <Border Padding="4" Background="{Binding SafetyFactor, 
                            Converter={StaticResource SafetyFactorColorConverter}}">
                        <TextBlock Text="{Binding SafetyFactor, StringFormat=F2}" 
                                   Foreground="White" FontWeight="SemiBold" 
                                   TextAlignment="Center" />
                    </Border>
                </DataTemplate>
            </DataGridTemplateColumn.CellTemplate>
        </DataGridTemplateColumn>

        <!-- Status -->
        <DataGridTemplateColumn Header="Status" Width="100">
            <DataGridTemplateColumn.CellTemplate>
                <DataTemplate>
                    <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
                        <TextBlock Text="{Binding Status, Converter={StaticResource StatusIconConverter}}" 
                                   FontSize="14" Margin="0,0,4,0" />
                        <TextBlock Text="{Binding Status}" FontSize="11" />
                    </StackPanel>
                </DataTemplate>
            </DataGridTemplateColumn.CellTemplate>
        </DataGridTemplateColumn>
    </DataGrid.Columns>
</DataGrid>

<!-- C# Converter: Safety Factor to Color -->
public class SafetyFactorColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double sf)
        {
            if (sf >= 1.5)
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2E7D32")); // Green
            else if (sf >= 1.0)
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F57C00")); // Orange
            else
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#C62828")); // Red
        }
        return new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => null;
}
```

---

## 5. Material/Section Selector with Autocomplete

```xml
<!-- XAML -->
<Grid Margin="0,8">
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="100" />
        <ColumnDefinition Width="*" />
    </Grid.ColumnDefinitions>

    <TextBlock Grid.Column="0" Text="Material:" VerticalAlignment="Center" />
    
    <ComboBox Grid.Column="1" Margin="8,0" Height="36" 
              ItemsSource="{Binding AvailableMaterials}" 
              SelectedItem="{Binding SelectedMaterial}"
              IsEditable="True" TextSearch.TextPath="Name">
        <ComboBox.ItemTemplate>
            <DataTemplate>
                <TextBlock Text="{Binding Name}" />
            </DataTemplate>
        </ComboBox.ItemTemplate>
    </ComboBox>
</Grid>

<!-- Section Selector -->
<Grid Margin="0,8">
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="100" />
        <ColumnDefinition Width="*" />
    </Grid.ColumnDefinitions>

    <TextBlock Grid.Column="0" Text="Section:" VerticalAlignment="Center" />
    
    <ComboBox Grid.Column="1" Margin="8,0" Height="36" 
              ItemsSource="{Binding FilteredSections}"
              SelectedItem="{Binding SelectedSection}"
              IsEditable="True">
        <ComboBox.ItemTemplate>
            <DataTemplate>
                <TextBlock Text="{Binding DisplayName}" />
            </DataTemplate>
        </ComboBox.ItemTemplate>
    </ComboBox>
</Grid>

<!-- C# ViewModel -->
public class MemberViewModel : ObservableObject
{
    private ObservableCollection<Material> _availableMaterials;
    private ObservableCollection<Section> _filteredSections;
    private Material _selectedMaterial;
    private Section _selectedSection;

    public ObservableCollection<Material> AvailableMaterials
    {
        get => _availableMaterials;
        set => SetProperty(ref _availableMaterials, value);
    }

    public Material SelectedMaterial
    {
        get => _selectedMaterial;
        set
        {
            SetProperty(ref _selectedMaterial, value);
            FilterSections(); // Update available sections
        }
    }

    public void FilterSections()
    {
        var sections = new ObservableCollection<Section>(
            LoadStandardSections(_selectedMaterial.Name)
        );
        FilteredSections = sections;
    }
}
```

---

## 6. Support Condition Definition

```xml
<!-- XAML -->
<Border Margin="0,8" Padding="12" Background="{StaticResource SurfaceL1Brush}" 
        BorderBrush="{StaticResource SurfaceL3Brush}" BorderThickness="1">
    <StackPanel>
        <TextBlock Text="Support Type" FontWeight="SemiBold" Margin="0,0,0,12" />

        <!-- Support Type Selection -->
        <RadioButton Content="Fixed (All DOF constrained)" Margin="0,0,0,8" 
                     IsChecked="{Binding SupportType, Converter={StaticResource EnumToBoolConverter}, ConverterParameter=Fixed}" />
        
        <RadioButton Content="Pinned (X, Y constrained, Z free)" Margin="0,0,0,8" 
                     IsChecked="{Binding SupportType, Converter={StaticResource EnumToBoolConverter}, ConverterParameter=Pinned}" />
        
        <RadioButton Content="Roller (Y constrained, X, Z free)" Margin="0,0,0,8" 
                     IsChecked="{Binding SupportType, Converter={StaticResource EnumToBoolConverter}, ConverterParameter=Roller}" />

        <!-- Visual Representation -->
        <TextBlock Margin="0,12,0,0" FontSize="11" Foreground="{StaticResource TextSecondaryBrush}">
            <Run Text="Selected: " />
            <Run Text="{Binding SupportType}" FontWeight="SemiBold" />
        </TextBlock>

        <!-- Reactions Display (after analysis) -->
        <TextBlock Margin="0,8,0,0" FontSize="11" FontWeight="SemiBold" Text="Reactions:" 
                   Visibility="{Binding HasReactions, Converter={StaticResource BoolToVisibilityConverter}}" />
        <TextBlock Margin="16,4,0,0" FontSize="10" Text="{Binding ReactionFx, StringFormat='Rx: {0:F2} kN'}" />
        <TextBlock Margin="16,2,0,0" FontSize="10" Text="{Binding ReactionFy, StringFormat='Ry: {0:F2} kN'}" />
        <TextBlock Margin="16,2,0,0" FontSize="10" Text="{Binding ReactionMz, StringFormat='Mz: {0:F2} kN·m'}" />
    </StackPanel>
</Border>
```

---

## 7. Analysis Progress Indicator

```xml
<!-- XAML -->
<StackPanel Visibility="{Binding IsAnalyzing, Converter={StaticResource BoolToVisibilityConverter}}" 
            VerticalAlignment="Center" HorizontalAlignment="Center">
    <!-- Spinning Circle -->
    <Border Width="48" Height="48" CornerRadius="24" 
            BorderBrush="{StaticResource PrimaryBlueBrush}" BorderThickness="4" 
            Margin="0,0,0,16">
        <Border.Effect>
            <!-- Rotation animation will be applied via C# code-behind -->
        </Border.Effect>
    </Border>

    <!-- Progress Text -->
    <TextBlock Text="Analyzing Structure..." FontSize="14" FontWeight="SemiBold" 
               Foreground="{StaticResource TextPrimaryBrush}" TextAlignment="Center" />
    
    <TextBlock Text="{Binding AnalysisProgress, StringFormat='Progress: {0:P}'}" 
               FontSize="11" Foreground="{StaticResource TextSecondaryBrush}" 
               TextAlignment="Center" Margin="0,8,0,0" />
</StackPanel>

<!-- Cancel Button -->
<Button Content="Cancel" Style="{StaticResource PrimaryButtonStyle}" 
        Command="{Binding CancelAnalysisCommand}"
        Visibility="{Binding IsAnalyzing, Converter={StaticResource BoolToVisibilityConverter}}"
        Margin="0,8,0,0" Width="120" HorizontalAlignment="Center" />
```

---

## 8. Keyboard Shortcuts Reference

```csharp
// C# - Register Key Bindings in MainWindow.xaml.cs
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        RegisterKeyBindings();
    }

    private void RegisterKeyBindings()
    {
        // Ctrl+N → New Project
        CommandBindings.Add(new CommandBinding(ApplicationCommands.New, NewProject_Executed));
        
        // Ctrl+O → Open
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Open, OpenProject_Executed));
        
        // Ctrl+S → Save
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Save, SaveProject_Executed));
        
        // F5 → Run Analysis
        InputBindings.Add(new KeyBinding(
            new RelayCommand(() => RunAnalysis()),
            new KeyGesture(Key.F5)
        ));

        // Delete → Delete Selected
        InputBindings.Add(new KeyBinding(
            new RelayCommand(() => DeleteSelected()),
            new KeyGesture(Key.Delete)
        ));
    }

    private void NewProject_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        (DataContext as MainViewModel)?.NewProjectCommand.Execute(null);
    }

    // ... other handlers
}
```

---

## 9. Tooltips for Technical Terms

```xml
<!-- XAML -->
<TextBlock Text="Safety Factor:" ToolTip="SF = Yield Strength / Applied Stress. SF > 1.5 is safe, SF &lt; 1.0 is unsafe." />

<TextBlock Text="Deflection:" ToolTip="Maximum displacement of member under load. Limited by L/240 for beams per AISC." />

<Button Content="?" Foreground="{StaticResource BrightBlueBrush}" FontWeight="Bold" Width="20" Height="20" 
        ToolTip="Click for detailed help on this analysis step." />
```

---

## 10. Accessibility Attributes

```xml
<!-- XAML with Accessibility -->
<Button Content="Run Analysis" 
        Style="{StaticResource PrimaryButtonStyle}"
        AutomationProperties.Name="Run Structural Analysis"
        AutomationProperties.HelpText="Executes the finite element analysis solver on the current structure definition. Shortcut: F5">
    <Button.ToolTip>
        <ToolTip Content="Shortcut: F5" />
    </Button.ToolTip>
</Button>

<TextBox Style="{StaticResource StructuredTextBoxStyle}"
         Text="{Binding BeamDepth}"
         AutomationProperties.LabeledBy="{Binding ElementName=BeamDepthLabel}"
         x:Name="BeamDepthInput" />

<TextBlock x:Name="BeamDepthLabel" Text="Beam Depth (mm):" />
```

---

## 11. Export to PDF/Excel

```csharp
// C# - Export Results Service
public class ExportService
{
    public void ExportResultsToPdf(Structure structure, AnalysisResults results, string filePath)
    {
        // Use iTextSharp or PDFSharp
        // Include:
        // - Title, date, author
        // - Input summary (members, loads, supports)
        // - Material and section properties
        // - Analysis results (stresses, deflections, reactions)
        // - Color-coded results table
        // - Assumptions and limitations
    }

    public void ExportResultsToExcel(AnalysisResults results, string filePath)
    {
        // Use EPPlus or ClosedXML
        // Sheets:
        // - Summary (project info)
        // - Input Data (members, loads, supports)
        // - Results (stresses, deflections, reactions)
        // - Visualization (charts of stress distribution)
    }
}
```

---

**Quick Reference Version:** 1.0  
**Last Updated:** 2026-09-05
