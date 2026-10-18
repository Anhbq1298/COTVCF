using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace Mcotis.Desktop.Application.Design;

/// <summary>The stay systems Design → Tension Cable Design chooses from.</summary>
public enum CableSystem { VslSsi2000, FatzerFlc, UtraconPt }

/// <summary>What the tension being designed for is: it only changes the cautions the design prints, never the screen.</summary>
public enum CableForceBasis { Service, Ultimate, Unknown }

/// <summary>How the candidates that pass the screen are ranked.</summary>
public enum CablePriority { LowestCost, FewestUnits, LeastSteel }

/// <summary>
/// One standard unit of a stay system: a VSL 6-N strand group, a FATZER full-locked-coil rope or a Utracon 37-strand tendon.
/// A stay is <c>n</c> identical units in parallel.
/// </summary>
/// <param name="BreakingLoadKN">Minimum / guaranteed breaking load of one unit [kN].</param>
/// <param name="MassKgPerM">Steel (or rope) mass per metre of one unit [kg/m].</param>
/// <param name="AxialStiffnessKN">EA of one unit [kN].</param>
/// <param name="ConduitMm">HDPE pipe or duct outside diameter, or the rope diameter [mm].</param>
/// <param name="Strands">Number of 7-wire strands of the unit; 0 for a rope.</param>
/// <param name="LimitTensionKN">The supplier's published EN 1993-1-11 limit tension F_Rd of one unit [kN] (FATZER ropes); 0 where none is published (VSL publishes 50 % / 60 % admissible values, Utracon strand data).</param>
public sealed record CableUnit(CableSystem System, string Code, string Name, double BreakingLoadKN, double SteelAreaMm2, double MassKgPerM,
    double AxialStiffnessKN, double ConduitMm, int Strands, double LimitTensionKN = 0);

/// <summary>The catalogue of the Technical Options Study of 18 Oct 2026 (VSL SSI 2000, FATZER FLC, Utracon UPS).</summary>
public static class TensionCableCatalogue
{
    /// <summary>Breaking load of one Ø15.7 mm 7-wire strand, 1860 MPa [kN].</summary>
    public const double StrandBreakingKN = 279;
    public const double StrandAreaMm2 = 150;
    public const double StrandMassKgPerM = 1.172;
    /// <summary>E of the strand [MPa].</summary>
    public const double StrandModulusMpa = 195_000;

    // VSL Stay Cable Systems Technical Catalogue (2018), SSI 2000: strands per unit and standard pipe outside diameter [mm]
    private static readonly (int Strands, double Pipe)[] Vsl =
    [
        (12, 125), (19, 140), (22, 160), (27, 160), (31, 160), (37, 180), (43, 200), (55, 200), (61, 225), (73, 250), (85, 250),
        (91, 280), (109, 315), (127, 315), (139, 315), (151, 355), (169, 355), (187, 400),
    ];

    // FATZER Structural Ropes planning aid (05/2023) p. 20, galvanized full locked coil rope, E = 160 GPa: the five sizes of the study —
    // diameter [mm], minimum breaking force Fmin [kN], limit tension FRd = Fuk / 1.5 [kN] (EN 1993-1-11), EA [MN], weight [kg/m]
    private static readonly (double D, double Fmin, double FRd, double Ea, double Mass)[] Fatzer =
    [
        (80, 6_390, 4_260, 704, 38), (100, 10_100, 6_733, 1_089, 58), (120, 14_500, 9_667, 1_566, 84), (135, 17_400, 11_600, 1_979, 106), (150, 23_000, 15_333, 2_525, 131),
    ];

    public static IReadOnlyList<CableUnit> All { get; } = Build();

    public static IReadOnlyList<CableUnit> Units(CableSystem system) => All.Where(unit => unit.System == system).ToArray();

    public static string Title(CableSystem system) => system switch
    {
        CableSystem.VslSsi2000 => "VSL SSI 2000 (parallel strand stay)",
        CableSystem.FatzerFlc => "FATZER full-locked-coil rope",
        _ => "Utracon / DSI-UPS 37-strand PT tendon (unqualified as a stay)",
    };

