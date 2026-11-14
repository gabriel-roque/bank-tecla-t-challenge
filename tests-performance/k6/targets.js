import { ledger, ledgerRps, query, queryRps, thresholds } from "./lib/workload.js";

export { ledger, query };

export const options = {
  scenarios: {
    ledger: {
      executor: "constant-arrival-rate",
      rate: ledgerRps,
      timeUnit: "1s",
      duration: __ENV.DURATION || "2m",
      preAllocatedVUs: 500,
      maxVUs: 5000,
      exec: "ledger",
      tags: { api: "ledger" },
    },
    query: {
      executor: "constant-arrival-rate",
      rate: queryRps,
      timeUnit: "1s",
      duration: __ENV.DURATION || "2m",
      preAllocatedVUs: 500,
      maxVUs: 5000,
      exec: "query",
      tags: { api: "query" },
    },
  },
  thresholds,
};
