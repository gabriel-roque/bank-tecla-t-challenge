import { ledger, ledgerRps, query, queryRps, thresholds } from "./lib/workload.js";

export { ledger, query };

const duration = __ENV.SOAK_DURATION || __ENV.DURATION || "30m";

export const options = {
  scenarios: {
    ledger_soak: {
      executor: "constant-arrival-rate",
      rate: ledgerRps,
      timeUnit: "1s",
      duration,
      preAllocatedVUs: 250,
      maxVUs: 5000,
      exec: "ledger",
      tags: { api: "ledger", profile: "soak" },
    },
    query_soak: {
      executor: "constant-arrival-rate",
      rate: queryRps,
      timeUnit: "1s",
      duration,
      preAllocatedVUs: 250,
      maxVUs: 5000,
      exec: "query",
      tags: { api: "query", profile: "soak" },
    },
  },
  thresholds,
};
