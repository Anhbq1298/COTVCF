import { describe, expect, it } from "vitest";
import {
  auditZsxnContinuity,
  buildVerification,
  calculateMegaColumn2,
  createMegaColumn2Default,
  isVerificationMatch,
  isVerificationScored,
  VERIFICATION_MATCH_TOLERANCE,
} from "./engine";

describe("Mega Column 2 — verification against the CTBUH/ArcelorMittal report", () => {
  const rows = buildVerification();
  const scored = rows.filter(isVerificationScored);

  it("compares every published §4.2 numerical result (large set)", () => {
    expect(rows.length).toBeGreaterThanOrEqual(80);
    expect(scored.length).toBeGreaterThanOrEqual(80);
    const groups = new Set(rows.map((r) => r.group));
    expect(groups.has("§4.2.5 / Table 12 Steel profiles")).toBe(true);
    expect([...groups].some((g) => g.startsWith("Table 9"))).toBe(true);
    expect([...groups].some((g) => g.startsWith("Table 10"))).toBe(true);
  });

  it("uses the <2% match rule on scored rows", () => {
    expect(VERIFICATION_MATCH_TOLERANCE).toBe(0.02);
    expect(scored.every((r) => r.tolerance === VERIFICATION_MATCH_TOLERANCE)).toBe(true);
  });

  it.each(scored.map((r) => [r.id, r] as const))(
    "matches published value for %s within 2%",
    (_id, row) => {
      expect(isVerificationMatch(row)).toBe(true);
      if (row.reported === 0) {
        expect(Math.abs(row.computed - row.reported)).toBeLessThanOrEqual(1e-12);
      } else {
        const relative = Math.abs(row.computed - row.reported) / Math.abs(row.reported);
        expect(relative).toBeLessThan(VERIFICATION_MATCH_TOLERANCE);
      }
    }
  );

  it("flags the known §4.2.4 n=12 print slip as unscored", () => {
    const slip = rows.find((r) => r.id === "s424-plastic-n12-printed")!;
    expect(isVerificationScored(slip)).toBe(false);
    expect(isVerificationMatch(slip)).toBe(false);
  });

  it("gives b* = 275 mm for HD 400x1299", () => {
    expect(rows.find((r) => r.id === "s425-bstar")!.computed).toBeCloseTo(275, 6);
  });

  it("keeps the equivalent-rectangle steel inertia within 2% of the exact value", () => {
    const exact = rows.find((r) => r.id === "s425-isx")!.computed;
    const equivalent = rows.find((r) => r.id === "s425-isx-star")!.computed;
    expect(Math.abs(exact - equivalent) / exact).toBeLessThan(0.02);
  });

  it("reproduces Table 12 Z_sx = 4·Aa·dsy = 627×10⁶ mm³", () => {
    expect(rows.find((r) => r.id === "s425-zsx")!.computed).toBe(627_000_000);
  });

  it("reproduces Table 9 E10-1 60% Pu AISC/Exp ≈ 81%", () => {
    const row = rows.find((r) => r.id === "t9-E10-1-60-aisc-ratio")!;
    expect(row.reported).toBe(81);
    expect(isVerificationMatch(row)).toBe(true);
  });

  it("reproduces Table 10 E10-1 60% Pu Exp/C₁ ≈ 169%", () => {
    const row = rows.find((r) => r.id === "t10-E10-1-60-ratio-aisc")!;
    expect(row.reported).toBe(169);
    expect(isVerificationMatch(row)).toBe(true);
  });
});

describe("Table 15 Case 2 plastic modulus continuity", () => {
  const audit = auditZsxnContinuity();

  it("the continuity-consistent form closes exactly onto Zsx at the case boundary", () => {
    expect(audit.continuousError).toBeCloseTo(0, 12);
  });

  it("the literal Table 15 Case 2 print overshoots Zsx at the same boundary", () => {
    expect(audit.printedError).toBeGreaterThan(0);
    expect(audit.surplus).toBeCloseTo(165000 * 600, 3);
  });
});

