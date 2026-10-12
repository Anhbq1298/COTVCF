/**
 * Diagram crops from CTBUH / ArcelorMittal "Composite Megacolumns" (2016) §4.
 * Full table pages are not embedded — formulas are written as equations in the UI.
 */

export type ReportFigure = {
  id: string;
  src: string;
  alt: string;
  caption: string;
  page: string;
  width: number;
  height: number;
};

/** Only section / equivalent-plate diagrams (not whole table pages). */
export const REPORT_DIAGRAMS: ReportFigure[] = [
  {
    id: "section-diagram",
    src: "/design/mega-column-2/section-diagram.png",
    alt: "Composite megacolumn section with four encased profiles and As1 / As2 rebar plates",
    caption: "Table 12 section diagram — four encased profiles with As1 / As2 plate labels.",
    page: "p. 38",
    width: 380,
    height: 324,
  },
  {
    id: "equivalent-plates",
    src: "/design/mega-column-2/equivalent-plates.png",
    alt: "Equivalent horizontal As1 and vertical As2 rebar plates with b* × d* steel rectangles",
    caption: "Table 11 — equivalent As1 / As2 plates and b* × d* steel rectangles.",
    page: "p. 37",
    width: 343,
    height: 285,
  },
  {
    id: "equivalent-profiles",
    src: "/design/mega-column-2/equivalent-profiles.png",
    alt: "Four equivalent rectangular steel plates replacing rolled HD profiles",
    caption: "Table 12 — equivalent rectangles replacing the rolled profiles.",
    page: "p. 38",
    width: 380,
    height: 323,
  },
  {
    id: "figure-32",
    src: "/design/mega-column-2/figure-32.png",
    alt: "Figure 32 — flange rebar layers replaced by an equivalent plate",
    caption: "Figure 32. Flange layer of rebar and equivalent plate (§4.2.1).",
    page: "p. 30",
    width: 512,
    height: 234,
  },
  {
    id: "figure-33",
    src: "/design/mega-column-2/figure-33.png",
    alt: "Figure 33 — web rebar line replaced by an equivalent plate",
    caption: "Figure 33. Web layer of rebar and equivalent plate (§4.2.3).",
    page: "p. 31",
    width: 293,
    height: 251,
  },
  {
    id: "table-14-interaction",
    src: "/design/mega-column-2/table-14-interaction.png",
    alt: "N–M interaction polygon with points A B C D E",
    caption: "Table 14 — N–M interaction polygon (points A–E).",
    page: "p. 40",
    width: 237,
    height: 187,
  },
];

export function figureById(id: string): ReportFigure {
  const found = REPORT_DIAGRAMS.find((f) => f.id === id);
  if (!found) throw new Error(`Unknown report figure: ${id}`);
  return found;
}
