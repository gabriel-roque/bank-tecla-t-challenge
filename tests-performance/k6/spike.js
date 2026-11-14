import { ledger, ledgerRps, query, queryRps, thresholds } from "./lib/workload.js";

export { ledger, query };

const spikeDuration = __ENV.SPIKE_DURATION || "5s";
const stages = (rate) => [
  { target: Math.ceil(rate * 0.2), duration: spikeDuration },
  { target: rate, duration: "1s" },
  { target: rate, duration: spikeDuration },
  { target: Math.ceil(rate * 0.2), duration: spikeDuration },
];

export const options = {
  scenarios: {
    ledger_spike: {
      executor: "ramping-arrival-rate",
      startRate: 1,
      timeUnit: "1s",
      stages: stages(ledgerRps),
      preAllocatedVUs: 500,
      maxVUs: 5000,
      exec: "ledger",
      tags: { api: "ledger", profile: "spike" },
    },
    query_spike: {
      executor: "ramping-arrival-rate",
      startRate: 1,
      timeUnit: "1s",
      stages: stages(queryRps),
      preAllocatedVUs: 500,
      maxVUs: 5000,
      exec: "query",
      tags: { api: "query", profile: "spike" },
    },
  },
  thresholds,
};
