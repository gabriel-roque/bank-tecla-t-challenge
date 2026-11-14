import { createToken } from "../dev/generate-token.mjs";

const baseUrl = process.env.BASE_URL ?? "http://localhost:8080";
const accountId = process.env.ACCOUNT_ID ?? "11111111-1111-1111-1111-111111111111";
const token = createToken({ accountId });
const headers = { authorization: `Bearer ${token}` };

const balanceResponse = await fetch(`${baseUrl}/v1/accounts/${accountId}/balance`, { headers });
const balance = await balanceResponse.json();
if (balanceResponse.status !== 200 || balance.consistency !== "strong") {
  throw new Error(`Fallback forte falhou: ${balanceResponse.status} ${JSON.stringify(balance)}`);
}

const statement = await fetch(`${baseUrl}/v1/accounts/${accountId}/statement`, { headers });
if (statement.status !== 503) {
  throw new Error(`Extrato deveria retornar 503, retornou ${statement.status}: ${await statement.text()}`);
}

process.stdout.write(`Fallback OK: saldo=${balance.balance} via ${balance.consistency}; extrato=503.\n`);
