# ADR-007 — Fallback controlado para leitura forte

- **Status:** Aceito
- **Data:** 2026-11-14

## Contexto

O PostgreSQL Read mantém as projeções otimizadas de saldo, extrato e movimentações. No desafio ele possui uma única instância e pode ficar temporariamente indisponível sem afetar a fonte da verdade no PostgreSQL Ledger.

O saldo de uma conta pode ser reconstruído de forma limitada usando o último snapshot e os eventos posteriores. Como o snapshot é criado a cada 50 eventos, normalmente serão reproduzidos no máximo 49 eventos após o snapshot.

Já a reconstrução de extratos e outras consultas projetadas pode exigir a leitura de muitos eventos, paginação e ordenação. Executar esse trabalho automaticamente no Ledger durante uma falha poderia sobrecarregar a fonte da verdade e prejudicar créditos e débitos.

## Decisão

A Query API terá acesso **somente leitura** ao PostgreSQL Ledger para atender:

- saldo histórico com `GET /v1/accounts/{accountId}/balance?at={instant}`;
- saldo atual com `consistency=strong`;
- fallback controlado do saldo atual quando o PostgreSQL Read estiver indisponível.

O usuário de banco da Query API terá apenas permissão de `SELECT` em:

- `ledger_events`;
- `account_snapshots`.

Durante a indisponibilidade do PostgreSQL Read:

| Consulta | Comportamento |
|---|---|
| Saldo atual | Reconstruído no Ledger por snapshot mais eventos posteriores |
| Saldo histórico | Continua atendido pelo Ledger usando `recorded_at <= at` |
| Extrato | Retorna `503 Service Unavailable` |
| Movimentação projetada | Retorna `503 Service Unavailable` |
| Créditos e débitos | Continuam sendo processados pela Ledger API |

O extrato e a consulta de movimentação não terão fallback automático para o Ledger no MVP.

## Proteção da fonte da verdade

O fallback terá:

- timeout curto;
- rate limit próprio;
- circuit breaker;
- limite de concorrência/bulkhead;
- nenhuma tentativa de retry agressivo;
- resposta `503` quando o Ledger estiver sob pressão ou indisponível.

As escritas financeiras possuem prioridade sobre consultas de contingência.

## Consequências

### Positivas

- saldo atual e histórico permanecem disponíveis durante uma falha isolada do Read Model;
- nenhuma escrita é feita pela Query API no Ledger;
- o trabalho de contingência fica limitado a uma conta e a uma pequena cauda de eventos;
- a fonte da verdade é protegida contra consultas extensas de extrato.

### Negativas

- a Query API passa a conhecer os contratos de leitura do Ledger;
- extrato e consulta de movimentação ficam temporariamente indisponíveis;
- o fallback precisa de limites para não competir com créditos e débitos;
- mudanças no schema de eventos e snapshots exigem compatibilidade com a Query API.

## Evolução futura

Se o SLA exigir disponibilidade contínua do extrato, a solução preferencial será adicionar alta disponibilidade ao PostgreSQL Read, conforme o ADR-004, em vez de transformar o Ledger em banco de consultas gerais.
