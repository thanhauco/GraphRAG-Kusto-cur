FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY GraphRag.Api/GraphRag.Api.csproj GraphRag.Api/
RUN dotnet restore GraphRag.Api/GraphRag.Api.csproj
COPY GraphRag.Api/ GraphRag.Api/
RUN dotnet publish GraphRag.Api/GraphRag.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8000
EXPOSE 8000
ENTRYPOINT ["dotnet", "GraphRag.Api.dll"]
