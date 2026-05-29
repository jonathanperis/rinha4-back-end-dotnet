# rinha4-back-end-dotnet Product Context

## Product identity and purpose

`rinha4-back-end-dotnet` is Jonathan Peris's .NET 10 NativeAOT implementation for Rinha de Backend 2026 fraud detection. The public website is a performance proof surface: it explains the runtime, exposes official and official-like benchmark evidence, and gives technical visitors fast routes to docs, reports, source, and upstream result provenance.

The implementation competes under a `1 CPU / 350 MB` envelope with a standalone `rinha4-lb-yolo-mode` load balancer, two .NET NativeAOT API instances, fd-pass control sockets, raw HTTP/1 parsing, prebuilt HTTP responses, and a clean IVF scorer with bounded repair by default. Bucket/hybrid paths remain available for explicit experiments.

## Register

brand

The site is a brand and proof surface for a technical competition entry. It should feel like a benchmark console and public run log, not an app dashboard or generic SaaS landing page.

## Primary users

1. Rinha de Backend competitors and observers checking what Jonathan built and how it performs.
2. Performance-focused .NET developers looking for low-level NativeAOT, raw socket, and data-path techniques.
3. Future Jonathan or collaborators comparing official submissions, CI candidates, and tuning experiments.
4. Recruiters or engineering peers who may skim the site as evidence of systems performance skill.

These users arrive in a skeptical, technical state of mind. They want proof, provenance, source links, and clear separation between official Rinha data and GitHub Actions projection data.

## Core value propositions

- .NET can compete in a low-latency fraud-scoring benchmark when the runtime path avoids framework overhead.
- The implementation is transparent: architecture, benchmark reports, official issue links, and CI history are exposed instead of hidden behind marketing copy.
- Current candidate evidence is stronger than the stale official issue: latest CI candidate reports show `0.34ms` p99, `0%` failure rate, and `6000` score, while latest calibrated CI shows `0.35ms` p99 and `6000` score.
- The public docs preserve implementation knowledge: raw HTTP server, fd-pass transport, bucket fast path, IVF fallback, benchmark workflow, and report archive.

## Canonical facts for design and copy

- Repository: `jonathanperis/rinha4-back-end-dotnet`.
- Challenge: Rinha de Backend 2026 by `zanfranceschi/rinha-de-backend-2026`.
- Runtime: .NET 10 NativeAOT, Docker, Linux amd64, raw socket HTTP/1.
- Public endpoint contract: `GET /ready`, `POST /fraud-score`.
- Current source docs state the default runtime shape as load balancer on `9999`, two API instances, Docker bridge network, public submission images, and total limits at or below `1 CPU / 350 MB`.
- Current README states the compose split as `0.425 CPU / 165 MB` per API and `0.15 CPU / 20 MB` for the load balancer.
- Site accent is GitHub Linguist C# green `#178600`.
- Latest synced official issue in `docs/public/official/latest.json`: issue `#2088`, official p99 `2.01ms`, failures `2.35%`, score `3031.24`, image `ghcr.io/jonathanperis/rinha4-back-end-dotnet:ci-2546b8a3bfbcbedc318e4326b0824c94f18c744a`.
- Latest CI candidate in `docs/public/reports/latest-candidate.json`: run `25981532396`, image `ghcr.io/jonathanperis/rinha4-back-end-dotnet:ci-ead329a626dec7a605145fd278655dfd0fa63a51`, p99 `0.34ms`, failures `0%`, score `6000`.
- Latest calibrated CI in `docs/public/reports/latest-calibrated.json`: run `25981532396`, image `ghcr.io/jonathanperis/rinha4-back-end-dotnet:ci-ead329a626dec7a605145fd278655dfd0fa63a51`, p99 `0.35ms`, failures `0%`, score `6000`, CPU limits api `0.40`, proxy `0.20`, not official Rinha hardware.
- Report index currently contains over two hundred archived runs. It is evidence, not decoration.

## Brand voice

- Technical, direct, and adversarial against latency.
- Console-native, but not fake terminal cosplay.
- Source-backed and precise. Every number needs provenance.
- Competitive without overclaiming official ranking impact.
- Minimal humor through labels and system phrasing, not memes.

Copy should use short, instrument-like phrases:

- `official result`
- `candidate evidence`
- `runtime path`
- `fraud decision`
- `source synced`
- `0% failures`
- `not official hardware`

Avoid generic marketing language such as `blazing fast`, `revolutionary`, `seamless`, `enterprise-grade`, and unsupported rank claims.

## Anti-references

- Generic SaaS homepage with gradient hero, floating cards, and vague benefits.
- Overdesigned dark observability dashboard that hides the actual benchmark evidence.
- Fake terminal output that does not map to real commands, refs, or metrics.
- AI-looking cyberpunk excess: decorative particles, glass panels, and neon for its own sake.
- Treating CI p99 and official p99 as the same kind of evidence.
- Making the page look like a product admin dashboard. This is a public proof dossier.

## Current aesthetic elements to preserve

