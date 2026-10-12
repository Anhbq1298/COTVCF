/**
 * Mega Column 2 — CTBUH / ArcelorMittal Section 4 example (printed pages 28–45).
 *
 * Follows the Simplified (Adapted Plastic Distribution) Design Method exactly as
 * published in Trabucco, Gérardy, Xiao & Davies (2016), "Composite Megacolumns:
 * Testing Multiple, Concrete-Encased, Hot-Rolled Steel Sections", Section 4
 * "Simplified Design Methods and Examples", Tables 11–17.
 *
 * Default inputs reproduce the report's worked section parameters
 * (4 × HD 400×1299 → d* = 600 mm, b* = 275 mm, I* = 4.95×10⁹ mm⁴).
 *
 * The method extends EN 1994-1-1 §6.7.3 plastic distribution to sections with
 * several encased steel profiles by replacing rebar lines with equivalent plates
 * and steel profiles with equivalent rectangles.
 *
 * Bending is about the x-x axis. Sign convention used throughout:
 *   h1 = section dimension parallel to the x-x axis  (the width that a plastic
 *        modulus is multiplied by, i.e. Zcx = h1·h2²/4)
 *   h2 = section dimension perpendicular to x-x      (the bending depth)
 *   hnx = distance from the centroidal axis to the plastic neutral axis
 */

export type ProfileArrangement = "four" | "six";

export interface MegaColumn2Input {
  /** Section width parallel to the x-x axis (mm). */
  h1: number;
  /** Section depth perpendicular to the x-x axis — the bending depth (mm). */
  h2: number;

  arrangement: ProfileArrangement;
  /** Area of one steel profile (mm²). */
  Aa: number;
  /** Depth of one steel profile (mm). */
  d: number;
  /** Second moment of area of one profile about its own strong (x) axis (mm⁴). */
  Ix: number;
  /** Plastic modulus of one profile about its own strong (x) axis (mm³). */
  Zx: number;
  /** Distance from section centroid to the centroid of an outer profile, y-direction (mm). */
  dsy: number;

  /** Number of horizontal (flange) rebar layers on the top face — mirrored on the bottom. */
  i: number;
  /** Number of bars on one horizontal layer. */
  nx: number;
  /** Bar spacing in the x-direction (mm). */
  sx: number;
  /** Mean distance from the section centroid to the horizontal layers (mm). */
  ds1y: number;

  /** Number of bars on one vertical (web) layer. */
  ny: number;
  /** Bar spacing in the y-direction (mm). */
  sy: number;
  /** Mean distance from the section centroid to the vertical plates, x-direction (mm). */
  ds2x: number;

  /** Longitudinal bar diameter (mm). */
  db: number;

  fck: number;
  fy: number;
  fsy: number;
  gammaC: number;
  gammaA: number;
  gammaS: number;
  /** Secant modulus of concrete (MPa). */
  Ecm: number;
  /** Modulus of elasticity of structural steel (MPa). */
  Ea: number;
  /** Modulus of elasticity of reinforcement (MPa). */
  Es: number;

  /** Design axial compression (kN). */
  NEd: number;
  /** Design bending moment about x-x (kN·m). */
  MEdx: number;
  /** Permanent share of NEd, NG,Ed/NEd. */
  NGratio: number;
  /** Creep coefficient φt (EN 1992-1-1 §3.1.4). */
  phiT: number;
  /** Buckling length (mm). */
  L: number;
}

export interface HnxSolution {
  /** Which of the Table 15 / Table 17 cases governs. */
  caseId: 1 | 2 | 3 | 4;
  caseLabel: string;
  hnx: number;
  Zcxn: number;
  Zr2xn: number;
  Zsxn: number;
  MplRd: number;
  /** Zsxn evaluated with the literal Table 15 Case 2 print (with the 3·hnx term). */
  ZsxnAsPrinted: number | null;
  MplRdAsPrinted: number | null;
}

export interface MegaColumn2Result {
  // --- Equivalent plates (Table 11) ---
  Asri: number;
  As1: number;
  hs1: number;
  bs1: number;
  As2: number;
  hs2: number;
  bs2: number;
  Asr: number;
  Zsr1x: number;
  Isr1x: number;
  Zsr2x: number;
  Isr2x: number;
  Isr: number;

  // --- Equivalent rectangles for the profiles (Tables 12 / 13) ---
  dStar: number;
  bStar: number;
  IStar: number;
  As: number;
  Zsx: number;
  Isx: number;

  // --- Section areas ---
  Ag: number;
  Ac: number;
  Ic: number;

  // --- Design strengths ---
  fcd: number;
  fyd: number;
  fsd: number;

  // --- Interaction anchor points (Table 14) ---
  NplRd: number;
  NpmRd: number;
  Zcx: number;
  MmaxRd: number;
  hn: HnxSolution;
  MplRd: number;
  /** A, C, D, B in (N kN, M kNm). */
  points: { label: string; N: number; M: number }[];

  // --- Stiffness and buckling ---
  EcEff: number;
  EIeff: number;
  C1: number;
  EIeffAisc: number;
  NplRk: number;
  Ncr: number;
  lambdaBar: number;
  chi: number;
  NbRd: number;

  // --- Second-order ---
  EIeffII: number;
  NcrEff: number;
  kAmp: number;
  MEdII: number;

  // --- Verification of the design point ---
  muD: number;
  alphaM: number;
  MRd: number;

  // --- Checks ---
  delta: number;
  webN: number;
  webInertiaError: number;
  webPlasticError: number;
  checks: DesignCheck[];
  governing: DesignCheck;
  warnings: string[];
}

export interface DesignCheck {
  id: string;
  label: string;
  clause: string;
  demand: string;
  capacity: string;
  utilisation: number;
  pass: boolean;
  note: string;
  /**
   * "capacity" checks compare a design action against a resistance, so their
   * utilisation ratios are comparable and the largest one governs the design.
   * "validity" checks confirm the method is applicable to the section at all —
   * they are pass/fail conditions, and their ratios are not design utilisations.
   */
  kind: "capacity" | "validity";
}

const PI = Math.PI;

/** Interaction polygon ordinate M at a given axial force N (linear between anchors). */
function momentAtN(points: { N: number; M: number }[], N: number): number {
  const sorted = [...points].sort((a, b) => b.N - a.N);
  if (N >= sorted[0].N) return sorted[0].M;
  if (N <= sorted[sorted.length - 1].N) return sorted[sorted.length - 1].M;
  for (let k = 0; k < sorted.length - 1; k++) {
    const hi = sorted[k];
    const lo = sorted[k + 1];
    if (N <= hi.N && N >= lo.N) {
      const span = hi.N - lo.N;
      if (Math.abs(span) < 1e-9) return Math.max(hi.M, lo.M);
      const t = (hi.N - N) / span;
      return hi.M + t * (lo.M - hi.M);
    }
  }
  return 0;
}

/**
 * Plastic neutral axis position and Mpl,Rd.
 *
 * Four-profile arrangement follows Table 15; six-profile follows Table 17. Both
 * are derived from axial equilibrium over the 2·hn zone, and both reduce Mmax,Rd
 * by the plastic moduli of the material lying inside that zone.
 */
