# ADR-003 — Replicação PostgreSQL via WAL

- **Status:** Aceito
- **Data:** 2026-11-13

## Contexto

Em produção, o PostgreSQL Ledger deve permanecer disponível após a falha de uma zona. O desafio é executado localmente com Docker Compose e não possui zonas de disponibilidade reais, mas precisa demonstrar o mecanismo de replicação e recuperação adotado para a fonte da verdade.

## Decisão

Usar **PostgreSQL WAL Streaming Replication** no Ledger, entre dois containers, para representar a topologia primário/standby:

```text
PostgreSQL Primary -- WAL --> PostgreSQL Standby
```

No PostgreSQL Ledger, a replicação será síncrona. O primário somente confirma o commit depois que o standby persistir o WAL, buscando demonstrar `RPO 0` durante o teste local.

Configurações principais:

```text
wal_level = replica
synchronous_commit = on
synchronous_standby_names = 'ledger-standby'
hot_standby = on
```

O standby será inicializado a partir de um backup-base do primário e continuará recebendo os registros WAL. A promoção será controlada durante o teste de failover.

## Teste demonstrativo

1. Registrar uma movimentação pela Ledger API.
2. Confirmar que o evento foi replicado para o standby.
3. Interromper o container primário.
4. Promover o standby para primário.
5. Redirecionar a conexão da aplicação para o novo primário.
6. Repetir a requisição com a mesma `Idempotency-Key`.
7. Confirmar que o evento existe uma única vez e que o saldo permanece correto.

## Limitações

- Os dois containers executam na mesma máquina e podem falhar juntos.
- O teste não reproduz latência, isolamento de rede ou falha de uma zona real.
- A promoção local será controlada; em cloud, o serviço gerenciado executaria health checks, fencing e failover automático.
- Esta replicação ocorre entre primário e standby do mesmo banco. A sincronização do Ledger com o Read Model continua sendo feita por Outbox, Kafka e o Projection Consumer do Ledger Worker.

## Cenário em cloud

Em produção, o PostgreSQL gerenciado terá primário e standby em zonas diferentes, endpoint estável, failover automático, backups e recuperação pontual. Para disaster recovery entre regiões, será usado um único writer global e uma réplica remota, normalmente assíncrona, evitando escrita ativa simultânea e split-brain.
