# ADR-008 — Documentação com OpenAPI, Scalar e AsyncAPI

- **Status:** Aceito
- **Data:** 2026-11-14

## Contexto

A solução possui dois tipos de contrato:

- endpoints HTTP síncronos expostos pela Ledger API e pela Query API;
- eventos assíncronos publicados no Kafka e consumidos pelo Ledger Worker.

Esses contratos precisam estar documentados no repositório de forma legível por pessoas e ferramentas, sem confundir a documentação HTTP com a documentação dos eventos.

## Decisão

### APIs HTTP

Usar **OpenAPI** para descrever os endpoints da Ledger API e da Query API.

Cada API C#/.NET gerará seu documento OpenAPI a partir dos próprios endpoints e modelos. O **Scalar** será usado como interface interativa para visualizar e testar esses contratos no ambiente de desenvolvimento.

Documentos previstos:

```text
/docs/ledger/openapi/v1.json
/docs/ledger/scalar/v1
/docs/query/openapi/v1.json
/docs/query/scalar/v1
```

O Scalar é a interface de documentação; o OpenAPI continua sendo o contrato HTTP.

### Eventos Kafka

Manter um arquivo **AsyncAPI em YAML** versionado no repositório:

```text
docs/asyncapi.yaml
```

Esse documento descreverá:

- servidores e protocolo Kafka;
- tópico utilizado pelo Ledger;
- eventos `CreditPosted` e `DebitPosted`;
- payload, headers, `correlation_id` e versão do evento;
- `account_id` como chave de partição;
- Ledger Worker como publicador e consumidor;
- garantias de ordenação e entrega pelo menos uma vez.

O Scalar poderá exibir o documento AsyncAPI, mas não será responsável por produzi-lo. O arquivo YAML será o contrato assíncrono versionado.

## Escopo do desafio

Serão entregues:

- OpenAPI gerado pelas duas APIs;
- Scalar habilitado no ambiente local/de desenvolvimento;
- `docs/asyncapi.yaml` com o tópico e os dois eventos do MVP.

Não fazem parte do MVP portal público de documentação, geração automática de clientes ou publicação externa dos contratos.

## Segurança

- Scalar e o endpoint OpenAPI ficarão habilitados por padrão somente no ambiente de desenvolvimento;
- nenhum exemplo conterá token, segredo ou PII real;
- a exposição em produção exigirá decisão explícita e controles de acesso.

## Consequências

### Positivas

- contratos HTTP e Kafka ficam claros e versionados;
- endpoints podem ser explorados localmente pelo Scalar;
- consumidores entendem schema, versão, partição e semântica dos eventos;
- alterações incompatíveis podem ser detectadas por testes de contrato.

### Negativas

- o AsyncAPI YAML precisa ser atualizado junto com os eventos C#;
- existe risco de divergência entre implementação e contrato assíncrono;
- testes de contrato serão necessários para reduzir esse risco.
