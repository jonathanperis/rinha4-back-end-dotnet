# rinha4-back-end-dotnet Design Context

## Design thesis

The site should feel like a source-backed benchmark terminal for a .NET NativeAOT Rinha submission: dark CRT, C# green signal, amber commands, hot-pink failure warnings, dense monospace labels, and explicit run provenance. The overhaul should not change the aesthetic category. It should make the current aesthetic more legible, more source-backed, and easier to scan.

Target direction: **competitive benchmark console with editorial hierarchy**.

Not: SaaS landing page, observability dashboard clone, generic cyberpunk poster, or fake terminal theater.

## Current visual system inventory

### Color tokens

Current CSS uses these core tokens:

```css
--bg-deep: #04120e;
--bg-metal: #071b14;
--term-green: #178600;        /* GitHub Linguist C# color */
--term-green-soft: #56b84d;
--term-green-dim: rgba(23, 134, 0, 0.24);
--term-green-glow: rgba(23, 134, 0, 0.55);
--text-main: #dff8dc;
--text-muted: #9acb93;
--warn-amber: #ffb300;
--kill-red: #ff2d55;
--violet-signal: #7d64ff;
```

Preserve `#178600` as the signature color. Expand only with disciplined roles:

- Green: ready, source, valid path, C# identity.
- Amber: commands, caution, score, metadata.
- Pink/red: failures, target gap, official-stale warning.
- Violet: rare compare or experiment accent only.
- Text: use brighter values for long paragraphs than current muted green when readability matters.

### Typography

Current system:

- `Share Tech Mono` and `JetBrains Mono` loaded from Google Fonts.
- All-caps labels with letter spacing.
- Huge hero headline with chromatic red/amber shadow.

Preserve monospace as the dominant brand voice. Add hierarchy through size, spacing, weight, and casing rather than font variety.

Recommended hierarchy:

- Display: `Share Tech Mono`, all caps, glitch shadow only at hero or major campaign phrase.
- Section labels: uppercase, compact, amber or muted green.
- Body copy: `JetBrains Mono` or current mono stack, sentence case, higher contrast, max `70ch` line length.
- Numeric metrics: `JetBrains Mono`, tabular look, larger than labels.
- Code/run refs: inline code with green-soft text and no heavy glow.

### Layout

Current layout:

- `site-nav`, `main`, and `footer` max at `1400px` with `calc(100% - 4rem)`.
- Desktop hero is a two-column grid, left copy and right terminal panel.
- Stats grid is four columns.
- System topology is two columns.
- Mobile collapses hero/topology to one column and stats to two/one columns.

Preserve the wide technical-cockpit feel, but introduce stronger content lanes:

1. **Hero lane:** product identity, current proof status, primary CTAs.
2. **Evidence lane:** side-by-side official versus candidate versus calibrated evidence.
3. **Runtime lane:** transport path and fraud decision cards.
4. **Archive lane:** latest/best/median report summaries before tables.
5. **Source lane:** upstream issue, comment, ranking, GitHub source, workflow run.

### Existing components

- `Navbar.astro`: brand and three links.
- `Hero.astro`: hero copy, CTA buttons, terminal panel.
- `Dashboard.astro`: official stat cards, source strip, topology, CI stream.
- `Footer.astro`: short provenance line.
- `reports/index.astro`: latest row plus paginated archive grouped by candidate, official-calibrated, experiment.
- `globals.css`: global CRT scanlines, flicker overlay, layout, buttons, panels, tables, responsive rules.

## Current rendered evaluation

Verified production build with Bun and a local static server under the production base path `/rinha4-back-end-dotnet/`.

### Strengths

- Very strong retro terminal / cyberpunk benchmark identity.
- Clear first impression: performance, fraud signal, NativeAOT, source proof.
- Hero headline is memorable and on-theme.
- Terminal panel supports the story and feels authentic because lines map to real concepts.
- Official metric cards and official source strip create trust.
- Topology cards communicate implementation shape without a diagram dependency.
- CI benchmark stream separates GitHub Actions evidence from official Rinha issue data.
- Site is static and lean, fitting the performance subject.

### Issues to fix in overhaul

1. **Evidence taxonomy is not explicit enough.** Official stale result, current candidate CI, calibrated CI, and experiments should be visually categorized as lanes.
2. **Official-stale versus current-clean can confuse visitors.** The hero currently says official failures are `2.35%` while CI later says `0%`; this is honest but needs a clear explanatory bridge.
3. **Clickable and static elements look too similar.** Endpoint badges, status pills, source links, and buttons all share bordered neon treatment.
4. **No explicit `:focus-visible` style was detected.** Add keyboard-visible focus rings that match the theme.
5. **Global flicker/scanlines can reduce text readability.** Keep the mood but reduce interference for paragraphs and respect `prefers-reduced-motion` completely.
6. **Long-form docs and report archive need stronger summary affordances.** The reports page has a lot of evidence; frontload latest/best/median and lane labels.
7. **The page leans on color semantics.** Add textual state markers so colorblind users can distinguish warning, good, stale, and experimental states.