    public static string ShortTitle(CableSystem system) => system switch
    {
        CableSystem.VslSsi2000 => "VSL SSI 2000",
        CableSystem.FatzerFlc => "FATZER FLC",
        _ => "Utracon PT",
    };

    /// <summary>True for a system the study says is qualified as a permanent exposed stay; the Utracon PT tendon is not.</summary>
    public static bool IsQualifiedStay(CableSystem system) => system != CableSystem.UtraconPt;

    private static List<CableUnit> Build()
    {
        var list = new List<CableUnit>();
        foreach (var (strands, pipe) in Vsl)
            list.Add(new(CableSystem.VslSsi2000, $"6-{strands}", $"VSL SSI 2000 6-{strands}", strands * StrandBreakingKN, strands * StrandAreaMm2,
                strands * StrandMassKgPerM, strands * StrandAreaMm2 * StrandModulusMpa / 1000, pipe, strands));
        foreach (var rope in Fatzer)
            list.Add(new(CableSystem.FatzerFlc, $"Ø{rope.D:0}", $"FATZER FLC Ø{rope.D:0}", rope.Fmin, double.NaN, rope.Mass, rope.Ea * 1000, rope.D, 0, rope.FRd));
        list.Add(new(CableSystem.UtraconPt, "37-strand", "Utracon PT 37 × Ø15.7", 37 * StrandBreakingKN, 37 * StrandAreaMm2, 37 * StrandMassKgPerM,
            37 * StrandAreaMm2 * StrandModulusMpa / 1000, 135, 37));
        return list;
    }
}

/// <summary>
/// The indicative rates of the study's cost build-up (Singapore, SGD, excluding GST and the supporting anchorage-box steelwork). They are
/// the study author's budgeting assumptions, not supplier quotations: the user edits them in the Design window.
/// </summary>
public sealed record TensionCableRates
{
    public double VslStrandSgdPerKg { get; init; } = 17;
    public double VslPipeSgdPerMmPerM { get; init; } = 0.75;
    public double VslEndBaseSgd { get; init; } = 12_000;
    public double VslEndPerStrandSgd { get; init; } = 220;
    public double VslInstallSgdPerM { get; init; } = 150;
    public double FatzerRopeSgdPerKg { get; init; } = 42;
    public double FatzerEndBaseSgd { get; init; } = 9_000;
    public double FatzerEndPerMm2Sgd { get; init; } = 3;
    public double FatzerInstallSgdPerM { get; init; } = 180;
    public double UtraconStrandSgdPerKg { get; init; } = 13.5;
    public double UtraconDuctSgdPerM { get; init; } = 180;
    public double UtraconEndBaseSgd { get; init; } = 7_000;
    public double UtraconEndPerStrandSgd { get; init; } = 125;
    public double UtraconInstallSgdPerM { get; init; } = 120;
    public double ContingencyPercent { get; init; }
    /// <summary>Singapore GST added to the cost [%]; 0 leaves it out.</summary>
    public double GstPercent { get; init; }

    /// <summary>One editable rate: key, label, unit, how it is read and written.</summary>
    public sealed record Entry(string Key, string Label, string Unit, Func<TensionCableRates, double> Get, Func<TensionCableRates, double, TensionCableRates> Set);

