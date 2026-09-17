# Dizido

Chat para equipes pequenas onde **o que foi decidido não se perde na conversa**.

Todo grupo de trabalho vive o mesmo problema: a decisão acontece no meio de duzentas
mensagens e, duas semanas depois, ninguém acha. No Dizido a decisão é um objeto de primeira
classe — fica registrada à parte, ligada à mensagem que a originou, e continua legível
quando o histórico já rolou longe.

**.NET 10 · Blazor · SignalR · PostgreSQL · Redis · Docker**

## O que tem

- **Conversas** diretas e em grupo, com papel por membro
- **Mensagens em tempo real** por SignalR, com controle de presença
- **Decisões** registradas separadamente das mensagens
- **Anexos** verificados pelo tipo real do arquivo (*magic numbers*), não pela extensão,
  guardados em armazenamento compatível com S3 e com imagens processadas por SkiaSharp
- **Reações** às mensagens
- **Autenticação** com ASP.NET Identity e token de acesso
- **Limites de uso** e **faxina** periódica dos dados temporários
- **Health checks** e registro estruturado com Serilog

## Organização

O código é separado por responsabilidade, não por tipo de arquivo:

| Projeto | Responsabilidade |
|---|---|
| `Dizido.Domain` | Entidades e regras — sem dependências |
| `Dizido.Contracts` | Contratos trocados entre cliente e servidor |
| `Dizido.Infrastructure` | Persistência (EF Core + Npgsql), S3, serviços externos |
| `Dizido.Api` | Endpoints, autenticação, tempo real |
| `Dizido.Ui` / `Dizido.Client` | Componentes e cliente Blazor |
| `Dizido.Web` | Aplicação que junta tudo e é servida |

Testes em `tests/`: `Dizido.Domain.Tests` (regras), `Dizido.Api.Tests` (endpoints) e
`Dizido.LoadTests` (carga).

## Como rodar

```bash
docker compose up -d                      # PostgreSQL 16 e Redis 7
dotnet run --project src/Dizido.Web
```

O `docker-compose.yml` só sobe as dependências; a aplicação roda pelo SDK em
desenvolvimento. As conexões ficam em `src/Dizido.Api/appsettings.Development.json`.

## Testes

```bash
dotnet test
```

A integração contínua roda `restore`, `build` em Release e a bateria de testes a cada push,
publicando o resultado como artefato.

## Publicação

`docker-compose.prod.yml` sobe a aplicação junto com o **Caddy**, que cuida do HTTPS e da
renovação do certificado sozinho. Em `deploy/` estão o `Caddyfile`, o exemplo de variáveis
de ambiente e os scripts de **backup** e **restauração** do banco.
