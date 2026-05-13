# Tasks

| ID | Task | Verify |
| --- | --- | --- |
| T1 | Add specs and C# project agent | files exist; `opencode.json` points at agent |
| T2 | Validate current hot-path route reorder if kept | vectorization tests + accuracy probe |
| T3 | Commit/push spec-only changes without generated bucket binary | `git diff --staged` excludes `data/references.bucket.bin` and unrelated runtime edits |
| T4 | Run Forevis 3-rep CI candidate for current head | benchmark archive shows median p99/failure |
| T5 | Compare against best candidate `25822565412` and Pedro same-CI `25808383580` | same-CI p99/score table |
| T6 | Investigate transport/parser safe wins | focused diff + tests + CI benchmark |
| T7 | Promote only `0%` failure candidates | CI result has `0` FP, `0` FN, `0` HTTP errors |
| T8 | Prepare submission after same-CI win/tie over target | `submission` runnable-only, image pinned, env matches benchmark |
| T9 | Trigger official preview | issue `rinha/test jonathanperis-dotnet` closes with `0%` failures |
| T10 | Sync official result into docs/state | docs JSON and `.specs/project/STATE.md` updated |
