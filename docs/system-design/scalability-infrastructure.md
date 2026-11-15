# Scalability and infrastructure

## Caminho da requisição

```text
Cliente
  -> WAF
  -> API Gateway
  -> Load Balancer / Ingress
  -> Ledger API ou Query API
```

| Camada | Papel | O que não resolve |
|---|---|---|
| WAF | Proteção L7, regras OWASP, bloqueio por origem/IP e tamanho de payload | Autorização de negócio e concorrência financeira |
| API Gateway | OIDC, quotas, rate limit, versionamento e roteamento | Durabilidade e consistência dos dados |
| Load Balancer | Distribuição entre réplicas e health checks | Idempotência, saldo correto e mensagens duplicadas |

Em uma nuvem, Gateway e Load Balancer podem estar no mesmo produto gerenciado, mas as responsabilidades continuam separadas.

## Serviços e forma de escala

As metas iniciais do desafio são **5.000 requisições por segundo na Ledger API** e **10.000 requisições por segundo na Query API**. Elas orientam os cenários Gatling/k6, mas precisam ser comprovadas e recalibradas no hardware alvo; o Docker local não representa a capacidade de cloud.

No desafio, o Docker Compose demonstra pelo menos duas instâncias para as aplicações e para o Ledger. O Read Model é uma exceção deliberada e utiliza uma única instância para reduzir a complexidade. Como todos os containers executam na mesma máquina, a topologia local não representa alta disponibilidade real.

| Serviço/workload | Docker no desafio | Possível cenário em cloud | Limite/cuidados |
|---|---:|---|---|
| Ledger API | 2 instâncias | 2+ réplicas multi-AZ com autoscaling por RPS, CPU e latência | Limitar pool de conexões para não saturar o writer PostgreSQL |
| Query API | 2 instâncias | 2+ réplicas multi-AZ com autoscaling independente | Consulta a instância única do PostgreSQL Read no desafio |
| Ledger Worker | 2 instâncias | Escala por Outbox pendente e consumer lag | Publisher escolhe a menor sequência pendente por conta com `SKIP LOCKED`; Consumer usa consumer group |
| Kafka | 2 brokers | Em cloud, 3+ brokers multi-AZ e mais partições conforme throughput | No desafio usa fator de replicação 2; com apenas dois brokers a tolerância a falhas é limitada |
| PostgreSQL Ledger | 1 primário + 1 standby | Banco gerenciado multi-AZ, failover automático, PITR e réplicas adicionais | Escrita da mesma conta precisa continuar serializada |
| PostgreSQL Read | 1 instância | Se o SLA exigir, primário + standby via WAL assíncrono | Pode ser reconstruído; não participa da confirmação do crédito/débito |

No Kafka local, os tópicos financeiros usam `replication.factor=2`, `min.insync.replicas=2` e produtores com `acks=all`. Se um broker cair, a publicação pausa e o Outbox acumula mensagens; a Ledger API continua gravando com segurança no PostgreSQL.

### Mudanças em cloud

Em produção, Docker Compose seria substituído por Kubernetes, ECS ou equivalente. O Nginx local daria lugar a WAF, API Gateway e Load Balancer gerenciados; as réplicas seriam distribuídas em zonas diferentes; PostgreSQL e Kafka usariam serviços gerenciados com failover, backup e recuperação testados. As quantidades seriam ajustadas por testes de carga e custo, sem obrigação de permanecer exatamente em duas instâncias.

## Concorrência dos Ledger Workers

Todos usam o mesmo `group.id`, por exemplo `account-balance-projection`. Kafka entrega cada partição a somente um Worker ativo desse grupo. Como mensagens da mesma conta usam a mesma chave, elas chegam ordenadas ao mesmo consumidor.

Kafka ainda pode redeliver uma mensagem após falha ou rebalanceamento. Portanto, o Worker executa em uma única transação no PostgreSQL Read:

1. insere `event_id` em `processed_events` com chave única;
2. se já existir, não aplica o evento novamente;
3. valida que `sequence = last_sequence + 1`;
4. atualiza saldo, extrato e checkpoint;
5. confirma a transação;
6. somente depois confirma o offset no Kafka.

Isso produz entrega **pelo menos uma vez** no transporte e efeito **uma vez** no Read Model.

## Comportamento em falhas

| Falha | Comportamento |
|---|---|
| Uma instância de API cai | Load Balancer remove a réplica; outras continuam atendendo |
| Kafka fica indisponível | Escritas continuam no Ledger e mensagens aguardam no Outbox |
| Projection Consumer para | Ledger continua correto; mensagens acumulam no Kafka como consumer lag e o Read Model fica atrasado |
| Todas as réplicas do Ledger Worker param | Escritas continuam enquanto houver capacidade segura; mensagens aguardam no Outbox até uma réplica voltar |
| PostgreSQL Read cai | Escritas continuam; saldo usa fallback forte controlado no Ledger; extrato e movimentação retornam `503` |
| PostgreSQL Ledger writer cai | Failover para standby; até concluir, novos comandos retornam erro sem confirmação ambígua |
| Resposta HTTP se perde após commit | Cliente repete a mesma `Idempotency-Key` e recebe o resultado original |

## Controles operacionais

- Deploy em pelo menos duas zonas de disponibilidade.
- Autoscaling com limites mínimos e máximos, Pod Disruption Budget e rolling update.
- Retry exponencial com jitter apenas em operações idempotentes.
- Timeouts, circuit breaker, bulkhead e backpressure.
- Backups, PITR e testes periódicos de restore e failover.
