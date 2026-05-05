#!/bin/bash
set -euo pipefail

echo "=== Rinha 2026 Local Benchmark ==="

# Check if stack is running
if ! docker compose ps | grep -q "webapi-1"; then
    echo "Starting stack..."
    docker compose up -d --build
    echo "Waiting for ready..."
    for i in {1..30}; do
        if curl -sf http://localhost:9999/ready > /dev/null 2>&1; then
            echo "Ready!"
            break
        fi
        sleep 1
    done
fi

echo ""
echo "=== Quick oha test (5k requests, 64 conns) ==="
if command -v oha &> /dev/null; then
    oha -n 5000 -c 64 -q 900 \
        -m POST \
        -H "Content-Type: application/json" \
        -d '{"id":"tx-test","transaction":{"amount":384.88,"installments":3,"requested_at":"2024-01-15T09:30:00Z"},"customer":{"avg_amount":769.76,"tx_count_24h":3,"known_merchants":["MERC-001"]},"merchant":{"id":"MERC-001","mcc":"5912","avg_amount":298.95},"terminal":{"is_online":false,"card_present":true,"km_from_home":13.7},"last_transaction":{"timestamp":"2024-01-15T09:15:00Z","km_from_current":18.8}}' \
        http://localhost:9999/fraud-score
else
    echo "oha not installed. Install with: cargo install oha"
fi

echo ""
echo "=== K6 ready test ==="
if command -v k6 &> /dev/null; then
    k6 run benchmarks/k6/ready.js
else
    echo "k6 not installed. Install from https://k6.io/docs/get-started/installation/"
fi

echo ""
echo "=== K6 fraud-score test ==="
if command -v k6 &> /dev/null; then
    k6 run benchmarks/k6/fraud-score.js
else
    echo "k6 not installed. Install from https://k6.io/docs/get-started/installation/"
fi

echo ""
echo "=== Done ==="
