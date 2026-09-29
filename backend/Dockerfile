# Build context must be the repository root so the canonical evidence
# inventory can be packaged with the published backend:
#   docker build -f backend/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY backend/ backend/
COPY docs/evidence/ docs/evidence/
RUN dotnet restore backend/Rafael.Portfolio.sln
RUN dotnet publish backend/src/Hosts/Web/Rafael.Portfolio.Web/Rafael.Portfolio.Web.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Rafael.Portfolio.Web.dll"]