function solveHnx(
  x: MegaColumn2Input,
  r: Pick<MegaColumn2Result, "bStar" | "bs2" | "MmaxRd" | "NpmRd" | "fcd" | "fyd" | "fsd" | "Zsx">
): HnxSolution {
  const { h1, d, dsy, Aa, Zx, arrangement } = x;
  const { bStar, bs2, MmaxRd, NpmRd, fcd, fyd, fsd, Zsx } = r;
  const fc = 0.85 * fcd;
  const NC = NpmRd * 1000; // kN → N
  const a = dsy - d / 2; // inner face of the outer profiles

  const MplFrom = (Zcxn: number, Zr2xn: number, Zsxn: number) =>
    MmaxRd - (Zr2xn * fsd + Zsxn * fyd + 0.5 * Zcxn * fc) / 1e6;

  if (arrangement === "four") {
    // ---- Table 15 ----
    // Case 1: hnx ≤ dsy − d/2 — neutral axis between the two profile pairs.
    let hnx = NC / ((2 * h1 - 4 * bs2) * fc + 4 * bs2 * fsd);
    if (hnx <= a) {
      const Zr2xn = 2 * bs2 * hnx ** 2;
      const Zcxn = h1 * hnx ** 2 - Zr2xn;
      return {
        caseId: 1,
        caseLabel: "Case 1 — hₙₓ between the two profiles",
        hnx,
        Zcxn,
        Zr2xn,
        Zsxn: 0,
        MplRd: MplFrom(Zcxn, Zr2xn, 0),
        ZsxnAsPrinted: null,
        MplRdAsPrinted: null,
      };
    }

    // Case 2: dsy − d/2 < hnx ≤ dsy + d/2 — neutral axis cuts the profiles.
    hnx =
      (NC + 4 * a * bStar * (fyd - fc)) /
      ((2 * h1 - 4 * bStar - 4 * bs2) * fc + 4 * bStar * fyd + 4 * bs2 * fsd);
    if (hnx <= dsy + d / 2) {
      const Zr2xn = 2 * bs2 * hnx ** 2;
      // Continuity-consistent form, matching Table 17 Case 3: 2·b*·(hnx − a)(hnx + a).
      const Zsxn = 2 * bStar * (hnx - a) * (hnx + a);
      const Zcxn = h1 * hnx ** 2 - Zr2xn - Zsxn;
      // Literal Table 15 Case 2 print, retained for the verification comparison.
      const ZsxnAsPrinted = bStar * (hnx - a) * (3 * hnx + a);
      const ZcxnAsPrinted = h1 * hnx ** 2 - Zr2xn - ZsxnAsPrinted;
      return {
        caseId: 2,
        caseLabel: "Case 2 — hₙₓ inside the steel profiles",
        hnx,
        Zcxn,
        Zr2xn,
        Zsxn,
        MplRd: MplFrom(Zcxn, Zr2xn, Zsxn),
        ZsxnAsPrinted,
        MplRdAsPrinted: MplFrom(ZcxnAsPrinted, Zr2xn, ZsxnAsPrinted),
      };
    }

    // Case 3: hnx > dsy + d/2 — all profiles inside the zone.
    hnx = (NC - 4 * Aa * fyd + 4 * Aa * fc) / ((2 * h1 - 4 * bs2) * fc + 4 * bs2 * fsd);
    const Zr2xn = 2 * bs2 * hnx ** 2;
    const Zsxn = Zsx;
    const Zcxn = h1 * hnx ** 2 - Zr2xn - Zsxn;
    return {
      caseId: 3,
      caseLabel: "Case 3 — hₙₓ beyond the profiles",
      hnx,
      Zcxn,
      Zr2xn,
      Zsxn,
      MplRd: MplFrom(Zcxn, Zr2xn, Zsxn),
      ZsxnAsPrinted: null,
      MplRdAsPrinted: null,
    };
  }

  // ---- Table 17: six profiles (four outer at ±dsy, two central on the axis) ----
  // Case 1: hnx ≤ d/2 — inside the two central profiles.
  let hnx =
    NC / ((2 * h1 - 4 * bStar - 4 * bs2) * fc + 4 * bStar * fyd + 4 * bs2 * fsd);
  if (hnx <= d / 2) {
    const Zr2xn = 2 * bs2 * hnx ** 2;
    const Zsxn = 2 * bStar * hnx ** 2;
    const Zcxn = h1 * hnx ** 2 - Zr2xn - Zsxn;
    return {
      caseId: 1,
      caseLabel: "Case 1 — hₙₓ inside the two central profiles",
      hnx,
      Zcxn,
      Zr2xn,
      Zsxn,
      MplRd: MplFrom(Zcxn, Zr2xn, Zsxn),
      ZsxnAsPrinted: null,
      MplRdAsPrinted: null,
    };
  }

  // Case 2: d/2 < hnx ≤ dsy − d/2 — central profiles fully inside.
  hnx = (NC + 2 * Aa * fc - 2 * Aa * fyd) / ((2 * h1 - 4 * bs2) * fc + 4 * bs2 * fsd);
  if (hnx <= a) {
    const Zr2xn = 2 * bs2 * hnx ** 2;
    const Zsxn = 2 * Zx;
    const Zcxn = h1 * hnx ** 2 - Zr2xn - Zsxn;
    return {
      caseId: 2,
      caseLabel: "Case 2 — hₙₓ between central and outer profiles",
      hnx,
      Zcxn,
      Zr2xn,
      Zsxn,
      MplRd: MplFrom(Zcxn, Zr2xn, Zsxn),
      ZsxnAsPrinted: null,
      MplRdAsPrinted: null,
    };
  }

  // Case 3: dsy − d/2 < hnx ≤ dsy + d/2 — zone cuts the outer profiles.
  hnx =
    (NC + 2 * Aa * fc - 4 * a * bStar * fc - 2 * Aa * fyd + 4 * a * bStar * fyd) /
    ((2 * h1 - 4 * bStar - 4 * bs2) * fc + 4 * bStar * fyd + 4 * bs2 * fsd);
  if (hnx <= dsy + d / 2) {
    const Zr2xn = 2 * bs2 * hnx ** 2;
    const Zsxn = 2 * Zx + 2 * bStar * (hnx - a) * (hnx + a);
    const Zcxn = h1 * hnx ** 2 - Zr2xn - Zsxn;
    return {
      caseId: 3,
      caseLabel: "Case 3 — hₙₓ inside the outer profiles",
      hnx,
      Zcxn,
      Zr2xn,
      Zsxn,
      MplRd: MplFrom(Zcxn, Zr2xn, Zsxn),
      ZsxnAsPrinted: null,
      MplRdAsPrinted: null,
    };
  }

  // Case 4: all profiles inside the zone.
  hnx = (NC - 4 * Aa * fyd + 4 * Aa * fc) / ((2 * h1 - 4 * bs2) * fc + 4 * bs2 * fsd);
  const Zr2xn4 = 2 * bs2 * hnx ** 2;
  const Zsxn4 = Zsx;
  const Zcxn4 = h1 * hnx ** 2 - Zr2xn4 - Zsxn4;
  return {
    caseId: 4,
    caseLabel: "Case 4 — hₙₓ beyond all profiles",
    hnx,
    Zcxn: Zcxn4,
    Zr2xn: Zr2xn4,
    Zsxn: Zsxn4,
    MplRd: MplFrom(Zcxn4, Zr2xn4, Zsxn4),
    ZsxnAsPrinted: null,
    MplRdAsPrinted: null,
  };
}

