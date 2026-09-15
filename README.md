# Cryptex

*A cryptex is a cipher-locked cylinder that holds a document - apt for an encrypted-document RAG system.*

A .NET 9 RAG (retrieval-augmented generation) backend for enterprise document Q&A, built to sit
behind [LibreChat](https://github.com/danny-avila/LibreChat) as a drop-in replacement for its
default Python `rag_api` sidecar. Point LibreChat's `RAG_API_URL` at this service and its existing
file-upload / RAG UI works unchanged.

## Why this exists

LibreChat ships with a Python FastAPI service (`rag_api`) for document ingestion and retrieval.
Cryptex re-implements that HTTP contract in .NET so document ingestion, chunking, encryption
handling, and vector storage can run as part of a single, statically-typed, self-hostable .NET
stack, with first-class support for password-protected Office documents, for corpora that a
customer's own code has AES-encrypted into opaque blobs, and a pluggable embedding backend
(Azure OpenAI or any local OpenAI-compatible server).

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
 │                              Cryptex.Api                                   │
 │  ┌────────────────────┐        ┌───────────────────────────────────────┐  │
 │  │  RagApiController   │        │           AdminController             │  │
 │  │  /health /embed      │        │  /admin/ingest-folder                │  │
 │  │  /query /documents   │        │  /admin/documents /admin/status      │  │
 │  └──────────┬──────────┘        └──────────────────┬────────────────────┘  │
 │             └───────────────────┬───────────────────┘                     │
 │                                 ▼                                         │
 │                        IngestionService                                   │
 │   (decrypt envelope → sniff format → parse → chunk → embed → upsert)     │
 └───────┬───────────────┬──────────────────┬─────────────────┬─────────────┘
         ▼               ▼                  ▼                 ▼
 ┌───────────────┐ ┌─────────────┐  ┌────────────────┐ ┌───────────────┐
 │ Cryptex.Ingestion│ │Cryptex.Security│ │Cryptex.Embeddings│ │Cryptex.VectorStore│
 │ PdfParser        │ │AesCbcEnvelope│  │AzureOpenAI /    │ │Qdrant.Client     │
 │ OpenXmlParser     │ │Decryptor     │  │Local (Ollama/   │ │(dense similarity │
 │ EncryptedXlsx     │ │OfficeCrypto  │  │vLLM/llama.cpp)  │ │ search)           │
 │ PlainText / Html  │ │Decryptor     │  │                 │ │                   │
 │ FormatSniffer     │ │AesGcmDocument│  │                 │ │                   │
 │                   │ │Cipher        │  │                 │ │                   │
 └───────────────┘ └─────────────┘  └────────────────┘ └───────┬───────┘
                                                                 ▼
                                                          ┌─────────────┐
                                                          │   Qdrant    │
                                                          │ (vector DB) │
                                                          └─────────────┘
```

Chunk text and metadata (page_content, source, file_id, ...) live as Qdrant point payloads;
`Cryptex.Core` defines the shared domain model (`DocumentChunk`, `IngestedDocument`,
`EmbeddingVector`, `SearchResult`) and the interfaces (`IDocumentParser`, `IEmbeddingProvider`,
`IVectorStore`, `IDocumentCipher`, `IReranker`) every other project implements against, plus the
token-aware chunker (`DocumentChunker`, backed by `Microsoft.ML.Tokenizers`'s tiktoken-compatible
tokenizer).

A lightweight file-backed `DocumentRegistry` (see `Cryptex.Api/Services`) tracks ingestion
bookkeeping (file name, status, chunk counts) for the admin endpoints; the actual retrievable state
is the chunk vectors in Qdrant.

## Project layout

```
Cryptex.sln
src/
  Cryptex.Core/        domain models, interfaces, token-aware chunking
  Cryptex.Ingestion/    per-format IDocumentParser implementations + DocumentParserFactory + FormatSniffer
  Cryptex.Security/     OfficeCryptoDecryptor (MS-OFFCRYPTO) + AesGcmDocumentCipher (at-rest AES-256-GCM)
                        + Envelope/AesCbcEnvelopeDecryptor (customer-encrypted source files)
  Cryptex.Embeddings/   AzureOpenAIEmbeddingProvider, LocalEmbeddingProvider, factory, reranker
  Cryptex.VectorStore/  QdrantVectorStore (Qdrant.Client)
  Cryptex.Api/          ASP.NET Core Web API: RagApiController, AdminController, DI wiring
  Cryptex.Tests/        xunit tests
docker/
  Dockerfile
  docker-compose.yml
  .env.example
```

## The LibreChat `rag_api` contract

The exact wire shapes this service implements live in one file, deliberately isolated so they're
easy to adjust if your LibreChat version's actual expectations differ:

**`src/Cryptex.Api/Contracts/RagApiContracts.cs`**

These are reasonable, documented conventions based on the
[rag_api](https://github.com/danny-avila/rag_api) README and source (its OpenAPI spec wasn't
reachable while building this), covering:

| Method | Path | Purpose |
|---|---|---|
| GET | `/health` | Liveness check (`{"status": "UP"}`) |
| POST | `/embed` | Multipart file upload → (decrypt envelope) → parse, chunk, embed, store; returns chunk count. Optional form fields: `password`, `key_id` (which envelope key to use), `envelope` (force envelope decryption) |
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
| Any of the above, wrapped in a customer AES-CBC envelope (`.enc` / `.aes`) | decrypted by `AesCbcEnvelopeDecryptor`, format resolved by `FormatSniffer`, then parsed as normal | Yes - see "Three distinct kinds of encryption" |

### How password-protected-document ingestion works

(This is concept #2 in "Three distinct kinds of encryption" below - the file is already a valid PDF
or Office container and the *format itself* carries the encryption. For files whose raw bytes your
own code encrypted into an opaque blob, see concept #1, customer-encrypted envelopes.)

1. **PDF**: the password is passed straight into PdfPig, which decrypts natively.
2. **XLSX**: if a password is supplied (or the file is detected as an encrypted CFB container),
   `EncryptedXlsxParser` opens it directly with EPPlus, which supports encrypted OOXML natively.
3. **DOCX/PPTX**: these formats don't have a native-decrypting SDK option in the OpenXml SDK, so
   `OfficeCryptoDecryptor` (in `Cryptex.Security`) implements the
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

### Three distinct kinds of encryption (read this before touching crypto here)

Three unrelated things in this codebase are all called "encryption". They are routinely confused,
so here is exactly what each one is, when it runs, and which class owns it.

| # | Concept | What it is | When it runs | Owned by | Key / secret |
|---|---|---|---|---|---|
| 1 | **Customer-encrypted envelopes** | A source file whose *raw bytes* the customer's own C# code ran AES over, producing an opaque blob (`IV \|\| ciphertext`). The file has no readable structure at all until it is decrypted. | On ingest, **before** format detection and parsing | `Cryptex.Security/Envelope/AesCbcEnvelopeDecryptor` (`IEnvelopeDecryptor`) | `CRYPTEX_ENVELOPE_KEY_<keyId>` (+ optional `CRYPTEX_ENVELOPE_HMAC_KEY`) |
| 2 | **Password-protected Office / PDF** | A file that is *already* a valid PDF or an MS-OFFCRYPTO (OLE2/CFB) container; the format itself carries the encryption. | On ingest, inside the parser factory | `Cryptex.Security/OfficeCryptoDecryptor` (+ PdfPig / EPPlus native decryption) | A per-request document **password**, never a stored key |
| 3 | **At-rest encryption of retained originals** | Encryption *this service applies to data it persists itself*, when `DocumentRegistry:RetainOriginals` is on. Nothing to do with incoming files. | After ingest, on write to disk | `Cryptex.Security/AesGcmDocumentCipher` (`IDocumentCipher`) | `CRYPTEX_AT_REST_KEY` |

A single file can hit more than one of these: an envelope (1) can decrypt to an MS-OFFCRYPTO
container (2), which is then decrypted with a password and parsed - and if originals are retained,
the bytes are re-encrypted on disk with (3).

#### 1. Customer-encrypted envelopes (AES-CBC, IV-prefixed)

For a corpus that the customer's own code encrypted in bulk: read the plaintext file bytes, run
`System.Security.Cryptography.Aes` in CBC mode, write `IV (16 bytes) || ciphertext` out as one
opaque blob. Cryptex reverses that **entirely in memory**, works out what the plaintext actually is,
and hands it to the ordinary parsers - which stay completely unaware encryption was ever involved.

```
report.pdf.enc ──▶ AesCbcEnvelopeDecryptor ──▶ FormatSniffer ──▶ DocumentParserFactory ──▶ chunks
                   (+ optional HMAC check)      (magic bytes ->    (PdfParser, OpenXmlParser, ...)
                                                 inner filename ->
                                                 text heuristic)
```

- **Layout**: `IV || ciphertext`, PKCS#7 padded. `Envelope:IvLength` and `Envelope:PaddingMode`
  (`Pkcs7` or `None`, for hand-rolled zero-padding encryptors) are configurable.
- **Key sizes**: AES-128/192/256, chosen implicitly by the key's byte length.
- **Key rotation**: `Envelope:Keys` maps `keyId -> base64 key`; `Envelope:DefaultKeyId` is used when
  a request carries no key id. `/embed` accepts an optional `key_id` form field and
  `/admin/ingest-folder` accepts `envelope_key_id` (plus `envelope_key_ids_by_file` overrides).
- **Which files**: any file whose extension is in `Envelope:EncryptedExtensions` (default
  `.enc`, `.aes`), or any file at all when the request sets `envelope` / `force_envelope`.
- **Optional HMAC (encrypt-then-MAC)**: configurable algorithm (`HMACSHA256` default), a **separate**
  MAC key, configurable placement (`Append` = `IV || ct || MAC`, `Prepend` = `MAC || IV || ct`) and
  an optional truncated length. Verified with `CryptographicOperations.FixedTimeEquals` **before**
  any decryption is attempted. Off by default only because not every existing corpus has a MAC.

##### Security notes (please do not "improve" these away)

- **Unauthenticated CBC is malleable.** Without a MAC, AES-CBC gives confidentiality only: an
  attacker who can modify a blob can flip chosen bits of the recovered plaintext and nothing will
  notice. **Enabling `Envelope:Hmac` is strongly recommended** whenever your encryptor emits a MAC.
- **Uniform opaque failure / no padding oracle.** Every failure path - wrong key, unknown key id,
  truncated blob, misaligned ciphertext, bad padding, failed MAC - throws the *same*
  `EnvelopeDecryptionException` with the *same* message ("Unable to decrypt envelope."), and the API
  returns exactly that. Distinguishable failures would turn `/embed` into a
  [padding oracle](https://en.wikipedia.org/wiki/Padding_oracle_attack) that lets an attacker who can
  submit chosen blobs decrypt real documents byte by byte. The specific internal reason is logged at
  **Debug** level, server-side only. `AesCbcEnvelopeDecryptorTests` has an explicit regression test
  asserting all three of wrong-key / tampered / truncated produce an identical type *and* message.

  This holds **across the decrypt/parse boundary too**, which is the subtler half. A blob that
  decrypts cleanly (valid padding) but whose plaintext is not a recognisable document would
  otherwise come back as a *format* error while a bad-padding blob came back as a *decryption*
  error - and that difference alone re-creates the oracle, since it tells the attacker their
  chosen ciphertext padded correctly. So `IngestionService.DecryptEnvelope` collapses every
  post-decryption failure (unrecognisable format, CFB container with no password supplied, a
  parser rejecting the content) into the same opaque `EnvelopeDecryptionException`. The trade-off
  is a genuine diagnostic loss: an operator debugging a real format problem must read the
  server-side log, because the caller is deliberately told nothing. `EnvelopeIngestionPipelineTests`
  pins this with a test asserting a decryptable-but-unparsable blob fails identically to a
  wrong-key blob.
- **Plaintext never touches disk.** Decryption is `MemoryStream`/`byte[]` only.
- Per-operation copies of key material are wiped with `CryptographicOperations.ZeroMemory`; keys come
  only from env vars / config, are never hardcoded, and are never logged - not even truncated.

##### Format detection after decryption (`Cryptex.Ingestion/FormatSniffer.cs`)

A decrypted blob has no usable extension, so the real format is resolved in this order:

1. **Magic bytes** - `%PDF-` → pdf; `PK\x03\x04` → OOXML zip, then `[Content_Types].xml` inside the
   zip distinguishes docx/xlsx/pptx; `\xD0\xCF\x11\xE0` → legacy OLE2/CFB, i.e. very likely an
   MS-OFFCRYPTO container, which is handed to `OfficeCryptoDecryptor` and sniffed again;
   `<!DOCTYPE html` / `<html` → html.
2. **The inner filename**, for the `report.pdf.enc` convention - strip the encrypted extension and
   use what's underneath.
3. **UTF-8 / UTF-16 text heuristic** → plain text.
4. Otherwise a `FormatDetectionException` ("could not determine format").

#### 3. At-rest encryption of retained originals

Separately from decrypting *incoming* files, `AesGcmDocumentCipher` (AES-256-GCM) encrypts data this
service itself persists (original file bytes, if `DocumentRegistry:RetainOriginals` is enabled). Its
key comes only from the `CRYPTEX_AT_REST_KEY` environment variable - never hardcoded, never logged.
Generate one with:

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
  "VectorStore": { "Qdrant": { "Host": "localhost", "GrpcPort": 6334, "ApiKey": "", "CollectionName": "cryptex_chunks" } },
  "Security": { "EncryptionKeyEnvVar": "CRYPTEX_AT_REST_KEY" },
  "Envelope": {                        // customer-encrypted source files; see the section above
    "Enabled": false,
    "IvLength": 16,
    "PaddingMode": "Pkcs7",            // "Pkcs7" | "None"
    "EncryptedExtensions": [ ".enc", ".aes" ],
    "DefaultKeyId": "default",
    "Keys": {},                        // keyId -> base64 key; supply via env in production
    "Hmac": {
      "Enabled": false,                // strongly recommended when your encryptor emits a MAC
      "Algorithm": "HMACSHA256",
      "KeyBase64": "",                 // supply via CRYPTEX_ENVELOPE_HMAC_KEY
      "Placement": "Append",           // "Append" (IV||ct||MAC) | "Prepend" (MAC||IV||ct)
      "Length": 0                      // 0 = the algorithm's full output size
    }
  },
  "Chunking": { "MaxTokens": 512, "OverlapTokens": 64 },
  "DocumentRegistry": { "StorePath": "data/documents.json", "OriginalsPath": "data/originals", "RetainOriginals": false }
}
```

Every setting can be overridden by environment variable using ASP.NET Core's standard `__`
separator convention, e.g. `Embedding__Provider=AzureOpenAI`, `VectorStore__Qdrant__Host=qdrant`.

**Envelope key material is the exception: it is never read from `appsettings.json` in production.**
Each key is supplied as its own environment variable, `CRYPTEX_ENVELOPE_KEY_<keyId>` (the suffix is
the key id), plus `CRYPTEX_ENVELOPE_HMAC_KEY` for the MAC key. These are merged in at startup and
win over anything bound from configuration, so real keys never sit in a committed file:

```bash
export CRYPTEX_ENVELOPE_KEY_default=$(openssl rand -base64 32)
export CRYPTEX_ENVELOPE_KEY_2024=$(openssl rand -base64 32)   # key rotation
export CRYPTEX_ENVELOPE_HMAC_KEY=$(openssl rand -base64 32)
```

## Running locally

```bash
dotnet restore
dotnet build
dotnet test
dotnet run --project src/Cryptex.Api
```

Swagger UI is available at `/swagger` in the Development environment.

## Deployment (docker-compose)

```bash
cd docker
cp .env.example .env   # fill in real values - never commit .env
docker compose up -d --build
```

This brings up: `qdrant` (vector DB), `cryptex-api` (this service), `librechat-mongo`, and
`librechat`, with `RAG_API_URL` already wired to `http://cryptex-api:8080` so LibreChat's file
upload / RAG UI talks to Cryptex transparently. See `docker/.env.example` for every configurable
variable (`AZURE_OPENAI_*`, `LOCAL_EMBEDDING_*`, `QDRANT_*`, `CRYPTEX_AT_REST_KEY`,
`ENVELOPE_*` / `CRYPTEX_ENVELOPE_KEY_*`, ...).

To bulk-ingest an existing company document share, mount it into the `cryptex-api` container and
call `/admin/ingest-folder` with its in-container path.

## Known limitations / left incomplete

- **Hybrid search**: dense-vector-only (see "Vector storage" above); sparse/BM25 fusion is a
  documented extension point, not implemented.
- **Re-indexing**: `/admin/reindex/{id}` requires `DocumentRegistry:RetainOriginals=true` to have
  original bytes to re-parse from, and currently returns `501 Not Implemented` even when originals
  are retained - the decrypt-and-replay path isn't wired up yet. Re-uploading (or re-running the
  bulk folder ingest) achieves the same result today.