## Proposed overhaul structure

### 1. Header: operator nav

Keep sparse nav, but make it feel intentionally bracketed:

```text
● rinha4.dotnet     [docs] [reports] [github]
```

Add optional small secondary status on desktop:

```text
candidate: 0% failures // ci score 6000
```

Rules:

- Brand remains green.
- Nav links have stronger hover and `:focus-visible` styles.
- External link indication for GitHub can be text-only: `github ↗`.

### 2. Hero: current state first, official proof always visible

Hero content should answer the evidence gap immediately.

Recommended hero stack:

```text
RINHA 2026 // .NET 10 NATIVEAOT
FRAUD SIGNAL UNDER LOAD.
Current candidate is clean in CI; latest official upstream issue is retained as historical proof.
```

Primary CTAs:

- `VERIFY UPSTREAM RESULT` -> official issue.
- `OPEN LATEST CI RUN` -> latest candidate run.
- Secondary text link: `TRACE RUNTIME PATH` -> docs architecture.

Hero terminal variant:

```text
> lane.official      issue #2088 // p99 2.01ms // failures 2.35%
> lane.candidate     run 25973947437 // p99 0.32ms // failures 0%
> lane.calibrated    api=0.40 proxy=0.20 // p99 0.31ms // score 6000
> runtime            native_aot // raw_http // uds
> target             official resubmission: 0% failures
```

This preserves the terminal panel while making the current product state clearer.

### 3. Evidence comparison band

Replace or augment the current four official cards with a lane comparison module:

| Lane | p99 | failures | score | provenance |
| --- | ---: | ---: | ---: | --- |
| Official upstream | 2.01ms | 2.35% | 3031.24 | issue #2088 |
| Latest CI candidate | 0.32ms | 0% | 6000 | run 25973947437 |
| Calibrated CI | 0.31ms | 0% | 6000 | api=0.40/proxy=0.20 |

Use cards or a compact table, but label every lane. Do not present CI as official hardware.

Recommended labels:

- `[official]` for upstream Rinha result.
- `[ci-candidate]` for normal GitHub Actions run.
- `[calibrated-ci]` for CPU-quota projection.
- `[experiment]` for failed or alternate tuning runs.

### 4. Source strip: provenance ledger

Current source strip works. Overhaul it into a provenance ledger with two sides:

- Upstream evidence: issue, result comment, ranking.
- Local evidence: latest CI run, report archive, source commit.

Keep amber border and compact buttons, but distinguish external links:

```text
OFFICIAL SOURCE
synced through gh from zanfranceschi/rinha-de-backend-2026
[issue #2088 ↗] [result comment ↗] [ranking ↗]

LOCAL EVIDENCE
main @ 4a8e42b // candidate run 25973947437
[latest ci run ↗] [report archive]
```

### 5. Runtime cards: make the topology a path

Current two cards are good. Add a thin visual connector or ordered labels:

1. `ingress` — yolo load balancer accepts `:9999`.
2. `transport` — Unix Domain Sockets to two APIs.
3. `parse` — raw HTTP/1 plus manual JSON field extraction.
4. `score` — bucket fast path plus rounded int16 IVF fallback.
5. `respond` — prebuilt response bytes.

This can remain text-first; no need for SVG unless it improves clarity.

### 6. Reports page: evidence cockpit

Before the archive table, add summaries:

- Latest candidate.
- Latest calibrated.
- Best candidate p99 with clean correctness.
- Latest experiment/failure note.
- Total runs archived.

Keep the existing paginated table, but group lanes with clear headings and filters.

### 7. Docs pages: maintain wiki, add overview rail

The docs already pull from `docs/wiki/*.md`. Improve navigation and scanability:

- Add a sticky mini table of contents on desktop.
- Add `Last generated from repo` / `source file` labels if easy.
- Use code block and table styling consistent with homepage.
- Preserve markdown as source of truth.

## A/B design variants

### Variant A — Evidence Terminal

Purpose: minimal visual change, maximum clarity.

Changes:

- Keep current two-column hero.
- Rewrite terminal lines to compare official/candidate/calibrated lanes.
- Add a compact lane comparison band below hero.
- Keep source strip and topology cards largely unchanged.

Best when: the goal is low-risk improvement and easy implementation.

### Variant B — Benchmark Ledger

