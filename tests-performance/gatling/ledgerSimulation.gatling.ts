import {
  atOnceUsers,
  constantUsersPerSec,
  getEnvironmentVariable,
  global,
  jsonPath,
  rampUsersPerSec,
  scenario,
  simulation,
  StringBody,
} from "@gatling.io/core";
import { http, status } from "@gatling.io/http";

const baseUrl = getEnvironmentVariable("BASE_URL", "http://localhost:8080");
const token = getEnvironmentVariable("ACCESS_TOKEN", "");
const accountId = getEnvironmentVariable("ACCOUNT_ID", "11111111-1111-1111-1111-111111111111");
const accountPoolSize = Number(getEnvironmentVariable("ACCOUNT_POOL_SIZE", "500"));
const ledgerRps = Number(getEnvironmentVariable("LEDGER_RPS", "500"));
const queryRps = Number(getEnvironmentVariable("QUERY_RPS", "1000"));
const rampSeconds = Number(getEnvironmentVariable("RAMP_SECONDS", "60"));
const steadySeconds = Number(getEnvironmentVariable("STEADY_SECONDS", "60"));

function pooledAccountId(index: number) {
  if (accountPoolSize === 1) return accountId;
  const suffix = String((index % accountPoolSize) + 1).padStart(12, "0");
  return `00000000-0000-4000-8000-${suffix}`;
}

export default simulation((setUp) => {
  const protocol = http.baseUrl(baseUrl).authorizationHeader(`Bearer ${token}`);

  const writes = scenario("ledger-write-500-rps")
    .exec((session) =>
      session
        .set("accountId", pooledAccountId(session.userId()))
        .set("idempotencyKey", crypto.randomUUID()),
    )
    .exec(
      http("post movement")
        .post("/v1/accounts/#{accountId}/movements")
        .header("Idempotency-Key", "#{idempotencyKey}")
        .body(
          StringBody(
            JSON.stringify({
              type: "CREDIT",
              amount: 1,
              currency: "BRL",
              effectiveAt: "2026-11-14T12:00:00Z",
              description: "Gatling",
            }),
          ),
        )
        .asJson()
        .check(status().is(201)),
    );

  const reads = scenario("query-read-1000-rps")
    .exec((session) => {
      const targetAccountId = pooledAccountId(session.userId());
      const readKind = session.userId() % 10;
      const queryPath = readKind < 8
        ? `/v1/accounts/${targetAccountId}/balance`
        : readKind === 8
          ? `/v1/accounts/${targetAccountId}/balance?consistency=strong`
          : `/v1/accounts/${targetAccountId}/statement?limit=20`;
      return session.set("queryPath", queryPath);
    })
    .exec(
      http("mixed query")
        .get((session) => session.get<string>("queryPath"))
        .check(status().is(200)),
    );

  const atomicity = scenario("idempotency-and-strong-consistency")
    .exec((session) =>
      session
        .set("accountId", accountId)
        .set("idempotencyKey", crypto.randomUUID())
        .set(
          "payload",
          JSON.stringify({
            type: "CREDIT",
            amount: 1,
            currency: "BRL",
            effectiveAt: new Date().toISOString(),
            description: "Gatling atomicity",
          }),
        ),
    )
    .exec(
      http("post movement once")
        .post("/v1/accounts/#{accountId}/movements")
        .header("Idempotency-Key", "#{idempotencyKey}")
        .body(StringBody("#{payload}"))
        .asJson()
        .check(
          status().is(201),
          jsonPath("$.movementId").find().saveAs("movementId"),
          jsonPath("$.balanceAfter").find().saveAs("balanceAfter"),
          jsonPath("$.recordedAt").find().saveAs("recordedAt"),
        ),
    )
    .exec(
      http("repeat same movement")
        .post("/v1/accounts/#{accountId}/movements")
        .header("Idempotency-Key", "#{idempotencyKey}")
        .body(StringBody("#{payload}"))
        .asJson()
        .check(
          status().is(201),
          jsonPath("$.movementId").find().is((session) => session.get<string>("movementId")),
        ),
    )
    .exec(
      http("get strong balance after idempotent write")
        .get("/v1/accounts/#{accountId}/balance?consistency=strong")
        .check(
          status().is(200),
          jsonPath("$.balance").find().is((session) => session.get<string>("balanceAfter")),
        ),
    )
    .exec((session) =>
      session.set("recordedAtEncoded", encodeURIComponent(session.get<string>("recordedAt"))),
    )
    .exec(
      http("get historical balance after idempotent write")
        .get("/v1/accounts/#{accountId}/balance?at=#{recordedAtEncoded}")
        .check(
          status().is(200),
          jsonPath("$.balance").find().is((session) => session.get<string>("balanceAfter")),
        ),
    )
    .pause(2)
    .exec(
      http("get movement after projection")
        .get("/v1/movements/#{movementId}")
        .check(status().is(200)),
    )
    .exec(
      http("get statement after projection")
        .get("/v1/accounts/#{accountId}/statement?limit=20")
        .check(status().is(200)),
    );

  setUp(
    writes.injectOpen(
      rampUsersPerSec(1).to(ledgerRps).during(rampSeconds),
      constantUsersPerSec(ledgerRps).during(steadySeconds),
    ),
    reads.injectOpen(
      rampUsersPerSec(1).to(queryRps).during(rampSeconds),
      constantUsersPerSec(queryRps).during(steadySeconds),
    ),
    atomicity.injectOpen(atOnceUsers(1)),
  )
    .protocols(protocol)
    .assertions(global().failedRequests().count().is(0));
});