    public static IReadOnlyList<Entry> Entries { get; } =
    [
        new("vslStrand", "VSL — protected strand and handling", "S$/kg", r => r.VslStrandSgdPerKg, (r, v) => r with { VslStrandSgdPerKg = v }),
        new("vslPipe", "VSL — HDPE pipe allowance", "S$ per mm of OD per m", r => r.VslPipeSgdPerMmPerM, (r, v) => r with { VslPipeSgdPerMmPerM = v }),
        new("vslEndBase", "VSL — anchorage base, per end", "S$", r => r.VslEndBaseSgd, (r, v) => r with { VslEndBaseSgd = v }),
        new("vslEndStrand", "VSL — anchorage per strand, per end", "S$/strand", r => r.VslEndPerStrandSgd, (r, v) => r with { VslEndPerStrandSgd = v }),
        new("vslInstall", "VSL — erection, stressing and QA", "S$/m per unit", r => r.VslInstallSgdPerM, (r, v) => r with { VslInstallSgdPerM = v }),
        new("fatzerRope", "FATZER — rope, fabrication and logistics", "S$/kg", r => r.FatzerRopeSgdPerKg, (r, v) => r with { FatzerRopeSgdPerKg = v }),
        new("fatzerEndBase", "FATZER — socket base, per end", "S$", r => r.FatzerEndBaseSgd, (r, v) => r with { FatzerEndBaseSgd = v }),
        new("fatzerEndD2", "FATZER — socket allowance × d², per end", "S$/mm²", r => r.FatzerEndPerMm2Sgd, (r, v) => r with { FatzerEndPerMm2Sgd = v }),
        new("fatzerInstall", "FATZER — erection and QA", "S$/m per unit", r => r.FatzerInstallSgdPerM, (r, v) => r with { FatzerInstallSgdPerM = v }),
        new("utraconStrand", "Utracon — strand", "S$/kg", r => r.UtraconStrandSgdPerKg, (r, v) => r with { UtraconStrandSgdPerKg = v }),
        new("utraconDuct", "Utracon — duct and handling", "S$/m per unit", r => r.UtraconDuctSgdPerM, (r, v) => r with { UtraconDuctSgdPerM = v }),
        new("utraconEndBase", "Utracon — anchorage base, per end", "S$", r => r.UtraconEndBaseSgd, (r, v) => r with { UtraconEndBaseSgd = v }),
        new("utraconEndStrand", "Utracon — anchorage per strand, per end", "S$/strand", r => r.UtraconEndPerStrandSgd, (r, v) => r with { UtraconEndPerStrandSgd = v }),
        new("utraconInstall", "Utracon — installation", "S$/m per unit", r => r.UtraconInstallSgdPerM, (r, v) => r with { UtraconInstallSgdPerM = v }),
        new("contingency", "Contingency on the pre-tax cost", "%", r => r.ContingencyPercent, (r, v) => r with { ContingencyPercent = v }),
        new("gst", "GST added (0 = excluded)", "%", r => r.GstPercent, (r, v) => r with { GstPercent = v }),
    ];

    public IReadOnlyList<string> Problems() => Entries.Where(entry => !double.IsFinite(entry.Get(this)) || entry.Get(this) < 0)
        .Select(entry => $"Rate {entry.Label}: enter a number of at least 0.").ToArray();

    /// <summary>Cost of one unit of a stay of length <paramref name="lengthM"/> per metre of stay [SGD/m], before contingency and GST: material, pipe or
    /// duct, both anchorage ends (amortised over the length) and installation — the study's build-up (its section 05).</summary>
    public double UnitCostPerM(CableUnit unit, double lengthM)
    {
        var length = Math.Max(lengthM, 1e-6);
        return unit.System switch
        {
            CableSystem.VslSsi2000 => unit.Strands * CableStrandMass * VslStrandSgdPerKg + VslPipeSgdPerMmPerM * unit.ConduitMm
                                      + 2 * (VslEndBaseSgd + VslEndPerStrandSgd * unit.Strands) / length + VslInstallSgdPerM,
            CableSystem.FatzerFlc => unit.MassKgPerM * FatzerRopeSgdPerKg + 2 * (FatzerEndBaseSgd + FatzerEndPerMm2Sgd * unit.ConduitMm * unit.ConduitMm) / length + FatzerInstallSgdPerM,
            _ => unit.Strands * CableStrandMass * UtraconStrandSgdPerKg + UtraconDuctSgdPerM
                 + 2 * (UtraconEndBaseSgd + UtraconEndPerStrandSgd * unit.Strands) / length + UtraconInstallSgdPerM,
        };
    }

