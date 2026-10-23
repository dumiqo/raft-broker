# Образ для Linux-прогона гейта (ADR 0009, задача S1-T11 follow-up).
#
# Почему производный образ, а не готовый mcr.microsoft.com/dotnet/sdk:10.0:
#   * SDK 10 обязателен - без него `dotnet test` не понимает флаг `--solution` (ADR 0001);
#   * тесты собраны под net8.0 и исполняются рантаймом 8.0, а в образе sdk:10.0
#     рантайма 8 нет вовсе (`dotnet --list-runtimes` показывает только 10.0.x).
#     Запускать их на рантайме 10 через DOTNET_ROLL_FORWARD значило бы проверять не тот
#     рантайм, который поедет в контейнер узла. Нужен именно aspnetcore-рантайм: тестовый
#     хост `Microsoft.Testing.Platform` тянет Microsoft.AspNetCore.App 8.0 - без него
#     тесты падают с "You must install or update .NET", не начав прогон.
#
# pwsh в образе уже есть - поэтому отдельная установка PowerShell не нужна.

FROM mcr.microsoft.com/dotnet/sdk:10.0

ARG DOTNET_RUNTIME_CHANNEL=8.0

# aspnetcore, а не dotnet: установщик aspnetcore-рантайма ставит заодно базовый
# Microsoft.NETCore.App той же версии.
RUN curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
    && bash /tmp/dotnet-install.sh \
        --runtime aspnetcore \
        --channel "${DOTNET_RUNTIME_CHANNEL}" \
        --install-dir /usr/share/dotnet \
    && rm -f /tmp/dotnet-install.sh \
    && dotnet --list-runtimes
