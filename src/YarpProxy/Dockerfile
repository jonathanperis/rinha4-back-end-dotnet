FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build

RUN apk add --no-cache \
    clang \
    lld \
    zlib-dev \
    musl-dev

ARG AOT=true
ARG BUILD_CONFIGURATION=Release
ARG TARGETARCH

RUN case "${TARGETARCH}" in \
        amd64) echo linux-musl-x64 > /tmp/runtime-id ;; \
        arm64) echo linux-musl-arm64 > /tmp/runtime-id ;; \
        *) echo "Unsupported TARGETARCH=${TARGETARCH}" >&2; exit 1 ;; \
    esac

WORKDIR /src

COPY ["src/YarpProxy/YarpProxy.csproj", "YarpProxy/"]
RUN dotnet restore "YarpProxy/YarpProxy.csproj" \
    -p:Configuration=${BUILD_CONFIGURATION} \
    -p:AOT=${AOT} \
    -r $(cat /tmp/runtime-id)

COPY ["src/YarpProxy/", "YarpProxy/"]
WORKDIR "/src/YarpProxy"
RUN dotnet publish "YarpProxy.csproj" \
    -c $BUILD_CONFIGURATION \
    -p:AOT=${AOT} \
    -r $(cat /tmp/runtime-id) \
    -o /app/publish \
    --self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime

WORKDIR /app
RUN mkdir -p /etc/nginx

ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_PROCESSOR_COUNT=1
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
ENV DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS=1
ENV DOTNET_SYSTEM_NET_SOCKETS_THREAD_COUNT=1

COPY --from=build /app/publish ./

EXPOSE 9999

ENTRYPOINT ["/app/YarpProxy"]