    private const double CableStrandMass = TensionCableCatalogue.StrandMassKgPerM;

    /// <summary>Contingency and GST on a pre-tax cost.</summary>
    public double WithAdditions(double preTax) => preTax * (1 + ContingencyPercent / 100) * (1 + GstPercent / 100);
}

/// <summary>
/// The basis of Design → Tension Cable Design: which systems are offered, the screening level, the highest use of it, how candidates are
/// ranked and the cost rates. The screen is the study's — a stay passes when <c>n × f × MBL ≥ T</c> with f = 0.50 (VSL's reference admissible
/// load) or 0.60 — and is a first-pass proxy, not an EN 1993-1-11 ultimate resistance.
/// </summary>
public sealed record TensionCableBasis
{
    /// <summary>The systems offered. The Utracon PT tendon is not one the study found qualified as an exposed stay, so it is offered only when ticked.</summary>
    public IReadOnlyList<CableSystem> Systems { get; init; } = [CableSystem.VslSsi2000, CableSystem.FatzerFlc];
    /// <summary>f: the fraction of the breaking load a unit may carry (0.50 reference; 0.60 subject to verification with the supplier).</summary>
    public double ScreeningFactor { get; init; } = 0.5;
    /// <summary>η: the highest use of f × MBL (1.0 = no reserve).</summary>
    public double MaxUtilisation { get; init; } = 1.0;
    public CablePriority Priority { get; init; } = CablePriority.LowestCost;
    public CableForceBasis ForceBasis { get; init; } = CableForceBasis.Service;
    /// <summary>The most parallel units one stay may have; a candidate that needs more is not offered.</summary>
    public int MaxUnits { get; init; } = 24;
    public TensionCableRates Rates { get; init; } = new();
    /// <summary>The load cases / combinations whose cable tension is designed for; empty = every case of the run.</summary>
    public IReadOnlyList<string> LoadCases { get; init; } = [];
    /// <summary>The cables to design; empty until chosen.</summary>
    public IReadOnlyList<string> Members { get; init; } = [];

    public const string Code = "Technical Options Study 18 Oct 2026 — 50 % / 60 % of breaking load screen; EN 1993-1-11, EN 1993-2 with the Singapore NA to be checked by the designer";

    public static TensionCableBasis Default { get; } = new();

    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (Systems.Count == 0) problems.Add("Tick at least one stay system.");
        if (!double.IsFinite(ScreeningFactor) || ScreeningFactor is <= 0 or > 1) problems.Add("Screening level: enter a fraction of the breaking load above 0 and at most 1 (0.50 or 0.60).");
        if (!double.IsFinite(MaxUtilisation) || MaxUtilisation is <= 0 or > 1) problems.Add("Maximum use: enter a fraction above 0 and at most 1.");
        if (MaxUnits < 1) problems.Add("Most units per stay: enter a whole number of at least 1.");
        problems.AddRange(Rates.Problems());
        return problems;
    }
}

/// <summary>One stay assembly: <see cref="Units"/> identical units of one catalogue size, with what it carries, weighs, extends and costs.</summary>
public sealed record TensionCableSelection(
    CableSystem System, string Code, string Name, int Units, int StrandsPerUnit, double ConduitMm,
    double UnitCapacityKN, double GroupCapacityKN, double Utilisation, double TensionKN, double LengthM,
    double SteelAreaMm2, double AxialStiffnessKN, double ElongationMm, double SteelMassT,
    double CostPerMSgd, double CostSgd, bool Qualified, double LimitTensionKN = 0)
{
    public int TotalStrands => StrandsPerUnit * Units;

    /// <summary>"4 × VSL SSI 2000 6-187" — the assembly in one line.</summary>
    public string Assembly => $"{Units} × {Name}";

    /// <summary>The assembly with what a reader checks first: strands, utilisation and cost.</summary>
    [JsonIgnore]
    public string Summary
    {
        get
        {
            var strands = StrandsPerUnit > 0 ? $" ({TotalStrands} strands, pipe Ø{ConduitMm:0} mm each)" : $" (rope Ø{ConduitMm:0} mm each)";
            return $"{Assembly}{strands} · use {Utilisation:0.00} · S${CostPerMSgd:#,##0}/m";
        }
    }
}

