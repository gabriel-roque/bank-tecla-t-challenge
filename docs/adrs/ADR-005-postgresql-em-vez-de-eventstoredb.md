# ADR-005 — PostgreSQL em vez de EventStoreDB

- **Status:** Aceito
- **Data:** 2026-11-13

## Contexto

A solução aplica Event Sourcing no Ledger e CQRS para separar escrita e leitura. Foram consideradas duas alternativas:

1. PostgreSQL como Event Store e outro PostgreSQL como Read Model;
2. EventStoreDB, atualmente chamado KurrentDB, como Event Store e PostgreSQL como Read Model.

Mesmo na segunda alternativa, o PostgreSQL de leitura continuaria necessário para saldo e extrato. Portanto, EventStoreDB substituiria apenas o banco de escrita; não eliminaria os dois modelos do CQRS.

EventStoreDB é uma alternativa tecnicamente válida e oferece recursos nativos como streams, numeração sequencial, concorrência otimista, projeções e persistent subscriptions. Esses recursos constam na [documentação oficial do KurrentDB](https://docs.kurrent.io/getting-started/features).

## Decisão

Usar dois bancos PostgreSQL fisicamente separados:

```text
PostgreSQL Ledger  -- Outbox/Kafka/Worker -->  PostgreSQL Read
fonte da verdade                              projeções
```

O PostgreSQL Ledger armazenará eventos, snapshots, idempotência e Outbox. O PostgreSQL Read armazenará saldo, extrato e controle de eventos processados.

## Justificativa

### Transação única na escrita

Evento, `Idempotency-Key` e mensagem de Outbox são gravados na mesma transação ACID do PostgreSQL. Isso evita dual-write entre a fonte da verdade e o Kafka.

### Concorrência explícita

A restrição `UNIQUE (account_id, sequence)` implementa concorrência otimista e deixa o comportamento verificável por testes de integração SQL.

### Recursos necessários estão disponíveis

PostgreSQL fornece transações ACID, índices, constraints, JSONB, particionamento, backup/PITR e WAL Streaming Replication. Esses recursos são suficientes para a volumetria e o objetivo demonstrativo do desafio.

### Escala independente com CQRS

Ledger e Read Model continuam em bancos separados. Assim, a leitura pode ganhar capacidade sem aumentar desnecessariamente o banco de escrita, mesmo ambos sendo PostgreSQL.

## Por que EventStoreDB não foi escolhido agora

EventStoreDB possui vantagens reais para sistemas centrados em streams. Persistent subscriptions, por exemplo, mantêm checkpoints no servidor e suportam consumidores concorrentes com entrega pelo menos uma vez, conforme a [documentação oficial](https://docs.kurrent.io/server/v23.10/features/persistent-subscriptions).

Porém, neste escopo ele adicionaria:

- uma tecnologia especializada para implantar, proteger, monitorar, fazer backup e recuperar;
- outro modelo de operação e troubleshooting para o time;
- dependência de SDK e práticas específicas do produto;
- a permanência do PostgreSQL Read, pois as consultas continuam usando projeções próprias.

A decisão não afirma que PostgreSQL é sempre superior. Ela prioriza simplicidade, domínio operacional e transação conjunta com idempotência e Outbox neste desafio.

## Quando reavaliar

A decisão deverá ser revista se testes demonstrarem que PostgreSQL não atende à volumetria, se streams e subscriptions passarem a dominar a plataforma ou se a organização já possuir EventStoreDB/KurrentDB operado como serviço padrão. Nesse caso, uma migração deverá ser precedida por testes de carga, replay, recuperação de desastre e custo operacional.
