# syntax=docker/dockerfile:1

# ------------------------------------------------------------------------------------------------
# Build stage
# ------------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy csproj files first for better layer caching on dependency restore.
COPY DocRag.sln ./
COPY src/DocRag.Core/DocRag.Core.csproj src/DocRag.Core/
COPY src/DocRag.Ingestion/DocRag.Ingestion.csproj src/DocRag.Ingestion/
COPY src/DocRag.Security/DocRag.Security.csproj src/DocRag.Security/
COPY src/DocRag.Embeddings/DocRag.Embeddings.csproj src/DocRag.Embeddings/
COPY src/DocRag.VectorStore/DocRag.VectorStore.csproj src/DocRag.VectorStore/
COPY src/DocRag.Api/DocRag.Api.csproj src/DocRag.Api/
COPY src/DocRag.Tests/DocRag.Tests.csproj src/DocRag.Tests/

RUN dotnet restore src/DocRag.Api/DocRag.Api.csproj

# Now copy the rest of the source and publish.
COPY src/ src/
RUN dotnet publish src/DocRag.Api/DocRag.Api.csproj -c Release -o /app/publish --no-restore

# ------------------------------------------------------------------------------------------------
# Runtime stage
# ------------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Non-root user for the container process.
RUN useradd -m -u 1000 docrag
RUN mkdir -p /app/data && chown -R docrag:docrag /app

COPY --from=build /app/publish ./

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080

USER docrag
VOLUME ["/app/data"]

ENTRYPOINT ["dotnet", "DocRag.Api.dll"]
