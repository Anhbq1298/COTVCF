# WPF Implementation Guide for Structural Engineering Tools

This guide provides step-by-step instructions for implementing the Structural Engineering Design System in WPF.

---

## 1. Project Setup & Dependencies

### Create New WPF Project
```bash
dotnet new wpf -n StructuralEngineeringTool
cd StructuralEngineeringTool
```

### Add Required NuGet Packages
```bash
dotnet add package MahApps.Metro
dotnet add package HandyControl
dotnet add package Extended.Wpf.Toolkit
dotnet add package OxyPlot.Wpf
dotnet add package HelixToolkit.Wpf
dotnet add package CommunityToolkit.Mvvm
dotnet add package Prism.Wpf
dotnet add package MaterialDesignInXamlToolkit
```

### Package.json (Alternative with NPM)
```xml
<!-- In .csproj file -->
<ItemGroup>
  <PackageReference Include="MahApps.Metro" Version="2.4.10" />
  <PackageReference Include="HandyControl" Version="3.5.1" />
  <PackageReference Include="OxyPlot.Wpf" Version="2.1.2" />
  <PackageReference Include="HelixToolkit.Wpf" Version="2.22.0" />
  <PackageReference Include="CommunityToolkit.Mvvm" Version="8.2.2" />
  <PackageReference Include="Prism.Wpf" Version="9.0.145" />
</ItemGroup>
```

---

## 2. Color & Style Resources

### Create `Resources/Themes/Colors.xaml`

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <!-- PRIMARY COLORS -->
    <Color x:Key="PrimaryBlue">#1F4788</Color>
    <Color x:Key="StructuralGreen">#2E7D32</Color>
    <Color x:Key="EngineeringOrange">#F57C00</Color>
    <Color x:Key="CriticalRed">#C62828</Color>
    <Color x:Key="CleanWhite">#FAFAFA</Color>
    <Color x:Key="DarkGray">#424242</Color>
    <Color x:Key="LightGray">#EEEEEE</Color>
    <Color x:Key="BrightBlue">#0288D1</Color>

    <!-- TEXT COLORS -->
    <Color x:Key="TextPrimary">#1A1A1A</Color>
    <Color x:Key="TextSecondary">#666666</Color>
    <Color x:Key="TextDisabled">#999999</Color>

    <!-- SURFACE COLORS -->
    <Color x:Key="SurfaceL0">#FFFFFF</Color>
    <Color x:Key="SurfaceL1">#F5F5F5</Color>
    <Color x:Key="SurfaceL2">#EEEEEE</Color>
    <Color x:Key="SurfaceL3">#E0E0E0</Color>

    <!-- BRUSHES -->
    <SolidColorBrush x:Key="PrimaryBlueBrush" Color="{StaticResource PrimaryBlue}" />
    <SolidColorBrush x:Key="StructuralGreenBrush" Color="{StaticResource StructuralGreen}" />
    <SolidColorBrush x:Key="EngineeringOrangeBrush" Color="{StaticResource EngineeringOrange}" />
    <SolidColorBrush x:Key="CriticalRedBrush" Color="{StaticResource CriticalRed}" />
    <SolidColorBrush x:Key="TextPrimaryBrush" Color="{StaticResource TextPrimary}" />
    <SolidColorBrush x:Key="TextSecondaryBrush" Color="{StaticResource TextSecondary}" />
    <SolidColorBrush x:Key="SurfaceL0Brush" Color="{StaticResource SurfaceL0}" />
    <SolidColorBrush x:Key="SurfaceL1Brush" Color="{StaticResource SurfaceL1}" />

    <!-- SHADOWS (Via Effects) -->
    <DropShadowEffect x:Key="ShadowL1" BlurRadius="4" ShadowDepth="2" Opacity="0.1" />
    <DropShadowEffect x:Key="ShadowL2" BlurRadius="8" ShadowDepth="4" Opacity="0.12" />
    <DropShadowEffect x:Key="ShadowL3" BlurRadius="16" ShadowDepth="8" Opacity="0.15" />

