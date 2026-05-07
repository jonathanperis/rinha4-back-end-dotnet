# Rules

Allowed in this repo:

- preprocess `references.json.gz`
- preprocess `mcc_risk.json`
- preprocess `normalization.json`
- use any classifier built from allowed reference data
- run the public official k6 script in CI
- compare against the public ranking preview

Not allowed:

- using official test payloads as reference data
- hardcoding expected answers from preview runs
- building correction tables from misclassified test payloads
- letting the reverse proxy inspect fraud payloads or answer `/fraud-score`

The CI benchmark only mounts official test data into the k6 container. API containers do not receive test payload files.

