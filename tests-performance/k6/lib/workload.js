import http from "k6/http";
import { check } from "k6";

export const ledgerRps = Number(__ENV.LEDGER_RPS || 500);
export const queryRps = Number(__ENV.QUERY_RPS || 1000);
export const baseUrl = __ENV.BASE_URL || "http://localhost:8080";
export const accountId = __ENV.ACCOUNT_ID || "11111111-1111-1111-1111-111111111111";
export const accountPoolSize = Number(__ENV.ACCOUNT_POOL_SIZE || 500);
export const accountPoolOffset = Number(__ENV.ACCOUNT_POOL_OFFSET || 0);
export const requestTimeout = __ENV.HTTP_TIMEOUT || "15s";
const token = __ENV.ACCESS_TOKEN || "";

export const thresholds = {
  http_req_failed: ["rate<0.01"],
  "http_req_duration{api:ledger}": ["p(95)<500"],
  "http_req_duration{api:query}": ["p(95)<200"],
  dropped_iterations: ["count==0"],
  checks: ["rate>0.99"],
};

const headers = { Authorization: `Bearer ${token}`, "Content-Type": "application/json" };

export function pooledAccountId(index) {
  if (accountPoolSize === 1) return accountId;
  const suffix = String(accountPoolOffset + (index % accountPoolSize) + 1).padStart(12, "0");
  return `00000000-0000-4000-8000-${suffix}`;
}

export function ledger() {
  const targetAccountId = pooledAccountId(__VU + __ITER);
  const response = http.post(
    `${baseUrl}/v1/accounts/${targetAccountId}/movements`,
    JSON.stringify({
      type: "CREDIT",
      amount: 1,
      currency: "BRL",
      effectiveAt: new Date().toISOString(),
      description: "k6",
    }),
    {
      timeout: requestTimeout,
      headers: {
        ...headers,
        "Idempotency-Key": `${__ENV.K6_PROFILE || "load"}-${__VU}-${__ITER}-${Date.now()}`,
      },
    },
  );
  check(response, { "movement created": (result) => result.status === 201 });
}

export function query() {
  const index = __VU + __ITER;
  const targetAccountId = pooledAccountId(index);
  const readKind = index % 10;
  const path = readKind < 8
    ? `/v1/accounts/${targetAccountId}/balance`
    : readKind === 8
      ? `/v1/accounts/${targetAccountId}/balance?consistency=strong`
      : `/v1/accounts/${targetAccountId}/statement?limit=20`;
  const response = http.get(`${baseUrl}${path}`, { headers, timeout: requestTimeout });
  check(response, { "query returned": (result) => result.status === 200 });
}