/// <summary>The design of one tension cable: the tension it is designed for, the chosen assembly and the best one of each other system.</summary>
/// <param name="Status">OK; CHECK when the choice rests on an unqualified system or interpolated catalogue data; ERROR when the cable cannot be designed; — for a cable with no tension.</param>
public sealed record TensionCableMemberDesign(string Member, string Section, double LengthM, string Case, double TensionKN,
    double? ModelAreaMm2, double? ModelCapacityKN, TensionCableSelection? Selected, IReadOnlyList<TensionCableSelection> Alternatives,
    string Status, IReadOnlyList<string> Notes, string Detail, string? Error = null)
{
    public double? CostSgd => Selected?.CostSgd;
}

public static class TensionCableDesigner
{
    /// <summary>Thermal length change of the steel for 40 °C, α = 12 × 10⁻⁶ /°C (the study's section 3.4) [mm per m of stay].</summary>
    private const double ThermalMmPerM = 12e-6 * 40 * 1000;

    /// <summary>The best assembly of every offered system for a stay of <paramref name="tensionKN"/> and <paramref name="lengthM"/>, the best overall first.</summary>
    public static IReadOnlyList<TensionCableSelection> Candidates(TensionCableBasis basis, double tensionKN, double lengthM)
    {
        var best = new List<TensionCableSelection>();
        foreach (var system in basis.Systems.Distinct())
        {
            TensionCableSelection? pick = null;
            foreach (var unit in TensionCableCatalogue.Units(system))
            {
                var candidate = Assemble(basis, unit, tensionKN, lengthM);
                if (candidate is null) continue;
                if (pick is null || Rank(basis, candidate).CompareTo(Rank(basis, pick)) < 0) pick = candidate;
            }
            if (pick is not null) best.Add(pick);
        }
        return best.OrderBy(item => Rank(basis, item)).ToArray();
    }

    // lexicographic: the priority's own measure, then the others as tie-breaks
    private static (double, double, double) Rank(TensionCableBasis basis, TensionCableSelection s) => basis.Priority switch
    {
        CablePriority.FewestUnits => (s.Units, s.CostPerMSgd, s.SteelAreaMm2),
        CablePriority.LeastSteel => (Math.Round(s.SteelAreaMm2, 3), s.CostPerMSgd, s.Units),
        _ => (Math.Round(s.CostPerMSgd, 6), s.Units, s.SteelAreaMm2),
    };

    private static TensionCableSelection? Assemble(TensionCableBasis basis, CableUnit unit, double tensionKN, double lengthM)
    {
        var reference = unit.BreakingLoadKN * basis.ScreeningFactor;
        var units = Math.Max(1, (int)Math.Ceiling(tensionKN / (reference * basis.MaxUtilisation) - 1e-9));
        if (units > basis.MaxUnits) return null;
        var group = units * reference;
        var area = double.IsNaN(unit.SteelAreaMm2) ? 0 : units * unit.SteelAreaMm2;
        var stiffness = units * unit.AxialStiffnessKN;
        var perMetre = basis.Rates.WithAdditions(units * basis.Rates.UnitCostPerM(unit, lengthM));
        return new(unit.System, unit.Code, unit.Name, units, unit.Strands, unit.ConduitMm, reference, group, tensionKN / group, tensionKN, lengthM,
            area, stiffness, tensionKN * lengthM / stiffness * 1000, units * unit.MassKgPerM * lengthM / 1000,
            perMetre, perMetre * lengthM, TensionCableCatalogue.IsQualifiedStay(unit.System), units * unit.LimitTensionKN);
    }

