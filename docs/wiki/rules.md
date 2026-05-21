# Rules

Allowed in this repo:

- preprocess `references.json.gz`
- preprocess `mcc_risk.json`
- preprocess `normalization.json`
- use any classifier built from allowed reference data
- build ANN or IVF indexes from `references.json.gz`
- run the public official k6 script in CI
- compare against the public ranking preview

Not allowed:

- using official test payloads as reference data
- hardcoding expected answers from preview runs
- building correction tables from misclassified test payloads
- letting the reverse proxy inspect fraud payloads or answer `/fraud-score`

The CI benchmark only mounts official test data into the k6 container. API containers do not receive test payload files.

The IVF scorer follows the same boundary: it trains and packs only
`references.json.gz`, reads no benchmark payload files, and fails startup when
the index is unavailable.
