# ---- build stage ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first (cached unless the csproj changes), then build + publish.
COPY rdslive-proxy.csproj ./
RUN dotnet restore

COPY . ./
RUN dotnet publish rdslive-proxy.csproj -c Release -o /app --no-restore

# ---- runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

# The app binds 0.0.0.0:13001 by default (see PROXY_BIND / PROXY_PORT).
ENV PROXY_PORT=13001
EXPOSE 13001

ENTRYPOINT ["dotnet", "rdslive-proxy.dll"]