    /// <summary>Designs one cable for <paramref name="tensionKN"/> [kN] over <paramref name="lengthM"/> [m].</summary>
    public static TensionCableMemberDesign Design(TensionCableBasis basis, string member, string section, double lengthM, string governingCase, double tensionKN,
        double? modelAreaMm2 = null, double? modelCapacityKN = null)
    {
        TensionCableMemberDesign Failed(string status, string message) =>
            new(member, section, lengthM, governingCase, tensionKN, modelAreaMm2, modelCapacityKN, null, [], status, [], message, status == "ERROR" ? message : null);
        if (basis.Problems() is { Count: > 0 } problems) return Failed("ERROR", problems[0]);
        if (!double.IsFinite(tensionKN)) return Failed("ERROR", "The cable has no tension in the results.");
        if (tensionKN <= 1e-6) return Failed("—", "The cable carries no tension in the solved results (slack), so no stay is sized for it.");
        if (!double.IsFinite(lengthM) || lengthM <= 0) return Failed("ERROR", "The cable has no length in the model.");
        var candidates = Candidates(basis, tensionKN, lengthM);
        if (candidates.Count == 0)
            return Failed("ERROR", $"No offered system carries {tensionKN / 1000:0.##} MN within {basis.MaxUnits} parallel units; raise the most units per stay or offer another system.");
        var selected = candidates[0];
        var notes = Notes(basis, selected);
        var status = selected.Qualified ? "OK" : "CHECK";
        return new(member, section, lengthM, governingCase, tensionKN, modelAreaMm2, modelCapacityKN, selected, candidates.Skip(1).ToArray(), status, notes,
            Detail(basis, member, section, governingCase, selected, candidates.Skip(1).ToArray(), notes, modelAreaMm2, modelCapacityKN));
    }

    /// <summary>The cautions the study attaches to a selection.</summary>
    public static IReadOnlyList<string> Notes(TensionCableBasis basis, TensionCableSelection s)
    {
        var notes = new List<string>();
        if (basis.ForceBasis == CableForceBasis.Ultimate)
            notes.Add("The tension is a factored (ULS) force: 50–60 % of the breaking load is not an EN 1993-1-11 ULS resistance — obtain the supplier's design resistance F_Rd and re-check.");
        if (basis.ForceBasis == CableForceBasis.Unknown)
            notes.Add("State whether the tension is SLS, ULS, fatigue or construction-stage before the sizes are used.");
        if (basis.ScreeningFactor > 0.5 + 1e-9)
            notes.Add($"{basis.ScreeningFactor:0.##} of the breaking load is above the 50 % reference: confirm it with the supplier for this load category.");
        if (!s.Qualified)
            notes.Add("The Utracon 37-strand PT tendon is not shown to be an approved, exposed, replaceable bridge stay: ask for a named stay system and proof of equivalent performance.");
        if (s.Units >= 4)
            notes.Add($"{s.Units} parallel units need separate anchorage heads, a force-sharing and stressing scheme and a node that can carry {s.Units * 2} anchorages.");
        if (s.TensionKN >= 80_000)
            notes.Add("A force of 80 MN or more: check bearing, fatigue, anchor boxes, diaphragms and the replacement access at each node.");
        if (s.LengthM < 25)
            notes.Add("A short stay: anchorage, jack clearances and access may govern the length.");
        notes.Add("Fatigue stress range, sag, anchorage slip, relaxation and the supporting anchorage steelwork are not in the screen or the cost.");
        return notes;
    }