Purpose: make provenance the hero.

Changes:

- Hero is followed by a ledger table styled as terminal output.
- Each row has lane, run/issue, image/ref, p99, failures, score.
- CTAs become `verify official`, `inspect candidate`, `compare history`.
- Topology moves slightly lower.

Best when: the audience is very technical and skeptical.

### Variant C — Runtime Trace

Purpose: make architecture the star while preserving evidence.

Changes:

- Hero terminal becomes a horizontal trace: `k6 -> yolo lb -> uds -> nativeaot api -> scorer -> bytes`.
- Evidence cards sit in a right rail or immediately below.
- Topology section becomes a five-step trace with details.

Best when: the site should sell the implementation technique more than benchmark numbers.

### Variant D — Split Official / Candidate Narrative

Purpose: resolve stale official issue confusion.

Changes:

- Two large cards below hero:
  - `official upstream: historical accepted result`
  - `current candidate: clean CI evidence`
- Each has its own status, links, and caveats.
- The rest of page follows current order.

Best when: the official issue remains stale and current CI is much better.

## Recommended A/B test setup

Use static variants first. Do not add heavy runtime experimentation if it harms performance or deploy simplicity.

Suggested paths:

- `/` current or chosen default.
- `/ab/evidence-terminal/`
- `/ab/benchmark-ledger/`
- `/ab/runtime-trace/`

If analytics is enabled through `PUBLIC_GA_ID`, track custom events on:

- official issue click
- latest CI run click
- report archive click
- docs architecture click
- GitHub click
- scroll depth to topology and report stream

If analytics is unavailable, use manual review and link-click telemetry from GitHub Pages/GA later. The design files should still define variants clearly.

## Accessibility requirements

- Add `:focus-visible` styles for all links and buttons.
- Ensure clickable elements are visually distinct from static badges.
- Add textual state markers, not color only: `[clean]`, `[stale official]`, `[warning]`, `[ci only]`, `[external]`.
- Respect `prefers-reduced-motion`; disable flicker and line animations under reduced motion.
- Consider reducing or localizing global scanline overlay on long-form text.
- Maintain no horizontal overflow on mobile.
- Keep paragraph line length near `60-75ch`.
- Test mobile hero collapse and report table scrolling.

Suggested focus style:

```css
:where(a, button):focus-visible {
  outline: 2px solid var(--warn-amber);
  outline-offset: 4px;
  box-shadow: 0 0 0 4px rgba(255, 179, 0, 0.16);
}
```

Suggested badge/link distinction:

```css
.badge { border-style: dashed; cursor: default; }
a.action { border-style: solid; cursor: pointer; }
a.action::after { content: " ↗"; }
```

## Component roadmap

1. `EvidenceLane.astro`
   - Props: `lane`, `status`, `p99`, `failures`, `score`, `href`, `caveat`.
   - Used on homepage and reports page.

2. `ProvenanceLedger.astro`
   - Renders official source and local CI source together.
   - Pulls from `official/latest.json` and `reports/latest-candidate.json`.

3. `RuntimeTrace.astro`
   - Text-first path visualization for LB, UDS, parser, scorer, response.

4. `ReportSummary.astro`
   - Computes latest/best/median-ish summaries from `reports/index.json`.
   - Avoid claiming medians unless actually computed from comparable clean rows.

5. `StatusPill.astro`
   - Distinguishes state labels from interactive links.

6. `ABShell.astro` or `src/pages/ab/*`
   - Static variant routes for comparison and review.

## Acceptance checklist for the overhaul

- Homepage clearly distinguishes official upstream results from CI candidate results above the fold.
- Every benchmark number has a lane label and link or source reference.
- Same aesthetic is preserved: dark terminal, C# green, amber, hot-pink, monospace, CRT influence.
- No unsupported official ranking claims are added.
- `bun run build` passes in `docs/`.
- Production base path works under `/rinha4-back-end-dotnet/`.
- Keyboard focus is visible.
- Reduced motion removes flicker and line reveal animation.
- Mobile has no horizontal overflow and reports remain readable.
- Reports page exposes latest/best summary before archival tables.

## Immediate implementation recommendation

Start with Variant D plus parts of Variant A:

1. Rewrite hero terminal into official/candidate/calibrated lanes.
2. Add a three-lane evidence band immediately after hero.
3. Split source strip into upstream and local evidence columns.
4. Add explicit caveats: `official hardware` versus `GitHub Actions` versus `calibrated CI`.
5. Add focus-visible and badge/action distinction in CSS.
6. Add an `/ab/evidence-terminal/` route that ships the experimental version before replacing `/`.

This yields visible A/B testing options while preserving the existing look and minimizing risk.
