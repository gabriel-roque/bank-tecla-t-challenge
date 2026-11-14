# Ledger Worker

## Propósito

O Ledger Worker transporta eventos já confirmados no PostgreSQL Ledger até o PostgreSQL Read sem participar da decisão financeira. Ele não autoriza débitos, não recalcula regras de negócio e não é fonte da verdade.

No desafio, ele é uma das três aplicações implantáveis e executa dois `BackgroundService` independentes:

```text
Ledger.Worker
├── OutboxPublisher      # PostgreSQL Ledger -> Kafka
└── ProjectionConsumer   # Kafka -> PostgreSQL Read
```

Uma falha recuperável em um serviço não deve encerrar o loop do outro. Cada serviço possui seu próprio tratamento de exceções, timeout e retry com backoff e jitter. Como são loops contínuos que precisam retomar o backlog, não se usa circuit breaker no Worker; o bloqueio temporário é obtido pelo backoff. O circuit breaker e o bulkhead são usados no fallback HTTP da Query API, onde é necessário rejeitar rapidamente para proteger o Ledger.

Os `BackgroundService` são adapters de entrada finos:

- `OutboxPublisherBackgroundService` controla agendamento, retry e backoff e chama `IPublishPendingOutboxUseCase`;
- `ProjectionConsumerBackgroundService` controla polling, commit e pause do Kafka e chama `IProjectLedgerEventUseCase`;
- `OutboxPublishingService` e `LedgerProjectionService`, na Application, concentram a orquestração;
- `IOutboxPublisher`, `ILedgerStore` e `IReadModelStore` são portas de saída implementadas pelos adapters PostgreSQL/Kafka.

Assim, o host não depende diretamente de `PostgresOutboxPublisher` e a recuperação de lacunas não fica acoplada ao consumidor Kafka.

## Outbox Publisher

O Publisher entrega ao Kafka as mensagens que já foram confirmadas junto com o evento financeiro.

Fluxo:

1. busca um lote de `outbox_messages` com `published_at IS NULL`;
2. considera elegível somente a menor `aggregate_sequence` pendente de cada conta;
3. coordena a disputa entre réplicas com `FOR UPDATE SKIP LOCKED`;
4. publica usando `account_id` como chave de partição;
5. aguarda a confirmação do Kafka com `acks=all`;
6. preenche `published_at` somente após a confirmação;
7. em caso de falha, libera a mensagem para uma nova tentativa com backoff.

Uma réplica que encontrar a sequência mais antiga de uma conta bloqueada poderá processar outras contas, mas não a sequência posterior daquela mesma conta. Isso preserva a ordem por `account_id` antes de o Kafka aplicar sua própria ordenação por partição.

Se o processo falhar depois da confirmação do Kafka, mas antes de preencher `published_at`, a mensagem poderá ser publicada novamente. Isso é esperado na entrega pelo menos uma vez e será neutralizado pelo consumidor idempotente.

No MVP, o bloqueio com `SKIP LOCKED` permanece durante o processamento do pequeno lote. Em uma evolução de maior escala, pode ser substituído por um lease com `locked_at`, `locked_by` e expiração, evitando manter uma transação aberta durante a chamada de rede.

A escolha, a consulta de referência e seus trade-offs estão formalizados no [ADR-012](../adrs/ADR-012-outbox-skip-locked.md).

## Projection Consumer

As duas réplicas usam o mesmo `group.id`. O Kafka atribui cada partição a somente um consumidor ativo do grupo, e eventos da mesma conta permanecem na mesma partição.

Para cada evento, o Consumer executa uma única transação no PostgreSQL Read:

1. tenta inserir `(projection_name, event_id)` em `processed_events`;
2. se o evento já existir, não aplica novamente seus efeitos;
3. valida `sequence = last_sequence + 1`;
4. atualiza `account_balances`;
5. insere a linha de `statement_entries`;
6. confirma a transação do banco;
7. somente depois confirma o offset no Kafka.

Se o processo falhar depois do commit no Read Model, mas antes de confirmar o offset, o Kafka entregará a mensagem novamente. A chave única de `processed_events` impede a duplicação do saldo e do extrato.

Uma lacuna de sequência não é ignorada. O caso de uso de projeção tenta recuperar os eventos ausentes no Ledger e aplicá-los em ordem. Se a lacuna for irrecuperável, o Consumer pausa a partição e não confirma offsets posteriores.

## Coordenação entre duas réplicas

| Atividade | Mecanismo de coordenação |
|---|---|
| Buscar mensagens do Outbox | Menor sequência pendente por conta + `FOR UPDATE SKIP LOCKED` impedem ultrapassagem e disputa pelo mesmo lote |
| Consumir Kafka | Mesmo consumer group atribui uma partição a uma única réplica |
| Tratar redelivery | `processed_events` torna a projeção idempotente |
| Preservar ordem por conta | `account_id` como chave de partição e validação de `sequence` |
| Confirmar processamento | Offset somente depois do commit no PostgreSQL Read |

Esses mecanismos evitam que duas instâncias alterem incorretamente o mesmo saldo, sem depender de afinidade entre uma conta e uma réplica específica.

## Comportamento em falhas

| Falha | Onde o backlog fica | Comportamento e recuperação |
|---|---|---|
| Kafka indisponível | PostgreSQL Outbox | Publisher mantém `published_at` nulo e tenta novamente quando Kafka voltar |
| PostgreSQL Read indisponível | Kafka | Publisher continua; consumer lag cresce e a projeção retoma do último offset |
| Projection Consumer indisponível | Kafka | Eventos permanecem retidos no tópico até o Consumer voltar |
| Outbox Publisher indisponível | PostgreSQL Outbox | Eventos confirmados aguardam outra réplica ou a recuperação do Publisher |
| Todas as réplicas do Worker indisponíveis | PostgreSQL Outbox | A Ledger API continua gravando enquanto houver capacidade segura; qualquer réplica retoma as pendências |
| Falha após publicar e antes de marcar Outbox | Possível duplicata no Kafka | `processed_events` elimina o efeito duplicado |
| Falha após projetar e antes de confirmar offset | Redelivery pelo Kafka | Transação e Inbox idempotente eliminam o efeito duplicado |

O sistema não descarta silenciosamente mensagens inválidas. Um evento que não possa ser processado pausa a partição correspondente e deixa evidência do erro para correção. Pular o evento quebraria a ordem financeira da conta.

## Saúde e proteção de capacidade

- `GET /health/live` informa se o processo do Worker está vivo.
- `GET /health/ready` informa o estado dos serviços e de suas dependências sem transformar uma indisponibilidade transitória do Read Model em perda de mensagens.
- retries são limitados e usam backoff; não existe loop agressivo contra PostgreSQL ou Kafka.
- o tamanho do Outbox e o consumer lag são os indicadores de backlog, embora uma stack completa de observabilidade não faça parte do desafio.
- em produção, um limite de segurança deve aplicar backpressure antes que o Outbox esgote o armazenamento do Ledger.

## Escopo do desafio e evolução

No desafio, as duas responsabilidades permanecem na mesma aplicação para manter somente três aplicações implantáveis: Ledger API, Query API e Ledger Worker. Duas réplicas do Worker executam ambos os serviços.

Em produção, Publisher e Consumer podem usar a mesma imagem com papéis configuráveis ou ser separados em deployments próprios. Essa evolução permite escalar pelo tamanho do Outbox e pelo consumer lag de forma independente, sem alterar contratos, eventos ou garantias de idempotência.
