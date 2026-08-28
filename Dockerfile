# Imagem da API do Dizido.
#
#   docker build -t dizido-api .
#   docker run --rm -p 8080:8080 dizido-api
#
# Multi-stage: o primeiro estágio tem o SDK inteiro (compilador, NuGet, ~900 MB) e o segundo
# só o runtime. O que vai para produção é o segundo. Compilar e executar na mesma imagem
# entregaria ao servidor o compilador, o código-fonte e o cache do NuGet — mais superfície de
# ataque e dez vezes o tamanho, para nada.

# ---------------------------------------------------------------------------
# Estágio 1 — compilar
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /origem

# Os .csproj primeiro, e o resto do código só depois.
#
# Cada linha do Dockerfile vira uma camada em cache, invalidada quando o que ela copia muda.
# Copiando só os .csproj antes do restore, mexer numa linha de C# não refaz o download dos
# pacotes — que é o passo lento. Copiar tudo de uma vez faria cada build baixar o NuGet inteiro
# de novo.
# O .editorconfig vai junto, e não é detalhe de editor: é ele que marca a pasta de migrations
# como código gerado. Sem ele aqui dentro, os analisadores tratam as migrations como código
# comum, o TreatWarningsAsErrors transforma os avisos em erro e o build quebra **só no Docker**
# — passando na máquina, onde o arquivo existe. É o tipo de diferença que consome uma tarde.
COPY Directory.Build.props Dizido.sln .editorconfig ./
COPY src/Dizido.Domain/Dizido.Domain.csproj src/Dizido.Domain/
COPY src/Dizido.Contracts/Dizido.Contracts.csproj src/Dizido.Contracts/
COPY src/Dizido.Infrastructure/Dizido.Infrastructure.csproj src/Dizido.Infrastructure/
COPY src/Dizido.Api/Dizido.Api.csproj src/Dizido.Api/

# O restore precisa saber o RID, senão o publish com --runtime falha pedindo restore de novo.
RUN dotnet restore src/Dizido.Api/Dizido.Api.csproj --runtime linux-x64

COPY src/ src/

# Três opções aqui, e as três valem uma explicação:
#
# --runtime linux-x64: sem isso, o publish copia os binários nativos de TODAS as plataformas
#   que os pacotes suportam. O SkiaSharp traz um libSkiaSharp de dezenas de MB para Windows,
#   macOS, arm, musl... e a pasta runtimes/ sozinha passou de 440 MB. Com o RID fixado, vai
#   só o de linux-x64.
#
# --self-contained false: o runtime do .NET já está na imagem base. Embutir outra cópia
#   engordaria o resultado sem ganho nenhum.
#
# SatelliteResourceLanguages=en: as bibliotecas trazem suas mensagens traduzidas em treze
#   idiomas. São mensagens de framework, que vão para o log — e log se pesquisa melhor em
#   inglês, que é como as respostas na internet estão escritas.
#
# --no-restore porque o restore já rodou acima, na camada em cache.
RUN dotnet publish src/Dizido.Api/Dizido.Api.csproj \
    --configuration Release \
    --runtime linux-x64 \
    --self-contained false \
    --no-restore \
    -p:SatelliteResourceLanguages=en \
    --output /app

# ---------------------------------------------------------------------------
# Estágio 2 — executar
# ---------------------------------------------------------------------------
#
# "chiseled" é uma imagem Ubuntu reduzida ao que o .NET precisa: sem shell, sem gerenciador de
# pacotes, sem utilitários. Isso corta o tamanho e, mais importante, o que um invasor encontra
# se conseguir executar código aqui dentro — não há bash para abrir nem curl para baixar nada.
#
# O preço é não dar para "entrar no contêiner e olhar". Depurar em produção passa a ser por log
# e métrica, que é como deveria ser de qualquer forma.
#
# Não é Alpine porque o SkiaSharp (miniaturas) traz binário nativo compilado para glibc, e o
# Alpine usa musl. Daí também o pacote NoDependencies: a variante comum exige fontconfig, que
# a imagem chiseled não tem — e nós não desenhamos texto, só redimensionamos imagem.
#
# Um ruído esperado no start: "Cannot load library libgssapi_krb5.so.2". É o Npgsql procurando
# a biblioteca de Kerberos, que a imagem enxuta não traz. Inofensivo — nos autenticamos por
# senha, não por Kerberos, e a conexão funciona. Some se um dia a imagem base ganhar a lib;
# não vale engordá-la por isso.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app

# A imagem chiseled já roda como usuário sem privilégio (uid 1654). Declarado aqui de forma
# explícita para que a intenção sobreviva a uma troca de imagem base.
USER $APP_UID

COPY --from=build /app .

# 8080 e não 80: portas abaixo de 1024 exigem privilégio, e o processo não roda como root.
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080

# Sem shell na imagem, a forma de lista é obrigatória — a forma de string ("dotnet Dizido.Api.dll")
# seria executada por /bin/sh, que não existe aqui.
ENTRYPOINT ["dotnet", "Dizido.Api.dll"]