</ResourceDictionary>
```

### Create `Resources/Themes/Styles.xaml`

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:local="clr-namespace:StructuralEngineeringTool">

    <ResourceDictionary.MergedDictionaries>
        <ResourceDictionary Source="Colors.xaml" />
    </ResourceDictionary.MergedDictionaries>

    <!-- BUTTON STYLE -->
    <Style x:Key="PrimaryButtonStyle" TargetType="Button">
        <Setter Property="Background" Value="{StaticResource PrimaryBlueBrush}" />
        <Setter Property="Foreground" Value="White" />
        <Setter Property="Height" Value="36" />
        <Setter Property="Padding" Value="16,8" />
        <Setter Property="FontSize" Value="13" />
        <Setter Property="FontWeight" Value="SemiBold" />
        <Setter Property="FontFamily" Value="Segoe UI" />
        <Setter Property="BorderThickness" Value="0" />
        <Setter Property="CornerRadius" Value="2" />
        <Setter Property="Cursor" Value="Hand" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="Button">
                    <Border Background="{TemplateBinding Background}" 
                            BorderThickness="{TemplateBinding BorderThickness}"
                            CornerRadius="2" Padding="{TemplateBinding Padding}">
                        <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" />
                    </Border>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
        <Style.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
                <Setter Property="Background">
                    <Setter.Value>
                        <SolidColorBrush Color="#163655" />
                    </Setter.Value>
                </Setter>
            </Trigger>
            <Trigger Property="IsPressed" Value="True">
                <Setter Property="Background">
                    <Setter.Value>
                        <SolidColorBrush Color="#0F2341" />
                    </Setter.Value>
                </Setter>
            </Trigger>
            <Trigger Property="IsEnabled" Value="False">
                <Setter Property="Background" Value="{StaticResource SurfaceL2Brush}" />
                <Setter Property="Foreground" Value="{StaticResource TextDisabledBrush}" />
                <Setter Property="Cursor" Value="No" />
            </Trigger>
        </Style.Triggers>
    </Style>

    <!-- TEXTBOX STYLE -->
    <Style x:Key="StructuredTextBoxStyle" TargetType="TextBox">
        <Setter Property="Height" Value="36" />
        <Setter Property="Padding" Value="12,8" />
        <Setter Property="FontSize" Value="13" />
        <Setter Property="FontFamily" Value="Segoe UI" />
        <Setter Property="Foreground" Value="{StaticResource TextPrimaryBrush}" />
        <Setter Property="Background" Value="{StaticResource SurfaceL0Brush}" />
        <Setter Property="BorderBrush" Value="{StaticResource SurfaceL3Brush}" />
        <Setter Property="BorderThickness" Value="1" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="TextBox">
                    <Border Background="{TemplateBinding Background}" 
                            BorderBrush="{TemplateBinding BorderBrush}"
                            BorderThickness="{TemplateBinding BorderThickness}"
                            Padding="{TemplateBinding Padding}">
                        <ScrollViewer x:Name="PART_ContentHost" Focusable="False" />
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter Property="BorderBrush" Value="{StaticResource PrimaryBlueBrush}" />
                            <Setter Property="BorderThickness" Value="1" />
                        </Trigger>
                        <Trigger Property="IsFocused" Value="True">
                            <Setter Property="BorderBrush" Value="{StaticResource PrimaryBlueBrush}" />
                            <Setter Property="BorderThickness" Value="2" />
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter Property="Background" Value="{StaticResource SurfaceL2Brush}" />
                            <Setter Property="Foreground" Value="{StaticResource TextDisabledBrush}" />
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- DATAGRID STYLE -->
    <Style x:Key="StructuredDataGridStyle" TargetType="DataGrid">
        <Setter Property="Background" Value="{StaticResource SurfaceL0Brush}" />
        <Setter Property="BorderBrush" Value="{StaticResource SurfaceL3Brush}" />
        <Setter Property="BorderThickness" Value="1" />
        <Setter Property="RowHeight" Value="36" />
        <Setter Property="HeadersHeight" Value="40" />
        <Setter Property="Foreground" Value="{StaticResource TextPrimaryBrush}" />
        <Setter Property="FontFamily" Value="Segoe UI" />
        <Setter Property="FontSize" Value="13" />
        <Setter Property="AlternatingRowBackground" Value="{StaticResource SurfaceL1Brush}" />
        <Setter Property="CanUserResizeRows" Value="False" />
    </Style>

</ResourceDictionary>
```

---

## 3. MVVM Architecture Setup

### Create `Models/Structure.cs`