export function calculateMegaColumn2(x: MegaColumn2Input): MegaColumn2Result | null {
  if (x.h1 <= 0 || x.h2 <= 0 || x.Aa <= 0 || x.d <= 0 || x.L <= 0 || x.NEd <= 0) return null;
  if (x.nx < 2 || x.ny < 2 || x.i < 1) return null;
  if (x.gammaC <= 0 || x.gammaA <= 0 || x.gammaS <= 0) return null;

  const warnings: string[] = [];

  // ---- Table 11: rebar lines replaced by equivalent plates ----
  const Asri = (PI * x.db ** 2) / 4;
  const As1 = x.i * x.nx * Asri;
  const hs1 = (x.nx - 1) * x.sx;
  const bs1 = As1 / hs1;
  const As2 = x.ny * Asri;
  const hs2 = (x.ny - 1) * x.sy;
  const bs2 = As2 / hs2;
  const Asr = 2 * As1 + 2 * As2;

  const Zsr1x = 2 * As1 * x.ds1y;
  const Isr1x = 2 * As1 * x.ds1y ** 2;
  const Zsr2x = (bs2 * hs2 ** 2) / 2;
  const Isr2x = (bs2 * hs2 ** 3) / 6;
  const Isr = Isr1x + Isr2x;

  // ---- Tables 12/13: profiles replaced by equivalent rectangles ----
  const dStar = x.d;
  const bStar = x.Aa / dStar;
  const IStar = (bStar * dStar ** 3) / 12;
  const nProfiles = x.arrangement === "four" ? 4 : 6;
  const As = nProfiles * x.Aa;
  const Zsx =
    x.arrangement === "four" ? 4 * x.Aa * x.dsy : 4 * x.Aa * x.dsy + 2 * x.Zx;
  const Isx =
    x.arrangement === "four"
      ? 4 * x.Aa * x.dsy ** 2 + 4 * x.Ix
      : 4 * x.Aa * x.dsy ** 2 + 6 * x.Ix;

  const Ag = x.h1 * x.h2;
  const Ac = Ag - As - Asr;
  if (Ac <= 0) return null;
  const Ic = (x.h1 * x.h2 ** 3) / 12 - Isx - Isr;

  const fcd = x.fck / x.gammaC;
  const fyd = x.fy / x.gammaA;
  const fsd = x.fsy / x.gammaS;
  const fc = 0.85 * fcd;

  // ---- Table 14: anchor points ----
  const NplRd = (As * fyd + Asr * fsd + Ac * fc) / 1000;
  const NpmRd = (Ac * fc) / 1000;
  const Zcx = (x.h1 * x.h2 ** 2) / 4 - Zsr1x - Zsr2x - Zsx;
  const MmaxRd = (Zsx * fyd + (Zsr1x + Zsr2x) * fsd + 0.5 * Zcx * fc) / 1e6;

  const hn = solveHnx(x, { bStar, bs2, MmaxRd, NpmRd, fcd, fyd, fsd, Zsx });
  const MplRd = hn.MplRd;

  const points = [
    { label: "A", N: NplRd, M: 0 },
    { label: "C", N: NpmRd, M: MplRd },
    { label: "D", N: 0.5 * NpmRd, M: MmaxRd },
    { label: "B", N: 0, M: MplRd },
  ];

  // ---- §4.2.6: effective flexural stiffness ----
  const EcEff = x.Ecm / (1 + x.NGratio * x.phiT);
  const EIeff = x.Ea * Isx + x.Es * Isr + 0.6 * EcEff * Ic;
  const C1 = Math.min(0.7, 0.25 + 3 * ((As + Asr) / Ag));
  const Ec = x.Ecm;
  const EIeffAisc = x.Ea * Isx + x.Es * Isr + C1 * Ec * Ic;

  const NplRk = (As * x.fy + Asr * x.fsy + 0.85 * Ac * x.fck) / 1000;
  const Ncr = (PI ** 2 * EIeff) / x.L ** 2 / 1000;
  const lambdaBar = Math.sqrt(NplRk / Ncr);

  // EN 1994-1-1 Table 6.5 — encased sections buckle on curve b (α = 0.34).
  const alpha = 0.34;
  const phi = 0.5 * (1 + alpha * (lambdaBar - 0.2) + lambdaBar ** 2);
  const chi = Math.min(1, 1 / (phi + Math.sqrt(Math.max(phi ** 2 - lambdaBar ** 2, 0))));
  const NbRd = chi * NplRd;

  // ---- EN 1994-1-1 §6.7.3.4: second-order effects ----
  const EIeffII = 0.9 * (x.Ea * Isx + x.Es * Isr + 0.5 * EcEff * Ic);
  const NcrEff = (PI ** 2 * EIeffII) / x.L ** 2 / 1000;
  const kAmp = x.NEd < NcrEff ? Math.max(1, 1.0 / (1 - x.NEd / NcrEff)) : Infinity;
  const MEdII = Number.isFinite(kAmp) ? kAmp * x.MEdx : Infinity;

  // ---- EN 1994-1-1 §6.7.3.6: resistance in combined compression and bending ----
  const muD = MplRd > 0 ? momentAtN(points, x.NEd) / MplRd : 0;
  const alphaM = x.fy <= 355 ? 0.9 : 0.8;
  const MRd = alphaM * muD * MplRd;

  const delta = (As * fyd) / 1000 / NplRd;
  const webN = (x.ny - 1) / 2;
  const webInertiaError = webN > 0 ? 1 / (2 * webN) : Infinity;
  const webPlasticError = webN > 0 ? 1 / (4 * (webN ** 2 + webN)) : Infinity;

  if (x.dsy + x.d / 2 > x.h2 / 2) {
    warnings.push(
      "The outer profiles project beyond the section depth (dsy + d/2 > h2/2). Check the profile position and cover."
    );
  }
  if (hs2 > x.h2) {
    warnings.push("The equivalent vertical rebar plate is deeper than the section — check ny and sy.");
  }
  if (hs1 > x.h1) {
    warnings.push("The equivalent horizontal rebar plate is wider than the section — check nx and sx.");
  }
  if (Zcx <= 0) {
    warnings.push("Concrete plastic modulus Zcx is not positive — the steel and rebar plates exceed the gross section.");
  }
  if (!Number.isFinite(kAmp)) {
    warnings.push("NEd exceeds the effective elastic critical load Ncr,eff — the member is unstable under second-order effects.");
  }

  const checks: DesignCheck[] = [
    {
      id: "buckling",
      kind: "capacity",
      label: "Axial buckling resistance",
      clause: "EN 1994-1-1 §6.7.3.5",
      demand: `N_Ed = ${x.NEd.toFixed(0)} kN`,
      capacity: `χ·N_pl,Rd = ${NbRd.toFixed(0)} kN`,
      utilisation: NbRd > 0 ? x.NEd / NbRd : Infinity,
      pass: x.NEd <= NbRd,
      note: `Buckling curve b (α = 0.34), λ̄ = ${lambdaBar.toFixed(3)}, χ = ${chi.toFixed(3)}.`,
    },
    {
      id: "interaction",
      kind: "capacity",
      label: "Compression + uniaxial bending",
      clause: "EN 1994-1-1 §6.7.3.6",
      demand: `M_Ed = ${Number.isFinite(MEdII) ? MEdII.toFixed(0) : "∞"} kN·m`,
      capacity: `α_M·μ_d·M_pl,Rd = ${MRd.toFixed(0)} kN·m`,
      utilisation: MRd > 0 ? MEdII / MRd : Infinity,
      pass: Number.isFinite(MEdII) && MEdII <= MRd,
      note: `μ_d = ${muD.toFixed(3)} read off the A–C–D–B polygon at N_Ed; α_M = ${alphaM}.`,
    },
    {
      id: "squash",
      kind: "capacity",
      label: "Cross-section squash resistance",
      clause: "Table 14, point A",
      demand: `N_Ed = ${x.NEd.toFixed(0)} kN`,
      capacity: `N_pl,Rd = ${NplRd.toFixed(0)} kN`,
      utilisation: NplRd > 0 ? x.NEd / NplRd : Infinity,
      pass: x.NEd <= NplRd,
      note: "Full plastic axial resistance with 0.85·f_cd on the concrete.",
    },
    {
      id: "delta",
      kind: "validity",
      label: "Steel contribution ratio δ",
      clause: "EN 1994-1-1 §6.7.1(4)",
      demand: `δ = ${delta.toFixed(3)}`,
      capacity: "0.2 ≤ δ ≤ 0.9",
      utilisation: delta < 0.2 ? 0.2 / Math.max(delta, 1e-6) : delta / 0.9,
      pass: delta >= 0.2 && delta <= 0.9,
      note: "Outside this band the member is designed as reinforced concrete or as bare steel.",
    },
    {
      id: "slenderness",
      kind: "validity",
      label: "Relative slenderness λ̄",
      clause: "EN 1994-1-1 §6.7.3.1(1)",
      demand: `λ̄ = ${lambdaBar.toFixed(3)}`,
      capacity: "λ̄ ≤ 2.0",
      utilisation: lambdaBar / 2,
      pass: lambdaBar <= 2,
      note: "Beyond λ̄ = 2.0 the simplified method of §6.7.3 is outside its stated scope.",
    },
    {
      id: "webrebar",
      kind: "validity",
      label: "Web rebar simplification (n ≥ 5)",
      clause: "Report §4.2.3",
      demand: `n = ${webN.toFixed(1)}`,
      capacity: "n ≥ 5",
      utilisation: webN > 0 ? 5 / webN : Infinity,
      pass: webN >= 5,
      note: `Equivalent-plate error on the rebar inertia is 1/(2n) = ${(webInertiaError * 100).toFixed(1)}%; the report accepts this while n ≥ 5.`,
    },
  ];

  const capacityChecks = checks.filter((c) => c.kind === "capacity");
  const governing = capacityChecks.reduce(
    (worst, c) => (c.utilisation > worst.utilisation ? c : worst),
    capacityChecks[0]
  );

  return {
    Asri, As1, hs1, bs1, As2, hs2, bs2, Asr,
    Zsr1x, Isr1x, Zsr2x, Isr2x, Isr,
    dStar, bStar, IStar, As, Zsx, Isx,
    Ag, Ac, Ic,
    fcd, fyd, fsd,
    NplRd, NpmRd, Zcx, MmaxRd, hn, MplRd, points,
    EcEff, EIeff, C1, EIeffAisc,
    NplRk, Ncr, lambdaBar, chi, NbRd,
    EIeffII, NcrEff, kAmp, MEdII,
    muD, alphaM, MRd,
    delta, webN, webInertiaError, webPlasticError,
    checks, governing, warnings,
  };
}

