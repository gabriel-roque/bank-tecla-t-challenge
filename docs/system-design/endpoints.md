# Endpoints previstos

## Roteamento

Externamente existe um único domínio, por exemplo `https://api.banco.com`. No Docker, o Nginx roteia pelo método e caminho; em cloud, essa função é do API Gateway. Internamente Ledger API e Query API são serviços diferentes.

O aplicativo mobile e os parceiros não acessam essas APIs diretamente. Aplicativos usam um BFF/backend de canal; webhooks são recebidos e validados por um serviço de integração, que então chama a Ledger API. Os detalhes estão em [Autenticação e autorização](authentication-authorization.md).

| Método e rota | Serviço | Cliente esperado | Scope exigido | Fonte de dados | Finalidade |
|---|---|---|---|---|---|
| `POST /v1/accounts/{accountId}/movements` | Ledger API | Serviços internos de pagamentos, cartões, depósitos, backoffice ou adaptador de parceiro | `ledger.movements.write` | PostgreSQL Ledger | Registrar crédito ou débito |
| `GET /v1/movements/{movementId}` | Query API | BFF, backoffice e atendimento | `ledger.movements.read` | PostgreSQL Read | Consultar uma movimentação; `movementId` corresponde ao `event_id` |
| `GET /v1/accounts/{accountId}/balance` | Query API | BFF, internet banking, backoffice e serviços internos | `ledger.balance.read` | PostgreSQL Read | Consultar saldo atual projetado |
| `GET /v1/accounts/{accountId}/balance?at={instant}` | Query API | Backoffice, atendimento e auditoria | `ledger.balance.read` | PostgreSQL Ledger | Consultar o saldo histórico no instante informado |
| `GET /v1/accounts/{accountId}/statement?from={instant}&to={instant}` | Query API | BFF, internet banking, backoffice e atendimento | `ledger.statement.read` | PostgreSQL Read | Consultar extrato paginado |

## Autenticação comum

Todos os endpoints de negócio exigem:

```http
Authorization: Bearer <access-token>
```

O token deve possuir o scope indicado na tabela e autorização para a conta presente na rota. Falha de autenticação retorna `401`; identidade válida sem permissão retorna `403`. Ledger API e Query API não expõem endpoints de login ou emissão de token.

## Registrar crédito ou débito

`POST /v1/accounts/{accountId}/movements`

Headers da operação:

```http
Idempotency-Key: 3b681d30-23fc-4ee0-94e2-e05ac51d2018
X-Correlation-Id: 5eaf7828-aa8a-4b35-bff4-bda95118b0b3
```

`Idempotency-Key` é obrigatório no comando. `X-Correlation-Id` é opcional em todos os endpoints; quando não for enviado, a API gera um UUID. As APIs devolvem o identificador no mesmo header de resposta para permitir correlação ponta a ponta.

Exemplo de payload:

```json
{
  "type": "DEBIT",
  "amount": 1000,
  "currency": "BRL",
  "effectiveAt": "2026-11-13T15:30:00Z",
  "description": "Compra",
  "externalReference": "order-123"
}
```

`amount` representa centavos e deve ser maior que zero. No escopo do desafio, somente `BRL` é aceito; qualquer outra moeda retorna `422`. A resposta `201 Created` confirma que o evento já foi gravado no Ledger, mesmo que a projeção ainda não tenha sido atualizada.

O saldo inicial de uma conta sem eventos é zero. O Ledger assume que o identificador opaco da conta já foi validado pelo serviço/canal de origem; o desafio não implementa cadastro de contas.

Erros principais:

- `400`: contrato inválido;
- `401/403`: autenticação ou autorização inválida;
- `409`: mesma `Idempotency-Key` usada com outro payload;
- `422`: regra de negócio rejeitada, como saldo insuficiente;
- `429`: limite de requisições excedido;
- `503`: escrita indisponível e não confirmada.

## Consultar saldo

`GET /v1/accounts/{accountId}/balance`

Parâmetros opcionais:

- `at`: instante histórico em UTC, aplicado sobre `recorded_at`;
- `consistency=eventual|strong`: padrão `eventual`;

Sem `at`, a leitura eventual consulta `account_balances` e inclui `lastSequence` e `projectedAt`. A leitura forte reconstrói o saldo atual no PostgreSQL Ledger.

Se o PostgreSQL Read estiver indisponível, a Query API pode usar o mesmo caminho forte como fallback controlado. Esse acesso utiliza credenciais somente leitura e limites próprios para proteger a escrita no Ledger.

Quando `at` for informado, a Query API faz uma leitura forte no Ledger: seleciona o último snapshot cujo `last_recorded_at <= at` e reproduz, por `sequence`, os eventos posteriores com `recorded_at <= at`.

`at` implica consistência forte. Enviar explicitamente `at` junto de `consistency=eventual` retorna `400`.

Neste escopo, o histórico representa **o saldo que o sistema conhecia naquele momento**, usando `recorded_at`. `effective_at` continua armazenado, mas uma visão bitemporal baseada na data efetiva fica como evolução futura.

## Consultar extrato

`GET /v1/accounts/{accountId}/statement`

Parâmetros: `from`, `to`, `cursor` e `limit`. O extrato vem do Read Model e retorna `nextCursor` para paginação estável. Caso a projeção esteja atrasada, a resposta informa seu checkpoint.

Se o PostgreSQL Read estiver indisponível, o endpoint retorna `503 Service Unavailable`. O MVP não reconstrói extratos diretamente no Ledger.

## Endpoints operacionais

Cada API e Worker expõe apenas na rede interna:

| Endpoint | Uso |
|---|---|
| `GET /health/live` | Confirma que o processo está vivo |
| `GET /health/ready` | Confirma que a instância pode receber trabalho |

Esses endpoints não passam pelo domínio público da API.
