# Build stage: .NET SDK Alpine with clang
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build

# Install build dependencies for NativeAOT
RUN apk add --no-cache \
    clang \
    lld \
    zlib-dev \
    musl-dev

ARG AOT
ARG EXTRA_OPTIMIZE
ARG BUILD_CONFIGURATION=Release
ARG CACHEBUST
ARG TARGETARCH

# Force cache invalidation when SHA changes
RUN echo "CACHEBUST=${CACHEBUST}"
RUN case "${TARGETARCH}" in \
        amd64) echo linux-musl-x64 > /tmp/runtime-id ;; \
        arm64) echo linux-musl-arm64 > /tmp/runtime-id ;; \
        *) echo "Unsupported TARGETARCH=${TARGETARCH}" >&2; exit 1 ;; \
    esac

WORKDIR /src

# ── DataConverter ──
COPY ["src/DataConverter/DataConverter.csproj", "DataConverter/"]
RUN dotnet restore "DataConverter/DataConverter.csproj"
COPY ["src/DataConverter/Program.cs", "DataConverter/"]
COPY ["src/WebApi/FraudVectorizer.cs", "WebApi/"]

# Copy dataset files
COPY ["data/references.json.gz", "data/"]
COPY ["data/mcc_risk.json", "data/"]
COPY ["data/normalization.json", "data/"]

RUN dotnet build "DataConverter/DataConverter.csproj" -c Release -o /app/build-converter
RUN /app/build-converter/DataConverter data/

# ── WebApi ──
COPY ["src/WebApi/WebApi.csproj", "WebApi/"]
RUN dotnet restore "WebApi/WebApi.csproj" \
    -p:Configuration=${BUILD_CONFIGURATION} \
    -p:AOT=${AOT} \
    -r $(cat /tmp/runtime-id)

COPY ["src/WebApi/Program.cs", "WebApi/"]
COPY ["src/WebApi/FraudVectorizer.cs", "WebApi/"]
WORKDIR "/src/WebApi"
RUN dotnet publish "WebApi.csproj" \
    -c $BUILD_CONFIGURATION \
    -p:AOT=${AOT} \
    -p:ExtraOptimize=${EXTRA_OPTIMIZE} \
    -r $(cat /tmp/runtime-id) \
    -o /app/publish \
    --self-contained

# ── Runtime: .NET ASP.NET Alpine (full deps for .NET 10 preview) ──
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime

WORKDIR /app

COPY --from=build /app/publish/WebApi /WebApi
COPY --from=build /src/data/references.bin /data/
COPY --from=build /src/data/mcc_risk.json /data/
COPY --from=build /src/data/normalization.json /data/

ENV DATA_PATH=/data/references.bin
ENV DOTNET_PROCESSOR_COUNT=1
ENV DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS=1
ENV DOTNET_SYSTEM_NET_SOCKETS_THREAD_COUNT=1
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1

EXPOSE 8080

ENTRYPOINT ["/WebApi"]
