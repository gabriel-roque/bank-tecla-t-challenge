# ADR-001 — Ledger simplificado sem partidas dobradas

- **Status:** Aceito
- **Data:** 2026-11-13

## Contexto

O desafio precisa registrar créditos e débitos, consultar saldo e extrato e demonstrar consistência, concorrência e recuperação de falhas. Um ledger de partidas dobradas adicionaria contas contábeis internas, lançamentos de contrapartida e novas regras de reconciliação que não são necessárias para demonstrar esses requisitos.

## Decisão

Não implementar a tabela `ledger_entries` neste escopo. Cada crédito ou débito será armazenado como um evento imutável em `ledger_events`.

O saldo será reconstruído pela sequência de eventos da conta, com snapshots apenas como otimização. Uma futura correção deverá usar evento compensatório, sem alterar o histórico.

## Consequências

### Positivas

- Menos tabelas e regras para implementar e testar.
- Foco nos pontos centrais do desafio: Event Sourcing, CQRS, concorrência, idempotência, Outbox e projeções.
- Código e documentação mais simples para avaliação.

### Negativas

- O modelo não representa a contrapartida contábil de cada movimentação.
- Não garante que débitos e créditos globais do banco somem zero.
- Não deve ser considerado um core bancário contábil completo.

## Evolução para produção

Se a solução passar a processar transferências, liquidação ou contabilidade regulatória, esta decisão deverá ser revista. A evolução incluiria `ledger_transactions`, `ledger_entries`, contas internas, partidas cuja soma seja zero por transação/moeda e reconciliação contábil independente.
