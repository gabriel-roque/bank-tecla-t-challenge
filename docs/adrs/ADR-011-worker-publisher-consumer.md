# ADR-011 — Publisher e Consumer na mesma aplicação Worker

- **Status:** Aceito
- **Data:** 2026-11-14

## Contexto

A arquitetura precisa publicar eventos confirmados no Kafka e projetá-los no PostgreSQL Read. Essas responsabilidades possuem dependências, backlogs e critérios de escala diferentes, mas criar duas novas aplicações aumentaria o escopo operacional do desafio e deixaria de atender à decisão de manter três aplicações implantáveis.

Foram consideradas duas opções:

1. Outbox Publisher e Projection Consumer em aplicações e deployments separados;
2. dois serviços de background independentes dentro da mesma aplicação Ledger Worker.

## Decisão

No desafio, adotar a segunda opção. `Ledger.Worker` executará:

- `OutboxPublisher`, responsável por PostgreSQL Ledger -> Kafka;
- `ProjectionConsumer`, responsável por Kafka -> PostgreSQL Read.

Serão executadas duas réplicas da aplicação. O Publisher coordena o Outbox com `FOR UPDATE SKIP LOCKED`; o Consumer usa um único consumer group e uma Inbox idempotente no PostgreSQL Read.

Cada serviço terá seu próprio loop, timeout, retry com backoff e tratamento de falhas. Uma indisponibilidade recuperável do PostgreSQL Read não deve impedir o Publisher de continuar enviando mensagens ao Kafka. Uma indisponibilidade do Kafka não altera eventos já confirmados no Ledger.

## Semântica dos backlogs

- Kafka indisponível ou Publisher parado: mensagens permanecem no Outbox.
- Consumer ou PostgreSQL Read indisponível: mensagens já publicadas permanecem no Kafka e aumentam o consumer lag.
- aplicação Worker inteira indisponível: nenhuma publicação nova ocorre e o Outbox cresce até uma réplica voltar.

Em todos os casos, a entrega continua sendo pelo menos uma vez. Duplicatas são esperadas e não alteram o resultado devido a `processed_events` e às transações do Read Model.

## Consequências

### Positivas

- mantém três aplicações implantáveis no desafio;
- reduz imagens, configurações e pipelines;
- demonstra coordenação entre réplicas, Outbox e consumo idempotente;
- permite que Publisher e Consumer continuem independentes no código.

### Negativas

- Publisher e Consumer recebem a mesma quantidade de réplicas, mesmo que seus backlogs sejam diferentes;
- uma falha fatal do processo interrompe as duas responsabilidades naquela réplica;
- escalar o Worker por um único gargalo também replica a responsabilidade que não precisava crescer.

## Evolução para produção

Se volume, isolamento ou operação exigirem escala independente, a mesma imagem poderá receber papéis configuráveis ou as responsabilidades poderão ser separadas em deployments próprios. A mudança não altera o Ledger, o tópico Kafka, o Read Model ou as garantias de idempotência.

Os detalhes de execução e recuperação estão em [`docs/system-design/worker.md`](../system-design/worker.md).