- Dark CRT-like page with scanlines, green C# signal color, amber command accent, and hot-pink failure accent.
- Monospace-heavy voice and labels.
- Huge glitch-styled all-caps hero: `FRAUD SIGNAL UNDER LOAD.`
- Terminal console panel in the hero.
- Metric card row for official evidence.
- Explicit source strip that links to upstream issue, result comment, and ranking.
- Topology cards with endpoint badges.
- CI benchmark stream section with report links.
- Lean page structure and fast static Astro deployment.

## Current-site evaluation

### What works

- Strong identity: the page reads as benchmark console, not generic landing page.
- Above-the-fold proof: official p99, failures, issue number, and source links are visible quickly.
- Honest evidence split: official Rinha issue data is separated from CI candidate data.
- Useful information architecture: hero, official cards, source strip, topology, CI stream, reports.
- Production base-path build works when served under `/rinha4-back-end-dotnet/`.

### Opportunities

1. The official issue is stale relative to current candidate evidence. The redesign should make this relationship explicit without implying CI equals official hardware.
2. Visual hierarchy is strong but uniform. Many modules share the same border, glow, all-caps labels, and mono texture, so scan speed drops below the fold.
3. The hero console is memorable, but it can carry clearer provenance: current image, official image, candidate run, official run, and hardware lane labels.
4. Static badges and clickable actions look similar. Links, status pills, endpoint badges, and disabled metric pills need distinct affordances.
5. Focus states are not currently defined with `:focus-visible`. Keyboard users need visible, on-brand focus treatment.
6. Global flicker and scanlines are flavorful but should stay quiet for body text and fully respect reduced motion.
7. Metrics need a clearer taxonomy: official, candidate CI, calibrated CI, comparison branch, experiment.
8. The report archive is rich but could expose latest, best, and representative summaries before the full table.

## Overhaul objective

Keep the same aesthetic family: dark terminal benchmark console, C# green, amber, hot-pink failure signal, monospace instrumentation, source-backed evidence. Overhaul the information hierarchy, proof density, interaction semantics, accessibility, and report framing.

The redesigned homepage should answer these questions in the first screen:

1. What is this? A .NET 10 NativeAOT Rinha fraud backend.
2. What is the current state? Official issue is stale; current candidate evidence is clean in CI.
3. Why believe it? Exact run IDs, image tags, issue links, scoring breakdown, and source path.
4. How does it work? LB to UDS API instances, raw HTTP parser, bucket fast path, IVF fallback.
5. Where do I go next? Official result, latest CI run, report archive, system docs, GitHub.

## A/B testing plan

### Test A: Hero proof density

- Control: current two-column hero with console and two CTAs.
- Variant A1: add a compact proof rail under the headline: `official #2088`, `current candidate`, `0% CI failures`, `6000 CI score`, `not official hardware`.
- Variant A2: replace the hero console's generic sequence with a provenance console that compares official image, candidate image, official p99, candidate p99, and calibrated p99.
- Hypothesis: visitors will understand the stale-official versus current-candidate distinction faster.
- Primary metric: clicks to reports or latest CI run.
- Guardrail metric: official result clicks should not drop sharply.

### Test B: CTA language

- Control: `OFFICIAL RESULT` and `READ SYSTEM NOTES`.
- Variant B1: literal CTAs: `OPEN OFFICIAL ISSUE`, `READ ARCHITECTURE`.
- Variant B2: operator CTAs: `VERIFY UPSTREAM RESULT`, `TRACE RUNTIME PATH`.
- Hypothesis: literal CTAs improve clarity, operator CTAs preserve mood while improving intent.
- Primary metric: CTA click-through split.
- Guardrail metric: bounce rate from first screen.

### Test C: Evidence order

- Control: official cards first, source strip, topology, CI stream.
- Variant C1: current candidate card first, then official stale card as historical baseline.
- Variant C2: side-by-side official versus current candidate comparison immediately below hero.
- Hypothesis: the side-by-side comparison reduces confusion about why official failures remain visible while current CI is clean.
- Primary metric: scroll depth to reports section.
- Guardrail metric: time on page should not collapse from information overload.

### Test D: Report archive framing

- Control: report table with latest stat row and paginated archive.
- Variant D1: latest, best, and median summary panels before the table.
- Variant D2: grouped lanes: candidate, official-calibrated, experiment, each with latest and best compact rows.
- Hypothesis: grouped lanes help technical visitors find credible evidence without reading 200 plus rows.
- Primary metric: report file link clicks.
- Guardrail metric: table pagination interaction stays usable on mobile.

### Test E: Effects intensity

- Control: current CRT scanline and flicker.
- Variant E1: scanline only in hero and data panels, not globally over all text.
- Variant E2: add a visible `reduced effects` toggle while still honoring `prefers-reduced-motion`.
- Hypothesis: reduced global effects improve readability without weakening brand recognition.
- Primary metric: scroll depth and time on docs pages from homepage.
- Guardrail metric: no increase in visual blandness during qualitative review.

## Implementation guardrails

- Do not ship unsupported benchmark numbers.
- Label CI and official hardware differences every time numbers appear together.
- Preserve source links for all externally verifiable claims.
- Keep the page static, fast, and deployable through the existing Astro GitHub Pages flow.
- Use `bun` for docs builds.
- Verify production base path `/rinha4-back-end-dotnet/` before visual sign-off.
