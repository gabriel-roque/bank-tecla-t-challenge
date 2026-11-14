import {
  atOnceUsers,
  getEnvironmentVariable,
  global,
  jsonPath,
  nothingFor,
  scenario,
  simulation,
  StringBody,
} from "@gatling.io/core";
import { http, status } from "@gatling.io/http";

const baseUrl = getEnvironmentVariable("BASE_URL", "http://localhost:8080");
const token = getEnvironmentVariable("ACCESS_TOKEN", "");
const accountId = crypto.randomUUID();

const movementBody = (type: "CREDIT" | "DEBIT", amount: number) =>
  StringBody(
    JSON.stringify({
      type,
      amount,
      currency: "BRL",
      effectiveAt: new Date().toISOString(),
      description: `Gatling concurrent ${type.toLowerCase()}`,
    }),
  );

export default simulation((setUp) => {
  const protocol = http.baseUrl(baseUrl).authorizationHeader(`Bearer ${token}`);

  const seed = scenario("seed-concurrent-account").exec(
    http("seed credit")
      .post(`/v1/accounts/${accountId}/movements`)
      .header("Idempotency-Key", `seed-${accountId}`)
      .body(movementBody("CREDIT", 100))
      .asJson()
      .check(status().is(201)),
  );

  const concurrentDebits = scenario("parallel-debits-same-account").exec(
    http("concurrent debit")
      .post(`/v1/accounts/${accountId}/movements`)
      .header("Idempotency-Key", (session) => `debit-${session.userId()}-${accountId}`)
      .body(movementBody("DEBIT", 80))
      .asJson()
      .check(status().in(201, 422)),
  );

  const verifyInvariant = scenario("verify-non-negative-invariant").exec(
    http("verify strong balance")
      .get(`/v1/accounts/${accountId}/balance?consistency=strong`)
      .check(
        status().is(200),
        jsonPath("$.balance").find().is("20"),
        jsonPath("$.lastSequence").find().is("2"),
      ),
  );

  setUp(
    seed.injectOpen(atOnceUsers(1)),
    concurrentDebits.injectOpen(nothingFor(2), atOnceUsers(2)),
    verifyInvariant.injectOpen(nothingFor(5), atOnceUsers(1)),
  )
    .protocols(protocol)
    .assertions(global().failedRequests().count().is(0));
});