```csharp
using System.Collections.ObjectModel;

namespace StructuralEngineeringTool.Models
{
    public class Structure
    {
        public string Name { get; set; }
        public ObservableCollection<Member> Members { get; set; }
        public ObservableCollection<Node> Nodes { get; set; }
        public ObservableCollection<LoadCase> LoadCases { get; set; }
        public ObservableCollection<Support> Supports { get; set; }

        public Structure()
        {
            Members = new ObservableCollection<Member>();
            Nodes = new ObservableCollection<Node>();
            LoadCases = new ObservableCollection<LoadCase>();
            Supports = new ObservableCollection<Support>();
        }
    }

    public class Member
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public Node StartNode { get; set; }
        public Node EndNode { get; set; }
        public string Material { get; set; } // "A36 Steel", "Grade 60 Concrete", etc.
        public string Section { get; set; }  // "W12x26", "C 15x40", etc.
        public double Length { get; set; }  // in mm
    }

    public class Node
    {
        public int Id { get; set; }
        public double X { get; set; }  // X coordinate (mm)
        public double Y { get; set; }  // Y coordinate (mm)
        public double Z { get; set; }  // Z coordinate (mm)
    }

    public class LoadCase
    {
        public string Name { get; set; }
        public ObservableCollection<Load> Loads { get; set; }

        public LoadCase()
        {
            Loads = new ObservableCollection<Load>();
        }
    }

    public class Load
    {
        public Node ApplicationNode { get; set; }
        public double Fx { get; set; }  // Force X (kN)
        public double Fy { get; set; }  // Force Y (kN)
        public double Fz { get; set; }  // Force Z (kN)
        public string Description { get; set; }
    }

    public class Support
    {
        public Node SupportNode { get; set; }
        public bool RestrictX { get; set; }
        public bool RestrictY { get; set; }
        public bool RestrictZ { get; set; }
        public string Type { get; set; } // "Fixed", "Pinned", "Roller"
    }
}
```

### Create `ViewModels/MainViewModel.cs`

```csharp
using System;
using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StructuralEngineeringTool.Models;

namespace StructuralEngineeringTool.ViewModels
{
    public class MainViewModel : ObservableObject
    {
        private Structure _currentStructure;
        private string _projectName;
        private string _statusMessage;

        public Structure CurrentStructure
        {
            get => _currentStructure;
            set => SetProperty(ref _currentStructure, value);
        }

        public string ProjectName
        {
            get => _projectName;
            set => SetProperty(ref _projectName, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public ICommand NewProjectCommand { get; }
        public ICommand OpenProjectCommand { get; }
        public ICommand SaveProjectCommand { get; }
        public ICommand RunAnalysisCommand { get; }
        public ICommand ExportReportCommand { get; }

        public MainViewModel()
        {
            CurrentStructure = new Structure { Name = "New Structure" };
            ProjectName = "Untitled Project";
            StatusMessage = "Ready";

            NewProjectCommand = new RelayCommand(NewProject);
            OpenProjectCommand = new RelayCommand(OpenProject);
            SaveProjectCommand = new RelayCommand(SaveProject);
            RunAnalysisCommand = new RelayCommand(RunAnalysis);
            ExportReportCommand = new RelayCommand(ExportReport);
        }

        private void NewProject()
        {
            CurrentStructure = new Structure { Name = "New Structure" };
            ProjectName = "Untitled Project";
            StatusMessage = "New project created";
        }

        private void OpenProject()
        {
            StatusMessage = "Opening project...";
            // Implement file dialog and load logic
        }

        private void SaveProject()
        {
            StatusMessage = "Project saved";
            // Implement save logic
        }

        private void RunAnalysis()
        {
            StatusMessage = "Analyzing...";
            // Call solver asynchronously
            System.Threading.Tasks.Task.Run(() =>
            {
                System.Threading.Thread.Sleep(2000); // Simulate solver time
                StatusMessage = "Analysis complete";
            });
        }

        private void ExportReport()
        {
            StatusMessage = "Exporting report...";
            // Implement PDF/Excel export
        }
    }
}
```

---

## 4. Main Window Layout

### Create `Views/MainWindow.xaml`

