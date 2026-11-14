# ADR-009 — Estratégia de testes automatizados e resiliência

- **Status:** Aceito
- **Data:** 2026-11-14

## Contexto

A solução precisa comprovar regras financeiras, consistência, concorrência, integração com PostgreSQL e Kafka, comportamento sob carga e reação a falhas parciais. Uma única ferramenta não cobre adequadamente todos esses níveis.

Gatling e k6 possuem capacidades parcialmente sobrepostas. Para evitar testes duplicados, cada ferramenta terá uma finalidade específica.

## Decisão

Adotar quatro grupos de testes:

| Ferramenta | Responsabilidade |
|---|---|
| xUnit / xUnit + Testcontainers | Testes unitários, invariantes e integrações reais com PostgreSQL e Kafka |
| Gatling TypeScript | Carga HTTP repetível, concorrência de requisições, idempotência e comportamento das APIs |
| k6 | Stress, spike, soak e descoberta do limite de capacidade |
| Docker Compose/scripts | Provocar indisponibilidade e coordenar testes de resiliência |

## xUnit e Testcontainers

Cobrirão:

- regras de crédito e débito;
- reconstrução do saldo por eventos e snapshots;
- concorrência otimista por `account_id + sequence`;
- repetição da mesma `Idempotency-Key`;
- transação única entre evento, idempotência e Outbox;
- dois Publishers concorrentes sem inversão da sequência de eventos da mesma conta;
- publicação e redelivery de mensagens Kafka;
- projeção idempotente em `processed_events`;
- replay e reconstrução do PostgreSQL Read;
- migrations e constraints dos dois bancos.

Esses testes verificam corretude e consistência. Eles não serão substituídos por testes de carga.

## Gatling TypeScript

Será usado no nível HTTP para cenários repetíveis de carga e comportamento:

- créditos e débitos simultâneos;
- múltiplos débitos concorrentes na mesma conta;
- repetição da mesma `Idempotency-Key`;
- consultas de saldo atual, histórico e extrato;
- mistura controlada entre comandos e consultas;
- validação de status, payload e tempo de resposta.

O Gatling não acessará PostgreSQL ou Kafka diretamente. Os testes serão executados contra o Nginx, reproduzindo o caminho externo das requisições.

## k6

Será usado para testes que buscam os limites da solução:

- **stress:** aumentar gradualmente a carga até ocorrer degradação;
- **spike:** aplicar um pico repentino de tráfego;
- **soak:** manter carga por período prolongado para detectar degradação;
- **breakpoint:** identificar a capacidade máxima sustentável.

Os scripts definirão thresholds, como percentis de latência e taxa máxima de erro. Os valores definitivos serão estabelecidos após uma execução de referência no ambiente local.

Os perfis implementados são `targets` (referência constante), `stress` (rampa progressiva), `spike` (salto súbito até a meta), `soak` (duração configurável) e `breakpoint` (degraus de capacidade). A carga agregada é distribuída entre contas; concorrência sobre a mesma conta permanece em cenários comportamentais dedicados para não confundir contenção de um agregado com capacidade total da API.

O k6 também atuará somente por HTTP. Não será utilizada extensão Kafka no MVP.

## Docker Compose e scripts de falha

Scripts externos coordenarão a interrupção e recuperação dos containers enquanto Gatling ou k6 continuam enviando requisições.

Cenários previstos:

| Falha provocada | Resultado esperado |
|---|---|
| Uma réplica de API é interrompida | Nginx encaminha para a réplica saudável |
| PostgreSQL Read é interrompido | Saldo usa fallback forte; extrato e movimentação retornam `503` |
| Kafka é interrompido | Créditos e débitos continuam; mensagens acumulam no Outbox |
| Projection Consumer é interrompido | Ledger continua correto, consumer lag cresce no Kafka e Read Model fica defasado |
| PostgreSQL Ledger primário é interrompido | Failover controlado para o standby, sem duplicar movimentações |

Os scripts Docker provocam a falha; Gatling ou k6 apenas geram tráfego e validam o comportamento HTTP.

## Execução no pipeline

- testes xUnit rápidos serão executados em todo pull request;
- testes com Testcontainers serão executados antes do merge;
- um cenário Gatling de curta duração validará regressões HTTP;
- stress, spike, soak e failover serão executados manualmente ou em pipeline agendado, pois consomem mais tempo e recursos.

## Consequências

### Positivas

- cobertura separada de corretude, integração, carga, stress e falhas;
- cenários financeiros críticos verificados com infraestrutura real;
- falhas parciais reproduzíveis no ambiente Docker;
- thresholds permitem falhar o pipeline diante de regressões relevantes.

### Negativas

- Gatling e k6 adicionam duas stacks de testes de desempenho;
- cenários semelhantes podem divergir se não houver disciplina de escopo;
- testes de stress e resiliência aumentam o tempo e o consumo de recursos;
- o ambiente Docker local não representa capacidade ou disponibilidade reais de uma cloud.

## Regra para evitar duplicação

Gatling não será usado para buscar o limite máximo da solução. k6 não repetirá toda a suíte comportamental do Gatling. Cenários compartilhados serão extraídos para dados e critérios comuns sempre que possível.
