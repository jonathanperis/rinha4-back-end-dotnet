FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
    clang zlib1g-dev

ARG AOT
ARG EXTRA_OPTIMIZE
ARG BUILD_CONFIGURATION=Release

WORKDIR /src

# ── DataConverter ──
COPY ["src/DataConverter/DataConverter.csproj", "src/DataConverter/"]
RUN dotnet restore "src/DataConverter/DataConverter.csproj"
COPY ["src/DataConverter/Program.cs", "src/DataConverter/"]

# Copy dataset files
COPY ["data/references.json.gz", "data/"]
COPY ["data/mcc_risk.json", "data/"]
COPY ["data/normalization.json", "data/"]

RUN dotnet build "src/DataConverter/DataConverter.csproj" -c Release -o /app/build-converter
RUN /app/build-converter/DataConverter data/

# ── WebApi ──
COPY ["src/WebApi/WebApi.csproj", "src/WebApi/"]
RUN dotnet restore "src/WebApi/WebApi.csproj" -p:Configuration=${BUILD_CONFIGURATION} -p:AOT=${AOT}

COPY ["src/WebApi/Program.cs", "src/WebApi/"]
WORKDIR "/src/WebApi"
RUN dotnet publish "WebApi.csproj" -c $BUILD_CONFIGURATION -p:AOT=${AOT} -p:ExtraOptimize=${EXTRA_OPTIMIZE} -o /app/publish

# ── Runtime ──
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

LABEL maintainer="Jonathan Peris"

USER app
WORKDIR /app

COPY --from=build /app/publish .
COPY --from=build /data/references.bin /data/
COPY --from=build /data/mcc_risk.json /data/
COPY --from=build /data/normalization.json /data/

ENV DATA_PATH=/data/references.bin

EXPOSE 8080

ENTRYPOINT ["./WebApi"]
