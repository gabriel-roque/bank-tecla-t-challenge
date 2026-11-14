import { ledger, ledgerRps, query, queryRps, thresholds } from "./lib/workload.js";

export { ledger, query };

const stageDuration = __ENV.BREAKPOINT_STAGE_DURATION || "5s";
const stages = (rate) => [0.25, 0.5, 0.75, 1, 1.25].map((factor) => ({
  target: Math.ceil(rate * factor),
  duration: stageDuration,
}));

export const options = {
  scenarios: {
    ledger_breakpoint: {
      executor: "ramping-arrival-rate",
      startRate: 1,
      timeUnit: "1s",
      stages: stages(ledgerRps),
      preAllocatedVUs: 250,
      maxVUs: 5000,
      exec: "ledger",
      tags: { api: "ledger", profile: "breakpoint" },
    },
    query_breakpoint: {
      executor: "ramping-arrival-rate",
      startRate: 1,
      timeUnit: "1s",
      stages: stages(queryRps),
      preAllocatedVUs: 250,
      maxVUs: 5000,
      exec: "query",
      tags: { api: "query", profile: "breakpoint" },
    },
  },
  thresholds,
};
