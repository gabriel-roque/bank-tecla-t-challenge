# ADR-010 — Escopo dos artefatos de documentação

- **Status:** Aceito
- **Data:** 2026-11-14

## Contexto

Uma solução financeira em produção normalmente possui diagramas C4 completos, runbooks, threat model formal, evidências recorrentes de recuperação e um plano detalhado de disaster recovery. Produzir todos esses artefatos com profundidade adequada aumentaria significativamente o escopo do desafio e desviaria o esforço da arquitetura, da consistência financeira e da implementação executável.

O desafio solicita que as decisões e os trade-offs estejam documentados no próprio repositório, mas não exige que todo o conjunto operacional de uma plataforma bancária em produção seja implementado.

## Decisão

No escopo do desafio serão entregues:

- diagrama principal da arquitetura e diagramas complementares incorporados aos documentos;
- documentação de componentes, endpoints, infraestrutura, modelo de dados, fluxos e comportamento em falhas;
- ADRs para as decisões arquiteturais relevantes;
- README na raiz com instruções reproduzíveis de execução e testes;
- contratos OpenAPI e AsyncAPI definidos no ADR-008.

Os documentos devem distinguir claramente o que será demonstrado no Docker local do que representa a arquitetura alvo em cloud.

## Evolução para produção

São evoluções naturais, mas não fazem parte da entrega do desafio:

- diagramas C4 adicionais de contexto, containers e componentes;
- runbooks operacionais e procedimentos de resposta a incidentes;
- threat model formal, classificação completa de dados e matriz de acessos;
- relatórios e evidências periódicas de backup, restore, PITR e failover;
- plano completo de disaster recovery e continuidade de negócio;
- histórico de capacidade, SLIs/SLOs e resultados de testes de carga.

## Consequências

### Positivas

- mantém o esforço concentrado nos requisitos avaliados e em uma aplicação executável;
- evita apresentar como concluídos artefatos que exigiriam validação operacional real;
- deixa explícito como a documentação evoluiria em um ambiente produtivo.

### Negativas

- a entrega não constitui documentação operacional suficiente para colocar o sistema diretamente em produção;
- uma adoção real exigirá completar e validar os artefatos listados como evolução.
