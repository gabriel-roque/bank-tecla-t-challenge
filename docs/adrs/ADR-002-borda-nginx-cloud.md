# ADR-002 — Nginx no desafio e borda gerenciada em cloud

- **Status:** Aceito
- **Data:** 2026-11-13

## Contexto

A arquitetura precisa demonstrar um ponto único de entrada, roteamento para múltiplas instâncias, rate limit e proteção básica. Implementar ou emular toda a infraestrutura de borda de uma nuvem aumentaria a complexidade do desafio sem contribuir diretamente para as regras financeiras avaliadas.

## Decisão

No ambiente Docker do desafio será utilizado **Nginx** como componente didático para:

- receber as chamadas HTTP em um único endereço;
- rotear comandos para a Ledger API e consultas para a Query API;
- balancear requisições entre duas instâncias de cada API;
- aplicar rate limit e limites básicos de requisição;
- usar health checks do Docker e detecção passiva de falhas do Nginx para evitar instâncias indisponíveis.

O Nginx não será apresentado como substituto completo de um WAF ou API Gateway de produção.

## Cenário em cloud

Em uma infraestrutura de cloud serão usados serviços gerenciados:

- **WAF:** proteção contra ataques conhecidos, regras OWASP, bloqueios por origem/IP e filtros de requisição;
- **API Gateway:** autenticação, autorização, quotas, rate limit, versionamento e roteamento das APIs;
- **Load Balancer:** distribuição do tráfego entre réplicas saudáveis em múltiplas zonas de disponibilidade.

As regras serão versionadas como infraestrutura como código e poderão variar por ambiente.

## Consequências

### Positivas

- Ambiente local simples e reproduzível com Docker Compose.
- Demonstra balanceamento e rate limit sem depender de conta em cloud.
- Mantém clara a arquitetura pretendida para produção.

### Negativas

- O ambiente local não reproduz todas as proteções de um WAF gerenciado.
- A redundância local não representa multi-AZ, pois todos os containers executam na mesma máquina.
- Regras do Nginx não devem ser copiadas diretamente para produção sem revisão de segurança.
