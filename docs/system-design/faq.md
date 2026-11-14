# Failure and consistency FAQ

Este documento resume os principais cenários de concorrência, idempotência e falha, além do comportamento atual da solução.

## O que acontece quando duas movimentações chegam ao mesmo tempo para a mesma conta?

Cada conta possui uma sequência crescente e o PostgreSQL aplica `UNIQUE (account_id, sequence)`. Apenas uma gravação vence; a operação conflitante recarrega o agregado, revalida saldo e tenta novamente poucas vezes com jitter. Dois débitos concorrentes nunca podem confirmar um saldo negativo.

## Como evitamos movimentações duplicadas depois de retry ou timeout?

O cliente envia `Idempotency-Key`. A chave é persistida por `client_id` junto com o hash do comando e a resposta original, na mesma transação do evento e do Outbox.

- mesma chave e mesmo comando: devolve a resposta original;
- mesma chave com comando diferente: retorna `409 Conflict`;
- timeout com resultado desconhecido: o cliente repete a mesma chave, sem tentar adivinhar se houve commit.

## E se a transação falhar no meio da gravação?

Evento, idempotência, snapshot eventual e mensagem de Outbox usam uma única transação PostgreSQL. Falha antes do commit faz rollback de tudo; não existe evento confirmado sem sua publicação pendente.

## E se uma instância da Ledger API ou Query API morrer?

O Nginx distribui tráfego entre duas réplicas e deixa de encaminhar para a instância indisponível. Como as APIs não guardam sessão local, a outra réplica continua atendendo. Se todas as réplicas de uma API caírem, aquela capacidade fica indisponível até a recuperação.

## E se uma instância do Kafka morrer?

No ambiente local, o tópico usa fator de replicação 2, `min.insync.replicas=2` e o produtor usa `acks=all`. Com um broker indisponível, novas publicações pausam e permanecem no Outbox; créditos e débitos continuam sendo confirmados no Ledger. Quando o broker retorna, o Worker drena o backlog.

Essa escolha prioriza consistência e ausência de perda. Em produção, a recomendação é usar pelo menos três brokers em zonas distintas para tolerar uma falha sem interromper publicações.

## E se todas as instâncias do Kafka ficarem indisponíveis?

O comportamento é o mesmo, com backlog maior: o Ledger continua sendo a fonte da verdade e o Outbox acumula mensagens. O Publisher usa timeout e backoff com jitter, sem loop agressivo. A capacidade do Outbox precisa ser monitorada para aplicar backpressure antes de esgotar armazenamento.

## E se um Worker morrer?

As duas réplicas executam Publisher e Consumer. A outra réplica retoma partições Kafka e mensagens do Outbox. Se todos os Workers caírem, os eventos ficam duráveis no Outbox/Kafka e são retomados após a recuperação.

## Uma mensagem pode ser publicada duas vezes?

Sim. Se o Kafka confirmar e o processo falhar antes de preencher `published_at`, a mensagem será reenviada. A entrega é pelo menos uma vez, e `processed_events` funciona como Inbox idempotente: o mesmo `event_id` não altera saldo nem extrato duas vezes.

## Como evitamos eventos fora de ordem?

Há três proteções:

1. o Outbox só libera a menor sequência pendente de cada conta;
2. Kafka usa `account_id` como chave de partição;
3. o projetor exige `sequence = last_sequence + 1`.

Ao detectar lacuna, o Consumer busca as sequências ausentes no Ledger e as aplica em ordem. Evento inválido ou lacuna irrecuperável pausa a partição e torna o Worker não pronto; o evento não é descartado nem convertido silenciosamente em outro tipo.

## E se o PostgreSQL Read ficar indisponível?

Saldo eventual tenta um fallback forte controlado no Ledger, protegido por timeout, bulkhead e circuit breaker. Consulta de movimentação e extrato retornam `503`, pois não fazem replay sob demanda. O Kafka mantém os eventos até o projetor conseguir retomar.

## E se o Read Model for perdido ou corrompido?

Ele não é fonte da verdade. O modo de replay do Worker limpa e reconstrói saldo, extrato e Inbox a partir de `ledger_events`, em ordem por conta e sequência, validando a quantidade projetada antes de concluir.

## E se o PostgreSQL Ledger primário morrer?

No Compose, o standby recebe WAL síncrono. Novas escritas pausam enquanto ocorre a promoção controlada e a renovação das conexões; o objetivo é RPO 0. O script `scripts/failure/ledger-failover.sh` demonstra o fluxo. Em produção, promoção, DNS e fencing devem ser fornecidos por PostgreSQL gerenciado ou orquestração de HA.

## E se um snapshot estiver corrompido ou em versão desconhecida?

Antes de reconstruir o agregado, a aplicação valida identificadores, sequência, saldo, moeda, versão do schema e checksum do snapshot. Qualquer divergência falha de forma explícita, impedindo que um saldo forte ou histórico use estado silenciosamente corrompido. Como snapshots são derivados, podem ser descartados e recriados a partir dos eventos.

## E se chegarem tipos ou versões desconhecidas de evento?

Ledger e projetor aceitam apenas os tipos e a versão suportados explicitamente. Um contrato desconhecido gera erro, pausa a partição Kafka e falha o readiness do Worker. Ele nunca é interpretado automaticamente como crédito ou débito.

## Como o sistema reage a excesso de carga?

O Nginx aplica rate limit e responde `429`; conexões e timeouts são limitados nas aplicações. Conflitos possuem retry curto, e dependências indisponíveis retornam `503`. Workers usam lotes pequenos e backoff. As metas de 5 mil escritas/s e 10 mil leituras/s precisam ser comprovadas no ambiente alvo pelos runners Gatling e k6.

## Como validar esses comportamentos localmente?

- fluxo principal: `node scripts/validation/smoke.mjs`;
- concorrência: `node scripts/validation/concurrency.mjs`;
- autenticação/autorização: `node scripts/validation/security.mjs`;
- falhas de componentes: scripts em `scripts/failure/`;
- carga Gatling: `sh scripts/performance/run-all-gatling.sh`;
- carga k6: `sh scripts/performance/run-all-k6.sh`;
- testes automatizados: `dotnet test Ledger.slnx -c Release`.

O Docker Compose demonstra os mecanismos em um único host. Multi-AZ, failover automático, backups/PITR e capacidade certificada continuam sendo responsabilidades do ambiente de produção.
