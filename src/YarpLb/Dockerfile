FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build

RUN apk add --no-cache \
    clang \
    lld \
    zlib-dev \
    musl-dev

ARG BUILD_CONFIGURATION=Release
ARG AOT=true
ARG TARGETARCH

RUN case "${TARGETARCH}" in \
        amd64|"") echo linux-musl-x64 > /tmp/runtime-id ;; \
        arm64) echo linux-musl-arm64 > /tmp/runtime-id ;; \
        *) echo "Unsupported TARGETARCH=${TARGETARCH}" >&2; exit 1 ;; \
    esac

WORKDIR /src
COPY ["src/YarpLb/YarpLb.csproj", "YarpLb/"]
RUN dotnet restore "YarpLb/YarpLb.csproj" \
    -p:Configuration=${BUILD_CONFIGURATION} \
    -p:AOT=${AOT} \
    -r $(cat /tmp/runtime-id)
COPY ["src/YarpLb/", "YarpLb/"]
WORKDIR /src/YarpLb
RUN dotnet publish "YarpLb.csproj" \
    -c ${BUILD_CONFIGURATION} \
    -p:AOT=${AOT} \
    -r $(cat /tmp/runtime-id) \
    -o /app/publish \
    --self-contained

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-alpine AS runtime
WORKDIR /app
COPY --from=build /app/publish/YarpLb /YarpLb
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
ENTRYPOINT ["/YarpLb"]