- **Envelope AEAD modes**: only AES-**CBC** with a prepended IV is implemented, because that is the
  confirmed format of the customer's corpus. AES-GCM/CCM envelopes, key-derivation-from-passphrase
  (PBKDF2/Argon2) envelopes, and per-file key ids encoded *inside* the blob are not supported - a key
  id must come from the request or from `Envelope:DefaultKeyId`.
- **Envelope HMAC is off by default**, so an unauthenticated (and therefore malleable) CBC envelope is
  accepted as-is. Turn `Envelope:Hmac:Enabled` on wherever the source encryptor produces a MAC.
- **`PaddingMode: None`** returns the final block verbatim, including any zero padding the encryptor
  added; nothing attempts to trim it, since trimming would corrupt legitimately zero-terminated binary
  payloads. Parsers tolerate the trailing bytes for every format tested here.
- **Format sniffing of an unknown binary** fails closed with `FormatDetectionException` rather than
  guessing; a decrypted blob that is neither a recognised container nor text needs a
  `name.<ext>.enc`-style filename to be ingested.
- **Agile OOXML encryption**: implemented in full per the MS-OFFCRYPTO spec (not simplified), but
  only unit-tested via a synthetically-built Standard-encryption fixture (see
  `Cryptex.Tests/Security/OfficeCryptoDecryptorTests.cs`) - no real Office-produced encrypted
  fixture was available in this environment to byte-for-byte cross-validate either scheme against
  a real Word/Excel/PowerPoint output.