```xml
<Window x:Class="StructuralEngineeringTool.Views.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:local="clr-namespace:StructuralEngineeringTool"
        Title="Structural Engineering Design Tool"
        Width="1400"
        Height="900"
        Background="{StaticResource SurfaceL0Brush}"
        Foreground="{StaticResource TextPrimaryBrush}"
        FontFamily="Segoe UI"
        FontSize="13">

    <Window.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <ResourceDictionary Source="/Resources/Themes/Colors.xaml" />
                <ResourceDictionary Source="/Resources/Themes/Styles.xaml" />
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </Window.Resources>

    <DockPanel>
        <!-- MENU BAR -->
        <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Background="{StaticResource SurfaceL1Brush}" 
                    Height="40" Margin="0" Padding="8">
            <Button Content="New Project" Style="{StaticResource PrimaryButtonStyle}" Margin="0,0,8,0"
                    Command="{Binding NewProjectCommand}" />
            <Button Content="Open" Style="{StaticResource PrimaryButtonStyle}" Margin="0,0,8,0"
                    Command="{Binding OpenProjectCommand}" />
            <Button Content="Save" Style="{StaticResource PrimaryButtonStyle}" Margin="0,0,8,0"
                    Command="{Binding SaveProjectCommand}" />
            <Separator Style="{StaticResource {x:Static ToolBar.SeparatorStyleKey}}" Margin="8,0" />
            <Button Content="Run Analysis (F5)" Style="{StaticResource PrimaryButtonStyle}" Margin="0,0,8,0"
                    Command="{Binding RunAnalysisCommand}" Foreground="White" />
            <Button Content="Export Report" Style="{StaticResource PrimaryButtonStyle}" Margin="0,0,8,0"
                    Command="{Binding ExportReportCommand}" />
        </StackPanel>

        <!-- STATUS BAR -->
        <StatusBar DockPanel.Dock="Bottom" Background="{StaticResource SurfaceL1Brush}" Height="28" Padding="8">
            <StatusBarItem>
                <TextBlock Text="{Binding StatusMessage}" Foreground="{StaticResource TextPrimaryBrush}" />
            </StatusBarItem>
        </StatusBar>

        <!-- MAIN CONTENT AREA -->
        <Grid>
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="250" />
                <ColumnDefinition Width="Auto" />
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="Auto" />
                <ColumnDefinition Width="280" />
            </Grid.ColumnDefinitions>

            <!-- LEFT PANEL: Project Tree -->
            <Border Grid.Column="0" Background="{StaticResource SurfaceL1Brush}" BorderBrush="{StaticResource SurfaceL3Brush}" 
                    BorderThickness="0,0,1,0">
                <StackPanel Margin="8">
                    <TextBlock Text="Project Tree" FontSize="13" FontWeight="SemiBold" Margin="0,0,0,8" 
                               Foreground="{StaticResource TextPrimaryBrush}" />
                    <!-- TreeView for structure elements will go here -->
                </StackPanel>
            </Border>

            <!-- SPLITTER 1 -->
            <GridSplitter Grid.Column="1" Width="4" HorizontalAlignment="Center" VerticalAlignment="Stretch" 
                          Background="{StaticResource SurfaceL2Brush}" Cursor="SizeWE" />

            <!-- CENTER PANEL: 3D Canvas -->
            <Border Grid.Column="2" Background="{StaticResource SurfaceL0Brush}" BorderBrush="{StaticResource SurfaceL3Brush}" 
                    BorderThickness="0">
                <StackPanel Margin="16" VerticalAlignment="Center" HorizontalAlignment="Center">
                    <TextBlock Text="3D Canvas Area" FontSize="16" FontWeight="SemiBold" 
                               Foreground="{StaticResource TextSecondaryBrush}" />
                    <TextBlock Text="(OpenGL / HelixToolkit viewport will render here)" FontSize="13" 
                               Foreground="{StaticResource TextSecondaryBrush}" Margin="0,8,0,0" />
                </StackPanel>
            </Border>

            <!-- SPLITTER 2 -->
            <GridSplitter Grid.Column="3" Width="4" HorizontalAlignment="Center" VerticalAlignment="Stretch" 
                          Background="{StaticResource SurfaceL2Brush}" Cursor="SizeWE" />

            <!-- RIGHT PANEL: Properties -->
            <Border Grid.Column="4" Background="{StaticResource SurfaceL1Brush}" BorderBrush="{StaticResource SurfaceL3Brush}" 
                    BorderThickness="1,0,0,0">
                <ScrollViewer>
                    <StackPanel Margin="12">
                        <TextBlock Text="Properties" FontSize="13" FontWeight="SemiBold" Margin="0,0,0,12" 
                                   Foreground="{StaticResource TextPrimaryBrush}" />
                        <!-- Property inputs will go here -->
                        <TextBlock Text="Select an element to view/edit properties" FontSize="11" 
                                   Foreground="{StaticResource TextSecondaryBrush}" />
                    </StackPanel>
                </ScrollViewer>
            </Border>
        </Grid>
    </DockPanel>

</Window>
```

