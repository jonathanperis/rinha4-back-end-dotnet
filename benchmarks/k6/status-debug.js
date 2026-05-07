import http from 'k6/http';
import { Counter } from 'k6/metrics';

export const options = {
  stages: [
    { duration: '3s', target: 50 },
    { duration: '5s', target: 200 },
    { duration: '2s', target: 0 },
  ],
};

const status0 = new Counter('status_0');
const status200 = new Counter('status_200');
const status400 = new Counter('status_400');
const status499 = new Counter('status_499');
const status500 = new Counter('status_500');
const status502 = new Counter('status_502');
const status504 = new Counter('status_504');
const statusOther = new Counter('status_other');

const payload = JSON.stringify({
  "id": "tx-test",
  "transaction": { "amount": 384.88, "installments": 3, "requested_at": "2024-01-15T09:30:00Z" },
  "customer": { "avg_amount": 769.76, "tx_count_24h": 3, "known_merchants": ["MERC-001"] },
  "merchant": { "id": "MERC-001", "mcc": "5912", "avg_amount": 298.95 },
  "terminal": { "is_online": false, "card_present": true, "km_from_home": 13.7 },
  "last_transaction": { "timestamp": "2024-01-15T09:15:00Z", "km_from_current": 18.8 }
});

export default function () {
  const res = http.post('http://localhost:9999/fraud-score', payload, { headers: { 'Content-Type': 'application/json' } });
  if (res.status === 0) status0.add(1);
  else if (res.status === 200) status200.add(1);
  else if (res.status === 400) status400.add(1);
  else if (res.status === 499) status499.add(1);
  else if (res.status === 500) status500.add(1);
  else if (res.status === 502) status502.add(1);
  else if (res.status === 504) status504.add(1);
  else statusOther.add(1);
}
