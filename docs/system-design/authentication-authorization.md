# Authentication and authorization

## Propósito

A autenticação comprova **quem está chamando** a API. A autorização determina **o que esse chamador pode fazer** e **sobre quais contas**. Esses controles reduzem acesso indevido, permitem aplicar menor privilégio e produzem uma trilha de auditoria confiável.

Ledger API e Query API são APIs internas do banco. Elas não implementam login, cadastro de usuário, emissão ou renovação de tokens.

## Quem chama as APIs

| Ator | Como acessa o Ledger | Tipo de credencial |
|---|---|---|
| Aplicativo mobile ou internet banking | Chama um BFF/backend de canal; nunca chama o Ledger diretamente | Sessão do cliente no canal; o BFF usa um token delegado internamente |
| Serviço de pagamentos, cartões ou depósitos | Chama diretamente a Ledger API pela rede interna | OAuth 2.1 `client_credentials` |
| Backoffice e atendimento | A interface chama seu backend/BFF, que acessa as APIs | Token delegado com usuário, cliente e scopes autorizados |
| Parceiro externo | Envia webhook ao serviço de integração; o adaptador valida a assinatura e chama a Ledger API | Assinatura do webhook na entrada e `client_credentials` na chamada interna |
| Auditoria | Usa serviço ou backend autorizado para consultas históricas | Token somente leitura e escopo mínimo |
| Ledger Worker | Não chama endpoints de negócio; usa PostgreSQL e Kafka diretamente pela rede interna | Identidade de workload e credenciais próprias de infraestrutura |

Um webhook não chama diretamente `POST /movements`. O serviço de integração valida assinatura, timestamp, replay e contrato do parceiro antes de transformar a mensagem em um comando interno idempotente.

## Fluxos de confiança

### Aplicativo ou internet banking

```text
Cliente -> App/Web -> BFF do canal -> Query API
                              \----> Serviço de pagamento -> Ledger API
```

O BFF autentica o cliente e recebe um token interno contendo o contexto necessário. Para consultas delegadas, o `accountId` da rota precisa estar autorizado para aquele cliente.

### Serviço interno

```text
Serviço interno -> Provedor OIDC -> access token
Serviço interno -> Nginx/API Gateway -> Ledger API ou Query API
```

O token identifica o serviço por `client_id`. Esse valor é obtido das claims validadas e participa do escopo da `Idempotency-Key`; o cliente não pode defini-lo por um header arbitrário.

### Parceiro e webhook

```text
Parceiro -> Webhook/Adaptador -> validação e normalização -> Ledger API
```

A credencial e o payload externos terminam no adaptador. A chamada ao Ledger utiliza identidade interna própria, correlação e `Idempotency-Key` derivada de uma referência estável do parceiro.

## Scopes

| Scope | Permissão |
|---|---|
| `ledger.movements.write` | Registrar crédito ou débito |
| `ledger.movements.read` | Consultar uma movimentação |
| `ledger.balance.read` | Consultar saldo atual ou histórico |
| `ledger.statement.read` | Consultar extrato |

Possuir um scope não concede acesso irrestrito a todas as contas. Tokens delegados também precisam carregar contexto de conta/cliente compatível com a rota. Tokens de serviço são limitados por políticas associadas ao `client_id`.

## Claims mínimas

| Claim | Finalidade |
|---|---|
| `iss` | Identificar o emissor confiável |
| `aud` | Restringir o token às APIs do Ledger |
| `exp`, `iat` | Controlar validade temporal |
| `jti` | Identificar o token para auditoria e resposta a incidentes |
| `client_id` | Identificar a aplicação chamadora |
| `scope` | Informar as operações permitidas |
| `sub` | Identificar o usuário quando existir contexto delegado |
| `account_id` | Restringir a conta no token delegado do MVP |

As APIs validam assinatura, emissor, audiência, expiração e scopes. Dados pessoais completos não são colocados no token nem persistidos nos eventos; são usados identificadores opacos.

## Respostas de segurança

| Situação | Resposta |
|---|---|
| Token ausente, inválido ou expirado | `401 Unauthorized` |
| Scope ausente ou conta não autorizada | `403 Forbidden` |
| Rate limit excedido | `429 Too Many Requests` |

As respostas não informam se uma conta de outro cliente existe. Logs e metadados registram identificadores técnicos, `client_id`, `sub` quando aplicável e `correlation_id`, sem token ou PII desnecessária.

## Implementação no desafio

No Docker local, o Nginx executa roteamento, balanceamento e rate limit, mas não será apresentado como um provedor de identidade. Ledger API e Query API validarão JWTs assinados para o ambiente de desenvolvimento.

Uma chave simétrica exclusivamente local é fornecida por variável de ambiente ao Compose, e o script `scripts/dev/generate-token.mjs` emite tokens curtos para os cenários de teste. O valor padrão é público, didático e não pode ser reutilizado fora do ambiente local. Os testes automatizados produzem tokens com diferentes scopes para verificar respostas `401`, `403` e acessos permitidos.

Essa abordagem mantém as três aplicações de negócio definidas na arquitetura e evita adicionar um servidor de identidade apenas para o desafio.

## Evolução em cloud

Em produção, um provedor OIDC gerenciado emitirá os tokens. API Gateway validará os controles básicos na borda e as APIs continuarão aplicando autorização por scope e conta. Identidades de workloads, mTLS, rotação de chaves, revogação e políticas de acesso serão administradas pela plataforma de segurança do banco.
