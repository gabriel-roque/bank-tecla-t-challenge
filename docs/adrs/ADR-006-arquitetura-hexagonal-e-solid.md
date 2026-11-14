# ADR-006 — Arquitetura Hexagonal e SOLID

- **Status:** Aceito
- **Data:** 2026-11-14

## Contexto

A solução possui três aplicações independentes: Ledger API, Query API e Ledger Worker. Elas precisam manter as regras de negócio isoladas de HTTP, PostgreSQL e Kafka, facilitando testes e permitindo substituir detalhes de infraestrutura sem alterar os casos de uso.

Foram consideradas Clean Architecture e Arquitetura Hexagonal. As duas atendem ao objetivo, porém a Clean Architecture poderia introduzir mais camadas e estrutura do que o necessário para o escopo do desafio.

## Decisão

Adotar **Arquitetura Hexagonal (Ports and Adapters)** no bounded context de Ledger, aplicando os princípios **SOLID** de forma pragmática. O domínio e a aplicação formam um único hexágono compartilhado por três hosts implantáveis; não existe um domínio duplicado para cada processo.

O bounded context será organizado em:

- **Domínio:** entidades, regras e invariantes de negócio, sem dependência de infraestrutura;
- **Aplicação:** casos de uso e portas necessárias para executá-los;
- **Adaptadores de entrada:** endpoints HTTP e consumidores Kafka;
- **Adaptadores de saída:** PostgreSQL, Kafka, Outbox e demais integrações.

As dependências apontam dos adaptadores para o núcleo da aplicação. O domínio não conhece ASP.NET, Entity Framework, Kafka ou PostgreSQL.

## Aplicação por serviço

### Ledger API

Concentra as regras de crédito, débito, idempotência e concorrência. Endpoints HTTP são adaptadores de entrada; persistência de eventos e Outbox são adaptadores de saída.

### Query API

Mantém uma estrutura mais simples, voltada aos casos de uso de saldo e extrato. O PostgreSQL Read é acessado por uma porta específica de consulta.

### Ledger Worker

Os `BackgroundService` de Outbox e Projection Consumer são adaptadores de entrada executados em background. Eles cuidam apenas do protocolo operacional — agendamento, polling, commit, pause, retry e backoff — e chamam `IPublishPendingOutboxUseCase` e `IProjectLedgerEventUseCase`. A orquestração de publicação e recuperação de lacunas permanece na Application. Kafka, PostgreSQL Ledger e PostgreSQL Read são adaptadores de saída acessados por portas.

## Uso de SOLID

- **Responsabilidade única:** cada caso de uso e adaptador possui uma finalidade clara;
- **Inversão de dependência:** os casos de uso dependem de portas, não de bibliotecas ou bancos concretos;
- **Segregação de interfaces:** são usadas portas pequenas e específicas, como `IPostMovementUseCase`, `ILedgerQueryUseCase`, `IPublishPendingOutboxUseCase`, `IProjectLedgerEventUseCase` e `IOutboxPublisher`;
- **Aberto/fechado e substituição de Liskov:** aplicados quando existirem variações reais, evitando hierarquias e abstrações antecipadas.

Não será criado um repositório genérico como `IRepository<T>`, pois ele esconderia operações específicas do Ledger e reduziria a clareza do domínio.

## Consequências

### Benefícios

- regras de negócio testáveis sem banco, Kafka ou servidor HTTP;
- infraestrutura substituível por adaptadores;
- limites claros entre domínio, aplicação e detalhes técnicos;
- evolução independente das três aplicações.

### Trade-offs

- criação de interfaces, mapeamentos e projetos adicionais;
- risco de complexidade acidental caso sejam criadas abstrações sem necessidade;
- disciplina necessária para impedir dependências de infraestrutura no domínio.

## Estrutura implementada

```text
src/
├── Ledger.Domain/                 # agregado, eventos, snapshots e invariantes
├── Ledger.Application/
│   ├── Commands/                  # comandos dos casos de uso
│   ├── Queries/                   # objetos de consulta
│   ├── Results/                   # respostas independentes de transporte
│   ├── Events/                    # contratos de eventos de aplicação
│   ├── UseCases/                  # orquestração dos comandos e consultas
│   └── Ports/
│       ├── Inbound/               # casos de uso chamados pelos adapters de entrada
│       └── Outbound/              # persistência e projeções requeridas pela aplicação
├── Ledger.Adapters/
│   ├── Persistence/
│   │   ├── Ledger/                # adapter PostgreSQL da fonte da verdade
│   │   ├── ReadModel/             # adapter PostgreSQL das projeções
│   │   └── Migrations/            # preparação dos bancos
│   └── Messaging/Kafka/           # adapter Outbox -> Kafka
├── Ledger.Api/
│   ├── Endpoints/                 # adapter HTTP de comandos
│   ├── Middleware/                # correlação HTTP
│   ├── Security/                  # autorização do adapter HTTP
│   └── Configuration/             # composition root e DI
├── Query.Api/
│   ├── Endpoints/                 # adapters HTTP de consulta
│   ├── Resilience/                # proteção do fallback forte
│   └── Configuration/             # composition root e DI
└── Ledger.Worker/
    ├── Services/                  # adapters de entrada em background
    ├── State/                     # estado operacional do host
    ├── Endpoints/                 # endpoints operacionais
    └── Configuration/             # composition root e DI
```

Os `Program.cs` são somente composition roots: configuram o host, selecionam modos operacionais e mapeiam adapters. Regras de negócio permanecem no domínio; orquestração nos casos de uso; PostgreSQL, Kafka e HTTP ficam nas bordas.

Existem três aplicações implantáveis, embora a solução contenha bibliotecas adicionais. Novos projetos C# somente serão criados quando trouxerem isolamento ou ciclos de implantação reais, evitando fragmentação sem benefício.
