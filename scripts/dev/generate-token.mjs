import { createHmac, randomUUID } from "node:crypto";

const base64Url = (value) => Buffer.from(JSON.stringify(value)).toString("base64url");

export function createToken({
  clientId = "local-client",
  scopes = [
    "ledger.movements.write",
    "ledger.movements.read",
    "ledger.balance.read",
    "ledger.statement.read",
  ],
  accountId,
  subject = "local-user",
  expiresInSeconds = 3600,
} = {}) {
  const now = Math.floor(Date.now() / 1000);
  const header = { alg: "HS256", typ: "JWT" };
  const payload = {
    iss: process.env.JWT_ISSUER ?? "ledger-local",
    aud: process.env.JWT_AUDIENCE ?? "ledger-api",
    sub: subject,
    client_id: clientId,
    scope: scopes.join(" "),
    iat: now,
    exp: now + expiresInSeconds,
    jti: randomUUID(),
    ...(accountId ? { account_id: accountId } : {}),
  };
  const unsigned = `${base64Url(header)}.${base64Url(payload)}`;
  const secret = process.env.JWT_SIGNING_KEY ?? "development-only-signing-key-change-me-123456789";
  const signature = createHmac("sha256", secret).update(unsigned).digest("base64url");
  return `${unsigned}.${signature}`;
}

if (process.argv[1] && import.meta.url.endsWith(process.argv[1])) {
  const accountId = process.argv[2];
  const scopes = process.argv[3]?.split(",").filter(Boolean);
  process.stdout.write(createToken({ accountId, scopes }));
}