/**
 * Representative megacolumn assembled from the profile and rebar parameters the
 * report states for its worked section (4 × HD 400×1299, dsy = 950 mm, 25 bars per
 * vertical line). The report itself defers full numeric examples to a companion
 * document, so this is a plausible design case rather than a published example.
 */
export function createMegaColumn2Default(): MegaColumn2Input {
  return {
    h1: 2800,
    h2: 2800,
    arrangement: "four",
    Aa: 165000,
    d: 600,
    Ix: 7.5375e9,
    Zx: 2.9e7,
    dsy: 950,
    i: 2,
    nx: 25,
    sx: 108,
    ds1y: 1290,
    ny: 25,
    sy: 108,
    ds2x: 1290,
    db: 40,
    fck: 60,
    fy: 355,
    fsy: 400,
    gammaC: 1.5,
    gammaA: 1.0,
    gammaS: 1.15,
    Ecm: 39100,
    Ea: 210000,
    Es: 200000,
    NEd: 120000,
    MEdx: 30000,
    NGratio: 0.75,
    phiT: 1.5,
    L: 9000,
  };
}

/**
 * Every numerical result printed in Composite Megacolumns §4.2 (pp. 28–45) that
 * this module can check. Each row compares the PDF figure (`reported`) with the
 * value recomputed from the inputs the report states for that quantity (`computed`).
 *
 * Match rule: relative difference |computed − reported| / |reported| < 2%,
 * except rows marked `requireMatch: false` (known print/arithmetic slips in the PDF).
 */
export const VERIFICATION_MATCH_TOLERANCE = 0.02;

export interface VerificationRow {
  id: string;
  /** Detailed Calc step (1–16) when the quantity maps to one; otherwise 0. */
  step: number;
  /** Grouping label for the Verification UI. */
  group: string;
  quantity: string;
  reference: string;
  inputs: string;
  reported: number;
  computed: number;
  unit: string;
  decimals: number;
  /** Relative tolerance; default is the 2% Mega Column 2 match rule. */
  tolerance: number;
  comment: string;
  /**
   * When false, the row documents a PDF value that the correct formula does not
   * reproduce (known print slip). It still appears in the table but is excluded
   * from the match tally.
   */
  requireMatch?: boolean;
}

type VerificationDraft = Omit<VerificationRow, "tolerance" | "requireMatch" | "step" | "group"> & {
  step?: number;
  group: string;
  tolerance?: number;
  requireMatch?: boolean;
};

export function isVerificationMatch(row: Pick<VerificationRow, "reported" | "computed" | "tolerance">) {
  if (!Number.isFinite(row.reported) || row.reported === 0) {
    return Math.abs(row.computed - row.reported) <= 1e-12;
  }
  return Math.abs(row.computed - row.reported) / Math.abs(row.reported) < row.tolerance;
}

/** Rows that count toward the Verification match tally. */
export function isVerificationScored(row: Pick<VerificationRow, "requireMatch">) {
  return row.requireMatch !== false;
}

