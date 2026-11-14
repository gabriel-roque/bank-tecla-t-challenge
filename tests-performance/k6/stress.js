import { ledger, ledgerRps, query, queryRps, thresholds } from "./lib/workload.js";

export { ledger, query };

const stageDuration = __ENV.STRESS_STAGE_DURATION || "5s";
const stages = (rate) => [
  { target: Math.ceil(rate * 0.25), duration: stageDuration },
  { target: Math.ceil(rate * 0.5), duration: stageDuration },
  { target: rate, duration: stageDuration },
  { target: Math.ceil(rate * 1.25), duration: stageDuration },
];

export const options = {
  scenarios: {
    ledger_stress: {
      executor: "ramping-arrival-rate",
      startRate: 1,
      timeUnit: "1s",
      stages: stages(ledgerRps),
      preAllocatedVUs: 250,
      maxVUs: 5000,
      exec: "ledger",
      tags: { api: "ledger", profile: "stress" },
    },
    query_stress: {
      executor: "ramping-arrival-rate",
      startRate: 1,
      timeUnit: "1s",
      stages: stages(queryRps),
      preAllocatedVUs: 250,
      maxVUs: 5000,
      exec: "query",
      tags: { api: "query", profile: "stress" },
    },
  },
  thresholds,
};
