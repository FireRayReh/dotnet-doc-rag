# syntax=docker/dockerfile:1

# ------------------------------------------------------------------------------------------------
# Build stage
# ------------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy csproj files first for better layer caching on dependency restore.
COPY Cryptex.sln ./
COPY src/Cryptex.Core/Cryptex.Core.csproj src/Cryptex.Core/
COPY src/Cryptex.Ingestion/Cryptex.Ingestion.csproj src/Cryptex.Ingestion/
COPY src/Cryptex.Security/Cryptex.Security.csproj src/Cryptex.Security/
COPY src/Cryptex.Embeddings/Cryptex.Embeddings.csproj src/Cryptex.Embeddings/
COPY src/Cryptex.VectorStore/Cryptex.VectorStore.csproj src/Cryptex.VectorStore/
COPY src/Cryptex.Api/Cryptex.Api.csproj src/Cryptex.Api/
COPY src/Cryptex.Tests/Cryptex.Tests.csproj src/Cryptex.Tests/

RUN dotnet restore src/Cryptex.Api/Cryptex.Api.csproj

# Now copy the rest of the source and publish.
COPY src/ src/
RUN dotnet publish src/Cryptex.Api/Cryptex.Api.csproj -c Release -o /app/publish --no-restore

# ------------------------------------------------------------------------------------------------
# Runtime stage
# ------------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Non-root user for the container process.
RUN useradd -m -u 1000 cryptex
RUN mkdir -p /app/data && chown -R cryptex:cryptex /app

COPY --from=build /app/publish ./

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080

USER cryptex
VOLUME ["/app/data"]

ENTRYPOINT ["dotnet", "Cryptex.Api.dll"]