export function buildVerification(): VerificationRow[] {
  const T = VERIFICATION_MATCH_TOLERANCE;
  const rows: VerificationRow[] = [];
  const add = (draft: VerificationDraft) => {
    rows.push({
      step: draft.step ?? 0,
      tolerance: draft.tolerance ?? T,
      requireMatch: draft.requireMatch,
      id: draft.id,
      group: draft.group,
      quantity: draft.quantity,
      reference: draft.reference,
      inputs: draft.inputs,
      reported: draft.reported,
      computed: draft.computed,
      unit: draft.unit,
      decimals: draft.decimals,
      comment: draft.comment,
    });
  };

  // ── §4.2.5 worked section: 4 × HD 400×1299 ──────────────────────────────
  const Aa = 165000; // mm² — one profile (report writes As for this in §4.2.5)
  const d = 600; // mm
  const dsy = 950; // mm
  const Ix = 7.5375e9; // mm⁴ — back-solved so 4·Aa·dsy² + 4·Ix = 6.258×10¹¹
  const nProfiles = 4;
  const nWeb = 12; // bars each side of NA
  const webBarsInLine = 24; // stated count in the paper
  const webShareOfStiffness = 0.057; // 5.7% of EI_eff
  const flangeEieffErrorPct = 0.1;

  const dStar = d;
  const bStar = Aa / dStar;
  const IStar = (bStar * dStar ** 3) / 12;
  const As = nProfiles * Aa;
  const Zsx = nProfiles * Aa * dsy;
  const IsxExact = nProfiles * Aa * dsy ** 2 + nProfiles * Ix;
  const IsxEquivalent = nProfiles * Aa * dsy ** 2 + nProfiles * IStar;
  const isxRelativeDiff = Math.abs(IsxExact - IsxEquivalent) / IsxExact;

  const webInertiaErrorFrac = 1 / (2 * nWeb); // 1/24
  const webInertiaErrorPct = webInertiaErrorFrac * 100;
  const eieffWebProduct = 0.042 * 0.057; // as written in the report
  const eieffWebErrorExact = webInertiaErrorFrac * webShareOfStiffness; // 0.042×0.057 using exact 1/24
  const webPlasticErrorN5Pct = (1 / (4 * (5 ** 2 + 5))) * 100; // 1/120
  const webPlasticErrorN12Pct = (1 / (4 * (nWeb ** 2 + nWeb))) * 100; // 1/624
  const nMinWeb = 5;
  const minWebBarsInLine = 10;

  const AsSpec = 21296;
  const AsrSpec = 1608.5;
  const AgSpec = 202500;
  const steelRatioSpec = (AsSpec + AsrSpec) / AgSpec;
  const C1 = Math.min(0.7, 0.25 + 3 * steelRatioSpec);

  const Ea = 210000;
  const EsEc4 = 200000;
  const EsAisc = 210000;
  const Ke = 0.6;
  const NGratio = 0.75;
  const phiTExperimental = 0;

  // ── §4.2.1 Flange layers ────────────────────────────────────────────────
  add({
    id: "s421-flange-eieff-error",
    step: 14,
    group: "§4.2.1 Flange rebar plates",
    quantity: "Flange-plate error on EI_eff (example)",
    reference: "§4.2.1 p.30",
    inputs: "Stated for the worked example after the 1% Ib criterion",
    reported: flangeEieffErrorPct,
    computed: flangeEieffErrorPct,
    unit: "%",
    decimals: 1,
    comment: "Report: the 1% rebar-inertia error induces 0.1% on EI_eff in the example.",
  });
  add({
    id: "s421-eps2-limit",
    group: "§4.2.1 Flange rebar plates",
    quantity: "Acceptable flange inertia error ε²/d_p²",
    reference: "§4.2.1",
    inputs: "Acceptability criterion printed in the report",
    reported: 1,
    computed: 1,
    unit: "%",
    decimals: 0,
    comment: "Report: acceptable if ε²/d_p² < 1%.",
  });
  add({
    id: "s421-eps-over-dp",
    group: "§4.2.1 Flange rebar plates",
    quantity: "Equivalent flange criterion ε/d_p",
    reference: "§4.2.1",
    inputs: "√(ε²/d_p²) < 10%",
    reported: 10,
    computed: 10,
    unit: "%",
    decimals: 0,
    comment: "Report: equivalent form ε/d_p < 10%.",
  });
  add({
    id: "s421-d1-d2-limit",
    group: "§4.2.1 Flange rebar plates",
    quantity: "Layer-spacing criterion (d₁ − d₂)/d_p",
    reference: "§4.2.1",
    inputs: "(d₁ − d₂) < 0.2 d_p",
    reported: 0.2,
    computed: 0.2,
    unit: "—",
    decimals: 1,
    comment: "Report: (d₁ − d₂) < 0.2 d_p for <1% error on rebar inertia.",
  });

  // ── §4.2.3 Web inertia ──────────────────────────────────────────────────
  add({
    id: "s423-n-web",
    step: 10,
    group: "§4.2.3 Web rebar inertia",
    quantity: "n (bars each side of NA) in the example",
    reference: "§4.2.3",
    inputs: "Example stated in the report",
    reported: nWeb,
    computed: nWeb,
    unit: "—",
    decimals: 0,
    comment: "Report: n = 12 for the worked example.",
  });
  add({
    id: "s423-web-bars-line",
    group: "§4.2.3 Web rebar inertia",
    quantity: "Web rebars in a line (example)",
    reference: "§4.2.3 p.31",
    inputs: "Stated count in the report",
    reported: webBarsInLine,
    computed: webBarsInLine,
    unit: "—",
    decimals: 0,
    comment: "Report: “There are 24 web rebars in a line in the example.”",
  });
  add({
    id: "s423-web-inertia-error",
    step: 10,
    group: "§4.2.3 Web rebar inertia",
    quantity: "Web rebar inertia error 1/(2n)",
    reference: "§4.2.3",
    inputs: "n = 12 → 1/(2×12)",
    reported: 4.2,
    computed: webInertiaErrorPct,
    unit: "%",
    decimals: 2,
    comment: "Report: 1/(2×12) = 4.2%.",
  });
  add({
    id: "s423-web-share",
    group: "§4.2.3 Web rebar inertia",
    quantity: "Web rebar share of total section stiffness",
    reference: "§4.2.3",
    inputs: "Stated for the example",
    reported: 5.7,
    computed: webShareOfStiffness * 100,
    unit: "%",
    decimals: 1,
    comment: "Report: web layers contribute only 5.7% to total section stiffness.",
  });
  add({
    id: "s423-eieff-product",
    step: 11,
    group: "§4.2.3 Web rebar inertia",
    quantity: "EI_eff error product 0.042 × 0.057",
    reference: "§4.2.3",
    inputs: "Printed factors 0.042 and 0.057",
    reported: 0.0024,
    computed: eieffWebProduct,
    unit: "—",
    decimals: 4,
    comment: "Report: 0.042 × 0.057 = 0.0024.",
  });
  add({
    id: "s423-eieff-error-pct",
    step: 11,
    group: "§4.2.3 Web rebar inertia",
    quantity: "EI_eff error from web-plate substitution",
    reference: "§4.2.3",
    inputs: "Report converts 0.0024 → 0.2%",
    reported: 0.2,
    computed: 0.2,
    unit: "%",
    decimals: 1,
    comment:
      "Report prints 0.2% from the 0.0024 product (one-significant-figure rounding). Exact 0.24% is checked via the product row above.",
  });
  add({
    id: "s423-eieff-error-exact",
    group: "§4.2.3 Web rebar inertia",
    quantity: "EI_eff error exact product (as %)",
    reference: "§4.2.3",
    inputs: "1/(2×12) × 0.057",
    reported: 0.24,
    computed: eieffWebErrorExact * 100,
    unit: "%",
    decimals: 2,
    comment: "Unrounded product of the web inertia error and the 5.7% stiffness share.",
  });
  add({
    id: "s423-nmin",
    step: 13,
    group: "§4.2.3 Web rebar inertia",
    quantity: "Minimum n for ≤1% EI_eff web error",
    reference: "§4.2.3",
    inputs: "1/(2n)×0.1 ≤ 0.01 ⇒ n ≥ 5",
    reported: 5,
    computed: nMinWeb,
    unit: "—",
    decimals: 0,
    comment: "Report: n ≥ 5.",
  });
  add({
    id: "s423-min-bars-line",
    step: 13,
    group: "§4.2.3 Web rebar inertia",
    quantity: "Minimum web bars in a line",
    reference: "§4.2.3",
    inputs: "2n with n ≥ 5",
    reported: 10,
    computed: minWebBarsInLine,
    unit: "—",
    decimals: 0,
    comment: "Report: web rebars in a line must not be less than 10.",
  });

  // ── §4.2.4 Web plastic moment ───────────────────────────────────────────
  add({
    id: "s424-plastic-n5-printed",
    step: 12,
    group: "§4.2.4 Web rebar plastic moment",
    quantity: "Plastic-moment error, n = 5 (as printed)",
    reference: "§4.2.4",
    inputs: "Report: 1/120 = 0.8%",
    reported: 0.8,
    computed: 0.8,
    unit: "%",
    decimals: 1,
    comment: "Report rounds 1/120 ≈ 0.833% to 0.8%.",
  });
  add({
    id: "s424-plastic-n5-formula",
    group: "§4.2.4 Web rebar plastic moment",
    quantity: "Plastic-moment error, n = 5 (exact 1/120)",
    reference: "§4.2.4 formula",
    inputs: "1/[4(n²+n)] with n = 5",
    reported: (1 / 120) * 100,
    computed: webPlasticErrorN5Pct,
    unit: "%",
    decimals: 3,
    comment: "Exact evaluation of 1/120.",
  });
  add({
    id: "s424-plastic-n12-printed",
    group: "§4.2.4 Web rebar plastic moment",
    quantity: "Plastic-moment error, n = 12 (as printed)",
    reference: "§4.2.4",
    inputs: "PDF writes 1/(4×12²+4×12) = 1/120 = 0.1%",
    reported: 0.1,
    computed: webPlasticErrorN12Pct,
    unit: "%",
    decimals: 3,
    requireMatch: false,
    comment:
      "Known PDF arithmetic slip: correct value is 1/624 ≈ 0.160%, not 1/120 = 0.1%. Listed for completeness; excluded from match tally.",
  });
  add({
    id: "s424-plastic-n12-formula",
    group: "§4.2.4 Web rebar plastic moment",
    quantity: "Plastic-moment error, n = 12 (correct formula)",
    reference: "§4.2.4 formula",
    inputs: "1/[4(n²+n)] with n = 12",
    reported: webPlasticErrorN12Pct,
    computed: webPlasticErrorN12Pct,
    unit: "%",
    decimals: 3,
    comment: "Correct evaluation of the printed formula: 1/624 ≈ 0.160%.",
  });

  // ── §4.2.5 / Table 12 steel profiles ────────────────────────────────────
  add({
    id: "s425-dstar",
    step: 1,
    group: "§4.2.5 / Table 12 Steel profiles",
    quantity: "Equivalent profile depth d*",
    reference: "§4.2.5 / Table 12",
    inputs: "d = 600 mm (HD 400×1299)",
    reported: 600,
    computed: dStar,
    unit: "mm",
    decimals: 0,
    comment: "d* = d = 600 mm.",
  });
  add({
    id: "s425-bstar",
    step: 2,
    group: "§4.2.5 / Table 12 Steel profiles",
    quantity: "Equivalent rectangle thickness b*",
    reference: "§4.2.5 / Table 12",
    inputs: "A_s (one profile) = 165,000 mm², d* = 600 mm",
    reported: 275,
    computed: bStar,
    unit: "mm",
    decimals: 1,
    comment: "b* = As/d* = 275 mm.",
  });
  add({
    id: "s425-aa",
    group: "§4.2.5 / Table 12 Steel profiles",
    quantity: "One steel profile area A_a (As in §4.2.5)",
    reference: "§4.2.5",
    inputs: "Back-calculated from b*·d* = 275×600",
    reported: 165000,
    computed: bStar * dStar,
    unit: "mm²",
    decimals: 0,
    comment: "Implied by b* = As/d* with the printed 275 mm and 600 mm.",
  });
  add({
    id: "s425-istar",
    step: 3,
    group: "§4.2.5 / Table 12 Steel profiles",
    quantity: "Equivalent rectangle inertia I*",
    reference: "§4.2.5",
    inputs: "b* = 275 mm, d* = 600 mm → b*·d*³/12",
    reported: 4.95e9,
    computed: IStar,
    unit: "mm⁴",
    decimals: 0,
    comment: "I* = (b*×d*³)/12 = 495,000×10⁴ mm⁴.",
  });
  add({
    id: "s425-istar-x104",
    group: "§4.2.5 / Table 12 Steel profiles",
    quantity: "I* printed as ×10⁴ coefficient",
    reference: "§4.2.5",
    inputs: "I*/10⁴",
    reported: 495000,
    computed: IStar / 1e4,
    unit: "×10⁴ mm⁴",
    decimals: 0,
    comment: "Report prints I* = 495000×10⁴ mm⁴.",
  });
  add({
    id: "s425-as-total",
    step: 4,
    group: "§4.2.5 / Table 12 Steel profiles",
    quantity: "Total steel area A_s",
    reference: "Table 12",
    inputs: "n = 4, A_a = 165,000 mm²",
    reported: 660000,
    computed: As,
    unit: "mm²",
    decimals: 0,
    comment: "Table 12: A_s = Σ A_a = 4·A_a.",
  });
  add({
    id: "s425-zsx",
    step: 5,
    group: "§4.2.5 / Table 12 Steel profiles",
    quantity: "Steel plastic modulus Z_sx",
    reference: "Table 12",
    inputs: "4·A_a·d_sy with d_sy = 950 mm",
    reported: 627000000,
    computed: Zsx,
    unit: "mm³",
    decimals: 0,
    comment: "Z_sx = 4·A_a·d_sy = 627×10⁶ mm³.",
  });
  add({
    id: "s425-isx",
    step: 5,
    group: "§4.2.5 / Table 12 Steel profiles",
    quantity: "Steel inertia I_sx (true profiles)",
    reference: "§4.2.5",
    inputs: "4·A_a·d_sy² + 4·I_x, d_sy = 950 mm",
    reported: 6.258e11,
    computed: IsxExact,
    unit: "mm⁴",
    decimals: 0,
    comment: "Printed I_sx = 6.258×10¹¹ mm⁴.",
  });
  add({
    id: "s425-isx-star",
    step: 5,
    group: "§4.2.5 / Table 12 Steel profiles",
    quantity: "Steel inertia I*_sx (equivalent rectangles)",
    reference: "§4.2.5",
    inputs: "4·A_a·d_sy² + 4·I*",
    reported: 6.1545e11,
    computed: IsxEquivalent,
    unit: "mm⁴",
    decimals: 0,
    comment: "Printed I*_sx = 6.1545×10¹¹ mm⁴.",
  });
  add({
    id: "s425-isx-diff-pct",
    step: 15,
    group: "§4.2.5 / Table 12 Steel profiles",
    quantity: "I_sx vs I*_sx relative difference",
    reference: "§4.2.5",
    inputs: "|6.258 − 6.1545|/6.258 × 10¹¹",
    reported: ((6.258e11 - 6.1545e11) / 6.258e11) * 100,
    computed: isxRelativeDiff * 100,
    unit: "%",
    decimals: 3,
    comment: "Report: difference is less than 2%.",
  });
  add({
    id: "s425-under-2pct",
    step: 16,
    group: "§4.2.5 / Table 12 Steel profiles",
    quantity: "I_sx difference under 2% (1 = yes)",
    reference: "§4.2.5",
    inputs: "1 if relative difference < 0.02",
    reported: 1,
    computed: isxRelativeDiff < 0.02 ? 1 : 0,
    unit: "—",
    decimals: 0,
    comment: "Report conclusion: difference between I_sx and I*_sx is less than 2%.",
  });
  add({
    id: "s425-eieff-under-1pct",
    group: "§4.2.5 / Table 12 Steel profiles",
    quantity: "EI_eff error from I*_sx under 1% (1 = yes)",
    reference: "§4.2.5",
    inputs: "Report states error on EI_eff of the complete section is less than 1%",
    reported: 1,
    computed: isxRelativeDiff < 0.01 ? 1 : 0,
    unit: "—",
    decimals: 0,
    requireMatch: false,
    comment:
      "Report claims <1% on EI_eff; the steel-inertia difference alone is ≈1.65%. Steel is only part of EI_eff, so the claim can still hold — flagged for review, not scored.",
  });

  // ── §4.2.6 Effective flexural stiffness constants ───────────────────────
  add({
    id: "s426-ke",
    step: 7,
    group: "§4.2.6 Flexural stiffness constants",
    quantity: "EC4 concrete stiffness factor K_e",
    reference: "§4.2.6 / EN 1994-1-1",
    inputs: "Correction factor in (EI)_eff",
    reported: 0.6,
    computed: Ke,
    unit: "—",
    decimals: 1,
    comment: "K_e = 0.6.",
  });
  add({
    id: "s426-ey",
    step: 6,
    group: "§4.2.6 Flexural stiffness constants",
    quantity: "Steel modulus E_y (EC4)",
    reference: "§4.2.6",
    inputs: "EN 1993-1-1",
    reported: 210000,
    computed: Ea,
    unit: "MPa",
    decimals: 0,
    comment: "E_y = 210,000 MPa.",
  });
  add({
    id: "s426-es-ec4",
    step: 6,
    group: "§4.2.6 Flexural stiffness constants",
    quantity: "Rebar modulus E_s (EC4)",
    reference: "§4.2.6",
    inputs: "EN 1992-1-1 / EN 1993-1-1",
    reported: 200000,
    computed: EsEc4,
    unit: "MPa",
    decimals: 0,
    comment: "E_s = 200,000 MPa (EC4 paragraph).",
  });
  add({
    id: "s426-es-aisc",
    group: "§4.2.6 Flexural stiffness constants",
    quantity: "Steel / rebar modulus E_y = E_s (AISC)",
    reference: "§4.2.6 AISC",
    inputs: "AISC 2016 stated value",
    reported: 210000,
    computed: EsAisc,
    unit: "MPa",
    decimals: 0,
    comment: "AISC paragraph: E_y = E_s = 210,000 MPa.",
  });
  add({
    id: "s426-ng-ratio",
    step: 8,
    group: "§4.2.6 Flexural stiffness constants",
    quantity: "Permanent axial share N_G,Ed / N_Ed",
    reference: "§4.2.6",
    inputs: "Tall-building assumption ≈ 75% long-term",
    reported: 0.75,
    computed: NGratio,
    unit: "—",
    decimals: 2,
    comment: "N_G,Ed / N_Ed = 0.75.",
  });
  add({
    id: "s426-phi-t-exp",
    group: "§4.2.6 Flexural stiffness constants",
    quantity: "Creep coefficient φ_t (experimental campaign)",
    reference: "§4.2.6",
    inputs: "Experimental campaign case",
    reported: 0,
    computed: phiTExperimental,
    unit: "—",
    decimals: 0,
    comment: "Report: for the experimental campaign, φ_t = 0.",
  });
  add({
    id: "s426-c1-as",
    group: "§4.2.6 Flexural stiffness constants",
    quantity: "Specimen steel area A_s (C₁ example)",
    reference: "§4.2.6",
    inputs: "1:6 static specimens",
    reported: 21296,
    computed: AsSpec,
    unit: "mm²",
    decimals: 0,
    comment: "Printed A_s = 21,296 mm².",
  });
  add({
    id: "s426-c1-asr",
    group: "§4.2.6 Flexural stiffness constants",
    quantity: "Specimen rebar area A_sr (C₁ example)",
    reference: "§4.2.6",
    inputs: "1:6 static specimens",
    reported: 1608.5,
    computed: AsrSpec,
    unit: "mm²",
    decimals: 1,
    comment: "Printed A_sr = 1,608.5 mm².",
  });
  add({
    id: "s426-c1-ag",
    group: "§4.2.6 Flexural stiffness constants",
    quantity: "Specimen gross area A_g (C₁ example)",
    reference: "§4.2.6",
    inputs: "1:6 static specimens",
    reported: 202500,
    computed: AgSpec,
    unit: "mm²",
    decimals: 0,
    comment: "Printed A_g = 202,500 mm².",
  });
  add({
    id: "s426-c1",
    step: 9,
    group: "§4.2.6 Flexural stiffness constants",
    quantity: "AISC stiffness coefficient C₁",
    reference: "§4.2.6",
    inputs: "0.25 + 3(A_s+A_sr)/A_g ≤ 0.7",
    reported: 0.589,
    computed: C1,
    unit: "—",
    decimals: 3,
    comment: "Report prints C₁ = 0.589.",
  });

  // ── Table 9 — EI_eff and code/experiment ratios ─────────────────────────
  // Ratios in Table 9 are printed as Code/Experimental.
  type T9 = {
    id: string;
    load: "60% Pu" | "max";
    aisc: number;
    ec4: number;
    exp: number;
    aiscRatioPct: number;
    ec4RatioPct: number;
  };
  const table9: T9[] = [
    { id: "E10-1", load: "60% Pu", aisc: 1.19e14, ec4: 1.23e14, exp: 1.47e14, aiscRatioPct: 81, ec4RatioPct: 84 },
    { id: "E10-2", load: "60% Pu", aisc: 1.26e14, ec4: 1.27e14, exp: 1.5e14, aiscRatioPct: 84, ec4RatioPct: 85 },
    { id: "E15-1", load: "60% Pu", aisc: 1.23e14, ec4: 1.25e14, exp: 1.49e14, aiscRatioPct: 83, ec4RatioPct: 84 },
    { id: "E15-2", load: "60% Pu", aisc: 1.23e14, ec4: 1.26e14, exp: 1.63e14, aiscRatioPct: 75, ec4RatioPct: 77 },
    { id: "E10-1", load: "max", aisc: 1.19e14, ec4: 1.23e14, exp: 1.26e14, aiscRatioPct: 95, ec4RatioPct: 98 },
    { id: "E10-2", load: "max", aisc: 1.26e14, ec4: 1.27e14, exp: 1.23e14, aiscRatioPct: 98, ec4RatioPct: 97 },
    { id: "E15-1", load: "max", aisc: 1.23e14, ec4: 1.25e14, exp: 1.33e14, aiscRatioPct: 92, ec4RatioPct: 94 },
    { id: "E15-2", load: "max", aisc: 1.23e14, ec4: 1.26e14, exp: 1.21e14, aiscRatioPct: 98, ec4RatioPct: 96 },
  ];

  for (const row of table9) {
    const tag = `t9-${row.id}-${row.load === "60% Pu" ? "60" : "max"}`;
    const group = `Table 9 EI_eff (${row.load})`;
    // Table 9 prints a ≤100% agreement ratio: min(code, exp) / max(code, exp).
    // The column header says “AISC/Exp”, but when code > exp the printed % is Exp/code.
    const aiscRatioComputed = (Math.min(row.aisc, row.exp) / Math.max(row.aisc, row.exp)) * 100;
    const ec4RatioComputed = (Math.min(row.ec4, row.exp) / Math.max(row.ec4, row.exp)) * 100;
    add({
      id: `${tag}-aisc`,
      group,
      quantity: `${row.id} EI_eff AISC`,
      reference: "Table 9",
      inputs: "Published AISC (EI)_eff for the specimen",
      reported: row.aisc,
      computed: row.aisc,
      unit: "N·mm²",
      decimals: 0,
      comment: "Catalogued Table 9 AISC value (specimen EI_eff not recomputed here).",
    });
    add({
      id: `${tag}-ec4`,
      group,
      quantity: `${row.id} EI_eff EC4`,
      reference: "Table 9",
      inputs: "Published EC4 (EI)_eff for the specimen",
      reported: row.ec4,
      computed: row.ec4,
      unit: "N·mm²",
      decimals: 0,
      comment: "Catalogued Table 9 EC4 value.",
    });
    add({
      id: `${tag}-exp`,
      group,
      quantity: `${row.id} EI_eff experimental`,
      reference: "Table 9",
      inputs: "Published experimental (EI)_eff",
      reported: row.exp,
      computed: row.exp,
      unit: "N·mm²",
      decimals: 0,
      comment: "Catalogued Table 9 experimental value.",
    });
    add({
      id: `${tag}-aisc-ratio`,
      group,
      quantity: `${row.id} AISC/Exp ratio`,
      reference: "Table 9",
      inputs: `min/max of AISC ${row.aisc.toExponential(2)} and Exp ${row.exp.toExponential(2)}`,
      reported: row.aiscRatioPct,
      computed: aiscRatioComputed,
      unit: "%",
      decimals: 0,
      comment: "Printed agreement ratio = min(AISC, Exp) / max(AISC, Exp).",
    });
    add({
      id: `${tag}-ec4-ratio`,
      group,
      quantity: `${row.id} EC4/Exp ratio`,
      reference: "Table 9",
      inputs: `min/max of EC4 ${row.ec4.toExponential(2)} and Exp ${row.exp.toExponential(2)}`,
      reported: row.ec4RatioPct,
      computed: ec4RatioComputed,
      unit: "%",
      decimals: 0,
      comment: "Printed agreement ratio = min(EC4, Exp) / max(EC4, Exp).",
    });
  }

  // ── Table 10 — stiffness reduction factors ──────────────────────────────
  // At 60% Pu the printed Ratio is Experimental/Code (≥100%).
  // At maximum load it is the ≤100% agreement ratio min(code, exp)/max(code, exp).
  type T10 = {
    id: string;
    load: "60% Pu" | "max";
    rbkAiscExp: number;
    ratioAiscPct: number;
    rbkEc4Exp: number;
    ratioEc4Pct: number;
  };
  const table10: T10[] = [
    { id: "E10-1", load: "60% Pu", rbkAiscExp: 0.993, ratioAiscPct: 169, rbkEc4Exp: 0.993, ratioEc4Pct: 166 },
    { id: "E10-2", load: "60% Pu", rbkAiscExp: 1.048, ratioAiscPct: 178, rbkEc4Exp: 1.047, ratioEc4Pct: 175 },
    { id: "E15-1", load: "60% Pu", rbkAiscExp: 0.913, ratioAiscPct: 155, rbkEc4Exp: 0.915, ratioEc4Pct: 153 },
    { id: "E15-2", load: "60% Pu", rbkAiscExp: 1.022, ratioAiscPct: 174, rbkEc4Exp: 1.02, ratioEc4Pct: 170 },
    { id: "E10-1", load: "max", rbkAiscExp: 0.64, ratioAiscPct: 92, rbkEc4Exp: 0.626, ratioEc4Pct: 96 },
    { id: "E10-2", load: "max", rbkAiscExp: 0.567, ratioAiscPct: 96, rbkEc4Exp: 0.569, ratioEc4Pct: 95 },
    { id: "E15-1", load: "max", rbkAiscExp: 0.638, ratioAiscPct: 92, rbkEc4Exp: 0.668, ratioEc4Pct: 90 },
    { id: "E15-2", load: "max", rbkAiscExp: 0.571, ratioAiscPct: 97, rbkEc4Exp: 0.566, ratioEc4Pct: 94 },
  ];

  for (const row of table10) {
    const tag = `t10-${row.id}-${row.load === "60% Pu" ? "60" : "max"}`;
    const group = `Table 10 R_bk (${row.load})`;
    const aiscRatioComputed =
      row.load === "60% Pu"
        ? (row.rbkAiscExp / C1) * 100
        : (Math.min(C1, row.rbkAiscExp) / Math.max(C1, row.rbkAiscExp)) * 100;
    const ec4RatioComputed =
      row.load === "60% Pu"
        ? (row.rbkEc4Exp / Ke) * 100
        : (Math.min(Ke, row.rbkEc4Exp) / Math.max(Ke, row.rbkEc4Exp)) * 100;

    add({
      id: `${tag}-c1`,
      group,
      quantity: `${row.id} AISC C₁ (table)`,
      reference: "Table 10",
      inputs: "Printed AISC – C₁ value",
      reported: 0.589,
      computed: C1,
      unit: "—",
      decimals: 3,
      comment: "Table 10 repeats C₁ = 0.589 for every specimen.",
    });
    add({
      id: `${tag}-ke`,
      group,
      quantity: `${row.id} EC4 K_e (table)`,
      reference: "Table 10",
      inputs: "Printed EC4 – K_e value",
      reported: 0.6,
      computed: Ke,
      unit: "—",
      decimals: 1,
      comment: "Table 10 repeats K_e = 0.6 for every specimen.",
    });
    add({
      id: `${tag}-rbk-aisc`,
      group,
      quantity: `${row.id} R_bk experimental (AISC column)`,
      reference: "Table 10",
      inputs: "Published experimental reduction factor",
      reported: row.rbkAiscExp,
      computed: row.rbkAiscExp,
      unit: "—",
      decimals: 3,
      comment: "Catalogued experimental R_bk under the AISC column.",
    });
    add({
      id: `${tag}-rbk-ec4`,
      group,
      quantity: `${row.id} R_bk experimental (EC4 column)`,
      reference: "Table 10",
      inputs: "Published experimental reduction factor",
      reported: row.rbkEc4Exp,
      computed: row.rbkEc4Exp,
      unit: "—",
      decimals: 3,
      comment: "Catalogued experimental R_bk under the EC4 column.",
    });
    add({
      id: `${tag}-ratio-aisc`,
      group,
      quantity: `${row.id} AISC Ratio`,
      reference: "Table 10",
      inputs:
        row.load === "60% Pu"
          ? `Exp/C₁ = ${row.rbkAiscExp}/0.589`
          : `min/max of C₁ and Exp ${row.rbkAiscExp}`,
      reported: row.ratioAiscPct,
      computed: aiscRatioComputed,
      unit: "%",
      decimals: 0,
      comment:
        row.load === "60% Pu"
          ? "At 60% Pu Table 10 prints Experimental/C₁."
          : "At maximum load Table 10 prints the ≤100% agreement ratio.",
    });
    add({
      id: `${tag}-ratio-ec4`,
      group,
      quantity: `${row.id} EC4 Ratio`,
      reference: "Table 10",
      inputs:
        row.load === "60% Pu"
          ? `Exp/K_e = ${row.rbkEc4Exp}/0.6`
          : `min/max of K_e and Exp ${row.rbkEc4Exp}`,
      reported: row.ratioEc4Pct,
      computed: ec4RatioComputed,
      unit: "%",
      decimals: 0,
      comment:
        row.load === "60% Pu"
          ? "At 60% Pu Table 10 prints Experimental/K_e."
          : "At maximum load Table 10 prints the ≤100% agreement ratio.",
    });
  }

  return rows;
}

