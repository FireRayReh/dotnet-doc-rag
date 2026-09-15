# DocRag

A .NET 9 RAG (retrieval-augmented generation) backend for enterprise document Q&A, built to sit
behind [LibreChat](https://github.com/danny-avila/LibreChat) as a drop-in replacement for its
default Python `rag_api` sidecar. Point LibreChat's `RAG_API_URL` at this service and its existing
file-upload / RAG UI works unchanged.

## Why this exists

LibreChat ships with a Python FastAPI service (`rag_api`) for document ingestion and retrieval.
DocRag re-implements that HTTP contract in .NET so document ingestion, chunking, encryption
handling, and vector storage can run as part of a single, statically-typed, self-hostable .NET
stack, with first-class support for password-protected Office documents and a pluggable embedding
backend (Azure OpenAI or any local OpenAI-compatible server).

## Architecture

```
                                   ┌─────────────┐
                                   │  LibreChat  │
                                   │  (chat UI + │
                                   │   backend)  │
                                   └──────┬──────┘
                                          │ RAG_API_URL
                                          ▼
 ┌───────────────────────────────────────────────────────────────────────────┐
 │                              DocRag.Api                                   │
 │  ┌────────────────────┐        ┌───────────────────────────────────────┐  │
 │  │  RagApiController   │        │           AdminController             │  │
 │  │  /health /embed      │        │  /admin/ingest-folder                │  │
 │  │  /query /documents   │        │  /admin/documents /admin/status      │  │
 │  └──────────┬──────────┘        └──────────────────┬────────────────────┘  │
 │             └───────────────────┬───────────────────┘                     │
 │                                 ▼                                         │
 │                        IngestionService                                   │
 │            (parse → chunk → embed → upsert → registry)                   │
 └───────┬───────────────┬──────────────────┬─────────────────┬─────────────┘
         ▼               ▼                  ▼                 ▼
 ┌───────────────┐ ┌─────────────┐  ┌────────────────┐ ┌───────────────┐
 │ DocRag.Ingestion│ │DocRag.Security│ │DocRag.Embeddings│ │DocRag.VectorStore│
 │ PdfParser        │ │OfficeCrypto  │  │AzureOpenAI /    │ │Qdrant.Client     │
 │ OpenXmlParser     │ │Decryptor     │  │Local (Ollama/   │ │(dense similarity │
 │ EncryptedXlsx     │ │AesGcmDocument│  │vLLM/llama.cpp)  │ │ search)           │
 │ PlainText / Html  │ │Cipher        │  │                 │ │                   │
 └───────────────┘ └─────────────┘  └────────────────┘ └───────┬───────┘
                                                                 ▼
                                                          ┌─────────────┐
                                                          │   Qdrant    │
                                                          │ (vector DB) │
                                                          └─────────────┘
```

Chunk text and metadata (page_content, source, file_id, ...) live as Qdrant point payloads;
`DocRag.Core` defines the shared domain model (`DocumentChunk`, `IngestedDocument`,
`EmbeddingVector`, `SearchResult`) and the interfaces (`IDocumentParser`, `IEmbeddingProvider`,
`IVectorStore`, `IDocumentCipher`, `IReranker`) every other project implements against, plus the
token-aware chunker (`DocumentChunker`, backed by `Microsoft.ML.Tokenizers`'s tiktoken-compatible
tokenizer).

A lightweight file-backed `DocumentRegistry` (see `DocRag.Api/Services`) tracks ingestion
bookkeeping (file name, status, chunk counts) for the admin endpoints; the actual retrievable state
is the chunk vectors in Qdrant.

## Project layout

```
DocRag.sln
src/
  DocRag.Core/        domain models, interfaces, token-aware chunking
  DocRag.Ingestion/    per-format IDocumentParser implementations + DocumentParserFactory
  DocRag.Security/     OfficeCryptoDecryptor (MS-OFFCRYPTO) + AesGcmDocumentCipher (at-rest AES-256-GCM)
  DocRag.Embeddings/   AzureOpenAIEmbeddingProvider, LocalEmbeddingProvider, factory, reranker
  DocRag.VectorStore/  QdrantVectorStore (Qdrant.Client)
  DocRag.Api/          ASP.NET Core Web API: RagApiController, AdminController, DI wiring
  DocRag.Tests/        xunit tests
docker/
  Dockerfile
  docker-compose.yml
  .env.example
```

## The LibreChat `rag_api` contract

The exact wire shapes this service implements live in one file, deliberately isolated so they're
easy to adjust if your LibreChat version's actual expectations differ:

**`src/DocRag.Api/Contracts/RagApiContracts.cs`**

These are reasonable, documented conventions based on the
[rag_api](https://github.com/danny-avila/rag_api) README and source (its OpenAPI spec wasn't
reachable while building this), covering:

| Method | Path | Purpose |
|---|---|---|
| GET | `/health` | Liveness check (`{"status": "UP"}`) |
| POST | `/embed` | Multipart file upload → parse, chunk, embed, store; returns chunk count |
| POST | `/query` | Similarity search by query text + optional `file_id` filter; returns scored chunks |
| DELETE | `/documents/{id}` | Delete all chunks for one file id |
| DELETE | `/documents` | Delete all chunks for a list of file ids (JSON body `{"file_ids": [...]}`) |
| GET | `/documents/{id}` | Fetch all stored chunks for a file id |

If your LibreChat deployment expects different field names or response shapes, `RagApiContracts.cs`
and `RagApiController.cs` are the only two files you should need to touch.

## Admin endpoints (bulk ingestion, not just per-chat uploads)

| Method | Path | Purpose |
|---|---|---|
| POST | `/admin/ingest-folder` | Bulk-ingest every supported file under a folder (e.g. a mounted company docs share) |
| GET | `/admin/documents` | List all ingested documents (per-chat uploads and bulk-ingested alike) |
| GET | `/admin/documents/{id}` | Get one document's ingestion status |
| POST | `/admin/reindex/{id}` | Re-run ingestion for a document (requires `RetainOriginals`, see below) |
| GET | `/admin/status` | Document/chunk counts and active embedding provider/model |

Example bulk ingest call:

```bash
curl -X POST http://localhost:8080/admin/ingest-folder \
  -H "Content-Type: application/json" \
  -d '{"path": "/data/company-docs", "recursive": true}'
```

## Supported document formats

| Format | Parser | Password-protected support |
|---|---|---|
| PDF | `PdfParser` (UglyToad.PdfPig) | Yes - native |
| DOCX / PPTX | `OpenXmlParser` (DocumentFormat.OpenXml) | Yes - decrypted first via `OfficeCryptoDecryptor` |
| XLSX | `OpenXmlParser` (unencrypted) / `EncryptedXlsxParser` (EPPlus, encrypted) | Yes - EPPlus opens encrypted xlsx directly |
| TXT / MD | `PlainTextParser` | N/A |
| HTML | `HtmlParser` (HtmlAgilityPack) | N/A |

### How encrypted-document ingestion works

1. **PDF**: the password is passed straight into PdfPig, which decrypts natively.
2. **XLSX**: if a password is supplied (or the file is detected as an encrypted CFB container),
   `EncryptedXlsxParser` opens it directly with EPPlus, which supports encrypted OOXML natively.
3. **DOCX/PPTX**: these formats don't have a native-decrypting SDK option in the OpenXml SDK, so
   `OfficeCryptoDecryptor` (in `DocRag.Security`) implements the
   [MS-OFFCRYPTO](https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-offcrypto/)
   spec directly: it opens the CFB (OLE2) container via OpenMcdf, reads the `EncryptionInfo` and
   `EncryptedPackage` streams, and decrypts to a plain OOXML zip in memory before handing it to
   `OpenXmlParser`. Both **Standard** (AES-128/192/256, ECMA-376 key derivation) and **Agile**
   (configurable hash/cipher via an XML descriptor) encryption schemes are implemented in full. An
   unsupported/future scheme throws `UnsupportedOfficeEncryptionException` rather than silently
   returning garbage; a wrong password throws `InvalidDocumentPasswordException`.

**Where passwords come from:** per-request, via the `password` form field on `/embed` or the
`password`/`passwords_by_file` fields on `/admin/ingest-folder` (per-file overrides for folders with
mixed passwords). There is no global password store - each ingestion call carries its own.

### At-rest encryption

Separately from decrypting *incoming* encrypted files, `AesGcmDocumentCipher` (AES-256-GCM) can
encrypt data this service itself persists (original file bytes, if `DocumentRegistry:RetainOriginals`
is enabled). Its key comes only from the `DOC_ENCRYPTION_KEY` environment variable - never
hardcoded, never logged. Generate one with:

```bash
openssl rand -base64 32
```

## Embedding providers (pluggable, model-agnostic)

Set `Embedding:Provider` to `AzureOpenAI` or `Local`:

- **AzureOpenAI**: `AzureOpenAIEmbeddingProvider` via the official `Azure.AI.OpenAI` SDK. Configure
  `Embedding:AzureOpenAI:{Endpoint,ApiKey,Deployment,Dimensions}`. `Deployment` defaults to
  `text-embedding-3-large` but is just a config value - point it at whatever deployment name you
  create in Azure, including future models, without touching code.
- **Local**: `LocalEmbeddingProvider` talks plain HTTP/JSON to any OpenAI-compatible
  `/embeddings` endpoint (Ollama, vLLM, llama.cpp server, LM Studio, ...). Configure
  `Embedding:Local:{BaseUrl,Model,Dimensions,ApiKey}`.

Because both providers implement the same `IEmbeddingProvider` interface and are selected purely
by configuration (`EmbeddingProviderFactory`), swapping in a newer embedding model as they're
released - or moving from local to hosted - never requires a code change, only a config/env change
and (if the dimensionality changed) re-ingesting into a fresh Qdrant collection.

An optional local cross-encoder reranker can be wired in via `Reranker:BaseUrl` (e.g. a
TEI/vLLM/Infinity `/rerank` endpoint); when unset, `SimpleCrossEncoderReranker` is a no-op
passthrough.

## Vector storage

`QdrantVectorStore` uses the official `Qdrant.Client` .NET client for upsert, delete (by id or by
`file_id`), and similarity search with a `file_id` payload filter. It performs dense-vector search
only - the installed client version's high-level helpers (`SearchAsync`/`ScrollAsync`) don't expose
a ready-made hybrid dense+sparse fusion query in this codebase; that would additionally require
configuring a named sparse vector on the collection and issuing a fused `QueryAsync` request. This
is called out as a documented extension point in `QdrantVectorStore.cs` rather than implemented,
since dense-only search already satisfies the rag_api contract.

## Configuration reference (`appsettings.json`)

```jsonc
{
  "Embedding": {
    "Provider": "Local",              // "AzureOpenAI" | "Local"
    "AzureOpenAI": { "Endpoint": "", "ApiKey": "", "Deployment": "text-embedding-3-large", "Dimensions": 3072 },
    "Local": { "BaseUrl": "http://localhost:11434/v1", "Model": "bge-m3", "Dimensions": 1024, "ApiKey": "" }
  },
  "Reranker": { "BaseUrl": "", "Model": "bge-reranker-v2-m3" },
  "VectorStore": { "Qdrant": { "Host": "localhost", "GrpcPort": 6334, "ApiKey": "", "CollectionName": "docrag_chunks" } },
  "Security": { "EncryptionKeyEnvVar": "DOC_ENCRYPTION_KEY" },
  "Chunking": { "MaxTokens": 512, "OverlapTokens": 64 },
  "DocumentRegistry": { "StorePath": "data/documents.json", "OriginalsPath": "data/originals", "RetainOriginals": false }
}
```

Every setting can be overridden by environment variable using ASP.NET Core's standard `__`
separator convention, e.g. `Embedding__Provider=AzureOpenAI`, `VectorStore__Qdrant__Host=qdrant`.

## Running locally

```bash
dotnet restore
dotnet build
dotnet test
dotnet run --project src/DocRag.Api
```

Swagger UI is available at `/swagger` in the Development environment.

## Deployment (docker-compose)

```bash
cd docker
cp .env.example .env   # fill in real values - never commit .env
docker compose up -d --build
```

This brings up: `qdrant` (vector DB), `docrag-api` (this service), `librechat-mongo`, and
`librechat`, with `RAG_API_URL` already wired to `http://docrag-api:8080` so LibreChat's file
upload / RAG UI talks to DocRag transparently. See `docker/.env.example` for every configurable
variable (`AZURE_OPENAI_*`, `LOCAL_EMBEDDING_*`, `QDRANT_*`, `DOC_ENCRYPTION_KEY`, ...).

To bulk-ingest an existing company document share, mount it into the `docrag-api` container and
call `/admin/ingest-folder` with its in-container path.

## Known limitations / left incomplete

- **Hybrid search**: dense-vector-only (see "Vector storage" above); sparse/BM25 fusion is a
  documented extension point, not implemented.
- **Re-indexing**: `/admin/reindex/{id}` requires `DocumentRegistry:RetainOriginals=true` to have
  original bytes to re-parse from, and currently returns `501 Not Implemented` even when originals
  are retained - the decrypt-and-replay path isn't wired up yet. Re-uploading (or re-running the
  bulk folder ingest) achieves the same result today.
- **Agile OOXML encryption**: implemented in full per the MS-OFFCRYPTO spec (not simplified), but
  only unit-tested via a synthetically-built Standard-encryption fixture (see
  `DocRag.Tests/Security/OfficeCryptoDecryptorTests.cs`) - no real Office-produced encrypted
  fixture was available in this environment to byte-for-byte cross-validate either scheme against
  a real Word/Excel/PowerPoint output.
