# Build único da solução. Cada serviço do compose escolhe o projeto via ARG PROJECT.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Chk3PocFrame.slnx ./
COPY src/Shared/*.csproj src/Shared/
COPY src/Integration/*.csproj src/Integration/
COPY src/Opcoes/*.csproj src/Opcoes/
COPY src/Cartao/*.csproj src/Cartao/
COPY src/Autorizacao/*.csproj src/Autorizacao/
COPY src/GatewayMock/*.csproj src/GatewayMock/
COPY src/CanalFake/*.csproj src/CanalFake/
COPY src/Worker/*.csproj src/Worker/
COPY tests/Chk3.Tests/*.csproj tests/Chk3.Tests/
RUN dotnet restore Chk3PocFrame.slnx
COPY . .
ARG PROJECT
RUN dotnet publish src/${PROJECT}/Chk3.${PROJECT}.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 DOTNET_CLI_TELEMETRY_OPTOUT=1
COPY --from=build /out .
ARG PROJECT
ENV ENTRY=Chk3.${PROJECT}.dll
# Usuário não root da imagem oficial.
USER $APP_UID
ENTRYPOINT ["sh", "-c", "exec dotnet $ENTRY"]