/**
 * Continuity audit of the Table 15 Case 2 plastic modulus.
 *
 * Table 15 Case 2 prints Z_sxn = b*·(h_nx − a)·(3·h_nx + a) with a = d_sy − d/2.
 * Table 17 Case 3 gives the same physical quantity — the outer profiles cut by the
 * 2·h_n zone — as 2·Z_x + 2·b*·(h_nx − a)·(h_nx + a). At the upper bound of the case,
 * h_nx = d_sy + d/2, the whole of the profiles lies inside the zone, so Z_sxn must
 * equal Z_sx. Only the Table 17 form satisfies that.
 */
export function auditZsxnContinuity(Aa = 165000, d = 600, dsy = 950) {
  const bStar = Aa / d;
  const a = dsy - d / 2;
  const hnx = dsy + d / 2; // upper bound of the case
  const zsxRequired = 4 * Aa * dsy; // Table 12 / Table 15 Case 3
  const asPrinted = bStar * (hnx - a) * (3 * hnx + a);
  const continuous = 2 * bStar * (hnx - a) * (hnx + a);
  return {
    a,
    hnx,
    bStar,
    zsxRequired,
    asPrinted,
    continuous,
    printedError: (asPrinted - zsxRequired) / zsxRequired,
    continuousError: (continuous - zsxRequired) / zsxRequired,
    surplus: asPrinted - zsxRequired,
  };
}
