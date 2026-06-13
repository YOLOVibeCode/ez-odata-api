// k6 perf scenario (spec 13 §7): sustained read traffic against a warm service.
// Usage: k6 run -e BASE=http://localhost:8080 -e KEY=ez_live_xxx read-throughput.js
import http from "k6/http";
import { check } from "k6";

export const options = {
  scenarios: {
    reads: { executor: "constant-vus", vus: 50, duration: "30s" },
  },
  thresholds: {
    http_req_duration: ["p(95)<120"], // NFR-2: p95 < 120ms
    http_req_failed: ["rate<0.001"],
  },
};

const BASE = __ENV.BASE ?? "http://localhost:8080";
const KEY = __ENV.KEY ?? "";
const SERVICE = __ENV.SERVICE ?? "sales";

export default function () {
  const res = http.get(`${BASE}/api/odata/${SERVICE}/customers?$top=25`, {
    headers: { "X-API-Key": KEY },
  });
  check(res, { "status 200": (r) => r.status === 200 });
}
