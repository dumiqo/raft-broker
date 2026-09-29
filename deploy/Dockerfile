# syntax=docker/dockerfile:1

# Образ узла (S0-T07).
#
# Базы пришпилены к точным версиям, а не к плавающим 10.0/8.0: образ, который сегодня
# собирается, а завтра уже нет, ломает идею детерминированной сборки из S0-T01.
# Версии обновляются осознанно и вместе с локальным SDK.
#
# SDK 10, а не 8: global.json требует SDK 10.0.100 - он нужен для dotnet test через
# Microsoft.Testing.Platform (ADR 0001), при этом TFM решения остаётся net8.0.

FROM mcr.microsoft.com/dotnet/sdk:10.0.201 AS build

WORKDIR /src

# Файлы сборки и исходники копируются одним слоем, а restore и publish идут одной
# командой. Разделять их на два слоя здесь смысла нет: кэш всё равно сбрасывается при
# любой правке кода, а список из восьми csproj в COPY пришлось бы поддерживать вручную.
COPY global.json Directory.Build.props Directory.Packages.props NuGet.Config ./
COPY proto/ proto/
COPY src/ src/

# CI=true включает RestoreLockedMode: если packages.lock.json разошёлся с csproj,
# сборка образа падает, а не тихо подтягивает другие версии зависимостей.
ENV CI=true
RUN dotnet publish src/RaftBroker.Server/RaftBroker.Server.csproj -c Release -o /app

# Рантайм aspnet, а не runtime: на S4-T01 узел станет gRPC-хостом на Kestrel,
# и менять базовый образ вместе с хостингом не придётся.
FROM mcr.microsoft.com/dotnet/aspnet:8.0.12 AS runtime

WORKDIR /app
COPY --from=build /app ./

# Значение по умолчанию; docker-compose переопределяет порт через RaftBroker__Port.
ENV RaftBroker__Port=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "RaftBroker.Server.dll"]
