FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY DomainCopilot.sln ./
COPY src/DomainCopilot.Domain/DomainCopilot.Domain.csproj src/DomainCopilot.Domain/
COPY src/DomainCopilot.Application/DomainCopilot.Application.csproj src/DomainCopilot.Application/
COPY src/DomainCopilot.Infrastructure/DomainCopilot.Infrastructure.csproj src/DomainCopilot.Infrastructure/
COPY src/DomainCopilot.Api/DomainCopilot.Api.csproj src/DomainCopilot.Api/
COPY src/DomainCopilot.Cli/DomainCopilot.Cli.csproj src/DomainCopilot.Cli/
RUN dotnet restore src/DomainCopilot.Api/DomainCopilot.Api.csproj
COPY src/ src/
COPY migrations/ migrations/
COPY prompts/ prompts/
COPY corpus/ corpus/
RUN dotnet publish src/DomainCopilot.Api/DomainCopilot.Api.csproj \
    --configuration Release --no-restore --output /app/publish -p:CorpusSourceDirectory=/src/corpus

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=build --chown=app:app /app/publish .
USER app
ENTRYPOINT ["dotnet", "DomainCopilot.Api.dll"]