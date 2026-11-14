import { randomUUID } from "node:crypto";
import { createToken } from "../dev/generate-token.mjs";

const baseUrl = process.env.BASE_URL ?? "http://localhost:8080";
const accountId = randomUUID();
const token = createToken({ accountId });
const authorization = `Bearer ${token}`;

async function movement(type, amount, key) {
  return fetch(`${baseUrl}/v1/accounts/${accountId}/movements`, {
    method: "POST",
    headers: {
      authorization,
      "content-type": "application/json",
      "idempotency-key": key,
    },
    body: JSON.stringify({
      type,
      amount,
      currency: "BRL",
      effectiveAt: new Date().toISOString(),
      description: "Concurrency validation",
    }),
  });
}

const seed = await movement("CREDIT", 100, randomUUID());
if (seed.status !== 201) throw new Error(`Seed falhou: ${seed.status} ${await seed.text()}`);

const responses = await Promise.all([
  movement("DEBIT", 80, randomUUID()),
  movement("DEBIT", 80, randomUUID()),
]);
const statuses = responses.map((response) => response.status).sort();
if (statuses[0] !== 201 || statuses[1] !== 422) {
  throw new Error(`Resultado concorrente inesperado: ${statuses.join(",")}`);
}

const balanceResponse = await fetch(
  `${baseUrl}/v1/accounts/${accountId}/balance?consistency=strong`,
  { headers: { authorization } },
);
const balance = await balanceResponse.json();
if (balanceResponse.status !== 200 || balance.balance !== 20 || balance.lastSequence !== 2) {
  throw new Error(`Saldo concorrente incorreto: ${JSON.stringify(balance)}`);
}

process.stdout.write(`Concorrência OK: account=${accountId}, balance=20, sequence=2\n`);
