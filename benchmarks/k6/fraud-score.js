import http from 'k6/http';
import { check } from 'k6';

export const options = {
    stages: [
        { duration: '10s', target: 100 },
        { duration: '20s', target: 500 },
        { duration: '10s', target: 0 },
    ],
    thresholds: {
        http_req_duration: ['p(99)<50'],
        http_req_failed: ['rate<0.01'],
    },
};

const payload = JSON.stringify({
    "id": "tx-test",
    "transaction": {
        "amount": 384.88,
        "installments": 3,
        "requested_at": "2024-01-15T09:30:00Z"
    },
    "customer": {
        "avg_amount": 769.76,
        "tx_count_24h": 3,
        "known_merchants": ["MERC-001"]
    },
    "merchant": {
        "id": "MERC-001",
        "mcc": "5912",
        "avg_amount": 298.95
    },
    "terminal": {
        "is_online": false,
        "card_present": true,
        "km_from_home": 13.7
    },
    "last_transaction": {
        "timestamp": "2024-01-15T09:15:00Z",
        "km_from_current": 18.8
    }
});

const headers = {
    'Content-Type': 'application/json',
};

export default function () {
    const res = http.post('http://localhost:9999/fraud-score', payload, { headers });
    check(res, {
        'status is 200': (r) => r.status === 200,
        'response time < 50ms': (r) => r.timings.duration < 50,
    });
}
