# Competitor Gap Analysis Spec

## Goal

Find missing latency ingredient versus Danilo and Pedro without losing `0%` failures or violating Rinha rules.

## Evidence Baseline

- Comparison run `25831057318` (`comparison`, `all-comparison`, 1 rep) completed after rerun.
- Same run: Danilo `0.46ms`, score `6000`, `0%` failures.
- Same run: Pedro `0.54ms`, score `6000`, `0%` failures.
- Same run: Jonathan pinned comparison image `1.43ms`, score `5843.76`, `0%` failures.
- Best Jonathan current candidate evidence remains run `25822565412`, Forevis, median p99 `1.33ms`, score `5875.65`, `0%` failures.
- Comparison branch Jonathan compose is not latest best candidate; first comparison step must remove this measurement skew.

## Competitor Facts To Compare

### Danilo

- Public source: `daniloitagyba/rinha-2026-dotnet`.
- Comparison image: `ghcr.io/daniloitagyba/rinha-2026-dotnet-tcp:f695c23b7cf2c66f6b82a99d5047da2b23f29df2`.
- Infra: custom C load balancer, `LB_MODE=fdpass`, API receives accepted sockets via `SCM_RIGHTS`.
- CPU split in comparison: LB `0.15`, APIs `0.425` each.
- API env: `SERVER_MODE=raw`, `WORKERS=2`, `TP_MIN_THREADS=64`.
- Scorer: bucket ANN with profile fast path, risky fallback, risky fine buckets, optional native AVX2 (`NATIVE_ANN=1`, `RISKY_NATIVE_FINE=1`).
- Index: single `references.idx`, mmaped, extension sections for profile counts/masks, neighbor orders, risky metadata, risky vectors/labels, risky fine offsets/keys, block vectors.

### Pedro

- Public source: `pedrosakuma/rinha-backend-2026`.
- Official issue `#3642`: p99 `1.38ms`, score `5859.53`, `0%` failures.
- Comparison image: `ghcr.io/pedrosakuma/rinha-backend-2026-api:wave29-fp2-a58980f`.
- Current repo compose image: `wave30-selective-f56b9f9`.
- Infra: Forevis LB, UDS upstreams, `cpuset` isolates APIs on `0`/`1`, LB on `2,3`.
- API env: `SCORER=ivf-blocked`, `IVF_BLOCKED_NPROBE=1`, `IVF_RERANK=48`, `IVF_BORDERLINE_NPROBE=32`, `IVF_BORDERLINE_RERANK=128`, `IVF_BBOX_REPAIR=1`, `TP_MIN_WORKERS=16`, `TP_MAX_WORKERS=16`, `RAW_HTTP=0` in submitted compose.
- Scorer: Block-SoA Q16 IVF, bbox repair, partial dimension block abort, software prefetch, worst-index top-5, selective decision cascade.
- Data: multiple mmaped files from image, host page cache shared between two API replicas.

## Our Known Differences

- Our comparison compose used older/pinned Jonathan image and nginx, not latest Forevis candidate.
- Our base runtime uses raw HTTP and pooled buffers; transport is not obvious sole gap.
- Our bucket index is mmaped, but IVF index loads into managed arrays per API instance.
- Our risky fallback fine index is built in managed arrays at load; Danilo stores more risky fallback sections precomputed/mmaped and has native AVX2 helpers.
- Our safe defaults do not use cpuset isolation in base/Forevis compose.
- Our ThreadPool min default is `128`; Pedro caps workers at `16`, Danilo uses raw workers plus `64` min threads.
- Our fast paths include profile/reference purity tables; Pedro adds selective cascade/residual sparse stages; Danilo adds native risky fine fallback.

## Constraints

- `0%` failures stays hard gate.
- No public or official test payload lookup tables.
- No preview miss correction tables.
- No fraud logic in LB.
- Competitor public code can be studied for architecture, but do not copy hidden/payload-derived tables or blindly paste exact configs.
- Every candidate must have same-CI comparison before promotion.

## Success Criteria

- Produce layer-by-layer gap table: infra, transport, parser/vectorizer, fast path, scorer/index, runtime/threading.
- Identify one or more missing factors with measured p99 impact and `0%` failures.
- Land only reversible changes with focused validation.
- Beat or tie Pedro/Danilo same-CI p99 class only after preserving `0%` failures.