### Create `Views/MainWindow.xaml.cs`

```csharp
using System.Windows;
using StructuralEngineeringTool.ViewModels;

namespace StructuralEngineeringTool.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainViewModel();
        }
    }
}
```

---

## 5. Input Validation & Converters

### Create `Converters/StringFormatConverter.cs`

```csharp
using System;
using System.Globalization;
using System.Windows.Data;

namespace StructuralEngineeringTool.Converters
{
    public class StringFormatConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double d && parameter is string format)
            {
                return d.ToString(format, culture);
            }
            return value?.ToString() ?? "";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string s && double.TryParse(s, out double result))
            {
                return result;
            }
            return null;
        }
    }
}
```

### Create `Services/InputValidator.cs`

```csharp
using System;

namespace StructuralEngineeringTool.Services
{
    public class InputValidator
    {
        // Validate section depth (e.g., W12x26 → depth = 12")
        public static bool ValidateSectionDepth(double depth, double memberLength)
        {
            if (depth <= 0) return false;
            if (depth > memberLength / 2) return false; // Section shouldn't be more than half member length
            return true;
        }

        // Validate load values
        public static bool ValidateLoad(double loadValue)
        {
            return !double.IsNaN(loadValue) && !double.IsInfinity(loadValue);
        }

        // Validate coordinate
        public static bool ValidateCoordinate(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0;
        }

        // Get user-friendly error message
        public static string GetErrorMessage(string fieldName, string validationRule)
        {
            return $"{fieldName}: {validationRule}";
        }
    }
}
```

---

## 6. Build Configuration

### Project Settings (.csproj)
The project is configured with the following build rules:

```xml
<!-- Platform: .NET 8.0 -->
<TargetFramework>net8.0</TargetFramework>

<!-- Output: bin\Debug\ConsoleApp1.exe -->
<OutputPath>bin\Debug\</OutputPath>

<!-- Runtime: Windows 64-bit -->
<RuntimeIdentifier>win-x64</RuntimeIdentifier>

<!-- Configuration: Debug Mode -->
<Configuration>Debug</Configuration>
<DebugSymbols>true</DebugSymbols>
<DebugType>full</DebugType>
```

**Build Output:**
- **Debug Build:** `bin\Debug\ConsoleApp1.exe`
- **Release Build:** `bin\Release\ConsoleApp1.exe` (with optimizations)
- **Intermediate Files:** `obj\Debug\` or `obj\Release\`

### Build & Run
```bash
# Clean previous builds
dotnet clean

# Build Debug configuration
dotnet build -c Debug

# Run the executable
dotnet run

# Build Release (optimized)
dotnet build -c Release

# Run Release build directly
bin\Release\ConsoleApp1.exe
```

### Build Rules Summary
| Property | Value |
|----------|-------|
| **Target Framework** | .NET 8.0 |
| **Platform** | Windows 64-bit (win-x64) |
| **Output Folder** | `bin\Debug\` |
| **Executable** | `ConsoleApp1.exe` |
| **Output Type** | Console Application |
| **Language Version** | Latest C# |

### Key Files Summary
```
StructuralEngineeringTool/
├── Models/
│   └── Structure.cs                 (domain models)
├── ViewModels/
│   └── MainViewModel.cs             (MVVM)
├── Views/
│   ├── MainWindow.xaml              (UI layout)
│   └── MainWindow.xaml.cs           (code-behind minimal)
├── Services/
│   └── InputValidator.cs            (validation)
├── Converters/
│   └── StringFormatConverter.cs      (data binding)
├── Resources/
│   └── Themes/
│       ├── Colors.xaml              (color palette)
│       └── Styles.xaml              (component styles)
├── App.xaml                         (app root)
└── App.xaml.cs                      (app startup)
```

---

## 7. Next Steps

1. **Implement 3D Canvas:** Add HelixToolkit viewport for structure visualization
2. **Build Analysis Solver:** Integrate FEA solver (custom or third-party)
3. **Create Result Visualizations:** Stress distribution, deformation plots
4. **Add File I/O:** Save/load projects in structured format (JSON, XML)
5. **Implement Export:** PDF reports, Excel results tables
6. **Add Help System:** Tooltips, documentation integration
7. **Performance Optimization:** Async solver, GPU rendering

---

**Implementation Version:** 1.0  
**Framework:** WPF (.NET Framework)  
**Design System:** Structural Engineering UI/UX Pro Max
