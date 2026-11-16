# ADR-013 — Kafka com três nós e tolerância à falha

- **Status:** Aceito
- **Data:** 2026-11-16

## Contexto

O Ledger confirma a movimentação financeira e o Outbox na mesma transação PostgreSQL. Kafka transporta esses eventos até o Read Model. A topologia anterior tinha dois nós combinando broker/controller, fator de replicação 2 e `min.insync.replicas=2`.

Essa configuração preservava os eventos no Outbox, mas a perda de um nó interrompia novas publicações por dois motivos independentes:

- restava apenas uma réplica sincronizada, abaixo do mínimo de duas;
- um quórum KRaft de dois controllers exige os dois votos e não tolera a perda de um membro.

O requisito de falhas parciais fica melhor demonstrado quando uma queda isolada não atrasa a projeção, sem reduzir a durabilidade das confirmações Kafka.

## Decisão

Usar três nós Kafka locais, cada um com os papéis `broker,controller`, e configurar:

- quórum KRaft com três voters e maioria de dois;
- seis partições para `ledger.movements.v1`;
- `replication.factor=3`;
- `min.insync.replicas=2`;
- produtor com `acks=all`;
- tópicos internos de offsets e transações com fator de replicação 3;
- bootstrap servers contendo os três brokers.

Com um broker indisponível, duas réplicas continuam no ISR e dois controllers mantêm o quórum. Líderes das partições afetadas são reeleitos, e Publisher e Consumer continuam trabalhando. Com dois brokers indisponíveis, novas publicações falham: o Outbox preserva as mensagens até a recuperação em vez de aceitar uma confirmação com somente uma cópia.

O inicializador reconcilia também clusters já existentes: aumenta o tópico para seis partições quando necessário, aplica `min.insync.replicas=2` e reatribui as réplicas entre os três brokers. Não é necessário apagar volumes apenas para migrar RF=2 para RF=3.

## Validação

O script `scripts/failure/kafka-broker-failover.sh`:

1. valida seis partições, RF=3, min ISR=2 e três membros no ISR;
2. interrompe um broker;
3. aguarda duas réplicas sincronizadas por partição;
4. executa crédito, idempotência, leitura forte/eventual e extrato durante a falha;
5. confirma Outbox sem pendências e consumer lag zero;
6. reinicia o broker;
7. aguarda a recuperação do log e o retorno a três ISR;
8. repete o fluxo e confirma novamente Outbox e offsets convergidos.

## Consequências

### Positivas

- tolera a perda de um broker sem interromper publicação ou projeção;
- mantém duas cópias sincronizadas para cada confirmação `acks=all` durante a falha;
- o quórum KRaft passa a tolerar a perda de um controller;
- torna a recuperação e a convergência verificáveis por script.

### Negativas

- aumenta consumo local de memória, disco, rede e tempo de inicialização;
- três containers na mesma máquina não representam independência de zona;
- combinar broker e controller é adequado ao ambiente didático, mas pode não ser a topologia operacional escolhida para clusters maiores.

## Produção

Os três brokers devem ser distribuídos entre zonas de disponibilidade. Controllers dedicados podem ser adotados conforme escala, isolamento operacional e oferta do serviço gerenciado. Métricas e alertas devem acompanhar ISR reduzido, partições sem líder, latência de produção, Outbox pendente e consumer lag.
