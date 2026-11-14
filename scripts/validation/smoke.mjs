import { randomUUID } from "node:crypto";
import { createToken } from "../dev/generate-token.mjs";

const baseUrl = process.env.BASE_URL ?? "http://localhost:8080";
const accountId = process.env.ACCOUNT_ID ?? "11111111-1111-1111-1111-111111111111";
const token = createToken({ accountId });
const request = (url, options = {}) => fetch(url, { ...options, signal: AbortSignal.timeout(10000) });
const headers = {
  authorization: `Bearer ${token}`,
  "content-type": "application/json",
  "idempotency-key": randomUUID(),
};
const payload = {
  type: "CREDIT",
  amount: 10000,
  currency: "BRL",
  effectiveAt: new Date().toISOString(),
  description: "Smoke test",
  externalReference: `smoke-${Date.now()}`,
};

const post = await request(`${baseUrl}/v1/accounts/${accountId}/movements`, {
  method: "POST",
  headers,
  body: JSON.stringify(payload),
});
if (post.status !== 201) {
  throw new Error(`POST falhou (${post.status}): ${await post.text()}`);
}
const movement = await post.json();

const repeated = await request(`${baseUrl}/v1/accounts/${accountId}/movements`, {
  method: "POST",
  headers,
  body: JSON.stringify(payload),
});
if (repeated.status !== 201) {
  throw new Error(`Idempotência respondeu ${repeated.status}: ${await repeated.text()}`);
}
const repeatedMovement = await repeated.json();
if (repeatedMovement.movementId !== movement.movementId) {
  throw new Error("A repetição idempotente retornou outra movimentação.");
}

await new Promise((resolve) => setTimeout(resolve, 1500));
const readHeaders = { authorization: `Bearer ${token}` };
const strong = await request(`${baseUrl}/v1/accounts/${accountId}/balance?consistency=strong`, { headers: readHeaders });
if (!strong.ok) throw new Error(`Saldo forte falhou: ${strong.status} ${await strong.text()}`);
const strongBalance = await strong.json();
if (strongBalance.balance < 10000) throw new Error(`Saldo forte inesperado: ${JSON.stringify(strongBalance)}`);

const eventual = await request(`${baseUrl}/v1/accounts/${accountId}/balance`, { headers: readHeaders });
if (!eventual.ok) throw new Error(`Saldo eventual falhou: ${eventual.status} ${await eventual.text()}`);
const eventualBalance = await eventual.json();
if (eventualBalance.balance !== strongBalance.balance) {
  throw new Error(`Projeção não convergiu: strong=${strongBalance.balance}, eventual=${eventualBalance.balance}`);
}

const historical = await request(
  `${baseUrl}/v1/accounts/${accountId}/balance?at=${encodeURIComponent(movement.recordedAt)}`,
  { headers: readHeaders },
);
if (!historical.ok) throw new Error(`Saldo histórico falhou: ${historical.status} ${await historical.text()}`);
const historicalBalance = await historical.json();
if (historicalBalance.balance !== movement.balanceAfter) {
  throw new Error(`Saldo histórico inesperado: ${JSON.stringify(historicalBalance)}`);
}

const movementQuery = await request(`${baseUrl}/v1/movements/${movement.movementId}`, { headers: readHeaders });
if (!movementQuery.ok) {
  throw new Error(`Consulta da movimentação falhou: ${movementQuery.status} ${await movementQuery.text()}`);
}

const statement = await request(`${baseUrl}/v1/accounts/${accountId}/statement?limit=10`, { headers: readHeaders });
if (!statement.ok) throw new Error(`Extrato falhou: ${statement.status} ${await statement.text()}`);

process.stdout.write(`Smoke OK: movement=${movement.movementId}, balance=${strongBalance.balance}\n`);