    private static string Detail(TensionCableBasis basis, string member, string section, string governingCase, TensionCableSelection s,
        IReadOnlyList<TensionCableSelection> others, IReadOnlyList<string> notes, double? modelArea, double? modelCapacity)
    {
        var ci = CultureInfo.InvariantCulture;
        var text = new StringBuilder();
        text.AppendLine($"{member} — tension cable design (preliminary, not for construction)");
        text.AppendLine($"Section in the model: {(string.IsNullOrEmpty(section) ? "—" : section)}" +
                        (modelArea is { } a ? $" · steel area {a:0} mm²" : "") + (modelCapacity is { } c ? $" · model capacity {c:0} kN" : ""));
        text.AppendLine($"Length L = {s.LengthM.ToString("0.###", ci)} m · governing tension T = {s.TensionKN.ToString("#,0.#", ci)} kN{(governingCase.Length > 0 ? $" ({governingCase})" : "")}");
        text.AppendLine();
        text.AppendLine("Screen: n = ceil( T / (f × MBL_unit × η) ), f = " + basis.ScreeningFactor.ToString("0.##", ci) + ", η = " + basis.MaxUtilisation.ToString("0.##", ci));
        text.AppendLine($"  {s.Name}: MBL = {(s.UnitCapacityKN / basis.ScreeningFactor).ToString("#,0", ci)} kN, f × MBL = {s.UnitCapacityKN.ToString("#,0.#", ci)} kN → n = {s.Units}");
        text.AppendLine($"  group reference capacity n × f × MBL = {s.GroupCapacityKN.ToString("#,0.#", ci)} kN · use T / capacity = {s.Utilisation.ToString("0.000", ci)}");
        text.AppendLine();
        text.AppendLine("Selected: " + s.Summary);
        if (s.StrandsPerUnit > 0) text.AppendLine($"  {s.TotalStrands} strands Ø15.7 mm = {s.SteelAreaMm2.ToString("#,0", ci)} mm² of steel, {s.TotalStrands * 7} wires");
        text.AppendLine($"  steel mass {s.SteelMassT.ToString("0.00", ci)} t over {s.LengthM.ToString("0.###", ci)} m");
        text.AppendLine($"  ΣEA = {(s.AxialStiffnessKN / 1000).ToString("#,0", ci)} MN · elastic extension ΔL = T L / ΣEA = {s.ElongationMm.ToString("0.0", ci)} mm · " +
                        $"thermal ΔL (ΔT = 40 °C, α = 12×10⁻⁶) = {(ThermalMmPerM * s.LengthM / 1000).ToString("0.0", ci)} mm");
        text.AppendLine($"  cost S${s.CostPerMSgd.ToString("#,0", ci)}/m of stay = S${s.CostSgd.ToString("#,0", ci)} over its length (indicative rates, supplier quotation required)");
        if (s.LimitTensionKN > 0)
            text.AppendLine($"  FATZER published limit tension F_Rd = Fuk / 1.5 of the group = {s.LimitTensionKN.ToString("#,0", ci)} kN ({(s.TensionKN / s.LimitTensionKN).ToString("0.00", ci)} of it)");
        if (modelArea is { } area && area > 0 && s.SteelAreaMm2 > 0)
            text.AppendLine($"  suggested steel area is {s.SteelAreaMm2 / area:0.00} × the model section's {area:0} mm²");
        if (others.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Other systems:");
            foreach (var other in others) text.AppendLine($"  {other.Summary}");
        }
        text.AppendLine();
        foreach (var note in notes) text.AppendLine("• " + note);
        return text.ToString();
    }

    /// <summary>Source mark of the cable sections the VCF system starts with (<see cref="StayLadder"/>).</summary>
    public const string StayLadderSource = "VSL SSI 2000 schedule";

    /// <summary>The selection schedule of the Technical Options Study (its section 04): for each demand of 10, 20 … 120 MN on a 50 m stay, the VSL SSI 2000
    /// assembly the design picks at the lowest cost (50 % of the breaking load). Their total strand counts — 73, 151, 218, 302, 374, 453, 507, 604, 676, 748,
    /// 845, 906 — are the cable sections the VCF system is offered, each one equivalent section of that many 15.7 mm strands.</summary>
    public static IReadOnlyList<(int DemandMN, TensionCableSelection Selection)> StayLadder { get; } = Enumerable.Range(1, 12).Select(step => step * 10)
        .Select(demand => (demand, Candidates(new TensionCableBasis { Systems = [CableSystem.VslSsi2000] }, demand * 1000.0, 50)[0])).ToArray();

    /// <summary>The designs as the Word report of Design → Tension Cable Design → Export to Word…: the basis, one table row per cable, each calculation.</summary>
    public static DesignReport WordReport(TensionCableBasis basis, IReadOnlyList<TensionCableMemberDesign> designs)
    {
        var ci = CultureInfo.InvariantCulture;
        string[] header = ["Cable", "Governing case", "T [kN]", "L [m]", "Suggested stay", "Strands", "Capacity [kN]", "Use", "ΔL [mm]", "S$/m", "Cost [S$]", "Status"];
        var rows = designs.Select(design => (IReadOnlyList<string>)
        [
            design.Member, design.Case, design.TensionKN.ToString("#,0", ci), design.LengthM.ToString("0.##", ci), design.Selected?.Assembly ?? design.Error ?? design.Detail.Trim(),
            design.Selected is null ? "" : design.Selected.StrandsPerUnit > 0 ? design.Selected.TotalStrands.ToString(ci) : "rope",
            design.Selected?.GroupCapacityKN.ToString("#,0", ci) ?? "", design.Selected?.Utilisation.ToString("0.00", ci) ?? "", design.Selected?.ElongationMm.ToString("0.0", ci) ?? "",
            design.Selected?.CostPerMSgd.ToString("#,0", ci) ?? "", design.Selected?.CostSgd.ToString("#,0", ci) ?? "", design.Status,
        ]).ToArray();
        var designed = designs.Where(design => design.Selected is not null).ToArray();
        return new("Tension Cable Design — preliminary, not for construction",
            [
                TensionCableBasis.Code,
                $"Systems: {string.Join(", ", basis.Systems.Select(TensionCableCatalogue.ShortTitle))} · screening f = {basis.ScreeningFactor.ToString("0.##", ci)} of the breaking load · maximum use η = {basis.MaxUtilisation.ToString("0.##", ci)} · " +
                $"priority {basis.Priority} · tension is {basis.ForceBasis} · at most {basis.MaxUnits} units per stay",
                $"{designs.Count} cable(s), {designed.Length} with a suggested stay; indicative cost S${designed.Sum(design => design.Selected!.CostSgd).ToString("#,0", ci)} (budgeting rates of the study author, not supplier quotations; excludes GST unless entered and the supporting anchorage steelwork).",
            ],
            header, rows, designed.Select(design => ($"{design.Member} — {design.Selected!.Assembly}", design.Detail)).ToArray(),
            designed.SelectMany(design => design.Notes).Distinct().ToArray());
    }

    /// <summary>The designs as a text report: the basis, a summary line per cable and each cable's calculation.</summary>
    public static string Report(TensionCableBasis basis, IReadOnlyList<TensionCableMemberDesign> designs)
    {
        var ci = CultureInfo.InvariantCulture;
        var text = new StringBuilder();
        text.AppendLine("TENSION CABLE DESIGN — PRELIMINARY, NOT FOR CONSTRUCTION");
        text.AppendLine(TensionCableBasis.Code);
        text.AppendLine($"Systems: {string.Join(", ", basis.Systems.Select(TensionCableCatalogue.ShortTitle))} · f = {basis.ScreeningFactor.ToString("0.##", ci)} · η = {basis.MaxUtilisation.ToString("0.##", ci)} · " +
                        $"priority {basis.Priority} · force basis {basis.ForceBasis}");
        text.AppendLine();
        foreach (var design in designs)
            text.AppendLine($"{design.Member,-10} {design.Status,-6} T = {design.TensionKN,10:#,0} kN  L = {design.LengthM,7:0.##} m  " + (design.Selected?.Summary ?? design.Error ?? design.Detail));
        var total = designs.Sum(design => design.Selected?.CostSgd ?? 0);
        text.AppendLine();
        text.AppendLine($"Indicative cost of the {designs.Count(design => design.Selected is not null)} cables designed: S${total.ToString("#,0", ci)}");
        foreach (var design in designs.Where(design => design.Selected is not null))
        {
            text.AppendLine();
            text.AppendLine(new string('-', 78));
            text.Append(design.Detail);
        }
        return text.ToString();
    }
}
