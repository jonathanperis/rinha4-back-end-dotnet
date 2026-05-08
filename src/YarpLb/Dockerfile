FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build

WORKDIR /src
COPY ["src/YarpLb/YarpLb.csproj", "YarpLb/"]
RUN dotnet restore "YarpLb/YarpLb.csproj"
COPY ["src/YarpLb/", "YarpLb/"]
WORKDIR /src/YarpLb
RUN dotnet publish "YarpLb.csproj" -c Release -o /app/publish --self-contained false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
ENTRYPOINT ["dotnet", "YarpLb.dll"]
