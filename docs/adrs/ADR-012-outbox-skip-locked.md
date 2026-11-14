# ADR-012 — Coordenação do Outbox com `FOR UPDATE SKIP LOCKED`

- **Status:** Aceito
- **Data:** 2026-11-14

## Contexto

O desafio executa duas réplicas do Ledger Worker. As duas podem buscar mensagens pendentes em `outbox_messages`, mas não devem selecionar simultaneamente o mesmo lote para publicação normal.

Além de impedir a seleção duplicada, é necessário preservar a ordem dos eventos de uma conta. Usar apenas `FOR UPDATE SKIP LOCKED` não oferece essa garantia: enquanto uma réplica mantém bloqueada a sequência 1, outra poderia pular essa linha e publicar a sequência 2 da mesma conta primeiro.

Um `SELECT ... FOR UPDATE` convencional faria uma réplica esperar pelas linhas bloqueadas pela outra. Para uma tabela com comportamento de fila, essa espera reduz o paralelismo e pode deixar uma réplica ociosa enquanto existem outras mensagens livres.

Segundo a [documentação oficial do PostgreSQL](https://www.postgresql.org/docs/current/sql-select.html#SQL-FOR-UPDATE-SHARE), `SKIP LOCKED` ignora linhas que não podem ser bloqueadas imediatamente. A documentação também alerta que isso produz uma visão inconsistente e, por isso, é apropriado para tabelas semelhantes a filas, não para consultas gerais.

## Decisão

O Outbox Publisher usará bloqueio pessimista com `FOR UPDATE SKIP LOCKED` para dividir mensagens pendentes entre as réplicas, mas uma mensagem somente será elegível quando não existir uma sequência anterior ainda pendente para a mesma conta.

Para tornar essa regra explícita, `outbox_messages` armazenará `aggregate_sequence`, copiada de `ledger_events.sequence` na mesma transação da escrita:

```sql
BEGIN;

SELECT message_id,
       topic,
       partition_key,
       aggregate_sequence,
       payload,
       headers
FROM outbox_messages AS candidate
WHERE candidate.published_at IS NULL
  AND NOT EXISTS (
      SELECT 1
      FROM outbox_messages AS previous
      WHERE previous.partition_key = candidate.partition_key
        AND previous.published_at IS NULL
        AND previous.aggregate_sequence < candidate.aggregate_sequence
  )
ORDER BY candidate.created_at, candidate.message_id
FOR UPDATE OF candidate SKIP LOCKED
LIMIT @batch_size;

-- Publicar o pequeno lote no Kafka e aguardar a confirmação acks=all.

UPDATE outbox_messages
SET published_at = clock_timestamp(),
    attempts = attempts + 1,
    last_error = NULL
WHERE message_id = ANY(@published_ids);

COMMIT;
```

Enquanto a primeira réplica mantém o evento mais antigo de uma conta bloqueado, a segunda réplica:

- pula a linha bloqueada;
- não considera as sequências posteriores daquela conta elegíveis, pois a anterior continua com `published_at IS NULL`;
- pode selecionar eventos de outras contas imediatamente.

Somente depois da confirmação do Kafka e do `COMMIT` que preenche `published_at` a próxima sequência da conta se torna elegível. Os locks são liberados no `COMMIT` ou `ROLLBACK`.

Um índice parcial em `(partition_key, aggregate_sequence) WHERE published_at IS NULL` apoia a verificação da sequência anterior. Outro índice parcial em `(created_at, message_id) WHERE published_at IS NULL` apoia a seleção global do lote.

`UNIQUE (partition_key, aggregate_sequence)` impede que duas mensagens diferentes representem a mesma sequência da conta. `message_id` completa a ordenação global para que o lote seja determinístico entre mensagens com o mesmo timestamp.

## Escopo da garantia

Essa decisão:

- reduz contenção entre réplicas do Publisher;
- impede que duas transações selecionem normalmente o mesmo lote ao mesmo tempo;
- impede que uma sequência posterior ultrapasse outra ainda pendente da mesma conta;
- permite paralelismo entre contas diferentes;
- não transforma a publicação em exatamente uma vez;
- não cria uma transação distribuída entre PostgreSQL e Kafka.

Se o Worker publicar no Kafka e falhar antes de atualizar `published_at`, a transação sofre rollback e outra réplica publicará a mensagem novamente. Essa duplicata é esperada pela entrega pelo menos uma vez e é neutralizada por `processed_events` no PostgreSQL Read.

Nesse cenário, as possíveis mensagens no Kafka serão `sequência 1`, duplicata da `sequência 1` e depois `sequência 2`; a sequência 2 não será publicada antes da primeira confirmação da sequência 1.

`SKIP LOCKED` será usado somente na fila de Outbox. Ele não será usado para leitura do saldo, reconstrução do agregado ou consultas que precisem enxergar todas as linhas aplicáveis.

## Limites operacionais

No MVP, a transação permanece aberta durante a publicação no Kafka. Para reduzir o tempo dos locks e o impacto de falhas:

- o lote será pequeno e configurável;
- cada lote conterá no máximo uma mensagem pendente por conta;
- a publicação terá timeout curto;
- falhas causarão rollback e retry com backoff exponencial e jitter;
- não haverá espera ativa ou loop agressivo;
- testes de integração executarão múltiplos Publishers concorrentes e verificarão ausência de perda, publicação simultânea duplicada e inversão de sequência por conta.

Manter uma transação aberta durante uma chamada de rede é um trade-off consciente para simplificar o desafio. Em uma volumetria maior, será preferível fazer uma reivindicação curta das mensagens por lease, usando campos como `locked_at`, `locked_by` e expiração, publicar fora da transação e finalizar o estado em outra transação.

## Teste de aceitação concorrente

O teste de integração com PostgreSQL real deverá:

1. inserir várias sequências para uma mesma conta e eventos de outras contas;
2. iniciar pelo menos dois Publishers concorrentes;
3. bloquear a primeira sequência de uma conta em uma réplica;
4. confirmar que a outra réplica processa contas diferentes, mas não seleciona a próxima sequência da conta bloqueada;
5. liberar o primeiro commit e confirmar que a sequência seguinte se torna elegível;
6. verificar que, para cada conta, a ordem observada no produtor Kafka nunca avança sobre uma sequência pendente anterior.

Uma repetição da mesma sequência após falha entre Kafka e PostgreSQL continua permitida. O teste deve rejeitar inversões como `1, 3, 2`, mas aceitar redelivery como `1, 1, 2`.

## Alternativas consideradas

### Uma única réplica do Publisher

Simplificaria a coordenação, mas criaria ponto único de processamento e impediria demonstrar escala horizontal do Worker.

### Lock distribuído externo

Adicionaria outra dependência e serializaria o Publisher inteiro, embora o PostgreSQL já consiga coordenar as próprias linhas da fila.

### Advisory locks

São possíveis, porém exigem uma convenção adicional para gerar e liberar as chaves. O bloqueio das próprias linhas do Outbox deixa a intenção e a transação mais explícitas.

### Lease desde o MVP

Evitaria a transação aberta durante a chamada ao Kafka, mas adicionaria estados, expiração, recuperação de leases abandonados e novas condições de corrida. Fica como evolução caso testes indiquem necessidade.

## Consequências

### Positivas

- duas réplicas processam lotes diferentes sem esperar pelas mesmas linhas;
- não é necessário Redis ou coordenador externo;
- rollback devolve automaticamente as linhas para processamento;
- o comportamento pode ser verificado com PostgreSQL real via Testcontainers.

### Negativas

- a seleção não representa uma visão completa da tabela enquanto outras linhas estiverem bloqueadas;
- uma conta com backlog é publicada sequencialmente, limitando o throughput de uma única conta;
- a transação fica aberta durante a publicação no MVP;
- uma falha na janela entre Kafka e PostgreSQL pode gerar duplicata;
- lotes grandes ou timeouts inadequados aumentariam contenção e consumo de conexões.