describe("calculateMegaColumn2", () => {
  it("returns a result for the default megacolumn", () => {
    const r = calculateMegaColumn2(createMegaColumn2Default());
    expect(r).not.toBeNull();
  });

  it("orders the interaction anchor points A > C > D > B in axial force", () => {
    const r = calculateMegaColumn2(createMegaColumn2Default())!;
    const [A, C, D, B] = r.points;
    expect(A.N).toBeGreaterThan(C.N);
    expect(C.N).toBeGreaterThan(D.N);
    expect(D.N).toBeGreaterThan(B.N);
    expect(B.N).toBe(0);
  });

  it("puts Mmax,Rd at point D above Mpl,Rd at points B and C", () => {
    const r = calculateMegaColumn2(createMegaColumn2Default())!;
    expect(r.MmaxRd).toBeGreaterThan(r.MplRd);
  });

  it("keeps Npm,Rd equal to the concrete squash load", () => {
    const x = createMegaColumn2Default();
    const r = calculateMegaColumn2(x)!;
    expect(r.NpmRd).toBeCloseTo((0.85 * r.Ac * r.fcd) / 1000, 6);
  });

  it("recovers Zsx = 4·Aa·dsy for the four-profile arrangement", () => {
    const x = createMegaColumn2Default();
    const r = calculateMegaColumn2(x)!;
    expect(r.Zsx).toBeCloseTo(4 * x.Aa * x.dsy, 6);
  });

  it("recovers Isx = 4·Aa·dsy² + 4·Ix", () => {
    const x = createMegaColumn2Default();
    const r = calculateMegaColumn2(x)!;
    expect(r.Isx).toBeCloseTo(4 * x.Aa * x.dsy ** 2 + 4 * x.Ix, 3);
  });

  it("recovers Table 12 b* and As for the default section", () => {
    const x = createMegaColumn2Default();
    const r = calculateMegaColumn2(x)!;
    expect(r.bStar).toBeCloseTo(275, 6);
    expect(r.As).toBe(4 * x.Aa);
  });

  it("satisfies axial equilibrium over the 2·hn zone at the plastic neutral axis", () => {
    const x = createMegaColumn2Default();
    const r = calculateMegaColumn2(x)!;
    const fc = 0.85 * r.fcd;
    const force =
      2 * r.hn.hnx * ((x.h1 - 2 * r.bs2) * fc + 2 * r.bs2 * r.fsd);
    const a = x.dsy - x.d / 2;
    let steel = 0;
    if (r.hn.caseId === 2) {
      steel = 4 * (r.hn.hnx - a) * r.bStar * (r.fyd - fc);
    } else if (r.hn.caseId === 3) {
      steel = 4 * x.Aa * (r.fyd - fc);
    }
    const total = (force + steel) / 1000;
    expect(total).toBeCloseTo(r.NpmRd, 3);
  });

  it("rejects a section whose plates exceed the gross area", () => {
    const x = createMegaColumn2Default();
    expect(calculateMegaColumn2({ ...x, h1: 100, h2: 100 })).toBeNull();
  });

  it("rejects non-positive geometry", () => {
    const x = createMegaColumn2Default();
    expect(calculateMegaColumn2({ ...x, h2: 0 })).toBeNull();
  });

  it("flags a slenderness beyond the scope of the simplified method", () => {
    const x = { ...createMegaColumn2Default(), L: 120000 };
    const r = calculateMegaColumn2(x)!;
    const check = r.checks.find((c) => c.id === "slenderness")!;
    expect(r.lambdaBar).toBeGreaterThan(2);
    expect(check.pass).toBe(false);
  });

  it("flags too few web bars for the equivalent-plate simplification", () => {
    const x = { ...createMegaColumn2Default(), ny: 7 };
    const r = calculateMegaColumn2(x)!;
    expect(r.checks.find((c) => c.id === "webrebar")!.pass).toBe(false);
  });

  it("reports the Case 2 as-printed alternative only inside Case 2", () => {
    const r = calculateMegaColumn2(createMegaColumn2Default())!;
    if (r.hn.caseId === 2) {
      expect(r.hn.MplRdAsPrinted).not.toBeNull();
      expect(r.hn.MplRdAsPrinted!).toBeLessThan(r.hn.MplRd);
    } else {
      expect(r.hn.MplRdAsPrinted).toBeNull();
    }
  });
});

describe("check classification", () => {
  it("separates strength checks from applicability checks", () => {
    const r = calculateMegaColumn2(createMegaColumn2Default())!;
    const capacity = r.checks.filter((c) => c.kind === "capacity").map((c) => c.id);
    const validity = r.checks.filter((c) => c.kind === "validity").map((c) => c.id);
    expect(capacity).toEqual(["buckling", "interaction", "squash"]);
    expect(validity).toEqual(["delta", "slenderness", "webrebar"]);
  });

  it("never lets an applicability band check govern the design utilisation", () => {
    const r = calculateMegaColumn2(createMegaColumn2Default())!;
    expect(r.governing.kind).toBe("capacity");
    expect(r.governing.id).not.toBe("delta");
  });

  it("governs on the worst strength check", () => {
    const r = calculateMegaColumn2(createMegaColumn2Default())!;
    const worst = Math.max(
      ...r.checks.filter((c) => c.kind === "capacity").map((c) => c.utilisation)
    );
    expect(r.governing.utilisation).toBeCloseTo(worst, 9);
  });
});
