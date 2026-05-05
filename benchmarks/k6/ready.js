import http from 'k6/http';
import { check } from 'k6';

export const options = {
    stages: [
        { duration: '5s', target: 100 },
        { duration: '10s', target: 1000 },
        { duration: '5s', target: 0 },
    ],
    thresholds: {
        http_req_duration: ['p(99)<10'],
    },
};

export default function () {
    const res = http.get('http://localhost:9999/ready');
    check(res, {
        'status is 200': (r) => r.status === 200,
    });
}
