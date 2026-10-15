using System.Collections.Immutable;
using Mcotis.Core;

namespace Mcotis.Application;

/// <summary>Positive line loads act downwards (-global Z) on every floor beam in the stated direction.</summary>
public sealed record BuildingStorey(double Height, double BeamXLineLoad, double BeamYLineLoad);
public sealed record TemplateBuildingInput(ImmutableArray<double> XBayWidths, ImmutableArray<double> YBayWidths,
    ImmutableArray<BuildingStorey> Storeys, MaterialDefinition Material, FrameSectionDefinition ColumnSection,
    FrameSectionDefinition BeamSection, Restraint BaseRestraint, bool IncludeSelfWeight = false);

/// <summary>A new, connected frame grid, created as one command in an empty geometry document. No slabs or diaphragms.
/// Every beam is split at a grid point; every column is split at a floor. Native units: kN, m, t.</summary>
public static class TemplateBuilding
{
    public const int MaximumPoints = 400;

    public static IDocumentCommand Create(TemplateBuildingInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.XBayWidths.IsDefault || input.YBayWidths.IsDefault || input.Storeys.IsDefaultOrEmpty ||
            input.XBayWidths.Length + input.YBayWidths.Length == 0)
            throw new ArgumentException("Provide at least one bay direction and one storey.");
        if (input.XBayWidths.Length > MaximumPoints || input.YBayWidths.Length > MaximumPoints || input.Storeys.Length > MaximumPoints ||
            (long)(input.XBayWidths.Length + 1) * (input.YBayWidths.Length + 1) * (input.Storeys.Length + 1) > MaximumPoints)
            throw new ArgumentException($"This template supports at most {MaximumPoints} points. Reduce the bay or storey counts.");
        if (input.XBayWidths.Concat(input.YBayWidths).Any(v => !double.IsFinite(v) || v <= Tolerances.Length) ||
            input.Storeys.Any(s => !double.IsFinite(s.Height) || s.Height <= Tolerances.Length ||
                !double.IsFinite(s.BeamXLineLoad) || s.BeamXLineLoad < 0 || !double.IsFinite(s.BeamYLineLoad) || s.BeamYLineLoad < 0))
            throw new ArgumentException("Widths and heights must be finite and positive; downward line loads must be finite and nonnegative.");
        if (!double.IsFinite(input.XBayWidths.Sum()) || !double.IsFinite(input.YBayWidths.Sum()) ||
            !double.IsFinite(input.Storeys.Sum(s => s.Height)))
            throw new ArgumentException("The total model dimensions overflow.");
        if (input.ColumnSection.MaterialId != input.Material.Id || input.BeamSection.MaterialId != input.Material.Id)
            throw new ArgumentException("Both sections must reference the template material.");

        return DocumentCommands.Edit("TemplateBuilding", editor =>
        {
            if (editor.Points.Count + editor.Frames.Count + editor.Cables.Count + editor.Shells.Count != 0)
                throw new InvalidOperationException("Create a template in a new model workspace; existing geometry is preserved.");
            editor.PutMaterial(input.Material);
            editor.PutFrameSection(input.ColumnSection);
            editor.PutFrameSection(input.BeamSection);
            var pattern = new LoadPattern(ObjectId.New(), "BuildingLoad", LoadPatternType.Other, input.IncludeSelfWeight ? 1 : 0);
            editor.PutLoadPattern(pattern);
            editor.PutLoadCase(new(ObjectId.New(), "BuildingStatic", LoadCaseType.LinearStatic, [new(pattern.Id, 1)]));
            editor.SetSettings(AnalysisSettings.Default with
            {
                ActiveDofs = input.YBayWidths.Length == 0 ? DofMask.XZ : input.XBayWidths.Length == 0 ? DofMask.YZ : DofMask.Full3D
            });
            int nx = input.XBayWidths.Length, ny = input.YBayWidths.Length, ns = input.Storeys.Length;
            var points = new ObjectId[ns + 1, nx + 1, ny + 1];
            double z = 0;
            for (int floor = 0; floor <= ns; floor++)
            {
                double x = 0;
                for (int ix = 0; ix <= nx; ix++)
                {
                    double y = 0;
                    for (int iy = 0; iy <= ny; iy++)
                    {
                        var key = $"P_{floor}_{ix}_{iy}";
                        var p = editor.AddPoint(new(x, y, z), key, origin: new(OriginKind.Generated, "TemplateBuilding/" + key));
                        points[floor, ix, iy] = p.Id;
                        if (floor == 0) editor.SetRestraint([p.Id], input.BaseRestraint);
                        if (iy < ny) y += input.YBayWidths[iy];
                    }
                    if (ix < nx) x += input.XBayWidths[ix];
                }
                if (floor < ns) z += input.Storeys[floor].Height;
            }
            void Frame(ObjectId start, ObjectId end, string key, ObjectId section, double q = 0)
            {
                var f = editor.AddFrame(start, end, key, origin: new(OriginKind.Generated, "TemplateBuilding/" + key));
                editor.SetFrameSection([f.Id], section);
                if (q != 0) editor.PutLoad(new FrameDistributedLoad(ObjectId.New(), pattern.Id, f.Id, LoadDirection.GlobalZ, 0, 1, -q, -q));
            }
            for (int floor = 1; floor <= ns; floor++)
                for (int ix = 0; ix <= nx; ix++)
                    for (int iy = 0; iy <= ny; iy++)
                    {
                        Frame(points[floor - 1, ix, iy], points[floor, ix, iy], $"Column_{floor}_{ix}_{iy}", input.ColumnSection.Id);
                        if (ix < nx) Frame(points[floor, ix, iy], points[floor, ix + 1, iy], $"BeamX_{floor}_{ix}_{iy}", input.BeamSection.Id, input.Storeys[floor - 1].BeamXLineLoad);
                        if (iy < ny) Frame(points[floor, ix, iy], points[floor, ix, iy + 1], $"BeamY_{floor}_{ix}_{iy}", input.BeamSection.Id, input.Storeys[floor - 1].BeamYLineLoad);
                    }
            // the grid and the storeys the building is laid out on, as references to draw and navigate on (no analysis input)
            editor.SetReferenceGeometry(new(
                [new GridSystem("GLOBAL", new(0, 0, 0), 0, GeometryEditService.LinesFromSpacings(true, 0, input.XBayWidths),
                    GeometryEditService.LinesFromSpacings(false, 0, input.YBayWidths))],
                GeometryEditService.LevelsFromHeights(0, [.. input.Storeys.Select(storey => storey.Height)])));
        });
    }
}
