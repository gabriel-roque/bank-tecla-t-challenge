# ADR-004 — Read Model com instância única

- **Status:** Aceito
- **Data:** 2026-11-13

## Contexto

O Read Model armazena projeções de saldo e extrato. Ele não é a fonte da verdade e pode ser reconstruído reproduzindo os eventos do PostgreSQL Ledger. Configurar replicação e failover para esse banco aumentaria a complexidade do ambiente Docker sem ser essencial para demonstrar os requisitos centrais do desafio.

## Decisão

Usar uma única instância de PostgreSQL para o Read Model no escopo do desafio:

```text
Ledger Worker --> PostgreSQL Read único --> Query API
```

Se o Read Model ficar indisponível, as escritas continuam sendo confirmadas no Ledger e acumuladas no Outbox/Kafka. O saldo atual pode usar o fallback forte e o saldo histórico continua lendo o Ledger. Extrato e consulta de movimentação ficam temporariamente indisponíveis, conforme o ADR-007.

## Consequências

### Positivas

- Menos containers e configuração de replicação.
- Ambiente local mais simples de executar e avaliar.
- O esforço permanece concentrado na consistência da fonte da verdade.

### Negativas

- O Read Model é um ponto único de falha para consultas no desafio.
- Extrato e consulta de movimentação podem ficar temporariamente indisponíveis.
- A recuperação pode exigir restauração ou reprojeção dos eventos.

## Evolução para alta disponibilidade

Se o SLA de consultas exigir alta disponibilidade, o PostgreSQL Read poderá utilizar primário e standby por **WAL Streaming Replication assíncrona**. A replicação pode ser assíncrona porque o banco é reconstruível e aceita pequena defasagem.

Após uma falha:

1. o standby é promovido;
2. a Query API e os Ledger Workers usam o novo primário;
3. o Worker retoma o consumo do Kafka;
4. `processed_events` impede que mensagens repetidas alterem o saldo novamente.

Em cloud, essa topologia poderá ser fornecida por um PostgreSQL gerenciado com failover automático, conforme disponibilidade exigida e custo aprovado.
