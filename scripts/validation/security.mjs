import { randomUUID } from "node:crypto";
import { createToken } from "../dev/generate-token.mjs";

const baseUrl = process.env.BASE_URL ?? "http://localhost:8080";
const accountId = randomUUID();
const otherAccountId = randomUUID();

const anonymous = await fetch(`${baseUrl}/v1/accounts/${accountId}/balance`);
if (anonymous.status !== 401) throw new Error(`Sem token deveria retornar 401, retornou ${anonymous.status}.`);

const readOnly = createToken({ accountId, scopes: ["ledger.balance.read"] });
const forbiddenWrite = await fetch(`${baseUrl}/v1/accounts/${accountId}/movements`, {
  method: "POST",
  headers: {
    authorization: `Bearer ${readOnly}`,
    "content-type": "application/json",
    "idempotency-key": randomUUID(),
  },
  body: JSON.stringify({
    type: "CREDIT",
    amount: 1,
    currency: "BRL",
    effectiveAt: new Date().toISOString(),
    description: "Forbidden",
  }),
});
if (forbiddenWrite.status !== 403) {
  throw new Error(`Scope insuficiente deveria retornar 403, retornou ${forbiddenWrite.status}.`);
}

const otherAccount = await fetch(`${baseUrl}/v1/accounts/${otherAccountId}/balance`, {
  headers: { authorization: `Bearer ${readOnly}` },
});
if (otherAccount.status !== 403) {
  throw new Error(`Outra conta deveria retornar 403, retornou ${otherAccount.status}.`);
}

process.stdout.write("Segurança OK: 401 sem token, 403 sem scope e 403 para outra conta.\n");
